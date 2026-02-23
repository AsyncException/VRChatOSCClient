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
    private readonly ILogger<OscCommunicator> _logger = logger;

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

    public async Task StartAsync(VRChatConnectionInfo connectionInfo, MessageFilter messageFilter, CancellationToken token) {
        if(_state is not null) {
            throw new InvalidOperationException("OSCCommunicator is already running. Please stop it before starting again.");
        }

        var state = new RunningState();

        await _semaphore.WaitAsync(token);

        try {
            _logger.LogStartingOscCommunicator();
            _messageFilter = messageFilter;

            try {
                await state.SenderSocket.ConnectAsync(connectionInfo.SendEndpoint, token).ConfigureAwait(false);
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Failed to connect sender socket to {SendEndpoint}", connectionInfo.SendEndpoint);
                _semaphore.Release();
                throw;
            }

            try {
                state.ReceiverSocket.Bind(connectionInfo.ReceiveEndpoint);
            }
            catch (Exception ex) {
                _logger.LogFailedReceiverSocketCreate(ex, connectionInfo.ReceiveEndpoint);
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
            _logger.LogFailedReceiverSocketCreate(ex, connectionInfo.ReceiveEndpoint);
            _semaphore.Release();
        }
    }

    public async Task StopAsync(CancellationToken token = default) {
        var state = _state;
        if(state is null) {
            return;
        }

        await _semaphore.WaitAsync(token);
        try {
            _logger.LogStoppingOscCommunicator();

            state.SenderSocket.Close();
            state.ReceiverSocket.Close();
            state.CTS.Cancel();
            
            if (!_receiverTask.IsCompleted) {
                await _receiverTask;
                _receiverTask = Task.CompletedTask;
            }

            if(!_dequeueTask.IsCompleted) {
                await _dequeueTask;
                _dequeueTask = Task.CompletedTask;
            }

            state.Dispose();
        }
        catch (Exception ex) {
            _logger.LogFailedReceiverSocketDestroy(ex);
        }
    }

    private async Task StartReceivingAsync() {
        var state = _state;
        if(state is null) {
            return;
        }
        
        Memory<byte> buffer = new byte[4096];
        while (!state.CTS.IsCancellationRequested) {
            try {
                _ = await state.ReceiverSocket.ReceiveAsync(buffer, state.CTS.Token);
                Message message = MessageParser.Parse(buffer);
                await _messageChannel.Writer.WriteAsync(message);
            }
            catch (Exception ex) {
                _logger.LogReceivingError(ex);
            }
        }
    }

    private async Task StartDequeueAsync() {
        var state = _state;
        if(state is null) {
            return;
        }

        while (!state.CTS.IsCancellationRequested) {
            try {
                Message message = await _messageChannel.Reader.ReadAsync(state.CTS.Token);
                _logger.LogMessageReceived(message.Address, message.Arguments[0]);

                if (!_messageFilter.IsAddressPatternMatch(message)) {
                    continue;
                }

                if (message.Address.StartsWith(ParameterChangedMessage.PARAMETER_CHANGED_ADDRESS)) {
                    ParameterChangedMessage parameterMessage = new(message);
                    if (_messageFilter.IsParameterPatternMatch(parameterMessage)) {
                        await _onParameterChanged.InvokeAsync(parameterMessage, state.CTS.Token);
                    }

                    continue;
                }

                if (message.Address.StartsWith(AvatarChangedMessage.AVATAR_CHANGED_ADDRESS)) {
                    AvatarChangedMessage avatarMessage = new(message);
                    await _onAvatarChanged.InvokeAsync(avatarMessage, state.CTS.Token);
                    continue;
                }

                await _onMessageReceived.InvokeAsync(message, state.CTS.Token);
            }
            catch (OperationCanceledException) { } // Ignore operation cancellation
            catch (Exception ex) {
                _logger.LogDequeueingError(ex);
            }
        }
    }

    /// <summary>
    /// Send an OSC message to the connected socket.
    /// </summary>
    /// <param name="message"></param>
    public void SendMessage(Message message) {
        var state = _state;
        if(state is null) {
            return;
        }

        state.SenderSocket.Send(MessageParser.Serialize(message).Span);
    }


    private class RunningState : IDisposable {
        public Socket SenderSocket { get; }
        public Socket ReceiverSocket { get; }
        public CancellationTokenSource CTS { get; }

        public RunningState() {
            SenderSocket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            ReceiverSocket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            CTS = new();
        }

        public void Dispose() {
            SenderSocket.Dispose();
            ReceiverSocket.Dispose();
            CTS.Dispose();
        }
    }
}

internal static partial class OscCommunicatorLogger
{

    [LoggerMessage(LogLevel.Information, "Starting OSCCommunicator")]
    public static partial void LogStartingOscCommunicator(this ILogger<OscCommunicator> logger);

    [LoggerMessage(LogLevel.Information, "Stopping OSCCommunicator")]
    public static partial void LogStoppingOscCommunicator(this ILogger<OscCommunicator> logger);

    [LoggerMessage(LogLevel.Error, "Failed to bind receiver socket to {ReceiveEndpoint}")]
    public static partial void LogFailedReceiverSocketCreate(this ILogger<OscCommunicator> logger, Exception exception, IPEndPoint ReceiveEndpoint);

    [LoggerMessage(LogLevel.Error, "Failed to disconnect receiver socket")]
    public static partial void LogFailedReceiverSocketDestroy(this ILogger<OscCommunicator> logger, Exception exception);

    [LoggerMessage(LogLevel.Trace, "Received message: {address}: {value}.")]
    public static partial void LogMessageReceived(this ILogger<OscCommunicator> logger, string address, object? value);

    [LoggerMessage(LogLevel.Error, "Exception occured while receiving and parsing message")]
    public static partial void LogDequeueingError(this ILogger<OscCommunicator> logger, Exception exception);

    [LoggerMessage(LogLevel.Error, "Exception occured while receiving and parsing message")]
    public static partial void LogReceivingError(this ILogger<OscCommunicator> logger, Exception exception);
}