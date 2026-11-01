using System;
using System.IO;

namespace NesEmulator.Gb;

/// <summary>
/// The Game Boy sound chip heard through VBA-M's 2007 sound engine (commit 5e3902b4, VBA 1.8.0-beta, right before the
/// switch to blargg's Gb_Apu; <see cref="VbamGbSound"/>; GPL-2.0, a documented exception - see
/// docs/projects/foreign-apu-project.md). Game Boy APU id "VBAM" (GbCores, the GB APU menu). A copy of APU_VBA.
/// The engine runs one tick per 48 M-cycles (192 T-cycles) like VBA's GB.cpp, and times length, envelope and sweep
/// itself, so <see cref="FrameSequencerStep"/> does nothing. Its ~21,845 Hz output is held (zero-order) to 22,050 Hz.
/// Not an APU_GB: the bridges that read APU_GB's channel views, wave RAM or the instrument hooks (BrokenDev's channel
/// mute, the WAVE memory domain, the plugin's sampler / instrument push) do not see this chip. Its save state starts
/// with an APU_GB-format block (the registers and wave RAM), so a core swap to or from any APU_GB-based chip loads;
/// the engine's own state follows behind a magic number (absent = start fresh from the registers).
/// </summary>
public sealed class APU_VBAM : IGbApu
{
    private const int TicksPerSample = VbamGbSound.ClockTicks * 4;   // M-cycles -> T-cycles
    private const int BaseClock = 4194304;
    private const int Magic = 0x314D4256;                            // "VBM1"

    private readonly VbamGbSound engine = new();
    private readonly GbModel model;
    private APU_GB? shadow;
    private int engineAcc;
    private long outAcc;
    private short lastL, lastR;
    private readonly short[] ring = new short[1 << 16];
    private int ringHead, ringCount;

    public APU_VBAM(GbModel model) { this.model = model; }

    public string CoreName => "VBAM";
    public int SampleRate => VbamGbSound.Rate;
    public void ResetPostBoot() => engine.Reset();
    public void FrameSequencerStep() { }
    public byte ReadRegister(int reg) => engine.Read(reg);
    public void WriteRegister(int reg, byte v) => engine.Write(reg, v);

    public void Tick(int tCycles)
    {
        for (engineAcc += tCycles; engineAcc >= TicksPerSample; engineAcc -= TicksPerSample)
            engine.Tick(out lastL, out lastR);
        for (outAcc += (long)tCycles * VbamGbSound.Rate; outAcc >= BaseClock; outAcc -= BaseClock)
        {
            Push(lastL); Push(lastR);
        }
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
        // APU_GB-format block first: the registers and wave RAM, no notes playing.
        var g = shadow ??= new APU_GB(model);
        g.WriteRegister(0x26, 0x00); g.WriteRegister(0x26, 0x80);
        for (int i = 0; i < 16; i++) g.WriteRegister(0x30 + i, engine.Mem[0x30 + i]);
        for (int reg = 0x10; reg <= 0x25; reg++)
            g.WriteRegister(reg, reg is 0x14 or 0x19 or 0x1E or 0x23 ? (byte)(engine.Mem[reg] & 0x7F) : engine.Mem[reg]);
        if ((engine.Mem[0x26] & 0x80) == 0) g.WriteRegister(0x26, 0x00);
        g.SaveState(w);

        w.Write(Magic);
        engine.Save(w);
        w.Write(engineAcc); w.Write(outAcc); w.Write(lastL); w.Write(lastR);
    }

    public void LoadState(BinaryReader r)
    {
        var g = shadow ??= new APU_GB(model);
        g.LoadState(r);
        try
        {
            if (r.ReadInt32() == Magic)
            {
                engine.Load(r);
                engineAcc = r.ReadInt32(); outAcc = r.ReadInt64(); lastL = r.ReadInt16(); lastR = r.ReadInt16();
                return;
            }
        }
        catch (EndOfStreamException) { }
        // A foreign state (another Game Boy chip's): start the engine fresh from its registers and wave RAM.
        engine.Reset();
        bool power = (g.ReadRegister(0x26) & 0x80) != 0;
        engine.Write(0x26, power ? (byte)0x80 : (byte)0x00);
        for (int i = 0; i < 16; i++) engine.Write(0x30 + i, g.WaveRam[i]);
        for (int reg = 0x10; reg <= 0x25; reg++)
            engine.Write(reg, reg is 0x14 or 0x19 or 0x1E or 0x23 ? (byte)(g.ReadRegister(reg) & 0x7F) : g.ReadRegister(reg));
        engineAcc = 0; outAcc = 0; lastL = lastR = 0;
    }
}
