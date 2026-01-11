namespace VRChatOSCClient.Utilities;

internal static class CancellationTokenResetter
{
    public static void Reset(ref CancellationTokenSource source) {
        if (!source.TryReset()) {
            source.Dispose();
            source = new CancellationTokenSource();
        }
    }
}
