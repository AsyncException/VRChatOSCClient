using System.Net;

namespace VRChatOSCClient.MulticastServices;

public record AnnouncedService(string ServiceId, string ServiceName, IPAddress[] Addresses, ushort Port, string Type);