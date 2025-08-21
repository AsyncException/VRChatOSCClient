using System.Net;

namespace VRChatOSCClient.OSCQuery;
public class VRChatConnectionInfo {

    /// <summary>
    /// This is the endpoint this app can receive data on. This is the endpoint Vrchat sends data to.
    /// </summary>
    public required IPEndPoint ReceiveEndpoint { get; init; }

    /// <summary>
    /// This is the endpoint where this app can send data to. This is the endpoint Vrchat receives data on.
    /// </summary>
    public required IPEndPoint SendEndpoint { get; init; }

    /// <summary>
    /// This is the endpoint where the OSCQuery server is running. This is used to query Vrchat for information.
    /// </summary>
    public required IPEndPoint OSCQueryEndpoint { get; init; }
}
