namespace NesEmulator
{
    public interface IAPU
    {
    // Core metadata (new)
    string CoreName { get; }
    string Description { get; }
    int Performance { get; } // relative performance score (higher=faster)
    int Rating { get; } // subjective quality rating 1..N
    string Category { get; }
        void Step(int cpuCycles);
        void WriteAPURegister(ushort address, byte value);
        byte ReadAPURegister(ushort address);
        float[] GetAudioSamples(int maxSamples = 0);
        int GetQueuedSampleCount();
        int GetSampleRate();
        void SetEnabledChannels(int channelMask); // Bitflags: 0x01=Square1, 0x02=Square2, 0x04=Triangle, 0x08=Noise, 0x10=DMC
        object GetState();
        void SetState(object state);
    // Optional lifecycle hook: drop queued audio and reset pacing filters
    void ClearAudioBuffers();
    // Optional lifecycle hook: reset internal runtime state without re-instantiation.
    // Implementations may choose a minimal reset (e.g., clear audio buffers and pacing) to avoid
    // large reallocations in AOT/WASM environments.
    void Reset();
    // Drain up to dest.Length queued samples (mono, GetSampleRate() Hz) into dest; returns how many.
    // For real-time hosts (plugin mode): cores that override it never allocate. This default keeps
    // every other core working unchanged, through the allocating GetAudioSamples.
    int ReadSamples(System.Span<float> dest)
    {
        if (dest.Length == 0) return 0;
        var s = GetAudioSamples(dest.Length);
        s.AsSpan().CopyTo(dest);
        return s.Length;
    }
    }
}
