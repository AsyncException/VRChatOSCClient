using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;

namespace VRChatOSCClient.OSCQuery;

/// <summary>
/// Service responsible for connecting to VRChat and retrieving required data.
/// </summary>
/// <param name="logger"></param>
/// <param name="factory"></param>
internal class VRChatDataFetcher(ILogger<VRChatDataFetcher> logger, IHttpClientFactory factory) {
    private readonly ILogger<VRChatDataFetcher> _logger = logger;
    private readonly IHttpClientFactory _clientFactory = factory;

    public static void ConfigureHTTPClient(HttpClient client, string serviceName) {
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", $"OscQuery-{serviceName}");
        client.DefaultRequestHeaders.CacheControl = new() { NoCache = true };
    }

    public async Task<IPEndPoint> GetConnectionEndpoint(IPAddress address, ushort port) {
        try {
            HttpClient client = _clientFactory.CreateClient(nameof(VRChatDataFetcher));
            UriBuilder uri = new("http", address.ToString(), port, "", "?HOST_INFO");

            string stringifiedData = await client.GetStringAsync(uri.Uri);
            JsonElement data = JsonSerializer.Deserialize<JsonElement>(stringifiedData);

            string? oscIpString = data.GetProperty("OSC_IP").GetString();
            if (string.IsNullOrEmpty(oscIpString) || !IPAddress.TryParse(oscIpString, out IPAddress? oscIP)) {
                _logger.LogError("Received empty or malformed IPAddress from host");
                throw new Exception("Received empty or malformed IPAddress from HOST_INFO");
            }

            if(!data.GetProperty("OSC_PORT").TryGetInt32(out int oscPort)) {
                _logger.LogError("Received empty or malformed port from host");
                throw new Exception("Received empty or malformed port from HOST_INFO");
            }

            return new IPEndPoint(oscIP, oscPort);
        }
        catch (Exception ex) {
            _logger.LogError(ex, "Exception occured while fetching connection endpoint");
            throw;
        }
    }

    public async Task<Dictionary<string, object?>> GetAvatarParameters(IPAddress address, ushort port, CancellationToken token) {
        HttpClient client = _clientFactory.CreateClient(nameof(VRChatDataFetcher));
        UriBuilder uri = new("http", address.ToString(), port);

        string stringifiedData = await client.GetStringAsync(uri.Uri, token);

        Dictionary<string, object?> parameters = [];
        JsonElement data = JsonSerializer.Deserialize<JsonElement>(stringifiedData);
        foreach(JsonProperty element in data.GetProperty("CONTENTS").GetProperty("avatar").GetProperty("CONTENTS").GetProperty("parameters").GetProperty("CONTENTS").EnumerateObject()) {
            ReadJsonProperty(element, parameters);
        }

        return parameters;
    }
    
    private static void ReadJsonProperty(JsonProperty property, Dictionary<string, object?> parameters) {
        int access = property.Value.GetProperty("ACCESS").GetInt32();
        if(access == 3) {
            parameters.Add(property.Name, property.Value.GetProperty("TYPE").GetString() switch {
                "T" => property.Value.GetProperty("VALUE").EnumerateArray().First().GetBoolean(),
                "f" => property.Value.GetProperty("VALUE").EnumerateArray().First().GetSingle(),
                "i" => property.Value.GetProperty("VALUE").EnumerateArray().First().GetInt32(),
                "s" => property.Value.GetProperty("VALUE").EnumerateArray().First().GetString(),
                _ => null
            });
        }
        else if(access == 0) {
            foreach(JsonProperty subProperty in property.Value.GetProperty("CONTENTS").EnumerateObject()) {
                ReadJsonProperty(subProperty, parameters);
            }
        }
    }
}