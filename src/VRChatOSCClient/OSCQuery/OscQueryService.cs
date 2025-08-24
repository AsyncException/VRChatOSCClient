using Makaretu.Dns;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using VRChatOSCClient.HttpServer;
using VRChatOSCClient.MulticastServices;
using VRChatOSCClient.OSCConnections;
using VRChatOSCClient.TaskExtensions;
using Message = VRChatOSCClient.OSCConnections.Message;

namespace VRChatOSCClient.OSCQuery;


internal class OscQueryService
{
    private readonly ILogger<OscQueryService> _logger;
    private readonly HostInfoHttpServer _httpServer;
    private readonly Multicaster _multicaster;
    private readonly Settings _settings;
    private readonly VRChatDataFetcher _dataFetcher;

    public int HttpPort { get; private set; }
    public int OscReceivePort { get; private set; }
    private HostInfo HostInfo { get; set; } = null!;
    private string LatestClient { get; set; } = string.Empty;


    public event Func<VRChatConnectionInfo, CancellationToken, Task> OnVrchatClientFound { add => _onVrchatClientFoundEvent.Add(value); remove => _onVrchatClientFoundEvent.Remove(value); }
    private readonly AsyncEvent<Func<VRChatConnectionInfo, CancellationToken, Task>> _onVrchatClientFoundEvent = new();

    public OscQueryService(ILogger<OscQueryService> logger, HostInfoHttpServer httpServer, Multicaster multicaster, Settings settings, VRChatDataFetcher dataFetcher) {
        _logger = logger;
        _settings = settings;
        _httpServer = httpServer;
        _multicaster = multicaster;
        _dataFetcher = dataFetcher;

        _multicaster.ServiceAnswerd += ServiceFound;
        
        HttpPort = GetAvailablePort(ProtocolType.Tcp);
        OscReceivePort = GetAvailablePort(ProtocolType.Udp);

        HostInfo = new(_settings.ServiceName, _settings.Address, OscReceivePort);
    }

    public void Start(CancellationToken token) {
        _logger.LogInformation("Starting OscQueryService");

        _httpServer.Start(_settings.Address.ToString(), (ushort)HttpPort, hasHostInfo => hasHostInfo ? HostInfo.ToString() : OscInfo.ToJson(), token);

        ServiceProfile httpProfile = new(_settings.ServiceName, "_oscjson._tcp", (ushort)HttpPort, [_settings.Address]);
        ServiceProfile oscProfile = new(_settings.ServiceName, "_osc._udp", (ushort)OscReceivePort, [_settings.Address]);

        _multicaster.Start(token, httpProfile, oscProfile);
    }

    public async Task StopAsync(CancellationToken token = default) {
        _logger.LogInformation("Stopping OscQueryService");

        _multicaster.Stop();
        await _httpServer.StopAsync(token);

        // Reset the latest client to avoid stale connections
        LatestClient = string.Empty;
    }

    private async Task ServiceFound(AnnouncedService service, CancellationToken token) {
        if (service.Type != "_tcp" || !service.ServiceName.StartsWith("VRChat-Client-") || LatestClient.Equals(service.ServiceId)) {
            return;
        }

        VRChatConnectionInfo connectionInfo = new() {
            ReceiveEndpoint = new(_settings.Address, OscReceivePort),
            SendEndpoint = await _dataFetcher.GetConnectionEndpoint(service.Addresses.First(), service.Port),
            OSCQueryEndpoint = new(service.Addresses.First(), service.Port)
        };

        await _onVrchatClientFoundEvent.InvokeAsync(connectionInfo, token);

        LatestClient = service.ServiceId;
    }

    public static int GetAvailablePort(ProtocolType type) {
        try {
            using Socket soc = new(AddressFamily.InterNetwork, type == ProtocolType.Udp ? SocketType.Dgram : SocketType.Stream, type);
            soc.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)soc.LocalEndPoint!).Port;
        }
        catch {
            Debug.WriteLine("Unable to find open Udp port"); // Keep monitoring if this how likely it is that this fails.
            throw;
        }
    }
}
