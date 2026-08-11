using Microsoft.Extensions.Logging;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using VRChatOSCClient.OSCQuery;
using VRChatOSCClient.TaskExtensions;

namespace VRChatOSCClient.OSCConnections;

// A class responsible for sending and receiving OSC messages and also fetch current parameters
internal class OscCommunicator(ILogger<OscCommunicator> logger)
{
    private RunningState? _state;
    
    private MessageFilter _messageFilter = new();
    private Task _receiverTask = Task.CompletedTask;
    private Task _dequeueTask = Task.CompletedTask;

    public event Func<ParameterChangedMessage, CancellationToken, Task> OnParameterChanged { add => _onParameterChanged.Add(value); remove => _onParameterChanged.Remove(value); }
    private readonly AsyncEvent<Func<ParameterChangedMessage, CancellationToken, Task>> _onParameterChanged = new();

    public event Func<AvatarChangedMessage, CancellationToken, Task> OnAvatarChanged { add => _onAvatarChanged.Add(value); remove => _onAvatarChanged.Remove(value); }
    private readonly AsyncEvent<Func<AvatarChangedMessage, CancellationToken, Task>> _onAvatarChanged = new();

    public event Func<Message, CancellationToken, Task> OnMessageReceived { add => _onMessageReceived.Add(value); remove => _onMessageReceived.Remove(value); }
    private readonly AsyncEvent<Func<Message, CancellationToken, Task>> _onMessageReceived = new();

    private readonly Channel<Message> _messageChannel = Channel.CreateUnbounded<Message>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly SemaphoreSlim _semaphore = new(1);

    public async Task StartAsync(VrChatConnectionInfo connectionInfo, MessageFilter messageFilter, CancellationToken token) {
        if(_state is not null) {
            throw new InvalidOperationException("OSCCommunicator is already running. Please stop it before starting again.");
        }

        var state = new RunningState();

        await _semaphore.WaitAsync(token);

        try {
            logger.LogInformation("Starting OSCCommunicator");
            _messageFilter = messageFilter;

            try {
                await state.SenderSocket.ConnectAsync(connectionInfo.SendEndpoint, token).ConfigureAwait(false);
            }
            catch (Exception ex) {
                logger.LogError(ex, "Failed to connect sender socket to {SendEndpoint}", connectionInfo.SendEndpoint);
                _semaphore.Release();
                throw;
            }

            try {
                state.ReceiverSocket.Bind(connectionInfo.ReceiveEndpoint);
            }
            catch (Exception ex) {
                logger.LogError(ex, "Failed to bind receiver socket to {ReceiveEndpoint}", connectionInfo.ReceiveEndpoint);
                _semaphore.Release();
                throw;
            }

            _state = state;

            if (!_messageFilter.DisableReceiving) {
                _receiverTask = StartReceivingAsync();
                _dequeueTask = StartDequeueAsync();
            }
        }
        catch (Exception ex) {
            logger.LogError(ex, "Failed to bind receiver socket to {ReceiveEndpoint}", connectionInfo.ReceiveEndpoint);
            _semaphore.Release();
        }
    }

    public async Task Stop(CancellationToken token = default) {
        var state = _state;
        if(state is null) {
            return;
        }

        try {
            logger.LogInformation("Stopping OSCCommunicator");

            state.SenderSocket.Close();
            state.ReceiverSocket.Close();
            await state.Cts.CancelAsync();

            await _receiverTask;
            await _dequeueTask;

            state.Dispose();
        }
        catch (Exception ex) {
            logger.LogError(ex, "Failed to disconnect receiver socket");
        }
    }

    private async Task StartReceivingAsync() {
        var state = _state;
        if(state is null) {
            return;
        }
        
        Memory<byte> buffer = new byte[4096];
        while (!state.Cts.IsCancellationRequested) {
            try {
                _ = await state.ReceiverSocket.ReceiveAsync(buffer, state.Cts.Token);
                var message = MessageParser.Parse(buffer);
                await _messageChannel.Writer.WriteAsync(message);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) {
                logger.LogError(ex, "Exception occured while receiving and parsing message");
            }
        }

        logger.LogDebug("[OscCommunicator]::StartReceiving exited successfully");
    }

    private async Task StartDequeueAsync() {
        var state = _state;
        if(state is null) {
            return;
        }

        while (!state.Cts.IsCancellationRequested) {
            try {
                var message = await _messageChannel.Reader.ReadAsync(state.Cts.Token);

                if (logger.IsEnabled(LogLevel.Trace))
                {
                    logger.LogTrace("Received message: {address}: {value}.", message.Address, message.Arguments[0]);
                }

                if (!_messageFilter.IsAddressPatternMatch(message)) {
                    continue;
                }

                if (message.Address.StartsWith(ParameterChangedMessage.PARAMETER_CHANGED_ADDRESS)) {
                    ParameterChangedMessage parameterMessage = new(message);
                    if (_messageFilter.IsParameterPatternMatch(parameterMessage)) {
                        await _onParameterChanged.InvokeAsync(parameterMessage, state.Cts.Token);
                    }

                    continue;
                }

                if (message.Address.StartsWith(AvatarChangedMessage.AVATAR_CHANGED_ADDRESS)) {
                    AvatarChangedMessage avatarMessage = new(message);
                    await _onAvatarChanged.InvokeAsync(avatarMessage, state.Cts.Token);
                    continue;
                }

                await _onMessageReceived.InvokeAsync(message, state.Cts.Token);
            }
            catch (OperationCanceledException) { } // Ignore operation cancellation
            catch (Exception ex) {
                logger.LogError(ex, "Exception occured while receiving and parsing message");
            }
        }

        logger.LogDebug("[OscCommunicator]::StartDequeueAsync exited successfully");
    }

    /// <summary>
    /// Send an OSC message to the connected socket.
    /// </summary>
    /// <param name="message"></param>
    public void SendMessage(Message message) {
        var state = _state;
        state?.SenderSocket.Send(MessageParser.Serialize(message).Span);
    }


    private class RunningState : IDisposable {
        public Socket SenderSocket { get; } = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        public Socket ReceiverSocket { get; } = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        public CancellationTokenSource Cts { get; } = new();

        public void Dispose() {
            SenderSocket.Dispose();
            ReceiverSocket.Dispose();
            Cts.Dispose();
        }
    }
}