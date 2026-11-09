using System.IO;
// The PC speaker: port 61h bit 1 gated with the 8254's channel 2 output, sampled on the machine clock. Written for BrokenNes.

using System;
using System.Collections.Generic;

namespace NesEmulator.X86;

/// <summary>
/// The speaker's sound as 16-bit stereo samples. It is drawn on the machine's own clock (PIT ticks), up to "now" just before anything that changes it (a write to port 61h or to the
/// timer's channel 2) and at the end of each frame, so the timing is exact and the same run gives the same samples. Off until <see cref="Enabled"/> (headless tools pay nothing).
/// The level is port 61h bit 1 AND the timer's channel 2 output (with the gate, bit 0, low the output stays high: a program toggling bit 1 by hand is heard too). Each sample averages
/// four points of the square wave, a cheap guard against the worst aliasing.
/// </summary>
public sealed class PcSpeaker : IPcSoundOutput
{
    public const int Rate = 44100;
    private const short Volume = 5000;
    private readonly PcMachine _m;
    private readonly List<short> _out = new();
    private long _pos;              // the next sample's index on the machine clock (samples since PIT time 0)
    public bool Enabled;

    public PcSpeaker(PcMachine m) { _m = m; }

    private static long PitOfSample(long n) => (long)((Int128)n * Pit8254.Freq / Rate);

    /// <summary>Draws the samples up to the machine's present time with the speaker as it is now (call before changing it).</summary>
    public void CatchUp()
    {
        if (!Enabled) return;
        long now = _m.PitNow;
        long end = (long)((Int128)now * Rate / Pit8254.Freq);
        if (_pos == 0 && end > Rate) _pos = end;   // switched on mid-run: start at the present
        int p61 = _m.Port61;
        bool on = (p61 & 2) != 0, gated = (p61 & 1) != 0;
        for (; _pos < end; _pos++)
        {
            int level;
            if (!on) level = 0;
            else if (!gated) level = 4;
            else
            {
                long t0 = PitOfSample(_pos), t1 = PitOfSample(_pos + 1);
                level = 0;
                for (int k = 0; k < 4; k++) level += _m.Pit.OutAt(2, t0 + (t1 - t0) * k / 4);
            }
            short v = (short)(level * Volume / 4);
            _out.Add(v); _out.Add(v);
        }
        if (_out.Count > Rate * 4) _out.RemoveRange(0, _out.Count - Rate * 2);   // nobody reading: keep the last half second
    }

    /// <summary>Hands over the samples drawn so far (stereo interleaved); returns how many shorts.</summary>
    public int Read(short[] buffer)
    {
        int n = Math.Min(_out.Count, buffer.Length) & ~1;
        _out.CopyTo(0, buffer, 0, n);
        _out.RemoveRange(0, n);
        return n;
    }

    public int Pending => _out.Count;

    /// <summary>After a state load or a cold boot: forget the drawn samples, carry on from the machine's present time.</summary>
    public void Restart() { _out.Clear(); _pos = (long)((Int128)_m.PitNow * Rate / Pit8254.Freq); }
    public void SaveOutput(BinaryWriter w) { PcOut.Write(w, _out); w.Write(_pos); }
    public void LoadOutput(BinaryReader r) { PcOut.Read(r, _out); _pos = r.ReadInt64(); }
}
