using System.Runtime.InteropServices;

namespace RootEngineProbe;

// Root strips unused Facepunch methods, but ships the public Steam C ABI.
// This adapter uses named SDK exports, never game-specific function addresses.
internal sealed class SteamNative : IDisposable
{
    private readonly IntPtr library;
    private readonly IntPtr sockets;
    private readonly CreatePair createPair;
    private readonly Send send;
    private readonly Receive receive;
    private readonly Release release;
    private readonly Close close;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Interface();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private delegate bool OverlayStatus(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool CreatePair(IntPtr self, out uint first, out uint second,
        [MarshalAs(UnmanagedType.I1)] bool networkLoopback, IntPtr firstIdentity, IntPtr secondIdentity);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Send(IntPtr self, uint connection, IntPtr data, uint length, int flags, out long number);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Receive(IntPtr self, uint connection, out IntPtr message, int maximum);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Release(IntPtr message);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool Close(IntPtr self, uint connection, int reason,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string debug, [MarshalAs(UnmanagedType.I1)] bool linger);

    public SteamNative()
    {
        if (IntPtr.Size != 8) throw new PlatformNotSupportedException("Root Steam transport requires the 64-bit game.");
        library = NativeLibrary.Load(Path.Combine(BepInEx.Paths.GameRootPath, "Root_Data", "Plugins", "x86_64", "steam_api64.dll"));
        try
        {
            sockets = Bind<Interface>("SteamAPI_SteamNetworkingSockets_SteamAPI_v012")();
            if (sockets == IntPtr.Zero) throw new InvalidOperationException("Steam networking is unavailable. Start Steam and sign in first.");
            createPair = Bind<CreatePair>("SteamAPI_ISteamNetworkingSockets_CreateSocketPair");
            send = Bind<Send>("SteamAPI_ISteamNetworkingSockets_SendMessageToConnection");
            receive = Bind<Receive>("SteamAPI_ISteamNetworkingSockets_ReceiveMessagesOnConnection");
            release = Bind<Release>("SteamAPI_SteamNetworkingMessage_t_Release");
            close = Bind<Close>("SteamAPI_ISteamNetworkingSockets_CloseConnection");
        }
        catch { NativeLibrary.Free(library); throw; }
    }

    internal IntPtr Sockets => sockets;
    internal T Bind<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
    public bool HasExport(string name) => NativeLibrary.TryGetExport(library, name, out _);
    public bool OverlayEnabled => Bind<OverlayStatus>("SteamAPI_ISteamUtils_IsOverlayEnabled")(
        Bind<Interface>("SteamAPI_SteamUtils_v010")());

    public (uint First, uint Second) Pair()
    {
        if (!createPair(sockets, out var first, out var second, false, IntPtr.Zero, IntPtr.Zero))
            throw new InvalidOperationException("Steam refused the local socket-pair test.");
        return (first, second);
    }

    public int SendReliable(uint connection, byte[] payload)
    {
        if (payload.Length is < 1 or > 512 * 1024) throw new ArgumentOutOfRangeException(nameof(payload));
        var pointer = Marshal.AllocHGlobal(payload.Length);
        try
        {
            Marshal.Copy(payload, 0, pointer, payload.Length);
            return send(sockets, connection, pointer, (uint)payload.Length, 8, out _);
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    public byte[]? ReceiveOne(uint connection)
    {
        var count = receive(sockets, connection, out var message, 1);
        if (count == 0) return null;
        if (count != 1 || message == IntPtr.Zero) throw new IOException("Steam connection could not receive a message.");
        try
        {
            // Public SteamNetworkingMessage_t begins with void* m_pData, int m_cbSize.
            var length = Marshal.ReadInt32(message, IntPtr.Size);
            if (length is < 1 or > 512 * 1024) throw new InvalidDataException("Steam message exceeds the transport limit.");
            var bytes = new byte[length];
            Marshal.Copy(Marshal.ReadIntPtr(message), bytes, 0, length);
            return bytes;
        }
        finally { release(message); }
    }

    public void CloseConnection(uint connection) => close(sockets, connection, 1000, "Private transport test complete", false);
    public void Dispose() => NativeLibrary.Free(library);
}
