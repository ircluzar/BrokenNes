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
using BrokenNes2.Presets;
using NesEmulator.Plugin;

namespace BrokenNes2;

public sealed unsafe partial class Bn2Plugin : FruityPluginBase
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
        public SpcRecipe? Recipe;     // the recipe it was started on (SNES Runaway), null for a plain instrument
        public bool Silenced;         // it lost its slot to a newer note: silent until released
        public float PitchMin, PitchMax;   // how far FL moved its pitch while it sounded (the host log: did FL keep the MIDI file's bends?)
        public int Note;              // its number among the instance's notes
        public int TrackKey = -1;     // a track channel playing the recording's expression: the track's key for this note (-1: FL's own pitch and volume)
        public float TrackVel;        // FL's velocity at its key-on (0..1)
        public float CapPitch;        // round 9 (capture): the pitch last written into the capture's lane (cents from C5)
        public bool CapOff = true;    // its release is in the lane (or it has no note-on there)
        public bool LiveSent;         // its note-on went out as MIDI (Bn2Plugin.CaptureLive.cs)
        public int LiveKey, LiveCh, LiveBase;
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
        for (int i = 0; i < 32; i++) pool.Push(new Voice());     // FL's voices are taken from here on the audio thread: made now, so a 9th or 20th note at once allocates nothing there (round 8 found 72 bytes per new peak of voices)
        HostLog.Text(InstanceNo,$"created (host tag 0x{hostTag:X}, FL's host version {HostVersion}); {(TrackOffers.Current is { } o ? $"{o.Slots.Length} tracks on offer" : "no tracks on offer")}");
        EmulatorHub.Resolve(this);
        TrackOffers.Register(this);   // the tracks of a recording on offer: this instance recognises its track from the notes FL asks it to play (Bn2Plugin.Tracks.cs)
        // relink: made while tracks are on offer (a new channel, or the one FL makes for a Replace): it joins them at once, and the Record window counts it (Bn2Plugin.Relink.cs)
        if (TrackOffers.Current != null && !Record.RecMutants.On(Record.RecMutants.NoAutoJoin)) Join("made while tracks are on offer (a new channel, or one FL made for a Replace)");
        CaptureHub.Register(this);    // round 9: "Capture from FL" reaches every instance of the process (CaptureHub.cs, Bn2Plugin.Capture.cs)
    }

    /// <summary>What FL shows. (The DLL and its folder have the same name: FL finds a native plugin by its file name.)</summary>
    public const string DisplayName = "BrokenNes2";
    public static FruityPluginInfo Info { get; } = new(DisplayName, DisplayName, Fpf.TypeFullGen | Fpf.WantNewTick | Fpf.MidiOut);     // (MIDI out: the Record window's live MIDI out, round 8)

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
    internal void RequestReset()
    {
        PrepareAgainForPreset();                 // a preset's engine is made again here, on the UI thread, before the audio thread drops the old one (Bn2Plugin.Presets.cs)
        Interlocked.Exchange(ref resetRequested, 1);
    }

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
        foreach (var v in voices) { v.Dsp = -1; v.Slot = -1; v.Instr = -1; v.Silenced = false; v.Recipe = null; }
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
        if (id < Fpd.TestBase) CaptureDispatch(id, index, value);     // round 9: FL's transport and timing calls for "Capture from FL" (Bn2Plugin.Capture.cs; nothing allocated)
        if (id == Fpd.SetSampleRate && value > 0)
        {
            EmulatorHub.SetHostRate((int)value);
            return 0;
        }
        // FL's transport started or stopped: the Test MIDI export's song follows it when asked to (Emulator.Song.cs; flags only, nothing allocated, whatever thread FL calls from)
        if (id < Fpd.TestBase && dispatchLogged < HostLog.PerInstance && HostLog.On) { dispatchLogged++; HostLog.Add(HostLog.KindDispatcher, InstanceNo, id, index, value); }
        if (id == Fpd.SetPlaying) { assignment.Emulator?.TransportChanged(value != 0); return 0; }
        // FL opened the instance, a project was loaded, or FL says the channel's context (its name) changed: ask FL for the channel's name (it can name the track at once: Bn2Plugin.Relink.cs)
        if (id == Fpd.PluginLoaded) PollChannelName("FL opened the instance (FPD_PluginLoaded)", fromEvent: true);
        else if (id == Fpd.ContextInfoChanged) PollChannelName($"FPD_ContextInfoChanged {index}", fromEvent: true);
        else if (id == Fpd.RoutingChanged && (value & 1) != 0) PollChannelName("FPD_RoutingChanged: names changed", fromEvent: true);
        else if (id == Fpd.ProjLoaded) PollChannelName("FPD_ProjLoaded", fromEvent: true);
        return base.Dispatcher(id, index, value);
    }

    private int dispatchLogged;

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
        v.PitchMin = v.PitchMax = p->InitLevels.Pitch;
        v.Note = notesSeen + 1;
        v.TrackKey = -1;
        v.CapOff = true; v.LiveSent = false;
        voices.Add(v);
        if (CaptureWanted) CaptureNoteOn(v);                           // round 9: the note into the capture's lane / out as MIDI (Bn2Plugin.Capture.cs)
        if (link != LinkNone || bindState == 1 || HostLog.On) MatchNote(v);       // a linked instance recognises its track from this note, before it sounds (Bn2Plugin.Tracks.cs)
        if (configured && expression != null) ExpressionNoteOn(v);   // a channel that plays a track: the track's key and expression for this note
        return v.Handle;
    }

    public override void ReleaseVoice(nint handle)
    {
        var v = Find(handle);
        if (v != null) { v.Released = true; if (CaptureWanted) CaptureNoteOff(v); }
    }

    public override void KillVoice(nint handle)
    {
        var v = Find(handle);
        if (v == null) return;
        if (CaptureWanted) CaptureNoteOff(v);
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
        ownSamples += length;
        renderLength = length;
        if (CaptureWanted) CaptureBlock();                       // round 9: the pitch FL wants each voice to play in this block, into the capture's lane (Bn2Plugin.Capture.cs)
        RenderCore(dest, length);
        if (audition != null) MixAudition(dest, length);        // a preset auditioned from the Presets tab sounds over whatever this instance plays (Bn2Plugin.Presets.cs)
        capLane?.Advance(length, EmulatorHub.HostRate);          // (the capture's clock: the samples FL rendered since its Play)
    }

    private void RenderCore(float* dest, int length)
    {
        renderCalls++;
        if (Interlocked.Exchange(ref resetRequested, 0) != 0) ApplyReset();
        var a = assignment;
        var emu = a.Emulator;
        float pan = Get(P.Pan) / 100f;
        float gl = Math.Min(1f, 1f - pan), gr = Math.Min(1f, 1f + pan);

        if (bindState == 1) { RenderBound(dest, length, gl, gr); return; }     // a track recognised from its notes, not settled yet: its preset, wherever the instance sits (Bn2Plugin.Tracks.cs)
        if (emu == null || a.Channel < 0)
        {
            FinishReleased();
            new Span<float>(dest, length * 2).Clear();
            return;
        }

        if (emu.Mode != 1 || emu.SamplerMode) emu.DrainLiveMidi(this);      // (the live MIDI out's last note-offs when the game is frozen or gone; in ROM mode they go after the frames below)
        if (emu.PresetMode)
        {
            RenderPreset(dest, length, gl, gr);             // the Presets chip: this instance plays its preset on an engine of its own (Bn2Plugin.Presets.cs)
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
            emu.DrainLiveMidi(this);                        // the live MIDI out's notes of the frames just made go to FL with this block (Emulator.Live.cs; nothing when it is off)
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

    private void RenderSampler(Emulator emu, float* dest, int length, float gl, float gr) => RenderSnes(emu.Snapshot, -1, dest, length, gl, gr);

    /// <summary>Plays a SNES snapshot's samples: the game's frozen sound memory (Instrument Runaway, instrument from the Instrument parameter) or a preset's (<paramref name="instrument"/> is its sample slot).</summary>
    private void RenderSnes(SpcSnapshot? snap, int instrument, float* dest, int length, float gl, float gr)
    {
        DropChip();
        int rate = EmulatorHub.HostRate;
        if (snap == null) { FinishReleased(); new Span<float>(dest, length * 2).Clear(); return; }
        if (sampler == null || !ReferenceEquals(samplerFor, snap) || samplerRate != rate)
        {
            DropSampler();
            sampler = TakePrepared<SnesSampler>(snap, rate) ?? new SnesSampler(snap, rate);       // (an instrument edit prepares it on the UI thread: see Bn2Plugin.Instruments.cs)
            samplerFor = snap;
            samplerRate = rate;
        }
        var s = sampler;
        replayProbe?.Bind(s);                                   // (the self-test's replay probe, round 8: null otherwise)
        float vol = Get(P.Volume) / 1000f;
        // a channel playing a track's expression: where in the track this block starts (one question to FL per block)
        if (expression != null && exprOn) { exprBlockBeat = TrackBeatNow(); exprBeatsPerSample = EmulatorHub.FlTempo / 60.0 / Math.Max(1, EmulatorHub.HostRate); }
        else exprBlockBeat = double.NaN;
        for (int off = 0; off < length; off += Chunk)
        {
            int n = Math.Min(Chunk, length - off);
            UpdateSamplerVoices(s, instrument, off);
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
    private void UpdateSamplerVoices(SnesSampler s, int forcedInstrument = -1, int offset = 0)
    {
        var ex = expression;
        double exBeat = ex != null && exprOn ? exprBlockBeat + offset * exprBeatsPerSample : double.NaN;
        int param = forcedInstrument >= 0 ? forcedInstrument : Get(P.Instrument);       // (a preset plays its own sample; Runaway plays the one the Instrument parameter names)
        int instrument = samplerFor?.Resolve(param) ?? param;
        bool hasSample = samplerFor?.HasSample(instrument) ?? false;   // an empty slot of the game's sample table plays nothing (the DSP would wander through other data)
        // a recipe (Recipe parameter 1 and up) plays what the game's driver did with an instrument; a recipe plays whatever sample the game keyed, so the sample table's check does not apply to it
        SpcRecipe? recipe = null;
        int rp = forcedInstrument >= 0 && PresetMutants.On(PresetMutants.VoiceIgnored) ? 0 : Get(P.Recipe);   // (a preset plays its own sample; the game voices it carries are in its snapshot, so the same parameter picks one)
        var recipes = samplerFor?.Recipes;
        if (rp > 0 && recipes != null && rp <= recipes.Length) recipe = recipes[rp - 1];
        foreach (var v in voices)
        {
            if (v.Finished) continue;
            if (v.Released)
            {
                if (v.Dsp >= 0) { s.NoteOff(v.Dsp); dspUsed[v.Dsp] = false; v.Dsp = -1; v.Recipe = null; }
                if (HostLog.On && v.Note <= 32) HostLog.Add(HostLog.KindNoteEnd, InstanceNo, v.Note, 0, 0, v.PitchMin, v.PitchMax);
                v.Finished = true;
                continue;
            }
            ref var fl = ref v.Params->FinalLevels;
            if (fl.Pitch < v.PitchMin) v.PitchMin = fl.Pitch;
            if (fl.Pitch > v.PitchMax) v.PitchMax = fl.Pitch;
            double cents = fl.Pitch + Get(P.Coarse) * 100 + Get(P.Fine);
            int level = Math.Clamp((int)Math.Round(127 * fl.Vol), 0, 127);
            if (v.TrackKey >= 0 && !double.IsNaN(exBeat))
            {
                // the recording's own pitch bend and controller 7 at this point of the track (FL's import does not bring them as the file has them): exact as the transcription made them
                cents = ex!.PitchCents(v.TrackKey, exBeat) + Get(P.Coarse) * 100 + Get(P.Fine);
                level = Math.Clamp((int)Math.Round(127 * (float)(v.TrackVel * ex.CcAt(exBeat) / 100.0)), 0, 127);
            }
            if (v.Dsp < 0)
            {
                if (recipe == null && !hasSample) continue;
                int dv = Array.IndexOf(dspUsed, false);
                if (dv < 0)
                {
                    foreach (var other in voices) if (other != v && other.Dsp >= 0) { dv = other.Dsp; other.Dsp = -1; other.Recipe = null; break; }   // the oldest note gives way
                    if (dv < 0) continue;
                }
                v.Dsp = dv;
                dspUsed[dv] = true;
                v.Recipe = recipe;
                replayProbe?.Mark(v.Handle, dv);
                if (recipe != null) s.NoteOnRecipe(dv, recipe, SnesSampler.PitchFor(cents, recipe.Pitch), level);
                else s.NoteOnCents(dv, instrument, cents, level, level);           // (an edited or preset sample's tuning is added to the cents before the one rounding: every register can be reached, round 8)
            }
            else if (v.Recipe != null) s.Update(v.Dsp, SnesSampler.PitchFor(cents, v.Recipe.Pitch), level, level);
            else s.UpdateCents(v.Dsp, instrument, cents, level, level);
        }
    }

    private void RenderSegaSampler(Emulator emu, float* dest, int length, float gl, float gr) => RenderSega(emu.SegaState, -1, dest, length, gl, gr);

    /// <summary>Plays a Sega snapshot's chips: the game's (Instrument Runaway) or a preset's (<paramref name="instrument"/> is the Instrument parameter's value for it: the picker number plus one).</summary>
    private void RenderSega(SegaSnapshot? snap, int instrument, float* dest, int length, float gl, float gr)
    {
        DropChip();
        int rate = EmulatorHub.HostRate;
        if (snap == null) { FinishReleased(); new Span<float>(dest, length * 2).Clear(); return; }
        if (segaSampler == null || !ReferenceEquals(segaFor, snap) || segaRate != rate)
        {
            DropSampler();
            segaSampler = TakePrepared<SegaSampler>(snap, rate) ?? (snap.Kind switch
            {
                SegaSnapshot.KindOpn => new OpnSampler(snap, rate),
                SegaSnapshot.KindOpll => new OpllSampler(snap, rate),
                _ => (SegaSampler)new PsgSampler(snap, rate),
            });
            segaFor = snap;
            segaRate = rate;
        }
        var s = segaSampler;
        float vol = Get(P.Volume) / 1000f;
        for (int off = 0; off < length; off += Chunk)
        {
            int n = Math.Min(Chunk, length - off);
            UpdateSegaVoices(s, snap, instrument);
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
    private void UpdateSegaVoices(SegaSampler s, SegaSnapshot snap, int forcedInstrument = -1)
    {
        // a game voice (the Recipe parameter, 1 and up) plays what the game's driver did with a channel; the slots it may take are its chip's. (A preset plays its own instrument: no game voice.)
        GenRecipe? recipe = null;
        int rp = forcedInstrument >= 0 ? 0 : Get(P.Recipe);
        if (rp > 0 && rp <= snap.Recipes.Length && snap.Kind == SegaSnapshot.KindOpn) recipe = snap.Recipes[rp - 1];
        int instrument = recipe != null ? RecipeInstrument + rp : snap.Resolve(forcedInstrument >= 0 ? forcedInstrument : Get(P.Instrument));
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
            int first, count, slot = -1;
            if (recipe == null) { first = snap.SlotBase(instrument); count = snap.Slots(instrument); }
            else if (recipe.IsFm) { first = 0; count = OpnSampler.FmSlots; }
            else { bool noise = recipe.Kind == GenRecipe.KindPsgNoise; first = snap.SlotBase(snap.PsgInstrument + (noise ? 1 : 0)); count = noise ? 1 : 3; }
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
            if (recipe != null) s.NoteOnRecipe(slot, recipe, hz, velocity); else s.NoteOn(slot, instrument, hz, velocity);
        }
    }

    /// <summary>The Game Boy channel (0 pulse 1, 1 pulse 2, 2 wave, 3 noise) the Channel parameter picks in Game Boy Runaway: Auto and Mix are pulse 1.</summary>
    internal static int GbChannelOf(int channelParam) => channelParam is >= 1 and <= 4 ? channelParam - 1 : 0;

    private void RenderGbSampler(Emulator emu, float* dest, int length, float gl, float gr) => RenderGb(emu.GbState, null, dest, length, gl, gr);

    /// <summary>Plays a Game Boy snapshot's channel: the game's (Instrument Runaway: the Channel parameter picks it) or a preset's (<paramref name="preset"/> names its channel and noise width and keeps its own duty).</summary>
    private void RenderGb(GbSnapshot? snap, PresetPlayable? preset, float* dest, int length, float gl, float gr)
    {
        DropChip();
        int rate = EmulatorHub.HostRate;
        if (snap == null) { FinishReleased(); new Span<float>(dest, length * 2).Clear(); return; }
        if (gbSampler == null || !ReferenceEquals(gbFor, snap) || gbRate != rate)
        {
            DropSampler();
            gbSampler = TakePrepared<GbSampler>(snap, rate) ?? new GbSampler(snap, rate);
            gbFor = snap;
            gbRate = rate;
        }
        var s = gbSampler;
        float vol = Get(P.Volume) / 1000f;
        for (int off = 0; off < length; off += Chunk)
        {
            int n = Math.Min(Chunk, length - off);
            UpdateGbVoice(s, preset);
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
    private void UpdateGbVoice(GbSampler s, PresetPlayable? preset = null)
    {
        int ch = preset != null ? preset.GbChannel : GbChannelOf(Get(P.Channel));
        var owner = ResolveOwner();
        if (gbGate && (owner == null || gbChannel != ch)) { s.NoteOff(gbChannel); gbGate = false; }
        if (owner == null) return;
        ref var fl = ref owner.Params->FinalLevels;
        double semis = 60.0 + (fl.Pitch + Get(P.Coarse) * 100 + Get(P.Fine)) / 100.0;
        double hz = 440.0 * Math.Pow(2.0, (semis - 69.0) / 12.0);
        bool shortNoise = preset != null ? preset.GbNoiseShort : Get(P.NoiseMode) == 1;
        if (!gbGate || owner.Fresh)
        {
            int level = Math.Clamp((int)Math.Round(15 * fl.Vol), 0, 15);
            int duty = preset != null ? -1 : Get(P.Instrument) is >= 1 and <= 4 ? Get(P.Instrument) - 1 : -1;   // 0 = the game's own duty (a preset always plays its own)
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
        double master = Get(P.Volume) / 1000.0;
        int level = Record.NesDirect.Level(fl.Vol, master);      // (the arithmetic lives in Record/NesDirect.cs since round 8: a NES recording searches its bends and volumes with these very functions)
        bool retrigger = v.Fresh || !gate;
        v.Fresh = false;

        // the registers (Record/NesDirect.cs since round 8: a NES preset recorded from a game writes them with this very code)
        curPeriod = Record.NesDirect.Write(c, ch, Get(P.Duty), Get(P.NoiseMode), ref level, cents, retrigger, curPeriod);
        gate = true;
        curLevel = level;
    }

    private void Silence(NesApuInstrument c, int ch) => Record.NesDirect.Silence(c, ch, Get(P.Duty));

    // ---- saved state: the parameters (the emulator-level ones as the emulator has them), the game's path, and where it sat ----

    private const uint PlaceMagic = 0x32554D45; // "EMU2"
    private const uint SpcMagic = 0x31435053;   // "SPC1"
    private const uint GbMagic = 0x31534247;    // "GBS1"
    /// <summary>The value a Sega voice's Instr takes while it plays a game voice (recipe n is this + n): above every instrument number a snapshot offers.</summary>
    private const int RecipeInstrument = 1000;
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
                // what the Genesis game's driver was heard doing with its channels (recipes), right after the chips they belong to
                if (segaSnap.Recipes.Length > 0)
                {
                    uint gnrMagic = GenRecipe.Tag;
                    var gnrBytes = GenRecipe.Write(segaSnap.Recipes);
                    int gnrLen = gnrBytes.Length;
                    stream.Write(&gnrMagic, 4);
                    stream.Write(&gnrLen, 4);
                    fixed (byte* p = gnrBytes) stream.Write(p, (uint)gnrLen);
                }
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
                // what the game's driver was heard doing with its instruments (recipes), right after the memory it belongs to; a build that does not know the tag skips it
                if (snap.Recipes.Length > 0)
                {
                    uint sprMagic = SpcRecipe.Tag;
                    var sprBytes = SpcRecipe.Write(snap.Recipes);
                    int sprLen = sprBytes.Length;
                    stream.Write(&sprMagic, 4);
                    stream.Write(&sprLen, 4);
                    fixed (byte* p = sprBytes) stream.Write(p, (uint)sprLen);
                }
                // the instrument edits that ride on that sound memory (Instruments/SnesOverlay.cs): their own section, after it, so a build that does not know them skips it
                if (snap.Overlay is { } overlay)
                {
                    uint spoMagic = Instruments.SnesOverlay.Tag;
                    var spoBytes = overlay.ToBytes();
                    int spoLen = spoBytes.Length;
                    stream.Write(&spoMagic, 4);
                    stream.Write(&spoLen, 4);
                    fixed (byte* p = spoBytes) stream.Write(p, (uint)spoLen);
                }
            }
            // The preset this instance plays (Direct mode, the Presets chips): its own versioned section, so the project plays when the library file is moved or gone (Presets/PresetSection.cs).
            if (PresetSectionBytes() is { } presetSection)
            {
                uint prsMagic = PresetSection.Tag;
                int prsLen = presetSection.Length;
                stream.Write(&prsMagic, 4);
                stream.Write(&prsLen, 4);
                fixed (byte* p = presetSection) stream.Write(p, (uint)prsLen);
            }
            // Which track of a recording this instance plays and how it was found (Bn2Plugin.Tracks.cs), after its preset.
            if (TrackSectionBytes() is { } trackSection)
            {
                uint trkMagic = TrackTag;
                int trkLen = trackSection.Length;
                stream.Write(&trkMagic, 4);
                stream.Write(&trkLen, 4);
                fixed (byte* p = trackSection) stream.Write(p, (uint)trkLen);
            }
            if (HostLog.On) HostLog.Text(InstanceNo, $"state saved: {Params.Length} values, ROM path {n} bytes, emulator #{emu} channel {ch}{(PresetSectionBytes() is { } ps ? $", PRS1 {ps.Length} bytes" : "")}{(TrackSectionBytes() is { } ts ? $", TRK1 {ts.Length} bytes" : "")}");
            // The Test MIDI export's song (Music/SongSection.cs): the MIDI file's bytes and path, so a reopened project rebuilds the same ROM; before the game's state, which then fits it.
            if (a.Emulator?.CaptureSongSection() is { } songSection)
            {
                uint sngMagic = Music.SongSection.Tag;
                int sngLen = songSection.Length;
                stream.Write(&sngMagic, 4);
                stream.Write(&sngLen, 4);
                fixed (byte* p = songSection) stream.Write(p, (uint)sngLen);
            }
            // A recording of the game's music (Record/RecSection.cs): the session itself, so a reopened project transcribes it again; the emulator's first instance writes it.
            if (a.Emulator?.CaptureRecordSection(this) is { } recordSection)
            {
                uint recMagic = Record.RecSection.Tag;
                int recLen = recordSection.Length;
                stream.Write(&recMagic, 4);
                stream.Write(&recLen, 4);
                fixed (byte* p = recordSection) stream.Write(p, (uint)recLen);
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
            // The instruments pushed into the running game (Instruments/GamePush.cs): the table of overrides and the original bytes, which no machine state holds. Last, so a build that does not know the section stops reading here.
            if (a.Emulator?.CaptureInjectSection() is { } injectSection)
            {
                uint injMagic = Instruments.GamePushFile.Tag;
                int injLen = injectSection.Length;
                stream.Write(&injMagic, 4);
                stream.Write(&injLen, 4);
                fixed (byte* p = injectSection) stream.Write(p, (uint)injLen);
            }
            return;
        }

        string? romPath = null;
        int hintEmu = 0, hintCh = -1;
        SpcSnapshot? restoredSnapshot = null;
        SpcRecipe[]? restoredRecipes = null;
        GenRecipe[]? restoredGenRecipes = null;
        Instruments.SnesOverlay? restoredOverlay = null;
        string? overlayProblem = null;
        GbSnapshot? restoredGb = null;
        SegaSnapshot? restoredSega = null;
        GameStateSection? restoredGame = null;
        string? gameProblem = null;
        CorruptionSection? restoredCorruption = null;
        byte[]? presetSectionBytes = null;
        string? presetSectionProblem = null;
        byte[]? injectPayload = null;
        string? injectProblem = null;
        Music.SongSection? restoredSong = null;
        string? songProblem = null;
        byte[]? restoredRecord = null;
        byte[]? trackBytes = null;
        var seen = HostLog.On ? new List<string>() : null;
        restoring = true;
        try
        {
            base.SaveRestoreState(stream, false);
            if (!RestoredCleanly)
            {
                HostLog.Text(InstanceNo, "state restored: not a BrokenNes2 state of this version (or empty): nothing taken; " + (link != LinkNone ? "it stays linked to the recording" : configured ? "the instance stays the user's own" : "nothing changes for the recording"));
                return;
            }
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
                    for (int sections = 0; sections < 12; sections++)
                    {
                        uint tag = 0; int slen = 0;
                        if (!stream.Read(&tag, 4) || !stream.Read(&slen, 4)) break;
                        seen?.Add($"{Encoding.ASCII.GetString(BitConverter.GetBytes(tag))} {slen} bytes");
                        if (tag == TrackTag && slen is >= 25 and <= 16 << 20)
                        {
                            var trkBuf = new byte[slen];
                            fixed (byte* p = trkBuf) if (!stream.Read(p, (uint)slen)) break;
                            trackBytes = trkBuf;
                        }
                        else if (tag == SpcMagic && slen == SpcSnapshot.AramSize + SpcSnapshot.DspSize)
                        {
                            var snapBuf = new byte[slen];
                            fixed (byte* p = snapBuf) if (!stream.Read(p, (uint)slen)) break; else restoredSnapshot = SpcSnapshot.FromBytes(snapBuf);
                        }
                        else if (tag == SpcRecipe.Tag && slen is >= 2 and <= SpcRecipe.MaxSection)
                        {
                            // read and checked, never trusted: a stream that is not a recipe list is dropped (the memory it belonged to still plays plain instruments)
                            var sprBuf = new byte[slen];
                            fixed (byte* p = sprBuf) if (!stream.Read(p, (uint)slen)) break; else restoredRecipes = SpcRecipe.Read(sprBuf);
                        }
                        else if (tag == GenRecipe.Tag && slen is >= 2 and <= GenRecipe.MaxSection)
                        {
                            var gnrBuf = new byte[slen];
                            fixed (byte* p = gnrBuf) if (!stream.Read(p, (uint)slen)) break; else restoredGenRecipes = GenRecipe.Read(gnrBuf);
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
                        else if (tag == Instruments.SnesOverlay.Tag && slen is >= 7 and <= Instruments.SnesOverlay.MaxBytes)
                        {
                            // the instrument edits of a SNES sound memory: read and checked, never trusted (a damaged one is dropped and the user is told, see Emulator.InstrumentNote)
                            var spoBuf = new byte[slen];
                            fixed (byte* p = spoBuf) if (!stream.Read(p, (uint)slen)) break; else restoredOverlay = Instruments.SnesOverlay.FromBytes(spoBuf, out overlayProblem);
                        }
                        else if (tag == Instruments.SnesOverlay.Tag)
                        {
                            overlayProblem = "damaged (its length)";
                            if (!(slen is >= 0 and <= GameStateSection.MaxRead && SkipSection(stream, slen))) break;
                        }
                        else if (tag == PresetSection.Tag)
                        {
                            // the instance's preset: read whole, checked later (a damaged or newer one is refused with a note on screen, never half used)
                            if (slen < PresetSection.MinBytes || slen > PresetSection.MaxBytes)
                            {
                                presetSectionProblem = "damaged (its length)";
                                if (!(slen is >= 0 and <= GameStateSection.MaxRead && SkipSection(stream, slen))) break;
                            }
                            else
                            {
                                var prsBuf = new byte[slen];
                                fixed (byte* p = prsBuf) if (!stream.Read(p, (uint)slen)) { presetSectionProblem = "cut short"; break; }
                                presetSectionBytes = prsBuf;
                            }
                        }
                        else if (tag == Music.SongSection.Tag)
                        {
                            // the Test MIDI export's song: read whole, checked (a damaged or newer one is refused with a note in the window, never half used)
                            if (slen < Music.SongSection.HeaderBytes + 4 || slen > Music.SongSection.MaxBytes)
                            {
                                songProblem = "damaged (its length)";
                                if (!(slen is >= 0 and <= GameStateSection.MaxRead && SkipSection(stream, slen))) break;
                            }
                            else
                            {
                                var sngBuf = new byte[slen];
                                fixed (byte* p = sngBuf) if (!stream.Read(p, (uint)slen)) { songProblem = "cut short"; break; }
                                restoredSong = Music.SongSection.Read(sngBuf, out songProblem);
                            }
                        }
                        else if (tag == Record.RecSection.Tag)
                        {
                            // a recording: read whole, checked and transcribed again on a worker (a damaged or newer one is refused with a note in the Record window, never half used)
                            if (slen < Record.RecSection.HeaderBytes + 6 || slen > Record.RecSection.MaxBytes)
                            {
                                if (!(slen is >= 0 and <= GameStateSection.MaxRead && SkipSection(stream, slen))) break;
                            }
                            else
                            {
                                var recBuf = new byte[slen];
                                fixed (byte* p = recBuf) if (!stream.Read(p, (uint)slen)) break;
                                restoredRecord = recBuf;
                            }
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
                            if (slen < 20 || slen > CorruptionSection.MaxRead) { restoredCorruption = CorruptionSection.Damaged("damaged"); break; }
                            var crpBuf = new byte[slen];
                            fixed (byte* p = crpBuf) if (!stream.Read(p, (uint)slen)) { restoredCorruption = CorruptionSection.Damaged("cut short"); break; }
                            restoredCorruption = CorruptionSection.Read(crpBuf, out _);          // damaged: the editor says so (the game-state section, if any, still brings the machine back)
                        }
                        else if (tag == Instruments.GamePushFile.Tag)
                        {
                            // the instruments pushed into the game: read and checked when the game is built, never trusted (a damaged or newer one is dropped and the editor says so, see Emulator.InjectNote)
                            if (slen < 11 || slen > Instruments.GamePushFile.MaxStored)
                            {
                                injectProblem = "damaged (its length)";
                                if (!(slen is >= 0 and <= GameStateSection.MaxRead && SkipSection(stream, slen))) break;
                            }
                            else
                            {
                                var injBuf = new byte[slen];
                                fixed (byte* p = injBuf) if (!stream.Read(p, (uint)slen)) { injectProblem = "cut short"; break; }
                                Instruments.GamePushFile.Read(injBuf, out _, out injectProblem);     // (the check is made here so that a bad section is refused now, with its reason)
                                if (injectProblem == null) injectPayload = injBuf;
                            }
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
        if (restoredSega != null && restoredGenRecipes != null) { restoredSega.Recipes = restoredGenRecipes; restoredSega.ListenNote = "Game voices from the project: " + restoredGenRecipes.Length; }
        if (restoredSnapshot != null && restoredRecipes != null) { restoredSnapshot.Recipes = restoredRecipes; restoredSnapshot.ListenNote = "Game voices from the project: " + restoredRecipes.Length; }
        // the recipes go on first: WithOverlay makes a copy that keeps them
        if (restoredOverlay != null && restoredSnapshot != null) restoredSnapshot = restoredSnapshot.WithOverlay(restoredOverlay);
        else if (restoredOverlay != null) overlayProblem = "the project has instrument edits but no sound memory to apply them to";
        if (emu2 != null && emu2.MemberCount == 1)
        {
            emu2.SetInjectRestore(injectPayload, injectProblem);
            emu2.Adopt(wMode, wChip, romPath, restoredSnapshot, restoredGb, restoredSega, restoredGame, gameProblem, restoredCorruption, restoredSong, songProblem);
            emu2.SetInstrumentNote(overlayProblem == null ? null : "The project's instrument edits were not restored: " + overlayProblem);
        }
        RestorePreset(presetSectionBytes, presetSectionProblem);
        if (restoredRecord != null) emu2?.AdoptRecord(restoredRecord);         // (the first instance of the project that carries the recording gives it to the emulator)
        // whether this state makes the instance clearly the user's own (it holds a game, a preset or a track: it leaves the recording and never joins it by itself), or not (FL's empty or default
        // state, a fresh instance's own state, another channel's values: a joined instance stays joined; relink, 2026-10-18)
        var restoredTrack = trackBytes == null ? (null, null, false) : ReadTrackSection(trackBytes);
        bool hadGame = restoredGame != null || gameProblem != null || restoredSnapshot != null || restoredGb != null || restoredSega != null || restoredSong != null || restoredRecord != null || restoredCorruption != null || injectPayload != null;
        bool usersOwn = RestoredUsersOwn(hadGame, presetSectionBytes != null, trackBytes != null);
        bool wasPristine = RestoredPristine();
        if (usersOwn) MarkConfigured("its project state holds " + string.Join(", ", new[] { hadGame ? "a game" : null, presetSectionBytes != null ? "a preset" : null, trackBytes != null ? "a track" : null }.Where(x => x != null).DefaultIfEmpty("its own settings")));
        else if (!wasPristine) pristine = false;
        RestoreTrack(restoredTrack);
        if (HostLog.On)
            HostLog.Text(InstanceNo, $"state restored: values [{string.Join(",", Values)}], ROM path {(romPath ?? "").Length} chars, place emulator #{hintEmu} channel {hintCh}; sections: {(seen is { Count: > 0 } ? string.Join("; ", seen) : "none")}; "
                + (usersOwn ? "the user's own instance (it does not join the recording by itself)" : (wasPristine ? "default values" : "its own values, but no game, preset or track") + (link != LinkNone ? ": it stays linked to the recording" : ": nothing changes for the recording"))
                + (restoredTrack.Binding != null ? "; plays " + restoredTrack.Binding.Describe() + (restoredTrack.On ? ", bends and volume from the recording" : "") : ""));
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
            // Instrument Runaway recipes (round 6): 122 seconds the Corruption window lets the corrupted game play before it switches Runaway on (value), 123 1 while it is waiting, 124 recipes the SNES snapshot holds (-1 none),
            // 125 a recipe's field, index = field << 16 | recipe: 0 sample, 1 ADSR1, 2 ADSR2, 3 gain, 4 key-on pitch, 5 events, 6 held (0/1), 7 hash (30 bits), 8 uses, 9 pitch events, 10 length in samples, 11 volume events, 12 volume at the key-on (left), 13 the largest pitch change in cents,
            // 126 voices of this instance playing a recipe, 127 chip writes the running SNES game's log holds (-1 none), 128 the snapshot's listening note (copied like 85), 129 the recipe the Recipe parameter plays (0 none)
            case 122: if (emu != null) emu.Corruption.PlayFor = (int)value; return 0;
            case 123: return emu?.Corruption.Listening.Length > 0 ? 1 : 0;
            case 124: return emu?.Snapshot?.Recipes.Length ?? -1;
            case 125:
            {
                var rs = emu?.Snapshot?.Recipes;
                int rec = (int)(index & 0xFFFF), field = (int)(index >> 16);
                if (rs == null || (uint)rec >= (uint)rs.Length) return -1;
                var r = rs[rec];
                return field switch
                {
                    0 => r.Srcn, 1 => r.Adsr1, 2 => r.Adsr2, 3 => r.Gain, 4 => r.Pitch, 5 => r.Events.Length, 6 => r.Held ? 1 : 0, 7 => (nint)(r.Hash() & 0x3FFFFFFF), 8 => r.Uses,
                    9 => r.Count(SpcEv.Pitch), 10 => r.LengthSamples, 11 => r.Count(SpcEv.VolL) + r.Count(SpcEv.VolR), 12 => r.VolL, 13 => r.Events.Where(e => e.Kind == SpcEv.Pitch).Select(e => Math.Abs(e.Value)).DefaultIfEmpty(0).Max(), _ => -1,
                };
            }
            case 126: return voices.Count(v => v.Recipe != null && v.Dsp >= 0);
            case 127: return (nint)(emu?.SpcLogCountForTests() ?? -1);
            case 128:
            {
                var note = emu?.Snapshot?.ListenNote;
                if (note == null) return -1;
                if (value != 0 && index > 0) note.AsSpan(0, Math.Min(note.Length, (int)index)).CopyTo(new Span<char>((void*)value, (int)index));
                return note.Length;
            }
            case 129: { int n = emu?.RecipeCount ?? 0; int rp = Get(P.Recipe); return rp > 0 && rp <= n ? rp : 0; }
            // Genesis recipes: 150 recipes the Sega snapshot holds (-1 none), 151 a recipe's field, index = field << 16 | recipe: 0 kind (0 FM, 1 PSG tone, 2 PSG noise), 1 block, 2 fnum, 3 events, 4 held, 5 hash (30 bits), 6 uses,
            // 7 pitch events, 8 length in samples, 9 patch changes, 10 volume changes, 11 largest pitch change in cents, 12 algorithm, 13 PSG period; 152 the Sega snapshot's listening note (copied like 85)
            case 150: return emu?.SegaState?.Recipes.Length ?? -1;
            case 151:
            {
                var rs = emu?.SegaState?.Recipes;
                int rec = (int)(index & 0xFFFF), field = (int)(index >> 16);
                if (rs == null || (uint)rec >= (uint)rs.Length) return -1;
                var r = rs[rec];
                return field switch
                {
                    0 => r.Kind, 1 => r.Block, 2 => r.Fnum, 3 => r.Events.Length, 4 => r.Held ? 1 : 0, 5 => (nint)(r.Hash() & 0x3FFFFFFF), 6 => r.Uses, 7 => r.Count(GenEv.Pitch), 8 => r.LengthSamples,
                    9 => r.Count(GenEv.Param), 10 => r.Count(GenEv.Vol), 11 => r.Events.Where(e => e.Kind == GenEv.Pitch).Select(e => Math.Abs(e.Value)).DefaultIfEmpty(0).Max(), 12 => r.Patch[28] & 7, 13 => r.Period, _ => -1,
                };
            }
            case 152:
            {
                var note = emu?.SegaState?.ListenNote;
                if (note == null) return -1;
                if (value != 0 && index > 0) note.AsSpan(0, Math.Min(note.Length, (int)index)).CopyTo(new Span<char>((void*)value, (int)index));
                return note.Length;
            }
            case 121: SessionGame.NoStateForTests = value != 0; return 0;      // 121 tests only: console games report that they cannot keep their state (value 1), or go back to the truth (0)
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
            default:
                if (id - Fpd.TestBase is >= 340 and < 360) return PresetTestHook(id - Fpd.TestBase, index, value);        // the presets (Bn2Plugin.Presets.cs)
                if (id - Fpd.TestBase is >= 600 and < 620) return SongTestHook(id - Fpd.TestBase, index, value);          // Test MIDI export (Bn2Plugin.Song.cs)
                if (id - Fpd.TestBase is (>= 640 and < 670) or (>= 680 and < 700)) return RecordTestHook(id - Fpd.TestBase, index, value);        // the recording and the live MIDI out (Bn2Plugin.Record.cs)
                if (id - Fpd.TestBase is >= 700 and < 720) return TrackTestHook(id - Fpd.TestBase, index, value);                                // the tracks on offer (Bn2Plugin.Tracks.cs)
                if (id - Fpd.TestBase is >= 720 and < 740) return RelinkTestHook(id - Fpd.TestBase, index, value);                               // linking to the tracks on offer, the channel's name, the host log (Bn2Plugin.Relink.cs)
                if (id - Fpd.TestBase is >= 740 and < 780) return NesRecordTestHook(id - Fpd.TestBase, index, value);                            // a NES game's recording (Bn2Plugin.RecordNes.cs)
                if (id - Fpd.TestBase is >= 840 and < 860) return CaptureTestHook(id - Fpd.TestBase, index, value);                              // round 9: Capture from FL (Bn2Plugin.Capture.cs; 790-819 are reload's)
                return id - Fpd.TestBase is >= 320 and < 340 ? InjectTestHook(id - Fpd.TestBase, index, value) : id - Fpd.TestBase is >= 300 and < 320 ? InstrumentTestHook(id - Fpd.TestBase, index, value) : TestEditor(id - Fpd.TestBase, index, value);
        }
    }

    public override IFruityEditor? CreateEditor(nint parent) => new Bn2Editor(this, parent);

    private nint TestEditor(int n, nint index, nint value) => Editor is Bn2Editor ed ? ed.Test(n, index, value) : 0;

    public override void Destroy()
    {
        CaptureHub.Unregister(this);
        TrackOffers.Unregister(this);
        ForgetTrack();
        HostLog.Text(InstanceNo, "destroyed");
        TrackOffers.Changed();
        EmulatorHub.Leave(this);
        voices.Clear();
        lock (chipGate) chip = null;
    }
}
