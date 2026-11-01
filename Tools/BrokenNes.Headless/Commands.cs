using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using NesEmulator;

namespace BrokenNes.Headless;

public static class Commands
{
    // ---------------------------------------------------------------- cores
    public static int Cores()
    {
        void Show(string kind, IReadOnlyDictionary<string, Type> map)
        {
            Console.WriteLine($"{kind} ({map.Count}):");
            foreach (var (id, t) in map.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                Console.WriteLine($"  {id,-10} {t.FullName}");
        }
        Show("CPU", CoreRegistry.CpuTypes);
        Show("PPU", CoreRegistry.PpuTypes);
        Show("APU", CoreRegistry.ApuTypes);
        return 0;
    }

    // ---------------------------------------------------------------- run
    public static int Run(Args a)
    {
        var romPath = a.Positional.FirstOrDefault() ?? throw new ArgumentException("run: missing <rom>");
        var lo = Loadout.From(a);
        int frames = a.GetInt("frames", 300);
        int pngEvery = a.GetInt("png-every", 0);
        string outdir = a.Get("outdir") ?? "out";
        var input = new InputScript(a.Get("input"));
        var audio = a.Has("wav") ? new List<float>() : null;

        var nes = Harness.Boot(File.ReadAllBytes(romPath), lo, Path.GetFileName(romPath));
        Console.WriteLine($"rom={Path.GetFileName(romPath)} cores: cpu={nes.GetCpuCoreId()} ppu={nes.GetPpuCoreId()} apu={nes.GetApuCoreId()}");

        var sw = Stopwatch.StartNew();
        int ran = 0;
        for (int f = 0; f < frames; f++)
        {
            nes.SetInput(input.At(f));
            nes.RunFrame();
            ran++;
            Harness.DrainAudio(nes, audio);
            if (pngEvery > 0 && f % pngEvery == 0)
                Media.WritePng(Path.Combine(outdir, $"frame_{f:D5}.png"), nes.GetFrameBuffer());
            if (nes.IsCrashed()) { Console.WriteLine($"CRASH at frame {f}: {nes.GetCrashInfo()}"); break; }
        }
        sw.Stop();

        var fb = nes.GetFrameBuffer();
        var r = nes.GetCpuRegs();
        Console.WriteLine($"frames={ran} time={sw.ElapsedMilliseconds}ms fps={ran / Math.Max(sw.Elapsed.TotalSeconds, 1e-9):F1} ({ran / Math.Max(sw.Elapsed.TotalSeconds, 1e-9) / 60.0:F2}x realtime)");
        Console.WriteLine($"final frame hash={Harness.Hash(fb)}  PC={r.PC:X4} A={r.A:X2} X={r.X:X2} Y={r.Y:X2} P={r.P:X2} SP={r.SP & 0xFF:X2}");
        if (a.Get("png") is { } png) { Media.WritePng(png, fb); Console.WriteLine($"wrote {png}"); }
        if (audio != null)
        {
            var wav = a.Get("wav")!;
            Media.WriteWav(wav, audio, nes.GetAudioSampleRate());
            Console.WriteLine($"wrote {wav} ({audio.Count} samples @ {nes.GetAudioSampleRate()}Hz, rms={Rms(audio):F4})");
        }
        return nes.IsCrashed() ? 1 : 0;
    }

    // ---------------------------------------------------------------- nestest
    static readonly Regex LogLine = new(@"^(?<pc>[0-9A-F]{4}).*A:(?<a>[0-9A-F]{2}) X:(?<x>[0-9A-F]{2}) Y:(?<y>[0-9A-F]{2}) P:(?<p>[0-9A-F]{2}) SP:(?<sp>[0-9A-F]{2})", RegexOptions.Compiled);

    /// Runs nestest.nes in "automation" mode (PC=$C000) on the CPU core alone and compares
    /// PC/A/X/Y/P/SP before every instruction against the reference log. P is compared with
    /// bits 4/5 masked since cores differ in how they keep those unused bits.
    public static int NesTest(Args a)
    {
        if (a.Positional.Count < 2) throw new ArgumentException("nestest: need <nestest.nes> <nestest.log>");
        var lines = File.ReadAllLines(a.Positional[1]).Select(l => LogLine.Match(l)).Where(m => m.Success).ToList();
        int max = Math.Min(a.GetInt("max", lines.Count), lines.Count);
        const int officialOnly = 5003; // lines after this exercise unofficial opcodes

        var bus = new Bus(new Cartridge(File.ReadAllBytes(a.Positional[0])));
        if (a.Get("cpu") is { } id && !bus.SetCpuCoreById(id)) throw new ArgumentException($"unknown CPU core '{id}'");
        var cpu = bus.cpu;
        cpu.IgnoreInvalidOpcodes = true;
        cpu.Reset();
        cpu.AddToPC(0xC000 - cpu.GetRegisters().PC);
        Console.WriteLine($"nestest on {cpu.GetType().Name}: comparing {max} instructions");

        int i = 0;
        string? firstDiff = null;
        for (; i < max; i++)
        {
            var m = lines[i];
            var r = cpu.GetRegisters();
            int ePc = Hex(m, "pc"), eA = Hex(m, "a"), eX = Hex(m, "x"), eY = Hex(m, "y"), eP = Hex(m, "p"), eSp = Hex(m, "sp");
            bool ok = r.PC == ePc && r.A == eA && r.X == eX && r.Y == eY && (r.P & 0xCF) == (eP & 0xCF) && (r.SP & 0xFF) == eSp;
            if (!ok)
            {
                firstDiff = $"line {i + 1}: expected PC={ePc:X4} A={eA:X2} X={eX:X2} Y={eY:X2} P={eP:X2} SP={eSp:X2}\n" +
                            $"         got      PC={r.PC:X4} A={r.A:X2} X={r.X:X2} Y={r.Y:X2} P={r.P:X2} SP={r.SP & 0xFF:X2}\n" +
                            $"  prev:  {(i > 0 ? File.ReadLines(a.Positional[1]).ElementAt(i - 1) : "(start)")}";
                break;
            }
            try { cpu.ExecuteInstruction(); }
            catch (Exception ex) { firstDiff = $"line {i + 1}: exception {ex.GetType().Name}: {ex.Message}"; break; }
        }
        byte r02 = bus.PeekRam(2), r03 = bus.PeekRam(3);
        Console.WriteLine($"matched {i}/{max} instructions (official-opcode section: {(i >= officialOnly ? "PASS" : $"FAIL at {i + 1}")})");
        Console.WriteLine($"result bytes $02={r02:X2} $03={r03:X2} ({(r02 == 0 && r03 == 0 ? "no error codes" : "error codes set, see nestest.txt")})");
        if (firstDiff != null) Console.WriteLine("first divergence " + firstDiff);
        return i >= max ? 0 : (i >= officialOnly ? 0 : 1);
    }

    static int Hex(Match m, string g) => Convert.ToInt32(m.Groups[g].Value, 16);

    // ---------------------------------------------------------------- blargg
    /// blargg's protocol: $6001-$6003 = DE B0 61 once the test is live; $6000 = $80 running,
    /// $81 = wants a reset, <$80 = finished with that result code (0 = pass); text at $6004.
    public static int Blargg(Args a)
    {
        var roms = ExpandRoms(a.Positional);
        if (roms.Count == 0) throw new ArgumentException("blargg: no .nes files given");
        var lo = Loadout.From(a);
        int maxFrames = a.GetInt("max-frames", 3000);
        string? pngDir = a.Get("png-dir");
        int pass = 0, fail = 0, other = 0;
        Console.WriteLine($"loadout: {lo}");
        foreach (var rom in roms)
        {
            string name = Path.GetRelativePath(Directory.GetCurrentDirectory(), rom);
            var (status, text, frames, nes) = RunBlargg(rom, lo, maxFrames);
            string tag = status switch { "pass" => "PASS", "fail" => "FAIL", _ => status.ToUpperInvariant() };
            if (status == "pass") pass++; else if (status == "fail") fail++; else other++;
            var oneLine = Regex.Replace(text, @"\s+", " ").Trim();
            Console.WriteLine($"{tag,-11} {name}  [{frames}f] {Truncate(oneLine, 140)}");
            if (pngDir != null && nes != null)
                Media.WritePng(Path.Combine(pngDir, Path.GetFileNameWithoutExtension(rom) + ".png"), nes.GetFrameBuffer());
        }
        Console.WriteLine($"\n{pass} passed, {fail} failed, {other} other (no-protocol / timeout / unsupported) of {roms.Count}");
        return fail == 0 ? 0 : 1;
    }

    static (string status, string text, int frames, NES? nes) RunBlargg(string rom, Loadout lo, int maxFrames)
    {
        NES nes;
        try { nes = Harness.Boot(File.ReadAllBytes(rom), lo, Path.GetFileName(rom)); }
        catch (Exception ex) { return ("unsupported", ex.Message, 0, null); }
        bool live = false;
        int resetAt = -1;
        for (int f = 0; f < maxFrames; f++)
        {
            try { nes.RunFrame(); } catch (Exception ex) { return ("exception", ex.Message, f, nes); }
            Harness.DrainAudio(nes, null);
            if (nes.IsCrashed()) return ("crash", nes.GetCrashInfo(), f, nes);
            if (!live) live = nes.PeekCpu(0x6001) == 0xDE && nes.PeekCpu(0x6002) == 0xB0 && nes.PeekCpu(0x6003) == 0x61;
            if (!live) continue;
            byte st = nes.PeekCpu(0x6000);
            if (st == 0x81)
            {
                if (resetAt < 0) resetAt = f + 10; // hold "reset button" ~160ms
                else if (f >= resetAt) { SoftReset(nes); resetAt = -1; }
                continue;
            }
            if (st < 0x80) return (st == 0 ? "pass" : "fail", $"(code {st}) " + ReadText(nes), f + 1, nes);
        }
        return (live ? "timeout" : "no-protocol", live ? ReadText(nes) : "(no $6000 signature; check PNG)", maxFrames, nes);
    }

    /// NES exposes no reset entry point; approximate the console reset button by resetting the CPU.
    static void SoftReset(NES nes)
    {
        var busField = typeof(NES).GetField("bus", BindingFlags.NonPublic | BindingFlags.Instance);
        (busField?.GetValue(nes) as Bus)?.cpu.Reset();
    }

    static string ReadText(NES nes)
    {
        var sb = new StringBuilder();
        for (ushort addr = 0x6004; addr < 0x7000; addr++)
        {
            byte b = nes.PeekCpu(addr);
            if (b == 0) break;
            sb.Append((char)b);
        }
        return sb.ToString();
    }

    static List<string> ExpandRoms(IEnumerable<string> paths)
    {
        var list = new List<string>();
        foreach (var p in paths)
        {
            if (Directory.Exists(p)) list.AddRange(Directory.GetFiles(p, "*.nes", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal));
            else if (File.Exists(p)) list.Add(p);
            else throw new FileNotFoundException(p);
        }
        return list;
    }

    // ---------------------------------------------------------------- compare
    public static int Compare(Args a)
    {
        var romPath = a.Positional.FirstOrDefault() ?? throw new ArgumentException("compare: missing <rom>");
        var la = Loadout.Parse(a.Get("a") ?? throw new ArgumentException("compare: need --a"));
        var lb = Loadout.Parse(a.Get("b") ?? throw new ArgumentException("compare: need --b"));
        int frames = a.GetInt("frames", 600);
        string outdir = a.Get("outdir") ?? "out/compare";
        var input = new InputScript(a.Get("input"));
        var rom = File.ReadAllBytes(romPath);
        var na = Harness.Boot(rom, la); var nb = Harness.Boot(rom, lb);
        Console.WriteLine($"A: {na.GetCpuCoreId()}/{na.GetPpuCoreId()}/{na.GetApuCoreId()}   B: {nb.GetCpuCoreId()}/{nb.GetPpuCoreId()}/{nb.GetApuCoreId()}");
        var audA = new List<float>(); var audB = new List<float>();
        int firstDiv = -1, diffFrames = 0;
        double worstPct = 0;
        long tA = 0, tB = 0;
        var sw = new Stopwatch();
        for (int f = 0; f < frames; f++)
        {
            var inp = input.At(f);
            na.SetInput(inp); nb.SetInput(inp);
            sw.Restart(); na.RunFrame(); tA += sw.ElapsedTicks;
            sw.Restart(); nb.RunFrame(); tB += sw.ElapsedTicks;
            Harness.DrainAudio(na, audA); Harness.DrainAudio(nb, audB);
            var fa = na.GetFrameBuffer(); var fbb = nb.GetFrameBuffer();
            int diffPx = 0;
            for (int i = 0; i + 2 < Math.Min(fa.Length, fbb.Length); i += 4)
                if (fa[i] != fbb[i] || fa[i + 1] != fbb[i + 1] || fa[i + 2] != fbb[i + 2]) diffPx++;
            if (diffPx > 0)
            {
                diffFrames++;
                double pct = 100.0 * diffPx / (256 * 240);
                worstPct = Math.Max(worstPct, pct);
                if (firstDiv < 0)
                {
                    firstDiv = f;
                    Media.WritePng(Path.Combine(outdir, "first_div_a.png"), fa);
                    Media.WritePng(Path.Combine(outdir, "first_div_b.png"), fbb);
                    Media.WritePng(Path.Combine(outdir, "first_div_diff.png"), DiffImage(fa, fbb));
                    Console.WriteLine($"first visual divergence at frame {f}: {diffPx} px ({pct:F2}%)");
                }
            }
            if (na.IsCrashed() || nb.IsCrashed()) { Console.WriteLine($"crash at frame {f}: A={na.IsCrashed()} B={nb.IsCrashed()} {na.GetCrashInfo()}{nb.GetCrashInfo()}"); frames = f + 1; break; }
        }
        Media.WritePng(Path.Combine(outdir, "final_a.png"), na.GetFrameBuffer());
        Media.WritePng(Path.Combine(outdir, "final_b.png"), nb.GetFrameBuffer());
        double fpsA = frames / (tA / (double)Stopwatch.Frequency), fpsB = frames / (tB / (double)Stopwatch.Frequency);
        Console.WriteLine($"frames={frames} differing={diffFrames} worst={worstPct:F2}%  speed A={fpsA:F0}fps B={fpsB:F0}fps");
        int n = Math.Min(audA.Count, audB.Count);
        double se = 0; for (int i = 0; i < n; i++) { double d = audA[i] - audB[i]; se += d * d; }
        Console.WriteLine($"audio: A {audA.Count} samples rms={Rms(audA):F4}; B {audB.Count} samples rms={Rms(audB):F4}; diff rms={(n > 0 ? Math.Sqrt(se / n) : 0):F4}");
        if (a.Has("wav"))
        {
            Media.WriteWav(Path.Combine(outdir, "a.wav"), audA, na.GetAudioSampleRate());
            Media.WriteWav(Path.Combine(outdir, "b.wav"), audB, nb.GetAudioSampleRate());
        }
        Console.WriteLine($"images in {outdir}/");
        return 0;
    }

    static byte[] DiffImage(byte[] a, byte[] b)
    {
        var d = new byte[256 * 240 * 4];
        for (int i = 0; i + 3 < d.Length && i + 2 < a.Length && i + 2 < b.Length; i += 4)
        {
            bool diff = a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2];
            byte g = (byte)((a[i] + a[i + 1] + a[i + 2]) / 12); // dim greyscale of A
            d[i] = diff ? (byte)255 : g; d[i + 1] = diff ? (byte)0 : g; d[i + 2] = diff ? (byte)255 : g; d[i + 3] = 255;
        }
        return d;
    }

    // ---------------------------------------------------------------- bench
    public static int Bench(Args a)
    {
        var romPath = a.Positional.FirstOrDefault() ?? throw new ArgumentException("bench: missing <rom>");
        var kind = (a.Get("kind") ?? "cpu").ToLowerInvariant();
        int frames = a.GetInt("frames", 600);
        var rom = File.ReadAllBytes(romPath);
        var baseLo = Loadout.From(a);
        var ids = kind switch
        {
            "cpu" => CoreRegistry.CpuIds, "ppu" => CoreRegistry.PpuIds, "apu" => CoreRegistry.ApuIds,
            _ => throw new ArgumentException("--kind must be cpu|ppu|apu")
        };
        Console.WriteLine($"{"core",-10} {"fps",8} {"x-rt",6}  {"final-hash",-12} notes");
        foreach (var id in ids)
        {
            var lo = kind switch { "cpu" => baseLo with { Cpu = id }, "ppu" => baseLo with { Ppu = id }, _ => baseLo with { Apu = id } };
            string notes = "";
            try
            {
                var nes = Harness.Boot(rom, lo);
                for (int f = 0; f < 30; f++) { nes.RunFrame(); Harness.DrainAudio(nes, null); } // warm-up / JIT
                var sw = Stopwatch.StartNew();
                int ran = 0;
                for (; ran < frames; ran++)
                {
                    nes.RunFrame(); Harness.DrainAudio(nes, null);
                    if (nes.IsCrashed()) { notes = "CRASH: " + nes.GetCrashInfo(); break; }
                }
                double fps = ran / sw.Elapsed.TotalSeconds;
                Console.WriteLine($"{id,-10} {fps,8:F0} {fps / 60,6:F1}  {Harness.Hash(nes.GetFrameBuffer()),-12} {notes}");
            }
            catch (Exception ex) { Console.WriteLine($"{id,-10} {"-",8} {"-",6}  {"-",-12} {ex.GetType().Name}: {Truncate(ex.Message, 80)}"); }
        }
        return 0;
    }

    static double Rms(List<float> s) => s.Count == 0 ? 0 : Math.Sqrt(s.Sum(x => (double)x * x) / s.Count);
    static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
