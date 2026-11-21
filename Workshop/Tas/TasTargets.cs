// TAS workline T1 (docs/projects/tas-format.md): the machines a TAS runs on. One ITasTarget per console family; the runner,
// the log and the checkpoints know only this interface. T7 (other consoles) = a target (or the generic SessionTarget, which already
// drives every IConsoleSession through the console-neutral pad) + a button map + memory addressing.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BrokenNes.Workshop.Debug;
using BrokenNes.Workshop.Tas.Script;
using NesEmulator;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Tas;

public interface ITasTarget : IDisposable
{
    string Console { get; }
    TasButtonMap Map { get; }
    double Fps { get; }
    string Title { get; }
    /// <summary>SHA-1 of the ROM, lower-case hex.</summary>
    string RomSha1 { get; }
    string Cores { get; }
    void SetPads(TasPad[] pads);
    void RunFrame();
    /// <summary>The last frame, 0xAARRGGBB, Width x Height (the frame dump hook and the PNGs read this).</summary>
    uint[] Argb { get; }
    int Width { get; }
    int Height { get; }
    /// <summary>The console's own picture hash (NES: the .bnm keyframe hash; others: FNV-1a over Argb).</summary>
    ulong PictureHash();
    ulong RamHash();
    int SampleRate { get; }
    /// <summary>Stereo interleaved samples since the last call (a mono core duplicates).</summary>
    int ReadSamples(short[] buffer);
    byte[] SaveState();
    void LoadState(byte[] state);
    void Reset();
    void Poke(string addr, byte[] bytes);
    byte[] Peek(string addr, int n);
    byte[] ExportSave();
    /// <summary>Runs a console-only op (N64: pak, prenmi, reload, clearsave, dumpram). False: not this console's.</summary>
    bool ConsoleOp(string op, string[] args, string outDir);
}

public sealed class TasOpenOptions
{
    public string Cores = "";         // CPU/PPU/APU ids ("" = the console's defaults)
    public string PowerOn = "fceux";  // NES power-on RAM
    public bool Ntsc;                 // NES exact NTSC frame rate
    public string? N64Renderer;       // software | parallel | null (auto, as SessionProbe)
    public string? PakDir;            // N64 Controller Pak images
}

public static class TasTargets
{
    /// <summary>Console ops a console's scripts may use (checked at expansion, before anything runs).</summary>
    public static Func<string, bool> ConsoleOps(string console) => console == "n64" ? N64Target.IsOp : _ => false;

    public static string DetectConsole(string romPath)
    {
        string ext = Path.GetExtension(romPath).ToLowerInvariant();
        if (ext is ".z64" or ".n64" or ".v64") return "n64";
        byte[] file = File.ReadAllBytes(romPath);
        byte[] rom = RomDetect.Unwrap(file, romPath, out string inner);
        var c = RomDetect.Detect(rom, inner) ?? throw new InvalidDataException($"{Path.GetFileName(romPath)}: not a ROM BrokenNes recognises");
        return c == ConsoleKind.Nes ? "nes" : Consoles.Key(c);
    }

    public static ITasTarget Open(string console, string romPath, TasOpenOptions o)
    {
        if (!File.Exists(romPath)) throw new FileNotFoundException($"no ROM at {romPath}");
        return console switch
        {
            "nes" => NesTarget.Open(romPath, o),
            "n64" => new N64Target(romPath, o),
            _ => SessionTarget.Open(console, romPath, o),
        };
    }

    internal static ulong Fnv(uint[] px, int n, ulong h = 1469598103934665603UL) { for (int i = 0; i < n; i++) { h ^= px[i]; h *= 1099511628211UL; } return h; }
    internal static string Sha1(byte[] b) => Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(b)).ToLowerInvariant();
    internal static int Addr(string t) => int.Parse(t.TrimStart('$').Replace("0x", "").Replace("0X", ""), NumberStyles.HexNumber);
}

/// <summary>The NES through the same DebugHost the movie player uses (no debugger: frame-identical to it), so .bnm keyframes match.</summary>
internal sealed class NesTarget : ITasTarget
{
    private readonly DebugHost h; private readonly NES nes; private readonly uint[] argb = new uint[256 * 240];
    public string Console => "nes"; public TasButtonMap Map => TasButtonMap.Nes; public double Fps { get; }
    public string Title { get; } public string RomSha1 { get; } public string Cores => h.Cores;
    private NesTarget(DebugHost host, string title, string sha1, bool ntsc) { h = host; nes = host.Nes!; Title = title; RomSha1 = sha1; Fps = ntsc ? 60.0988 : 60; }

    public static NesTarget Open(string romPath, TasOpenOptions o)
    {
        byte[] file = File.ReadAllBytes(romPath);
        byte[] rom = RomDetect.Unwrap(file, romPath, out string inner);
        string cpu = "", ppu = "", apu = "";
        if (o.Cores.Length > 0) { var p = o.Cores.Split('/'); if (p.Length != 3) throw new ArgumentException("cores: CPU/PPU/APU"); cpu = p[0]; ppu = p[1]; apu = p[2]; }
        var host = DebugHost.Open(rom, inner, ConsoleKind.Nes, false, cpu, ppu, apu, o.PowerOn, o.Ntsc);
        return new NesTarget(host, Path.GetFileNameWithoutExtension(romPath), TasTargets.Sha1(file), o.Ntsc);
    }

    public void SetPads(TasPad[] p) => nes.SetInputs(Bits(p[0].Buttons), p.Length > 1 ? Bits(p[1].Buttons) : Bits(0));
    // TasButtonMap.Nes order = A B Select Start Up Down Left Right = the NES's SetInputs order
    private static bool[] Bits(ulong b) { var r = new bool[8]; for (int i = 0; i < 8; i++) r[i] = (b & (1UL << i)) != 0; return r; }
    public void RunFrame() => h.FrameOnce();
    public uint[] Argb
    {
        get
        {
            var fb = nes.GetFrameBuffer();
            for (int i = 0; i < argb.Length && i * 4 + 3 < fb.Length; i++) argb[i] = 0xFF000000u | (uint)fb[i * 4] << 16 | (uint)fb[i * 4 + 1] << 8 | fb[i * 4 + 2];
            return argb;
        }
    }
    public int Width => 256; public int Height => 240;
    public ulong PictureHash() => h.MoviePicture();
    public ulong RamHash() => h.MovieRam();
    public int SampleRate => nes.GetAudioSampleRate();
    public int ReadSamples(short[] buf)
    {
        var bus = nes.DebugBus; if (bus == null) return 0;
        int q = Math.Min(bus.GetQueuedSamples(), buf.Length / 2); if (q <= 0) return 0;
        var f = bus.GetAudioSamples(q);
        for (int i = 0; i < f.Length; i++) { short v = (short)Math.Clamp(f[i] * 32767f, -32768f, 32767f); buf[2 * i] = v; buf[2 * i + 1] = v; }
        return f.Length * 2;
    }
    public byte[] SaveState() => h.SaveState();
    public void LoadState(byte[] s) => h.LoadState(s);
    public void Reset() => nes.Reset();
    public void Poke(string addr, byte[] b) { int a = TasTargets.Addr(addr); if (a >= 0x2000) throw new ArgumentException($"NES poke {addr}: only CPU RAM $0000-$1FFF"); for (int i = 0; i < b.Length; i++) nes.PokeSystemRam((a + i) & 0x7FF, b[i]); }
    public byte[] Peek(string addr, int n) { int a = TasTargets.Addr(addr); if (a >= 0x2000) throw new ArgumentException($"NES peek {addr}: only CPU RAM $0000-$1FFF"); var r = new byte[n]; for (int i = 0; i < n; i++) r[i] = nes.PeekSystemRam((a + i) & 0x7FF); return r; }
    public byte[] ExportSave() => nes.ExportMapperBatteryMemory() ?? Array.Empty<byte>();
    public bool ConsoleOp(string op, string[] a, string dir) => false;
    public void Dispose() => h.Dispose();
}

/// <summary>Any IConsoleSession (SNES, WideMess, GB/GBC, SMS/GG/MD...) through the console-neutral pad; memory = the session's first work-RAM domain. T7 hardens these.</summary>
internal sealed class SessionTarget : ITasTarget
{
    private readonly DebugHost h; private readonly IConsoleSession s;
    public string Console { get; } public TasButtonMap Map { get; } public double Fps => s.FramesPerSecond;
    public string Title => s.Title; public string RomSha1 { get; } public string Cores => h.Cores;
    private SessionTarget(string c, DebugHost host, string sha) { Console = c; h = host; s = host.Session!; Map = TasButtonMap.Generic(c); RomSha1 = sha; }
    public static SessionTarget Open(string console, string romPath, TasOpenOptions o)
    {
        byte[] file = File.ReadAllBytes(romPath);
        byte[] rom = RomDetect.Unwrap(file, romPath, out string inner);
        var c = Consoles.FromKey(console);
        if (c == ConsoleKind.WideMess) Consoles.WideMessEnabled = true;
        string cpu = "", ppu = "", apu = "";
        if (o.Cores.Length > 0) { var p = o.Cores.Split('/'); cpu = p[0]; ppu = p[1]; apu = p[2]; }
        return new SessionTarget(console, DebugHost.Open(rom, inner, c, false, cpu, ppu, apu), TasTargets.Sha1(file));
    }
    private PadButtons Pad(ulong b) { PadButtons p = 0; for (int i = 0; i < Map.Buttons.Count; i++) if ((b & (1UL << i)) != 0) p |= Enum.Parse<PadButtons>(Map.Buttons[i]); return p; }
    public void SetPads(TasPad[] p) => h.SetPads(Pad(p[0].Buttons), p.Length > 1 ? Pad(p[1].Buttons) : 0);
    public void RunFrame() => h.FrameOnce();
    public uint[] Argb => s.Frame; public int Width => s.FrameWidth; public int Height => s.FrameHeight;
    public ulong PictureHash() => TasTargets.Fnv(s.Frame, s.FrameWidth * s.FrameHeight);
    public ulong RamHash() => h.MovieRam();
    public int SampleRate => s.SampleRate;
    public int ReadSamples(short[] b) => s.ReadSamples(b);
    public byte[] SaveState() => h.SaveState();
    public void LoadState(byte[] st) => h.LoadState(st);
    public void Reset() => s.Reset();
    private NesEmulator.Corruption.IMemoryDomain Ram() => h.Domains().FirstOrDefault(d => d.Kind == NesEmulator.Corruption.DomainKind.Work && d.Unavailable == null) ?? throw new NotSupportedException($"the {Console} session has no work RAM domain");
    public void Poke(string addr, byte[] b) { var d = Ram(); int a = TasTargets.Addr(addr); for (int i = 0; i < b.Length; i++) d.Poke((a + i) % d.Size, b[i]); }
    public byte[] Peek(string addr, int n) { var d = Ram(); int a = TasTargets.Addr(addr); var r = new byte[n]; for (int i = 0; i < n; i++) r[i] = d.Peek((a + i) % d.Size); return r; }
    public byte[] ExportSave() => s.HasBattery ? s.ExportSave() : Array.Empty<byte>();
    public bool ConsoleOp(string op, string[] a, string dir) => false;
    public void Dispose() => h.Dispose();
}
