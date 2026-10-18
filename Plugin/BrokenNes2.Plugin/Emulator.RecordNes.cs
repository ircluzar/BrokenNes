// Round 8 (record-nes): a recording session of the emulator's NES game, the NES half of Emulator.Record.cs. The same window, options, states and threads as the SNES recording:
//
//   Record   arms a capture (Record/NesRecorder.cs) with its first memory blocks made here, on the UI thread; it begins between two frames (at once, when Instrument Runaway is switched off, or with
//            FL's Play), from the game chip's own state (APU_FIX.ProbeControl). Every write the game's CPU makes to $4000-$4017 then reaches it through the machine's record tap (NesAudioMachine.RecordTap,
//            the delegate made here), from the audio thread, without allocating; the end of every frame is marked.
//   Stop     ends it between two frames; the feeder thread transcribes it (Record/NesTranscribe.cs): the same RecordHeld as a SNES recording (its tracks seen through the SNES transcription's shape,
//            Record/NesAdapter.cs), so the window, the files, Save MIDI..., the presets, the tracks on offer and their matching work unchanged; the NES transcription itself rides beside it (RecordHeld.Nes).
//   Project  REC1 version 2 carries the NES session (RecSection.BuildNes); a reopened project transcribes it again.
// The live MIDI out of a NES game shares the record tap (Emulator.Live.cs: NesLive).
using BrokenNes2.Presets;
using BrokenNes2.Record;

namespace BrokenNes2;

public sealed partial class Emulator
{
    private volatile NesRecorder? nesRecorder;
    private NesGame? nesRecGame;
    private readonly byte[] nesLatch = new byte[0x18];
    // the one tap the NES machine calls for a recording and for the live MIDI out (made once, on the UI thread; the audio thread only sets it)
    private Action<ushort, byte, long>? nesApuTap;
    private NesRecorder? nesRecTarget;
    private NesLive? nesLiveTarget;
    private NesGame? nesTapped;
    private long nesTapWrites;
    private string nesRecChip = "";

    /// <summary>The NES capture in progress (tests).</summary>
    internal NesRecorder? NesRecorderForTests => nesRecorder;

    /// <summary>Tests: the writes the tap has seen since the recording began (an independent count beside the capture's).</summary>
    internal long? NesWritesForTests() => nesRecorder != null || recording?.Nes != null ? nesTapWrites : null;

    private void OnNesApuWrite(ushort address, byte value, long clock)
    {
        if (nesRecTarget is { } r) { nesTapWrites++; r.OnWrite(address, value, clock); }
        nesLiveTarget?.OnWrite(address, value, clock);
    }

    /// <summary>Points the machine's record tap at whatever listens now (between frames, under romLock). Allocation-free.</summary>
    private void RetapNes(NesGame? g)
    {
        if (nesTapped != null && !ReferenceEquals(nesTapped, g) && nesTapped.Nes is { } old && ReferenceEquals(old.RecordTap, nesApuTap)) old.RecordTap = null;
        nesTapped = null;
        if (g?.Nes is not { } m) return;
        bool want = nesRecTarget != null || nesLiveTarget != null;
        var t = want ? nesApuTap : null;
        if (!ReferenceEquals(m.RecordTap, t)) m.RecordTap = t;
        if (want) nesTapped = g;
    }

    /// <summary>Record on a NES game (StartRecording sends a NES emulator here). Null when armed, else why not.</summary>
    private string? StartNesRecording()
    {
        IGame? g;
        lock (romLock) { ApplyPending(); g = game; }
        if (g is not NesGame) return "the NES game is still loading: try again in a moment";
        var rec = new NesRecorder();
        rec.Arm();
        recName = DefaultRecordingName();
        recGameName = g is NesGame { IsSongTest: true } && song is { } sg ? "MIDI test: " + sg.FileName : RomPath != null ? Path.GetFileName(RomPath) : "VRUN (built-in)";
        recSha1 = SourceGameSha1();
        recCreated = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        recId = (ulong)Random.Shared.NextInt64();
        nesApuTap ??= OnNesApuWrite;                      // (the delegate is made here, on the UI thread: the audio thread only sets it)
        nesRecChip = g.Nes?.ApuCoreId ?? "";
        Volatile.Write(ref recStartWanted, RecordUi.StartWithTransport ? 0 : 1);
        Volatile.Write(ref recStopWanted, 0);
        nesRecorder = rec;
        RecordUi.State = RecordPanel.Armed;
        RecordUi.Status = RecordUi.StartWithTransport ? "armed: the recording starts when FL's transport starts (Play)" : Runaway ? "armed: the recording starts when Instrument Runaway is switched off and the game runs" : "starting ...";
        new Thread(() => NesFeeder(rec)) { IsBackground = true, Name = "BrokenNes2 NES recording" }.Start();
        return null;
    }

    /// <summary>Between two frames, under romLock: an armed NES recording begins when the game runs, a running one ends when asked or when its game was replaced. Allocation-free.</summary>
    private void NesRecordService(IGame? g)
    {
        var rec = nesRecorder;
        if (rec == null) return;
        if (RecordUi.State == RecordPanel.Armed)
        {
            if (Volatile.Read(ref recStopWanted) != 0) { rec.Halt(RecEnd.Stop); return; }
            if (Volatile.Read(ref recStartWanted) == 0 || Runaway || g is not NesGame ng || ng.Nes is not { } m || SongHolds(g)) return;
            uint written = m.CopyApuLatch(nesLatch);
            rec.Begin(m.Nes, nesLatch, written, nesRecChip);          // (the chip's name read on the UI thread: a type's name may allocate)
            nesTapWrites = 0;
            nesRecTarget = rec;
            nesRecGame = ng;
            RetapNes(ng);
            RecordUi.State = RecordPanel.Recording;
            return;
        }
        if (RecordUi.State != RecordPanel.Recording) return;
        if (!ReferenceEquals(g, nesRecGame)) { rec.Halt(RecEnd.GameReplaced); NesDetach(); return; }
        if (Volatile.Read(ref recStopWanted) != 0) { rec.Halt((RecEnd)Volatile.Read(ref recStopWhy)); NesDetach(); return; }
        if (rec.Halted) { NesDetach(); return; }
    }

    /// <summary>After a frame of the NES game: its end goes into the recording.</summary>
    private void NesRecordClock(IGame g)
    {
        if (nesRecorder is { Recording: true } rec && ReferenceEquals(g, nesRecGame) && g.Nes is { } m) rec.FrameEnd(m.ApuClock);
    }

    private void NesDetach()
    {
        nesRecTarget = null;
        RetapNes(nesLiveTarget != null ? game as NesGame : null);
    }

    private void NesFeeder(NesRecorder rec)
    {
        try
        {
            while (true)
            {
                rec.Feed();
                if (Monitor.TryEnter(romLock, 5))
                {
                    try { NesRecordService(game); }
                    finally { Monitor.Exit(romLock); }
                }
                if (rec.Halted) break;
                if (RecordUi.State == RecordPanel.Recording) RecordUi.Status = $"recording: {rec.Seconds:0.0} s, {rec.Count:N0} chip writes and frames, {rec.DmcSamples} DMC samples, {rec.MemoryBytes / 1048576.0:0.0} MB{(rec.Dropped > 0 ? $", {rec.Dropped:N0} writes LOST (the capture ran out of memory blocks)" : "")}";
                Thread.Sleep(10);
            }
            bool begun;
            lock (romLock)
            {
                if (nesRecTarget == rec) NesDetach();
                begun = nesRecGame != null;
                nesRecGame = null;
            }
            nesRecorder = null;
            if (!begun)
            {
                RecordUi.State = RecordPanel.Idle; RecordUi.Status = "";
                RecordUi.SetResult("Stopped before the recording began (the game did not run): nothing was recorded", false);
                return;
            }
            RecordUi.State = RecordPanel.Finishing;
            RecordUi.Status = "transcribing ...";
            var s = rec.Finish(recName, recGameName, recSha1, recCreated, recId);
            if (s.Count == 0)
            {
                RecordUi.State = RecordPanel.Idle; RecordUi.Status = "";
                RecordUi.SetResult($"The recording ran {s.Seconds:0.0} s but the game wrote nothing to its sound chip: nothing to transcribe", false);
                return;
            }
            TranscribeNes(s, addPresets: true);
        }
        catch (Exception e)
        {
            BrokenNes.Fruity.Diag.Log("NES recording failed: " + e);
            nesRecorder = null;
            RecordUi.State = recording != null ? RecordPanel.Done : RecordPanel.Idle; RecordUi.Status = "";
            RecordUi.SetResult("The recording failed: " + e.Message.TrimEnd('.'), false);
        }
    }

    /// <summary>Transcribes a NES session and holds the result like a SNES one (Emulator.Record.cs: Transcribe). Worker thread.</summary>
    private void TranscribeNes(NesSession s, bool addPresets)
    {
        RecordUi.State = RecordPanel.Finishing;
        RecordUi.Status = $"transcribing {s.Name} ...";
        var ntr = NesTranscribe.Run(s, new NesOptions { Name = s.Name, FlTempo = EmulatorHub.FlTempo });
        var tr = NesAdapter.AsTranscription(ntr);
        string folder = Path.Combine(Path.GetTempPath(), "BrokenNes2", "Recordings", PresetStore.SafeFileName(s.Name) + "-" + (s.Id & 0xFFFFFFFF).ToString("x8"));
        Directory.CreateDirectory(folder);
        string midi = Path.Combine(folder, PresetStore.SafeFileName(s.Name) + ".mid");
        File.WriteAllBytes(midi, tr.Midi);
        var paths = new string[tr.Tracks.Count];
        for (int k = 0; k < paths.Length; k++) { paths[k] = Path.Combine(folder, PresetStore.SafeFileName(tr.Tracks[k].Name) + ".mid"); File.WriteAllBytes(paths[k], tr.TrackMidi(k)); }
        var ids = new string[tr.Tracks.Count];
        string note = addPresets ? AddPresets(tr, ids) : "the presets are not in the library yet: press Add presets";
        recSection = RecSection.BuildNes(s);
        recCarried = null;
        recording = new RecordHeld(tr.Session, tr, folder, midi, paths, ids, note, Interlocked.Increment(ref recGeneration)) { Nes = ntr };
        if (tr.Tracks.Count > 0 && !RecMutants.On(RecMutants.DealIgnored)) TrackOffers.Offer(Id, recording);
        RecordUi.State = RecordPanel.Done;
        RecordUi.Status = "";
        if (ntr.Notes.Count == 0) RecordUi.SetResult($"Recorded {s.Seconds:0.0} s, but no note: {(ntr.Facts.Count > 1 ? ntr.Facts[1] : "the game sounded none")}", false);
        else RecordUi.SetResult($"Recorded {s.Seconds:0.0} s: {tr.Tracks.Count} track{(tr.Tracks.Count == 1 ? "" : "s")}, {ntr.Notes.Count} notes. Drag the MIDI file into FL, or Save MIDI... " + note, true);
    }
}
