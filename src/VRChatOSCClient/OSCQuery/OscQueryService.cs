using Makaretu.Dns;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using VRChatOSCClient.HttpServer;
using VRChatOSCClient.Models;
using VRChatOSCClient.MulticastServices;
using VRChatOSCClient.TaskExtensions;

namespace VRChatOSCClient.OSCQuery;


internal class OscQueryService
{
    private readonly Settings _settings;
    private readonly Multicaster _multicaster;
    private readonly HostInfoHttpServer _httpServer;
    private readonly VRChatDataFetcher _dataFetcher;
    private readonly ILogger<OscQueryService> _logger;

    public int HttpPort { get; init; }
    public int OscReceivePort { get; init; }
    private string LatestClient { get; set; } = string.Empty;


    public event Func<VRChatConnectionInfo, CancellationToken, Task> OnVrchatClientFound { add => _onVrchatClientFoundEvent.Add(value); remove => _onVrchatClientFoundEvent.Remove(value); }
    private readonly AsyncEvent<Func<VRChatConnectionInfo, CancellationToken, Task>> _onVrchatClientFoundEvent = new();

    public OscQueryService(ILogger<OscQueryService> logger, HostInfoHttpServer httpServer, Multicaster multicaster, Settings settings, VRChatDataFetcher dataFetcher) {
        _logger = logger;
        _settings = settings;
        _httpServer = httpServer;
        _multicaster = multicaster;
        _dataFetcher = dataFetcher;

        _multicaster.ServiceAnswered += ServiceFound;
        
        HttpPort = GetAvailablePort(ProtocolType.Tcp);
        OscReceivePort = GetAvailablePort(ProtocolType.Udp);
    }

    public void Start(CancellationToken token) {
        _logger.LogStartingOscQueryService();

        _httpServer.Start(_settings.Address.ToString(), (ushort)HttpPort, HttpServerResponse, token);

        ServiceProfile httpProfile = new(_settings.ServiceName, "_oscjson._tcp", (ushort)HttpPort, [_settings.Address]);
        ServiceProfile oscProfile = new(_settings.ServiceName, "_osc._udp", (ushort)OscReceivePort, [_settings.Address]);

        _multicaster.ServiceAnswered += ServiceFound;
        _multicaster.Start(httpProfile, oscProfile);
    }

    public async Task StopAsync(CancellationToken token = default) {
        _logger.LogStoppingOscQueryService();

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

    public int GetAvailablePort(ProtocolType type) {
        try {
            using Socket soc = new(AddressFamily.InterNetwork, type == ProtocolType.Udp ? SocketType.Dgram : SocketType.Stream, type);
            soc.Bind(new IPEndPoint(_settings.Address, 0));
            return ((IPEndPoint)soc.LocalEndPoint!).Port;
        }
        catch(Exception ex) {
            _logger.LogAvailablePortError(ex);
            throw;
        }
    }
}


internal static partial class OscQueryInfoLogger {
    [LoggerMessage(LogLevel.Information, "Starting OscQueryService")]
    public static partial void LogStartingOscQueryService(this ILogger<OscQueryService> logger);

    [LoggerMessage(LogLevel.Information, "Stopping OscQueryService")]
    public static partial void LogStoppingOscQueryService(this ILogger<OscQueryService> logger);

    [LoggerMessage(LogLevel.Error, "Unable to find open UDP port")]
    public static partial void LogAvailablePortError(this ILogger<OscQueryService> logger, Exception ex);
}