using NesEmulator.Plugin;
using NesEmulator.Snes;

namespace BrokenNes2;

/// <summary>
/// A copy of a SNES game's sound memory taken when Instrument Runaway starts: the 64 KB of audio RAM (where the game's BRR samples, its sample
/// directory and its echo buffer live) and the 128 S-DSP registers. Immutable once made; every instance plays from its own working copy.
/// </summary>
public sealed class SpcSnapshot
{
    public const int AramSize = 0x10000, DspSize = 128;
    public readonly byte[] Aram = new byte[AramSize];
    public readonly byte[] Dsp = new byte[DspSize];

    public SpcSnapshot(byte[] aram, byte[] dsp)
    {
        Array.Copy(aram, Aram, Math.Min(aram.Length, AramSize));
        Array.Copy(dsp, Dsp, Math.Min(dsp.Length, DspSize));
    }

    /// <summary>Where the game's sample directory is (the DSP's DIR register is the page): 4 bytes per sample, start and loop address.</summary>
    public int DirectoryBase => Dsp[0x5D] * 0x100;

    public int SampleStart(int srcn) => Entry(srcn, 0);
    public int SampleLoop(int srcn) => Entry(srcn, 2);

    private int Entry(int srcn, int offset)
    {
        int a = (DirectoryBase + (srcn & 0xFF) * 4 + offset) & 0xFFFF;
        return Aram[a] | (Aram[(a + 1) & 0xFFFF] << 8);
    }

    /// <summary>One instrument found in the game's sample table: its slot, where its BRR data starts and loops, its length in 9-byte blocks (16 samples
    /// each, so half a millisecond), whether it loops, and whether one of the game's eight voices is playing it right now.</summary>
    public readonly record struct SampleInfo(int Slot, int Start, int Loop, int Blocks, bool Loops, bool Sounding)
    {
        public double Milliseconds => Blocks * 0.5;
    }

    private SampleInfo[]? instruments;

    /// <summary>The instruments the game has loaded, read from its sample table: from the first slot that holds a well-formed sample, on, until a slot
    /// that does not. "Well-formed" means a chain of BRR blocks (range 12 or less) that ends with an end block, and a loop point on a block boundary
    /// inside it. It is a good guess, not a certainty: a table is only 4-byte entries, and other data can look like a sample.</summary>
    public IReadOnlyList<SampleInfo> Instruments()
    {
        if (instruments != null) return instruments;
        var list = new List<SampleInfo>();
        for (int slot = 0; slot < 256; slot++)
        {
            if (!TryParse(slot, out int blocks, out bool loops)) { if (list.Count > 0) break; else continue; }
            bool sounding = false;
            for (int v = 0; v < 8; v++) if (Dsp[v * 16 + 4] == slot && Dsp[v * 16 + 8] != 0) sounding = true;
            list.Add(new SampleInfo(slot, SampleStart(slot), SampleLoop(slot), blocks, loops, sounding));
        }
        return instruments = list.ToArray();
    }

    private bool TryParse(int srcn, out int blocks, out bool loops)
    {
        blocks = 0; loops = false;
        int start = SampleStart(srcn);
        if (start < 0x0100 || start + 9 > 0xFFC0 || DirectoryBase + (srcn & 0xFF) * 4 + 3 >= AramSize) return false;
        int a = start;
        while (blocks < 4096 && a + 9 <= 0xFFC0)
        {
            byte h = Aram[a];
            if ((h >> 4) > 12) return false;
            blocks++;
            if ((h & 1) != 0)
            {
                loops = (h & 2) != 0;
                if (!loops) return true;
                int loopAt = SampleLoop(srcn);
                return loopAt >= start && loopAt <= a && (loopAt - start) % 9 == 0;
            }
            a += 9;
        }
        return false;
    }

    /// <summary>A slot that holds a sample (one of <see cref="Instruments"/>).</summary>
    public bool HasSample(int srcn) => Instruments().Any(i => i.Slot == srcn);

    /// <summary>The instrument an instance with this slot setting plays: the slot itself when the game has a sample there, otherwise the one the game
    /// was playing when it froze, otherwise the first one found. (A new instance starts on slot 0, which many games do not use.)</summary>
    public int Resolve(int srcn)
    {
        var list = Instruments();
        if (list.Count == 0 || HasSample(srcn)) return srcn;
        foreach (var i in list) if (i.Sounding) return i.Slot;
        return list[0].Slot;
    }

    /// <summary>The envelope the game has set on a voice playing this sample number (ADSR1, ADSR2, GAIN), if one of its eight voices has it.</summary>
    public bool TryEnvelope(int srcn, out byte adsr1, out byte adsr2, out byte gain)
    {
        // a voice counts only if the game has set an envelope on it (ADSR on, or a gain): the all-zero registers of a voice it never used would
        // silence the instrument. A voice that is sounding right now (envelope above zero) is preferred.
        int best = -1;
        for (int v = 0; v < 8; v++)
        {
            int r = v * 16;
            if (Dsp[r + 4] != srcn || !HasSample(srcn)) continue;
            if ((Dsp[r + 5] & 0x80) == 0 && Dsp[r + 7] == 0) continue;
            if (best < 0 || (Dsp[r + 8] != 0 && Dsp[best * 16 + 8] == 0)) best = v;
        }
        if (best >= 0)
        {
            adsr1 = Dsp[best * 16 + 5]; adsr2 = Dsp[best * 16 + 6]; gain = Dsp[best * 16 + 7];
            return true;
        }
        adsr1 = adsr2 = gain = 0;
        return false;
    }

    /// <summary>A snapshot with one looping square-wave sample (sample number 0, 2 kHz at pitch $1000), for tests.</summary>
    public static SpcSnapshot TestSample()
    {
        var aram = new byte[AramSize];
        var dsp = new byte[DspSize];
        dsp[0x5D] = 0x01;                                            // directory at $0100
        aram[0x100] = 0x00; aram[0x101] = 0x02; aram[0x102] = 0x00; aram[0x103] = 0x02;   // sample 0 starts and loops at $0200
        aram[0x200] = 0x93;                                          // BRR block: range 9, filter 0, loop + end
        for (int i = 0; i < 4; i++) aram[0x201 + i] = 0x77;          // eight samples high ...
        for (int i = 4; i < 8; i++) aram[0x201 + i] = 0x99;          // ... eight samples low: 16 samples per cycle
        dsp[0x0C] = dsp[0x1C] = 0x7F;                                // master volume
        dsp[0x6C] = 0x20;                                            // echo writes off
        return new SpcSnapshot(aram, dsp);
    }

    // ---- saved with the project: [aram][dsp] ----
    public byte[] ToBytes()
    {
        var b = new byte[AramSize + DspSize];
        Aram.CopyTo(b, 0);
        Dsp.CopyTo(b, AramSize);
        return b;
    }

    public static SpcSnapshot? FromBytes(byte[] b) => b.Length == AramSize + DspSize ? new SpcSnapshot(b.AsSpan(0, AramSize).ToArray(), b.AsSpan(AramSize).ToArray()) : null;
}

/// <summary>
/// A private S-DSP over a working copy of a <see cref="SpcSnapshot"/>: eight voices that play the game's own samples, driven by FL notes (no
/// SPC700 program runs). Each instance owns one, so instances never have to agree on timing. The DSP makes 32 kHz stereo, resampled to the host rate.
/// </summary>
internal sealed class SnesSampler
{
    private const int KonReg = 0x4C, KoffReg = 0x5C;
    private readonly SpcSnapshot snap;
    private readonly DSP_SFC dsp;
    private readonly StreamResampler left = new(), right = new();
    private readonly float[] tmpL, tmpR;
    private readonly float[] one = new float[1];
    private byte koff = 0xFF, pendingKon;

    public SnesSampler(SpcSnapshot snapshot, int hostRate, int maxBlock = 4096)
    {
        snap = snapshot;
        var aram = (byte[])snapshot.Aram.Clone();
        dsp = new DSP_SFC(aram);
        dsp.Output = Push;
        tmpL = new float[maxBlock]; tmpR = new float[maxBlock];
        left.SetRates(32000, hostRate); right.SetRates(32000, hostRate);

        // the game's DSP state, without anything sounding: no key-on, nothing muted, no noise or pitch modulation
        for (int i = 0; i < SpcSnapshot.DspSize; i++) dsp.Write(i, snapshot.Dsp[i]);
        dsp.Write(0x6C, (byte)(snapshot.Dsp[0x6C] & 0x3F));            // FLG: soft reset and mute off, echo-write flag and noise rate as the game had them
        if (snapshot.Dsp[0x0C] == 0 && snapshot.Dsp[0x1C] == 0) { dsp.Write(0x0C, 0x60); dsp.Write(0x1C, 0x60); }
        dsp.Write(0x2D, 0); dsp.Write(0x3D, 0);                        // PMON, NON
        dsp.Write(0x4D, (byte)(snapshot.Dsp[0x4D] != 0 ? 0xFF : 0));   // echo on every voice if the game used it on any
        for (int v = 0; v < 8; v++) { dsp.Write(v * 16, 0); dsp.Write(v * 16 + 1, 0); }
        dsp.Write(KoffReg, 0xFF);
        dsp.Write(KonReg, 0);
    }

    private void Push(short l, short r)
    {
        one[0] = l * (1f / 32768f); left.Write(one);
        one[0] = r * (1f / 32768f); right.Write(one);
    }

    private static int PitchReg(double semitoneFromRoot) =>
        Math.Clamp((int)Math.Round(0x1000 * Math.Pow(2.0, semitoneFromRoot / 12.0)), 1, 0x3FFF);

    /// <summary>The DSP pitch register for a note: $1000 plays the sample at its own rate, at the root key (C5 = 60).</summary>
    public static int PitchFor(double cents) => PitchReg(cents / 100.0);

    public void NoteOn(int voice, int srcn, int pitch, int volL, int volR)
    {
        int r = voice * 16;
        dsp.Write(r, (byte)Math.Clamp(volL, -128, 127));
        dsp.Write(r + 1, (byte)Math.Clamp(volR, -128, 127));
        dsp.Write(r + 2, (byte)(pitch & 0xFF));
        dsp.Write(r + 3, (byte)((pitch >> 8) & 0x3F));
        dsp.Write(r + 4, (byte)srcn);
        // the game's own envelope for this instrument when one of its voices has it, else an organ-like one (instant attack, holds until key-off)
        if (snap.TryEnvelope(srcn, out byte a1, out byte a2, out byte g)) { dsp.Write(r + 5, a1); dsp.Write(r + 6, a2); dsp.Write(r + 7, g); }
        else { dsp.Write(r + 5, 0xFF); dsp.Write(r + 6, 0xE0); dsp.Write(r + 7, 0x7F); }
        koff &= (byte)~(1 << voice);
        dsp.Write(KoffReg, koff);
        pendingKon |= (byte)(1 << voice);
    }

    public void Update(int voice, int pitch, int volL, int volR)
    {
        int r = voice * 16;
        dsp.Write(r, (byte)Math.Clamp(volL, -128, 127));
        dsp.Write(r + 1, (byte)Math.Clamp(volR, -128, 127));
        dsp.Write(r + 2, (byte)(pitch & 0xFF));
        dsp.Write(r + 3, (byte)((pitch >> 8) & 0x3F));
    }

    public void NoteOff(int voice)
    {
        koff |= (byte)(1 << voice);
        dsp.Write(KoffReg, koff);
        pendingKon &= (byte)~(1 << voice);
    }

    /// <summary>Makes the key-ons of this block take effect (the DSP keeps one pending key-on register).</summary>
    public void FlushKon()
    {
        if (pendingKon != 0) { dsp.Write(KonReg, pendingKon); pendingKon = 0; }
    }

    /// <summary>Fills <paramref name="dest"/> with <paramref name="frames"/> interleaved stereo frames at the host rate.</summary>
    public void Render(Span<float> dest, int frames)
    {
        FlushKon();
        while (left.Available < frames) dsp.RunSample();
        left.Read(tmpL.AsSpan(0, frames));
        right.Read(tmpR.AsSpan(0, frames));
        for (int i = 0; i < frames; i++) { dest[i * 2] = tmpL[i]; dest[i * 2 + 1] = tmpR[i]; }
    }
}
