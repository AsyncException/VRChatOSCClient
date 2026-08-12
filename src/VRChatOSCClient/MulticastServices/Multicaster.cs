using Makaretu.Dns;
using Microsoft.Extensions.Logging;
using System.Net;
using VRChatOSCClient.TaskExtensions;
using static System.String;

namespace VRChatOSCClient.MulticastServices;

/// <summary>
/// Service that handles multicast DNS service discovery and advertisement
/// </summary>
internal sealed class Multicaster(ILogger<Multicaster> logger) : IDisposable
{
    private RunningState? _state;

    private ServiceProfile[] _profiles = [];

    /// <summary>
    /// This event gets called when a new service is discovered on the network
    /// </summary>
    public event Func<AnnouncedService, CancellationToken, Task> ServiceAnswered { add => _serviceAnsweredEvent.Add(value); remove => _serviceAnsweredEvent.Remove(value); }
    private readonly AsyncEvent<Func<AnnouncedService, CancellationToken, Task>> _serviceAnsweredEvent = new();

    /// <summary>
    /// Starts the multicaster, advertising the given service profiles and listening for answers
    /// </summary>
    /// <param name="serviceProfiles">The profiles to advertise</param>
    public void Start(params ServiceProfile[] serviceProfiles) {
        if(_state is not null) {
            logger.LogInformation("Multicaster already started");
            throw new InvalidOperationException("Multicaster is already started");
        }

        logger.LogInformation("Multicaster starting");

        var state = new RunningState();
        state.MulticastService.NetworkInterfaceDiscovered += InterfaceDiscovered;
        state.MulticastService.AnswerReceived += AnswerReceivedAsync;

        state.MulticastService.Start();

        _profiles = serviceProfiles;

        foreach (var profile in _profiles) {
            state.ServiceDiscovery.Advertise(profile);
        }

        _state = state;
    }

    /// <summary>
    /// Stops the multicaster, unadvertising all services and stopping listening
    /// </summary>
    public void Stop() {
        var state = _state;

        if(state is null) {
            return;
        }

        logger.LogInformation("Multicaster stopping");

        foreach (var profile in _profiles) {
            state.ServiceDiscovery.Unadvertise(profile);
        }

        state.MulticastService.NetworkInterfaceDiscovered -= InterfaceDiscovered;
        state.MulticastService.AnswerReceived -= AnswerReceivedAsync;

        state.MulticastService.Stop();
        state.Cts.Cancel();

        state.Dispose();

        _profiles = [];

        _state = null;
    }

    private void InterfaceDiscovered(object? sender, NetworkInterfaceEventArgs args) {
        var state = _state;
        if(state is null) {
            return;
        }

        logger.LogDebug("Network interface discovered");

        foreach (var profiles in _profiles) {
            state.MulticastService.SendQuery(profiles.QualifiedServiceName);
        }
    }

    private async void AnswerReceivedAsync(object? sender, MessageEventArgs args)
    {
        try
        {
            var state = _state;
            if (state is null) {
                return;
            }

            var records = args.Message.AdditionalRecords.OfType<SRVRecord>();
            foreach (var record in records) {
                var domainName = record.Name.Labels;
                IPAddress[] addresses = [.. args.Message.AdditionalRecords.OfType<ARecord>().Select(aRecord => aRecord.Address)];

                var srvs = new AnnouncedService(
                    ServiceId: $"{record.CanonicalName}:{record.Port}",
                    ServiceName: domainName[0],
                    Addresses: addresses,
                    Port: record.Port,
                    Type: domainName[2]
                );

                if (logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug("Service located at: {address}:{port} as {serviceId} {instanceName}", Join(",", srvs.Addresses.Select(addr => addr.ToString())), srvs.Port, srvs.ServiceId, srvs.ServiceName);
                }

                try {
                    await _serviceAnsweredEvent.InvokeAsync(srvs, state.Cts.Token);
                }
                catch (Exception ex) { logger.LogError(ex, "Could not handle ServiceAnsweredEvent"); }
            }
        }
        catch (Exception e)
        {
            logger.LogCritical(e, "Critical failure when receiving answer from service");
        }
    }

    #region IDisposable Support
    private bool _disposedValue;

    public void Dispose()
    {
        if (_disposedValue) return;
        
        Stop();
        _disposedValue = true;
    }
    #endregion

    private sealed class RunningState : IDisposable {
        public MulticastService MulticastService { get; }
        public ServiceDiscovery ServiceDiscovery { get; }
        public CancellationTokenSource Cts { get; }

        public RunningState() {
            Cts = new CancellationTokenSource();
            MulticastService = new MulticastService { UseIpv6 = false, IgnoreDuplicateMessages = true };
            ServiceDiscovery = new ServiceDiscovery(MulticastService);
        }

        public void Dispose() {
            Cts.Dispose();
            ServiceDiscovery.Dispose();
            MulticastService.Dispose();
        }
    }
}