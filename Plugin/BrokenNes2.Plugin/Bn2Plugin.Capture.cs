// Round 9 (capture): the instance's side of "Capture from FL" (Test MIDI export window). While a take is armed or running, every BrokenNes2 instance writes into its lane what FL makes it play:
// the notes FL starts (TriggerVoice: InitLevels and FinalLevels pitch as FL hands them, cents above the channel's base key, which the builder reads (Record/FlNotes.cs); the instance's
// Coarse / Fine; FL's velocity), every change of that pitch FL makes while a note sounds (slides,
// per-note pitch, fine pitch: read once per block, which is as often as FL changes them), the releases; on the lane's own clock of rendered samples since FL's Play (FL gives no song position).
// FL's timing calls (FPD_SetSamplesPerTick, FPD_SetTimeSig, FPE_Tempo, FPD_ProcessMode, FPD_SetSampleRate, FPD_SongPosChanged) are kept for the process in Capture/FlClock.cs. Nothing here
// allocates on the audio thread (the lanes' chunks are made by the capture's worker). Plugin test hooks 840-859 (790-819 are reload's).
using System.Runtime.InteropServices;
using BrokenNes.Fruity;
using BrokenNes2.Capture;

namespace BrokenNes2;

public sealed unsafe partial class Bn2Plugin
{
    private volatile CaptureLane? capLane;
    private volatile CaptureSession? capSession;
    private float capTuning = float.NaN;
    private long capNotes;

    /// <summary>The take this instance writes into (the hub sets it on the UI thread, or the constructor while a take is armed).</summary>
    internal void AttachLane(CaptureSession s, CaptureLane lane) { capSession = s; capTuning = float.NaN; Interlocked.Exchange(ref capNotes, 0); capLane = lane; }
    internal void DetachLane() { capLane = null; capSession = null; }
    internal CaptureLane? Lane => capLane;
    internal long CapturedNotes => Interlocked.Read(ref capNotes);

    /// <summary>What this instance is, for its track (the UI thread or the capture's worker): its role, its name (FL's name for the channel when FL gives one), where it sits.</summary>
    internal LaneIdentity CaptureIdentity()
    {
        var a = assignment;
        var e = a.Emulator;
        var role = CapRole.Other;
        string name;
        if (e == null) name = $"BrokenNes2 #{InstanceNo}";
        else if (e.PresetMode && preset is { } lp)
        {
            role = lp.Play.Kind switch
            {
                Instruments.InstrumentKind.NesPulse1 => CapRole.Pulse1, Instruments.InstrumentKind.NesPulse2 => CapRole.Pulse2, Instruments.InstrumentKind.NesTriangle => CapRole.Triangle,
                Instruments.InstrumentKind.NesNoise => CapRole.Noise, Instruments.InstrumentKind.NesDmc => CapRole.Dmc, _ => CapRole.Other,
            };
            name = lp.Data.Name.Length > 0 ? lp.Data.Name : LaneIdentity.RoleName(role);
        }
        else if (a.Channel is >= 0 and < Ch.ToneCount && !e.AnySamplerMode) { role = (CapRole)a.Channel; name = Ch.Names[a.Channel]; }
        else name = e.AnySamplerMode ? $"Sampler #{InstanceNo}" : Ch.Label(a.Channel, e.Console, e.Mode);
        if (channelName is { Length: > 0 } fl && !fl.StartsWith(DisplayName, StringComparison.OrdinalIgnoreCase)) name = fl;      // FL's own name for the channel (relink reads it when FL gives one)
        string detail = e == null ? "no emulator" : $"emulator #{e.Id}, {(e.Mode == 1 ? "ROM mode" : "Direct mode")}, {e.ChipId} chip, channel {Ch.Label(a.Channel)}";
        return new LaneIdentity(InstanceNo, role, name, detail, e?.Id ?? 0, CaptureBaseHint);
    }

    // ---- FL's calls (any FL thread; nothing allocated) ----

    /// <summary>The Dispatcher's timing calls, before anything else looks at them.</summary>
    private void CaptureDispatch(nint id, nint index, nint value)
    {
        switch ((int)id)
        {
            case Fpd.SetPlaying:
            {
                FlClock.Playing();
                var lane = capLane; var s = capSession;
                if (lane == null || s == null) break;
                if (value != 0) s.TransportStart(lane); else s.TransportStop(lane);
                break;
            }
            case 13:                                                                   // FPD_SongPosChanged: the user moved the song position (or FL looped): kept in the take, said in the facts
                FlClock.SongPosChanged();
                capLane?.Add(CapEv.SongPos, 0, 0);
                break;
            case Fpd.SetSamplesPerTick: FlClock.SamplesPerTick(value); break;
            case 14: ReadTimeSig(value); break;                                        // FPD_SetTimeSig: Value points at FL's TTimeSigInfo { StepsPerBar, StepsPerBeat, PPQ }
            case 1: FlClock.ProcessMode((int)value); break;                            // FPD_ProcessMode (PM_IsRendering = 16 while FL exports)
            case Fpd.SetSampleRate: FlClock.Rate((int)value); break;
        }
    }

    /// <summary>FPE_Tempo (ProcessEvent 0): the tempo as a float in Value, the average samples per tick in Flags.</summary>
    private void CaptureProcessEvent(int id, int value, int flags)
    {
        if (id == Fpe.Tempo) FlClock.TempoEvent(value, flags);
    }

    /// <summary>FL's TTimeSigInfo, read only where the memory is committed and readable (FL's pointer; a bad one is counted, never followed).</summary>
    private static void ReadTimeSig(nint p)
    {
        if ((ulong)p < 0x10000 || !Readable(p, 12)) { FlClock.TimeSigUnread(); return; }
        int* t = (int*)p;
        FlClock.TimeSig(t[0], t[1], t[2]);
    }

    private static bool Readable(nint p, int bytes)
    {
        if (CapVirtualQuery(p, out var mbi, (nuint)sizeof(CapMemInfo)) == 0) return false;
        const uint commit = 0x1000, readable = 0x02 | 0x04 | 0x08 | 0x20 | 0x40 | 0x80;
        if (mbi.State != commit || (mbi.Protect & 0x101) != 0 || (mbi.Protect & readable) == 0) return false;
        return (long)mbi.BaseAddress + (long)mbi.RegionSize - (long)p >= bytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CapMemInfo
    {
        public nint BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State, Protect, Type;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "VirtualQuery")] private static partial nuint CapVirtualQuery(nint address, out CapMemInfo info, nuint length);

    // ---- the notes (FL's mixer thread) ----

    private float CapTuningNow => Get(P.Coarse) * 100 + Get(P.Fine);

    /// <summary>Whether TriggerVoice, the releases and the blocks have anything to do here (a take listens, or the live MIDI out is on).</summary>
    private bool CaptureWanted => capLane != null || CaptureHub.LiveOn || LivePending;

    /// <summary>A note FL starts (TriggerVoice).</summary>
    private void CaptureNoteOn(Voice v)
    {
        var lane = capLane;
        float tuning = CapTuningNow;
        float final = v.Params->FinalLevels.Pitch;
        v.CapPitch = final + tuning;
        v.CapOff = false;
        float vel = BitConverter.Int32BitsToSingle(Host.VoiceProcessEvent(v.HostTag, Fpv.GetVelocity, 0, 0));
        int kind = CapEv.On;
        if (!(vel > 0 && vel <= 2)) { vel = v.Params->InitLevels.Vol; kind = CapEv.OnLevelVelocity; }       // FL gave no velocity: the voice's level (counted, said)
        if (lane != null)
        {
            if (lane.State == 0) capSession?.JoinLate(lane);                         // a channel FL never started while the take runs: it joins at the others' clock (said)
            if (lane.Running)
            {
                if (tuning != capTuning) { lane.Add(CapEv.Tuning, 0, tuning); capTuning = tuning; }
                lane.Add(kind, (int)v.Handle, v.Params->InitLevels.Pitch, final, vel);           // (as FL hands them: cents above the channel's base key; the builder reads the base)
                // FL's song position, asked as the SDK says (TFPTime "filled with an optional offset in samples": 0 here): kept beside the note, reported against the capture's own clock
                TFPTime ft; ft.T = 0; ft.T2 = CapEv.MixSentinel;
                Host.Dispatcher(HostTag, Fhd.GetMixingTime, Gt.Beats, (nint)(&ft));
                lane.Add(CapEv.MixTime, (int)v.Handle, (float)ft.T, (float)ft.T2);
                Interlocked.Increment(ref capNotes);
            }
        }
        LiveNoteOn(v, final, tuning, vel);
    }

    /// <summary>FL released the voice (Voice_Release), or killed it before releasing it.</summary>
    private void CaptureNoteOff(Voice v)
    {
        LiveNoteOff(v);
        if (v.CapOff) return;
        v.CapOff = true;
        var lane = capLane;
        if (lane != null && lane.Running) lane.Add(CapEv.Off, (int)v.Handle, 0);
    }

    /// <summary>The start of a block (Gen_Render, before anything plays): the pitch FL wants each sounding voice to play now, written when it changed.</summary>
    private void CaptureBlock()
    {
        float tuning = CapTuningNow;
        var lane = capLane;
        if (lane != null && lane.Running)
            foreach (var v in voices)
            {
                if (v.Released || v.CapOff) continue;
                float final = v.Params->FinalLevels.Pitch;
                if (final + tuning == v.CapPitch) continue;
                v.CapPitch = final + tuning;
                lane.Add(CapEv.Pitch, (int)v.Handle, final, tuning);
            }
        LiveBlock(tuning);
    }

    /// <summary>The base key this instance knows FL counts its pitch from (-1: it does not know; the capture reads C5 and checks the notes): reload's per-instance base, once the instance found it
    /// from its track's notes or a project gave it (Bn2Plugin.Tracks.cs, Record/FlNotes.cs).</summary>
    private int CaptureBaseHint => pitchBaseKnown ? pitchBase : -1;

    // ---- the window's live MIDI out of the notes (Bn2Plugin.CaptureLive.cs) ----
    partial void LiveNoteOn(Voice v, float final, float tuning, float vel);
    partial void LiveNoteOff(Voice v);
    partial void LiveBlock(float tuning);

    // ---- test hooks 840-859 ----

    /// <summary>840 arm a capture from this instance's emulator (0 armed, 1 refused: the words in 843 field 5), 841 cancel it, 842 the take's state (-1 none, 0 cancelled, 1 armed, 2 recording,
    /// 3 finishing, 4 done), 843 copy a text: field = index >> 16 (0 the window's status, 1 the facts joined by '\n', 2 the report joined by '\n', 3 the MIDI file's path, 4 the problem, 5 the last
    /// hook's words, 6 this instance's identity "role|name|detail"), max = index &amp; 0xFFFF, 844 copy the MIDI file's bytes (index = buffer size; returns the length, -1 none), 845 Save MIDI to the
    /// UTF-16 path <c>value</c> (0 written, 1 refused), 846 a number: index 0 notes, 1 tracks, 2 bend events, 3 notes that move, 4 events lost, 5 lanes, 6 this instance's lane events (-1 none),
    /// 7 this instance's notes captured, 8 the take's tempo x 1000, 9 the take's microseconds per quarter, 10 bars, 11 end tick, 12 this lane's state (-1 none), 847 the bytes allocated so far on
    /// this thread, 848 live MIDI out of the notes (value 1 on, 0 off, -1 read), 849 the deliberate-bug code (CaptureMutants) = <c>value</c>, 850 forget FL's timing and any take (a fresh process),
    /// 851 FlClock as text (copied like 843), 852 build now (tests: every started lane stopped; 0 done), 853 the converter's track for NES channel <c>index</c> (-1 none).</summary>
    private nint CaptureTestHook(int n, nint index, nint value)
    {
        var s = CaptureSession.Current;
        var r = s?.Result;
        switch (n)
        {
            case 840: { var why = CaptureHub.Arm(this); CaptureHub.LastWords = why ?? "armed"; return why == null ? 0 : 1; }
            case 841: CaptureHub.Cancel("cancelled by the test"); return 0;
            case 842: return s == null ? -1 : s.State;
            case 843:
            {
                int field = (int)(index >> 16), max = (int)(index & 0xFFFF);
                var id = field == 6 ? CaptureIdentity() : null;
                string? text = field switch
                {
                    0 => CaptureHub.StatusText(), 1 => r == null ? null : string.Join("\n", r.Facts), 2 => r == null ? null : string.Join("\n", r.Report), 3 => CaptureHub.MidiPath, 4 => r?.Problem,
                    5 => CaptureHub.LastWords, 6 => $"{id!.Role}|{id.Name}|{id.Detail}", _ => null,
                };
                return text == null ? -1 : CopyText(text, max, value);
            }
            case 844: { var b = r?.Midi; return b == null || b.Length == 0 ? -1 : CopyBytes(b, index, value); }
            case 845: { var path = Marshal.PtrToStringUni(value); var why = path == null ? "no path" : CaptureHub.SaveMidi(path); CaptureHub.LastWords = why ?? "saved"; return why == null ? 0 : 1; }
            case 846:
                return index switch
                {
                    0 => r?.Notes ?? -1, 1 => r?.Tracks.Count ?? -1, 2 => r?.BendEvents ?? -1, 3 => r?.MovingNotes ?? -1, 4 => (nint)(r?.LostEvents ?? -1), 5 => s?.LaneCount ?? -1,
                    6 => capLane?.Count ?? -1, 7 => (nint)CapturedNotes, 8 => r == null ? -1 : (nint)Math.Round(r.Tempo.Bpm * 1000), 9 => r?.MicrosPerQuarter ?? -1, 10 => r?.Bars ?? -1,
                    11 => (nint)(r?.EndTick ?? -1), 12 => capLane?.State ?? -1, _ => -1,
                };
            case 847: return (nint)GC.GetAllocatedBytesForCurrentThread();
            case 848: if (value >= 0) CaptureHub.SetLive(value != 0); return CaptureHub.LiveOn ? 1 : 0;
            case 849: CaptureMutants.Active = (int)value; return 0;
            case 850: CaptureSession.Forget(); FlClock.Reset(); CaptureHub.Reset(); return 0;
            case 851: return CopyText(FlClock.Counts(), index, value);
            case 852: if (s == null) return 1; foreach (var l in s.Lanes) l.Stop(); s.Finish(); return 0;
            case 853: return r == null || (uint)index >= 4 ? -1 : r.ConverterTrack[index];
            default: return 0;
        }
    }
}
