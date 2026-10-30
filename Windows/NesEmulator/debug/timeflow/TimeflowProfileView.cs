using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using NesEmulator.Debugging.Port;
using NesEmulator.Systems;

namespace NesEmulator.Debugging.Timeflow;

/// <summary>
/// A machine of a timeline's own ROM, cores and power-on, opened by the engine (the desktop app's Profile selection re-runs a span on one of
/// these, never on the game the user is playing; the profile viewers name symbols with one). Workshop and the MCP server open theirs with the
/// Workshop's movie machine (the same cores, the same checks).
/// </summary>
public sealed class ProfileMachine : IDisposable
{
    public NES? Nes { get; private set; }
    public IConsoleSession? Session { get; private set; }
    public DebugMachine Machine { get; private set; } = null!;
    public Debugger Debugger { get; private set; } = null!;
    public TimeflowSource Source { get; private set; } = null!;
    public DebugPort? Port { get; private set; }
    public TimelineMeta Meta { get; private set; } = null!;

    /// <summary>The re-run's target: this machine, just powered on.</summary>
    public ProfileTarget Target => new() { Machine = Machine, Debugger = Debugger, Source = Source, RunFrame = RunFrame, Port = Port, PoweredOn = !ran };
    private bool ran;

    private void RunFrame(PadButtons p1, PadButtons p2)
    {
        ran = true;
        if (Nes != null) { Nes.SetInputs(NesPad(p1), NesPad(p2)); Nes.RunFrame(); return; }
        Session!.SetPad(0, p1); Session.SetPad(1, p2); Session.RunFrame();
    }

    private static bool[] NesPad(PadButtons b) => new[]
    {
        (b & PadButtons.A) != 0, (b & PadButtons.B) != 0, (b & PadButtons.Select) != 0, (b & PadButtons.Start) != 0,
        (b & PadButtons.Up) != 0, (b & PadButtons.Down) != 0, (b & PadButtons.Left) != 0, (b & PadButtons.Right) != 0,
    };

    /// <summary>Opens the machine a timeline was recorded on, with the debugger and these symbols (the Sega consoles are refused until T0).</summary>
    public static ProfileMachine Open(string timelineDir, IEnumerable<string>? symbols = null)
    {
        var meta = TimelineMeta.Load(timelineDir);
        if (meta.Console is "sms" or "gg" or "md")
            throw new NotSupportedException($"profiling the {meta.Console} needs the debugger on its Z80 / 68000, which is milestone T0 (not built here yet): no profile is made");
        byte[] file = TimeflowStore.RomOf(timelineDir, meta);
        byte[] rom = RomDetect.Unwrap(file, meta.RomName.Length > 0 ? meta.RomName : "rom" + meta.RomExt, out string inner);
        var pm = new ProfileMachine { Meta = meta };
        var cores = meta.Cores.Split('/');
        string Core(int i) => i < cores.Length ? cores[i].Trim() : "";
        if (meta.Console == "nes")
        {
            var nes = new NES { RomName = Path.GetFileName(inner) };
            nes.LoadROM(rom);
            if (!nes.SetCpuCore(Core(0).Length > 0 ? Core(0) : "FIX") | !nes.SetPpuCore(Core(1).Length > 0 ? Core(1) : "FIX") | !nes.SetApuCore(Core(2).Length > 0 ? Core(2) : "FIX"))
                throw new InvalidOperationException($"the cores {meta.Cores} of timeline {meta.Id} did not apply");
            if (meta.Ntsc) { var speed = nes.GetSpeedConfig() ?? throw new InvalidOperationException("this NES has no speed config for NTSC frame timing"); speed.NtscAccurateFrameRate = true; }
            switch (meta.PowerOn)
            {
                case "fceux" or "": break;
                case "zeros": for (int i = 0; i < 2048; i++) nes.PokeSystemRam(i, 0x00); break;
                case "ones": for (int i = 0; i < 2048; i++) nes.PokeSystemRam(i, 0xFF); break;
                default: throw new ArgumentException($"timeline {meta.Id}: power-on RAM '{meta.PowerOn}' is not fceux, zeros or ones");
            }
            pm.Nes = nes;
            pm.Machine = DebugMachines.For(nes);
            pm.Source = TimeflowSource.For(nes);
        }
        else
        {
            var c = Consoles.FromKey(meta.Console);
            if (c == ConsoleKind.WideMess) Consoles.WideMessEnabled = true;
            string ci = CoreCatalog.Resolve(c, CoreSlot.Cpu, Core(0)), pi = CoreCatalog.Resolve(c, CoreSlot.Ppu, Core(1)), ai = CoreCatalog.Resolve(c, CoreSlot.Apu, Core(2));
            var s = ConsoleSessions.Create(c, rom, ci, pi, ai, null, Path.GetFileName(inner));
            pm.Session = s;
            pm.Machine = DebugMachines.For(s);
            pm.Source = TimeflowSource.For(s);
        }
        pm.Debugger = new Debugger(pm.Machine);
        foreach (var f in symbols ?? Array.Empty<string>()) if (File.Exists(f)) pm.Debugger.Symbols.Load(f, pm.Machine.Console);
        try { pm.Port = new DebugPort(pm.Machine, pm.Debugger); pm.Port.Attach(); } catch (NotSupportedException) { pm.Port = null; }
        return pm;
    }

    public void Dispose()
    {
        try { Port?.Detach(); } catch (Exception) { }
        try { Debugger?.Detach(); } catch (Exception) { }
        Session?.Dispose();
    }
}

/// <summary>One stored frame of a profile, decoded: its instructions (in order), how many accesses, its chip events.</summary>
public sealed class ProfileFrameView
{
    public long Frame;
    public List<ProfInstr> Instructions = new();
    public long Accesses, Writes;
    public JsonObject Events = new();
}

/// <summary>
/// A kept profile opened for viewing (the desktop's Debugger and Event View windows in profile mode): a frame at a time, its trace lines,
/// its events, the registers and call stack at any of its instructions. Each use counts as a use for the profile's time to live.
/// </summary>
public sealed class ProfileView : IDisposable
{
    public string Dir { get; }
    public ProfileMeta Meta { get; }
    public ProfileReader Reader { get; }
    public ProfileContext Ctx { get; }
    private readonly ProfileMachine machine;
    private ProfileFrameView? cached;

    private ProfileView(string dir, ProfileMeta meta, ProfileReader reader, ProfileMachine m)
    {
        Dir = dir; Meta = meta; Reader = reader; machine = m;
        Ctx = new ProfileContext
        {
            Machine = m.Machine, Debugger = m.Debugger, Live = false, Console = meta.Console, TimelineId = meta.Timeline, From = meta.From,
            To = Math.Max(meta.From, meta.LastFrameStored), StackKnownFrom = meta.StackKnownFrom, Vars = meta.Vars, OffFrame = meta.OffFrame, OffWhat = meta.OffWhat,
        };
    }

    public static ProfileView Open(string profileDir)
    {
        var rd = new ProfileReader(profileDir);
        var meta = rd.Meta;
        if (!Directory.Exists(meta.TimelineDir)) throw new FileNotFoundException($"profile {meta.Id}'s timeline {meta.Timeline} is gone ({meta.TimelineDir})");
        var m = ProfileMachine.Open(meta.TimelineDir, meta.Symbols);
        return new ProfileView(profileDir, meta, rd, m);
    }

    public IReadOnlyList<string> CpuIds => machine.Machine.Cpus.Select(c => c.Id).ToArray();

    private sealed class Collect : ProfileSink
    {
        public readonly ProfileFrameView V = new();
        public override void OnFrame(in ProfFrame f) => V.Frame = f.Frame;
        public override void OnInstr(in ProfInstr i) => V.Instructions.Add(i);
        public override void OnAccess(in ProfAccess a) { V.Accesses++; if (a.Write) V.Writes++; }
    }

    /// <summary>A stored frame (the last one asked is kept).</summary>
    public ProfileFrameView Frame(long frame)
    {
        if (cached != null && cached.Frame == frame) return cached;
        var c = new Collect();
        var ev = ProfileQuery.Parse($"events {frame} limit 5000");
        Reader.Feed(new ProfileSink[] { c, ev }, Ctx, frame, frame);
        c.V.Frame = frame;
        c.V.Events = ((ProfileQuery)ev).Result();
        cached = c.V;
        return cached;
    }

    /// <summary>The call stack at instruction #seq (innermost first), from its frame's records alone (each frame starts with the stacks).</summary>
    public JsonObject StackAt(long seq)
    {
        long f = Reader.FrameOfSeq(seq);
        var q = ProfileQuery.Parse($"stack {seq}");
        Reader.Feed(new ProfileSink[] { q }, Ctx, f, f);
        return q.Result();
    }

    /// <summary>Any query over the whole stored span.</summary>
    public JsonObject Query(string text)
    {
        var q = ProfileQuery.Parse(text);
        Reader.Feed(new ProfileSink[] { q }, Ctx);
        return q.Result();
    }

    public string TraceLine(in ProfInstr i) => Ctx.TraceLine(in i);
    public string Registers(in ProfInstr i) => Ctx.Cpu(i.Cpu).FormatRegisters(in i.R);

    public void Dispose() => machine.Dispose();
}
