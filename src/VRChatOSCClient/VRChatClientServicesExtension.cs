using Microsoft.Extensions.DependencyInjection;
using System.Net;
using VRChatOSCClient.HttpServer;
using VRChatOSCClient.MulticastServices;
using VRChatOSCClient.OSCConnections;
using VRChatOSCClient.OSCQuery;

namespace VRChatOSCClient;
public static class VRChatClientServicesExtension
{
    public static IServiceCollection AddVRChatClient(this IServiceCollection services, string serviceName, IPAddress address, ServiceLifetime lifetime = ServiceLifetime.Singleton) {
        services.AddTransient<Settings>(_ => new Settings() { ServiceName = serviceName, Address = address });
        services.AddHttpClient(nameof(VRChatDataFetcher), client => VRChatDataFetcher.ConfigureHTTPClient(client, serviceName));
        services.AddTransient<OscQueryService>();
        services.AddTransient<VRChatDataFetcher>();
        services.AddTransient<HostInfoHttpServer>();
        services.AddTransient<Multicaster>();
        services.AddTransient<OscCommunicator>();
        services.Add(new ServiceDescriptor(typeof(IVRChatClient), typeof(VRChatClient), lifetime));
        return services;
    }
}

public class Settings {
    public required string ServiceName { get; set; }
    public required IPAddress Address { get; set; }
}