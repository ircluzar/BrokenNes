namespace BrokenNes2.Instruments;

/// <summary>
/// The SNES instruments of a <see cref="SpcSnapshot"/> (the Instrument Runaway working copy of a game's sound memory): one per sample the game's directory lists (the list the Instrument picker shows), with the sample's BRR data as a blob
/// and its settings as fields. What the fields mean and which ones the sampler plays:
/// <list type="bullet">
/// <item>the BRR blob and the loop pointer live in the audio RAM copy (the directory entry at DIR x $100 + slot x 4); <see cref="SnesSampler"/> plays both as they are;</item>
/// <item>the envelope (ADSR1, ADSR2, GAIN) is what <see cref="SpcSnapshot.TryEnvelope"/> returns: an edit's own, else the game's voice that used the sample, else the organ-like default; a game's envelope is
///   read here as the value the sampler will play, so an unchanged write adds nothing;</item>
/// <item>tuning, the left / right volume weights and the echo / noise / pitch-modulation flags are not in a game's sound memory in a form the sampler uses (it plays FL's pitch and level and turns noise and modulation off);
///   they are edits of the <see cref="SnesOverlay"/> that rides on the snapshot, which the sampler reads (and which is saved with the project). Their clean values are the sampler's own: no tuning, full weights, noise and modulation off, echo as the game used it.</item>
/// </list>
/// </summary>
public sealed class SnesInstruments : IInstrumentSource
{
    private readonly SpcSnapshot snap;
    public SnesInstruments(SpcSnapshot snapshot) { snap = snapshot; }

    public ConsoleId Console => ConsoleId.Snes;
    public object Snapshot => snap;

    private static readonly string[] EnvelopeModes = ["GAIN", "ADSR"];
    private static readonly string[] GainModes = ["Direct level", "Curve"];

    /// <summary>The organ-like envelope <see cref="SnesSampler"/> uses for a sample no game voice set one on (instant attack, held until key-off).</summary>
    public const byte DefaultAdsr1 = 0xFF, DefaultAdsr2 = 0xE0, DefaultGain = 0x7F;

    /// <summary>The fields of a SNES sample instrument with the sampler's own defaults filled in (the blob is added by the reader).</summary>
    public static Field[] Fields(bool echoDefault)
    {
        var f = new[]
        {
            Field.Enum("env.adsr", "Envelope mode", "Envelope", EnvelopeModes, "Which envelope the voice runs: ADSR (attack, decay, sustain, release by rates) or GAIN (a direct level or a ramp). ADSR1 bit 7."),
            Field.Range("env.ar", "Attack rate", "ADSR envelope", 0, 15, "How fast the volume rises from silence to full after key-on: 15 is instant, 0 takes about 4 seconds. ADSR1 bits 3-0 (used when the mode is ADSR)."),
            Field.Range("env.dr", "Decay rate", "ADSR envelope", 0, 7, "How fast the volume falls from full to the sustain level after the attack (0 slowest, 7 fastest). ADSR1 bits 6-4."),
            Field.Range("env.sl", "Sustain level", "ADSR envelope", 0, 7, "The volume the decay stops at, in eighths of full (0 is 1/8, 7 is full). ADSR2 bits 7-5."),
            Field.Range("env.sr", "Sustain rate", "ADSR envelope", 0, 31, "How fast the volume keeps fading while the key is held (31 fastest, 0 holds the level for ever). ADSR2 bits 4-0."),
            Field.Enum("gain.mode", "GAIN mode", "GAIN envelope", GainModes, "Used when the envelope mode is GAIN: the GAIN byte is either a fixed volume (Direct level, bit 7 off) or a ramp that moves the volume by itself (Curve, bit 7 on)."),
            Field.Range("gain.curve", "GAIN curve", "GAIN envelope", 0, 3, "GAIN bits 6-5. As a Curve: 0 linear decrease, 1 exponential decrease, 2 linear increase, 3 bent-line increase. As a Direct level they are the top two bits of the 7-bit volume (volume = 32 x this + rate)."),
            Field.Range("gain.rate", "GAIN rate", "GAIN envelope", 0, 31, "GAIN bits 4-0. As a Curve: how fast it moves (31 fastest). As a Direct level: the low five bits of the volume."),
            Field.Signed("vol.l", "Left weight", "Voice", -128, 127, 8, "The sample's share of the note's level on the left, -128 to 127 (127 is the full level, negative flips the phase). The game's driver rewrites the DSP's voice volumes at every note; here the note's level from FL is scaled by this."),
            Field.Signed("vol.r", "Right weight", "Voice", -128, 127, 8, "The sample's share of the note's level on the right, -128 to 127 (127 is the full level, negative flips the phase)."),
            Field.Signed("tune.cents", "Tuning", "Voice", -SnesOverlay.MaxTuneCents, SnesOverlay.MaxTuneCents, 14, "Cents added to every note of this sample (4800 is four octaves). The DSP's pitch register scales with frequency: $1000 plays the sample at its own rate."),
            Field.Bits("mix.echo", "Echo", "Voice", 1, "Sends this voice into the echo unit (its volume, delay and feedback are the game's and shared by every voice)."),
            Field.Bits("mix.noise", "Noise", "Voice", 1, "Replaces the sample by the DSP's noise generator (its rate is the game's FLG setting); the envelope and volumes still shape it."),
            Field.Bits("mix.pmod", "Pitch modulation", "Voice", 1, "The previous DSP voice's output bends this voice's pitch (voice 0 has no previous one, so which voice a note lands on decides what is heard)."),
            Field.Range("loop.addr", "Loop pointer", "Sample", 0, 0xFFFF, "The audio-RAM address the sample jumps back to after its last block when that block has the loop flag (the directory's second word). A clean sample has it on a 9-byte block boundary inside the sample.", hex: true),
        };
        f[Array.FindIndex(f, x => x.Id == "mix.echo")].Value = echoDefault ? 1 : 0;
        Find(f, "vol.l").Value = 127; Find(f, "vol.r").Value = 127;
        return f;
    }

    private static Field Find(Field[] f, string id) => Array.Find(f, x => x.Id == id)!;

    /// <summary>An empty instrument of the SNES sample kind with the right schema (the codec uses it to decode saved values).</summary>
    public static Instrument Template(int slot, int blobLength = 0) =>
        new(ConsoleId.Snes, InstrumentKind.SnesSample, slot, $"Sample {slot}", "A BRR sample from the game's sample directory, with the settings the Runaway sampler plays it with.", Fields(false),
            [new Blob("sample", "BRR sample", BlobKind.Brr, new byte[blobLength], blobLength, blobLength,
                "The sample as the DSP reads it: 9-byte blocks of a header (range, filter, loop flag, end flag) and sixteen signed 4-bit samples. Its length is fixed (it lives in audio RAM between other data).")]);

    public IReadOnlyList<Instrument> List() => snap.Instruments().Select(i => Build(i)).ToList();

    public Instrument? Read(int index)
    {
        foreach (var i in snap.Instruments()) if (i.Slot == index) return Build(i);
        return null;
    }

    private Instrument Build(SpcSnapshot.SampleInfo info)
    {
        int slot = info.Slot, start = info.Start;
        int length = Math.Max(0, Math.Min(info.Blocks * 9, SpcSnapshot.AramSize - start));
        var inst = Template(slot, length);
        inst.Origin = start;
        var data = new byte[length];
        Array.Copy(snap.Aram, start, data, 0, length);
        inst.Blobs[0].Data = data;
        inst.Fields[Array.FindIndex(inst.Fields, f => f.Id == "mix.echo")].Value = snap.Dsp[0x4D] != 0 ? 1 : 0;

        var ov = snap.Overlay;
        int oi = ov?.IndexOf(slot) ?? -1;
        // envelope: what the sampler plays
        string source;
        if (snap.TryEnvelope(slot, out byte a1, out byte a2, out byte g))
            source = oi >= 0 && (ov!.At(oi).Flags & SnesOverlay.HasEnv) != 0 ? "set by an edit" : $"the game's own, from DSP voice {EnvelopeVoice(slot)}";
        else { a1 = DefaultAdsr1; a2 = DefaultAdsr2; g = DefaultGain; source = "the organ-like default (no game voice used this sample)"; }
        Set(inst, "env.adsr", a1 >> 7); Set(inst, "env.ar", a1 & 15); Set(inst, "env.dr", a1 >> 4 & 7);
        Set(inst, "env.sl", a2 >> 5); Set(inst, "env.sr", a2 & 31);
        Set(inst, "gain.mode", g >> 7); Set(inst, "gain.curve", g >> 5 & 3); Set(inst, "gain.rate", g & 31);
        Set(inst, "loop.addr", info.Loop);
        if (oi >= 0)
        {
            ref readonly var e = ref ov!.At(oi);
            if ((e.Flags & SnesOverlay.HasTune) != 0) Set(inst, "tune.cents", e.Tune);
            if ((e.Flags & SnesOverlay.HasVol) != 0) { Set(inst, "vol.l", e.WeightL); Set(inst, "vol.r", e.WeightR); }
            if ((e.Flags & SnesOverlay.HasMix) != 0) { Set(inst, "mix.echo", e.Mix & SnesOverlay.MixEcho); Set(inst, "mix.noise", e.Mix >> 1 & 1); Set(inst, "mix.pmod", e.Mix >> 2 & 1); }
        }
        inst.Facts.Add(("Sample slot", $"{slot} (directory entry at ${snap.DirectoryBase + slot * 4:X4})"));
        inst.Facts.Add(("Audio RAM", $"${start:X4}, {info.Blocks} blocks = {info.Blocks * 16} samples = {info.Milliseconds:0.0} ms at pitch $1000"));
        inst.Facts.Add(("Loops", info.Loops ? $"yes, from ${info.Loop:X4} (block {(info.Loop - start) / 9})" : "no (plays once)"));
        inst.Facts.Add(("Envelope", source));
        if (info.Sounding) { inst.Facts.Add(("Game", "one of the game's voices was playing it when the game froze")); inst.Sounding = true; }
        return inst;
    }

    private static void Set(Instrument i, string id, int value) { var f = i.Field(id)!; f.Value = f.Clamp(value); }

    /// <summary>The DSP voice whose envelope <see cref="SpcSnapshot.TryEnvelope"/> takes (same choice), for display.</summary>
    private int EnvelopeVoice(int srcn)
    {
        int best = -1;
        for (int v = 0; v < 8; v++)
        {
            int r = v * 16;
            if (snap.Dsp[r + 4] != srcn) continue;
            if ((snap.Dsp[r + 5] & 0x80) == 0 && snap.Dsp[r + 7] == 0) continue;
            if (best < 0 || (snap.Dsp[r + 8] != 0 && snap.Dsp[best * 16 + 8] == 0)) best = v;
        }
        return best;
    }

    // ---- writing ----

    private static (byte A1, byte A2, byte G) Envelope(Instrument i) =>
        ((byte)(i.Get("env.adsr") << 7 | i.Get("env.dr") << 4 | i.Get("env.ar")), (byte)(i.Get("env.sl") << 5 | i.Get("env.sr")), (byte)(i.Get("gain.mode") << 7 | i.Get("gain.curve") << 5 | i.Get("gain.rate")));

    private static bool Differs(Instrument a, Instrument b, params string[] ids) { foreach (var id in ids) if (a.Get(id) != b.Get(id)) return true; return false; }

    public WriteResult Write(Instrument e)
    {
        if (e.Console != ConsoleId.Snes || e.Kind != InstrumentKind.SnesSample) return WriteResult.Fail($"this is not a SNES sample ({e.Console} {e.Kind})");
        var cur = Read(e.Index);
        if (cur == null) return WriteResult.Fail($"this sound memory has no sample in slot {e.Index}");
        string? bad = InstrumentChecks.Validate(e, cur);
        if (bad != null) return WriteResult.Fail(bad);

        var changed = new List<string>();
        var aram = (byte[])snap.Aram.Clone();
        var dsp = (byte[])snap.Dsp.Clone();
        int start = cur.Origin;
        var data = e.Blobs[0].Data;
        bool blobChanged = !cur.Blobs[0].Data.AsSpan().SequenceEqual(data);
        if (blobChanged)
        {
            if (data.Length == 0 || data.Length % 9 != 0) return WriteResult.Fail($"a BRR sample is whole 9-byte blocks (this one is {data.Length} bytes)");
            if (start + data.Length > SpcSnapshot.AramSize) return WriteResult.Fail("the sample would run past the end of audio RAM");
            Array.Copy(data, 0, aram, start, data.Length);
            changed.Add($"{CountDiff(cur.Blobs[0].Data, data)} sample bytes");
        }
        bool loopChanged = e.Get("loop.addr") != cur.Get("loop.addr") && !InstrumentMutants.On(InstrumentMutants.WriteSkipsLoop);
        if (loopChanged)
        {
            int at = (snap.DirectoryBase + e.Index * 4 + 2) & 0xFFFF;
            int v = e.Get("loop.addr");
            aram[at] = (byte)v; aram[(at + 1) & 0xFFFF] = (byte)(v >> 8);
            changed.Add("loop pointer");
        }

        // the overlay entry: what the edit changed, on top of what the snapshot already had
        var entry = new SnesOverlay.Entry { Srcn = (byte)e.Index };
        int oi = snap.Overlay?.IndexOf(e.Index) ?? -1;
        if (oi >= 0) entry = snap.Overlay!.At(oi);
        bool overlayChanged = false;
        if (Differs(e, cur, "env.adsr", "env.ar", "env.dr", "env.sl", "env.sr", "gain.mode", "gain.curve", "gain.rate"))
        {
            var (a1, a2, g) = Envelope(e);
            // an envelope that is the one the game (or the organ-like default) already gives needs no entry: an edit undone leaves the snapshot's bytes as they were
            if (!snap.TryGameEnvelope(e.Index, out byte b1, out byte b2, out byte bg)) { b1 = DefaultAdsr1; b2 = DefaultAdsr2; bg = DefaultGain; }
            if (a1 == b1 && a2 == b2 && g == bg && !InstrumentMutants.On(InstrumentMutants.WriteLeavesTrace)) { entry.Flags &= unchecked((byte)~SnesOverlay.HasEnv); entry.Adsr1 = entry.Adsr2 = entry.Gain = 0; }
            else { entry.Flags |= SnesOverlay.HasEnv; entry.Adsr1 = a1; entry.Adsr2 = a2; entry.Gain = g; }
            changed.Add("envelope"); overlayChanged = true;
        }
        if (Differs(e, cur, "tune.cents"))
        {
            int t = e.Get("tune.cents");
            if (t == 0) { entry.Flags &= unchecked((byte)~SnesOverlay.HasTune); entry.Tune = 0; } else { entry.Flags |= SnesOverlay.HasTune; entry.Tune = (short)t; }
            changed.Add("tuning"); overlayChanged = true;
        }
        if (Differs(e, cur, "vol.l", "vol.r"))
        {
            int l = e.Get("vol.l"), r = e.Get("vol.r");
            if (l == 127 && r == 127) { entry.Flags &= unchecked((byte)~SnesOverlay.HasVol); entry.WeightL = entry.WeightR = 0; }
            else { entry.Flags |= SnesOverlay.HasVol; entry.WeightL = (sbyte)l; entry.WeightR = (sbyte)r; }
            changed.Add("volume weights"); overlayChanged = true;
        }
        if (Differs(e, cur, "mix.echo", "mix.noise", "mix.pmod"))
        {
            int mix = e.Get("mix.echo") | e.Get("mix.noise") << 1 | e.Get("mix.pmod") << 2;
            int def = snap.Dsp[0x4D] != 0 ? SnesOverlay.MixEcho : 0;
            if (mix == def) { entry.Flags &= unchecked((byte)~SnesOverlay.HasMix); entry.Mix = 0; } else { entry.Flags |= SnesOverlay.HasMix; entry.Mix = (byte)mix; }
            changed.Add("echo / noise / modulation"); overlayChanged = true;
        }
        if (blobChanged || loopChanged)
        {
            // pinned only while the sample is not well-formed by the sampler's own rule (a header out of range, no end block, a loop pointer off the blocks): a sample put back to a good state is listed on its own again,
            // so undoing every edit leaves the snapshot's bytes exactly as they were
            var probe = new SpcSnapshot(aram, dsp, null);
            int blocks = data.Length / 9;
            if (probe.WellFormed(e.Index, out int found, out _) && found == blocks && probe.SampleStart(e.Index) == cur.Origin) { entry.Flags &= unchecked((byte)~SnesOverlay.Pinned); entry.Blocks = 0; }
            else { entry.Flags |= SnesOverlay.Pinned; entry.Blocks = (ushort)blocks; }
            overlayChanged = true;
        }
        var overlay = snap.Overlay;
        if (overlayChanged) overlay = SnesOverlay.With(snap.Overlay, entry);
        return WriteResult.Success(new SpcSnapshot(aram, dsp, overlay), changed);
    }

    private static int CountDiff(byte[] a, byte[] b) { int n = 0; for (int i = 0; i < Math.Min(a.Length, b.Length); i++) if (a[i] != b[i]) n++; return n; }
}

/// <summary>Checks the readers and writers share.</summary>
internal static class InstrumentChecks
{
    /// <summary>Null when <paramref name="edited"/> has the same schema as <paramref name="current"/> (the same field ids, blob ids and blob lengths where fixed) and every value is in range; otherwise the reason.</summary>
    public static string? Validate(Instrument edited, Instrument current)
    {
        if (edited.Fields.Length != current.Fields.Length || edited.Blobs.Length != current.Blobs.Length) return "the instrument does not have this kind's fields";
        for (int i = 0; i < edited.Fields.Length; i++)
        {
            var f = edited.Fields[i];
            if (f.Id != current.Fields[i].Id) return $"field {i} is '{f.Id}', expected '{current.Fields[i].Id}'";
            if (f.Value < f.Min || f.Value > f.Max) return $"{f.Name} ({f.Id}) is {f.Value}, outside {f.Min} to {f.Max}";
        }
        for (int i = 0; i < edited.Blobs.Length; i++)
        {
            var b = edited.Blobs[i]; var c = current.Blobs[i];
            if (b.Id != c.Id) return $"blob {i} is '{b.Id}', expected '{c.Id}'";
            if (b.Data.Length < c.MinLength || b.Data.Length > c.MaxLength) return b.Data.Length == 0 ? $"{c.Name} is empty (it must be {Describe(c)})" : $"{c.Name} is {b.Data.Length} bytes (it must be {Describe(c)})";
        }
        return null;
    }

    private static string Describe(Blob b) => b.MinLength == b.MaxLength ? $"exactly {b.MinLength} bytes" : $"{b.MinLength} to {b.MaxLength} bytes";
}
