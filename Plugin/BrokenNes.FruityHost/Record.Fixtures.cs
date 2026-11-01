// Round 8 (record): what the record checks stand on. A BRR encoder and samples of KNOWN pitch; a synthetic sound driver that writes to a fake S-DSP at a known tempo (with its own list of the notes it
// played: the truth the transcription is held to); real games run headless with the capture attached (and an independent count of the S-DSP writes beside it); and a replay of a MIDI file the way
// FL would hand it to the plugin, written from the MIDI bytes alone (VRUN's own MIDI parser reads them), so the check never trusts the transcription's own idea of what it wrote.
using BrokenNes2;
using BrokenNes2.Presets;
using BrokenNes2.Record;
using NesEmulator.Systems;
using Vrun.Audio;

namespace BrokenNes.FruityHost;

internal static class RecBrr
{
    /// <summary>Encodes 16-bit PCM (a multiple of 16 samples) as BRR with filter 0, the smallest range that holds each block; the last block gets the end flag (and the loop flag when <paramref name="loops"/>).</summary>
    public static byte[] Encode(short[] pcm, bool loops)
    {
        int blocks = pcm.Length / 16;
        var o = new byte[blocks * 9];
        for (int b = 0; b < blocks; b++)
        {
            int max = 0;
            for (int i = 0; i < 16; i++) max = Math.Max(max, Math.Abs((int)pcm[b * 16 + i]));
            int range = 0;
            while (range < 12 && max > 7 * (1 << range)) range++;
            byte h = (byte)(range << 4);
            if (b == blocks - 1) h |= (byte)(loops ? 3 : 1);
            o[b * 9] = h;
            for (int i = 0; i < 16; i += 2)
            {
                int n0 = Math.Clamp((int)Math.Round(pcm[b * 16 + i] / (double)(1 << range)), -8, 7), n1 = Math.Clamp((int)Math.Round(pcm[b * 16 + i + 1] / (double)(1 << range)), -8, 7);
                o[b * 9 + 1 + i / 2] = (byte)((n0 & 15) << 4 | (n1 & 15));
            }
        }
        return o;
    }

    /// <summary>A looping sample of period <paramref name="period"/> samples (fractional allowed), <paramref name="loopSamples"/> long (a multiple of 16 holding a whole number of periods), of shape: 0 sine, 1 saw, 2 square, 3 weak fundamental (harmonics 0.25, 1, 0.5).</summary>
    public static short[] Wave(double period, int loopSamples, int shape, double amp = 12000)
    {
        var p = new short[loopSamples];
        for (int i = 0; i < loopSamples; i++)
        {
            double ph = (i / period) % 1.0, x = 2 * Math.PI * ph;
            double v = shape switch
            {
                0 => Math.Sin(x),
                1 => 2 * ph - 1,
                2 => ph < 0.5 ? 0.8 : -0.8,
                _ => (0.25 * Math.Sin(x) + Math.Sin(2 * x) + 0.5 * Math.Sin(3 * x)) / 1.6,
            };
            p[i] = (short)Math.Round(v * amp);
        }
        return p;
    }
}

/// <summary>One note the synthetic driver played: the truth the extraction and the transcription are held to.</summary>
internal sealed class SynthNote
{
    public int Voice, Srcn, Key, Instrument;
    public uint Start, End;
    public List<(uint T, int P)> Pitches = new();
    public List<(uint T, int L, int R)> Vols = new();
}

/// <summary>
/// A sound driver written here, playing on a fake S-DSP: a directory at $0200 with four instruments of known pitch (at $1000 an A of 500 Hz at $1000 = saw of period 64, B 250 Hz square, C 1000 Hz sine, and later
/// D 400 Hz sine loaded under B's sample number), notes on a 16th grid at a known tempo, quantised to the driver's 2 ms tick: a melody with vibrato (A, voice 0), two-note chords with slides
/// (B, voices 1 and 2), a panned bass with volume fades (C, voice 3), and the melody doubled an octave down now and then (A, voice 4: the same instrument twice at once). Every write goes
/// to the capture (and to a counter beside it, which the capture's count must equal).
/// </summary>
internal sealed class SynthDriver
{
    public const int Dir = 0x0200;
    public static readonly (int Srcn, int Addr, double Period, int Loop, int Shape, double DriverCents)[] Instruments =
    [
        (3, 0x1000, 64, 64, 1, 23),        // A: 500 Hz saw, the driver tunes it 23 cents sharp
        (7, 0x1400, 128, 128, 2, -37),     // B: 250 Hz square, 37 cents flat
        (9, 0x1800, 32, 64, 0, 0),         // C: 1000 Hz sine
        (7, 0x1C00, 80, 80, 0, 12),        // D: 400 Hz sine, loaded under B's number at 12 s
    ];

    public readonly byte[] Aram = new byte[0x10000];
    public readonly byte[] Regs = new byte[128];
    public readonly List<SynthNote> Notes = new();
    public long Probe;
    public double Bpm;
    private readonly List<(long T, int Seq, int Reg, int Val, int AramAddr)> plan = new();
    private int seq;
    private readonly Random rng;

    public SynthDriver(double bpm, int seed = 7)
    {
        Bpm = bpm;
        rng = new Random(seed);
        Regs[0x5D] = Dir >> 8; Regs[0x0C] = 0x7F; Regs[0x1C] = 0x7F; Regs[0x6C] = 0x20;
        for (int i = 0; i < 3; i++) Put(i);
        // instrument D's bytes are in audio RAM from the start; its directory entry takes B's place at 12 s
        var d = Instruments[3];
        var brr = RecBrr.Encode(RecBrr.Wave(d.Period, d.Loop, d.Shape), true);
        brr.CopyTo(Aram, d.Addr);
    }

    private void Put(int i)
    {
        var x = Instruments[i];
        var brr = RecBrr.Encode(RecBrr.Wave(x.Period, x.Loop, x.Shape), true);
        brr.CopyTo(Aram, x.Addr);
        int e = Dir + x.Srcn * 4;
        Aram[e] = (byte)x.Addr; Aram[e + 1] = (byte)(x.Addr >> 8); Aram[e + 2] = (byte)x.Addr; Aram[e + 3] = (byte)(x.Addr >> 8);
    }

    public static double F0(int instrument) => 32000.0 / Instruments[instrument].Period;

    /// <summary>The register the driver keys MIDI note <paramref name="key"/> of an instrument with (equal temperament, the driver's own fine tuning), moved by <paramref name="cents"/>.</summary>
    public static int Reg(int instrument, double key, double cents = 0)
    {
        double m0 = 69 + 12 * Math.Log2(F0(instrument) / 440);
        return Math.Clamp((int)Math.Round(4096 * Math.Pow(2, (key - m0) / 12 + (Instruments[instrument].DriverCents + cents) / 1200)), 1, 0x3FFF);
    }

    private long Tick(double seconds) => (long)Math.Round(seconds / 0.002) * 64;      // the driver's 2 ms tick, in S-DSP samples

    private void W(long t, int reg, int val) => plan.Add((t, seq++, reg, val, -1));
    private void A(long t, int addr, int val) => plan.Add((t, seq++, -1, val, addr));

    private void Key(SynthNote n, int instrument, int voice, int key, double start, double end, int l, int r, Func<double, double>? cents = null, Func<double, double>? gain = null)
    {
        n.Voice = voice; n.Srcn = Instruments[instrument].Srcn; n.Instrument = instrument;
        n.Key = key + (int)Math.Round((cents?.Invoke(0) ?? 0) / 100);       // the note it is keyed on (a slide starts below its target)
        long t0 = Tick(start), t1 = Tick(end);
        int b = voice * 16;
        int p0 = Reg(instrument, key, cents?.Invoke(0) ?? 0);
        W(t0, b + 0, (byte)l); W(t0, b + 1, (byte)r);
        W(t0, b + 2, p0 & 0xFF); W(t0, b + 3, p0 >> 8);
        W(t0, b + 4, n.Srcn);
        W(t0, b + 5, 0x8F); W(t0, b + 6, 0xE0);
        W(t0, 0x5C, 0);                                     // (key-off released before the key-on, as drivers do)
        W(t0, 0x4C, 1 << voice);
        n.Start = (uint)t0; n.End = (uint)t1;
        n.Pitches.Add(((uint)t0, p0));
        n.Vols.Add(((uint)t0, l, r));
        int lastP = p0, lastL = l, lastR = r;
        for (long t = t0 + 64 * 2; t < t1; t += 64 * 2)
        {
            double el = (t - t0) / 32000.0;
            if (cents != null)
            {
                int p = Reg(instrument, key, cents(el));
                if (p != lastP) { W(t, b + 2, p & 0xFF); W(t, b + 3, p >> 8); n.Pitches.Add(((uint)t, p)); lastP = p; }
            }
            if (gain != null)
            {
                int nl = (int)Math.Round(l * gain(el)), nr = (int)Math.Round(r * gain(el));
                if (nl != lastL || nr != lastR) { W(t, b + 0, (byte)nl); W(t, b + 1, (byte)nr); n.Vols.Add(((uint)t, nl, nr)); lastL = nl; lastR = nr; }
            }
        }
        W(t1, 0x5C, 1 << voice);
        Notes.Add(n);
    }

    /// <summary>Plays <paramref name="seconds"/> of music into <paramref name="rec"/> (Arm-ed beforehand). Returns the length in S-DSP samples.</summary>
    public long Play(SpcRecorder rec, double seconds, Action<byte, byte, long>? tap = null)
    {
        double beat = 60 / Bpm, s16 = beat / 4;
        // melody (A, voice 0) with vibrato on the long notes; doubled an octave down (voice 4) every fourth bar
        int[] scale = [60, 62, 64, 65, 67, 69, 71, 72, 74, 76];
        int pos = 5;
        for (double t = 0.05 + s16 * 2; t < seconds - 1; )
        {
            int len = rng.Next(1, 5);
            pos = Math.Clamp(pos + rng.Next(-2, 3), 0, scale.Length - 1);
            int key = scale[pos];
            double end = t + len * s16 - 0.01;
            Func<double, double>? vib = len >= 3 ? el => el < 0.06 ? 0 : 30 * Math.Sin(2 * Math.PI * 6 * (el - 0.06)) : null;
            Key(new SynthNote(), 0, 0, key, t, end, 90, 90, vib);
            int bar = (int)((t - 0.05) / (beat * 4));
            if (bar % 4 == 3 && len >= 2) Key(new SynthNote(), 0, 4, key - 12, t, end, 50, 50, vib);
            t += len * s16;
        }
        // chords (B then D from 12 s, voices 1 and 2), every two beats, a slide up a fourth on every third chord
        int chord = 0;
        for (double t = 0.05; t < seconds - 1; t += 2 * beat, chord++)
        {
            int ins = t >= 12 ? 3 : 1;
            if (ins == 3 && chord > 0 && t - 2 * beat < 12)
            {
                // the driver loads D's directory entry under B's sample number (a new instrument, the same number)
                long at = Tick(t) - 64;
                var d = Instruments[3];
                int e = Dir + d.Srcn * 4;
                A(at, e, d.Addr & 0xFF); A(at, e + 1, d.Addr >> 8); A(at, e + 2, d.Addr & 0xFF); A(at, e + 3, d.Addr >> 8);
            }
            int root = 55 + rng.Next(0, 5);
            Func<double, double>? slide = chord % 3 == 2 ? el => el < 0.06 ? -500 * (1 - el / 0.06) : 0 : null;
            Key(new SynthNote(), ins, 1, root, t, t + 1.5 * beat, 70, 70, slide);
            Key(new SynthNote(), ins, 2, root + 4, t, t + 1.5 * beat, 70, 70);
        }
        // bass (C, voice 3): every beat, panned left, fading
        for (double t = 0.05; t < seconds - 1; t += beat)
        {
            int key = 40 + rng.Next(0, 8);
            Key(new SynthNote(), 2, 3, key, t, t + beat * 0.8, 60, 30, null, el => Math.Max(0.2, 1 - el * 1.5));
        }
        plan.Sort((a, b) => a.T != b.T ? a.T.CompareTo(b.T) : a.Seq.CompareTo(b.Seq));
        long length = Tick(seconds);
        rec.Begin(Aram, Regs, 0);
        foreach (var (t, _, reg, val, addr) in plan)
        {
            if (reg < 0) { Aram[addr] = (byte)val; continue; }
            Regs[reg] = (byte)val;
            Probe++;
            rec.OnWrite((byte)reg, (byte)val, t);
            tap?.Invoke((byte)reg, (byte)val, t);
        }
        rec.Clock(length);
        rec.Halt(RecEnd.Stop);
        Notes.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Voice.CompareTo(b.Voice));
        return length;
    }
}

/// <summary>A real game run headless with the capture on its S-DSP writes, and a counter on the S-DSP's own write probe beside it.</summary>
internal sealed class RecGame : IDisposable
{
    public IConsoleSession S = null!;
    public NesEmulator.Snes.APU_SFC Spc = null!;
    public SpcRecorder Rec = new();
    public long Probe;
    public string Name = "", Path = "";
    public byte[] Rom = [];
    public Func<int, int> Pad = _ => 0;
    public int Frame;

    public static RecGame? Make(string path, int parkFrame, string? script)
    {
        var rom = ImodelGames.LoadRom(path, out string name);
        if (rom == null) return null;
        var g = new RecGame { Rom = rom, Name = name, Path = path, Pad = script != null ? InjectRun.Pad(script) : _ => 0 };
        g.S = InjectRun.Create(ConsoleKind.Snes, rom, name);
        g.Spc = (NesEmulator.Snes.APU_SFC)((SnesSession)g.S).Apu;
        InjectRun.Frames(g.S, 0, parkFrame, g.Pad);
        g.Frame = parkFrame;
        return g;
    }

    /// <summary>Records <paramref name="frames"/> frames: the capture armed and begun now, every S-DSP write counted by the DSP's own probe too.</summary>
    public RecSession Record(int frames, string recName, Action<int>? beforeFrame = null)
    {
        Rec = new SpcRecorder();
        Rec.Arm();
        Probe = 0;
        Spc.Dsp.WriteTap = (_, _) => Probe++;
        Spc.DspWriteTap = Rec.OnWrite;
        Rec.Begin(Spc.Aram, Spc.Dsp.Regs, Spc.SamplesProduced);
        var buf = new short[1 << 16];
        for (int f = 0; f < frames; f++)
        {
            beforeFrame?.Invoke(Frame);
            S.SetPad(0, (PadButtons)Pad(Frame));
            S.RunFrame();
            while (S.ReadSamples(buf) > 0) { }
            Frame++;
            Rec.Clock(Spc.SamplesProduced);
            if (f % 60 == 0) Rec.Feed();
        }
        Rec.Halt(RecEnd.Stop);
        Spc.DspWriteTap = null; Spc.Dsp.WriteTap = null;
        return Rec.Finish(Spc.Aram, Spc.Dsp.Regs, recName, Name, Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Rom)).ToLowerInvariant(), 1_760_000_000, 0x5EED);
    }

    public void Dispose() => S.Dispose();
}

/// <summary>A MIDI file as FL would play it into one BrokenNes2 instance per track: read with VRUN's parser (not with anything the transcription wrote), the tempo map from the conductor, each track's channel
/// with its pitch-bend range from its RPN 0, its bends, controller 7 and notes in time. FL's import makes a channel of each track, so a channel's state belongs to its track (two tracks with the same
/// MIDI channel number, past 15 instruments, stay apart, as they do in FL; a General MIDI player would mix them).</summary>
internal sealed class MidiReplay
{
    public sealed class Note { public int Track, Channel, Key, Velocity; public long On, Off; public int Index; }
    public sealed class Chan { public int Range = -1; public List<(long Tick, int Bend)> Bends = new(); public List<(long Tick, int Cc)> Cc7 = new(); public bool RpnSeen; }

    public int Ppq, Format;
    public List<(long Tick, int Us)> Tempo = new();
    public List<Note> Notes = new();
    /// <summary>Keyed by track x 16 + channel.</summary>
    public Dictionary<int, Chan> Channels = new();
    public Chan Of(int track, int ch) => Channels.TryGetValue(track * 16 + ch, out var c) ? c : new Chan();
    public List<string> TrackNames = new();
    public List<HashSet<int>> TrackChannels = new();
    public List<string> Problems = new();

    public static MidiReplay Read(byte[] midi)
    {
        var f = MidiFile.Parse(midi);
        var r = new MidiReplay { Ppq = f.TicksPerQuarter, Format = f.Format };
        for (int ti = 0; ti < f.Tracks.Count; ti++)
        {
            long tick = 0;
            string name = "";
            var chans = new HashSet<int>();
            var open = new Dictionary<(int Ch, int Key), Note>();
            int rpnMsb = 127, rpnLsb = 127;
            foreach (var e in f.Tracks[ti])
            {
                tick += e.DeltaTime;
                if (e.Status == MidiFile.Meta)
                {
                    if (e.Data1 == 0x03 && e.MetaData != null) name = System.Text.Encoding.UTF8.GetString(e.MetaData);
                    if (e.Data1 == 0x51 && e.MetaData is { Length: 3 } m) r.Tempo.Add((tick, m[0] << 16 | m[1] << 8 | m[2]));
                    continue;
                }
                if (e.Status < 0x80 || e.Status >= 0xF0) continue;
                int ch = e.Channel, cmd = e.Command;
                chans.Add(ch);
                if (!r.Channels.TryGetValue(ti * 16 + ch, out var c)) r.Channels[ti * 16 + ch] = c = new Chan();
                switch (cmd)
                {
                    case 0x90 when e.Data2 > 0:
                        if (open.ContainsKey((ch, e.Data1))) r.Problems.Add($"track {ti}: key {e.Data1} struck again on channel {ch} before it was released (tick {tick})");
                        var n = new Note { Track = ti, Channel = ch, Key = e.Data1, Velocity = e.Data2, On = tick, Off = -1, Index = r.Notes.Count };
                        open[(ch, e.Data1)] = n; r.Notes.Add(n);
                        break;
                    case 0x80:
                    case 0x90:
                        if (open.Remove((ch, e.Data1), out var o)) o.Off = tick; else r.Problems.Add($"track {ti}: a note-off of key {e.Data1} on channel {ch} with no note (tick {tick})");
                        break;
                    case 0xB0:
                        if (e.Data1 == 101) rpnMsb = e.Data2;
                        else if (e.Data1 == 100) rpnLsb = e.Data2;
                        else if (e.Data1 == 6 && rpnMsb == 0 && rpnLsb == 0) { c.Range = e.Data2; c.RpnSeen = true; }
                        else if (e.Data1 == 7) c.Cc7.Add((tick, e.Data2));
                        break;
                    case 0xE0: c.Bends.Add((tick, (e.Data1 | e.Data2 << 7) - 8192)); break;
                }
            }
            foreach (var o in open.Values) r.Problems.Add($"track {ti}: key {o.Key} on channel {o.Channel} never released");
            r.TrackNames.Add(name);
            r.TrackChannels.Add(chans);
        }
        r.Notes.Sort((a, b) => a.Track != b.Track ? a.Track.CompareTo(b.Track) : a.On != b.On ? a.On.CompareTo(b.On) : a.Index.CompareTo(b.Index));
        if (r.Tempo.Count == 0) r.Tempo.Add((0, 500000));
        return r;
    }

    /// <summary>Seconds at a tick, from the file's own tempo map.</summary>
    public double Seconds(long tick)
    {
        double s = 0; long last = 0; int us = Tempo[0].Us;
        foreach (var (t, u) in Tempo)
        {
            if (t > tick) break;
            s += (t - last) * (double)us / Ppq / 1e6; last = t; us = u;
        }
        return s + (tick - last) * (double)us / Ppq / 1e6;
    }

    public long TickAt(double seconds)
    {
        double s = 0; long last = 0; int us = Tempo[0].Us;
        foreach (var (t, u) in Tempo)
        {
            double seg = (t - last) * (double)us / Ppq / 1e6;
            if (s + seg > seconds) break;
            s += seg; last = t; us = u;
        }
        return last + (long)Math.Round((seconds - s) * Ppq * 1e6 / us);
    }

    /// <summary>The value in force at a tick: the last event at or before it.</summary>
    public static int At(List<(long Tick, int V)> list, long tick, int initial)
    {
        int v = initial;
        foreach (var (t, x) in list) { if (t > tick) break; v = x; }
        return v;
    }

}
