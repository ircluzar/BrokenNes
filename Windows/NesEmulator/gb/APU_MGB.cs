using System;
using System.IO;

namespace NesEmulator.Gb;

/// <summary>
/// The Game Boy sound chip heard through minigb_apu as of Peanut-GB 024175d (2022-03-31, before its April 2022 rework; <see cref="MinigbApu"/>,
/// MIT). Game Boy APU id "MGB". Register writes go straight into the engine; it renders one SDL audio callback (803 stereo samples at 48 kHz)
/// per Game Boy frame (70,224 T-cycles), so every change is heard on that block grid. <see cref="FrameSequencerStep"/> does nothing (the
/// engine has no frame sequencer). Save state = an APU_GB-format block (registers + wave RAM) then the engine's whole state behind a magic number.
/// </summary>
public sealed class APU_MGB : IGbApu
{
    private const int ClocksPerFrame = 70224;
    private const int Magic = 0x3142474D;   // "MGB1"

    private readonly GbModel model;
    private MinigbApu? engineField;
    private MinigbApu Engine => engineField ??= new MinigbApu();
    private APU_GB? shadow;
    private int clock;
    private readonly short[] ring = new short[1 << 16];
    private int ringHead, ringCount;

    public APU_MGB(GbModel model) { this.model = model; }

    public string CoreName => "MGB";
    public int SampleRate => (int)MinigbApu.AudioSampleRate;
    public void ResetPostBoot() { Engine.Init(); clock = 0; ringHead = ringCount = 0; }
    public void FrameSequencerStep() { }
    public byte ReadRegister(int reg) => reg >= 0x10 && reg <= 0x3F ? Engine.Read((ushort)(0xFF00 + reg)) : (byte)0xFF;

    public void WriteRegister(int reg, byte v)
    {
        if (reg >= 0x10 && reg <= 0x3F) Engine.Write((ushort)(0xFF00 + reg), v);
    }

    public void Tick(int tCycles)
    {
        clock += tCycles;
        while (clock >= ClocksPerFrame)
        {
            clock -= ClocksPerFrame;
            var block = Engine.Callback();
            for (int i = 0; i < block.Length; i++)
            {
                float f = block[i] * 32767f;
                Push((short)(f > 32767f ? 32767 : f < -32768f ? -32768 : (int)f));
            }
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
        var e = Engine;
        var g = shadow ??= new APU_GB(model);
        g.WriteRegister(0x26, 0x00); g.WriteRegister(0x26, 0x80);
        for (int i = 0; i < 16; i++) g.WriteRegister(0x30 + i, e.Mem[0x20 + i]);
        for (int reg = 0x10; reg <= 0x25; reg++)
            g.WriteRegister(reg, reg is 0x14 or 0x19 or 0x1E or 0x23 ? (byte)(e.Mem[reg - 0x10] & 0x7F) : e.Mem[reg - 0x10]);
        g.SaveState(w);
        w.Write(Magic);
        e.Save(w);
        w.Write(clock);
    }

    public void LoadState(BinaryReader r)
    {
        var e = Engine;
        var g = shadow ??= new APU_GB(model);
        g.LoadState(r);
        try
        {
            if (r.ReadInt32() == Magic) { e.Load(r); clock = r.ReadInt32(); return; }
        }
        catch (EndOfStreamException) { }
        // A foreign state: rebuild from the registers and wave RAM; channels that were sounding are re-triggered.
        e.Init();
        clock = 0;
        for (int i = 0; i < 16; i++) e.Write((ushort)(0xFF30 + i), g.WaveRam[i]);
        for (int reg = 0x10; reg <= 0x25; reg++)
        {
            if (reg is 0x14 or 0x19 or 0x1E or 0x23) continue;
            e.Write((ushort)(0xFF00 + reg), g.ReadRegister(reg));
        }
        int[] hi = { 0x14, 0x19, 0x1E, 0x23 };
        for (int ch = 0; ch < 4; ch++)
        {
            var v = g.Channel(ch);
            if (ch < 3) e.Write((ushort)(0xFF00 + hi[ch] - 1), (byte)(v.Period & 0xFF));
            e.Write((ushort)(0xFF00 + hi[ch]), (byte)((ch < 3 ? (v.Period >> 8) & 7 : 0) | (g.ReadRegister(hi[ch]) & 0x40) | (v.Enabled ? 0x80 : 0)));
        }
    }
}
