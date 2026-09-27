using Canis.json;
using Canis.messages.timer;

namespace RootEngineProbe;

internal static class NativeTimerUI
{
    // Call once when a host message enters the client. Native constructors,
    // updates, and getters all receive timestamps in the client's UTC clock.
    public static DisplayTimer ForClient(DisplayTimer timer, long estimatedHostNow)
    {
        // The board bootstrap has no online match supplying a clock offset.
        // Leave native countdown and hide behavior intact. IL2CPP may share
        // trivial getters with unrelated classes, so no UI methods are patched.
        var local = JSON.Deserialize<DisplayTimer>(JSON.ToJSON(timer, false));
        var offset = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - estimatedHostNow;
        local.StartedAt += offset;
        local.EndsAt += offset;
        return local;
    }
}
