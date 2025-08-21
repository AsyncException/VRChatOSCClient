using Microsoft.Extensions.Logging;
using System.Net.Sockets;
using System.Threading;
using VRChatOSCClient.OSCQuery;
using VRChatOSCClient.TaskExtensions;

namespace VRChatOSCClient.OSCConnections;

// A class responsible for sending and receiving OSC messages and also fetch current parameters
internal class OscCommunicator(ILogger<OscCommunicator> logger)
{
    private readonly ILogger<OscCommunicator> _logger = logger;

    private Socket? _senderSocket;
    private Socket? _receiverSocket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

    private CancellationTokenSource _cancellationTokenSource = new();
    private Task? _backgroundTask;
    private MessageFilter _messageFilter = new();

    public event Func<ParameterChangedMessage, CancellationToken, Task> OnParameterChanged { add => _onParameterChanged.Add(value); remove => _onParameterChanged.Remove(value); }
    private readonly AsyncEvent<Func<ParameterChangedMessage, CancellationToken, Task>> _onParameterChanged = new();

    public event Func<AvatarChangedMessage, CancellationToken, Task> OnAvatarChanged { add => _onAvatarChanged.Add(value); remove => _onAvatarChanged.Remove(value); }
    private readonly AsyncEvent<Func<AvatarChangedMessage, CancellationToken, Task>> _onAvatarChanged = new();

    public event Func<Message, CancellationToken, Task> OnMessageReceived { add => _onAvatarChanged.Add(value); remove => _onAvatarChanged.Remove(value); }
    private readonly AsyncEvent<Func<Message, CancellationToken, Task>> _onMessageReceived = new();

    private readonly SemaphoreSlim _semaphore = new(1);

    public async Task StartAsync(VRChatConnectionInfo connectionInfo, MessageFilter messageFilter, CancellationToken token) {
        if ((_senderSocket?.Connected ?? false) || (_receiverSocket?.IsBound ?? false)) {
            await StopAsync(token);
        }

        await _semaphore.WaitAsync(token);

        _logger.LogInformation("Starting OSCCommunicator");
        _messageFilter = messageFilter;

        if (!_cancellationTokenSource.TryReset()) {
            _cancellationTokenSource = new CancellationTokenSource();
        }

        try {
            _senderSocket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            await _senderSocket.ConnectAsync(connectionInfo.SendEndpoint, token).ConfigureAwait(false);
        }
        catch (Exception ex) {
            _logger.LogError(ex, "Failed to connect sender socket to {SendEndpoint}", connectionInfo.SendEndpoint);
            _semaphore.Release();
            throw;
        }

        try {
            _receiverSocket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _receiverSocket.Bind(connectionInfo.ReceiveEndpoint);
        }
        catch (Exception ex) {
            _logger.LogError(ex, "Failed to bind receiver socket to {ReceiveEndpoint}", connectionInfo.ReceiveEndpoint);
            _semaphore.Release();
            throw;
        }

        if (!_messageFilter.DisableReceiving) {
            _backgroundTask = StartReceivingAsync();
        }

        _semaphore.Release();
    }

    public async Task StopAsync(CancellationToken token = default) {
        if (!(_senderSocket?.Connected ?? false) && !(_receiverSocket?.IsBound ?? false)) {
            return;
        }

        await _semaphore.WaitAsync(token);

        _logger.LogInformation("Stopping OSCCommunicator");

        await _cancellationTokenSource.CancelAsync();
        if (_backgroundTask is not null) {
            await _backgroundTask.ConfigureAwait(false);
            _backgroundTask = Task.CompletedTask;
        }

        try {
            _senderSocket?.Close();
            _senderSocket?.Dispose();
            _senderSocket = null;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) {
            _logger.LogError(ex, "Failed to disconnect sender socket");
        }

        try {
            _receiverSocket?.Close();
            _receiverSocket?.Dispose();
            _receiverSocket = null;
        }
        catch (Exception ex) {
            _logger.LogError(ex, "Failed to disconnect receiver socket");
        }

        _semaphore.Release();
    }

    public async Task StartReceivingAsync() {
        Memory<byte> buffer = new byte[4096];
        while (!_cancellationTokenSource.IsCancellationRequested) {
            try {
                _ = await _receiverSocket!.ReceiveAsync(buffer, _cancellationTokenSource.Token).ConfigureAwait(false);
                Message message = MessageParser.Parse(buffer);
                _logger.LogTrace("Received message: {address}. {value}.", message.Address, message.Arguments[0]);

                if (!_messageFilter.IsAddressPatternMatch(message)) {
                    continue;
                }

                if (message.Address.StartsWith(ParameterChangedMessage.PARAMETER_CHANGED_ADDRESS)) {
                    ParameterChangedMessage parameterMessage = new(message);
                    if (_messageFilter.IsParameterPatternMatch(parameterMessage)) {
                        await _onParameterChanged.InvokeAsync(parameterMessage, _cancellationTokenSource.Token).ConfigureAwait(false);
                    }

                    continue;
                }

                if (message.Address.StartsWith(AvatarChangedMessage.AVATAR_CHANGED_ADDRESS)) {
                    AvatarChangedMessage avatarMessage = new(message);
                    await _onAvatarChanged.InvokeAsync(avatarMessage, _cancellationTokenSource.Token).ConfigureAwait(false);
                    continue;
                }

                await _onMessageReceived.InvokeAsync(message, _cancellationTokenSource.Token).ConfigureAwait(false);
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Exception occured while receiving and parsing message");
            }
        }
    }

    public void SendMessage(Message message) {
        if(_senderSocket is not null) {
            _senderSocket.Send(MessageParser.Serialize(message).Span);
        }
    }
}
