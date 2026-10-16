using NesEmulator.Plugin;
using NesEmulator.Snes;

namespace BrokenNes2;

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
