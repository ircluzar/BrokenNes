// A Covox Speech Thing on LPT1: an 8-bit resistor-ladder DAC hung on the parallel port's data lines. Written for BrokenNes from the hardware
// description (no source: there is nothing to port - the device is eight resistors and the printer port's data latch).
//
// Port 378h (LPT1 data): a write latches the byte; the DAC's level follows the latch at once and holds it until the next write (sample-and-hold
// at write time: the program's own timing is the sample rate, as on the real thing). 8-bit unsigned, 80h = silence. A read of 378h gives the latch
// back (the port is bidirectional-less, as on a plain SPP); 379h / 37Ah are not answered (no printer status lines: FFh). No filter, no IRQ, no DMA.
// The level is drawn on the machine clock up to "now" before every write and at the end of each frame, onto the console's 44.1 kHz timeline;
// each 44.1 kHz sample takes the level held at its start (a write inside a sample period is heard from the next sample on).

using System;
using System.Collections.Generic;
using System.IO;

namespace NesEmulator.X86;

public sealed class CovoxCard : IPcSoundCard, IPcSoundOutput
{
    public string Id => "COVOX";
    private const int Rate = 44100, Gain = 96;
    private readonly PcMachine _m;
    private readonly List<short> _out = new();
    private long _pos;
    private byte _latch = 0x80;
    public long IrqsRaised => 0;
    /// <summary>Writes to the DAC (diagnostics, not saved).</summary>
    public long Writes;

    public CovoxCard(PcMachine m) { _m = m; Reset(); }

    public void Reset() { _latch = 0x80; Restart(); }

    public void Restart() { _out.Clear(); _pos = (long)((Int128)_m.PitNow * Rate / Pit8254.Freq); }
    public void SaveOutput(BinaryWriter w) { PcOut.Write(w, _out); w.Write(_pos); }
    public void LoadOutput(BinaryReader r) { PcOut.Read(r, _out); _pos = r.ReadInt64(); }

    public void Advance()
    {
        long end = (long)((Int128)_m.PitNow * Rate / Pit8254.Freq);
        short v = (short)((_latch - 0x80) * Gain);
        for (; _pos < end; _pos++) { _out.Add(v); _out.Add(v); }
        if (_out.Count > Rate * 4) _out.RemoveRange(0, _out.Count - Rate * 2);
    }

    public bool In(int port, bool word, out int value)
    {
        value = 0xff;
        if (word || port != 0x378) return false;
        value = _latch; return true;
    }

    public bool Out(int port, bool word, int value)
    {
        if (word || port != 0x378) return false;
        Advance(); _latch = (byte)value; Writes++; return true;
    }

    public int MixInto(short[] buffer, int count)
    {
        int n = Math.Min(_out.Count, count) & ~1;
        for (int i = 0; i < n; i++) buffer[i] = (short)Math.Clamp(buffer[i] + _out[i], -32768, 32767);
        _out.RemoveRange(0, n);
        return n;
    }

    public int Pending => _out.Count;
    public void Save(BinaryWriter w) { w.Write(1); w.Write(_latch); }
    public void Load(BinaryReader r) { if (r.ReadInt32() != 1) throw new InvalidDataException("Covox state version"); _latch = r.ReadByte(); }
}
