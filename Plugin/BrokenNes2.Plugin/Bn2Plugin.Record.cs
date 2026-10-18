// Round 8 (record): the instance's side of a recording. FL's tempo (FPE_Tempo) for a recording with no tempo of its own; the deal (an instance created while "Give the tracks to the next new
// instances" is armed takes the next track's preset on the deal's emulator, Direct mode, PRE-SNES chip); a probe of the instance's own S-DSP writes (the self-test replays a recording's MIDI file
// through presets and compares, per note, the registers written with the game's); live MIDI out's way to FL (MIDIOut_Delayed, FHD_ActivateMIDI); the test hooks 640-669 and 680-699
// (the editor's are 670-679, Bn2Editor.Record.cs).
using BrokenNes.Fruity;
using BrokenNes2.Record;

namespace BrokenNes2;

/// <summary>Every S-DSP write the instance's sampler makes, with the sampler's sample clock, and a mark when an FL voice is given an S-DSP voice: the self-test's replay probe. Preallocated (the audio thread fills it without allocating).</summary>
internal sealed class ReplayProbe
{
    public readonly long[] Time;
    public readonly int[] Code;
    public int Count;
    public long Lost;
    public readonly Action<int, byte> Tap;
    private SnesSampler? sampler;

    public ReplayProbe(int capacity) { Time = new long[capacity]; Code = new int[capacity]; Tap = OnWrite; }

    /// <summary>Taps a sampler (the audio thread, when the instance makes or takes one).</summary>
    public void Bind(SnesSampler s)
    {
        if (ReferenceEquals(sampler, s)) return;
        sampler = s;
        s.Dsp.WriteTap = Tap;
    }

    private void OnWrite(int addr, byte value) => Add(sampler?.SamplesRun ?? 0, addr << 8 | value);

    /// <summary>An FL voice (its handle's low 16 bits) starts on S-DSP voice <paramref name="voice"/>.</summary>
    public void Mark(nint handle, int voice) => Add(sampler?.SamplesRun ?? 0, 1 << 24 | voice << 16 | (int)(handle & 0xFFFF));

    private void Add(long t, int code)
    {
        if (Count >= Time.Length) { Lost++; return; }
        Time[Count] = t; Code[Count] = code; Count++;
    }
}

public sealed unsafe partial class Bn2Plugin
{
    private volatile ReplayProbe? replayProbe;
    private static string lastRecordResult = "";
    /// <summary>The track this instance took from a deal (its name), or null.</summary>
    internal string? DealtTrack { get; private set; }
    private int dealtIndex = -1;

    public override int ProcessEvent(int id, int value, int flags)
    {
        // FL's tempo (a float in Value): a recording with no tempo of its own is written at it, so its notes keep their real times in FL
        if (id == Fpe.Tempo) EmulatorHub.FlTempo = BitConverter.Int32BitsToSingle(value);
        return 0;
    }

    /// <summary>A live MIDI out message to FL (the audio thread): MIDIOut_Delayed, so it is sent when FL's MIDI clock reaches the mixer's (the block it belongs to).</summary>
    internal void SendMidiOut(uint msg) => Host.MidiOutDelayed(HostTag, (nint)msg);

    /// <summary>Live MIDI out on, from this instance's editor (UI thread): FL is asked to enable its MIDI output (FHD_ActivateMIDI). Null when on, else why not.</summary>
    internal string? StartLiveMidi()
    {
        var emu = assignment.Emulator;
        if (emu == null) return "this instance has no emulator";
        var why = emu.StartLive();
        if (why == null) Host.Dispatcher(HostTag, Fhd.ActivateMidi, 0, 0);
        return why;
    }

    /// <summary>A new instance while a deal is armed: it takes the next track's preset on the deal's emulator (Direct mode, PRE-SNES). Called once, when the instance is made.</summary>
    private void TakeDeal()
    {
        if (!RecordDeal.TryTake(out string name, out byte[] preset, out int emuId, out int index, out int count)) return;
        Volatile.Write(ref Values[P.Emulator], emuId);
        EmulatorHub.Resolve(this);
        var emu = Emulator;
        if (emu == null) { Diag.Log($"deal: track {index + 1} has no emulator"); return; }
        if (emu.Mode != 0) emu.SetMode(0);
        if (emu.EnterPresetMode("SNES") is { } why) { presetNote = $"Track {index + 1} of the recording was not loaded: {why}"; return; }
        if (AdoptPreset(preset, null, out _) is { } why2) { presetNote = $"Track {index + 1} of the recording was not loaded: {why2}"; return; }
        DealtTrack = name;
        dealtIndex = index;
        lastRecordResult = $"This instance plays track {index + 1} of {count}: {name}";
    }

    /// <summary>Hooks of the recording, for the self-test (Fpd.TestBase + n, n from 640). See the list in the code.</summary>
    private nint RecordTestHook(int n, nint index, nint value)
    {
        var emu = assignment.Emulator;
        switch (n)
        {
            case 640: { var why = emu == null ? "no emulator" : emu.StartRecording(); lastRecordResult = why ?? "armed"; return why == null ? 0 : 1; }     // Record (0 armed, 1 refused: the words in 643 field 8)
            case 641: emu?.StopRecording(); return 0;                                                                                                   // Stop
            case 642: return emu?.RecordUi.State ?? -1;                                                                                                // 0 idle, 1 armed, 2 recording, 3 transcribing, 4 done
            case 643:                                                                                                                                   // copy a text: field = index >> 16 (0 status, 1 result, 2 facts, 3 MIDI path, 4 folder, 5 track names, 6 preset note, 7 project problem, 8 last hook result, 9 track paths), max = index & 0xFFFF
            {
                var held = emu?.Recording;
                int field = (int)(index >> 16), max = (int)(index & 0xFFFF);
                string? text = field switch
                {
                    0 => emu?.RecordUi.Status, 1 => emu?.RecordUi.Result, 2 => held == null ? null : string.Join(" | ", held.Tr.Facts), 3 => held?.MidiPath, 4 => held?.Folder,
                    5 => held == null ? null : string.Join(" | ", held.Tr.Tracks.Select(t => t.Name)), 6 => held?.PresetNote, 7 => emu?.RecordProblem, 8 => lastRecordResult, 9 => held == null ? null : string.Join("|", held.TrackPaths),
                    _ => null,
                };
                return text == null ? -1 : CopyText(text, max, value);
            }
            case 644: return (nint)(emu?.Recorder?.Count ?? emu?.Recording?.Session.Count ?? -1);
            case 645: return (nint)(emu?.Recorder?.Dropped ?? emu?.Recording?.Session.Dropped ?? -1);
            case 646: return emu?.Recording?.Tr.Tracks.Count ?? -1;
            case 647:                                                                                                                                   // a track's field: index = field << 16 | track
            {
                var tr = emu?.Recording?.Tr;
                int field = (int)(index >> 16), k = (int)(index & 0xFFFF);
                if (tr == null || (uint)k >= (uint)tr.Tracks.Count) return -1;
                var t = tr.Tracks[k];
                return field switch { 0 => t.Notes.Count, 1 => t.Channel, 2 => t.BendRange, 3 => t.Tune, 4 => t.Srcn, 5 => t.Lane, 6 => t.PresetFile?.Length ?? 0, 7 => t.PitchValues, 8 => t.PitchExact, 9 => t.Instrument, _ => -1 };
            }
            case 648: { var path = System.Runtime.InteropServices.Marshal.PtrToStringUni(value); if (emu == null || path == null) return 1; var (ok, msg) = emu.SaveRecordingMidi(path); lastRecordResult = msg; return ok ? 0 : 1; }
            case 649: { var b = emu?.Recording?.Tr.Midi; return b == null ? -1 : CopyBytes(b, index, value); }
            case 650: { var b = emu?.CaptureRecordSection(this); return b == null ? -1 : CopyBytes(b, index, value); }
            case 651:                                                                                                                                   // options: bit 0 start with FL's transport, 1 stop with it, 2 lanes; value < 0 only reads
            {
                if (emu == null) return -1;
                var u = emu.RecordUi;
                int was = (u.StartWithTransport ? 1 : 0) | (u.StopWithTransport ? 2 : 0) | (u.Lanes ? 4 : 0);
                if (value >= 0) { u.StartWithTransport = (value & 1) != 0; u.StopWithTransport = (value & 2) != 0; u.Lanes = (value & 4) != 0; }
                return was;
            }
            case 652: { if (value == 0) { RecordDeal.Disarm(); return 0; } if (emu == null) return 1; var (ok, msg) = emu.DealTracks(); lastRecordResult = msg; return ok ? 0 : 1; }
            case 653: { var (next, count, e) = RecordDeal.State; return RecordDeal.Armed ? next | count << 8 | e << 16 : -1; }
            case 654: replayProbe = value > 0 ? new ReplayProbe((int)value) : null; return 0;                                                          // the replay probe: capacity (0 off)
            case 655:                                                                                                                                   // read it: index = max records, value = buffer of (int64 time, int32 code) records; returns the count (value 0: the count held)
            {
                var p = replayProbe;
                if (p == null) return -1;
                if (value == 0) return p.Count;
                int nrec = Math.Min(p.Count, (int)index);
                byte* b = (byte*)value;
                for (int i = 0; i < nrec; i++) { *(long*)(b + i * 12) = p.Time[i]; *(int*)(b + i * 12 + 8) = p.Code[i]; }
                return nrec;
            }
            case 656: return (nint)GC.GetAllocatedBytesForCurrentThread();
            case 657: RecMutants.Active = (int)value; return 0;
            case 658: EmulatorHub.FlTempo = value / 1000.0; return 0;
            case 659: emu?.Recorder?.StallFeeder(value != 0); return emu?.Recorder != null ? 0 : 1;
            case 660: { var why = emu?.Retranscribe() ?? "no emulator"; lastRecordResult = why ?? "transcribing again"; return why == null ? 0 : 1; }
            case 661: { var tr = emu?.Recording?.Tr; int k = (int)(index >> 24); if (tr == null || (uint)k >= (uint)tr.Tracks.Count) return -1; return CopyBytes(tr.TrackMidi(k), index & 0xFFFFFF, value); }   // a track's own MIDI: index = track << 24 | buffer size
            case 662: { if (emu == null) return 1; lastRecordResult = emu.AddRecordingPresets(); return 0; }
            case 663: return (nint)(emu?.Recording?.Generation ?? 0);
            case 664: return dealtIndex;                                                                                                                 // the track this instance took from a deal (-1 none)
            case 665: { var p = replayProbe; return p == null ? -1 : (nint)p.Lost; }
            case 666: { int k = (int)(index >> 24); var held = emu?.Recording; if (held == null || (uint)k >= (uint)held.Tr.Tracks.Count) return -1; var f = held.Tr.Tracks[k].PresetFile; return f == null ? 0 : CopyBytes(f, index & 0xFFFFFF, value); }   // a track's preset file: index = track << 24 | buffer size
            case 667: { if (emu == null || index <= 0) return 1; emu.AdoptRecord(new ReadOnlySpan<byte>((void*)value, (int)index).ToArray()); return 0; }    // a REC1 section's bytes (index of them at value) as a project would bring them
            case 668: emu?.ForgetRecordingForTests(); return 0;                                                                                        // tests: the emulator holds no recording again
            // live MIDI out (P2): 680 on (value 1: 0 on, 1 refused, the words in 643 field 8) / off (value 0), 681 state (-1 off, 1 on, 2 ending), 682 a counter (index: 0 notes on, 1 notes off,
            // 2 bends, 3 volume changes, 4 messages lost, 5 keyed before the root was known, 6 recut, 7 out of range, 8 sent, 9 instruments, 10 waiting), 683 the port (value >= 0 sets it),
            // 684 the root of sample number <index> in MIDI cents (-1 unknown), 685 its channel (-1 none), 686 estimate the waiting roots now (returns how many), 687 the description (copied like 643)
            case 680: { if (value == 0) { emu?.StopLive(); lastRecordResult = "live off"; return 0; } var why = StartLiveMidi(); lastRecordResult = why ?? "live on"; return why == null ? 0 : 1; }
            case 681: { var l = emu?.Live; return l == null ? -1 : l.Ended ? 2 : !l.Primed ? 3 : 1; }            // (3: reading the roots of the samples loaded now)
            case 688:                                                                                                                                   // the roots notes were keyed with: value 0 makes the log (index entries), else copies index ints to value; returns the notes logged
            {
                var l = emu?.Live;
                if (l == null) return -1;
                if (value == 0) { l.KeyRootLog = new int[Math.Max(1, (int)index)]; return 0; }
                var log = l.KeyRootLog;
                if (log == null) return -1;
                int nlog = (int)Math.Min(Math.Min(l.NotesOn, log.Length), index);
                new ReadOnlySpan<int>(log, 0, nlog).CopyTo(new Span<int>((void*)value, nlog));
                return nlog;
            }
            case 682:
            {
                var l = emu?.Live;
                if (l == null) return -1;
                return (nint)(index switch { 0 => l.NotesOn, 1 => l.NotesOff, 2 => l.Bends, 3 => l.Controllers, 4 => l.Dropped, 5 => l.RootUnknown, 6 => l.Recut, 7 => l.OutOfRange, 8 => l.Sent, 9 => l.Instruments, 10 => l.Pending, _ => -1 });
            }
            case 683: { if (emu == null) return -1; if (value >= 0) emu.LivePort = (int)value; return emu.LivePort; }
            case 684: return emu?.Live?.RootOf((int)index) ?? -1;
            case 685: return emu?.Live?.ChannelOfSample((int)index) ?? -1;
            case 686: return emu?.Live?.EstimatePending() ?? -1;
            case 687: { var l = emu?.Live; return l == null ? -1 : CopyText(l.Describe(), index, value); }
            case 689: emu?.DrainLiveMidi(this); return 0;                                                                                               // the queued live notes to this instance's host now (what a render does after its frames)
            default: return 0;
        }
    }

    private static nint CopyBytes(byte[] b, nint max, nint buffer)
    {
        if (buffer != 0 && max > 0) new ReadOnlySpan<byte>(b, 0, (int)Math.Min(b.Length, max)).CopyTo(new Span<byte>((void*)buffer, (int)Math.Min(b.Length, max)));
        return b.Length;
    }
}
