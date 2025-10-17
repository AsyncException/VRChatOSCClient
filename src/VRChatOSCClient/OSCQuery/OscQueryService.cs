using Makaretu.Dns;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using VRChatOSCClient.HttpServer;
using VRChatOSCClient.MulticastServices;
using VRChatOSCClient.TaskExtensions;

namespace VRChatOSCClient.OSCQuery;


internal class OscQueryService(ILogger<OscQueryService> logger, HostInfoHttpServer httpServer, Multicaster multicaster, Settings settings, VRChatDataFetcher dataFetcher)
{
    private readonly ILogger<OscQueryService> _logger = logger;
    private readonly HostInfoHttpServer _httpServer = httpServer;
    private readonly Multicaster _multicaster = multicaster;
    private readonly Settings _settings = settings;
    private readonly VRChatDataFetcher _dataFetcher = dataFetcher;

    public int HttpPort { get; init; } = GetAvailablePort(ProtocolType.Tcp, settings);
    public int OscReceivePort { get; init; } = GetAvailablePort(ProtocolType.Udp, settings);
    private string LatestClient { get; set; } = string.Empty;


    public event Func<VRChatConnectionInfo, CancellationToken, Task> OnVrchatClientFound { add => _onVrchatClientFoundEvent.Add(value); remove => _onVrchatClientFoundEvent.Remove(value); }
    private readonly AsyncEvent<Func<VRChatConnectionInfo, CancellationToken, Task>> _onVrchatClientFoundEvent = new();

    public void Start() {
        _logger.LogInformation("Starting OscQueryService");

        _httpServer.Start(_settings.Address.ToString(), (ushort)HttpPort, HttpServerResponse);

        ServiceProfile httpProfile = new(_settings.ServiceName, "_oscjson._tcp", (ushort)HttpPort, [_settings.Address]);
        ServiceProfile oscProfile = new(_settings.ServiceName, "_osc._udp", (ushort)OscReceivePort, [_settings.Address]);

        _multicaster.ServiceAnswered += ServiceFound;
        _multicaster.Start(httpProfile, oscProfile);
    }

    public async Task StopAsync(CancellationToken token = default) {
        _logger.LogInformation("Stopping OscQueryService");

        _multicaster.Stop();
        _multicaster.ServiceAnswered -= ServiceFound;
        await _httpServer.StopAsync(token).ConfigureAwait(false);

        // Reset the latest client to avoid stale connections
        LatestClient = string.Empty;
    }

    private string HttpServerResponse(bool hasHostInfo) {
        if (hasHostInfo) {
            HostInfo info = new(_settings.ServiceName, _settings.Address, OscReceivePort);
            return info.ToString();
        }

        return OscInfo.ToJson();
    }

    private async Task ServiceFound(AnnouncedService service, CancellationToken token) {
        if (service.Type != "_tcp" || !service.ServiceName.StartsWith("VRChat-Client-") || LatestClient.Equals(service.ServiceId)) {
            return;
        }

        LatestClient = service.ServiceId;

        VRChatConnectionInfo connectionInfo = new() {
            ReceiveEndpoint = new(_settings.Address, OscReceivePort),
            SendEndpoint = await _dataFetcher.GetConnectionEndpoint(service.Addresses.First(), service.Port),
            OSCQueryEndpoint = new(service.Addresses.First(), service.Port)
        };

        await _onVrchatClientFoundEvent.InvokeAsync(connectionInfo, token);
    }

    public static int GetAvailablePort(ProtocolType type, Settings settings) {
        try {
            using Socket soc = new(AddressFamily.InterNetwork, type == ProtocolType.Udp ? SocketType.Dgram : SocketType.Stream, type);
            soc.Bind(new IPEndPoint(settings.Address, 0));
            return ((IPEndPoint)soc.LocalEndPoint!).Port;
        }
        catch {
            Debug.WriteLine("Unable to find open Udp port"); // Keep monitoring if this how likely it is that this fails.
            throw;
        }
    }
}
