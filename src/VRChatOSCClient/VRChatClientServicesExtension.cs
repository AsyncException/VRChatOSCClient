using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using VRChatOSCClient.HttpServer;
using VRChatOSCClient.Models;
using VRChatOSCClient.MulticastServices;
using VRChatOSCClient.OpenVR;
using VRChatOSCClient.OSCConnections;
using VRChatOSCClient.OSCQuery;

namespace VRChatOSCClient;

public static class VRChatClientServicesExtension
{
    extension(IServiceCollection services) {

        public IServiceCollection AddVRChatClient(string serviceName, IPAddress address, ServiceLifetime lifetime = ServiceLifetime.Singleton) {
            services.AddTransient<Settings>(_ => new Settings(serviceName, address));
            services.AddHttpClient(nameof(VRChatDataFetcher), client => VRChatDataFetcher.ConfigureHTTPClient(client, serviceName));
            services.AddTransient<OscQueryService>();
            services.AddTransient<VRChatDataFetcher>();
            services.AddTransient<HostInfoHttpServer>();
            services.AddTransient<Multicaster>();
            services.AddTransient<OscCommunicator>();
            services.Add(new ServiceDescriptor(typeof(IVRChatClient), typeof(VRChatClient), lifetime));

            return services;
        }

        public IServiceCollection AddOpenVRClient(IConfiguration configuration) {
            services.Configure<OpenVRWrapperSettings>(configuration);
            services.AddOptions<OpenVRWrapperSettings>();
            services.AddSingleton<OpenVRWrapper>();

            return services;
        }

        public IServiceCollection AddOpenVRClient(Action<OpenVRWrapperSettings> settingsFactory) {
            services.Configure<OpenVRWrapperSettings>(settingsFactory);
            services.AddSingleton<OpenVRWrapper>();

            return services;
        }
        
    }
}
