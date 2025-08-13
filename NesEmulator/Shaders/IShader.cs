namespace NesEmulator.Shaders;

public interface IShader
{
    string Id { get; }
    string DisplayName { get; }
    string? VertexSource { get; }
    string FragmentSource { get; }
    IReadOnlyDictionary<string, string>? Defines { get; }
}
