using Mono.Cecil;

// Il2CppInterop's version-1 MethodAddressToToken.db maps native RVAs to
// generated binding tokens. Resolve those tokens without loading game code.
internal static class NativeAddressMap
{
    public static int Write(string directory)
    {
        using var reader = new BinaryReader(File.OpenRead(Path.Combine(directory, "MethodAddressToToken.db")));
        if (reader.ReadInt32() != 0x4D544D55 || reader.ReadInt32() != 1)
            throw new InvalidDataException("Unsupported Il2CppInterop address map. Expected UMTM version 1.");
        var assemblyCount = reader.ReadInt32();
        var methodCount = reader.ReadInt32();
        var offset = reader.ReadInt32();
        if (assemblyCount is < 1 or > 10000 || methodCount < 0 || offset < 20 ||
            offset + (long)methodCount * 16 > reader.BaseStream.Length)
            throw new InvalidDataException("Invalid Il2CppInterop address map bounds.");
        var names = Enumerable.Range(0, assemblyCount).Select(_ => reader.ReadString()).ToArray();
        var assemblies = new Dictionary<int, AssemblyDefinition>();
        try
        {
            reader.BaseStream.Position = offset;
            var addresses = Enumerable.Range(0, methodCount).Select(_ => reader.ReadInt64()).ToArray();
            foreach (var address in addresses)
            {
                var token = reader.ReadInt32();
                var index = reader.ReadInt32();
                if (index < 0 || index >= names.Length) throw new InvalidDataException("Invalid assembly index in address map.");
                if (!assemblies.TryGetValue(index, out var assembly))
                {
                    var name = new System.Reflection.AssemblyName(names[index]).Name;
                    if (string.IsNullOrEmpty(name) || Path.GetFileName(name) != name)
                        throw new InvalidDataException("Invalid assembly name in address map.");
                    assembly = AssemblyDefinition.ReadAssembly(Path.Combine(directory, name + ".dll"));
                    assemblies.Add(index, assembly);
                }
                if (assembly.MainModule.LookupToken(token) is MethodDefinition method)
                    Console.WriteLine($"{address:x}\t{assembly.Name.Name}\t{method.FullName}");
                else throw new InvalidDataException($"Method token {token:x} could not be resolved in {names[index]}.");
            }
        }
        finally { foreach (var assembly in assemblies.Values) assembly.Dispose(); }
        return 0;
    }
}
