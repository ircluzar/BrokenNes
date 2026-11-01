// Round 8 (record, P2): the checks of the live MIDI out. The truth is the game's own S-DSP writes (a recording made at the same time, read here with a reader of its own, not RecNotes);
// the MIDI stream is read back message by message as FL would receive it. Every key-on must be a note-on in the same order on its instrument's channel, with the key-on level as
// velocity and the key-on pitch register reproduced (key + bend from the sample's root) within one register step; every pitch and volume value the game held for 4 S-DSP samples or more
// while the note was its channel's newest must appear, in order, among the bends / controller-7 values sent for it; every note ends.
using BrokenNes2.Record;

namespace BrokenNes.FruityHost;

internal static class LiveChecks
{
    public sealed class Key
    {
        public int Voice, Srcn, Channel, Pitch0, Level0;
        public uint Start, End;
        public bool KoffHeld, EndedInside;
        public List<(uint T, int P)> Pitches = new();
        public List<(uint T, int L)> Levels = new();
    }

    public sealed class Note
    {
        public int Channel, Key, Velocity;
        public bool Off, OffByNote;
        public List<int> Bends = new(), Ccs = new();
    }

    /// <summary>The key-ons of a recording, in the order the S-DSP saw them (a write's bits from voice 0 up), with each voice's pitch and level until its key-off or next key-on.</summary>
    public static List<Key> Truth(RecSession s)
    {
        var regs = (byte[])s.BaseRegs.Clone();
        var open = new Key?[8];
        var list = new List<Key>();
        var chanOf = new Dictionary<int, int>();
        int P(int v) => regs[v * 16 + 2] | (regs[v * 16 + 3] & 0x3F) << 8;
        int L(int v) => Math.Min(127, Math.Max(Math.Abs((int)(sbyte)regs[v * 16]), Math.Abs((int)(sbyte)regs[v * 16 + 1])));
        for (int i = 0; i < s.Count; i++)
        {
            s.Get(i, out uint t, out int reg, out int val);
            if (reg >= 0x80) continue;                       // (a recording's instrument markers)
            if (reg == 0x4C)
            {
                regs[reg] = (byte)val;
                for (int v = 0; v < 8; v++)
                {
                    if ((val >> v & 1) == 0) continue;
                    if (open[v] is { } o) { o.End = t; o.EndedInside = true; open[v] = null; }
                    int srcn = regs[v * 16 + 4];
                    if (!chanOf.TryGetValue(srcn, out int ch)) chanOf[srcn] = ch = RecTranscribe.ChannelOf(chanOf.Count);
                    var k = new Key { Voice = v, Srcn = srcn, Channel = ch, Pitch0 = P(v), Level0 = L(v), Start = t, End = t, KoffHeld = (regs[0x5C] >> v & 1) != 0 };
                    k.Pitches.Add((t, k.Pitch0)); k.Levels.Add((t, k.Level0));
                    list.Add(k);
                    if (!k.KoffHeld) open[v] = k;
                }
                continue;
            }
            if (reg == 0x5C)
            {
                int rising = val & ~regs[0x5C];
                regs[reg] = (byte)val;
                for (int v = 0; v < 8; v++) if ((rising >> v & 1) != 0 && open[v] is { } o) { o.End = t; o.EndedInside = true; open[v] = null; }
                continue;
            }
            regs[reg] = (byte)val;
            int vv = reg >> 4, lo = reg & 15;
            if (lo > 3 || open[vv] is not { } n) continue;
            if (lo >= 2) { int p = P(vv); if (n.Pitches[^1].T == t) n.Pitches[^1] = (t, p); else n.Pitches.Add((t, p)); }
            else { int l = L(vv); if (n.Levels[^1].T == t) n.Levels[^1] = (t, l); else n.Levels.Add((t, l)); }
        }
        foreach (var o in open) if (o != null) o.End = (uint)s.Length;
        return list;
    }

    /// <summary>A MIDI stream (Status | Data1 &lt;&lt; 8 | Data2 &lt;&lt; 16 [| Port &lt;&lt; 24]) read as a receiver would: notes in order, each with the bends and controller-7 values its channel carried while it was the newest.</summary>
    public static List<Note> Parse(IEnumerable<uint> msgs, out int rpnRange, out List<string> problems)
    {
        var list = new List<Note>();
        problems = new List<string>();
        rpnRange = -1;
        var bend = new int[16]; var cc = new int[16]; Array.Fill(cc, 100);
        var newest = new Note?[16];
        var rpn = new int[16]; Array.Fill(rpn, 0x3FFF);
        var rangeSeen = new bool[16];
        foreach (uint m in msgs)
        {
            int st = (int)(m & 0xFF), d1 = (int)(m >> 8 & 0x7F), d2 = (int)(m >> 16 & 0x7F), ch = st & 15;
            switch (st & 0xF0)
            {
                case 0x90 when d2 > 0:
                    if (!rangeSeen[ch]) problems.Add($"a note on channel {ch + 1} before its bend range (RPN 0)");
                    var n = new Note { Channel = ch, Key = d1, Velocity = d2 };
                    n.Bends.Add(bend[ch]); n.Ccs.Add(cc[ch]);
                    newest[ch] = n; list.Add(n);
                    break;
                case 0x80:
                case 0x90:
                {
                    var o = list.LastOrDefault(x => x.Channel == ch && x.Key == d1 && !x.Off);
                    if (o == null) { problems.Add($"a note-off of key {d1} on channel {ch + 1} with no note"); break; }
                    o.Off = true; o.OffByNote = true;
                    if (ReferenceEquals(newest[ch], o)) newest[ch] = null;
                    break;
                }
                case 0xE0:
                    bend[ch] = (d1 | d2 << 7) - 8192;
                    newest[ch]?.Bends.Add(bend[ch]);
                    break;
                case 0xB0:
                    if (d1 == 101) rpn[ch] = (rpn[ch] & 0x7F) | d2 << 7;
                    else if (d1 == 100) rpn[ch] = (rpn[ch] & ~0x7F) | d2;
                    else if (d1 == 6 && rpn[ch] == 0) { rangeSeen[ch] = true; if (rpnRange < 0) rpnRange = d2; else if (rpnRange != d2) problems.Add($"channel {ch + 1} has bend range {d2}, another {rpnRange}"); }
                    else if (d1 == 7) { cc[ch] = d2; newest[ch]?.Ccs.Add(d2); }
                    else if (d1 == 123) { foreach (var x in list) if (x.Channel == ch) x.Off = true; newest[ch] = null; }
                    break;
            }
        }
        return list;
    }

    public sealed class Stats
    {
        public int Keys, Notes, ChannelOk, VelocityOk, Pitch0Ok, Pitch0Checked, Values, ValuesFound, Levels, LevelsFound, LevelsBeyond, Hanging, KeyedOff, NoteOffs;
        public List<string> Problems = new();
        public string Line() => $"{Notes} note-ons for {Keys} key-ons: channel right on {ChannelOk}, velocity = key-on level on {VelocityOk}, key-on pitch register reproduced (key + bend from the root) within one step on {Pitch0Ok} of {Pitch0Checked}; "
            + $"{ValuesFound:N0} of {Values:N0} pitch values the game held 4 samples or more found in order among the bends; {LevelsFound:N0} of {Levels:N0} volume values among the controller-7 values ({LevelsBeyond} past what controller 7 can say); {NoteOffs} of the {KeyedOff} notes the game released got their note-off; {Hanging} notes never ended";
        public bool AllGood => Keys == Notes && ChannelOk == Keys && VelocityOk == Keys && Pitch0Ok == Pitch0Checked && ValuesFound == Values && LevelsFound == Levels && Hanging == 0 && NoteOffs == KeyedOff;
    }

    /// <summary>The register a live note says at bend <paramref name="b"/>: its key and bend as MIDI cents, from the root it was keyed with.</summary>
    public static int Register(int key, int b, int range, int root) => Math.Clamp((int)Math.Round(4096 * Math.Pow(2, (key * 100.0 + b * range * 100.0 / 8192.0 - root) / 1200.0)), 1, 0x3FFF);

    public static Stats Compare(List<Key> truth, List<Note> live, int[] roots, int range)
    {
        var st = new Stats { Keys = truth.Count, Notes = live.Count, Hanging = live.Count(n => !n.Off) };
        // the window of each key-on: until its key-off or the next key-on on its channel (the channel follows its newest note)
        var nextOnChannel = new uint[truth.Count];
        var lastOn = new Dictionary<int, int>();
        for (int i = truth.Count - 1; i >= 0; i--)
        {
            nextOnChannel[i] = lastOn.TryGetValue(truth[i].Channel, out int j) ? truth[j].Start : uint.MaxValue;
            lastOn[truth[i].Channel] = i;
        }
        for (int i = 0; i < Math.Min(truth.Count, live.Count); i++)
        {
            var k = truth[i]; var n = live[i];
            if (k.EndedInside) { st.KeyedOff++; if (n.OffByNote) st.NoteOffs++; else if (st.Problems.Count < 6) st.Problems.Add($"key-on {i} (voice {k.Voice}): the game released it, the note got no note-off"); }
            if (n.Channel == k.Channel) st.ChannelOk++; else if (st.Problems.Count < 6) st.Problems.Add($"key-on {i} (voice {k.Voice}, sample {k.Srcn}): channel {n.Channel + 1}, its instrument's is {k.Channel + 1}");
            if (n.Velocity == Math.Clamp(Math.Max(1, k.Level0), 1, 127)) st.VelocityOk++; else if (st.Problems.Count < 6) st.Problems.Add($"key-on {i}: velocity {n.Velocity}, the key-on level {k.Level0}");
            int root = i < roots.Length ? roots[i] : 6000;
            int r0 = Register(n.Key, n.Bends[0], range, root);
            if (n.Key is > 0 and < 127)
            {
                st.Pitch0Checked++;
                if (Math.Abs(r0 - k.Pitch0) <= 1) st.Pitch0Ok++;
                else if (st.Problems.Count < 6) st.Problems.Add($"key-on {i} (sample {k.Srcn}): the note says ${r0:X4} (key {n.Key}, bend {n.Bends[0]}, root {root / 100.0:0.00}), the game keyed ${k.Pitch0:X4}");
            }
            uint end = Math.Min(k.End, nextOnChannel[i]);
            // pitch: the values held 4 samples or more inside the window, in order, each within one step of a bend sent for this note (in order)
            var want = Held(k.Pitches.Select(x => (x.T, x.P)).ToList(), end);
            var got = n.Bends.Select(b => Register(n.Key, b, range, root)).ToList();
            int gi = 0, found = 0;
            foreach (int w in want)
            {
                int at = got.FindIndex(gi, x => Math.Abs(x - w) <= 1);
                if (at < 0) continue;
                found++; gi = at;
            }
            st.Values += want.Count; st.ValuesFound += found;
            if (found < want.Count && st.Problems.Count < 6) st.Problems.Add($"key-on {i} (sample {k.Srcn}): {want.Count - found} of its {want.Count} held pitch values not among its {got.Count} bends ({string.Join(" ", want.Take(6).Select(x => $"${x:X4}"))} ... vs {string.Join(" ", got.Take(6).Select(x => $"${x:X4}"))})");
            // volume: controller 7 is the level relative to the key-on level (100 = as keyed)
            int vel = Math.Max(1, Math.Min(127, k.Level0));
            var wantL = Held(k.Levels.Select(x => (x.T, x.L)).ToList(), end);
            var gotL = n.Ccs.Select(c => (int)Math.Round(c * vel / 100.0)).ToList();
            gi = 0;
            foreach (int w in wantL)
            {
                if (w * 100.0 / vel > 127.4) { st.LevelsBeyond++; continue; }
                st.Levels++;
                int at = gotL.FindIndex(gi, x => Math.Abs(x - w) <= 1);
                if (at < 0) { if (st.Problems.Count < 6) st.Problems.Add($"key-on {i}: level {w} not among its controller-7 values ({string.Join(" ", n.Ccs.Take(8))}, key-on level {vel})"); continue; }
                st.LevelsFound++; gi = at;
            }
        }
        if (truth.Count != live.Count) st.Problems.Insert(0, $"{live.Count} note-ons for {truth.Count} key-ons");
        return st;

        static List<int> Held(List<(uint T, int V)> l, uint end)
        {
            var r = new List<int>();
            for (int j = 0; j < l.Count; j++)
            {
                uint t0 = l[j].T, t1 = j + 1 < l.Count ? Math.Min(l[j + 1].T, end) : end;
                if (t0 >= end || t1 < t0 + 4) continue;
                if (r.Count == 0 || r[^1] != l[j].V) r.Add(l[j].V);
            }
            return r;
        }
    }

    /// <summary>The library check: the synthetic driver's 20 s (four instruments, one loaded under another's number at 12 s) played into the live out the way the plugin does (the worker's root
    /// estimate after each key-on), against the driver's own writes; the roots against the samples' known pitch; nothing allocated per write.</summary>
    public static void Live(ImodelReport r)
    {
        var drv = new SynthDriver(132.5);
        var rec = new SpcRecorder(); rec.Arm(); for (int i = 0; i < 40; i++) rec.Feed();
        var live = new RecLive { KeyRootLog = new int[100_000] };
        live.Attach(drv.Aram, drv.Regs);
        int primed = live.Prime(drv.Aram, drv.Regs[0x5D]);
        var msgs = new List<uint>();
        drv.Play(rec, 20, (reg, val, t) =>
        {
            live.OnWrite(reg, val, t);
            if (reg == 0x4C) live.EstimatePending();             // (the worker, at once)
            while (live.TryDequeue(out uint m)) msgs.Add(m);
        });
        live.EndFrame(); live.AllOff();
        while (live.TryDequeue(out uint m)) msgs.Add(m);
        var s = rec.Finish(drv.Aram, drv.Regs, "Live", "synthetic driver", "", 1_760_000_000, 5);
        var truth = Truth(s);
        var notes = Parse(msgs, out int range, out var problems);
        foreach (var p in problems.Take(4)) r.Expect(false, "the stream: " + p);
        r.Expect(range == live.Range, $"the bend range announced is {range}, the live out bends with {live.Range}");
        var st = Compare(truth, notes, live.KeyRootLog, live.Range);
        foreach (var p in st.Problems.Take(6)) r.Expect(false, p);
        r.Expect(st.AllGood, st.Line());
        r.Expect(truth.Count == drv.Notes.Count, $"the reader found {truth.Count} key-ons, the driver played {drv.Notes.Count}");
        // the roots: A, B, C primed before the first note; D's (loaded under B's number at 12 s) estimated after its first key-on, which is keyed as if the sample's rate were C5 and counted
        for (int i = 0; i < 3; i++)
        {
            var ins = SynthDriver.Instruments[i];
            double want = 6900 + 1200 * Math.Log2(SynthDriver.F0(i) / 440);
            int? got = i == 1 ? null : live.RootOf(ins.Srcn);
            if (i != 1) r.Expect(got is { } g && Math.Abs(g - want) <= 3, $"sample {ins.Srcn}: root {(got is { } g2 ? (g2 / 100.0).ToString("0.00") : "unknown")}, the sample is {want / 100:0.00}");
        }
        double wantD = 6900 + 1200 * Math.Log2(SynthDriver.F0(3) / 440);
        r.Expect(live.RootOf(7) is { } gd && Math.Abs(gd - wantD) <= 3, $"sample 7 after D took its place: root {live.RootOf(7)}, D is {wantD / 100:0.00}");
        r.Expect(primed >= 3 && live.RootUnknown == 1, $"{primed} roots primed, {live.RootUnknown} notes keyed before their root was known (D's first only)");
        // nothing allocated per write: the same writes again into a fresh live out, no reader (twice: the first pass warms the code up)
        long a0 = 0, a1 = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            var again = new RecLive();
            var aram = new byte[0x10000]; Array.Copy(drv.Aram, aram, aram.Length);
            again.Attach(aram, s.BaseRegs);
            again.Prime(aram, s.BaseRegs[0x5D]);
            a0 = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < s.Count; i++)
            {
                s.Get(i, out uint t, out int reg, out int val);
                if (reg >= 0x80) continue;
                again.OnWrite((byte)reg, (byte)val, t);
                if ((i & 63) == 0) { while (again.TryDequeue(out _)) { } again.EndFrame(); }
            }
            again.AllOff();
            while (again.TryDequeue(out _)) { }
            a1 = GC.GetAllocatedBytesForCurrentThread();
        }
        r.Expect(a1 - a0 == 0, $"{a1 - a0:N0} bytes allocated by {s.Count:N0} writes into the live out");
        r.Note(st.Line());
        r.Note($"live: {live.Describe()}; {msgs.Count:N0} messages; {primed} roots primed; 0 bytes per write ({s.Count:N0} writes)");
    }
}
