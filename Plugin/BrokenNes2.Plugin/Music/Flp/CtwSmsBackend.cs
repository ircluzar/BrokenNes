// The music pipeline's CTW SMS back end: VRUN: Corrupt The World on the Master System (VRUN_CTW_SMS_dev) IS the VRUN NES game translated to C (tools/fab2c.py): its music is VRUN's NES
// music engine (the converter's streams, VRUN's plain decoders) whose APU writes nes_apu.c turns into SN76489 PSG writes once a frame. So the song is made as VRUN NES makes it (the
// BrokenNes2 instances on the MASTER SYSTEM console are read as the NES channels they sit on: Pulse 1 / Pulse 2 / Triangle / Noise = PSG tone 1 / tone 2 / tone 3 / noise, through
// VrunNesBackend and VRUN's own converter, Music/Vrun), and this file is the Master System's fence: what nes_apu.c and the translated decoders do with it (read in the game's source):
//   squares: PSG N = NES period + 1, halved while over 1023 (notes under about A2, 109 Hz, an octave or more up); no duty (the PSG is 50%); level -> 2 dB steps (vol2att: some merge);
//   triangle: N = 2 (period + 1), at FL's key since 2026-11-13: the streams are converted with ConvertOptions.TriangleAtKey as VRUN NES's .flp songs are (before: an octave
//   below the key, where the NES triangle sounds at the squares' period; FruityHost tri-octave-sms measured it -1200 cents, now about 0); a note under the PSG's range is raised by whole octaves chosen at its key-on and kept for the whole note (slides never jump; a slide below the note's octave stops
//   at N = 1023), a fixed attenuation 3; noise: the PSG's 3 rates from the NES period index (0-3 high, 4-7 mid, 8-15 low), white noise;
//   pitch bends and slides ON, as VRUN NES plays them (2026-11-10: fab2c translates the bend decoders' SSF periods; the template's title song has VRUN's bend decoders on its
//   three tonal channels, so the converter's 0xD1 pitch / 0xD2 slide play: the bent NES period goes through the same PSG mapping each frame);
//   pulse duty: the converter's 0xD3 duty opcode is read by the template's duty decoders (fab2c steps over it in every other square decoder), but the PSG has no duty: 50% (a note);
//   the NES frame count runs on the Master System's 59.92 Hz frame (0.30% slower than the NES's 60.10 Hz).
// The test ROM is THE GAME with its title song's streams replaced (Resources/flpctwsms.sms, BrokenDev/tools/music/ctwsms/build-ctwsms.ps1): it boots to the title screen and plays it.
using BrokenNes2.Music;

namespace BrokenNes2.Music.Flp;

public sealed class CtwSmsSong
{
    public VrunNesSong Nes = null!;
    public SongBuild? Build;
    public List<FenceLine> Fence = new();
    public double Seconds;
    public IEnumerable<string> Caveats => Fence.Where(f => f.Level != FenceLevel.Ok).Select(FlpFence.Line);
}

/// <summary>Deliberate bugs for FruityHost flp-test (bits of FlpMutants.Mask above Super VRUN's).</summary>
public static class CtwSmsMutants { public const int SlidesDropped = 262144, PresetIgnored = 524288, PresetFenceSilent = 1048576, PeriodicAsWhiteSilent = 2097152, TriangleHalvedPerFrame = 1 << 26; }

public static class CtwSmsBackend
{
    /// <summary>2 (2026-11-10): the template's title song has VRUN's bend + duty decoders. A version-1 template (plain decoders) would read a bend as the loop: refused.</summary>
    /// <summary>3 (2026-11-10): the game translated from the MMC3 main build (main.fab + mapper_mmc3.fab); same layout as 2.</summary>
    /// <summary>4 (2026-11-10): the title song's noise has VRUN's noise-volume decoder (opcode 0xD4), and the four regions are the song room measured in the game's data
    /// banks (build-ctwsms.ps1 + ctwsms_room.py), not the game's 1280. A version-3 template reads 0xD4 as the noise's loop: refused.</summary>
    public const int TemplateVersion = 4;
    public const double Fps = 3579545.0 / (228 * 262), NesFps = 1789773.0 / (262.0 * (341.0 / 3.0));
    public static readonly string[] Names = ["tone 1 (square 1)", "tone 2 (square 2)", "tone 3 (triangle)", "noise"];
    /// <summary>nes_apu.c's level -> attenuation (tools/gen_tables.py vol2att).</summary>
    public static readonly int[] Vol2Att = [.. Enumerable.Range(0, 16).Select(v => v == 0 ? 15 : Math.Min(14, (int)Math.Round(-20 * Math.Log10(v / 15.0) / 2, MidpointRounding.ToEven)))];
    /// <summary>The lowest pitch a PSG tone plays (N = 1023).</summary>
    public static readonly double LowestHz = 3579545.0 / 32 / 1023;
    /// <summary>The song room per channel (square 1, square 2, triangle, noise): the regions of the bundled template's manifest (Resources/flpctwsms.txt). Null with
    /// <see cref="RoomProblem"/> when the template is not in this build.</summary>
    public static int[]? Room => room.Value.Room;
    public static string? RoomProblem => room.Value.Problem;
    private static readonly Lazy<(int[]? Room, string? Problem)> room = new(() =>
    {
        try
        {
            using var st = typeof(CtwSmsBackend).Assembly.GetManifestResourceStream("flpctwsms.txt") ?? throw new InvalidOperationException("no resource flpctwsms.txt");
            var m = VocGbBackend.Manifest(new StreamReader(st).ReadToEnd(), "bn2-flp-ctwsms", TemplateVersion);
            return ([.. new[] { "square1", "square2", "triangle", "noise" }.Select(k => VocGbBackend.Region(m, k).Size)], null);
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or KeyNotFoundException or IOException) { return (null, "the CTW SMS template's manifest: " + e.Message); }
    });

    /// <summary>The PSG period nes_apu.c writes for a NES period on a square, and how many times it was halved (each an octave up). The triangle has its own rule: <see cref="TrianglePsg"/>
    /// (this with <paramref name="triangle"/> is the period doubled and halved per frame: the rule before 2026-11-12).</summary>
    public static (int N, int Halved) PsgPeriod(int nesPeriod, bool triangle)
    {
        int n = triangle ? (nesPeriod + 1) << 1 : nesPeriod + 1, h = 0;
        while (n > 1023) { n >>= 1; h++; }
        return (n, h);
    }

    /// <summary>nes_apu.c's triangle (2026-11-12): N = 2 (period + 1); at a key-on ($4008 written non-zero after zero: since 2026-11-13 a rewrite of a sounding note, VRUN's $D1 pitch step, is not one; key-ons from NmiLatch.Rising) the note's octave raise <paramref name="shift"/> is chosen from the
    /// period's high byte written after it (one octave from $200, two from $400: N is at most 4096) and kept until the next key-on; N past 1023 (a slide below the note's octave,
    /// or the edge periods $1FF / $3FF) stops at 1023.</summary>
    public static int TrianglePsg(int nesPeriod, bool keyOn, ref int shift)
    {
        if (FlpMutants.On(CtwSmsMutants.TriangleHalvedPerFrame)) return PsgPeriod(nesPeriod, true).N;
        if (keyOn) { int h = (nesPeriod >> 8) & 7; shift = h > 3 ? 2 : h > 1 ? 1 : 0; }
        int n = ((nesPeriod & 0x7FF) + 1) << 1 >> shift;
        return Math.Min(1023, n);
    }

    public static int NoiseRate(int index) => index < 4 ? 0 : index < 8 ? 1 : 2;

    /// <param name="triangleAtKey">false = the old octave (before 2026-11-13: an octave below FL's key), kept for FruityHost tri-octave-sms's before/after.</param>
    public static CtwSmsSong Build(FlpSongModel song, string name, bool triangleAtKey = true)
    {
        var o = new CtwSmsSong();
        var F = o.Fence;
        foreach (var c in song.Caveats) F.Add(new FenceLine("project", FenceLevel.Note, c));
        F.AddRange(FlpFence.ChannelVolume(song));
        song = FlpNesFallback.Apply(song, b => b.Console == 3, 3, "Master System", "CTW SMS", F);
        // the Master System instances, read as the NES channels they sit on; everything else is said here
        var sub = new FlpSongModel { Ppq = song.Ppq, TempoMilli = song.TempoMilli, End = song.End, Title = song.Title };
        int slides = 0, detuned = 0, low = 0, notHalf = 0, triRaised = 0, triStopped = 0; string triWhere = "", stopWhere = ""; double worstOff = 0; string lowWhere = "";
        var noiseIdx = new SortedSet<int>();
        var levels = new SortedSet<int>();
        var levelsNoise = new List<int>();
        foreach (var ins in song.Instruments)
        {
            string who = $"channel {ins.Rack} \"{ins.Name}\"";
            if (ins.Bn2 is not { } b)
            {
                if (ins.Notes.Count > 0) F.Add(new FenceLine("instruments", FenceLevel.Over, $"{who} is not BrokenNes2 ({(ins.Plugin.Length > 0 ? ins.Plugin : ins.Type == 0 ? "sampler" : "type " + ins.Type)}): its {ins.Notes.Count} notes are not in the game"));
                continue;
            }
            if (ins.Notes.Count == 0) continue;
            if (b.Mode != "direct") { F.Add(new FenceLine("instruments", FenceLevel.Over, $"{who} is in ROM mode (it plays a game's channel, not notes): its {ins.Notes.Count} notes are not in the game")); continue; }
            int ch = b.Channel;
            bool preset = false;
            FlpPresetApprox? approx = null;
            if (b.Core is >= FlpPresetSound.FirstPresetChip and <= FlpPresetSound.LastPresetChip && !FlpMutants.On(CtwSmsMutants.PresetIgnored))
            {
                var (pch, over) = PresetVoice(b, who, ins, F, out approx);
                if (over) continue;
                ch = pch; preset = true;
            }
            if (!preset && b.Console != 3) { F.Add(new FenceLine("instruments", FenceLevel.Over, $"{who} is a {b.ConsoleName} instrument: its {ins.Notes.Count} notes are not in this Master System song (that console's back end plays it)")); continue; }
            if (ch is < 0 or > 3) { F.Add(new FenceLine("channels", FenceLevel.Over, $"{who}: channel {(ch == 4 ? "Mix" : ch.ToString())} is not tone 1 / tone 2 / tone 3 / noise: its {ins.Notes.Count} notes are not in the game")); continue; }
            var st = new FlpBn2 { Version = b.Version, Params = (int[])b.Params.Clone(), Console = 0, ConsoleName = "NES", Core = 0, Channel = ch, Mode = "direct" };
            if (preset && st.Params.Length > 5) st.Params[5] = (st.Params[5] & ~3) | 2;      // a PSG preset has no duty: the PSG's square, 50% (as the plugin plays it)
            if (preset && st.Params.Length > 7) st.Params[7] = 0;                             // nor the NES's short noise
            // the duty goes to the converter (its 0xD3, which the game reads and the PSG cannot play); Coarse/Fine as VRUN NES (pitch opcodes)
            if (ch < 2 && st.Params.Length > 5 && (st.Params[5] & 3) != 2) notHalf += ins.Notes.Count(n => !n.Slide);
            if (st.Params.Length > 3 && (st.Params[3] != 0 || st.Params[4] != 0)) detuned += ins.Notes.Count(n => !n.Slide);
            var copy = new FlpInstrument { Rack = ins.Rack, Name = ins.Name, Plugin = ins.Plugin, Type = ins.Type, Bn2 = st };
            if (approx != null) FlpPresetApprox.Attach(copy, approx);
            var mono = FlpMono.Resolve([ins]);
            foreach (var seg in mono.Segs)
            {
                double c = seg.V.PitchAt(seg.From);
                if (ch == 2)
                {
                    // at FL's key (TriangleAtKey); raised whole octaves for the whole note when its start is under the PSG's range, a slide below that stops
                    double hz = 440 * Math.Pow(2, (c / 100 - (triangleAtKey ? 0 : 12) - 69) / 12), lowHz = hz;
                    for (int t = seg.From; t < seg.To; t += seg.V.Glides.Count > 0 ? 1 : Math.Max(1, seg.To - seg.From)) lowHz = Math.Min(lowHz, 440 * Math.Pow(2, (seg.V.PitchAt(t) / 100 - (triangleAtKey ? 0 : 12) - 69) / 12));
                    int k = 0; while (hz * (1 << k) < LowestHz) k++;
                    string at = $"{FlpFence.NoteName((int)Math.Round(c / 100))} at {seg.From / (double)Math.Max(1, song.Ppq) / 4 + 1:0.##} bars";
                    if (k > 0) { triRaised++; if (triWhere.Length == 0) triWhere = at + $" ({k} octave{(k > 1 ? "s" : "")} up)"; }
                    if (lowHz * (1 << k) < LowestHz * 0.999) { triStopped++; if (stopWhere.Length == 0) stopWhere = at; }
                    continue;
                }
                if (ch < 3 && 440 * Math.Pow(2, (c / 100 - 69) / 12) < LowestHz) { low++; if (lowWhere.Length == 0) lowWhere = $"{FlpFence.NoteName((int)Math.Round(c / 100))} on {Names[ch]} at {seg.From / (double)Math.Max(1, song.Ppq) / 4 + 1:0.##} bars"; }
                if (ch == 3) { noiseIdx.Add(VrunNesBackend.NoiseIndex(c - 6000)); if (seg.V.Level > 0) levelsNoise.Add(seg.V.Level); }
                else if (ch < 2 && seg.V.Level > 0) levels.Add(seg.V.Level);
            }
            foreach (var n in ins.Notes)
            {
                if (n.Slide) { slides++; if (FlpMutants.On(CtwSmsMutants.SlidesDropped)) continue; }
                else if (n.Fine != 120) { detuned++; worstOff = Math.Max(worstOff, Math.Abs(n.Fine - 120)); }
                copy.Notes.Add(n);
            }
            sub.Instruments.Add(copy);
        }
        o.Nes = VrunNesBackend.Build(sub, name, triangleAtKey);    // the triangle at FL's key, as VRUN NES's .flp songs (2026-11-13; nes_apu.c's N = 2 (period + 1) then sounds at the key)
        if (sub.Instruments.Count == 0)
        { F.Add(new FenceLine("channels", FenceLevel.Over, "NOTHING for CTW SMS: no BrokenNes2 instance on the Master System console in Direct mode with notes on tone 1, tone 2, tone 3 (Triangle) or noise")); return o; }
        foreach (var c in o.Nes.Caveats)
            if (!c.StartsWith("NOTHING for VRUN NES")) F.Add(new FenceLine("VRUN engine", FenceLevel.Note, c.Replace("square 1", "tone 1").Replace("square 2", "tone 2")));
        o.Seconds = o.Nes.Seconds;
        if (Room is not { } rm) { F.Add(new FenceLine("bytes", FenceLevel.Over, "the song room is unknown: " + RoomProblem)); return o; }
        o.Build = SongConvert.Convert(o.Nes.Midi, out var why, rm.Min(), triangleAtKey);
        if (o.Build == null) { F.Add(new FenceLine("converter", FenceLevel.Over, "VRUN's converter refused the song: " + why)); return o; }
        foreach (var l in o.Build.Facts.Lines()) F.Add(new FenceLine("converter", FenceLevel.Ok, l));
        foreach (var l in VrunNesBackend.ChannelChoice(o.Nes, o.Build.Facts)) F.Add(new FenceLine("channels", FenceLevel.Note, l));
        var used = o.Nes.Parts.Select(p => $"{Names[p.NesChannel]} <- {p.Instruments} ({p.Notes} notes)").ToList();
        F.Add(new FenceLine("channels", used.Count > 0 ? FenceLevel.Ok : FenceLevel.Over, used.Count > 0 ? string.Join(", ", used) + "; the game has 4 channels (VRUN NES's), one note at a time each"
            : "NOTHING for CTW SMS: no BrokenNes2 instance on the Master System console in Direct mode with notes on tone 1, tone 2, tone 3 (Triangle) or noise"));

        // the bytes the template's decoders cannot play: refused, never played wrong. They know notes, waits, volume, the long wait $D0 n, the bends $D1/$D2 lo hi (squares and
        // triangle: the title song's bend decoders) and the duty $D3 d (only as a square's first opcode, as VRUN's converter writes it), and the loop $FF
        for (int c = 0; c < 4; c++)
        {
            var st = o.Build.Channels[c];
            if (st.Length > rm[c]) F.Add(new FenceLine("bytes", FenceLevel.Over, $"{Names[c]}: {st.Length} bytes, the game's song room is {rm[c]}: {st.Length - rm[c]} over"));
            string? bad = BadOpcode(st, c);
            if (bad != null) F.Add(new FenceLine("opcodes", FenceLevel.Over, $"{Names[c]}: {bad}"));
        }
        F.Add(new FenceLine("bytes", FenceLevel.Ok, $"{string.Join(" + ", o.Build.Channels.Select(x => x.Length))} bytes (the song room: {string.Join(" / ", rm)} bytes a channel, measured in the game's data banks; VRUN's converter says what it cut)"));
        if (slides > 0) F.Add(new FenceLine("slides", FenceLevel.Ok, $"{slides} slide notes play as VRUN NES plays them (its bend decoders: the period glides each frame, then nes_apu.c's PSG mapping)"));
        if (detuned > 0) F.Add(new FenceLine("tuning", FenceLevel.Ok, $"{detuned} notes are off their semitone (note fine pitch up to {worstOff:0} cents, or Coarse/Fine on the instance): played bent, as VRUN NES plays them"));
        if (low > 0) F.Add(new FenceLine("note range", FenceLevel.Over, $"{low} notes are under the PSG's lowest tone ({LowestHz:0.0} Hz, about A2): the game plays them an octave (or more) up, the plugin's Master System chip leaves them silent; the first is {lowWhere}"));
        else F.Add(new FenceLine("note range", FenceLevel.Ok, "every tone 1 / 2 note is A2 (110 Hz) or higher: inside the PSG's range"));
        // the triangle sounds at FL's key, as VRUN NES's .flp songs (TriangleAtKey, 2026-11-13)
        if (o.Nes.Parts.Any(p => p.NesChannel == 2)) F.Add(new FenceLine("triangle octave", FenceLevel.Ok, "tone 3 (the triangle) plays at FL's key, as the plugin and VRUN NES play it"));
        if (triRaised > 0 && !FlpMutants.On(CtwSmsMutants.TriangleHalvedPerFrame))
            F.Add(new FenceLine("note range", FenceLevel.Over, $"{triRaised} tone 3 (triangle) notes are under the PSG's lowest tone ({LowestHz:0.0} Hz) at FL's key: the game raises each by whole octaves for the whole note, slides included; the first is {triWhere}"));
        if (triStopped > 0 && !FlpMutants.On(CtwSmsMutants.TriangleHalvedPerFrame))
            F.Add(new FenceLine("note range", FenceLevel.Over, $"{triStopped} tone 3 (triangle) notes slide below their octave's PSG range: the slide stops at the lowest tone ({LowestHz:0.0} Hz) instead of jumping an octave; the first is {stopWhere}"));
        F.Add(notHalf > 0
            ? new FenceLine("duty", FenceLevel.Note, $"{notHalf} tone 1 / 2 notes have a Duty other than 50%: the game reads VRUN's duty opcode, but the PSG has no duty: they play at 50% (as the plugin's Master System chip plays them)")
            : new FenceLine("duty", FenceLevel.Ok, "the PSG's squares are 50%, as the plugin's Master System chip plays them"));
        var merged = levels.GroupBy(l => Vol2Att[l]).Where(g => g.Count() > 1).Select(g => string.Join("/", g)).ToList();
        F.Add(new FenceLine("volume", merged.Count > 0 ? FenceLevel.Note : FenceLevel.Ok, $"tone 1 / 2 levels {string.Join(", ", levels)} -> the PSG's 2 dB steps" + (merged.Count > 0 ? $": {string.Join(", ", merged)} sound the same" : "") + "; tone 3 (the triangle) a fixed attenuation 3 (6 dB under full)"));
        var nlev = new SortedSet<int>(levelsNoise);
        if (nlev.Count > 0)
            F.Add(new FenceLine("volume", nlev.Count(l => l < 15) > 0 && nlev.GroupBy(l => Vol2Att[l]).Any(g => g.Count() > 1) ? FenceLevel.Note : FenceLevel.Ok,
                nlev.All(l => l == 15) ? "the noise hits at full level, as in FL" : $"noise levels {string.Join(", ", nlev)} play (VRUN's noise volume, opcode $D4) -> the PSG's 2 dB steps" +
                (nlev.GroupBy(l => Vol2Att[l]).Where(g => g.Count() > 1).Select(g => string.Join("/", g)).ToList() is { Count: > 0 } nm ? $": {string.Join(", ", nm)} sound the same" : "")));
        if (noiseIdx.Count > 0)
        {
            int rates = noiseIdx.Select(NoiseRate).Distinct().Count();
            F.Add(new FenceLine("noise", rates < noiseIdx.Count ? FenceLevel.Note : FenceLevel.Ok, $"{noiseIdx.Count} noise periods -> the PSG's 3 rates ({rates} used)" + (rates < noiseIdx.Count ? $": {noiseIdx.Count - rates} of them merge" : "")));
        }
        F.Add(new FenceLine("timing", FenceLevel.Note, $"VRUN's engine counts NES frames; the Master System's frame is {Fps:0.000} Hz: the song plays {(1 - Fps / NesFps) * 100:0.00}% slower ({o.Seconds * NesFps / Fps:0.00} s instead of {o.Seconds:0.00} s)"));
        F.Add(new FenceLine("loop", FenceLevel.Ok, "each channel loops at its own end (VRUN's converter makes them the song's length)"));
        return o;
    }

    /// <summary>A Presets-chip instance (docs/projects/flp-music-pipeline.md section 9): the PSG voice its Master System preset is (tone 0-2 or the noise, 3) with its fence
    /// lines, or Over = true with the line that says why the game cannot play it (another console, the FM unit, damaged, no preset). A PSG preset has no audible fields
    /// (SegaInstruments: the plugin plays the plain PSG from FL's pitch and level), so the voice it is IS what the game can carry of it.</summary>
    private static (int Channel, bool Over) PresetVoice(FlpBn2 b, string who, FlpInstrument ins, List<FenceLine> F, out FlpPresetApprox? approx)
    {
        approx = null;
        void Out(string why) { if (!FlpMutants.On(CtwSmsMutants.PresetFenceSilent)) F.Add(new FenceLine("preset", FenceLevel.Over, $"{who} {why}: its {ins.Notes.Count} notes are not in the game")); }
        if (b.Preset == null) { Out("is on a Presets chip with no preset in its state (no PRS1 section)"); return (0, true); }
        string? problem = b.Preset.Ok ? null : b.Preset.Problem;
        var pd = b.Preset.Ok ? BrokenNes2.Presets.PresetFile.Read(b.Preset.File).Data : null;
        if (b.Preset.Ok && pd == null) problem = "not a readable preset file";
        var play = pd != null ? BrokenNes2.Presets.PresetPlayable.Build(pd, out problem) : null;
        if (play == null) { Out($"plays a damaged preset ({problem})"); return (0, true); }
        string name = pd!.Name.Length > 0 ? pd.Name : pd.InstrumentName;
        if (play.Console != BrokenNes2.Instruments.ConsoleId.MasterSystem || play.Kind is not (BrokenNes2.Instruments.InstrumentKind.PsgTones or BrokenNes2.Instruments.InstrumentKind.PsgNoise))
        {
            // the engine approximation (FlpPresetApprox): VRUN's engine on the PSG keeps the level steps and pitch steps; duty and short noise are lost on the PSG (50% squares, white noise)
            approx = FlpPresetApprox.Of(b, out problem);
            if (approx == null) { Out($"plays a preset that cannot be read ({problem})"); return (0, true); }
            int ac = approx.Role == 0 ? (b.Channel is 0 or 1 or 2 ? b.Channel : 0) : approx.Role > 0 ? approx.Role : b.Channel is >= 0 and <= 3 ? b.Channel : 0;
            var lost = new List<string>(approx.Lost);
            if (approx.Duty is >= 0 and not 2) lost.Add($"duty {VrunNesBackend.DutyNames[approx.Duty]}% (the PSG's squares are 50%)");
            if (approx.ShortNoise) lost.Add("short / periodic noise (the driver writes white noise)");
            if (ac == 2 && approx.Level != null) lost.Add("the level steps on tone 3 (VRUN's triangle has no volume: a fixed attenuation)");
            approx.Duty = 2; approx.ShortNoise = false;
            if (!FlpMutants.On(CtwSmsMutants.PresetFenceSilent))
                F.Add(new FenceLine("preset", FenceLevel.Note, $"{who}: {(play.Console == BrokenNes2.Instruments.ConsoleId.MasterSystem ? "Master System FM-unit" : BrokenNes2.Presets.PresetFile.ConsoleName(play.Console))} preset \"{name}\" approximated on {Names[ac]}: kept {(approx.Kept.Count > 0 ? string.Join(", ", approx.Kept) : "the notes")}" + (lost.Count > 0 ? $"; lost: {string.Join("; ", lost)}" : "")));
            return (ac, false);
        }
        if (play.Kind == BrokenNes2.Instruments.InstrumentKind.PsgNoise)
        {
            F.Add(new FenceLine("preset", FenceLevel.Ok, $"{who}: Master System PSG noise preset \"{name}\" on the noise: the plugin plays the PSG's noise from FL's pitch and level, the game alike"));
            if (!play.Sega!.IsWhiteNoise(play.InstrumentValue - 1) && !FlpMutants.On(CtwSmsMutants.PeriodicAsWhiteSilent))
                F.Add(new FenceLine("preset", FenceLevel.Note, $"{who}: the preset is PERIODIC noise (a buzz at the note's pitch in the plugin): the game's driver writes white noise only (as this back end's reading of nes_apu.c says; the game source was not re-read for this line), on the PSG's 3 fixed rates: it plays as a hiss"));
            return (3, false);
        }
        int ch = b.Channel is >= 0 and <= 2 ? b.Channel : 0;
        F.Add(new FenceLine("preset", FenceLevel.Ok, $"{who}: Master System PSG tones preset \"{name}\" on {Names[ch]}: the plugin plays it as the PSG's plain square from FL's pitch and level (its registers are not played), the game alike"));
        if (b.Channel != ch) F.Add(new FenceLine("preset", FenceLevel.Note, $"{who}: the instance's channel {(b.Channel == 4 ? "Mix" : b.Channel.ToString())} is not a tone: the preset plays on tone 1"));
        var mono = FlpMono.Resolve([ins]);
        if (mono.Cut > 0) F.Add(new FenceLine("preset", FenceLevel.Note, $"{who}: the preset plays up to 3 notes at once in the plugin (the PSG's three tones); the game's {Names[ch]} plays one at a time: {mono.Cut} notes are cut by a newer one"));
        return (ch, false);
    }

    /// <summary>Null when the template's decoders play every byte of channel c's stream, else what they would misread.</summary>
    public static string? BadOpcode(byte[] st, int c)
    {
        for (int i = 0; i < st.Length; i++)
        {
            int v = st[i];
            if (v < 0xD0 || v == 0xFF) continue;
            if (v == 0xD0) { i++; continue; }
            if ((v == 0xD1 || v == 0xD2) && c < 3) { i += 2; continue; }
            if (v == 0xD3 && c < 2 && i == 0) { i++; continue; }
            if (v == 0xD4 && c == 3 && i == 0) continue;
            return $"opcode ${v:X2} at byte {i}: the game's decoders know notes, waits, volume, bends (tones), the duty (a square's first opcode), the noise volume (the noise's first opcode) and the loop";
        }
        return null;
    }

    /// <summary>The test ROM: the game with its title song's streams replaced. Null with the reason when the song cannot be put there.</summary>
    public static byte[]? Patch(byte[] template, string manifest, CtwSmsSong s, out string? why)
    {
        why = null;
        if (s.Build == null) { why = "VRUN's converter refused the song"; return null; }
        Dictionary<string, string> m;
        try { m = VocGbBackend.Manifest(manifest, "bn2-flp-ctwsms", TemplateVersion); }
        catch (FormatException) { why = $"the CTW SMS template is not version {TemplateVersion} (this build needs its title song's bend, duty and noise-volume decoders and the measured song room)"; return null; }
        if (template.Length.ToString() != m["rom-bytes"]) { why = "the template is not the one its manifest describes"; return null; }
        if (s.Fence.Any(f => f.Rule == "opcodes" && f.Level == FenceLevel.Over)) { why = "the song has opcodes the game's decoders cannot play (see the fence)"; return null; }
        var rom = (byte[])template.Clone();
        string[] keys = ["square1", "square2", "triangle", "noise"];
        byte[] fill = [0xA1, 0xA2, 0xA3, 0xA4];
        for (int c = 0; c < 4; c++)
        {
            var (off, size) = VocGbBackend.Region(m, keys[c]);
            var st = s.Build.Channels[c];
            if (st.Length > size) { why = $"{Names[c]}: {st.Length} bytes, the game's song region is {size}"; return null; }
            for (int i = 0; i < size; i++) rom[off + i] = i < st.Length ? st[i] : fill[c];
        }
        // the Sega header's checksum (BIOS-checked on a real Master System): makesms wrote it for the template; recompute it the same way
        SegaChecksum(rom);
        return rom;
    }

    /// <summary>The "TMR SEGA" header at $7FF0: the checksum over $0000-$7FEF (the 32 KB size code the header says), little-endian at $7FFA.</summary>
    public static void SegaChecksum(byte[] rom)
    {
        if (rom.Length < 0x8000 || System.Text.Encoding.ASCII.GetString(rom, 0x7FF0, 8) != "TMR SEGA") return;
        int code = rom[0x7FFF] & 15;
        int end = code switch { 0xA => 0x1FF0, 0xB => 0x3FF0, 0xC => 0x7FF0, 0xD => 0xBFF0, 0xE => 0xFFFF, 0xF => 0x1FFFF, 0x0 => 0x3FFFF, 0x1 => 0x7FFFF, 0x2 => 0xFFFFF, _ => 0x7FF0 };
        int sum = 0;
        for (int i = 0; i < Math.Min(end, rom.Length); i++) if (i < 0x7FF0 || i >= 0x8000) sum += rom[i];
        rom[0x7FFA] = (byte)sum; rom[0x7FFB] = (byte)(sum >> 8);
    }
}
