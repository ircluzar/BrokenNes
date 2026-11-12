// The music pipeline's Vaults GG back end: the Game Gear port plays the Game Boy version's song bytes UNCHANGED through its own PSG driver (VRUN_VOC_GG_dev src/voc_sound.c: pulse 1 / 2
// -> tone 0 / 1, wave -> tone 2, noise -> noise; tools/psg/convert_music.py copies the Game Boy songs), so the song is VocGbBackend's (same events, same streams, the same import into the
// Game Boy roster serves both games) and this file adds the Game Gear's fence: what its driver does with those bytes (voc_sound.c's mus_decode / mus_output, read and measured):
//   duty, the instruments' envelope / vibrato / sweep / wave shape and the panning are not played (the PSG's squares are 50%, the wave channel is a square on tone 2, the mix is mono);
//   periods over 1023 (notes under about A2, 110 Hz) are halved until they fit: an octave (or more) up; noise: the PSG's 3 fixed rates, white only (the Game Boy's 7-bit width is not
//   played); levels -> 2 dB attenuation steps (att_of: some neighbouring levels sound the same); the Game Gear's frame is 59.92 Hz, the songs count Game Boy frames (59.73 Hz): 0.32% fast.
// The test ROM is the game's own sound unit with the song written in (Resources/flpvocgg.gg).
namespace BrokenNes2.Music.Flp;

public static class VocGgBackend
{
    public const double Fps = 3579545.0 / (228 * 262);         // 59.9227 Hz (NTSC)
    public static readonly int[] AttOf = [15, 12, 9, 7, 6, 5, 4, 3, 3, 2, 2, 1, 1, 1, 0, 0];
    private const double PsgClock = 3579545.0;

    /// <summary>The PSG period the driver writes for a Game Boy register value (voc_sound.c gb_per + mus_output: x 0.852, halved while over 1023); the number of halvings.</summary>
    public static (int N, int Halved) Period(int c, int x)
    {
        int q = 2048 - (x & 2047);
        if (c == 2) q <<= 1;
        return OfPer(q & 0xFFFF);
    }

    /// <summary>The pulse-unit period of a note byte (convert_music.py mus_ptab: tones and the wave channel alike). Since 2026-11-12 (convert_music.py note_per) the period whose
    /// PSG period after the driver's x 0.852 and halvings is the nearest to the note (an octave up per halving), among equals the one nearest 131072 / f; before, round(131072 / f)
    /// (held notes 6 cents flat at C4, 34 at C7).</summary>
    public static readonly int[] Ptab = [.. Enumerable.Range(0, 128).Select(NotePer)];

    /// <summary>Deliberate bug for flp-test: the note table as before 2026-11-12 (round(131072 / f)).</summary>
    public const int OldNoteTableMutant = 1 << 30;
    public static int PtabOf(int note) => FlpMutants.On(OldNoteTableMutant) ? OldPer(note) : Ptab[note];
    private static int OldPer(int n) => Math.Min(65535, (int)Math.Round(131072.0 / VocGbBackend.MidiHz(n), MidpointRounding.ToEven));

    private static int NotePer(int n)
    {
        double f = VocGbBackend.MidiHz(n);
        int q0 = OldPer(n);
        if (q0 >= 65535) return q0;
        int h0 = OfPer(q0).Halved, best = q0;
        double bestErr = double.MaxValue;
        for (int q = Math.Max(1, (int)(q0 * 0.97)); q <= Math.Min(65535, (int)(q0 * 1.03) + 2); q++)
        {
            var (N, h) = OfPer(q);
            if (h != h0) continue;
            double err = Math.Round(Math.Abs(1200 * Math.Log2(PsgClock / (32.0 * N) / (f * Math.Pow(2, h)))), 9);
            if (err < bestErr || err == bestErr && Math.Abs(q - q0) < Math.Abs(best - q0)) { bestErr = err; best = q; }
        }
        return best;
    }

    /// <summary>The PSG period the driver writes for a note byte ("on": mus_ptab, not the Game Boy register).</summary>
    public static (int N, int Halved) PeriodOfNote(int note) => OfPer(PtabOf(Math.Clamp(note, 0, 127)));

    private static (int N, int Halved) OfPer(int q)
    {
        int n = q - (q >> 3) - (q >> 6) - (q >> 7), h = 0;
        while (n > 1023) { n = (n + 1) >> 1; h++; }
        return (Math.Max(1, n), h);
    }

    /// <summary>The PSG noise rate (0-2) of a Game Boy NR43 value (convert_music.py mus_nrate).</summary>
    public static int NoiseRate(int nr43)
    {
        int r = nr43 & 7, s = nr43 >> 4;
        double f = 524288.0 / (r == 0 ? 0.5 : r) / Math.Pow(2, s + 1);
        double[] rates = [PsgClock / 512, PsgClock / 1024, PsgClock / 2048];
        int best = 0;
        for (int i = 1; i < 3; i++) if (Math.Abs(Math.Log(f / rates[i])) < Math.Abs(Math.Log(f / rates[best]))) best = i;
        return best;
    }

    public static VocSong Build(FlpSongModel song, string name)
    {
        var v = VocGbBackend.Build(song, name, VocTarget.Gg);
        var F = v.Fence;
        int notDuty = 0, wave = 0, low = 0, lowWorst = 0, panned = 0, width7 = 0;
        var levels = new SortedSet<int>(); var rates = new SortedSet<int>(); var nr = new SortedSet<int>();
        for (int c = 0; c < 4; c++)
        {
            VocInstrument? cur = null;
            foreach (var e in v.Ch[c])
            {
                switch (e.Op)
                {
                    case "inst": cur = e.Inst; break;
                    case "pan": panned++; break;
                    case "vol": if (c != 2) levels.Add(e.Value); break;
                    case "on" or "pitch":
                        if (c < 2 && cur is { Duty: not 2 }) notDuty++;
                        if (c == 2) wave++;
                        if (c == 3) { rates.Add(NoiseRate(e.Value)); nr.Add(e.Value & 0xF7); if (cur?.Width7 == true) width7++; break; }
                        goto case "freq";
                    case "freq":
                        if (c < 3)
                        {
                            var (_, h) = e.Op == "on" ? PeriodOfNote(e.Value) : Period(c, e.Value);
                            if (h > 0 && e.Op != "freq") { low++; lowWorst = Math.Max(lowWorst, h); }
                        }
                        break;
                }
            }
        }
        if (low > 0) F.Add(new FenceLine("note range", FenceLevel.Over, $"{low} notes are under the Game Gear PSG's lowest tone (period 1023: about A2, 110 Hz): the driver plays them {(lowWorst > 1 ? "up to " + lowWorst + " octaves" : "an octave")} higher"));
        else F.Add(new FenceLine("note range", FenceLevel.Ok, "every note is inside the PSG's tone range (A2 and up)"));
        if (notDuty > 0) F.Add(new FenceLine("duty", FenceLevel.Note, $"{notDuty} pulse notes have a duty other than 50%: the PSG's squares are 50% (the Game Gear driver does not play the duty)"));
        else F.Add(new FenceLine("duty", FenceLevel.Ok, "every pulse note is 50% (the PSG's square)"));
        if (wave > 0) F.Add(new FenceLine("wave", FenceLevel.Note, $"{wave} wave-channel notes play on PSG tone 2: a square, not the triangle (same pitch)"));
        if (panned > 0) F.Add(new FenceLine("pan", FenceLevel.Note, "Pan is not played: the Game Gear driver's mix is mono"));
        if (v.Notes[3] > 0)
        {
            F.Add(new FenceLine("noise", rates.Count < nr.Count ? FenceLevel.Note : FenceLevel.Ok, $"{nr.Count} noise settings play on the PSG's 3 fixed rates (used: {string.Join(", ", rates.Select(r => new[] { "high", "mid", "low" }[r]))})"
                + (rates.Count < nr.Count ? $": {nr.Count - rates.Count} of them merge" : "")));
            if (width7 > 0) F.Add(new FenceLine("noise", FenceLevel.Note, $"{width7} hits use Short (7-bit) noise: the Game Gear driver plays white noise only"));
        }
        var merged = levels.GroupBy(l => AttOf[l]).Where(g => g.Count() > 1).Select(g => string.Join("/", g)).ToList();
        F.Add(new FenceLine("volume", merged.Count > 0 ? FenceLevel.Note : FenceLevel.Ok, $"levels {string.Join(", ", levels)} -> the PSG's 2 dB steps" + (merged.Count > 0 ? $": {string.Join(", ", merged)} sound the same" : "")));
        F.Add(new FenceLine("timing", FenceLevel.Note, $"the songs count Game Boy frames; the Game Gear's frame is {Fps:0.000} Hz: the song plays {(Fps / VocGbBackend.Fps - 1) * 100:0.00}% fast ({v.Seconds * VocGbBackend.Fps / Fps:0.00} s instead of {v.Seconds:0.00} s)"));
        F.Add(new FenceLine("effects", FenceLevel.Ok, "the instruments' envelopes, vibrato and sweep: not played by the Game Gear driver, and the plugin's songs use none"));
        return v;
    }

    /// <summary>The test ROM: null with the reason when the song does not fit.</summary>
    public static byte[]? Patch(byte[] template, string manifest, VocSong s, out string? why)
    {
        why = null;
        var m = VocGbBackend.Manifest(manifest, "bn2-flp-vocgg");
        if (template.Length.ToString() != m["rom-bytes"]) { why = "the template is not the one its manifest describes"; return null; }
        var rom = (byte[])template.Clone();
        var parts = new List<(string Key, byte[] Data, string What)> { ("nrate", s.Noise.Select(x => (byte)NoiseRate(x)).ToArray(), "noise rates") };
        for (int c = 0; c < 4; c++) parts.Add(("stream" + c, s.Streams[c], VocGbBackend.Names[c]));
        foreach (var (key, data, what) in parts)
        {
            var (off, size) = VocGbBackend.Region(m, key);
            if (data.Length > size) { why = $"{what}: {data.Length} bytes, the test ROM has room for {size}"; return null; }
            Array.Clear(rom, off, size);
            data.CopyTo(rom, off);
        }
        return rom;
    }
}
