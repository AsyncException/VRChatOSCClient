using Makaretu.Dns;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;
using VRChatOSCClient.HttpServer;
using VRChatOSCClient.Models;
using VRChatOSCClient.MulticastServices;
using VRChatOSCClient.TaskExtensions;

namespace VRChatOSCClient.OSCQuery;


internal class OscQueryService(ILogger<OscQueryService> logger, HostInfoHttpServer httpServer, Multicaster multicaster, Settings settings, VrChatDataFetcher dataFetcher)
{
    public int HttpPort { get; } = GetAvailablePort(ProtocolType.Tcp, settings, logger);
    public int OscReceivePort { get; } = GetAvailablePort(ProtocolType.Udp,  settings, logger);
    private string LatestClient { get; set; } = string.Empty;


    public event Func<VrChatConnectionInfo, CancellationToken, Task> OnVrchatClientFound { add => _onVrchatClientFoundEvent.Add(value); remove => _onVrchatClientFoundEvent.Remove(value); }
    private readonly AsyncEvent<Func<VrChatConnectionInfo, CancellationToken, Task>> _onVrchatClientFoundEvent = new();
    

    public void Start(CancellationToken token) {
        logger.LogInformation("Starting OscQueryService");
        multicaster.ServiceAnswered += ServiceFound;

        httpServer.Start(settings.Address.ToString(), (ushort)HttpPort, HttpServerResponse, token);

        ServiceProfile httpProfile = new(settings.ServiceName, "_oscjson._tcp", (ushort)HttpPort, [settings.Address]);
        ServiceProfile oscProfile = new(settings.ServiceName, "_osc._udp", (ushort)OscReceivePort, [settings.Address]);

        multicaster.ServiceAnswered += ServiceFound;
        multicaster.Start(httpProfile, oscProfile);
    }

    public async Task StopAsync() {
        logger.LogInformation("Stopping OscQueryService");

        multicaster.Stop();
        multicaster.ServiceAnswered -= ServiceFound;
        await httpServer.StopAsync();

        // Reset the latest client to avoid stale connections
        LatestClient = string.Empty;
    }

    private string HttpServerResponse(bool hasHostInfo) {
        if (!hasHostInfo)
            return OscInfo.ToJson();
        
        HostInfo info = new(settings.ServiceName, settings.Address, OscReceivePort);
        return info.ToString();

    }

    private async Task ServiceFound(AnnouncedService service, CancellationToken token) {
        if (service.Type != "_tcp" || !service.ServiceName.StartsWith("VRChat-Client-") || LatestClient.Equals(service.ServiceId)) {
            return;
        }

        LatestClient = service.ServiceId;

        var connectionInfo = new VrChatConnectionInfo {
            ReceiveEndpoint = new IPEndPoint(settings.Address, OscReceivePort),
            SendEndpoint = await dataFetcher.GetConnectionEndpoint(service.Addresses.First(), service.Port),
            OscQueryEndpoint = new IPEndPoint(service.Addresses.First(), service.Port)
        };

        await _onVrchatClientFoundEvent.InvokeAsync(connectionInfo, token);
    }

    private static int GetAvailablePort(ProtocolType type, Settings settings, ILogger<OscQueryService> logger) {
        try {
            using Socket soc = new(AddressFamily.InterNetwork, type == ProtocolType.Udp ? SocketType.Dgram : SocketType.Stream, type);
            soc.Bind(new IPEndPoint(settings.Address, 0));
            return ((IPEndPoint)soc.LocalEndPoint!).Port;
        }
        catch(Exception ex) {
            logger.LogError(ex, "Unable to find open UDP port");
            throw;
        }
    }
}