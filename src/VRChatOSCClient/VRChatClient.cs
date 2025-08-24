using Microsoft.Extensions.Logging;
using System.Net;
using VRChatOSCClient.OSCConnections;
using VRChatOSCClient.OSCQuery;
using VRChatOSCClient.TaskExtensions;

namespace VRChatOSCClient;

public interface IVRChatClient {
    event Func<Message, CancellationToken, Task> OnMessageReceived;
    event Func<ParameterChangedMessage, CancellationToken, Task> OnParameterReceived;
    event Func<Dictionary<string, object?>, CancellationToken, Task> OnAvatarChanged;
    event Func<VRChatConnectionInfo, CancellationToken, Task> OnVRChatClientFound;

    public void Start(MessageFilter? messageFilter = default, CancellationToken token = default);
    Task Start(IPEndPoint sendEndpoint, IPEndPoint receiveEndpoint, MessageFilter? messageFilter, CancellationToken token);
    Task StartAndWaitAsync(MessageFilter? messageFilter = null, CancellationToken token = default);
    public Task StopAsync(CancellationToken token = default);
    void Send(Message message);
    void SendChatMessage(string message, bool bypassKeyboard = true, bool enableNotification = false);
    void SendParameterChange<T>(string parameter, T value) where T : notnull;
}

internal class VRChatClient : IVRChatClient
{
    private readonly ILogger<VRChatClient> _logger;
    private readonly OscQueryService _queryService;
    private readonly OscCommunicator _oscCommunicator;
    private readonly TaskCompletionSource _firstClientTcs;
    private readonly VRChatDataFetcher _dataFetcher;

    public event Func<Message, CancellationToken, Task> OnMessageReceived { add => _oscCommunicator.OnMessageReceived += value; remove => _oscCommunicator.OnMessageReceived -= value; }
    public event Func<ParameterChangedMessage, CancellationToken, Task> OnParameterReceived { add => _oscCommunicator.OnParameterChanged += value; remove => _oscCommunicator.OnParameterChanged -= value; }

    public event Func<Dictionary<string, object?>, CancellationToken, Task> OnAvatarChanged { add => _onAvatarChanged.Add(value); remove => _onAvatarChanged.Remove(value); }
    private readonly AsyncEvent<Func<Dictionary<string, object?>, CancellationToken, Task>> _onAvatarChanged = new();

    public event Func<VRChatConnectionInfo, CancellationToken, Task> OnVRChatClientFound { add => _onVRChatClientFound.Add(value); remove => _onVRChatClientFound.Remove(value); }
    private readonly AsyncEvent<Func<VRChatConnectionInfo, CancellationToken, Task>> _onVRChatClientFound = new();


    private MessageFilter _messageFilter = new();
    private VRChatConnectionInfo _connectionInfo = new() { SendEndpoint = new(System.Net.IPAddress.Loopback, 0), OSCQueryEndpoint = new(System.Net.IPAddress.Loopback, 0), ReceiveEndpoint = new(System.Net.IPAddress.Loopback, 0) };

    public VRChatClient(ILogger<VRChatClient> logger, OscQueryService queryService, OscCommunicator oscCOmmunicator, VRChatDataFetcher dataFetcher) {
        _logger = logger;
        _queryService = queryService;
        _oscCommunicator = oscCOmmunicator;
        _firstClientTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        _queryService.OnVrchatClientFound += OnVrchatClientFound;
        _oscCommunicator.OnAvatarChanged += OnAvatarChangedLoad;
        _dataFetcher = dataFetcher;
    }

    /// <summary>
    /// Starts up the VRChatClient
    /// </summary>
    /// <param name="messageFilter"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    public void Start(MessageFilter? messageFilter = default, CancellationToken token = default) {
        _logger.LogInformation("Starting VRChatClient");
        
        if(messageFilter is not null) {
            _messageFilter = messageFilter;
        }

        _queryService.Start(token);
    }

    /// <summary>
    /// Starts the VRChatClient with specified endpoints. This will bypass the OSCQuery service.
    /// </summary>
    /// <param name="sendEndpoint"></param>
    /// <param name="receiveEndpoint"></param>
    /// <param name="messageFilter"></param>
    public async Task Start(IPEndPoint sendEndpoint, IPEndPoint receiveEndpoint, MessageFilter? messageFilter, CancellationToken token) {
        VRChatConnectionInfo connection = new() {
            SendEndpoint = sendEndpoint,
            ReceiveEndpoint = receiveEndpoint,
            OSCQueryEndpoint = new(IPAddress.Loopback, 0)
        };

        await _oscCommunicator.StartAsync(connection, messageFilter ?? new(), token);
        await _onVRChatClientFound.InvokeAsync(connection, token);
    }

    /// <summary>
    /// Starts the VRChatClient and waits for the game to connect.
    /// </summary>
    /// <param name="messageFilter"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    public async Task StartAndWaitAsync(MessageFilter? messageFilter = default, CancellationToken token = default) {
        try {
            Start(messageFilter, token);
            await Task.Run(async () => await _firstClientTcs.Task, token);
        }
        catch (TaskCanceledException) { }
    }

    /// <summary>
    /// Stops all services related to the VRChatClient.
    /// </summary>
    /// <param name="token"></param>
    /// <returns></returns>
    public async Task StopAsync(CancellationToken token = default) {
        _logger.LogInformation("Stopping VRChatClient");

        await _queryService.StopAsync(token).ConfigureAwait(false);
        await _oscCommunicator.StopAsync(token).ConfigureAwait(false);
        
        // Reset the first client task source
        _firstClientTcs.TrySetCanceled(token);
    }

    /// <summary>
    /// Called whenever a client is found (first or subsequent).
    /// </summary>
    private async Task OnVrchatClientFound(VRChatConnectionInfo connection, CancellationToken token) {
        _logger.LogInformation("Found Vrchat client. Receiving on: {receiveIP}:{receivePort}. Sending on: {sendIP}:{sendPort}. OSCserver: {oscIP}:{oscPort}", connection.ReceiveEndpoint.Address, connection.ReceiveEndpoint.Port, connection.SendEndpoint.Address, connection.SendEndpoint.Port, connection.OSCQueryEndpoint.Address, connection.OSCQueryEndpoint.Port);
        _connectionInfo = connection;

        await _oscCommunicator.StartAsync(connection, _messageFilter, CancellationToken.None);
        
        _firstClientTcs.TrySetResult();

        await _onVRChatClientFound.InvokeAsync(connection, token);
    }

    /// <summary>
    /// Fetches the avatar parameters when the avatar changes.
    /// </summary>
    /// <param name="message"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    private async Task OnAvatarChangedLoad(AvatarChangedMessage message, CancellationToken token) {
        Dictionary<string, object?> avatarParameters = await _dataFetcher.GetAvatarParameters(_connectionInfo.OSCQueryEndpoint.Address, (ushort)_connectionInfo.OSCQueryEndpoint.Port, token);
        await _onAvatarChanged.InvokeAsync(avatarParameters, token);
    }

    /// <summary>
    /// Fetches the avatars parameters from the VRChat client. 
    /// </summary>
    /// <param name="token"></param>
    /// <returns></returns>
    public async Task<Dictionary<string, object?>> GetAvatarParametersAsync(CancellationToken token) {
        return _connectionInfo.OSCQueryEndpoint.Port == 0
            ? []
            : await _dataFetcher.GetAvatarParameters(_connectionInfo.OSCQueryEndpoint.Address, (ushort)_connectionInfo.OSCQueryEndpoint.Port, token);
    }

    /// <summary>
    /// Sends a message to the VRChat client.
    /// </summary>
    /// <param name="message"></param>
    public void Send(Message message) => _oscCommunicator.SendMessage(message);

    /// <summary>
    /// Update a parameter on the VRChat client.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="parameter">The name of the parameter without the address prefix</param>
    /// <param name="value">what to update the value to</param>
    public void SendParameterChange<T>(string parameter, T value) where T : notnull => Send(new ParameterChangedMessage(parameter, value));

    /// <summary>
    /// Sends a chat message to the VRChat client.
    /// </summary>
    /// <param name="message"></param>
    /// <param name="bypassKeyboard"></param>
    /// <param name="enableNotification"></param>
    public void SendChatMessage(string message, bool bypassKeyboard = true, bool enableNotification = false) => Send(new ChatMessage(message, bypassKeyboard, enableNotification));
}
