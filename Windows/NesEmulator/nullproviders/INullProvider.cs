namespace NesEmulator.NullProviders;

/// <summary>
/// Interface for providers that generate animated visuals when no ROM is loaded or when test ROM is active.
/// Null providers serve as a replacement for actual emulation, providing visual feedback.
/// </summary>
public interface INullProvider
{
    /// <summary>
    /// Display name shown in the config menu
    /// </summary>
    string DisplayName { get; }
    
    /// <summary>
    /// Brief description of this null provider's visual effect
    /// </summary>
    string Description { get; }
    
    /// <summary>
    /// Generate one frame of the null provider animation into the provided buffer.
    /// Buffer is expected to be 256x240x4 (RGBA) = 245,760 bytes.
    /// </summary>
    /// <param name="frameBuffer">Output buffer to write RGBA pixels into</param>
    /// <param name="frameCounter">Current frame number for animation timing</param>
    void GenerateFrame(byte[] frameBuffer, int frameCounter);
}
