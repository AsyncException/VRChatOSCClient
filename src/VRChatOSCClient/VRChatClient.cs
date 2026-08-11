using Microsoft.Extensions.Logging;
using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using VRChatOSCClient.OSCConnections;
using VRChatOSCClient.OSCQuery;
using VRChatOSCClient.TaskExtensions;

namespace VRChatOSCClient;

public interface IVRChatClient {
    event Func<Message, CancellationToken, Task> OnMessageReceived;
    event Func<ParameterChangedMessage, CancellationToken, Task> OnParameterReceived;
    event Func<Dictionary<string, object?>, CancellationToken, Task> OnAvatarChanged;
    event Func<VrChatConnectionInfo, CancellationToken, Task> OnVRChatClientFound;

    public void Start(MessageFilter? messageFilter = default, CancellationToken token = default);
    Task Start(IPEndPoint sendEndpoint, IPEndPoint receiveEndpoint, MessageFilter? messageFilter, CancellationToken token);
    Task StartAndWaitAsync(MessageFilter? messageFilter = null, CancellationToken token = default);
    public Task StopAsync();
    void Send(Message message);
    void SendChatMessage(string message, bool bypassKeyboard = true, bool enableNotification = false);
    void SendParameterChange<T>(string parameter, T value) where T : notnull;
}

internal class VRChatClient(ILogger<VRChatClient> logger, OscQueryService queryService, OscCommunicator oscCOmmunicator, VrChatDataFetcher dataFetcher) : IVRChatClient
{
    private readonly ILogger<VRChatClient> _logger = logger;
    private readonly OscQueryService _queryService = queryService;
    private readonly OscCommunicator _oscCommunicator = oscCOmmunicator;
    private readonly VrChatDataFetcher _dataFetcher = dataFetcher;

    public event Func<Message, CancellationToken, Task> OnMessageReceived { add => _oscCommunicator.OnMessageReceived += value; remove => _oscCommunicator.OnMessageReceived -= value; }
    public event Func<ParameterChangedMessage, CancellationToken, Task> OnParameterReceived { add => _oscCommunicator.OnParameterChanged += value; remove => _oscCommunicator.OnParameterChanged -= value; }

    public event Func<Dictionary<string, object?>, CancellationToken, Task> OnAvatarChanged { add => _onAvatarChanged.Add(value); remove => _onAvatarChanged.Remove(value); }
    private readonly AsyncEvent<Func<Dictionary<string, object?>, CancellationToken, Task>> _onAvatarChanged = new();

    public event Func<VrChatConnectionInfo, CancellationToken, Task> OnVRChatClientFound { add => _onVRChatClientFound.Add(value); remove => _onVRChatClientFound.Remove(value); }
    private readonly AsyncEvent<Func<VrChatConnectionInfo, CancellationToken, Task>> _onVRChatClientFound = new();

    private TaskCompletionSource _firstClientTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private MessageFilter _messageFilter = new();
    private VrChatConnectionInfo _connectionInfo = new() { 
        SendEndpoint = new(IPAddress.Loopback, 0),
        OscQueryEndpoint = new(IPAddress.Loopback, 0),
        ReceiveEndpoint = new(IPAddress.Loopback, 0) 
    };

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

        _queryService.OnVrchatClientFound += OnVrchatClientFound;
        _oscCommunicator.OnAvatarChanged += OnAvatarChangedLoad;
        _queryService.Start(token);
    }

    /// <summary>
    /// Starts the VRChatClient with specified endpoints. This will bypass the OSCQuery service.
    /// </summary>
    /// <param name="sendEndpoint"></param>
    /// <param name="receiveEndpoint"></param>
    /// <param name="messageFilter"></param>
    public async Task Start(IPEndPoint sendEndpoint, IPEndPoint receiveEndpoint, MessageFilter? messageFilter, CancellationToken token) {
        VrChatConnectionInfo connection = new() {
            SendEndpoint = sendEndpoint,
            ReceiveEndpoint = receiveEndpoint,
            OscQueryEndpoint = new(IPAddress.Loopback, 0)
        };

        _queryService.OnVrchatClientFound += OnVrchatClientFound;
        _oscCommunicator.OnAvatarChanged += OnAvatarChangedLoad;
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
    public async Task StopAsync() {
        _logger.LogInformation("Stopping VRChatClient");

        await _queryService.StopAsync();
        await _oscCommunicator.Stop();
        _queryService.OnVrchatClientFound -= OnVrchatClientFound;
        _oscCommunicator.OnAvatarChanged -= OnAvatarChangedLoad;

        // Reset the first client task source
        _firstClientTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// Called whenever a client is found (first or subsequent).
    /// </summary>
    private async Task OnVrchatClientFound(VrChatConnectionInfo connection, CancellationToken token) {
        _logger.LogInformation("Found Vrchat client. Receiving on: {receiveIP}:{receivePort}. Sending on: {sendIP}:{sendPort}. OSCserver: {oscIP}:{oscPort}", connection.ReceiveEndpoint.Address, connection.ReceiveEndpoint.Port, connection.SendEndpoint.Address, connection.SendEndpoint.Port, connection.OscQueryEndpoint.Address, connection.OscQueryEndpoint.Port);
        _connectionInfo = connection;

        await _oscCommunicator.StartAsync(connection, _messageFilter, token);
        
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
        Dictionary<string, object?> avatarParameters = [];

        try {
            avatarParameters = await _dataFetcher.GetAvatarParameters(_connectionInfo.OscQueryEndpoint.Address, (ushort)_connectionInfo.OscQueryEndpoint.Port, token);
        }
        catch(Exception ex) {
            _logger.LogError(ex, "Failed to fetch parameters of the current avatar");
        }

        await _onAvatarChanged.InvokeAsync(avatarParameters, token);
    }

    /// <summary>
    /// Fetches the avatars parameters from the VRChat client. 
    /// </summary>
    /// <param name="token"></param>
    /// <returns></returns>
    public async Task<Dictionary<string, object?>> GetAvatarParametersAsync(CancellationToken token) {
        if(_connectionInfo.OscQueryEndpoint.Port == 0) {
            _logger.LogWarning("OSCQuery endpoint is not set. Cannot fetch avatar parameters.");
            return [];
        }

        Dictionary<string, object?> parameters = await _dataFetcher.GetAvatarParameters(_connectionInfo.OscQueryEndpoint.Address, (ushort)_connectionInfo.OscQueryEndpoint.Port, token);
        return parameters;
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

public class AvatarParameterStore : IReadOnlyDictionary<string, IAvatarParameter>
{
    private readonly ConcurrentDictionary<string, IAvatarParameter> _parameters = [];

    public int Count => _parameters.Count;
    public IEnumerable<string> Keys => _parameters.Keys;
    public IAvatarParameter this[string key] => _parameters[key];
    public IEnumerable<IAvatarParameter> Values => _parameters.Values;

    internal ConcurrentDictionary<string, IAvatarParameter> GetDictionary() => _parameters;
    internal void AddOrUpdate(string key, IAvatarParameter parameter) => _parameters.AddOrUpdate(key, parameter, (k, v) => parameter);
    public bool ContainsKey(string key) => _parameters.ContainsKey(key);
    public IEnumerator<KeyValuePair<string, IAvatarParameter>> GetEnumerator() => _parameters.GetEnumerator();
    public bool TryGetValue(string key, [MaybeNullWhen(false)] out IAvatarParameter value) => _parameters.TryGetValue(key, out value);
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public interface IAvatarParameter { public string Type { get; } }
public readonly record struct BooleanParameter(bool Value) : IAvatarParameter { public string Type { get; } = "bool"; }
public readonly record struct SingleParameter(float Value) : IAvatarParameter { public string Type { get; } = "float"; }
public readonly record struct IntegerParameter(int Value) : IAvatarParameter { public string Type { get; } = "int"; }
public readonly record struct StringParameter(string Value) : IAvatarParameter { public string Type { get; } = "string"; }