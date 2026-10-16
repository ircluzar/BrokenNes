using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using NesEmulator.Systems;

namespace NesEmulator.Corruption;

public sealed class StartOptions
{
    /// <summary>The most frames a <c>boot</c> anchor may replay (about ten minutes of game). A longer one is refused with a message, not run.</summary>
    public int MaxReplayFrames { get; set; } = 36_000;
    /// <summary>Progress text ("replaying 2400 / 4200 frames"), called about twice a second of replay.</summary>
    public Action<string>? Status { get; set; }
    /// <summary>Polled between frames of a replay; true stops it (the start then reports it was cancelled).</summary>
    public Func<bool>? Cancelled { get; set; }
    /// <summary>Apply a corruption made on another game anyway (a warning is given). Off: it is refused.</summary>
    public bool AllowOtherGame { get; set; }
    /// <summary>Builds a fresh session for a <c>boot</c> replay (or a <c>state</c>/<c>none</c> start with no running game): console, a private copy of the ROM, the cores, the file name. Default: <see cref="ConsoleSessions.Create"/> with the cores the entry names.</summary>
    public Func<ConsoleKind, byte[], MachineSpec, string, IConsoleSession>? SessionFactory { get; set; }
}

public sealed class StartResult
{
    public bool Ok { get; set; }
    /// <summary>One line for the user: what happened, or why not.</summary>
    public string Message { get; set; } = "";
    public List<string> Warnings { get; } = new();
    /// <summary>The machine the writes landed in: the running session (state and none anchors), or the new one a replay built (the caller owns and runs it).</summary>
    public IConsoleSession? Session { get; set; }
    public bool SessionIsNew { get; set; }
    public ApplyReport? Report { get; set; }
    public AnchorKind Used { get; set; }
    public int FramesReplayed { get; set; }
    public bool Cancelled { get; set; }
    /// <summary>Writes for frames after the anchor (<see cref="CorruptionWrite.At"/> above 0), for a <see cref="CorruptionTimeline"/>.</summary>
    public List<CorruptionWrite> Later { get; } = new();
    public WriteJournal Journal { get; } = new();
}

/// <summary>Applies the writes due at later frames: <see cref="Tick"/> is called before each frame with the number of frames run since the anchor.</summary>
public sealed class CorruptionTimeline
{
    private readonly IReadOnlyList<IMemoryDomain> domains;
    private readonly SortedDictionary<int, List<CorruptionWrite>> due = new();
    public CorruptionTimeline(IReadOnlyList<IMemoryDomain> domains, IEnumerable<CorruptionWrite> later)
    {
        this.domains = domains;
        foreach (var w in later) { if (!due.TryGetValue(w.At, out var l)) due[w.At] = l = new(); l.Add(w); }
    }
    public bool Done => due.Count == 0;
    public ApplyReport? Tick(int framesSinceAnchor)
    {
        if (!due.TryGetValue(framesSinceAnchor, out var l)) return null;
        due.Remove(framesSinceAnchor);
        return CorruptionApplier.Apply(domains, l, null, framesSinceAnchor);
    }
}

/// <summary>Puts a stockpile entry's corruption into a machine: finds the anchor, builds or loads the machine at it, applies the writes. Every refusal says why; nothing is applied to the wrong game or the wrong memory.</summary>
public static class AnchorStart
{
    public static string Sha1Hex(byte[] rom) => Convert.ToHexString(SHA1.HashData(rom)).ToLowerInvariant();

    /// <summary>
    /// Starts <paramref name="entry"/> on <paramref name="rom"/> (the loaded game's file bytes). <paramref name="running"/> is the machine that runs the game now, used by <c>none</c> and <c>state</c> anchors; a <c>boot</c>
    /// anchor always builds a new machine (never touching the running one). Must run on the thread that owns the machines, between frames.
    /// </summary>
    public static StartResult Start(StockpileEntry entry, Stockpile? file, byte[] rom, string fileName, ConsoleKind console, IConsoleSession? running, StartOptions? options = null)
    {
        options ??= new StartOptions();
        var r = new StartResult();
        string label = entry.Name.Length > 0 ? $"'{entry.Name}'" : "the corruption";

        // the game it belongs to
        string sha = Sha1Hex(rom);
        if (entry.Game.Sha1.Length > 0 && !string.Equals(entry.Game.Sha1, sha, StringComparison.OrdinalIgnoreCase))
        {
            string made = entry.Game.Title.Length > 0 ? $"\"{entry.Game.Title}\"" : entry.Game.File.Length > 0 ? entry.Game.File : "another game";
            if (!options.AllowOtherGame) return Refuse(r, $"{label} was made on {made}, not on the game that is loaded ({fileName}); load that game to start it");
            r.Warnings.Add($"{label} was made on {made}; its writes are applied to {fileName} anyway");
        }
        else if (entry.Game.Sha1.Length == 0) r.Warnings.Add($"{label} does not say which game it was made on; it is applied to {fileName}");
        if (entry.Game.Console.Length > 0 && !RomDetect.Runs(console, Consoles.FromKey(entry.Game.Console)))
            return Refuse(r, $"{label} is for the {Consoles.DisplayName(Consoles.FromKey(entry.Game.Console))}, not the {Consoles.DisplayName(console)}");

        // the anchor, with its alternative when the first is refused
        var anchor = file?.Resolve(entry.Anchor) ?? (entry.Anchor.Kind == AnchorKind.Ref ? null : entry.Anchor);
        if (anchor == null) return Refuse(r, $"{label} points to a shared base the stockpile does not contain");
        var tried = new List<string>();
        for (var a = anchor; a != null; a = a.Alt == null ? null : file?.Resolve(a.Alt) ?? (a.Alt.Kind == AnchorKind.Ref ? null : a.Alt))
        {
            var attempt = TryAnchor(a, entry, rom, fileName, console, running, options, r, out string why);
            if (attempt) { if (tried.Count > 0) r.Warnings.Insert(0, "the first anchor was refused (" + string.Join("; ", tried) + "), the alternative was used"); return Finish(entry, r, console, options); }
            if (r.Cancelled) return r;
            tried.Add(why);
        }
        return Refuse(r, $"{label} cannot be started: " + string.Join("; and ", tried));
    }

    private static StartResult Refuse(StartResult r, string why) { r.Ok = false; r.Message = why; return r; }

    private static bool TryAnchor(Anchor a, StockpileEntry entry, byte[] rom, string fileName, ConsoleKind console, IConsoleSession? running, StartOptions o, StartResult r, out string why)
    {
        why = "";
        IConsoleSession? session = null;
        bool isNew = false;
        try
        {
            switch (a.Kind)
            {
                case AnchorKind.None:
                    session = running ?? Fresh(console, rom, entry.Machine, fileName, o, out isNew);
                    r.Used = AnchorKind.None;
                    break;

                case AnchorKind.State:
                {
                    if (a.Console.Length > 0 && !RomDetect.Runs(console, Consoles.FromKey(a.Console))) { why = $"its saved machine is a {Consoles.DisplayName(Consoles.FromKey(a.Console))}'s"; return false; }
                    session = running ?? Fresh(console, rom, entry.Machine, fileName, o, out isNew);
                    if (!session.SupportsState) { why = session.StateUnsupportedReason; if (isNew) session.Dispose(); return false; }
                    try { session.LoadState(a.StateData); }
                    catch (Exception e) when (e is System.IO.InvalidDataException or NotSupportedException) { why = e.Message; if (isNew) session.Dispose(); return false; }
                    r.Used = AnchorKind.State;
                    break;
                }

                case AnchorKind.Boot:
                {
                    if (a.Frames > o.MaxReplayFrames)
                    { why = $"it replays {a.Frames:N0} frames from power-on ({a.Frames / 3600.0:F1} minutes of game), more than the {o.MaxReplayFrames:N0} allowed"; return false; }
                    bool ok1 = InputScript.TryParse(a.Inputs, out var in1, out string e1), ok2 = InputScript.TryParse(a.Inputs2, out var in2, out string e2);
                    if (!ok1 || !ok2) { why = "its recorded input is damaged (" + (!ok1 ? e1 : e2) + ")"; return false; }
                    session = Fresh(console, rom, entry.Machine, fileName, o, out isNew);
                    if (a.Battery is { Length: > 0 }) session.ImportSave(a.Battery);
                    var p1 = new InputScript.Player(in1); var p2 = new InputScript.Player(in2);
                    var buf = new short[32768];
                    var clock = Stopwatch.StartNew();
                    long lastReport = 0;
                    for (int f = 0; f < a.Frames; f++)
                    {
                        if (o.Cancelled?.Invoke() == true) { session.Dispose(); r.Cancelled = true; r.Ok = false; r.Message = $"stopped at frame {f:N0} of {a.Frames:N0}; nothing was applied"; why = "cancelled"; return false; }
                        session.SetPad(0, (PadButtons)p1.Bits(f));
                        session.SetPad(1, (PadButtons)p2.Bits(f));
                        session.RunFrame();
                        while (session.ReadSamples(buf) > 0) { }
                        if (o.Status != null && clock.ElapsedMilliseconds - lastReport >= 500)
                        {
                            lastReport = clock.ElapsedMilliseconds;
                            o.Status($"replaying {f + 1:N0} / {a.Frames:N0} frames");
                        }
                    }
                    r.FramesReplayed = a.Frames;
                    r.Used = AnchorKind.Boot;
                    break;
                }

                default:
                    why = "it has no usable anchor";
                    return false;
            }
        }
        catch (Exception e)
        {
            why = e.Message;
            if (isNew) session?.Dispose();
            return false;
        }
        r.Session = session;
        r.SessionIsNew = isNew || a.Kind == AnchorKind.Boot;
        return true;
    }

    private static IConsoleSession Fresh(ConsoleKind console, byte[] rom, MachineSpec m, string fileName, StartOptions o, out bool isNew)
    {
        isNew = true;
        var copy = (byte[])rom.Clone();     // a session that will be corrupted owns its ROM: a ROM write must not reach the caller's bytes
        if (o.SessionFactory != null) return o.SessionFactory(console, copy, m, fileName);
        string Pick(CoreSlot slot, string stored) => CoreCatalog.Resolve(console, slot, stored);
        return ConsoleSessions.Create(console, copy, Pick(CoreSlot.Cpu, m.Cpu), Pick(CoreSlot.Ppu, m.Ppu), Pick(CoreSlot.Apu, m.Apu), null, fileName);
    }

    private static StartResult Finish(StockpileEntry entry, StartResult r, ConsoleKind console, StartOptions o)
    {
        var session = r.Session!;
        var domains = MachineDomains.For(session);
        string label = entry.Name.Length > 0 ? $"'{entry.Name}'" : "the corruption";
        if (domains.Count == 0)
        {
            if (r.SessionIsNew) session.Dispose();
            r.Session = null;
            return Refuse(r, $"{label} cannot be applied: " + MachineDomains.Why(session));
        }
        // the cores the corruption was made with, against the ones this machine runs (the machine names them; a difference is said, not hidden)
        foreach (var w in entry.Writes) if (w.At > 0) r.Later.Add(w);
        r.Report = CorruptionApplier.Apply(domains, entry.Writes, r.Journal, 0);
        r.Warnings.AddRange(r.Report.Problems);
        if (r.Report.Requested > 0 && r.Report.Applied == 0 && r.Later.Count == 0)
        {
            if (r.SessionIsNew) session.Dispose();
            r.Session = null;
            return Refuse(r, $"{label} could not be applied: " + (r.Report.Problems.Count > 0 ? r.Report.Problems[0] : r.Report.Describe()));
        }
        r.Ok = true;
        string how = r.Used switch
        {
            AnchorKind.Boot => $"replayed {r.FramesReplayed:N0} frames from power-on",
            AnchorKind.State => "loaded its saved machine",
            _ => "applied to the machine as it is",
        };
        r.Message = $"started {label}: {how}; {r.Report.Describe()}" + (r.Later.Count > 0 ? $"; {r.Later.Count} more writes follow at later frames" : "");
        return r;
    }
}
