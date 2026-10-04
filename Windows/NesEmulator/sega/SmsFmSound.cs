using System;
using System.IO;

namespace NesEmulator.Sega;

/// <summary>
/// The Japanese Master System / Mark III sound unit: the SN76489 and the YM2413 behind ports <c>$F0</c> (register address), <c>$F1</c> (register data) and <c>$F2</c> (audio control), mixed
/// to stereo. A console without the FM chip is <see cref="SmsSound"/> (<see cref="HasFm"/> false); the board only routes the three ports when a unit says it has the chip.
/// </summary>
/// <remarks>
/// <para><b>Audio control ($F2).</b> Bit 0 enables the FM output; bit 1 mutes the PSG. Both are 0 at power-on (PSG only). The detection routine every FM game runs (Phantasy Star's: I/O chip off, write 0 and 1 to $F2 seven times,
/// read it back each time) gets <see cref="ReadAudioControl"/>, the value written masked to bits 0-1, while the board lets it through (the I/O chip off; see BOARD_SMS). That read-back and the register file a game builds
/// through $F0/$F1 were measured against the oracle (Mesen 2.1.1 with its FM chip): the probes, and four Japanese games' register files frame by frame, are in the record (docs/projects/sega-track-a.md).
/// What the two bits do to the MIX is the convention above (bit 0 FM on, bit 1 PSG off): the oracle shows its audio control register but its audio cannot be captured here, so the mix is not compared with a recording.</para>
/// <para><b>Clocks.</b> The YM2413 runs from the Z80 clock: one chip sample per 72 clocks (49,716 Hz on an NTSC console, 49,262 Hz PAL), converted to the host rate by <see cref="Ym2413Resampler"/> (the converter of emu2413).
/// A register write lands on the next chip sample boundary, which is at most 72 Z80 clocks (20 microseconds) after the Z80's write; the chip's state is brought to the Z80's time before every write by the board.</para>
/// <para><b>Mix.</b> The PSG's band-limited stereo output and the FM output (mono, centre) are added at the host rate. <see cref="FmGain"/> and <see cref="PsgGain"/> scale them; the defaults put a full-volume FM channel
/// (peak 2,042 of 32,768) a little under a full-volume PSG tone (peak 3,277), the level the two chips have on the real console to within the accuracy measured in the record.</para>
/// <para>Per-channel stems: <see cref="Fm"/>.<see cref="Ym2413.Mask"/> mutes FM voices, the PSG's volume registers silence its voices; nothing here allocates per sample.</para>
/// </remarks>
public sealed class SmsFmSound : ISmsSound
{
    /// <summary>Gain of the FM part in the mix (1.0 is the chip's own scale: a full-volume channel peaks at about 2,042 of 32,768).</summary>
    public float FmGain = 1.0f;
    /// <summary>Gain of the PSG part in the mix.</summary>
    public float PsgGain = 1.0f;

    private readonly Sn76489 psg;
    private readonly Ym2413 fm;
    private readonly Ym2413Resampler resampler;
    private readonly int sampleRate;

    private int audioControl;            // $F2 as written (bits 0 and 1)
    private int fmRemainder;             // Z80 clocks toward the next chip sample

    // output queues at the host rate
    private const int QueueMask = 16383;
    private readonly short[] psgL = new short[QueueMask + 1], psgR = new short[QueueMask + 1];
    private long psgRead, psgWrite;
    private readonly short[] fmQ = new short[QueueMask + 1];
    private long fmRead, fmWrite;
    private readonly short[] drain = new short[1024];   // 512 stereo frames: the largest the PSG reads without allocating

    /// <param name="cpuClockHz">The Z80 clock of the machine.</param>
    /// <param name="sampleRate">The host audio rate.</param>
    /// <param name="variant">The PSG part (Sega on every console that has an FM chip).</param>
    public SmsFmSound(double cpuClockHz, int sampleRate, PsgVariant variant = PsgVariant.Sega)
    {
        this.sampleRate = sampleRate;
        psg = new Sn76489(cpuClockHz, sampleRate, variant);
        fm = new Ym2413(cpuClockHz);
        resampler = new Ym2413Resampler(cpuClockHz / Ym2413.ClocksPerSample, sampleRate);
        Reset();
    }

    public string CoreName => "SN76489 + YM2413";
    public int SampleRate => sampleRate;
    public Sn76489 Psg => psg;
    public Ym2413 Fm => fm;

    public bool HasFm => true;
    /// <summary>FM is heard (bit 0 of the audio control).</summary>
    public bool FmEnabled => (audioControl & 1) != 0;
    /// <summary>PSG is heard (bit 1 of the audio control is clear).</summary>
    public bool PsgEnabled => (audioControl & 2) == 0;
    public int AudioControl => audioControl;

    public void Reset()
    {
        psg.Reset(); fm.Reset(); fm.ResetPatch(); resampler.Reset();
        audioControl = 0; fmRemainder = 0;
        psgRead = psgWrite = fmRead = fmWrite = 0;
    }

    public void Run(int cpuClocks)
    {
        psg.Run(cpuClocks);
        int n;
        while ((n = psg.ReadSamples(drain)) > 0)
            for (int i = 0; i + 1 < n; i += 2)
            {
                psgL[psgWrite & QueueMask] = drain[i]; psgR[psgWrite & QueueMask] = drain[i + 1]; psgWrite++;
            }
        fmRemainder += cpuClocks;
        while (fmRemainder >= Ym2413.ClocksPerSample)
        {
            fmRemainder -= Ym2413.ClocksPerSample;
            int s = fm.Calc();
            resampler.Push((short)Math.Clamp(s, short.MinValue, short.MaxValue));
            while (resampler.TryRead(out short o)) { fmQ[fmWrite & QueueMask] = o; fmWrite++; }
        }
        // a stalled reader must not make the queues wrap over unread samples: drop the oldest, counted
        if (psgWrite - psgRead > QueueMask) { Overflowed += psgWrite - psgRead - QueueMask; psgRead = psgWrite - QueueMask; }
        if (fmWrite - fmRead > QueueMask) { fmRead = fmWrite - QueueMask; }
    }

    /// <summary>Samples dropped because nothing read the queue (a probe that never drains the unit).</summary>
    public long Overflowed { get; private set; }

    public void WritePsg(byte value) => psg.Write(value);
    public void WriteStereo(byte value) => psg.WriteStereo(value);

    public void WriteFmAddress(byte value) => fm.WriteAddress(value);
    public void WriteFmData(byte value) => fm.WriteData(value);
    public void WriteAudioControl(byte value) => audioControl = value & 3;
    public byte ReadAudioControl() => (byte)audioControl;

    public int ReadSamples(Span<short> dest)
    {
        long frames = Math.Min(Math.Min(psgWrite - psgRead, fmWrite - fmRead), dest.Length / 2);
        bool useFm = FmEnabled, usePsg = PsgEnabled;
        for (long i = 0; i < frames; i++)
        {
            long a = (psgRead + i) & QueueMask, b = (fmRead + i) & QueueMask;
            float l = usePsg ? psgL[a] * PsgGain : 0f, r = usePsg ? psgR[a] * PsgGain : 0f;
            if (useFm) { float f = fmQ[b] * FmGain; l += f; r += f; }
            dest[(int)(2 * i)] = (short)Math.Clamp(l, -32768f, 32767f);
            dest[(int)(2 * i + 1)] = (short)Math.Clamp(r, -32768f, 32767f);
        }
        psgRead += frames; fmRead += frames;
        return (int)(frames * 2);
    }

    /// <summary>The four PSG voices, then the YM2413's fourteen (nine melodic channels and the five rhythm voices; see <see cref="Ym2413.DescribeVoices"/>). A muted half reports silent voices.</summary>
    public int DescribeVoices(Span<HubVoice> voices)
    {
        int n = psg.DescribeVoices(voices);
        if (!PsgEnabled) for (int i = 0; i < n; i++) voices[i] = voices[i] with { Level = 0f };
        if (voices.Length < n + Ym2413.VoiceCount) return n;
        int m = fm.DescribeVoices(voices.Slice(n, Ym2413.VoiceCount));
        if (!FmEnabled) for (int i = 0; i < m; i++) voices[n + i] = voices[n + i] with { Level = 0f };
        return n + m;
    }

    public void SaveState(BinaryWriter w)
    {
        psg.SaveState(w); fm.SaveState(w); resampler.SaveState(w);
        w.Write(audioControl); w.Write(fmRemainder);
        // the samples the host has not read yet, so that a restored unit keeps the offset between the PSG and FM streams (the resampler's delay) and its next samples are the carried-on unit's, bit for bit
        int np = (int)(psgWrite - psgRead), nf = (int)(fmWrite - fmRead);
        w.Write(np);
        for (long i = 0; i < np; i++) { w.Write(psgL[(psgRead + i) & QueueMask]); w.Write(psgR[(psgRead + i) & QueueMask]); }
        w.Write(nf);
        for (long i = 0; i < nf; i++) w.Write(fmQ[(fmRead + i) & QueueMask]);
    }

    public void LoadState(BinaryReader r)
    {
        psg.LoadState(r);
        if (r.BaseStream.CanSeek && r.BaseStream.Position >= r.BaseStream.Length)
        {
            // the state of a PSG-only unit (an APU swap from the stock unit): the PSG goes on as it was, the FM chip starts as at power-on
            fm.Reset(); fm.ResetPatch(); resampler.Reset(); audioControl = 0; fmRemainder = 0;
            psgRead = psgWrite = fmRead = fmWrite = 0;
            return;
        }
        fm.LoadState(r); resampler.LoadState(r);
        audioControl = r.ReadInt32(); fmRemainder = r.ReadInt32();
        psgRead = psgWrite = fmRead = fmWrite = 0;
        int np = r.ReadInt32();
        if (np < 0 || np > QueueMask + 1) throw new InvalidDataException("SMS-FM state: bad PSG queue length");
        for (int i = 0; i < np; i++) { psgL[i] = r.ReadInt16(); psgR[i] = r.ReadInt16(); }
        psgWrite = np;
        int nf = r.ReadInt32();
        if (nf < 0 || nf > QueueMask + 1) throw new InvalidDataException("SMS-FM state: bad FM queue length");
        for (int i = 0; i < nf; i++) fmQ[i] = r.ReadInt16();
        fmWrite = nf;
    }

    public string Describe() =>
        $"SN76489 ({psg.Variant}) + YM2413: tones {psg.Period(0)}/{psg.Period(1)}/{psg.Period(2)} volumes {psg.Volume(0)}/{psg.Volume(1)}/{psg.Volume(2)}/{psg.Volume(3)} noise {psg.NoiseControl} | {fm.Describe()} | audio control ${audioControl:X2} (FM {(FmEnabled ? "on" : "off")}, PSG {(PsgEnabled ? "on" : "muted")})";
}
