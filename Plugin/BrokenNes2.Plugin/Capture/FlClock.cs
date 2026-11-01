// Round 9 (capture): what FL Studio tells a plugin about time, kept for the process (FL tells every instance the same). FL gives no song position (the user's real log: FHD_GetMixingTime answered
// nothing on every note), so a capture's clock is the samples FL renders since its transport started, and these values turn seconds into beats:
//
//   FPD_SetSamplesPerTick (20)  Value holds a 32-bit float (the log's 1129579756 = 0x435404EC = 212.0192 samples per tick = 44,100 x 60 / (130 BPM x PPQ 96): FL's default tempo and PPQ at
//                               44.1 kHz, exactly; sent at every Play and many times while idle). A tick is 1/PPQ of a beat.
//   FPD_SetTimeSig (14)         Value points at FL's TTimeSigInfo { StepsPerBar, StepsPerBeat, PPQ } (the log: an address 16 bytes past the instance's host tag, sent once when FL makes the instance).
//   FPE_Tempo (ProcessEvent 0)  Value holds the tempo as a 32-bit float (BPM); Flags the average samples per tick (the SDK's words; whether FL sends it to a generator is not in the log: the log did not
//                               record ProcessEvent before this round).
//   FPD_SetSampleRate (4)       the rate the samples are counted at.
//
// The tempo of a capture is FL's FPE_Tempo when FL sent one, else rate x 60 / (samples per tick x PPQ), else 120 BPM: the file says which, and whether the two agree. The notes keep their real times
// in every case (the tempo only sets where the beats fall), which is what VRUN's converter turns into frames.
namespace BrokenNes2.Capture;

/// <summary>A copy of what FL had said about time at one moment.</summary>
public readonly record struct FlTiming(float SamplesPerTick, int SptChanges, int StepsPerBar, int StepsPerBeat, int Ppq, bool TimeSigKnown, double Tempo, int TempoChanges, int TempoFlags, int ProcessMode, int Rate)
{
    public bool HasSpt => SamplesPerTick > 0 && float.IsFinite(SamplesPerTick);
    public bool HasTempo => Tempo is > 10 and < 1000;
    /// <summary>Beats in a bar (FL's steps per bar / steps per beat; 4 when FL said nothing usable).</summary>
    public int BeatsPerBar => TimeSigKnown && StepsPerBeat > 0 && StepsPerBar % StepsPerBeat == 0 && StepsPerBar / StepsPerBeat is >= 1 and <= 16 ? StepsPerBar / StepsPerBeat : 4;
}

/// <summary>The tempo a capture is written at, where it came from, and what the other source said.</summary>
public sealed record TempoChoice(double Bpm, string Source, string? Check, bool Guessed);

public static class FlClock
{
    private static readonly object gate = new();
    private static float spt;
    private static int sptChanges, stepsPerBar, stepsPerBeat, ppq, tempoChanges, tempoFlags, processMode = -1, rate;
    private static bool timeSig;
    private static double tempo = double.NaN;
    private static int sptCalls, timeSigCalls, tempoCalls, songPosCalls, playingCalls;

    /// <summary>FPD_SetSamplesPerTick: the float in Value's low 32 bits.</summary>
    public static void SamplesPerTick(long value)
    {
        float f = BitConverter.Int32BitsToSingle(unchecked((int)value));
        lock (gate) { sptCalls++; if (f != spt) { if (spt != 0) sptChanges++; spt = f; } }
    }

    /// <summary>FPD_SetTimeSig, once read (the caller reads FL's struct only where the memory is readable): steps per bar, steps per beat, PPQ.</summary>
    public static void TimeSig(int bar, int beat, int pq)
    {
        lock (gate)
        {
            timeSigCalls++;
            bool sane = bar is > 0 and <= 256 && beat is > 0 and <= 64 && pq is >= 24 and <= 9600;
            if (!sane) return;
            stepsPerBar = bar; stepsPerBeat = beat; ppq = pq; timeSig = true;
        }
    }

    /// <summary>FPD_SetTimeSig whose struct could not be read.</summary>
    public static void TimeSigUnread() { lock (gate) timeSigCalls++; }

    /// <summary>FPE_Tempo: the tempo (float bits in Value) and Flags.</summary>
    public static void TempoEvent(int value, int flags)
    {
        double t = BitConverter.Int32BitsToSingle(value);
        lock (gate) { tempoCalls++; tempoFlags = flags; if (t is > 10 and < 1000 && t != tempo) { if (!double.IsNaN(tempo)) tempoChanges++; tempo = t; } }
    }

    public static void ProcessMode(int mode) { lock (gate) processMode = mode; }
    public static void Rate(int r) { if (r > 0) lock (gate) rate = r; }
    public static void SongPosChanged() => Interlocked.Increment(ref songPosCalls);
    public static void Playing() => Interlocked.Increment(ref playingCalls);

    public static FlTiming Now(int fallbackRate)
    {
        lock (gate) return new FlTiming(spt, sptChanges, stepsPerBar, stepsPerBeat, ppq, timeSig, tempo, tempoChanges, tempoFlags, processMode, rate > 0 ? rate : fallbackRate);
    }

    /// <summary>The calls FL made, for the host log.</summary>
    public static string Counts() { lock (gate) return $"FPD_SetSamplesPerTick {sptCalls} calls (last {spt:0.####}), FPD_SetTimeSig {timeSigCalls} ({(timeSig ? $"{stepsPerBar} steps per bar, {stepsPerBeat} per beat, PPQ {ppq}" : "never read")}), FPE_Tempo {tempoCalls} ({(double.IsNaN(tempo) ? "none" : $"last {tempo:0.###} BPM, flags {tempoFlags}")}), FPD_SongPosChanged {songPosCalls}, FPD_SetPlaying {playingCalls}, FPD_ProcessMode last {processMode}"; }

    /// <summary>Tests: forget everything FL said (a fresh FL process).</summary>
    public static void Reset()
    {
        lock (gate) { spt = 0; sptChanges = stepsPerBar = stepsPerBeat = ppq = tempoChanges = tempoFlags = 0; processMode = -1; rate = 0; timeSig = false; tempo = double.NaN; sptCalls = timeSigCalls = tempoCalls = songPosCalls = playingCalls = 0; }
    }

    /// <summary>The tempo a take is written at (see the file's header).</summary>
    public static TempoChoice Resolve(FlTiming t)
    {
        double? fromSpt = t.HasSpt && t.Ppq > 0 && t.Rate > 0 ? t.Rate * 60.0 / (t.SamplesPerTick * (double)t.Ppq) : null;
        string sptWords = t.HasSpt ? $"{t.SamplesPerTick:0.###} samples per tick x PPQ {(t.Ppq > 0 ? t.Ppq.ToString() : "?")} at {t.Rate:N0} Hz" : "";
        if (CaptureMutants.On(CaptureMutants.TempoIgnored)) return new TempoChoice(120, "120 BPM", null, true);       // (the deliberate bug)
        if (t.HasTempo)
        {
            string? check = fromSpt is double s ? (Math.Abs(s / t.Tempo - 1) < 0.002 ? $"FL's {sptWords} agree ({s:0.###} BPM)" : $"FL's {sptWords} give {s:0.###} BPM instead: the file follows FPE_Tempo") : null;
            return new TempoChoice(t.Tempo, $"FL's tempo (FPE_Tempo) {t.Tempo:0.###} BPM", check, false);
        }
        if (fromSpt is double bpm && bpm is > 10 and < 1000) return new TempoChoice(bpm, $"{bpm:0.###} BPM from FL's {sptWords}", "FL sent no FPE_Tempo", false);
        if (t.HasSpt && t.Rate > 0)
        {
            double g = t.Rate * 60.0 / (t.SamplesPerTick * 96.0);
            if (g is > 10 and < 1000) return new TempoChoice(g, $"{g:0.###} BPM from FL's {t.SamplesPerTick:0.###} samples per tick, ASSUMING PPQ 96 (FL's time signature could not be read)", null, true);
        }
        return new TempoChoice(120, "120 BPM ASSUMED: FL told BrokenNes2 no tempo (no FPE_Tempo, no samples per tick); the notes keep their real times, only the beat grid may not be FL's", null, true);
    }
}
