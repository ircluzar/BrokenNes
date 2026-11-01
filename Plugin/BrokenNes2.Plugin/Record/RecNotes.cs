// Round 8 (record): the notes of a recording. The same reading of the S-DSP writes as round 6's SpcRecipes.Notes (a key-on opens a note on its voice, a key-off or the next key-on closes it, the voice's
// register writes in between are the note's own changes), over the WHOLE session and keeping the RAW registers: the pitch register as the driver wrote it (not cents rounded to an integer, which
// could not be replayed exactly), both volumes, the envelope. Nothing here is lost silently: what a transcription cannot replay is counted per note (a sample switched under a sounding note, an
// envelope rewritten while it sounds, pitch modulation, a key-on while the voice's key-off bit is held).
namespace BrokenNes2.Record;

/// <summary>A pitch register value from time <c>T</c> (samples from the recording's start) on.</summary>
public readonly record struct RecPitch(uint T, int P);

/// <summary>Both voice volumes (signed registers) from time <c>T</c> on.</summary>
public readonly record struct RecVol(uint T, int L, int R);

/// <summary>One note of the recording: its voice, the instrument (a <see cref="RecVersion"/> index), when it started and ended, what was keyed, and what changed while it sounded.</summary>
public sealed class RecNote
{
    public int Voice, Version, Srcn;
    public uint Start, End;
    public bool KeyedOff;
    public int VolL, VolR, Pitch, Adsr1, Adsr2, Gain, NoiseRate;
    public bool Noise, Echo, Pmod;
    /// <summary>The pitch register over the note, the key-on value first; consecutive equal values are merged, two writes in one sample are one value.</summary>
    public List<RecPitch> Pitches = new();
    /// <summary>The volumes over the note, the key-on value first.</summary>
    public List<RecVol> Vols = new();
    /// <summary>What the transcription cannot replay, counted: the sample number changed while it sounded, the envelope (ADSR1, ADSR2, GAIN) was written, the noise / echo / modulation bits changed.</summary>
    public int SrcnChanges, EnvChanges, FlagChanges;
    /// <summary>Keyed on while its key-off bit was still set: the S-DSP starts it and releases it at once (two samples).</summary>
    public bool KeyedWhileOff;
    public double StartSeconds => Start / 32000.0;
    public double Seconds => (End - Start) / 32000.0;
}

/// <summary>What the reading found besides the notes.</summary>
public sealed record RecNoteStats(int KeyOns, int Notes, int Silent, int NoVersion, int HeldAtStart, int Markers);

public static class RecNotes
{
    /// <summary>Every note of the session in the order they were keyed (by time, then voice). Notes that never asked for any volume (both registers zero all along) are left out and counted.</summary>
    public static List<RecNote> Extract(RecSession s, out RecNoteStats stats)
    {
        var sh = new int[128];
        for (int r = 0; r < 128; r++) sh[r] = s.BaseRegs[r];
        var current = new int[256];
        Array.Fill(current, -1);
        var open = new RecNote?[8];
        var done = new List<RecNote>();
        int keyOns = 0, markers = 0, noVersion = 0;

        void Close(int v, uint at, bool off)
        {
            var n = open[v];
            if (n == null) return;
            n.End = Math.Max(at, n.Start);
            n.KeyedOff = off;
            done.Add(n);
            open[v] = null;
        }

        for (int i = 0; i < s.Count; i++)
        {
            s.Get(i, out uint t, out int reg, out int val);
            if (reg == RecSession.MarkerVersion)
            {
                markers++;
                if (val < s.Versions.Length) current[s.Versions[val].Srcn] = val;
                continue;
            }
            int prev = sh[reg];
            sh[reg] = val;
            if ((reg & 15) < 8)
            {
                int v = reg >> 4;
                var n = open[v];
                if (n == null) continue;
                switch (reg & 15)
                {
                    case 0: case 1: AddVol(n, t, (sbyte)sh[v * 16], (sbyte)sh[v * 16 + 1]); break;
                    case 2: case 3: AddPitch(n, t, sh[v * 16 + 2] | (sh[v * 16 + 3] & 0x3F) << 8); break;
                    case 4: if (val != n.Srcn) n.SrcnChanges++; break;
                    case 5: case 6: case 7: if (val != prev) n.EnvChanges++; break;
                }
            }
            else if (reg == 0x4C)
            {
                for (int v = 0; v < 8; v++)
                {
                    if ((val >> v & 1) == 0) continue;
                    keyOns++;
                    Close(v, t, false);
                    int b = v * 16, srcn = sh[b + 4];
                    int ver = current[srcn];
                    if (ver < 0) { noVersion++; continue; }          // (only when the capture ran out of room for instruments: counted)
                    var n = new RecNote
                    {
                        Voice = v, Version = ver, Srcn = srcn, Start = t,
                        VolL = (sbyte)sh[b], VolR = (sbyte)sh[b + 1], Pitch = sh[b + 2] | (sh[b + 3] & 0x3F) << 8, Adsr1 = sh[b + 5], Adsr2 = sh[b + 6], Gain = sh[b + 7],
                        Noise = (sh[0x3D] >> v & 1) != 0, Echo = (sh[0x4D] >> v & 1) != 0, Pmod = v > 0 && (sh[0x2D] >> v & 1) != 0, NoiseRate = sh[0x6C] & 0x1F,
                    };
                    n.Pitches.Add(new RecPitch(t, n.Pitch));
                    n.Vols.Add(new RecVol(t, n.VolL, n.VolR));
                    if ((sh[0x5C] >> v & 1) != 0) { n.KeyedWhileOff = true; open[v] = n; Close(v, t + 2, true); continue; }
                    open[v] = n;
                }
            }
            else if (reg == 0x5C)
            {
                for (int v = 0; v < 8; v++) if ((val >> v & 1) != 0 && (prev >> v & 1) == 0) Close(v, t, true);
            }
            else if (reg is 0x3D or 0x4D or 0x2D)
            {
                for (int v = 0; v < 8; v++) if (open[v] is { } n && ((val ^ prev) >> v & 1) != 0) n.FlagChanges++;
            }
        }
        for (int v = 0; v < 8; v++) Close(v, s.Length, false);

        done.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Voice.CompareTo(b.Voice));
        int silent = 0;
        var result = new List<RecNote>(done.Count);
        foreach (var n in done)
        {
            bool any = false;
            foreach (var vol in n.Vols) if (vol.L != 0 || vol.R != 0) { any = true; break; }
            if (!any) { silent++; continue; }
            result.Add(n);
        }
        stats = new RecNoteStats(keyOns, done.Count, silent, noVersion, s.HeldAtStart, markers);
        return result;
    }

    private static void AddPitch(RecNote n, uint t, int p)
    {
        var last = n.Pitches[^1];
        if (last.P == p) return;
        if (last.T == t && n.Pitches.Count > 1) { n.Pitches[^1] = new RecPitch(t, p); if (n.Pitches[^2].P == p) n.Pitches.RemoveAt(n.Pitches.Count - 1); return; }
        if (last.T == t) { n.Pitches[^1] = new RecPitch(t, p); return; }      // (the key-on's own sample: the value it plays with)
        n.Pitches.Add(new RecPitch(t, p));
    }

    private static void AddVol(RecNote n, uint t, int l, int r)
    {
        var last = n.Vols[^1];
        if (last.L == l && last.R == r) return;
        if (last.T == t && n.Vols.Count > 1) { n.Vols[^1] = new RecVol(t, l, r); if (n.Vols[^2].L == l && n.Vols[^2].R == r) n.Vols.RemoveAt(n.Vols.Count - 1); return; }
        if (last.T == t) { n.Vols[^1] = new RecVol(t, l, r); return; }
        n.Vols.Add(new RecVol(t, l, r));
    }
}
