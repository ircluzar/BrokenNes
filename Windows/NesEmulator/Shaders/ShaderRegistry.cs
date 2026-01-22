using System.Diagnostics.CodeAnalysis;

namespace NesEmulator.Shaders;

[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class ShaderDefinitionAttribute : Attribute
{
}

public static class ShaderRegistry
{
    private static readonly Lazy<IReadOnlyList<IShader>> _all = new(Discover);
    private static readonly Lazy<Dictionary<string, IShader>> _byId = new(() => _all.Value.ToDictionary(s => s.Id));

    public static IReadOnlyList<IShader> All => _all.Value;

    public static IShader? GetById(string id)
        => _byId.Value.TryGetValue(id, out var s) ? s : null;

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Generated types are preserved via DynamicDependency.")]
    private static IReadOnlyList<IShader> Discover()
    {
        var asm = typeof(ShaderRegistry).Assembly;
        var list = new List<IShader>();
        foreach (var t in asm.GetTypes())
        {
            if (!typeof(IShader).IsAssignableFrom(t)) continue;
            if (t.IsAbstract || t.IsInterface) continue;
            if (t.GetConstructor(Type.EmptyTypes) is null) continue;
            if (t.GetCustomAttributes(typeof(ShaderDefinitionAttribute), inherit: false).Length == 0) continue;
            if (Activator.CreateInstance(t) is IShader inst)
                list.Add(inst);
        }
        return list.OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
    }
}
