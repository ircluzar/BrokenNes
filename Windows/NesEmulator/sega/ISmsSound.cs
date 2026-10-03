using System;
using System.IO;

namespace NesEmulator.Sega;

/// <summary>
/// The Master System / Game Gear / SG-1000 sound unit as the board sees it, and the seam the bridges plug into: the board writes the PSG port, the Game Gear stereo port and
/// (on a console that has one) the YM2413 ports, runs the unit by the Z80 cycles that elapse, and drains stereo samples. It sits in the board's APU slot like <c>ISnesApu</c> and
/// <see cref="IGenesisSound"/>. The stock unit is <see cref="SmsSound"/>; Track C's bridges implement this same interface (a Sega game's sound replayed on NES chips) so the board
/// never knows which one it has.
/// </summary>
public interface ISmsSound
{
    string CoreName { get; }
    /// <summary>The rate <see cref="ReadSamples"/> delivers.</summary>
    int SampleRate { get; }
    void Reset();

    /// <summary>Run the unit for <paramref name="cpuClocks"/> Z80 clock cycles (the PSG divides by 16 itself).</summary>
    void Run(int cpuClocks);

    /// <summary>A byte written to the PSG port (<c>$7E</c> / <c>$7F</c> write).</summary>
    void WritePsg(byte value);

    /// <summary>The Game Gear stereo port (<c>$06</c>); ignored by every other model.</summary>
    void WriteStereo(byte value);

    // ---- the FM chip (YM2413) on the Japanese Mark III / SMS: ports $F0 (address), $F1 (data), $F2 (audio control) ----

    /// <summary>True when the unit has an FM chip (the stock unit has none until the Master System track adds the YM2413).</summary>
    bool HasFm { get; }
    void WriteFmAddress(byte value);
    void WriteFmData(byte value);
    /// <summary>A write to <c>$F2</c>: bit 0 enables FM, bit 1 the PSG mix (the detection routine writes and reads this).</summary>
    void WriteAudioControl(byte value);
    /// <summary>A read of <c>$F2</c>: what the detection routine sees. A console without the chip returns <c>$FF</c>.</summary>
    byte ReadAudioControl();

    /// <summary>Move queued stereo samples (interleaved L,R) into <paramref name="dest"/>. Returns the number of shorts written (always even).</summary>
    int ReadSamples(Span<short> dest);

    /// <summary>What the unit is playing, as hub voices, for the NES-register bridges and the plugin. Returns how many it wrote.</summary>
    int DescribeVoices(Span<HubVoice> voices);

    void SaveState(BinaryWriter w);
    void LoadState(BinaryReader r);

    /// <summary>One-line human-readable state for probes and logs.</summary>
    string Describe();
}

/// <summary>The stock sound unit: the SN76489 (with the Game Gear's stereo port) and no FM chip.</summary>
public sealed class SmsSound : ISmsSound
{
    private readonly Sn76489 psg;

    /// <param name="cpuClockHz">The Z80 clock of the machine (from <see cref="SegaTimeline.Z80"/> and the master clock).</param>
    /// <param name="sampleRate">The audio rate the host wants.</param>
    /// <param name="variant">The Master System / Game Gear part (<see cref="PsgVariant.Sega"/>) or the SG-1000's discrete TI part (<see cref="PsgVariant.Ti"/>).</param>
    public SmsSound(double cpuClockHz, int sampleRate, PsgVariant variant = PsgVariant.Sega)
    {
        psg = new Sn76489(cpuClockHz, sampleRate, variant);
    }

    public string CoreName => "SN76489";
    public int SampleRate => psg.SampleRate;
    public Sn76489 Psg => psg;

    public void Reset() => psg.Reset();
    public void Run(int cpuClocks) => psg.Run(cpuClocks);
    public void WritePsg(byte value) => psg.Write(value);
    public void WriteStereo(byte value) => psg.WriteStereo(value);

    public bool HasFm => false;
    public void WriteFmAddress(byte value) { }
    public void WriteFmData(byte value) { }
    public void WriteAudioControl(byte value) { }
    public byte ReadAudioControl() => 0xFF;

    public int ReadSamples(Span<short> dest) => psg.ReadSamples(dest);
    public int DescribeVoices(Span<HubVoice> voices) => psg.DescribeVoices(voices);

    public void SaveState(BinaryWriter w) => psg.SaveState(w);
    public void LoadState(BinaryReader r) => psg.LoadState(r);

    public string Describe() =>
        $"SN76489 ({psg.Variant}): tones {psg.Period(0)}/{psg.Period(1)}/{psg.Period(2)} volumes {psg.Volume(0)}/{psg.Volume(1)}/{psg.Volume(2)}/{psg.Volume(3)} noise {psg.NoiseControl} stereo ${psg.StereoMask:X2}";
}
