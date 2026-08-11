using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using Valve.VR;
using VRChatOSCClient.TaskExtensions;

namespace VRChatOSCClient.OpenVR;

public class OpenVrWrapper : IAsyncDisposable
{
    private readonly string _appId;
    private readonly string _appManifestPath;
    private readonly ILogger<OpenVrWrapper> _logger;
    private readonly Channel<VREvent_t> _eventChannel = Channel.CreateUnbounded<VREvent_t>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    /// <summary>
    /// Gets called when an event is received from OpenVR
    /// </summary>
    public event Func<VREvent_t, CancellationToken, Task> OnEventReceived { add => _onEventReceived.Add(value); remove => _onEventReceived.Remove(value); }
    private readonly AsyncEvent<Func<VREvent_t, CancellationToken, Task>> _onEventReceived = new();

    /// <summary>
    /// Gets called when OpenVR is shutdown
    /// </summary>
    public event Func<VREvent_t, CancellationToken, Task> OnShutdownReceived { add => _onShutReceived.Add(value); remove => _onShutReceived.Remove(value); }
    private readonly AsyncEvent<Func<VREvent_t, CancellationToken, Task>> _onShutReceived = new();

    /// <summary>
    /// Gets called when the SteamVR client is found and events are being received
    /// </summary>
    public event Func<CancellationToken, Task> OnSteamVrFound { add => _onSteamVrFound.Add(value); remove => _onSteamVrFound.Remove(value); }
    private readonly AsyncEvent<Func<CancellationToken, Task>> _onSteamVrFound = new();

    public CVRApplications Applications => Valve.VR.OpenVR.Applications;
    public CVRSystem System => Valve.VR.OpenVR.System;
    public bool AutoLaunch { get => Applications.GetApplicationAutoLaunch(_appId); set => Applications.SetApplicationAutoLaunch(_appId, value); }

    private readonly CancellationTokenSource _cancellationTokenSource = new();
    
    private Task _eventReceiverTask = Task.CompletedTask;
    private Task _dequeueTask = Task.CompletedTask;

    public OpenVrWrapper(ILogger<OpenVrWrapper> logger, IOptions<OpenVrWrapperSettings> options) {
        _logger = logger;
        _appManifestPath = ValidateManifestPath(options);
        var appManifest = GetAppManifest(_appManifestPath);

        _appId = appManifest.Applications.Count > 0 ? appManifest.Applications[0].AppKey : throw new Exception("Manifest dos not contain any applications");
    }

    /// <summary>
    /// Starts openvr service and returns a task that completes when openvr has been connected.
    /// </summary>
    /// <remarks>The returned task completes when the operation has started. If cancellation is requested via
    /// the associated cancellation token, the task may complete in a canceled state.</remarks>
    /// <returns>A task that represents the asynchronous start operation.</returns>
    public Task Start() => Task.Run(async () => await InternalStart(_cancellationTokenSource.Token));

    /// <summary>
    /// Starts openvr service and wait for openvr to connect.
    /// </summary>
    /// <returns>A task that represents the asynchronous start and wait operation.</returns>
    public async Task StartAndWaitAsync() => await InternalStart(_cancellationTokenSource.Token);

    /// <summary>
    /// The internal start method that actually connects the openvr service.
    /// </summary>
    /// <param name="token"></param>
    /// <returns></returns>
    /// <exception cref="OpenVrException">Thrown when an error occures while connecting</exception>
    private async Task InternalStart(CancellationToken token) {
        _logger.LogInformation("Starting OpenVR Service");
        try {
            var err = EVRInitError.None;
            
            while (!token.IsCancellationRequested) {
                Valve.VR.OpenVR.Init(ref err, EVRApplicationType.VRApplication_Background);

                if (err == EVRInitError.None) {
                    break; //successful start
                }

                if (err == EVRInitError.Init_NoServerForBackgroundApp) continue;
                
                _logger.LogError("Error occured while initializing OpenVR, error: {err}", err);
                throw new OpenVrException("Error occured while initializing OpenVR`", err);
            }

            ValidateInstalled(_logger, Applications, _appId, _appManifestPath);

            _eventReceiverTask = StartReceivingAsync();
            _dequeueTask = StartDequeueAsync();

            await _onSteamVrFound.InvokeAsync(token);
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Stops the openvr service and background processes.
    /// </summary>
    /// <returns></returns>
    public async Task StopAsync() {
        _logger.LogInformation("Stopping OpenVR Service");
        await _cancellationTokenSource.CancelAsync();

        try {
            await Task.WhenAll(_eventReceiverTask, _dequeueTask);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) {   
            _logger.LogError(e, "Error occurred while stopping OpenVR Service");
        }
    }

    /// <summary>
    /// Start receiving events from OpenVR and enqueue them to the channel.
    /// </summary>
    /// <returns></returns>
    private async Task StartReceivingAsync() {
        try {
            VREvent_t vrevent = new();
            while (Valve.VR.OpenVR.System.PollNextEvent(ref vrevent, (uint)Marshal.SizeOf(vrevent)) && !_cancellationTokenSource.IsCancellationRequested) {
                await _eventChannel.Writer.WriteAsync(vrevent, _cancellationTokenSource.Token);
            }

        }
        catch (OperationCanceledException) { }
        catch (Exception e) {
            _logger.LogError(e, "Error occurred while receiving SteamVR events");
            throw;
        }

    }

    /// <summary>
    /// Start dequeueing events from the channel and invoking the appropriate event handlers.
    /// </summary>
    /// <returns></returns>
    private async Task StartDequeueAsync() {
        while (!_cancellationTokenSource.IsCancellationRequested) {
            try {
                var vrevent = await _eventChannel.Reader.ReadAsync(_cancellationTokenSource.Token);

                if (vrevent.eventType == 700) {
                    // !!! its important not to await this. If this is awaited the StopAsync may be called and will hang because the StartDequeueAsync will never exit as its busy with awaiting the StopAsync method.
                    _ = Task.Run(async () => await _onShutReceived.InvokeAsync(vrevent, _cancellationTokenSource.Token));
                    break;
                }

                var eventCall = vrevent.eventType switch {
                    _ => _onEventReceived.InvokeAsync(vrevent, _cancellationTokenSource.Token)
                };

                await eventCall;
            }
            catch (Exception e) {
                _logger.LogError(e, "Error occurred while dequeueing SteamVR events");
                throw;
            }
        }
    }

    /// <summary>
    /// Validates if the options that were given are correct and returns the manifest path.
    /// </summary>
    /// <param name="options"></param>
    /// <returns></returns>
    /// <exception cref="Exception"></exception>
    private static string ValidateManifestPath(IOptions<OpenVrWrapperSettings> options) {
        if (options is null) {
            throw new Exception("No settings were provided");
        }

        var appManifestPath = options.Value.ManifestPath;
        if (string.IsNullOrEmpty(appManifestPath)) {
            throw new Exception("Manifest path was empty");
        }

        if (!Path.HasExtension(appManifestPath)) {
            appManifestPath = Path.Combine(appManifestPath, "app.vrmanifest");
        }

        return appManifestPath;
    }

    /// <summary>
    /// Reads appmanifest information from path
    /// </summary>
    /// <param name="appManifestPath"></param>
    /// <returns></returns>
    /// <exception cref="Exception"></exception>
    private static AppManifest GetAppManifest(string appManifestPath) {
        using var stream = File.OpenRead(appManifestPath);
        return JsonSerializer.Deserialize(stream, AppManifestTypeInfo.Default.AppManifest) ?? throw new Exception("Could not deserialize app manifest");
    }

    /// <summary>
    /// Validates if the app is registered with openvr and registers itself if its not.
    /// </summary>
    /// <param name="logger"></param>
    /// <param name="applications"></param>
    /// <param name="appId"></param>
    /// <param name="appManifestPath"></param>
    /// <exception cref="Exception"></exception>
    private static void ValidateInstalled(ILogger<OpenVrWrapper> logger, CVRApplications applications, string appId, string appManifestPath)
    {
        if (applications.IsApplicationInstalled(appId)) return;
        
        logger.LogInformation("Installing app.vrmanifest");
        var addManifestErr = applications.AddApplicationManifest(appManifestPath, false);

        if (addManifestErr == EVRApplicationError.None) return;
        
        logger.LogError("Unable to install app.vrmanifest, error: {err}", addManifestErr);
        throw new Exception($"Unable to install app.vrmanifest, error: {addManifestErr}");
    }

    public async ValueTask DisposeAsync() {
        GC.SuppressFinalize(this);
        await StopAsync();
    }
}

public class OpenVrWrapperSettings
{
    public string ManifestPath { get; set; } = string.Empty;
}

public class OpenVrException(string message, EVRInitError err) : Exception(message)
{
    public EVRInitError Error { get; init; } = err;
}