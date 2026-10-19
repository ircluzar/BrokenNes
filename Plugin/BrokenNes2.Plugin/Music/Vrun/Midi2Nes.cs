using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Vrun.Audio;

/// <summary>
/// MIDI -> NES APU opcode streams, one per NES channel (Square1, Square2,
/// Triangle, Noise). A C# port of what was engine/audio/midi2nes.py -
/// byte-identical output, verified on every song VRUN ships - and now the
/// one implementation: the VRUN build runs it through cli/midi2nes, the FL
/// Studio bridge plugin calls it in-process (docs/26-live-audio-bridge-spec.md).
///
/// <para><b>Opcode format</b> (the runtime decoder in the ROM reads exactly
/// this; originally nesmolol's NesSoundEngine.cs design):</para>
/// <code>
///   0x00-0x7F  note on (MIDI note 0-127; period from PERIOD_LO/HI)
///   0x80       note off
///   0x81-0xBF  wait 1-63 frames
///   0xC0-0xCF  volume 0-15
///   0xD0 xx    wait xx (1-255) frames
///   0xD1 lo hi pitch: play period hi:lo now (a note-on at an exact period)
///   0xD2 lo hi slide: add hi.lo (signed 8.8) to the period every frame
///   0xFF       end: loop to the start of this channel's stream
/// </code>
/// <para>0xD1/0xD2 are the pitch-bend opcodes, written only when the caller
/// asks for bends (<see cref="ConvertOptions.Bends"/>) and only on square 1,
/// square 2 and the triangle; a song that has them needs the bend decoders.
/// See <see cref="Bends"/>. Without bends the output is exactly what it
/// always was.</para>
///
/// <para>Every whole-song decision the converter makes - which MIDI channel
/// goes where, cap-1 retiming, overload thinning, the shared loop period,
/// truncation - needs the complete song, which is why the bridge plugin's
/// Live mode can't make them and its Exact mode converts a whole captured
/// song first. The reasons behind each one are documented at the method.</para>
/// </summary>
public static class Midi2Nes
{
    /// <summary>Exact NTSC frame rate (~60.0988 Hz).</summary>
    public static readonly double Fps = 1789773.0 / (262.0 * (341.0 / 3.0));

    public static readonly string[] ChannelNames = { "square1", "square2", "triangle", "noise" };

    /// <summary>
    /// Per-channel byte cap. NESFab allocates each `data` array into one
    /// contiguous ROM span (never split across banks), so one very dense
    /// channel could fail allocation even with plenty of total ROM free.
    /// History: 2048 -> 1152 (a bigger data set broke an unrelated
    /// allocation) -> 1280 once cap-1 retiming shrank everything, which is
    /// exactly enough for think's noise channel (1227 B, a golden track) to
    /// ship untruncated. Changing the DATA size can shift bank packing enough
    /// to break an unrelated allocation elsewhere - re-verify the build after
    /// touching this.
    /// </summary>
    public const int MaxChannelBytes = 1280;

    // ── Period table ─────────────────────────────────────────────────────

    /// <summary>
    /// The 128-entry APU period table (11-bit, split lo/hi). Notes whose true
    /// period exceeds the register max (0x7FF - every MIDI note 0-32) clamp
    /// to 0x7FF, the lowest reproducible pitch; the old "0 if too low" was
    /// backwards (0 is the HIGHEST frequency, an ultrasonic squeal) and hit
    /// 29% of boss's square2 notes.
    /// </summary>
    public static (byte[] Lo, byte[] Hi) BuildPeriodTable()
    {
        var lo = new byte[128];
        var hi = new byte[128];
        for (int note = 0; note < 128; note++)
        {
            double freq = 440.0 * Math.Pow(2.0, (note - 69) / 12.0);
            if (freq < 10.0)
                freq = 10.0;
            double period = 1789773.0 / (16.0 * freq) - 1.0;
            int p = period > 0x7FF ? 0x7FF : (int)period;
            lo[note] = (byte)(p & 0xFF);
            hi[note] = (byte)((p >> 8) & 0x07);
        }
        return (lo, hi);
    }

    // ── Tempo ────────────────────────────────────────────────────────────

    /// <summary>The first valid Set Tempo meta event in any track, in
    /// microseconds per quarter note; 500000 (120 BPM) if none. Only the
    /// FIRST tempo is honoured - tempo changes later in a song are ignored,
    /// in the build and therefore in the game.</summary>
    public static int FindTempo(MidiFile midi)
    {
        foreach (var events in midi.Tracks)
            foreach (var ev in events)
                if (ev.Status == MidiFile.Meta && ev.Data1 == MidiFile.SetTempo && ev.MetaData is { Length: 3 } b)
                {
                    int micros = (b[0] << 16) | (b[1] << 8) | b[2];
                    if (micros > 0)
                        return micros;
                }
        return 500000;
    }

    // ── Channel map ──────────────────────────────────────────────────────

    /// <summary>
    /// Picks which MIDI channel feeds which NES channel, from the song's own
    /// statistics (the old fixed 0-3 identity map silently dropped every
    /// note on channels 4-15 - 60-84% of several tracks' notes):
    /// <list type="number">
    /// <item>Count note-ons per MIDI channel.</item>
    /// <item>Noise: channel 9 (General MIDI drums) if it has more notes than
    /// channel 3, else channel 3.</item>
    /// <item>Square1/Square2/Triangle: the 3 densest remaining channels
    /// (ties: lower channel first).</item>
    /// <item>Triangle: the one of those with the lowest average note (the
    /// APU's bass voice); the squares get the other two, lower channel
    /// first.</item>
    /// </list>
    /// Returns {midi channel -> NES channel 0-3}, in the order the converter
    /// processes them (that order is also the order its log lines appear in).
    /// </summary>
    public static List<KeyValuePair<int, int>> SelectChannelMap(MidiFile midi, string? name, Action<string> log)
    {
        var noteCounts = new Dictionary<int, int>();
        var pitchSum = new Dictionary<int, int>();
        // First-seen order, like the Python dict, for the dropped-note sum.
        var seenOrder = new List<int>();
        foreach (var events in midi.Tracks)
            foreach (var ev in events)
                if (ev.Command == MidiFile.NoteOn && ev.Data2 > 0)
                {
                    int ch = ev.Channel;
                    if (!noteCounts.ContainsKey(ch))
                    {
                        noteCounts[ch] = 0;
                        pitchSum[ch] = 0;
                        seenOrder.Add(ch);
                    }
                    noteCounts[ch] += 1;
                    pitchSum[ch] += ev.Data1;
                }

        int Count(int ch) => noteCounts.TryGetValue(ch, out int c) ? c : 0;

        int noiseCh = Count(9) > Count(3) ? 9 : 3;

        // OrderByDescending is stable: equal counts keep ascending channel
        // order, exactly like Python's sorted(..., reverse=True).
        var remaining = Enumerable.Range(0, 16)
            .Where(ch => ch != noiseCh && Count(ch) > 0)
            .OrderByDescending(Count)
            .ToList();
        var top3 = remaining.Take(3).ToList();

        string label = name != null ? $" ({name})" : "";
        if (top3.Count == 0)
        {
            log($"[midi2nes] channel map{label}: no channels left besides noise - " +
                "falling back to the original identity map {0:0,1:1,2:2,3:3}");
            return new List<KeyValuePair<int, int>> { new(0, 0), new(1, 1), new(2, 2), new(3, 3) };
        }

        // min() keeps the FIRST minimum in top3's order.
        int triCh = top3[0];
        double best = (double)pitchSum[triCh] / noteCounts[triCh];
        foreach (int ch in top3)
        {
            double avg = (double)pitchSum[ch] / noteCounts[ch];
            if (avg < best)
            {
                best = avg;
                triCh = ch;
            }
        }
        var others = top3.Where(ch => ch != triCh).OrderBy(ch => ch).ToList();

        var mapping = new List<KeyValuePair<int, int>> { new(noiseCh, 3), new(triCh, 2) };
        for (int i = 0; i < others.Count; i++)
            mapping.Add(new(others[i], i)); // 0 = square1, 1 = square2

        var parts = new List<string>
        {
            $"noise<-ch{noiseCh}({Count(noiseCh)}n)",
            $"triangle<-ch{triCh}({Count(triCh)}n)",
        };
        for (int i = 0; i < others.Count; i++)
            parts.Add($"square{i + 1}<-ch{others[i]}({Count(others[i])}n)");

        var mapped = new HashSet<int>(mapping.Select(kv => kv.Key));
        int dropped = seenOrder.Where(ch => !mapped.Contains(ch)).Sum(ch => noteCounts[ch]);
        int total = seenOrder.Sum(ch => noteCounts[ch]);
        string pct = total != 0 ? (100.0 * dropped / total).ToString("F1", CultureInfo.InvariantCulture) : "0.0";
        log($"[midi2nes] channel map{label}: {string.Join(", ", parts)} " +
            $"({dropped}/{total} notes on other channels still dropped, {pct}%)");
        return mapping;
    }

    // ── Stream building blocks ───────────────────────────────────────────

    public static void EmitWait(List<int> stream, int frames)
    {
        while (frames > 0)
        {
            if (frames <= 63)
            {
                stream.Add(0x80 + frames);
                frames = 0;
            }
            else if (frames <= 255)
            {
                stream.Add(0xD0);
                stream.Add(frames);
                frames = 0;
            }
            else
            {
                stream.Add(0xD0);
                stream.Add(255);
                frames -= 255;
            }
        }
    }

    /// <summary>Drops a note-off that is immediately followed by a note-on
    /// (optionally with a volume change in between) - the note-on silences
    /// and re-pitches the channel anyway.</summary>
    public static List<int> CompressPattern(List<int> stream)
    {
        var o = new List<int>(stream.Count);
        int n = stream.Count;
        for (int i = 0; i < n; i++)
        {
            int b = stream[i];
            if (b == 0x80 && i + 1 < n)
            {
                int nxt = stream[i + 1];
                if (nxt < 0x80)
                    continue;
                if (nxt >= 0xC0 && nxt < 0xD0 && i + 2 < n && stream[i + 2] < 0x80)
                    continue;
            }
            o.Add(b);
        }
        return o;
    }

    // ── Cap-1 retiming ───────────────────────────────────────────────────
    //
    // The runtime decoder processes exactly ONE opcode per channel per NMI
    // frame (AUDIO_LOOP_CAP = 1): an instant opcode (note on/off, volume)
    // costs one whole frame, a wait W costs W frames, the 0xFF loop costs
    // one. Waits sized to the pure musical gap made every instant add a
    // frame of drift ("slow and jittery"), each channel at its own rate, and
    // each channel looped at its own last-event time. RetimeForCap1() fixes
    // all of it at build time:
    //   1. every wait is shortened by the instants since the previous wait
    //      (their decode frames pre-pay that part of the gap);
    //   2. a gap too dense to shorten carries the shortfall as debt repaid
    //      from the next wait - bounded error, never accumulating;
    //   3. every channel is padded to one SHARED loop period (the whole song
    //      length) minus 1 frame for the 0xFF read, so all 4 stay
    //      phase-locked forever.

    readonly record struct Tok(bool IsWait, int V);

    /// <summary>Byte stream -> instant/wait tokens, consecutive waits merged
    /// into one musical gap. Stops at 0xFF.</summary>
    static List<Tok> Tokenize(List<int> stream)
    {
        var toks = new List<Tok>();
        int i = 0;
        int n = stream.Count;
        while (i < n)
        {
            int b = stream[i];
            if (b == 0xFF)
                break;
            int w;
            if (b == 0xD0)
            {
                w = stream[i + 1];
                i += 2;
            }
            else if (b >= 0x81 && b <= 0xBF)
            {
                w = b - 0x80;
                i += 1;
            }
            else
            {
                toks.Add(new Tok(false, b));
                i += 1;
                continue;
            }
            if (toks.Count > 0 && toks[^1].IsWait)
                toks[^1] = new Tok(true, toks[^1].V + w);
            else
                toks.Add(new Tok(true, w));
        }
        return toks;
    }

    /// <summary>
    /// Collapses each run of same-instant opcodes to its audible final state:
    /// a monophonic channel can only express the LAST same-instant note and
    /// volume, and under cap 1 every intermediate burns a frame (a chord
    /// becomes a smeared arpeggio). Kept: the last note-on, the last volume,
    /// and a note-off only if no same-instant note-on comes after it. Order:
    /// note-on first (pitch exactly on time), then volume, then a trailing
    /// note-off (a deliberate same-instant on+off is a 1-frame blip).
    /// </summary>
    static List<Tok> CollapseSameInstant(List<Tok> toks)
    {
        var o = new List<Tok>();
        var group = new List<int>();

        void Flush()
        {
            if (group.Count == 0)
                return;
            int? lastOn = null;
            int lastOnIdx = -1;
            int? lastVol = null;
            int lastOffIdx = -1;
            for (int gi = 0; gi < group.Count; gi++)
            {
                int b = group[gi];
                if (b < 0x80)
                {
                    lastOn = b;
                    lastOnIdx = gi;
                }
                else if (b == 0x80)
                    lastOffIdx = gi;
                else
                    lastVol = b;
            }
            if (lastOn is int on)
                o.Add(new Tok(false, on));
            if (lastVol is int vol)
                o.Add(new Tok(false, vol));
            if (lastOffIdx >= 0 && lastOffIdx > lastOnIdx)
                o.Add(new Tok(false, 0x80));
            else if (lastOffIdx >= 0 && lastOn == null)
                o.Add(new Tok(false, 0x80));
            group.Clear();
        }

        foreach (var t in toks)
        {
            if (!t.IsWait)
                group.Add(t.V);
            else
            {
                Flush();
                if (o.Count > 0 && o[^1].IsWait)
                    o[^1] = new Tok(true, o[^1].V + t.V);
                else
                    o.Add(t);
            }
        }
        Flush();
        return o;
    }

    /// <summary>The core cap-1 compensation: each wait shortened by the
    /// instants before it, over-dense gaps carried as debt.</summary>
    static (List<Tok> Adj, int Emitted) Compensate(List<Tok> toks)
    {
        var adj = new List<Tok>(toks.Count);
        int emitted = 0;
        int pending = 0;
        int debt = 0;
        foreach (var t in toks)
        {
            if (!t.IsWait)
            {
                adj.Add(t);
                emitted += 1;
                pending += 1;
            }
            else
            {
                int needed = pending + debt;
                pending = 0;
                if (t.V > needed)
                {
                    adj.Add(new Tok(true, t.V - needed));
                    emitted += t.V - needed;
                    debt = 0;
                }
                else
                    debt = needed - t.V;
            }
        }
        return (adj, emitted);
    }

    /// <summary>
    /// Rewrites waits so every event lands on its intended frame under the
    /// cap-1 decoder and the stream loops in exactly <paramref name="totalFrames"/>.
    /// A channel genuinely denser than one opcode per frame is thinned at the
    /// congestion point, lowest musical priority first (a volume change, then
    /// a note-off a later note-on supersedes, then a non-final note-on) -
    /// bounded, logged event loss, because a stretched loop would desync the
    /// channel from its 3 siblings forever. Expects a CompressPattern()-ed
    /// stream ending in 0xFF; returns the retimed stream, also 0xFF-ended.
    /// </summary>
    public static List<int> RetimeForCap1(List<int> stream, int totalFrames, string? channelName, Action<string> log)
    {
        var toks = CollapseSameInstant(Tokenize(stream));

        int droppedVol = 0, droppedOff = 0, droppedOn = 0;
        while (true)
        {
            var (_, emittedFrames) = Compensate(toks);
            int deficit = emittedFrames - (totalFrames - 1);
            if (deficit <= 2)
                break; // small trailing deficits are reclaimed after the pad step
            int pending = 0;
            int debt = 0;
            int runStart = 0;
            int victim = -1;
            int victimKind = 0; // 1 vol, 2 off, 3 on
            for (int k = 0; k < toks.Count; k++)
            {
                var t = toks[k];
                if (!t.IsWait)
                {
                    pending += 1;
                    continue;
                }
                int needed = pending + debt;
                if (t.V < needed)
                {
                    var seg = new List<int>();
                    for (int j = runStart; j < k; j++)
                        if (!toks[j].IsWait)
                            seg.Add(j);
                    foreach (int j in seg)
                        if (toks[j].V >= 0xC0 && toks[j].V <= 0xCF)
                        {
                            victim = j;
                            victimKind = 1;
                            break;
                        }
                    if (victim < 0)
                        foreach (int j in seg)
                            if (toks[j].V == 0x80)
                            {
                                victim = j;
                                victimKind = 2;
                                break;
                            }
                    if (victim < 0)
                    {
                        var ons = seg.Where(j => toks[j].V < 0x80).ToList();
                        if (ons.Count > 1)
                        {
                            victim = ons[0];
                            victimKind = 3;
                        }
                    }
                    break;
                }
                debt = 0;
                pending = 0;
                runStart = k + 1;
            }
            if (victim < 0)
                break; // congestion only in the trailing run - reclaim handles it
            toks.RemoveAt(victim);
            if (victimKind == 1) droppedVol++;
            else if (victimKind == 2) droppedOff++;
            else droppedOn++;
        }

        if (droppedVol + droppedOff + droppedOn > 0)
        {
            string nm = channelName != null ? $" ({channelName})" : "";
            log($"[midi2nes] channel overload{nm}: dropped {droppedVol} volume / " +
                $"{droppedOff} note-off / {droppedOn} note-on opcode(s) - denser " +
                "than the 1-opcode-per-frame cap=1 ceiling; thinned at the congestion " +
                "points to keep the shared loop period exact");
        }

        var (adj, emitted) = Compensate(toks);

        // Land the loop period EXACTLY: pad the remainder (-1 covers the 0xFF
        // read frame), or reclaim a small trailing deficit from the last
        // wait(s) - trailing events marginally early, never a stretched loop.
        int pad = totalFrames - 1 - emitted;
        if (pad > 0)
            adj.Add(new Tok(true, pad));
        else if (pad < 0)
        {
            int deficit = -pad;
            for (int k = adj.Count - 1; k >= 0; k--)
            {
                if (deficit <= 0)
                    break;
                if (adj[k].IsWait)
                {
                    int take = Math.Min(deficit, adj[k].V);
                    adj[k] = new Tok(true, adj[k].V - take);
                    deficit -= take;
                }
            }
            adj = adj.Where(t => !t.IsWait || t.V > 0).ToList();
        }

        var o = new List<int>();
        foreach (var t in adj)
        {
            if (!t.IsWait)
                o.Add(t.V);
            else
                EmitWait(o, t.V);
        }
        o.Add(0xFF);
        return o;
    }

    /// <summary>
    /// Cuts a stream to at most <paramref name="maxBytes"/> on an opcode
    /// boundary, always ending in 0xFF. With <paramref name="totalFrames"/>,
    /// the cut stream gets a note-off and is re-padded to the shared loop
    /// period, so a truncated channel loses its tail CONTENT but never its
    /// loop ALIGNMENT.
    /// </summary>
    public static (List<int> Stream, bool Truncated) TruncateStream(List<int> stream, int maxBytes = MaxChannelBytes, int totalFrames = 0)
    {
        if (stream.Count <= maxBytes)
            return (stream, false);
        int padReserve = 1 + 1;
        if (totalFrames != 0)
            padReserve += 2 * (totalFrames / 255 + 1);
        var o = new List<int>();
        int keptFrames = 0;
        int i = 0;
        int n = stream.Count;
        while (i < n)
        {
            int b = stream[i];
            int width = b == 0xD0 ? 2 : b == Bends.OpPitch || b == Bends.OpSlide ? 3 : 1;
            if (o.Count + width > maxBytes - padReserve)
                break;
            for (int k = 0; k < width && i + k < n; k++)
                o.Add(stream[i + k]);
            if (b == 0xD0)
                keptFrames += stream[i + 1];
            else if (b >= 0x81 && b <= 0xBF)
                keptFrames += b - 0x80;
            else if (b != 0xFF)
                keptFrames += 1; // instant opcode: 1 decode frame at cap 1
            i += width;
        }
        if (totalFrames != 0)
        {
            o.Add(0x80); // note-off; costs 1 decode frame
            keptFrames += 1;
            int pad = totalFrames - 1 - keptFrames;
            if (pad > 0)
                EmitWait(o, pad);
        }
        o.Add(0xFF);
        return (o, true);
    }

    // ── Conversion ───────────────────────────────────────────────────────

    /// <summary>
    /// The 4 channel streams for a song under <paramref name="channelMap"/>,
    /// retimed to the shared loop period (the song's full span in frames,
    /// across every track, end-of-track metas included). Streams are keyed
    /// by NES channel; a channel with no mapped source is absent (the caller
    /// fills it with a silent full-length loop). Not yet truncated.
    /// </summary>
    public static (Dictionary<int, List<int>> Streams, List<int> Order, int TotalFrames) Convert(
        MidiFile midi, List<KeyValuePair<int, int>> channelMap, Action<string> log)
        => Convert(midi, channelMap, log, ConvertOptions.Default, out _);

    /// <summary>As <see cref="Convert(MidiFile, List{KeyValuePair{int, int}}, Action{string})"/>,
    /// with <paramref name="options"/>. With bends on, each retimed tonal
    /// stream then gets its bend opcodes (<see cref="Bends"/>) and
    /// <paramref name="bends"/> says, per channel whose MIDI channel bends,
    /// what was done; it is empty with bends off.</summary>
    public static (Dictionary<int, List<int>> Streams, List<int> Order, int TotalFrames) Convert(
        MidiFile midi, List<KeyValuePair<int, int>> channelMap, Action<string> log,
        ConvertOptions options, out List<BendReport> bends)
    {
        bends = new List<BendReport>();
        int microsPerQuarter = FindTempo(midi);
        // Same operation order as the Python original - these floats feed
        // truncation to whole frames, so the order matters for identity.
        double framesPerTick = ((double)microsPerQuarter / midi.TicksPerQuarter * Fps) / 1_000_000.0;

        int totalTicks = 0;
        foreach (var events in midi.Tracks)
        {
            int t = 0;
            foreach (var ev in events)
                t += ev.DeltaTime;
            totalTicks = Math.Max(totalTicks, t);
        }
        int totalFrames = Math.Max((int)(totalTicks * framesPerTick), 2);

        var map = new Dictionary<int, int>();
        var order = new List<int>();
        foreach (var kv in channelMap)
        {
            map[kv.Key] = kv.Value;
            if (!order.Contains(kv.Value))
                order.Add(kv.Value);
        }
        var channelEvents = new Dictionary<int, List<(int Tick, MidiEvent Ev)>>();
        foreach (int nes in order)
            channelEvents[nes] = new List<(int, MidiEvent)>();

        foreach (var events in midi.Tracks)
        {
            int absoluteTick = 0;
            foreach (var ev in events)
            {
                absoluteTick += ev.DeltaTime;
                // Channel-voice messages only: a meta event (0xFF) would
                // otherwise read as "channel 15", a real leak once the
                // selector can map channel 15.
                if (ev.Status < 0xF0 && map.TryGetValue(ev.Channel, out int nes))
                    channelEvents[nes].Add((absoluteTick, ev));
            }
        }

        var streams = new Dictionary<int, List<int>>();
        foreach (int nes in order)
        {
            // Stable sort by tick (List.Sort isn't stable; OrderBy is).
            var events = channelEvents[nes].OrderBy(e => e.Tick).ToList();
            var stream = new List<int>();
            int currentVolume = -1;
            int channelVolume = 127;
            int lastVelocity = 127;
            int lastTick = 0;
            double frameAccum = 0.0;
            bool notePlaying = false;
            int lastNote = 0;

            // Elapsed ticks only become a wait right before a byte is
            // actually emitted - not at every event (pitch bends, other CCs,
            // repeated notes...). Flushing per event made 36.6% of all waits
            // pure waste, 99.6% of vectorplane's noise stream.
            void FlushWait(int tick)
            {
                int deltaTicks = tick - lastTick;
                frameAccum += deltaTicks * framesPerTick;
                int framesToWait = (int)frameAccum;
                frameAccum -= framesToWait;
                EmitWait(stream, framesToWait);
                lastTick = tick;
            }

            static int VolumeOf(int velocity, int channelVolume)
            {
                int effective = (velocity * channelVolume) / 127;
                int vol = effective >> 3;
                if (vol == 0 && effective > 0)
                    vol = 1;
                return vol;
            }

            foreach (var (tick, ev) in events)
            {
                if (ev.Command == MidiFile.NoteOn && ev.Data2 > 0)
                {
                    lastVelocity = ev.Data2;
                    int vol = VolumeOf(lastVelocity, channelVolume);
                    int note = ev.Data1;
                    bool volWillEmit = vol != currentVolume;
                    bool noteWillEmit = note < 128 && (!notePlaying || note != lastNote);
                    if (volWillEmit || noteWillEmit)
                    {
                        FlushWait(tick);
                        // Note BEFORE volume: under cap 1 only the first lands
                        // on time. A late pitch is an audible wrong-pitch
                        // blip; a late volume is a barely-perceptible attack
                        // softening. Pitch wins.
                        if (noteWillEmit)
                        {
                            stream.Add(note);
                            lastNote = note;
                        }
                        if (volWillEmit)
                        {
                            stream.Add(0xC0 | (vol & 0x0F));
                            currentVolume = vol;
                        }
                    }
                    if (note < 128)
                        notePlaying = true;
                }
                else if (ev.Command == MidiFile.ControlChange && ev.Data1 == 7)
                {
                    // Always tracked, even while silent: a later note-on
                    // needs the current value.
                    channelVolume = ev.Data2;
                    if (notePlaying)
                    {
                        int vol = VolumeOf(lastVelocity, channelVolume);
                        if (vol != currentVolume)
                        {
                            FlushWait(tick);
                            stream.Add(0xC0 | (vol & 0x0F));
                            currentVolume = vol;
                        }
                    }
                }
                else if (ev.Command == MidiFile.NoteOff || (ev.Command == MidiFile.NoteOn && ev.Data2 == 0))
                {
                    if (notePlaying)
                    {
                        FlushWait(tick);
                        stream.Add(0x80);
                        notePlaying = false;
                    }
                }
            }

            stream.Add(0xFF);
            var compressed = CompressPattern(stream);
            streams[nes] = RetimeForCap1(compressed, totalFrames, ChannelNames[nes], log);

            if (options.Bends)
            {
                int midiCh = channelMap.Where(kv => kv.Value == nes).Select(kv => kv.Key).First();
                var report = ApplyBends(streams, nes, midiCh, events, framesPerTick, totalFrames, options);
                if (report != null)
                {
                    bends.Add(report);
                    log(Bends.Describe(report, null));
                }
            }
        }
        return (streams, order, totalFrames);
    }

    /// <summary>
    /// Adds one channel's bend opcodes to its retimed stream (in place in
    /// <paramref name="streams"/>). Null when its MIDI channel never bends
    /// away from the centre. The noise is counted and left alone. Notes are
    /// never cut to make room for bends: a channel whose bends would take it
    /// past <see cref="MaxChannelBytes"/> is fitted again with the moving-
    /// pitch tolerance doubled (up to <see cref="Bends.MaxToleranceCents"/>),
    /// then with slides off (held bent pitches only), then without bends;
    /// a channel truncated even without its bends gets none. The report says
    /// which.
    /// </summary>
    static BendReport? ApplyBends(Dictionary<int, List<int>> streams, int nes, int midiCh,
        List<(int Tick, MidiEvent Ev)> events, double framesPerTick, int totalFrames, ConvertOptions options)
    {
        var src = Bends.BuildSource(events, framesPerTick, totalFrames);
        if (src.NonZeroEvents == 0)
            return null;
        var baseReport = new BendReport
        {
            NesChannel = nes,
            MidiChannel = midiCh,
            Events = src.Events,
            NonZeroEvents = src.NonZeroEvents,
            RangeSemitones = src.MaxRange,
            RangeFromRpn = src.RangeFromRpn,
            ToleranceCents = options.BendToleranceCents,
            RequestedToleranceCents = options.BendToleranceCents,
        };
        if (nes == 3)
            return baseReport with { Ignored = true };
        var original = streams[nes];
        if (original.Count > MaxChannelBytes)
            return baseReport with
            {
                Dropped = true,
                DropReason = $"the channel is over {MaxChannelBytes} bytes even without them (it is truncated)",
            };
        double tol = options.BendToleranceCents;
        while (true)
        {
            var (fitted, report) = Bends.Fit(original, src, tol, nes, midiCh);
            if (fitted.Count <= MaxChannelBytes)
            {
                streams[nes] = fitted;
                return report with { RequestedToleranceCents = options.BendToleranceCents };
            }
            if (tol * 2 > Bends.MaxToleranceCents)
                break;
            tol *= 2;
        }
        // Still too big: keep the held bent pitches (and bent note-ons), drop the slides.
        var (steps, stepsReport) = Bends.Fit(original, src, tol, nes, midiCh, slides: false);
        if (steps.Count <= MaxChannelBytes)
        {
            streams[nes] = steps;
            return stepsReport with { RequestedToleranceCents = options.BendToleranceCents };
        }
        return baseReport with
        {
            Dropped = true,
            DropReason = $"even their held pitches alone would take the channel to {steps.Count} bytes, over {MaxChannelBytes}",
        };
    }

    /// <summary>
    /// The whole pipeline for one song, exactly what the VRUN build bakes:
    /// channel map, conversion, then per NES channel a silent full-length
    /// loop for an unmapped channel and truncation to <see cref="MaxChannelBytes"/>.
    /// <paramref name="log"/> receives the same diagnostic lines, in the same
    /// order, that the build prints (build_music.py parses them).
    /// </summary>
    public static SongResult ConvertSong(MidiFile midi, string name, Action<string> log)
        => ConvertSong(midi, name, log, ConvertOptions.Default);

    /// <summary>As <see cref="ConvertSong(MidiFile, string, Action{string})"/>,
    /// with <paramref name="options"/>. With bends on, the log gains one line
    /// per bending channel and a final "bend channels" line, and the result's
    /// <see cref="SongResult.Bends"/> holds the reports.</summary>
    public static SongResult ConvertSong(MidiFile midi, string name, Action<string> log, ConvertOptions options)
    {
        var map = SelectChannelMap(midi, name, log);
        var (streams, _, totalFrames) = Convert(midi, map, log, options, out var bendReports);
        log($"[midi2nes] loop period ({name}): {totalFrames} frames " +
            $"({(totalFrames / Fps).ToString("F2", CultureInfo.InvariantCulture)}s) - all 4 channels phase-locked to it");

        var final = new byte[4][];
        var untruncatedLengths = new int[4];
        var truncated = new bool[4];
        for (int nes = 0; nes < 4; nes++)
        {
            untruncatedLengths[nes] = streams.TryGetValue(nes, out var s0) ? s0.Count : 1;
            // A channel with no mapped source still needs a full-length
            // silent loop (not a bare 0xFF), or it would re-decode its loop
            // opcode every frame AND break the shared loop period.
            var data = streams.TryGetValue(nes, out var s) ? s : RetimeForCap1(new List<int> { 0xFF }, totalFrames, null, log);
            var (cut, wasTruncated) = TruncateStream(data, MaxChannelBytes, totalFrames);
            truncated[nes] = wasTruncated;
            if (wasTruncated)
                log($"[midi2nes] truncated {ChannelNames[nes]} to {cut.Count} bytes, " +
                    $"re-padded to the shared {totalFrames}-frame loop " +
                    $"(exceeded MAX_CHANNEL_BYTES={MaxChannelBytes} - see truncate_stream()'s own header)");
            if (cut.Count == 0)
                cut = new List<int> { 0xFF };
            final[nes] = cut.Select(b => (byte)b).ToArray();
        }
        if (options.Bends)
        {
            // The build reads this line (build_music.py) and the bend
            // decoders follow the streams themselves, so it is a report, not
            // a contract: which channels hold bend opcodes.
            var played = Enumerable.Range(0, 3).Where(c => final[c].Length > 0 && HasBendOps(final[c])).Select(c => ChannelNames[c]).ToList();
            log($"[midi2nes] bend channels ({name}): {(played.Count > 0 ? string.Join(",", played) : "none")}");
        }
        return new SongResult(name, final, totalFrames, map, untruncatedLengths, truncated) { Bends = bendReports };
    }

    /// <summary>True when a stream holds a bend opcode (0xD1/0xD2) - the
    /// decoder that plays it must be a bend decoder.</summary>
    public static bool HasBendOps(IReadOnlyList<byte> stream)
    {
        int i = 0;
        while (i < stream.Count)
        {
            int b = stream[i];
            if (b == 0xFF)
                return false;
            if (b == Bends.OpPitch || b == Bends.OpSlide)
                return true;
            i += b == 0xD0 ? 2 : 1;
        }
        return false;
    }
}

/// <summary>One converted song: the 4 channel streams exactly as baked into
/// the ROM (square1, square2, triangle, noise), the shared loop period, and
/// what the converter decided.</summary>
public sealed record SongResult(
    string Name,
    byte[][] Channels,
    int LoopFrames,
    List<KeyValuePair<int, int>> ChannelMap,
    int[] UntruncatedLengths,
    bool[] Truncated)
{
    /// <summary>One report per channel whose MIDI channel bends (bends on
    /// only; empty otherwise).</summary>
    public IReadOnlyList<BendReport> Bends { get; init; } = Array.Empty<BendReport>();
}
