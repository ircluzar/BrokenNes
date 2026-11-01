using System;
using NesEmulator.Sega;

namespace NesEmulator.Mix
{
    /// <summary>One operator of an FM patch, as the YM2612's registers hold it (every field is a register field; none is derived).</summary>
    /// <param name="Mul">Frequency multiple (register MUL, 0 means 0.5, 1 to 15 mean x1 to x15). The patches keep every carrier at an integer multiple of the note, so the played pitch is the note's.</param>
    /// <param name="Tl">Total level (0 loudest, each step 0.75 dB, 127 silent). On a modulator it sets how hard it bends the next operator (the modulation index); on a carrier, its loudness.</param>
    /// <param name="Dt">Detune (register DT: 0 none, 1 to 3 sharp, 5 to 7 flat).</param>
    /// <param name="Ar">Attack rate (31 is instant).</param>
    /// <param name="D1r">First decay rate (0 holds the level). A carrier of these patches holds (D1r 0, Sl 0); a modulator may fade, so the tone starts bright and settles.</param>
    /// <param name="Sl">Sustain level the first decay runs to (in steps of 3 dB; 0 is none).</param>
    /// <param name="D2r">Second decay rate.</param>
    /// <param name="Rr">Release rate (0 to 15, 15 the fastest).</param>
    /// <param name="Rs">Rate (key) scaling.</param>
    public readonly record struct FmOperator(int Mul, int Tl, int Dt = 0, int Ar = 31, int D1r = 0, int Sl = 0, int D2r = 0, int Rr = 10, int Rs = 0);

    /// <summary>
    /// A YM2612 instrument as a register set: an algorithm, the feedback of operator 1, and four operators in algorithm order (operator 1 first). The chip's register order is operator 1, 3, 2, 4;
    /// <see cref="FmPatchBank.SlotOffset"/> does the mapping. <see cref="Psg"/> marks the one entry of the bank that is not FM: the NES pulses play the PSG's square tones.
    /// </summary>
    public sealed class FmPatch
    {
        public string Name { get; }
        public string Description { get; }
        public int Algorithm { get; }
        public int Feedback { get; }
        public FmOperator[] Operators { get; }
        /// <summary>True for the entry that is not an FM patch: the pulses are the PSG's own square tones (50% duty), no FM voice is used for them.</summary>
        public bool Psg { get; }
        /// <summary>Bit n is set when operator n+1 (algorithm order) reaches the output: the algorithm's carriers.</summary>
        public int CarrierMask { get; }
        /// <summary>Sum of the squares of the carriers' linear amplitudes at their own total levels (a carrier at TL 0 is 1): the patch's loudness in units of one full-scale carrier's power x 2.</summary>
        public double PowerSum { get; }
        /// <summary>A total level at or above this is an operator that is switched off (silent), not a quiet carrier.</summary>
        public const int OffTl = 100;
        /// <summary>The patch's measured RMS at its own total levels once its modulators have settled (mean over seconds 1.5 to 7.5 of a held C4 through the chip), or 0 when none has been measured and the formula of <see cref="PowerSum"/> is used. A modulator at a ratio of 1 or 2 to its carrier changes the carrier's power with its index (a sideband falls onto a harmonic or DC), so this is measured, not derived; <c>--md-patches --calibrate</c> prints the table and the verifier checks it against the chip.</summary>
        public double CalibratedRms { get; internal set; }
        /// <summary>The largest total level among the carriers that sound: attenuation can be added to the carriers up to 126 minus this.</summary>
        public int MaxCarrierTl { get; }

        internal FmPatch(string name, string description, int algorithm, int feedback, FmOperator[] ops, bool psg = false)
        {
            Name = name; Description = description; Algorithm = algorithm; Feedback = feedback; Operators = ops; Psg = psg;
            CarrierMask = FmPatchBank.CarriersOf(algorithm);
            double sum = 0; int max = 0;
            for (int i = 0; i < 4; i++)
                if ((CarrierMask >> i & 1) != 0)
                {
                    double a = FmPatchBank.Amplitude(ops[i].Tl);
                    sum += a * a;
                    if (ops[i].Tl < OffTl) max = Math.Max(max, ops[i].Tl);   // an operator parked at (nearly) silent total level is not part of the sound
                }
            PowerSum = sum; MaxCarrierTl = max;
        }

        /// <summary>The RMS of the patch's output (as a fraction of full scale) with every carrier at its own total level plus <paramref name="extraTl"/> steps of attenuation (the carriers' sum scales with them, so the output does too).</summary>
        public double Rms(int extraTl = 0) => Psg ? 0 : CalibratedRms > 0 ? CalibratedRms * FmPatchBank.Amplitude(extraTl) : FmPatchBank.CarrierRms * Math.Sqrt(PowerSumAt(extraTl));

        private double PowerSumAt(int extraTl)
        {
            double sum = 0;
            for (int i = 0; i < 4; i++)
                if ((CarrierMask >> i & 1) != 0)
                {
                    double a = FmPatchBank.Amplitude(Math.Min(127, Operators[i].Tl + extraTl));
                    sum += a * a;
                }
            return sum;
        }

        /// <summary>The extra attenuation (carrier total-level steps) that puts the patch's output nearest, in decibels, to <paramref name="targetRms"/>; the limits are 0 (full) and what keeps every carrier above silence.</summary>
        public int AttenuationFor(double targetRms, bool ladder = false)
        {
            if (ladder) targetRms = FmPatchBank.LadderIdeal(targetRms);      // the discrete chip's ladder will add to what the channel makes: ask the channel for less
            if (Psg || !(targetRms > 0)) return 127;
            double full = Rms(0);
            if (targetRms >= full) return 0;
            int best = 0; double bd = double.MaxValue;
            int limit = Math.Max(0, 126 - MaxCarrierTl);
            for (int k = 0; k <= limit; k++)
            {
                double d = Math.Abs(Math.Log(Rms(k) / targetRms));
                if (d < bd) { bd = d; best = k; }
            }
            return best;
        }
    }

    /// <summary>
    /// The built-in YM2612 patch bank of <see cref="APU_MD"/> (a NES game on Genesis sound chips): sixteen timbres a NES square can become, plus the sine voice the triangle uses.
    /// <para><b>Written for this project</b> from FM theory and the YM2612's register map (the operators' roles in each of the eight algorithms, modulation index from the total level
    /// of a modulator, harmonic content from the frequency multiples), not copied from any game's or driver's patch bank and not taken from any emulator's presets. They are first drafts of timbres
    /// built to be recognisable and to be played by a NES note: every patch keeps its carriers at integer multiples of the note (so the pitch is the note's, whatever the timbre), holds its
    /// carriers' level instead of letting them decay (so the NES's own envelope and volume writes shape the note, not the patch), and lets modulators fade where a timbre starts bright and settles
    /// (a pluck, a bell). The test in <c>--sega-bridge-test</c> measures what each one is: the pitch of the fundamental, the harmonic series, the level against the NES chip. Nobody has listened to them yet.</para>
    /// <para>Level: a patch's loudness at its own total levels is known exactly (<see cref="FmPatch.Rms"/>: a carrier is a phase-modulated sine whose RMS does not depend on its modulation), so the
    /// bridge adds the attenuation that puts it at the loudness the NES channel would have, in 0.75 dB steps (<see cref="FmPatch.AttenuationFor"/>).</para>
    /// </summary>
    public static class FmPatchBank
    {
        /// <summary>RMS of one carrier at total level 0 with no envelope attenuation, as a fraction of full scale, through the non-ladder chip (measured by the verifier: a full-scale 9-bit channel is 255 in 256, scaled by 128/6).</summary>
        public const double CarrierRms = 0.1187;

        // ---- the discrete YM2612's ladder effect (Model 1): its DAC adds 4 to a positive channel value and subtracts 3 from a negative one, a square wave 3.5 high in step with the signal. At loud levels it
        // is nothing; at the quiet end of the NES's range (volume 1 is a 9-bit channel value of about 9 RMS) it adds up to 3 dB. The AC RMS of a channel value s plus 3.5 sign(s) is sqrt(r^2 + 2 x 3.5 x E|s| + 3.5^2)
        // with E|s| about 0.9 r for the sine-like waveforms of these patches (exactly 0.9 for a sine). Units: one 9-bit step is 1/1536 of full scale (128/6 of 32768). ----
        private const double NineBitPerRms = 1536.0, LadderStep = 3.5, LadderMeanAbs = 0.9;

        /// <summary>The RMS (as a fraction of full scale) the discrete chip's ladder makes of a channel whose ideal RMS is <paramref name="idealRms"/>.</summary>
        public static double LadderActual(double idealRms)
        {
            double r = idealRms * NineBitPerRms;
            return Math.Sqrt(r * r + 2 * LadderStep * LadderMeanAbs * r + LadderStep * LadderStep) / NineBitPerRms;
        }

        /// <summary>The ideal RMS a channel needs for the discrete chip's ladder to bring its output to <paramref name="outputRms"/> (0 when that is below what the ladder alone gives).</summary>
        public static double LadderIdeal(double outputRms)
        {
            double t = outputRms * NineBitPerRms, k = LadderStep * LadderMeanAbs;
            double under = t * t - LadderStep * LadderStep + k * k;
            return under <= 0 ? 0 : Math.Max(0, -k + Math.Sqrt(under)) / NineBitPerRms;
        }

        /// <summary>The linear amplitude of a carrier at total level <paramref name="tl"/> (0.75 dB a step; 1 at TL 0).</summary>
        public static double Amplitude(int tl) => Math.Pow(10.0, -tl * 0.75 / 20.0);

        /// <summary>The operators (bit 0 operator 1, in algorithm order) that reach the output in each of the eight algorithms.</summary>
        public static int CarriersOf(int algorithm) => algorithm switch { 7 => 0xF, 5 or 6 => 0xE, 4 => 0xA, _ => 0x8 };

        /// <summary>The register offset of operator <paramref name="n"/> (0 to 3, algorithm order) inside a channel's register block: the chip stores them in the order 1, 3, 2, 4.</summary>
        public static int SlotOffset(int n) => n switch { 0 => 0, 1 => 8, 2 => 4, _ => 12 };

        private static FmOperator O(int mul, int tl, int dt = 0, int ar = 31, int d1r = 0, int sl = 0, int d2r = 0, int rr = 10, int rs = 0) => new(mul, tl, dt, ar, d1r, sl, d2r, rr, rs);
        private static readonly FmOperator Off = O(1, 127);

        private static FmPatch Fm(string name, string description, int alg, int fb, FmOperator o1, FmOperator o2, FmOperator o3, FmOperator o4) =>
            new(name, description, alg, fb, new[] { o1, o2, o3, o4 });

        /// <summary>The bank. Index 0 is the default (a synth brass); the last entry is the PSG square, the only one that is not FM.</summary>
        public static readonly FmPatch[] Patches =
        {
            Fm("Brass", "Two 1:1 pairs a few cents apart whose modulators swell and settle (alg 4): a synth brass",
                4, 3, O(1, 20, ar: 18, d1r: 8, sl: 2), O(1, 4), O(1, 24, dt: 3, ar: 17, d1r: 8, sl: 2), O(1, 22, dt: 3)),
            Fm("Buzz lead", "A fed-back operator (feedback 3) bending a 1:1 carrier (alg 4): a bright, buzzy lead with strong odd harmonics",
                4, 3, O(1, 14), O(1, 0), Off, Off),
            Fm("Square", "Odd harmonics 1, 3, 5, 7 at 1/n (alg 7, additive): a band-limited square wave",
                7, 0, O(1, 3), O(3, 16), O(5, 22), O(7, 25)),
            Fm("Pulse 25%", "Harmonics 1, 2, 3, 5 at the spectrum of a 25% pulse (alg 7, additive): a thin NES-like pulse",
                7, 0, O(1, 7), O(2, 11), O(3, 20), O(5, 26)),
            Fm("Organ", "Four drawbars at 1x, 2x, 4x and 6x (alg 7, additive): a flute-and-reed organ",
                7, 0, O(1, 4), O(2, 7), O(4, 11), O(6, 15, ar: 28)),
            Fm("Strings", "One 1:1 modulator over three carriers, the outer two a few cents either side, slow attack (alg 5): a string ensemble",
                5, 0, O(1, 28, ar: 15, rr: 7), O(1, 3, ar: 15, rr: 7), O(1, 30, dt: 3, ar: 14, rr: 7), O(1, 30, dt: 7, ar: 14, rr: 7)),
            Fm("Clarinet", "A modulator at twice the carrier's frequency gives odd harmonics only (alg 4)",
                4, 0, O(2, 30, d1r: 6, sl: 3), O(1, 0), Off, Off),
            Fm("Flute", "A whisper of 1:1 modulation on a sine, soft attack (alg 4)",
                4, 0, O(1, 54), O(1, 0, ar: 22), Off, Off),
            Fm("E. piano", "A bright tine (14x, fast fade) over a 1:1 body that mellows (alg 4)",
                4, 0, O(1, 36, d1r: 10, sl: 8), O(1, 4), O(14, 38, d1r: 26, sl: 15), O(1, 18)),
            Fm("Pluck", "A hard 1:1 modulator that fades to a whisper: bright, then mellow (alg 4)",
                4, 0, O(1, 16, d1r: 14, sl: 10), O(1, 4), Off, Off),
            Fm("Bell", "A 7x modulator that fades over a fundamental and an octave partial (alg 4): glassy",
                4, 0, O(7, 24, d1r: 12, sl: 12), O(1, 4), O(3, 30, d1r: 14, sl: 15), O(2, 14)),
            Fm("Marimba", "A 3x modulator that fades fast: a knock, then a round tone (alg 4)",
                4, 0, O(3, 22, d1r: 16, sl: 12), O(1, 3), Off, Off),
            Fm("Bass punch", "A fed-back 1:1 modulator that fades quickly: a punchy synth bass (alg 4)",
                4, 3, O(1, 20, d1r: 14, sl: 10), O(1, 0), Off, Off),
            Fm("Bass growl", "A four-operator chain, every ratio 1:1 (alg 0): a harsh, buzzing bass",
                0, 4, O(1, 30, d1r: 6, sl: 4), O(1, 24, d1r: 6, sl: 4), O(1, 20), O(1, 0)),
            Fm("Sine", "One carrier and nothing else (alg 7): a pure sine; the voice the triangle always uses",
                7, 0, Off, Off, Off, O(1, 0)),
            new FmPatch("PSG square", "No FM: the pulses are the PSG's square tones (50% duty, 109 Hz and up); the triangle stays a sine and the noise stays the PSG's",
                7, 0, new[] { Off, Off, Off, Off }, psg: true),
        };

        /// <summary>
        /// The measured steady-state RMS of each patch at its own total levels (see <see cref="FmPatch.CalibratedRms"/>), in the order of <see cref="Patches"/>. Printed by <c>--md-patches --calibrate</c>;
        /// <c>--sega-bridge-test</c> measures every patch again and fails when the chip no longer plays one at this level (a patch edited without calibrating, or a change to the FM core).
        /// </summary>
        private static readonly double[] Calibration =
        {
            0.07208, 0.12252, 0.09904, 0.08281, 0.12015, 0.07972, 0.14689, 0.11714, 0.10870, 0.08325, 0.09093, 0.09160, 0.11816, 0.13760, 0.11868, 0,
        };

        static FmPatchBank()
        {
            for (int i = 0; i < Patches.Length && i < Calibration.Length; i++) Patches[i].CalibratedRms = Calibration[i];
        }

        /// <summary>The calibration table as it is compiled in (for the verifier).</summary>
        public static double[] CalibrationTable => (double[])Calibration.Clone();

        /// <summary>Patch names, in index order.</summary>
        public static string[] Names { get; } = Array.ConvertAll(Patches, p => p.Name);

        public static int Count => Patches.Length;
        public const int DefaultIndex = 0;
        /// <summary>The index of the sine patch (the triangle's voice).</summary>
        public static readonly int SineIndex = Array.FindIndex(Patches, p => p.Name == "Sine");
        /// <summary>The index of the PSG square entry.</summary>
        public static readonly int PsgIndex = Array.FindIndex(Patches, p => p.Psg);

        /// <summary>A patch index made safe: anything out of range is the last patch (the PSG square), never an exception.</summary>
        public static int Clamp(int index) => Math.Clamp(index, 0, Patches.Length - 1);

        // ---- pitch: the YM2612 frequency registers (block 0-7, fnum 1-2047) ----

        /// <summary>The phase step the chip's frequency registers make: ymfm's arithmetic, ((fnum x 2) << block) >> 2, in 20-bit-phase units per sample (the multiple of the carriers, 1, is not applied).</summary>
        public static int PhaseStep(int block, int fnum) => ((fnum << 1) << block) >> 2;

        /// <summary>The frequency the chip plays for a block and fnum at a chip input clock (master / 7): step x (clock / 144) / 2^20.</summary>
        public static double Hz(int block, int fnum, double chipClockHz) => PhaseStep(block, fnum) * (chipClockHz / 144.0) / 1048576.0;

        /// <summary>
        /// The block and fnum whose frequency is nearest, in cents, to <paramref name="hz"/>, or false when the chip cannot reach it (the top is about 6.6 kHz). Every block is tried and the nearest wins, so the
        /// truncation in the chip's phase step (block 0 loses the low bit of fnum) is part of the choice. The error is bounded by half a step of the phase counter: under 1.7 cents at 27 Hz (the NES triangle's lowest note),
        /// under 0.9 cents from 54 Hz up, and the NES's own timer steps are coarser than that above 250 Hz.
        /// </summary>
        public static bool TryPitch(double hz, double chipClockHz, out int block, out int fnum, out double cents)
        {
            block = 0; fnum = 0; cents = 0;
            if (!(hz > 0)) return false;
            double target = hz * 1048576.0 / (chipClockHz / 144.0);    // wanted phase step
            double best = double.MaxValue;
            for (int b = 0; b < 8; b++)
            {
                // step = (fnum << b) >> 1: fnum = 2 step / 2^b (block 0: step = fnum / 2 truncated, so an even fnum is exact)
                double scale = (1 << b) / 2.0;                  // step per unit of fnum (0.5 at block 0, 1 at block 1, ...)
                int centre = (int)Math.Round(target / scale);
                for (int f = centre - 1; f <= centre + 1; f++)
                {
                    if (f < 1 || f > 2047) continue;
                    int step = PhaseStep(b, f);
                    if (step < 1) continue;
                    double c = 1200 * Math.Log2(step / target);
                    if (Math.Abs(c) < best - 1e-9) { best = Math.Abs(c); block = b; fnum = f; cents = c; }
                }
            }
            return best < 50;      // a pitch the chip cannot get within a quarter of a semitone (above its top, about 6.6 kHz) is out of range
        }
    }
}
