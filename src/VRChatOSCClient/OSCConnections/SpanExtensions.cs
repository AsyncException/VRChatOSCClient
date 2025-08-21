using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace VRChatOSCClient.OSCConnections;
internal static class SpanExtensions
{
    public static int IndexOf(this ReadOnlySpan<byte> span, byte value)
    {
        for (int i = 0; i < span.Length; i++)
        {
            if (span[i] == value)
            {
                return i;
            }
        }
        return -1;
    }
}
