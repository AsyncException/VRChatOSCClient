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
    /// Starts the OpenVR service and starts listening for OpenVR calls
    /// </summary>
    public void Start() => _ = InternalStart(_cancellationTokenSource.Token);

    /// <summary>
    /// Starts the OpenVR service and starts listening for OpenVR calls, awaiting until SteamVR is found
    /// </summary>
    /// <returns></returns>
    public async Task StartAndWaitAsync() => await InternalStart(_cancellationTokenSource.Token);

    private async Task InternalStart(CancellationToken token) {
        try {
            CancellationTokenResetter.Reset(ref _cancellationTokenSource);

            while (!token.IsCancellationRequested) {
                EVRInitError err = EVRInitError.None;
                Valve.VR.OpenVR.Init(ref err, EVRApplicationType.VRApplication_Background);

                if (err == EVRInitError.None) {
                    break; //successful start
                }

                if (err != EVRInitError.Init_NoServerForBackgroundApp) {
                    _logger.LogError("Error occured while initializing OpenVR, error: {error}", err);
                    throw new OpenVRException("Error occured while initializing OpenVR`", err);
                }

                await Task.Delay(1000, token);
            }

            if (token.IsCancellationRequested) {
                return;
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

    private async Task StartReceivingAsync() {
        await Task.Yield();

        VREvent_t vrevent = new();
        uint eventSize = (uint)Marshal.SizeOf(vrevent);

        while (!_cancellationTokenSource.IsCancellationRequested) {
            try {
                bool gotevents = false;
                while (Valve.VR.OpenVR.System.PollNextEvent(ref vrevent, eventSize) && !_cancellationTokenSource.IsCancellationRequested) {
                    gotevents = true;
                    await _eventChannel.Writer.WriteAsync(vrevent, _cancellationTokenSource.Token);
                }

                if(!gotevents) {
                    await Task.Delay(10, _cancellationTokenSource.Token);
                }
            }
            catch (OperationCanceledException) {
                break;
            }
            catch (Exception e) {
                _logger.LogError(e, "Error occurred while receiving SteamVR events");
                throw;
            }
        }
    }
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
            catch (OperationCanceledException) {
                break;
            }
            catch (Exception e) {
                _logger.LogError(e, "Error occurred while dequeueing SteamVR events");
                throw;
            }
        }
    }

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
    private static AppManifest GetAppManifest(string appManifestPath) {
        using FileStream stream = File.OpenRead(appManifestPath);
        return JsonSerializer.Deserialize(stream, AppManifestTypeInfo.Default.AppManifest) ?? throw new Exception("Could not deserialize app manifest");
    }
    private static void ValidateInstalled(ILogger<OpenVRWrapper> logger, CVRApplications applications, string appId, string appManifestPath) {
        if (!applications.IsApplicationInstalled(appId)) {
            logger.LogInformation("Installing app.vrmanifest");
            EVRApplicationError addManifestErr = applications.AddApplicationManifest(appManifestPath, false);
            if (addManifestErr != EVRApplicationError.None) {
                logger.LogError("Unable to install app.vrmanifest, error: {error}", addManifestErr);
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