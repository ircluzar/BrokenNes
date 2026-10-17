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
    // instrument edits (SpcSnapshot.Overlay, null for a game's own sound memory: then none of this runs): the overlay entry each DSP voice plays, and the voice masks the edits' echo / noise / pitch-modulation flags set
    private readonly int[] voiceOverlay = [-1, -1, -1, -1, -1, -1, -1, -1];
    private byte eonMask, nonMask, pmonMask;
    private readonly byte defaultEon, flgBase;     // the echo bits plain instruments use; the game's FLG (echo-write flag and noise rate)

    // a voice playing a recipe: its macro walks forward one DSP sample at a time (no allocation: the state lives in these eight objects)
    private sealed class Macro
    {
        public SpcRecipe? R;
        public int Next, BasePitch, Cents, Level, VolL, VolR;
        public long Age;
        public double Scale;
    }
    private readonly Macro[] macros = Enumerable.Range(0, 8).Select(_ => new Macro()).ToArray();
    private int macroActive;

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
        flgBase = (byte)(snapshot.Dsp[0x6C] & 0x3F);
        dsp.Write(0x6C, flgBase);                                      // FLG: soft reset and mute off, echo-write flag and noise rate as the game had them
        if (snapshot.Dsp[0x0C] == 0 && snapshot.Dsp[0x1C] == 0) { dsp.Write(0x0C, 0x60); dsp.Write(0x1C, 0x60); }
        dsp.Write(0x2D, 0); dsp.Write(0x3D, 0);                        // PMON, NON
        defaultEon = (byte)(snapshot.Dsp[0x4D] != 0 ? 0xFF : 0);
        eonMask = defaultEon;
        dsp.Write(0x4D, eonMask);                                      // echo on every voice if the game used it on any
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

    /// <summary>The DSP pitch register for a note of a recipe: the pitch the game keyed the note on with is the root key (C5), and FL's pitch moves it from there.</summary>
    public static int PitchFor(double cents, int rootPitch) => Math.Clamp((int)Math.Round(rootPitch * Math.Pow(2.0, cents / 1200.0)), 1, 0x3FFF);

    public void NoteOn(int voice, int srcn, int pitch, int volL, int volR)
    {
        EndMacro(voice);
        var ov = snap.Overlay;
        if (ov != null)
        {
            // the sample's edited tuning, volume weights and flags (its edited envelope comes through SpcSnapshot.TryEnvelope below)
            int oi = ov.IndexOf(srcn);
            voiceOverlay[voice] = oi;
            ApplyMix(voice, ov, oi);
            if (oi >= 0) { pitch = ov.AdjustPitch(oi, pitch); volL = ov.AdjustVolume(oi, volL, false); volR = ov.AdjustVolume(oi, volR, true); }
        }
        else SetFlags(voice, false, defaultEon != 0);               // a plain note on a voice that played a recipe: the plain flags again
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

    /// <summary>Sets this DSP voice's bits in the echo, noise and pitch-modulation registers: the defaults (echo as the game used it, no noise, no modulation) or the sample's edited flags.</summary>
    private void ApplyMix(int voice, Instruments.SnesOverlay ov, int oi)
    {
        bool echo = snap.Dsp[0x4D] != 0, noise = false, pmod = false;
        if (oi >= 0 && (ov.At(oi).Flags & Instruments.SnesOverlay.HasMix) != 0)
        {
            byte m = ov.At(oi).Mix;
            echo = (m & Instruments.SnesOverlay.MixEcho) != 0; noise = (m & Instruments.SnesOverlay.MixNoise) != 0; pmod = (m & Instruments.SnesOverlay.MixPmod) != 0;
        }
        byte bit = (byte)(1 << voice);
        eonMask = echo ? (byte)(eonMask | bit) : (byte)(eonMask & ~bit);
        nonMask = noise ? (byte)(nonMask | bit) : (byte)(nonMask & ~bit);
        pmonMask = pmod ? (byte)(pmonMask | bit) : (byte)(pmonMask & ~bit);
        dsp.Write(0x4D, eonMask); dsp.Write(0x3D, nonMask); dsp.Write(0x2D, pmonMask);
    }

    public void Update(int voice, int pitch, int volL, int volR)
    {
        if (macros[voice].R != null) { UpdateRecipe(voice, pitch, volL); return; }
        var ov = snap.Overlay;
        if (ov != null && voiceOverlay[voice] >= 0)
        {
            int oi = voiceOverlay[voice];
            pitch = ov.AdjustPitch(oi, pitch); volL = ov.AdjustVolume(oi, volL, false); volR = ov.AdjustVolume(oi, volR, true);
        }
        int r = voice * 16;
        dsp.Write(r, (byte)Math.Clamp(volL, -128, 127));
        dsp.Write(r + 1, (byte)Math.Clamp(volR, -128, 127));
        dsp.Write(r + 2, (byte)(pitch & 0xFF));
        dsp.Write(r + 3, (byte)((pitch >> 8) & 0x3F));
    }

    public void NoteOff(int voice)
    {
        EndMacro(voice);
        koff |= (byte)(1 << voice);
        dsp.Write(KoffReg, koff);
        pendingKon &= (byte)~(1 << voice);
    }

    // ---- recipes: a voice that plays what the game's driver did ----

    private void SetFlags(int voice, bool noise, bool echo)
    {
        byte bit = (byte)(1 << voice);
        byte n = (byte)(noise ? nonMask | bit : nonMask & ~bit), e = (byte)(echo ? eonMask | bit : eonMask & ~bit);
        if (n != nonMask) { nonMask = n; dsp.Write(0x3D, n); }
        if (e != eonMask) { eonMask = e; dsp.Write(0x4D, e); }
        byte pm = (byte)(pmonMask & ~bit);                         // pitch modulation needs the neighbouring voice's output: a recipe voice does not replay it
        if (pm != pmonMask) { pmonMask = pm; dsp.Write(0x2D, pm); }
    }

    private void EndMacro(int voice)
    {
        var m = macros[voice];
        if (m.R == null) return;
        m.R = null;
        macroActive--;
    }

    private void WritePitch(int voice, Macro m)
    {
        int p = (int)Math.Clamp(Math.Round(m.BasePitch * Math.Pow(2.0, m.Cents / 1200.0)), 1, 0x3FFF);
        int r = voice * 16;
        dsp.Write(r + 2, (byte)(p & 0xFF));
        dsp.Write(r + 3, (byte)((p >> 8) & 0x3F));
    }

    private void WriteVolume(int voice, Macro m)
    {
        int r = voice * 16;
        dsp.Write(r, (byte)Math.Clamp((int)Math.Round(m.VolL * m.Scale), -128, 127));
        dsp.Write(r + 1, (byte)Math.Clamp((int)Math.Round(m.VolR * m.Scale), -128, 127));
    }

    /// <summary>Starts a voice on a recipe: the key-on settings the game's driver used, then its macro, with <paramref name="pitch"/> (the register value of the FL note, the recipe's own pitch being the root key) and
    /// <paramref name="level"/> (0 to 127, FL's velocity) applied on top. The recipe's loudest moment is as loud as a plain instrument at the same level.</summary>
    public void NoteOnRecipe(int voice, SpcRecipe recipe, int pitch, int level)
    {
        var m = macros[voice];
        if (m.R == null) macroActive++;
        m.R = recipe; m.Next = 0; m.Age = 0; m.BasePitch = pitch; m.Cents = 0; m.Level = level;
        m.Scale = level / 127.0 * 127.0 / recipe.PeakVolume;
        m.VolL = recipe.VolL; m.VolR = recipe.VolR;
        SetFlags(voice, recipe.Non, recipe.Eon);
        if (recipe.Non) dsp.Write(0x6C, (byte)((flgBase & 0xE0) | recipe.NoiseRate));        // the noise rate is one setting for all voices: the newest noise note's
        int r = voice * 16;
        WriteVolume(voice, m);
        WritePitch(voice, m);
        dsp.Write(r + 4, (byte)recipe.Srcn);
        dsp.Write(r + 5, (byte)recipe.Adsr1); dsp.Write(r + 6, (byte)recipe.Adsr2); dsp.Write(r + 7, (byte)recipe.Gain);
        koff &= (byte)~(1 << voice);
        dsp.Write(KoffReg, koff);
        pendingKon |= (byte)(1 << voice);
    }

    private void UpdateRecipe(int voice, int pitch, int level)
    {
        var m = macros[voice];
        m.BasePitch = pitch; m.Level = level;
        m.Scale = level / 127.0 * 127.0 / m.R!.PeakVolume;
        WritePitch(voice, m);
        WriteVolume(voice, m);
    }

    /// <summary>One DSP sample's worth of macro time: the changes whose time has come are written to the voice.</summary>
    private void TickMacros()
    {
        if (macroActive == 0) return;
        for (int v = 0; v < 8; v++)
        {
            var m = macros[v];
            var r = m.R;
            if (r == null) continue;
            var ev = r.Events;
            while (m.Next < ev.Length && ev[m.Next].T <= m.Age)
            {
                var e = ev[m.Next++];
                int reg = v * 16;
                switch (e.Kind)
                {
                    case SpcEv.Pitch: m.Cents = e.Value; WritePitch(v, m); break;
                    case SpcEv.VolL: m.VolL = e.Value; WriteVolume(v, m); break;
                    case SpcEv.VolR: m.VolR = e.Value; WriteVolume(v, m); break;
                    case SpcEv.Adsr1: dsp.Write(reg + 5, (byte)e.Value); break;
                    case SpcEv.Adsr2: dsp.Write(reg + 6, (byte)e.Value); break;
                    case SpcEv.Gain: dsp.Write(reg + 7, (byte)e.Value); break;
                    case SpcEv.Srcn: dsp.Write(reg + 4, (byte)e.Value); break;
                    case SpcEv.Flags: SetFlags(v, (e.Value & 1) != 0, (e.Value & 2) != 0); break;
                }
            }
            m.Age++;
        }
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
        while (left.Available < frames) { TickMacros(); dsp.RunSample(); }
        left.Read(tmpL.AsSpan(0, frames));
        right.Read(tmpR.AsSpan(0, frames));
        for (int i = 0; i < frames; i++) { dest[i * 2] = tmpL[i]; dest[i * 2 + 1] = tmpR[i]; }
    }
}
