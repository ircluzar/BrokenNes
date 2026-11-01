using System;
using System.IO;

namespace NesEmulator.Gb;

/// <summary>
/// The Game Boy sound chip heard through GameYob's (Drenn/Stewmath, Nintendo DS) sound mapping (<see cref="GameYobSound"/>;
/// MIT, a documented exception - see docs/projects/foreign-apu-project.md). Game Boy APU id "GYOB" (GbCores, the GB
/// APU menu). The arm9 half (length/envelope/sweep) runs on every Tick; the DS-side commands it emits are delivered
/// once per Game Boy frame (70,224 T-cycles; GameYob's default non-"hyperSound" mode) and the emulated DS mixer then
/// renders that frame at 32,768 Hz. <see cref="FrameSequencerStep"/> does nothing (the engine times itself).
/// Not an APU_GB: bridges reading APU_GB's channel views do not see this chip. Save state = an APU_GB-format block
/// (registers + wave RAM) then the engine's state behind a magic number, so swaps to/from APU_GB-based chips load.
/// </summary>
public sealed class APU_GYOB : IGbApu
{
    private const int BaseClock = 4194304;
    private const int ClocksPerFrame = 70224;
    private const int Magic = 0x424F5947;   // "GYOB"

    private readonly GbModel model;
    private GameYobSound? engineField;
    private GameYobSound Engine => engineField ??= new GameYobSound();
    private APU_GB? shadow;
    private int clock;
    private long rateAcc;
    private readonly short[] block = new short[2048 * 2];
    private readonly short[] ring = new short[1 << 16];
    private int ringHead, ringCount;

    public APU_GYOB(GbModel model) { this.model = model; }

    public string CoreName => "GYOB";
    public int SampleRate => GameYobSound.Rate;
    public void ResetPostBoot() { Engine.Reset(); clock = 0; rateAcc = 0; ringHead = ringCount = 0; }
    public void FrameSequencerStep() { }
    public byte ReadRegister(int reg) => reg >= 0x10 && reg <= 0x3F ? Engine.Read(reg) : (byte)0xFF;

    public void WriteRegister(int reg, byte v)
    {
        if (reg >= 0x10 && reg <= 0x3F) Engine.Write(reg, v);
    }

    public void Tick(int tCycles)
    {
        var e = Engine;
        e.UpdateSound(tCycles);
        clock += tCycles;
        if (clock < ClocksPerFrame) return;
        rateAcc += (long)GameYobSound.Rate * clock;
        int n = (int)Math.Min(rateAcc / BaseClock, block.Length / 2);
        rateAcc -= (long)n * BaseClock;
        if (rateAcc > BaseClock) rateAcc = 0;
        clock = 0;
        e.Render(block, n);
        for (int i = 0; i < n * 2; i++) Push(block[i]);
    }

    private void Push(short s)
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
        var e = Engine;
        var g = shadow ??= new APU_GB(model);
        g.WriteRegister(0x26, 0x00); g.WriteRegister(0x26, 0x80);
        for (int i = 0; i < 16; i++) g.WriteRegister(0x30 + i, e.io[0x30 + i]);
        for (int reg = 0x10; reg <= 0x25; reg++)
            g.WriteRegister(reg, reg is 0x14 or 0x19 or 0x1E or 0x23 ? (byte)(e.io[reg] & 0x7F) : e.io[reg]);
        if ((e.io[0x26] & 0x80) == 0) g.WriteRegister(0x26, 0x00);
        g.SaveState(w);

        w.Write(Magic);
        e.Save(w);
        w.Write(clock); w.Write(rateAcc);
    }

    public void LoadState(BinaryReader r)
    {
        var e = Engine;
        var g = shadow ??= new APU_GB(model);
        g.LoadState(r);
        try
        {
            if (r.ReadInt32() == Magic)
            {
                e.Load(r);
                clock = r.ReadInt32(); rateAcc = r.ReadInt64();
                return;
            }
        }
        catch (EndOfStreamException) { }
        // A foreign state: like SoundEngine::refresh(), rebuild from the registers and wave RAM.
        e.Reset();
        clock = 0; rateAcc = 0;
        byte nr52 = g.ReadRegister(0x26);
        for (int i = 0; i < 16; i++) e.io[0x30 + i] = g.WaveRam[i];
        for (int reg = 0x10; reg <= 0x25; reg++) e.io[reg] = g.ReadRegister(reg);
        e.io[0x26] = nr52;
        e.Refresh();
    }
}
