// Instrument extraction: the glue between a running core and the capture core (InstrumentCapture.cs), shared by the plugin (Core), the desktop (Windows) and the Workshop (BrokenDev's CLI).
// A feed hooks the sound chip's register-write observer (to see the key-ons) and is told the end of every video frame, where it reads the chip's own probe (APU_FIX.ProbeControl, APU_GB's
// channel probe) into one CaptureFrame per channel. Detach() puts the observer back as it was.
using BrokenNes2.Instruments;
using NesEmulator;
using NesEmulator.Gb;

namespace BrokenNes2.Extract;

public interface IExtractFeed
{
    InstrumentRecorder Recorder { get; }
    void EndFrame();
    void Detach();
}

public sealed class NesExtractFeed : IExtractFeed
{
    private readonly APU_FIX apu;
    private readonly Func<byte[], uint> latchOf;
    private readonly Action<ushort, byte>? before;
    private readonly bool[] keyed = new bool[4];
    private readonly byte[] latch = new byte[0x18];
    private readonly CaptureFrame[] row = new CaptureFrame[4];
    public InstrumentRecorder Recorder { get; } = new(ConsoleId.Nes);

    /// <summary><paramref name="copyLatch"/> is the machine's CopyApuLatch (the $4000-$4017 values as last written).</summary>
    public NesExtractFeed(APU_FIX apu, Func<byte[], uint> copyLatch)
    {
        this.apu = apu;
        latchOf = copyLatch;
        before = apu.RegisterWriteObserver;
        apu.RegisterWriteObserver = (a, v) => { before?.Invoke(a, v); int c = CaptureRules.NesKeyed(a); if (c >= 0) keyed[c] = true; };
    }

    /// <summary>The FIX-family APU of a machine, or null (the capture reads APU_FIX's probe).</summary>
    public static APU_FIX? FixOf(NES nes) => nes.GetApuStateProbe() as APU_FIX;

    public void EndFrame()
    {
        latchOf(latch);
        var p = apu.ProbeControl();
        row[0] = CaptureRules.Nes(0, p.P1Enabled, p.P1Length, p.P1Const, p.P1Vol, p.P1EnvDecay, p.P1Timer, p.P1Duty, keyOn: keyed[0]);
        row[1] = CaptureRules.Nes(1, p.P2Enabled, p.P2Length, p.P2Const, p.P2Vol, p.P2EnvDecay, p.P2Timer, p.P2Duty, keyOn: keyed[1]);
        row[2] = CaptureRules.Nes(2, p.TEnabled, p.TLength, true, 15, 15, p.TTimer, 0, p.TLinear, keyOn: keyed[2]);
        var n = CaptureRules.Nes(3, p.NEnabled, p.NLength, p.NConst, p.NVol, p.NEnvDecay, 99, 0, keyOn: keyed[3]);
        n.Period = (ushort)(p.NPeriodReg & 15); n.Duty = (byte)(p.NPeriodReg >> 7 & 1);
        row[3] = n;
        Array.Clear(keyed);
        Recorder.Frame(row, latch);
    }

    public void Detach() => apu.RegisterWriteObserver = before;
}

public sealed class GbExtractFeed : IExtractFeed
{
    private readonly APU_GB apu;
    private readonly Action<int, byte>? before;
    private readonly bool[] keyed = new bool[4];
    private readonly CaptureFrame[] row = new CaptureFrame[4];
    private readonly byte[] regs = new byte[GbSnapshot.RegCount + GbSnapshot.WaveSize + 1];
    public InstrumentRecorder Recorder { get; } = new(ConsoleId.GameBoy);

    public GbExtractFeed(APU_GB apu)
    {
        this.apu = apu;
        before = apu.RegisterWriteObserver;
        apu.RegisterWriteObserver = (r, v) => { before?.Invoke(r, v); int c = CaptureRules.GbKeyed(r, v); if (c >= 0) keyed[c] = true; };
    }

    public void EndFrame()
    {
        for (int r = 0; r < GbSnapshot.RegCount; r++) regs[r] = apu.RawRegister(0x10 + r);
        apu.WaveRam.AsSpan(0, GbSnapshot.WaveSize).CopyTo(regs.AsSpan(GbSnapshot.RegCount));
        regs[^1] = (byte)(apu.Model == GbModel.Cgb ? 1 : 0);
        for (int c = 0; c < 4; c++)
        {
            bool on = apu.ChannelOn(c);
            int level = !on ? 0 : c == 2 ? CaptureRules.GbWaveLevel(apu.RawRegister(0x1C) >> 5 & 3) : apu.ChannelVolume(c);
            int period = c == 3 ? apu.RawRegister(0x22) : apu.ChannelFreq(c);
            int duty = c switch { 0 => apu.RawRegister(0x11) >> 6, 1 => apu.RawRegister(0x16) >> 6, 2 => apu.RawRegister(0x1C) >> 5 & 3, _ => apu.RawRegister(0x22) >> 3 & 1 };
            row[c] = new CaptureFrame { Level = (byte)level, Period = (ushort)period, Duty = (byte)duty, KeyOn = keyed[c] };
        }
        Array.Clear(keyed);
        Recorder.Frame(row, regs);
    }

    public void Detach() => apu.RegisterWriteObserver = before;
}
