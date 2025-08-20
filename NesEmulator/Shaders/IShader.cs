namespace NesEmulator.Shaders;

public interface IShader
{
    string Id { get; }
    string DisplayName { get; }
    // Shader metadata (new)
    string CoreName { get; } // reuse naming convention for consistency
    string Description { get; }
    int Performance { get; } // relative perf score (compile/runtime cost heuristic)
    int Rating { get; } // subjective quality rating
    string? VertexSource { get; }
    string FragmentSource { get; }
    IReadOnlyDictionary<string, string>? Defines { get; }
}
