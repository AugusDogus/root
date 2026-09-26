using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Steamworks;

namespace RootEngineProbe;

internal sealed class SteamInvitations : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr FriendsInterface();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool Invite(IntPtr self, ulong friend, [MarshalAs(UnmanagedType.LPUTF8Str)] string connect);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Count(IntPtr self, int flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong FriendId(IntPtr self, int index, int flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Name(IntPtr self, ulong friend);
    private readonly IntPtr friends;
    private readonly Invite invite;
    private readonly Count count;
    private readonly FriendId friendId;
    private readonly Name name;
    private readonly Il2CppSystem.Action<Friend, string> callback;

    public SteamInvitations(SteamNative api, Action<SteamInvitation> accepted, Action<string>? invalid = null)
    {
        if (!Dispatch.Callbacks.ContainsKey(CallbackType.GameRichPresenceJoinRequested) ||
            Dispatch.Callbacks[CallbackType.GameRichPresenceJoinRequested].Count == 0)
            throw new InvalidOperationException("Root has not registered Steam invitation dispatch. Restart the mod after signing into Steam.");
        friends = api.Bind<FriendsInterface>("SteamAPI_SteamFriends_v017")();
        if (friends == IntPtr.Zero) throw new InvalidOperationException("Steam friends interface is unavailable.");
        invite = api.Bind<Invite>("SteamAPI_ISteamFriends_InviteUserToGame");
        count = api.Bind<Count>("SteamAPI_ISteamFriends_GetFriendCount");
        friendId = api.Bind<FriendId>("SteamAPI_ISteamFriends_GetFriendByIndex");
        name = api.Bind<Name>("SteamAPI_ISteamFriends_GetFriendPersonaName");
        callback = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<Friend, string>>(
            (Action<Friend, string>)((sender, text) =>
            {
                if (SteamInvitation.TryParse(text, out var parsed) && parsed is { } value && value.Host == (ulong)sender.Id)
                    accepted(value);
                else if (text.StartsWith("root6:", StringComparison.Ordinal))
                    invalid?.Invoke("This invitation is invalid or uses a different mod version. Install the same release as your host and ask for a new invitation.");
            })) ?? throw new InvalidOperationException("Steam invitation callback could not be registered.");
        SteamFriends.OnGameRichPresenceJoinRequested = Il2CppSystem.Delegate.Combine(
            SteamFriends.OnGameRichPresenceJoinRequested, callback).Cast<Il2CppSystem.Action<Friend, string>>();
    }

    public IEnumerable<(ulong Id, string Name)> Friends()
    {
        var length = Math.Clamp(count(friends, 4), 0, 10000); // immediate friends only
        for (var index = 0; index < length; index++)
        {
            var id = friendId(friends, index, 4);
            if (id != 0) yield return (id, Marshal.PtrToStringUTF8(name(friends, id)) ?? "Steam friend");
        }
    }

    public bool Send(ulong recipient, SteamInvitation invitation) =>
        recipient != 0 && SteamInvitation.TryParse(invitation.Encode(), out _) && invite(friends, recipient, invitation.Encode());
    public string NameFor(ulong identity) => MatchLobby.CleanName(Marshal.PtrToStringUTF8(name(friends, identity)) ?? "Steam friend");

    // Exercise the actual converted IL2CPP delegate without contacting Steam friends.
    internal void SimulateAcceptance(ulong sender, string text) => callback.Invoke(new Friend(sender), text);

    public void Dispose()
    {
        var remaining = Il2CppSystem.Delegate.Remove(SteamFriends.OnGameRichPresenceJoinRequested, callback);
        SteamFriends.OnGameRichPresenceJoinRequested = remaining?.Cast<Il2CppSystem.Action<Friend, string>>();
    }
}
