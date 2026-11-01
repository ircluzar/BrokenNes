using System;
using System.IO;

namespace NesEmulator.Gb;

/// <summary>
/// The Game Boy sound chip heard through gnuboy 1.0.3's sound engine (<see cref="GnubSound"/>, ported from gnuboy's
/// sound.c - GPL-2.0, a documented exception, see THIRD_PARTY_NOTICES.md section 2). Unlike APU_ZS15 there is no stock
/// unit in front: the game reads gnuboy's registers (raw bytes, no read masks; NR52 status as gnuboy keeps it).
/// Clocked from <see cref="Tick"/> (T-cycles / 2 = gnuboy's cpu.snd units); <see cref="FrameSequencerStep"/> is ignored
/// (gnuboy times length, envelope and sweep per output sample). Output is gnuboy's own: 44,620 Hz stereo 8-bit, widened
/// to 16 bits (x 256), reported as <see cref="SampleRate"/> = 44,620 for the host to resample.
/// Not an <see cref="APU_GB"/>: no Channel(n) views, WaveRam, NR50/NR51 members, debug mute or override hook, so tools
/// and bridges that need those do not see this chip. A state saved by another chip is not understood: loading it
/// restarts the engine from its reset.
/// </summary>
public sealed class APU_GNUB : IGbApu, IGbSoundingChannels
{
    public string CoreName => "GNUB";
    public int SampleRate => GnubSound.ActualRate;

    private const int Magic = 0x42554E47;   // "GNUB"
    private const int Version = 2;   // 2: an APU_GB-format block comes first (so other Game Boy chips can take GNUB's registers over in a live swap)

    private readonly GnubSound snd;
    private readonly GbModel model;
    private APU_GB? shadow;                     // writes / reads the APU_GB-format block of the state
    private int halfT;                          // odd T-cycle carried to the next Tick
    private readonly short[] ring = new short[1 << 15];
    private int ringHead, ringCount;

    public APU_GNUB(GbModel model)
    {
        this.model = model;
        snd = new GnubSound(model == GbModel.Cgb) { Output = Push };
        snd.sound_reset();
    }

    public void ResetPostBoot() { snd.sound_reset(); halfT = 0; ringHead = ringCount = 0; }

    public void Tick(int tCycles)
    {
        int t = halfT + tCycles;
        halfT = t & 1;
        snd.Advance(t >> 1);
    }

    public void FrameSequencerStep() { }

    public byte ReadRegister(int reg) => reg >= 0x10 && reg <= 0x3F ? snd.sound_read(reg) : (byte)0xFF;

    public void WriteRegister(int reg, byte v)
    {
        if (reg >= 0x10 && reg <= 0x3F) snd.sound_write(reg, v);
    }

    private void Push(int l, int r)
    {
        Put((short)(l << 8)); Put((short)(r << 8));
    }

    private void Put(short s)
    {
        if (ringCount == ring.Length) { ringHead = (ringHead + 1) & (ring.Length - 1); ringCount--; }
        ring[(ringHead + ringCount) & (ring.Length - 1)] = s;
        ringCount++;
    }

    public int ReadSamples(short[] buffer)
    {
        int n = Math.Min(ringCount, buffer.Length) & ~1;
        for (int i = 0; i < n; i++) buffer[i] = ring[(ringHead + i) & (ring.Length - 1)];
        ringHead = (ringHead + n) & (ring.Length - 1);
        ringCount -= n;
        return n;
    }

    public void SaveState(BinaryWriter w)
    {
        // APU_GB-format block first: the registers and wave RAM, no notes playing (as APU_VBA / APU_TGBD do).
        var g = shadow ??= new APU_GB(model);
        g.WriteRegister(0x26, 0x00); g.WriteRegister(0x26, 0x80);
        for (int i = 0; i < 16; i++) g.WriteRegister(0x30 + i, snd.Wave[i]);
        for (int reg = 0x10; reg <= 0x25; reg++)
            g.WriteRegister(reg, reg is 0x14 or 0x19 or 0x1E or 0x23 ? (byte)(snd.Hi[reg] & 0x7F) : snd.Hi[reg]);
        if ((snd.Hi[0x26] & 0x80) == 0) g.WriteRegister(0x26, 0x00);
        g.SaveState(w);

        w.Write(Magic); w.Write(Version);
        w.Write(snd.Hi); w.Write(snd.Wave); w.Write(snd.Snd); w.Write(halfT);
        for (int c = 0; c < 4; c++)
        {
            var ch = snd.Ch[c];
            w.Write(ch.on); w.Write(ch.pos); w.Write(ch.cnt); w.Write(ch.encnt); w.Write(ch.swcnt); w.Write(ch.len);
            w.Write(ch.enlen); w.Write(ch.swlen); w.Write(ch.swfreq); w.Write(ch.freq); w.Write(ch.envol); w.Write(ch.endir);
        }
    }

    public void LoadState(BinaryReader r)
    {
        ringHead = ringCount = 0;
        try
        {
            var g = shadow ??= new APU_GB(model);
            g.LoadState(r);                                                    // every Game Boy chip's state starts with an APU_GB-format block
            if (r.BaseStream.Position >= r.BaseStream.Length || r.ReadInt32() != Magic || r.ReadInt32() != Version)
            {
                // another chip's state: start fresh from its registers and wave RAM
                ResetPostBoot();
                bool power = (g.ReadRegister(0x26) & 0x80) != 0;
                snd.sound_write(0x26, power ? (byte)0x80 : (byte)0x00);
                for (int i = 0; i < 16; i++) snd.sound_write(0x30 + i, g.WaveRam[i]);
                for (int reg = 0x10; reg <= 0x25; reg++)
                    snd.sound_write(reg, reg is 0x14 or 0x19 or 0x1E or 0x23 ? (byte)(g.ReadRegister(reg) & 0x7F) : g.ReadRegister(reg));
                return;
            }
            r.Read(snd.Hi, 0, snd.Hi.Length); r.Read(snd.Wave, 0, snd.Wave.Length);
            snd.Snd = r.ReadInt32(); halfT = r.ReadInt32();
            for (int c = 0; c < 4; c++)
            {
                GnubSound.Chan ch;
                ch.on = r.ReadInt32(); ch.pos = r.ReadUInt32(); ch.cnt = r.ReadInt32(); ch.encnt = r.ReadInt32(); ch.swcnt = r.ReadInt32();
                ch.len = r.ReadInt32(); ch.enlen = r.ReadInt32(); ch.swlen = r.ReadInt32(); ch.swfreq = r.ReadInt32(); ch.freq = r.ReadInt32();
                ch.envol = r.ReadInt32(); ch.endir = r.ReadInt32();
                snd.Ch[c] = ch;
            }
        }
        catch (EndOfStreamException) { ResetPostBoot(); }
    }

    // IGbSoundingChannels: gnuboy keeps NR52 as the raw byte written, so a swap asks the engine
    public int SoundingMask { get { int m = 0; for (int i = 0; i < 4; i++) if (snd.Ch[i].on != 0) m |= 1 << i; return m; } }
    public int PeriodHigh(int channel) => channel switch { 0 => snd.Hi[0x14] & 7, 1 => snd.Hi[0x19] & 7, 2 => snd.Hi[0x1E] & 7, _ => 0 };
}
