using Microsoft.Extensions.Logging;
using VRChatOSCClient.OSCConnections;
using VRChatOSCClient.OSCQuery;
using VRChatOSCClient.TaskExtensions;

namespace VRChatOSCClient;

public interface IVRChatClient {
    event Func<Message, CancellationToken, Task> OnMessageReceived;
    public Task StartAsync(MessageFilter? messageFilter = default, CancellationToken token = default);
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

    public event Func<Message, CancellationToken, Task> OnMessageReceived { add => _onMessageReceived.Add(value); remove => _onMessageReceived.Remove(value); }
    private readonly AsyncEvent<Func<Message, CancellationToken, Task>> _onMessageReceived = new();

    private MessageFilter _messageFilter = new();

    public VRChatClient(ILogger<VRChatClient> logger, OscQueryService queryService, OscCommunicator oscCOmmunicator) {
        _logger = logger;
        _queryService = queryService;
        _oscCommunicator = oscCOmmunicator;
        _firstClientTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        _oscCommunicator.OnMessageReceived += ForwardOnMessageReceived;
        _queryService.OnVrchatClientFound += OnVrchatClientFound;
    }

    /// <summary>
    /// Starts up the VRChatClient and waits for Vrchat to be found.
    /// </summary>
    /// <param name="messageFilter"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    public async Task StartAsync(MessageFilter? messageFilter = default, CancellationToken token = default) {
        _logger.LogInformation("Starting VRChatClient");
        
        if(messageFilter is not null) {
            _messageFilter = messageFilter;
        }

        _queryService.Start();
        await Task.Run(async () => await _firstClientTcs.Task, token);
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

        if (_oscCommunicator.Connected) {
            await _oscCommunicator.StopAsync(token).ConfigureAwait(false);
        }
        
        // Reset the first client task source
        _firstClientTcs.TrySetCanceled(token);
    }

    /// <summary>
    /// Acts as an event forwatder for the OSCCommunicator's OnMessageReceived event.
    /// </summary>
    /// <param name="message"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    private async Task ForwardOnMessageReceived(Message message, CancellationToken token) => await _onMessageReceived.InvokeAsync(message, token).ConfigureAwait(false);

    /// <summary>
    /// Called whenever a client is found (first or subsequent).
    /// </summary>
    private async Task OnVrchatClientFound(VRChatConnectionInfo connection) {
        _logger.LogInformation("Found Vrchat client. Receiving on: {receiveIP}:{receivePort}. Sending on: {sendIP}:{sendPort}", connection.ReceiveEndpoint.Address, connection.ReceiveEndpoint.Port, connection.SendEndpoint.Address, connection.SendEndpoint.Port);

        await _oscCommunicator.StartAsync(connection, _messageFilter, CancellationToken.None);
        
        _firstClientTcs.TrySetResult();
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
