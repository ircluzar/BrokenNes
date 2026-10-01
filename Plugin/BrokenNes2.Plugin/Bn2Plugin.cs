// BrokenNes2, Direct mode: FL's notes drive a bare NES sound chip. Four channels (pulse 1, pulse 2,
// triangle, noise), each monophonic with last-note priority; the note's colour in the piano roll
// picks the channel (like Bendy's colour -> MIDI channel). Pitch, volume and slides are re-read from
// FL's live voice parameters every 64 samples (~1.3 ms) and written straight to the APU registers, so
// piano-roll slides and per-note pitch automation bend smoothly: far finer than a ROM-driven NES
// could do (one change per 16.6 ms frame).
using BrokenNes.Fruity;
using NesEmulator.Plugin;

namespace BrokenNes2;

public sealed unsafe class Bn2Plugin : FruityPluginBase
{
    public const double CpuHz = 1789773.0;
    private const int Chunk = 64;
    private const int NumChannels = 4;

    private sealed class Voice
    {
        public nint Handle, HostTag;
        public TVoiceParams* Params;
        public int Color;
        public bool Released, Finished, KillRequested, Fresh;
    }

    private readonly List<Voice> voices = new(32);
    private readonly Stack<Voice> pool = new(32);
    private readonly List<nint> toKill = new(32);
    private readonly Voice?[] owners = new Voice?[NumChannels];
    private readonly bool[] gate = new bool[NumChannels];
    private readonly int[] curPeriod = new int[NumChannels];
    private readonly int[] curLevel = new int[NumChannels];
    private readonly float[] mono = new float[Chunk];
    private readonly IReadOnlyList<string> cores = Bn2Params.Cores;

    private NesApuInstrument instrument;
    private nint nextHandle = 1;
    private int pendingCore = -1;
    private int pendingRate;
    private long renderCalls;

    public Bn2Plugin(FruityHost host, nint hostTag) : base(host, hostTag, Bn2Params.Build())
    {
        instrument = new NesApuInstrument(cores[Get(P.Core)], 44100);
    }

    public static FruityPluginInfo Info { get; } = new("BrokenNes2", "BrokenNes2");

    // ---- host -> plugin ----

    public override nint Dispatcher(nint id, nint index, nint value)
    {
        if (id == Fpd.SetSampleRate && value > 0)
        {
            Volatile.Write(ref pendingRate, (int)value);
            return 0;
        }
        return base.Dispatcher(id, index, value);
    }

    protected override void OnParamChanged(int index, int value)
    {
        if (index == P.Core) Volatile.Write(ref pendingCore, value);
    }

    public override nint TriggerVoice(TVoiceParams* p, nint setTag)
    {
        var v = pool.Count > 0 ? pool.Pop() : new Voice();
        v.Handle = nextHandle++;
        v.HostTag = setTag;
        v.Params = p;
        v.Color = Host.VoiceProcessEvent(setTag, Fpv.GetColor, 0, 0);
        v.Released = v.Finished = v.KillRequested = false;
        v.Fresh = true;
        voices.Add(v);
        return v.Handle;
    }

    public override void ReleaseVoice(nint handle)
    {
        var v = Find(handle);
        if (v != null) v.Released = true;
    }

    public override void KillVoice(nint handle)
    {
        var v = Find(handle);
        if (v == null) return;
        voices.Remove(v);
        pool.Push(v);
    }

    private Voice? Find(nint handle)
    {
        foreach (var v in voices)
            if (v.Handle == handle) return v;
        return null;
    }

    /// <summary>Finished voices are handed back to FL here, outside any iteration, since FL may call Voice_Kill re-entrantly.</summary>
    public override void NewTick()
    {
        toKill.Clear();
        foreach (var v in voices)
            if (v.Finished && !v.KillRequested)
            {
                v.KillRequested = true;
                toKill.Add(v.HostTag);
            }
        foreach (var tag in toKill)
            Host.VoiceKill(tag, true);
    }

    // ---- audio ----

    public override void Render(float* dest, int length)
    {
        renderCalls++;
        int rate = Volatile.Read(ref pendingRate);
        if (rate > 0 && rate != instrument.HostSampleRate) instrument.HostSampleRate = rate;
        int core = Interlocked.Exchange(ref pendingCore, -1);
        if (core >= 0 && (uint)core < cores.Count && cores[core] != instrument.CoreId)
        {
            try { instrument.SetCore(cores[core]); }
            catch (Exception e) { Diag.Log($"core switch to {cores[core]} failed: {e.Message}"); }
        }

        float pan = Get(P.Pan) / 100f;
        float gl = Math.Min(1f, 1f - pan), gr = Math.Min(1f, 1f + pan);

        for (int off = 0; off < length; off += Chunk)
        {
            int n = Math.Min(Chunk, length - off);
            UpdateRegisters();
            instrument.Render(mono.AsSpan(0, n));
            for (int i = 0; i < n; i++)
            {
                float s = mono[i];
                dest[(off + i) * 2] = s * gl;
                dest[(off + i) * 2 + 1] = s * gr;
            }
        }
    }

    private int ChannelOf(Voice v, int mode) => mode == 0 ? v.Color & 3 : mode - 1;

    /// <summary>The control-rate step: pick each NES channel's sounding voice (the newest unreleased one)
    /// and write its pitch, volume and gate to the chip.</summary>
    private void UpdateRegisters()
    {
        int mode = Get(P.Channel);
        Array.Clear(owners);
        for (int i = voices.Count - 1; i >= 0; i--)
        {
            var v = voices[i];
            if (v.Released) { v.Finished = true; continue; }   // the NES has no release tail: the channel simply closes
            if (v.Finished) continue;
            int ch = ChannelOf(v, mode);
            owners[ch] ??= v;
        }
        for (int ch = 0; ch < NumChannels; ch++) Apply(ch, owners[ch]);
    }

    private void W(int reg, int value)
    {
        if (instrument.Shadow(reg) != (byte)value) instrument.Write(reg, (byte)value);
    }

    private void Apply(int ch, Voice? v)
    {
        if (v == null)
        {
            if (gate[ch]) { Silence(ch); gate[ch] = false; curLevel[ch] = 0; }
            return;
        }

        ref var fl = ref v.Params->FinalLevels;
        double cents = fl.Pitch + Get(P.Coarse) * 100 + Get(P.Fine);
        double freq = 440.0 * Math.Pow(2.0, ((60.0 + cents / 100.0) - 69.0) / 12.0);
        double master = Get(P.Volume) / 1000.0;
        int level = Math.Clamp((int)Math.Round(15 * fl.Vol * master), 0, 15);
        bool retrigger = v.Fresh || !gate[ch];
        v.Fresh = false;

        switch (ch)
        {
            case 0:
            case 1:
            {
                int b = ch * 4;
                int period = (int)Math.Round(CpuHz / (16.0 * freq) - 1.0);
                if (period > 0x7FF) period = 0x7FF;
                if (period < 8) level = 0;                       // the pulse channel mutes below period 8
                period = Math.Max(period, 8);
                W(b, (Get(P.Duty) << 6) | 0x30 | level);         // length halt + constant volume
                W(b + 1, 0x08);                                  // sweep off
                int hi = (period >> 8) & 7, lo = period & 0xFF;
                if (retrigger || hi != ((curPeriod[ch] >> 8) & 7))
                {
                    W(b + 2, lo);
                    instrument.Write(b + 3, (byte)(hi | 0xF8));  // a $4003 write restarts the waveform: only on a new note or a high-bit change
                }
                else W(b + 2, lo);
                curPeriod[ch] = period;
                break;
            }
            case 2:
            {
                int period = (int)Math.Round(CpuHz / (32.0 * freq) - 1.0);
                period = Math.Clamp(period, 2, 0x7FF);
                W(8, level > 0 ? 0xFF : 0x80);                   // the triangle has no volume: on or off
                int hi = (period >> 8) & 7, lo = period & 0xFF;
                if (retrigger || hi != ((curPeriod[ch] >> 8) & 7))
                {
                    W(0x0A, lo);
                    instrument.Write(0x0B, (byte)(hi | 0xF8));
                }
                else W(0x0A, lo);
                curPeriod[ch] = period;
                break;
            }
            default:
            {
                // Noise has 16 fixed pitches: higher notes pick a shorter period. Bends step through them.
                double semis = 60.0 + cents / 100.0;
                int idx = Math.Clamp(15 - (int)Math.Floor((semis - 24.0) / 3.0), 0, 15);
                W(0x0C, 0x30 | level);
                W(0x0E, (Get(P.NoiseMode) << 7) | idx);
                if (retrigger) instrument.Write(0x0F, 0xF8);
                curPeriod[ch] = idx;
                break;
            }
        }
        gate[ch] = true;
        curLevel[ch] = level;
    }

    private void Silence(int ch)
    {
        switch (ch)
        {
            case 0: case 1: W(ch * 4, (Get(P.Duty) << 6) | 0x30); break;
            case 2: W(8, 0x80); break;
            default: W(0x0C, 0x30); break;
        }
    }

    // ---- editor readout and test hooks ----

    public override string GetReadout()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"Chip: {instrument.CoreId} ({instrument.NativeSampleRate} Hz native)   voices held by FL: {voices.Count}\n");
        string[] names = ["Pulse 1", "Pulse 2", "Triangle", "Noise"];
        for (int ch = 0; ch < NumChannels; ch++)
        {
            if (!gate[ch]) { sb.Append($"{names[ch],-9}: -\n"); continue; }
            string pitch = ch == 3 ? $"noise period index {curPeriod[ch]}"
                : $"period {curPeriod[ch]} = {CpuHz / ((ch == 2 ? 32 : 16) * (curPeriod[ch] + 1.0)):0.00} Hz";
            sb.Append($"{names[ch],-9}: {pitch}   level {curLevel[ch]}\n");
        }
        sb.Append("Note colour picks the channel (Channel = Auto): 0 pulse 1, 1 pulse 2, 2 triangle, 3 noise, repeating.");
        return sb.ToString();
    }

    /// <summary>Test-host hooks (see <see cref="Fpd.TestBase"/>): 1 period[ch], 2 level[ch], 3 voices held, 4 bytes
    /// allocated on this thread, 5 chip name length-free index, 6 APU register shadow[index], 7 render calls, 8 gate[ch].</summary>
    protected override nint TestDispatcher(int id, nint index, nint value) => (id - Fpd.TestBase) switch
    {
        1 => curPeriod[(int)index & 3],
        2 => curLevel[(int)index & 3],
        3 => voices.Count,
        4 => (nint)GC.GetAllocatedBytesForCurrentThread(),
        5 => Array.IndexOf(cores.ToArray(), instrument.CoreId),
        6 => instrument.Shadow((int)index & 0x1F),
        7 => (nint)renderCalls,
        8 => gate[(int)index & 3] ? 1 : 0,
        _ => 0,
    };

    public override void Destroy() { voices.Clear(); }
}
