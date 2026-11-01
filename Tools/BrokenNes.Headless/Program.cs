// BrokenNes.Headless — cross-platform (Linux/macOS/Windows) command-line harness for
// developing and testing emulator cores without the WinForms/DirectX/WebView2 host.
//
// It compiles the same sources as the Windows host (Windows/NesEmulator/**) so any new
// CPU_*/PPU_*/APU_* class dropped into that folder is picked up here automatically.
//
// Commands (run with no args for help):
//   cores                                  list discovered CPU/PPU/APU core ids
//   run <rom> [opts]                       run N frames, dump PNG/WAV, print frame hash + speed
//   nestest <nestest.nes> <nestest.log>    instruction-by-instruction CPU trace check
//   blargg <rom|dir>... [opts]             run blargg-protocol test ROMs ($6000 status), report pass/fail
//   compare <rom> --a k=v,.. --b k=v,..    run two core loadouts side by side, report first divergence
//   bench <rom> [opts]                     frames/sec for each core of a kind (--kind cpu|ppu|apu)
//
// Common options: --cpu ID --ppu ID --apu ID --frames N --input "F:BTN[+BTN],F2:..."
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using NesEmulator;

namespace BrokenNes.Headless;

public static class Program
{
    public static int Main(string[] argv)
    {
        if (argv.Length == 0 || argv[0] is "-h" or "--help" or "help") { PrintHelp(); return 0; }
        var cmd = argv[0];
        var a = new Args(argv.Skip(1).ToArray());
        try
        {
            return cmd switch
            {
                "cores" => Commands.Cores(),
                "run" => Commands.Run(a),
                "nestest" => Commands.NesTest(a),
                "blargg" => Commands.Blargg(a),
                "compare" => Commands.Compare(a),
                "bench" => Commands.Bench(a),
                _ => Fail($"Unknown command '{cmd}'")
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            if (a.Has("verbose")) Console.Error.WriteLine(ex);
            return 2;
        }
    }

    static int Fail(string msg) { Console.Error.WriteLine(msg); PrintHelp(); return 2; }

    static void PrintHelp()
    {
        Console.WriteLine("""
BrokenNes.Headless — headless core dev harness

  cores
  run <rom> [--frames 300] [--cpu ID] [--ppu ID] [--apu ID]
            [--png out.png] [--png-every N --outdir dir] [--wav out.wav] [--input "60:START,120:A+RIGHT"]
  nestest <nestest.nes> <nestest.log> [--cpu ID] [--max 9000]
  blargg <rom|dir>... [--cpu ID] [--ppu ID] [--apu ID] [--max-frames 3000] [--png-dir dir]
  compare <rom> --a cpu=FMC,ppu=FMC,apu=FMC --b cpu=XYZ [--frames 600] [--outdir dir]
  bench <rom> [--kind cpu|ppu|apu] [--frames 600] [--cpu ID] [--ppu ID] [--apu ID]

Buttons: A B SELECT START UP DOWN LEFT RIGHT. Input spec 'F:BTN' holds buttons for frame F only;
'F-G:BTN' holds them for frames F..G inclusive.
""");
    }
}

/// Minimal --key value / --flag parser; positional args kept in order.
public sealed class Args
{
    public readonly List<string> Positional = new();
    readonly Dictionary<string, string> _opts = new(StringComparer.OrdinalIgnoreCase);
    public Args(string[] a)
    {
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i].StartsWith("--"))
            {
                var k = a[i][2..];
                if (i + 1 < a.Length && !a[i + 1].StartsWith("--")) _opts[k] = a[++i];
                else _opts[k] = "true";
            }
            else Positional.Add(a[i]);
        }
    }
    public bool Has(string k) => _opts.ContainsKey(k);
    public string? Get(string k) => _opts.TryGetValue(k, out var v) ? v : null;
    public int GetInt(string k, int d) => _opts.TryGetValue(k, out var v) && int.TryParse(v, out var n) ? n : d;
}

/// A CPU/PPU/APU selection. Null entries leave the emulator default.
public sealed record Loadout(string? Cpu, string? Ppu, string? Apu)
{
    public static Loadout From(Args a) => new(a.Get("cpu"), a.Get("ppu"), a.Get("apu"));
    public static Loadout Parse(string spec)
    {
        string? c = null, p = null, ap = null;
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) throw new ArgumentException($"bad loadout part '{part}' (want cpu=ID)");
            switch (kv[0].Trim().ToLowerInvariant())
            {
                case "cpu": c = kv[1].Trim(); break;
                case "ppu": p = kv[1].Trim(); break;
                case "apu": ap = kv[1].Trim(); break;
                default: throw new ArgumentException($"unknown loadout key '{kv[0]}'");
            }
        }
        return new(c, p, ap);
    }
    public override string ToString() => $"cpu={Cpu ?? "default"} ppu={Ppu ?? "default"} apu={Apu ?? "default"}";
}

public static class Harness
{
    /// Create an NES, load the ROM and apply the requested cores. Throws if a core id is unknown.
    public static NES Boot(byte[] rom, Loadout lo, string romName = "(headless)")
    {
        var nes = new NES { RomName = romName };
        nes.LoadROM(rom);
        if (nes.IsCrashed()) throw new InvalidOperationException($"ROM failed to load: {nes.GetCrashInfo()}");
        if (lo.Cpu != null && !nes.SetCpuCore(lo.Cpu)) throw new ArgumentException($"unknown CPU core '{lo.Cpu}' (have: {string.Join(",", nes.GetCpuCoreIds())})");
        if (lo.Ppu != null && !nes.SetPpuCore(lo.Ppu)) throw new ArgumentException($"unknown PPU core '{lo.Ppu}' (have: {string.Join(",", nes.GetPpuCoreIds())})");
        if (lo.Apu != null && !nes.SetApuCore(lo.Apu)) throw new ArgumentException($"unknown APU core '{lo.Apu}' (have: {string.Join(",", nes.GetApuCoreIds())})");
        return nes;
    }

    public static string Hash(byte[] data) => Convert.ToHexString(SHA1.HashData(data))[..12].ToLowerInvariant();

    /// Drain all queued audio (bypasses NES.GetAudioBuffer's soft-cap so nothing is dropped).
    public static void DrainAudio(NES nes, List<float>? sink)
    {
        while (nes.GetQueuedAudioSamples() > 0)
        {
            var chunk = nes.GetAudioBuffer();
            if (chunk.Length == 0) break;
            sink?.AddRange(chunk);
            if (chunk.Length < 2048) break;
        }
    }
}

/// Parses "60:START,120-130:A+RIGHT" into per-frame button arrays.
public sealed class InputScript
{
    readonly List<(int from, int to, bool[] buttons)> _events = new();
    static readonly string[] Names = { "A", "B", "SELECT", "START", "UP", "DOWN", "LEFT", "RIGHT" };
    public InputScript(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return;
        foreach (var ev in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = ev.Split(':', 2);
            var range = p[0].Split('-', 2);
            int from = int.Parse(range[0]), to = range.Length > 1 ? int.Parse(range[1]) : from;
            var b = new bool[8];
            foreach (var name in p[1].Split('+'))
            {
                int idx = Array.IndexOf(Names, name.Trim().ToUpperInvariant());
                if (idx < 0) throw new ArgumentException($"unknown button '{name}'");
                b[idx] = true;
            }
            _events.Add((from, to, b));
        }
    }
    public bool[] At(int frame)
    {
        var b = new bool[8];
        foreach (var (f, t, bs) in _events)
            if (frame >= f && frame <= t) for (int i = 0; i < 8; i++) b[i] |= bs[i];
        return b;
    }
}
