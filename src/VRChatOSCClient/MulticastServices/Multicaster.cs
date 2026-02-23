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
internal class Multicaster(ILogger<Multicaster> logger) : IDisposable
{
    private readonly ILogger<Multicaster> _logger = logger;

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
        if(_state is null) {
            _logger.LogMulticasterAlreadyStarted();
            throw new InvalidOperationException("Multicaster is already started");
        }

        _logger.LogMulticasterStarted();

        var state = new RunningState();
        state.MulticastService.NetworkInterfaceDiscovered += InterfaceDiscovered;
        state.MulticastService.AnswerReceived += AnswerReceivedAsync;

        state.MulticastService.Start();

        _profiles = serviceProfiles;

        foreach (ServiceProfile profile in _profiles) {
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

        _logger.LogMulticasterStopped();

        foreach (ServiceProfile profile in _profiles) {
            state.ServiceDiscovery.Unadvertise(profile);
        }

        state.MulticastService.NetworkInterfaceDiscovered -= InterfaceDiscovered;
        state.MulticastService.AnswerReceived -= AnswerReceivedAsync;

        state.MulticastService.Stop();
        state.CTS.Cancel();

        state.Dispose();

        _profiles = [];

        _state = null;
    }

    private void InterfaceDiscovered(object? sender, NetworkInterfaceEventArgs args) {
        var state = _state;

        if(state is null) {
            return;
        }

        _logger.LogInterfaceDiscovered();

        foreach (ServiceProfile profiles in _profiles) {
            state.MulticastService.SendQuery(profiles.QualifiedServiceName);
        }
    }

    private async void AnswerReceivedAsync(object? sender, MessageEventArgs args) {
        var state = _state;

        if (state == null) {
            return;
        }

        IEnumerable<SRVRecord> records = args.Message.AdditionalRecords.OfType<SRVRecord>();
        foreach (SRVRecord record in records) {
            IReadOnlyList<string> domainName = record.Name.Labels;
            IPAddress[] addresses = [.. args.Message.AdditionalRecords.OfType<ARecord>().Select(record => record.Address)];

            AnnouncedService? srvs = new(
                ServiceId: $"{record.CanonicalName}:{record.Port}",
                ServiceName: domainName[0],
                Addresses: addresses,
                Port: record.Port,
                Type: domainName[2]
                );

            _logger.LogInterfaceDiscovered(string.Join(",", srvs.Addresses.Select(addr => addr.ToString())), srvs.Port, srvs.ServiceId, srvs.ServiceName);

            try {
                await _serviceAnsweredEvent.InvokeAsync(srvs, state.CTS.Token);
            }
            catch (Exception ex) { _logger.LogServiceAnsweredEventError(ex); }
        }
    }

    #region IDisposable Support
    private bool _disposedValue;

    protected virtual void Dispose(bool disposing) {
        if (!_disposedValue) {
            if (disposing) {}
            Stop();
            _disposedValue = true;
        }
    }

    public void Dispose() {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
    #endregion

    private sealed class RunningState : IDisposable {
        public MulticastService MulticastService { get; }
        public ServiceDiscovery ServiceDiscovery { get; }
        public CancellationTokenSource CTS { get; }

        public RunningState() {
            CTS = new CancellationTokenSource();
            MulticastService = new MulticastService { UseIpv6 = false, IgnoreDuplicateMessages = true };
            ServiceDiscovery = new ServiceDiscovery(MulticastService);
        }

        public void Dispose() {
            CTS.Dispose();
            ServiceDiscovery.Dispose();
            MulticastService.Dispose();
        }
    }
}


public static partial class MulticasterLogger
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Multicaster starting")]
    public static partial void LogMulticasterStarted(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Multicaster already started")]
    public static partial void LogMulticasterAlreadyStarted(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Multicaster stopping")]
    public static partial void LogMulticasterStopped(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Network interface discovered")]
    public static partial void LogInterfaceDiscovered(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Service located at: {address}:{port} as {serviceId} {instanceName}")]
    public static partial void LogInterfaceDiscovered(this ILogger logger, string? address, ushort port, string serviceId, string instanceName);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not handle ServiceAnsweredEvent")]
    public static partial void LogServiceAnsweredEventError(this ILogger logger, Exception exception);
}
