namespace BrokenNes2;

// What a SNES game's sound driver really does with its instruments, learned by listening (round 6).
//
// Instrument Runaway used to copy the game's audio RAM and S-DSP registers at one instant and play FL notes on a private S-DSP over that copy. A corrupted game keeps most of its character in what the SPC700 program DOES
// with the chip over time (which registers it writes at a key-on, how it bends the pitch and the volume while a note sounds, which envelope it sets), none of which is in a copy of memory. So the plugin now also records every
// write the SPC700 makes to the S-DSP (SpcWriteLog, a ring that the game's session fills), and when Instrument Runaway starts it turns the recent writes into RECIPES: for each distinct way the driver keyed a voice on
// (sample number, envelope, noise / echo / pitch-modulation flags), the settings at the key-on and a MACRO, the timed changes the driver made to that voice's pitch (as cents from the key-on pitch), volume, envelope and
// flags while the note sounded. A recipe is played by an FL note: the key-on settings, then the macro, with FL's pitch added to the macro's pitch changes and FL's velocity scaling the volumes.
//
// What a recipe cannot be: the driver's decisions (which note comes next, random or state-dependent behaviour that does not repeat note to note), anything between voices (pitch modulation needs the neighbouring voice's
// output), and anything outside the S-DSP (the SPC700 and the song keep running in the game, not in the plugin). See docs/projects/round6-runaway.md.

/// <summary>What a recipe's macro changes (the S-DSP's per-voice registers and the voice's bits in the global ones).</summary>
public enum SpcEv : byte { Pitch = 0, VolL = 1, VolR = 2, Adsr1 = 3, Adsr2 = 4, Gain = 5, Srcn = 6, Flags = 7 }

/// <summary>One timed change: <c>T</c> samples (32 kHz) after the key-on. Pitch: cents from the key-on pitch. VolL / VolR: the signed register. Adsr1 / Adsr2 / Gain / Srcn: the register. Flags: bit 0 noise, bit 1 echo, bit 2 pitch modulation.</summary>
public readonly record struct SpcEvent(int T, SpcEv Kind, int Value);

/// <summary>One way a game's driver plays an instrument: how it keyed a voice on, and what it did to the voice while it sounded.</summary>
public sealed class SpcRecipe
{
    public int Srcn, Adsr1, Adsr2, Gain, VolL, VolR, Pitch, NoiseRate;
    public bool Non, Eon, Pmon;
    /// <summary>How many key-ons with these settings the listening window held.</summary>
    public int Uses;
    /// <summary>How long the note the macro was taken from sounded, in samples (32 kHz).</summary>
    public int LengthSamples;
    public bool KeyedOff, Truncated;
    /// <summary>The voice was already sounding when the listening began and was never keyed on again: the game's driver was not playing notes any more (or this was one note held for the whole time), and this is what the voice was doing.</summary>
    public bool Held;
    public SpcEvent[] Events = [];

    public double Seconds => LengthSamples / 32000.0;

    /// <summary>The loudest the note ever asked for (the volume registers' absolute value, at the key-on or later): FL's velocity maps to this, so a recipe is as loud as a plain instrument at the same velocity.</summary>
    public int PeakVolume
    {
        get
        {
            int p = Math.Max(Math.Abs(VolL), Math.Abs(VolR));
            foreach (var e in Events) if (e.Kind is SpcEv.VolL or SpcEv.VolR) p = Math.Max(p, Math.Abs(e.Value));
            return Math.Max(1, p);
        }
    }

    public int Count(SpcEv kind) { int n = 0; foreach (var e in Events) if (e.Kind == kind) n++; return n; }

    /// <summary>The note never asked for any volume (both registers zero at the key-on and whatever the driver did after): nothing to play.</summary>
    public bool Silent
    {
        get
        {
            if (VolL != 0 || VolR != 0) return false;
            foreach (var e in Events) if (e.Kind is SpcEv.VolL or SpcEv.VolR && e.Value != 0) return false;
            return true;
        }
    }

    /// <summary>The picker's line for this recipe: a name and a note.</summary>
    public (string Name, string Note) Describe(int index)
    {
        int pitch = Count(SpcEv.Pitch), vol = Count(SpcEv.VolL) + Count(SpcEv.VolR), env = Count(SpcEv.Adsr1) + Count(SpcEv.Adsr2) + Count(SpcEv.Gain) + Count(SpcEv.Srcn), fl = Count(SpcEv.Flags);
        var moves = new List<string>();
        if (pitch > 0) moves.Add($"{pitch} pitch changes");
        if (vol > 0) moves.Add($"{vol} volume changes");
        if (env > 0) moves.Add($"{env} envelope changes");
        if (fl > 0) moves.Add($"{fl} flag changes");
        var flags = new List<string>();
        if (Non) flags.Add("noise");
        if (Eon) flags.Add("echo");
        if (Pmon) flags.Add("pitch modulation (not replayed)");
        string env0 = (Adsr1 & 0x80) != 0 ? $"ADSR {Adsr1:X2}{Adsr2:X2}" : $"gain {Gain:X2}";
        string name = Held ? $"Sounding voice {index + 1}: sample #{Srcn}, {env0}" : $"Game voice {index + 1}: sample #{Srcn}, {env0}";
        string note = (Held ? $"was sounding when the game stopped keying notes, for {Seconds:0.#} s" : $"played {Uses}x, {Seconds:0.00} s") + (moves.Count > 0 ? ", " + string.Join(", ", moves) : ", no changes while it sounds") + (flags.Count > 0 ? ", " + string.Join(", ", flags) : "") + (Truncated ? ", cut at the macro limit" : "");
        return (name, note);
    }

    // ---- saved with the project (tag SPR1) ----

    public const uint Tag = 0x31525053;            // "SPR1"
    public const int MaxRecipes = 48, MaxEvents = 1024, MaxSection = 600_000;
    private const int Version = 1;

    public static byte[] Write(IReadOnlyList<SpcRecipe> list)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)Version);
        w.Write((byte)Math.Min(list.Count, MaxRecipes));
        for (int i = 0; i < Math.Min(list.Count, MaxRecipes); i++)
        {
            var r = list[i];
            w.Write((byte)r.Srcn); w.Write((byte)r.Adsr1); w.Write((byte)r.Adsr2); w.Write((byte)r.Gain);
            w.Write((sbyte)r.VolL); w.Write((sbyte)r.VolR); w.Write((ushort)r.Pitch);
            w.Write((byte)((r.Non ? 1 : 0) | (r.Eon ? 2 : 0) | (r.Pmon ? 4 : 0) | (r.KeyedOff ? 8 : 0) | (r.Truncated ? 16 : 0) | (r.Held ? 32 : 0)));
            w.Write((byte)r.NoiseRate); w.Write((ushort)Math.Min(r.Uses, 65535)); w.Write(r.LengthSamples);
            int n = Math.Min(r.Events.Length, MaxEvents);
            Varint(w, (uint)n);
            int last = 0;
            for (int k = 0; k < n; k++)
            {
                var e = r.Events[k];
                Varint(w, (uint)(e.T - last)); last = e.T;
                w.Write((byte)e.Kind);
                Varint(w, (uint)((e.Value << 1) ^ (e.Value >> 31)));          // zigzag
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
    public static SpcRecipe[]? Read(byte[] b)
    {
        if (b.Length < 2 || b.Length > MaxSection) return null;
        try
        {
            using var r = new BinaryReader(new MemoryStream(b));
            if (r.ReadByte() != Version) return null;
            int count = r.ReadByte();
            if (count > MaxRecipes) return null;
            var list = new SpcRecipe[count];
            long totalEvents = 0;
            for (int i = 0; i < count; i++)
            {
                var rec = new SpcRecipe
                {
                    Srcn = r.ReadByte(), Adsr1 = r.ReadByte(), Adsr2 = r.ReadByte(), Gain = r.ReadByte(),
                    VolL = r.ReadSByte(), VolR = r.ReadSByte(), Pitch = r.ReadUInt16(),
                };
                if (rec.Pitch is 0 or > 0x3FFF) return null;
                int f = r.ReadByte();
                if (f > 63) return null;
                rec.Non = (f & 1) != 0; rec.Eon = (f & 2) != 0; rec.Pmon = (f & 4) != 0; rec.KeyedOff = (f & 8) != 0; rec.Truncated = (f & 16) != 0; rec.Held = (f & 32) != 0;
                rec.NoiseRate = r.ReadByte();
                if (rec.NoiseRate > 31) return null;
                rec.Uses = r.ReadUInt16();
                rec.LengthSamples = r.ReadInt32();
                if (rec.LengthSamples < 0 || rec.LengthSamples > 32000 * 600) return null;
                uint n = ReadVarint(r);
                if (n > MaxEvents || (totalEvents += n) > MaxRecipes * MaxEvents) return null;
                var ev = new SpcEvent[n];
                long t = 0;
                for (int k = 0; k < n; k++)
                {
                    t += ReadVarint(r);
                    if (t > 32000 * 600) return null;
                    byte kind = r.ReadByte();
                    if (kind > (byte)SpcEv.Flags) return null;
                    uint z = ReadVarint(r);
                    int v = (int)(z >> 1) ^ -(int)(z & 1);
                    if (!ValueOk((SpcEv)kind, v)) return null;
                    ev[k] = new SpcEvent((int)t, (SpcEv)kind, v);
                }
                rec.Events = ev;
                list[i] = rec;
            }
            return r.BaseStream.Position == b.Length ? list : null;
        }
        catch (Exception) { return null; }
    }

    private static bool ValueOk(SpcEv kind, int v) => kind switch
    {
        SpcEv.Pitch => v is >= -12000 and <= 12000,
        SpcEv.VolL or SpcEv.VolR => v is >= -128 and <= 127,
        SpcEv.Flags => v is >= 0 and <= 7,
        _ => v is >= 0 and <= 255,
    };

    /// <summary>A hash of everything a recipe is (tests compare derived and restored recipes with it).</summary>
    public ulong Hash()
    {
        ulong h = 1469598103934665603UL;
        void Mix(long v) => h = (h ^ (ulong)v) * 1099511628211UL;
        Mix(Srcn); Mix(Adsr1); Mix(Adsr2); Mix(Gain); Mix(VolL); Mix(VolR); Mix(Pitch); Mix(NoiseRate); Mix((Non ? 1 : 0) | (Eon ? 2 : 0) | (Pmon ? 4 : 0) | (Held ? 8 : 0)); Mix(Uses); Mix(LengthSamples);
        foreach (var e in Events) { Mix(e.T); Mix((int)e.Kind); Mix(e.Value); }
        return h;
    }
}

/// <summary>
/// Every write the SPC700 program made to the S-DSP, newest last, in a ring of 262,144 (a busy driver makes about 1,000 to 2,500 a second, so that is minutes). Allocated once, with the game; the game's session fills it from the audio thread
/// without allocating (<see cref="OnWrite"/>); it is read under the emulator's lock, between frames.
/// </summary>
public sealed class SpcWriteLog
{
    public const int Capacity = 1 << 18;
    private readonly uint[] time = new uint[Capacity];
    private readonly ushort[] regval = new ushort[Capacity];
    private readonly byte[] baseRegs = new byte[128];
    private long count;

    /// <summary>The S-DSP's 128 registers as they were just before the oldest write the ring still holds: the state the log starts from (<see cref="Begin"/>), moved forward by every write that fell off the ring.</summary>
    public byte[] BaseRegs => baseRegs;

    /// <summary>Starts the log over on a chip whose registers are <paramref name="regs"/> now: nothing is known about the time before, but the registers' values are.</summary>
    public void Begin(byte[] regs) { Array.Copy(regs, baseRegs, 128); count = 0; }

    /// <summary>Starts the log over on this unit and taps its SPC700's DSP writes (the unit's registers as they are now are the log's starting point).</summary>
    public void Attach(NesEmulator.Snes.APU_SFC spc) { Begin(spc.Dsp.Regs); spc.DspWriteTap = OnWrite; }

    /// <summary>Writes seen in all (the ring keeps the last <see cref="Capacity"/>).</summary>
    public long Count => count;
    public long Dropped => Math.Max(0, count - Capacity);

    public void OnWrite(byte reg, byte value, long sample)
    {
        int i = (int)(count & (Capacity - 1));
        if (count >= Capacity) baseRegs[(regval[i] >> 8) & 0x7F] = (byte)regval[i];      // the oldest write leaves the ring: the starting state takes it in
        time[i] = (uint)sample;
        regval[i] = (ushort)(reg << 8 | value);
        count++;
    }

    public void Clear() { count = 0; }

    /// <summary>The write number <paramref name="index"/> (0 is the first ever, valid from <see cref="Dropped"/> to <see cref="Count"/> - 1).</summary>
    public void Get(long index, out uint sample, out byte reg, out byte value)
    {
        int i = (int)(index & (Capacity - 1));
        sample = time[i]; reg = (byte)(regval[i] >> 8); value = (byte)regval[i];
    }
}

/// <summary>What deriving recipes found, for the window's text and the tests.</summary>
public sealed record SpcDeriveStats(int KeyOns, int Notes, int Recipes, int SkippedSilent, long WritesInWindow, long LogDropped, double WindowSeconds)
{
    public override string ToString() => $"{KeyOns} key-ons in {WindowSeconds:0.#} s ({WritesInWindow:N0} chip writes), {Recipes} recipes" + (SkippedSilent > 0 ? $", {SkippedSilent} silent notes left out" : "") + (LogDropped > 0 ? $", {LogDropped:N0} older writes had fallen off the log" : "");
}

public static class SpcRecipes
{
    public const long MaxNoteSamples = 32000L * 8;
    /// <summary>How much of the end of the log Instrument Runaway turns into recipes when it starts, in seconds (a longer-playing game is listened to for its last stretch).</summary>
    public const double ListenWindowSeconds = 30;

    private sealed class Open
    {
        public int Voice;
        public long T0;
        public int Srcn, A1, A2, G, VL, VR, P, NoiseRate;
        public bool Non, Eon, Pmon, Non0, Eon0, Pmon0;          // the flags now, and as they were at the key-on
        public readonly List<SpcEvent> Events = new();
        public int LastCents;
        public bool Truncated, Held;
        public int Flags => (Non ? 1 : 0) | (Eon ? 2 : 0) | (Pmon ? 4 : 0);
    }

    /// <summary>
    /// Turns the S-DSP writes of the last <paramref name="windowSamples"/> samples (32 kHz) before <paramref name="now"/> into recipes. The registers' values at the start of the window are known: the log starts from the chip's registers
    /// (<see cref="SpcWriteLog.Begin"/>) and every write older than the window is applied to them first, so a key-on never has settings the driver set before the window missing. A note already sounding when the window starts has no key-on in it
    /// and is not learned.
    /// </summary>
    public static SpcRecipe[] Derive(SpcWriteLog log, long now, long windowSamples, byte[] dspNow, out SpcDeriveStats stats)
    {
        var all = Notes(log, now, windowSamples, dspNow, out stats, out int silent);
        // one recipe per distinct way of keying a voice on; the note with the most going on (then the longest) is the one the macro is taken from
        var groups = new Dictionary<long, (SpcRecipe Recipe, int Score)>();
        foreach (var note in all)
        {
            var rec = note.Recipe;
            // the key: the settings and the flags as they were at the key-on (the events may change them later)
            long key = ((((long)rec.Srcn * 257 + rec.Adsr1) * 257 + rec.Adsr2) * 257 + rec.Gain) * 16 + ((rec.Non ? 1 : 0) | (rec.Eon ? 2 : 0) | (rec.Pmon ? 4 : 0) | (rec.Held ? 8 : 0));
            int score = Math.Min(rec.Events.Length, 400) * 100000 + Math.Min(rec.LengthSamples, 99999);
            if (groups.TryGetValue(key, out var g))
            {
                g.Recipe.Uses++;
                if (score > g.Score) { rec.Uses = g.Recipe.Uses; groups[key] = (rec, score); }
            }
            else groups[key] = (rec, score);
        }
        var result = groups.Values.Select(g => g.Recipe).OrderByDescending(r => r.Uses).ThenByDescending(r => r.Events.Length).ThenBy(r => r.Srcn).Take(SpcRecipe.MaxRecipes).ToArray();
        stats = stats with { Recipes = result.Length };
        return result;
    }

    /// <summary>One note the driver played inside the window: the voice, when it was keyed on and when it ended (samples at 32 kHz, on the SPC's clock) and the recipe that is exactly this note (its own macro).</summary>
    public sealed record SpcNote(int Voice, long Start, long End, bool KeyedOff, SpcRecipe Recipe);

    /// <summary>Every note of the window, each with its own macro (what <see cref="Derive"/> groups into recipes); notes that never asked for any volume are left out and counted in <paramref name="silent"/>.</summary>
    public static List<SpcNote> Notes(SpcWriteLog log, long now, long windowSamples, byte[] dspNow, out SpcDeriveStats stats, out int silent)
    {
        // the registers at the start of the window (the log's starting state, moved forward by the writes older than the window), then the writes of the window in order
        var sh = new int[128];
        for (int r = 0; r < 128; r++) sh[r] = log.BaseRegs[r];
        var t = new List<long>(); var rg = new List<byte>(); var vl = new List<byte>();
        uint nowU = (uint)now;
        for (long i = log.Dropped; i < log.Count; i++)
        {
            log.Get(i, out uint s, out byte reg, out byte val);
            uint age = nowU - s;
            if (age > windowSamples) { sh[reg & 0x7F] = val; continue; }
            t.Add(now - age); rg.Add(reg); vl.Add(val);
        }

        var cur = new Open?[8];
        var notes = new List<(Open Note, long End, bool KeyOff)>();
        int keyOns = 0;
        long start = now - Math.Min(windowSamples, now);
        // a voice that sounds when the listening ends starts as a note held since the window began; a key-on or a key-off inside the window turns it into an ordinary note (or none), and what is left at the end is a voice the driver did not touch with a key-on
        for (int v = 0; v < 8; v++)
            if (windowSamples > 0 && dspNow[v * 16 + 8] != 0)
            {
                int b = v * 16;
                var h = new Open
                {
                    Voice = v, T0 = start, Held = true, VL = (sbyte)sh[b], VR = (sbyte)sh[b + 1], P = sh[b + 2] | (sh[b + 3] & 0x3F) << 8, Srcn = sh[b + 4], A1 = sh[b + 5], A2 = sh[b + 6], G = sh[b + 7],
                    Non = (sh[0x3D] >> v & 1) != 0, Eon = (sh[0x4D] >> v & 1) != 0, Pmon = v > 0 && (sh[0x2D] >> v & 1) != 0, NoiseRate = sh[0x6C] & 0x1F,
                };
                h.Non0 = h.Non; h.Eon0 = h.Eon; h.Pmon0 = h.Pmon;
                cur[v] = h;
            }

        void Close(int v, long at, bool keyOff) { if (cur[v] is { } n) { if (!n.Held || at >= now) notes.Add((n, at, keyOff)); cur[v] = null; } }
        void Add(Open n, long at, SpcEv kind, int value)
        {
            long dt = at - n.T0;
            if (dt > MaxNoteSamples || n.Events.Count >= SpcRecipe.MaxEvents) { n.Truncated = true; return; }
            // two writes in one sample (the two halves of a pitch, say) are one change
            if (n.Events.Count > 0 && n.Events[^1].T == (int)dt && n.Events[^1].Kind == kind) { n.Events[^1] = new SpcEvent((int)dt, kind, value); return; }
            n.Events.Add(new SpcEvent((int)dt, kind, value));
        }
        int Known(int r) => sh[r];

        for (int i = 0; i < rg.Count; i++)
        {
            byte reg = (byte)(rg[i] & 0x7F), val = vl[i];
            long at = t[i];
            int prev = sh[reg];
            sh[reg] = val;

            if ((reg & 15) < 8)
            {
                int v = reg >> 4;
                if (cur[v] is { } n)
                {
                    switch (reg & 15)
                    {
                        case 0: Add(n, at, SpcEv.VolL, (sbyte)val); break;
                        case 1: Add(n, at, SpcEv.VolR, (sbyte)val); break;
                        case 2: case 3:
                            {
                                int p = sh[v * 16 + 2] | (sh[v * 16 + 3] & 0x3F) << 8;
                                int cents = p <= 0 || n.P <= 0 ? (p <= 0 ? -12000 : 12000) : (int)Math.Clamp(Math.Round(1200 * Math.Log2((double)p / n.P)), -12000, 12000);
                                if (cents != n.LastCents) { n.LastCents = cents; Add(n, at, SpcEv.Pitch, cents); }
                            }
                            break;
                        case 4: Add(n, at, SpcEv.Srcn, val); break;
                        case 5: Add(n, at, SpcEv.Adsr1, val); break;
                        case 6: Add(n, at, SpcEv.Adsr2, val); break;
                        case 7: Add(n, at, SpcEv.Gain, val); break;
                    }
                }
            }
            else if (reg == 0x4C)
            {
                for (int v = 0; v < 8; v++)
                {
                    if ((val >> v & 1) == 0) continue;
                    keyOns++;
                    Close(v, at, false);
                    int b = v * 16;
                    var n = new Open
                    {
                        Voice = v, T0 = at, VL = (sbyte)sh[b], VR = (sbyte)sh[b + 1], P = sh[b + 2] | (sh[b + 3] & 0x3F) << 8, Srcn = sh[b + 4], A1 = sh[b + 5], A2 = sh[b + 6], G = sh[b + 7],
                        Non = (Known(0x3D) >> v & 1) != 0, Eon = (Known(0x4D) >> v & 1) != 0, Pmon = v > 0 && (Known(0x2D) >> v & 1) != 0, NoiseRate = Known(0x6C) & 0x1F,
                    };
                    n.Non0 = n.Non; n.Eon0 = n.Eon; n.Pmon0 = n.Pmon;
                    cur[v] = n;
                }
            }
            else if (reg == 0x5C)
            {
                int was = prev;
                for (int v = 0; v < 8; v++) if ((val >> v & 1) != 0 && (was >> v & 1) == 0) Close(v, at, true);
            }
            else if (reg == 0x3D || reg == 0x4D || reg == 0x2D)
            {
                for (int v = 0; v < 8; v++)
                {
                    if (cur[v] is not { } n) continue;
                    bool bit = (val >> v & 1) != 0;
                    bool changed = reg == 0x3D ? bit != n.Non : reg == 0x4D ? bit != n.Eon : (v > 0 && bit != n.Pmon);
                    if (!changed) continue;
                    if (reg == 0x3D) n.Non = bit; else if (reg == 0x4D) n.Eon = bit; else n.Pmon = bit;
                    Add(n, at, SpcEv.Flags, n.Flags);
                }
            }
        }
        for (int v = 0; v < 8; v++) Close(v, now, false);

        var result = new List<SpcNote>(notes.Count);
        silent = 0;
        foreach (var (n, end, keyOff) in notes)
        {
            var rec = new SpcRecipe
            {
                Srcn = n.Srcn, Adsr1 = n.A1, Adsr2 = n.A2, Gain = n.G, VolL = n.VL, VolR = n.VR, Pitch = Math.Clamp(n.P, 1, 0x3FFF), NoiseRate = n.NoiseRate, Held = n.Held,
                Non = n.Non0, Eon = n.Eon0, Pmon = n.Pmon0, Uses = 1, LengthSamples = (int)Math.Min(end - n.T0, int.MaxValue / 2), KeyedOff = keyOff, Truncated = n.Truncated, Events = n.Events.ToArray(),
            };
            if (rec.Silent) { silent++; continue; }
            result.Add(new SpcNote(n.Voice, n.T0, end, keyOff, rec));
        }
        stats = new SpcDeriveStats(keyOns, notes.Count, 0, silent, rg.Count, log.Dropped, Math.Min(windowSamples, now) / 32000.0);
        return result;
    }
}
