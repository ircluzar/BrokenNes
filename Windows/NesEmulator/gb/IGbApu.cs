using System.IO;

namespace NesEmulator.Gb;

/// <summary>
/// What BOARD_GB needs from a sound chip. APU_GB is the real one; cross-console bridges implement this to put a
/// Game Boy game's sound through another console's audio hardware.
/// </summary>
public interface IGbApu
{
    string CoreName { get; }
    void ResetPostBoot();
    /// <summary>Advance by T-cycles of the 4.194304 MHz base clock.</summary>
    void Tick(int tCycles);
    /// <summary>512 Hz frame-sequencer step (falling edge of DIV bit 4, bit 5 at double speed).</summary>
    void FrameSequencerStep();
    /// <summary>Registers $FF10-$FF3F by their low byte.</summary>
    byte ReadRegister(int reg);
    void WriteRegister(int reg, byte v);
    int SampleRate { get; }
    /// <summary>Interleaved stereo 16-bit samples; returns the number of shorts written.</summary>
    int ReadSamples(short[] buffer);
    void SaveState(BinaryWriter w);
    void LoadState(BinaryReader r);
}
