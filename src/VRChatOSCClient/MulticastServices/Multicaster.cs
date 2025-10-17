using Makaretu.Dns;
using Makaretu.Dns.Resolving;
using Microsoft.Extensions.Logging;
using System.Net;
using VRChatOSCClient.TaskExtensions;
using VRChatOSCClient.Utilities;

namespace VRChatOSCClient.MulticastServices;

/// <summary>
/// Service that handles multicast DNS service discovery and advertisement
/// </summary>
internal class Multicaster : IDisposable
{
    private readonly ILogger<Multicaster> _logger;
    private readonly MulticastService _multicastService;
    private readonly ServiceDiscovery _serviceDiscovery;
    private ServiceProfile[] _profiles = [];

    /// <summary>
    /// This event gets called when a new service is discovered on the network
    /// </summary>
    public event Func<AnnouncedService, CancellationToken, Task> ServiceAnswered { add => _serviceAnsweredEvent.Add(value); remove => _serviceAnsweredEvent.Remove(value); }
    private readonly AsyncEvent<Func<AnnouncedService, CancellationToken, Task>> _serviceAnsweredEvent = new();

    private CancellationTokenSource _cts = new();

    public Multicaster(ILogger<Multicaster> logger) {
        _logger = logger;
        _multicastService = new MulticastService { UseIpv6 = false, IgnoreDuplicateMessages = true };
        _serviceDiscovery = new ServiceDiscovery(_multicastService);
    }

    /// <summary>
    /// Starts the multicaster, advertising the given service profiles and listening for answers
    /// </summary>
    /// <param name="serviceProfiles">The profiles to advertise</param>
    public void Start(params ServiceProfile[] serviceProfiles) {
        _logger.LogInformation("Multicaster starting");

        CancellationTokenResetter.Reset(ref _cts);

        _multicastService.NetworkInterfaceDiscovered += InterfaceDiscovered;
        _multicastService.AnswerReceived += AnswerReceivedAsync;

        _profiles = serviceProfiles;
        _multicastService.Start();

        foreach (ServiceProfile profile in _profiles) {
            _serviceDiscovery.Advertise(profile);
        }
    }

    /// <summary>
    /// Stops the multicaster, unadvertising all services and stopping listening
    /// </summary>
    public void Stop() {
        _logger.LogInformation("Multicaster stopping");

        _cts.Cancel();

        foreach (ServiceProfile profile in _profiles) {
            _serviceDiscovery.Unadvertise(profile);
        }

        _multicastService.Stop();

        _profiles = [];

        _multicastService.NetworkInterfaceDiscovered -= InterfaceDiscovered;
        _multicastService.AnswerReceived -= AnswerReceivedAsync;
    }

    private void InterfaceDiscovered(object? sender, NetworkInterfaceEventArgs args) {
        _logger.LogDebug("Network interface discovered");
        foreach(ServiceProfile profile in _profiles) {
            _multicastService.SendQuery(profile.QualifiedServiceName);
        }
    }

    private async void AnswerReceivedAsync(object? sender, MessageEventArgs args) {
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

            //_logger.LogDebug("Service located at: {address}:{port} as {serviceId} {instanceName}", string.Join(",", srvs.Addresses.Select(addr => addr.ToString())), srvs.Port, srvs.ServiceId, srvs.ServiceName);

            try {
                await _serviceAnsweredEvent.InvokeAsync(srvs, _cts.Token);
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Could not handle ServiceAnsweredEvent"); 
            }
        }
    }

    public void Dispose() {
        GC.SuppressFinalize(this);
        _multicastService.Dispose();
        _serviceDiscovery.Dispose();
    }
}
