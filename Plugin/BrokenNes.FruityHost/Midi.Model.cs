// Round 7 (midi): the decoder model of VRUN's music engine, ported from BrokenDev/tools/music/decoder_model.py, and the machine the checks run ROMs on.
//
// The ROM plays four opcode streams, one opcode per channel per frame at most (engine/audio/gen_music_decoders.py). The model predicts, for every frame, what the four decoders leave in
// the APU registers: both pulses' period and volume, the triangle's period and linear-counter reload, the noise period and volume. A ROM is run on the plugin's own machine
// (NesAudioMachine, CPU/PPU/APU FIX, the class the plugin's NesGame wraps) and its APU register latch ($4000-$4017, the last value written to each) is read after every frame.
// Comparing the latch, not the APU's inner state (decoder_model.py reads a Workshop --trace), needs no slack: the triangle's linear counter is compared as the value written.
using NesEmulator.Plugin;

namespace BrokenNes.FruityHost;

internal static class MidiModel
{
    public static readonly int[] NoiseCycles = [4, 8, 16, 32, 64, 96, 128, 160, 202, 254, 380, 508, 762, 1016, 2034, 4068];
    public const int LatchBytes = 0x18;

    private struct St { public int Per, Vol, Lin, NPer; }

    /// <summary>The predicted state after each frame's decode (the templates' wait / one-opcode / loop rules), from <paramref name="init"/>.
    /// Square 1, square 2 and the triangle follow VRUN's bend decoders (round 9: the template carries them on all three; on a stream without bend opcodes they write
    /// exactly what the plain decoders write): the period is a 16.8 fixed-point value with an 8.8 slide, a running slide is stepped before the wait check on every
    /// frame (low period register written, high one only when it changes), cleared on a frame whose pointer is 0; $D1 lo hi = play period hi:lo (a note-on at an exact
    /// period), $D2 lo hi = set the slide. The model keeps the two period registers as last written, like the latch it is compared with.
    /// <paramref name="flipSlides"/> negates every slide (a negative control: the check must then fail on a song that slides).</summary>
    private static St[] Run(byte[] stream, int kind, int frames, byte[] plo, byte[] phi, St init, bool flipSlides = false)
    {
        int ptr = 0, wait = 0, vol = 15;              // mus_sq1_vol / mus_sq2_vol start at 15 (audio_engine.fab)
        int per = 0, vel = 0;                         // mus_<ch>_per (24-bit: hi.lo.fraction) and mus_<ch>_vel (signed 8.8)
        int lo = init.Per & 0xFF, hi = init.Per >> 8; // the period registers as last written
        var st = init;
        var o = new St[frames];
        for (int f = 0; f < frames; f++)
        {
            if (kind != 2)
            {
                if (ptr == 0) vel = 0;
                else if (vel != 0)
                {
                    int oldHi = per >> 16 & 0xFF;
                    per = per + vel & 0xFFFFFF;
                    lo = per >> 8 & 0xFF;
                    if ((per >> 16 & 0xFF) != oldHi) hi = per >> 16 & 0xFF;
                }
            }
            if (wait > 0) wait--;
            if (wait == 0)
            {
                int op = stream[ptr];
                if (op < 0x80)
                {
                    if (kind == 2) { st.Vol = 15; st.NPer = NoiseCycles[op & 0x0F]; }
                    else
                    {
                        if (kind == 0) st.Vol = vol; else st.Lin = 0x7F;
                        lo = plo[op]; hi = phi[op];
                        per = (hi << 8 | lo) << 8; vel = 0;
                    }
                    ptr++;
                }
                else if (op == 0x80) { if (kind == 1) st.Lin = 0; else st.Vol = 0; ptr++; }
                else if (op < 0xC0) { wait = op - 0x80; ptr++; }
                else if (op < 0xD0) { if (kind == 0) { vol = op & 0x0F; st.Vol = vol; } ptr++; }
                else if (op == 0xD0) { wait = stream[ptr + 1]; ptr += 2; }
                else if (op == 0xFF || kind == 2) ptr = 0;                          // the loop (the noise's plain decoder reads anything else as the loop too)
                else
                {
                    int a = stream[ptr + 1], b = stream[ptr + 2];
                    ptr += 3;
                    if (op == 0xD1)
                    {
                        per = (b << 8 | a) << 8; vel = 0;
                        lo = a; hi = b;
                        if (kind == 0) st.Vol = vol; else st.Lin = 0x7F;
                    }
                    else vel = flipSlides ? -(short)(b << 8 | a) : (short)(b << 8 | a);
                }
            }
            if (kind != 2) st.Per = (hi & 7) << 8 | lo;
            o[f] = st;
        }
        return o;
    }

    /// <summary>The eight observed fields of a frame's latch: p1per, p1vol, p2per, p2vol, tper, tlin, nper, nvol.</summary>
    public static int Field(ReadOnlySpan<byte> l, int field) => field switch
    {
        0 => (l[3] & 7) << 8 | l[2], 1 => l[0] & 15,
        2 => (l[7] & 7) << 8 | l[6], 3 => l[4] & 15,
        4 => (l[0xB] & 7) << 8 | l[0xA], 5 => l[8] & 0x7F,
        6 => NoiseCycles[l[0xE] & 15], _ => l[0xC] & 15,
    };

    public static readonly string[] FieldNames = ["p1per", "p1vol", "p2per", "p2vol", "tper", "tlin", "nper", "nvol"];

    public sealed record Score(int Start, long Agree, long Of, double Worst, string[] Mismatches);

    /// <summary>Scores the latch trace (24 bytes a frame) against the model over <paramref name="frames"/> frames from <paramref name="start"/>; the first <paramref name="skip"/> frames of the decode are not compared.</summary>
    public static Score ScoreAt(byte[][] streams, byte[] trace, int start, int frames, int skip = 0, bool flipSlides = false)
    {
        var (plo, phi) = Vrun.Audio.Midi2Nes.BuildPeriodTable();
        int total = trace.Length / LatchBytes;
        frames = Math.Min(frames, total - start);
        if (frames <= skip) return new Score(start, 0, 1, 0, ["no frames to compare"]);
        // the state the registers were in just before the decode began: what the ROM's start-up left there is not something the streams say
        ReadOnlySpan<byte> before = start > 0 ? trace.AsSpan((start - 1) * LatchBytes, LatchBytes) : new byte[LatchBytes];
        var pred = new St[4][];
        for (int c = 0; c < 4; c++)
        {
            int kind = c < 2 ? 0 : c == 2 ? 1 : 2;
            var init = new St
            {
                Per = c < 2 ? Field(before, c * 2) : c == 2 ? Field(before, 4) : 0,
                Vol = c < 2 ? Field(before, c * 2 + 1) : c == 3 ? Field(before, 7) : 0,
                Lin = c == 2 ? (Field(before, 5) != 0 ? 0x7F : 0) : 0,
                NPer = c == 3 ? Field(before, 6) : 0,
            };
            pred[c] = Run(streams[c], kind, frames, plo, phi, init, flipSlides);
        }
        long agree = 0, of = 0;
        double worst = 1;
        var bad = new List<string>();
        for (int field = 0; field < 8; field++)
        {
            int c = field < 4 ? field / 2 : field < 6 ? 2 : 3;
            int ok = 0, n = 0;
            for (int k = skip; k < frames; k++)
            {
                var l = trace.AsSpan((start + k) * LatchBytes, LatchBytes);
                int o = Field(l, field);
                var s = pred[c][k];
                int p = field switch { 0 or 2 or 4 => s.Per, 5 => s.Lin, 6 => s.NPer, _ => s.Vol };
                n++;
                if (o == p) ok++;
                else if (bad.Count < 6) bad.Add($"{FieldNames[field]} frame {start + k}: model {p:X} trace {o:X}");
            }
            agree += ok; of += n;
            worst = Math.Min(worst, n == 0 ? 0 : (double)ok / n);
        }
        return new Score(start, agree, of, worst, bad.ToArray());
    }

    /// <summary>The decode start that agrees best, scanned over <paramref name="lo"/>..<paramref name="hi"/> (decoder_model.py --scan-start).</summary>
    public static Score Best(byte[][] streams, byte[] trace, int frames, int lo = 0, int hi = 120, int skip = 0)
    {
        Score? best = null;
        for (int s = lo; s <= hi && (s + 1) * LatchBytes < trace.Length; s++)
        {
            var sc = ScoreAt(streams, trace, s, frames, skip);
            if (best == null || sc.Agree > best.Agree) best = sc;
        }
        return best ?? new Score(0, 0, 1, 0, ["empty trace"]);
    }
}

/// <summary>A ROM run headless on the plugin's machine (CPU/PPU/APU FIX, no input), with what the checks read after every frame.</summary>
internal sealed class MidiRun
{
    /// <summary>24 bytes per frame: the APU registers as the frame's NMI left them (round 9: BrokenNes2.Music.NmiLatch, from every write the ROM made; a busy frame's NMI
    /// can still be writing when the frame ends, which a read at the frame's end would show a frame late).</summary>
    public byte[] Latch = [];
    /// <summary>24 bytes per frame: the latch read when the frame ended (round 7's sample point; kept to count the frames where the two differ).</summary>
    public byte[] EndLatch = [];
    public long[] Picture = [];         // a hash of the picture after each frame
    public int[] Loops = [], Position = [];    // the player's loop counter and song position (RAM), -1 when the ROM has no such symbol

    /// <summary>Frames whose registers at the frame's end differ from those after its NMI (the NMI was still writing when the frame ended).</summary>
    public int LateFrames
    {
        get
        {
            int n = 0;
            for (int f = 0; f + 1 < Latch.Length / MidiModel.LatchBytes; f++)
                if (!Latch.AsSpan(f * MidiModel.LatchBytes, MidiModel.LatchBytes).SequenceEqual(EndLatch.AsSpan(f * MidiModel.LatchBytes, MidiModel.LatchBytes))) n++;
            return n;
        }
    }

    public static MidiRun Of(byte[] rom, int frames, string? mlb = null, int preroll = 0)
    {
        var m = new NesAudioMachine(rom, mlb, "FIX", "FIX", "FIX") { HostSampleRate = 44100 };
        ushort loopsAt = 0, posAt = 0;
        bool haveLoops = mlb != null && m.Symbols.TryGetRam("pl_loops@0", out loopsAt, out _);
        bool havePos = mlb != null && m.Symbols.TryGetRam("pl_pos_frames@0", out posAt, out _);
        var r = new MidiRun { EndLatch = new byte[frames * MidiModel.LatchBytes], Picture = new long[frames], Loops = new int[frames], Position = new int[frames] };
        var buf = new float[4096];
        var latch = new byte[MidiModel.LatchBytes];
        for (int i = 0; i < preroll; i++) { m.RunFrame(); while (m.ReadAvailable(buf) > 0) { } }
        var nmi = new BrokenNes2.Music.NmiLatch(CopyLatch(m, latch));  // (from the registers after the preroll, which is not reported)
        m.RecordTap = nmi.OnWrite;
        for (int f = 0; f <= frames; f++)                                  // one frame more than reported: the last frame's NMI may write on into it
        {
            m.RunFrame();
            while (m.ReadAvailable(buf) > 0) { }
            nmi.FrameRan(m.FrameStartClock);
            if (m.Crashed) throw new InvalidOperationException("the ROM crashed: " + m.CrashInfo);
            if (f == frames) break;
            m.CopyApuLatch(latch);
            latch.CopyTo(r.EndLatch, f * MidiModel.LatchBytes);
            ulong h = 1469598103934665603UL;
            foreach (byte b in m.FrameBuffer) h = (h ^ b) * 1099511628211UL;
            r.Picture[f] = (long)h;
            r.Loops[f] = haveLoops ? m.PeekRam(loopsAt) : -1;
            r.Position[f] = havePos ? m.PeekRam(posAt) | m.PeekRam((ushort)(posAt + 1)) << 8 : -1;
        }
        m.RecordTap = null;
        r.Latch = nmi.PerNmi().AsSpan(0, frames * MidiModel.LatchBytes).ToArray();
        return r;
    }

    private static byte[] CopyLatch(NesAudioMachine m, byte[] buf) { m.CopyApuLatch(buf); return buf; }
}
