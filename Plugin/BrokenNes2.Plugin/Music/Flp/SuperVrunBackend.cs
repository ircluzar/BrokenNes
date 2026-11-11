// The music pipeline's Super VRUN back end (docs/projects/flp-music-pipeline.md section 8): the neutral song (FlpSongModel) -> the MIDI layout Super VRUN's own music converter reads.
//
// Every Super VRUN build plays the SAME song modules: SuperVRUN_dev/snes-dev/supervrun/audio/<song>.it, made by tools/music/sf2it.py from a VRUN MIDI + MNES.sf2 (the SNES's SNESMOD driver plays
// them; WideMess plays the same soundbank), and read again by the ports' converters: SuperVRUN_MD_dev/tools/itconv.py (Genesis: YM2612 FM 1-5 + PSG 1-3 + PSG noise + the DAC) and
// SuperVRUN_DOS_dev/tools/oplconv.py (DOS and Windows 95: OPL2, 7 tonal voices + noise + percussion). So one file serves the five engines; what differs is the fence (each engine's limits).
//
// The MIDI (sf2it's "MNES" layout, read from sf2it.py's own parse_midi / note kinds): one MIDI channel per BrokenNes2 instance (9 skipped: sf2it plays channel 10 as DPCM), program = the NES
// sound (0-3 = pulse at 12.5 / 25 / 50 / 75 % duty, 4 = triangle, 5 = noise), noise key = 48 + NES period index (long mode) or 64 + index (short mode: sf2it reads key % 16 and key >= 64),
// velocity = 127 x sqrt(level / 15) (FluidSynth's amplitude is (velocity/127)^2: the NES level is linear), CC7 127, CC10 from Pan, an RPN 0 bend range and bends for Coarse/Fine, note fine
// pitch and FL's slides. One note at a time per instance, newest wins (FlpMono, the plugin's rule).
using BrokenNes2.Record;

namespace BrokenNes2.Music.Flp;

/// <summary>Deliberate bugs for FruityHost flp-test (bits of FlpMutants.Mask above phase 1's).</summary>
public static class SvMutants { public const int VoiceFenceBlind = 256, DutyAs50 = 512, ShortNoiseAsLong = 1024, VelocityLinear = 2048; }

public enum SvEngine { Snes, WideMess, Md, Dos, Win95 }

public sealed record SvPart(int MidiChannel, string Instrument, string Sound, int Notes, int Slides);

/// <summary>A preset instance's sample for sf2it.py (--presets): MIDI channel (1-16), the sound, the sample name the converters read (pcm..: itconv.py plays it on the DAC, oplconv.py as a tonal patch).</summary>
public sealed record SvPreset(int MidiChannel, string Instrument, string SampleName, FlpPresetPcm Sound);

public sealed record SuperVrunSong(byte[] Midi, FenceReport Fence, List<SvPart> Parts, double Seconds)
{
    public List<SvPreset> Presets { get; init; } = new();
}

public static class SuperVrunBackend
{
    public static readonly string[] Names = ["Super VRUN SNES", "Super VRUN WideMess", "Super VRUN MD", "Super VRUN DOS", "Super VRUN 95"];
    public static readonly string[] Ids = ["supervrun-snes", "supervrun-wms", "supervrun-md", "supervrun-dos", "supervrun-95"];
    public static SvEngine? Parse(string id) { int i = Array.IndexOf(Ids, id.ToLowerInvariant()); return i < 0 ? null : (SvEngine)i; }

    /// <summary>The engines' voice budgets (from the drivers, see the file header). Music voices; how many of them the game's sound effects take.</summary>
    public static (int Voices, int SfxShared, string What) Budget(SvEngine e) => e switch
    {
        SvEngine.Snes or SvEngine.WideMess => (8, 2, "SNESMOD has 8 channels (sf2it.py N_CH); the game's effects take channels 7-8 (SFX_CHANNELS)"),
        SvEngine.Md => (8, 0, "Genesis: tonal parts on FM 1-5 + PSG 1-3 (itconv.py assign_voices), one PSG noise, one DAC; effects use the DAC"),
        _ => (7, 1, "OPL2: 7 tonal voices + 1 noise + 1 percussion (oplconv.py TONAL); effects take voice 6 (dos_snd.c SFX_V)"),
    };

    private static readonly string[] SoundNames = ["pulse 12.5%", "pulse 25%", "pulse 50%", "pulse 75%", "triangle", "noise", "preset"];
    /// <summary>The MIDI program of a preset instance's notes (sf2it.py plays the channel's --presets sample for it).</summary>
    public const int PresetProgram = 6;
    /// <summary>Sound RAM a song may use ($1A00-$FE00, supervrun_fence.py's default budget).</summary>
    public const int SongAram = 0xFE00 - 0x1A00;

    public static SuperVrunSong Build(FlpSongModel song, string name, SvEngine engine)
    {
        var f = new FenceReport(Names[(int)engine]);
        foreach (var c in song.Caveats) f.Note("reader", c);
        f.Lines.AddRange(FlpFence.ChannelVolume(song));
        var used = new List<(FlpInstrument Ins, int Sound)>();
        var presetOf = new Dictionary<FlpInstrument, FlpPresetPcm>();
        foreach (var ins in song.Instruments)
        {
            string who = $"channel {ins.Rack} \"{ins.Name}\"";
            if (ins.Bn2 is not { } b)
            {
                if (ins.Notes.Count > 0) f.Over("instrument", $"{who} is not BrokenNes2 ({(ins.Plugin.Length > 0 ? ins.Plugin : ins.Type == 0 ? "sampler" : "type " + ins.Type)}): its {ins.Notes.Count} notes are not in the game");
                continue;
            }
            if (ins.Notes.Count == 0) continue;
            if (b.Preset != null && b.Core is >= FlpPresetSound.FirstPresetChip and <= FlpPresetSound.LastPresetChip && b.Mode == "direct")
            {
                if (FlpPresetSound.Of(b.Preset, out string? why) is not { } ps) { f.Over("preset", $"{who} plays a preset that cannot be built ({why}): its {ins.Notes.Count} notes are not in the game"); continue; }
                presetOf[ins] = ps;
                used.Add((ins, PresetProgram));
                continue;
            }
            if (b.Mode != "direct") { f.Over("instrument", $"{who} is in ROM mode: its {ins.Notes.Count} notes are not in the game"); continue; }
            if (b.Console != 0) { f.Over("instrument", $"{who} is a {b.ConsoleName} instrument: Super VRUN's songs are NES-style (pulse/triangle/noise), its {ins.Notes.Count} notes are not in the game"); continue; }
            if (b.Channel is < 0 or > 3) { f.Over("instrument", $"{who}: NES channel {(b.Channel == 4 ? "Mix/DMC" : b.Channel.ToString())} is not pulse/triangle/noise: its {ins.Notes.Count} notes are not in the game (DPCM is not built for .flp songs)"); continue; }
            if (b.Core != 0) f.Note("instrument", $"{who}: sound chip #{b.Core}, not FIX: the game plays its own NES-style samples");
            int sound = b.Channel <= 1 ? (FlpMutants.On(SvMutants.DutyAs50) ? 2 : b.Param(5) & 3) : b.Channel == 2 ? 4 : 5;
            used.Add((ins, sound));
        }
        if (used.Count > 15) f.Over("instruments", $"{used.Count} BrokenNes2 instances play: the converter's MIDI has 15 channels for them (10 is DPCM): instances after the 15th are not in the game");
        else if (used.Count > 0) f.Ok("instruments", $"{used.Count} BrokenNes2 instance{(used.Count == 1 ? "" : "s")} (15 fit)");

        int scale = Math.Max(1, (int)Math.Round(RecMidi.Ppq / (double)Math.Max(1, song.Ppq)));
        long T(double flTicks) => (long)Math.Round(flTicks * RecMidi.Ppq / Math.Max(1, song.Ppq), MidpointRounding.AwayFromZero);
        var tracks = new List<SmfTrack>();
        var parts = new List<SvPart>();
        var segsAll = new List<(int Sound, List<MonoSeg> Segs)>();
        int midiCh = 0;
        foreach (var (ins, sound) in used.Take(15))
        {
            if (midiCh == 9) midiCh++;
            int mc = midiCh++;
            var b = ins.Bn2!;
            string who = $"channel {ins.Rack} \"{ins.Name}\"";
            var part = FlpMono.Resolve([ins]);
            if (part.Empty > 0) f.Note("notes", $"{who}: {part.Empty} notes end where they start (length 0): silent in FL and in the game");
            if (part.LoneSlides > 0) f.Note("notes", $"{who}: {part.LoneSlides} slide notes have no note under them: they do nothing in FL either");
            if (part.Cut > 0) f.Note("mono", $"{who}: {part.Cut} notes overlap a newer one: one note at a time per instance, as the plugin plays them (the newest sounds)");
            int pan = b.Param(2);
            var trk = new SmfTrack { Name = ins.Name.Length > 0 ? ins.Name : SoundNames[sound], Instrument = "BrokenNes2 " + SoundNames[sound] };
            tracks.Add(trk);
            trk.Events.Add(new SmfEvent(0, 0, (byte)(0xC0 | mc), (byte)sound, 0));      // program change (RecMidi has no helper; a two-byte message, Data2 unused)
            trk.Events.Add(RecMidi.Cc(0, mc, 7, 127));
            trk.Events.Add(RecMidi.Cc(0, mc, 10, Math.Clamp((int)Math.Round(64 + pan * 0.63), 0, 127)));
            bool noise = sound == 5;
            if (sound == PresetProgram) PresetFence(f, engine, who, presetOf[ins]);
            int shortMode = b.Param(7) != 0 && !FlpMutants.On(SvMutants.ShortNoiseAsLong) ? 1 : 0;
            int KeyOf(MonoSeg s) => noise ? (shortMode == 1 ? 64 : 48) + VrunNesBackend.NoiseIndex(s.V.PitchAt(s.From) - 6000) : Math.Clamp((int)Math.Round(s.V.Base / 100, MidpointRounding.AwayFromZero), 0, 127);
            double maxBend = 0;
            if (!noise)
                foreach (var s in part.Segs)
                {
                    int key = KeyOf(s);
                    for (int t = s.From; t <= s.To; t += s.V.Glides.Count > 0 ? 1 : Math.Max(1, s.To - s.From)) maxBend = Math.Max(maxBend, Math.Abs(s.V.PitchAt(t) - key * 100));
                }
            int range = Math.Clamp((int)Math.Ceiling(maxBend / 100 - 1e-9), 2, 24);
            if (maxBend > 2400) f.Note("bend", $"{who}: a slide reaches {maxBend / 100:0.#} semitones from its note: bends are capped at 24 semitones");
            if (!noise) { trk.Events.AddRange(RecMidi.BendRange(0, mc, range)); trk.Events.Add(RecMidi.PitchBend(0, mc, 0)); }
            int lastBend = 0, notes = 0, silent = 0, lo = 999, hi = -1;
            void Bend(long tick, double cents)
            {
                int v = Math.Clamp((int)Math.Round(cents / (range * 100.0) * 8192, MidpointRounding.AwayFromZero), -8192, 8191);
                if (v != lastBend) { trk.Events.Add(RecMidi.PitchBend(tick, mc, v)); lastBend = v; }
            }
            var kept = new List<MonoSeg>();
            foreach (var s in part.Segs)
            {
                int level = s.V.Level;
                if (level == 0) { silent++; continue; }
                kept.Add(s);
                int key = KeyOf(s);
                if (!noise)
                {
                    Bend(T(s.From), s.V.PitchAt(s.From) - key * 100);
                    foreach (var g in s.V.Glides)
                    {
                        double a = Math.Max(g.Start, s.From), z = Math.Min(g.Start + g.Len, s.To);
                        for (double t = Math.Ceiling(a); t <= z; t += 1) Bend(T(t), s.V.PitchAt(t) - key * 100);
                        if (z > a && z < s.To) Bend(T(z), s.V.PitchAt(z) - key * 100);
                    }
                    lo = Math.Min(lo, key); hi = Math.Max(hi, key);
                }
                int vel = sound == 4 ? 127 : Math.Clamp((int)Math.Round(127 * (FlpMutants.On(SvMutants.VelocityLinear) ? level / 15.0 : Math.Sqrt(level / 15.0))), 1, 127);
                trk.Events.Add(RecMidi.On(T(s.From), mc, key, vel));
                trk.Events.Add(RecMidi.Off(T(s.To), mc, key));
                notes++;
            }
            if (silent > 0) f.Note("level", $"{who}: {silent} notes have level 0 (velocity x Volume): silent in FL and in the game");
            if (sound == 4 && kept.Any(s => s.V.Level < 15)) f.Note("level", $"{who}: triangle notes softer than full: the NES triangle has no volume, the game plays them at its one level (as FL's NES chip does)");
            segsAll.Add((sound, kept));
            parts.Add(new SvPart(mc + 1, ins.Name, SoundNames[sound], notes, part.Voices.Sum(v => v.Glides.Count)));
            if (sound != PresetProgram) EngineSoundFence(f, engine, who, sound, shortMode, pan, lo, hi);
            else if (hi >= 0) RangeFence(f, engine, who, lo, hi, presetOf[ins].C5);
        }

        // the voice budget: how many parts sound at once (the converter packs notes onto the engine's voices and drops or steals what does not fit: its own count comes after conversion)
        var (voices, sfx, what) = Budget(engine);
        var tonal = segsAll.Where(p => engine is SvEngine.Snes or SvEngine.WideMess || p.Sound != 5 && !(engine == SvEngine.Md && p.Sound == PresetProgram)).Select(p => (IEnumerable<MonoSeg>)p.Segs).ToList();
        var (peak, at) = FlpMono.Peak(tonal);
        string kind = engine is SvEngine.Snes or SvEngine.WideMess ? "parts" : "tonal parts";
        double Bar(int tick) => tick / (4.0 * Math.Max(1, song.Ppq)) + 1;
        if (peak > voices && !FlpMutants.On(SvMutants.VoiceFenceBlind)) f.Over("voices", $"up to {peak} {kind} sound at once (bar {Bar(at):0.##}), the engine has {voices}: {what}. The converter drops or steals the extra notes ({FlpMono.TicksOver(tonal, voices)} ticks over)");
        else if (sfx > 0 && peak > voices - sfx) f.Note("voices", $"up to {peak} {kind} sound at once (bar {Bar(at):0.##}): {voices} fit, but {what}: an effect cuts the least important note while it plays");
        else f.Ok("voices", $"at most {peak} {kind} at once ({voices} voices{(sfx > 0 ? $", {voices - sfx} free of effects" : "")})");
        if (engine is not SvEngine.Snes and not SvEngine.WideMess)
        {
            var nz = segsAll.Where(p => p.Sound == 5).Select(p => (IEnumerable<MonoSeg>)p.Segs).ToList();
            var (np, nat) = FlpMono.Peak(nz);
            if (np > 1) f.Over("noise", $"{np} noise parts at once (bar {Bar(nat):0.##}): the engine has one noise voice, the loudest / newest hit sounds");
            else if (np == 1) f.Ok("noise", "one noise part at a time (1 noise voice)");
        }

        if (song.Tempo < 40 || song.Tempo > 300) f.Note("tempo", $"tempo {song.Tempo:0.###} BPM: the converter's tick grid (55-100 Hz) may not fit it; its report says the drift");
        if (parts.Count == 0) f.Over("song", $"NOTHING for {Names[(int)engine]}: no BrokenNes2 instance on the NES console in Direct mode with notes on pulse 1, pulse 2, triangle or noise");
        f.Note("timing", "the converter snaps notes to its tick grid (SNESMOD ticks at 8000 / (20480 // T) Hz): its report says the drift");
        f.Note("loop", "the song loops to its start (the converter's loop is the module's end)");

        var presets = new List<SvPreset>();
        for (int i = 0; i < parts.Count; i++)
        {
            var pins = used[i].Ins;
            if (presetOf.TryGetValue(pins, out var ps)) presets.Add(new SvPreset(parts[i].MidiChannel, pins.Name, $"pcm{parts[i].MidiChannel}", ps));
        }
        if (presets.Count > 0 && engine is SvEngine.Snes or SvEngine.WideMess)
            SoundRamFence(f, presets.Sum(p => p.Sound.BrrBytes));

        int us = (int)Math.Round(60_000_000.0 / Math.Max(1e-3, song.Tempo), MidpointRounding.AwayFromZero);
        var midi = RecMidi.Write([(0, us), (T(song.End), us)], name, [$"made from {name}.flp by the BrokenNes music pipeline ({Names[(int)engine]} back end, MNES layout for sf2it.py)"], tracks);
        return new SuperVrunSong(midi, f, parts, song.End / (double)Math.Max(1, song.Ppq) * 60.0 / Math.Max(1e-3, song.Tempo)) { Presets = presets };
    }

    /// <summary>The presets' samples against the sound RAM a song has (SNES / WideMess; supervrun_fence.py adds the converter's own total of patterns + samples).</summary>
    public static void SoundRamFence(FenceReport f, int brr)
    {
        if (brr > SongAram && !FlpMutants.On(PsMutants.PresetFenceBlind)) f.Over("sound-ram", $"the presets' samples are {brr:N0} BRR bytes, more than the {SongAram:N0} bytes of sound RAM a song has ($1A00-$FE00): smconv cannot place the song");
        else if (brr > SongAram / 2) f.Note("sound-ram", $"the presets' samples are {brr:N0} BRR bytes of the {SongAram:N0} a song has: little room for the patterns and the effects loaded with it");
        else f.Ok("sound-ram", $"the presets' samples are {brr:N0} BRR bytes (of {SongAram:N0})");
    }

    /// <summary>Per sound, what each engine does with it (from the converters: sf2it.py's MNES samples, itconv.py's patches / PSG, oplconv.py's / dos_snd.c's patches).</summary>
    private static void EngineSoundFence(FenceReport f, SvEngine e, string who, int sound, int shortMode, int pan, int lo, int hi)
    {
        bool snes = e is SvEngine.Snes or SvEngine.WideMess;
        if (sound <= 3)
        {
            if (!snes && sound == 3) f.Note("duty", $"{who}: 75% duty plays the 25% patch ({(e == SvEngine.Md ? "itconv.py KIND_PATCH" : "oplconv.py PATCH")}: the same timbre on the NES)");
            if (!snes) f.Note("timbre", $"{who}: {SoundNames[sound]} becomes {(e == SvEngine.Md ? "a 4-operator FM patch (FM 1-5) or a PSG square (PSG 1-3: 50% only, notes below A2 / 110 Hz up an octave)" : "a 2-operator OPL2 patch")}: an imitation of the NES pulse");
            else f.Ok("timbre", $"{who}: {SoundNames[sound]} from the NES recordings (MNES.sf2)");
        }
        else if (sound == 4)
            f.Add(snes ? FenceLevel.Ok : FenceLevel.Note, "timbre", $"{who}: triangle{(snes ? " from the NES recordings" : e == SvEngine.Md ? ": FM patch 3 (triangle parts go to FM first)" : ": OPL2 patch 3")}");
        else
        {
            if (e == SvEngine.Md) f.Note("noise", $"{who}: NES noise periods play at the nearest of the SN76489's 3 noise rates{(shortMode == 1 ? "; short (metallic) mode is not played: white noise" : "")}");
            else if (!snes) f.Note("noise", $"{who}: noise is an OPL2 noise patch pitched from the NES period{(shortMode == 1 ? "; short (metallic) mode is not played" : "")}");
            else f.Ok("noise", $"{who}: noise rebuilt from the NES LFSR ({(shortMode == 1 ? "short" : "long")} mode)");
        }
        if (pan != 0)
        {
            if (snes) f.Ok("pan", $"{who}: Pan {pan} (SNESMOD pans per note)");
            else f.Note("pan", $"{who}: Pan {pan} is not played: {(e == SvEngine.Md ? "itconv.py writes no pan" : "OPL2 is mono")}, centred");
        }
        if (hi >= 0)
        {
            // SNESMOD: pitch = note x 64 + PitchBase must stay below 6912 (check_modules.py rule 3): about MIDI 108 with sf2it's samples; the converter's own check comes after
            if (snes && hi > 107) f.Over("range", $"{who}: notes up to MIDI {hi}: SNESMOD cannot reach octave 9 (check_modules.py): above about MIDI 107 the build fails");
            else if (e == SvEngine.Md && hi > 107) f.Note("range", $"{who}: notes up to MIDI {hi}: the YM2612 clamps at block 7 / fnum 2047");
            else if (!snes && e != SvEngine.Md && hi > 108) f.Note("range", $"{who}: notes up to MIDI {hi}: OPL2 clamps at block 7 / fnum 1023");
            else f.Ok("range", $"{who}: notes MIDI {lo}-{hi}");
        }
    }

    /// <summary>What each engine does with a preset's sample (FlpPresetSound): the SNES / WideMess play it as an instrument of the module (smconv re-encodes the 16-bit PCM as BRR), the Genesis on the DAC
    /// (itconv.py: pcm.. samples are DAC clips), DOS / 95 cannot (oplconv.py plays its default patch).</summary>
    private static void PresetFence(FenceReport f, SvEngine e, string who, FlpPresetPcm p)
    {
        string what = $"{p.Console} preset \"{p.Name}\"{(p.Corrupted ? " (corrupted)" : "")}";
        switch (e)
        {
            case SvEngine.Snes or SvEngine.WideMess:
                f.Ok("preset", $"{who}: {what} is the song's own instrument: {p.How}; {p.Pcm.Length} samples = {p.BrrBytes:N0} BRR bytes{(p.Exact ? " (smconv re-encodes the PCM as BRR: near, not bit-exact)" : "")}");
                if (p.Envelope.Length > 0) f.Note("envelope", $"{who}: the preset's {p.Envelope} is not played: SNESMOD holds the note's level while the key is down and cuts it at the note's end");
                if (p.Noise) f.Note("flags", $"{who}: the preset has the noise flag: in the plugin the S-DSP's noise generator replaces its sample; the game plays the sample (SNESMOD has no per-instrument noise)");
                if (p.Pmod) f.Note("flags", $"{who}: the preset has pitch modulation (by the voice before it): not in the game");
                if (!p.Loops) f.Note("loop",$"{who}: the preset's sample does not loop: notes longer than {p.Pcm.Length / 32.0:0} ms at middle C fall silent before their end, as in the plugin");
                break;
            case SvEngine.Md:
                if (FlpMutants.On(PsMutants.MdPresetAsPulse)) { f.Ok("preset", $"{who}: {what} as FM patch 2"); break; }
                f.Note("preset", $"{who}: {what} plays on the DAC (itconv.py: a pcm.. sample is a DAC clip at the note's pitch, 8-bit at the driver's DAC rate, about 7.6 kHz): the preset's own sound, but one clip at a time, shared with the game's effects, and a clip plays to its end (its loop repeated up to 1 s; the driver's DAC has no note-off)");
                break;
            default:
                f.Over("preset", $"{who}: {what} cannot be an OPL2 sound: oplconv.py plays it as its default 2-operator patch (the 50% pulse imitation), the preset's timbre is not in the game");
                break;
        }
    }

    private static void RangeFence(FenceReport f, SvEngine e, string who, int lo, int hi, double c5)
    {
        // the S-DSP's pitch register at a note is $1000 x c5 / 32000 x 2^((note-60)/12) and tops out at $3FFF
        double top = 60 + 12 * Math.Log2(0x3FFF / 4096.0 * 32000.0 / c5);
        if (e is SvEngine.Snes or SvEngine.WideMess && hi > top) f.Over("range", $"{who}: notes up to MIDI {hi}: the preset's sample cannot go above about MIDI {Math.Floor(top)} on the S-DSP (pitch $3FFF)");
        else f.Ok("range", $"{who}: notes MIDI {lo}-{hi}");
    }

    private static void Add(this FenceReport f, FenceLevel l, string rule, string text) => f.Lines.Add(new FenceLine(rule, l, text));
}
