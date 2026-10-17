// BrokenNes2: an FL Studio instrument built on BrokenNes. One FL instance is ONE CHANNEL of an emulator (pulse 1, pulse 2,
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
        public int Slot = -1;         // the slot of the Sega sampler playing it (see SegaSnapshot.SlotBase), -1 none
        public int Instr = -1;        // the instrument it was started on
        public bool Silenced;         // it lost its slot to a newer note: silent until released
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

    // Instrument Runaway on a Master System, Game Gear or Genesis game: this instance's own set of the game's sound chips, set up as the game had them
    private SegaSampler? segaSampler;
    private SegaSnapshot? segaFor;
    private int segaRate;
    private readonly bool[] slotUsed = new bool[32];

    // Instrument Runaway on a Game Boy game: this instance's own sound unit, playing one channel with the game's settings
    private GbSampler? gbSampler;
    private GbSnapshot? gbFor;
    private int gbRate, gbChannel = -1;
    private bool gbGate;

    public Bn2Plugin(FruityHost host, nint hostTag) : base(host, hostTag, Bn2Params.Build())
    {
        EmulatorHub.Resolve(this);
    }

    /// <summary>What FL shows. (The DLL and its folder have the same name: FL finds a native plugin by its file name.)</summary>
    public const string DisplayName = "BrokenNes2";
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
        segaSampler = null;
        segaFor = null;
        Array.Clear(dspUsed);
        Array.Clear(slotUsed);
        foreach (var v in voices) { v.Dsp = -1; v.Slot = -1; v.Instr = -1; v.Silenced = false; }
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
            case P.Voice:
            case P.Emulator: Reresolve(); break;
        }
    }

    /// <summary>The channel this instance asks for: the Sega chip voice its Voice parameter names, else the one its Channel parameter names; -1 is Auto.</summary>
    public int RequestedChannel => Get(P.Voice) != 0 ? Ch.OfVoice(Get(P.Voice)) : Get(P.Channel) - 1;

    /// <summary>Puts the Channel and Voice parameters to ask for <paramref name="channel"/> (-1: Auto). Used by the editor; FL sees both parameter changes.</summary>
    internal void SetChannelFromUi(int channel)
    {
        int voice = Ch.VoiceOf(channel), chan = channel is >= 0 and < Ch.Count ? channel + 1 : 0;
        if (Get(P.Voice) != voice) SetFromUi(P.Voice, voice);
        if (Get(P.Channel) != chan) SetFromUi(P.Channel, chan);
    }

    /// <summary>The Emulator, Channel or Voice parameter changed: take a new place (staying on the same emulator when it is on Auto).</summary>
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
        v.Slot = -1; v.Instr = -1; v.Silenced = false;
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
        if (emu.SegaSamplerMode)
        {
            RenderSegaSampler(emu, dest, length, gl, gr);
            return;
        }
        if (sampler != null || gbSampler != null || segaSampler != null) DropSampler();

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

    private void RenderSegaSampler(Emulator emu, float* dest, int length, float gl, float gr)
    {
        DropChip();
        var snap = emu.SegaState;
        int rate = EmulatorHub.HostRate;
        if (snap == null) { FinishReleased(); new Span<float>(dest, length * 2).Clear(); return; }
        if (segaSampler == null || !ReferenceEquals(segaFor, snap) || segaRate != rate)
        {
            DropSampler();
            segaSampler = snap.Kind switch
            {
                SegaSnapshot.KindOpn => new OpnSampler(snap, rate),
                SegaSnapshot.KindOpll => new OpllSampler(snap, rate),
                _ => new PsgSampler(snap, rate),
            };
            segaFor = snap;
            segaRate = rate;
        }
        var s = segaSampler;
        float vol = Get(P.Volume) / 1000f;
        for (int off = 0; off < length; off += Chunk)
        {
            int n = Math.Min(Chunk, length - off);
            UpdateSegaVoices(s, snap);
            s.Render(stereo.AsSpan(0, n * 2), n);
            for (int i = 0; i < n; i++)
            {
                dest[(off + i) * 2] = stereo[i * 2] * vol * gl;
                dest[(off + i) * 2 + 1] = stereo[i * 2 + 1] * vol * gr;
            }
        }
    }

    /// <summary>The control-rate step of Sega Runaway: each FL voice owns a slot of the sampler (the instrument's range of them: the YM2413's nine channels, the YM2612's six, eight DAC voices, the PSG's three tones or its noise; the
    /// oldest note gives way for one more), with pitch and level re-read from FL's live values like in Direct mode, so slides and bends work. A note that lost its slot stays silent until it ends.</summary>
    private void UpdateSegaVoices(SegaSampler s, SegaSnapshot snap)
    {
        int instrument = snap.Resolve(Get(P.Instrument));
        foreach (var v in voices)
        {
            if (v.Finished) continue;
            if (v.Released)
            {
                if (v.Slot >= 0) { s.NoteOff(v.Slot); slotUsed[v.Slot] = false; v.Slot = -1; }
                v.Finished = true;
                continue;
            }
            if (v.Silenced) continue;
            ref var fl = ref v.Params->FinalLevels;
            double cents = fl.Pitch + Get(P.Coarse) * 100 + Get(P.Fine);
            double hz = 440.0 * Math.Pow(2.0, ((60.0 + cents / 100.0) - 69.0) / 12.0);
            double velocity = Math.Clamp(fl.Vol, 0.0, 1.0);
            if (v.Slot >= 0 && v.Instr != instrument) { s.NoteOff(v.Slot); slotUsed[v.Slot] = false; v.Slot = -1; }     // another instrument was picked under a held note: it starts again on that one
            if (v.Slot >= 0) { s.Update(v.Slot, hz, velocity); continue; }
            int first = snap.SlotBase(instrument), count = snap.Slots(instrument), slot = -1;
            for (int i = first; i < first + count && i < slotUsed.Length; i++) if (!slotUsed[i]) { slot = i; break; }
            if (slot < 0)
            {
                // all taken: the oldest note of this instrument's slots gives way
                foreach (var other in voices)
                    if (other != v && other.Slot >= first && other.Slot < first + count)
                    {
                        slot = other.Slot;
                        s.NoteOff(slot);
                        other.Slot = -1; other.Silenced = true;
                        break;
                    }
                if (slot < 0) { v.Silenced = true; continue; }
            }
            slotUsed[slot] = true;
            v.Slot = slot; v.Instr = instrument;
            s.NoteOn(slot, instrument, hz, velocity);
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
                // The Master System chip cannot play below 109 Hz. A keyboard note must be the pitch asked for or nothing, so such notes are silent (and counted),
                // not moved an octave up as they are for a NES game on that chip.
                NesEmulator.APU_SMS.DefaultLowNotes = NesEmulator.LowNotePolicy.Drop;
                // No CPU reads this chip back: it only takes registers and plays, so its front NES core (frame IRQ, $4015, DMC) is not stepped (it would only cost time).
                NesEmulator.APU_SMS.DefaultStepFront = false;
                // The Genesis chip: the same two switches (its PSG-square patch cannot play below 109 Hz; no CPU reads the chip back). Its FM voices go down to the NES's own 27 Hz, so nothing is dropped there.
                NesEmulator.APU_MD.DefaultLowNotes = NesEmulator.LowNotePolicy.Drop;
                NesEmulator.APU_MD.DefaultStepFront = false;
                // The Master System's FM chip: the same two switches (its PSG-square patch cannot play below 109 Hz; no CPU reads the chip back).
                NesEmulator.APU_SMSFM.DefaultLowNotes = NesEmulator.LowNotePolicy.Drop;
                NesEmulator.APU_SMSFM.DefaultStepFront = false;
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
        // The Genesis chip plays its pulses with the FM patch the Instrument parameter picks (an index into the bank; out of range is the last). Each instance owns its chip, so each has its own patch.
        if (c.Apu is NesEmulator.APU_MD md)
        {
            int patch = NesEmulator.Mix.FmPatchBank.Clamp(Get(P.Instrument));
            if (md.Patch != patch) md.Patch = patch;
        }
        else if (c.Apu is NesEmulator.APU_SMSFM sf)
        {
            // The Master System FM chip: the Instrument parameter is an entry of its bank (the chip's 15 instruments, 6 designed ones, the PSG square).
            int patch = NesEmulator.Mix.OpllPatchBank.Clamp(Get(P.Instrument));
            if (sf.Patch != patch) sf.Patch = patch;
        }
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
    /// <summary>The biggest Sega snapshot section a project may carry: the largest real one (12 DAC sounds of 64 KB at most 4 in all, the chip registers) is under 270,000 bytes.</summary>
    private const int MaxSegaSection = 300_000;

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
            var segaSnap = a.Emulator is { Runaway: true } segaRunning ? segaRunning.SegaState : null;
            if (segaSnap != null)
            {
                uint segaMagic = segaSnap.Tag;
                var segaBytes = segaSnap.ToBytes();
                int segaLen = segaBytes.Length;
                stream.Write(&segaMagic, 4);
                stream.Write(&segaLen, 4);
                fixed (byte* p = segaBytes) stream.Write(p, (uint)segaLen);
            }
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
            // The whole machine of the game: a reopened project continues from where the game was, and every change made to the game stays (see GameState.cs).
            if (a.Emulator?.CaptureGameSection() is { } gameSection)
            {
                uint gameMagic = GameStateSection.Tag;
                int gameLen = gameSection.Length;
                stream.Write(&gameMagic, 4);
                stream.Write(&gameLen, 4);
                fixed (byte* p = gameSection) stream.Write(p, (uint)gameLen);
            }
            // A corruption started from a stockpile, last: where it came from, and (for a console with no saved state) the replay that rebuilds the game (see Corruption.cs).
            if (a.Emulator?.CaptureCorruptionSection() is { } corruptionSection)
            {
                uint crpMagic = CorruptionSection.Tag;
                int crpLen = corruptionSection.Length;
                stream.Write(&crpMagic, 4);
                stream.Write(&crpLen, 4);
                fixed (byte* p = corruptionSection) stream.Write(p, (uint)crpLen);
            }
            return;
        }

        string? romPath = null;
        int hintEmu = 0, hintCh = -1;
        SpcSnapshot? restoredSnapshot = null;
        GbSnapshot? restoredGb = null;
        SegaSnapshot? restoredSega = null;
        GameStateSection? restoredGame = null;
        string? gameProblem = null;
        CorruptionSection? restoredCorruption = null;
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
                    // The sections after the place record: tag, length, bytes. The ones this build knows are read and checked, never trusted; the others (a later build's) are skipped by their length;
                    // a stream that ends, or whose length cannot be right, ends the reading (what was read before it stands).
                    for (int sections = 0; sections < 8; sections++)
                    {
                        uint tag = 0; int slen = 0;
                        if (!stream.Read(&tag, 4) || !stream.Read(&slen, 4)) break;
                        if (tag == SpcMagic && slen == SpcSnapshot.AramSize + SpcSnapshot.DspSize)
                        {
                            var snapBuf = new byte[slen];
                            fixed (byte* p = snapBuf) if (!stream.Read(p, (uint)slen)) break; else restoredSnapshot = SpcSnapshot.FromBytes(snapBuf);
                        }
                        else if (tag == GbMagic && slen == GbSnapshot.RegCount + GbSnapshot.WaveSize + 1)
                        {
                            var gbBuf = new byte[slen];
                            fixed (byte* p = gbBuf) if (!stream.Read(p, (uint)slen)) break; else restoredGb = GbSnapshot.FromBytes(gbBuf);
                        }
                        else if ((tag == SegaSnapshot.TagPsg || tag == SegaSnapshot.TagOpll || tag == SegaSnapshot.TagOpn) && slen is > 0 and <= MaxSegaSection)
                        {
                            // read and checked, never trusted: a stream that is not a snapshot of the kind its tag says is dropped
                            var segaBuf = new byte[slen];
                            fixed (byte* p = segaBuf) if (!stream.Read(p, (uint)slen)) break; else restoredSega = SegaSnapshot.FromBytes(segaBuf);
                            if (restoredSega != null && restoredSega.Tag != tag) restoredSega = null;
                        }
                        else if (tag == GameStateSection.Tag)
                        {
                            if (slen < GameStateSection.HeaderSize || slen > GameStateSection.MaxRead) { gameProblem = "damaged"; break; }
                            var gameBuf = new byte[slen];
                            fixed (byte* p = gameBuf) if (!stream.Read(p, (uint)slen)) { gameProblem = "cut short"; break; }
                            restoredGame = GameStateSection.Read(gameBuf, out gameProblem);
                        }
                        else if (tag == CorruptionSection.Tag)
                        {
                            if (slen < 16 || slen > CorruptionSection.MaxRead) { restoredCorruption = CorruptionSection.Damaged("damaged"); break; }
                            var crpBuf = new byte[slen];
                            fixed (byte* p = crpBuf) if (!stream.Read(p, (uint)slen)) { restoredCorruption = CorruptionSection.Damaged("cut short"); break; }
                            restoredCorruption = CorruptionSection.Read(crpBuf, out _);          // damaged: the editor says so (the game-state section, if any, still brings the machine back)
                        }
                        else if (slen is >= 0 and <= GameStateSection.MaxRead && SkipSection(stream, slen)) { }
                        else break;                                          // a section nobody here knows, and cannot be skipped
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
        if (emu2 != null && emu2.MemberCount == 1) emu2.Adopt(wMode, wChip, romPath, restoredSnapshot, restoredGb, restoredSega, restoredGame, gameProblem, restoredCorruption);
    }

    /// <summary>Reads past a section this build does not know; false when the stream ends first.</summary>
    private static bool SkipSection(ComStream stream, int length)
    {
        var scratch = new byte[Math.Min(length, 65536)];
        fixed (byte* p = scratch)
            while (length > 0)
            {
                int take = Math.Min(length, scratch.Length);
                if (!stream.Read(p, (uint)take)) return false;
                length -= take;
            }
        return true;
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
            case 22: return emu?.Activity((int)index % Ch.Total) ?? 0;
            case 24: return emu?.AutoCreated == true ? 1 : 0;
            case 25: return emu?.Owner((int)index % Ch.Total) == this ? 1 : 0;       // whether this instance sits on channel index (any of the Ch ids)
            case 26: return emu?.StemAvailable((int)index % Ch.Total) == true ? 1 : 0;   // whether the running game's sound unit has a stem for channel index
            case 43: emu?.Reset(); return 0;
            case 44: emu?.SetPad(this, (int)value); return 0;
            case 45: return emu?.Runaway == true ? 1 : 0;
            case 46: emu?.ReplaceSnapshotForTests(SpcSnapshot.TestSample()); return 0;
            case 47: emu?.SetRunaway(value != 0); return 0;
            case 48: return emu?.SamplerMode == true ? 1 : 0;
            case 49: return emu?.PadBits ?? 0;
            case 50: return emu?.GbSamplerMode == true ? 1 : 0;
            case 51: emu?.PokeSnesToneForTests(); return 0;
            // the Master System chip's PSG as this instance's chip has it: 52 tone period of voice index (0-2), 53 volume register of voice index (0-3, 15 = off),
            // 54 notes silenced for being below the PSG's range, 55 noise control register (-1 when this instance's chip is not the Master System one)
            case 52: return chip?.Apu is NesEmulator.APU_SMS s52 && (uint)index < 3 ? s52.Psg.Period((int)index) : -1;
            case 53: return chip?.Apu is NesEmulator.APU_SMS s53 && (uint)index < 4 ? s53.Psg.Volume((int)index) : -1;
            case 54: return chip?.Apu is NesEmulator.APU_SMS s54 ? (nint)s54.LowNotesDropped : -1;
            case 55: return chip?.Apu is NesEmulator.APU_SMS s55 ? s55.Psg.NoiseControl : -1;
            // the Genesis chip as this instance's chip has it (-1 when this instance's chip is not the Genesis one): 56 patch, 57 FM voice index (0 pulse 1, 1 pulse 2, 2 triangle) keyed (0/1),
            // 58 that voice's frequency in hundredths of a hertz, 59 its carrier attenuation (total-level steps added to the patch), 60 notes silenced for being above the FM range, 61 PSG noise control register, 62 PSG volume register of voice index (0-3, 15 = off)
            case 56: return chip?.Apu is NesEmulator.APU_MD m56 ? m56.Patch : -1;
            case 57: return chip?.Apu is NesEmulator.APU_MD m57 && (uint)index < 3 ? (m57.FmKeyed((int)index) ? 1 : 0) : -1;
            case 58: return chip?.Apu is NesEmulator.APU_MD m58 && (uint)index < 3 ? (nint)Math.Round(m58.FmHz((int)index) * 100) : -1;
            case 59: return chip?.Apu is NesEmulator.APU_MD m59 && (uint)index < 3 ? m59.FmAttenuation((int)index) : -1;
            case 60: return chip?.Apu is NesEmulator.APU_MD m60 ? (nint)m60.NotesAboveFmRange : -1;
            case 61: return chip?.Apu is NesEmulator.APU_MD m61 ? m61.Psg.NoiseControl : -1;
            case 62: return chip?.Apu is NesEmulator.APU_MD m62 && (uint)index < 4 ? m62.Psg.Volume((int)index) : -1;
            // the Master System FM chip as this instance's chip has it (-1 when this instance's chip is not that one): 63 patch, 64 FM channel index (0 pulse 1, 1 pulse 2, 2 triangle) keyed (0/1),
            // 65 that channel's frequency in hundredths of a hertz, 66 its volume register (0 loudest, 15 quietest), 67 notes silenced for being above the FM range, 68 its instrument number (0 user, 1-15 ROM),
            // 69 PSG volume register of voice index (0-3, 15 = off), 70 syncs at which a note was quieter than the NES channel's
            case 63: return chip?.Apu is NesEmulator.APU_SMSFM f63 ? f63.Patch : -1;
            case 64: return chip?.Apu is NesEmulator.APU_SMSFM f64 && (uint)index < 3 ? (f64.FmKeyed((int)index) ? 1 : 0) : -1;
            case 65: return chip?.Apu is NesEmulator.APU_SMSFM f65 && (uint)index < 3 ? (nint)Math.Round(f65.FmHz((int)index) * 100) : -1;
            case 66: return chip?.Apu is NesEmulator.APU_SMSFM f66 && (uint)index < 3 ? f66.FmAttenuation((int)index) : -1;
            case 67: return chip?.Apu is NesEmulator.APU_SMSFM f67 ? (nint)f67.NotesAboveFmRange : -1;
            case 68: return chip?.Apu is NesEmulator.APU_SMSFM f68 && (uint)index < 3 ? f68.FmInstrument((int)index) : -1;
            case 69: return chip?.Apu is NesEmulator.APU_SMSFM f69 && (uint)index < 4 ? f69.Psg.Volume((int)index) : -1;
            case 70: return chip?.Apu is NesEmulator.APU_SMSFM f70 ? (nint)f70.TooQuietSyncs : -1;
            // Instrument Runaway on a Sega game: 71 the kind of the captured snapshot (0 none, 1 PSG, 2 YM2413, 3 YM2612), 72 how many instruments it offers, 73 notes this instance's sampler could not play (-1 no sampler),
            // 74 DAC sounds found, 75 DAC sound index: its pitch in hundredths of a hertz (0 for a one-shot), 76 DAC sound index: loop length (0 none), 77 voices of this instance that hold a slot, 78 the instrument the Instrument parameter plays,
            // 79 the snapshot's bytes as saved (a length; value 1 puts a copy of the snapshot back through its own bytes), 80 whether the instance can play (sampler mode and not out of slots)
            case 71: return emu?.SegaState?.Kind ?? 0;
            case 72: return emu?.SegaState?.InstrumentCount ?? 0;
            case 73: return segaSampler?.NotesDropped is { } dropped ? (nint)dropped : -1;
            case 74: return emu?.SegaState?.Dac.Length ?? 0;
            case 75: { var d = emu?.SegaState?.Dac; return d != null && (uint)index < (uint)d.Length ? (nint)Math.Round(d[(int)index].CycleHz * 100) : -1; }
            case 76: { var d = emu?.SegaState?.Dac; return d != null && (uint)index < (uint)d.Length ? d[(int)index].LoopLength : -1; }
            case 77: return voices.Count(v => v.Slot >= 0);
            case 78: return emu?.SegaState?.Resolve(Get(P.Instrument)) ?? -1;
            case 79:
            {
                var snap = emu?.SegaState;
                if (snap == null) return -1;
                var bytes = snap.ToBytes();
                if (value != 0) { var again = SegaSnapshot.FromBytes(bytes); if (again == null || again.Tag != snap.Tag) return -2; emu!.ReplaceSegaForTests(again); }
                return bytes.Length;
            }
            case 80: return segaSampler != null && assignment.Channel == Ch.Sampler ? 1 : 0;
            // the game's state in a project: 81 byte at work-RAM offset index (-1 outside), 82 write value at offset index (1 done, 0 outside), 83 run exactly value frames (returns the frame count; a frozen game does not run),
            // 84 hash of the whole machine as its state is written (0 none), 85 the state note: copies up to index chars into the buffer at value and returns its length (-1 no note),
            // 86 how the project restore of the game's state went (0 none in the project, 1 put back, 2 refused or failed), 87 sets the biggest state section a save may write (value bytes; 0 = the default),
            // 88 the size in bytes of the state section this instance would save now (-1 none)
            case 81: return emu?.PeekWorkRam((int)index) ?? -1;
            case 82: return emu?.PokeWorkRam((int)index, (byte)value) == true ? 1 : 0;
            case 83: return (nint)(emu?.RunFramesForTests((int)value) ?? -1);
            case 84: return (nint)(emu?.GameStateHashForTests() ?? 0);
            case 85:
            {
                var note = emu?.StateNote;
                if (note == null) return -1;
                if (value != 0 && index > 0) note.AsSpan(0, Math.Min(note.Length, (int)index)).CopyTo(new Span<char>((void*)value, (int)index));
                return note.Length;
            }
            case 86: return emu?.StateRestoreResult ?? 0;
            case 87: GameStateSection.MaxStored = value > 0 ? (int)value : GameStateSection.DefaultMaxStored; return 0;
            case 88: return emu?.CaptureGameSection()?.Length ?? -1;
            case 89: return emu?.GameStateSupportedForTests() ?? -1;       // 89 whether the running game can keep its state in a project (1/0, -1 no game)
            // corruption (see Corruption.cs): 100 open the stockpile file at the UTF-16 path <value> (read on a worker), 101 busy flags (1 reading, 2 starting), 102 the list's length (-1: no stockpile open),
            // 103 flags of row <index>: bit 0 can start, bits 8-15 the Fit (0 fits, 1 other game, 2 other console, 3 no game, 4 cannot here), 104 select row <value>, 105 freeze at once (value 0/1),
            // 106 start the selected row (0 begun, 1 refused: the reason is in the result), 107 cancel, 108 remove (restart without the writes), 109 copy text <index> chars into the buffer <value>: field in the high bits of
            // index (0 result, 1 status, 2 why of the selected row, 3 the selected row's name, 4 the stockpile problems), the max chars in the low 16, returns the length, 110 the result's good flag, 111 the result's stamp,
            // 112 whether a corruption is recorded as started on this game, 113 frames the farm has recorded (-1 no farm), 114 the farm's reason why a replay is not possible (1 poisoned, 0 not), 115 the SNES audio RAM hash,
            // 116 the work RAM hash (WRAM, else RAM), 117 hash of the Runaway snapshot's audio RAM, 118 the ARAM byte at <index>, 119 write ARAM byte <index> = <value> straight into the running game (a corruption made outside the farm)
            case 120: Emulator.NoPumpForTests = value != 0; return 0;      // 120 tests only: the editor stops running the game to keep the picture moving (value 1), or goes back to it (0)
            case 100: { var path = System.Runtime.InteropServices.Marshal.PtrToStringUni(value); if (emu == null || path == null) return 1; emu.OpenStockpile(path); return 0; }
            case 101: return emu == null ? 0 : (emu.Corruption.Reading ? 1 : 0) | (emu.Corruption.Busy ? 2 : 0);
            case 102: return emu?.Corruption.View?.File?.Entries.Count ?? -1;
            case 103: { var rows = emu?.CorruptionRows(); if (rows == null || (uint)index >= (uint)rows.Count) return -1; return (rows[(int)index].CanStart ? 1 : 0) | ((int)rows[(int)index].Fit << 8); }
            case 104: if (emu != null) emu.Corruption.Selected = (int)value; return 0;
            case 105: if (emu != null) emu.Corruption.Freeze = value != 0; return 0;
            case 106: return emu == null ? 1 : emu.StartCorruption(emu.Corruption.Selected) == null ? 0 : 1;
            case 107: emu?.CancelCorruption(); return 0;
            case 108: return emu == null ? 1 : emu.RemoveCorruption(emu.Corruption.Selected) == null ? 0 : 1;
            case 109:
            {
                if (emu == null) return -1;
                int field = (int)(index >> 16), max = (int)(index & 0xFFFF);
                var rows = emu.CorruptionRows();
                int sel = emu.Corruption.Selected;
                string? text = field switch
                {
                    0 => emu.Corruption.Result,
                    1 => emu.Corruption.Status,
                    2 => (uint)sel < (uint)rows.Count ? rows[sel].Why : "",
                    3 => (uint)sel < (uint)rows.Count ? rows[sel].Entry.Name : "",
                    4 => emu.Corruption.View is { } v ? string.Join(" | ", v.Problems) : "",
                    _ => null,
                };
                if (text == null) return -1;
                if (value != 0 && max > 0) text.AsSpan(0, Math.Min(text.Length, max)).CopyTo(new Span<char>((void*)value, max));
                return text.Length;
            }
            case 110: return emu?.Corruption.ResultGood == true ? 1 : 0;
            case 111: return emu?.Corruption.ResultStamp ?? 0;
            case 112: return emu?.Corruption.Applied != null ? 1 : 0;
            case 113: return (nint)(emu?.CorruptionFarmFrames() ?? -1);
            case 114: return emu?.CorruptionFarmPoisoned() == true ? 1 : 0;
            case 115: return (nint)(emu?.DomainHashForTests("ARAM") ?? 0);
            case 116: { long h = emu?.DomainHashForTests("WRAM") ?? 0; return (nint)(h != 0 ? h : emu?.DomainHashForTests("RAM") ?? 0); }
            case 117: return (nint)(emu?.SnapshotHashForTests() ?? 0);
            case 118: return emu?.PeekDomainForTests("ARAM", (int)index) ?? -1;
            case 119: return emu?.PokeDomainForTests("ARAM", (int)index, (byte)value) == true ? 1 : 0;
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
