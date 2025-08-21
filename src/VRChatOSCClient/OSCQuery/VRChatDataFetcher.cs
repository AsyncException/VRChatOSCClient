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
}