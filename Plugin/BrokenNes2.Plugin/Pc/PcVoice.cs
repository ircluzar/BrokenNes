// The x86 PC console's chips in Direct mode (2026-11-10): one FL instance = one voice of a real BrokenNes x86 sound device.
// Each instance owns a PcMachine whose CPU never runs: the plugin only moves the machine's clock (IdleFor) and writes the device's ports as a DOS program would,
// so the sound is the x86 console's own (Opl2.cs = ymfm or another emulator's OPL2 engine through PcSound.Apply, PcSpeaker through the 8254, DOSBox 0.72's Tandy and
// Sound Blaster, DOSBox 0.74's Gravis Ultrasound, the Covox), with their quirks kept (art chips: the Tandy mutes 5 s after its last write, the SB's direct DAC is smeared
// over 1 ms ticks, the GUS's AutoAmp never recovers ...). The machine draws at 44.1 kHz; this class resamples linearly to the host's rate and takes the DC away (the
// speaker and the Tandy are unipolar).
// Made on the UI thread (a PcMachine allocates its memory); Render allocates nothing.
using NesEmulator.X86;

namespace BrokenNes2.Pc;

public enum PcKind { Adlib, Speaker, SbDac, Tandy, Gus, Covox }

public sealed class PcVoice
{
    public readonly PcKind Kind;
    public readonly string ApuId;
    private readonly PcMachine m;
    private const int Rate = 44100;
    private readonly short[] buf = new short[4096];
    private readonly float[] q = new float[8192];     // 44.1 kHz mono queue
    private int qHead, qCount;
    private double frac;                               // resampler position between q[head] and q[head+1]
    private float dcX, dcY;
    private long samplesMade;                          // 44.1 kHz samples the machine was asked for
    private readonly float gain;

    // the note
    public bool Keyed { get; private set; }
    private double hz;
    private int level = 127;                           // velocity 0-127
    private int lastKeyHi = -1, lastFnum = -1;
    private byte[] patch = (byte[])OplBank.Patches[2].Regs.Clone();
    private bool snare;
    private int wave;                                  // DAC chips / Tandy mode
    private double phase;
    private uint rng = 0x12345678;
    private readonly sbyte[] table = new sbyte[PcWaves.Points];
    public long PortWrites;

    /// <summary>The chips' kinds by chip id (the Cx chip ids of the PC console).</summary>
    public static PcKind KindOf(string apu) => apu.StartsWith("ADLIB", StringComparison.OrdinalIgnoreCase) ? PcKind.Adlib : apu.ToUpperInvariant() switch
    {
        "SPEAKER" => PcKind.Speaker, "SB_D072" => PcKind.SbDac, "TANDY_D072" => PcKind.Tandy, "GUS_D074" => PcKind.Gus, "COVOX" => PcKind.Covox,
        _ => throw new ArgumentException("not a PC sound device: " + apu),
    };

    public PcVoice(string apuId)
    {
        ApuId = apuId;
        Kind = KindOf(apuId);
        m = new PcMachine(1 << 20);
        m.Speaker.Enabled = true;
        m.Opl.Enabled = true;
        if (!PcSound.Apply(m, apuId)) throw new ArgumentException("PcSound does not know " + apuId);
        // levels matched to the ymfm AdLib (measured rms of A4 by `selftest --only pc`): MAME 0.36 and DBOPL draw twice as loud (their own quirk), so they are halved here
        gain = Kind switch
        {
            PcKind.Adlib => apuId.ToUpperInvariant() is "ADLIB_M036" or "ADLIB_D074F" ? 0.5f / 5000f : 1f / 5000f,
            PcKind.Speaker => 0.6f / 5000f, PcKind.Tandy => 0.5f / 11000f, PcKind.Gus => 0.33f / 9000f, PcKind.SbDac => 0.37f / 14000f, _ => 1f / 14000f,
        };
        Setup();
    }

    private void Out(int port, int v) { m.Out8(port, (byte)v); PortWrites++; }
    private void Opl(int r, int v) { Out(0x388, r); Out(0x389, v); }
    private void Step(long instr) => m.IdleFor(Math.Max(0, instr));

    private void Setup()
    {
        switch (Kind)
        {
            case PcKind.Adlib:
                Opl(0x01, 0x20);       // waveform select on
                Opl(0xBD, 0x00);
                break;
            case PcKind.SbDac:
                Out(0x226, 1); Step(2000); Out(0x226, 0); Step(20000);
                m.In8(0x22E); m.In8(0x22A);                        // the DSP's AAh
                Out(0x22C, 0xD1);                                  // speaker on
                Step(2000);
                break;
            case PcKind.Gus:
                GusReg8(0x4C, 0x00); Step(2000); GusReg8(0x4C, 0x07);   // out of reset, DAC on
                GusReg8(0x0E, 0xC0 | 13);                               // 14 active voices: 44.1 kHz
                Out(0x240, 0x00);                                       // line out on
                break;
            case PcKind.Tandy:
                for (int c = 0; c < 4; c++) Out(0xC0, 0x9F | (c << 5));  // all silent
                break;
        }
    }

    // ---- the Gravis Ultrasound's register file (240h: 342h voice, 343h register, 344h/345h data, 347h DRAM) ----
    private void GusReg8(int reg, int v) { Out(0x343, reg); Out(0x345, v); }
    private void GusReg16(int reg, int v) { Out(0x343, reg); m.Out16(0x344, (ushort)v); PortWrites++; }
    private void GusPoke(int addr, int v) { GusReg16(0x43, addr & 0xFFFF); GusReg8(0x44, (addr >> 16) & 0x0F); Out(0x347, v); }
    private static int GusAddrHi(int a) => (a >> 7) & 0x1FFF;
    private static int GusAddrLo(int a) => (a & 0x7F) << 9;

    /// <summary>The FM patch (OPL chips; 11 bytes) or the waveform / Tandy mode (the other chips) the next note plays.</summary>
    public void SetPatch(byte[] regs, bool isSnare)
    {
        if (regs.AsSpan().SequenceEqual(patch) && isSnare == snare) return;
        patch = (byte[])regs.Clone();
        snare = isSnare;
        if (Kind == PcKind.Adlib) WritePatch();
    }

    public void SetWave(int w)
    {
        if (w == wave && !(Kind == PcKind.Gus && !gusLoaded)) return;
        wave = w;
        uint r = 0xC0FFEE;
        for (int i = 0; i < table.Length; i++) table[i] = (sbyte)Math.Clamp(PcWaves.At(w, i, ref r), -127, 127);
        if (Kind == PcKind.Gus) GusLoad();
    }

    private bool gusLoaded;
    private void GusLoad()
    {
        for (int i = 0; i < table.Length; i++) GusPoke(i, (byte)table[i]);
        GusPoke(table.Length, (byte)table[0]);
        gusLoaded = true;
    }

    private int CarrierTl(int baseTl) => Math.Clamp(baseTl + (level >= 127 ? 0 : level <= 0 ? 63 : (int)Math.Round(-20 * Math.Log10(level / 127.0) / 0.75)), 0, 63);

    private void WritePatch()
    {
        if (snare)
        {
            // the rhythm mode's snare: operator 14h (channel 7's carrier), pitch from channel 7
            Opl(0x20 + 0x14, patch[1]); Opl(0x40 + 0x14, (patch[3] & 0xC0) | CarrierTl(patch[3] & 0x3F)); Opl(0x60 + 0x14, patch[5]); Opl(0x80 + 0x14, patch[7]); Opl(0xE0 + 0x14, patch[9]);
            return;
        }
        Opl(0x20, patch[0]); Opl(0x23, patch[1]);
        int modTl = patch[2] & 0x3F;
        Opl(0x40, (patch[2] & 0xC0) | ((patch[10] & 1) != 0 ? CarrierTl(modTl) : modTl));
        Opl(0x43, (patch[3] & 0xC0) | CarrierTl(patch[3] & 0x3F));
        Opl(0x60, patch[4]); Opl(0x63, patch[5]); Opl(0x80, patch[6]); Opl(0x83, patch[7]);
        Opl(0xE0, patch[8] & 3); Opl(0xE3, patch[9] & 3); Opl(0xC0, patch[10] & 0x0F);
    }

    private static (int hi, int fnum) OplFreq(double f)
    {
        for (int block = 0; block < 8; block++)
        {
            int fn = (int)Math.Round(f * (1 << (20 - block)) / 49716.0);
            if (fn < 1024) return (block << 2 | fn >> 8, fn);
        }
        return (7 << 2 | 3, 1023);
    }

    public void NoteOn(double frequency, int velocity)
    {
        hz = frequency; level = Math.Clamp(velocity, 0, 127);
        switch (Kind)
        {
            case PcKind.Adlib:
                if (snare) { Opl(0xBD, 0x20); WritePatch(); SetFreq(true); Opl(0xBD, 0x28); }
                else { Opl(0xB0, lastKeyHi < 0 ? 0 : lastKeyHi & 0x1F); WritePatch(); SetFreq(true); }
                break;
            case PcKind.Speaker:
                Out(0x43, 0xB6); SetFreq(true); Out(0x61, 0x03);
                break;
            case PcKind.Tandy:
                SetFreq(true); TandyLevel();
                break;
            case PcKind.Gus:
                if (!gusLoaded) GusLoad();
                Out(0x342, 0);
                GusReg16(0x02, GusAddrHi(0)); GusReg16(0x03, GusAddrLo(0));
                GusReg16(0x04, GusAddrHi(table.Length - 1)); GusReg16(0x05, GusAddrLo(table.Length - 1));
                GusReg16(0x0A, GusAddrHi(0)); GusReg16(0x0B, GusAddrLo(0));
                GusReg8(0x0C, 7);
                SetFreq(true);
                GusReg16(0x09, GusVolume() << 4);
                GusReg8(0x00, 0x08);           // run, 8-bit, loop
                break;
            default:
                phase = 0;
                break;
        }
        Keyed = true;
    }

    private int GusVolume() => level <= 0 ? 0 : Math.Clamp(4095 + (int)Math.Round(Math.Log2(level / 127.0) * 256), 0, 4095);   // (the GUS volume is logarithmic: 256 steps an octave of level)

    private void TandyLevel()
    {
        int att = level <= 0 ? 15 : Math.Clamp((int)Math.Round(-20 * Math.Log10(level / 127.0) / 2), 0, 15);
        int ch = wave == 0 ? 0 : 3;
        Out(0xC0, 0x90 | (ch << 5) | att);
    }

    public void Bend(double frequency) { if (Keyed && frequency != hz) { hz = frequency; SetFreq(false); } }

    private void SetFreq(bool force)
    {
        switch (Kind)
        {
            case PcKind.Adlib:
            {
                var (hi, fn) = OplFreq(hz);
                if (!force && hi == lastKeyHi && fn == lastFnum) return;
                int ch = snare ? 7 : 0;
                Opl(0xA0 + ch, fn & 0xFF);
                Opl(0xB0 + ch, (snare ? 0 : 0x20) | hi);
                lastKeyHi = hi; lastFnum = fn;
                break;
            }
            case PcKind.Speaker:
            {
                int d = Math.Clamp((int)Math.Round(Pit8254.Freq / Math.Max(1.0, hz)), 1, 65535);
                Out(0x42, d & 0xFF); Out(0x42, d >> 8);
                break;
            }
            case PcKind.Tandy:
            {
                // the SN76496 at 3,579,545 Hz: f = clock / (32 n); the noise modes take tone 3's pitch (mode 3), whose own volume stays off
                int n = Math.Clamp((int)Math.Round(3579545.0 / 32 / Math.Max(1.0, wave == 0 ? hz : hz / 2)), 1, 1023);
                int ch = wave == 0 ? 0 : 2;
                Out(0xC0, 0x80 | (ch << 5) | (n & 15)); Out(0xC0, (n >> 4) & 0x3F);
                if (wave != 0 && force) Out(0xC0, 0xE0 | (wave == 2 ? 4 : 0) | 3);
                break;
            }
            case PcKind.Gus:
            {
                int fc = Math.Clamp((int)Math.Round(hz * table.Length / Rate * 1024), 1, 32767);
                GusReg16(0x01, fc);
                break;
            }
        }
    }

    public void NoteOff()
    {
        if (!Keyed) return;
        Keyed = false;
        switch (Kind)
        {
            case PcKind.Adlib:
                if (snare) Opl(0xBD, 0x20);
                else Opl(0xB0, lastKeyHi & 0x1F);
                break;
            case PcKind.Speaker: Out(0x61, 0x00); break;
            case PcKind.Tandy: Out(0xC0, 0x9F | ((wave == 0 ? 0 : 3) << 5)); break;
            case PcKind.Gus: Out(0x342, 0); GusReg8(0x00, 0x03); break;
        }
    }

    // ---- sound ----

    private long InstrAt(long sample) => (long)((Int128)sample * m.InstructionsPerSecond / Rate);

    /// <summary>Makes <paramref name="k"/> more 44.1 kHz samples into the queue.</summary>
    private void Make(int k)
    {
        if (Kind is PcKind.SbDac or PcKind.Covox)
        {
            // a DAC voice: the program writes each sample itself (8 bits, sample-and-hold), one write per 44.1 kHz sample
            double inc = hz * PcWaves.Points / Rate;
            for (int i = 0; i < k; i++)
            {
                int s = 0x80;
                if (Keyed)
                {
                    int idx = (int)phase & (PcWaves.Points - 1);
                    int v = wave == 6 ? PcWaves.At(6, idx, ref rng) : table[idx];
                    s = 0x80 + v * level / 127;
                    phase += inc; if (phase >= PcWaves.Points) phase -= PcWaves.Points;
                }
                if (Kind == PcKind.Covox) Out(0x378, s);
                else { Out(0x22C, 0x10); Out(0x22C, s); }
                samplesMade++;
                Step(InstrAt(samplesMade) - m.Clock);
            }
        }
        else
        {
            samplesMade += k;
            Step(InstrAt(samplesMade) - m.Clock);
        }
        m.Speaker.CatchUp(); m.Opl.CatchUp(); m.Card?.Advance();
        while (true)
        {
            int n = m.Speaker.Read(buf);
            if (n == 0) break;
            m.Opl.MixInto(buf, n);
            m.Card?.MixInto(buf, n);
            for (int i = 0; i + 1 < n; i += 2)
            {
                float x = (buf[i] + buf[i + 1]) * 0.5f * gain;
                float y = x - dcX + 0.9995f * dcY;            // DC blocker
                dcX = x; dcY = y;
                if (qCount < q.Length) { q[(qHead + qCount) % q.Length] = y; qCount++; }
            }
            if (n < buf.Length) break;
        }
    }

    /// <summary>Mono samples at <paramref name="hostRate"/> into <paramref name="dest"/>.</summary>
    public void Render(Span<float> dest, int hostRate)
    {
        double step = (double)Rate / hostRate;
        for (int i = 0; i < dest.Length; i++)
        {
            int guard = 0;
            while (qCount < 2 && guard++ < 4) Make(64);
            if (qCount < 2) { dest[i] = 0; continue; }
            float a = q[qHead], b = q[(qHead + 1) % q.Length];
            dest[i] = a + (b - a) * (float)frac;
            frac += step;
            while (frac >= 1 && qCount >= 2) { frac -= 1; qHead = (qHead + 1) % q.Length; qCount--; }
        }
    }

    /// <summary>The machine's clock in 44.1 kHz samples (tests).</summary>
    public long Samples => samplesMade;
}
