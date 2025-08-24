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
        object GetState();
        void SetState(object state);
    // Optional lifecycle hook: drop queued audio and reset pacing filters
    void ClearAudioBuffers();
    // Optional lifecycle hook: reset internal runtime state without re-instantiation.
    // Implementations may choose a minimal reset (e.g., clear audio buffers and pacing) to avoid
    // large reallocations in AOT/WASM environments.
    void Reset();
    }
}
