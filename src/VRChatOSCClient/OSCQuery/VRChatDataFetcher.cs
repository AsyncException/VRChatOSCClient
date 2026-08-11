using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace VRChatOSCClient.OSCQuery;

/// <summary>
/// Service responsible for connecting to VRChat and retrieving required data.
/// </summary>
/// <param name="logger"></param>
/// <param name="factory"></param>
internal class VrChatDataFetcher(ILogger<VrChatDataFetcher> logger, IHttpClientFactory factory) {
    
    public static void ConfigureHttpClient(HttpClient client, string serviceName) {
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", $"OscQuery-{serviceName}");
        client.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true };
    }

    public async Task<IPEndPoint> GetConnectionEndpoint(IPAddress address, ushort port) {
        try {
            using var client = factory.CreateClient(nameof(VrChatDataFetcher));
            var uri = new UriBuilder("http", address.ToString(), port, "", "?HOST_INFO");

            var stringifiedData = await client.GetStringAsync(uri.Uri);
            var data = JsonSerializer.Deserialize<JsonElement>(stringifiedData);

            var oscIpString = data.GetProperty("OSC_IP").GetString();
            if (string.IsNullOrEmpty(oscIpString) || !IPAddress.TryParse(oscIpString, out var oscIp)) {
                logger.LogError("Received empty or malformed IPAddress from host");
                throw new Exception("Received empty or malformed IPAddress from HOST_INFO");
            }

            if (data.GetProperty("OSC_PORT").TryGetInt32(out var oscPort))
                return new IPEndPoint(oscIp, oscPort);
            
            logger.LogError("Received empty or malformed Port from host");
            throw new Exception("Received empty or malformed port from HOST_INFO");
        }
        catch (Exception ex) {
            logger.LogError(ex, "Exception occured while fetching connection endpoint");
            throw;
        }
    }

    public async Task<Dictionary<string, object?>> GetAvatarParameters(IPAddress address, ushort port, CancellationToken token) {
        var client = factory.CreateClient(nameof(VrChatDataFetcher));
        var uri = new UriBuilder("http", address.ToString(), port);

        var stringifiedData = await client.GetStringAsync(uri.Uri, token);

        Dictionary<string, object?> parameters = [];
        var data = JsonSerializer.Deserialize<JsonElement>(stringifiedData);
        foreach(var element in data.GetProperty("CONTENTS").GetProperty("avatar").GetProperty("CONTENTS").GetProperty("parameters").GetProperty("CONTENTS").EnumerateObject()) {
            ReadJsonProperty(element, parameters);
        }

        return parameters;
    }
    
    private static void ReadJsonProperty(JsonProperty property, Dictionary<string, object?> parameters)
    {
        var access = property.Value.GetProperty("ACCESS").GetInt32();

        switch (access)
        {
            case 3:
                parameters.Add(property.Name, property.Value.GetProperty("TYPE").GetString() switch {
                    "T" => property.Value.GetProperty("VALUE").EnumerateArray().First().GetBoolean(),
                    "f" => property.Value.GetProperty("VALUE").EnumerateArray().First().GetSingle(),
                    "i" => property.Value.GetProperty("VALUE").EnumerateArray().First().GetInt32(),
                    "s" => property.Value.GetProperty("VALUE").EnumerateArray().First().GetString(),
                    _ => null
                });
                break;
            case 0:
            {
                foreach(var subProperty in property.Value.GetProperty("CONTENTS").EnumerateObject()) {
                    ReadJsonProperty(subProperty, parameters);
                }

                break;
            }
        }
    }
}