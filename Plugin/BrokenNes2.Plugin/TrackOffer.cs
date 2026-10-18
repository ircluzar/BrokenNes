// Round 8 follow-up (record, 2026-10-18): the tracks of a recording on offer to BrokenNes2 channels.
//
// A finished recording (and one a project brings back) puts its tracks on offer until the user withdraws them in the Record window (or the emulator that made it closes). While they are, every
// LINKED BrokenNes2 instance (Bn2Plugin.Relink.cs: joined by itself when made while tracks are on offer, whatever parameters FL pushes; or linked by hand) recognises its track from the notes FL
// asks it to play (Record/RecTracks.cs), or from its channel's name, and plays that track's preset from the note on. That is what makes FL's "import the MIDI file, Replace each MIDI Out channel by
// BrokenNes2" work whatever order and whatever way FL creates the instances; the Record window counts the channels that joined and the ones that found their track (Count, CounterText).
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
internal sealed record TrackOfferSnapshot(OfferedRecording[] Recordings, TrackSlot[] Slots, TrackIndex Index, long Version)
{
    /// <summary>The emulators whose recordings these are: their instances made the recording and never join it.</summary>
    public int[] SourceEmulators { get; init; } = [];

    /// <summary>Whether emulator <paramref name="id"/> made one of the recordings (any thread; allocation-free).</summary>
    public bool IsSource(int id) { foreach (int e in SourceEmulators) if (e == id) return true; return false; }
}

/// <summary>The Record window's count of the channels linked to the tracks on offer: joined (linked, found or waiting), found (playing a track), not linked; and one line per instance.</summary>
internal readonly record struct OfferCount(int Joined, int Found, int NotLinked, string[] Lines);

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

    /// <summary>The instances of the process (tests: nothing left alive from an earlier check).</summary>
    public static int Live { get { lock (gate) return instances.Count; } }

    public static void Register(Bn2Plugin p) { lock (gate) instances.Add(p); Changed(); }
    public static void Unregister(Bn2Plugin p) { lock (gate) instances.Remove(p); Changed(); }

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
        Bn2Plugin[] all;
        lock (gate)
        {
            var list = (current?.Recordings ?? []).Where(r => r.EmulatorId != emulatorId).Append(rec).ToArray();
            Publish(list);
            EnsureWorker();
            all = instances.ToArray();
        }
        HostLog.Text(0, $"the {slots.Length} tracks of \"{held.Session.Name}\" (emulator #{emulatorId}) are on offer: " + string.Join(", ", slots.Select(s => $"{s.Info.Index + 1} \"{s.Info.Name}\" ch {s.Info.Channel + 1} {s.Info.NoteCount} notes{(s.Problem != null ? " (" + s.Problem + ")" : "")}")));
        foreach (var p in all) { p.LogPresence(); p.OfferAppeared(); }          // (the log names every instance alive now; those still pristine join the tracks, the others show "Link to recording")
        _ = rate;
    }

    private static long membersVersion;

    /// <summary>Changes whenever an instance joins, leaves, finds its track or settles, or what is on offer changes: the editors repaint on it (any thread; allocation-free).</summary>
    public static long MembersVersion => Interlocked.Read(ref membersVersion);
    public static void Changed() => Interlocked.Increment(ref membersVersion);

    /// <summary>"Link all BrokenNes2 channels" (the Record window): every instance of the process that is not linked, plays no track and did not make the recording is linked by hand. Returns how many;
    /// <paramref name="already"/>: how many were linked or playing a track already.</summary>
    public static int LinkAll(out int already)
    {
        already = 0;
        var snap = current;
        if (snap == null) return 0;
        Bn2Plugin[] list;
        lock (gate) list = instances.ToArray();
        int n = 0;
        foreach (var p in list)
        {
            int role = p.OfferRole(snap);
            if (role is 3 or 4) { already++; continue; }
            if (role != 2) continue;
            if (RecMutants.On(RecMutants.LinkAllIgnored)) { n++; continue; }      // (the self-test's deliberate bug: counted as linked, nothing done)
            if (p.LinkByHand("Link all, in the Record window", fromButton: false) == null) n++;
        }
        HostLog.Text(0, $"Link all: {n} channel{(n == 1 ? "" : "s")} linked by hand, {already} linked or playing a track already");
        return n;
    }

    /// <summary>The channels and the tracks on offer (UI thread): joined, found, not linked, a line for each instance (the recording's own channels left out).</summary>
    public static OfferCount Count()
    {
        var snap = current;
        if (snap == null) return new OfferCount(0, 0, 0, []);
        Bn2Plugin[] list;
        lock (gate) list = instances.ToArray();
        int joined = 0, found = 0, notLinked = 0;
        var lines = new List<string>();
        foreach (var p in list.OrderBy(x => x.InstanceNo))
        {
            int role = p.OfferRole(snap);
            if (role == 4) { found++; joined++; }
            else if (role == 3) joined++;
            else if (role == 2) notLinked++;
            else continue;
            lines.Add(p.OfferLine(snap));
        }
        if (RecMutants.On(RecMutants.CounterCountsFoundOnly)) joined = found;      // (the self-test's deliberate bug: the r12 counter, which only moved once a channel had found its track)
        return new OfferCount(joined, found, notLinked, lines.ToArray());
    }

    /// <summary>"3 channels joined, 1 found its track; 2 not linked": what the Record window shows (and its offer button).</summary>
    public static string CounterText() => CounterText(Count());

    public static string CounterText(OfferCount c) =>
        $"{c.Joined} channel{(c.Joined == 1 ? "" : "s")} joined, {c.Found} found {(c.Found == 1 ? "its" : "their")} track" + (c.NotLinked > 0 ? $"; {c.NotLinked} not linked" : "");

    /// <summary>The track a channel's name names (FL names imported channels after the MIDI file's track names): the one whose name it is (case and spaces at the ends aside), else the longest
    /// track name it starts with followed by something that is not a letter or digit (" #2", " (2)"); null with the reason when none or more than one fit.</summary>
    public static (TrackSlot? Slot, string Why) FindByName(string channel)
    {
        var snap = current;
        if (snap == null) return (null, "no tracks are on offer");
        string c = channel.Trim();
        var exact = snap.Slots.Where(s => string.Equals(s.Info.Name.Trim(), c, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return (exact[0], "");
        if (exact.Count > 1) return (null, $"{exact.Count} tracks on offer have that name");
        var starts = snap.Slots.Where(s => s.Info.Name.Trim().Length > 0 && c.Length > s.Info.Name.Trim().Length && c.StartsWith(s.Info.Name.Trim(), StringComparison.OrdinalIgnoreCase) && !char.IsLetterOrDigit(c[s.Info.Name.Trim().Length])).ToList();
        if (starts.Count == 0) return (null, "no track on offer has that name");
        int longest = starts.Max(s => s.Info.Name.Trim().Length);
        var best = starts.Where(s => s.Info.Name.Trim().Length == longest).ToList();
        return best.Count == 1 ? (best[0], "") : (null, $"{best.Count} tracks on offer fit that name");
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
        Changed();
        HostLog.SetOffer(list.Length > 0);                // (the host log runs for every instance while tracks are on offer)
        if (list.Length == 0) { current = null; return; }
        var slots = list.SelectMany(r => r.Slots).ToArray();
        var infos = new TrackInfo[slots.Length];
        var notes = new List<(int, double, int, int, int)>();
        var vels = new Dictionary<(int Track, long Tick, int Key), int>();
        int first = 0;
        for (int r = 0; r < list.Length; r++)
        {
            foreach (var s in list[r].Slots) { s.Info.Recording = r; infos[first + s.Info.Index] = s.Info; }
            notes.AddRange(TrackIndex.NotesOf(list[r].Held.Tr, first));
            foreach (var kv in TrackIndex.VelocitiesOf(list[r].Held.Tr, first)) vels[kv.Key] = kv.Value;
            first += list[r].Slots.Length;
        }
        var index = new TrackIndex(infos, notes);
        index.SetVelocities(vels);                     // (FL's velocity tells apart tracks that play the same notes: a NES note on two duty tracks, record-nes)
        current = new TrackOfferSnapshot(list, slots, index, Interlocked.Increment(ref version)) { SourceEmulators = list.Select(r => r.EmulatorId).ToArray() };
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
