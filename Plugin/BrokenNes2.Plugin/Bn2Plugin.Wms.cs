// WideMess milestone 7a: an instance on the WideMess sound chip (chip WMS). Direct mode: FL's notes play the chip's built-in 16-bit instruments on its new voices 8-31, 24 at once (WmsBank); Instrument
// Runaway on a WideMess game: they play the game's own instruments (WmsSnapshot). Test hooks 900-939.
namespace BrokenNes2;

public sealed unsafe partial class Bn2Plugin
{
    /// <summary>The WMS chip's output level after the S-DSP: 1 (the voices' own level, WmsSampler.FullLevel, matches a NES pulse; nothing is scaled after the chip).</summary>
    public const float WmsGain = 1.0f;

    private volatile WmsSampler? wmsEngine;           // Direct mode on chip WMS: the built-in instruments (made on the UI thread, PrepareWms)
    private int wmsEngineRate;
    private WmsSampler? wmsSampler;                    // Instrument Runaway on a WideMess game
    private WmsSnapshot? wmsFor;
    private int wmsRate;
    private readonly bool[] wmsUsed = new bool[WmsSampler.Voices];
    private WmsSampler? wmsVoicesOf;                   // the engine the voices' numbers (Voice.Dsp) belong to

    /// <summary>Deliberate bugs for the self-test (hook 939): 1 = only 8 notes at once (the S-DSP's count), 2 = every note on the widest loop (the pitch register out of its range, so high notes go flat).</summary>
    internal static volatile int WmsMutant;

    /// <summary>UI thread: the Direct-mode engine of the WMS chip, made ahead of the first block (it copies 64 KB and makes the chip; the bank's 1 MB is made once per process).</summary>
    private void PrepareWms()
    {
        int rate = EmulatorHub.HostRate;
        if (wmsEngine != null && wmsEngineRate == rate) return;
        var e = new WmsSampler(WmsSnapshot.BuiltInBank, rate);
        wmsEngineRate = rate;
        wmsEngine = e;
    }

    /// <summary>The voices' numbers belong to the engine they were started on: another engine starts them all over.</summary>
    private void BindWmsVoices(WmsSampler s)
    {
        if (ReferenceEquals(wmsVoicesOf, s)) return;
        wmsVoicesOf = s;
        Array.Clear(wmsUsed);
        foreach (var v in voices) { v.Dsp = -1; v.Instr = -1; }
    }

    /// <summary>Direct mode on the WMS chip: this instance's notes on the chip's built-in instruments (the Instrument parameter; 0 = the channel's own).</summary>
    private void RenderWmsDirect(Emulator emu, int channel, float* dest, int length, float gl, float gr)
    {
        DropChip();
        var s = wmsEngine;
        int rate = EmulatorHub.HostRate;
        if (s == null || wmsEngineRate != rate) { s = new WmsSampler(WmsSnapshot.BuiltInBank, rate); wmsEngine = s; wmsEngineRate = rate; }   // (a sample-rate change: rare; the first engine is made on the UI thread)
        BindWmsVoices(s);
        float vol = Get(P.Volume) / 1000f * WmsGain, peak = 0;
        for (int off = 0; off < length; off += Chunk)
        {
            int n = Math.Min(Chunk, length - off);
            UpdateWmsVoices(s, builtIn: true, channel);
            s.Render(stereo.AsSpan(0, n * 2), n);
            for (int i = 0; i < n; i++)
            {
                float l = stereo[i * 2] * vol, r = stereo[i * 2 + 1] * vol;
                peak = Math.Max(peak, Math.Max(Math.Abs(l), Math.Abs(r)));
                dest[(off + i) * 2] = l * gl;
                dest[(off + i) * 2 + 1] = r * gr;
            }
        }
        emu.SetActivity(channel, Math.Min(15, (int)(peak * 40)));
    }

    /// <summary>Instrument Runaway on a WideMess game: this instance plays the game's instruments on a WideMess chip of its own.</summary>
    private void RenderWmsSampler(Emulator emu, float* dest, int length, float gl, float gr)
    {
        DropChip();
        var snap = emu.WmsState;
        int rate = EmulatorHub.HostRate;
        if (snap == null) { FinishReleased(); new Span<float>(dest, length * 2).Clear(); return; }
        if (wmsSampler == null || !ReferenceEquals(wmsFor, snap) || wmsRate != rate)
        {
            wmsSampler = TakePrepared<WmsSampler>(snap, rate) ?? new WmsSampler(snap, rate);     // (Runaway's start and an edit prepare it on the UI thread: Emulator.PrepareWmsSamplers)
            wmsFor = snap;
            wmsRate = rate;
        }
        var s = wmsSampler;
        BindWmsVoices(s);
        float vol = Get(P.Volume) / 1000f;
        for (int off = 0; off < length; off += Chunk)
        {
            int n = Math.Min(Chunk, length - off);
            UpdateWmsVoices(s, builtIn: false, Ch.Sampler);
            s.Render(stereo.AsSpan(0, n * 2), n);
            for (int i = 0; i < n; i++)
            {
                dest[(off + i) * 2] = stereo[i * 2] * vol * gl;
                dest[(off + i) * 2 + 1] = stereo[i * 2 + 1] * vol * gr;
            }
        }
    }

    /// <summary>The control-rate step: each FL voice owns one voice of the chip (the new voices for the built-in bank and the new voices' directory, the S-DSP's for its own directory; the oldest note gives way
    /// when they are all taken), with pitch and level re-read from FL's live values, so slides and bends work.</summary>
    private void UpdateWmsVoices(WmsSampler s, bool builtIn, int channel)
    {
        int param = Get(P.Instrument);
        int instrument = builtIn ? WmsBank.Clamp(param) : s.Snapshot.Resolve(param);
        bool playable = builtIn || s.Snapshot.HasInstrument(instrument);
        bool wide = s.NeedsWide(instrument);
        int wave = builtIn ? WmsBank.WaveOf(instrument, channel, Get(P.Duty), Get(P.NoiseMode)) : 0;
        int lo = wide ? 0 : WmsSampler.WideVoices, hi = wide ? WmsSampler.WideVoices : WmsSampler.Voices;
        if (WmsMutant == 1 && wide) hi = 8;
        foreach (var v in voices)
        {
            if (v.Finished) continue;
            if (v.Released)
            {
                if (v.Dsp >= 0) { s.NoteOff(v.Dsp); wmsUsed[v.Dsp] = false; v.Dsp = -1; }
                Finish(v);
                continue;
            }
            ref var fl = ref v.Params->FinalLevels;
            if (fl.Pitch < v.PitchMin) v.PitchMin = fl.Pitch;
            if (fl.Pitch > v.PitchMax) v.PitchMax = fl.Pitch;
            double cents = FlCents(fl.Pitch);
            int level = Math.Clamp((int)Math.Round(127 * fl.Vol), 0, 127);
            if (v.Dsp < 0)
            {
                if (!playable || v.Silenced) continue;           // (a note that gave its voice to a newer one stays silent until FL releases it: it never takes a voice back)
                int dv = -1;
                for (int k = lo; k < hi; k++) if (!wmsUsed[k]) { dv = k; break; }
                if (dv < 0)
                {
                    foreach (var other in voices) if (other != v && other.Dsp >= lo && other.Dsp < hi) { dv = other.Dsp; other.Dsp = -1; other.Silenced = true; break; }   // the oldest note gives way
                    if (dv < 0) continue;
                }
                v.Dsp = dv;
                v.Instr = instrument;
                wmsUsed[dv] = true;
                if (builtIn) s.NoteOnPatch(dv, instrument, wave, Hz(cents), level);
                else s.NoteOnSample(dv, instrument, cents, level);
                if (WmsMutant == 2 && builtIn) s.ForceTable(dv, 0);   // (the deliberate bug: the pitch is worked out for the widest loop from the next step on)
            }
            else if (builtIn) s.UpdatePatch(v.Dsp, Hz(cents), level);
            else s.UpdateSample(v.Dsp, cents, level);
        }
    }

    private static double Hz(double c5Cents) => 261.6255653005986 * Math.Pow(2.0, c5Cents / 1200.0);

    /// <summary>Test hooks 900-939 (the self-test's wms and rom-wms checks): 900 whether the emulator is in WMS Direct mode, 901 in WMS Runaway, 902 how many of this instance's engine's new voices sound,
    /// 903 how many of this instance's FL voices hold a chip voice, 904 the instruments the Runaway snapshot lists (-1 none), 905 instrument index: its slot (bit 16: from the new voices' directory, bit 17: PCM),
    /// 906 instrument index: its length in samples, 907 the instrument the Instrument parameter plays now (-1 none), 908 a hash of the Runaway snapshot's bytes (0 none), 909 put a WideMess snapshot made of the
    /// frozen game's own bytes back through its project form (WMS1 write + read; 1 done, 0 refused), 910 the number of built-in instruments, 911 the chip index of WMS, 912 the console of the running game's session
    /// (0 SNES, 1 WideMess, -1 none), 913 whether the running game's sound unit is the WideMess one and unlocked, 914 the running game's description (text, into the buffer at value, at most index chars),
    /// 915 the project's instrument note (text, the same way), 939 set a deliberate bug (WmsMutant).</summary>
    private nint WmsTestHook(int n, nint index, nint value)
    {
        var emu = assignment.Emulator;
        switch (n)
        {
            case 900: return emu?.WmsDirectMode == true ? 1 : 0;
            case 901: return emu?.WmsSamplerMode == true ? 1 : 0;
            case 902: return (wmsEngine ?? wmsSampler)?.WideSounding ?? -1;
            case 903: return voices.Count(v => v.Dsp >= 0 && !v.Finished);
            case 904: return emu?.WmsState?.Instruments().Count ?? -1;
            case 905: { var l = emu?.WmsState?.Instruments(); if (l == null || (uint)index >= (uint)l.Count) return -1; var s = l[(int)index]; return s.Slot | (s.WideDir ? 1 << 16 : 0) | (s.Pcm ? 1 << 17 : 0); }
            case 906: { var l = emu?.WmsState?.Instruments(); return l == null || (uint)index >= (uint)l.Count ? -1 : l[(int)index].Length; }
            case 907: return emu?.WmsState is { } w7 ? w7.Resolve(Get(P.Instrument)) : -1;
            case 908:
            {
                var ws = emu?.WmsState;
                if (ws == null) return 0;
                ulong h = 14695981039346656037UL;
                foreach (byte b in ws.Low) h = (h ^ b) * 1099511628211UL;
                foreach (byte b in ws.High) h = (h ^ b) * 1099511628211UL;
                foreach (byte b in ws.Dsp) h = (h ^ b) * 1099511628211UL;
                foreach (byte b in ws.Wide) h = (h ^ b) * 1099511628211UL;
                return (nint)(long)(h | 1);
            }
            case 909:
            {
                var ws = emu?.WmsState;
                if (ws == null) return 0;
                var back = WmsSnapshot.FromBytes(ws.ToBytes(), out _);
                if (back == null) return 0;
                emu!.ReplaceWmsForTests(back);
                return 1;
            }
            case 910: return WmsBank.Patches.Length;
            case 911: return Cx.IndexOfChip("WMS");
            case 912: return emu?.RunningSessionKindForTests() ?? -1;
            case 913: return emu?.RunningWmsUnlockedForTests() == true ? 1 : 0;
            case 914: return CopyText(emu?.RomDescription ?? "", index, value);       // the running game's description (the status line's), into the buffer at value (at most index chars)
            case 915: return CopyText(emu?.InstrumentNote ?? "", index, value);       // the project's instrument note (a WideMess section that was not used says why)
            case 939: WmsMutant = (int)value; return 0;
        }
        return 0;
    }
}
