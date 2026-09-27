using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.Common;

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: CiInterop GAME_DIRECTORY OUTPUT_BEPINEX_DIRECTORY UNITY_VERSION");
    return 2;
}

var game = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
var interop = Path.Combine(output, "interop");
if (Directory.Exists(interop))
{
    Console.Error.WriteLine("Interop output already exists. Use a fresh build directory.");
    return 1;
}

Paths.SetExecutablePath(Path.Combine(game, "Root.exe"), output);
Environment.SetEnvironmentVariable("BEPINEX_GAME_ASSEMBLY_PATH", Path.Combine(game, "GameAssembly.dll"));
Logger.Listeners.Add(new BuildLogListener());

// These two internal entry points belong to the checksum-pinned BepInEx build.
// Invoke generation only, never Initialize(), which starts the native runtime.
RequiredMethod(typeof(UnityInfo), "Initialize").Invoke(null, new object[] { Paths.ExecutablePath, Paths.GameDataPath });
var version = UnityInfo.Version;
if ($"{version.Major}.{version.Minor}.{version.Build}" != args[2])
{
    Console.Error.WriteLine($"Expected Unity {args[2]}, found {version}. Update the pinned build inputs before compiling.");
    return 1;
}
var assembly = Assembly.Load("BepInEx.Unity.IL2CPP");
var manager = assembly.GetType("BepInEx.Unity.IL2CPP.Il2CppInteropManager")
    ?? throw new MissingMemberException("Pinned BepInEx no longer exposes Il2CppInteropManager.");
RequiredMethod(manager, "GenerateInteropAssemblies").Invoke(null, null);

// BepInEx logs generation failures instead of throwing them. Require its final
// completion marker and the actual game assemblies before reporting success.
foreach (var name in new[] { "assembly-hash.txt", "tuber-canis.dll", "tuber-client.dll" })
{
    var file = Path.Combine(interop, name);
    if (!File.Exists(file) || new FileInfo(file).Length == 0)
    {
        Console.Error.WriteLine($"Binding generation did not produce {name}. Inspect the generator errors above.");
        return 1;
    }
}
Console.WriteLine("Generated Root compilation references without starting the game.");
return 0;

static MethodInfo RequiredMethod(Type type, string name) =>
    type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
    ?? throw new MissingMethodException(type.FullName, name);

sealed class BuildLogListener : ILogListener
{
    public LogLevel LogLevelFilter => LogLevel.Info | LogLevel.Message | LogLevel.Warning | LogLevel.Error | LogLevel.Fatal;
    public void LogEvent(object sender, LogEventArgs eventArgs) => Console.WriteLine(eventArgs.ToString());
    public void Dispose() { }
}
