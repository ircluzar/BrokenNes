using System;
using System.Collections.Generic;
using System.Linq;
using NesEmulator.Systems;

namespace NesEmulator.Corruption;

/// <summary>Where the running machine can be stored for a replay, and how (<see cref="ConsoleFarm.Capture"/>).</summary>
public sealed class AnchorCapture
{
    public Anchor? Anchor { get; init; }
    /// <summary>Why no anchor could be made (words for the user), when <see cref="Anchor"/> is null.</summary>
    public string Reason { get; init; } = "";
    /// <summary>"state", "boot" or "none" (nothing could be captured).</summary>
    public string How => Anchor == null ? "none" : Anchor.Kind == AnchorKind.State ? "state" : "boot";
}

/// <summary>
/// The farming side of a console session (SNES, Game Boy, Master System, Game Gear, Genesis): the part of the Glitch Harvester that knows how to say where a running machine is, so that a corruption made now can be started again later (in this app
/// or in the FL plugin), and how to blast it. No UI and no threads: the host calls it between frames, on the thread that runs the machine. A host attaches the session it runs and feeds <see cref="OnFrame"/> the pads it hands the
/// machine, once per frame, before the frame runs. Everything that changes the machine is either a pad (recorded by <see cref="OnFrame"/>) or a write (recorded by <see cref="Blast"/>/<see cref="NoteWrites"/>), so a replay from power-on
/// can reach the same machine; anything else (<see cref="Poison"/>) says that it cannot.
/// </summary>
public sealed class ConsoleFarm
{
    public const int RecorderCapacity = 65536;
    /// <summary>The most corruption writes a replay recipe will carry (past it positions can no longer be stored by replay).</summary>
    public const int MaxRecordedWrites = 200_000;

    private readonly InputRecorder p1 = new(RecorderCapacity), p2 = new(4096);
    private readonly List<AnchorEvent> events = new();
    private int eventWrites;
    private bool eventsFull;
    private byte[]? initialBattery;
    private string romSha1 = "";

    public IConsoleSession? Session { get; private set; }
    public byte[] Rom { get; private set; } = Array.Empty<byte>();
    public string FileName { get; private set; } = "";
    public MachineSpec Machine { get; private set; } = new();
    /// <summary>The CPU and sound unit are the console's own: a replay from power-on is exact only then (a bridge or another clock changes what the machine does).</summary>
    public bool CoresAreOwn { get; private set; } = true;
    /// <summary>Why the cores are not the console's own (for the message), when they are not.</summary>
    public string NotOwnReason { get; private set; } = "";

    public bool Attached => Session != null;
    /// <summary>The game's save RAM is no longer the player's: a corruption wrote to it, or the machine is a replayed or loaded one. A host must not write it over the player's save file.</summary>
    public bool SaveRamTouched { get; private set; }
    /// <summary>Frames the machine has run since it started, as recorded.</summary>
    public int Frames => p1.Frames;
    /// <summary>Corruption writes recorded since the game started.</summary>
    public int RecordedWrites => eventWrites;
    /// <summary>Why a replay from power-on cannot reproduce this machine (null: it can).</summary>
    public string? PoisonReason => p1.Poisoned ?? p2.Poisoned ?? (eventsFull ? "too many corruption writes have been made since the game started to replay them" : null) ?? (CoresAreOwn ? null : NotOwnReason);

    /// <summary>A new session is running: start recording from its power-on.</summary>
    public void Attach(IConsoleSession session, byte[] rom, string fileName, MachineSpec machine, bool coresAreOwn, string notOwnReason = "")
    {
        Session = session; Rom = rom; FileName = fileName; Machine = machine; CoresAreOwn = coresAreOwn; NotOwnReason = notOwnReason;
        romSha1 = ""; initialBattery = null; SaveRamTouched = false;
        ResetRecording();
    }

    public void Detach() { Session = null; Rom = Array.Empty<byte>(); romSha1 = ""; initialBattery = null; SaveRamTouched = false; ResetRecording(); }

    private void ResetRecording() { p1.Reset(); p2.Reset(); events.Clear(); eventWrites = 0; eventsFull = false; }

    /// <summary>The cores changed (a hot swap that did not change the machine, or one that did).</summary>
    public void SetCores(MachineSpec machine, bool coresAreOwn, string notOwnReason = "") { Machine = machine; CoresAreOwn = coresAreOwn; NotOwnReason = notOwnReason; }

    /// <summary>The save RAM the game started with (loaded before its first frame). Without this a replay would start with empty save RAM.</summary>
    public void SetInitialBattery(byte[]? data) => initialBattery = data == null || data.Length == 0 ? null : (byte[])data.Clone();

    /// <summary>The pads about to be handed to the machine for its next frame. Call once per <c>RunFrame</c>, before it.</summary>
    public void OnFrame(ushort pad1, ushort pad2) { p1.Record(pad1); p2.Record(pad2); }

    /// <summary>Something happened that a replay from power-on could not reproduce (a reset, a core swap, a state loaded): positions can no longer be stored that way until the game starts again.</summary>
    public void Poison(string why) { string at = $" (at frame {p1.Frames:N0})"; p1.Poison(why + at); p2.Poison(why + at); }

    /// <summary>Writes were made to the machine other than through <see cref="Blast"/> (auto-corrupt, a replayed entry's own): remember them at this frame, so a replay reaches the same machine.</summary>
    public void NoteWrites(IReadOnlyCollection<CorruptionWrite> writes)
    {
        if (writes.Count == 0) return;
        if (writes.Any(w => string.Equals(w.Domain, "SRAM", StringComparison.OrdinalIgnoreCase))) SaveRamTouched = true;
        if (eventWrites + writes.Count > MaxRecordedWrites) { eventsFull = true; return; }
        eventWrites += writes.Count;
        events.Add(new AnchorEvent { Frame = p1.Frames, Writes = writes.Select(w => w with { At = 0 }).ToList() });
    }

    public GameRef Game()
    {
        if (romSha1.Length == 0) romSha1 = AnchorStart.Sha1Hex(Rom);
        return new GameRef { Console = Consoles.Key(Session?.Console ?? ConsoleKind.Nes), Sha1 = romSha1, Title = Session?.Title ?? "", File = FileName };
    }

    public IReadOnlyList<IMemoryDomain> Domains() => Session == null ? Array.Empty<IMemoryDomain>() : MachineDomains.For(Session);

    /// <summary>
    /// Says where the machine is now, as an anchor: its saved state when the console has states (with the replay recipe next to it as the alternative when the recording is whole), else the recipe from power-on, else why not.
    /// Must be called between frames.
    /// </summary>
    public AnchorCapture Capture(StartOptions? options = null)
    {
        var s = Session;
        if (s == null) return new AnchorCapture { Reason = "no console game is running" };
        int cap = (options ?? new StartOptions()).MaxReplayFrames;
        Anchor? boot = null; string bootWhy;
        if (PoisonReason is { } why) bootWhy = why;
        else if (p1.Frames > cap) bootWhy = $"the game has run {p1.Frames:N0} frames, more than the {cap:N0} a replay from power-on may take";
        else
        {
            boot = new Anchor
            {
                Kind = AnchorKind.Boot, Frames = p1.Frames, Inputs = p1.ToScript(), Inputs2 = p2.ToScript(), Battery = initialBattery,
                Events = events.Select(e => new AnchorEvent { Frame = e.Frame, Writes = e.Writes.ToList() }).ToList(),
            };
            bootWhy = "";
        }

        if (s.SupportsState)
        {
            try
            {
                byte[] state = s.SaveState();
                string format = "";
                try { if (ConsoleStateFile.Peek(state).Units.TryGetValue("format", out var f)) format = f; } catch (System.IO.InvalidDataException) { }
                return new AnchorCapture { Anchor = new Anchor { Kind = AnchorKind.State, Console = Consoles.Key(s.Console), StateFormat = format, StateData = state, Frame = p1.Frames, Alt = boot } };
            }
            catch (Exception e) when (e is NotSupportedException or System.IO.InvalidDataException)
            {
                if (boot != null) return new AnchorCapture { Anchor = boot };
                return new AnchorCapture { Reason = $"the machine could not be saved ({e.Message}) and a replay from power-on is not possible: {bootWhy}" };
            }
        }
        if (boot != null) return new AnchorCapture { Anchor = boot };
        return new AnchorCapture { Reason = $"the {Consoles.DisplayName(s.Console)} has no save states and the game cannot be replayed from power-on: {bootWhy}. Restart the game and play to this place again." };
    }

    /// <summary>The machine was replaced by one that <see cref="AnchorStart"/> built from <paramref name="anchor"/> (or loaded into): record from there, so a position reached next can be stored too.</summary>
    public void Rebase(IConsoleSession session, Anchor anchor, IReadOnlyList<CorruptionWrite>? appliedAfter = null)
    {
        Session = session;
        SaveRamTouched = true;           // a replayed or loaded machine: its save RAM is the recipe's, not the player's
        var recipe = anchor.Kind == AnchorKind.Boot ? anchor : anchor.Alt is { Kind: AnchorKind.Boot } alt ? alt : null;
        ResetRecording();
        initialBattery = recipe?.Battery;
        if (recipe != null
            && InputScript.TryParse(recipe.Inputs, out var s1, out _) && InputScript.TryParse(recipe.Inputs2, out var s2, out _))
        {
            p1.Prime(s1, recipe.Frames); p2.Prime(s2, recipe.Frames);
            foreach (var e in recipe.Events) { events.Add(new AnchorEvent { Frame = e.Frame, Writes = e.Writes.ToList() }); eventWrites += e.Writes.Count; }
            if (eventWrites > MaxRecordedWrites) eventsFull = true;
        }
        else
        {
            p1.Poison("a saved state was loaded, and the game before it is not known"); p2.Poison("a saved state was loaded, and the game before it is not known");
        }
        if (appliedAfter is { Count: > 0 }) NoteWrites(appliedAfter.ToList());
    }

    /// <summary>
    /// Blasts the running machine: <paramref name="intensity"/> writes of <paramref name="type"/> over the selected domains, chosen by <paramref name="seed"/>. Returns the writes (with the bytes that were there) and what applying them did;
    /// the writes are remembered at this frame. Must be called between frames.
    /// </summary>
    public ApplyReport Blast(IEnumerable<string> selectedDomains, int intensity, BlastType type, long seed, bool weightBySize, out List<CorruptionWrite> writes)
    {
        var s = Session;
        if (s == null) { writes = new(); var r = new ApplyReport(); r.Problems.Add("no console game is running"); return r; }
        var domains = Domains();
        writes = BlastGenerator.Generate(domains, selectedDomains, intensity, type, seed, s.Console, weightBySize);
        var report = CorruptionApplier.Apply(domains, writes, null, 0);
        NoteWrites(writes);
        return report;
    }

    /// <summary>
    /// Starts <paramref name="entry"/> on the running game (see <see cref="AnchorStart"/>). A <c>boot</c> anchor builds a new machine; <paramref name="install"/> is the host's way of putting it where the running one is (it returns false
    /// when it could not, and the new machine is let go). A state or an immediate anchor changes the running machine in place, so the host must call this between frames. The recording is rebased on whatever machine results, with the entry's
    /// own writes remembered after it.
    /// </summary>
    public StartResult Start(StockpileEntry entry, Stockpile? file, StartOptions options, Func<IConsoleSession, bool> install)
    {
        var s = Session;
        if (s == null) return new StartResult { Ok = false, Message = "no console game is running" };
        var anchor = file?.Resolve(entry.Anchor) ?? (entry.Anchor.Kind == AnchorKind.Ref ? null : entry.Anchor);
        bool replay = anchor is { Kind: AnchorKind.Boot };
        var r = AnchorStart.Start(entry, file, Rom, FileName, s.Console, replay ? null : s, options);
        if (!r.Ok || r.Session == null || r.AnchorUsed == null) return r;
        if (r.SessionIsNew)
        {
            if (!install(r.Session)) { r.Session.Dispose(); r.Ok = false; r.Session = null; r.Message = "the replayed machine could not be put in place of the running one"; return r; }
        }
        // the machine is now at the anchor with the entry's writes; the recording starts from the anchor and remembers them
        Rebase(r.Session, r.AnchorUsed, entry.Writes.Where(w => w.At == 0).ToList());
        return r;
    }
}
