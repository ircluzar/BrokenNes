// Round 8 (record-nes): live MIDI out of a NES game. While it plays, the notes a recording would transcribe go out as MIDI as they happen: the chip model (NesApuModel) follows the game's writes
// from the game chip's own state, inside the game's frames (the audio thread; nothing allocated: the model, the queue and the tables are made once), and every change of what a channel SOUNDS like
// becomes a message:
//   a note-on when a channel becomes audible or is struck again (its fourth register), on the MIDI channel of its instrument (the channel and its duty, or the noise and its mode, in the order they
//   first sound; channel 10 left out, as the transcription does); the key is the musical note of its period (the noise: its index's key), velocity its volume (0-15 as the transcription shows it);
//   a pitch bend (range 12 semitones, RPN 0 on each channel's first note) for every period move (the driver's vibrato, slides, the hardware sweep); controller 7 for every volume move (the decay envelope),
//   relative to the struck level (the transcription's own values); a note-off when the channel falls silent (its volume, its length or linear counter, $4015); a duty change ends the note on one
//   instrument's channel and strikes it on the other's (the file instead keeps the wave running: say so).
// Not live (the file does): the per-track bend range, the tempo, the DMC.
using NesEmulator;

namespace BrokenNes2.Record;

public sealed class NesLive
{
    public const int QueueSize = 8192, DefaultRange = 12;
    private readonly uint[] queue = new uint[QueueSize];
    private int head, tail;
    private NesApuModel m = new();
    /// <summary>Tests: the clock (CPU cycles) of every note-on, beside <see cref="NoteLog"/>.</summary>
    public long[]? NoteClock;

    /// <summary>Tests: listens from a recording's saved starting state (NesSession.BaseState) at clock 0, as Attach would from the game's chip. Notes sounding in it are not sent.</summary>
    public void AttachSaved(int[] state)
    {
        m = NesApuModel.Load(state) ?? throw new ArgumentException("not a chip state");
        for (int c = 0; c < 4; c++) vKey[c] = Audible(c) ? -2 : -1;
        attached = true;
    }
    // per NES channel: the note it sounds (key -1 none; -2 sounding since before the live out started: not sent), its MIDI channel, struck level and velocity, its duty / mode
    private readonly int[] vKey = new int[4], vChan = new int[4], vVel = new int[4], vSub = new int[4], vPeriod = new int[4], vLevel = new int[4];
    // per instrument (channel x 4 + duty or mode): its MIDI channel (-1 not sounded yet)
    private readonly int[] chanOf = new int[16];
    private readonly int[] chanBend = new int[16], chanCc = new int[16];
    private readonly bool[] chanReady = new bool[16];
    private int instruments;
    private bool attached, keyedSinceAllOff;

    public volatile int Port = 1;
    public int Range = DefaultRange;
    public long NotesOn, NotesOff, Bends, Controllers, Dropped, DutySwitches, Sent;
    public volatile bool Ended;
    public bool Primed => true;
    public int Instruments => instruments;
    public int Pending => tail - head;
    public bool Sounding => keyedSinceAllOff;
    /// <summary>Tests: a log of every note-on (NES channel, key, velocity) as packed ints (made by the test, filled without allocating).</summary>
    public int[]? NoteLog;

    public NesLive()
    {
        Array.Fill(vKey, -1);
        Array.Fill(chanOf, -1);
        Array.Fill(chanBend, int.MinValue);
        Array.Fill(chanCc, -1);
    }

    /// <summary>The game it listens to (between frames, under the emulator's lock): its chip's state now (APU_FIX's counters; another chip's from the registers last written). Notes sounding already are
    /// not sent (their start was not seen). Allocation-free.</summary>
    public void Attach(NES nes, byte[] latch, uint written)
    {
        if (nes.ActiveApu is APU_FIX fix) m.Set(fix.ProbeControl(), nes.ApuClock);
        else m.SetFromLatch(latch, written, nes.ApuClock);
        for (int c = 0; c < 4; c++) vKey[c] = Audible(c) ? -2 : -1;
        attached = true;
    }

    public void Detach() => attached = false;

    /// <summary>Every write of the game to $4000-$4017 (the audio thread, inside a frame; the bus calls it before the chip takes the write). Allocation-free.</summary>
    public void OnWrite(ushort address, byte value, long clock)
    {
        if (!attached || RecMutants.On(NesLiveMutants.Silent)) return;
        int reg = address - 0x4000;
        if ((uint)reg >= 0x18) return;
        Step(clock);
        bool struck = m.Write(reg, value);
        Evaluate(struck && reg < 0x10 ? reg >> 2 : -1);
    }

    /// <summary>The end of a frame: the quarter and half frames up to it.</summary>
    public void EndFrame(long clock) { if (attached) Step(clock); }

    private void Step(long clock) { while (m.StepToNextEvent(clock)) Evaluate(-1); }

    private bool Audible(int c) => c switch { 0 => m.P1.Audible, 1 => m.P2.Audible, 2 => m.T.Audible, _ => m.N.Audible };
    private int Period(int c) => c switch { 0 => m.P1.Timer, 1 => m.P2.Timer, 2 => m.T.Timer, _ => m.N.Index };
    private int Level(int c) => c switch { 0 => m.P1.VolumeInForce, 1 => m.P2.VolumeInForce, 2 => 15, _ => m.N.VolumeInForce };
    private int Sub(int c) => c switch { 0 => m.P1.Duty, 1 => m.P2.Duty, 3 => m.N.Short ? 1 : 0, _ => 0 };

    private void Evaluate(int struck)
    {
        for (int c = 0; c < 4; c++)
        {
            bool aud = Audible(c);
            int k = vKey[c];
            if (!aud) { if (k >= 0) NoteOff(c); vKey[c] = -1; continue; }
            if (k == -2) { if (struck == c) NoteOn(c); continue; }        // (sounding since before: sent from its next strike)
            if (k < 0) { NoteOn(c); continue; }
            if (struck == c) { NoteOff(c); NoteOn(c); continue; }
            if (Sub(c) != vSub[c]) { NoteOff(c); NoteOn(c); DutySwitches++; continue; }
            int p = Period(c), lv = Level(c);
            int ch = vChan[c];
            if (p != vPeriod[c]) { vPeriod[c] = p; SendBend(ch, BendOf(c, p, vKey[c])); }
            if (lv != vLevel[c] && c != 2) { vLevel[c] = lv; SendCc(ch, CcOf(lv, vVel[c])); }
        }
    }

    private double MidiOf(int c, int p) => c == 3 ? NesDirect.NoiseKey(p) : NesDirect.MidiOf(p, c == 2);

    private int BendOf(int c, int p, int key)
    {
        double b = Math.Round((MidiOf(c, p) - key) * 8192.0 / Range);
        return (int)Math.Clamp(b, -8192, 8191);
    }

    private static int CcOf(int level, int vel) => NesTranscribe.CcInterval(level, vel).Ideal;

    private void NoteOn(int c)
    {
        int sub = Sub(c), inst = c * 4 + sub;
        int ch = chanOf[inst];
        if (ch < 0) { ch = RecTranscribe.ChannelOf(instruments++); chanOf[inst] = ch; }
        if (!chanReady[ch])
        {
            Put(0xB0 | ch, 101, 0); Put(0xB0 | ch, 100, 0); Put(0xB0 | ch, 6, Range); Put(0xB0 | ch, 38, 0); Put(0xB0 | ch, 101, 127); Put(0xB0 | ch, 100, 127);
            chanReady[ch] = true;
        }
        int p = Period(c), lv = Level(c);
        int key = Math.Clamp((int)Math.Round(MidiOf(c, p)), 0, 127);
        int vel = c == 2 ? 127 : Math.Max(1, NesTranscribe.VelocityFor(lv));
        vKey[c] = key; vChan[c] = ch; vVel[c] = vel; vSub[c] = sub; vPeriod[c] = p; vLevel[c] = lv;
        SendBend(ch, BendOf(c, p, key));
        SendCc(ch, c == 2 ? 100 : CcOf(lv, vel));
        Put(0x90 | ch, key, vel);
        if (NoteLog is { } log && NotesOn < log.Length) { log[NotesOn] = c << 16 | key << 8 | vel; if (NoteClock is { } nc && NotesOn < nc.Length) nc[NotesOn] = m.Clock; }
        NotesOn++;
        keyedSinceAllOff = true;
    }

    private void NoteOff(int c)
    {
        int key = vKey[c];
        vKey[c] = -1;
        if (key < 0 || RecMutants.On(NesLiveMutants.NoteOffLost)) return;
        Put(0x80 | vChan[c], key, 0);
        NotesOff++;
    }

    private void SendBend(int ch, int bend)
    {
        if (bend == chanBend[ch]) return;
        int u = bend + 8192;
        Put(0xE0 | ch, u & 0x7F, u >> 7 & 0x7F);
        chanBend[ch] = bend;
        Bends++;
    }

    private void SendCc(int ch, int cc)
    {
        if (cc == chanCc[ch]) return;
        Put(0xB0 | ch, 7, cc);
        chanCc[ch] = cc;
        Controllers++;
    }

    /// <summary>Every sounding note off, and "all notes off" on every channel used (switched off, the game replaced or frozen). A second call with nothing struck in between sends nothing.</summary>
    public void AllOff()
    {
        if (!keyedSinceAllOff) return;
        keyedSinceAllOff = false;
        for (int c = 0; c < 4; c++) if (vKey[c] >= 0) NoteOff(c);
        for (int ch = 0; ch < 16; ch++) if (chanReady[ch]) Put(0xB0 | ch, 123, 0);
    }

    private void Put(int status, int d1, int d2)
    {
        if (tail - head >= QueueSize) { Dropped++; return; }
        queue[tail & (QueueSize - 1)] = (uint)(status & 0xFF | (d1 & 0x7F) << 8 | (d2 & 0x7F) << 16);
        tail++;
    }

    public bool TryDequeue(out uint msg)
    {
        if (head == tail) { msg = 0; return false; }
        msg = queue[head & (QueueSize - 1)];
        head++;
        Sent++;
        return true;
    }

    public string Describe() =>
        $"{NotesOn:N0} notes on {instruments} channel{(instruments == 1 ? "" : "s")} (port {Port}), {Bends:N0} bends, {Controllers:N0} volume changes"
        + (DutySwitches > 0 ? $"; {DutySwitches} duty changes struck the note again on the other duty's channel" : "")
        + (instruments > 15 ? $"; {instruments - 15} instruments share a channel with an earlier one" : "")
        + (Dropped > 0 ? $"; {Dropped} messages LOST (more than {QueueSize} between two audio blocks)" : "");
}

public static class NesLiveMutants
{
    public const int Silent = 121, NoteOffLost = 122;
}
