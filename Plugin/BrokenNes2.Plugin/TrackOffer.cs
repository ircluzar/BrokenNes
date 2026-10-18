// Round 8 follow-up (record, 2026-10-18): the tracks of a recording on offer to BrokenNes2 channels.
//
// A finished recording (and one a project brings back) puts its tracks on offer until the user withdraws them in the Record window. While they are, every UNTOUCHED BrokenNes2 instance
// (Bn2Plugin.Tracks.cs: never configured by the user or by a project, whatever default state or parameters FL hands it) recognises its track from the notes FL asks it to play
// (Record/RecTracks.cs) and plays that track's preset from the note on. That is what makes FL's "import the MIDI file, Replace each MIDI Out channel by BrokenNes2" work whatever order and
// whatever way FL creates the instances.
//
// Nothing of this allocates on the audio thread: the tracks' presets and one engine per track (a SnesSampler at FL's rate) are built here, on a worker; an instance that recognises its track
// takes that engine with one exchange, and the worker builds the next one. The same worker then settles the instance: it moves it to the tracks' emulator (Direct mode, PRE-SNES chip), where it
// is an ordinary preset instance (the project keeps it as one).
using BrokenNes2.Presets;
using BrokenNes2.Record;

namespace BrokenNes2;

/// <summary>A prebuilt engine and the rate it was built for: a SNES track's sampler, or (record-nes) a NES track's own copy of its preset with a register player of its own (a NES preset's player is
/// the instance's, never shared).</summary>
internal sealed record TrackEngine(SnesSampler? Sampler, int Rate)
{
    public LoadedPreset? Nes { get; init; }
}

/// <summary>One track on offer: its preset (loaded once, shared by every instance that plays it), the engine the next instance takes.</summary>
internal sealed class TrackSlot
{
    public required TrackInfo Info;
    public required RecordHeld Held;
    public required TrackExpression Expression;
    public LoadedPreset? Preset;
    public string? Problem;
    public TrackEngine? Ready;
    /// <summary>Instances playing it now (the window says so).</summary>
    public int Playing;
}

/// <summary>A recording on offer.</summary>
internal sealed record OfferedRecording(int EmulatorId, RecordHeld Held, TrackSlot[] Slots);

/// <summary>What is on offer now: every offered recording's tracks in one index (immutable once published).</summary>
internal sealed record TrackOfferSnapshot(OfferedRecording[] Recordings, TrackSlot[] Slots, TrackIndex Index, long Version);

internal static class TrackOffers
{
    private static readonly object gate = new();
    private static volatile TrackOfferSnapshot? current;
    private static readonly List<Bn2Plugin> instances = new();
    private static Thread? worker;
    private static long version;
    private static int tracksEmulator;

    private static volatile bool expressionFromRecording = Prefs.GetInt("TrackExpression", 1) == 1;

    /// <summary>Whether channels that play a track take its pitch bends and volume from the recording (by song position) rather than from FL (whose MIDI import loses controller 7 and
    /// reads bends at 2 semitones): the default for a channel when it takes a track; remembered.</summary>
    public static bool ExpressionFromRecording => expressionFromRecording;

    /// <summary>Sets it for the channels that play a track now and for those to come (UI thread).</summary>
    public static void SetExpression(bool on)
    {
        expressionFromRecording = on;
        Prefs.SetInt("TrackExpression", on ? 1 : 0);
        Bn2Plugin[] list;
        lock (gate) list = instances.ToArray();
        foreach (var p in list) p.SetTrackExpression(on);
    }

    /// <summary>The number of instances made when the last offer was made (only the self-test's deliberate bug "late instances ignored" reads it).</summary>
    public static int OfferedAfterInstance;

    /// <summary>What is on offer (null: nothing). Read on any thread.</summary>
    public static TrackOfferSnapshot? Current => current;

    /// <summary>Every engine on offer is built (tests wait for it).</summary>
    public static bool Ready { get { var s = current; return s != null && s.Slots.All(x => x.Preset == null || x.Ready != null); } }

    public static void Register(Bn2Plugin p) { lock (gate) instances.Add(p); }
    public static void Unregister(Bn2Plugin p) { lock (gate) instances.Remove(p); }

    /// <summary>Offers a recording's tracks (a worker or the UI thread): the presets are read and checked here; the engines are built by the worker. Replaces what the same emulator offered before.</summary>
    public static void Offer(int emulatorId, RecordHeld held)
    {
        var slots = new TrackSlot[held.Tr.Tracks.Count];
        int rate = EmulatorHub.HostRate;
        for (int k = 0; k < slots.Length; k++)
        {
            var t = held.Tr.Tracks[k];
            var info = new TrackInfo { Index = k, Name = t.Name, Channel = t.Channel, BendRange = t.BendRange, NoteCount = t.Notes.Count, PresetFile = t.PresetFile };
            var slot = new TrackSlot { Info = info, Held = held, Expression = TrackExpression.Of(t) };
            if (t.PresetFile == null) slot.Problem = t.PresetProblem ?? "the track has no preset";
            else
            {
                var read = PresetFile.Read(t.PresetFile);
                var play = read.Data == null ? null : PresetPlayable.Build(read.Data, out string? why);
                if (read.Data == null) slot.Problem = read.Problem;
                else if (play != null && play.CannotPlay != null) slot.Problem = play.CannotPlay;                               // (a NES DMC track: no engine plays the DMC yet)
                else if (play == null || (play.Snes == null && play.Console != Instruments.ConsoleId.Nes)) slot.Problem = "the preset does not build a SNES or NES engine";
                else slot.Preset = new LoadedPreset { Data = read.Data, File = t.PresetFile, Play = play };
            }
            slots[k] = slot;
        }
        var rec = new OfferedRecording(emulatorId, held, slots);
        OfferedAfterInstance = HostLog.InstancesMade;
        HostLog.Text(0, $"the {slots.Length} tracks of \"{held.Session.Name}\" (emulator #{emulatorId}) are on offer: " + string.Join(", ", slots.Select(s => $"{s.Info.Index + 1} ch {s.Info.Channel + 1} {s.Info.NoteCount} notes{(s.Problem != null ? " (" + s.Problem + ")" : "")}")));
        lock (gate)
        {
            var list = (current?.Recordings ?? []).Where(r => r.EmulatorId != emulatorId).Append(rec).ToArray();
            Publish(list);
            EnsureWorker();
        }
        _ = rate;
    }

    /// <summary>Takes a recording's tracks off offer (the window's button, a forgotten recording). Instances already playing a track keep playing it.</summary>
    public static void Withdraw(int emulatorId)
    {
        lock (gate)
        {
            var list = (current?.Recordings ?? []).Where(r => r.EmulatorId != emulatorId).ToArray();
            Publish(list);
        }
    }

    public static void WithdrawAll() { lock (gate) Publish([]); }

    private static void Publish(OfferedRecording[] list)
    {
        if (list.Length == 0) { current = null; return; }
        var slots = list.SelectMany(r => r.Slots).ToArray();
        var infos = new TrackInfo[slots.Length];
        var notes = new List<(int, double, int, int, int)>();
        int first = 0;
        for (int r = 0; r < list.Length; r++)
        {
            foreach (var s in list[r].Slots) { s.Info.Recording = r; infos[first + s.Info.Index] = s.Info; }
            notes.AddRange(TrackIndex.NotesOf(list[r].Held.Tr, first));
            first += list[r].Slots.Length;
        }
        current = new TrackOfferSnapshot(list, slots, new TrackIndex(infos, notes), Interlocked.Increment(ref version));
    }

    private static void EnsureWorker()
    {
        if (worker != null) return;
        worker = new Thread(Work) { IsBackground = true, Name = "BrokenNes2 track offer" };
        worker.Start();
    }

    /// <summary>The worker: engines for the tracks on offer (one waiting per track, at FL's rate), and the instances that recognised a track settled on the tracks' emulator.</summary>
    private static void Work()
    {
        while (true)
        {
            try
            {
                var snap = current;
                int rate = EmulatorHub.HostRate;
                if (snap != null)
                    foreach (var s in snap.Slots)
                    {
                        if (s.Preset is not { } lp) continue;
                        var r = Volatile.Read(ref s.Ready);
                        if (r != null && r.Rate == rate) continue;
                        if (lp.Play.Snes is { } spc) Volatile.Write(ref s.Ready, new TrackEngine(new SnesSampler(spc, rate), rate));
                        else if (lp.Play.Console == Instruments.ConsoleId.Nes)
                            Volatile.Write(ref s.Ready, new TrackEngine(null, rate) { Nes = new LoadedPreset { Data = lp.Data, File = lp.File, Play = lp.Play, Nes = new NesPresetPlayer(lp.Play.Instrument, rate, PresetNote.IsNesTrack(lp.Data)) } });
                    }
                Bn2Plugin[] list;
                lock (gate) list = instances.ToArray();
                foreach (var p in list) p.SettleIfWanted();
            }
            catch (Exception e) { BrokenNes.Fruity.Diag.Log("track offer: " + e.Message); }
            Thread.Sleep(15);
        }
    }

    /// <summary>The emulator the tracks' instances sit on: the one used before while it is still in preset mode, else the lowest free number (made explicit by the instance that asks). 0: all eight are in use.
    /// A NES recording's tracks have one of their own (PRE-NES), a SNES recording's theirs (PRE-SNES): <paramref name="nes"/>.</summary>
    public static int TracksEmulator(bool nes = false)
    {
        lock (EmulatorHub.Gate)
        {
            ref int mine = ref nes ? ref nesTracksEmulator : ref tracksEmulator;
            if (mine > 0 && EmulatorHub.Get(mine) is { PresetMode: true, AutoCreated: false }) return mine;
            for (int i = 1; i <= EmulatorHub.MaxEmulators; i++)
                if (EmulatorHub.Get(i) == null) { mine = i; return i; }
            return 0;
        }
    }

    private static int nesTracksEmulator;

    /// <summary>The slot of a track of a recording (by the recording's session id and the track's index), or null.</summary>
    public static TrackSlot? Find(ulong sessionId, int track)
    {
        var snap = current;
        if (snap == null) return null;
        foreach (var r in snap.Recordings) if (r.Held.Session.Id == sessionId && (uint)track < (uint)r.Slots.Length) return r.Slots[track];
        return null;
    }
}
