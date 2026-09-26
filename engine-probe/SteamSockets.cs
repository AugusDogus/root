using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace RootEngineProbe;

// Owns only this mod's listeners and connections. Root keeps its Steam session.
internal sealed class SteamSockets : IDisposable
{
    [StructLayout(LayoutKind.Explicit, Size = 136)]
    internal struct Identity
    {
        [FieldOffset(0)] public int Type;
        [FieldOffset(4)] public int Size;
        [FieldOffset(8)] public ulong SteamId;
    }

    // Public SteamNetConnectionInfo_t layout for ISteamNetworkingSockets012, win64.
    [StructLayout(LayoutKind.Explicit, Size = 696)]
    internal struct Info
    {
        [FieldOffset(0)] public Identity Remote;
        [FieldOffset(144)] public uint ListenSocket;
        [FieldOffset(176)] public int State;
        [FieldOffset(180)] public int EndReason;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Config { public int Key; public int Type; public IntPtr Value; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void StatusCallback(IntPtr message);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr UserInterface();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong UserId(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RunCallbacks(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint Listen(IntPtr self, int port, int count, ref Config config);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint Connect(IntPtr self, ref Identity identity, int port, int count, ref Config config);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Accept(IntPtr self, uint connection);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool GetInfo(IntPtr self, uint connection, out Info info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool CloseListen(IntPtr self, uint socket);


    // Route by SDK handles so local self-connections do not depend on which
    // endpoint dispatches an event. The thunk remains valid during teardown.
    private static readonly ConcurrentQueue<uint> Events = new();
    private static readonly StatusCallback Callback = pointer => Events.Enqueue(unchecked((uint)Marshal.ReadInt32(pointer)));
    private static readonly Dictionary<uint, SteamSockets> ConnectionOwners = new();
    private static readonly Dictionary<uint, SteamSockets> ListenerOwners = new();
    private readonly ConcurrentQueue<uint> changes = new();
    private readonly HashSet<uint> connections = new();
    private readonly List<uint> listeners = new();
    private readonly SteamNative api;
    private readonly GetInfo getInfo;
    private readonly Accept accept;
    private readonly CloseListen closeListen;
    private readonly RunCallbacks runCallbacks;
    private readonly Config config;
    public ulong Self { get; }

    public SteamSockets(SteamNative api)
    {
        this.api = api;
        getInfo = api.Bind<GetInfo>("SteamAPI_ISteamNetworkingSockets_GetConnectionInfo");
        accept = api.Bind<Accept>("SteamAPI_ISteamNetworkingSockets_AcceptConnection");
        closeListen = api.Bind<CloseListen>("SteamAPI_ISteamNetworkingSockets_CloseListenSocket");
        runCallbacks = api.Bind<RunCallbacks>("SteamAPI_ISteamNetworkingSockets_RunCallbacks");
        var user = api.Bind<UserInterface>("SteamAPI_SteamUser_v023")();
        if (user == IntPtr.Zero) throw new InvalidOperationException("Steam user interface is unavailable.");
        Self = api.Bind<UserId>("SteamAPI_ISteamUser_GetSteamID")(user);
        if (Self == 0) throw new InvalidOperationException("Steam did not provide a signed-in identity.");
        config = new Config { Key = 201, Type = 5, Value = Marshal.GetFunctionPointerForDelegate(Callback) };
    }

    public uint ListenOn(int port)
    {
        var options = config;
        var listener = api.Bind<Listen>("SteamAPI_ISteamNetworkingSockets_CreateListenSocketP2P")(api.Sockets, port, 1, ref options);
        if (listener == 0) throw new IOException("Steam could not open the private listen socket. Restart this session to choose a new virtual port.");
        listeners.Add(listener);
        ListenerOwners.Add(listener, this);
        return listener;
    }

    public uint ConnectTo(ulong peer, int port)
    {
        var identity = new Identity { Type = 16, Size = 8, SteamId = peer };
        var options = config;
        var connection = api.Bind<Connect>("SteamAPI_ISteamNetworkingSockets_ConnectP2P")(api.Sockets, ref identity, port, 1, ref options);
        if (connection == 0) throw new IOException("Steam could not start the private connection. Check Steam and the invitation.");
        connections.Add(connection);
        ConnectionOwners.Add(connection, this);
        return connection;
    }

    public IEnumerable<(uint Connection, Info Info)> Changes()
    {
        runCallbacks(api.Sockets);
        while (Events.TryDequeue(out var changed))
        {
            if (!getInfo(api.Sockets, changed, out var info)) continue;
            if (ListenerOwners.TryGetValue(info.ListenSocket, out var owner) || ConnectionOwners.TryGetValue(changed, out owner))
                owner.changes.Enqueue(changed);
        }
        while (changes.TryDequeue(out var connection))
        {
            if (!getInfo(api.Sockets, connection, out var info)) continue;
            if (listeners.Contains(info.ListenSocket))
            {
                connections.Add(connection);
                ConnectionOwners[connection] = this;
            }
            yield return (connection, info);
        }
    }

    public Info State(uint connection) => getInfo(api.Sockets, connection, out var info)
        ? info : throw new IOException("Steam connection is no longer available. Rejoin before retrying a move.");
    public void AcceptPeer(uint connection)
    {
        if (accept(api.Sockets, connection) != 1) throw new IOException("Steam refused the incoming private connection.");
    }
    public void Close(uint connection)
    {
        if (Steamworks.SteamClient.IsValid) api.CloseConnection(connection);
        connections.Remove(connection);
        ConnectionOwners.Remove(connection);
    }
    public void Dispose()
    {
        foreach (var connection in connections)
        {
            if (Steamworks.SteamClient.IsValid) api.CloseConnection(connection);
            ConnectionOwners.Remove(connection);
        }
        connections.Clear();
        foreach (var listener in listeners)
        {
            if (Steamworks.SteamClient.IsValid) closeListen(api.Sockets, listener);
            ListenerOwners.Remove(listener);
        }
        listeners.Clear();
    }
}
