using System;
using System.IO;

namespace NesEmulator.Gb;

/// <summary>
/// The Game Boy sound chip heard through TGB Dual's (Hii, 2001) apu.cpp (<see cref="TgbdApu"/>; GPL-2.0-or-later, a
/// documented exception - see docs/projects/foreign-apu-project.md). Game Boy APU id "TGBD" (GbCores, the GB APU menu).
/// Register writes are queued with the running clock; every 70,224 T-cycles (one GB frame, like the libretro
/// frontend's refresh) a whole block of 44,100 Hz samples is rendered at once, replaying the queue. The engine times
/// length, envelope and sweep itself, so <see cref="FrameSequencerStep"/> does nothing.
/// Not an APU_GB: the bridges that read APU_GB's channel views, wave RAM or the instrument hooks do not see this chip.
/// Its save state starts with an APU_GB-format block (registers and wave RAM), so a core swap to or from any
/// APU_GB-based chip loads; the engine's own state follows behind a magic number (absent = start fresh from the
/// registers).
/// </summary>
public sealed class APU_TGBD : IGbApu
{
    private const int BaseClock = 4194304;
    private const int Magic = 0x44424754;   // "TGBD"

    private readonly GbModel model;
    private TgbdApu? engineField;
    private TgbdApu Engine => engineField ??= new TgbdApu(model == GbModel.Cgb);
    private APU_GB? shadow;
    private int clock;              // T-cycles since the last render (TGB's cpu total_clock, rebased per frame)
    private long rateAcc;           // dmy_renderer's error-diffused samples-per-frame
    private readonly short[] block = new short[1024 * 2];
    private readonly short[] ring = new short[1 << 16];
    private int ringHead, ringCount;

    public APU_TGBD(GbModel model) { this.model = model; }

    public string CoreName => "TGBD";
    public int SampleRate => TgbdApu.Rate;
    public void ResetPostBoot() { Engine.Reset(); clock = 0; rateAcc = 0; ringHead = ringCount = 0; }
    public void FrameSequencerStep() { }
    public byte ReadRegister(int reg) => reg >= 0x10 && reg <= 0x3F ? Engine.Read(reg) : (byte)0xFF;

    public void WriteRegister(int reg, byte v)
    {
        if (reg >= 0x10 && reg <= 0x3F) Engine.Write(reg, v, clock);
    }

    public void Tick(int tCycles)
    {
        clock += tCycles;
        if (clock < TgbdApu.ClocksPerFrame) return;
        rateAcc += (long)TgbdApu.Rate * clock;
        int n = (int)Math.Min(rateAcc / BaseClock, block.Length / 2);
        rateAcc -= (long)n * BaseClock;
        if (rateAcc > BaseClock) rateAcc = 0;
        var e = Engine;
        e.Render(block, n, clock);
        e.Rebase(clock);
        clock = 0;
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
        // APU_GB-format block first: the registers and wave RAM, no notes playing.
        var g = shadow ??= new APU_GB(model);
        g.WriteRegister(0x26, 0x00); g.WriteRegister(0x26, 0x80);
        for (int i = 0; i < 16; i++) g.WriteRegister(0x30 + i, e.mem[0x20 + i]);
        for (int reg = 0x10; reg <= 0x25; reg++)
            g.WriteRegister(reg, reg is 0x14 or 0x19 or 0x1E or 0x23 ? (byte)(e.mem[reg - 0x10] & 0x7F) : e.mem[reg - 0x10]);
        if (e.stat.master_enable == 0) g.WriteRegister(0x26, 0x00);
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
        // A foreign state (another Game Boy chip's): start the engine fresh from its registers and wave RAM.
        e.Reset();
        clock = 0; rateAcc = 0;
        bool power = (g.ReadRegister(0x26) & 0x80) != 0;
        e.Write(0x26, power ? (byte)0x80 : (byte)0x00, 0);
        for (int i = 0; i < 16; i++) e.Write(0x30 + i, g.WaveRam[i], 0);
        for (int reg = 0x10; reg <= 0x25; reg++)
            e.Write(reg, reg is 0x14 or 0x19 or 0x1E or 0x23 ? (byte)(g.ReadRegister(reg) & 0x7F) : g.ReadRegister(reg), 0);
    }
}
