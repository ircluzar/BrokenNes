// The music pipeline's presets (docs/projects/flp-music-pipeline.md section 9): the instrument a BrokenNes2 instance plays from its PRS1 section (a Runaway preset: Presets/PresetFile.cs) -> one
// sample a game engine can ship. The shared reader (FlpSong / flpsong.mjs) already carries each instance's preset file byte for byte (FlpBn2.Preset); this turns it into sound, in C# only (the
// canonical JSON is unchanged: the twins stay identical).
//
// What a preset stores: the instrument as it is now (INS1, InstrumentCodec: for the SNES the BRR bytes at their audio-RAM address, the loop pointer, ADSR / GAIN, tuning, volumes, mix; for the
// Genesis an FM patch's registers or a DAC sample; PSG / Game Boy / NES registers), the clean one (CLN1), the corruption history with seeds (HIS1), the context an engine needs (CTX1: the SNES's
// 128 S-DSP registers + any audio RAM a corrupted sample plays into; Sega clocks; Game Boy CGB flag) and the learned note behaviour (NOTE). Never a recording: the sound is rebuilt.
//
// The sample (32 kHz, 16-bit mono, an optional loop from LoopStart to the end, C5 = the rate that sounds at MIDI 60 as the plugin plays it):
//   SNES sample   EXACT: the BRR walked the way the S-DSP walks it (its own bytes, then the loop pointer, into the audio RAM the preset kept for a corrupted sample), decoded with the DSP's own
//                 arithmetic; C5 = 32000 x the pitch register the plugin keys at middle C ($1000 x tuning) / $1000. The ADSR / GAIN envelope is not in the sample (the engine's own envelope plays).
//   anything else RENDERED: the plugin's own engine for the preset (the sampler Runaway uses) plays middle C for 1 s at 33488 Hz (128 x 261.63 Hz); the last 10 periods (1280 samples) loop.
using BrokenNes2.Instruments;
using BrokenNes2.Presets;
using NesEmulator.Sega;

namespace BrokenNes2.Music.Flp;

/// <summary>Deliberate bugs for FruityHost flp-test (bits above the Super VRUN back end's).</summary>
public static class PsMutants { public const int LoopIgnored = 4096, TuningIgnored = 8192, PresetFenceBlind = 16384, MdPresetAsPulse = 32768, GbPresetIgnored = 65536, NesPresetIgnored = 131072, ApproxStepsDropped = 4194304, ApproxEnvelopeFlat = 8388608; }

/// <summary>A preset as one sample: <see cref="Pcm"/> at <see cref="Rate"/>, looping from <see cref="LoopStart"/> to the end (-1: plays once), sounding middle C (MIDI 60) when played at <see cref="C5"/> Hz.</summary>
public sealed class FlpPresetPcm
{
    public const int Rate = 32000;
    public short[] Pcm = [];
    public int LoopStart = -1;
    public double C5 = Rate;
    /// <summary>The rate the PCM was made at (32000 for a SNES sample; a rendered one is made at 128 x middle C, so its loop is whole periods and whole BRR blocks).</summary>
    public int SampleRate = Rate;
    public ConsoleId Console;
    public InstrumentKind Kind;
    public string Name = "", How = "";
    public bool Exact;
    /// <summary>SNES: the instrument's envelope registers (ADSR1, ADSR2, GAIN) and volumes, said in the fence (the engines play their own envelopes).</summary>
    public string Envelope = "";
    public bool Corrupted;
    /// <summary>SNES: the voice flags the preset carries (the S-DSP's noise generator in place of the sample, pitch modulation by the previous voice, echo): a song's instrument has none of them.</summary>
    public bool Noise, Pmod, Echo;
    /// <summary>The BRR bytes the sample becomes on the SNES (16 samples per 9-byte block).</summary>
    public int BrrBytes => (Pcm.Length + 15) / 16 * 9;
    public bool Loops => LoopStart >= 0;
}

public static class FlpPresetSound
{
    public const int MaxSamples = 65536, RenderRate = 33488, RenderSamples = RenderRate, LoopSamples = 1280;      // 33488 Hz = 128 x 261.63 (0.002 cents): 10 periods of 128 samples loop, 80 BRR blocks
    public const double MiddleC = 261.6255653005986;
    /// <summary>The Presets chips (Bn2Consoles.cs Cx.PresetChips: PRESETS, PRE-NES, PRE-GB, PRE-SNES, PRE-SMS, PRE-MD at chip indexes 23-28, append-only; the plugin's `presets` check pins them).</summary>
    public const int FirstPresetChip = 23, LastPresetChip = 28;

    /// <summary>The preset's sound, or null with the reason (a damaged preset, one that cannot play).</summary>
    public static FlpPresetPcm? Of(FlpPreset p, out string? why)
    {
        why = null;
        if (!p.Ok) { why = p.Problem; return null; }
        var read = PresetFile.Read(p.File);
        if (read.Data is not { } d) { why = read.Problem; return null; }
        var play = PresetPlayable.Build(d, out string? problem);
        if (play == null) { why = problem; return null; }
        if (play.CannotPlay != null) { why = play.CannotPlay; return null; }
        var o = new FlpPresetPcm { Console = play.Console, Kind = play.Kind, Name = d.Name.Length > 0 ? d.Name : d.InstrumentName, Corrupted = d.Corrupted };
        try
        {
            if (play.Kind == InstrumentKind.SnesSample) Snes(play, o);
            else Render(play, o);
        }
        catch (Exception e) { why = "its engine failed: " + e.Message; return null; }
        if (o.Pcm.Length == 0) { why = "it makes no sound at middle C"; return null; }
        return o;
    }

    // ---- SNES: the BRR walk ----

    private static void Snes(PresetPlayable play, FlpPresetPcm o)
    {
        var snap = play.Snes!;
        var inst = play.Instrument;
        int srcn = play.InstrumentValue, start = snap.SampleStart(srcn), loop = snap.SampleLoop(srcn);
        var aram = snap.Aram;
        var pcm = new List<short>(4096);
        // (block address, the filter's two previous samples when the block's filter reads them) -> where the block starts in the PCM: decoding until that state comes back gives a loop
        // that repeats EXACTLY (a filtered block after a loop decodes differently on each pass until the filter's state repeats); a state that does not come back within UnrollMax
        // samples of the first jump back is cut at that first jump (the loop then repeats the first pass: said in How)
        const int UnrollMax = 16384;
        var at = new Dictionary<(int, int, int), int>();
        var first = new Dictionary<int, int>();       // block address -> where it was first decoded
        int p1 = 0, p2 = 0, pos = start & 0xFFFF, firstBack = -1, firstTarget = -1;
        bool cut = false;
        o.LoopStart = -1;
        while (pcm.Count < MaxSamples)
        {
            var key = (aram[pos] & 0x0C) != 0 ? (pos, p1, p2) : (pos, 0, 0);
            if (at.TryGetValue(key, out int seen)) { o.LoopStart = seen; break; }
            if (first.TryGetValue(pos, out int f0)) { if (firstBack < 0) { firstBack = pcm.Count; firstTarget = f0; } }
            else first[pos] = pcm.Count;
            if (firstBack >= 0 && pcm.Count - firstBack > UnrollMax)
            {
                pcm.RemoveRange(firstBack, pcm.Count - firstBack);
                o.LoopStart = firstTarget;
                cut = true;
                break;
            }
            at[key] = pcm.Count;
            int header = aram[pos], shift = header >> 4, filter = header & 0x0C;
            for (int k = 0; k < 8; k++)
            {
                int bytev = aram[(pos + 1 + k) & 0xFFFF];
                for (int h = 0; h < 2; h++)
                {
                    int nib = h == 0 ? bytev >> 4 : bytev & 15;
                    int s = (nib ^ 8) - 8;
                    s = (s << shift) >> 1;
                    if (shift >= 0x0D) s = (s >> 25) << 11;
                    int q1 = p1, q2 = p2 >> 1;
                    if (filter >= 8)
                    {
                        s += q1 - q2;
                        if (filter == 8) { s += q2 >> 4; s += (q1 * -3) >> 6; }
                        else { s += (q1 * -13) >> 7; s += (q2 * 3) >> 4; }
                    }
                    else if (filter != 0) { s += q1 >> 1; s += (-q1) >> 5; }
                    s = (short)(Math.Clamp(s, -32768, 32767) * 2);
                    p2 = p1; p1 = s;
                    pcm.Add((short)s);
                }
            }
            if ((header & 1) != 0)
            {
                if ((header & 2) == 0) break;                                     // end, no loop: the voice stops
                pos = loop & 0xFFFF;
            }
            else pos = (pos + 9) & 0xFFFF;
        }
        if (FlpMutants.On(PsMutants.LoopIgnored)) o.LoopStart = -1;
        o.Pcm = pcm.ToArray();
        int reg = FlpMutants.On(PsMutants.TuningIgnored) ? 0x1000 : new SnesSampler(snap, FlpPresetPcm.Rate).PitchOf(srcn, 0);
        o.C5 = FlpPresetPcm.Rate * reg / 4096.0;
        o.Exact = true;
        o.How = $"its BRR sample, decoded as the S-DSP plays it ({first.Count} blocks{(o.Loops ? $", loop at sample {o.LoopStart}" : ", no loop")}{(firstBack >= 0 && !cut && o.Loops && pcm.Count > firstBack ? $", the loop unrolled {pcm.Count - firstBack} samples until the BRR filter repeats" : "")}{(cut ? ", the loop's filter never repeats: the first pass loops (near, not exact)" : "")}{(play.Overrun > 0 ? $", {play.Overrun} bytes of the game's audio RAM it plays into" : "")})";
        int adsr1 = inst.Get("env.adsr") << 7 | inst.Get("env.dr") << 4 | inst.Get("env.ar"), adsr2 = inst.Get("env.sl") << 5 | inst.Get("env.sr"), gain = inst.Get("gain.mode") << 7 | inst.Get("gain.curve") << 5 | inst.Get("gain.rate");
        o.Noise = inst.Get("mix.noise") != 0; o.Pmod = inst.Get("mix.pmod") != 0; o.Echo = inst.Get("mix.echo") != 0;
        o.Envelope = (adsr1 & 0x80) != 0 ? $"ADSR ${adsr1:X2} ${adsr2:X2}" : $"GAIN ${gain:X2}";
    }

    // ---- everything else: the plugin's engine plays middle C ----

    private static void Render(PresetPlayable play, FlpPresetPcm o)
    {
        const int rate = RenderRate, block = 256;
        var mono = new float[RenderSamples];
        double hz = MiddleC;
        switch (play.Console)
        {
            case ConsoleId.GameBoy:
            {
                var s = new GbSampler(play.Gb!, rate);
                s.NoteOn(play.GbChannel, hz, 15, -1, play.GbNoiseShort);
                Stereo(mono, (b, n) => s.Render(b, n), block);
                break;
            }
            case ConsoleId.Nes:
                // the NES preset player lives in the plugin (NesPresetPlayer reads the plugin's voice state): not rendered here; the caller's fence says so
                throw new NotSupportedException("a NES preset is played by the plugin's own NES register player, which the pipeline cannot run (use a plain NES instance: the game's NES sounds)");
            default:
            {
                var snap = play.Sega!;
                SegaSampler s = snap.Kind switch { SegaSnapshot.KindOpn => new OpnSampler(snap, rate), SegaSnapshot.KindOpll => new OpllSampler(snap, rate), _ => new PsgSampler(snap, rate) };
                int instrument = snap.Resolve(play.InstrumentValue);
                s.NoteOn(snap.SlotBase(instrument), instrument, hz, 1.0);
                Stereo(mono, (b, n) => s.Render(b, n), block);
                break;
            }
        }
        float peak = 0;
        foreach (var v in mono) peak = Math.Max(peak, Math.Abs(v));
        if (peak < 1e-4f) return;
        float g = 0.9f / peak;
        o.Pcm = mono.Select(v => (short)Math.Clamp((int)Math.Round(v * g * 32767), -32768, 32767)).ToArray();
        o.LoopStart = FlpMutants.On(PsMutants.LoopIgnored) ? -1 : o.Pcm.Length - LoopSamples;
        o.C5 = rate; o.SampleRate = rate;
        o.How = $"the plugin's own {play.Console} engine playing middle C for 1 s (the last {LoopSamples} samples, 10 periods of 128, loop)";
    }

    private static void Stereo(float[] mono, Action<Span<float>, int> render, int block)
    {
        var st = new float[block * 2];
        for (int off = 0; off < mono.Length; off += block)
        {
            int n = Math.Min(block, mono.Length - off);
            render(st, n);
            for (int i = 0; i < n; i++) mono[off + i] = 0.5f * (st[2 * i] + st[2 * i + 1]);
        }
    }

    /// <summary>What the plugin itself plays for middle C held <paramref name="seconds"/> (mono, 32 kHz): the reference a game's playback is compared against.</summary>
    public static float[] PluginRender(FlpPreset p, double seconds)
    {
        var d = PresetFile.Read(p.File).Data!;
        var play = PresetPlayable.Build(d, out _)!;
        var mono = new float[(int)(seconds * FlpPresetPcm.Rate)];
        if (play.Console == ConsoleId.Snes)
        {
            var s = new SnesSampler(play.Snes!, FlpPresetPcm.Rate);
            s.NoteOnCents(0, play.InstrumentValue, 0, 127, 127);
            Stereo(mono, (b, n) => s.Render(b, n), 256);
            return mono;
        }
        var o = new FlpPresetPcm();
        Render(play, o);
        return o.Pcm.Select(v => v / 32768f).Take(mono.Length).ToArray();
    }

    /// <summary>A 16-bit mono WAV with a "smpl" chunk for the loop (sf2it.py reads the PCM and the loop from it).</summary>
    public static byte[] Wav(FlpPresetPcm s)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int data = s.Pcm.Length * 2, smpl = s.Loops ? 36 + 24 : 0;
        w.Write("RIFF"u8); w.Write(4 + 8 + 16 + 8 + data + (smpl > 0 ? 8 + smpl : 0)); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(s.SampleRate); w.Write(s.SampleRate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(data); foreach (var v in s.Pcm) w.Write(v);
        if (smpl > 0)
        {
            w.Write("smpl"u8); w.Write(smpl);
            w.Write(0); w.Write(0); w.Write(1_000_000_000 / s.SampleRate); w.Write(60); w.Write(0); w.Write(0); w.Write(0); w.Write(1); w.Write(0);
            w.Write(0); w.Write(0); w.Write(s.LoopStart); w.Write(s.Pcm.Length - 1); w.Write(0); w.Write(0);
        }
        w.Flush();
        return ms.ToArray();
    }
}
