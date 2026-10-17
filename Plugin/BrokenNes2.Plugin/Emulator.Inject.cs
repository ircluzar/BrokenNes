// Round 6 (inject): the emulator's side of "push an instrument into the running game". The library (Instruments/GamePush*.cs) works on a console session; this file finds the session of the running game under the emulator's lock,
// tells the corruption farm what was written (Farm.NoteWrites) or that something a replay cannot repeat was installed (Farm.Poison), keeps the note for the user, and writes and reads the project section INJ1.
//
// Threads: every call here is made from FL's GUI thread (the tab's buttons) or from the project's save and load. The game is never running a frame while the lock is held; the sound hooks the library installs run on the audio thread
// inside the chip's own writes and never allocate.
using BrokenNes.Fruity;
using BrokenNes2.Instruments;
using NesEmulator.Corruption;

namespace BrokenNes2;

public sealed partial class Emulator
{
    private volatile string? injectNote;

    /// <summary>Words for the Instruments tab about the instruments pushed into the game that could not be kept (a project's section that was damaged or newer, a sound unit that was replaced), or null.</summary>
    public string? InjectNote => injectNote;

    internal void SetInjectNote(string? note) => injectNote = note;

    private void NoteToFarm(SessionGame g, PushOutcome o)
    {
        if (g.Farm == null || PushMutants.On(PushMutants.PushForgetsFarm)) return;
        if (o.Writes.Count > 0) g.Farm.NoteWrites(o.Writes.Select(w => new CorruptionWrite(w.Domain, w.Address, w.Value)).ToList());
        if (o.Poison != null) g.Farm.Poison(o.Poison);
    }

    /// <summary>The running console game, or why there is none to push into.</summary>
    private SessionGame? PushGame(out string why)
    {
        why = "";
        if (!Runaway) { why = "Instrument Runaway is off: freeze the game first, an edit is pushed from the frozen copy"; return null; }
        var g = game;
        if (g == null) { why = "there is no game running"; return null; }
        if (g is not SessionGame sg)
        {
            why = GamePush.WhyNot(Instruments.ConsoleId.Nes, InstrumentKind.NesPulse1)!;
            return null;
        }
        return sg;
    }

    /// <summary>Pushes the edited instrument into the running game (see <see cref="GamePush"/>). <paramref name="clean"/> is the instrument as the game had it, when the tab knows it. UI thread.</summary>
    internal PushOutcome PushInstrument(Instrument edited, Instrument? clean)
    {
        lock (EmulatorHub.Gate)
        lock (romLock)
        {
            ApplyPending();
            var g = PushGame(out string why);
            if (g == null) return PushOutcome.Refused(why);
            object? work = Snapshot ?? (object?)GbState ?? SegaState;
            var o = GamePush.Push(g.Session, work, edited, clean);
            if (o.Ok) { NoteToFarm(g, o); injectNote = GamePush.Verify(g.Session); }      // (no Changed(): a push touches no FL-visible configuration; the tab repaints on its provider's version)
            return o;
        }
    }

    /// <summary>Takes a pushed instrument out of the game: its bytes back where the game still has the pushed ones, its overrides gone. UI thread.</summary>
    internal PushOutcome RemovePush(Instrument inst)
    {
        lock (EmulatorHub.Gate)
        lock (romLock)
        {
            ApplyPending();
            var g = PushGame(out string why);
            if (g == null) return PushOutcome.Refused(why);
            var kind = GamePushes.KindOf(inst.Kind);
            if (kind == null) return PushOutcome.Refused("nothing was pushed for this kind of instrument");
            var o = GamePush.Remove(g.Session, kind.Value, inst.Index);
            if (o.Ok) NoteToFarm(g, o);
            return o;
        }
    }

    /// <summary>Takes everything pushed out of the game.</summary>
    internal PushOutcome RemoveAllPushes()
    {
        lock (EmulatorHub.Gate)
        lock (romLock)
        {
            ApplyPending();
            if (game is not SessionGame g) return PushOutcome.Refused("there is no console game running");
            var o = GamePush.RemoveAll(g.Session);
            if (o.Ok) NoteToFarm(g, o);
            return o;
        }
    }

    /// <summary>Whether this instrument of the frozen copy has been pushed into the game (the tab marks it).</summary>
    internal bool IsPushed(Instrument inst)
    {
        lock (romLock)
        {
            ApplyPending();
            return game is SessionGame g && GamePush.IsPushed(g.Session, inst.Kind, inst.Index);
        }
    }

    /// <summary>How many instruments are pushed into the running game.</summary>
    public int PushedCount
    {
        get { lock (romLock) { ApplyPending(); return game is SessionGame g ? GamePush.Count(g.Session) : 0; } }
    }

    /// <summary>One line per instrument pushed into the running game.</summary>
    internal IReadOnlyList<string> DescribePushes()
    {
        lock (romLock) { ApplyPending(); return game is SessionGame g ? GamePush.Describe(g.Session) : []; }
    }

    /// <summary>Checks that the sound core still holds what was pushed (a replaced sound unit loses it); the answer is also the tab's note.</summary>
    internal string? VerifyPushes()
    {
        lock (romLock)
        {
            ApplyPending();
            if (game is not SessionGame g || GamePush.Count(g.Session) == 0) return injectNote;
            return injectNote = GamePush.Verify(g.Session) ?? (injectNote != null && injectNote.StartsWith("Pushed instruments are no longer") ? null : injectNote);
        }
    }

    /// <summary>The game was replaced (a reload, a reset, a corruption started from a stockpile): what was pushed into the old one went with it. The user is told.</summary>
    private void NotePushesLost(IGame? old, IGame? replacement)
    {
        if (old is SessionGame og && GamePush.Count(og.Session) > 0 && !ReferenceEquals(old, replacement))
            injectNote = $"{GamePush.Count(og.Session)} pushed instrument{(GamePush.Count(og.Session) == 1 ? "" : "s")} left the game when the game was replaced (a reload, a reset or a corruption started from a stockpile): push again";
    }

    /// <summary>How many memory writes the corruption farm holds for the running game (-1: no console game).</summary>
    internal int FarmWritesForTests() { lock (romLock) { ApplyPending(); return (game as SessionGame)?.Farm?.RecordedWrites ?? -1; } }

    /// <summary>A hash of the sound memory of the running game itself (not Runaway's copy): the SNES audio RAM, the Genesis YM2612 channel patches, the Game Boy wave RAM, the YM2413 user instrument; 0 with no game. Tests compare it before a push, after it and after the removal.</summary>
    internal long LiveSoundHashForTests()
    {
        lock (romLock)
        {
            ApplyPending();
            if (game is not SessionGame g) return 0;
            var m = PushMachine.Of(g.Session);
            ulong h = 1469598103934665603UL;
            void Add(ReadOnlySpan<byte> b) { foreach (byte x in b) h = (h ^ x) * 1099511628211UL; }
            if (m.Spc != null) Add(m.Spc.Aram);
            else if (m.MdUnit != null)
            {
                Span<byte> p = stackalloc byte[OpnPushHook.PatchBytes];
                for (int c = 0; c < 6; c++) { OpnPushHook.ReadPatch(m.MdUnit.Fm, c, p); Add(p); }
                Add([m.MdUnit.Fm.Register(0x22)]);
            }
            else if (m.GbApu != null) Add(m.GbApu.WaveRam);
            else if (m.SmsFm != null) for (int i = 0; i < 8; i++) Add([m.SmsFm.Fm.Register(i)]);
            else return 0;
            return (long)(h | 1);
        }
    }

    // ---- the project section INJ1 ----

    /// <summary>The payload of the project's INJ1 section, or null when nothing is pushed. Under the emulator's lock, between frames.</summary>
    internal byte[]? CaptureInjectSection()
    {
        if (Mode != 1) return null;
        lock (romLock)
        {
            if (loading || Volatile.Read(ref pendingGame) != null) return carriedInject;
            var g = game;
            if (g is not SessionGame sg) return carriedInject;
            return GamePush.Serialize(sg.Session);
        }
    }

    private volatile byte[]? carriedInject;
    private InjectRestore? pendingInject;

    /// <summary>What a project carried of the pushes: the section's bytes, or why it could not be read.</summary>
    internal sealed record InjectRestore(byte[]? Payload, string? Problem);

    /// <summary>Called when the project is opened, before the game is built: the section waits for the game (<see cref="ApplyInjectRestore"/>).</summary>
    internal void SetInjectRestore(byte[]? payload, string? problem)
    {
        pendingInject = payload == null && problem == null ? null : new InjectRestore(payload, problem);
        carriedInject = payload;
        injectNote = problem == null ? null : $"The project's pushed instruments were not restored: the section is {problem}";
    }

    /// <summary>The game's state was not put back: the pushes of the project are not installed (the memory they wrote is not in the game), and the user is told.</summary>
    private void DropInjectRestore(string why)
    {
        var req = Interlocked.Exchange(ref pendingInject, null);
        if (req?.Payload != null) injectNote = "The project's pushed instruments were not restored: " + why;
    }

    /// <summary>On the game's loading thread, after the game's state is back: the pushed instruments of the project are installed in the game, and the user is told what is not as it was. Never throws.</summary>
    private void ApplyInjectRestore(IGame g)
    {
        var req = Interlocked.Exchange(ref pendingInject, null);
        if (req == null) return;
        try
        {
            if (req.Payload == null) return;
            if (g is not SessionGame sg) { injectNote = "The project's pushed instruments were not restored: this game cannot take them"; return; }
            string? problem = GamePush.Restore(sg.Session, req.Payload, out var notes);
            if (problem != null) { injectNote = "The project's pushed instruments were not restored: " + problem; return; }
            carriedInject = null;
            injectNote = notes.Count > 0 ? "Pushed instruments restored, but " + string.Join("; ", notes) : null;
        }
        catch (Exception e)
        {
            Diag.Log("restoring the pushed instruments failed: " + e);
            injectNote = "The project's pushed instruments could not be restored: " + e.Message;
        }
    }
}
