using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Vrun.Audio;

/// <summary>What the converter does beyond its defaults. <see cref="Default"/>
/// is exactly what it always did (pitch bends ignored), so a caller that
/// passes no options gets byte-for-byte the old output.</summary>
public sealed record ConvertOptions
{
    public static readonly ConvertOptions Default = new();

    /// <summary>The fit tolerance a song gets unless it asks for another one.</summary>
    public const double DefaultBendToleranceCents = 10.0;

    /// <summary>Play MIDI pitch bends (RPN 0 sets the range; 2 semitones when
    /// a channel never sets it) on square 1, square 2 and the triangle, with
    /// the bend opcodes (<see cref="Bends"/>). Off: bends are ignored, as
    /// they always were.</summary>
    public bool Bends { get; init; }

    /// <summary>How far, in cents, the played pitch may stray from the
    /// period the NES would give the exact bent pitch before the converter
    /// spends another opcode on it. Raised automatically (doubled, up to
    /// <see cref="Bends.MaxToleranceCents"/>) when a channel's bends would
    /// push it past <see cref="Midi2Nes.MaxChannelBytes"/>; the report says so.</summary>
    public double BendToleranceCents { get; init; } = DefaultBendToleranceCents;
}

/// <summary>What the converter did with one NES channel's pitch bends (one
/// pass of the loop). Only channels whose MIDI channel holds a non-zero bend
/// get one.</summary>
public sealed record BendReport
{
    public int NesChannel { get; init; }
    public int MidiChannel { get; init; }
    /// <summary>Pitch-bend events on the MIDI channel, and how many are not centred.</summary>
    public int Events { get; init; }
    public int NonZeroEvents { get; init; }
    /// <summary>The widest bend range in effect at a non-zero bend, in semitones.</summary>
    public double RangeSemitones { get; init; }
    /// <summary>True when that range came from RPN 0, false for the 2-semitone default.</summary>
    public bool RangeFromRpn { get; init; }
    /// <summary>The noise channel: it has no pitch to bend, its bends are counted and ignored.</summary>
    public bool Ignored { get; init; }
    /// <summary>Not played on this channel; <see cref="DropReason"/> says why.</summary>
    public bool Dropped { get; init; }
    public string? DropReason { get; init; }
    public int PitchOps { get; init; }
    public int SlideOps { get; init; }
    /// <summary>Note-ons written as a pitch opcode because the note starts bent.</summary>
    public int NoteOnsAsPitch { get; init; }
    public int BytesAdded { get; init; }
    /// <summary>The moving-pitch tolerance used, and the one asked for (higher when the channel needed room).</summary>
    public double ToleranceCents { get; init; }
    public double RequestedToleranceCents { get; init; }
    /// <summary>Slides were turned off to fit the channel: only held bent pitches (and bent note-ons) are played.</summary>
    public bool SlidesOff { get; init; }
    /// <summary>Frames where the channel plays the MIDI's own note (the fit is judged on those), how many are bent, and how many of those hold their bend.</summary>
    public int CheckedFrames { get; init; }
    public int BentFrames { get; init; }
    public int HeldBentFrames { get; init; }
    /// <summary>Checked frames off target: a held bend not at its exact period, a moving one beyond the tolerance (an opcode was needed while the channel's one-opcode-per-frame slot was taken, or no slope fitted).</summary>
    public int FramesOverTolerance { get; init; }
    /// <summary>Largest distance, in cents, between the played period and the period the NES's own note rule gives the exact bent pitch, on held and on moving frames.</summary>
    public double MaxHeldErrorCents { get; init; }
    public double MaxMovingErrorCents { get; init; }
    /// <summary>Largest distance, in cents, between the played period and the exact bent pitch (includes the NES's 1-period resolution).</summary>
    public double MaxTotalErrorCents { get; init; }
    /// <summary>Checked frames whose exact pitch is below the lowest period the NES has (0x7FF): played at 0x7FF, not counted in the errors.</summary>
    public int FramesBelowRange { get; init; }
}

/// <summary>
/// MIDI pitch bends for the VRUN engine.
///
/// <para><b>Opcodes</b> (they extend the format in <see cref="Midi2Nes"/>; a
/// song with bends needs the bend decoders, see
/// engine/audio/gen_music_decoders.py):</para>
/// <code>
///   0xD1 lo hi   pitch: play period hi:lo (11 bits) now - like a note-on at
///                an exact period (volume / triangle on, both period
///                registers), and stops any slide
///   0xD2 lo hi   slide: from the next frame on, add hi.lo (signed 8.8
///                fixed point, -128.0 .. +127.996 period units) to the
///                period every frame, until a note-on, a pitch, another
///                slide or the loop; 0 stops it
/// </code>
/// Each costs one frame of the channel's one-opcode-per-frame budget, like
/// every instant opcode. A note-on and the loop also stop the slide. The
/// decoder keeps the period as a 16.8 fixed-point value, writes the low
/// period register every frame a slide runs and the high one only when it
/// changes (a pulse's $4003/$4007 write restarts its waveform: a click).
///
/// <para><b>How the converter places them.</b> The note/volume stream is
/// built and retimed exactly as without bends. Then, per frame of one loop,
/// the converter knows what the decoder plays and what the MIDI asks for
/// (the note sounding on the channel plus the bend, the bend range from RPN
/// 0). Bend opcodes go only into frames where the decoder is idle (inside a
/// wait, which is split around them): notes never move, a song with no bend
/// gets exactly the old bytes, and bends are what gets thinned when a
/// channel is busy. A pitch-bent note-on becomes a pitch opcode (exact, same
/// frame). Elsewhere a greedy fit in the decoder's own arithmetic keeps the
/// played period within the tolerance of the period the NES's note rule
/// (floor of the exact period, as the note table) gives the exact bent
/// pitch: a slide is started one frame before the error would exceed the
/// tolerance, with the slope that stays inside for the most frames; a jump
/// gets a pitch opcode on its frame. Frames are judged only where the
/// decoder plays the MIDI's own note (cap-1 retiming can move a note by a
/// frame; those frames are the note timing's, not the bend's).</para>
/// </summary>
public static class Bends
{
    public const int OpPitch = 0xD1;
    public const int OpSlide = 0xD2;

    /// <summary>The tolerance is doubled at most up to this when a channel's
    /// bends would not fit in <see cref="Midi2Nes.MaxChannelBytes"/>; beyond
    /// it the channel's bends are dropped (and reported).</summary>
    public const double MaxToleranceCents = 80.0;

    /// <summary>A frame holds its bend when the bend does not change from
    /// this many frames before it to this many after it (about 50 ms each
    /// side): held pitch is placed exactly, moving pitch within the tolerance.</summary>
    public const int SteadyWindow = 3;

    const double Cpu = 1789773.0;
    const int MaxPeriod = 0x7FF;
    const int MaxScan = 2048;
    const int RolloutFrames = 12;

    /// <summary>The exact (unrounded) 11-bit period of a fractional MIDI note,
    /// with the same rule <see cref="Midi2Nes.BuildPeriodTable"/> uses for
    /// whole notes (so a bend of 0 gives the table's own period).</summary>
    public static double ExactPeriod(double note)
    {
        double freq = 440.0 * Math.Pow(2.0, (note - 69) / 12.0);
        if (freq < 10.0)
            freq = 10.0;
        return Cpu / (16.0 * freq) - 1.0;
    }

    /// <summary>The period the note table's rule gives a fractional note: the
    /// exact period truncated, 0x7FF when it is longer than that.</summary>
    public static int RulePeriod(double note)
    {
        double p = ExactPeriod(note);
        return p > MaxPeriod ? MaxPeriod : (int)p;
    }

    /// <summary>The tolerance, in cents, of a frame whose bend moves: the
    /// asked-for tolerance, or half of the bend's speed there (cents per
    /// frame) when that is more - sampling a curve at 60 frames a second is
    /// itself only right to half a frame, so on a fast glide an error of half
    /// a frame's motion is an error of half a frame in time. The speed is the
    /// smaller of the motion over the 2 frames before and the 2 frames after
    /// (per frame): a jump, or a glide's first or last frame, moves on one
    /// side only and keeps the asked-for tolerance.</summary>
    public static double MovingTolerance(double tolCents, double semis2Before, double semis, double semis2After)
    {
        double speed = 100.0 * Math.Min(Math.Abs(semis - semis2Before), Math.Abs(semis2After - semis)) / 2.0;
        return Math.Max(tolCents, speed / 2.0);
    }

    /// <summary>Cents from the pitch of period <paramref name="reg"/> to the
    /// exact pitch of fractional note <paramref name="note"/> (positive:
    /// sharp). The same for the pulses and the triangle: the triangle sounds
    /// an octave lower for every period, so the ratio is unchanged.</summary>
    public static double CentsOff(int reg, double note)
    {
        double played = Cpu / (16.0 * (reg + 1));
        double ideal = 440.0 * Math.Pow(2.0, (note - 69) / 12.0);
        return 1200.0 * Math.Log2(played / ideal);
    }

    // ── The MIDI side: what one NES channel's MIDI channel asks for, per frame ──

    /// <summary>One MIDI channel's sounding note and bend, frame by frame.</summary>
    internal sealed class PitchSource
    {
        public int[] Note = Array.Empty<int>();         // -1: nothing sounding
        public double[] Semis = Array.Empty<double>();   // the bend, in semitones
        public int Events;
        public int NonZeroEvents;
        public double MaxRange;
        public bool RangeFromRpn;
    }

    /// <summary>Builds the per-frame note and bend of one channel from the
    /// same sorted events <see cref="Midi2Nes.Convert(MidiFile, List{KeyValuePair{int, int}}, Action{string})"/>
    /// turns into a stream. The note follows the converter's own monophonic
    /// rule (a note-on sounds its note; any note-off silences the channel).
    /// An event belongs to frame floor(tick * framesPerTick), like the waits;
    /// a frame shows the state after its last event.</summary>
    internal static PitchSource BuildSource(List<(int Tick, MidiEvent Ev)> events, double framesPerTick, int frames)
    {
        var src = new PitchSource { Note = new int[frames], Semis = new double[frames], MaxRange = 2.0 };
        var changes = new List<(int Frame, int Note, double Semis)>();
        bool playing = false;
        int lastNote = 0;
        int rpnMsb = 127, rpnLsb = 127;
        int rangeSemis = 2, rangeCents = 0;
        int bend = 0;

        foreach (var (tick, ev) in events)
        {
            int cmd = ev.Command;
            if (cmd == MidiFile.NoteOn && ev.Data2 > 0)
            {
                if (ev.Data1 < 128)
                {
                    playing = true;
                    lastNote = ev.Data1;
                }
            }
            else if (cmd == MidiFile.NoteOff || (cmd == MidiFile.NoteOn && ev.Data2 == 0))
                playing = false;
            else if (cmd == MidiFile.ControlChange)
            {
                int c = ev.Data1, v = ev.Data2 & 0x7F;
                if (c == 101) rpnMsb = v;
                else if (c == 100) rpnLsb = v;
                else if (c == 99 || c == 98) { rpnMsb = 127; rpnLsb = 127; } // an NRPN is selected: data entry is not ours
                else if (c == 6 && rpnMsb == 0 && rpnLsb == 0) { rangeSemis = v; src.RangeFromRpn = true; }
                else if (c == 38 && rpnMsb == 0 && rpnLsb == 0) { rangeCents = v; src.RangeFromRpn = true; }
                else if (c == 121) { bend = 0; rpnMsb = 127; rpnLsb = 127; } // reset all controllers: bend centred, RPN deselected
                else
                    continue;
            }
            else if (cmd == MidiFile.PitchBend)
            {
                bend = (((ev.Data2 & 0x7F) << 7) | (ev.Data1 & 0x7F)) - 8192;
                src.Events++;
                if (bend != 0)
                    src.NonZeroEvents++;
            }
            else
                continue;

            double range = rangeSemis + rangeCents / 100.0;
            if (bend != 0 && range > src.MaxRange)
                src.MaxRange = range;
            changes.Add(((int)(tick * framesPerTick), playing ? lastNote : -1, bend / 8192.0 * range));
        }

        int ci = 0;
        int note = -1;
        double semis = 0.0;
        for (int k = 0; k < frames; k++)
        {
            while (ci < changes.Count && changes[ci].Frame <= k)
            {
                note = changes[ci].Note;
                semis = changes[ci].Semis;
                ci++;
            }
            src.Note[k] = note;
            src.Semis[k] = semis;
        }
        return src;
    }

    // ── The decoder side: one pass of a retimed stream, frame by frame ──────

    const int KNote = 0, KOff = 1, KVol = 2, KWait = 3, KLoop = 4;

    readonly record struct Op(int Offset, int Length, int Frame, int Kind, int Value);

    static List<Op> ParseOps(List<int> stream, out int frames)
    {
        var ops = new List<Op>();
        int f = 0;
        int i = 0;
        while (i < stream.Count)
        {
            int b = stream[i];
            if (b == 0xFF)
            {
                ops.Add(new Op(i, 1, f, KLoop, 0));
                f += 1;
                break;
            }
            if (b == 0xD0)
            {
                int w = stream[i + 1];
                ops.Add(new Op(i, 2, f, KWait, w));
                f += w;
                i += 2;
                continue;
            }
            if (b >= 0x81 && b <= 0xBF)
            {
                ops.Add(new Op(i, 1, f, KWait, b - 0x80));
                f += b - 0x80;
            }
            else if (b < 0x80)
            {
                ops.Add(new Op(i, 1, f, KNote, b));
                f += 1;
            }
            else if (b == 0x80)
            {
                ops.Add(new Op(i, 1, f, KOff, 0));
                f += 1;
            }
            else if (b >= 0xC0 && b <= 0xCF)
            {
                ops.Add(new Op(i, 1, f, KVol, b & 0x0F));
                f += 1;
            }
            else
                throw new InvalidOperationException($"bend fit: opcode 0x{b:X2} at {i} is not a note/volume/wait opcode");
            i += 1;
        }
        frames = f;
        return ops;
    }

    /// <summary>
    /// Adds the bend opcodes to one tonal channel's retimed stream. Returns
    /// the new stream (the old bytes wherever nothing is inserted; the same
    /// number of frames per loop) and what was done.
    /// </summary>
    internal static (List<int> Stream, BendReport Report) Fit(List<int> stream, PitchSource src, double tolCents,
        int nesChannel, int midiChannel, bool slides = true)
    {
        var (tableLo, tableHi) = Midi2Nes.BuildPeriodTable();
        var ops = ParseOps(stream, out int frames);

        var busy = new int[frames];
        var insertable = new bool[frames];
        Array.Fill(busy, -1);
        for (int oi = 0; oi < ops.Count; oi++)
        {
            var op = ops[oi];
            if (op.Kind == KWait)
                for (int f = op.Frame; f < op.Frame + op.Value && f < frames; f++)
                    insertable[f] = true;
            else
                busy[op.Frame] = oi;
        }
        var nextIns = new int[frames + 1];
        nextIns[frames] = frames;
        for (int f = frames - 1; f >= 0; f--)
            nextIns[f] = f + 1 < frames && insertable[f + 1] ? f + 1 : nextIns[f + 1];

        // The MIDI side, sized to this stream's own loop (the converter lands
        // it on the song's length; the decoder's truth is the stream).
        int Midi(int k) => k < src.Note.Length ? src.Note[k] : (src.Note.Length > 0 ? src.Note[^1] : -1);
        double Semi(int k) => k < src.Semis.Length ? src.Semis[k] : (src.Semis.Length > 0 ? src.Semis[^1] : 0.0);

        // Held or moving: a frame is held when the bend does not change from
        // SteadyWindow frames before it to SteadyWindow frames after it.
        var changed = new bool[frames];
        for (int k = 1; k < frames; k++)
            changed[k] = Semi(k) != Semi(k - 1);
        var steady = new bool[frames];
        for (int k = 0; k < frames; k++)
        {
            bool s = true;
            for (int d = -SteadyWindow + 1; d <= SteadyWindow && s; d++)
                if (k + d > 0 && k + d < frames && changed[k + d])
                    s = false;
            steady[k] = s;
        }

        // Band of acceptable periods per frame. Held pitch: exactly the
        // period the note table's rule gives the exact bent pitch (a bend held
        // at +2 sounds exactly like the note 2 semitones up). Moving pitch:
        // within MovingTolerance(k) cents of the exact bent pitch, plus the
        // two periods around it (the NES's resolution); with slides off,
        // anything.
        var tolAt = new double[frames];
        for (int k = 0; k < frames; k++)
            tolAt[k] = MovingTolerance(tolCents, Semi(Math.Max(k - 2, 0)), Semi(k), Semi(k + 2));
        (int Lo, int Hi, int Rule, bool Below) Band(int k, int note)
        {
            double x = note + Semi(k);
            double exact = ExactPeriod(x);
            if (exact > MaxPeriod)
                return (MaxPeriod, MaxPeriod, MaxPeriod, true);
            int rule = Math.Max((int)exact, 0);
            if (steady[k])
                return (rule, rule, rule, false);
            if (!slides)
                return (0, MaxPeriod, rule, false);
            double up = Math.Pow(2.0, tolAt[k] / 1200.0), down = 1.0 / up;
            int lo = Math.Min((int)Math.Ceiling((exact + 1.0) * down - 1.0 - 1e-9), rule);
            int hi = Math.Max((int)Math.Floor((exact + 1.0) * up - 1.0 + 1e-9), Math.Min(rule + 1, MaxPeriod));
            return (Math.Max(lo, 0), Math.Min(hi, MaxPeriod), rule, false);
        }

        var inserted = new SortedDictionary<int, (int Code, int Value)>();
        var replaced = new Dictionary<int, int>();
        var played = new int[frames];
        var decAt = new int[frames];

        long per = 0;
        bool known = false;
        int vel = 0;
        int decNote = -1;
        bool sounding = false;

        bool Aligned(int k) => sounding && decNote >= 0 && Midi(k) == decNote;
        static int Reg(long p) => (int)(p >> 8);

        // First checked frame in (k, until] that leaves its band if nothing
        // changes, or -1. A note-on or note-off ahead ends the question.
        int FirstViolation(int k, long p, int v, int until)
        {
            for (int j = k + 1; j <= until && j < frames; j++)
            {
                p += v;
                int oj = busy[j];
                if (oj >= 0 && (ops[oj].Kind == KNote || ops[oj].Kind == KOff))
                    return -1;
                if (sounding && Midi(j) == decNote)
                {
                    var bd = Band(j, decNote);
                    int r = Reg(p);
                    if (r < bd.Lo || r > bd.Hi)
                        return j;
                }
            }
            return -1;
        }

        // The slide from frame k that keeps the most following frames in
        // their bands: (slope, frames covered). Prefers 0 when it fits.
        (int V, int Covered) BestSlide(int k, long p0)
        {
            long lo = short.MinValue, hi = short.MaxValue;
            int covered = 0;
            for (int i = 1; i <= MaxScan; i++)
            {
                int j = k + i;
                if (j >= frames)
                    break;
                int oj = busy[j];
                if (oj >= 0 && (ops[oj].Kind == KNote || ops[oj].Kind == KOff))
                    break;
                if (sounding && Midi(j) == decNote)
                {
                    var bd = Band(j, decNote);
                    long vmin = CeilDiv((long)bd.Lo * 256 - p0, i);
                    long vmax = FloorDiv(((long)bd.Hi + 1) * 256 - 1 - p0, i);
                    long nlo = Math.Max(lo, vmin), nhi = Math.Min(hi, vmax);
                    if (nlo > nhi)
                        break;
                    lo = nlo;
                    hi = nhi;
                }
                covered = i;
            }
            if (covered == 0)
                return (0, 0);
            long v = lo <= 0 && hi >= 0 ? 0 : lo + (hi - lo) / 2;
            return ((int)v, covered);
        }

        // Frames off target from frame k (its register already p) over the
        // next RolloutFrames, and opcodes spent, if the fit carries on with
        // its plain rules (a pitch opcode on a free frame that is off, a
        // look-ahead slope otherwise). Compares the two ways out of an off
        // frame.
        (int Off, int Ops) Rollout(int k, long p, int v)
        {
            int off = 0, used = 1;
            bool aligned = sounding && Midi(k) == decNote;
            if (aligned)
            {
                var b0 = Band(k, decNote);
                int r0 = Reg(p);
                if (r0 < b0.Lo || r0 > b0.Hi)
                    off++;
            }
            for (int j = k + 1; j <= k + RolloutFrames && j < frames; j++)
            {
                p += v;
                int oj = busy[j];
                if (oj >= 0 && (ops[oj].Kind == KNote || ops[oj].Kind == KOff))
                    break;
                bool here = sounding && Midi(j) == decNote;
                var bj = here ? Band(j, decNote) : default;
                if (insertable[j])
                {
                    int rj0 = Reg(p);
                    if (here && (rj0 < bj.Lo || rj0 > bj.Hi))
                    {
                        p = (long)bj.Rule << 8;
                        v = 0;
                        used++;
                    }
                    else
                    {
                        int jj = FirstViolation(j, p, v, nextIns[j]);
                        if (jj >= 0)
                        {
                            var (v2, c2) = BestSlide(j, p);
                            if (c2 >= 1 && v2 != v)
                            {
                                v = v2;
                                used++;
                            }
                        }
                    }
                }
                if (here)
                {
                    int rj = Reg(p);
                    if (rj < bj.Lo || rj > bj.Hi)
                        off++;
                }
            }
            return (off, used);
        }

        for (int k = 0; k < frames; k++)
        {
            // The decoder: frame 0 of a pass (pointer at 0) clears the slide,
            // any other frame steps it, then the opcode of this frame (if any).
            if (k == 0)
                vel = 0;
            else if (vel != 0)
                per += vel;

            int oi = busy[k];
            if (oi >= 0)
            {
                var op = ops[oi];
                if (op.Kind == KNote)
                {
                    per = ((long)((tableHi[op.Value] << 8) | tableLo[op.Value])) << 8;
                    known = true;
                    vel = 0;
                    decNote = op.Value;
                    sounding = true;
                    if (Aligned(k))
                    {
                        var bd = Band(k, decNote);
                        int r = Reg(per);
                        if (r < bd.Lo || r > bd.Hi)
                        {
                            replaced[oi] = bd.Rule;
                            per = (long)bd.Rule << 8;
                        }
                    }
                }
                else if (op.Kind == KOff)
                    sounding = false;
            }
            else if (insertable[k] && known)
            {
                bool done = false;
                if (Aligned(k))
                {
                    var bd = Band(k, decNote);
                    int r = Reg(per);
                    if (r < bd.Lo || r > bd.Hi)
                    {
                        // Off target now. A held pitch, or a moving one no
                        // slope can follow: a pitch opcode lands this frame.
                        // A moving one: a pitch opcode (this frame right, but
                        // it stops the motion, so the next frame may be off)
                        // or a slope (this frame stays off) - whichever leaves
                        // fewer frames off target over the next RolloutFrames
                        // (then fewer opcodes; then the slope).
                        var (v, covered) = slides && !steady[k] ? BestSlide(k, per) : (0, 0);
                        bool slope = false;
                        if (covered >= 1 && v != vel)
                        {
                            var bySlope = Rollout(k, per, v);
                            var byPitch = Rollout(k, (long)bd.Rule << 8, 0);
                            slope = bySlope.Off < byPitch.Off || (bySlope.Off == byPitch.Off && bySlope.Ops <= byPitch.Ops);
                        }
                        if (slope)
                        {
                            inserted[k] = (OpSlide, v);
                            vel = v;
                        }
                        else
                        {
                            inserted[k] = (OpPitch, bd.Rule);
                            per = (long)bd.Rule << 8;
                            vel = 0;
                        }
                        done = true;
                    }
                }
                if (!done && slides)
                {
                    int ni = nextIns[k];
                    int j = FirstViolation(k, per, vel, ni);
                    if (j >= 0)
                    {
                        var (v, covered) = BestSlide(k, per);
                        // A jump on the next free frame (the bend still
                        // before it, then a change wider than the band): the
                        // pitch opcode there lands it exactly.
                        bool jump = j > 0 && Semi(j - 1) == Semi(Math.Max(j - 2, 0)) && 100.0 * Math.Abs(Semi(j) - Semi(j - 1)) > tolAt[j];
                        bool jumpAhead = j == ni && ni < frames && covered <= 1 && jump;
                        if (!jumpAhead && covered >= 1 && v != vel)
                        {
                            inserted[k] = (OpSlide, v);
                            vel = v;
                        }
                    }
                }
            }
            played[k] = known ? Reg(per) : -1;
            decAt[k] = Aligned(k) ? decNote : -1;
        }

        // What it sounds like, judged on the frames that play the MIDI's note.
        int checkedFrames = 0, bent = 0, held = 0, over = 0, below = 0;
        double maxHeld = 0, maxMoving = 0, maxTotal = 0;
        for (int k = 0; k < frames; k++)
        {
            int n = decAt[k];
            if (n < 0)
                continue;
            var bd = Band(k, n);
            checkedFrames++;
            if (Semi(k) != 0.0)
            {
                bent++;
                if (steady[k])
                    held++;
            }
            if (bd.Below)
            {
                below++;
                continue;
            }
            int r = played[k];
            if (r < bd.Lo || r > bd.Hi)
                over++;
            double x = n + Semi(k);
            double fit = Math.Abs(CentsOff(r, x) - CentsOff(bd.Rule, x));
            if (steady[k])
                maxHeld = Math.Max(maxHeld, fit);
            else
                maxMoving = Math.Max(maxMoving, fit);
            maxTotal = Math.Max(maxTotal, Math.Abs(CentsOff(r, x)));
        }

        // The new bytes: waits split around inserted opcodes, bent note-ons
        // rewritten, everything else copied as it was.
        var o = new List<int>(stream.Count + 3 * (inserted.Count + replaced.Count));
        int idx = 0;
        while (idx < ops.Count)
        {
            var op = ops[idx];
            if (op.Kind == KWait)
            {
                int last = idx;
                while (last + 1 < ops.Count && ops[last + 1].Kind == KWait)
                    last++;
                int gStart = op.Frame, gEnd = ops[last].Frame + ops[last].Value;
                var here = inserted.Where(kv => kv.Key >= gStart && kv.Key < gEnd).ToList();
                if (here.Count == 0)
                {
                    for (int q = op.Offset; q < ops[last].Offset + ops[last].Length; q++)
                        o.Add(stream[q]);
                }
                else
                {
                    int cur = gStart;
                    foreach (var (f, ins) in here)
                    {
                        Midi2Nes.EmitWait(o, f - cur);
                        o.Add(ins.Code);
                        o.Add(ins.Value & 0xFF);
                        o.Add((ins.Value >> 8) & 0xFF);
                        cur = f + 1;
                    }
                    Midi2Nes.EmitWait(o, gEnd - cur);
                }
                idx = last + 1;
                continue;
            }
            if (replaced.TryGetValue(idx, out int rv))
            {
                o.Add(OpPitch);
                o.Add(rv & 0xFF);
                o.Add((rv >> 8) & 0xFF);
            }
            else
                for (int q = op.Offset; q < op.Offset + op.Length; q++)
                    o.Add(stream[q]);
            idx++;
        }

        if (FrameCount(o) != frames)
            throw new InvalidOperationException($"bend fit changed the loop length ({FrameCount(o)} frames, was {frames})");

        var report = new BendReport
        {
            NesChannel = nesChannel,
            MidiChannel = midiChannel,
            Events = src.Events,
            NonZeroEvents = src.NonZeroEvents,
            RangeSemitones = src.MaxRange,
            RangeFromRpn = src.RangeFromRpn,
            PitchOps = inserted.Count(kv => kv.Value.Code == OpPitch),
            SlideOps = inserted.Count(kv => kv.Value.Code == OpSlide),
            NoteOnsAsPitch = replaced.Count,
            BytesAdded = o.Count - stream.Count,
            ToleranceCents = tolCents,
            SlidesOff = !slides,
            CheckedFrames = checkedFrames,
            BentFrames = bent,
            HeldBentFrames = held,
            FramesOverTolerance = over,
            MaxHeldErrorCents = maxHeld,
            MaxMovingErrorCents = maxMoving,
            MaxTotalErrorCents = maxTotal,
            FramesBelowRange = below,
        };
        return (o, report);
    }

    /// <summary>Frames one pass of a stream takes under the cap-1 decoder
    /// (bend opcodes included), its loop opcode counted.</summary>
    public static int FrameCount(IReadOnlyList<int> stream)
    {
        int f = 0;
        int i = 0;
        while (i < stream.Count)
        {
            int b = stream[i];
            if (b == 0xFF)
                return f + 1;
            if (b == 0xD0)
            {
                f += stream[i + 1];
                i += 2;
            }
            else if (b == OpPitch || b == OpSlide)
            {
                f += 1;
                i += 3;
            }
            else if (b >= 0x81 && b <= 0xBF)
            {
                f += b - 0x80;
                i += 1;
            }
            else
            {
                f += 1;
                i += 1;
            }
        }
        return f;
    }

    /// <summary>The log line for one channel's bends (the build prints it, the
    /// AudioPlayer configurator and BrokenNes2 show it).</summary>
    internal static string Describe(BendReport r, string? songName)
    {
        string ch = $"{Midi2Nes.ChannelNames[r.NesChannel]}<-ch{r.MidiChannel}";
        string label = songName != null ? $" ({songName}, {ch})" : $" ({ch})";
        var ci = CultureInfo.InvariantCulture;
        string events = $"{r.NonZeroEvents} non-zero bend event(s)";
        if (r.Ignored)
            return $"[midi2nes] bends{label}: {events} ignored - the noise channel has no pitch to bend";
        string range = $"range {r.RangeSemitones.ToString("0.##", ci)} semitones ({(r.RangeFromRpn ? "RPN 0" : "default, no RPN 0")})";
        if (r.Dropped)
            return $"[midi2nes] bends{label}: {events}, {range}: NOT played - {r.DropReason}";
        string tol = r.ToleranceCents.ToString("0.##", ci);
        string mode = r.SlidesOff
            ? $" (SLIDES OFF: held bent pitches only - with slides the channel would pass {Midi2Nes.MaxChannelBytes} bytes even at {tol} cents)"
            : r.ToleranceCents > r.RequestedToleranceCents
                ? $" (moving-pitch tolerance raised to {tol} cents to stay under {Midi2Nes.MaxChannelBytes} bytes)" : "";
        string belowText = r.FramesBelowRange > 0 ? $", {r.FramesBelowRange} frame(s) below the lowest NES pitch (played at 0x7FF)" : "";
        return $"[midi2nes] bends{label}: {events}, {range}: {r.SlideOps} slide + {r.PitchOps} pitch opcode(s), " +
               $"{r.NoteOnsAsPitch} bent note-on(s) sent as pitch, +{r.BytesAdded} bytes{mode}; " +
               $"{r.BentFrames} bent frame(s), {r.HeldBentFrames} held: held max {r.MaxHeldErrorCents.ToString("0.0", ci)} cents, " +
               $"moving max {r.MaxMovingErrorCents.ToString("0.0", ci)} cents from the NES period of the exact pitch " +
               $"({r.MaxTotalErrorCents.ToString("0.0", ci)} from the exact pitch); " +
               $"{r.FramesOverTolerance} frame(s) off target (held: not exact; moving: beyond {tol} cents){belowText}";
    }

    static long FloorDiv(long a, long b) => a >= 0 ? a / b : -((-a + b - 1) / b);
    static long CeilDiv(long a, long b) => -FloorDiv(-a, b);
}
