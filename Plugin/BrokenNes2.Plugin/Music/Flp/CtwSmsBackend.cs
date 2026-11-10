// The music pipeline's CTW SMS back end: VRUN: Corrupt The World on the Master System (VRUN_CTW_SMS_dev) IS the VRUN NES game translated to C (tools/fab2c.py): its music is VRUN's NES
// music engine (the converter's streams, VRUN's plain decoders) whose APU writes nes_apu.c turns into SN76489 PSG writes once a frame. So the song is made as VRUN NES makes it (the
// BrokenNes2 instances on the MASTER SYSTEM console are read as the NES channels they sit on: Pulse 1 / Pulse 2 / Triangle / Noise = PSG tone 1 / tone 2 / tone 3 / noise, through
// VrunNesBackend and VRUN's own converter, Music/Vrun), and this file is the Master System's fence: what nes_apu.c and the translated decoders do with it (read in the game's source):
//   squares: PSG N = NES period + 1, halved while over 1023 (notes under about A2, 109 Hz, an octave or more up); no duty (the PSG is 50%); level -> 2 dB steps (vol2att: some merge);
//   triangle: N = 2 (period + 1) (the same halving), a fixed attenuation 3; noise: the PSG's 3 rates from the NES period index (0-3 high, 4-7 mid, 8-15 low), white noise;
//   no pitch bends or slides: the game's decoders are VRUN's plain ones (the bend decoders do not translate: fab2c has no SSF type), so the song is converted without bends, every
//   slide and every note off its semitone is said; the NES frame count runs on the Master System's 59.92 Hz frame (0.30% slower than the NES's 60.10 Hz).
// The test ROM is THE GAME with its title song's streams replaced (Resources/flpctwsms.sms, BrokenDev/tools/music/ctwsms/build-ctwsms.ps1): it boots to the title screen and plays it.
using BrokenNes2.Music;

namespace BrokenNes2.Music.Flp;

public sealed class CtwSmsSong
{
    public VrunNesSong Nes = null!;
    public SongBuild? Build;
    public List<FenceLine> Fence = new();
    public double Seconds;
    public IEnumerable<string> Caveats => Fence.Where(f => f.Fit != Fit.Ok).Select(f => f.ToString());
}

public static class CtwSmsBackend
{
    public const double Fps = 3579545.0 / (228 * 262), NesFps = 1789773.0 / (262.0 * (341.0 / 3.0));
    public static readonly string[] Names = ["tone 1 (square 1)", "tone 2 (square 2)", "tone 3 (triangle)", "noise"];
    /// <summary>nes_apu.c's level -> attenuation (tools/gen_tables.py vol2att).</summary>
    public static readonly int[] Vol2Att = [.. Enumerable.Range(0, 16).Select(v => v == 0 ? 15 : Math.Min(14, (int)Math.Round(-20 * Math.Log10(v / 15.0) / 2, MidpointRounding.ToEven)))];
    /// <summary>The lowest pitch a PSG tone plays (N = 1023).</summary>
    public static readonly double LowestHz = 3579545.0 / 32 / 1023;
    public const int Region = 1280;

    /// <summary>The PSG period nes_apu.c writes for a NES period on a square (triangle: doubled first), and how many times it was halved.</summary>
    public static (int N, int Halved) PsgPeriod(int nesPeriod, bool triangle)
    {
        int n = triangle ? (nesPeriod + 1) << 1 : nesPeriod + 1, h = 0;
        while (n > 1023) { n >>= 1; h++; }
        return (n, h);
    }

    public static int NoiseRate(int index) => index < 4 ? 0 : index < 8 ? 1 : 2;

    public static CtwSmsSong Build(FlpSongModel song, string name)
    {
        var o = new CtwSmsSong();
        var F = o.Fence;
        foreach (var c in song.Caveats) F.Add(new FenceLine("project", Fit.Changed, c));
        // the Master System instances, read as the NES channels they sit on; everything else is said here
        var sub = new FlpSongModel { Ppq = song.Ppq, TempoMilli = song.TempoMilli, End = song.End, Title = song.Title };
        int slides = 0, detuned = 0, low = 0; double worstOff = 0; string lowWhere = "";
        var noiseIdx = new SortedSet<int>();
        var levels = new SortedSet<int>();
        foreach (var ins in song.Instruments)
        {
            string who = $"channel {ins.Rack} \"{ins.Name}\"";
            if (ins.Bn2 is not { } b)
            {
                if (ins.Notes.Count > 0) F.Add(new FenceLine("instruments", Fit.Out, $"{who} is not BrokenNes2 ({(ins.Plugin.Length > 0 ? ins.Plugin : ins.Type == 0 ? "sampler" : "type " + ins.Type)}): its {ins.Notes.Count} notes are not in the game"));
                continue;
            }
            if (ins.Notes.Count == 0) continue;
            if (b.Mode != "direct") { F.Add(new FenceLine("instruments", Fit.Out, $"{who} is in ROM mode (it plays a game's channel, not notes): its {ins.Notes.Count} notes are not in the game")); continue; }
            if (b.Console != 3) { F.Add(new FenceLine("instruments", Fit.Out, $"{who} is a {b.ConsoleName} instrument: its {ins.Notes.Count} notes are not in this Master System song (that console's back end plays it)")); continue; }
            if (b.Channel is < 0 or > 3) { F.Add(new FenceLine("channels", Fit.Out, $"{who}: channel {(b.Channel == 4 ? "Mix" : b.Channel.ToString())} is not tone 1 / tone 2 / tone 3 / noise: its {ins.Notes.Count} notes are not in the game")); continue; }
            var st = new FlpBn2 { Version = b.Version, Params = (int[])b.Params.Clone(), Console = 0, ConsoleName = "NES", Core = 0, Channel = b.Channel, Mode = "direct" };
            if (st.Params.Length > 5) st.Params[5] = 2;           // (no duty on the PSG: VRUN NES's duty caveat is not this game's)
            if (st.Params.Length > 3) { if (st.Params[3] != 0 || st.Params[4] != 0) detuned += ins.Notes.Count(n => !n.Slide); st.Params[3] = 0; st.Params[4] = 0; }
            var copy = new FlpInstrument { Rack = ins.Rack, Name = ins.Name, Plugin = ins.Plugin, Type = ins.Type, Bn2 = st };
            var mono = FlpMonoChannel.Of([ins]);
            foreach (var seg in mono.Segments)
            {
                double c = seg.Voice.PitchAt(seg.From);
                if (b.Channel < 3 && 440 * Math.Pow(2, (c / 100 - 69) / 12) < LowestHz) { low++; if (lowWhere.Length == 0) lowWhere = $"{FlpFence.NoteName((int)Math.Round(c / 100))} on {Names[b.Channel]} at {seg.From / (double)Math.Max(1, song.Ppq) / 4 + 1:0.##} bars"; }
                if (b.Channel == 3) noiseIdx.Add(VrunNesBackend.NoiseIndex(c - 6000));
                else if (b.Channel < 2 && seg.Voice.Level > 0) levels.Add(seg.Voice.Level);
            }
            foreach (var n in ins.Notes)
            {
                if (n.Slide) { slides++; continue; }
                if (n.Fine != 120) { detuned++; worstOff = Math.Max(worstOff, Math.Abs(n.Fine - 120)); }
                copy.Notes.Add(n with { Fine = 120 });
            }
            sub.Instruments.Add(copy);
        }
        o.Nes = VrunNesBackend.Build(sub, name);
        if (sub.Instruments.Count == 0)
        { F.Add(new FenceLine("channels", Fit.Out, "NOTHING for CTW SMS: no BrokenNes2 instance on the Master System console in Direct mode with notes on tone 1, tone 2, tone 3 (Triangle) or noise")); return o; }
        foreach (var c in o.Nes.Caveats)
            if (!c.StartsWith("NOTHING for VRUN NES")) F.Add(new FenceLine("VRUN engine", Fit.Changed, c.Replace("square 1", "tone 1").Replace("square 2", "tone 2")));
        o.Seconds = o.Nes.Seconds;
        o.Build = SongConvert.Convert(o.Nes.Midi, out var why);
        if (o.Build == null) { F.Add(new FenceLine("converter", Fit.Out, "VRUN's converter refused the song: " + why)); return o; }
        foreach (var l in o.Build.Facts.Lines()) F.Add(new FenceLine("converter", Fit.Ok, l));
        foreach (var l in VrunNesBackend.ChannelChoice(o.Nes, o.Build.Facts)) F.Add(new FenceLine("channels", Fit.Changed, l));
        var used = o.Nes.Parts.Select(p => $"{Names[p.NesChannel]} <- {p.Instruments} ({p.Notes} notes)").ToList();
        F.Add(new FenceLine("channels", used.Count > 0 ? Fit.Ok : Fit.Out, used.Count > 0 ? string.Join(", ", used) + "; the game has 4 channels (VRUN NES's), one note at a time each"
            : "NOTHING for CTW SMS: no BrokenNes2 instance on the Master System console in Direct mode with notes on tone 1, tone 2, tone 3 (Triangle) or noise"));

        // the bytes the plain decoders cannot play (a bend or any opcode past $D0 is their loop): refused, never played wrong
        for (int c = 0; c < 4; c++)
        {
            var st = o.Build.Channels[c];
            if (st.Length > Region) F.Add(new FenceLine("bytes", Fit.Out, $"{Names[c]}: {st.Length} bytes, the game's song region is {Region}: {st.Length - Region} over"));
            for (int i = 0; i < st.Length; i++)
            {
                int v = st[i];
                if (v == 0xD0) { i++; continue; }
                if (v > 0xD0 && v != 0xFF) { F.Add(new FenceLine("opcodes", Fit.Out, $"{Names[c]}: opcode ${v:X2} at byte {i}: the game's decoders know only notes, waits, volume and the loop")); break; }
            }
        }
        F.Add(new FenceLine("bytes", Fit.Ok, $"{string.Join(" + ", o.Build.Channels.Select(x => x.Length))} bytes (VRUN's converter: at most {Region} a channel, it says what it cut)"));
        if (slides > 0) F.Add(new FenceLine("slides", Fit.Out, $"{slides} slide notes are not played: CTW SMS's music decoders are VRUN's plain ones (no pitch bends: the bend decoders do not translate to the Master System, fab2c has no SSF type); each note keeps its first pitch"));
        if (detuned > 0) F.Add(new FenceLine("tuning", Fit.Changed, $"{detuned} notes are off their semitone (note fine pitch up to {worstOff:0} cents, or Coarse/Fine on the instance): played on the nearest semitone (no bends in this game)"));
        if (low > 0) F.Add(new FenceLine("note range", Fit.Out, $"{low} notes are under the PSG's lowest tone ({LowestHz:0.0} Hz, about A2): the game plays them an octave (or more) up, the plugin's Master System chip leaves them silent; the first is {lowWhere}"));
        else F.Add(new FenceLine("note range", Fit.Ok, "every tone note is A2 (110 Hz) or higher: inside the PSG's range"));
        F.Add(new FenceLine("duty", Fit.Ok, "the PSG's squares are 50%, as the plugin's Master System chip plays them (Pulse duty is not used)"));
        var merged = levels.GroupBy(l => Vol2Att[l]).Where(g => g.Count() > 1).Select(g => string.Join("/", g)).ToList();
        F.Add(new FenceLine("volume", merged.Count > 0 ? Fit.Changed : Fit.Ok, $"tone 1 / 2 levels {string.Join(", ", levels)} -> the PSG's 2 dB steps" + (merged.Count > 0 ? $": {string.Join(", ", merged)} sound the same" : "") + "; tone 3 (the triangle) a fixed attenuation 3 (6 dB under full), the noise full"));
        if (noiseIdx.Count > 0)
        {
            int rates = noiseIdx.Select(NoiseRate).Distinct().Count();
            F.Add(new FenceLine("noise", rates < noiseIdx.Count ? Fit.Changed : Fit.Ok, $"{noiseIdx.Count} noise periods -> the PSG's 3 rates ({rates} used)" + (rates < noiseIdx.Count ? $": {noiseIdx.Count - rates} of them merge" : "")));
        }
        F.Add(new FenceLine("timing", Fit.Changed, $"VRUN's engine counts NES frames; the Master System's frame is {Fps:0.000} Hz: the song plays {(1 - Fps / NesFps) * 100:0.00}% slower ({o.Seconds * NesFps / Fps:0.00} s instead of {o.Seconds:0.00} s)"));
        F.Add(new FenceLine("loop", Fit.Ok, "each channel loops at its own end (VRUN's converter makes them the song's length)"));
        return o;
    }

    /// <summary>The test ROM: the game with its title song's streams replaced. Null with the reason when the song cannot be put there.</summary>
    public static byte[]? Patch(byte[] template, string manifest, CtwSmsSong s, out string? why)
    {
        why = null;
        if (s.Build == null) { why = "VRUN's converter refused the song"; return null; }
        var m = VocGbBackend.Manifest(manifest, "bn2-flp-ctwsms");
        if (template.Length.ToString() != m["rom-bytes"]) { why = "the template is not the one its manifest describes"; return null; }
        if (s.Fence.Any(f => f.Rule == "opcodes" && f.Fit == Fit.Out)) { why = "the song has opcodes the game's decoders cannot play (see the fence)"; return null; }
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
