// Instrument EXTRACTION (2026-11-10): capture one instrument from a channel of a running game and keep it as a preset (*.bn2preset). One capture core for the three front ends (the plugin in
// ROM mode, the BrokenNes desktop's Sound menu, BrokenDev's `instrument extract` through the Workshop's --extract-instrument). Pure code: no emulator, no plugin, no clock, no file system.
// The host feeds it ONE CaptureFrame per video frame per channel (what the chip held at the frame's end, from the core's own probes) and marks the frames where the game keyed the channel on;
// the core picks the note sounding at the frame asked for and returns a CapturedNote. See docs/projects/flp-music-pipeline.md, "Instrument extraction", for the definition:
//
//   key-on     the frame the game RE-STARTED the channel: NES a write of $4003/$4007/$400B/$400F (the length load that restarts the envelope / sequencer), Game Boy a write to NRx4 with bit 7,
//              or (a driver that never re-keys) a frame where the channel's level goes from 0 to above 0.
//   the note   the last key-on at or before the frame asked for whose note is still sounding there; if the channel is silent there, the next key-on after it.
//   snapshot   the console's whole sound-register block at the key-on frame (NES $4000-$4017 as last written, Game Boy NR10-NR51 + wave RAM): the instrument (duty, envelope, sweep...) comes from it.
//   envelope   per frame from the key-on until the key-off: the level the chip produced (0-15: NES envelope / constant volume, triangle 15 while it runs; Game Boy envelope volume, wave 15/7/3),
//              the period (NES timer or noise index; Game Boy 11-bit frequency or NR43), and the duty (pulse duty; NES noise mode; Game Boy wave level code / noise width).
//   key-off    the first frame after the key-on where the level is 0 (Silenced), or the channel is keyed again (Retrigger), or the cap (MaxFrames) or the end of the recording.
using BrokenNes2.Instruments;

namespace BrokenNes2.Extract;

public enum CaptureEnd : byte { Silenced = 0, Retrigger = 1, Cap = 2, RecordingEnd = 3 }

/// <summary>What one channel did in one video frame (see the file header).</summary>
public struct CaptureFrame
{
    public byte Level;      // NES / Game Boy 0-15; SNES, Master System, Genesis 0-255 (see ExtractChips.cs for what each channel's level is)
    public ushort Period;   // NES timer (pulse / triangle), noise index | mode << 7; Game Boy 11-bit frequency (pulse / wave), NR43 (noise); SNES the 14-bit pitch register; PSG the 10-bit period (noise: the control);
                            // YM2413 block << 9 | F-number; YM2612 block << 11 | F-number; DAC 0
    public byte Duty;       // pulse duty 0-3; NES noise mode; Game Boy wave level code, noise width; SNES the voice volume (0-127); others 0 (7 bits kept)
    public bool KeyOn;      // the game keyed the channel this frame
}

/// <summary>One captured note of one channel: the register snapshot at its key-on and its life frame by frame. Serialised (<see cref="ToBytes"/>) as the preset's note behaviour, version 4.</summary>
public sealed class CapturedNote
{
    public const int MaxFrames = 600, Format = 1, FormatWide = 2;
    public ConsoleId Console;
    public int Channel, KeyOnFrame;
    public CaptureEnd End;
    /// <summary>NES: $4000-$4017 (24 bytes) + DMC bytes not kept. Game Boy: NR10-NR51 (32 bytes) + wave RAM (16) + 1 byte CGB. SNES / Master System / Genesis: the key-on block of ExtractChips.cs
    /// (SNES the 128 S-DSP registers with the voice's sample; Sega the sound unit's registers and, for a DAC note, the sample it streamed).</summary>
    public byte[] Regs = [];
    public CaptureFrame[] Life = [];

    public int Frames => Life.Length;

    /// <summary>The pitch of frame <paramref name="i"/> relative to the key-on frame, in cents (0 for noise).</summary>
    public double CentsAt(int i)
    {
        if (Life.Length == 0 || IsNoise) return 0;
        double f0 = Hz(Life[0].Period), f = Hz(Life[Math.Clamp(i, 0, Life.Length - 1)].Period);
        return f0 > 0 && f > 0 ? 1200.0 * Math.Log2(f / f0) : 0;
    }

    /// <summary>Unpitched: the NES / Game Boy / PSG noise, the Genesis DAC (its pitch is the sample's own rate).</summary>
    public bool IsNoise => Console switch
    {
        ConsoleId.Nes or ConsoleId.GameBoy => Channel == 3,
        ConsoleId.Snes => false,
        _ => Channel == ExtractChips.PsgNoise || Console == ConsoleId.Genesis && Channel == ExtractChips.GenDac,
    };

    /// <summary>The frequency a period stands for (pulse / wave / triangle / FM channel; the SNES the pitch register itself, for ratios), 0 for noise.</summary>
    public double Hz(int period)
    {
        if (Console == ConsoleId.Nes) return Channel switch { 0 or 1 => 1789773.0 / (16.0 * (period + 1)), 2 => 1789773.0 / (32.0 * (period + 1)), _ => 0 };
        if (Console == ConsoleId.GameBoy) return Channel switch { 0 or 1 => 131072.0 / Math.Max(1, 2048 - period), 2 => 65536.0 / Math.Max(1, 2048 - period), _ => 0 };
        return IsNoise ? 0 : ExtractChips.Hz(Console, Channel, period);
    }

    /// <summary>The key-on pitch as a MIDI key (fractional), or 60 for noise: the key FL plays for the preset to sound at the captured pitch (the SNES: $1000 at key 60; an FM channel: its carriers' note).</summary>
    public double MidiKey => IsNoise || Life.Length == 0 || Hz(Life[0].Period) <= 0 ? 60
        : Console == ConsoleId.Snes ? 60 + 12 * Math.Log2(Life[0].Period / 4096.0)
        : 69 + 12 * Math.Log2(Hz(Life[0].Period) * ExtractChips.SoundingMultiple(this) / 440.0);

    /// <summary>NES and Game Boy notes are written in format 1 (what the first version of the plugin reads); the other consoles' in format 2 (a longer register block, more channels, levels to 255).</summary>
    public bool Wide => Console is not (ConsoleId.Nes or ConsoleId.GameBoy);

    public byte[] ToBytes()
    {
        var o = new List<byte> { (byte)(Wide ? FormatWide : Format), (byte)Console, (byte)Channel, (byte)End };
        void U16(int v) { o.Add((byte)v); o.Add((byte)(v >> 8)); }
        U16(KeyOnFrame & 0xFFFF); U16(KeyOnFrame >> 16 & 0xFFFF);
        if (Wide) { U16(Regs.Length & 0xFFFF); U16(Regs.Length >> 16); } else o.Add((byte)Regs.Length);
        o.AddRange(Regs);
        U16(Life.Length);
        foreach (var f in Life) { o.Add(f.Level); U16(f.Period); o.Add((byte)(f.Duty | (f.KeyOn ? 0x80 : 0))); }
        return o.ToArray();
    }

    public static CapturedNote? FromBytes(byte[] b)
    {
        try
        {
            if (b.Length < 11 || b[0] is not (Format or FormatWide) || b[1] > 4 || b[3] > 3) return null;
            bool wide = b[0] == FormatWide;
            var n = new CapturedNote { Console = (ConsoleId)b[1], Channel = b[2], End = (CaptureEnd)b[3], KeyOnFrame = b[4] | b[5] << 8 | b[6] << 16 | b[7] << 24 };
            if (wide != n.Wide || b[2] >= ExtractChips.ChannelCount(n.Console) + (wide ? 0 : 1)) return null;
            int p = 8, rl;
            if (wide) { if (b.Length < 14) return null; rl = b[8] | b[9] << 8 | b[10] << 16 | b[11] << 24; p = 12; if (rl < 0 || rl > ExtractChips.MaxBlock) return null; }
            else rl = b[p++];
            if (p + rl + 2 > b.Length) return null;
            n.Regs = b.AsSpan(p, rl).ToArray(); p += rl;
            int count = b[p] | b[p + 1] << 8; p += 2;
            if (count == 0 || count > MaxFrames || p + count * 4 != b.Length) return null;
            n.Life = new CaptureFrame[count];
            for (int i = 0; i < count; i++, p += 4)
                n.Life[i] = new CaptureFrame { Level = wide ? b[p] : (byte)Math.Min(15, (int)b[p]), Period = (ushort)(b[p + 1] | b[p + 2] << 8), Duty = (byte)(b[p + 3] & 0x7F), KeyOn = (b[p + 3] & 0x80) != 0 };
            return n;
        }
        catch { return null; }
    }

    public string Describe() => $"{ChannelName(Console, Channel)} keyed at frame {KeyOnFrame}, {Frames} frame{(Frames == 1 ? "" : "s")} ({End switch { CaptureEnd.Silenced => "until it went silent", CaptureEnd.Retrigger => "until the next note", CaptureEnd.Cap => $"cut at {MaxFrames}", _ => "until the recording ended" }})";

    public static string ChannelName(ConsoleId c, int ch) => c switch
    {
        ConsoleId.Nes => ch switch { 0 => "Pulse 1", 1 => "Pulse 2", 2 => "Triangle", 3 => "Noise", _ => "DMC" },
        ConsoleId.GameBoy => ch switch { 0 => "Square 1", 1 => "Square 2", 2 => "Wave", _ => "Noise" },
        _ => ExtractChips.ChannelName(c, ch),
    };
}

/// <summary>Collects the frames of every channel of a console while the game runs, and picks a note out of them (<see cref="Pick"/>). Memory: 4 bytes x channels x frames, plus a register snapshot per key-on.</summary>
public sealed class InstrumentRecorder
{
    public readonly ConsoleId Console;
    public readonly int Channels;
    private readonly List<CaptureFrame>[] frames;
    private readonly Dictionary<(int Ch, int Frame), byte[]> keyRegs = new();
    private byte[] lastRegs = [];
    public int FrameCount { get; private set; }

    public InstrumentRecorder(ConsoleId console)
    {
        Console = console;
        Channels = ExtractChips.ChannelCount(console);
        frames = Enumerable.Range(0, Channels).Select(_ => new List<CaptureFrame>()).ToArray();
    }

    /// <summary>One video frame: <paramref name="ch"/> the channels' frames (Channels of them), <paramref name="regs"/> the sound-register block at the frame's end (copied for a key-on).</summary>
    public void Frame(ReadOnlySpan<CaptureFrame> ch, byte[] regs) => Frame(ch, regs, null);

    /// <summary>One video frame where the register block of a key-on is made per channel, only for the channels keyed (<paramref name="blockOf"/>: a SNES voice's block holds its sample).</summary>
    public void Frame(ReadOnlySpan<CaptureFrame> ch, Func<int, byte[]> blockOf) => Frame(ch, [], blockOf);

    /// <summary>Replaces the register block kept for a key-on (the Genesis DAC: the sample is known only when its stream ends).</summary>
    public void SetKeyBlock(int channel, int frame, byte[] block) { if (keyRegs.ContainsKey((channel, frame))) keyRegs[(channel, frame)] = block; }

    private void Frame(ReadOnlySpan<CaptureFrame> ch, byte[] regs, Func<int, byte[]>? blockOf)
    {
        for (int c = 0; c < Channels; c++)
        {
            var f = ch[c];
            var list = frames[c];
            bool rising = f.Level > 0 && (list.Count == 0 || list[^1].Level == 0);
            if (rising) f.KeyOn = true;
            if (f.KeyOn) keyRegs[(c, FrameCount)] = blockOf != null ? blockOf(c) : (byte[])regs.Clone();
            list.Add(f);
        }
        lastRegs = regs;
        FrameCount++;
    }

    /// <summary>The note of channel <paramref name="channel"/> sounding at frame <paramref name="frame"/> (see the file header), or null with the reason.</summary>
    public CapturedNote? Pick(int channel, int frame, out string? problem)
    {
        problem = null;
        if (channel < 0 || channel >= Channels) { problem = $"no channel {channel} (0-{Channels - 1})"; return null; }
        var list = frames[channel];
        if (frame < 0 || frame >= list.Count) { problem = $"frame {frame} was not recorded (0-{list.Count - 1})"; return null; }
        int k = -1;
        if (list[frame].Level > 0) for (int i = frame; i >= 0; i--) if (list[i].KeyOn) { k = i; break; }
        if (k < 0) for (int i = frame + 1; i < list.Count; i++) if (list[i].KeyOn && list[i].Level > 0) { k = i; break; }
        if (k < 0) { problem = $"{CapturedNote.ChannelName(Console, channel)} plays no note at or after frame {frame}"; return null; }
        // a key-on whose first frame is silent (keyed with volume 0 then raised) starts the note at its first sounding frame
        while (k < list.Count && list[k].Level == 0) k++;
        if (k >= list.Count) { problem = "the note never sounds"; return null; }
        var life = new List<CaptureFrame> { list[k] };
        var end = CaptureEnd.RecordingEnd;
        for (int i = k + 1; i < list.Count; i++)
        {
            if (list[i].KeyOn) { end = CaptureEnd.Retrigger; break; }
            if (list[i].Level == 0) { end = CaptureEnd.Silenced; break; }
            if (life.Count >= CapturedNote.MaxFrames) { end = CaptureEnd.Cap; break; }
            life.Add(list[i]);
        }
        byte[] regs = null!;
        for (int i = k; i >= 0 && regs == null; i--) keyRegs.TryGetValue((channel, i), out regs!);
        return new CapturedNote { Console = Console, Channel = channel, KeyOnFrame = k, End = end, Regs = regs ?? lastRegs, Life = life.ToArray() };
    }

    /// <summary>The frames of a channel (tests, the report).</summary>
    public IReadOnlyList<CaptureFrame> FramesOf(int channel) => frames[channel];
}

/// <summary>The per-console rules that turn a chip's probed state into a <see cref="CaptureFrame"/>.</summary>
public static class CaptureRules
{
    /// <summary>NES: the level a pulse / noise channel outputs (0 when disabled, its length counter ran out, or a pulse's timer is below 8), the triangle 15 while both its counters run.</summary>
    public static CaptureFrame Nes(int channel, bool enabled, int lengthCounter, bool constant, int vol, int envDecay, int timer, int duty, int linear = 0, bool keyOn = false)
    {
        int level = channel switch
        {
            0 or 1 => !enabled || lengthCounter == 0 || timer < 8 ? 0 : constant ? vol : envDecay,
            2 => enabled && lengthCounter > 0 && linear > 0 && timer >= 2 ? 15 : 0,
            _ => !enabled || lengthCounter == 0 ? 0 : constant ? vol : envDecay,
        };
        return new CaptureFrame { Level = (byte)level, Period = (ushort)timer, Duty = (byte)duty, KeyOn = keyOn };
    }

    /// <summary>NES: a write that keys a channel ($4003, $4007, $400B, $400F), or -1.</summary>
    public static int NesKeyed(int address) => (address & 0xFFFF) switch { 0x4003 => 0, 0x4007 => 1, 0x400B => 2, 0x400F => 3, _ => -1 };

    /// <summary>Game Boy: a write that triggers a channel (NRx4 with bit 7), or -1.</summary>
    public static int GbKeyed(int reg, byte v) => (v & 0x80) == 0 ? -1 : (reg & 0xFF) switch { 0x14 => 0, 0x19 => 1, 0x1E => 2, 0x23 => 3, _ => -1 };

    /// <summary>Game Boy: wave level code (NR32 bits 6-5) as a 0-15 level.</summary>
    public static int GbWaveLevel(int code) => code switch { 1 => 15, 2 => 7, 3 => 3, _ => 0 };
}
