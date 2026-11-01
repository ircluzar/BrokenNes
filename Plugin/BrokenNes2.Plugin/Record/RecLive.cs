// Round 8 (record, P2): live MIDI out. While a SNES game plays, the same notes a recording would transcribe are sent as MIDI as they happen: one MIDI channel per instrument (the sample
// number, in the order they are first keyed, channel 10 left out as the transcription does), a note-on at each key-on with the key from the sample's root note, a pitch bend for the rest
// of the pitch and for every move (range 12 semitones, announced with RPN 0 on each channel's first note), controller 7 for the volume relative to the key-on level (100 = as keyed), a
// note-off at each key-off. It runs on the audio thread inside the game's frames: nothing here allocates (the queue, the voice and channel tables are made once). The root of a sample is
// estimated on a worker thread the first time it is keyed (RecRoot, as the transcription does); notes keyed before it is known are keyed as if the sample's own rate were C5, and counted.
//
// What it does not do (the transcription does, offline): the driver's 12-TET offset per instrument (so a live note can be a few cents from the preset of the same instrument), the per-track
// bend range, lanes, the tempo. Pitch and volume writes are taken once they have held for two S-DSP samples (a pitch written in two halves is one move), or at the end of the frame.
namespace BrokenNes2.Record;

public sealed class RecLive
{
    public const int QueueSize = 8192;              // messages between two renders (a power of two)
    public const int DefaultRange = 12;
    private const int Unknown = int.MinValue;

    private readonly uint[] queue = new uint[QueueSize];
    private int head, tail;
    private readonly byte[] regs = new byte[128];
    private byte[] aram = new byte[0x10000];

    // per S-DSP voice: the note it sounds (key -1: none), its MIDI channel, its key-on level, the root it was keyed with (MIDI cents)
    private readonly int[] vKey = new int[8], vChan = new int[8], vLevel = new int[8], vRoot = new int[8];
    // per sample number: its channel (-1: not keyed yet), its root (MIDI cents), the start address the root was found for, a request for the worker
    private readonly int[] chanOf = new int[256], rootOf = new int[256], rootStart = new int[256], needRoot = new int[256];
    // per MIDI channel: the voice whose bend and controller 7 it carries, what was sent last, whether its bend range was announced
    private readonly int[] chanVoice = new int[16], chanBend = new int[16], chanCc = new int[16];
    private readonly bool[] chanReady = new bool[16];
    private int instruments, dirty;
    private long dirtyAt = -1;

    /// <summary>The roots of the samples the directory named when the live out started are known (<see cref="Prime"/>): it listens from then on.</summary>
    public volatile bool Primed;
    /// <summary>Tests: the root (MIDI cents) each note was keyed with, by note number (made by the test, filled without allocating).</summary>
    public int[]? KeyRootLog;
    /// <summary>The MIDI port FL routes the messages to (the Port byte of TMIDIOutMsg).</summary>
    public volatile int Port = 1;
    public int Range = DefaultRange;
    /// <summary>Counters for the window and the tests (written on the audio thread).</summary>
    public long NotesOn, NotesOff, Bends, Controllers, Dropped, RootUnknown, Recut, OutOfRange, Sent;
    public int Instruments => instruments;
    /// <summary>Set when the live out was switched off: the note-offs are queued, the next drain sends them and lets go.</summary>
    public volatile bool Ended;
    public int Pending => tail - head;

    public RecLive()
    {
        Array.Fill(vKey, -1);
        Array.Fill(chanOf, -1);
        Array.Fill(rootOf, Unknown);
        Array.Fill(rootStart, -1);
        Array.Fill(chanVoice, -1);
        Array.Fill(chanBend, int.MinValue);
        Array.Fill(chanCc, -1);
    }

    /// <summary>The sound unit it listens to (between frames, under the emulator's lock): its memory and its registers as they are now. Notes that sound already are not sent (their key-on was not seen).</summary>
    public void Attach(byte[] unitAram, byte[] unitRegs)
    {
        aram = unitAram;
        Array.Copy(unitRegs, regs, 128);
        dirty = 0; dirtyAt = -1;
    }

    /// <summary>Another game: the roots known are of the old one's samples; each sample's first key-on asks again (allocation-free).</summary>
    public void ForgetRoots() => Array.Fill(rootStart, -1);

    /// <summary>Another sound unit took over (a chip change): its memory from now on.</summary>
    public void Rebind(byte[] unitAram) => aram = unitAram;

    /// <summary>Every S-DSP write of the game (the audio thread, inside a frame; allocation-free).</summary>
    public void OnWrite(byte reg, byte value, long sample)
    {
        if (RecMutants.On(RecMutants.LiveOutSilent)) return;       // (the self-test's deliberate bug: nothing is sent)
        reg &= 0x7F;
        if (dirty != 0 && sample > dirtyAt + 1) Flush();
        int lo = reg & 0x0F, v = reg >> 4;
        if (reg == 0x4C)
        {
            Flush();
            regs[reg] = value;
            for (int i = 0; i < 8; i++) if ((value >> i & 1) != 0) KeyOn(i);
            return;
        }
        if (reg == 0x5C)
        {
            Flush();
            int rising = value & ~regs[0x5C];
            regs[reg] = value;
            for (int i = 0; i < 8; i++) if ((rising >> i & 1) != 0 && vKey[i] >= 0) NoteOff(i);
            return;
        }
        regs[reg] = value;
        if (lo <= 3 && vKey[v] >= 0) { dirty |= 1 << v; dirtyAt = sample; }
    }

    /// <summary>The end of a frame: moves still waiting are sent.</summary>
    public void EndFrame() { if (dirty != 0) Flush(); }

    private void Flush()
    {
        int d = dirty;
        dirty = 0;
        for (int v = 0; v < 8; v++)
        {
            if ((d >> v & 1) == 0 || vKey[v] < 0) continue;
            int ch = vChan[v];
            if (chanVoice[ch] != v) continue;                        // the channel follows its newest note (as the transcription's one channel per instrument)
            SendBend(ch, BendOf(v));
            int level = Level(v);
            int cc = Math.Clamp((int)Math.Round(100.0 * level / Math.Max(1, vLevel[v])), 0, 127);
            if (cc != chanCc[ch]) { Put(0xB0 | ch, 7, cc); chanCc[ch] = cc; Controllers++; }
        }
    }

    private int Pitch(int v) => regs[v * 16 + 2] | (regs[v * 16 + 3] & 0x3F) << 8;
    private int Level(int v) => Math.Min(127, Math.Max(Math.Abs((int)(sbyte)regs[v * 16]), Math.Abs((int)(sbyte)regs[v * 16 + 1])));

    /// <summary>The voice's pitch now as MIDI cents (from its root).</summary>
    private double MidiCents(int v, int root) => root + 1200.0 * Math.Log2(Math.Max(1, Pitch(v)) / 4096.0);

    private int BendOf(int v)
    {
        double off = MidiCents(v, vRoot[v]) - vKey[v] * 100.0;
        double b = Math.Round(off * 8192.0 / (Range * 100.0));
        if (b < -8192 || b > 8191) { OutOfRange++; b = Math.Clamp(b, -8192, 8191); }
        return (int)b;
    }

    private void SendBend(int ch, int bend)
    {
        if (bend == chanBend[ch]) return;
        int u = bend + 8192;
        Put(0xE0 | ch, u & 0x7F, u >> 7 & 0x7F);
        chanBend[ch] = bend;
        Bends++;
    }

    private void KeyOn(int v)
    {
        if (vKey[v] >= 0) NoteOff(v);
        int srcn = regs[v * 16 + 4];
        int ch = chanOf[srcn];
        if (ch < 0) { ch = RecTranscribe.ChannelOf(instruments++); chanOf[srcn] = ch; }
        // the sample's root: estimated on the worker for the sample now at this number
        int dir = regs[0x5D] << 8, e = (dir + srcn * 4) & 0xFFFF;
        int start = aram[e] | aram[(e + 1) & 0xFFFF] << 8;
        if (rootStart[srcn] != start) { Volatile.Write(ref rootOf[srcn], Unknown); rootStart[srcn] = start; Volatile.Write(ref needRoot[srcn], 1); }
        int root = Volatile.Read(ref rootOf[srcn]);
        if (root == Unknown) { root = 6000; RootUnknown++; }
        if (RecMutants.On(RecMutants.LiveRootIgnored)) root = 6000;  // (the self-test's deliberate bug: every sample is keyed as if its rate were C5)
        vRoot[v] = root;
        if (KeyRootLog is { } log && NotesOn < log.Length) log[NotesOn] = RecMutants.On(RecMutants.LiveRootIgnored) ? Volatile.Read(ref rootOf[srcn]) is var r0 && r0 != Unknown ? r0 : 6000 : root;
        double mc = MidiCents(v, root);
        int key = Math.Clamp((int)Math.Round(mc / 100.0), 0, 127);
        vKey[v] = key; vChan[v] = ch;
        int level = Math.Max(1, Level(v));
        vLevel[v] = level;
        if (!chanReady[ch])
        {
            Put(0xB0 | ch, 101, 0); Put(0xB0 | ch, 100, 0); Put(0xB0 | ch, 6, Range); Put(0xB0 | ch, 38, 0); Put(0xB0 | ch, 101, 127); Put(0xB0 | ch, 100, 127);
            chanReady[ch] = true;
        }
        for (int o = 0; o < 8; o++) if (o != v && vKey[o] == key && vChan[o] == ch) Recut++;   // two voices on one key of one channel: the receiver hears one note
        chanVoice[ch] = v;
        SendBend(ch, BendOf(v));
        if (chanCc[ch] != 100) { Put(0xB0 | ch, 7, 100); chanCc[ch] = 100; Controllers++; }
        Put(0x90 | ch, key, Math.Min(127, level));
        NotesOn++;
        keyedSinceAllOff = true;
        if ((regs[0x5C] >> v & 1) != 0) NoteOff(v);                    // keyed while its key-off bit is held: the S-DSP releases it at once (the transcription's two-sample note)
    }

    private void NoteOff(int v)
    {
        int ch = vChan[v], key = vKey[v];
        vKey[v] = -1;
        if (chanVoice[ch] == v) chanVoice[ch] = -1;
        if (RecMutants.On(RecMutants.LiveNoteOffLost)) return;        // (the self-test's deliberate bug: notes hang)
        Put(0x80 | ch, key, 0);
        NotesOff++;
    }

    /// <summary>Every sounding note off, and "all notes off" on every channel used (the live out stops, the game was replaced or frozen). Under the emulator's lock; a second call with nothing
    /// keyed in between sends nothing.</summary>
    public void AllOff()
    {
        if (!keyedSinceAllOff) return;
        keyedSinceAllOff = false;
        for (int v = 0; v < 8; v++) if (vKey[v] >= 0) NoteOff(v);
        for (int ch = 0; ch < 16; ch++) if (chanReady[ch]) Put(0xB0 | ch, 123, 0);
        dirty = 0;
    }

    private bool keyedSinceAllOff;
    /// <summary>A note was keyed since the last <see cref="AllOff"/>.</summary>
    public bool Sounding => keyedSinceAllOff;

    private void Put(int status, int d1, int d2)
    {
        if (tail - head >= QueueSize) { Dropped++; return; }
        queue[tail & (QueueSize - 1)] = (uint)(status & 0xFF | (d1 & 0x7F) << 8 | (d2 & 0x7F) << 16);
        tail++;
    }

    /// <summary>The next message (Status | Data1 &lt;&lt; 8 | Data2 &lt;&lt; 16, no port), under the emulator's lock.</summary>
    public bool TryDequeue(out uint msg)
    {
        if (head == tail) { msg = 0; return false; }
        msg = queue[head & (QueueSize - 1)];
        head++;
        Sent++;
        return true;
    }

    /// <summary>The worker: the roots of the samples keyed since the last call, from the sound memory as it is (a racy read of bytes is harmless: the next key-on of a sample that changed asks again). Returns how many were estimated.</summary>
    public int EstimatePending()
    {
        int n = 0;
        for (int s = 0; s < 256; s++)
        {
            if (Volatile.Read(ref needRoot[s]) == 0) continue;
            Volatile.Write(ref needRoot[s], 0);
            int start = rootStart[s];
            if (start < 0) continue;
            int dir = regs[0x5D] << 8, e = (dir + s * 4) & 0xFFFF;
            int loop = aram[(e + 2) & 0xFFFF] | aram[(e + 3) & 0xFFFF] << 8;
            var brr = RecSession.SampleIn(aram, start, out _);
            int loopOff = loop - start;
            if (loopOff < 0 || loopOff % 9 != 0 || loopOff >= brr.Length) loopOff = -1;
            var est = RecRoot.Estimate(brr, loopOff);
            Volatile.Write(ref rootOf[s], est.Found ? (int)Math.Round(est.Midi * 100) : 6000);
            n++;
        }
        return n;
    }

    /// <summary>Before the live out starts (the worker): the roots of every sample the directory names now, so the notes of the samples already loaded are keyed right from the first one.
    /// An entry whose bytes run 4,096 blocks without an end is not a sample and is left for its first key-on. Sets <see cref="Primed"/>. Returns how many were estimated.</summary>
    public int Prime(byte[]? unitAram, int dirReg)
    {
        int n = 0;
        if (unitAram != null)
        {
            int dir = dirReg << 8;
            for (int s = 0; s < 256; s++)
            {
                int e = (dir + s * 4) & 0xFFFF;
                int start = unitAram[e] | unitAram[(e + 1) & 0xFFFF] << 8, loop = unitAram[(e + 2) & 0xFFFF] | unitAram[(e + 3) & 0xFFFF] << 8;
                if (start == 0 && loop == 0) continue;
                var brr = RecSession.SampleIn(unitAram, start, out bool ended);
                if (!ended) continue;
                int loopOff = loop - start;
                if (loopOff < 0 || loopOff % 9 != 0 || loopOff >= brr.Length) loopOff = -1;
                var est = RecRoot.Estimate(brr, loopOff);
                rootStart[s] = start;
                Volatile.Write(ref rootOf[s], est.Found ? (int)Math.Round(est.Midi * 100) : 6000);
                n++;
            }
        }
        Primed = true;
        return n;
    }

    /// <summary>The root a sample number is keyed with (MIDI cents), or null while unknown.</summary>
    public int? RootOf(int srcn) { int r = Volatile.Read(ref rootOf[srcn & 0xFF]); return r == Unknown ? null : r; }

    /// <summary>The MIDI channel of a sample number (-1: not keyed yet).</summary>
    public int ChannelOfSample(int srcn) => chanOf[srcn & 0xFF];

    public string Describe() =>
        $"{NotesOn:N0} notes on {instruments} channel{(instruments == 1 ? "" : "s")} (port {Port}), {Bends:N0} bends, {Controllers:N0} volume changes"
        + (RootUnknown > 0 ? $"; {RootUnknown} keyed before their sample's root was known (as if the sample's rate were C5)" : "")
        + (instruments > 15 ? $"; {instruments - 15} instruments share a channel with an earlier one" : "")
        + (Recut > 0 ? $"; {Recut} notes struck a key another voice of the same instrument held" : "")
        + (OutOfRange > 0 ? $"; {OutOfRange} moves past the {Range}-semitone bend range" : "")
        + (Dropped > 0 ? $"; {Dropped} messages LOST (more than {QueueSize} between two audio blocks)" : "");
}
