using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using Valve.VR;
using VRChatOSCClient.TaskExtensions;

namespace VRChatOSCClient.OpenVR;

public class OpenVRWrapper
{
    private readonly string _appId;
    private readonly string _appManifestPath;
    private readonly AppManifest _appManifest;
    private readonly ILogger<OpenVRWrapper> _logger;
    private readonly IOptions<OpenVRWrapperSettings>? _options;
    private readonly TaskCompletionSource _firstClientTcs;

    public event Func<VREvent_t, CancellationToken, Task> OnEventReceived { add => _onEventReceived.Add(value); remove => _onEventReceived.Remove(value); }
    private readonly AsyncEvent<Func<VREvent_t, CancellationToken, Task>> _onEventReceived = new();

    public event Func<CancellationToken, Task> OnSteamVRFound { add => _onSteamVRFound.Add(value); remove => _onSteamVRFound.Remove(value); }
    private readonly AsyncEvent<Func<CancellationToken, Task>> _onSteamVRFound = new();

    private readonly Channel<VREvent_t> _eventChannel = Channel.CreateUnbounded<VREvent_t>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

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

        _firstClientTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        _appManifest = GetAppManifest(_appManifestPath);

        _appId = _appManifest.Applications.Count > 0 ? _appManifest.Applications[0].AppKey : throw new Exception("Manifest dos not contain any applications");
    }

    public void Start() {
        _ = Task.Run(async () => await InternalStart(CancellationToken.None));
    }

    public async Task StartAndWaitAsync(CancellationToken token = default) {
        await InternalStart(CancellationToken.None);
    }

    private async Task InternalStart(CancellationToken token) {
        while(!token.IsCancellationRequested) {
            EVRInitError err = EVRInitError.None;
            Valve.VR.OpenVR.Init(ref err, EVRApplicationType.VRApplication_Background);
            
            if(err == EVRInitError.None) {
                break; //successful start
            }

            if(err != EVRInitError.Init_NoServerForBackgroundApp) {
                _logger.LogError("Error occured while initializing OpenVR, error: {error}", err);
                throw new OpenVRException("Error occured while initializing OpenVR`", err);
            }

            await Task.Delay(1000, token);
        }

        ValidateInstalled(_logger, Applications, _appId, _appManifestPath);
        _eventReceiverTask = Task.Run(StartReceivingAsync);
        _dequeueTask = Task.Run(StartDequeueAsync);

        await _onSteamVRFound.InvokeAsync(token);
    }
    
    private async Task StartReceivingAsync() {
        try {
            while (!_cancellationTokenSource.IsCancellationRequested) {
                VREvent_t vrevent = new();
                while (Valve.VR.OpenVR.System.PollNextEvent(ref vrevent, (uint)Marshal.SizeOf(vrevent)) && !_cancellationTokenSource.IsCancellationRequested) {
                    await _eventChannel.Writer.WriteAsync(vrevent);
                }
            }
        }
        catch(Exception e) {
            _logger.LogError(e, "Error occurred while receiving SteamVR events");
            throw;
        }
        
    }

    private async Task StartDequeueAsync() {
        try {
            while (!_cancellationTokenSource.IsCancellationRequested) {
                VREvent_t vrevent = await _eventChannel.Reader.ReadAsync(_cancellationTokenSource.Token);
                await _onEventReceived.InvokeAsync(vrevent, _cancellationTokenSource.Token);
            }
        }
        catch (Exception e) {
            _logger.LogError(e, "Error occurred while dequeueing SteamVR events");
            throw;
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
}

public class OpenVRWrapperSettings {
    public string ManifestPath { get; set; } = string.Empty;
}

public class OpenVRException(string message, EVRInitError err) : Exception(message)
{
    public EVRInitError Error { get; init; } = err;
}