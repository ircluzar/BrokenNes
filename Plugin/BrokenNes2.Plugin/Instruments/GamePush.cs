// Round 6 (inject): pushing an edited instrument into the RUNNING game.
//
// Instrument Runaway freezes a game and keeps a private copy of its sound memory; edits normally change only that copy (what FL's notes play). This is the option to also put the edit into the game itself, so that when Runaway is
// switched off the game's own music plays the edited instrument. What can stick depends on the console and on the game's sound driver, and none of it is hidden:
//   * what lives in memory the driver does not rewrite (a SNES sample and its loop pointer in audio RAM) is written there, and stays until the game loads other sounds over it;
//   * what the driver rewrites at will (a SNES voice's envelope, volumes, pitch; a Genesis channel's patch; the DAC stream) is overridden where the chip reads it: a small optional hook in the sound core (DSP_SFC.Overrides,
//     Ym2612.Override) that is null, and changes nothing, unless an instrument has been pushed;
//   * everything else is refused with the reason (PushOutcome.Message), never half done.
// The records of what was pushed (with the original bytes, so "remove from game" can put them back) live here, per game; they are saved in the project as the section INJ1 (GamePushFile).
// No UI and no emulator class in this file: it works on an IConsoleSession so the checks can drive real games without the plugin (the glue to the Emulator is Emulator.Inject.cs).
using System.Runtime.CompilerServices;
using NesEmulator.Gb;
using NesEmulator.Mix;
using NesEmulator.Sega;
using NesEmulator.Snes;
using NesEmulator.Systems;

namespace BrokenNes2.Instruments;

/// <summary>What kind of thing a push put into the game. The numbers are saved in projects: append only.</summary>
public enum PushKind : byte { SnesSample = 1, OpnPatch = 2, OpnDac = 3, GbWave = 4, OpllUser = 5 }

/// <summary>A stretch of the game's memory a push wrote: where, what was there (to put back) and what the push wrote.</summary>
public sealed class PushRegion
{
    /// <summary>The corruption domain it is in ("ARAM": the SNES audio RAM), for the corruption farm that keeps a replayable record of the game.</summary>
    public string Domain = "ARAM";
    public int Address;
    public byte[] Original = [];
    public byte[] Pushed = [];
}

/// <summary>One instrument pushed into the game. <see cref="A"/> / <see cref="B"/> / <see cref="P"/> are what the kind needs (see each pusher): the original and the edited form of what is overridden, and a few numbers.</summary>
public sealed class PushRecord
{
    public PushKind Kind;
    /// <summary>The instrument's index in its snapshot (a SNES sample slot, a Genesis channel or DAC number, ...): a label; what is matched in the game is the content.</summary>
    public int Index;
    public string Name = "";
    public byte[] A = [], B = [];
    public int[] P = [];
    public List<PushRegion> Regions = new();

    public PushRecord Clone() => new() { Kind = Kind, Index = Index, Name = Name, A = (byte[])A.Clone(), B = (byte[])B.Clone(), P = (int[])P.Clone(), Regions = Regions.Select(r => new PushRegion { Domain = r.Domain, Address = r.Address, Original = (byte[])r.Original.Clone(), Pushed = (byte[])r.Pushed.Clone() }).ToList() };
}

/// <summary>What a push or a removal did: <see cref="Message"/> is for the user (what stuck, what the driver overwrites, what was refused), <see cref="Writes"/> are the bytes written into the game's memory (for the corruption farm), <see cref="Poison"/> says that something was
/// installed that a replay of the game from power-on could not reproduce.</summary>
public sealed class PushOutcome
{
    public bool Ok;
    public string Message = "";
    public readonly List<(string Domain, int Address, byte Value)> Writes = new();
    public string? Poison;
    public static PushOutcome Refused(string why) => new() { Ok = false, Message = why };
}

/// <summary>The part of a console session a push works on, found through the picture bridge of the Sega consoles.</summary>
internal sealed class PushMachine
{
    public IConsoleSession Session = null!;
    public ConsoleId Console;
    public SnesSession? Snes;
    public APU_SFC? Spc;
    public MdSession? Md;
    public MdSoundUnit? MdUnit;
    public GbSession? Gb;
    public APU_GB? GbApu;
    public SmsSession? Sms;
    public SmsFmSound? SmsFm;

    public static PushMachine Of(IConsoleSession s)
    {
        var inner = s is SegaPictureSession w ? w.Inner : s;
        var m = new PushMachine { Session = inner };
        switch (inner)
        {
            case SnesSession sn: m.Console = ConsoleId.Snes; m.Snes = sn; m.Spc = sn.Apu as APU_SFC; break;
            case MdSession md: m.Console = ConsoleId.Genesis; m.Md = md; m.MdUnit = md.Board.Sound as MdSoundUnit; break;
            case GbSession gb: m.Console = ConsoleId.GameBoy; m.Gb = gb; m.GbApu = gb.Apu as APU_GB; break;
            case SmsSession sms: m.Console = ConsoleId.MasterSystem; m.Sms = sms; m.SmsFm = sms.Board.Sound as SmsFmSound; break;
            default: m.Console = ConsoleId.Nes; break;
        }
        return m;
    }
}

/// <summary>Everything pushed into one game: the records, and the hooks installed in its sound core for them. Lives as long as the game's session.</summary>
public sealed class GamePushes
{
    private static readonly ConditionalWeakTable<object, GamePushes> Table = new();
    private static object Key(IConsoleSession s) => s is SegaPictureSession w ? w.Inner : s;

    public static GamePushes For(IConsoleSession s) => Table.GetValue(Key(s), _ => new GamePushes());
    public static GamePushes? Peek(IConsoleSession s) => Table.TryGetValue(Key(s), out var g) ? g : null;

    /// <summary>The records, newest last. Changed only on the UI thread (the emulator's lock held).</summary>
    public readonly List<PushRecord> Records = new();

    // the hooks as installed: Verify compares them with what the sound core holds now
    internal DspVoiceOverrides? Dsp;
    internal object? DspOwner;
    internal OpnPushHook? Opn;
    internal object? OpnOwner;
    internal GbPushHook? Gb;
    internal object? GbOwner;
    internal OpllPushHook? Opll;
    internal object? OpllOwner;

    public int Count => Records.Count;

    public PushRecord? Find(PushKind kind, int index) { foreach (var r in Records) if (r.Kind == kind && r.Index == index) return r; return null; }

    public static PushKind? KindOf(InstrumentKind k) => k switch
    {
        InstrumentKind.SnesSample => PushKind.SnesSample,
        InstrumentKind.OpnPatch => PushKind.OpnPatch,
        InstrumentKind.OpnDac => PushKind.OpnDac,
        InstrumentKind.GbWave => PushKind.GbWave,
        InstrumentKind.OpllUser => PushKind.OpllUser,
        _ => null,
    };
}

/// <summary>Push / remove / report, per console.</summary>
public static class GamePush
{
    /// <summary>Why a push is refused on kinds that have nothing to push (also what the tab shows). Null when the kind can be pushed.</summary>
    public static string? WhyNot(ConsoleId console, InstrumentKind kind) => kind switch
    {
        InstrumentKind.PsgTones or InstrumentKind.PsgNoise =>
            "cannot push on the PSG: its tone, volume and noise registers are written by the game's driver tens of times a second as the music plays, there is no instrument memory to change, and the Runaway sampler does not play these registers either (it plays the PSG from FL's pitch and level)",
        InstrumentKind.OpllRom => "a ROM instrument is built into the YM2413 and is read-only: copy it into the user instrument first",
        InstrumentKind.GbPulse1 or InstrumentKind.GbPulse2 or InstrumentKind.GbNoise =>
            "cannot push on a Game Boy pulse or noise channel yet: the game's driver writes the duty and envelope registers at every note, and no hook on the channel trigger is built for them (the wave channel's RAM can be pushed)",
        InstrumentKind.NesPulse1 or InstrumentKind.NesPulse2 or InstrumentKind.NesTriangle or InstrumentKind.NesNoise or InstrumentKind.NesDmc =>
            "cannot push on the NES: a NES game has no instrument memory (its code writes the sound chip's registers for every note), and BrokenNes2 has no NES sampler to edit instruments for",
        _ => null,
    };

    /// <summary>Pushes <paramref name="edited"/> (an instrument of the Runaway working copy <paramref name="workingCopy"/>, with the edit in it) into the running game of <paramref name="session"/>. Replaces an earlier push of the same instrument.
    /// The game must not be running a frame meanwhile (the emulator's lock). Never throws for a game that cannot take it: the answer says why.</summary>
    public static PushOutcome Push(IConsoleSession session, object? workingCopy, Instrument edited, Instrument? clean = null)
    {
        string? no = WhyNot(edited.Console, edited.Kind);
        if (no != null) return PushOutcome.Refused(no);
        var m = PushMachine.Of(session);
        var st = GamePushes.For(session);
        try
        {
            return edited.Kind switch
            {
                InstrumentKind.SnesSample => SnesPusher.Push(m, st, workingCopy as SpcSnapshot, edited),
                InstrumentKind.OpnPatch => OpnPusher.PushPatch(m, st, workingCopy as SegaSnapshot, edited, clean),
                InstrumentKind.OpnDac => OpnPusher.PushDac(m, st, workingCopy as SegaSnapshot, edited, clean),
                InstrumentKind.GbWave => OtherPushers.PushGbWave(m, st, edited, clean),
                InstrumentKind.OpllUser => OtherPushers.PushOpll(m, st, workingCopy as SegaSnapshot, edited, clean),
                _ => PushOutcome.Refused($"cannot push a {edited.Kind} instrument"),
            };
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return PushOutcome.Refused("the push failed and was not applied: " + e.Message);
        }
    }

    /// <summary>Takes a pushed instrument out of the game: its bytes go back where the game still has the pushed ones, and its overrides are removed.</summary>
    public static PushOutcome Remove(IConsoleSession session, PushKind kind, int index)
    {
        var st = GamePushes.Peek(session);
        var rec = st?.Find(kind, index);
        if (st == null || rec == null) return PushOutcome.Refused("nothing was pushed for this instrument");
        var m = PushMachine.Of(session);
        return RemoveRecord(m, st, rec);
    }

    /// <summary>Takes every pushed instrument out of the game.</summary>
    public static PushOutcome RemoveAll(IConsoleSession session)
    {
        var st = GamePushes.Peek(session);
        if (st == null || st.Count == 0) return PushOutcome.Refused("nothing was pushed into this game");
        var m = PushMachine.Of(session);
        var all = new PushOutcome { Ok = true };
        int n = 0;
        foreach (var rec in st.Records.ToArray())
        {
            var o = RemoveRecord(m, st, rec);
            n++;
            all.Message += (all.Message.Length > 0 ? " " : "") + o.Message;
            all.Writes.AddRange(o.Writes);
        }
        if (n > 1) all.Message = $"removed {n} pushed instruments from the game. " + all.Message;
        return all;
    }

    private static PushOutcome RemoveRecord(PushMachine m, GamePushes st, PushRecord rec)
    {
        var o = rec.Kind switch
        {
            PushKind.SnesSample => SnesPusher.Remove(m, st, rec),
            PushKind.OpnPatch or PushKind.OpnDac => OpnPusher.Remove(m, st, rec),
            PushKind.GbWave => OtherPushers.RemoveGbWave(m, st, rec),
            PushKind.OpllUser => OtherPushers.RemoveOpll(m, st, rec),
            _ => PushOutcome.Refused("unknown kind of push"),
        };
        return o;
    }

    /// <summary>Whether a push exists for this instrument (the tab marks it).</summary>
    public static bool IsPushed(IConsoleSession session, InstrumentKind kind, int index)
    {
        var pk = GamePushes.KindOf(kind);
        return pk != null && GamePushes.Peek(session)?.Find(pk.Value, index) != null;
    }

    public static int Count(IConsoleSession session) => GamePushes.Peek(session)?.Count ?? 0;

    /// <summary>One line per pushed instrument, for the tab and the logs.</summary>
    public static IReadOnlyList<string> Describe(IConsoleSession session)
    {
        var st = GamePushes.Peek(session);
        return st == null ? [] : st.Records.Select(r => $"{r.Kind} #{r.Index} {r.Name}").ToList();
    }

    /// <summary>Whether the sound core still holds the hooks the records need (a sound unit swapped for another loses them). Returns the problems in words; the hooks are put back where the same unit is still there.</summary>
    public static string? Verify(IConsoleSession session)
    {
        var st = GamePushes.Peek(session);
        if (st == null || st.Count == 0) return null;
        var m = PushMachine.Of(session);
        var lost = new List<string>();
        if (st.Records.Any(r => r.Kind == PushKind.SnesSample) && (m.Spc == null || !ReferenceEquals(m.Spc.Dsp, st.DspOwner) || !ReferenceEquals(m.Spc.Dsp.Overrides, st.Dsp))) lost.Add("the SNES sound unit was replaced");
        if (st.Records.Any(r => r.Kind is PushKind.OpnPatch or PushKind.OpnDac) && (m.MdUnit == null || !ReferenceEquals(m.MdUnit.Fm, st.OpnOwner) || !ReferenceEquals(m.MdUnit.Fm.Override, st.Opn))) lost.Add("the Genesis sound unit was replaced");
        if (st.Records.Any(r => r.Kind == PushKind.GbWave) && (m.GbApu == null || !ReferenceEquals(m.GbApu, st.GbOwner) || !ReferenceEquals(m.GbApu.Override, st.Gb))) lost.Add("the Game Boy sound unit was replaced");
        if (st.Records.Any(r => r.Kind == PushKind.OpllUser) && (m.SmsFm == null || !ReferenceEquals(m.SmsFm.Fm, st.OpllOwner) || !ReferenceEquals(m.SmsFm.Fm.Override, st.Opll))) lost.Add("the Master System sound unit was replaced");
        if (lost.Count == 0) return null;
        return "Pushed instruments are no longer in the game: " + string.Join(", ", lost.Distinct()) + ", and with it what was pushed";
    }

    // ---- the saved form (the project section INJ1) ----

    /// <summary>The section's payload for a project, or null when nothing was pushed.</summary>
    public static byte[]? Serialize(IConsoleSession session)
    {
        var st = GamePushes.Peek(session);
        if (st == null || st.Count == 0) return null;
        return GamePushFile.Write(st.Records, st.Opn?.SaveState() ?? []);
    }

    /// <summary>Puts a project's pushes back on a game whose state has been restored (the memory they wrote is in that state already): the records and the hooks. Returns a problem in words, or null; nothing is changed when there is one. The notes list
    /// what the game's memory looks like against the records.</summary>
    public static string? Restore(IConsoleSession session, byte[] payload, out IReadOnlyList<string> notes)
    {
        notes = [];
        var recs = GamePushFile.Read(payload, out var hookState, out string? problem);
        if (recs == null) return problem ?? "damaged";
        var m = PushMachine.Of(session);
        var st = GamePushes.For(session);
        var list = new List<string>();
        // every record must be for a core this game has
        foreach (var r in recs)
        {
            string? why = r.Kind switch
            {
                PushKind.SnesSample => m.Spc == null ? "the project pushed SNES samples, but this game does not run on the SFC audio unit" : null,
                PushKind.OpnPatch or PushKind.OpnDac => m.MdUnit == null ? "the project pushed Genesis instruments, but this game has no Genesis sound unit" : null,
                PushKind.GbWave => m.GbApu == null ? "the project pushed a Game Boy wave, but this game has no Game Boy sound unit" : null,
                PushKind.OpllUser => m.SmsFm == null ? "the project pushed a YM2413 instrument, but this game has no YM2413" : null,
                _ => "unknown kind of push",
            };
            if (why != null) return why;
        }
        st.Records.Clear();
        st.Records.AddRange(recs);
        if (!PushMutants.On(PushMutants.RestoreSkipsHooks)) PushHooks.Install(m, st);
        if (!PushMutants.On(PushMutants.OpnStateNotRestored)) st.Opn?.LoadState(hookState);
        // does the game's memory hold what the records say it does?
        foreach (var r in recs)
            foreach (var reg in r.Regions)
            {
                var live = PushHooks.ReadRegion(m, r.Kind, reg);
                if (live == null) continue;
                if (live.AsSpan().SequenceEqual(reg.Pushed)) continue;
                list.Add(live.AsSpan().SequenceEqual(reg.Original) ? $"{r.Name}: the game's memory at ${reg.Address:X4} is the original (the pushed bytes are not in it)" : $"{r.Name}: the game has changed the memory at ${reg.Address:X4} since it was pushed");
            }
        notes = list;
        return null;
    }
}

/// <summary>Installs the hooks that go with a set of records into the game's sound core, and reads back the memory a record wrote.</summary>
internal static class PushHooks
{
    public static void Install(PushMachine m, GamePushes st)
    {
        // SNES: one table of per-sample overrides on the S-DSP
        if (m.Spc != null)
        {
            var items = st.Records.Where(r => r.Kind == PushKind.SnesSample).Select(SnesPusher.EntryOf).Where(e => e.Flags != 0).ToList();
            st.Dsp = items.Count == 0 ? null : new DspVoiceOverrides(items);
            st.DspOwner = m.Spc.Dsp;
            m.Spc.Dsp.Overrides = st.Dsp;
        }
        if (m.MdUnit != null)
        {
            var recs = st.Records.Where(r => r.Kind is PushKind.OpnPatch or PushKind.OpnDac).ToList();
            var prev = st.Opn;
            st.Opn = recs.Count == 0 ? null : new OpnPushHook(recs);
            if (prev != null && st.Opn != null) st.Opn.LoadState(prev.SaveState());       // the volume memory of the channels carries over to the new table
            st.OpnOwner = m.MdUnit.Fm;
            m.MdUnit.Fm.Override = st.Opn;
        }
        if (m.GbApu != null)
        {
            var recs = st.Records.Where(r => r.Kind == PushKind.GbWave).ToList();
            st.Gb = recs.Count == 0 ? null : new GbPushHook(recs);
            st.GbOwner = m.GbApu;
            m.GbApu.Override = st.Gb;
        }
        if (m.SmsFm != null)
        {
            var recs = st.Records.Where(r => r.Kind == PushKind.OpllUser).ToList();
            st.Opll = recs.Count == 0 ? null : new OpllPushHook(recs);
            st.OpllOwner = m.SmsFm.Fm;
            m.SmsFm.Fm.Override = st.Opll;
        }
    }

    /// <summary>The bytes the game's memory holds now where a record wrote (null when that memory is not reachable).</summary>
    public static byte[]? ReadRegion(PushMachine m, PushKind kind, PushRegion reg)
    {
        if (reg.Domain == "ARAM" && m.Spc != null)
        {
            if (reg.Address < 0 || reg.Address + reg.Pushed.Length > 0x10000) return null;
            return m.Spc.Aram.AsSpan(reg.Address, reg.Pushed.Length).ToArray();
        }
        if (reg.Domain == "WAVE" && m.GbApu != null) return (byte[])m.GbApu.WaveRam.Clone();
        return null;
    }
}
