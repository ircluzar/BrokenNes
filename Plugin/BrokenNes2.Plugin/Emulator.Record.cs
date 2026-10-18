// Round 8 (record): a recording session of the emulator's SNES game, turned into a MIDI file and presets for FL Studio.
//
//   Record   arms a capture (Record/RecSession.cs: SpcRecorder) with its first memory blocks made here, on the UI thread. It begins between two frames of the game: at once when the game runs,
//            when Instrument Runaway is switched off when it is frozen, or when FL's transport starts (an option). From then on every S-DSP write of the game goes to it, from the audio thread,
//            without allocating (the recorder's delegate is made here; setting it between frames costs nothing).
//   Stop     (or FL's transport stopping, an option; or the memory cap; or the game being replaced) ends it between two frames. A feeder thread, one per recording, keeps the spare memory
//            blocks ready while it runs and, once it ended, builds the session, transcribes it (Record/RecTranscribe.cs), writes the MIDI files (the whole song, and each track alone) to a
//            temporary folder, and adds the presets to the library (Presets\<game>\Recordings\<recording>, once: the same bytes are not added twice).
//   Project  the session rides in the project as section REC1 (written by the emulator's first instance); a reopened project transcribes it again, to the same bytes.
//   Deal     "Give the tracks to the next new instances": each BrokenNes2 instance created next (FL's channel Replace, or its MIDI import) takes the next track's preset on an emulator of its own
//            in Direct mode on the PRE-SNES chip (Bn2Plugin.Record.cs).
// Nothing is silent: every refusal, loss, cap and fallback is a line of the window (RecordPanel) and of the transcription's facts.
using BrokenNes2.Presets;
using BrokenNes2.Record;
using BrokenNes2.Ui;

namespace BrokenNes2;

/// <summary>What the Record window shows between paints, and what the threads set.</summary>
internal sealed class RecordPanel
{
    public const int Idle = 0, Armed = 1, Recording = 2, Finishing = 3, Done = 4;
    public volatile int State;
    /// <summary>What is going on now ("armed: ...", "transcribing ..."), for the state box.</summary>
    public volatile string Status = "";
    public volatile string Result = "";
    public volatile bool ResultGood;
    public int ResultStamp;
    /// <summary>Options: start when FL's transport starts (else at once, or when Runaway is switched off); stop when it stops; lanes (overlapping notes of one instrument on extra tracks).</summary>
    public volatile bool StartWithTransport, StopWithTransport = true, Lanes;

    public void SetResult(string text, bool good)
    {
        Result = text; ResultGood = good;
        Interlocked.Increment(ref ResultStamp);
    }
}

/// <summary>A finished recording as an emulator holds it: the session, its transcription, the files written for FL, the presets in the library.</summary>
internal sealed record RecordHeld(RecSession Session, Transcription Tr, string Folder, string MidiPath, string[] TrackPaths, string[] PresetIds, string PresetNote, long Generation)
{
    public string MidiName => Path.GetFileName(MidiPath);
}

/// <summary>"Give the tracks to the next new instances": one deal for the process. Each BrokenNes2 instance created while it is armed takes the next track (its preset) on the deal's emulator.</summary>
internal static class RecordDeal
{
    private static readonly object gate = new();
    private static List<(string Name, byte[] Preset)> items = new();
    private static int next, emulatorId;
    private static long expires;
    public const int ArmedMinutes = 10;

    public static bool Armed { get { lock (gate) return next < items.Count && Environment.TickCount64 < expires; } }
    public static (int Next, int Count, int Emulator) State { get { lock (gate) return (next, items.Count, emulatorId); } }

    /// <summary>Arms the deal with a recording's tracks (lanes included: they play their instrument's preset). Null when armed, else why not.</summary>
    public static string? Arm(RecordHeld held)
    {
        if (RecMutants.On(RecMutants.DealIgnored)) return null;                 // (the self-test's deliberate bug: says it is armed, deals nothing)
        var list = new List<(string, byte[])>();
        foreach (var t in held.Tr.Tracks) if (t.PresetFile != null) list.Add((t.Name, t.PresetFile));
        if (list.Count == 0) return "the recording has no track with a preset";
        int free = 0;
        for (int i = 1; i <= EmulatorHub.MaxEmulators; i++) if (EmulatorHub.Get(i) == null) { free = i; break; }
        if (free == 0) return $"all {EmulatorHub.MaxEmulators} emulators are in use: the tracks need one of their own (in Direct mode)";
        lock (gate)
        {
            items = list; next = 0; emulatorId = free;
            expires = Environment.TickCount64 + ArmedMinutes * 60_000L;
        }
        return null;
    }

    public static void Disarm() { lock (gate) { items = new(); next = 0; } }

    /// <summary>The next track for a new instance, and the emulator it plays on. False when the deal is not armed (or has expired, or every track was given).</summary>
    public static bool TryTake(out string name, out byte[] preset, out int emulator, out int index, out int count)
    {
        lock (gate)
        {
            name = ""; preset = []; emulator = emulatorId; index = next; count = items.Count;
            if (next >= items.Count || Environment.TickCount64 >= expires) return false;
            (name, preset) = items[next++];
            return true;
        }
    }
}

public sealed partial class Emulator
{
    internal RecordPanel RecordUi { get; } = new();
    private volatile SpcRecorder? recorder;
    private Action<byte, byte, long>? recorderTap;
    private SessionGame? recGame;
    private string recName = "", recGameName = "", recSha1 = "";
    private long recCreated;
    private ulong recId;
    private int recStartWanted, recStopWanted, recStopWhy;
    private volatile RecordHeld? recording;
    private long recGeneration;
    private volatile byte[]? recSection, recCarried;
    private volatile string? recProblem;

    /// <summary>The last finished recording (null: none yet).</summary>
    internal RecordHeld? Recording => recording;
    /// <summary>The capture in progress (armed or recording), null when none.</summary>
    internal SpcRecorder? Recorder => recorder;
    /// <summary>Why a project's recording was not used (null: nothing to say).</summary>
    internal string? RecordProblem => recProblem;

    /// <summary>Record: arms a capture on this emulator's SNES game. Null when armed (or recording), else why not, in words for the window.</summary>
    internal string? StartRecording()
    {
        if (RecordUi.State is RecordPanel.Armed or RecordPanel.Recording) return "already recording: press Stop first";
        if (RecordUi.State == RecordPanel.Finishing) return "the last recording is still being transcribed";
        if (Mode != 1) return "recording needs ROM mode and a SNES game: load one in the Game list";
        if (Console != Cx.Snes) return $"recording works on SNES games for now (this emulator runs the {Cx.Names[Console]}): the other consoles come later";
        IGame? g;
        lock (romLock) { ApplyPending(); g = game; }
        if (g is not SessionGame sg || sg.Console != Cx.Snes) return "the SNES game is still loading: try again in a moment";
        if (sg.SpcUnit == null) return "this SNES game's sound unit cannot be listened to";
        var rec = new SpcRecorder();
        rec.Arm();
        recName = DefaultRecordingName();
        recGameName = Path.GetFileName(RomPath ?? "");
        recSha1 = SourceGameSha1();
        recCreated = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        recId = (ulong)Random.Shared.NextInt64();
        recorderTap = rec.OnWrite;                        // (the delegate is made here, on the UI thread: the audio thread only sets it)
        Volatile.Write(ref recStartWanted, RecordUi.StartWithTransport ? 0 : 1);
        Volatile.Write(ref recStopWanted, 0);
        recorder = rec;
        RecordUi.State = RecordPanel.Armed;
        RecordUi.Status = RecordUi.StartWithTransport ? "armed: the recording starts when FL's transport starts (Play)" : Runaway ? "armed: the recording starts when Instrument Runaway is switched off and the game runs" : "starting ...";
        new Thread(() => Feeder(rec)) { IsBackground = true, Name = "BrokenNes2 recording" }.Start();
        return null;
    }

    /// <summary>Stop: the recording ends between two frames; the feeder thread then transcribes it.</summary>
    internal void StopRecording()
    {
        if (recorder == null) return;
        Volatile.Write(ref recStopWhy, (int)RecEnd.Stop);
        Volatile.Write(ref recStopWanted, 1);
    }

    private string DefaultRecordingName()
    {
        string title = PresetStore.GameTitle(Path.GetFileName(RomPath ?? "")) is { Length: > 0 } t ? t : "SNES";
        if (title.Length > 24) title = title[..24].TrimEnd();
        return $"{title} {DateTime.Now:HH.mm.ss}";
    }

    /// <summary>FL's transport (flags only: any thread, no allocation). With the options on, a start begins an armed recording and a stop ends a running one.</summary>
    private void RecordTransport(bool playing, int before)
    {
        if (recorder == null) return;
        if (playing && before != 1 && RecordUi.StartWithTransport) Volatile.Write(ref recStartWanted, 1);
        if (!playing && before == 1 && RecordUi.StopWithTransport && RecordUi.State == RecordPanel.Recording) { Volatile.Write(ref recStopWhy, (int)RecEnd.Transport); Volatile.Write(ref recStopWanted, 1); }
    }

    /// <summary>Between two frames, under romLock (the audio thread, the editor's picture pump, or the feeder): an armed recording begins when the game runs, a running one ends when asked, or when its game was replaced. Allocation-free.</summary>
    private void RecordService(IGame? g)
    {
        var rec = recorder;
        if (rec == null) return;
        if (RecordUi.State == RecordPanel.Armed)
        {
            if (Volatile.Read(ref recStopWanted) != 0) { rec.Halt(RecEnd.Stop); return; }
            if (Volatile.Read(ref recStartWanted) == 0 || Runaway || g is not SessionGame sg || sg.SpcUnit is not { } spc || sg.SpcLog is not { } log) return;
            rec.Begin(spc.Aram, spc.Dsp.Regs, spc.SamplesProduced);
            log.Recorder = recorderTap;
            recGame = sg;
            RecordUi.State = RecordPanel.Recording;
            return;
        }
        if (RecordUi.State != RecordPanel.Recording) return;
        if (!ReferenceEquals(g, recGame)) { rec.Halt(RecEnd.GameReplaced); Detach(); return; }
        if (Volatile.Read(ref recStopWanted) != 0) { rec.Halt((RecEnd)Volatile.Read(ref recStopWhy)); Detach(); return; }
        if (rec.Halted) { Detach(); return; }          // (the memory cap, inside a frame)
    }

    /// <summary>After a frame: the recording's clock moves on even when the driver wrote nothing.</summary>
    private void RecordClock(IGame g)
    {
        if (recorder is { Recording: true } rec && ReferenceEquals(g, recGame) && recGame!.SpcUnit is { } spc) rec.Clock(spc.SamplesProduced);
    }

    private void Detach()
    {
        if (recGame?.SpcLog is { } log && ReferenceEquals(log.Recorder, recorderTap)) log.Recorder = null;
    }

    /// <summary>One thread per recording: keeps the memory blocks ahead of the capture, applies a start or a stop when no audio is pulled (FL stopped, the editor closed), and, once the recording ended,
    /// builds the session and transcribes it.</summary>
    private void Feeder(SpcRecorder rec)
    {
        try
        {
            while (true)
            {
                rec.Feed();
                if (Monitor.TryEnter(romLock, 5))
                {
                    try { RecordService(game); }
                    finally { Monitor.Exit(romLock); }
                }
                if (rec.Halted) break;
                if (RecordUi.State == RecordPanel.Recording) RecordUi.Status = $"recording: {rec.Samples / 32000.0:0.0} s, {rec.Count:N0} chip writes, {rec.Instruments} instruments, {rec.MemoryBytes / 1048576.0:0.0} MB{(rec.Dropped > 0 ? $", {rec.Dropped:N0} writes LOST (the capture ran out of memory blocks)" : "")}";
                Thread.Sleep(10);
            }
            byte[]? endAram = null, endRegs = null;
            bool begun;
            lock (romLock)
            {
                Detach();
                begun = recGame != null;
                if (recGame != null && ReferenceEquals(game, recGame) && recGame.SpcUnit is { } spc) { endAram = (byte[])spc.Aram.Clone(); endRegs = (byte[])spc.Dsp.Regs.Clone(); }
                recGame = null;
            }
            recorder = null;
            if (!begun)
            {
                RecordUi.State = RecordPanel.Idle; RecordUi.Status = "";
                RecordUi.SetResult("Stopped before the recording began (the game did not run): nothing was recorded", false);
                return;
            }
            RecordUi.State = RecordPanel.Finishing;
            RecordUi.Status = "transcribing ...";
            var s = rec.Finish(endAram, endRegs, recName, recGameName, recSha1, recCreated, recId);
            if (s.Count == 0)
            {
                RecordUi.State = RecordPanel.Idle; RecordUi.Status = "";
                RecordUi.SetResult($"The recording ran {s.Seconds:0.0} s but the game wrote nothing to its sound chip: nothing to transcribe", false);
                return;
            }
            Transcribe(s, addPresets: true);
        }
        catch (Exception e)
        {
            BrokenNes.Fruity.Diag.Log("recording failed: " + e);
            recorder = null;
            RecordUi.State = recording != null ? RecordPanel.Done : RecordPanel.Idle; RecordUi.Status = "";
            RecordUi.SetResult("The recording failed: " + e.Message.TrimEnd('.'), false);
        }
    }

    /// <summary>Transcribes a session and holds the result: the MIDI files in a temporary folder, the presets in the library (when asked), the project section. Worker thread.</summary>
    private void Transcribe(RecSession s, bool addPresets)
    {
        RecordUi.State = RecordPanel.Finishing;
        RecordUi.Status = $"transcribing {s.Name} ...";
        var o = new RecOptions { Name = s.Name, FlTempo = EmulatorHub.FlTempo, Lanes = RecordUi.Lanes };
        var tr = RecTranscribe.Run(s, o);
        string folder = Path.Combine(Path.GetTempPath(), "BrokenNes2", "Recordings", PresetStore.SafeFileName(s.Name) + "-" + (s.Id & 0xFFFFFFFF).ToString("x8"));
        Directory.CreateDirectory(folder);
        string midi = Path.Combine(folder, PresetStore.SafeFileName(s.Name) + ".mid");
        File.WriteAllBytes(midi, tr.Midi);
        var paths = new string[tr.Tracks.Count];
        for (int k = 0; k < paths.Length; k++) { paths[k] = Path.Combine(folder, PresetStore.SafeFileName(tr.Tracks[k].Name) + ".mid"); File.WriteAllBytes(paths[k], tr.TrackMidi(k)); }
        var ids = new string[tr.Tracks.Count];
        string note = "";
        if (addPresets) note = AddPresets(tr, ids);
        else note = "the presets are not in the library yet: press Add presets";
        recSection = RecSection.Build(s);
        recCarried = null;
        recording = new RecordHeld(s, tr, folder, midi, paths, ids, note, Interlocked.Increment(ref recGeneration));
        RecordUi.State = RecordPanel.Done;
        RecordUi.Status = "";
        if (tr.Notes.Count == 0) RecordUi.SetResult($"Recorded {s.Seconds:0.0} s, but no note: {(tr.Facts.Count > 1 ? tr.Facts[1] : "the game started none")}", false);
        else RecordUi.SetResult($"Recorded {s.Seconds:0.0} s: {tr.Tracks.Count} track{(tr.Tracks.Count == 1 ? "" : "s")}, {tr.Notes.Count} notes. Drag the MIDI file into FL, or Save MIDI... " + note, true);
    }

    /// <summary>The recording's presets into the library (Presets\&lt;game&gt;\Recordings\&lt;recording&gt;): one per instrument; a file with the same bytes is not added twice. Returns the line for the window.</summary>
    private static string AddPresets(Transcription tr, string[] ids)
    {
        int added = 0, had = 0;
        var problems = new List<string>();
        var store = new PresetStore(PresetLibrary.FolderPath());
        foreach (var t in tr.Tracks)
        {
            if (t.PresetFile == null || t.Preset == null) { if (t.Lane == 0 && t.PresetProblem != null) problems.Add(t.PresetProblem); continue; }
            if (t.Lane > 0) { int owner = tr.Tracks.FindIndex(x => x.Version == t.Version && x.Lane == 0); ids[t.Index] = owner >= 0 ? ids[owner] : ""; continue; }
            var r = store.AddFile(t.PresetFile, t.Preset, "Recordings", tr.Session.Name);
            if (!r.Ok) { problems.Add(r.Message); continue; }
            ids[t.Index] = r.Id ?? "";
            if (r.Message.Contains("already")) had++; else added++;
        }
        PresetLibrary.Instance.Rescan();
        string where = $"Presets\\{PresetStore.CategoryFolder(tr.Session.Game)}\\Recordings\\{PresetStore.SafeFileName(tr.Session.Name)}";
        return (added + had > 0 ? $"{added + had} preset{(added + had == 1 ? "" : "s")} in the library ({where}{(had > 0 ? $", {had} already there" : "")})" : "no preset could be made")
            + (problems.Count > 0 ? "; not kept: " + string.Join("; ", problems.Take(2)) : "");
    }

    /// <summary>Add presets (a project's recording does not add them by itself): the held recording's presets into the library.</summary>
    internal string AddRecordingPresets()
    {
        var held = recording;
        if (held == null) return "no recording";
        var ids = (string[])held.PresetIds.Clone();
        string note = AddPresets(held.Tr, ids);
        recording = held with { PresetIds = ids, PresetNote = note };
        return note;
    }

    /// <summary>The held recording transcribed again (the Lanes option changed). Null when started, else why not.</summary>
    internal string? Retranscribe()
    {
        var held = recording;
        if (held == null) return "no recording to transcribe again";
        if (RecordUi.State is RecordPanel.Armed or RecordPanel.Recording or RecordPanel.Finishing) return "busy";
        RecordUi.State = RecordPanel.Finishing;
        Task.Run(() =>
        {
            try { Transcribe(held.Session, addPresets: false); var h = recording; if (h != null) recording = h with { PresetIds = held.PresetIds, PresetNote = held.PresetNote }; }
            catch (Exception e) { RecordUi.State = RecordPanel.Done; RecordUi.SetResult("Transcribing again failed: " + e.Message, false); }
        });
        return null;
    }

    /// <summary>Save MIDI...: the whole recording's MIDI file at <paramref name="path"/>. The words for the window.</summary>
    internal (bool Ok, string Message) SaveRecordingMidi(string path)
    {
        var held = recording;
        if (held == null) return (false, "Nothing to save: record first");
        try
        {
            if (Path.GetExtension(path).Length == 0) path += ".mid";
            var bytes = RecMutants.On(RecMutants.SaveWritesHalf) ? held.Tr.Midi[..(held.Tr.Midi.Length / 2)] : held.Tr.Midi;      // (the self-test's deliberate bug)
            string tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
            return (true, $"Saved {Path.GetFileName(path)} ({held.Tr.Midi.Length:N0} bytes, {held.Tr.Tracks.Count} tracks)");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return (false, "Could not save: " + e.Message); }
    }

    /// <summary>Give the tracks to the next new instances. The words for the window.</summary>
    internal (bool Ok, string Message) DealTracks()
    {
        var held = recording;
        if (held == null) return (false, "Record first: the tracks come from a recording");
        if (RecordDeal.Arm(held) is { } why) return (false, "Cannot give the tracks: " + why);
        var (_, count, emu) = RecordDeal.State;
        return (true, $"The next {count} BrokenNes2 instances you add take the tracks in order (track 1 first), on emulator #{emu} in Direct mode on the PRE-SNES chip: in FL, replace each imported channel by BrokenNes2 in the order of the tracks. Armed for {RecordDeal.ArmedMinutes} minutes.");
    }

    // ---- the project (REC1) ----

    /// <summary>The section the project keeps (the emulator's first instance writes it): the held recording, or the section a project brought that this build could not read (kept as it was, so nothing is lost).</summary>
    internal byte[]? CaptureRecordSection(Bn2Plugin who)
    {
        lock (EmulatorHub.Gate) if (members.Count == 0 || !ReferenceEquals(members[0], who)) return null;
        return recSection ?? recCarried;
    }

    /// <summary>Tests: the emulator holds no recording (the next project's or test's section is taken).</summary>
    internal void ForgetRecordingForTests()
    {
        if (RecordUi.State is RecordPanel.Armed or RecordPanel.Recording or RecordPanel.Finishing) return;
        recording = null; recSection = null; recCarried = null; recProblem = null;
        RecordUi.State = RecordPanel.Idle; RecordUi.Status = ""; RecordUi.SetResult("", true);
    }

    /// <summary>A project's REC1: read and transcribed again on a worker (its presets are not added to the library by themselves). A damaged one is refused with the reason; a newer one is kept as it was.</summary>
    internal void AdoptRecord(byte[] section)
    {
        if (recording != null || RecordUi.State != RecordPanel.Idle) return;
        RecordUi.State = RecordPanel.Finishing;
        RecordUi.Status = "reading the project's recording ...";
        Task.Run(() =>
        {
            try
            {
                var s = RecSection.Read(section, out var why);
                if (s == null)
                {
                    recProblem = "The project's recording was not used: " + why;
                    if (why != null && why.StartsWith("made by a newer")) recCarried = section;      // (written back as it came)
                    RecordUi.State = RecordPanel.Idle; RecordUi.Status = "";
                    RecordUi.SetResult(recProblem, false);
                    return;
                }
                recProblem = null;
                Transcribe(s, addPresets: false);
            }
            catch (Exception e) { RecordUi.State = RecordPanel.Idle; RecordUi.Status = ""; recProblem = "The project's recording was not used: " + e.Message; RecordUi.SetResult(recProblem, false); }
        });
    }
}
