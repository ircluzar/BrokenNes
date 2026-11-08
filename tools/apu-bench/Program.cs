// ApuBench: what each BrokenNes sound chip costs to make audio, in the smallest setup that makes it sound (docs/projects/nano-lightest-apu.md).
//
//   dotnet run -c Release --project tools/apu-bench -- [-seconds 3] [-runs 7] [-only NES:FIX,GB:GB] [-csv out.csv]
//
// Every rig plays the same little tune: every channel the chip has is keyed on at setup and gets a new note every 125 ms (8 changes a second),
// the host side runs in 1 ms slices (step the chip, drain its samples), as a real-time host would. A rig is timed over <seconds> of emulated audio,
// <runs> times after a one-second warm-up; the median is reported. Nothing in the cores is changed: they are driven through their public seams.

using System.Diagnostics;
using System.Reflection;
using NesEmulator;
using NesEmulator.Gb;
using NesEmulator.Mix;
using NesEmulator.Sega;
using NesEmulator.Snes;
using NesEmulator.X86;

double seconds = 3; int runs = 7; string? only = null; string? csv = null;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "-seconds": seconds = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
        case "-runs": runs = int.Parse(args[++i]); break;
        case "-only": only = args[++i]; break;
        case "-csv": csv = args[++i]; break;
    }
}

var rigs = new List<Func<Rig>>();
foreach (var id in CoreRegistry.ApuIds) { var c = id; rigs.Add(() => new NesRig(c)); }
foreach (var id in GbCores.ApuIds) { if (id.Equals("NES", StringComparison.OrdinalIgnoreCase)) continue; var c = id; rigs.Add(() => new GbRig(c)); }
foreach (var id in SnesCores.ApuIds) { if (id is "NES" or "HLE") continue; var c = id; rigs.Add(() => new SnesRig(c)); }
foreach (var id in SmsSoundFactories.Ids) { if (id.StartsWith("NES", StringComparison.OrdinalIgnoreCase) || id == "GG" || id == "GGS") continue; var c = id; rigs.Add(() => new SmsRig(c)); }
foreach (var id in GenesisSoundFactories.Ids) { if (id is "NES" or "MD-STUB") continue; var c = id; rigs.Add(() => new MdRig(c)); }
foreach (var id in OplEngines.Factories.Keys) { var c = id; rigs.Add(() => new OplEngineRig(c)); }
rigs.Add(() => new YmfmOplRig());

var filter = only?.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
var rows = new List<string>();
Console.WriteLine($"# ApuBench {seconds}s x {runs} runs, {Environment.ProcessorCount} logical CPUs, {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
Console.WriteLine("name,rate,ns_per_sample,cpu_pct,alloc_bytes_per_s,state_kb,peak,status");
foreach (var make in rigs)
{
    Rig rig;
    try { rig = make(); }
    catch (Exception e) { Console.WriteLine($"?,0,0,0,0,0,0,create failed: {Flat(e)}"); continue; }
    if (filter != null && !filter.Contains(rig.Name)) continue;
    string line;
    try { line = Measure(rig, seconds, runs); }
    catch (Exception e) { line = $"{rig.Name},0,0,0,0,0,0,failed: {Flat(e)}"; }
    Console.WriteLine(line);
    rows.Add(line);
}
if (csv != null) File.WriteAllLines(csv, new[] { "name,rate,ns_per_sample,cpu_pct,alloc_bytes_per_s,state_kb,peak,status" }.Concat(rows));
return 0;

static string Flat(Exception e) => (e.GetType().Name + " " + e.Message).Replace(',', ';').Replace('\n', ' ').Replace('\r', ' ');

static string Measure(Rig rig, double seconds, int runs)
{
    rig.Setup();
    rig.Play(1.0);                 // warm-up: JIT tiers, tables, queues
    rig.Peak = 0;
    rig.Play(0.5);
    float peak = rig.Peak;
    var times = new List<double>(); var allocs = new List<double>(); long samples = 0;
    for (int r = 0; r < runs; r++)
    {
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        samples = rig.Play(seconds);
        sw.Stop();
        long a1 = GC.GetAllocatedBytesForCurrentThread();
        times.Add(sw.Elapsed.TotalSeconds);
        allocs.Add((a1 - a0) / seconds);
    }
    times.Sort(); allocs.Sort();
    double t = times[times.Count / 2];
    double nsPerSample = t * 1e9 / Math.Max(1, samples);
    double cpuPct = t / seconds * 100.0;
    long state = StateSize.Of(rig.StateRoot);
    string status = peak > 0.0005f ? "ok" : "SILENT";
    return string.Join(',', rig.Name, rig.Rate, nsPerSample.ToString("F1"), cpuPct.ToString("F3"), allocs[allocs.Count / 2].ToString("F0"), (state / 1024.0).ToString("F1"), peak.ToString("F4"), status);
}

/// <summary>A sound chip in its minimal host. <see cref="Play"/> returns the native-rate sample frames it produced.</summary>
abstract class Rig
{
    public abstract string Name { get; }
    public abstract int Rate { get; }
    public abstract object StateRoot { get; }
    public float Peak;
    public abstract void Setup();
    /// <summary>A new note on every channel (k counts up).</summary>
    protected abstract void Change(int k);
    /// <summary>Run 1 ms of emulated time, return the sample frames drained.</summary>
    protected abstract int Slice();

    protected static readonly double[] Notes = { 220, 246.94, 261.63, 293.66, 329.63, 349.23, 392, 440 };
    private int k, ms;
    public long Play(double seconds)
    {
        long frames = 0; int n = (int)Math.Round(seconds * 1000);
        for (int i = 0; i < n; i++)
        {
            if (ms++ % 125 == 0) Change(k++);
            frames += Slice();
        }
        return frames;
    }
    protected void PeakF(ReadOnlySpan<float> s) { foreach (var v in s) { float a = Math.Abs(v); if (a > Peak) Peak = a; } }
    protected void PeakS(ReadOnlySpan<short> s) { foreach (var v in s) { float a = Math.Abs(v / 32768f); if (a > Peak) Peak = a; } }
}

/// <summary>A NES APU core (any IAPU, including the harvested foreign chips and the bridge cores) on an idle NES: only the APU is stepped, 32 CPU cycles at a time, exactly as NesApuInstrument does.</summary>
sealed class NesRig : Rig
{
    readonly string id; IAPU apu = null!; readonly float[] buf = new float[4096]; double acc;
    public NesRig(string id) { this.id = id; }
    public override string Name => "NES:" + id;
    public override int Rate => apu?.GetSampleRate() ?? 0;
    public override object StateRoot => apu;
    public override void Setup()
    {
        var nes = new NES { RomName = "bench-idle.nes" };
        nes.LoadROM(IdleRom());
        if (!nes.SetApuCore(id)) throw new InvalidOperationException("SetApuCore failed");
        var bus = (Bus)typeof(NES).GetField("bus", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(nes)!;
        apu = bus.ActiveAPU;
        apu.WriteAPURegister(0x4017, 0x40);
        apu.WriteAPURegister(0x4015, 0x0F);
    }
    static byte[] IdleRom()
    {
        var img = new byte[16 + 0x4000];
        img[0] = (byte)'N'; img[1] = (byte)'E'; img[2] = (byte)'S'; img[3] = 0x1A; img[4] = 1;
        img[16] = 0x4C; img[17] = 0x00; img[18] = 0xC0;      // JMP $C000
        img[16 + 0x3FFC] = 0x00; img[16 + 0x3FFD] = 0xC0; img[16 + 0x3FFA] = 0x00; img[16 + 0x3FFB] = 0xC0; img[16 + 0x3FFE] = 0x00; img[16 + 0x3FFF] = 0xC0;
        return img;
    }
    protected override void Change(int k)
    {
        void Pulse(int b, double f, byte ctl) { int p = (int)(1789773 / (16 * f)) - 1; apu.WriteAPURegister((ushort)b, ctl); apu.WriteAPURegister((ushort)(b + 1), 0x08); apu.WriteAPURegister((ushort)(b + 2), (byte)p); apu.WriteAPURegister((ushort)(b + 3), (byte)(0xF8 | (p >> 8))); }
        Pulse(0x4000, Notes[k % 8], 0xB8);
        Pulse(0x4004, Notes[(k + 2) % 8] * 1.5, 0x78);
        int t = (int)(1789773 / (32 * Notes[(k + 4) % 8] / 2)) - 1;
        apu.WriteAPURegister(0x4008, 0xFF); apu.WriteAPURegister(0x400A, (byte)t); apu.WriteAPURegister(0x400B, (byte)(0xF8 | (t >> 8)));
        apu.WriteAPURegister(0x400C, 0x38); apu.WriteAPURegister(0x400E, (byte)(k & 0x0F)); apu.WriteAPURegister(0x400F, 0xF8);
    }
    protected override int Slice()
    {
        acc += 1789.773; int cyc = (int)acc; acc -= cyc; int got = 0;
        while (cyc > 0) { int s = Math.Min(32, cyc); apu.Step(s); cyc -= s; }
        int n; while ((n = apu.ReadSamples(buf)) > 0) { PeakF(buf.AsSpan(0, n)); got += n; if (n < buf.Length) break; }
        return got;
    }
}

/// <summary>A Game Boy APU: ticked 16 T-cycles at a time, its 512 Hz frame sequencer clocked every 8192 T-cycles (the DIV edge the board would supply).</summary>
sealed class GbRig : Rig
{
    readonly string id; IGbApu apu = null!; readonly short[] buf = new short[8192]; double acc; int seq;
    public GbRig(string id) { this.id = id; }
    public override string Name => "GB:" + id;
    public override int Rate => apu?.SampleRate ?? 0;
    public override object StateRoot => apu;
    public override void Setup()
    {
        apu = GbCores.CreateApu(id, GbModel.Dmg);
        apu.ResetPostBoot();
        W(0x26, 0x80); W(0x24, 0x77); W(0x25, 0xFF);
        W(0x11, 0x80); W(0x12, 0xF0); W(0x16, 0x80); W(0x17, 0xF0);
        W(0x1A, 0x00); for (int i = 0; i < 16; i++) W(0x30 + i, (byte)(i < 8 ? 0xCD : 0x32)); W(0x1A, 0x80); W(0x1C, 0x20);
        W(0x21, 0xF0);
    }
    void W(int r, byte v) => apu.WriteRegister(r, v);
    protected override void Change(int k)
    {
        int x1 = 2048 - (int)(131072 / Notes[k % 8]); W(0x13, (byte)x1); W(0x14, (byte)(0x80 | (x1 >> 8)));
        int x2 = 2048 - (int)(131072 / (Notes[(k + 2) % 8] * 1.5)); W(0x18, (byte)x2); W(0x19, (byte)(0x80 | (x2 >> 8)));
        int x3 = 2048 - (int)(65536 / (Notes[(k + 4) % 8] / 2)); W(0x1D, (byte)x3); W(0x1E, (byte)(0x80 | (x3 >> 8)));
        W(0x22, (byte)(0x50 | (k & 7))); W(0x23, 0x80);
    }
    protected override int Slice()
    {
        acc += 4194.304; int t = (int)acc; acc -= t;
        while (t > 0) { int s = Math.Min(16, t); apu.Tick(s); t -= s; seq += s; if (seq >= 8192) { seq -= 8192; apu.FrameSequencerStep(); } }
        int n = apu.ReadSamples(buf); PeakS(buf.AsSpan(0, n)); return n / 2;
    }
}

/// <summary>
/// A SNES audio unit (SPC700 + S-DSP + ARAM). It cannot sound without SPC700 code, so the rig plays the main CPU's half of the IPL boot protocol to upload
/// a 420-byte driver plus a one-block looping BRR square; the driver keys on all eight voices and then spins in BRA $.
/// The notes are HELD (eight voices, fixed pitches; see the doc's caveats). RunTo once per 1 ms.
/// </summary>
sealed class SnesRig : Rig
{
    const double Master = 21477272.0;
    readonly string id; ISnesApu apu = null!; readonly short[] buf = new short[8192]; long clock; double acc;
    public SnesRig(string id) { this.id = id; }
    public override string Name => "SNES:" + id;
    public override int Rate => apu?.SampleRate ?? 0;
    public override object StateRoot => apu;
    void Wait(Func<bool> cond)
    {
        for (int i = 0; i < 200000 && !cond(); i++) { clock += 256; apu.RunTo(clock); }
        if (!cond()) throw new InvalidOperationException("IPL handshake timed out: " + apu.Describe());
    }
    public override void Setup()
    {
        apu = SnesCores.CreateApu(id);
        apu.Reset();
        // $0200: sample directory (entry 0 -> $0204, loop $0204); $0204: one BRR block, end+loop, shift 11, a square; $0210: the driver.
        var img = new List<byte> { 0x04, 0x02, 0x04, 0x02, 0xB3, 0x77, 0x77, 0x77, 0x77, 0x99, 0x99, 0x99, 0x99, 0, 0, 0 };
        void Dsp(int r, int v) { img.Add(0x8F); img.Add((byte)r); img.Add(0xF2); img.Add(0x8F); img.Add((byte)v); img.Add(0xF3); }
        Dsp(0x6C, 0x20); Dsp(0x0C, 0x7F); Dsp(0x1C, 0x7F); Dsp(0x2C, 0); Dsp(0x3C, 0); Dsp(0x5D, 0x02); Dsp(0x2D, 0); Dsp(0x3D, 0); Dsp(0x4D, 0); Dsp(0x5C, 0);
        for (int v = 0; v < 8; v++)
        {
            double f = Notes[v] * (v % 2 == 1 ? 1.5 : 1);
            int pitch = (int)(f * 16 / 32000.0 * 4096);   // a 16-sample loop at 1.0 = 2 kHz
            Dsp(v * 16 + 0, 0x18); Dsp(v * 16 + 1, 0x18); Dsp(v * 16 + 2, pitch & 0xFF); Dsp(v * 16 + 3, (pitch >> 8) & 0x3F); Dsp(v * 16 + 4, 0); Dsp(v * 16 + 5, 0); Dsp(v * 16 + 7, 0x7F);
        }
        Dsp(0x4C, 0xFF);
        img.Add(0x2F); img.Add(0xFE);   // BRA $
        Wait(() => apu.ReadPort(0) == 0xAA && apu.ReadPort(1) == 0xBB);
        apu.WritePort(2, 0x00); apu.WritePort(3, 0x02); apu.WritePort(1, 0x01); apu.WritePort(0, 0xCC);
        Wait(() => apu.ReadPort(0) == 0xCC);
        for (int i = 0; i < img.Count; i++) { apu.WritePort(1, img[i]); apu.WritePort(0, (byte)i); int ii = i; Wait(() => apu.ReadPort(0) == (byte)ii); }
        apu.WritePort(2, 0x10); apu.WritePort(3, 0x02); apu.WritePort(1, 0x00); apu.WritePort(0, (byte)(img.Count + 1));
        Wait(() => apu.ReadPort(0) == (byte)(img.Count + 1));
        while (apu.ReadSamples(buf) > 0) { }
    }
    protected override void Change(int k) { }
    protected override int Slice()
    {
        acc += Master / 1000; long d = (long)acc; acc -= d; clock += d; apu.RunTo(clock);
        int got = 0, n; while ((n = apu.ReadSamples(buf)) > 0) { PeakS(buf.AsSpan(0, n)); got += n / 2; if (n < buf.Length) break; }
        return got;
    }
}

/// <summary>A Master System / Game Gear / Mark III sound unit (SN76489, plus the YM2413 on the FM units), run by Z80 clocks 16 at a time, no Z80.</summary>
sealed class SmsRig : Rig
{
    readonly string id; ISmsSound s = null!; readonly short[] buf = new short[8192]; double acc;
    public SmsRig(string id) { this.id = id; }
    public override string Name => "SMS:" + id;
    public override int Rate => s?.SampleRate ?? 0;
    public override object StateRoot => s;
    public override void Setup()
    {
        var req = new SmsSoundRequest(new SegaMachine(SegaTimeline.MasterSystem(SegaRegion.Ntsc), GenesisModel.Model1, 44100), NesEmulator.Systems.ConsoleKind.MasterSystem, PsgVariant.Sega);
        s = SmsSoundFactories.Create(id, req);
        s.Reset();
        if (s.HasFm) { s.WriteAudioControl(0x03); Fm(0x0E, 0); }
    }
    void Fm(int r, int v) { s.WriteFmAddress((byte)r); s.WriteFmData((byte)v); }
    protected override void Change(int k)
    {
        for (int c = 0; c < 3; c++)
        {
            int n = (int)(3579545 / (32 * Notes[(k + 2 * c) % 8] * (c == 1 ? 1.5 : 1)));
            s.WritePsg((byte)(0x80 | (c << 5) | (n & 0xF))); s.WritePsg((byte)((n >> 4) & 0x3F)); s.WritePsg((byte)(0x90 | (c << 5) | 2));
        }
        s.WritePsg((byte)(0xE4 | (k & 3))); s.WritePsg(0xF2);
        if (s.HasFm)
            for (int c = 0; c < 9; c++)
            {
                int f = (int)(Notes[(k + c) % 8] * 32768 / 49716.0);   // block 5
                Fm(0x30 + c, ((c % 15) + 1) << 4); Fm(0x20 + c, 0); Fm(0x10 + c, f & 0xFF); Fm(0x20 + c, 0x10 | (5 << 1) | ((f >> 8) & 1));
            }
    }
    protected override int Slice()
    {
        acc += 3579.545; int c = (int)acc; acc -= c;
        while (c > 0) { int st = Math.Min(16, c); s.Run(st); c -= st; }
        int got = 0, n; while ((n = s.ReadSamples(buf)) > 0) { PeakS(buf.AsSpan(0, n)); got += n / 2; if (n < buf.Length) break; }
        return got;
    }
}

/// <summary>A Genesis sound unit with its Z80 held in reset: the 68000 side requests the bus and writes the YM2612 at $A04000-3 and the PSG through the VDP port. RunTo once per 1 ms.</summary>
sealed class MdRig : Rig
{
    sealed class NoHost : IGenesisSoundHost
    {
        public byte ReadBus(int address, long masterClock, out int wait) { wait = 0; return 0xFF; }
        public void WriteBus(int address, byte value, long masterClock, out int wait) { wait = 0; }
    }
    readonly string id; IGenesisSound s = null!; readonly short[] buf = new short[8192]; long clock; double acc; double master;
    public MdRig(string id) { this.id = id; }
    public override string Name => "MD:" + id;
    public override int Rate => s?.SampleRate ?? 0;
    public override object StateRoot => s;
    public override void Setup()
    {
        var tl = SegaTimeline.Genesis(SegaRegion.Ntsc); master = tl.MasterHz;
        s = GenesisSoundFactories.Create(id, new SegaMachine(tl, GenesisModel.Model1, 44100));
        s.Attach(new NoHost());
        s.Reset();
        s.WriteBusRequest(true, clock);
        for (int i = 0; i < 100 && !s.IsBusGranted(clock); i++) { clock += 100; s.RunTo(clock); }
        Ym(0, 0x22, 0); Ym(0, 0x27, 0); Ym(0, 0x2B, 0);
        for (int ch = 0; ch < 6; ch++)
        {
            int part = ch / 3, c = ch % 3;
            for (int o = 0; o < 16; o += 4) { Ym(part, 0x30 + o + c, 0x01); Ym(part, 0x40 + o + c, 0x24); Ym(part, 0x50 + o + c, 0x1F); Ym(part, 0x60 + o + c, 0); Ym(part, 0x70 + o + c, 0); Ym(part, 0x80 + o + c, 0x0F); Ym(part, 0x90 + o + c, 0); }
            Ym(part, 0xB0 + c, 0x07); Ym(part, 0xB4 + c, 0xC0);
        }
    }
    void Ym(int part, int r, int v) { clock += 30; s.Write(0x4000 + part * 2, (byte)r, clock); clock += 30; s.Write(0x4001 + part * 2, (byte)v, clock); }
    protected override void Change(int k)
    {
        int[] code = { 0, 1, 2, 4, 5, 6 };
        for (int ch = 0; ch < 6; ch++)
        {
            int part = ch / 3, c = ch % 3; int f = (int)(Notes[(k + ch) % 8] * 131072 / 53267.0);
            Ym(0, 0x28, code[ch]); Ym(part, 0xA4 + c, (4 << 3) | (f >> 8)); Ym(part, 0xA0 + c, f & 0xFF); Ym(0, 0x28, 0xF0 | code[ch]);
        }
        for (int c = 0; c < 3; c++)
        {
            int n = (int)(3579545 / (32 * Notes[(k + 2 * c) % 8]));
            s.WritePsg((byte)(0x80 | (c << 5) | (n & 0xF)), clock); s.WritePsg((byte)((n >> 4) & 0x3F), clock); s.WritePsg((byte)(0x90 | (c << 5) | 2), clock);
        }
        s.WritePsg((byte)(0xE4 | (k & 3)), clock); s.WritePsg(0xF2, clock);
    }
    protected override int Slice()
    {
        acc += master / 1000; long d = (long)acc; acc -= d; clock += d; s.RunTo(clock);
        int got = 0, n; while ((n = s.ReadSamples(buf)) > 0) { PeakS(buf.AsSpan(0, n)); got += n / 2; if (n < buf.Length) break; }
        return got;
    }
}

static class Opl
{
    public static readonly int[] Op1 = { 0, 1, 2, 8, 9, 10, 16, 17, 18 };
    public static void Setup(Action<int, int> w)
    {
        w(0x01, 0x20); w(0xBD, 0);
        for (int ch = 0; ch < 9; ch++)
        {
            foreach (int o in new[] { Op1[ch], Op1[ch] + 3 }) { w(0x20 + o, 0x21); w(0x40 + o, o == Op1[ch] ? 0x18 : 0x08); w(0x60 + o, 0xF4); w(0x80 + o, 0x07); w(0xE0 + o, 0); }
            w(0xC0 + ch, 0x0E);
        }
    }
    public static void Change(Action<int, int> w, int k, double[] notes)
    {
        for (int ch = 0; ch < 9; ch++)
        {
            int f = (int)(notes[(k + ch) % 8] * 1048576 / 49716.0 / 16);   // block 4
            w(0xB0 + ch, 0); w(0xA0 + ch, f & 0xFF); w(0xB0 + ch, 0x20 | (4 << 2) | (f >> 8));
        }
    }
}

/// <summary>An AdLib alternative engine (IOplEngine): register writes in, mono samples out, 1 ms blocks.</summary>
sealed class OplEngineRig : Rig
{
    readonly string id; IOplEngine e = null!; readonly short[] buf = new short[4096]; double acc;
    public OplEngineRig(string id) { this.id = id; }
    public override string Name => "PC-OPL:" + id;
    public override int Rate => e?.Rate ?? 0;
    public override object StateRoot => e;
    public override void Setup() { e = OplEngines.Factories[id](); e.Reset(); Opl.Setup((r, v) => e.Write(r, (byte)v)); }
    protected override void Change(int k) => Opl.Change((r, v) => e.Write(r, (byte)v), k, Notes);
    protected override int Slice()
    {
        acc += e.Rate / 1000.0; int n = (int)acc; acc -= n; e.Generate(buf, n); PeakS(buf.AsSpan(0, n)); return n;
    }
}

/// <summary>The AdLib card's own engine (ymfm-derived Opl2), clocked by a fake PIT counter.</summary>
sealed class YmfmOplRig : Rig
{
    Opl2 o = null!; long pit; double acc; readonly short[] buf = new short[4096];
    public override string Name => "PC-OPL:YMFM";
    public override int Rate => Opl2.Rate;
    public override object StateRoot => o;
    public override void Setup() { o = new Opl2(() => pit) { Enabled = true }; o.Reset(); o.Restart(); Opl.Setup(W); }
    void W(int r, int v) { o.WriteAddress((byte)r); o.WriteData((byte)v); }
    protected override void Change(int k) => Opl.Change(W, k, Notes);
    protected override int Slice()
    {
        acc += 1193.182; long d = (long)acc; acc -= d; pit += d; o.CatchUp();
        Array.Clear(buf); int got = 0, n; while ((n = o.MixInto(buf, buf.Length)) > 0) { PeakS(buf.AsSpan(0, n)); got += n / 2; if (n < buf.Length) break; Array.Clear(buf); }
        return got;   // MixInto hands interleaved stereo shorts
    }
}

/// <summary>Rough retained size of an object graph (arrays: elements, objects: 8 bytes a field + header). Stops at the NES board (Bus, NES, CPU, PPU, mapper) so a NES APU is counted alone.</summary>
static class StateSize
{
    public static long Of(object root)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance); var stack = new Stack<object>(); stack.Push(root); long total = 0;
        while (stack.Count > 0)
        {
            var o = stack.Pop(); if (!seen.Add(o)) continue; var t = o.GetType();
            if (o is Array a)
            {
                var et = t.GetElementType()!;
                total += 24 + (long)a.Length * (et.IsValueType ? (et.IsPrimitive ? System.Runtime.InteropServices.Marshal.SizeOf(et == typeof(char) ? typeof(short) : et == typeof(bool) ? typeof(byte) : et) : 16) : 8);
                if (!et.IsValueType) foreach (var e in a) if (e != null && !Skip(e)) stack.Push(e);
                continue;
            }
            if (o is string s) { total += 22 + s.Length * 2; continue; }
            total += 16;
            for (var tt = t; tt != null && tt != typeof(object); tt = tt.BaseType)
                foreach (var f in tt.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    total += f.FieldType.IsValueType && f.FieldType.IsPrimitive ? System.Runtime.InteropServices.Marshal.SizeOf(f.FieldType == typeof(char) ? typeof(short) : f.FieldType == typeof(bool) ? typeof(byte) : f.FieldType) : 8;
                    if (f.FieldType.IsValueType) continue;
                    object? v; try { v = f.GetValue(o); } catch { continue; }
                    if (v != null && !Skip(v) && v is not Delegate && v is not Type && v is not Assembly) stack.Push(v);
                }
        }
        return total;
    }
    static bool Skip(object v) => v is NES || v is Bus || v is ICPU || v is IPPU || v is Cartridge || v.GetType().Namespace?.StartsWith("System.Reflection") == true;
}
