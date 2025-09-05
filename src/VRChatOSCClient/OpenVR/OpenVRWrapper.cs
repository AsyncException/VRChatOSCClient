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

    public event Func<VREvent_t, CancellationToken, Task> OnEventReceived { add => _onEventReceived.Add(value); remove => _onEventReceived.Remove(value); }
    private readonly AsyncEvent<Func<VREvent_t, CancellationToken, Task>> _onEventReceived = new();

    private readonly Channel<VREvent_t> _eventChannel = Channel.CreateUnbounded<VREvent_t>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    public CVRApplications Applications { get; init; }
    public CVRSystem System { get; init; }
    public bool AutoLaunch { get => Applications.GetApplicationAutoLaunch(_appId); set => Applications.SetApplicationAutoLaunch(_appId, value); }

    private CancellationTokenSource _cancellationTokenSource = new();
    private Task _eventReceiverTask = Task.CompletedTask;
    private Task _dequeueTask = Task.CompletedTask;

    public OpenVRWrapper(ILogger<OpenVRWrapper> logger, IOptions<OpenVRWrapperSettings> options) {
        _logger = logger;
        _options = options;
        _appManifestPath = ValidateManifestPath(options);

        EVRInitError err = EVRInitError.None;
        Valve.VR.OpenVR.Init(ref err, EVRApplicationType.VRApplication_Background);

        if (err != EVRInitError.None) {
            _logger.LogError("Error occured while initializing OpenVR, error: {error}", err);
            throw new Exception($"Error occured while initializing OpenVR, error: {err}");
        }

        _appManifest = GetAppManifest(_appManifestPath);

        _appId = _appManifest.Applications.Count > 0 ? _appManifest.Applications[0].AppKey : throw new Exception("Manifest dos not contain any applications");

        Applications = Valve.VR.OpenVR.Applications;
        System = Valve.VR.OpenVR.System;

        ValidateInstalled(_logger, Applications, _appId, _appManifestPath);

        _eventReceiverTask = StartReceivingAsync();
        _dequeueTask = StartDequeueAsync();
    }
    
    private async Task StartReceivingAsync() {
        while (!_cancellationTokenSource.IsCancellationRequested) {
            VREvent_t vrevent = new();
            while(Valve.VR.OpenVR.System.PollNextEvent(ref vrevent, (uint)Marshal.SizeOf(vrevent)) && !_cancellationTokenSource.IsCancellationRequested) {
                await _eventChannel.Writer.WriteAsync(vrevent);
            }
        }
    }

    private async Task StartDequeueAsync() {
        while (!_cancellationTokenSource.IsCancellationRequested) {
            VREvent_t vrevent = await _eventChannel.Reader.ReadAsync(_cancellationTokenSource.Token);
            await _onEventReceived.InvokeAsync(vrevent, _cancellationTokenSource.Token);
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

        if (Path.HasExtension(appManifestPath)) {
            appManifestPath = Path.GetDirectoryName(appManifestPath) ?? throw new Exception("Couldnt get directory for the manifest");
        }

        return appManifestPath;
    }
    private static AppManifest GetAppManifest(string appManifestPath) {
        using FileStream stream = File.OpenRead(Path.Combine(appManifestPath, "app.vrmanifest"));
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