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

public static class VrChatClientServicesExtension
{
    extension(IServiceCollection services) {

        public IServiceCollection AddVrChatClient(string serviceName, IPAddress address, ServiceLifetime lifetime = ServiceLifetime.Singleton) {
            services.AddTransient<Settings>(_ => new Settings(serviceName, address));
            services.AddHttpClient(nameof(VrChatDataFetcher), client => VrChatDataFetcher.ConfigureHttpClient(client, serviceName));
            services.AddTransient<OscQueryService>();
            services.AddTransient<VrChatDataFetcher>();
            services.AddTransient<HostInfoHttpServer>();
            services.AddTransient<Multicaster>();
            services.AddTransient<OscCommunicator>();
            services.Add(new ServiceDescriptor(typeof(IVrChatClient), typeof(VrChatClient), lifetime));

            return services;
        }

        public IServiceCollection AddOpenVrClient(IConfiguration configuration) {
            services.Configure<OpenVrWrapperSettings>(configuration);
            services.AddOptions<OpenVrWrapperSettings>();
            services.AddSingleton<OpenVrWrapper>();

            return services;
        }

        public IServiceCollection AddOpenVrClient(Action<OpenVrWrapperSettings> settingsFactory) {
            services.Configure<OpenVrWrapperSettings>(settingsFactory);
            services.AddSingleton<OpenVrWrapper>();

            return services;
        }
        
    }
}
