namespace NesEmulator;

/// <summary>
/// What the BrokenNes2 plugin needs of a Nano instrument engine (the C# twin of one of the web libraries under Nano/: NanoGenesisEngine, NanoSnesEngine, NanoDosEngine),
/// so one plugin path (Bn2Plugin.NanoMore.cs) plays them all. The program number picks the engine's preset as the web player's program change does (n modulo the preset count).
/// Nothing here allocates once the engine is made: the plugin calls it on the audio thread.
/// </summary>
public interface INanoEngine
{
    /// <summary>Samples rendered so far: the clock events are scheduled on.</summary>
    long Frame { get; }
    /// <summary>Channels with a note on them now (the self-test reads it).</summary>
    int SoundingChannels { get; }
    void NoteOnProgram(long frame, int id, double note, double vel, int program, double bend);
    void NoteOff(long frame, int id);
    void BendVoice(long frame, int id, double cents);
    /// <summary>Fills left[off..off+n) and right (null: mono into left).</summary>
    void Render(float[] left, float[]? right, int off, int n);
}
