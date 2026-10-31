using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BrokenNes.Workshop.Debug;
using NesEmulator;
using NesEmulator.Debugging;
using NesEmulator.Debugging.Movies;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Movies;

/// <summary>
/// A machine that plays or records a <see cref="Movie"/> (milestone D5): a ROM on its console with named cores, the NES's power-on RAM and
/// frame timing, two pads, the keyframe hashes, save states, and the D1 debugger (no hook armed unless something asks: a movie played
/// with it is frame-identical to one played without it) for the course variables, the symbols and the traces of the divergence finder.
/// </summary>
internal sealed class MovieMachine : IDisposable
{
    public DebugHost Host { get; }
    public string ConsoleKey { get; }
    public string RomPath { get; }
    public string RomName { get; }
    public byte[] RomFile { get; }
    public string RomSha256 { get; }
    public bool PowerOnNtsc { get; }
    public string PowerOn { get; }
    public string Cores => Host.Cores;
    public bool HasDebugger => Host.Dbg != null;
    public long FramesRun { get; private set; }

    private MovieMachine(DebugHost h, string key, string path, byte[] file, string powerOn, bool ntsc)
    {
        Host = h; ConsoleKey = key; RomPath = Path.GetFullPath(path); RomName = Path.GetFileName(path); RomFile = file;
        RomSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file)).ToLowerInvariant();
        PowerOn = powerOn; PowerOnNtsc = ntsc;
    }

    /// <summary>The console keys a movie may name.</summary>
    public static readonly string[] ConsoleKeys = { "nes", "snes", "wms", "gb", "gbc", "sms", "gg", "md" };

    public static ConsoleKind ParseConsole(string key)
    {
        string k = key.Trim().ToLowerInvariant();
        if (!ConsoleKeys.Contains(k)) throw new ArgumentException($"unknown console '{key}' ({string.Join(", ", ConsoleKeys)})");
        return k == "nes" ? ConsoleKind.Nes : Consoles.FromKey(k);
    }

    /// <summary>
    /// Opens a ROM. <paramref name="cores"/>: "CPU/PPU/APU" ids, or "" for the console's defaults (the NES: FIX/FIX/FIX, as the desktop app);
    /// a core the console's menu does not offer is refused (never a quiet substitute). <paramref name="debugger"/>: attach the D1 debugger
    /// (every console: NES, SNES, WideMess, Game Boy, and since Timeflow T0 the Master System, Game Gear and Genesis); its hooks stay off until something arms them.
    /// </summary>
    public static MovieMachine Open(string romPath, string? consoleKey, string cores, string powerOn, bool ntsc, bool debugger, IEnumerable<string>? symbols = null)
    {
        if (!File.Exists(romPath)) throw new FileNotFoundException($"no ROM at {romPath}");
        byte[] file = File.ReadAllBytes(romPath);
        byte[] rom = RomDetect.Unwrap(file, romPath, out string inner);
        ConsoleKind c = !string.IsNullOrWhiteSpace(consoleKey) ? ParseConsole(consoleKey!) : RomDetect.Detect(rom, inner) ?? throw new InvalidDataException($"{Path.GetFileName(romPath)}: not a ROM BrokenNes recognises (give the console)");
        if (c == ConsoleKind.WideMess) Consoles.WideMessEnabled = true;
        string cpu = "", ppu = "", apu = "";
        if (!string.IsNullOrWhiteSpace(cores))
        {
            var p = cores.Split('/');
            if (p.Length != 3) throw new ArgumentException($"cores '{cores}': give CPU/PPU/APU ids (for example FIX/FIX/FIX)");
            cpu = p[0].Trim(); ppu = p[1].Trim(); apu = p[2].Trim();
            if (c != ConsoleKind.Nes)
                foreach (var (slot, id) in new[] { (CoreSlot.Cpu, cpu), (CoreSlot.Ppu, ppu), (CoreSlot.Apu, apu) })
                    if (CoreCatalog.Find(c, slot, id) == null) throw new ArgumentException($"the {Consoles.DisplayName(c)} {slot} menu does not offer {id}");
        }
        bool canDebug = c is ConsoleKind.Nes or ConsoleKind.Snes or ConsoleKind.WideMess or ConsoleKind.GameBoy or ConsoleKind.GameBoyColor or ConsoleKind.MasterSystem or ConsoleKind.GameGear or ConsoleKind.Genesis;
        DebugHost h;
        try { h = DebugHost.Open(rom, inner, c, debugger && canDebug, cpu, ppu, apu, powerOn, ntsc); }
        catch (InvalidOperationException e) { throw new ArgumentException($"cores '{cores}' did not apply: {e.Message}"); }
        if (c == ConsoleKind.Nes && cpu.Length > 0)
        {
            string want = $"{cpu}/{ppu}/{apu}";
            if (!h.Cores.Equals(want, StringComparison.OrdinalIgnoreCase)) { h.Dispose(); throw new ArgumentException($"the NES cores {want} did not apply (running {h.Cores})"); }
        }
        var m = new MovieMachine(h, c == ConsoleKind.Nes ? "nes" : Consoles.Key(c), romPath, file, powerOn, ntsc);
        if (symbols != null && h.Dbg != null) foreach (var s in symbols) h.Dbg.Symbols.Load(s, h.Machine.Console);
        else if (symbols != null && symbols.Any()) { m.Dispose(); throw new ArgumentException($"symbols need the debugger, which does not cover the {Consoles.DisplayName(c)}"); }
        if (h.Dbg != null && h.Machine is not (SmsDebugMachine or MdDebugMachine))
        {
            // the debug port (D7) listens (the NES, SNES / WideMess and Game Boy: the Sega consoles have none): its markers, at the movie frame they happen in, go into a recording and a replay's report
            m.Port = new NesEmulator.Debugging.Port.DebugPort(h.Machine, h.Dbg);
            m.Port.Event += e => { if (e.Kind == NesEmulator.Debugging.Port.PortEventKind.Marker) m.MarksSeen.Add(new MovieMark(m.FramesRun, e.Text)); };
            m.Port.Attach();
        }
        return m;
    }

    /// <summary>Opens the machine a movie names: its console, cores, power-on and timing (a ROM of another build is allowed: bisect).</summary>
    public static MovieMachine OpenFor(Movie mv, string romPath, bool debugger, IEnumerable<string>? symbols = null, string? coresOverride = null, bool? ntscOverride = null)
    {
        var m = Open(romPath, mv.Console, coresOverride ?? mv.Cores, mv.Console == "nes" ? mv.PowerOn : "fceux", ntscOverride ?? mv.NtscTiming, debugger, symbols);
        if (mv.Start == MovieStart.State)
        {
            try { m.Host.LoadState(mv.StartState!); }
            catch (Exception e) { m.Dispose(); throw new InvalidDataException($"the movie's start state does not load on this ROM / these cores: {e.Message}"); }
        }
        return m;
    }

    public static string EmulatorBuild()
    {
        var a = typeof(MovieMachine).Assembly;
        string v = a.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? a.GetName().Version?.ToString() ?? "?";
        return $"BrokenNes Workshop {v}";
    }

    /// <summary>One frame with these pads (on this thread; the debugger's hooks, if armed, see it).</summary>
    public void Frame(PadButtons p1, PadButtons p2)
    {
        Host.SetPads(p1, p2);
        Host.FrameOnce();
        FramesRun++;
    }

    /// <summary>The debug port on this machine (null without the debugger) and the markers it heard, by movie frame.</summary>
    public NesEmulator.Debugging.Port.DebugPort? Port { get; private set; }
    public readonly List<MovieMark> MarksSeen = new();

    public ulong Picture() => Host.MoviePicture();
    public ulong Ram() => Host.MovieRam();
    public bool Crashed => Host.Crashed;
    public string CrashInfo => Host.CrashInfo;

    public long[] EvalVars(Movie mv)
    {
        if (mv.Vars.Count == 0) return Array.Empty<long>();
        var d = Host.Dbg ?? throw new InvalidOperationException("course variables need the debugger");
        return mv.Vars.Select(v => d.Evaluate(v.Expr)).ToArray();
    }

    public void Dispose() { Port?.Detach(); Host.Dispose(); }
}
