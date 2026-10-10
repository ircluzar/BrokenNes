using System;
using NesEmulator.Sega;

namespace NesEmulator.Mix
{
    /// <summary>
    /// One YM2413 instrument as the Master System FM chip (<see cref="APU_SMSFM"/>) can be asked to play it: one of the chip's 15 built-in ROM instruments, a designed instrument loaded into the chip's single user
    /// instrument (eight register bytes), or the one entry that is not FM at all (the pulses on the PSG's square tones).
    /// </summary>
    public sealed class OpllPatch
    {
        public string Name { get; }
        public string Description { get; }
        /// <summary>1 to 15: the chip's built-in instrument of that number (Violin ... Electric Guitar). 0: a designed instrument (<see cref="User"/>) or the PSG entry.</summary>
        public int Rom { get; }
        /// <summary>The eight bytes of the user instrument (registers $00 to $07) a designed patch loads; null for a ROM instrument and for the PSG entry.</summary>
        public byte[]? User { get; }
        /// <summary>True for the entry that is not FM: the NES pulses play the PSG's square tones (50% duty, 109 Hz and up).</summary>
        public bool Psg { get; }
        /// <summary>The frequency multiple fields of the modulator and the carrier (0 to 15; the chip's table turns them into x0.5, x1, x2 ... x15).</summary>
        public int ModMl { get; }
        public int CarMl { get; }
        /// <summary>The instrument's fundamental as a multiple of the channel's base frequency (the one the F-number and block set): the greatest common divisor of the two multiples, so 1 for every ratio that is a whole number of the base
        /// (1:1, 3:1, 1:2, 7:1) and 0.5 for a carrier at half the base (the ROM synthesizer bass). A note is set on the chip at the note's frequency divided by this, so the sound's fundamental is the note.</summary>
        public double Fundamental { get; }
        /// <summary>True when the carrier holds its level while the key is down (the instrument's EG type); a percussive one fades by itself (a guitar, a piano).</summary>
        public bool Sustained { get; }
        /// <summary>True when the carrier has vibrato (the chip's PM bit): its pitch wobbles by up to 14 cents at about 6.4 Hz.</summary>
        public bool CarrierPm { get; }
        /// <summary>The carrier's key-scale level field (0 to 3): how much quieter the chip plays this instrument as the pitch rises (none, 1.5, 3 or 6 dB an octave, at the top of the F-number range).</summary>
        public int CarrierKl { get; }
        /// <summary>The key-scale attenuation (in the chip's 0.375 dB envelope steps) at the note the calibration was made at (C4): the figure the level maths takes the pitch's own key scaling against.</summary>
        public int ReferenceKsl { get; }
        /// <summary>The patch's measured RMS (as a fraction of full scale, at the chip's own volume 0, before the bridge's gain) of a held C4, 100 to 400 ms after the key went down: what the bridge scales to put the note at the NES channel's loudness.
        /// Measured, not derived: it is printed by <c>--sega-bridge-test --only smsfm --calibrate</c> and the verifier measures it again.</summary>
        public double CalibratedRms { get; internal set; }

        internal OpllPatch(string name, string description, int rom, byte[]? user, bool psg = false)
        {
            Name = name; Description = description; Rom = rom; User = user; Psg = psg;
            if (psg) { ModMl = CarMl = 1; Fundamental = 1; Sustained = true; return; }
            byte[] d = user ?? Ym2413.RomPatches;
            int o = user != null ? 0 : rom * 8;
            ModMl = d[o] & 15; CarMl = d[o + 1] & 15;
            Sustained = ((d[o + 1] >> 5) & 1) != 0;
            CarrierKl = (d[o + 3] >> 6) & 3;
            CarrierPm = ((d[o + 1] >> 6) & 1) != 0;
            Fundamental = Gcd(OpllPatchBank.MultipleTimesTwo(ModMl), OpllPatchBank.MultipleTimesTwo(CarMl)) / 2.0;
            ReferenceKsl = OpllPatchBank.TryPitch(OpllPatchBank.CalibrationHz / Fundamental, OpllPatchBank.ClockHz, out int blk, out int fnum, out _) ? OpllPatchBank.KeyScaleSteps(CarrierKl, blk, fnum) : 0;
        }

        private static int Gcd(int a, int b) { while (b != 0) { (a, b) = (b, a % b); } return a; }

        /// <summary>How far, in decibels, the patch's loudest volume step is above the NES channel's <paramref name="targetRms"/> once the bridge's <paramref name="gain"/> is applied, at the calibration note C4 (negative: even the loudest step is too quiet).
        /// Computed once per patch and NES level; the pitch's own key scaling is added by <see cref="AttenuationAt"/>.</summary>
        public double ExcessDb(double targetRms, double gain) => Psg || !(targetRms > 0) || !(CalibratedRms > 0) ? double.NegativeInfinity : 20 * Math.Log10(CalibratedRms * gain / targetRms);

        /// <summary>The volume register (0 loudest to 15, 3 dB a step) that puts the note nearest, in decibels, to the target whose <see cref="ExcessDb"/> at C4 is <paramref name="excessDb"/>, allowing for the key scaling the pitch (block, F-number) brings: a higher note is
        /// played quieter by the chip's carrier key-scale level (up to 6 dB an octave at the top), which the volume register takes back.</summary>
        public int AttenuationAt(double excessDb, int block, int fnum)
        {
            if (Psg || double.IsNegativeInfinity(excessDb)) return 15;
            double db = excessDb - EffectiveKslDb(block, fnum);
            return (int)Math.Clamp(Math.Round(db / OpllPatchBank.StepDb), 0, 15);
        }

        /// <summary>How much quieter (positive, in decibels) the chip plays this instrument at this pitch than at C4, from the carrier's key-scale level.</summary>
        public double EffectiveKslDb(int block, int fnum) => (OpllPatchBank.KeyScaleSteps(CarrierKl, block, fnum) - ReferenceKsl) * OpllPatchBank.EnvelopeStepDb;

        /// <summary>Whether the loudest step reaches the target at this pitch (within half a volume step): false means the note is played quieter than the NES channel would be.</summary>
        public bool Reaches(double excessDb, int block, int fnum) => Psg || excessDb - EffectiveKslDb(block, fnum) >= -OpllPatchBank.StepDb / 2;
    }

    /// <summary>
    /// The built-in YM2413 patch bank of <see cref="APU_SMSFM"/> (a NES game, or a NES register stream, on the Master System's FM chip): the chip's 15 instruments from its own ROM, six designed ones, and the PSG square.
    /// <para>The chip has one user instrument and nine channels. A ROM instrument plays the pulses and leaves the user instrument to the triangle, which is always the designed sine; a designed instrument is loaded into the user
    /// instrument and so plays the triangle too (the chip cannot hold two). The PSG entry is no FM: the pulses are the PSG's square tones, the triangle is still the sine.</para>
    /// <para>The designed instruments were written for this project from FM theory and the YM2413's register map (the layout in <c>Ym2413</c>), not taken from any game, driver or emulator preset; the ROM instruments are the chip's own
    /// (see <c>sega/THIRD_PARTY_NOTICES.md</c>). Nobody has listened to any of them: what is measured is each patch's pitch, level and harmonic content.</para>
    /// </summary>
    public static class OpllPatchBank
    {
        /// <summary>The chip's volume register steps in decibels.</summary>
        public const double StepDb = 3.0;
        /// <summary>The chip's envelope generator step in decibels (the unit of its key-scale and total-level maths).</summary>
        public const double EnvelopeStepDb = 0.375;
        /// <summary>The chip's clock in the bridge (twice the NES CPU clock) and the note the patches are calibrated at.</summary>
        public const double ClockHz = 2 * NesVoiceWriter.NesCpuHz;
        public const double CalibrationHz = 261.63;

        // The chip's key-scale level table: the attenuation (in envelope steps) at the top block by the top four bits of the F-number; the chip takes 8 steps off per octave down. Taken from the chip itself (Ym2413's table); a verifier compares the maths here with the chip.
        private static readonly int[] KslRom = { 0, 24, 32, 37, 40, 43, 45, 47, 48, 50, 51, 52, 53, 54, 55, 56 };

        /// <summary>The key-scale attenuation in envelope steps for a carrier with key-scale field <paramref name="kl"/> at a block and F-number: nothing for 0, half of the level for 1, the level for 2, twice it for 3.</summary>
        public static int KeyScaleSteps(int kl, int block, int fnum)
        {
            int level = Math.Max(0, KslRom[(fnum >> 5) & 15] - 8 * (7 - block));
            return kl switch { 0 => 0, 1 => level >> 1, 2 => level, _ => level * 2 };
        }

        /// <summary>The frequency multiple fields (0 to 15) as multiples of the base frequency, times two (the chip's table: 0.5, 1, 2 ... 10, 10, 12, 12, 15, 15).</summary>
        public static int MultipleTimesTwo(int ml) => ml switch { 0 => 1, 11 => 20, 13 => 24, 15 => 30, _ => ml * 2 };

        private static readonly string[] RomNames =
        {
            "Violin", "Guitar", "Piano", "Flute", "Clarinet", "Oboe", "Trumpet", "Organ", "Horn", "Synthesizer", "Harpsichord", "Vibraphone", "Synth bass", "Acoustic bass", "Electric guitar",
        };

        private static OpllPatch Rom(int n, string description) => new(RomNames[n - 1], description, n, null);

        private static OpllPatch Designed(string name, string description, params byte[] user) => new(name, description, 0, user);

        /// <summary>The bank: index 0 to 14 are the chip's ROM instruments 1 to 15, then the designed ones, and the last entry is the PSG square (the only one that is not FM).</summary>
        public static readonly OpllPatch[] Patches =
        {
            Rom(1, "The chip's violin: a 1:1 pair with vibrato, sustained"),
            Rom(2, "The chip's guitar: a 3:1 modulator over a 1:1 carrier, a plucked decay"),
            Rom(3, "The chip's piano: a 3:1 pair, a struck decay"),
            Rom(4, "The chip's flute: a 1:1 pair, sustained"),
            Rom(5, "The chip's clarinet: a 2:1 modulator, odd harmonics, sustained"),
            Rom(6, "The chip's oboe: a 1:2 pair, sustained"),
            Rom(7, "The chip's trumpet: a 1:1 pair with vibrato, sustained"),
            Rom(8, "The chip's organ: a 3:1 pair, sustained"),
            Rom(9, "The chip's horn: a 1:1 pair, sustained"),
            Rom(10, "The chip's synthesizer: a 1:1 pair, sustained"),
            Rom(11, "The chip's harpsichord: a 3:1 pair, a plucked decay"),
            Rom(12, "The chip's vibraphone: a 7:1 modulator, a ringing decay"),
            Rom(13, "The chip's synthesizer bass: a carrier at half the base frequency (the bridge sets the base an octave up so the note is the note)"),
            Rom(14, "The chip's acoustic bass: a 1:1 pair, a plucked decay"),
            Rom(15, "The chip's electric guitar: a 1:1 pair, a plucked decay"),
            // modulator and carrier registers: AM|PM|EG|KR|ML, then KL|TL (modulator) and KL|WS|WS|FB, then AR|DR, SL|RR for each
            Designed("Sine", "One carrier and a modulator that never opens (attack rate 0): a pure sine (the triangle's voice while a ROM instrument plays the pulses)",
                0x21, 0x21, 0x3F, 0x00, 0x00, 0xF0, 0x08, 0x08),
            Designed("Rectified", "A sine with its lower half cut off (the chip's half-sine wave): even harmonics, a reedy tone",
                0x21, 0x21, 0x3F, 0x10, 0x00, 0xF0, 0x08, 0x08),
            Designed("Fed-back lead", "A modulator feeding back on itself over a 1:1 carrier: a bright, buzzy lead with strong upper harmonics",
                0x21, 0x21, 0x10, 0x07, 0xF0, 0xF0, 0x08, 0x08),
            Designed("Soft organ", "A weak 2:1 modulator on a 1:1 carrier: a sine with a touch of its octave and third harmonic",
                0x22, 0x21, 0x22, 0x00, 0xF0, 0xF0, 0x08, 0x08),
            Designed("Bell", "A 7:1 modulator that fades over a 1:1 carrier that rings (a percussive pair): glassy",
                0x07, 0x01, 0x14, 0x00, 0xF4, 0xF3, 0x44, 0x34),
            Designed("Pluck", "A fast-fading 1:1 modulator on a 1:1 carrier that decays: bright, then mellow",
                0x01, 0x01, 0x1A, 0x00, 0xF7, 0xF4, 0x25, 0x25),
            new OpllPatch("PSG square", "No FM: the pulses are the PSG's square tones (50% duty, 109 Hz and up); the triangle stays a sine and the noise stays the PSG's", 0, null, psg: true),
        };

        /// <summary>The measured RMS of each patch (see <see cref="OpllPatch.CalibratedRms"/>), in the order of <see cref="Patches"/>.</summary>
        private static readonly double[] Calibration =
        {
            0.00958, 0.02281, 0.02469, 0.02651, 0.02490, 0.02829, 0.02821, 0.02502, 0.01939, 0.01689, 0.01339, 0.02043, 0.00748, 0.02972, 0.02485, 0.04424, 0.02416, 0.04630, 0.05345, 0.03226, 0.02112, 0,
        };

        static OpllPatchBank()
        {
            for (int i = 0; i < Patches.Length && i < Calibration.Length; i++) Patches[i].CalibratedRms = Calibration[i];
        }

        /// <summary>The calibration table as it is compiled in (for the verifier).</summary>
        public static double[] CalibrationTable => (double[])Calibration.Clone();

        public static string[] Names { get; } = Array.ConvertAll(Patches, p => p.Name);
        public static int Count => Patches.Length;
        public const int DefaultIndex = 0;
        /// <summary>The designed sine (the triangle's voice and the plugin's measuring patch).</summary>
        public static readonly int SineIndex = Array.FindIndex(Patches, p => p.Name == "Sine");
        /// <summary>The PSG square entry.</summary>
        public static readonly int PsgIndex = Array.FindIndex(Patches, p => p.Psg);

        /// <summary>A patch index made safe: anything out of range is the last patch (the PSG square), never an exception.</summary>
        public static int Clamp(int index) => Math.Clamp(index, 0, Patches.Length - 1);

        // ---- pitch: the chip's frequency registers (block 0 to 7, F-number 1 to 511) ----

        /// <summary>The frequency of the carrier of a x1 instrument that block and F-number make, for a chip clock of <paramref name="clockHz"/> (the Z80 clock): F-number x 2^block x (clock / 72) / 2^19.</summary>
        public static double Hz(int block, int fnum, double clockHz) => fnum * (double)(1 << block) * (clockHz / Ym2413.ClocksPerSample) / 524288.0;

        /// <summary>
        /// The block and F-number whose frequency is nearest, in cents, to <paramref name="hz"/> (the base frequency: a note's frequency over the instrument's <see cref="OpllPatch.Fundamental"/>), or false when the chip cannot reach it (the top is
        /// about 6.2 kHz). Every block is tried and the nearest wins. The error is bounded by half a step of the 9-bit F-number: 3.4 cents at the bottom of a block (F-number 256), 1.7 at the top, so under 3.4 cents from about 49 Hz up and
        /// up to 6 cents at the bottom of the NES triangle's range (27 Hz), where block 0 has only 142 steps.
        /// </summary>
        public static bool TryPitch(double hz, double clockHz, out int block, out int fnum, out double cents)
        {
            block = 0; fnum = 0; cents = 0;
            if (!(hz > 0)) return false;
            double unit = (clockHz / Ym2413.ClocksPerSample) / 524288.0;
            double steps = hz / unit;                       // wanted F-number x 2^block
            double best = double.MaxValue;
            for (int b = 0; b < 8; b++)
            {
                int f = (int)Math.Round(steps / (1 << b));
                if (f < 1 || f > 511) continue;
                double c = 1200 * Math.Log2(f * (double)(1 << b) / steps);
                if (Math.Abs(c) < best - 1e-9) { best = Math.Abs(c); block = b; fnum = f; cents = c; }
            }
            return best < 50;
        }
    }
}
