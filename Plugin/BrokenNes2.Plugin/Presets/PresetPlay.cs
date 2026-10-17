// Round 6 (presets): what it takes to PLAY a preset without its game. A pure library (no plugin, no audio): the preset's instrument is turned into the snapshot the existing sampler of its console plays (the very classes Instrument Runaway uses:
// SnesSampler over a SpcSnapshot, OpnSampler / OpllSampler / PsgSampler over a SegaSnapshot, GbSampler over a GbSnapshot), built around the one instrument. The plugin builds the sampler from it on the UI thread and hands it to the audio thread the way an
// instrument edit does (Bn2Plugin.PrepareSamplerFor / TakePrepared), so nothing is allocated while playing.
//
// Why this equals Runaway's sound: the sampler is told the same things (the same sample bytes at the same address, the same directory entry, the same registers, the same envelope, the same chip clocks), and everything else the game's sound memory held
// is something the sampler does not read for this instrument. The checks prove it on rendered PCM (see FruityHost `preset-test`, check `play-equals-runaway`); the one known difference is the SNES echo buffer, whose old contents the game's copy still holds.
using BrokenNes2.Instruments;
using NesEmulator.Sega;

namespace BrokenNes2.Presets;

/// <summary>The facts an engine needs besides the instrument, packed by <see cref="Of"/> when the Instruments tab hands an instrument to the Presets tab.
/// <list type="bullet">
/// <item>SNES: the 128 S-DSP registers of the working copy (directory page, echo, noise rate, master volume); and, only for a corrupted sample that plays on past its own bytes (no end block, or a loop pointer outside it), the bytes of audio RAM it plays into:
///   u8 flags (1 = more than <see cref="MaxOverrun"/> bytes were needed and the rest is silence), u16 range count, then per range u16 address, u16 length, the bytes. A clean sample, and any sample that keeps its structure, has none.</item>
/// <item>Master System / Genesis (19 bytes): PSG clock i32, PSG variant u8, YM2612 clock i32 (0 on the Master System), the snapshot's kind u8 (1 PSG alone, 2 PSG + YM2413, 3 PSG + YM2612), the instrument's place among the PSG's three (0 tones, 1 white noise, 2 periodic noise; 255 not a PSG instrument), a Genesis DAC loop's exact pitch f64 (0 none).</item>
/// <item>Game Boy: 1 byte, 1 when the game was a Game Boy Color game.</item>
/// <item>NES: nothing.</item>
/// </list></summary>
public static class PresetContext
{
    public const int SegaLength = 19, SnesLength = SpcSnapshot.DspSize, MaxOverrun = 16384;

    public static byte[] Of(object? snap, Instrument inst)
    {
        switch (snap)
        {
            case SpcSnapshot s: return SnesContext(s, inst);
            case SegaSnapshot g:
            {
                var b = new byte[SegaLength];
                BitConverter.TryWriteBytes(b.AsSpan(0), g.PsgClock); b[4] = (byte)g.PsgVariant; BitConverter.TryWriteBytes(b.AsSpan(5), g.OpnClock);
                b[9] = (byte)g.Kind;
                b[10] = inst.Kind is InstrumentKind.PsgTones or InstrumentKind.PsgNoise && g.IsPsgInstrument(inst.Index) ? (byte)(inst.Index - g.PsgInstrument) : (byte)255;
                double cycle = inst.Kind == InstrumentKind.OpnDac && double.IsFinite(inst.Exact) && inst.Exact > 0 ? inst.Exact : 0;
                BitConverter.TryWriteBytes(b.AsSpan(11), cycle);
                return b;
            }
            case GbSnapshot gb: return [(byte)(gb.Cgb ? 1 : 0)];
            default: return [];
        }
    }

    /// <summary>The 128 registers, then the audio RAM the sample plays into when it leaves its own bytes (see the class remarks).</summary>
    private static byte[] SnesContext(SpcSnapshot s, Instrument inst)
    {
        var regs = (byte[])s.Dsp.Clone();
        if (inst.Kind != InstrumentKind.SnesSample || inst.Blobs.Length == 0 || inst.Blobs[0].Data.Length == 0) return regs;
        var outside = SnesOverrun(s.Aram, inst.Origin, inst.Blobs[0].Data.Length, inst.Get("loop.addr"), out bool partial);
        if (outside.Count == 0) return regs;
        var o = new List<byte>(regs) { (byte)(partial ? 1 : 0), (byte)outside.Count, (byte)(outside.Count >> 8) };
        foreach (var (addr, len) in outside)
        {
            o.Add((byte)addr); o.Add((byte)(addr >> 8)); o.Add((byte)len); o.Add((byte)(len >> 8));
            for (int i = 0; i < len; i++) o.Add(s.Aram[(addr + i) & 0xFFFF]);
        }
        return o.ToArray();
    }

    /// <summary>The stretches of audio RAM (address, length) outside the sample that the DSP reads while it plays: it decodes 9-byte blocks from the start, goes on to the next block until a block has the end flag, and then stops or (loop flag) jumps to the loop address.
    /// A sample that ends inside itself and loops inside itself reads nothing else. One that has no end block, or whose loop address is outside it, plays on into whatever is there. At most <see cref="MaxOverrun"/> bytes are returned.</summary>
    internal static List<(int Addr, int Len)> SnesOverrun(byte[] aram, int start, int length, int loopAddr, out bool partial)
    {
        partial = false;
        var touched = new SortedSet<int>();
        var seen = new HashSet<int>();
        int p = start & 0xFFFF;
        for (int guard = 0; guard < 70000 && seen.Add(p); guard++)
        {
            bool inside = p >= start && p + 9 <= start + length;
            if (!inside) for (int i = 0; i < 9; i++) touched.Add((p + i) & 0xFFFF);
            byte h = aram[p];
            if ((h & 1) != 0) { if ((h & 2) == 0) break; p = loopAddr & 0xFFFF; }
            else p = (p + 9) & 0xFFFF;
            if (touched.Count > MaxOverrun) { partial = true; break; }
        }
        // bytes that belong to the sample itself are not "outside" even when a block straddles its end
        touched.RemoveWhere(a => a >= start && a < start + length);
        var ranges = new List<(int, int)>();
        int total = 0;
        foreach (int a in touched)
        {
            if (total >= MaxOverrun) { partial = true; break; }
            if (ranges.Count > 0 && ranges[^1].Item1 + ranges[^1].Item2 == a) ranges[^1] = (ranges[^1].Item1, ranges[^1].Item2 + 1);
            else ranges.Add((a, 1));
            total++;
        }
        return ranges;
    }

    /// <summary>Reads the overrun part of a SNES context (empty when there is none); null when it is not well-formed.</summary>
    internal static List<(int Addr, byte[] Data)>? SnesOverrunRanges(byte[] ctx, out bool partial)
    {
        partial = false;
        var list = new List<(int, byte[])>();
        if (ctx.Length == SnesLength) return list;
        if (ctx.Length < SnesLength + 3) return null;
        partial = (ctx[SnesLength] & 1) != 0;
        if ((ctx[SnesLength] & ~1) != 0) return null;
        int count = ctx[SnesLength + 1] | ctx[SnesLength + 2] << 8, p = SnesLength + 3, total = 0;
        if (count == 0 || count > MaxOverrun) return null;
        for (int i = 0; i < count; i++)
        {
            if (p + 4 > ctx.Length) return null;
            int addr = ctx[p] | ctx[p + 1] << 8, len = ctx[p + 2] | ctx[p + 3] << 8;
            p += 4;
            if (len == 0 || p + len > ctx.Length || (total += len) > MaxOverrun) return null;
            list.Add((addr, ctx.AsSpan(p, len).ToArray()));
            p += len;
        }
        return p == ctx.Length ? list : null;
    }

    /// <summary>Null when the context fits the console, else what is wrong with it.</summary>
    public static string? Check(ConsoleId console, byte[] ctx)
    {
        switch (console)
        {
            case ConsoleId.Snes: return SnesOverrunRanges(ctx, out _) != null ? null : $"{ctx.Length} bytes, a SNES preset's is {SnesLength} (and, for a sample that plays on past its end, the stretch of audio RAM it plays into)";
            case ConsoleId.GameBoy: return ctx.Length == 1 && ctx[0] <= 1 ? null : "not a Game Boy context";
            case ConsoleId.Nes: return ctx.Length == 0 ? null : "not a NES context";
            case ConsoleId.MasterSystem:
            case ConsoleId.Genesis:
            {
                if (ctx.Length != SegaLength) return $"{ctx.Length} bytes, a Master System or Genesis preset's is {SegaLength}";
                int psg = BitConverter.ToInt32(ctx, 0), opn = BitConverter.ToInt32(ctx, 5), kind = ctx[9], rel = ctx[10];
                double cycle = BitConverter.ToDouble(ctx, 11);
                if (psg < 1_000_000 || psg > 10_000_000 || ctx[4] > 2) return "a PSG clock or variant out of range";
                if (kind is < SegaSnapshot.KindPsg or > SegaSnapshot.KindOpn) return "an unknown sound unit";
                if (kind == SegaSnapshot.KindOpn != (console == ConsoleId.Genesis)) return "a sound unit that does not belong to this console";
                if (kind == SegaSnapshot.KindOpn && (opn < 1_000_000 || opn > 20_000_000)) return "a YM2612 clock out of range";
                if (rel is > 2 and < 255) return "a PSG place out of range";
                if (!double.IsFinite(cycle) || cycle < 0 || cycle > 100_000) return "a loop pitch out of range";
                return null;
            }
            default: return "unknown console";
        }
    }
}

/// <summary>A preset made ready to play: the snapshot of its console around the one instrument, and what the plugin tells the sampler (which instrument number, which Game Boy channel).</summary>
public sealed class PresetPlayable
{
    public ConsoleId Console;
    public InstrumentKind Kind;
    public int Index;
    public Instrument Instrument = null!;
    public SpcSnapshot? Snes;
    /// <summary>SNES: the game voices (recipes) the preset carries for its sample (also in <see cref="Snes"/>.Recipes); the Recipe parameter 1 and up plays one of them. Empty for a preset without note behaviour.</summary>
    public SpcRecipe[] Recipes = [];
    public SegaSnapshot? Sega;
    public GbSnapshot? Gb;
    /// <summary>What the Instrument parameter holds to play this instrument on the snapshot: the SNES sample slot, or the Sega picker index plus one.</summary>
    public int InstrumentValue;
    /// <summary>The Game Boy channel (0 pulse 1, 1 pulse 2, 2 wave, 3 noise) and whether its noise is the 7-bit one.</summary>
    public int GbChannel;
    public bool GbNoiseShort;
    /// <summary>SNES: how many bytes of the game's audio RAM past the sample the preset keeps because the corrupted sample plays on into them (0: the sample stays inside itself), and whether more were needed than a preset can hold (the rest is silence).</summary>
    public int Overrun;
    public bool OverrunPartial;
    /// <summary>Not null when this preset cannot be played yet: the reason, in the words the user sees ("cannot play on this console yet: ...").</summary>
    public string? CannotPlay;

    private const int MaxDacSounds = DacAnalyzer.MaxSounds;

    /// <summary>The snapshot and settings that play a preset. Null with the reason when the preset cannot even be set up (a damaged instrument, a sample that does not fit audio RAM, ...).</summary>
    public static PresetPlayable? Build(PresetData d, out string? problem)
    {
        var inst = InstrumentCodec.Unpack(d.Instrument, out string? p1);
        if (inst == null) { problem = "its instrument is " + p1; return null; }
        string? cp = PresetContext.Check(d.Console, d.Context);
        if (cp != null) { problem = "its context is " + cp; return null; }
        var p = new PresetPlayable { Console = inst.Console, Kind = inst.Kind, Index = inst.Index, Instrument = inst };
        problem = inst.Console switch
        {
            ConsoleId.Snes => BuildSnes(p, inst, d.Context),
            ConsoleId.Genesis or ConsoleId.MasterSystem => BuildSega(p, inst, d.Context),
            ConsoleId.GameBoy => BuildGb(p, inst, d.Context),
            _ => BuildNes(p, inst),
        };
        if (problem == null && inst.Console == ConsoleId.Snes && PresetNote.SnesList(d) is { } voices) { p.Recipes = voices; p.Snes!.Recipes = voices; }
        return problem == null ? p : null;
    }

    // ---- SNES: an audio RAM with the sample at its address, the directory entry that points at it, and the envelope and mix the instrument carries as an overlay entry ----

    private static string? BuildSnes(PresetPlayable p, Instrument inst, byte[] context)
    {
        var dspRegs = context.AsSpan(0, PresetContext.SnesLength).ToArray();
        var sample = inst.Blobs[0].Data;
        int slot = inst.Index, origin = inst.Origin;
        if (slot is < 0 or > 255) return "its sample slot is outside the directory";
        if (sample.Length == 0 || sample.Length % 9 != 0) return "its sample is not whole 9-byte BRR blocks";
        if (origin < 0 || origin + sample.Length > SpcSnapshot.AramSize) return "its sample does not fit in audio RAM";
        int dir = dspRegs[0x5D] * 0x100, entry = dir + slot * 4;
        if (entry + 3 >= SpcSnapshot.AramSize) return "its directory entry is outside audio RAM";
        if (entry < origin + sample.Length && entry + 4 > origin) return "its directory entry would sit inside its sample";
        var aram = new byte[SpcSnapshot.AramSize];
        // a corrupted sample that plays on into the game's memory brings the bytes it plays into (rare: a sample keeps its end block and its loop inside itself unless the corruption broke them on purpose)
        var overrun = PresetContext.SnesOverrunRanges(context, out bool partial) ?? [];
        foreach (var (addr, data) in overrun)
            for (int i = 0; i < data.Length; i++) aram[(addr + i) & 0xFFFF] = data[i];
        p.Overrun = overrun.Sum(r => r.Data.Length);
        p.OverrunPartial = partial;
        Array.Copy(sample, 0, aram, origin, sample.Length);
        int loop = inst.Get("loop.addr");
        aram[entry] = (byte)origin; aram[entry + 1] = (byte)(origin >> 8); aram[entry + 2] = (byte)loop; aram[entry + 3] = (byte)(loop >> 8);
        var dsp = (byte[])dspRegs.Clone();
        for (int v = 0; v < 8; v++) for (int r = 0; r < 10; r++) dsp[v * 16 + r] = 0;       // the game's voices: volume, pitch, source, envelope, level readbacks. The sampler writes them all at every key-on; no game voice is playing here
        dsp[0x4C] = 0; dsp[0x7C] = 0;                                                       // key-on, end flags

        var e = new SnesOverlay.Entry { Srcn = (byte)slot, Blocks = (ushort)(sample.Length / 9) };
        e.Flags = SnesOverlay.Pinned;                                                         // listed and playable however the sample looks (a corrupted one may break the rules the list checks)
        if (!PresetMutants.On(PresetMutants.SnesEnvelopeDropped))
        {
            // the envelope the instrument carries, set explicitly: the game's voices are not here to supply it
            e.Flags |= SnesOverlay.HasEnv;
            e.Adsr1 = (byte)(inst.Get("env.adsr") << 7 | inst.Get("env.dr") << 4 | inst.Get("env.ar"));
            e.Adsr2 = (byte)(inst.Get("env.sl") << 5 | inst.Get("env.sr"));
            e.Gain = (byte)(inst.Get("gain.mode") << 7 | inst.Get("gain.curve") << 5 | inst.Get("gain.rate"));
        }
        int tune = inst.Get("tune.cents");
        if (tune != 0 && !PresetMutants.On(PresetMutants.SnesTuningDropped)) { e.Flags |= SnesOverlay.HasTune; e.Tune = (short)tune; }
        if (inst.Get("vol.l") != 127 || inst.Get("vol.r") != 127) { e.Flags |= SnesOverlay.HasVol; e.WeightL = (sbyte)inst.Get("vol.l"); e.WeightR = (sbyte)inst.Get("vol.r"); }
        int mix = inst.Get("mix.echo") | inst.Get("mix.noise") << 1 | inst.Get("mix.pmod") << 2, def = dsp[0x4D] != 0 ? SnesOverlay.MixEcho : 0;
        if (mix != def) { e.Flags |= SnesOverlay.HasMix; e.Mix = (byte)mix; }
        p.Snes = new SpcSnapshot(aram, dsp, SnesOverlay.With(null, e));
        p.InstrumentValue = slot;
        return null;
    }

    // ---- Master System, Game Gear, Genesis: the sound unit's snapshot with the one instrument in it, at the number the game's snapshot gave it ----

    private static string? BuildSega(PresetPlayable p, Instrument inst, byte[] ctx)
    {
        int kind = ctx[9];
        var snap = new SegaSnapshot(kind, BitConverter.ToInt32(ctx, 0), (PsgVariant)ctx[4]) { OpnClock = kind == SegaSnapshot.KindOpn ? BitConverter.ToInt32(ctx, 5) : 0 };
        int index = inst.Index;
        switch (inst.Kind)
        {
            case InstrumentKind.OpnPatch:
                if (kind != SegaSnapshot.KindOpn || index is < 0 or > 5) return "its patch number does not fit a Genesis";
                break;
            case InstrumentKind.OpnDac:
            {
                if (kind != SegaSnapshot.KindOpn || index < 6 || index - 6 >= MaxDacSounds) return "its sound number does not fit a Genesis";
                var stubs = new DacSample[index - 5];                                   // the sounds before it are placeholders (the sampler only reads the one it plays); the instrument is written over the last
                for (int i = 0; i < stubs.Length; i++) stubs[i] = new DacSample(8000, Enumerable.Repeat((byte)128, 96).ToArray(), -1, -1, 1);
                stubs[^1] = new DacSample(8000, Enumerable.Repeat((byte)128, inst.Blobs[0].Data.Length).ToArray(), -1, -1, 1);
                snap.Dac = stubs;
                break;
            }
            case InstrumentKind.OpllUser:
                if (kind != SegaSnapshot.KindOpll || index != 0) return "its instrument number does not fit a Master System FM unit";
                break;
            case InstrumentKind.OpllRom:
                if (kind != SegaSnapshot.KindOpll || index is < 1 or > 15) return "its instrument number does not fit a Master System FM unit";
                break;
            case InstrumentKind.PsgTones:
            case InstrumentKind.PsgNoise:
            {
                int rel = ctx[10];
                if (rel > 2 || inst.Kind == InstrumentKind.PsgTones != (rel == 0)) return "its place among the PSG's instruments is not known";
                index = snap.PsgInstrument + rel;
                break;
            }
            default: return $"a {inst.Kind} is not a Master System or Genesis instrument";
        }
        // the instruments that have settings the sampler plays are written into the snapshot by the model's own writer (the PSG's registers and a ROM instrument have none: the sampler plays the PSG from FL's pitch and level, the ROM from the chip)
        if (inst.Kind is InstrumentKind.OpnPatch or InstrumentKind.OpnDac or InstrumentKind.OpllUser)
        {
            var res = new SegaInstruments(snap).Write(inst);
            if (!res.Ok) return res.Reason ?? "the sound unit refused its settings";
            snap = (SegaSnapshot)res.Snapshot!;
            if (inst.Kind == InstrumentKind.OpnDac && inst.Field("dac.cycle") is { } cf)
            {
                // the loop's exact pitch (the field holds it to a hundredth of a hertz): what the game's snapshot had, so the note plays at the very same speed
                double exact = BitConverter.ToDouble(ctx, 11);
                var d = snap.Dac[index - 6];
                if (exact > 0 && Math.Round(exact * 100) == cf.Value) d.CycleHz = exact;
            }
        }
        p.Sega = snap;
        p.InstrumentValue = index + 1 + (PresetMutants.On(PresetMutants.GenesisPatchIndexOff) ? 1 : 0);
        return null;
    }

    // ---- Game Boy: the sound registers with the channel's written, and the wave RAM ----

    private static string? BuildGb(PresetPlayable p, Instrument inst, byte[] ctx)
    {
        if (inst.Index is < 0 or > 3) return "its channel is not a Game Boy channel";
        var snap = new GbSnapshot(new byte[GbSnapshot.RegCount], new byte[GbSnapshot.WaveSize], ctx[0] != 0);
        if (!PresetMutants.On(PresetMutants.GbDutyDropped) || inst.Kind == InstrumentKind.GbWave || inst.Kind == InstrumentKind.GbNoise)
        {
            var res = new GbInstruments(snap).Write(inst);
            if (!res.Ok) return res.Reason ?? "the sound unit refused its settings";
            snap = (GbSnapshot)res.Snapshot!;
        }
        p.Gb = snap;
        p.GbChannel = inst.Index;
        p.GbNoiseShort = inst.Kind == InstrumentKind.GbNoise && inst.Get("width") == 1;
        p.InstrumentValue = 0;
        return null;
    }

    // ---- NES: no sampler exists; the plugin's own register player (NesPresetPlayer) takes the instrument's fields ----

    private static string? BuildNes(PresetPlayable p, Instrument inst)
    {
        if (inst.Kind == InstrumentKind.NesDmc)
            p.CannotPlay = "cannot play on this console yet: the NES DMC reads its sample from the game's memory and Direct mode has no game to read it from";
        return null;
    }

    /// <summary>One line for the Emulator tab and the Presets tab: what is loaded.</summary>
    public static string Describe(PresetData d) => $"{PresetFile.ConsoleName(d.Console)}  {(d.InstrumentName.Length > 0 ? d.InstrumentName : d.Kind.ToString())}{(d.Corrupted ? $"  (corrupted, {d.Steps} step{(d.Steps == 1 ? "" : "s")})" : "")}{(d.NoteVersion != 0 ? "  +  " + PresetNote.Describe(d) : "")}";
}
