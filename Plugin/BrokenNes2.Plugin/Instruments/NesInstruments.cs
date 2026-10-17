namespace BrokenNes2.Instruments;

/// <summary>
/// The NES APU's registers $4000-$4017 as a game left them, and the bytes of its DMC sample: the NES counterpart of the SNES / Game Boy / Sega snapshots. Instrument Runaway does not capture the NES yet (it freezes and silences the game), so nothing
/// in the plugin builds one from a running game today; <see cref="FromLatch"/> does it from the NES core's APU register latch (<c>NesAudioMachine.CopyApuLatch</c>) and a CPU peek, for the presets and for whoever builds the NES sampler.
/// </summary>
public sealed class NesSnapshot
{
    public const int RegCount = 0x18, MaxDmc = 4081;
    public readonly byte[] Regs = new byte[RegCount];
    public byte[] Dmc = [];

    public NesSnapshot(byte[] regs, byte[] dmc)
    {
        Array.Copy(regs, Regs, Math.Min(regs.Length, RegCount));
        Dmc = dmc.Length > MaxDmc ? dmc[..MaxDmc] : (byte[])dmc.Clone();
    }

    /// <summary>Builds a snapshot from the register latch (the last value written to each of $4000-$4017) and the mask of registers ever written (bit n = $4000 + n); <paramref name="peekCpu"/> reads the CPU's memory, where the DMC sample lives
    /// ($C000 + 64 x $4012, 16 x $4013 + 1 bytes). A DMC that was never set up has an empty sample.</summary>
    public static NesSnapshot FromLatch(byte[] latch, uint written, Func<ushort, byte> peekCpu)
    {
        var regs = new byte[RegCount];
        Array.Copy(latch, regs, Math.Min(latch.Length, RegCount));
        var dmc = Array.Empty<byte>();
        if ((written >> 16 & 0xF) != 0)                           // $4010-$4013 were written
        {
            int addr = 0xC000 + regs[0x12] * 64, len = regs[0x13] * 16 + 1;
            dmc = new byte[len];
            for (int i = 0; i < len; i++) dmc[i] = peekCpu((ushort)(0x8000 | ((addr + i) & 0x7FFF)));   // the address wraps from $FFFF to $8000
        }
        return new NesSnapshot(regs, dmc);
    }

    public byte[] ToBytes()
    {
        var b = new byte[RegCount + 2 + Dmc.Length];
        Regs.CopyTo(b, 0);
        b[RegCount] = (byte)Dmc.Length; b[RegCount + 1] = (byte)(Dmc.Length >> 8);
        Dmc.CopyTo(b, RegCount + 2);
        return b;
    }

    public static NesSnapshot? FromBytes(byte[] b)
    {
        if (b.Length < RegCount + 2) return null;
        int n = b[RegCount] | b[RegCount + 1] << 8;
        if (n > MaxDmc || b.Length != RegCount + 2 + n) return null;
        return new NesSnapshot(b.AsSpan(0, RegCount).ToArray(), b.AsSpan(RegCount + 2, n).ToArray());
    }
}

/// <summary>
/// The NES instruments of a <see cref="NesSnapshot"/>: 0 pulse 1, 1 pulse 2, 2 triangle, 3 noise, 4 DMC. Shallow, as the plan says: a field list with ranges. <b>Nothing plays them today</b>: Instrument Runaway on a NES game freezes and silences the game (Direct mode's
/// Duty and Noise mode are plugin parameters, not the game's registers), so every field is marked as having no audible effect, with that reason.
/// </summary>
public sealed class NesInstruments : IInstrumentSource
{
    private readonly NesSnapshot snap;
    public NesInstruments(NesSnapshot snapshot) { snap = snapshot; }

    public ConsoleId Console => ConsoleId.Nes;
    public object Snapshot => snap;

    private const string Silent = "No NES sampler exists yet: Instrument Runaway on a NES game freezes and silences it; nothing plays the game's registers.";

    private static Field R(string id, string name, string group, int min, int max, string explanation) => Field.Range(id, name, group, min, max, explanation, Audible.No, Silent);
    private static Field B(string id, string name, string group, int width, string explanation) => Field.Bits(id, name, group, width, explanation, Audible.No, Silent);

    private static Field[] PulseFields() =>
    [
        Field.Enum("duty", "Duty", "Pulse", ["12.5%", "25%", "50%", "75%"], "The pulse wave's width. $4000 bits 7-6.", Audible.No, Silent),
        B("halt", "Loop / halt", "Envelope", 1, "Length-counter halt, which is also the envelope's loop flag: the note never ends by its length. $4000 bit 5."),
        B("const", "Constant volume", "Envelope", 1, "On: the volume field is the volume. Off: it is the envelope's period. $4000 bit 4."),
        R("vol", "Volume / period", "Envelope", 0, 15, "The volume (constant volume on) or the envelope's decay period. $4000 bits 3-0."),
        B("sweep.on", "Sweep on", "Sweep", 1, "Enables the frequency sweep unit. $4001 bit 7."),
        R("sweep.period", "Sweep period", "Sweep", 0, 7, "How often the sweep steps (in half-frames). $4001 bits 6-4."),
        B("sweep.neg", "Sweep down", "Sweep", 1, "Negate: the sweep lowers the pitch instead of raising it. $4001 bit 3."),
        R("sweep.shift", "Sweep shift", "Sweep", 0, 7, "How big each sweep step is. $4001 bits 2-0."),
        R("length", "Length", "Pulse", 0, 31, "Index into the length-counter table: how long the note lasts if it is not halted. $4003 bits 7-3."),
    ];

    private static Field[] TriangleFields() =>
    [
        B("control", "Control / halt", "Triangle", 1, "Holds the linear counter and length counter (the note does not end by itself). $4008 bit 7."),
        R("linear", "Linear counter", "Triangle", 0, 127, "How long the note lasts, in quarter-frames, when not held. $4008 bits 6-0."),
        R("length", "Length", "Triangle", 0, 31, "Index into the length-counter table. $400B bits 7-3."),
    ];

    private static Field[] NoiseFields() =>
    [
        B("halt", "Loop / halt", "Envelope", 1, "Length-counter halt and envelope loop. $400C bit 5."),
        B("const", "Constant volume", "Envelope", 1, "On: the volume field is the volume. Off: it is the envelope's period. $400C bit 4."),
        R("vol", "Volume / period", "Envelope", 0, 15, "The volume or the envelope's decay period. $400C bits 3-0."),
        Field.Enum("mode", "Mode", "Noise", ["long (hiss)", "short (buzz)"], "Short mode feeds back from a nearer tap so the pattern repeats quickly and sounds tonal. $400E bit 7.", Audible.No, Silent),
        R("period", "Period", "Noise", 0, 15, "Index into the noise period table (0 highest, 15 lowest pitch). $400E bits 3-0."),
        R("length", "Length", "Noise", 0, 31, "Index into the length-counter table. $400F bits 7-3."),
    ];

    private static Field[] DmcFields() =>
    [
        B("irq", "IRQ enable", "DMC", 1, "Raise an interrupt when the sample ends (if it does not loop). $4010 bit 7."),
        B("loop", "Loop", "DMC", 1, "Restart the sample when it ends. $4010 bit 6."),
        R("rate", "Rate", "DMC", 0, 15, "Index into the DMC rate table (0 slowest, 15 fastest). $4010 bits 3-0."),
        R("load", "Direct load", "DMC", 0, 127, "The DMC's output level, set directly (a DC offset or a one-sample click). $4011."),
        R("addr", "Sample address", "DMC", 0, 255, "Where the sample starts in the CPU's memory: $C000 + 64 x this. $4012."),
        R("len", "Sample length", "DMC", 0, 255, "How long the sample is: 16 x this + 1 bytes. $4013."),
    ];

    private static RegisterMap PulseMap(int channel)
    {
        int b = channel * 4;
        return new RegisterMap().Add("duty", b, 6, 2).Add("halt", b, 5, 1).Add("const", b, 4, 1).Add("vol", b, 0, 4)
            .Add("sweep.on", b + 1, 7, 1).Add("sweep.period", b + 1, 4, 3).Add("sweep.neg", b + 1, 3, 1).Add("sweep.shift", b + 1, 0, 3).Add("length", b + 3, 3, 5);
    }

    private static readonly RegisterMap Pulse1Map = PulseMap(0), Pulse2Map = PulseMap(1);
    private static readonly RegisterMap TriangleMap = new RegisterMap().Add("control", 8, 7, 1).Add("linear", 8, 0, 7).Add("length", 0xB, 3, 5);
    private static readonly RegisterMap NoiseMap = new RegisterMap().Add("halt", 0xC, 5, 1).Add("const", 0xC, 4, 1).Add("vol", 0xC, 0, 4).Add("mode", 0xE, 7, 1).Add("period", 0xE, 0, 4).Add("length", 0xF, 3, 5);
    private static readonly RegisterMap DmcMap = new RegisterMap().Add("irq", 0x10, 7, 1).Add("loop", 0x10, 6, 1).Add("rate", 0x10, 0, 4).Add("load", 0x11, 0, 7).Add("addr", 0x12, 0, 8).Add("len", 0x13, 0, 8);

    private static readonly InstrumentKind[] Kinds = [InstrumentKind.NesPulse1, InstrumentKind.NesPulse2, InstrumentKind.NesTriangle, InstrumentKind.NesNoise, InstrumentKind.NesDmc];
    private static readonly string[] Names = ["Pulse 1", "Pulse 2", "Triangle", "Noise", "DMC"];

    public static Instrument Template(InstrumentKind kind, int blobLength = 0) => kind switch
    {
        InstrumentKind.NesPulse1 => new(ConsoleId.Nes, kind, 0, Names[0], "The first pulse channel.", PulseFields(), []),
        InstrumentKind.NesPulse2 => new(ConsoleId.Nes, kind, 1, Names[1], "The second pulse channel.", PulseFields(), []),
        InstrumentKind.NesTriangle => new(ConsoleId.Nes, kind, 2, Names[2], "The triangle channel (fixed volume).", TriangleFields(), []),
        InstrumentKind.NesNoise => new(ConsoleId.Nes, kind, 3, Names[3], "The noise channel.", NoiseFields(), []),
        InstrumentKind.NesDmc => new(ConsoleId.Nes, kind, 4, Names[4], "The delta-modulation sample channel.", DmcFields(),
            [new Blob("sample", "DMC sample", BlobKind.Bytes, new byte[blobLength], 0, NesSnapshot.MaxDmc, "The sample's bytes, read from the CPU's memory: each bit says whether the output level steps up or down, least significant bit first.", Audible.No, Silent)]),
        _ => throw new ArgumentException($"{kind} is not a NES instrument kind"),
    };

    private static RegisterMap MapOf(InstrumentKind kind) => kind switch
    {
        InstrumentKind.NesPulse1 => Pulse1Map, InstrumentKind.NesPulse2 => Pulse2Map, InstrumentKind.NesTriangle => TriangleMap, InstrumentKind.NesNoise => NoiseMap, _ => DmcMap,
    };

    public IReadOnlyList<Instrument> List() => Kinds.Select((_, i) => Read(i)!).ToList();

    public Instrument? Read(int index)
    {
        if (index is < 0 or > 4) return null;
        var inst = Template(Kinds[index], index == 4 ? snap.Dmc.Length : 0);
        MapOf(inst.Kind).Read(snap.Regs, inst);
        if (index == 4) inst.Blobs[0].Data = (byte[])snap.Dmc.Clone();
        return inst;
    }

    public WriteResult Write(Instrument e)
    {
        if (e.Console != ConsoleId.Nes) return WriteResult.Fail($"this is a {e.Console} instrument, the snapshot is a NES one");
        var cur = Read(e.Index);
        if (cur == null) return WriteResult.Fail($"the NES has no instrument {e.Index}");
        if (cur.Kind != e.Kind) return WriteResult.Fail($"instrument {e.Index} is a {cur.Kind}, not a {e.Kind}");
        string? bad = InstrumentChecks.Validate(e, cur);
        if (bad != null) return WriteResult.Fail(bad);
        var regs = (byte[])snap.Regs.Clone();
        var changed = new List<string>();
        int n = MapOf(e.Kind).Write(regs, e, cur);
        if (n > 0) changed.Add($"{n} register fields");
        var dmc = snap.Dmc;
        if (e.Kind == InstrumentKind.NesDmc && !e.Blobs[0].Data.AsSpan().SequenceEqual(dmc)) { dmc = e.Blobs[0].Data; changed.Add("DMC sample"); }
        return WriteResult.Success(new NesSnapshot(regs, dmc), changed);
    }
}
