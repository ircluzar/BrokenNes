using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NesEmulator.Sega;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// What <c>--md-run --wav out.wav [--vgm out.vgm] [--sound-report]</c> adds to a headless game run: the sound unit's output samples collected into a WAV, the YM2612 and PSG writes recorded as a VGM (the game's own sound
/// driver traffic, replayable with <c>--ym-render</c>), and a measurement report: the Z80 driver's life signs (code in RAM, ticks run, bank reads), FM key-ons per channel with the pitches they were struck at, the DAC stream's
/// rate, PSG tones, and the audio itself (peak, RMS per second, clipped samples, how much of the run is not silent). There is no playback device on the dev machine, so everything here is a measurement of numbers, never of sound.
/// </summary>
internal sealed class MdAudioCapture
{
    private readonly BOARD_MD board;
    private readonly MdSoundUnit? unit;
    private readonly VgmWriter vgm;
    private readonly List<short> pcm = new();
    private readonly short[] buf = new short[16384];
    private readonly int[] addr = new int[2];
    private readonly Ym2612? fm;

    private readonly long[] keyOns = new long[6];
    private readonly List<double>[] pitches = Enumerable.Range(0, 6).Select(_ => new List<double>()).ToArray();
    private readonly int[] fnumLow = new int[6], fnumHigh = new int[6];      // the frequency registers as written, per channel
    private long dacWrites, dacFirstMaster = -1, dacLastMaster;
    private long ymWrites, psgWrites;
    private readonly List<double> psgTones = new();
    private long psgNoiseWrites;
    private int psgLatch;
    private readonly int[] psgPeriod = new int[3];
    private bool lfoOn; private long timerWrites;

    public MdAudioCapture(BOARD_MD board)
    {
        this.board = board;
        unit = board.Sound as MdSoundUnit;
        vgm = new VgmWriter(board.Timeline.MasterHz);
        if (unit != null)
        {
            fm = unit.Fm;
            unit.YmPortTap = OnYm;
            unit.PsgPortTap = OnPsg;
        }
    }

    private void OnYm(long master, int port, byte value)
    {
        vgm.YmPort(master, port, value);
        int part = port >> 1;
        if ((port & 1) == 0) { addr[part] = value; return; }
        ymWrites++;
        int reg = addr[part];
        if (part == 0 && reg == 0x2A) { dacWrites++; if (dacFirstMaster < 0) dacFirstMaster = master; dacLastMaster = master; }
        else if (part == 0 && reg == 0x22) lfoOn |= (value & 8) != 0;
        else if (part == 0 && (reg == 0x24 || reg == 0x25 || reg == 0x26 || reg == 0x27)) timerWrites++;
        else if (reg >= 0xA0 && reg <= 0xA6 && (reg & 3) != 3 && (reg & 4) == 0) fnumLow[(reg & 3) + part * 3] = value;
        else if (reg >= 0xA4 && reg <= 0xA6 && (reg & 3) != 3) fnumHigh[(reg & 3) + part * 3] = value;
        else if (part == 0 && reg == 0x28)
        {
            int c = value & 3; if (c == 3) return;
            int ch = c + ((value & 4) != 0 ? 3 : 0);
            if ((value & 0xF0) != 0)
            {
                keyOns[ch]++;
                int fnum = (fnumHigh[ch] & 7) << 8 | fnumLow[ch], block = fnumHigh[ch] >> 3 & 7;
                double hz = fnum * Math.Pow(2, block) * (board.Timeline.MasterHz / 7 / 144.0) / 2097152.0;
                if (pitches[ch].Count < 200000) pitches[ch].Add(hz);
            }
        }
    }

    private void OnPsg(long master, byte value)
    {
        vgm.Psg(master, value);
        psgWrites++;
        if ((value & 0x80) != 0)
        {
            psgLatch = value >> 4 & 7;
            int c = psgLatch >> 1;
            if ((psgLatch & 1) == 0 && c < 3) psgPeriod[c] = psgPeriod[c] & 0x3F0 | value & 0xF;
            else if ((psgLatch & 1) == 0 && c == 3) psgNoiseWrites++;
        }
        else
        {
            int c = psgLatch >> 1;
            if ((psgLatch & 1) == 0 && c < 3)
            {
                psgPeriod[c] = psgPeriod[c] & 0x00F | (value & 0x3F) << 4;
                if (psgPeriod[c] > 1) psgTones.Add(board.Timeline.MasterHz / 15.0 / (32.0 * psgPeriod[c]));
            }
        }
    }

    /// <summary>Takes what the session has delivered (instead of discarding it) so the WAV is complete.</summary>
    public void Drain(MdSession session)
    {
        int n;
        while ((n = session.ReadSamples(buf)) > 0) for (int i = 0; i < n; i++) pcm.Add(buf[i]);
    }

    public void Finish(string? wavPath, string? vgmPath, string title)
    {
        if (wavPath != null) { WavFile.Write(wavPath, pcm, board.Sound.SampleRate); Console.WriteLine($"   wrote {wavPath} ({pcm.Count / 2:N0} stereo samples at {board.Sound.SampleRate} Hz, {pcm.Count / 2.0 / board.Sound.SampleRate:F1} s)"); }
        if (vgmPath != null) { vgm.Save(vgmPath, board.MasterClock, title); Console.WriteLine($"   wrote {vgmPath} ({vgm.YmWrites:N0} YM2612 writes, {vgm.PsgWrites:N0} PSG writes: the game's own sound driver traffic; replay it with --ym-render)"); }
        Report();
    }

    private static string Note(double hz)
    {
        if (hz <= 0) return "-";
        double midi = 69 + 12 * Math.Log2(hz / 440.0);
        int n = (int)Math.Round(midi);
        string[] names = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
        return $"{names[((n % 12) + 12) % 12]}{n / 12 - 1}";
    }

    public void Report()
    {
        var u = unit;
        if (u == null) { Console.WriteLine("   sound report: the board's sound unit is not the real MD unit (nothing to measure)"); return; }
        double seconds = board.MasterClock / board.Timeline.MasterHz;
        Console.WriteLine($"   -- sound report ({seconds:F1} s of emulated time; measured, not listened to)");
        int ramUsed = u.Ram.Count(x => x != 0);
        Console.WriteLine($"   Z80 sound driver: {u.Z80Ticks:N0} T-states run ({u.Z80Ticks / Math.Max(1.0, seconds * 3_579_545):P0} of the time the Z80 had), {ramUsed:N0} of 8192 bytes of Z80 RAM hold code or data, {u.BusRequests:N0} bus requests, {u.BankReads:N0} bank-window reads, bank register ${u.BankRegister:X3}");
        Console.WriteLine($"   YM2612 traffic: {ymWrites:N0} data writes ({ymWrites / Math.Max(0.001, seconds):F0} a second), timers {(timerWrites > 0 ? "used" : "unused")}, LFO {(lfoOn ? "on" : "off")}; PSG writes {psgWrites:N0}");
        var parts = new List<string>();
        for (int ch = 0; ch < 6; ch++)
        {
            var p = pitches[ch].Where(h => h > 0).OrderBy(h => h).ToList();
            if (keyOns[ch] == 0) { parts.Add($"FM{ch + 1}: none"); continue; }
            int distinct = p.Select(h => (int)Math.Round(12 * Math.Log2(h / 8.176))).Distinct().Count();
            parts.Add(p.Count == 0 ? $"FM{ch + 1}: {keyOns[ch]} key-ons" : $"FM{ch + 1}: {keyOns[ch]:N0} key-ons, {distinct} distinct notes, {p[0]:F0}-{p[^1]:F0} Hz ({Note(p[0])}-{Note(p[^1])}, median {Note(p[p.Count / 2])})");
        }
        Console.WriteLine("   FM notes: " + string.Join("; ", parts));
        if (dacWrites > 0)
        {
            double span = Math.Max(1.0, (dacLastMaster - dacFirstMaster) / board.Timeline.MasterHz);
            Console.WriteLine($"   DAC: {dacWrites:N0} sample writes over {span:F1} s (about {dacWrites / span:F0} writes a second while it plays)");
        }
        else Console.WriteLine("   DAC: not used");
        if (psgTones.Count > 0) Console.WriteLine($"   PSG tones: {psgTones.Count:N0} tone writes, {psgTones.Min():F0}-{psgTones.Max():F0} Hz ({Note(psgTones.Min())}-{Note(psgTones.Max())}); noise register writes {psgNoiseWrites:N0}");
        else Console.WriteLine($"   PSG tones: none; noise register writes {psgNoiseWrites:N0}");

        long clipped = 0; int peak = 0; double sum = 0;
        int rate = board.Sound.SampleRate;
        var perSecond = new List<double>();
        double acc = 0; int cnt = 0;
        for (int i = 0; i < pcm.Count; i += 2)
        {
            int l = pcm[i], r = pcm[i + 1];
            int a = Math.Max(Math.Abs(l), Math.Abs(r));
            if (a > peak) peak = a;
            if (a >= 32767) clipped++;
            sum += (double)l * l + (double)r * r;
            acc += ((double)l * l + (double)r * r) / 2; cnt++;
            if (cnt == rate) { perSecond.Add(Math.Sqrt(acc / cnt)); acc = 0; cnt = 0; }
        }
        double rms = pcm.Count == 0 ? 0 : Math.Sqrt(sum / pcm.Count);
        int loud = perSecond.Count(x => x > 32768 * Math.Pow(10, -50 / 20.0));
        Console.WriteLine($"   audio: {pcm.Count / 2:N0} samples, peak {peak} ({20 * Math.Log10(Math.Max(1, peak) / 32768.0):F1} dBFS), RMS {rms:F0} ({20 * Math.Log10(Math.Max(1e-9, rms) / 32768.0):F1} dBFS), clipped samples {clipped:N0}; {loud} of {perSecond.Count} whole seconds above -50 dBFS RMS{(perSecond.Count > 0 ? $", loudest second {20 * Math.Log10(Math.Max(1e-9, perSecond.Max()) / 32768.0):F1} dBFS" : "")}");
    }
}
