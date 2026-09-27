using System;
using System.Collections.Generic;
using System.Reflection;
using NesEmulator.Mix;
using NesEmulator.Snes;

namespace NesEmulator.Mix
{
    // =================================================================================================
    // MIX LAB audio bridges. Both directions talk to contracts only:
    //   SNES side: ISnesApu (ports + RunTo + ReadSamples) for driving, ISnesApuProbe (DSP registers + ARAM) for
    //              listening. APU_SFC joins through SfcApuAdapter; APU_HLE has no DSP, so it can be driven but
    //              not listened to (and, being silent by design, is silent here too).
    //   NES side:  IAPU, discovered by CoreRegistry (APU_SNES), and any IAPU as the back end of NesApuOnSnes.
    // Timebase: one NES CPU cycle = 12 SNES master clocks (1.789773 MHz x 12 = 21.477 MHz).
    // =================================================================================================

    /// <summary>What a bridge needs to LISTEN to a SNES audio unit: the S-DSP register file and ARAM.</summary>
    public interface ISnesApuProbe
    {
        byte[] DspRegs { get; }
        byte[] Aram { get; }
    }

    /// <summary>APU_SFC behind ISnesApu + ISnesApuProbe (its DSP registers and ARAM are public already).</summary>
    internal sealed class SfcApuAdapter : ISnesApu, ISnesApuProbe
    {
        private readonly APU_SFC a;
        public SfcApuAdapter(APU_SFC apu) { a = apu; }
        public string CoreName => a.CoreName;
        public void Reset() => a.Reset();
        public byte ReadPort(int port) => a.ReadPort(port);
        public void WritePort(int port, byte value) => a.WritePort(port, value);
        public void RunTo(long masterClock) => a.RunTo(masterClock);
        public int SampleRate => a.SampleRate;
        public int ReadSamples(Span<short> dest) => a.ReadSamples(dest);
        public string Describe() => a.Describe();
        public byte[] DspRegs => a.Dsp.Regs;
        public byte[] Aram => a.Aram;
    }

    /// <summary>
    /// Drives ANY ISnesApu from outside the SNES CPU: boots a 22-byte SPC700 program through the standard IPL upload
    /// protocol, after which each (DSP register, value) pair written to ports 1/2 with a fresh counter in port 0 is
    /// copied into the S-DSP, and the counter echoed back.
    /// </summary>
    internal sealed class SpcDspDriver
    {
        private readonly ISnesApu apu;
        private long mc;
        private byte counter;
        public bool Booted { get; private set; }
        public string Note = "";
        public long MasterClock => mc;
        public long DspWrites { get; private set; }
        public long Failed; public string Ports => $"{apu.ReadPort(0):X2} {apu.ReadPort(1):X2} {apu.ReadPort(2):X2} {apu.ReadPort(3):X2} sent={counter:X2}";

        public SpcDspDriver(ISnesApu apu) { this.apu = apu; }

        /// <summary>Advance the audio unit to at least this master clock (it never goes backwards).</summary>
        public void RunTo(long masterClock) { if (masterClock > mc) mc = masterClock; apu.RunTo(mc); }
        private void Pump(int clocks = 256) { mc += clocks; apu.RunTo(mc); }
        private bool WaitPort0(byte v, int iterations = 4000)
        {
            for (int i = 0; i < iterations; i++) { if (apu.ReadPort(0) == v) return true; Pump(); }
            return false;
        }

        // $0200:  MOV A,$F4 / CMP A,$00 / BEQ -6 / MOV A,$F5 / MOV $F2,A / MOV A,$F6 / MOV $F3,A /
        //         MOV A,$F4 / MOV $00,A / MOV $F4,A / BRA $0200
        private static readonly byte[] Loop =
        {
            0xE4, 0xF4, 0x64, 0x00, 0xF0, 0xFA, 0xE4, 0xF5, 0xC4, 0xF2, 0xE4, 0xF6, 0xC4, 0xF3,
            0xE4, 0xF4, 0xC4, 0x00, 0xC4, 0xF4, 0x2F, 0xEA,
        };

        public bool Boot(IList<(ushort addr, byte[] data)> extraBlocks)
        {
            // Nothing goes to zero page: the IPL keeps its own destination pointer at $00/$01 while it loads.
            var blocks = new List<(ushort, byte[])> { (0x0200, Loop) };
            blocks.AddRange(extraBlocks);
            for (int i = 0; i < 400000 && !(apu.ReadPort(0) == 0xAA && apu.ReadPort(1) == 0xBB); i++) Pump();
            if (!(apu.ReadPort(0) == 0xAA && apu.ReadPort(1) == 0xBB)) { Note = "no IPL ready signal ($AA/$BB)"; return false; }
            byte kick = 0xCC, idx = 0;
            foreach (var (addr, data) in blocks)
            {
                apu.WritePort(1, 1); apu.WritePort(2, (byte)addr); apu.WritePort(3, (byte)(addr >> 8)); apu.WritePort(0, kick);
                if (!WaitPort0(kick)) { Note = $"IPL did not acknowledge block at ${addr:X4}"; return false; }
                for (int i = 0; i < data.Length; i++)
                {
                    apu.WritePort(1, data[i]); apu.WritePort(0, (byte)i);
                    if (!WaitPort0((byte)i)) { Note = $"IPL stalled at byte {i} of block ${addr:X4}"; return false; }
                    idx = (byte)i;
                }
                kick = (byte)(idx + 2);
            }
            apu.WritePort(1, 0); apu.WritePort(2, 0x00); apu.WritePort(3, 0x02); apu.WritePort(0, kick);
            for (int i = 0; i < 64; i++) Pump();                       // let the driver start and settle
            counter = apu.ReadPort(0);
            // Prove the driver answers before trusting it.
            if (!WriteDsp(0x0C, 0x00)) { Note = "uploaded driver never answered (this SNES APU does not run SPC700 code)"; return false; }
            Booted = true;
            return true;
        }

        public bool WriteDsp(byte reg, byte value)
        {
            counter++;
            apu.WritePort(1, reg); apu.WritePort(2, value); apu.WritePort(0, counter);
            bool ok = WaitPort0(counter, 400);
            if (ok) DspWrites++; else Failed++;
            return ok;
        }
    }

    /// <summary>
    /// The NES APU's control logic, rebuilt from the register writes alone (no front-core internals needed):
    /// length counters, envelopes, sweep, the triangle's linear counter and the frame sequencer. It produces what
    /// each channel should sound like; a SNES audio unit then produces the sound.
    /// </summary>
    internal sealed class NesChannelModel
    {
        public sealed class Pulse
        {
            public int Duty, Vol, Timer, Len, EnvDiv, EnvDecay, SweepDiv, SweepPeriod, SweepShift;
            public bool Halt, Const, EnvStart, SweepOn, SweepNeg, SweepReload, Enabled;
            public int Target(bool ones) { int d = Timer >> SweepShift; return SweepNeg ? Timer - d - (ones ? 1 : 0) : Timer + d; }
            public bool Audible(bool ones) => Enabled && Len > 0 && Timer >= 8 && Target(ones) <= 0x7FF;
            public int Volume => Const ? Vol : EnvDecay;
        }
        public readonly Pulse[] P = { new(), new() };
        public int TriTimer, TriLen, TriLinear, TriReload; public bool TriControl, TriReloadFlag, TriEnabled;
        public int NoiseVol, NoisePeriodIdx, NoiseLen, NoiseEnvDiv, NoiseEnvDecay; public bool NoiseHalt, NoiseConst, NoiseEnvStart, NoiseEnabled, NoiseShort;
        public bool DmcEnabled; public int DmcStarts;
        private int frameCycle; private bool mode5;

        private static readonly int[] LengthTable = { 10, 254, 20, 2, 40, 4, 80, 6, 160, 8, 60, 10, 14, 12, 26, 14, 12, 16, 24, 18, 48, 20, 96, 22, 192, 24, 72, 26, 16, 28, 32, 30 };
        public static readonly int[] NoisePeriods = { 4, 8, 16, 32, 64, 96, 128, 160, 202, 254, 380, 508, 762, 1016, 2034, 4068 };

        public bool TriAudible => TriEnabled && TriLen > 0 && TriLinear > 0 && TriTimer >= 2;
        public bool NoiseAudible => NoiseEnabled && NoiseLen > 0;
        public int NoiseVolume => NoiseConst ? NoiseVol : NoiseEnvDecay;

        public void Write(ushort a, byte v)
        {
            if (a >= 0x4000 && a <= 0x4007)
            {
                var p = P[(a >> 2) & 1];
                switch (a & 3)
                {
                    case 0: p.Duty = v >> 6; p.Halt = (v & 0x20) != 0; p.Const = (v & 0x10) != 0; p.Vol = v & 15; break;
                    case 1: p.SweepOn = (v & 0x80) != 0; p.SweepPeriod = (v >> 4) & 7; p.SweepNeg = (v & 8) != 0; p.SweepShift = v & 7; p.SweepReload = true; break;
                    case 2: p.Timer = (p.Timer & 0x700) | v; break;
                    case 3: p.Timer = (p.Timer & 0xFF) | ((v & 7) << 8); if (p.Enabled) p.Len = LengthTable[v >> 3]; p.EnvStart = true; break;
                }
                return;
            }
            switch (a)
            {
                case 0x4008: TriControl = (v & 0x80) != 0; TriReload = v & 0x7F; break;
                case 0x400A: TriTimer = (TriTimer & 0x700) | v; break;
                case 0x400B: TriTimer = (TriTimer & 0xFF) | ((v & 7) << 8); if (TriEnabled) TriLen = LengthTable[v >> 3]; TriReloadFlag = true; break;
                case 0x400C: NoiseHalt = (v & 0x20) != 0; NoiseConst = (v & 0x10) != 0; NoiseVol = v & 15; break;
                case 0x400E: NoiseShort = (v & 0x80) != 0; NoisePeriodIdx = v & 15; break;
                case 0x400F: if (NoiseEnabled) NoiseLen = LengthTable[v >> 3]; NoiseEnvStart = true; break;
                case 0x4015:
                    P[0].Enabled = (v & 1) != 0; if (!P[0].Enabled) P[0].Len = 0;
                    P[1].Enabled = (v & 2) != 0; if (!P[1].Enabled) P[1].Len = 0;
                    TriEnabled = (v & 4) != 0; if (!TriEnabled) TriLen = 0;
                    NoiseEnabled = (v & 8) != 0; if (!NoiseEnabled) NoiseLen = 0;
                    if ((v & 0x10) != 0) DmcStarts++;
                    DmcEnabled = (v & 0x10) != 0; break;
                case 0x4017:
                    mode5 = (v & 0x80) != 0; frameCycle = 0;
                    if (mode5) { Quarter(); Half(); }
                    break;
            }
        }

        public void Clock(int cpuCycles)
        {
            // 4-step: quarter frames at 3729/7457/11186/14915, halves at 7457 and 14915; 5-step: 3729/7457/11186/18641, halves at 7457/18641.
            for (int i = 0; i < cpuCycles; i++)
            {
                frameCycle++;
                if (!mode5)
                {
                    if (frameCycle == 3729 || frameCycle == 11186) Quarter();
                    else if (frameCycle == 7457) { Quarter(); Half(); }
                    else if (frameCycle >= 14915) { Quarter(); Half(); frameCycle = 0; }
                }
                else
                {
                    if (frameCycle == 3729 || frameCycle == 11186) Quarter();
                    else if (frameCycle == 7457) { Quarter(); Half(); }
                    else if (frameCycle >= 18641) { Quarter(); Half(); frameCycle = 0; }
                }
            }
        }

        private static void Env(ref bool start, ref int div, ref int decay, int period, bool loop)
        {
            if (start) { start = false; decay = 15; div = period; return; }
            if (div > 0) { div--; return; }
            div = period;
            if (decay > 0) decay--; else if (loop) decay = 15;
        }

        private void Quarter()
        {
            foreach (var p in P) Env(ref p.EnvStart, ref p.EnvDiv, ref p.EnvDecay, p.Vol, p.Halt);
            Env(ref NoiseEnvStart, ref NoiseEnvDiv, ref NoiseEnvDecay, NoiseVol, NoiseHalt);
            if (TriReloadFlag) TriLinear = TriReload; else if (TriLinear > 0) TriLinear--;
            if (!TriControl) TriReloadFlag = false;
        }

        private void Half()
        {
            for (int i = 0; i < 2; i++)
            {
                var p = P[i];
                if (!p.Halt && p.Len > 0) p.Len--;
                int target = p.Target(i == 0);
                if (p.SweepDiv == 0 && p.SweepOn && p.SweepShift > 0 && p.Timer >= 8 && target <= 0x7FF) p.Timer = target;
                if (p.SweepDiv == 0 || p.SweepReload) { p.SweepDiv = p.SweepPeriod; p.SweepReload = false; } else p.SweepDiv--;
            }
            if (!TriControl && TriLen > 0) TriLen--;
            if (!NoiseHalt && NoiseLen > 0) NoiseLen--;
        }
    }

    /// <summary>Minimal BRR encoder/decoder (filter 0 on encode; all four filters on decode).</summary>
    internal static class Brr
    {
        /// <summary>One looping block per 16 nibbles; the last block carries END+LOOP.</summary>
        public static byte[] Encode(int[] nibbles, int shift = 12)
        {
            int blocks = nibbles.Length / 16; var outp = new byte[blocks * 9];
            for (int b = 0; b < blocks; b++)
            {
                outp[b * 9] = (byte)((shift << 4) | (b == blocks - 1 ? 0x03 : 0x00));
                for (int i = 0; i < 8; i++)
                    outp[b * 9 + 1 + i] = (byte)(((nibbles[b * 16 + i * 2] & 15) << 4) | (nibbles[b * 16 + i * 2 + 1] & 15));
            }
            return outp;
        }

        /// <summary>Decode from start until an END block (max blocks). Returns samples and the sample index of the loop point (-1: no loop).</summary>
        public static (short[] samples, int loopIndex) Decode(byte[] aram, int start, int loopAddr, int maxBlocks = 1024)
        {
            var s = new List<short>(); int p1 = 0, p2 = 0, loopIndex = -1;
            int a = start & 0xFFFF;
            for (int b = 0; b < maxBlocks; b++)
            {
                if (a == loopAddr) loopIndex = s.Count;
                byte h = aram[a]; int shift = h >> 4, filter = (h >> 2) & 3; bool end = (h & 1) != 0, loop = (h & 2) != 0;
                for (int i = 0; i < 16; i++)
                {
                    int by = aram[(a + 1 + i / 2) & 0xFFFF]; int n = (i & 1) == 0 ? by >> 4 : by & 15; if (n >= 8) n -= 16;
                    int v = shift <= 12 ? (n << shift) >> 1 : (n < 0 ? -2048 : 0);
                    switch (filter)
                    {
                        case 1: v += p1 + ((-p1) >> 4); break;
                        case 2: v += (p1 << 1) + ((-(p1 * 3)) >> 5) - p2 + (p2 >> 4); break;
                        case 3: v += (p1 << 1) + ((-(p1 * 13)) >> 6) - p2 + ((p2 * 3) >> 4); break;
                    }
                    v = Math.Clamp(v, -32768, 32767); v = (short)(v << 1) >> 1;
                    s.Add((short)v); p2 = p1; p1 = v;
                }
                a = (a + 9) & 0xFFFF;
                if (end) { if (!loop) loopIndex = -1; break; }
            }
            return (s.ToArray(), loopIndex);
        }
    }
}

namespace NesEmulator
{
    /// <summary>
    /// MIX LAB: a NES APU whose sound comes from a SNES audio unit (any ISnesApu, MixConfig.SnesApu).
    /// A NES APU core (MixConfig.NesFrontApu) stays in front for $4015 reads, the frame IRQ and DMC DMA; its own
    /// sound is discarded. The bridge rebuilds each channel's state from the register writes and plays it on the S-DSP:
    /// voices 0/1 = pulses (four duty samples), 2 = triangle, 3 = noise (the DSP's noise generator), 4 = a short
    /// noise hit for every DMC sample start (DPCM samples themselves are not converted).
    /// </summary>
    public sealed class APU_SNES : IAPU
    {
        private readonly IAPU front;
        private readonly ISnesApu snes;
        private readonly SpcDspDriver drv;
        private readonly NesChannelModel model = new();
        private readonly Queue<float> queue = new();
        private readonly short[] pull = new short[4096];
        private readonly int[] dspCache = new int[128];
        private long nesCycles; private int sinceSync; private int dmcSeen; private int channelMask = 0x1F;
        public string Status { get; private set; } = "";
        public long DspWrites => drv.DspWrites;
        private string Dsp() { if (snes is not ISnesApuProbe pr) return ""; var r = pr.DspRegs; string V(int v) => $"v{v}(vol={(sbyte)r[v*16]} p={r[v*16+2] | r[v*16+3] << 8:X4} src={r[v*16+4]} adsr1={r[v*16+5]:X2} gain={r[v*16+7]:X2} envx={r[v*16+8]} outx={(sbyte)r[v*16+9]})"; return $"{V(0)} {V(1)} FLG={r[0x6C]:X2} MVOL={r[0x0C]:X2} KON={r[0x4C]:X2} KOF={r[0x5C]:X2} NON={r[0x3D]:X2} ENDX={r[0x7C]:X2}"; }
        private static readonly int DebugEvery = int.TryParse(Environment.GetEnvironmentVariable("MIX_APULOG"), out var de) ? de : 0; private int dbgCycles;

        private const int DirPage = 0x03, SampleBase = 0x0400;

        public APU_SNES(Bus bus)
        {
            var t = CoreRegistry.ApuTypes.TryGetValue(MixConfig.NesFrontApu, out var ft) ? ft : throw new ArgumentException($"No NES APU '{MixConfig.NesFrontApu}'");
            front = CoreRegistry.CreateInstance<IAPU>(t, bus) ?? throw new InvalidOperationException("front APU");
            snes = SnesCores.CreateApu(MixConfig.SnesApu);
            drv = new SpcDspDriver(snes);
            Boot();
        }

        public string CoreName => $"SNES:{snes.CoreName}+{front.CoreName}";
        public string Description => "MIX LAB: NES channels played on a SNES S-DSP";
        public int Performance => 0; public int Rating => 1; public string Category => "Experimental";

        private void Boot()
        {
            Array.Fill(dspCache, -1);
            // Samples: 4 pulse duties (16 samples each = one period), a 32-step triangle.
            var dir = new List<byte>(); var data = new List<byte>(); int addr = SampleBase;
            void Add(int[] nib) { var b = Brr.Encode(nib); dir.Add((byte)addr); dir.Add((byte)(addr >> 8)); dir.Add((byte)addr); dir.Add((byte)(addr >> 8)); data.AddRange(b); addr += b.Length; }
            foreach (int high in new[] { 2, 4, 8, 12 }) { var n = new int[16]; for (int i = 0; i < 16; i++) n[i] = i < high ? 6 : -6; Add(n); }
            { var n = new int[32]; for (int i = 0; i < 32; i++) n[i] = i < 16 ? 7 - i : i - 24; Add(n); }
            var blocks = new List<(ushort, byte[])> { ((ushort)(DirPage << 8), dir.ToArray()), ((ushort)SampleBase, data.ToArray()) };
            if (!drv.Boot(blocks)) { Status = "SNES APU not driven: " + drv.Note; return; }
            // Globals: full main volume, no echo, echo writes off, sample directory, all voices keyed on and looping.
            W(0x6C, 0x20); W(0x0C, 0x60); W(0x1C, 0x60); W(0x2C, 0); W(0x3C, 0); W(0x4D, 0); W(0x2D, 0); W(0x5D, DirPage); W(0x3D, 0x08);
            for (int v = 0; v < 5; v++)
            {
                W((byte)(v * 16 + 0), 0); W((byte)(v * 16 + 1), 0);
                W((byte)(v * 16 + 4), (byte)(v switch { 2 => 4, _ => 2 }));
                if (v == 4) { W(0x45, 0xFF); W(0x46, 0x16); } // DMC hit: fast attack, quick decay to silence
                else { W((byte)(v * 16 + 5), 0x00); W((byte)(v * 16 + 7), 0x7F); } // direct gain, full
            }
            W(0x5C, 0x00); W(0x4C, 0x0F);                     // key on voices 0-3
            Status = $"driving {snes.CoreName}";
        }

        private void W(byte reg, byte v) { if (dspCache[reg] == v) return; if (drv.WriteDsp(reg, v)) dspCache[reg] = v; }

        private static readonly int[] DspNoiseHz = { 0, 16, 21, 25, 31, 42, 50, 63, 83, 100, 125, 167, 200, 250, 333, 400, 500, 667, 800, 1000, 1300, 1600, 2000, 2700, 3200, 4000, 5300, 6400, 8000, 10700, 16000, 32000 };

        private void Sync()
        {
            if (!drv.Booted) return;
            for (int i = 0; i < 2; i++)
            {
                var p = model.P[i]; bool on = p.Audible(i == 0) && (channelMask & (1 << i)) != 0 && !PitchGuard.Blocks(1789773.0 / (16 * (p.Timer + 1)));
                int pitch = Math.Clamp(229091 / (p.Timer + 1), 0, 0x3FFF);
                W((byte)(i * 16 + 2), (byte)pitch); W((byte)(i * 16 + 3), (byte)(pitch >> 8));
                W((byte)(i * 16 + 4), (byte)p.Duty);
                byte vol = (byte)(on ? p.Volume * 2 : 0); W((byte)(i * 16 + 0), vol); W((byte)(i * 16 + 1), vol);   // full NES volume ~ -20 dBFS here too
            }
            {
                bool on = model.TriAudible && (channelMask & 4) != 0 && !PitchGuard.Blocks(1789773.0 / (32 * (model.TriTimer + 1)));
                int pitch = Math.Clamp(229091 / (model.TriTimer + 1), 0, 0x3FFF);
                W(0x22, (byte)pitch); W(0x23, (byte)(pitch >> 8));
                byte vol = (byte)(on ? 26 : 0); W(0x20, vol); W(0x21, vol);
            }
            {
                bool on = model.NoiseAudible && (channelMask & 8) != 0;
                double f = 1789773.0 / NesChannelModel.NoisePeriods[model.NoisePeriodIdx];
                int best = 31; double bd = double.MaxValue;
                for (int i = 1; i < 32; i++) { double d = Math.Abs(Math.Log(DspNoiseHz[i] / f)); if (d < bd) { bd = d; best = i; } }
                W(0x6C, (byte)(0x20 | best));
                byte vol = (byte)(on ? model.NoiseVolume * 2 : 0); W(0x30, vol); W(0x31, vol);
            }
            if (model.DmcStarts != dmcSeen && (channelMask & 0x10) != 0)
            {
                dmcSeen = model.DmcStarts; W(0x40, 30); W(0x41, 30);
                dspCache[0x4C] = -1; W(0x4C, 0x10);              // key on the DMC hit (KON is a strobe: always write)
                dspCache[0x4C] = -1; dspCache[0x3D] = -1; W(0x3D, 0x18);
            }
        }

        public void Step(int cpuCycles)
        {
            front.Step(cpuCycles);
            model.Clock(cpuCycles);
            nesCycles += cpuCycles; sinceSync += cpuCycles;
            if (sinceSync >= 1864) { sinceSync = 0; Sync(); }
            if (DebugEvery > 0 && (dbgCycles += cpuCycles) >= DebugEvery) { dbgCycles = 0; var m = model; Console.Error.WriteLine($"t={nesCycles / 1789773.0:F2}s p0[en={m.P[0].Enabled} len={m.P[0].Len} vol={m.P[0].Volume} t={m.P[0].Timer} aud={m.P[0].Audible(true)}] p1[en={m.P[1].Enabled} len={m.P[1].Len} vol={m.P[1].Volume} t={m.P[1].Timer} aud={m.P[1].Audible(false)}] tri[en={m.TriEnabled} len={m.TriLen} lin={m.TriLinear} t={m.TriTimer}] noise[en={m.NoiseEnabled} len={m.NoiseLen} vol={m.NoiseVolume}] dmc={m.DmcStarts} writes={drv.DspWrites} failed={drv.Failed} ports={drv.Ports} | {Dsp()}"); }       // ~960 Hz
            drv.RunTo(nesCycles * 12);
            if (front.GetQueuedSampleCount() > 8192) front.GetAudioSamples();
        }

        public void WriteAPURegister(ushort address, byte value) { model.Write(address, value); front.WriteAPURegister(address, value); }
        public byte ReadAPURegister(ushort address) => front.ReadAPURegister(address);

        private void Drain()
        {
            int n;
            while ((n = snes.ReadSamples(pull)) > 0)
                for (int i = 0; i + 1 < n; i += 2) queue.Enqueue((pull[i] + pull[i + 1]) / 65536f);
            while (queue.Count > 96000) queue.Dequeue();
        }

        public float[] GetAudioSamples(int maxSamples = 0)
        {
            Drain();
            int n = maxSamples > 0 ? Math.Min(maxSamples, queue.Count) : queue.Count;
            var r = new float[n]; for (int i = 0; i < n; i++) r[i] = queue.Dequeue();
            return r;
        }
        public int GetQueuedSampleCount() { Drain(); return queue.Count; }
        public int GetSampleRate() => snes.SampleRate;
        public void SetEnabledChannels(int channelMask) { this.channelMask = channelMask; front.SetEnabledChannels(channelMask); }
        public object GetState() => front.GetState();
        public void SetState(object state) => front.SetState(state);
        public void ClearAudioBuffers() { Drain(); queue.Clear(); front.ClearAudioBuffers(); }
        public void Reset() { front.Reset(); }
    }
}

namespace NesEmulator.Mix
{
    /// <summary>A NES APU core (any id) running on its own, driven through WriteAPURegister, for SNES->NES audio.</summary>
    internal sealed class NesApuHost
    {
        public readonly NesEmulator.IAPU Apu;
        public NesApuHost(string apuId)
        {
            var nes = new NesEmulator.NES { RomName = "mixlab-idle.nes" };
            nes.LoadROM(SnesToNes.IdleRom());
            if (!nes.SetApuCore(apuId) || !nes.GetApuCoreId().EndsWith("_" + apuId, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"NES APU '{apuId}' not applied (got {nes.GetApuCoreId()})");
            var bus = (NesEmulator.Bus)typeof(NesEmulator.NES).GetField("bus", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(nes)!;
            Apu = bus.ActiveAPU;   // bus.apu is not the live core after a switch
        }
    }

    /// <summary>
    /// MIX LAB: a SNES audio unit whose sound comes from a NES APU. The real SNES unit (MixConfig.SnesFrontApu, which must
    /// expose ISnesApuProbe) stays in front so the game's SPC700 driver, handshakes and uploads all work; its sound is
    /// discarded. 960 times a second the bridge reads the eight DSP voices and replays the loudest on the NES channels:
    /// tonal voices -> pulse 1, pulse 2, triangle (pitch = playback rate / the sample's own period, measured once per
    /// sample by autocorrelation of its loop); noise voices and one-shot (non-looping) samples -> the noise channel.
    /// </summary>
    internal sealed class NesApuOnSnes : ISnesApu
    {
        private readonly ISnesApu front;
        private readonly ISnesApuProbe probe;
        private readonly NesEmulator.IAPU back;
        private readonly string backId;
        private long lastClock, nextSync;
        private readonly short[] scratch = new short[8192];
        private readonly Queue<float> queue = new();
        private readonly Dictionary<int, double> periodCache = new();
        private readonly int[] nesCache = new int[0x18];
        public long Syncs; public int VoicesMapped;

        public NesApuOnSnes()
        {
            front = SnesCores.CreateApu(MixConfig.SnesFrontApu);
            probe = front as ISnesApuProbe ?? throw new NotSupportedException($"SNES APU '{MixConfig.SnesFrontApu}' exposes no DSP state to listen to");
            backId = MixConfig.NesBackApu;
            back = new NesApuHost(backId).Apu;
            Array.Fill(nesCache, -1);
            N(0x4017, 0x40); N(0x4015, 0x0F);
            N(0x4001, 0x00); N(0x4005, 0x00); N(0x400F, 0x08);
        }

        public string CoreName => $"NES:{back.CoreName}+{front.CoreName}";
        public void Reset() => front.Reset();
        public byte ReadPort(int port) => front.ReadPort(port);
        public void WritePort(int port, byte value) => front.WritePort(port, value);
        public int SampleRate => back.GetSampleRate();
        public string Describe() => $"{CoreName} syncs={Syncs} mapped={VoicesMapped} | {front.Describe()}";

        private void N(ushort a, byte v) { if (nesCache[a - 0x4000] == v) return; nesCache[a - 0x4000] = v; back.WriteAPURegister(a, v); }

        public void RunTo(long masterClock)
        {
            const long SyncEvery = 21_477_272 / 960;
            while (lastClock < masterClock)
            {
                long t = Math.Min(masterClock, nextSync);
                front.RunTo(t);
                int cycles = (int)(t / 12 - lastClock / 12);
                if (cycles > 0) back.Step(cycles);
                lastClock = t;
                if (t >= nextSync) { Sync(); nextSync += SyncEvery; }
            }
            while (front.ReadSamples(scratch) > 0) { }
            var s = back.GetAudioSamples();
            foreach (var x in s) queue.Enqueue(x);
            while (queue.Count > 96000) queue.Dequeue();
        }

        public int ReadSamples(Span<short> dest)
        {
            int n = 0;
            while (n + 1 < dest.Length && queue.Count > 0)
            {
                short v = (short)Math.Clamp(queue.Dequeue() * 32767f, -32768, 32767);
                dest[n++] = v; dest[n++] = v;
            }
            return n;
        }

        private static readonly int[] DspNoiseHz = { 0, 16, 21, 25, 31, 42, 50, 63, 83, 100, 125, 167, 200, 250, 333, 400, 500, 667, 800, 1000, 1300, 1600, 2000, 2700, 3200, 4000, 5300, 6400, 8000, 10700, 16000, 32000 };

        /// <summary>The sample's own waveform period in samples (0 = no loop: a one-shot, treated as percussion).</summary>
        private double Period(int srcn)
        {
            var r = probe.DspRegs; var ar = probe.Aram;
            int d = (r[0x5D] << 8) + srcn * 4;
            int start = ar[d & 0xFFFF] | ar[(d + 1) & 0xFFFF] << 8, loop = ar[(d + 2) & 0xFFFF] | ar[(d + 3) & 0xFFFF] << 8;
            int key = (start << 16) | loop;
            if (periodCache.TryGetValue(key, out var cached)) return cached;
            var (smp, loopIdx) = Brr.Decode(ar, start, loop);
            double period = 0;
            if (loopIdx >= 0 && smp.Length - loopIdx >= 16)
            {
                int L = smp.Length - loopIdx;
                var w = new double[L * 3]; for (int i = 0; i < w.Length; i++) w[i] = smp[loopIdx + i % L];
                double best = 0; var corr = new double[Math.Min(L, 2048) + 1];
                for (int lag = 4; lag < corr.Length; lag++)
                {
                    double s = 0, e = 0; for (int i = 0; i < L; i++) { s += w[i] * w[i + lag]; e += w[i] * w[i]; }
                    corr[lag] = e > 0 ? s / e : 0; if (corr[lag] > best) best = corr[lag];
                }
                for (int lag = 4; lag < corr.Length; lag++)
                    if (corr[lag] >= 0.9 * best && corr[lag] > 0.3 && (lag + 1 >= corr.Length || corr[lag] >= corr[lag + 1])) { period = lag; break; }
                if (period == 0) period = L;
            }
            periodCache[key] = period;
            return period;
        }

        private void Sync()
        {
            Syncs++;
            var r = probe.DspRegs;
            var tonal = new List<(int v, double loud, double hz)>(); (int v, double loud) noise = (-1, 0);
            for (int v = 0; v < 8; v++)
            {
                int b = v * 16, envx = r[b + 8] & 0x7F;
                double loud = envx * (Math.Abs((sbyte)r[b]) + Math.Abs((sbyte)r[b + 1])) / 2.0;
                if (loud < 40) continue;
                bool isNoise = (r[0x3D] & (1 << v)) != 0;
                int pitch = r[b + 2] | (r[b + 3] & 0x3F) << 8;
                double period = isNoise ? 0 : Period(r[b + 4]);
                if (isNoise || period == 0) { if (loud > noise.loud) noise = (v, loud); continue; }
                double hz = 32000.0 * pitch / 4096.0 / period;
                if (PitchGuard.Blocks(hz)) continue;   // muted: the next loudest voice gets the channel
                tonal.Add((v, loud, hz));
            }
            tonal.Sort((a, c) => c.loud.CompareTo(a.loud));
            VoicesMapped = Math.Min(tonal.Count, 3) + (noise.v >= 0 ? 1 : 0);
            int Vol(double loud) => Math.Clamp((int)Math.Round(15 * Math.Sqrt(loud / 16129.0) * 1.6), 1, 15);
            for (int ch = 0; ch < 2; ch++)
            {
                ushort b = (ushort)(0x4000 + ch * 4);
                if (ch < tonal.Count && tonal[ch].hz > 20)
                {
                    int t = Math.Clamp((int)Math.Round(1789773.0 / (16 * tonal[ch].hz) - 1), 8, 2047);
                    N(b, (byte)((ch == 0 ? 0x80 : 0x40) | 0x30 | Vol(tonal[ch].loud)));
                    N((ushort)(b + 2), (byte)t);
                    N((ushort)(b + 3), (byte)(0x08 | (t >> 8)));
                }
                else N(b, (byte)((ch == 0 ? 0x80 : 0x40) | 0x30));
            }
            if (tonal.Count > 2 && tonal[2].hz > 20)
            {
                int t = Math.Clamp((int)Math.Round(1789773.0 / (32 * tonal[2].hz) - 1), 2, 2047);
                N(0x4008, 0xFF); N(0x400A, (byte)t); N(0x400B, (byte)(0x08 | (t >> 8)));
            }
            else N(0x4008, 0x80);
            if (noise.v >= 0)
            {
                double hz = (r[0x3D] & (1 << noise.v)) != 0 ? DspNoiseHz[r[0x6C] & 31] : 4000;
                int best = 0; double bd = double.MaxValue;
                for (int i = 0; i < 16; i++) { double d = Math.Abs(Math.Log(1789773.0 / NesChannelModel.NoisePeriods[i] / 16 / Math.Max(hz, 1))); if (d < bd) { bd = d; best = i; } }
                N(0x400C, (byte)(0x30 | Vol(noise.loud))); N(0x400E, (byte)best);
            }
            else N(0x400C, 0x30);
        }
    }
}
