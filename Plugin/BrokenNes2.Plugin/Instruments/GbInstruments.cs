namespace BrokenNes2.Instruments;

/// <summary>
/// The four Game Boy instruments of a <see cref="GbSnapshot"/> (the Runaway copy of the sound registers NR10-NR51 and the 16 bytes of wave RAM), numbered as the sampler's channels: 0 pulse 1, 1 pulse 2, 2 wave, 3 noise.
/// What <see cref="GbSampler"/> plays: a pulse's duty (unless the Instrument dropdown overrides it) and the direction and pace of its envelope (it starts at the note's level, with no length limit and no sweep); the wave channel's 32 samples
/// (its level and on/off come from the note); the noise channel's envelope direction and pace (its pitch is the note's, its width the Noise mode slider). The other fields are shown, with the reason, as having no audible effect today.
/// </summary>
public sealed class GbInstruments : IInstrumentSource
{
    private readonly GbSnapshot snap;
    public GbInstruments(GbSnapshot snapshot) { snap = snapshot; }

    public ConsoleId Console => ConsoleId.GameBoy;
    public object Snapshot => snap;

    private const string NoSweep = "The sampler turns the sweep off for every note.";
    private const string NoLength = "The sampler plays a note with no length limit.";
    private const string NoteLevel = "The note starts at the level FL asks for, not at this volume.";

    private static readonly string[] Directions = ["fades out", "swells"];

    private static Field[] PulseFields(bool sweep)
    {
        var f = new List<Field>();
        if (sweep)
        {
            f.Add(Field.Range("sweep.period", "Sweep pace", "Sweep", 0, 7, "How often the pitch sweeps, in 128 Hz steps (0 sweep off). NR10 bits 6-4.", Audible.No, NoSweep));
            f.Add(Field.Enum("sweep.dir", "Sweep direction", "Sweep", ["up", "down"], "Whether the sweep raises or lowers the pitch. NR10 bit 3.", Audible.No, NoSweep));
            f.Add(Field.Range("sweep.shift", "Sweep shift", "Sweep", 0, 7, "How big each sweep step is (a larger shift is a smaller step). NR10 bits 2-0.", Audible.No, NoSweep));
        }
        f.Add(Field.Enum("duty", "Duty", "Pulse", ["12.5%", "25%", "50%", "75%"], "The pulse wave's width: 12.5% is thin and nasal, 50% hollow and full, 75% sounds like 25%. NR11/NR21 bits 7-6.", Audible.Yes, "unless the Instrument dropdown overrides the duty"));
        f.Add(Field.Range("length", "Length", "Pulse", 0, 63, "Length counter start (64 minus this, in 256 Hz ticks); only cuts the note when length is enabled. NR11/NR21 bits 5-0.", Audible.No, NoLength));
        f.Add(Field.Range("env.vol", "Start volume", "Envelope", 0, 15, "The volume the envelope starts at (0 silent, 15 loudest). NR12/NR22 bits 7-4.", Audible.No, NoteLevel));
        f.Add(Field.Enum("env.dir", "Envelope direction", "Envelope", Directions, "Whether the volume fades out or swells after the note starts. NR12/NR22 bit 3."));
        f.Add(Field.Range("env.pace", "Envelope pace", "Envelope", 0, 7, "Steps of 1/64 s between volume changes (0 holds the volume). NR12/NR22 bits 2-0."));
        return f.ToArray();
    }

    private static Field[] WaveFields() =>
    [
        Field.Bits("dac", "Channel on", "Wave", 1, "Wave channel DAC power. NR30 bit 7.", Audible.No, "The sampler switches the channel on for each note."),
        Field.Range("length", "Length", "Wave", 0, 255, "Length counter start (256 minus this). NR31.", Audible.No, NoLength),
        Field.Range("level", "Output level", "Wave", 0, 3, "0 mute, 1 full, 2 half, 3 quarter. NR32 bits 6-5.", Audible.No, "The sampler picks the level from the note's velocity."),
    ];

    private static Field[] NoiseFields() =>
    [
        Field.Range("length", "Length", "Noise", 0, 63, "Length counter start. NR41 bits 5-0.", Audible.No, NoLength),
        Field.Range("env.vol", "Start volume", "Envelope", 0, 15, "The volume the envelope starts at. NR42 bits 7-4.", Audible.No, NoteLevel),
        Field.Enum("env.dir", "Envelope direction", "Envelope", Directions, "Whether the volume fades out or swells after the note starts. NR42 bit 3."),
        Field.Range("env.pace", "Envelope pace", "Envelope", 0, 7, "Steps of 1/64 s between volume changes (0 holds the volume). NR42 bits 2-0."),
        Field.Range("clock.shift", "Clock shift", "Noise", 0, 15, "The noise's clock is divided by 2 to the power of this: a higher shift is a lower, rumblier noise. NR43 bits 7-4.", Audible.No, "The sampler sets the noise rate from the note's pitch."),
        Field.Enum("width", "Width", "Noise", ["15 bits (hiss)", "7 bits (buzz)"], "A 7-bit shift register repeats quickly and sounds tonal. NR43 bit 3.", Audible.No, "The width is the plugin's Noise mode slider."),
        Field.Range("divisor", "Divisor", "Noise", 0, 7, "Base divisor of the noise clock (0 counts as 0.5). NR43 bits 2-0.", Audible.No, "The sampler sets the noise rate from the note's pitch."),
    ];

    private static RegisterMap PulseMap(int channel)
    {
        int nr1 = channel == 0 ? 0x10 : 0x15;   // NR10, or the unused NR20
        var m = new RegisterMap();
        if (channel == 0) m.Add("sweep.period", 0x10 - 0x10, 4, 3).Add("sweep.dir", 0x10 - 0x10, 3, 1).Add("sweep.shift", 0x10 - 0x10, 0, 3);
        int b1 = nr1 + 1 - 0x10, b2 = nr1 + 2 - 0x10;
        return m.Add("duty", b1, 6, 2).Add("length", b1, 0, 6).Add("env.vol", b2, 4, 4).Add("env.dir", b2, 3, 1).Add("env.pace", b2, 0, 3);
    }

    private static readonly RegisterMap Pulse1Map = PulseMap(0), Pulse2Map = PulseMap(1);
    private static readonly RegisterMap WaveMap = new RegisterMap().Add("dac", 0x1A - 0x10, 7, 1).Add("length", 0x1B - 0x10, 0, 8).Add("level", 0x1C - 0x10, 5, 2);
    private static readonly RegisterMap NoiseMap = new RegisterMap().Add("length", 0x20 - 0x10, 0, 6).Add("env.vol", 0x21 - 0x10, 4, 4).Add("env.dir", 0x21 - 0x10, 3, 1).Add("env.pace", 0x21 - 0x10, 0, 3)
        .Add("clock.shift", 0x22 - 0x10, 4, 4).Add("width", 0x22 - 0x10, 3, 1).Add("divisor", 0x22 - 0x10, 0, 3);

    private static readonly string[] Names = ["Pulse 1", "Pulse 2", "Wave", "Noise"];
    private static readonly InstrumentKind[] Kinds = [InstrumentKind.GbPulse1, InstrumentKind.GbPulse2, InstrumentKind.GbWave, InstrumentKind.GbNoise];

    public static Instrument Template(InstrumentKind kind) => kind switch
    {
        InstrumentKind.GbPulse1 => new(ConsoleId.GameBoy, kind, 0, Names[0], "The first pulse channel: duty, envelope, and a frequency sweep.", PulseFields(true), []),
        InstrumentKind.GbPulse2 => new(ConsoleId.GameBoy, kind, 1, Names[1], "The second pulse channel: duty and envelope.", PulseFields(false), []),
        InstrumentKind.GbWave => new(ConsoleId.GameBoy, kind, 2, Names[2], "The wave channel: 32 four-bit samples played in a loop.", WaveFields(),
            [new Blob("wave", "Wave RAM", BlobKind.Nibbles, new byte[GbSnapshot.WaveSize], GbSnapshot.WaveSize, GbSnapshot.WaveSize, "The 32 four-bit samples of the wave (two to a byte, high nibble first) the channel loops over at the note's pitch.")]),
        InstrumentKind.GbNoise => new(ConsoleId.GameBoy, kind, 3, Names[3], "The noise channel: a shift register clocked at the note's rate.", NoiseFields(), []),
        _ => throw new ArgumentException($"{kind} is not a Game Boy instrument kind"),
    };

    private static RegisterMap MapOf(InstrumentKind kind) => kind switch { InstrumentKind.GbPulse1 => Pulse1Map, InstrumentKind.GbPulse2 => Pulse2Map, InstrumentKind.GbWave => WaveMap, _ => NoiseMap };

    public IReadOnlyList<Instrument> List() => Kinds.Select((_, i) => Read(i)!).ToList();

    public Instrument? Read(int index)
    {
        if (index is < 0 or > 3) return null;
        var inst = Template(Kinds[index]);
        MapOf(inst.Kind).Read(snap.Regs, inst);
        if (inst.Kind == InstrumentKind.GbWave) inst.Blobs[0].Data = (byte[])snap.Wave.Clone();
        inst.Facts.Add(("Model", snap.Cgb ? "Game Boy Color" : "Game Boy"));
        return inst;
    }

    public WriteResult Write(Instrument e)
    {
        if (e.Console != ConsoleId.GameBoy) return WriteResult.Fail($"this is a {e.Console} instrument, the snapshot is a Game Boy one");
        var cur = Read(e.Index);
        if (cur == null) return WriteResult.Fail($"the Game Boy has no instrument {e.Index}");
        if (cur.Kind != e.Kind) return WriteResult.Fail($"instrument {e.Index} is a {cur.Kind}, not a {e.Kind}");
        string? bad = InstrumentChecks.Validate(e, cur);
        if (bad != null) return WriteResult.Fail(bad);
        var regs = (byte[])snap.Regs.Clone();
        var wave = (byte[])snap.Wave.Clone();
        var changed = new List<string>();
        int n = MapOf(e.Kind).Write(regs, e, cur);
        if (n > 0) changed.Add($"{n} register fields");
        if (e.Kind == InstrumentKind.GbWave && !e.Blobs[0].Data.AsSpan().SequenceEqual(wave))
        {
            e.Blobs[0].Data.CopyTo(wave, 0);
            if (InstrumentMutants.On(InstrumentMutants.GbWaveNibbleSwap)) for (int i = 0; i < wave.Length; i++) wave[i] = (byte)(wave[i] << 4 | wave[i] >> 4);
            changed.Add("wave RAM");
        }
        return WriteResult.Success(new GbSnapshot(regs, wave, snap.Cgb), changed);
    }
}
