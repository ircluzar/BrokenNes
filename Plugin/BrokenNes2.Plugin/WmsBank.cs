// WideMess milestone 7a: the WideMess sound chip as an instrument (chip WMS, Direct mode). Its built-in instruments are 16-bit PCM waveforms made here, laid out in the chip's 1 MB sound RAM
// the way a WideMess game lays out its own (a 16-byte-entry directory and the samples, docs/projects/widemess-spec.md section 4), and played on the chip's voices 8-31 by WmsSampler.
namespace BrokenNes2;

/// <summary>
/// The WMS chip's built-in instruments (the Instrument parameter picks one; 0 is the channel's own). Every waveform is 16-bit PCM in eleven loops of one cycle, one per octave (4,096 samples down to 4), each
/// band-limited so that nothing above 16 kHz is in it at twice its own rate: a note starts on the loop whose rate puts its pitch register between $1000 and $1FFF (1 to 2 times the loop's own rate), so the
/// pitch is exact to a fifth of a cent and a held note can bend up an octave or more and down as far as FL takes it. Each instrument has its S-DSP envelope (ADSR). Designed here, from the additive formulas
/// of the waveforms named; nobody has listened to them (what is measured is pitch, level, harmonic content and polyphony).
/// </summary>
internal static class WmsBank
{
    /// <summary>One instrument: a waveform and its envelope (the S-DSP's ADSR1 / ADSR2).</summary>
    public sealed record Patch(string Name, string Note, string Description, int Wave, byte Adsr1, byte Adsr2);

    // the waveforms (a directory block each: Tables entries for the looped ones, one for the noises)
    public const int WSquare = 0, WPulse25 = 1, WPulse12 = 2, WTriangle = 3, WSaw = 4, WSine = 5, WOrgan = 6, WStrings = 7, WEPiano = 8, WBass = 9, WNoise = 10, WNoisePeriodic = 11;
    public const int Waves = 12, Tables = 11, LongestTable = 4096;
    /// <summary>Where the bank's directory and samples sit in the 1 MB (a WideMess game's own layout: the directory on a 256-byte boundary above the SPC700's 64 KB).</summary>
    public const int DirectoryAt = 0x10000, SamplesAt = 0x11000;
    /// <summary>The peak of a waveform (16-bit). Low enough that a few voices summed in the S-DSP's 16-bit main sum do not clip.</summary>
    public const int Peak = 12000;

    private const byte Hold1 = 0xFF, Hold2 = 0xE0;     // instant attack, held at full level until the key goes up (the S-DSP's release then takes about 8 ms)

    /// <summary>The instruments, as the Instrument parameter numbers them. Out of range is the last one.</summary>
    public static readonly Patch[] Patches =
    [
        new("Auto", "the channel's own", "Pulse 1 / 2: a pulse at the Pulse duty; Triangle: a triangle; Noise: noise in the Noise mode (long = white, short = periodic)", -1, Hold1, Hold2),
        new("Square", "50% pulse", "odd harmonics at 1/n: the NES pulse at 50%", WSquare, Hold1, Hold2),
        new("Pulse 25%", "25% pulse", "every harmonic but the fourths: the NES pulse at 25% (and 75%)", WPulse25, Hold1, Hold2),
        new("Pulse 12.5%", "12.5% pulse", "the thin NES pulse", WPulse12, Hold1, Hold2),
        new("Triangle", "odd harmonics at 1/n^2", "the NES triangle's shape without its 4-bit steps", WTriangle, Hold1, Hold2),
        new("Sawtooth", "every harmonic at 1/n", "bright and buzzy", WSaw, Hold1, Hold2),
        new("Sine", "one harmonic", "a pure tone", WSine, Hold1, Hold2),
        new("Organ", "drawbars 1, 2, 3, 4, 6, 8", "six harmonics at the levels of a drawbar organ, held", WOrgan, Hold1, Hold2),
        new("Strings", "soft saw, slow attack", "every harmonic at 1/n^1.4, a 0.2 s attack, held", WStrings, 0xFA, Hold2),
        new("Pluck", "saw that fades", "a sawtooth with a fast decay to a quiet tail", WSaw, 0xBF, 0x4E),
        new("E. piano", "sine with a tine", "a fundamental, a soft octave and a bright 14th harmonic, fading", WEPiano, 0xCF, 0x8C),
        new("Synth bass", "round saw", "every harmonic at 1/n^2 with a strong fundamental, a short decay", WBass, 0xDF, 0xA8),
        new("Noise", "white", "a 16,384-sample white noise; the note sets how bright it is (C5 = the chip's own rate)", WNoise, Hold1, Hold2),
        new("Noise (periodic)", "the NES's short noise", "the 93-step sequence of the NES noise's short mode, looped: a metallic buzz", WNoisePeriodic, Hold1, Hold2),
    ];

    public static int Clamp(int index) => Math.Clamp(index, 0, Patches.Length - 1);

    /// <summary>The waveform a patch plays on a channel: patch 0 ("Auto") follows the channel (the Pulse duty: 12.5, 25, 50, 75 %; the Noise mode).</summary>
    public static int WaveOf(int patch, int channel, int duty, int noiseMode)
    {
        var p = Patches[Clamp(patch)];
        if (p.Wave >= 0) return p.Wave;
        return channel switch
        {
            Ch.Triangle => WTriangle,
            Ch.Noise => noiseMode == 1 ? WNoisePeriodic : WNoise,
            _ => duty switch { 0 => WPulse12, 2 => WSquare, _ => WPulse25 },   // 25% and 75% have the same harmonics
        };
    }

    public static bool IsNoise(int wave) => wave is WNoise or WNoisePeriodic;

    /// <summary>The directory entry of a waveform's loop <paramref name="table"/> (0 = 4,096 samples ... 10 = 4), or of a noise.</summary>
    public static int Entry(int wave, int table) => IsNoise(wave) ? Waves * Tables + (wave - WNoise) : wave * Tables + Math.Clamp(table, 0, Tables - 1);

    /// <summary>The loop length (samples) of table <paramref name="table"/>.</summary>
    public static int TableLength(int table) => LongestTable >> table;

    /// <summary>The table a note of <paramref name="hz"/> starts on: the one whose rate puts the pitch register between $1000 and $1FFF (the lowest and highest notes use the end tables).</summary>
    public static int TableFor(double hz)
    {
        for (int t = 0; t < Tables; t++)
            if (hz * TableLength(t) < 64000) return t;
        return Tables - 1;
    }

    /// <summary>The pitch register for <paramref name="hz"/> on table <paramref name="table"/> (a noise: C5 = $1000), 1..$3FFF.</summary>
    public static int PitchFor(int wave, int table, double hz) =>
        Math.Clamp((int)Math.Round(IsNoise(wave) ? 0x1000 * hz / 261.6255653005986 : 4096.0 * hz * TableLength(table) / 32000.0), 1, 0x3FFF);

    // ---- the sound RAM image ----

    private static byte[]? image;
    private static readonly object gate = new();

    /// <summary>The bank's 1 MB sound RAM image (made once per process, on the first thread that asks: the UI thread that places an instance on the chip, never the audio thread). Read-only once made.</summary>
    public static byte[] Image
    {
        get
        {
            var img = Volatile.Read(ref image);
            if (img != null) return img;
            lock (gate)
            {
                image ??= Build();
                return image;
            }
        }
    }

    private static byte[] Build()
    {
        var ram = new byte[0x100000];
        int at = SamplesAt;
        var sine = new double[LongestTable];
        for (int k = 0; k < LongestTable; k++) sine[k] = Math.Sin(2 * Math.PI * k / LongestTable);
        var cycle = new double[LongestTable];
        for (int w = 0; w < WNoise; w++)
        {
            // the waveform's level is set once, from its widest table (every table of it then has the same fundamental: the octaves do not jump in loudness)
            double scale = 0;
            for (int t = 0; t < Tables; t++)
            {
                int n = TableLength(t);
                Synth(w, n, sine, cycle);
                for (int k = 0; k < n; k++) scale = Math.Max(scale, Math.Abs(cycle[k]));
            }
            scale = scale > 0 ? Peak / scale : 0;
            for (int t = 0; t < Tables; t++)
            {
                int n = TableLength(t);
                Synth(w, n, sine, cycle);
                int start = at;
                for (int k = 0; k < n; k++, at += 2)
                {
                    int s = (int)Math.Round(cycle[k] * scale);
                    ram[at] = (byte)s; ram[at + 1] = (byte)(s >> 8);
                }
                WriteEntry(ram, Entry(w, t), start, start, at, loops: true);
            }
        }
        // white noise: a fixed seed (the same bytes in every process)
        {
            int start = at; uint x = 0x9E3779B9;
            for (int k = 0; k < 16384; k++, at += 2)
            {
                x ^= x << 13; x ^= x >> 17; x ^= x << 5;
                int s = (int)((x & 0xFFFF) * (2 * Peak) / 65535) - Peak;
                ram[at] = (byte)s; ram[at + 1] = (byte)(s >> 8);
            }
            WriteEntry(ram, Entry(WNoise, 0), start, start, at, loops: true);
        }
        // the NES noise's short mode: its 15-bit register with the tap at bit 6, 93 steps; four periods (a multiple of four samples, the PCM group)
        {
            int start = at; int reg = 1;
            for (int k = 0; k < 93 * 4; k++, at += 2)
            {
                int s = (reg & 1) != 0 ? -Peak : Peak;
                int fb = (reg ^ (reg >> 6)) & 1; reg = (reg >> 1) | (fb << 14);
                ram[at] = (byte)s; ram[at + 1] = (byte)(s >> 8);
            }
            WriteEntry(ram, Entry(WNoisePeriodic, 0), start, start, at, loops: true);
        }
        return ram;
    }

    /// <summary>A 16-byte directory entry (spec 4): start, format (bit 0 PCM, bit 1 loops), loop, end.</summary>
    internal static void WriteEntry(byte[] ram, int index, int start, int loop, int end, bool loops)
    {
        int e = DirectoryAt + index * 16;
        ram[e] = (byte)start; ram[e + 1] = (byte)(start >> 8); ram[e + 2] = (byte)(start >> 16 & 0x0F);
        ram[e + 3] = (byte)(loops ? 3 : 1);
        ram[e + 4] = (byte)loop; ram[e + 5] = (byte)(loop >> 8); ram[e + 6] = (byte)(loop >> 16 & 0x0F);
        ram[e + 8] = (byte)end; ram[e + 9] = (byte)(end >> 8); ram[e + 10] = (byte)(end >> 16 & 0x0F);
    }

    /// <summary>One cycle of waveform <paramref name="wave"/> in <paramref name="n"/> samples, band-limited to n/4 harmonics (and at most 256).</summary>
    private static void Synth(int wave, int n, double[] sine, double[] outp)
    {
        int hmax = Math.Min(n / 4, 256);
        if (hmax < 1) hmax = 1;
        int step = LongestTable / n;
        Array.Clear(outp, 0, n);
        for (int h = 1; h <= hmax; h++)
        {
            double amp; double phase = 0;     // phase: 0 = a sine, 0.25 = a cosine (fractions of a cycle)
            switch (wave)
            {
                case WSquare: amp = h % 2 == 1 ? 1.0 / h : 0; break;
                case WPulse25: amp = 2 * Math.Sin(Math.PI * h * 0.25) / (Math.PI * h); phase = 0.25 - 0.125 * h; break;
                case WPulse12: amp = 2 * Math.Sin(Math.PI * h * 0.125) / (Math.PI * h); phase = 0.25 - 0.0625 * h; break;
                case WTriangle: amp = h % 2 == 1 ? ((h / 2) % 2 == 0 ? 1.0 : -1.0) / ((double)h * h) : 0; break;
                case WSaw: amp = (h % 2 == 1 ? 1.0 : -1.0) / h; break;
                case WSine: amp = h == 1 ? 1 : 0; break;
                case WOrgan: amp = h switch { 1 => 1.0, 2 => 0.8, 3 => 0.6, 4 => 0.5, 6 => 0.3, 8 => 0.25, _ => 0 }; break;
                case WStrings: amp = (h % 2 == 1 ? 1.0 : -1.0) / Math.Pow(h, 1.4); break;
                case WEPiano: amp = h switch { 1 => 1.0, 2 => 0.25, 3 => 0.08, 14 => 0.1, _ => 0 }; break;
                case WBass: amp = h == 1 ? 1.4 : (h % 2 == 1 ? 1.0 : -1.0) / ((double)h * h); break;
                default: amp = 0; break;
            }
            if (amp == 0) continue;
            int ph = (int)Math.Round(phase * LongestTable) & (LongestTable - 1);
            for (int k = 0; k < n; k++) outp[k] += amp * sine[(h * k * step + ph) & (LongestTable - 1)];
        }
    }
}
