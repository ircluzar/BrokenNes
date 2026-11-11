// The music pipeline's AdLib back end (2026-11-10, composing gaps part B): an FL project composed on the plugin's x86 PC console (BrokenNes2 instances on the AdLib chips, Direct mode,
// one instance = one OPL2 voice with its own patch) -> an OPL2 song for the DOS / Windows 95 games' own AdLib drivers.
//
//  * Super VRUN DOS / 95 (SuperVRUN_DOS_dev platform/dos/dos_snd.c, SuperVRUN_95_dev platform/win/win_snd.c): the blob oplconv.py writes for a converted module (header: tick rate x 256
//    u16, orders, 0, 3-byte entry offsets; stream: 1v off, 2v on, 3v lo hi freq, 4v tl level, 6v + 10 bytes the voice's own patch (added for this), 8n wait, 02 jump = the loop). Put at
//    music/adlib/<song>.opl in the game repo, it REPLACES that song's SNES-converted version (oplconv.py); every other song stays converted (build-supervrun.ps1 -Engine dos|95).
//  * Vaults DOS / 95: see VaultsAdlib (VaultsAdlibBackend.cs).
//
// What is read (as the plugin plays it, FlpMono): every BrokenNes2 instance in Direct mode on a PC chip; AdLib chips (PC_ADLIB*) = an FM voice, the patch = the instance's patch editor patch
// (OPC1) or its Instrument's bank patch (Pc/OplPatch.cs); an instance on a Presets chip with a preset = a voice whose patch is FITTED to the preset's sound (Pc/OplFit.cs, part C), the
// similarity in the fence. Everything else is said in the fence (no silent drops): NES-style instances, the speaker / Sound Blaster / Tandy / GUS / Covox chips, ROM mode, pan, too many voices.
// Pitch: FL key + note fine + Coarse/Fine + slides (FlpMono), as an F-number + block per tick (60 ticks a second); level: velocity and Volume as the carrier's attenuation (0.75 dB steps).
using BrokenNes2.Pc;

namespace BrokenNes2.Music.Flp;

/// <summary>Deliberate bugs for FruityHost flp-test (FlpMutants.Mask bits, high enough to stay clear of the other back ends').</summary>
public static class AdlibMutants { public const int PatchOrderSwapped = 1 << 27, OctaveUp = 1 << 28, FenceBlindChip = 1 << 29; }

public sealed record AdlibVoice(int Voice, string Instrument, string PatchFrom, byte[] Patch, int Notes, double? Similarity);

public sealed record AdlibSong(byte[] Blob, FenceReport Fence, List<AdlibVoice> Voices, double Seconds, int Ticks);

/// <summary>A voice's events per 60 Hz tick (shared by the Super VRUN and Vaults writers).</summary>
public sealed record AdlibEvent(int Tick, int Voice, int Op, int A, int B);   // Op: 1 off, 2 on, 3 freq (A = lo, B = hi: block << 2 | fnum >> 8), 4 level (A = tl)

public static class AdlibBackend
{
    /// <summary>The plugin's PC chips (Bn2Consoles.Pc.cs): chip index FirstPcChip + i (append-only; the plugin's `pc` check pins it). The first 6 are AdLib FM engines.</summary>
    public const int FirstPcChip = 77;
    public static readonly string[] PcChips = ["PC_ADLIB", "PC_ADLIB_M036", "PC_ADLIB_D072", "PC_ADLIB_D074C", "PC_ADLIB_D074F", "PC_ADLIB_NUK174", "PC_SPEAKER", "PC_SB_D072", "PC_TANDY_D072", "PC_GUS_D074", "PC_COVOX"];
    public const int AdlibChips = 6;
    public const double TickHz = 60;

    public static bool IsPcChip(int core) => core >= FirstPcChip && core < FirstPcChip + PcChips.Length;
    public static bool IsPresetChip(int core) => core >= FlpPresetSound.FirstPresetChip && core <= FlpPresetSound.LastPresetChip;

    /// <summary>Whether a song is composed for the AdLib (any instance on an AdLib chip or on a PC chip at all): then it replaces the converted song.</summary>
    public static bool IsAdlibSong(FlpSongModel song) => song.Instruments.Any(i => i.Bn2 is { } b && IsPcChip(b.Core));

    /// <summary>F-number + block of a frequency (the drivers' and oplconv.py's rule: the lowest block whose F-number fits 10 bits). Returns (B0 low bits, A0).</summary>
    public static (int Hi, int Lo) Freq(double hz)
    {
        if (hz <= 0) return (0, 0);
        if (FlpMutants.On(AdlibMutants.OctaveUp)) hz *= 2;
        for (int block = 0; block < 8; block++)
        {
            int fn = (int)Math.Round(hz * (1 << (20 - block)) / 49716.0);
            if (fn < 1024) return (block << 2 | fn >> 8, fn & 255);
        }
        return (7 << 2 | 3, 255);
    }

    public static double Hz(double centsFromMidi0) => 440.0 * Math.Pow(2, (centsFromMidi0 - 6900) / 1200);

    /// <summary>The level (carrier TL) of a note: the patch's own carrier level + the velocity and Volume as attenuation (20 log10, 0.75 dB a step).</summary>
    public static int Tl(byte[] patch, int velocity, double volume)
    {
        double a = Math.Clamp(velocity / 127.0, 1e-4, 1) * Math.Clamp(volume, 1e-4, 1);
        return Math.Clamp((patch[3] & 0x3F) + (int)Math.Round(-20 * Math.Log10(a) / 0.75), 0, 63);
    }

    public static int TickOf(FlpSongModel song, double flTick) => (int)Math.Round(flTick / Math.Max(1, song.Ppq) * 60.0 / Math.Max(1, song.Tempo) * TickHz);

    /// <summary>The voices, their patches and their events; the fence lines about what is read. <paramref name="maxVoices"/> voices are written (more is OVER and dropped, said).</summary>
    public static (List<AdlibVoice> Voices, List<AdlibEvent> Events, int End) Read(FlpSongModel song, FenceReport f, int maxVoices, string driver)
    {
        var voices = new List<AdlibVoice>();
        var events = new List<AdlibEvent>();
        int pan = 0, end = 0;
        foreach (var ins in song.Instruments)
        {
            string who = $"channel {ins.Rack} \"{ins.Name}\"";
            if (ins.Bn2 is not { } b) { if (ins.Notes.Count > 0) f.Note("not BrokenNes2", $"{who}: {ins.Plugin} is not a BrokenNes2 instance: its {ins.Notes.Count} notes are not in the AdLib song"); continue; }
            if (ins.Notes.Count == 0) continue;
            if (b.Mode != "direct") { f.Over("mode", $"{who}: ROM mode: its notes are not in the AdLib song (compose on the PC console in Direct mode)"); continue; }
            byte[]? patch = null; string from = ""; double? sim = null;
            if (IsPcChip(b.Core))
            {
                int k = b.Core - FirstPcChip;
                if (k >= AdlibChips && !FlpMutants.On(AdlibMutants.FenceBlindChip)) { f.Over("chip", $"{who}: {PcChips[k]} is not an AdLib voice: {driver} plays the AdLib only (its {ins.Notes.Count} notes are not in the song)"); continue; }
                if (k >= AdlibChips) k = 0;
                if (k > 0) f.Note("engine", $"{who}: composed on {PcChips[k]} (an emulator's OPL2 core, art); the game's AdLib / OPL3 plays the patch as a real chip does (ymfm in BrokenNes)");
                var custom = b.Sections.FirstOrDefault(s => s.Tag == "OPC1").Data;
                if (custom is { Length: 12 } && custom[0] == 1) { patch = custom[1..]; from = "its own patch (patch editor)"; }
                else
                {
                    var bp = OplBank.Patches[OplBank.Clamp(b.Param(11))];
                    if (bp.Snare) { f.Over("rhythm", $"{who}: \"{bp.Name}\" is the rhythm-mode snare: {driver} has no rhythm mode (not played)"); continue; }
                    patch = bp.Regs; from = $"bank patch {OplBank.Clamp(b.Param(11))} \"{bp.Name}\"";
                }
            }
            else if (IsPresetChip(b.Core) && b.Preset is { } pr)
            {
                var pcm = FlpPresetSound.Of(pr, out string? why);
                if (pcm == null) { f.Over("preset", $"{who}: its preset cannot be played ({why}): not in the song"); continue; }
                double f0 = FlpPresetSound.MiddleC * pcm.SampleRate / pcm.C5;
                int n = Math.Min(pcm.SampleRate / 2, Math.Max(pcm.Pcm.Length, 1));
                var t = new float[n];
                for (int i = 0; i < n; i++)
                {
                    int j = i;
                    if (j >= pcm.Pcm.Length) j = pcm.Loops ? pcm.LoopStart + (j - pcm.LoopStart) % Math.Max(1, pcm.Pcm.Length - pcm.LoopStart) : -1;
                    t[i] = j < 0 ? 0 : pcm.Pcm[j] / 32768f;
                }
                var fit = OplFit.Fit(t, pcm.SampleRate, f0, budget: 400);
                patch = fit.Patch; sim = fit.Similarity; from = $"fitted to its preset \"{pcm.Name}\" ({fit.Similarity:0.0}% alike, from {fit.StartedFrom})";
                f.Lines.Add(new FenceLine("preset fit", fit.Similarity >= 80 ? FenceLevel.Ok : FenceLevel.Note, $"{who}: the preset \"{pcm.Name}\" becomes an OPL2 patch {fit.Similarity:0.0}% alike (spectrum {fit.SpectrumDb:0.0} dB, envelope {fit.EnvelopeDiff:0.00} apart)"));
            }
            else { f.Note("not AdLib", $"{who}: a {b.ConsoleName} instance: this song is composed for the AdLib, its {ins.Notes.Count} notes are not in it (compose it on a PC AdLib chip)"); continue; }

            if (voices.Count >= maxVoices) { f.Over("voices", $"{who}: {driver} has {maxVoices} music voices: this instance is voice {voices.Count + 1}, not played"); continue; }
            int v = voices.Count;
            if (b.Param(2) != 0) pan++;
            var part = FlpMono.Resolve([ins]);
            double volume = Math.Clamp(b.Param(1, 1000) / 1000.0, 0, 1);
            int lastTick = -1;
            foreach (var seg in part.Segs)
            {
                int t0 = TickOf(song, seg.From), t1 = Math.Max(t0 + 1, TickOf(song, seg.To));
                bool fresh = seg.From == seg.V.Start;
                var (hi, lo) = Freq(Hz(seg.V.PitchAt(seg.From)));
                events.Add(new(t0, v, 4, Tl(patch, seg.V.Velocity, volume), 0));
                events.Add(new(t0, v, 3, lo, hi));
                events.Add(new(t0, v, 2, 0, 0));       // (a note the newer one had cut resumes with a key-on: a retrigger, the OPL2 has no resume)
                for (int t = t0 + 1; t < t1; t++)
                {
                    double fl = song.Ppq * song.Tempo / 60.0 * t / TickHz;   // the FL tick of this 60 Hz tick
                    var (h2, l2) = Freq(Hz(seg.V.PitchAt(fl)));
                    if (h2 != hi || l2 != lo) { events.Add(new(t, v, 3, l2, h2)); hi = h2; lo = l2; }
                }
                events.Add(new(t1, v, 1, 0, 0));
                lastTick = t1; end = Math.Max(end, t1);
            }
            if (part.Cut > 0) f.Note("mono", $"{who}: {part.Cut} notes cut by a newer one (one note at a time per instance, as the plugin plays it)");
            voices.Add(new AdlibVoice(v, ins.Name, from, patch, part.Voices.Count, sim));
        }
        // an off at the same tick as the next on of the voice is dropped (the on retriggers: off + on)
        events = events.OrderBy(e => e.Tick).ThenBy(e => e.Voice).ThenBy(e => e.Op == 1 ? 0 : e.Op == 4 ? 1 : e.Op == 3 ? 2 : 3).ToList();
        events = events.Where((e, i) => !(e.Op == 1 && events.Skip(i + 1).TakeWhile(x => x.Tick == e.Tick).Any(x => x.Voice == e.Voice && x.Op == 2))).ToList();
        if (pan > 0) f.Note("pan", $"{pan} instances are panned: the OPL2 is mono (not played)");
        f.Note("timing", $"notes and slides are placed on the driver's {TickHz:0} Hz ticks (up to {500 / TickHz:0.0} ms from FL's time); slides move once a tick");
        return (voices, events, end);
    }

    /// <summary>Super VRUN DOS / 95: the song blob (see the file header), looping. Null when the song is not composed for the AdLib.</summary>
    public static AdlibSong? BuildSuperVrun(FlpSongModel song, string name)
    {
        if (!IsAdlibSong(song)) return null;
        var f = new FenceReport("Super VRUN DOS (AdLib, composed in FL)");
        foreach (var c in song.Caveats) f.Note("reader", c);
        f.Lines.AddRange(FlpFence.ChannelVolume(song));
        // dos_snd.c: voices 0-6 tonal (6 is borrowed by the sound effects while one plays), 7 noise, 8 percussion: an FL song may use 0-8 (all are plain voices in the stream)
        var (voices, events, end) = Read(song, f, 9, "Super VRUN's AdLib driver");
        if (voices.Count >= 7) f.Note("sfx", $"voice 7 (the 7th instance, \"{voices[6].Instrument}\") is borrowed by the game's sound effects while one plays (dos_snd.c SFX_V): it is silent then");
        f.Ok("voices", $"{voices.Count} of 9 voices: " + string.Join("; ", voices.Select(v => $"{v.Voice + 1} \"{v.Instrument}\" = {v.PatchFrom}")));
        var s = new List<byte>();
        foreach (var v in voices) { s.Add((byte)(0x60 | v.Voice)); s.AddRange(SvPatchBytes(v.Patch)); }
        int now = 0;
        foreach (var e in events)
        {
            Wait(s, e.Tick - now); now = e.Tick;
            switch (e.Op)
            {
                case 1: s.Add((byte)(0x10 | e.Voice)); break;
                case 2: s.Add((byte)(0x20 | e.Voice)); break;
                case 3: s.Add((byte)(0x30 | e.Voice)); s.Add((byte)e.A); s.Add((byte)e.B); break;
                case 4: s.Add((byte)(0x40 | e.Voice)); s.Add((byte)e.A); break;
            }
        }
        // to the end of the last bar (4/4), then the loop to the start
        int barTicks = Math.Max(1, (int)Math.Round(4 * 60.0 / Math.Max(1, song.Tempo) * TickHz));
        int loopAt = Math.Max(end, (int)Math.Ceiling((double)TickOf(song, song.End) / barTicks) * barTicks);
        Wait(s, loopAt - now);
        s.AddRange([0x02, 0, 0, 0]);
        int rate = (int)Math.Round(TickHz * 256);
        var blob = new List<byte> { (byte)rate, (byte)(rate >> 8), 1, 0, 0, 0, 0 };
        blob.AddRange(s);
        f.Ok("size", $"{blob.Count} bytes, {loopAt / TickHz:0.00} s a loop, {events.Count(e => e.Op == 2)} key-ons");
        return new AdlibSong(blob.ToArray(), f, voices, loopAt / TickHz, loopAt);
    }

    /// <summary>A plugin patch (11 bytes, SBI order) in dos_snd.c's 10-byte order: mod 20, car 20, mod 40, mod 60, car 60, mod 80, car 80, mod E0, car E0, C0 (the carrier's level is the driver's).</summary>
    public static byte[] SvPatchBytes(byte[] p) => FlpMutants.On(AdlibMutants.PatchOrderSwapped) ? [p[1], p[0], p[2], p[4], p[5], p[6], p[7], (byte)(p[8] & 3), (byte)(p[9] & 3), (byte)(p[10] & 0x0F)] : [p[0], p[1], p[2], p[4], p[5], p[6], p[7], (byte)(p[8] & 3), (byte)(p[9] & 3), (byte)(p[10] & 0x0F)];

    private static void Wait(List<byte> s, int ticks)
    {
        // 8n..FF: wait (n & 7F) + 1 ticks, the driver counts n then runs at the next
        while (ticks > 0) { int n = Math.Min(128, ticks); s.Add((byte)(0x80 | (n - 1))); ticks -= n; }
    }
}
