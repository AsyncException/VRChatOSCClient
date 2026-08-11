using System.Net;

namespace VRChatOSCClient.HttpServer;

internal record HostInfo(string ServiceName, IPAddress IpAddress, int Port) {
    public override string ToString() {
        return $$"""
        {
          "NAME": "{{ServiceName}}",
          "OSC_IP": "{{IpAddress}}",
          "OSC_PORT": {{Port}},
          "OSC_TRANSPORT": "UDP",
          "EXTENSIONS": {
            "ACCESS": true,
            "CLIPMODE": true,
            "RANGE": true,
            "TYPE": true,
            "VALUE": true
          }
        }
        """;
    }
}