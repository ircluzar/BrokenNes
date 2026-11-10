// The music pipeline's first back end (docs/projects/flp-music-pipeline.md): the neutral song (FlpSong) -> VRUN CTW NES (the MMC3 main build's audio engine: NESFab, the same four decoders
// and the same converter the round-7 sound-test template carries). It writes the Standard MIDI File that VRUN's converter (Music/Vrun/Midi2Nes.cs) reads, laid out the way the capture
// (Capture/CaptureSong.cs) lays it out: square 1 on MIDI channel 1, square 2 on 2, triangle on 3, noise on 10; then the round-7 path takes over (SongConvert -> MusicTemplate.Patch).
//
// What it reads: every BrokenNes2 instance on the NES console in Direct mode, on pulse 1 / pulse 2 / triangle / noise (Channel, or Auto = where it sat). It plays them as the plugin does
// (Record/NesDirect.cs, Bn2Plugin.ResolveOwner): one note at a time per channel (the newest held note), pitch = FL key + note fine pitch + Coarse/Fine from C5, FL's slide notes glide
// the sounding note (FL's measured slide shape, flp2nano's FL_SLIDE), level = round(15 x velocity/128 x Volume).
// Pulse duty (2026-11-10): each square's Duty (12.5/25/50/75%) goes to the MIDI as CC 14 at tick 0 (value duty x 32), which VRUN's converter turns into its duty
// opcode 0xD3 (Midi2Nes.DutyCc / OpDuty; the template's square decoders are VRUN's duty decoders). One duty per square for the whole song.
// What it reports (the caveats: nothing is dropped without a line): everything else on the rack, two instances on one NES channel (merged, newest note wins), instances
// of one square with different duties (the first one's plays), noise level below 15 and short noise (VRUN's noise decoder hits at full volume, long mode), silent notes, pan (the NES is mono), slides with no note
// under them, notes that end where they start. The round-7 facts (the converter's own: channel choice, thinning, truncation, bends) come on top, from SongConvert.
using BrokenNes2.Record;

namespace BrokenNes2.Music.Flp;

/// <summary>One NES channel's part. Duty: the square's pulse duty 0-3 (12.5/25/50/75%) written as CC 14; -1 on the triangle and noise.</summary>
public sealed record VrunNesPart(int NesChannel, string Instruments, int Notes, int Slides, int BentNotes, int BendRange, int Duty = -1);

public sealed record VrunNesSong(byte[] Midi, List<string> Caveats, List<VrunNesPart> Parts, double Seconds);

public static class VrunNesBackend
{
    public static readonly string[] NesNames = ["square 1", "square 2", "triangle", "noise"];
    public static readonly string[] DutyNames = ["12.5", "25", "50", "75"];
    private static readonly int[] MidiChannelOf = [0, 1, 2, 9];
    /// <summary>FL's slide as the plugin heard it (measured on the user's overworld.wav: Nano/gb/tools/flp2nano/convert.mjs FL_SLIDE): starts 0.85 tick early, lasts 1.8 ticks longer.</summary>
    public const double SlideEarly = 0.85, SlideExtra = 1.8;

    private sealed class Glide { public double Start, Len, To; public int At; public bool Hit; }
    private sealed class Voice { public int Start, End, Order, Velocity; public double Base, Volume; public List<Glide> Glides = new(); }

    private static double PitchAt(Voice v, double t)
    {
        double cur = v.Base;
        foreach (var g in v.Glides)
        {
            if (t <= g.Start) break;
            double from = cur;
            if (g.Len <= 0 || t >= g.Start + g.Len) cur = g.To;
            else { cur = from + (g.To - from) * (t - g.Start) / g.Len; break; }
        }
        return cur;
    }

    /// <summary>The noise period index the plugin plays for a pitch (cents from C5): Record/NesDirect.NoiseIndex.</summary>
    public static int NoiseIndex(double c5Cents) => Math.Clamp(15 - (int)Math.Floor((60.0 + c5Cents / 100.0 - 24.0) / 3.0), 0, 15);

    public static VrunNesSong Build(FlpSongModel song, string name)
    {
        var cav = new List<string>(song.Caveats);
        var byNes = new List<FlpInstrument>[4];
        for (int i = 0; i < 4; i++) byNes[i] = new();
        foreach (var ins in song.Instruments)
        {
            string who = $"channel {ins.Rack} \"{ins.Name}\"";
            if (ins.Bn2 is not { } b)
            {
                if (ins.Notes.Count > 0 && !ins.Plugin.Equals("BrokenNes2", StringComparison.OrdinalIgnoreCase))
                    cav.Add($"{who} is not BrokenNes2 ({(ins.Plugin.Length > 0 ? ins.Plugin : ins.Type == 0 ? "sampler" : "type " + ins.Type)}): its {ins.Notes.Count} notes are not in the game");
                continue;
            }
            if (ins.Notes.Count == 0) continue;
            if (b.Mode != "direct") { cav.Add($"{who} is in ROM mode (it plays a game's channel, not notes): its {ins.Notes.Count} notes are not in the game"); continue; }
            if (b.Preset != null && b.Core is >= FlpPresetSound.FirstPresetChip and <= FlpPresetSound.LastPresetChip && !FlpMutants.On(PsMutants.NesPresetIgnored))
            { cav.Add($"OVER {who} plays a Runaway preset (Presets chip): VRUN's NES engine plays only its own pulse / triangle / noise sounds, the preset is not in the game and its {ins.Notes.Count} notes are left out (put the part on a plain NES instance to hear it as VRUN's sound)"); continue; }
            if (b.Console != 0) { cav.Add($"{who} is a {b.ConsoleName} instrument: VRUN NES has no such sound, its {ins.Notes.Count} notes are not in the game (that console's back end will play it)"); continue; }
            if (b.Channel is < 0 or > 3) { cav.Add($"{who}: NES channel {(b.Channel == 4 ? "Mix/DMC" : b.Channel.ToString())} is not pulse/triangle/noise: its {ins.Notes.Count} notes are not in the game"); continue; }
            if (b.Core != 0) cav.Add($"{who}: sound chip #{b.Core}, not FIX: VRUN's game plays it on the real NES sound (FIX)");
            byNes[b.Channel].Add(ins);
        }

        int scale = Math.Max(1, (int)Math.Round(RecMidi.Ppq / (double)Math.Max(1, song.Ppq)));
        if (RecMidi.Ppq % Math.Max(1, song.Ppq) != 0) cav.Add($"FL's PPQ {song.Ppq} does not divide 960: note times are rounded to the nearest MIDI tick");
        long T(double flTicks) => (long)Math.Round(flTicks * RecMidi.Ppq / song.Ppq, MidpointRounding.AwayFromZero);
        var tracks = new List<SmfTrack>();
        var parts = new List<VrunNesPart>();
        for (int nc = 0; nc < 4; nc++)
        {
            var group = byNes[nc];
            if (group.Count == 0) continue;
            string names = string.Join(" + ", group.Select(g => $"\"{g.Name}\""));
            if (group.Count > 1) cav.Add($"{NesNames[nc]}: {group.Count} instances play it ({names}): VRUN has one {NesNames[nc]}, they are merged (the newest note sounds)");
            int mc = MidiChannelOf[nc];
            var voices = new List<Voice>();
            var glides = new List<Glide>();
            int order = 0, silent = 0, empty = 0, panned = 0, partDuty = -1;
            foreach (var ins in group)
            {
                var b = ins.Bn2!;
                double tune = FlpMutants.On(FlpMutants.TuningIgnored) ? 0 : b.Param(3) * 100 + b.Param(4);
                double volume = Math.Clamp(b.Param(1, 1000) / 1000.0, 0, 1);
                int duty = b.Param(5) & 3, noiseMode = b.Param(7);
                string who = $"channel {ins.Rack} \"{ins.Name}\"";
                if (nc <= 1)
                {
                    if (partDuty < 0) partDuty = duty;
                    else if (duty != partDuty) cav.Add($"{who}: Duty {DutyNames[duty]}% plays as {DutyNames[partDuty]}% in the game (one duty per square: the first instance's)");
                }
                if (nc == 3 && noiseMode != 0) cav.Add($"{who}: short (metallic) noise plays as long noise in the game (VRUN's noise decoder is long mode)");
                if (b.Param(2) != 0) panned++;
                foreach (var n in ins.Notes)
                {
                    double cents = n.Key * 100 + (FlpMutants.On(FlpMutants.NoteFineIgnored) ? 0 : n.Fine - 120) + tune;          // MIDI-0 based, FL's channels count from C5 = 60
                    if (n.Slide) glides.Add(new Glide { At = n.Start, Start = n.Start - SlideEarly, Len = Math.Max(0, n.End - n.Start) + SlideExtra, To = cents });
                    else if (n.End > n.Start) voices.Add(new Voice { Start = n.Start, End = n.End, Base = cents, Velocity = n.Velocity, Volume = volume, Order = order++ });
                    else empty++;
                }
            }
            if (panned > 0) cav.Add($"{NesNames[nc]}: Pan is set: the NES is mono, the game plays it centred");
            if (empty > 0) cav.Add($"{NesNames[nc]}: {empty} notes end where they start (length 0): silent in FL and in the game");
            foreach (var g in glides)
            {
                foreach (var v in voices) if (v.Start <= g.At && g.At < v.End) { v.Glides.Add(FlpMutants.On(FlpMutants.SlideSignFlipped) ? new Glide { At = g.At, Start = g.Start, Len = g.Len, To = 2 * v.Base - g.To } : g); g.Hit = true; }
                if (!g.Hit) cav.Add($"{NesNames[nc]}: a slide note at tick {g.At} has no note under it: it does nothing in FL either");
            }
            foreach (var v in voices) v.Glides.Sort((a, c) => a.Start.CompareTo(c.Start));

            // one note at a time: the newest held voice owns the channel (Bn2Plugin.ResolveOwner)
            var times = voices.SelectMany(v => new[] { v.Start, v.End }).Distinct().OrderBy(x => x).ToList();
            var segs = new List<(Voice V, int From, int To)>();
            for (int i = 0; i + 1 < times.Count; i++)
            {
                int t = times[i];
                Voice? own = null;
                foreach (var v in voices) if (v.Start <= t && t < v.End && (own == null || (FlpMutants.On(FlpMutants.OldestNoteWins) ? v.Start < own.Start : v.Start > own.Start || v.Start == own.Start && v.Order > own.Order))) own = v;
                if (own == null) continue;
                if (segs.Count > 0 && segs[^1].V == own && segs[^1].To == t) segs[^1] = (own, segs[^1].From, times[i + 1]);
                else segs.Add((own, t, times[i + 1]));
            }

            int KeyOf(Voice v, int from) => nc == 3 ? 0x30 | NoiseIndex(PitchAt(v, from) - 6000) : Math.Clamp((int)Math.Round(v.Base / 100, MidpointRounding.AwayFromZero), 0, 127);
            double maxBend = 0;
            if (nc < 3)
                foreach (var s in segs)
                {
                    int key = KeyOf(s.V, s.From);
                    if (s.V.Glides.Count > 0) for (int t = s.From; t <= s.To; t++) maxBend = Math.Max(maxBend, Math.Abs(PitchAt(s.V, t) - key * 100));
                    else maxBend = Math.Max(maxBend, Math.Abs(PitchAt(s.V, s.From) - key * 100));
                }
            int range = Math.Clamp((int)Math.Ceiling(maxBend / 100 - 1e-9), 2, 48);
            if (maxBend > 4800) cav.Add($"{NesNames[nc]}: a slide reaches {maxBend / 100:0.#} semitones from its note: bends are capped at 48 semitones");

            var trk = new SmfTrack { Name = string.Join(" + ", group.Select(g => g.Name.Length > 0 ? g.Name : NesNames[nc])), Instrument = "BrokenNes2 NES " + NesNames[nc] };
            tracks.Add(trk);
            if (nc < 3) { trk.Events.AddRange(RecMidi.BendRange(0, mc, range)); trk.Events.Add(RecMidi.PitchBend(0, mc, 0)); }
            if (nc <= 1) trk.Events.Add(RecMidi.Cc(0, mc, Vrun.Audio.Midi2Nes.DutyCc, (FlpMutants.On(FlpMutants.DutyIgnored) ? 2 : partDuty) * 32));
            int lastBend = 0, notes = 0, bent = 0, quiet = 0;
            void Bend(long tick, double cents)
            {
                int v = (int)Math.Round(cents / (range * 100.0) * 8192, MidpointRounding.AwayFromZero);
                v = Math.Clamp(v, -8192, 8191);
                if (v != lastBend) { trk.Events.Add(RecMidi.PitchBend(tick, mc, v)); lastBend = v; }
            }
            foreach (var s in segs)
            {
                int level = Math.Clamp((int)Math.Round(15 * (s.V.Velocity / 128.0) * s.V.Volume, MidpointRounding.AwayFromZero), 0, 15);
                if (level == 0) { silent++; continue; }
                if (nc == 3 && level < 15) quiet++;
                int key = KeyOf(s.V, s.From);
                if (nc < 3)
                {
                    Bend(T(s.From), PitchAt(s.V, s.From) - key * 100);
                    foreach (var g in s.V.Glides)
                    {
                        double a = Math.Max(g.Start, s.From), z = Math.Min(g.Start + g.Len, s.To);
                        for (double t = Math.Ceiling(a); t <= z; t += 1) Bend(T(t), PitchAt(s.V, t) - key * 100);
                        if (z > a && z < s.To) Bend(T(z), PitchAt(s.V, z) - key * 100);     // the glide's end: its target exactly
                    }
                    if (s.V.Glides.Count > 0) bent++;
                }
                trk.Events.Add(RecMidi.On(T(s.From), mc, key, nc == 2 ? 120 : level * 8));      // the converter's volume = velocity >> 3: level x 8 is exactly the level
                trk.Events.Add(RecMidi.Off(T(s.To), mc, key));
                notes++;
            }
            if (silent > 0) cav.Add($"{NesNames[nc]}: {silent} notes have level 0 (velocity x Volume): silent in FL and in the game");
            if (quiet > 0) cav.Add($"noise: {quiet} hits are softer than full level in FL: VRUN's noise decoder hits at full volume");
            parts.Add(new VrunNesPart(nc, names, notes, voices.Sum(v => v.Glides.Count), bent, range, nc <= 1 ? partDuty : -1));
        }
        if (parts.Count == 0) cav.Add("NOTHING for VRUN NES: no BrokenNes2 instance on the NES console in Direct mode with notes on pulse 1, pulse 2, triangle or noise");
        int us = (int)Math.Round(60_000_000.0 / Math.Max(1e-3, song.Tempo), MidpointRounding.AwayFromZero);
        // the conductor holds the tempo again at the song's end, so the file lasts as long as the timeline (VRUN's loop is the longest track)
        var tempos = new List<(long, int)> { (0, us), (T(song.End), us) };
        var midi = RecMidi.Write(tempos, name, [$"made from {name}.flp by the BrokenNes music pipeline (VRUN NES back end)"], tracks);
        return new VrunNesSong(midi, cav, parts, song.End / (double)Math.Max(1, song.Ppq) * 60.0 / Math.Max(1e-3, song.Tempo));
    }

    /// <summary>After the round-7 conversion: the lines where VRUN's converter put a part somewhere else than its own NES channel (it chooses by itself: Midi2Nes.SelectChannelMap).</summary>
    public static IEnumerable<string> ChannelChoice(VrunNesSong s, SongFacts f)
    {
        foreach (var p in s.Parts)
        {
            int want = MidiChannelOf[p.NesChannel] + 1;
            int got = Array.IndexOf(f.Source, want);
            if (got != p.NesChannel)
                yield return got < 0 ? $"VRUN's converter left {NesNames[p.NesChannel]} ({p.Instruments}) out" : $"VRUN's converter plays {NesNames[p.NesChannel]} ({p.Instruments}) on {NesNames[got]}";
        }
    }
}
