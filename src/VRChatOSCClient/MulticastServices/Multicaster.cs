using Makaretu.Dns;
using Makaretu.Dns.Resolving;
using Microsoft.Extensions.Logging;
using System.Net;
using VRChatOSCClient.TaskExtensions;

namespace VRChatOSCClient.MulticastServices;

internal class Multicaster
{
    private readonly ILogger<Multicaster> _logger;
    private readonly MulticastService _multicastService;
    private readonly ServiceDiscovery _serviceDiscovery;
    private ServiceProfile[] _profiles = [];

    public event Func<AnnouncedService, Task> ServiceAnswerd { add => _serviceAnswerdEvent.Add(value); remove => _serviceAnswerdEvent.Remove(value); }
    private readonly AsyncEvent<Func<AnnouncedService, Task>> _serviceAnswerdEvent = new();

    public Multicaster(ILogger<Multicaster> logger) {
        _logger = logger;
        _multicastService = new MulticastService { UseIpv6 = true, IgnoreDuplicateMessages = true };
        _serviceDiscovery = new ServiceDiscovery(_multicastService);

        _multicastService.NetworkInterfaceDiscovered += InterfaceDiscovered;
        _multicastService.AnswerReceived += AnswerReceived;
    }

    public void Start(params ServiceProfile[] serviceProfiles) {
        _logger.LogInformation("Multicaster starting");
        _profiles = serviceProfiles;
        _multicastService.Start();

        foreach (ServiceProfile profile in _profiles) {
            _serviceDiscovery.Advertise(profile);
        }
    }

    public void Stop() {
        _logger.LogInformation("Multicaster stopping");
        _multicastService.Stop();

        foreach (ServiceProfile profile in _profiles) {
            _serviceDiscovery.Unadvertise(profile);
        }

        _profiles = [];
    }

    private void InterfaceDiscovered(object? sender, NetworkInterfaceEventArgs args) {
        _logger.LogDebug("Network interface discovered");
        foreach(ServiceProfile profiles in _profiles) {
            _multicastService.SendQuery(profiles.QualifiedServiceName);
        }
    }

    private async void AnswerReceived(object? sender, MessageEventArgs args) {
        IEnumerable<SRVRecord> records = args.Message.AdditionalRecords.OfType<SRVRecord>();
        foreach(SRVRecord record in records) {
            IReadOnlyList<string> domainName = record.Name.Labels;
            IPAddress[] addresses = [.. args.Message.AdditionalRecords.OfType<ARecord>().Select(record => record.Address)];

            AnnouncedService? srvs = new(
                ServiceId: $"{record.CanonicalName}:{record.Port}",
                ServiceName: domainName[0],
                Addresses: addresses,
                Port: record.Port,
                Type: domainName[2]
                );

            _logger.LogDebug("Service located at: {address}:{port} as {serviceId} {instanceName}", string.Join(",", srvs.Addresses.Select(addr => addr.ToString())), srvs.Port, srvs.ServiceId, srvs.ServiceName);

            try {
                await _serviceAnswerdEvent.InvokeAsync(srvs);
            }
            catch (Exception ex) { _logger.LogError(ex, "Could not handle ServiceAnsweredEvent"); }
        }
    }
}

public record AnnouncedService(string ServiceId, string ServiceName, IPAddress[] Addresses, ushort Port, string Type);