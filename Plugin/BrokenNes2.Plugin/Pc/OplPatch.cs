// The x86 PC console's FM patches (2026-11-10, composing gaps round): one 2-operator OPL2 voice, as the AdLib's registers hold it, and the banks the plugin offers.
//
// Byte order (11 bytes, the classic SBI order): mod 20, car 20, mod 40, car 40, mod 60, car 60, mod 80, car 80, mod E0, car E0, C0.
//   20  AM | VIB | EG (sustained) | KSR | MULT (4 bits)      40  KSL (2 bits) | TL (6 bits, 0.75 dB a step)
//   60  AR | DR                                             80  SL | RR
//   E0  waveform (0-3: sine, half sine, abs sine, quarter)  C0  FB (bits 1-3) | CON (bit 0: 1 = additive, both operators heard)
// The carrier's TL is the patch's own level; the note's velocity is added to it (and to the modulator's when the patch is additive).
//
// Banks, in the Instrument parameter's order (append-only like the chips: a saved project stores the number):
//   0-6     Super VRUN DOS: the seven patches of platform/dos/dos_snd.c (made by tools/oplconv.py), the game's own sound
//   7-8     Vaults DOS: the tone voice and the rhythm-mode snare of src/dos/dos_opl.c (the Game Gear driver's PSG bytes on the AdLib)
//   9-23    the YM2413's 15 built-in instruments (Ym2413.RomPatchBytes, already in BrokenNes for the Master System FM chip) as OPL2 patches: the same operator fields,
//           its rectified wave = the OPL2's half sine (waveform 1)
//   24-151  a General MIDI bank, 128 programs. NOT the classic AdLib GM bank (GENMIDI / the Windows 3.1 FM bank): no copy of it exists in BrokenNes or its tools,
//           and nothing was downloaded. Designed here: one hand-made template per GM family (piano, chromatic percussion, organ ... sound effects), eight programs a
//           family varied from it (modulator level, multiple, feedback, envelope). Program n = Instrument 24 + n.
using NesEmulator.Sega;

namespace BrokenNes2.Pc;

public sealed record OplPatch(string Name, string Note, string Description, byte[] Regs, bool Snare = false)
{
    public const int Size = 11;
    public byte this[int i] => Regs[i];
    public bool Additive => (Regs[10] & 1) != 0;
    public OplPatch With(byte[] regs) => this with { Regs = regs };
}

public static class OplBank
{
    public const int SvrunFirst = 0, VaultsFirst = 7, OpllFirst = 9, GmFirst = 24;

    public static OplPatch[] Patches => Holder.P;
    private static class Holder { internal static readonly OplPatch[] P = Build(); }   // (a nested class: built after every static table below)
    public static int Count => Patches.Length;
    public static int Clamp(int i) => Math.Clamp(i, 0, Patches.Length - 1);

    /// <summary>Super VRUN DOS's 10-byte rows (mod 20, car 20, mod 40, mod 60, car 60, mod 80, car 80, mod E0, car E0, C0) as 11-byte patches (the carrier's level 0: the game sets it per note).</summary>
    private static byte[] FromSvrun(params byte[] q) => [q[0], q[1], q[2], 0, q[3], q[4], q[5], q[6], q[7], q[8], q[9]];

    private static OplPatch[] Build()
    {
        var l = new List<OplPatch>
        {
            new("SV pulse 12.5%", "Super VRUN DOS", "x1:x1, feedback 6: bright, all harmonics (the game's 12.5% pulse)", FromSvrun(0x21, 0x21, 0x0E, 0xF0, 0xF0, 0x0F, 0x08, 0, 0, 0x0C)),
            new("SV pulse 25%", "Super VRUN DOS", "x1:x1, feedback 4 (the game's 25/75% pulse)", FromSvrun(0x21, 0x21, 0x14, 0xF0, 0xF0, 0x0F, 0x08, 0, 0, 0x08)),
            new("SV pulse 50%", "Super VRUN DOS", "x2:x1: the odd harmonics, square-like (the game's 50% pulse)", FromSvrun(0x22, 0x21, 0x1C, 0xF0, 0xF0, 0x0F, 0x08, 0, 0, 0x00)),
            new("SV triangle", "Super VRUN DOS", "the carrier alone: a sine (the NES triangle's fundamental)", FromSvrun(0x21, 0x21, 0x3F, 0xF0, 0xF0, 0x0F, 0x08, 0, 0, 0x00)),
            new("SV mix", "Super VRUN DOS", "the 'mix' samples: softer", FromSvrun(0x21, 0x21, 0x1A, 0xF0, 0xF0, 0x0F, 0x08, 0, 0, 0x06)),
            new("SV noise", "Super VRUN DOS", "x15:x14, feedback 7: noise-like (the game's noise voice)", FromSvrun(0x2F, 0x2E, 0x00, 0xF0, 0xF0, 0x0F, 0x08, 0, 0, 0x0E)),
            new("SV percussion", "Super VRUN DOS", "the noise patch, decaying: the game's DPCM voice", FromSvrun(0x0F, 0x0E, 0x00, 0xF8, 0xF6, 0x0F, 0xF8, 0, 0, 0x0E)),
            new("Vaults tone", "Vaults DOS", "carrier x1 + modulator x2 at depth 16h: the Game Gear PSG tone on the AdLib", [0x22, 0x21, 0x16, 0x00, 0xF0, 0xF0, 0x0F, 0x0F, 0, 0, 0x00]),
            new("Vaults noise (snare)", "Vaults DOS", "the rhythm mode's snare (the OPL2's noise), pitched by voice 7: the PSG noise on the AdLib", [0x20, 0x20, 0x00, 0x00, 0xF0, 0xF0, 0x0F, 0x0F, 0, 0, 0x00], Snare: true),
        };
        string[] opll = ["Violin", "Guitar", "Piano", "Flute", "Clarinet", "Oboe", "Trumpet", "Organ", "Horn", "Synthesizer", "Harpsichord", "Vibraphone", "Synth bass", "Acoustic bass", "Electric guitar"];
        for (int i = 1; i <= 15; i++) l.Add(new($"OPLL {opll[i - 1]}", "YM2413 ROM", $"the YM2413's built-in instrument {i} ({opll[i - 1]}) on the OPL2", FromOpll(Ym2413.RomPatchBytes(i), 0)));
        for (int p = 0; p < 128; p++) l.Add(Gm(p));
        return l.ToArray();
    }

    /// <summary>A YM2413 instrument (8 bytes at <paramref name="o"/>) as an OPL2 patch: bytes 0-1 are reg 20 as they are, 2 the modulator's KSL/TL, 3 the carrier's KSL + the two rectify bits + FB, 4-7 AR/DR and SL/RR.</summary>
    public static byte[] FromOpll(byte[] d, int o)
    {
        byte b3 = d[o + 3];
        return [d[o], d[o + 1], d[o + 2], (byte)(b3 & 0xC0), d[o + 4], d[o + 5], d[o + 6], d[o + 7], (byte)((b3 >> 3) & 1), (byte)((b3 >> 4) & 1), (byte)((b3 & 7) << 1)];
    }

    // ---- the designed General MIDI bank ----

    public static readonly string[] GmNames =
    [
        "Acoustic Grand Piano", "Bright Acoustic Piano", "Electric Grand Piano", "Honky-tonk Piano", "Electric Piano 1", "Electric Piano 2", "Harpsichord", "Clavinet",
        "Celesta", "Glockenspiel", "Music Box", "Vibraphone", "Marimba", "Xylophone", "Tubular Bells", "Dulcimer",
        "Drawbar Organ", "Percussive Organ", "Rock Organ", "Church Organ", "Reed Organ", "Accordion", "Harmonica", "Tango Accordion",
        "Acoustic Guitar (nylon)", "Acoustic Guitar (steel)", "Electric Guitar (jazz)", "Electric Guitar (clean)", "Electric Guitar (muted)", "Overdriven Guitar", "Distortion Guitar", "Guitar Harmonics",
        "Acoustic Bass", "Electric Bass (finger)", "Electric Bass (pick)", "Fretless Bass", "Slap Bass 1", "Slap Bass 2", "Synth Bass 1", "Synth Bass 2",
        "Violin", "Viola", "Cello", "Contrabass", "Tremolo Strings", "Pizzicato Strings", "Orchestral Harp", "Timpani",
        "String Ensemble 1", "String Ensemble 2", "Synth Strings 1", "Synth Strings 2", "Choir Aahs", "Voice Oohs", "Synth Voice", "Orchestra Hit",
        "Trumpet", "Trombone", "Tuba", "Muted Trumpet", "French Horn", "Brass Section", "Synth Brass 1", "Synth Brass 2",
        "Soprano Sax", "Alto Sax", "Tenor Sax", "Baritone Sax", "Oboe", "English Horn", "Bassoon", "Clarinet",
        "Piccolo", "Flute", "Recorder", "Pan Flute", "Blown Bottle", "Shakuhachi", "Whistle", "Ocarina",
        "Lead 1 (square)", "Lead 2 (sawtooth)", "Lead 3 (calliope)", "Lead 4 (chiff)", "Lead 5 (charang)", "Lead 6 (voice)", "Lead 7 (fifths)", "Lead 8 (bass + lead)",
        "Pad 1 (new age)", "Pad 2 (warm)", "Pad 3 (polysynth)", "Pad 4 (choir)", "Pad 5 (bowed)", "Pad 6 (metallic)", "Pad 7 (halo)", "Pad 8 (sweep)",
        "FX 1 (rain)", "FX 2 (soundtrack)", "FX 3 (crystal)", "FX 4 (atmosphere)", "FX 5 (brightness)", "FX 6 (goblins)", "FX 7 (echoes)", "FX 8 (sci-fi)",
        "Sitar", "Banjo", "Shamisen", "Koto", "Kalimba", "Bagpipe", "Fiddle", "Shanai",
        "Tinkle Bell", "Agogo", "Steel Drums", "Woodblock", "Taiko Drum", "Melodic Tom", "Synth Drum", "Reverse Cymbal",
        "Guitar Fret Noise", "Breath Noise", "Seashore", "Bird Tweet", "Telephone Ring", "Helicopter", "Applause", "Gunshot",
    ];

    // one template per family (mod 20, car 20, mod 40, car 40, mod 60, car 60, mod 80, car 80, mod E0, car E0, C0)
    private static readonly byte[][] Families =
    [
        [0x01, 0x01, 0x4F, 0x00, 0xF1, 0xF2, 0x53, 0x74, 0, 0, 0x06],   // piano: struck, decaying
        [0x07, 0x12, 0x4F, 0x00, 0xF2, 0xF2, 0x60, 0x72, 0, 0, 0x08],   // chromatic percussion: inharmonic bell
        [0x32, 0x21, 0x16, 0x00, 0xF0, 0xF0, 0x07, 0x07, 0, 0, 0x01],   // organ: additive, sustained
        [0x01, 0x11, 0x4F, 0x00, 0xF1, 0xD2, 0x53, 0x74, 0, 0, 0x06],   // guitar: plucked
        [0x01, 0x01, 0x11, 0x00, 0xF2, 0xF5, 0x1F, 0x88, 0, 0, 0x0A],   // bass
        [0x21, 0x21, 0x1E, 0x00, 0x71, 0x61, 0x15, 0x16, 0, 0, 0x0A],   // strings: slow attack, sustained
        [0x61, 0x61, 0x22, 0x00, 0x51, 0x51, 0x14, 0x15, 0, 0, 0x08],   // ensemble: vibrato, softer
        [0x21, 0x21, 0x17, 0x00, 0x75, 0x74, 0x18, 0x08, 0, 0, 0x0C],   // brass
        [0x31, 0x22, 0x1C, 0x00, 0x72, 0x71, 0x16, 0x07, 1, 0, 0x0A],   // reed: half-sine modulator
        [0xE1, 0xE1, 0x24, 0x00, 0x62, 0x52, 0x05, 0x06, 0, 0, 0x02],   // pipe: breathy sine
        [0x22, 0x21, 0x0F, 0x00, 0xF0, 0xF0, 0x07, 0x07, 0, 0, 0x0E],   // synth lead: x2:x1, high feedback
        [0x21, 0x21, 0x23, 0x00, 0x52, 0x42, 0x25, 0x26, 0, 0, 0x08],   // synth pad
        [0x34, 0x21, 0x1A, 0x00, 0x43, 0x53, 0x34, 0x35, 2, 0, 0x0C],   // synth effects
        [0x05, 0x01, 0x4A, 0x00, 0xF3, 0xF4, 0x52, 0x75, 0, 0, 0x08],   // ethnic: plucked, bright
        [0x06, 0x01, 0x00, 0x00, 0xF8, 0xF6, 0x87, 0x87, 0, 0, 0x0C],   // percussive: fast decay
        [0x0F, 0x0E, 0x00, 0x00, 0xF0, 0xF4, 0x0F, 0x48, 0, 0, 0x0E],   // sound effects: noise-like FM
    ];

    private static OplPatch Gm(int program)
    {
        int fam = program / 8, v = program % 8;
        var r = (byte[])Families[fam].Clone();
        // modulator level: brighter or softer by up to 4.5 dB
        int tl = (r[2] & 0x3F) + (v % 4 - 1) * 2;
        r[2] = (byte)((r[2] & 0xC0) | Math.Clamp(tl, 0, 63));
        // the second half of a family: the modulator one multiple up (another colour), the attack a step faster
        if (v >= 4) r[0] = (byte)((r[0] & 0xF0) | Math.Min(15, (r[0] & 0x0F) + 1));
        if (v >= 4 && (r[4] >> 4) < 15) r[4] = (byte)(r[4] + 0x10);
        // odd programs: one step more feedback
        if ((v & 1) == 1) r[10] = (byte)((r[10] & 1) | Math.Min(14, (r[10] & 0x0E) + 2));
        return new OplPatch($"GM {program + 1} {GmNames[program]}", "GM (designed)", $"General MIDI program {program + 1}, {GmNames[program]}: designed from the family's template (not the classic AdLib bank)", r);
    }

    // ---- the editor's fields ----

    /// <summary>The patch fields the editor offers: (name, register byte, shift, bit count). Operator fields come twice (mod, car).</summary>
    public static readonly (string Name, int Byte, int Shift, int Bits)[] Fields =
    [
        ("Mod AR", 4, 4, 4), ("Mod DR", 4, 0, 4), ("Mod SL", 6, 4, 4), ("Mod RR", 6, 0, 4), ("Mod MULT", 0, 0, 4), ("Mod KSL", 2, 6, 2), ("Mod TL", 2, 0, 6), ("Mod wave", 8, 0, 2),
        ("Mod sustain", 0, 5, 1), ("Mod KSR", 0, 4, 1), ("Mod vibrato", 0, 6, 1), ("Mod tremolo", 0, 7, 1),
        ("Car AR", 5, 4, 4), ("Car DR", 5, 0, 4), ("Car SL", 7, 4, 4), ("Car RR", 7, 0, 4), ("Car MULT", 1, 0, 4), ("Car KSL", 3, 6, 2), ("Car TL", 3, 0, 6), ("Car wave", 9, 0, 2),
        ("Car sustain", 1, 5, 1), ("Car KSR", 1, 4, 1), ("Car vibrato", 1, 6, 1), ("Car tremolo", 1, 7, 1),
        ("Feedback", 10, 1, 3), ("Additive", 10, 0, 1),
    ];

    public static int GetField(byte[] regs, int f) { var d = Fields[f]; return (regs[d.Byte] >> d.Shift) & ((1 << d.Bits) - 1); }

    public static void SetField(byte[] regs, int f, int value)
    {
        var d = Fields[f];
        int mask = ((1 << d.Bits) - 1) << d.Shift;
        regs[d.Byte] = (byte)((regs[d.Byte] & ~mask) | ((Math.Clamp(value, 0, (1 << d.Bits) - 1) << d.Shift) & mask));
    }
}

/// <summary>The waveforms a DAC chip (Sound Blaster direct DAC, Covox, Gravis Ultrasound) plays: one cycle, 8-bit signed, 256 points.</summary>
public static class PcWaves
{
    public static readonly string[] Names = ["Square 50%", "Square 25%", "Square 12.5%", "Triangle", "Sawtooth", "Sine", "Noise (8-bit)"];
    public static int Count => Names.Length;
    public static int Clamp(int i) => Math.Clamp(i, 0, Names.Length - 1);
    public const int Points = 256;

    /// <summary>The sample at phase <paramref name="i"/> (0-255) of wave <paramref name="w"/>, -127..127 (noise: <paramref name="rng"/> steps once a cycle point).</summary>
    public static int At(int w, int i, ref uint rng)
    {
        switch (w)
        {
            case 0: return i < 128 ? 96 : -96;
            case 1: return i < 64 ? 96 : -96;
            case 2: return i < 32 ? 96 : -96;
            case 3: return i < 128 ? -127 + i * 2 : 383 - i * 2;
            case 4: return i - 128;
            case 5: return (int)Math.Round(Math.Sin(i * Math.PI / 128) * 120);
            default: rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5; return (int)(rng & 0xFF) - 128;
        }
    }
}

/// <summary>The Tandy chip's voices one instance can be: a tone, or the noise (periodic or white, pitched by the note through tone 3, as the SN76496's mode 3 does).</summary>
public static class TandyModes
{
    public static readonly string[] Names = ["Tone (square)", "Periodic noise", "White noise"];
    public static int Clamp(int i) => Math.Clamp(i, 0, Names.Length - 1);
}
