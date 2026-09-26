using Mono.Cecil;

// Read generated bindings without executing Windows or IL2CPP code.
if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: NativeApi <interop-directory> <type-name-substring> [more filters]");
    return 2;
}
foreach (var file in File.Exists(args[0]) ? new[] { args[0] } : Directory.EnumerateFiles(args[0], "*.dll"))
{
    using var assembly = AssemblyDefinition.ReadAssembly(file);
    foreach (var type in assembly.MainModule.Types.SelectMany(Types).Where(type => args.Skip(1).Any(filter => type.FullName.Contains(filter, StringComparison.OrdinalIgnoreCase))))
    {
        Console.WriteLine($"TYPE {type.FullName} : {type.BaseType}");
        foreach (var field in type.Fields.Where(field => field.IsLiteral))
            Console.WriteLine($"  CONST {field.Name} = {field.Constant}");
        foreach (var field in type.Fields.Where(field => field.IsPublic && !field.IsLiteral))
            Console.WriteLine($"  FIELD {field.FieldType} {field.Name}");
        foreach (var property in type.Properties)
            Console.WriteLine($"  PROPERTY {property.PropertyType} {property.Name}");
        foreach (var method in type.Methods.Where(method => !method.IsGetter && !method.IsSetter && method.Name != ".cctor"))
            Console.WriteLine($"  METHOD {method}");
    }
}
return 0;

static IEnumerable<TypeDefinition> Types(TypeDefinition type)
{
    yield return type;
    foreach (var child in type.NestedTypes.SelectMany(Types)) yield return child;
}
