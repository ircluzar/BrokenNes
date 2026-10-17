using NesEmulator.Sega;

namespace BrokenNes2.Instruments;

/// <summary>
/// The Sega instruments of a <see cref="SegaSnapshot"/> (the Runaway copy of a Master System, Game Gear or Genesis game's sound chips), numbered exactly as the plugin's Instrument picker numbers them (<see cref="SegaSnapshot.Instrument"/>):
/// <list type="bullet">
/// <item>PSG unit: 0 the three square tones, 1 white noise, 2 periodic noise;</item>
/// <item>YM2413 unit: 0 the user instrument (8 bytes, editable and played by <see cref="OpllSampler"/>), 1-15 the chip's ROM instruments (shown from the chip's own table, read-only: copy one into the user instrument to change it), 16-18 the PSG's;</item>
/// <item>YM2612 unit: 0-5 the six FM channels' patches (all 28 operator registers, algorithm, feedback, pan, AMS, FMS, and the chip's shared LFO), 6 and up the DAC sounds the game played (8-bit samples with their rate and loop), then the PSG's three.</item>
/// </list>
/// Which fields the samplers play: every YM2612 and YM2413 patch field is loaded onto the sampler's chip as it is (the carriers' total level is the base the note's velocity adds attenuation to), except the channel's pan (the sampler plays both
/// sides); the DAC's samples, rate, loop and loop pitch are all used; the PSG's register values are not (the sampler plays the PSG from FL's pitch and level; only the choice of white or periodic noise, which is the instrument you pick, matters).
/// </summary>
public sealed class SegaInstruments : IInstrumentSource
{
    private readonly SegaSnapshot snap;
    public SegaInstruments(SegaSnapshot snapshot) { snap = snapshot; }

    public ConsoleId Console => snap.Kind == SegaSnapshot.KindOpn ? ConsoleId.Genesis : ConsoleId.MasterSystem;
    public object Snapshot => snap;

    private const string PsgNote = "The sampler plays the PSG from FL's pitch and level; these registers are only what the game had.";

    // ---- schemas ----

    private static readonly string[] SsgNames = ["off", "on"];

    private static RegisterMap OpnMap { get; } = BuildOpnMap();

    private static RegisterMap BuildOpnMap()
    {
        var m = new RegisterMap();
        for (int n = 0; n < 4; n++)
        {
            string p = $"op{n + 1}.";
            int b = n * 7;
            m.Add(p + "dt", b + 0, 4, 3).Add(p + "mul", b + 0, 0, 4).Add(p + "tl", b + 1, 0, 7).Add(p + "rs", b + 2, 6, 2).Add(p + "ar", b + 2, 0, 5)
             .Add(p + "am", b + 3, 7, 1).Add(p + "d1r", b + 3, 0, 5).Add(p + "d2r", b + 4, 0, 5).Add(p + "sl", b + 5, 4, 4).Add(p + "rr", b + 5, 0, 4)
             .Add(p + "ssg.on", b + 6, 3, 1).Add(p + "ssg.mode", b + 6, 0, 3);
        }
        return m.Add("alg", 28, 0, 3).Add("fb", 28, 3, 3).Add("pan.l", 29, 7, 1).Add("pan.r", 29, 6, 1).Add("ams", 29, 4, 2).Add("fms", 29, 0, 3);
    }

    private static Field[] OpnFields()
    {
        var f = new List<Field>
        {
            Field.Range("alg", "Algorithm", "Channel", 0, 7, "How the four operators connect: 0 is a chain of four (the first modulates the second ... heavy modulation), 7 is four sine waves side by side (additive). The ones that reach the output are carriers, the rest modulate. $B0 bits 2-0."),
            Field.Range("fb", "Feedback", "Channel", 0, 7, "How strongly operator 1 modulates itself (0 none, 7 most): bends its sine toward a saw, then noise. $B0 bits 5-3."),
            Field.Bits("pan.l", "Left", "Channel", 1, "Whether the channel is heard on the left ($B4 bit 7).", Audible.No, "The sampler plays every channel on both sides."),
            Field.Bits("pan.r", "Right", "Channel", 1, "Whether the channel is heard on the right ($B4 bit 6).", Audible.No, "The sampler plays every channel on both sides."),
            Field.Range("ams", "AM sensitivity", "Channel", 0, 3, "How much the shared LFO makes the operators that have AM on swell and fade in volume (0 off, 3 deepest). $B4 bits 5-4. Needs the LFO on."),
            Field.Range("fms", "FM sensitivity", "Channel", 0, 7, "How much the shared LFO bends the pitch, a vibrato (0 off, 7 deepest). $B4 bits 2-0. Needs the LFO on."),
            Field.Bits("lfo.on", "LFO on", "Chip (shared by all six channels)", 1, "Turns the chip's low-frequency oscillator on ($22 bit 3). It is one oscillator for all six channels: changing it here changes it for every patch."),
            Field.Range("lfo.freq", "LFO speed", "Chip (shared by all six channels)", 0, 7, "The LFO's speed, from about 4 Hz (0) to 72 Hz (7). $22 bits 2-0."),
        };
        for (int n = 1; n <= 4; n++)
        {
            string p = $"op{n}.", g = $"Operator {n}", o = $"op {n}";
            f.Add(Field.Range(p + "dt", $"Op {n} detune", g, 0, 7, "Detune: 0 none, 1-3 sharpen by a few cents (more at high notes), 4-7 flatten by the same amounts (bit 2 is the sign). $30 bits 6-4."));
            f.Add(Field.Range(p + "mul", $"Op {n} multiple", g, 0, 15, "Frequency multiple of the note: 0 is x0.5, 1 to 15 are x1 to x15. A carrier on a whole multiple keeps the note's pitch; a modulator on other ratios makes inharmonic, bell-like tones. $30 bits 3-0."));
            f.Add(Field.Range(p + "tl", $"Op {n} total level", g, 0, 127, "Attenuation: 0 loudest, each step 0.75 dB, 127 silent. On a carrier it is the loudness (the sampler adds the note's velocity on top); on a modulator it is how hard it bends the next operator. $40."));
            f.Add(Field.Range(p + "rs", $"Op {n} rate scaling", g, 0, 3, "Key scaling of the envelope: higher notes run it faster (0 none, 3 most). $50 bits 7-6."));
            f.Add(Field.Range(p + "ar", $"Op {n} attack rate", g, 0, 31, "How fast the level rises from silence at key-on: 31 instant, 0 never rises. $50 bits 4-0."));
            f.Add(Field.Bits(p + "am", $"Op {n} AM", g, 1, "Lets the LFO's amplitude modulation (tremolo, depth from the channel's AM sensitivity) act on this operator. $60 bit 7."));
            f.Add(Field.Range(p + "d1r", $"Op {n} decay 1 rate", g, 0, 31, "First decay: how fast the level falls from its peak to the sustain level (0 holds the peak, 31 fastest). $60 bits 4-0."));
            f.Add(Field.Range(p + "d2r", $"Op {n} decay 2 rate", g, 0, 31, "Second decay: how fast the level keeps falling while the key is held, after the sustain level (0 holds). $70 bits 4-0."));
            f.Add(Field.Range(p + "sl", $"Op {n} sustain level", g, 0, 15, "Where the first decay stops: 0 at the peak, 15 about 93 dB down (3 dB a step). $80 bits 7-4."));
            f.Add(Field.Range(p + "rr", $"Op {n} release rate", g, 0, 15, "How fast the level falls after key-off (0 slowest, 15 fastest). $80 bits 3-0."));
            f.Add(Field.Enum(p + "ssg.on", $"Op {n} SSG-EG", g, SsgNames, "Turns the SSG-EG envelope shaper on for this operator: the envelope repeats or holds as a saw, triangle or pulse. $90 bit 3."));
            f.Add(Field.Range(p + "ssg.mode", $"Op {n} SSG-EG mode", g, 0, 7, "SSG-EG shape (when on): bit 2 starts the envelope inverted, bit 1 alternates direction each cycle, bit 0 holds at the end of a cycle. $90 bits 2-0."));
        }
        return f.ToArray();
    }

    private static Field[] DacFields() =>
    [
        Field.Range("dac.rate", "Rate", "Sound", 1000, 100000, "The rate in Hz the game's driver wrote the samples to the DAC at. A one-shot plays at this speed on middle C; a loop is scaled by the note over its own pitch."),
        Field.Bits("dac.loop", "Loops", "Loop", 1, "Whether the stretch between loop start and loop end repeats while the key is held (otherwise the sound plays once)."),
        Field.Range("dac.loop.start", "Loop start", "Loop", 0, DacSample.MaxLength, "The sample number the loop jumps back to."),
        Field.Range("dac.loop.end", "Loop end", "Loop", 0, DacSample.MaxLength, "The sample number (exclusive) where the loop ends and jumps back."),
        Field.Range("dac.cycle", "Loop pitch", "Loop", 0, 2_000_000, "The pitch of the loop at its own speed, in hundredths of a hertz (the note a looped sound plays when asked for that note's frequency). 0 means unknown: it then plays at middle C like a one-shot."),
    ];

    private static Field[] PsgToneFields() =>
    [
        Field.Range("tone1.period", "Tone 1 period", "Tones", 0, 1023, "The game's tone 1 register: the pitch is clock / 32 / period.", Audible.No, PsgNote),
        Field.Range("tone2.period", "Tone 2 period", "Tones", 0, 1023, "The game's tone 2 register.", Audible.No, PsgNote),
        Field.Range("tone3.period", "Tone 3 period", "Tones", 0, 1023, "The game's tone 3 register.", Audible.No, PsgNote),
        Field.Range("tone1.att", "Tone 1 attenuation", "Tones", 0, 15, "Attenuation in 2 dB steps: 0 loudest, 15 off.", Audible.No, PsgNote),
        Field.Range("tone2.att", "Tone 2 attenuation", "Tones", 0, 15, "Attenuation in 2 dB steps: 0 loudest, 15 off.", Audible.No, PsgNote),
        Field.Range("tone3.att", "Tone 3 attenuation", "Tones", 0, 15, "Attenuation in 2 dB steps: 0 loudest, 15 off.", Audible.No, PsgNote),
        Field.Bits("stereo", "Game Gear stereo", "Tones", 8, "Game Gear stereo port: bits 7-4 put noise, tone 3, tone 2, tone 1 on the left; bits 3-0 on the right.", Audible.No, "The sampler plays every voice on both sides."),
    ];

    private static Field[] PsgNoiseFields() =>
    [
        Field.Enum("noise.mode", "Noise type", "Noise", ["periodic", "white"], "White noise is a random hiss; periodic noise is a short repeating pattern that buzzes at a pitch. Noise register bit 2.", Audible.No, "Which noise plays is the instrument you pick (white or periodic), not this register."),
        Field.Range("noise.rate", "Noise rate", "Noise", 0, 3, "Shift rate: 0, 1, 2 are the clock over 512, 1024, 2048; 3 follows tone 3's pitch. Noise register bits 1-0.", Audible.No, PsgNote),
        Field.Range("noise.att", "Noise attenuation", "Noise", 0, 15, "Attenuation in 2 dB steps: 0 loudest, 15 off.", Audible.No, PsgNote),
    ];

    private static RegisterMap OpllMap { get; } = BuildOpllMap();

    private static RegisterMap BuildOpllMap()
    {
        var m = new RegisterMap();
        foreach (var (p, b) in new[] { ("mod.", 0), ("car.", 1) })
        {
            m.Add(p + "am", b, 7, 1).Add(p + "vib", b, 6, 1).Add(p + "eg", b, 5, 1).Add(p + "ksr", b, 4, 1).Add(p + "mul", b, 0, 4)
             .Add(p + "ar", 4 + b, 4, 4).Add(p + "dr", 4 + b, 0, 4).Add(p + "sl", 6 + b, 4, 4).Add(p + "rr", 6 + b, 0, 4);
        }
        return m.Add("mod.ksl", 2, 6, 2).Add("mod.tl", 2, 0, 6).Add("car.ksl", 3, 6, 2).Add("car.wave", 3, 4, 1).Add("mod.wave", 3, 3, 1).Add("mod.fb", 3, 0, 3);
    }

    private static Field[] OpllFields()
    {
        var f = new List<Field>();
        foreach (var (p, g, role) in new[] { ("mod.", "Modulator", "modulator"), ("car.", "Carrier", "carrier") })
        {
            f.Add(Field.Bits(p + "am", $"{g} AM", g, 1, $"Tremolo on the {role}: the chip's amplitude LFO (about 3.7 Hz) swells and fades its level."));
            f.Add(Field.Bits(p + "vib", $"{g} vibrato", g, 1, $"Vibrato on the {role}: the chip's pitch LFO (about 6.4 Hz) bends its pitch."));
            f.Add(Field.Enum(p + "eg", $"{g} sustained", g, ["percussive", "sustained"], $"Whether the {role} holds its sustain level while the key is down (sustained) or keeps decaying into silence (percussive)."));
            f.Add(Field.Bits(p + "ksr", $"{g} key-rate scaling", g, 1, $"Higher notes run the {role}'s envelope faster."));
            f.Add(Field.Range(p + "mul", $"{g} multiple", g, 0, 15, $"Frequency multiple of the {role}: 0 is x0.5, then x1 to x10, 10, 12, 12, 15, 15."));
            f.Add(Field.Range(p + "ar", $"{g} attack", g, 0, 15, $"Attack rate of the {role} (15 fastest, 0 never opens)."));
            f.Add(Field.Range(p + "dr", $"{g} decay", g, 0, 15, $"Decay rate of the {role} (15 fastest)."));
            f.Add(Field.Range(p + "sl", $"{g} sustain level", g, 0, 15, $"Level the {role}'s decay stops at (0 the peak, 15 about 45 dB down)."));
            f.Add(Field.Range(p + "rr", $"{g} release", g, 0, 15, p == "mod." ? "A sustained modulator holds its level at key-off on this chip, so this matters only when the modulator is percussive: it is then how fast it fades after reaching its sustain level (15 fastest)."
                : $"How fast the carrier falls after key-off when sustained (15 fastest); when percussive it is the rate it fades at after reaching its sustain level."));
            f.Add(Field.Range(p + "ksl", $"{g} level scaling", g, 0, 3, $"Key-level scaling: how much quieter the {role} gets as the note goes higher (0 none, 3 most: 6 dB per octave)."));
            f.Add(Field.Enum(p + "wave", $"{g} waveform", g, ["sine", "half sine"], $"The {role}'s wave: a full sine, or a sine with its lower half cut off (reedy, even harmonics)."));
        }
        f.Add(Field.Range("mod.tl", "Modulator level", "Modulator", 0, 63, "Modulator total level (0.75 dB steps, 0 loudest): how hard it bends the carrier, the brightness of the tone."));
        f.Add(Field.Range("mod.fb", "Feedback", "Modulator", 0, 7, "How strongly the modulator modulates itself (0 none, 7 most)."));
        return f.ToArray();
    }

    /// <summary>The fields of one Sega instrument kind, with the blob where it has one (the DAC sound).</summary>
    public static Instrument Template(ConsoleId console, InstrumentKind kind, int index, int blobLength = 1)
    {
        return kind switch
        {
            InstrumentKind.OpnPatch => new(console, kind, index, $"FM {index + 1}", "A YM2612 channel patch: algorithm, feedback, four operators.", OpnFields(), []),
            InstrumentKind.OpnDac => new(console, kind, index, "DAC sound", "A sound the game streamed to the YM2612's DAC.", DacFields(),
                [new Blob("pcm", "Samples", BlobKind.Pcm8, new byte[blobLength], 1, DacSample.MaxLength, "Unsigned 8-bit samples, 128 the middle, in the order the game wrote them to the DAC.")]),
            InstrumentKind.PsgTones => new(console, kind, index, "PSG square tones", "The SN76489's three square-wave tones.", PsgToneFields(), []),
            InstrumentKind.PsgNoise => new(console, kind, index, "PSG noise", "The SN76489's noise voice.", PsgNoiseFields(), []),
            InstrumentKind.OpllUser => new(console, kind, index, "User instrument", "The YM2413's one programmable instrument: a modulator and a carrier, eight registers.", OpllFields(), []),
            InstrumentKind.OpllRom => new(console, kind, index, "ROM instrument", "One of the YM2413's fifteen built-in instruments (read-only).", OpllFields(), []),
            _ => throw new ArgumentException($"{kind} is not a Sega instrument kind"),
        };
    }

    private InstrumentKind KindOf(int i)
    {
        if (snap.IsPsgInstrument(i)) return snap.IsNoiseInstrument(i) ? InstrumentKind.PsgNoise : InstrumentKind.PsgTones;
        return snap.Kind switch
        {
            SegaSnapshot.KindOpll => i == 0 ? InstrumentKind.OpllUser : InstrumentKind.OpllRom,
            SegaSnapshot.KindOpn => i < 6 ? InstrumentKind.OpnPatch : InstrumentKind.OpnDac,
            _ => InstrumentKind.PsgTones,
        };
    }

    public IReadOnlyList<Instrument> List()
    {
        var list = new List<Instrument>();
        for (int i = 0; i < snap.InstrumentCount; i++) list.Add(Read(i)!);
        return list;
    }

    public Instrument? Read(int index)
    {
        if (index < 0 || index >= snap.InstrumentCount) return null;
        var kind = KindOf(index);
        var (name, note) = snap.Instrument(index);
        Instrument inst;
        switch (kind)
        {
            case InstrumentKind.OpnPatch:
            {
                inst = Template(Console, kind, index);
                OpnMap.Read(snap.OpnPatch[index], inst);
                inst.Field("lfo.on")!.Value = snap.OpnLfo >> 3 & 1;
                inst.Field("lfo.freq")!.Value = snap.OpnLfo & 7;
                break;
            }
            case InstrumentKind.OpnDac:
            {
                var d = snap.Dac[index - 6];
                inst = Template(Console, kind, index, d.Data.Length);
                inst.Blobs[0].Data = (byte[])d.Data.Clone();
                inst.Field("dac.rate")!.Value = d.Rate;
                inst.Field("dac.loop")!.Value = d.Loops ? 1 : 0;
                inst.Field("dac.loop.start")!.Value = d.Loops ? d.LoopStart : 0;
                inst.Field("dac.loop.end")!.Value = d.Loops ? d.LoopEnd : 0;
                inst.Field("dac.cycle")!.Value = inst.Field("dac.cycle")!.Clamp((int)Math.Round(d.CycleHz * 100));
                inst.Exact = d.CycleHz;
                inst.Facts.Add(("Length", $"{d.Data.Length} samples, {d.Seconds * 1000:0} ms at {d.Rate} Hz"));
                if (d.Repeats > 1) inst.Facts.Add(("Played", $"{d.Repeats} times in the stretch of writes recorded"));
                break;
            }
            case InstrumentKind.PsgTones:
            {
                inst = Template(Console, kind, index);
                for (int t = 0; t < 3; t++) { inst.Field($"tone{t + 1}.period")!.Value = snap.PsgPeriod[t]; inst.Field($"tone{t + 1}.att")!.Value = snap.PsgVolume[t]; }
                inst.Field("stereo")!.Value = snap.PsgStereo & 0xFF;
                break;
            }
            case InstrumentKind.PsgNoise:
            {
                inst = Template(Console, kind, index);
                inst.Field("noise.mode")!.Value = snap.PsgNoise >> 2 & 1;
                inst.Field("noise.rate")!.Value = snap.PsgNoise & 3;
                inst.Field("noise.att")!.Value = snap.PsgVolume[3];
                inst.Facts.Add(("Plays as", snap.IsWhiteNoise(index) ? "white noise" : "periodic noise"));
                break;
            }
            default:  // the YM2413: the user instrument, or a ROM instrument from the chip's table
            {
                inst = Template(Console, kind, index);
                var regs = kind == InstrumentKind.OpllUser ? snap.Opll : Ym2413.RomPatchBytes(index).Concat(new byte[56]).ToArray();
                OpllMap.Read(regs, inst);
                if (kind == InstrumentKind.OpllRom)
                {
                    inst.Editable = false;
                    inst.ReadOnlyReason = "A ROM instrument is built into the chip. Copy it into the user instrument (CopyRomToUser) to change a version of it.";
                }
                break;
            }
        }
        inst.Name = name;
        if (note != null) inst.Facts.Add(("Game", note));
        inst.Sounding = kind == InstrumentKind.OpnPatch && snap.OpnKeyed[index] || kind is InstrumentKind.OpllUser or InstrumentKind.OpllRom && snap.OpllInPlay().Any(p => p.Instrument == index && p.Keyed);
        return inst;
    }

    /// <summary>A copy of the snapshot whose user instrument is the chip's ROM instrument <paramref name="rom"/> (1 to 15): the way to start a corruption from a built-in one. Null when the snapshot has no YM2413.</summary>
    public SegaSnapshot? CopyRomToUser(int rom)
    {
        if (snap.Kind != SegaSnapshot.KindOpll || rom is < 1 or > 15) return null;
        var c = Copy(snap);
        Array.Copy(Ym2413.RomPatchBytes(rom), c.Opll, 8);
        return c;
    }

    // ---- writing ----

    /// <summary>An independent copy of a snapshot (every array and sample copied).</summary>
    public static SegaSnapshot Copy(SegaSnapshot s)
    {
        var c = new SegaSnapshot(s.Kind, s.PsgClock, s.PsgVariant);
        Array.Copy(s.PsgPeriod, c.PsgPeriod, 3); Array.Copy(s.PsgVolume, c.PsgVolume, 4);
        c.PsgNoise = s.PsgNoise; c.PsgStereo = s.PsgStereo;
        Array.Copy(s.Opll, c.Opll, 64);
        c.OpnClock = s.OpnClock; c.OpnLfo = s.OpnLfo;
        for (int ch = 0; ch < 6; ch++) { Array.Copy(s.OpnPatch[ch], c.OpnPatch[ch], SegaSnapshot.OpnPatchBytes); c.OpnKeyed[ch] = s.OpnKeyed[ch]; }
        c.Dac = s.Dac.Select(d => new DacSample(d.Rate, (byte[])d.Data.Clone(), d.LoopStart, d.LoopEnd, d.Repeats) { CycleHz = d.CycleHz }).ToArray();
        // the game's voices (what its driver was heard doing, round 6 `runaway`) are immutable once captured: the edited copy shares them (a recipe carries the patch the game wrote, so an edit of the plain instrument does not change them)
        c.Recipes = s.Recipes; c.ListenNote = s.ListenNote;
        return c;
    }

    public WriteResult Write(Instrument e)
    {
        if (e.Console != Console) return WriteResult.Fail($"this is a {e.Console} instrument, the snapshot is a {Console} one");
        var cur = Read(e.Index);
        if (cur == null) return WriteResult.Fail($"this snapshot has no instrument {e.Index}");
        if (cur.Kind != e.Kind) return WriteResult.Fail($"instrument {e.Index} is a {cur.Kind}, not a {e.Kind}");
        string? bad = InstrumentChecks.Validate(e, cur);
        if (bad != null) return WriteResult.Fail(bad);
        var c = Copy(snap);
        var changed = new List<string>();
        switch (e.Kind)
        {
            case InstrumentKind.OpnPatch:
            {
                int n = OpnMap.Write(c.OpnPatch[e.Index], InstrumentMutants.On(InstrumentMutants.OpnSwapsOperators) ? SwapOps(e) : e, cur);
                if (n > 0) changed.Add($"{n} patch fields");
                if (e.Get("lfo.on") != cur.Get("lfo.on") || e.Get("lfo.freq") != cur.Get("lfo.freq"))
                {
                    c.OpnLfo = c.OpnLfo & ~0x0F | e.Get("lfo.on") << 3 | e.Get("lfo.freq");
                    changed.Add("LFO");
                }
                break;
            }
            case InstrumentKind.OpnDac:
            {
                var d = snap.Dac[e.Index - 6];
                var data = e.Blobs[0].Data;
                bool loop = e.Get("dac.loop") != 0;
                int ls = e.Get("dac.loop.start"), le = e.Get("dac.loop.end");
                if (loop && (ls >= le || le > data.Length)) return WriteResult.Fail($"the loop {ls} to {le} does not fit a sound of {data.Length} samples (start below end, end no further than the length)");
                bool dataChanged = !cur.Blobs[0].Data.AsSpan().SequenceEqual(data);
                bool anyField = false;
                foreach (var f in e.Fields) if (f.Value != cur.Get(f.Id)) anyField = true;
                if (!dataChanged && !anyField) break;
                double cycle = e.Get("dac.cycle") == cur.Get("dac.cycle") ? d.CycleHz : Math.Round(e.Exact * 100) == e.Get("dac.cycle") ? e.Exact : e.Get("dac.cycle") / 100.0;
                bool loopSame = e.Get("dac.loop") == cur.Get("dac.loop") && e.Get("dac.loop.start") == cur.Get("dac.loop.start") && e.Get("dac.loop.end") == cur.Get("dac.loop.end");
                var nd = new DacSample(e.Get("dac.rate"), (byte[])data.Clone(), loopSame && !dataChanged ? d.LoopStart : loop ? ls : -1, loopSame && !dataChanged ? d.LoopEnd : loop ? le : -1, d.Repeats) { CycleHz = cycle };
                c.Dac[e.Index - 6] = nd;
                if (dataChanged) changed.Add($"{CountDiff(cur.Blobs[0].Data, data)} sample bytes");
                if (anyField) changed.Add("rate / loop");
                break;
            }
            case InstrumentKind.PsgTones:
                for (int t = 0; t < 3; t++) { c.PsgPeriod[t] = e.Get($"tone{t + 1}.period"); c.PsgVolume[t] = e.Get($"tone{t + 1}.att"); }
                c.PsgStereo = e.Get("stereo");
                break;
            case InstrumentKind.PsgNoise:
                c.PsgNoise = e.Get("noise.mode") << 2 | e.Get("noise.rate");
                c.PsgVolume[3] = e.Get("noise.att");
                break;
            case InstrumentKind.OpllUser:
            {
                var regs = c.Opll;
                int n = OpllMap.Write(regs, e, cur);
                if (n > 0) changed.Add($"{n} instrument fields");
                break;
            }
            default:
                return WriteResult.Fail(cur.ReadOnlyReason ?? "this instrument cannot be written");
        }
        // the PSG copies: report only a real change
        if (e.Kind is InstrumentKind.PsgTones or InstrumentKind.PsgNoise && !e.SameValues(cur)) changed.Add("PSG registers (not played by the sampler)");
        return WriteResult.Success(c, changed);
    }

    /// <summary>The mutant of <see cref="InstrumentMutants.OpnSwapsOperators"/>: operators 2 and 3 exchanged on the way into the registers (the YM2612 stores operators in the order 1, 3, 2, 4).</summary>
    private static Instrument SwapOps(Instrument e)
    {
        var c = e.Clone();
        foreach (var suffix in new[] { "dt", "mul", "tl", "rs", "ar", "am", "d1r", "d2r", "sl", "rr", "ssg.on", "ssg.mode" })
            (c.Field("op2." + suffix)!.Value, c.Field("op3." + suffix)!.Value) = (e.Field("op3." + suffix)!.Value, e.Field("op2." + suffix)!.Value);
        return c;
    }

    private static int CountDiff(byte[] a, byte[] b) { int n = Math.Abs(a.Length - b.Length); for (int i = 0; i < Math.Min(a.Length, b.Length); i++) if (a[i] != b[i]) n++; return n; }
}
