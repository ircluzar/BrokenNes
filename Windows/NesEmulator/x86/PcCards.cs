// The x86 console's plug-in sound cards (beside the board's PC speaker and AdLib): the interface, and a re-creation of the DOSBox mixer the ported
// cards were written against. Written for BrokenNes.
//
// DosboxMixer is NOT a port: DOSBox's mixer.cpp is not among the pinned sources (C:\BrokenNes-tools\apu-harvest has only the card files), so it is
// written from DOSBox's documented / well-known mixer behaviour, the parts the cards depend on: a 1 ms tick that asks every enabled channel for
// "needed" mixer-rate samples (rate/1000 with a 14-bit fractional carry); a channel converts that to its own rate (freq_add, rounded up) and calls
// its card's callback; AddSamples steps through the card's samples with sample-and-hold (no interpolation), AddStretched spreads a short block
// linearly over the rest of the tick, AddSilence just ends the tick's block, FillUp mixes a channel up to the present moment within the tick;
// volumes are 13-bit fixed point; the sum is clipped to 16 bits. The card's events (DOSBox's PIC_AddEvent, delays in milliseconds) run on the
// machine clock in time order with the ticks. The mixer's output is then held onto the console's 44.1 kHz timeline.

using System;
using System.Collections.Generic;
using System.IO;

namespace NesEmulator.X86;

/// <summary>A sound card in the x86 PC's slot: its ports, its sound on the 44.1 kHz timeline, its state.</summary>
public interface IPcSoundCard
{
    /// <summary>The card's x86 APU id family ("SB_D072", "GUS_D074", ...).</summary>
    string Id { get; }
    /// <summary>A port read; false = not the card's port.</summary>
    bool In(int port, bool word, out int value);
    /// <summary>A port write; false = not the card's port (a word write it does not take is split into bytes by the board).</summary>
    bool Out(int port, bool word, int value);
    /// <summary>Runs the card's events and sound up to the machine's present time.</summary>
    void Advance();
    /// <summary>Adds the card's drawn sound (stereo interleaved, 44.1 kHz) onto <paramref name="buffer"/>.</summary>
    int MixInto(short[] buffer, int count);
    int Pending { get; }
    /// <summary>Forget the drawn samples, carry on from the present (after a state load or a cold boot).</summary>
    void Restart();
    /// <summary>Power-on state.</summary>
    void Reset();
    void Save(BinaryWriter w);
    void Load(BinaryReader r);
    /// <summary>IRQs the card raised so far (diagnostics, not saved).</summary>
    long IrqsRaised { get; }
}

/// <summary>FA-ST-3: a sound source's drawn-but-not-yet-read output (its 44.1 kHz samples, its resampler's position and history), saved after the machine
/// so a state load continues sample for sample instead of dropping them and restarting from the present (PcSession, the "PCAU" trailer).</summary>
public interface IPcSoundOutput { void SaveOutput(BinaryWriter w); void LoadOutput(BinaryReader r); }

internal static class PcOut
{
    public static void Write(BinaryWriter w, List<short> l) { w.Write(l.Count); foreach (var v in l) w.Write(v); }
    public static void Read(BinaryReader r, List<short> l) { l.Clear(); int n = r.ReadInt32(); for (int i = 0; i < n; i++) l.Add(r.ReadInt16()); }
    public static void Write(BinaryWriter w, List<int> l) { w.Write(l.Count); foreach (var v in l) w.Write(v); }
    public static void Read(BinaryReader r, List<int> l) { l.Clear(); int n = r.ReadInt32(); for (int i = 0; i < n; i++) l.Add(r.ReadInt32()); }
}

public sealed class DosboxMixer : IPcSoundOutput
{
    public const int Shift = 14, Remain = (1 << Shift) - 1, VolShift = 13;
    public const int OutRate = 44100;
    private readonly Func<long> _pitNow;
    public readonly int Rate;
    private readonly bool _stereoOut;

    public sealed class Chan
    {
        internal DosboxMixer M = null!;
        public Action<int> Handler = _ => { };
        public bool Enabled;
        public int Freq, FreqAdd, FreqIndex, Done, Needed;
        public int LastL, LastR;
        public int VolL = 1 << VolShift, VolR = 1 << VolShift;
        public void SetFreq(int f) { Freq = f; FreqAdd = (int)(((long)f << Shift) / M.Rate); }
        public void Enable(bool on)
        {
            if (on == Enabled) return;
            Enabled = on;
            if (on) { FreqIndex = Remain; Done = Math.Max(Done, M.Needed == 0 ? 0 : Math.Min(M.Needed, M.PartialIndex())); LastL = LastR = 0; }
        }
        public void SetVolume(float l, float r) { VolL = (int)((1 << VolShift) * l); VolR = (int)((1 << VolShift) * r); }

        public void Mix(int needed)
        {
            Needed = needed;
            int guard = 0;
            while (Enabled && Needed > Done)
            {
                long todo = Needed - Done;
                todo *= FreqAdd;
                if ((todo & Remain) != 0) todo += Remain + 1;
                todo >>= Shift;
                Handler((int)todo);
                if (++guard > 16) { AddSilence(); break; }   // (a card that adds nothing would spin DOSBox's loop forever)
            }
        }

        public void FillUp() { if (Enabled) Mix(Math.Min(M.Needed, M.PartialIndex())); }

        private void Put(int l, int r)
        {
            if (Done >= M._bufL.Length) { Done++; return; }
            M._bufL[Done] += l * VolL; M._bufR[Done] += r * VolR; Done++;
        }

        /// <summary>Sample-and-hold through <paramref name="len"/> card samples (frames: <paramref name="l"/>, <paramref name="r"/>).</summary>
        public void AddSamples(int len, Func<int, int> l, Func<int, int> r)
        {
            if (len <= 0) return;
            FreqIndex &= Remain;
            int pos = 0;
            while (true)
            {
                int np = FreqIndex >> Shift;
                if (pos < np) { pos = np; }
                if (pos >= len) break;
                LastL = l(pos); LastR = r(pos);
                Put(LastL, LastR);
                FreqIndex += FreqAdd;
            }
        }

        public void AddMono(int len, Func<int, int> s) => AddSamples(len, s, s);

        /// <summary>Spreads <paramref name="len"/> samples over the rest of this tick (DOSBox's direct-DAC path), linearly from the last one.</summary>
        public void AddStretched(int len, short[] data)
        {
            if (Done >= Needed || len <= 0) return;
            int outlen = Needed - Done;
            int index = 0, add = (int)(((long)len << Shift) / outlen);
            int pos = 0;
            int diff = data[0] - LastL;
            for (int i = 0; i < outlen; i++)
            {
                int np = index >> Shift;
                if (pos < np) { pos = np; LastL += diff; diff = data[Math.Min(pos, len - 1)] - LastL; }
                int dm = index & Remain;
                index += add;
                int s = LastL + (int)(((long)diff * dm) >> Shift);
                Put(s, s);
            }
            LastR = LastL;
        }

        public void AddSilence() { if (Done < Needed) { Done = Needed; LastL = LastR = 0; FreqIndex = Remain; } }

        internal void Save(BinaryWriter w) { w.Write(Enabled); w.Write(Freq); w.Write(FreqIndex); w.Write(Done); w.Write(Needed); w.Write(LastL); w.Write(LastR); w.Write(VolL); w.Write(VolR); }
        internal void Load(BinaryReader r) { Enabled = r.ReadBoolean(); SetFreq(r.ReadInt32()); FreqIndex = r.ReadInt32(); Done = r.ReadInt32(); Needed = r.ReadInt32(); LastL = r.ReadInt32(); LastR = r.ReadInt32(); VolL = r.ReadInt32(); VolR = r.ReadInt32(); }
    }

    private readonly List<Chan> _chans = new();
    private int[] _bufL = new int[4096], _bufR = new int[4096];
    private long _tick;            // ms ticks finished (absolute: tick k ends at (k+1) ms of machine time)
    private int _tickRemain;
    public int Needed { get; private set; }
    private long _mixCount;        // mixer-rate frames produced since time 0 (as the ticks count them)
    private long _outIndex;        // 44.1 kHz frames handed on
    private readonly List<int> _mixL = new(), _mixR = new();
    private long _mixBase;         // index of _mixL[0]
    private readonly List<short> _out = new();

    // events (DOSBox's PIC_AddEvent): due time in PIT ticks, the card's event code and value
    private struct Ev { public long Due; public int Code, Val; public long Seq; }
    private readonly List<Ev> _events = new();
    private long _seq;
    public Action<int, int> OnEvent = (_, _) => { };

    public DosboxMixer(Func<long> pitNow, int rate, bool stereoOut = true)
    {
        _pitNow = pitNow; Rate = rate; _stereoOut = stereoOut;
        Restart(true);
    }

    public Chan AddChannel(Action<int> handler, int freq)
    {
        var c = new Chan { M = this, Handler = handler };
        c.SetFreq(freq);
        _chans.Add(c);
        return c;
    }

    private static long TickEnd(long k) => (long)((Int128)(k + 1) * Pit8254.Freq / 1000);
    private static long TickStart(long k) => (long)((Int128)k * Pit8254.Freq / 1000);

    private void NextNeeded() { _tickRemain += (Rate << Shift) / 1000; Needed = _tickRemain >> Shift; _tickRemain &= Remain; }

    /// <summary>How far into this tick the machine is, in mixer samples (for FillUp).</summary>
    internal int PartialIndex()
    {
        long s = TickStart(_tick), e = TickEnd(_tick), now = Math.Clamp(_pitNow(), s, e);
        return (int)((Needed * (now - s)) / Math.Max(1, e - s));
    }

    public double NowMs => _pitNow() * 1000.0 / Pit8254.Freq;

    public void AddEvent(int code, double delayMs, int val)
    {
        long due = _pitNow() + Math.Max(0, (long)(delayMs * Pit8254.Freq / 1000.0));
        _events.Add(new Ev { Due = due, Code = code, Val = val, Seq = _seq++ });
    }
    public void RemoveEvents(int code) => _events.RemoveAll(e => e.Code == code);
    public bool HasEvent(int code) => _events.Exists(e => e.Code == code);

    private long _now;
    /// <summary>The time the mixer is working at (an event or tick being run), PIT ticks.</summary>
    public long Now => _now;

    /// <summary>Runs every event and tick due by the machine's present time, in time order.</summary>
    public void Advance()
    {
        long now = _pitNow();
        while (true)
        {
            long te = TickEnd(_tick);
            int ei = -1; long ed = long.MaxValue;
            for (int i = 0; i < _events.Count; i++)
                if (_events[i].Due < ed || (_events[i].Due == ed && _events[i].Seq < _events[ei].Seq)) { ed = _events[i].Due; ei = i; }
            if (ei >= 0 && ed <= te && ed <= now)
            {
                var e = _events[ei]; _events.RemoveAt(ei);
                _now = e.Due;
                OnEvent(e.Code, e.Val);
                continue;
            }
            if (te > now) break;
            _now = te;
            RunTick();
        }
        _now = now;
    }

    private void RunTick()
    {
        int n = Needed;
        if (_bufL.Length < n * 4) { Array.Resize(ref _bufL, n * 4); Array.Resize(ref _bufR, n * 4); }
        foreach (var c in _chans) c.Mix(n);
        for (int i = 0; i < n; i++)
        {
            _mixL.Add(Math.Clamp(_bufL[i] >> VolShift, -32768, 32767));
            _mixR.Add(Math.Clamp(_bufR[i] >> VolShift, -32768, 32767));
        }
        int carry = 0;
        foreach (var c in _chans) { c.Done = c.Done > n ? c.Done - n : 0; carry = Math.Max(carry, c.Done); }
        // shift the carried-over samples down
        int len = _bufL.Length;
        Array.Copy(_bufL, n, _bufL, 0, len - n); Array.Clear(_bufL, len - n, n);
        Array.Copy(_bufR, n, _bufR, 0, len - n); Array.Clear(_bufR, len - n, n);
        _mixCount += n;
        _tick++;
        NextNeeded();
        Emit();
    }

    private void Emit()
    {
        while (true)
        {
            long mi = (long)((Int128)_outIndex * Rate / OutRate);
            if (mi >= _mixCount) break;
            int k = (int)(mi - _mixBase);
            if (k < 0) k = 0;
            int l = _mixL[k], r = _stereoOut ? _mixR[k] : l;
            _out.Add((short)l); _out.Add((short)r);
            _outIndex++;
        }
        long keepFrom = (long)((Int128)_outIndex * Rate / OutRate) - _mixBase;
        if (keepFrom > 0) { int d = (int)Math.Min(keepFrom, _mixL.Count); _mixL.RemoveRange(0, d); _mixR.RemoveRange(0, d); _mixBase += d; }
        if (_out.Count > OutRate * 4) _out.RemoveRange(0, _out.Count - OutRate * 2);
    }

    public int MixInto(short[] buffer, int count)
    {
        int n = Math.Min(_out.Count, count) & ~1;
        for (int i = 0; i < n; i++) buffer[i] = (short)Math.Clamp(buffer[i] + _out[i], -32768, 32767);
        _out.RemoveRange(0, n);
        return n;
    }

    public int Pending => _out.Count;

    /// <summary>Drops drawn samples; <paramref name="cold"/> also forgets the events, the channels' blocks and puts the tick at the present.</summary>
    public void Restart(bool cold = false)
    {
        _out.Clear(); _mixL.Clear(); _mixR.Clear();
        if (cold)
        {
            _events.Clear();
            long now = _pitNow();
            _tick = (long)((Int128)now * 1000 / Pit8254.Freq);
            _tickRemain = 0; NextNeeded();
            _mixCount = (long)((Int128)_tick * Rate / 1000);
            Array.Clear(_bufL); Array.Clear(_bufR);
            foreach (var c in _chans) { c.Done = 0; c.FreqIndex = Remain; c.LastL = c.LastR = 0; }
        }
        _mixBase = _mixCount;
        _outIndex = (long)(((Int128)_mixCount * OutRate + Rate - 1) / Rate);
    }

    public void SaveOutput(BinaryWriter w) { PcOut.Write(w, _out); PcOut.Write(w, _mixL); PcOut.Write(w, _mixR); w.Write(_mixBase); w.Write(_outIndex); }
    public void LoadOutput(BinaryReader r) { PcOut.Read(r, _out); PcOut.Read(r, _mixL); PcOut.Read(r, _mixR); _mixBase = r.ReadInt64(); _outIndex = r.ReadInt64(); }

    public void Save(BinaryWriter w)
    {
        w.Write(_tick); w.Write(_tickRemain); w.Write(Needed); w.Write(_mixCount); w.Write(_seq);
        int carry = 0; foreach (var c in _chans) carry = Math.Max(carry, Math.Min(c.Done, _bufL.Length));
        w.Write(carry); for (int i = 0; i < carry; i++) { w.Write(_bufL[i]); w.Write(_bufR[i]); }
        foreach (var c in _chans) c.Save(w);
        w.Write(_events.Count); foreach (var e in _events) { w.Write(e.Due); w.Write(e.Code); w.Write(e.Val); w.Write(e.Seq); }
    }

    public void Load(BinaryReader r)
    {
        _tick = r.ReadInt64(); _tickRemain = r.ReadInt32(); Needed = r.ReadInt32(); _mixCount = r.ReadInt64(); _seq = r.ReadInt64();
        Array.Clear(_bufL); Array.Clear(_bufR);
        int carry = r.ReadInt32(); for (int i = 0; i < carry; i++) { _bufL[i] = r.ReadInt32(); _bufR[i] = r.ReadInt32(); }
        foreach (var c in _chans) c.Load(r);
        _events.Clear(); int n = r.ReadInt32();
        for (int i = 0; i < n; i++) _events.Add(new Ev { Due = r.ReadInt64(), Code = r.ReadInt32(), Val = r.ReadInt32(), Seq = r.ReadInt64() });
        Restart();
    }
}
