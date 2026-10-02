// Bogue :: BrokenNes 2: an FL Studio instrument built on BrokenNes. One FL instance is ONE CHANNEL of an emulator (pulse 1, pulse 2,
// triangle, noise, or in ROM mode the game's whole mix), like any other instrument is one voice. Instances coordinate through
// EmulatorHub: they share an emulator (its mode, sound chip, cores, game and picture) and each takes a channel of it.
//
//  Direct  FL's notes play the instance's channel of a bare NES sound chip, no ROM in between. The channel is monophonic
//          with last-note priority. Pitch, volume and slides are re-read from FL's live voice parameters every 64 samples
//          (~1.3 ms) and written straight to the APU registers, so piano-roll slides and per-note pitch automation bend
//          smoothly: far finer than a ROM-driven NES could do (one change per 16.6 ms frame).
//
//  ROM     A NES game runs once per emulator and every instance on it listens to its own channel of the game (or to the
//          whole mix). The picture is shown in every instance's editor. The CPU, PPU and sound-chip cores can be switched
//          while it runs. FL's notes do not drive the game yet: that needs the VRUN live-driver ROM
//          (see VRUN docs/26-live-audio-bridge-spec.md).
using System.Text;
using BrokenNes.Fruity;
using NesEmulator.Plugin;

namespace BrokenNes2;

public sealed unsafe class Bn2Plugin : FruityPluginBase
{
    public const double CpuHz = 1789773.0;
    private const int Chunk = 64;

    private sealed class Voice
    {
        public nint Handle, HostTag;
        public TVoiceParams* Params;
        public bool Released, Finished, KillRequested, Fresh;
        public int Dsp = -1;          // the S-DSP voice playing it in sampler mode
    }

    private readonly List<Voice> voices = new(32);
    private readonly Stack<Voice> pool = new(32);
    private readonly List<nint> toKill = new(32);
    private readonly float[] mono = new float[Chunk];
    private float[] romMono = new float[2048];
    private nint nextHandle = 1;
    private long renderCalls;

    // Direct mode: this instance's own chip, playing its channel only
    private readonly object chipGate = new();
    private NesApuInstrument? chip;
    private bool gate;
    private int curPeriod, curLevel;

    // ROM mode: where this instance reads its channel in the emulator's ring
    private long romReadPos = -1;
    private int romEpoch;

    // where the instance sits (set by EmulatorHub under its lock)
    private volatile Assignment assignment = Assignment.None;
    private bool restoring;
    private int resetRequested;

    // Instrument Runaway on a SNES game: this instance's own S-DSP over a copy of the game's sound memory
    private SnesSampler? sampler;
    private SpcSnapshot? samplerFor;
    private int samplerRate;
    private readonly bool[] dspUsed = new bool[8];
    private readonly float[] stereo = new float[Chunk * 2];

    // Instrument Runaway on a Game Boy game: this instance's own sound unit, playing one channel with the game's settings
    private GbSampler? gbSampler;
    private GbSnapshot? gbFor;
    private int gbRate, gbChannel = -1;
    private bool gbGate;

    public Bn2Plugin(FruityHost host, nint hostTag) : base(host, hostTag, Bn2Params.Build())
    {
        EmulatorHub.Resolve(this);
    }

    /// <summary>What FL shows. (The DLL and its folder stay BrokenNes2: FL finds a native plugin by its file name.)</summary>
    public const string DisplayName = "Bogue :: BrokenNes 2";
    public static FruityPluginInfo Info { get; } = new(DisplayName, DisplayName);

    protected override int StateVersion => 2;

    public Assignment Assignment => assignment;
    public Emulator? Emulator => assignment.Emulator;

    internal void SetAssignment(Assignment a)
    {
        assignment = a;
        romReadPos = -1;
        if (a.Emulator != null) SyncFromEmulator(a.Emulator);
        PrepareChip();
    }

    /// <summary>The emulator was reset (its Reset Console button): this instance clears itself at the start of its next block.</summary>
    internal void RequestReset() => Interlocked.Exchange(ref resetRequested, 1);

    /// <summary>Drops every voice (FL gets them back with Voice_Kill at the next tick: a stuck note ends), throws the chip away for a fresh one,
    /// and forgets where it was reading the game.</summary>
    private void ApplyReset()
    {
        foreach (var v in voices) v.Released = v.Finished = true;
        lock (chipGate) { chip = null; gate = false; }
        curLevel = 0;
        curPeriod = 0;
        romReadPos = -1;
        DropSampler();
        PrepareChip();
    }

    private void DropSampler()
    {
        gbSampler = null;
        gbFor = null;
        gbGate = false;
        gbChannel = -1;
        sampler = null;
        samplerFor = null;
        Array.Clear(dspUsed);
        foreach (var v in voices) v.Dsp = -1;
    }

    /// <summary>Makes the emulator-level parameter values show what the emulator it sits on has (the emulator owns them).</summary>
    internal void SyncFromEmulator(Emulator e)
    {
        Volatile.Write(ref Values[P.Core], e.Chip);
        Volatile.Write(ref Values[P.Mode], e.Mode);
        Volatile.Write(ref Values[P.Console], e.Console);
        PrepareChip();
    }

    // ---- host -> plugin ----

    public override nint Dispatcher(nint id, nint index, nint value)
    {
        if (id == Fpd.SetSampleRate && value > 0)
        {
            EmulatorHub.SetHostRate((int)value);
            return 0;
        }
        return base.Dispatcher(id, index, value);
    }

    protected override void OnParamChanged(int index, int value)
    {
        if (restoring) return;
        var e = assignment.Emulator;
        switch (index)
        {
            case P.Core: e?.SetChip(value); break;
            case P.Mode: e?.SetMode(value); break;
            case P.Console: e?.SetConsole(value); break;
            case P.Channel:
            case P.Emulator: Reresolve(); break;
        }
    }

    /// <summary>The Emulator or Channel parameter changed: take a new place (staying on the same emulator when it is on Auto).</summary>
    private void Reresolve() =>
        EmulatorHub.Resolve(this, hintEmulator: Get(P.Emulator) == 0 ? assignment.Emulator?.Id ?? 0 : 0, hintChannel: -1);

    public override nint TriggerVoice(TVoiceParams* p, nint setTag)
    {
        var v = pool.Count > 0 ? pool.Pop() : new Voice();
        v.Handle = nextHandle++;
        v.HostTag = setTag;
        v.Params = p;
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
        if (Interlocked.Exchange(ref resetRequested, 0) != 0) ApplyReset();
        var a = assignment;
        var emu = a.Emulator;
        float pan = Get(P.Pan) / 100f;
        float gl = Math.Min(1f, 1f - pan), gr = Math.Min(1f, 1f + pan);

        if (emu == null || a.Channel < 0)
        {
            FinishReleased();
            new Span<float>(dest, length * 2).Clear();
            return;
        }

        if (emu.SamplerMode)
        {
            RenderSampler(emu, dest, length, gl, gr);
            return;
        }
        if (emu.GbSamplerMode)
        {
            RenderGbSampler(emu, dest, length, gl, gr);
            return;
        }
        if (sampler != null || gbSampler != null) DropSampler();

        if (emu.Mode == 1)
        {
            FinishReleased();   // voices still come and go in FL: keep them balanced even though the game does not play them yet
            DropChip();
            if (romMono.Length < length) romMono = new float[length];
            emu.PullRom(a.Channel, ref romReadPos, ref romEpoch, romMono.AsSpan(0, length));
            float vol = Get(P.Volume) / 1000f, peak = 0;
            for (int i = 0; i < length; i++)
            {
                float s = romMono[i] * vol;
                peak = Math.Max(peak, Math.Abs(romMono[i]));
                dest[i * 2] = s * gl;
                dest[i * 2 + 1] = s * gr;
            }
            emu.SetActivity(a.Channel, Math.Min(15, (int)(peak * 40)));
            return;
        }

        if (a.Channel >= Ch.ToneCount)   // Mix in Direct mode cannot happen once the hub has re-resolved; stay silent if it does
        {
            FinishReleased();
            new Span<float>(dest, length * 2).Clear();
            return;
        }

        var c = EnsureChip(emu);
        if (c == null)
        {
            FinishReleased();
            new Span<float>(dest, length * 2).Clear();
            return;
        }

        for (int off = 0; off < length; off += Chunk)
        {
            int n = Math.Min(Chunk, length - off);
            UpdateRegisters(c, a.Channel);
            c.Render(mono.AsSpan(0, n));
            for (int i = 0; i < n; i++)
            {
                float s = mono[i];
                dest[(off + i) * 2] = s * gl;
                dest[(off + i) * 2 + 1] = s * gr;
            }
        }
        emu.SetActivity(a.Channel, gate ? curLevel : 0);
    }

    // ---- Instrument Runaway: FL notes play the game's own samples ----

    private void RenderSampler(Emulator emu, float* dest, int length, float gl, float gr)
    {
        DropChip();
        var snap = emu.Snapshot;
        int rate = EmulatorHub.HostRate;
        if (snap == null) { FinishReleased(); new Span<float>(dest, length * 2).Clear(); return; }
        if (sampler == null || !ReferenceEquals(samplerFor, snap) || samplerRate != rate)
        {
            DropSampler();
            sampler = new SnesSampler(snap, rate);
            samplerFor = snap;
            samplerRate = rate;
        }
        var s = sampler;
        float vol = Get(P.Volume) / 1000f;
        for (int off = 0; off < length; off += Chunk)
        {
            int n = Math.Min(Chunk, length - off);
            UpdateSamplerVoices(s);
            s.Render(stereo.AsSpan(0, n * 2), n);
            for (int i = 0; i < n; i++)
            {
                dest[(off + i) * 2] = stereo[i * 2] * vol * gl;
                dest[(off + i) * 2 + 1] = stereo[i * 2 + 1] * vol * gr;
            }
        }
    }

    /// <summary>The control-rate step of sampler mode: each FL voice owns one S-DSP voice (eight, so eight notes at once; the oldest is taken
    /// over for a ninth), with pitch and volume re-read from FL's live values like in Direct mode, so slides and bends work.</summary>
    private void UpdateSamplerVoices(SnesSampler s)
    {
        int instrument = samplerFor?.Resolve(Get(P.Instrument)) ?? Get(P.Instrument);
        bool hasSample = samplerFor?.HasSample(instrument) ?? false;   // an empty slot of the game's sample table plays nothing (the DSP would wander through other data)
        foreach (var v in voices)
        {
            if (v.Finished) continue;
            if (v.Released)
            {
                if (v.Dsp >= 0) { s.NoteOff(v.Dsp); dspUsed[v.Dsp] = false; v.Dsp = -1; }
                v.Finished = true;
                continue;
            }
            ref var fl = ref v.Params->FinalLevels;
            double cents = fl.Pitch + Get(P.Coarse) * 100 + Get(P.Fine);
            int pitch = SnesSampler.PitchFor(cents);
            int level = Math.Clamp((int)Math.Round(127 * fl.Vol), 0, 127);
            if (v.Dsp < 0)
            {
                if (!hasSample) continue;
                int dv = Array.IndexOf(dspUsed, false);
                if (dv < 0)
                {
                    foreach (var other in voices) if (other != v && other.Dsp >= 0) { dv = other.Dsp; other.Dsp = -1; break; }   // the oldest note gives way
                    if (dv < 0) continue;
                }
                v.Dsp = dv;
                dspUsed[dv] = true;
                s.NoteOn(dv, instrument, pitch, level, level);
            }
            else s.Update(v.Dsp, pitch, level, level);
        }
    }

    /// <summary>The Game Boy channel (0 pulse 1, 1 pulse 2, 2 wave, 3 noise) the Channel parameter picks in Game Boy Runaway: Auto and Mix are pulse 1.</summary>
    internal static int GbChannelOf(int channelParam) => channelParam is >= 1 and <= 4 ? channelParam - 1 : 0;

    private void RenderGbSampler(Emulator emu, float* dest, int length, float gl, float gr)
    {
        DropChip();
        var snap = emu.GbState;
        int rate = EmulatorHub.HostRate;
        if (snap == null) { FinishReleased(); new Span<float>(dest, length * 2).Clear(); return; }
        if (gbSampler == null || !ReferenceEquals(gbFor, snap) || gbRate != rate)
        {
            DropSampler();
            gbSampler = new GbSampler(snap, rate);
            gbFor = snap;
            gbRate = rate;
        }
        var s = gbSampler;
        float vol = Get(P.Volume) / 1000f;
        for (int off = 0; off < length; off += Chunk)
        {
            int n = Math.Min(Chunk, length - off);
            UpdateGbVoice(s);
            s.Render(stereo.AsSpan(0, n * 2), n);
            for (int i = 0; i < n; i++)
            {
                dest[(off + i) * 2] = stereo[i * 2] * vol * gl;
                dest[(off + i) * 2 + 1] = stereo[i * 2 + 1] * vol * gr;
            }
        }
    }

    /// <summary>The control-rate step of Game Boy Runaway: the newest held note plays the chosen channel (last note wins, like a Direct-mode channel);
    /// pitch follows FL's live value so slides and bends work. Volume is the note's velocity at its start (the Game Boy's envelope cannot be
    /// changed under a playing note).</summary>
    private void UpdateGbVoice(GbSampler s)
    {
        int ch = GbChannelOf(Get(P.Channel));
        var owner = ResolveOwner();
        if (gbGate && (owner == null || gbChannel != ch)) { s.NoteOff(gbChannel); gbGate = false; }
        if (owner == null) return;
        ref var fl = ref owner.Params->FinalLevels;
        double semis = 60.0 + (fl.Pitch + Get(P.Coarse) * 100 + Get(P.Fine)) / 100.0;
        double hz = 440.0 * Math.Pow(2.0, (semis - 69.0) / 12.0);
        bool shortNoise = Get(P.NoiseMode) == 1;
        if (!gbGate || owner.Fresh)
        {
            int level = Math.Clamp((int)Math.Round(15 * fl.Vol), 0, 15);
            int duty = Get(P.Instrument) is >= 1 and <= 4 ? Get(P.Instrument) - 1 : -1;   // 0 = the game's own duty
            s.NoteOn(ch, hz, level, duty, shortNoise);
            owner.Fresh = false;
            gbGate = true;
            gbChannel = ch;
        }
        else s.Pitch(ch, hz, shortNoise);
    }

    // ---- the Direct-mode chip ----

    /// <summary>The chip of this instance, made when it sits on a tone channel of a Direct emulator and kept on that emulator's sound chip.</summary>
    private NesApuInstrument? EnsureChip(Emulator emu)
    {
        string core = emu.ChipId;
        int rate = EmulatorHub.HostRate;
        var c = Volatile.Read(ref chip);
        if (c != null && c.CoreId == core && c.HostSampleRate == rate) return c;
        lock (chipGate)
        {
            try
            {
                if (chip == null) { chip = new NesApuInstrument(core, rate); gate = false; }
                else
                {
                    if (chip.CoreId != core) chip.SetCore(core);
                    if (chip.HostSampleRate != rate) chip.HostSampleRate = rate;
                }
            }
            catch (Exception e) { Diag.Log($"sound chip {core} failed: {e.Message}"); chip = null; }
            return chip;
        }
    }

    /// <summary>Builds the chip ahead of the first block when this instance needs one, on the thread that placed it (not the mixer).</summary>
    private void PrepareChip()
    {
        var a = assignment;
        var e = a.Emulator;
        if (e != null && e.Mode == 0 && a.Channel is >= 0 and < Ch.ToneCount) EnsureChip(e);
    }

    private void DropChip()
    {
        if (chip == null) return;
        lock (chipGate) { chip = null; gate = false; }
    }

    private void FinishReleased()
    {
        foreach (var v in voices)
            if (v.Released) v.Finished = true;
    }

    /// <summary>The newest unreleased voice owns the channel; released voices are finished (the NES has no release tail: the channel
    /// simply closes).</summary>
    private Voice? ResolveOwner()
    {
        Voice? owner = null;
        for (int i = voices.Count - 1; i >= 0; i--)
        {
            var v = voices[i];
            if (v.Released) { v.Finished = true; continue; }
            if (v.Finished) continue;
            owner ??= v;
        }
        return owner;
    }

    /// <summary>The control-rate step of Direct mode: write this channel's pitch, volume and gate to the chip.</summary>
    private void UpdateRegisters(NesApuInstrument c, int ch) => Apply(c, ch, ResolveOwner());

    private void W(NesApuInstrument c, int reg, int value)
    {
        if (c.Shadow(reg) != (byte)value) c.Write(reg, (byte)value);
    }

    private void Apply(NesApuInstrument c, int ch, Voice? v)
    {
        if (v == null)
        {
            if (gate) { Silence(c, ch); gate = false; curLevel = 0; }
            return;
        }

        ref var fl = ref v.Params->FinalLevels;
        double cents = fl.Pitch + Get(P.Coarse) * 100 + Get(P.Fine);
        double freq = 440.0 * Math.Pow(2.0, ((60.0 + cents / 100.0) - 69.0) / 12.0);
        double master = Get(P.Volume) / 1000.0;
        int level = Math.Clamp((int)Math.Round(15 * fl.Vol * master), 0, 15);
        bool retrigger = v.Fresh || !gate;
        v.Fresh = false;

        switch (ch)
        {
            case Ch.Pulse1:
            case Ch.Pulse2:
            {
                int b = ch * 4;
                int period = (int)Math.Round(CpuHz / (16.0 * freq) - 1.0);
                if (period > 0x7FF) period = 0x7FF;
                if (period < 8) level = 0;                       // the pulse channel mutes below period 8
                period = Math.Max(period, 8);
                W(c, b, (Get(P.Duty) << 6) | 0x30 | level);      // length halt + constant volume
                W(c, b + 1, 0x08);                               // sweep off
                int hi = (period >> 8) & 7, lo = period & 0xFF;
                if (retrigger || hi != ((curPeriod >> 8) & 7))
                {
                    W(c, b + 2, lo);
                    c.Write(b + 3, (byte)(hi | 0xF8));           // a $4003 write restarts the waveform: only on a new note or a high-bit change
                }
                else W(c, b + 2, lo);
                curPeriod = period;
                break;
            }
            case Ch.Triangle:
            {
                int period = (int)Math.Round(CpuHz / (32.0 * freq) - 1.0);
                period = Math.Clamp(period, 2, 0x7FF);
                W(c, 8, level > 0 ? 0xFF : 0x80);                // the triangle has no volume: on or off
                int hi = (period >> 8) & 7, lo = period & 0xFF;
                if (retrigger || hi != ((curPeriod >> 8) & 7))
                {
                    W(c, 0x0A, lo);
                    c.Write(0x0B, (byte)(hi | 0xF8));
                }
                else W(c, 0x0A, lo);
                curPeriod = period;
                break;
            }
            default:
            {
                // Noise has 16 fixed pitches: higher notes pick a shorter period. Bends step through them.
                double semis = 60.0 + cents / 100.0;
                int idx = Math.Clamp(15 - (int)Math.Floor((semis - 24.0) / 3.0), 0, 15);
                W(c, 0x0C, 0x30 | level);
                W(c, 0x0E, (Get(P.NoiseMode) << 7) | idx);
                if (retrigger) c.Write(0x0F, 0xF8);
                curPeriod = idx;
                break;
            }
        }
        gate = true;
        curLevel = level;
    }

    private void Silence(NesApuInstrument c, int ch)
    {
        switch (ch)
        {
            case Ch.Pulse1: case Ch.Pulse2: W(c, ch * 4, (Get(P.Duty) << 6) | 0x30); break;
            case Ch.Triangle: W(c, 8, 0x80); break;
            default: W(c, 0x0C, 0x30); break;
        }
    }

    // ---- saved state: the parameters (the emulator-level ones as the emulator has them), the game's path, and where it sat ----

    private const uint PlaceMagic = 0x32554D45; // "EMU2"
    private const uint SpcMagic = 0x31435053;   // "SPC1"
    private const uint GbMagic = 0x31534247;    // "GBS1"

    public override void SaveRestoreState(ComStream stream, bool save)
    {
        if (save)
        {
            base.SaveRestoreState(stream, true);
            var a = assignment;
            // The ROM path (UTF-8, int length first; 0 = the built-in game). The ROM itself is not stored in the project: the file is read again when it opens.
            var bytes = Encoding.UTF8.GetBytes(a.Emulator?.RomPath ?? "");
            int n = bytes.Length;
            stream.Write(&n, 4);
            if (n > 0) fixed (byte* p = bytes) stream.Write(p, (uint)n);
            // Where the instance sat, so a reopened project keeps its emulator numbers and channels.
            uint magic = PlaceMagic;
            int emu = a.Emulator?.Id ?? 0, ch = a.Channel;
            stream.Write(&magic, 4);
            stream.Write(&emu, 4);
            stream.Write(&ch, 4);
            // A project saved in Instrument Runaway on a SNES game keeps a copy of the game's sound memory, so the instruments come back.
            var snap = a.Emulator is { Runaway: true } running ? running.Snapshot : null;
            var gbSnap = a.Emulator is { Runaway: true } gbRunning ? gbRunning.GbState : null;
            if (gbSnap != null)
            {
                uint gbMagic = GbMagic;
                var gbBytes = gbSnap.ToBytes();
                int gbLen = gbBytes.Length;
                stream.Write(&gbMagic, 4);
                stream.Write(&gbLen, 4);
                fixed (byte* p = gbBytes) stream.Write(p, (uint)gbLen);
            }
            if (snap != null)
            {
                uint spcMagic = SpcMagic;
                var snapBytes = snap.ToBytes();
                int snapLen = snapBytes.Length;
                stream.Write(&spcMagic, 4);
                stream.Write(&snapLen, 4);
                fixed (byte* p = snapBytes) stream.Write(p, (uint)snapLen);
            }
            return;
        }

        string? romPath = null;
        int hintEmu = 0, hintCh = -1;
        SpcSnapshot? restoredSnapshot = null;
        GbSnapshot? restoredGb = null;
        restoring = true;
        try
        {
            base.SaveRestoreState(stream, false);
            if (!RestoredCleanly) return;
            int len = 0;
            if (stream.Read(&len, 4) && len is >= 0 and <= 4096)
            {
                bool ok = true;
                if (len > 0)
                {
                    var buf = new byte[len];
                    fixed (byte* p = buf) ok = stream.Read(p, (uint)len);
                    if (ok) romPath = Encoding.UTF8.GetString(buf);
                }
                uint magic = 0; int e = 0, c = 0;
                if (ok && stream.Read(&magic, 4) && magic == PlaceMagic && stream.Read(&e, 4) && stream.Read(&c, 4))
                {
                    hintEmu = e; hintCh = c;
                    uint tag = 0; int slen = 0;
                    if (stream.Read(&tag, 4) && stream.Read(&slen, 4))
                    {
                        if (tag == SpcMagic && slen == SpcSnapshot.AramSize + SpcSnapshot.DspSize)
                        {
                            var snapBuf = new byte[slen];
                            fixed (byte* p = snapBuf) if (stream.Read(p, (uint)slen)) restoredSnapshot = SpcSnapshot.FromBytes(snapBuf);
                        }
                        else if (tag == GbMagic && slen == GbSnapshot.RegCount + GbSnapshot.WaveSize + 1)
                        {
                            var gbBuf = new byte[slen];
                            fixed (byte* p = gbBuf) if (stream.Read(p, (uint)slen)) restoredGb = GbSnapshot.FromBytes(gbBuf);
                        }
                    }
                }
            }
        }
        finally { restoring = false; }
        if (!RestoredCleanly) return;

        // What the saved parameters wish for the emulator, then the place: an emulator that is new is configured as saved, one that
        // already exists keeps what it has (every instance of a project carries the same values).
        int wMode = Get(P.Mode), wChip = Get(P.Core);
        EmulatorHub.Resolve(this, hintEmu, hintCh);
        var emu2 = assignment.Emulator;
        if (emu2 != null && emu2.MemberCount == 1) emu2.Adopt(wMode, wChip, romPath, restoredSnapshot, restoredGb);
    }

    // ---- test hooks (see Fpd.TestBase) ----

    private IEnumerable<Bn2Plugin> Peers() => assignment.Emulator?.MembersSnapshot() ?? new List<Bn2Plugin> { this };

    private Bn2Plugin? OwnerOf(int channel) => assignment.Emulator?.Owner(channel & 3) ?? (channel == assignment.Channel ? this : null);

    /// <summary>Test-host hooks: 1 period of channel, 2 level, 3 voices held by the emulator's instances, 4 bytes allocated on this
    /// thread, 5 sound chip index, 6 register shadow of this instance's chip, 7 render calls, 8 gate of channel, 9 ROM loaded,
    /// 10 picture hash, 11 ROM frames run, 12 ROM crashed, 13 picture version, 14 console, 15 console of the running game (-1 none),
    /// 16 emulator id (0 none), 17 channel (-1 none), 18 emulators in the process, 19 instances on this emulator, 20 problem?,
    /// 21 mode, 22 activity of channel, 24 emulator auto-created?, 30+ editor hooks (see <see cref="Bn2Editor"/>).</summary>
    protected override nint TestDispatcher(int id, nint index, nint value)
    {
        var emu = assignment.Emulator;
        switch (id - Fpd.TestBase)
        {
            case 1: return OwnerOf((int)index)?.curPeriod ?? 0;
            case 2: return OwnerOf((int)index)?.curLevel ?? 0;
            case 3: return Peers().Sum(p => p.voices.Count);
            case 4: return (nint)GC.GetAllocatedBytesForCurrentThread();
            case 5: return emu?.Chip ?? Get(P.Core);
            case 6: return chip?.Shadow((int)index & 0x1F) ?? 0;
            case 7: return (nint)renderCalls;
            case 8: return OwnerOf((int)index)?.gate == true ? 1 : 0;
            case 9: return emu?.RomLoaded == true ? 1 : 0;
            case 10: return (nint)(emu?.PictureHash() ?? 0);
            case 11: return (nint)(emu?.FramesRun ?? 0);
            case 12: return emu?.RomCrashed == true ? 1 : 0;
            case 13: return (nint)(emu?.PictureVersion ?? 0);
            case 14: return emu?.Console ?? 0;
            case 15: return emu?.RomConsole ?? -1;
            case 16: return emu?.Id ?? 0;
            case 17: return assignment.Channel;
            case 18: return EmulatorHub.Count;
            case 19: return emu?.MemberCount ?? 0;
            case 20: return assignment.Problem != null ? 1 : 0;
            case 21: return emu?.Mode ?? 0;
            case 22: return emu?.Activity((int)index % Ch.Count) ?? 0;
            case 24: return emu?.AutoCreated == true ? 1 : 0;
            case 43: emu?.Reset(); return 0;
            case 44: emu?.SetPad(this, (int)value); return 0;
            case 45: return emu?.Runaway == true ? 1 : 0;
            case 46: emu?.ReplaceSnapshotForTests(SpcSnapshot.TestSample()); return 0;
            case 47: emu?.SetRunaway(value != 0); return 0;
            case 48: return emu?.SamplerMode == true ? 1 : 0;
            case 49: return emu?.PadBits ?? 0;
            case 50: return emu?.GbSamplerMode == true ? 1 : 0;
            case 51: emu?.PokeSnesToneForTests(); return 0;
            case 40: About.ResetForTests(deleteState: true); return 0;
            case 41: About.ResetForTests(deleteState: false); return 0;
            case 42: { var path = System.Runtime.InteropServices.Marshal.PtrToStringUni(value); return emu == null || path == null ? 1 : emu.LoadRomFile(path) == null ? 0 : 1; }
            default: return TestEditor(id - Fpd.TestBase, index, value);
        }
    }

    public override IFruityEditor? CreateEditor(nint parent) => new Bn2Editor(this, parent);

    private nint TestEditor(int n, nint index, nint value) => Editor is Bn2Editor ed ? ed.Test(n, index, value) : 0;

    public override void Destroy()
    {
        EmulatorHub.Leave(this);
        voices.Clear();
        lock (chipGate) chip = null;
    }
}
