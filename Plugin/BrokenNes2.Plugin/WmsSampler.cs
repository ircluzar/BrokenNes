// WideMess milestone 7a: the WideMess sound chip played from FL's notes (chip WMS in Direct mode: its built-in instruments; Instrument Runaway on a WideMess game: the game's own).
using NesEmulator.Plugin;
using NesEmulator.Snes;

namespace BrokenNes2;

/// <summary>
/// A private WideMess sound chip (the S-DSP with voices 8-31 attached and unlocked, <c>DSP_SFC.WideMess.cs</c>) over a <see cref="WmsSnapshot"/>, driven by FL notes: no SPC700 program runs. Notes take the
/// new voices 8-31 (24 at once; the oldest gives way to a 25th) for the built-in instruments and the new voices' directory; the S-DSP's own voices 0-7 (8 at once) for a game's S-DSP-directory samples.
/// The DSP makes 32 kHz stereo, resampled to the host rate. Each instance owns one. Nothing here allocates after the constructor (the constructor runs on the UI thread: Bn2Plugin.PrepareWms / PrepareSamplerFor).
/// </summary>
internal sealed class WmsSampler
{
    public const int WideVoices = 24, SnesVoices = 8, Voices = WideVoices + SnesVoices;
    private const int Kon = 0x4C, Koff = 0x5C, WBank = 0x0C, WDirL = 0x5D, WDirH = 0x6D;
    /// <summary>The level a voice gets for FL's full velocity, of the S-DSP's 127: a 25% pulse is then as loud as the NES chip's (the self-test's level check), and eight notes at full velocity fit the S-DSP's
    /// 16-bit main sum (the spec's: the 32 voices add with saturation, the driver keeps the sum in range).</summary>
    public const int FullLevel = 43;

    private readonly WmsSnapshot snap;
    private readonly DSP_SFC dsp;
    private readonly StreamResampler left = new(), right = new();
    private readonly float[] tmpL, tmpR;
    private readonly float[] one = new float[1];
    private readonly byte[] koffW = [0xFF, 0xFF, 0xFF], pendingKonW = new byte[3];
    private byte koffS = 0xFF, pendingKonS;
    private int bank = -1;
    private readonly int[] table = new int[Voices], wave = new int[Voices];

    public WmsSampler(WmsSnapshot snapshot, int hostRate, int maxBlock = 4096)
    {
        snap = snapshot;
        var low = (byte[])snapshot.Low.Clone();
        dsp = new DSP_SFC(low);
        dsp.AttachWide(snapshot.High);            // shared: a DSP only reads the sound RAM above 64 KB
        dsp.SetWideOn(true);
        dsp.Output = Push;
        tmpL = new float[maxBlock]; tmpR = new float[maxBlock];
        left.SetRates(32000, hostRate); right.SetRates(32000, hostRate);

        // the snapshot's S-DSP state, with nothing sounding: no key-on, nothing muted, no noise or pitch modulation; echo as the game used it (none for the built-in bank)
        for (int i = 0; i < WmsSnapshot.DspSize; i++) dsp.Write(i, snapshot.Dsp[i]);
        dsp.Write(0x6C, (byte)(snapshot.Dsp[0x6C] & 0x3F));
        if (snapshot.Dsp[0x0C] == 0 && snapshot.Dsp[0x1C] == 0) { dsp.Write(0x0C, 0x60); dsp.Write(0x1C, 0x60); }
        dsp.Write(0x2D, 0); dsp.Write(0x3D, 0);
        dsp.Write(0x4D, (byte)(snapshot.Dsp[0x4D] != 0 ? 0xFF : 0));
        for (int v = 0; v < 8; v++) { dsp.Write(v * 16, 0); dsp.Write(v * 16 + 1, 0); }
        dsp.Write(Koff, 0xFF); dsp.Write(Kon, 0);
        // the new voices: the game's directory, its banks' echo as it used it, everything keyed off
        dsp.WriteHigh(WDirL, (byte)(snapshot.WideDirectory >> 8));
        dsp.WriteHigh(WDirH, (byte)(snapshot.WideDirectory >> 16));
        for (int b = 0; b < 3; b++)
        {
            Bank(b);
            bool echo = snapshot.Wide[b * 128 + 0x4D] != 0;
            dsp.WriteHigh(0x2D, 0); dsp.WriteHigh(0x3D, 0); dsp.WriteHigh(0x4D, (byte)(echo ? 0xFF : 0));
            for (int v = 0; v < 8; v++) { dsp.WriteHigh(v * 16, 0); dsp.WriteHigh(v * 16 + 1, 0); }
            dsp.WriteHigh(Koff, 0xFF); dsp.WriteHigh(Kon, 0);
        }
    }

    private void Push(short l, short r)
    {
        one[0] = l * (1f / 32768f); left.Write(one);
        one[0] = r * (1f / 32768f); right.Write(one);
    }

    private void Bank(int b) { if (bank != b) { dsp.WriteHigh(WBank, (byte)b); bank = b; } }

    /// <summary>The sampler's own DSP (tests).</summary>
    internal DSP_SFC Dsp => dsp;
    /// <summary>The snapshot it plays.</summary>
    public WmsSnapshot Snapshot => snap;

    /// <summary>Whether sampler voice <paramref name="voice"/> (0-23 the new voices 8-31, 24-31 the S-DSP's 0-7) is one of the new voices.</summary>
    public static bool IsWide(int voice) => voice < WideVoices;

    /// <summary>Where an instrument plays: the new voices (built-in bank, the new voices' directory) or the S-DSP's own (its directory's BRR samples).</summary>
    public bool NeedsWide(int instrument) => snap.BuiltIn || (uint)instrument >= (uint)snap.Instruments().Count || snap.Instruments()[instrument].WideDir;

    // ---- a note of the built-in bank (Direct mode) ----

    /// <summary>Starts a built-in instrument on new voice <paramref name="voice"/> (0-23): <paramref name="patch"/> is the Instrument parameter, the waveform <paramref name="waveIndex"/> (WmsBank.WaveOf), <paramref name="hz"/> the note, <paramref name="level"/> 0..127.</summary>
    public void NoteOnPatch(int voice, int patch, int waveIndex, double hz, int level)
    {
        var p = WmsBank.Patches[WmsBank.Clamp(patch)];
        int t = WmsBank.IsNoise(waveIndex) ? 0 : WmsBank.TableFor(hz);
        table[voice] = t; wave[voice] = waveIndex;
        int pitch = WmsBank.PitchFor(waveIndex, t, hz);
        WriteWide(voice, Level(level), Level(level), pitch, WmsBank.Entry(waveIndex, t), p.Adsr1, p.Adsr2, 0x7F);
        KeyOn(voice);
    }

    /// <summary>A held built-in note's new pitch and level (its loop stays the one it started on).</summary>
    public void UpdatePatch(int voice, double hz, int level)
    {
        int pitch = WmsBank.PitchFor(wave[voice], table[voice], hz);
        int b = voice >> 3, r = (voice & 7) << 4;
        Bank(b);
        int v = Level(level);
        dsp.WriteHigh(r, (byte)v); dsp.WriteHigh(r + 1, (byte)v);
        dsp.WriteHigh(r + 2, (byte)pitch); dsp.WriteHigh(r + 3, (byte)(pitch >> 8 & 0x3F));
    }

    private static int Level(int level) => Math.Clamp((level * FullLevel + 63) / 127, 0, 127);

    /// <summary>Tests only (a deliberate bug of the self-test): the loop a voice's pitch is worked out for.</summary>
    internal void ForceTable(int voice, int t) => table[voice] = t;

    // ---- a note of a frozen game's instrument (Instrument Runaway) ----

    /// <summary>The pitch register for FL's cents: $1000 plays the sample at its own rate at C5.</summary>
    public static int PitchFor(double cents) => Math.Clamp((int)Math.Round(0x1000 * Math.Pow(2.0, cents / 1200.0)), 1, 0x3FFF);

    /// <summary>Starts instrument <paramref name="instrument"/> (an entry of <see cref="WmsSnapshot.Instruments"/>) on sampler voice <paramref name="voice"/>: a new voice for the new voices' directory, an S-DSP voice (24-31) for the S-DSP's.</summary>
    public void NoteOnSample(int voice, int instrument, double cents, int level)
    {
        var list = snap.Instruments();
        if ((uint)instrument >= (uint)list.Count) return;
        var s = list[instrument];
        byte a1 = 0xFF, a2 = 0xE0, g = 0x7F;
        if (snap.TryEnvelope(s, out byte e1, out byte e2, out byte eg)) { a1 = e1; a2 = e2; g = eg; }
        int pitch = PitchFor(cents);
        if (IsWide(voice)) { WriteWide(voice, level, level, pitch, s.Slot, a1, a2, g); KeyOn(voice); }
        else
        {
            int r = (voice - WideVoices) * 16;
            dsp.Write(r, (byte)Math.Clamp(level, 0, 127)); dsp.Write(r + 1, (byte)Math.Clamp(level, 0, 127));
            dsp.Write(r + 2, (byte)pitch); dsp.Write(r + 3, (byte)(pitch >> 8 & 0x3F));
            dsp.Write(r + 4, (byte)s.Slot);
            dsp.Write(r + 5, a1); dsp.Write(r + 6, a2); dsp.Write(r + 7, g);
            KeyOn(voice);
        }
    }

    /// <summary>A held game note's new pitch and level.</summary>
    public void UpdateSample(int voice, double cents, int level)
    {
        int pitch = PitchFor(cents), v = Math.Clamp(level, 0, 127);
        if (IsWide(voice))
        {
            int b = voice >> 3, r = (voice & 7) << 4;
            Bank(b);
            dsp.WriteHigh(r, (byte)v); dsp.WriteHigh(r + 1, (byte)v);
            dsp.WriteHigh(r + 2, (byte)pitch); dsp.WriteHigh(r + 3, (byte)(pitch >> 8 & 0x3F));
        }
        else
        {
            int r = (voice - WideVoices) * 16;
            dsp.Write(r, (byte)v); dsp.Write(r + 1, (byte)v);
            dsp.Write(r + 2, (byte)pitch); dsp.Write(r + 3, (byte)(pitch >> 8 & 0x3F));
        }
    }

    // ---- voices ----

    private void WriteWide(int voice, int volL, int volR, int pitch, int srcn, byte adsr1, byte adsr2, byte gain)
    {
        int b = voice >> 3, r = (voice & 7) << 4;
        Bank(b);
        dsp.WriteHigh(r, (byte)Math.Clamp(volL, -128, 127)); dsp.WriteHigh(r + 1, (byte)Math.Clamp(volR, -128, 127));
        dsp.WriteHigh(r + 2, (byte)pitch); dsp.WriteHigh(r + 3, (byte)(pitch >> 8 & 0x3F));
        dsp.WriteHigh(r + 4, (byte)srcn);
        dsp.WriteHigh(r + 5, adsr1); dsp.WriteHigh(r + 6, adsr2); dsp.WriteHigh(r + 7, gain);
    }

    private void KeyOn(int voice)
    {
        if (IsWide(voice))
        {
            int b = voice >> 3; byte bit = (byte)(1 << (voice & 7));
            koffW[b] &= (byte)~bit;
            Bank(b); dsp.WriteHigh(Koff, koffW[b]);
            pendingKonW[b] |= bit;
        }
        else
        {
            byte bit = (byte)(1 << (voice - WideVoices));
            koffS &= (byte)~bit;
            dsp.Write(Koff, koffS);
            pendingKonS |= bit;
        }
    }

    public void NoteOff(int voice)
    {
        if (IsWide(voice))
        {
            int b = voice >> 3; byte bit = (byte)(1 << (voice & 7));
            koffW[b] |= bit;
            Bank(b); dsp.WriteHigh(Koff, koffW[b]);
            pendingKonW[b] &= (byte)~bit;
        }
        else
        {
            byte bit = (byte)(1 << (voice - WideVoices));
            koffS |= bit;
            dsp.Write(Koff, koffS);
            pendingKonS &= (byte)~bit;
        }
    }

    /// <summary>The key-ons of this block take effect (the DSP keeps one pending key-on register per bank).</summary>
    private void FlushKon()
    {
        for (int b = 0; b < 3; b++) if (pendingKonW[b] != 0) { Bank(b); dsp.WriteHigh(Kon, pendingKonW[b]); pendingKonW[b] = 0; }
        if (pendingKonS != 0) { dsp.Write(Kon, pendingKonS); pendingKonS = 0; }
    }

    /// <summary>How many of the new voices sound right now (their envelope above zero): tests and the window.</summary>
    public int WideSounding
    {
        get
        {
            int n = 0;
            for (int v = 0; v < WideVoices; v++) { Bank(v >> 3); if (dsp.ReadHigh(((v & 7) << 4) + 8) != 0) n++; }
            return n;
        }
    }

    /// <summary>S-DSP samples this sampler has run.</summary>
    internal long SamplesRun { get; private set; }

    /// <summary>Fills <paramref name="dest"/> with <paramref name="frames"/> interleaved stereo frames at the host rate.</summary>
    public void Render(Span<float> dest, int frames)
    {
        FlushKon();
        while (left.Available < frames) { dsp.RunSample(); SamplesRun++; }
        left.Read(tmpL.AsSpan(0, frames));
        right.Read(tmpR.AsSpan(0, frames));
        for (int i = 0; i < frames; i++) { dest[i * 2] = tmpL[i]; dest[i * 2 + 1] = tmpR[i]; }
    }
}
