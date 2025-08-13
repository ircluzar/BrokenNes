namespace NesEmulator.Shaders;

public interface IShaderProvider
{
    IReadOnlyList<IShader> All { get; }
    IShader? GetById(string id);
}

public sealed class ShaderProvider : IShaderProvider
{
    public IReadOnlyList<IShader> All => ShaderRegistry.All;
    public IShader? GetById(string id) => ShaderRegistry.GetById(id);
}
