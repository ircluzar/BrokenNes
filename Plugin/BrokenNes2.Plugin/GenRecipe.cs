using NesEmulator.Sega;

namespace BrokenNes2;

// What a Genesis game's sound driver does with its instruments, learned by listening (round 6; the same idea as SpcRecipe.cs for the SNES).
//
// Instrument Runaway used to copy the YM2612's channel patches as they stood when the game froze. A Z80 (or 68000) sound driver loads a patch into a channel for each note, bends its frequency, sweeps its operators' total levels (the
// fades and swells), changes the LFO, and shapes the PSG's volume and pitch note by note: none of that is in one instant's register file. So the plugin now keeps a log of every write to the YM2612's registers (the DAC's data
// stream, which has its own recorder, left out) and to the PSG, and when Instrument Runaway starts it turns the recent writes into RECIPES: for each distinct way the driver sounded a channel (the patch it keyed on, or the PSG
// tone or noise), the settings at the key-on and a MACRO, the timed changes the driver made to that channel while the note sounded (pitch as cents from the key-on pitch, any patch register, the LFO, the PSG's attenuation and noise
// control). A recipe is played by an FL note: the key-on settings, then the macro with FL's pitch added to its pitch changes and FL's velocity added to the carriers' levels.
//
// What a recipe cannot be: the driver's decisions (which note comes next; behaviour that is random or depends on state that does not repeat note to note), anything that needs the other channels (the YM2612's channel 3 special
// mode, CSM), and anything outside these chips (the Z80 and the 68000 keep running in the game, not in the plugin). See docs/projects/round6-runaway.md.

/// <summary>What a recipe's macro changes. Pitch: cents from the key-on pitch (FM: the channel frequency; PSG: the tone). Param: an FM patch register, value = patch byte index x 256 + the register value (index n x 7 + k for operator n in algorithm order
/// and register group k = DT/MUL, TL, RS/AR, AM/D1R, D2R, SL/RR, SSG-EG; 28 = $B0; 29 = $B4). Lfo: the LFO register. Vol: the PSG's attenuation (0 loudest, 15 off). Noise: the PSG's noise control.</summary>
public enum GenEv : byte { Pitch = 0, Param = 1, Lfo = 2, Vol = 3, Noise = 4 }

/// <summary>One timed change: <c>T</c> samples (at 32 kHz) after the key-on.</summary>
public readonly record struct GenEvent(int T, GenEv Kind, int Value);

/// <summary>One way a game's driver sounds a channel: how it started the note and what it did to the channel while it sounded.</summary>
public sealed class GenRecipe
{
    public const int KindFm = 0, KindPsgTone = 1, KindPsgNoise = 2;
    public int Kind;
    /// <summary>FM: the channel's registers as they were at the key-on: 4 operators x 7 (algorithm order), then $B0 and $B4 (the same layout as <see cref="SegaSnapshot.OpnPatch"/>).</summary>
    public byte[] Patch = new byte[SegaSnapshot.OpnPatchBytes];
    public int Block, Fnum;
    /// <summary>FM: the operators keyed (the high nibble of the key-on write), and the LFO register at the key-on.</summary>
    public int OpMask = 0xF, Lfo;
    /// <summary>PSG: the tone register (period), the attenuation and the noise control at the key-on.</summary>
    public int Period, Att, Noise;
    public int Uses, LengthSamples;
    public bool KeyedOff, Truncated, Held;
    public GenEvent[] Events = [];

    public double Seconds => LengthSamples / 32000.0;
    public bool IsFm => Kind == KindFm;

    public int Count(GenEv kind) { int n = 0; foreach (var e in Events) if (e.Kind == kind) n++; return n; }

    /// <summary>The loudest attenuation the PSG note asked for (smallest register value, at the key-on or later): FL's velocity maps to this.</summary>
    public int LoudestAtt
    {
        get
        {
            int a = Att;
            foreach (var e in Events) if (e.Kind == GenEv.Vol) a = Math.Min(a, e.Value);
            return a;
        }
    }

    /// <summary>The note never made a sound: a PSG note whose attenuation was 15 (off) throughout, or an FM note with every carrier at a total level of 127.</summary>
    public bool Silent
    {
        get
        {
            if (Kind == KindFm)
            {
                int mask = NesEmulator.Mix.FmPatchBank.CarriersOf(Patch[28] & 7);
                for (int n = 0; n < 4; n++)
                {
                    if ((mask >> n & 1) == 0) continue;
                    if ((Patch[n * 7 + 1] & 0x7F) < 127) return false;
                    foreach (var e in Events) if (e.Kind == GenEv.Param && e.Value >> 8 == n * 7 + 1 && (e.Value & 0x7F) < 127) return false;
                }
                return true;
            }
            return LoudestAtt >= 15;
        }
    }

    /// <summary>The picker's line for this recipe: a name and a note.</summary>
    public (string Name, string Note) Describe(int index)
    {
        var moves = new List<string>();
        int pitch = Count(GenEv.Pitch), param = Count(GenEv.Param), vol = Count(GenEv.Vol), lfo = Count(GenEv.Lfo), noise = Count(GenEv.Noise);
        if (pitch > 0) moves.Add($"{pitch} pitch changes");
        if (param > 0) moves.Add($"{param} patch changes");
        if (vol > 0) moves.Add($"{vol} volume changes");
        if (lfo > 0) moves.Add($"{lfo} LFO changes");
        if (noise > 0) moves.Add($"{noise} noise changes");
        string prefix = Held ? "Sounding voice" : "Game voice";
        string name = Kind switch
        {
            KindFm => $"{prefix} {index + 1}: FM algorithm {Patch[28] & 7}, feedback {Patch[28] >> 3 & 7}",
            KindPsgTone => $"{prefix} {index + 1}: PSG tone",
            _ => $"{prefix} {index + 1}: PSG {((Noise & 4) != 0 ? "white" : "periodic")} noise",
        };
        string note = (Held ? $"was sounding when the game stopped playing notes, for {Seconds:0.#} s" : $"played {Uses}x, {Seconds:0.00} s") + (moves.Count > 0 ? ", " + string.Join(", ", moves) : ", no changes while it sounds") + (Truncated ? ", cut at the macro limit" : "");
        return (name, note);
    }

    // ---- saved with the project (tag GNR1) ----

    public const uint Tag = 0x31524E47;            // "GNR1"
    public const int MaxRecipes = 48, MaxEvents = 1024, MaxSection = 600_000;
    private const int Version = 1;

    public static byte[] Write(IReadOnlyList<GenRecipe> list)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)Version);
        w.Write((byte)Math.Min(list.Count, MaxRecipes));
        for (int i = 0; i < Math.Min(list.Count, MaxRecipes); i++)
        {
            var r = list[i];
            w.Write((byte)r.Kind);
            w.Write(r.Patch);
            w.Write((byte)r.Block); w.Write((ushort)r.Fnum); w.Write((byte)r.OpMask); w.Write((byte)r.Lfo);
            w.Write((ushort)r.Period); w.Write((byte)r.Att); w.Write((byte)r.Noise);
            w.Write((byte)((r.KeyedOff ? 1 : 0) | (r.Truncated ? 2 : 0) | (r.Held ? 4 : 0)));
            w.Write((ushort)Math.Min(r.Uses, 65535)); w.Write(r.LengthSamples);
            int n = Math.Min(r.Events.Length, MaxEvents);
            Varint(w, (uint)n);
            int last = 0;
            for (int k = 0; k < n; k++)
            {
                var e = r.Events[k];
                Varint(w, (uint)(e.T - last)); last = e.T;
                w.Write((byte)e.Kind);
                Varint(w, (uint)((e.Value << 1) ^ (e.Value >> 31)));
            }
        }
        return ms.ToArray();
    }

    private static void Varint(BinaryWriter w, uint v)
    {
        while (v >= 0x80) { w.Write((byte)(v | 0x80)); v >>= 7; }
        w.Write((byte)v);
    }

    private static uint ReadVarint(BinaryReader r)
    {
        uint v = 0; int shift = 0;
        while (true)
        {
            byte b = r.ReadByte();
            v |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return v;
            shift += 7;
            if (shift > 28) throw new InvalidDataException("varint too long");
        }
    }

    /// <summary>Reads a saved recipe list; null for anything that is not one (a damaged or foreign stream is survived, never trusted: every count and range is checked).</summary>
    public static GenRecipe[]? Read(byte[] b)
    {
        if (b.Length < 2 || b.Length > MaxSection) return null;
        try
        {
            using var r = new BinaryReader(new MemoryStream(b));
            if (r.ReadByte() != Version) return null;
            int count = r.ReadByte();
            if (count > MaxRecipes) return null;
            var list = new GenRecipe[count];
            long totalEvents = 0;
            for (int i = 0; i < count; i++)
            {
                var rec = new GenRecipe { Kind = r.ReadByte() };
                if (rec.Kind > KindPsgNoise) return null;
                if (r.Read(rec.Patch, 0, SegaSnapshot.OpnPatchBytes) != SegaSnapshot.OpnPatchBytes) return null;
                rec.Block = r.ReadByte(); rec.Fnum = r.ReadUInt16(); rec.OpMask = r.ReadByte(); rec.Lfo = r.ReadByte();
                if (rec.Block > 7 || rec.Fnum > 2047 || rec.OpMask > 15) return null;
                rec.Period = r.ReadUInt16(); rec.Att = r.ReadByte(); rec.Noise = r.ReadByte();
                if (rec.Period > 1023 || rec.Att > 15 || rec.Noise > 7) return null;
                int f = r.ReadByte();
                if (f > 7) return null;
                rec.KeyedOff = (f & 1) != 0; rec.Truncated = (f & 2) != 0; rec.Held = (f & 4) != 0;
                rec.Uses = r.ReadUInt16();
                rec.LengthSamples = r.ReadInt32();
                if (rec.LengthSamples < 0 || rec.LengthSamples > 32000 * 600) return null;
                uint n = ReadVarint(r);
                if (n > MaxEvents || (totalEvents += n) > MaxRecipes * MaxEvents) return null;
                var ev = new GenEvent[n];
                long t = 0;
                for (int k = 0; k < n; k++)
                {
                    t += ReadVarint(r);
                    if (t > 32000 * 600) return null;
                    byte kind = r.ReadByte();
                    if (kind > (byte)GenEv.Noise) return null;
                    uint z = ReadVarint(r);
                    int v = (int)(z >> 1) ^ -(int)(z & 1);
                    if (!ValueOk((GenEv)kind, v)) return null;
                    ev[k] = new GenEvent((int)t, (GenEv)kind, v);
                }
                rec.Events = ev;
                list[i] = rec;
            }
            return r.BaseStream.Position == b.Length ? list : null;
        }
        catch (Exception) { return null; }
    }

    private static bool ValueOk(GenEv kind, int v) => kind switch
    {
        GenEv.Pitch => v is >= -12000 and <= 12000,
        GenEv.Param => v >> 8 is >= 0 and < SegaSnapshot.OpnPatchBytes && v >= 0 && (v & 0xFF) <= 255,
        GenEv.Lfo => v is >= 0 and <= 255,
        GenEv.Vol => v is >= 0 and <= 15,
        _ => v is >= 0 and <= 7,
    };

    /// <summary>A hash of everything a recipe is (tests compare derived and restored recipes with it).</summary>
    public ulong Hash()
    {
        ulong h = 1469598103934665603UL;
        void Mix(long v) => h = (h ^ (ulong)v) * 1099511628211UL;
        Mix(Kind); foreach (byte p in Patch) Mix(p);
        Mix(Block); Mix(Fnum); Mix(OpMask); Mix(Lfo); Mix(Period); Mix(Att); Mix(Noise); Mix(Held ? 1 : 0); Mix(Uses); Mix(LengthSamples);
        foreach (var e in Events) { Mix(e.T); Mix((int)e.Kind); Mix(e.Value); }
        return h;
    }
}

/// <summary>
/// Every write the Genesis game made to the YM2612's registers (the DAC's data stream left out: <see cref="DacRecorder"/> has it) and to the PSG, newest last, in a ring of 262,144. Allocated once, with the game; the game's session
/// fills it from the audio thread without allocating (<see cref="OnYm"/>, <see cref="OnPsg"/>); read under the emulator's lock, between frames. <see cref="Begin"/> keeps the chips' registers at the start, and the writes that fall off the
/// ring are folded into them, so the registers' values at any later moment are known.
/// </summary>
public sealed class GenWriteLog
{
    public const int Capacity = 1 << 18;
    public const int PsgMarker = 0x200;
    private readonly long[] time = new long[Capacity];
    private readonly ushort[] reg = new ushort[Capacity];
    private readonly byte[] val = new byte[Capacity];
    private readonly byte[] baseYm = new byte[512];
    private readonly int[] basePsgPeriod = new int[3], basePsgVolume = [15, 15, 15, 15];
    private int basePsgNoise;
    private long count;
    private readonly int[] addr = new int[2];

    public long Count => count;
    public long Dropped => Math.Max(0, count - Capacity);
    public byte[] BaseYm => baseYm;
    public int BasePsgPeriod(int i) => basePsgPeriod[i];
    public int BasePsgVolume(int i) => basePsgVolume[i];
    public int BasePsgNoise => basePsgNoise;
    /// <summary>The master clock rate the clocks in this log are in (set by <see cref="Begin"/>).</summary>
    public double MasterHz { get; private set; } = 53_693_175;

    /// <summary>Starts the log over on a sound unit whose chips hold these registers now.</summary>
    public void Begin(MdSoundUnit unit, double masterHz)
    {
        for (int i = 0; i < 512; i++) baseYm[i] = unit.Fm.Register(i);
        for (int i = 0; i < 3; i++) basePsgPeriod[i] = unit.Psg.Period(i);
        for (int i = 0; i < 4; i++) basePsgVolume[i] = unit.Psg.Volume(i);
        basePsgNoise = unit.Psg.NoiseControl;
        MasterHz = masterHz;
        count = 0;
    }

    public void Clear() { count = 0; }

    private void Put(long master, int r, byte v)
    {
        int i = (int)(count & (Capacity - 1));
        if (count >= Capacity) Fold(reg[i], val[i]);                  // the oldest write leaves the ring: the starting state takes it in
        time[i] = master; reg[i] = (ushort)r; val[i] = v;
        count++;
    }

    private void Fold(int r, byte v)
    {
        if (r < 512) baseYm[r] = v;
        else { var s = new PsgDecoder(basePsgPeriod, basePsgVolume, basePsgNoise, foldLatch); s.Write(v); basePsgNoise = s.Noise; foldLatch = s.Latch; }
    }
    private int foldLatch;

    /// <summary>The YM2612 port tap: (master clock, port 0 to 3, value). Ports 0 and 2 set the register address of part 1 and 2; 1 and 3 write it. A DAC data write (register $2A of part 1) is not kept.</summary>
    public void OnYm(long master, int port, byte v)
    {
        int part = port >> 1 & 1;
        if ((port & 1) == 0) { addr[part] = v; return; }
        int reg = addr[part];
        if (part == 0 && reg == 0x2A) return;
        Put(master, part * 256 + reg, v);
    }

    /// <summary>The PSG port tap: (master clock, value).</summary>
    public void OnPsg(long master, byte v) => Put(master, PsgMarker, v);

    public void Get(long index, out long master, out int r, out byte value)
    {
        int i = (int)(index & (Capacity - 1));
        master = time[i]; r = reg[i]; value = val[i];
    }
}

/// <summary>The SN76489's registers rebuilt from its writes: a latch byte (bit 7 set) names a register and gives its low 4 bits, a data byte (bit 7 clear) continues the latched tone register's high 6 bits.</summary>
public struct PsgDecoder
{
    private readonly int[] period, volume;
    public int Noise, Latch;
    public PsgDecoder(int[] period, int[] volume, int noise, int latch) { this.period = period; this.volume = volume; Noise = noise; Latch = latch; }

    /// <summary>Applies one write. Returns the channel (0 to 3) whose register changed and sets <paramref name="isVolume"/>, or -1 when nothing did.</summary>
    public int Write(byte v) => Write(v, out _);

    public int Write(byte v, out bool isVolume)
    {
        isVolume = false;
        if ((v & 0x80) != 0)
        {
            Latch = v >> 4 & 7;                                    // bits 6-5 channel, bit 4 volume
            int ch = Latch >> 1;
            if ((Latch & 1) != 0) { volume[ch] = v & 15; isVolume = true; return ch; }
            if (ch < 3) { period[ch] = period[ch] & 0x3F0 | v & 15; return ch; }
            Noise = v & 7; return 3;
        }
        int c = Latch >> 1;
        if ((Latch & 1) != 0) { volume[c] = v & 15; isVolume = true; return c; }
        if (c < 3) { period[c] = period[c] & 15 | (v & 0x3F) << 4; return c; }
        Noise = v & 7;
        return 3;
    }
}

/// <summary>What deriving Genesis recipes found.</summary>
public sealed record GenDeriveStats(int KeyOns, int Notes, int Recipes, int SkippedSilent, long WritesInWindow, long LogDropped, double WindowSeconds)
{
    public string Short() => Recipes > 0 ? $"Listened for {WindowSeconds:0.#} s: {KeyOns} notes, {Recipes} game voice{(Recipes == 1 ? "" : "s")}." : $"No notes in the {WindowSeconds:0.#} s it listened to: let the game play first.";
    public override string ToString() => $"{KeyOns} notes in {WindowSeconds:0.#} s ({WritesInWindow:N0} chip writes), {Recipes} recipes" + (SkippedSilent > 0 ? $", {SkippedSilent} silent notes left out" : "") + (LogDropped > 0 ? $", {LogDropped:N0} older writes had fallen off the log" : "");
}

public static class GenRecipes
{
    public const double ListenWindowSeconds = 30;
    public const long MaxNoteSamples = 32000L * 8;

    private sealed class Open
    {
        public int Kind, Channel;                           // FM: 0 to 5; PSG: 0 to 3
        public long T0;
        public byte[] Patch = new byte[SegaSnapshot.OpnPatchBytes];
        public int Block, Fnum, OpMask, Lfo, Period, Att, Noise;
        public int LastCents;
        public double Step0;                                // FM: phase step at the key-on; PSG: the period
        public readonly List<GenEvent> Events = new();
        public bool Truncated, Held;
    }

    public sealed record GenNote(int Channel, long Start, long End, bool KeyedOff, GenRecipe Recipe);

    private static int PhaseStep(int block, int fnum) => NesEmulator.Mix.FmPatchBank.PhaseStep(block, fnum);

    /// <summary>The patch register an FM register write is, as an index into <see cref="GenRecipe.Patch"/> (-1: not a patch register of this channel). <paramref name="channel"/> is 0 to 5.</summary>
    private static int PatchIndexOf(int channel, int r)
    {
        int part = channel / 3, c = channel % 3;
        if (r < part * 256 || r >= part * 256 + 256) return -1;
        int low = r & 0xFF;
        if (low >= 0xB0 && low <= 0xB2 && low - 0xB0 == c) return 28;
        if (low >= 0xB4 && low <= 0xB6 && low - 0xB4 == c) return 29;
        if (low < 0x30 || low >= 0xA0 || (low & 3) != c) return -1;
        int k = (low >> 4) - 3, offset = low & 0x0C;
        int n = offset switch { 0 => 0, 8 => 1, 4 => 2, _ => 3 };
        return n * 7 + k;
    }

    /// <summary>Every note of the window with its own macro (what <see cref="Derive"/> groups into recipes). <paramref name="unitNow"/> says which channels sound now (a channel that sounds when the listening ends and was not keyed on in the window is a held voice).</summary>
    public static List<GenNote> Notes(GenWriteLog log, long nowMaster, long windowSamples, bool[] fmSoundingNow, bool[] psgSoundingNow, out GenDeriveStats stats, out int silent)
    {
        double toSamples = 32000.0 / log.MasterHz;
        long windowMaster = (long)(windowSamples / toSamples);
        long startMaster = nowMaster - Math.Min(windowMaster, nowMaster);

        // the chips' registers at the start of the window: the log's starting state with the writes older than the window folded in
        var ym = new int[512];
        for (int i = 0; i < 512; i++) ym[i] = log.BaseYm[i];
        var psgPeriod = new int[3]; var psgVol = new int[4];
        for (int i = 0; i < 3; i++) psgPeriod[i] = log.BasePsgPeriod(i);
        for (int i = 0; i < 4; i++) psgVol[i] = log.BasePsgVolume(i);
        var psg = new PsgDecoder(psgPeriod, psgVol, log.BasePsgNoise, 0);
        var inWindow = new List<(long At, int Reg, byte Val)>();
        for (long i = log.Dropped; i < log.Count; i++)
        {
            log.Get(i, out long m, out int r, out byte v);
            if (m < startMaster) { if (r < 512) ym[r] = v; else psg.Write(v); continue; }
            inWindow.Add((m, r, v));
        }

        var fm = new Open?[6];
        var tone = new Open?[4];            // PSG tone 0 to 2 and the noise voice (3)
        var notes = new List<(Open Note, long End, bool KeyOff)>();
        int keyOns = 0;

        void Close(Open?[] slot, int i, long at, bool keyOff) { if (slot[i] is { } n) { if (!n.Held || at >= nowMaster) notes.Add((n, at, keyOff)); slot[i] = null; } }
        void Add(Open n, long at, GenEv kind, int value)
        {
            long dt = (long)((at - n.T0) * toSamples);
            if (dt > MaxNoteSamples || n.Events.Count >= GenRecipe.MaxEvents) { n.Truncated = true; return; }
            if (n.Events.Count > 0 && n.Events[^1].T == (int)dt && n.Events[^1].Kind == kind && (kind != GenEv.Param || n.Events[^1].Value >> 8 == value >> 8)) { n.Events[^1] = new GenEvent((int)dt, kind, value); return; }
            n.Events.Add(new GenEvent((int)dt, kind, value));
        }
        Open OpenFm(int ch, long at, int opMask, bool held)
        {
            var n = new Open { Kind = GenRecipe.KindFm, Channel = ch, T0 = at, OpMask = opMask, Held = held };
            int part = ch / 3, c = ch % 3;
            int[] off = [0, 8, 4, 12];
            for (int o = 0; o < 4; o++) for (int k = 0; k < 7; k++) n.Patch[o * 7 + k] = (byte)ym[part * 256 + 0x30 + k * 0x10 + off[o] + c];
            n.Patch[28] = (byte)ym[part * 256 + 0xB0 + c]; n.Patch[29] = (byte)ym[part * 256 + 0xB4 + c];
            n.Fnum = (ym[part * 256 + 0xA4 + c] & 7) << 8 | ym[part * 256 + 0xA0 + c];
            n.Block = ym[part * 256 + 0xA4 + c] >> 3 & 7;
            n.Lfo = ym[0x22];
            n.Step0 = Math.Max(1, PhaseStep(n.Block, n.Fnum));
            return n;
        }
        Open OpenPsg(int ch, long at, bool held)
        {
            var n = new Open { Kind = ch == 3 ? GenRecipe.KindPsgNoise : GenRecipe.KindPsgTone, Channel = ch, T0 = at, Att = psgVol[ch], Noise = psg.Noise, Period = ch < 3 ? psgPeriod[ch] : psgPeriod[2], Held = held };
            n.Step0 = Math.Max(1, n.Period);
            return n;
        }

        // held notes: channels that sound at the end and are not keyed on again inside the window start as notes held since the window began
        if (windowSamples > 0)
        {
            for (int ch = 0; ch < 6; ch++) if (fmSoundingNow[ch] && !(ch == 5 && (ym[0x2B] & 0x80) != 0)) fm[ch] = OpenFm(ch, startMaster, 0xF, true);
            for (int ch = 0; ch < 4; ch++) if (psgSoundingNow[ch] && psgVol[ch] < 15) tone[ch] = OpenPsg(ch, startMaster, true);
        }

        foreach (var (at, r, v) in inWindow)
        {
            if (r == GenWriteLog.PsgMarker)
            {
                int prevVol = 0;
                // the register this write touches, and what it was
                int[] before = [psgVol[0], psgVol[1], psgVol[2], psgVol[3]];
                int[] beforePer = [psgPeriod[0], psgPeriod[1], psgPeriod[2]];
                int beforeNoise = psg.Noise;
                int ch = psg.Write(v, out bool isVol);
                if (ch < 0) continue;
                if (isVol)
                {
                    prevVol = before[ch];
                    if (prevVol >= 15 && psgVol[ch] < 15) { keyOns++; Close(tone, ch, at, false); tone[ch] = OpenPsg(ch, at, false); tone[ch]!.Att = psgVol[ch]; }
                    else if (psgVol[ch] >= 15 && prevVol < 15) Close(tone, ch, at, true);
                    else if (tone[ch] is { } n) Add(n, at, GenEv.Vol, psgVol[ch]);
                }
                else if (ch < 3)
                {
                    if (tone[ch] is { } n && psgPeriod[ch] != beforePer[ch])
                    {
                        int cents = psgPeriod[ch] <= 0 ? 12000 : (int)Math.Clamp(Math.Round(1200 * Math.Log2(n.Step0 / psgPeriod[ch])), -12000, 12000);      // a smaller period is a higher tone
                        if (cents != n.LastCents) { n.LastCents = cents; Add(n, at, GenEv.Pitch, cents); }
                    }
                    // the third tone's period is the noise voice's rate when the noise control asks for it: a noise note feels it too
                    if (ch == 2 && tone[3] is { } nn && (psg.Noise & 3) == 3 && psgPeriod[2] != beforePer[2]) { /* the noise rate moved: carried by the noise voice's own control events */ }
                }
                else if (tone[3] is { } nv && psg.Noise != beforeNoise) Add(nv, at, GenEv.Noise, psg.Noise);
                continue;
            }

            ym[r] = v;
            if (r == 0x28)
            {
                int sel = v & 7, ops = v >> 4;
                int ch = sel switch { 0 => 0, 1 => 1, 2 => 2, 4 => 3, 5 => 4, 6 => 5, _ => -1 };
                if (ch < 0) continue;
                if (ops != 0)
                {
                    keyOns++;
                    Close(fm, ch, at, false);
                    if (ch == 5 && (ym[0x2B] & 0x80) != 0) continue;           // channel 6 on the DAC makes no FM note
                    fm[ch] = OpenFm(ch, at, ops, false);
                }
                else Close(fm, ch, at, true);
                continue;
            }
            if (r == 0x22)
            {
                for (int ch = 0; ch < 6; ch++) if (fm[ch] is { } n) Add(n, at, GenEv.Lfo, v);
                continue;
            }
            for (int ch = 0; ch < 6; ch++)
            {
                if (fm[ch] is not { } n) continue;
                int part = ch / 3, c = ch % 3;
                int low = r & 0xFF;
                if (r >> 8 != part) continue;
                if (low == 0xA0 + c)
                {
                    int fnum = (ym[part * 256 + 0xA4 + c] & 7) << 8 | v, block = ym[part * 256 + 0xA4 + c] >> 3 & 7;
                    int step = Math.Max(1, PhaseStep(block, fnum));
                    int cents = (int)Math.Clamp(Math.Round(1200 * Math.Log2(step / n.Step0)), -12000, 12000);
                    if (cents != n.LastCents) { n.LastCents = cents; Add(n, at, GenEv.Pitch, cents); }
                    continue;
                }
                int idx = PatchIndexOf(ch, r);
                if (idx >= 0) Add(n, at, GenEv.Param, idx * 256 + v);
            }
        }
        for (int ch = 0; ch < 6; ch++) Close(fm, ch, nowMaster, false);
        for (int ch = 0; ch < 4; ch++) Close(tone, ch, nowMaster, false);

        var result = new List<GenNote>(notes.Count);
        silent = 0;
        foreach (var (n, endMaster, keyOff) in notes)
        {
            var rec = new GenRecipe
            {
                Kind = n.Kind, Block = n.Block, Fnum = n.Fnum, OpMask = n.OpMask, Lfo = n.Lfo, Period = n.Period, Att = n.Att, Noise = n.Noise,
                Uses = 1, LengthSamples = (int)Math.Min((long)((endMaster - n.T0) * toSamples), int.MaxValue / 2), KeyedOff = keyOff, Truncated = n.Truncated, Held = n.Held, Events = n.Events.ToArray(),
            };
            n.Patch.CopyTo(rec.Patch, 0);
            if (rec.Silent) { silent++; continue; }
            result.Add(new GenNote(n.Channel, (long)((n.T0 - startMaster) * toSamples), (long)((endMaster - startMaster) * toSamples), keyOff, rec));
        }
        stats = new GenDeriveStats(keyOns, notes.Count, 0, silent, inWindow.Count, log.Dropped, Math.Min(windowSamples, (long)(nowMaster * toSamples)) / 32000.0);
        return result;
    }

    private static byte[] MaskLevels(byte[] patch)
    {
        var m = (byte[])patch.Clone();
        for (int n = 0; n < 4; n++) m[n * 7 + 1] = 0;
        return m;
    }

    /// <summary>Turns the chips' writes of the last <paramref name="windowSamples"/> samples (32 kHz) before <paramref name="nowMaster"/> into recipes: one per distinct way a channel was sounded (the patch and key-on frequency's block for FM, the tone or noise kind and noise control for the PSG),
    /// the note with the most going on (then the longest) supplying the macro.</summary>
    public static GenRecipe[] Derive(GenWriteLog log, long nowMaster, long windowSamples, bool[] fmSoundingNow, bool[] psgSoundingNow, out GenDeriveStats stats)
    {
        var all = Notes(log, nowMaster, windowSamples, fmSoundingNow, psgSoundingNow, out stats, out _);
        var groups = new Dictionary<string, (GenRecipe Recipe, int Score)>();
        foreach (var note in all)
        {
            var r = note.Recipe;
            // an FM recipe is a patch (the operators' total levels, which a driver sets for each note's volume, are not part of what makes two notes the same instrument)
            string key = r.IsFm ? $"F{Convert.ToHexString(MaskLevels(r.Patch))}{r.OpMask:X}{(r.Held ? 'h' : ' ')}" : $"P{r.Kind}{r.Noise}{(r.Held ? 'h' : ' ')}";
            int score = Math.Min(r.Events.Length, 400) * 100000 + Math.Min(r.LengthSamples, 99999);
            if (groups.TryGetValue(key, out var g))
            {
                g.Recipe.Uses++;
                if (score > g.Score) { r.Uses = g.Recipe.Uses; groups[key] = (r, score); }
            }
            else groups[key] = (r, score);
        }
        var result = groups.Values.Select(g => g.Recipe).OrderByDescending(r => r.Uses).ThenByDescending(r => r.Events.Length).ThenBy(r => r.Kind).Take(GenRecipe.MaxRecipes).ToArray();
        stats = stats with { Recipes = result.Length };
        return result;
    }
}
