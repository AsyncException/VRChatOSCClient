using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using Valve.VR;
using VRChatOSCClient.TaskExtensions;
using VRChatOSCClient.Utilities;

namespace VRChatOSCClient.OpenVR;

public class OpenVRWrapper : IAsyncDisposable
{
    private readonly string _appId;
    private readonly string _appManifestPath;
    private readonly AppManifest _appManifest;
    private readonly ILogger<OpenVRWrapper> _logger;
    private readonly IOptions<OpenVRWrapperSettings>? _options;
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
    public event Func<CancellationToken, Task> OnSteamVRFound { add => _onSteamVRFound.Add(value); remove => _onSteamVRFound.Remove(value); }
    private readonly AsyncEvent<Func<CancellationToken, Task>> _onSteamVRFound = new();

    public CVRApplications Applications => Valve.VR.OpenVR.Applications;
    public CVRSystem System => Valve.VR.OpenVR.System;
    public bool AutoLaunch { get => Applications.GetApplicationAutoLaunch(_appId); set => Applications.SetApplicationAutoLaunch(_appId, value); }

    private CancellationTokenSource _cancellationTokenSource = new();
    private Task _eventReceiverTask = Task.CompletedTask;
    private Task _dequeueTask = Task.CompletedTask;

    public OpenVRWrapper(ILogger<OpenVRWrapper> logger, IOptions<OpenVRWrapperSettings> options) {
        _logger = logger;
        _options = options;
        _appManifestPath = ValidateManifestPath(options);
        _appManifest = GetAppManifest(_appManifestPath);

        _appId = _appManifest.Applications.Count > 0 ? _appManifest.Applications[0].AppKey : throw new Exception("Manifest dos not contain any applications");
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
    /// <param name="token">A cancellation token that can be used to cancel the operation before it completes.</param>
    /// <returns>A task that represents the asynchronous start and wait operation.</returns>
    public async Task StartAndWaitAsync() => await InternalStart(_cancellationTokenSource.Token);

    /// <summary>
    /// The internal start method that actually connects the openvr service.
    /// </summary>
    /// <param name="token"></param>
    /// <returns></returns>
    /// <exception cref="OpenVRException">Thrown when an error occures while connecting</exception>
    private async Task InternalStart(CancellationToken token) {
        _logger.LogStartingOpenVRService();
        while(!token.IsCancellationRequested) {
            EVRInitError err = EVRInitError.None;
            Valve.VR.OpenVR.Init(ref err, EVRApplicationType.VRApplication_Background);
            
            if(err == EVRInitError.None) {
                break; //successful start
            }

            if(err != EVRInitError.Init_NoServerForBackgroundApp) {
                _logger.LogIntializationError(err);
                throw new OpenVRException("Error occured while initializing OpenVR`", err);
            }

            ValidateInstalled(_logger, Applications, _appId, _appManifestPath);

            _eventReceiverTask = StartReceivingAsync();
            _dequeueTask = StartDequeueAsync();

            await _onSteamVRFound.InvokeAsync(token);
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Stops the OpenVR service and stops listening for OpenVR calls
    /// </summary>
    /// <returns></returns>
    public async Task StopAsync() {
        _cancellationTokenSource.Cancel();
        if (_eventReceiverTask is not null) {
            await _eventReceiverTask;
            _eventReceiverTask = null!;
        }

        if (_dequeueTask is not null) {
            await _dequeueTask;
            _dequeueTask = null!;
        }

        _cancellationTokenSource.Dispose();
    }

    /// <summary>
    /// Stops the openvr service and background processes.
    /// </summary>
    /// <returns></returns>
    public async Task StopAsync() {
        _logger.LogStoppingOpenVRService();
        _cancellationTokenSource.Cancel();
        try {
            await Task.WhenAll(_eventReceiverTask, _dequeueTask);
        }
        catch (OperationCanceledException) {}
        catch (Exception e) {
            _logger.LogStoppingError(e);
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
            catch (OperationCanceledException) {
                break;
            }
            catch (Exception e) {
                _logger.LogError(e, "Error occurred while receiving SteamVR events");
                throw;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) {
            _logger.LogReceivingError(e);
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
                VREvent_t vrevent = await _eventChannel.Reader.ReadAsync(_cancellationTokenSource.Token);

                if(vrevent.eventType == 700) {
                    // !!! its important not to await this. If this is awaited the StopAsync may be called and it will hang because the StartDequeueAsync will never exit as its busy with awaiting the StopAsync method.
                    _ = Task.Run(async() => await _onShutReceived.InvokeAsync(vrevent, _cancellationTokenSource.Token));
                    break;
                }

                Task eventCall = vrevent.eventType switch {
                    _ => _onEventReceived.InvokeAsync(vrevent, _cancellationTokenSource.Token)
                };

                await eventCall;
            }
        }
        catch (Exception e) {
            _logger.LogDequeueError(e);
            throw;
        }
    }

    /// <summary>
    /// Validates if the options that were given are correct and returns the manifest path.
    /// </summary>
    /// <param name="options"></param>
    /// <returns></returns>
    /// <exception cref="Exception"></exception>
    private static string ValidateManifestPath(IOptions<OpenVRWrapperSettings> options) {
        if (options is null) {
            throw new Exception("No settings were provided");
        }

        string appManifestPath = options.Value.ManifestPath;
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
        using FileStream stream = File.OpenRead(appManifestPath);
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
    private static void ValidateInstalled(ILogger<OpenVRWrapper> logger, CVRApplications applications, string appId, string appManifestPath) {
        if (!applications.IsApplicationInstalled(appId)) {
            logger.LogInstallingManifest();
            EVRApplicationError addManifestErr = applications.AddApplicationManifest(appManifestPath, false);
            if (addManifestErr != EVRApplicationError.None) {
                logger.LogInstallingManifestError(addManifestErr);
                throw new Exception($"Unable to install app.vrmanifest, error: {addManifestErr}");
            }
        }
    }

    public async ValueTask DisposeAsync() {
        GC.SuppressFinalize(this);
        await StopAsync();
    }
}

public class OpenVRWrapperSettings
{
    public string ManifestPath { get; set; } = string.Empty;
}

public class OpenVRException(string message, EVRInitError err) : Exception(message)
{
    public EVRInitError Error { get; init; } = err;
}

public static partial class OpenVRWrapperLogger
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Starting OpenVR Service")]
    public static partial void LogStartingOpenVRService(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stopping OpenVR Service")]
    public static partial void LogStoppingOpenVRService(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error occured while initializing OpenVR, error: {err}")]
    public static partial void LogIntializationError(this ILogger logger, EVRInitError err);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error occurred while stopping OpenVRWrapper")]
    public static partial void LogStoppingError(this ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error occurred while receiving SteamVR events")]
    public static partial void LogReceivingError(this ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error occurred while dequeueing SteamVR events")]
    public static partial void LogDequeueError(this ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Installing app.vrmanifest")]
    public static partial void LogInstallingManifest(this ILogger logger);
    
    [LoggerMessage(Level = LogLevel.Error, Message = "Unable to install app.vrmanifest, error: {err}")]
    public static partial void LogInstallingManifestError(this ILogger logger, EVRApplicationError err);
}