using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace RootEngineProbe;

// A build-specific diagnostic map, never used as hardcoded patch addresses.
internal static class NativeMethods
{
    public static void Write(string output)
    {
        var module = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
            .Single(m => m.ModuleName == "GameAssembly.dll");
        var types = new[]
        {
            typeof(tuber.client.match.commands.PlayOfflineMatch),
            typeof(tuber.client.match.commands.PlayCanisOnlineMatch),
            typeof(tuber.client.match.canis.TuberCanisMatch),
            typeof(Canis.boardgames.CanisMatch),
            typeof(dwd.core.session.SessionProvider),
            typeof(tuber.client.match.behaviours.TuberMatchHub)
        };
        var lines = new List<string>();
        foreach (var type in types.Concat(types.SelectMany(t => t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))))
            foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.NonPublic))
                if (field.Name.StartsWith("NativeMethodInfoPtr_") && field.GetValue(null) is IntPtr info && info != IntPtr.Zero)
                    lines.Add($"{Marshal.ReadIntPtr(info).ToInt64() - module.BaseAddress.ToInt64():x}\t{type.FullName}\t{field.Name}");
        File.WriteAllLines(Path.Combine(output, "native-methods.tsv"), lines);
    }
}
