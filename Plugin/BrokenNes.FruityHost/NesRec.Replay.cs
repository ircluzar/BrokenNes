// Round 8 (record-nes): replaying a NES recording's MIDI file and comparing, value by value, the registers the replay writes with what the game's chip sounded.
//
//   NesReplay.Library   the MIDI file read by VRUN's parser (MidiReplay: nothing the transcription wrote is trusted), each track played the way FL hands a channel its notes (key + the channel's bend at the
//                       file's RPN range, velocity / 127 x CC7 / 100, a control step every 64 samples at 48 kHz) by the very code a recorded track's preset plays with (NesDirect.Write / Silence and
//                       the track mode's retrigger rule), on a bare FIX chip per track; every register write logged with its sample time, and a mark when a note becomes the one playing
//   NesReplay.Compare   per game note and per value it held for 4 ms or more (three control steps): in the middle of that value (counted from the note's own start on both sides), the track of the
//                       duty (noise mode) sounding then must have the game's period, volume and duty in its registers, and every other duty's track of that note must be silent (volume 0); a value the
//                       transcription lists as not exact is counted as reported; anything else is a failure. The DLL check feeds it the instances' own writes (plugin hooks 740/741) instead.
using BrokenNes2.Record;
using NesEmulator.Plugin;

namespace BrokenNes.FruityHost;

internal static class NesReplay
{
    public const int Rate = 48000, Block = 64;

    /// <summary>One track's replay: the writes (sample time, reg << 8 | value; a mark: 1 << 24 | the note's index in the track) in order.</summary>
    public sealed class Played
    {
        public List<(long T, int Code)> Log = new();
        public int Notes;
    }

    /// <summary>Plays every track of a NES recording's MIDI file on its own chip, as the recorded-track preset does through the plugin.</summary>
    public static List<Played> Library(byte[] midi, NesTranscription tr)
    {
        var m = MidiReplay.Read(midi);
        var result = new List<Played>();
        double end = m.Notes.Count == 0 ? 1 : m.Seconds(m.Notes.Max(n => n.Off)) + 0.3;
        for (int k = 0; k < tr.Tracks.Count; k++)
        {
            var t = tr.Tracks[k];
            var notes = m.Notes.Where(n => n.Track == k + 1).OrderBy(n => n.On).ToList();
            var pl = new Played { Notes = notes.Count };
            result.Add(pl);
            if (t.Channel == 4) continue;                      // (the DMC: no engine plays it)
            var chan = notes.Count > 0 ? m.Of(k + 1, notes[0].Channel) : new MidiReplay.Chan();
            int range = chan.Range >= 0 ? chan.Range : 2;
            var chip = new NesApuInstrument("FIX", Rate);
            long now = 0;
            chip.WriteTap = (reg, v) => pl.Log.Add((now, reg << 8 | v));
            // the duty and noise mode the track's PRESET holds (what the instance plays), not the transcription's idea of them
            int duty = Math.Max(0, t.Duty), mode = Math.Max(0, t.Mode);
            if (t.PresetFile != null && BrokenNes2.Presets.PresetFile.Read(t.PresetFile).Data is { } pd && BrokenNes2.Instruments.InstrumentCodec.Unpack(pd.Instrument, out _) is { } inst)
            {
                if (inst.Field("duty") is { } fd) duty = fd.Value;
                if (inst.Field("mode") is { } fm) mode = fm.Value;
            }
            bool gate = false;
            int curPeriod = 0, next = 0, owner = -1;
            var active = new List<int>();
            for (long s = 0; s < (long)(end * Rate); s += Block)
            {
                now = s;
                long tick = m.TickAt(s / (double)Rate);
                active.RemoveAll(i => notes[i].Off <= tick);
                while (next < notes.Count && notes[next].On <= tick) { active.Add(next); next++; }
                int newest = active.Count > 0 ? active[^1] : -1;          // (the newest held FL voice owns the channel, as ResolveOwner says)
                if (newest < 0) { if (gate) { NesDirect.Silence(chip, t.Channel, duty); gate = false; } owner = -1; continue; }
                var n = notes[newest];
                bool fresh = newest != owner;                             // (a voice's first control step: TriggerVoice made it Fresh; a track's notes never overlap, so no older one resumes)
                if (fresh) pl.Log.Add((now, 1 << 24 | newest));
                owner = newest;
                int bend = MidiReplay.At(chan.Bends, tick, 0), cc = MidiReplay.At(chan.Cc7, tick, 100);
                double cents = (float)((n.Key - 60) * 100 + bend * range * 100.0 / 8192.0);
                int level = NesDirect.Level((float)(n.Velocity / 127.0 * cc / 100.0), 1.0);
                bool retrigger = fresh || !gate;
                curPeriod = NesDirect.Write(chip, t.Channel, duty, mode, ref level, cents, retrigger, curPeriod);
                gate = true;
            }
        }
        return result;
    }

    /// <summary>The comparison (see the file's header). <paramref name="label"/> names the take in the report.</summary>
    public static void Compare(ImodelReport r, string label, NesTranscription tr, IReadOnlyList<Played> played, out string line)
    {
        int values = 0, exact = 0, reported = 0, bad = 0, gatedOk = 0, gatedBad = 0, missing = 0, shortVals = 0, compared = 0;
        const uint MinHeld = 7160;                                  // 4 ms in CPU cycles
        // per track: the mark of each note (sample time of the first control step that played it)
        var marks = new List<Dictionary<int, long>>();
        foreach (var p in played)
        {
            var d = new Dictionary<int, long>();
            foreach (var (t, code) in p.Log) if (code >> 24 == 1 && !d.ContainsKey(code & 0xFFFFFF)) d[code & 0xFFFFFF] = t;
            marks.Add(d);
        }
        // a track's register shadow at a sample time (the log is in time order)
        int RegAt(int track, int reg, long t)
        {
            int v = 0;
            foreach (var (tt, code) in played[track].Log) { if (tt > t) break; if (code >> 24 == 0 && (code >> 8 & 0xFF) == reg) v = code & 0xFF; }
            return v;
        }
        // which tracks hold each game note, and at which index of the track
        var holders = new Dictionary<int, List<(int Track, int K)>>();
        for (int k = 0; k < tr.Tracks.Count; k++)
            for (int q = 0; q < tr.Tracks[k].Notes.Count; q++)
            {
                int ni = tr.Tracks[k].Notes[q];
                if (!holders.TryGetValue(ni, out var l)) holders[ni] = l = new();
                l.Add((k, q));
            }
        for (int ni = 0; ni < tr.Notes.Count; ni++)
        {
            var n = tr.Notes[ni];
            if (n.Channel == 4 || !holders.TryGetValue(ni, out var hs)) continue;
            int c = n.Channel;
            compared++;
            // the times at which any of its values changes
            var cuts = n.Periods.Select(v => v.T).Concat(n.Vols.Select(v => v.T)).Concat(n.Duties.Select(v => v.T)).Concat(n.Modes.Select(v => v.T)).Append(n.End).Distinct().OrderBy(x => x).ToList();
            for (int i = 0; i + 1 < cuts.Count; i++)
            {
                uint a = cuts[i], b = cuts[i + 1];
                if (b - a < MinHeld) { shortVals++; continue; }
                uint mid = (a + b) / 2;
                int wantP = NesNote.At(n.Periods, mid), wantV = NesNote.At(n.Vols, mid), wantD = c < 2 ? NesNote.At(n.Duties, mid) : c == 3 ? NesNote.At(n.Modes, mid) : 0;
                foreach (var (k, q) in hs)
                {
                    var t = tr.Tracks[k];
                    if (!marks[k].TryGetValue(q, out long t0)) { missing++; continue; }
                    long at = t0 + (long)Math.Round((mid - n.Start) / NesSession.CpuHz * Rate);
                    bool mine = c < 2 ? t.Duty == wantD : c == 3 ? t.Mode == wantD : true;
                    int b0 = c < 2 ? c * 4 : 0;
                    if (!mine)
                    {
                        // another duty's track of this note: silent now
                        int v = RegAt(k, b0 == 0 && c == 3 ? 0x0C : b0, at) & 15;
                        if (v == 0) gatedOk++;
                        else { gatedBad++; if (gatedBad <= 4) r.Expect(false, $"{label}: track {k + 1} ({t.Name}) sounds at volume {v} at {mid / NesSession.CpuHz:0.000} s while the note's other duty does"); }
                        continue;
                    }
                    values++;
                    int gotP, gotV, gotD;
                    switch (c)
                    {
                        case 2: gotP = RegAt(k, 0x0A, at) | (RegAt(k, 0x0B, at) & 7) << 8; gotV = RegAt(k, 0x08, at) == 0xFF ? 15 : 0; gotD = 0; break;
                        case 3: gotP = RegAt(k, 0x0E, at) & 15; gotV = RegAt(k, 0x0C, at) & 15; gotD = RegAt(k, 0x0E, at) >> 7; break;
                        default: gotP = RegAt(k, b0 + 2, at) | (RegAt(k, b0 + 3, at) & 7) << 8; int r0 = RegAt(k, b0, at); gotV = r0 & 15; gotD = r0 >> 6; break;
                    }
                    if (gotP == wantP && gotV == wantV && gotD == wantD) { exact++; continue; }
                    if (tr.Reported(k, ni, mid)) { reported++; continue; }
                    bad++;
                    if (bad <= 6) r.Expect(false, $"{label}: track {k + 1} ({t.Name}) note at {n.StartSeconds:0.000} s, value at {mid / NesSession.CpuHz:0.000} s: the replay wrote period {gotP:X3} volume {gotV} {(c == 3 ? "mode" : "duty")} {gotD}, the game {wantP:X3} {wantV} {wantD}, and the transcription did not say so");
                }
            }
        }
        // the notes' times: each note's start in the replay against the game's, the take's constant offset (the file's lead before its first bar) taken out
        var offs = new List<double>();
        for (int k = 0; k < tr.Tracks.Count; k++)
            for (int q = 0; q < tr.Tracks[k].Notes.Count; q++)
                if (marks[k].TryGetValue(q, out long t0)) offs.Add(t0 / (double)Rate - tr.Notes[tr.Tracks[k].Notes[q]].StartSeconds);
        offs.Sort();
        double med = offs.Count > 0 ? offs[offs.Count / 2] : 0, worstMs = offs.Count > 0 ? offs.Max(o => Math.Abs(o - med)) * 1000 : 0;
        r.Expect(worstMs <= 2.5, $"{label}: a note starts {worstMs:0.00} ms from the game's time in the replay (FL's control step is 1.33 ms, a tick 0.5 ms or less)");
        r.Expect(bad == 0 && values > 0, $"{label}: {exact:N0} of {values:N0} values (period, volume and duty together) exact, {reported} reported as not exact, {bad} wrong WITHOUT a word");
        r.Expect(gatedBad == 0, $"{label}: {gatedBad} moments where a note's other duty's track was not silent");
        r.Expect(missing == 0, $"{label}: {missing} notes never played by their track");
        line = $"{label}: {compared:N0} notes; {exact:N0} of {values:N0} values held 4 ms or more exact in the replay's registers (period, volume, duty), {reported} reported, 0 unreported; {gatedOk:N0} duty-gated moments silent as they should be; {shortVals:N0} shorter values not compared; note starts within {worstMs:0.00} ms";
    }
}
