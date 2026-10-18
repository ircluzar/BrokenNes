// Round 8 follow-up (record, 2026-10-18): an instance recognises which track of an offered recording its FL channel plays (TrackOffer.cs, Record/RecTracks.cs).
//
// ELIGIBLE means LINKED to the tracks on offer (relink, 2026-10-18: Bn2Plugin.Relink.cs). An instance joins by itself when it is created while tracks are on offer (a new channel, or the one FL makes
// for a Replace) or is still pristine when they are offered; FL's parameter pushes do not undo that (they are logged). It is the user's own CONFIGURED instance (it leaves, and never joins by itself)
// when: a project state it restores holds a game, a preset or a track; the user changes a value in its editor; the user loads a preset, a game or starts a recording from it; it settled on a track;
// a track was picked for it by hand. The editor's "Link to recording" and the Record window's "Link all" link any instance by hand. (Until relink, eligibility was an "untouched" heuristic that
// non-default parameter pushes defeated, with nothing on screen until the first note: the user's r12 report.)
//
// At each note FL starts (TriggerVoice, the audio thread, before the note is rendered) a linked instance asks FL for the song position (FHD_GetMixingTime in beats; with GT_LocalTime too, for
// the log), and the note's colour, and feeds the matcher. When the matcher names a track the instance takes that track's prebuilt engine and preset and plays it from this very note (the "bound"
// render path: whatever emulator it sits on). The offer's worker then settles it on the tracks' emulator (Direct mode, PRE-SNES), where it is an ordinary preset instance; section TRK1 of
// the project says which track of which recording it plays, and how it was found.
using System.Buffers.Binary;
using System.Text;
using BrokenNes.Fruity;
using BrokenNes2.Record;

namespace BrokenNes2;

/// <summary>The track an instance plays (for the window and the project): the recording, the track, how it was found.</summary>
internal sealed record TrackBinding(ulong SessionId, string Recording, int Track, string Name, int How)
{
    public const int ByNotes = 1, ByHand = 2, ByName = 3;
    public string HowWords => How switch { ByHand => "picked by hand", ByName => "found by its channel's name", _ => "recognised from its notes" };
    public string Describe() => $"track {Track + 1} of \"{Recording}\": {Name} ({HowWords})";
}

public sealed unsafe partial class Bn2Plugin
{
    private volatile bool configured;
    private readonly TrackMatcher matcher = new();
    private volatile TrackSlot? boundSlot;
    private volatile int bindState;              // 0 none, 1 bound by its notes and playing (not settled yet: the bound render path), 2 settled (an ordinary preset instance)
    private int boundIndex = -1, settleWanted;
    private volatile TrackBinding? binding;
    private volatile string? trackNote;          // what the window says about this instance's track (a settle refused, an engine missing)
    private long ownSamples, engineWaits;
    private int notesSeen, paramLogged;
    // the track's own pitch bends and volume (by song position) instead of FL's: see TrackExpression
    private volatile TrackExpression? expression;
    private volatile bool exprOn;
    private double exprOffset, exprBlockBeat = double.NaN, exprBeatsPerSample;
    private bool exprOwnClock;
    private int renderLength;

    /// <summary>The window's "bends and volume from the recording" for this channel (UI thread).</summary>
    internal void SetTrackExpression(bool on) { if (expression != null) exprOn = on; }
    internal bool TrackExpressionOn => expression != null && exprOn;

    /// <summary>The track's beat now (the start of the block being rendered), from FL's song position or the instance's own clock as the track was found; NaN when there is none.</summary>
    private double TrackBeatNow()
    {
        double t = exprOwnClock
            ? (ownSamples - renderLength) / (double)Math.Max(1, EmulatorHub.HostRate) * EmulatorHub.FlTempo / 60.0
            : SongBeats(0);
        return t - exprOffset;
    }

    /// <summary>A note of a channel that plays a track's expression: its key is the track's note at that time (FL's own pitch may hold an imported bend), its velocity FL's at the key-on.</summary>
    private void ExpressionNoteOn(Voice v)
    {
        v.TrackKey = -1;
        var ex = expression;
        if (ex == null || !exprOn || RecMutants.On(RecMutants.ExpressionIgnored)) return;
        double beat = TrackBeatNow();
        if (double.IsNaN(beat)) return;
        v.TrackKey = ex.KeyAt(beat, v.Params->InitLevels.Pitch, TrackIndex.Tolerance);
        v.TrackVel = v.Params->InitLevels.Vol;
    }
    internal readonly int InstanceNo = HostLog.NextInstance();

    /// <summary>The track this instance plays, or null.</summary>
    internal TrackBinding? Binding => binding;
    internal bool Configured => configured;
    internal string? TrackNote => trackNote;
    internal TrackMatcher Matcher => matcher;

    /// <summary>The user or a project configured this instance (it is the user's own): it does not join an offer by itself, and leaves one it joined by itself. A channel linked by hand stays
    /// linked when <paramref name="keepHandLink"/> (a knob changed in its editor), and leaves otherwise (a preset, a game, a project state of its own, Unlink).</summary>
    internal void MarkConfigured(string why, bool keepHandLink = false)
    {
        pristine = false;
        if (!configured) { configured = true; HostLog.Text(InstanceNo, "configured: " + why); if (link == LinkNone) linkNote ??= why; }
        if ((link == LinkAuto || (link == LinkHand && !keepHandLink)) && bindState == 0 && binding == null)
        {
            link = LinkNone;
            linkNote = why;
            HostLog.Text(InstanceNo, "left the recording: " + why);
            TrackOffers.Changed();
        }
    }

    protected override void OnUserChange(int index, int value) => MarkConfigured($"you set {Params[index].Name} to {value} in its editor", keepHandLink: true);

    public override int ProcessParam(int index, int value, int recFlags)
    {
        int before = (uint)index < (uint)Params.Length ? Get(index) : 0;
        int r = base.ProcessParam(index, value, recFlags);
        if ((recFlags & Rec.UpdateValue) != 0 && (uint)index < (uint)Params.Length)
        {
            if (HostLog.On && paramLogged < HostLog.PerInstance) { paramLogged++; HostLog.Add(HostLog.KindParam, InstanceNo, index, Get(index), recFlags); }
            int now = Get(index);
            if (now != before && now != Params[index].Default)
            {
                // FL set a value that is neither the default nor the one it had: logged (above), and the instance is no longer pristine; it stays in the recording if it joined it (relink: FL's
                // Replace may push the old channel's values; the old rule took that for the user's own configuration and the counter never moved). No text here: this can be the audio thread.
                pristine = false;
                if (RecMutants.On(RecMutants.ParamPushUnjoins) && bindState == 0 && binding == null) { configured = true; link = LinkNone; TrackOffers.Changed(); }      // (the deliberate bug: the old rule)
            }
        }
        return r;
    }

    /// <summary>Whether a restored state makes this instance clearly the user's own: it holds a game (its machine, a frozen game's sound, a song, a recording, a corruption, pushed instruments),
    /// a preset or a track. Values alone do not (FL may hand a default or another channel's values).</summary>
    private static bool RestoredUsersOwn(bool hadGame, bool hadPreset, bool hadTrack)
    {
        if (RecMutants.On(RecMutants.DefaultStateCountsAsTouched)) return true;
        if (RecMutants.On(RecMutants.UsersOwnStateJoins)) return false;
        return hadGame || hadPreset || hadTrack;
    }

    /// <summary>Whether the restored values are the defaults (emulator-level values aside: they belong to the emulator): the instance stays pristine.</summary>
    private bool RestoredPristine()
    {
        if (!RestoredCleanly) return true;
        for (int i = 0; i < Params.Length; i++) if (!P.IsEmulatorLevel(i) && Get(i) != Params[i].Default) return false;
        return true;
    }

    // ---- the audio thread ----

    /// <summary>FL's song position of the block being mixed, in beats (NaN: FL gave none).</summary>
    private double SongBeats(int flags)
    {
        TFPTime t; t.T = double.NaN; t.T2 = 0;
        Host.Dispatcher(HostTag, Fhd.GetMixingTime, Gt.Beats | flags, (nint)(&t));
        return t.T;
    }

    /// <summary>A note FL starts (TriggerVoice): the linked instance weighs it against the tracks on offer and, when a track is named, plays it from this note. Allocation-free.</summary>
    private void MatchNote(Voice v)
    {
        notesSeen++;
        bool log = HostLog.On && notesSeen <= 32;
        var snap = TrackOffers.Current;
        bool eligible = snap != null && bindState != 2 && (link != LinkNone || bindState == 1) && binding == null
            && !(bindState == 0 && assignment.Emulator is { } se && snap.IsSource(se.Id))                                           // (the recording's own channels never join it)
            && !(RecMutants.On(RecMutants.OfferIgnoresLateInstances) && InstanceNo <= TrackOffers.OfferedAfterInstance);    // (the deliberate bug: instances made before the offer are ignored)
        if (!eligible && !log) return;
        double pos = SongBeats(0);
        double local = log ? SongBeats(Gt.LocalTime) : double.NaN;
        int color = Host.VoiceProcessEvent(v.HostTag, Fpv.GetColor, 0, 0);
        if (log)
        {
            int len = Host.VoiceProcessEvent(v.HostTag, Fpv.GetLength, 0, 0);
            float vel = BitConverter.Int32BitsToSingle(Host.VoiceProcessEvent(v.HostTag, Fpv.GetVelocity, 0, 0));
            HostLog.Add(HostLog.KindNote, InstanceNo, notesSeen, (long)color << 32 | (uint)len, (long)(vel * 1000), v.Params->InitLevels.Pitch, pos, local);
        }
        if (!eligible) return;
        double own = ownSamples / (double)Math.Max(1, EmulatorHub.HostRate) * EmulatorHub.FlTempo / 60.0;
        int before = matcher.Bound;
        float flVel = BitConverter.Int32BitsToSingle(Host.VoiceProcessEvent(v.HostTag, Fpv.GetVelocity, 0, 0));          // (FL's velocity: tells apart tracks that play the same notes, record-nes)
        matcher.Feed(snap!.Index, v.Params->InitLevels.Pitch, pos, own, color, flVel > 0 ? flVel * 127.0 : -1);
        if (RecMutants.On(RecMutants.BindAfterFirstNote) && notesSeen == 1) { pendingLateBind = matcher.Bound; return; }      // (the self-test's deliberate bug: the first note is played before the track is taken)
        if (matcher.Bound != before || (matcher.Bound >= 0 && boundIndex != matcher.Bound)) { if (matcher.Bound < 0) Unbind(); else Bind(snap, matcher.Bound); }
        if (matcher.Bound >= 0) { exprOffset = matcher.Offset; exprOwnClock = matcher.OwnClock; ExpressionNoteOn(v); }
        if (matcher.Settled && bindState == 1) Volatile.Write(ref settleWanted, 1);
        if (log) HostLog.Add(HostLog.KindMatch, InstanceNo, notesSeen, (uint)(matcher.Bound + 1) | (matcher.Settled ? 1L << 16 : 0) | (matcher.Unsure ? 1L << 17 : 0) | (long)matcher.How << 18, matcher.Candidates);
    }

    private int pendingLateBind = -1;

    /// <summary>Plays track <paramref name="t"/> from now: its preset, and the engine prebuilt for it (taken with one exchange). Audio thread, allocation-free.</summary>
    private void Bind(TrackOfferSnapshot snap, int t)
    {
        var slot = snap.Slots[t];
        if (slot.Preset == null) { trackNote = $"track {t + 1} has no preset ({slot.Problem}): it is not played"; return; }
        CountOn(slot);
        DropSampler();                                                   // (the voices let go of the last engine; nothing allocated)
        var eng = Interlocked.Exchange(ref slot.Ready, null);
        TrackOffers.Changed();                                           // (the window's count: this channel found its track)
        if (slot.Preset.Play.Console == Instruments.ConsoleId.Nes)
        {
            // a NES track (record-nes): the engine is this instance's own copy of the preset with its own register player; none ready yet: silent until the worker builds one (RenderBound)
            preset = eng?.Nes != null && eng.Rate == EmulatorHub.HostRate ? eng.Nes : slot.Preset;
            expression = slot.Expression;
            exprOn = TrackOffers.ExpressionFromRecording;
            boundSlot = slot;
            boundIndex = t;
            bindState = 1;
            return;
        }
        if (RecMutants.On(RecMutants.EnginesNotPrebuilt)) eng = new TrackEngine(new SnesSampler(slot.Preset.Play.Snes!, EmulatorHub.HostRate), EmulatorHub.HostRate);   // (the deliberate bug: built on the audio thread)
        if (eng != null && eng.Rate == EmulatorHub.HostRate) { sampler = eng.Sampler; samplerFor = slot.Preset.Play.Snes; samplerRate = eng.Rate; }
        preset = slot.Preset;
        expression = slot.Expression;
        exprOn = TrackOffers.ExpressionFromRecording;
        boundSlot = slot;
        boundIndex = t;
        bindState = 1;
    }

    /// <summary>The instance no longer plays a track (another preset was loaded, the preset was dropped): UI thread.</summary>
    internal void ForgetTrack()
    {
        CountOn(null);
        binding = null;
        expression = null;
        boundSlot = null; boundIndex = -1;
        if (bindState == 1) preset = null;
        bindState = 0;
    }

    private void Unbind()
    {
        TrackOffers.Changed();
        CountOn(null);
        expression = null;
        boundSlot = null; boundIndex = -1;
        if (bindState == 1) { bindState = 0; preset = null; DropSampler(); }
    }

    private TrackSlot? countedSlot;

    /// <summary>The window's count of instances playing each track: this instance counts on <paramref name="s"/> now (null: on none). Allocation-free.</summary>
    private void CountOn(TrackSlot? s)
    {
        var old = countedSlot;
        if (ReferenceEquals(old, s)) return;
        if (old != null) Interlocked.Decrement(ref old.Playing);
        if (s != null) Interlocked.Increment(ref s.Playing);
        countedSlot = s;
    }

    /// <summary>The bound render path: the track's preset on its engine, whatever emulator the instance sits on (until it is settled). When the engine was not ready yet (a second instance on one
    /// track before the worker built another), it is silent until it is (counted), never built here.</summary>
    private void RenderBound(float* dest, int length, float gl, float gr)
    {
        var lp = preset;
        var slot = boundSlot;
        if (lp != null && lp.Play.Console == Instruments.ConsoleId.Nes)
        {
            // a NES track (record-nes): its own register player, taken from the worker's engine (never built here)
            if (lp.Nes == null || lp.Nes.HostRate != EmulatorHub.HostRate)
            {
                var eng = slot == null ? null : Interlocked.Exchange(ref slot.Ready, null);
                if (eng?.Nes != null && eng.Rate == EmulatorHub.HostRate) { preset = lp = eng.Nes; }
                else { engineWaits++; FinishReleased(); new Span<float>(dest, length * 2).Clear(); return; }
            }
            RenderPreset(dest, length, gl, gr);
            return;
        }
        if (lp?.Play.Snes is not { } snap) { FinishReleased(); new Span<float>(dest, length * 2).Clear(); return; }
        if (sampler == null || !ReferenceEquals(samplerFor, snap) || samplerRate != EmulatorHub.HostRate)
        {
            var eng = slot == null ? null : Interlocked.Exchange(ref slot.Ready, null);
            if (eng != null && eng.Rate == EmulatorHub.HostRate) { DropSampler(); sampler = eng.Sampler; samplerFor = snap; samplerRate = eng.Rate; }
            else if (slot != null) { engineWaits++; FinishReleased(); new Span<float>(dest, length * 2).Clear(); return; }
        }
        RenderPreset(dest, length, gl, gr);
    }

    // ---- the offer's worker ----

    /// <summary>The worker: an instance that recognised its track goes to the tracks' emulator (Direct mode, PRE-SNES) and is an ordinary preset instance from then on.</summary>
    internal void SettleIfWanted()
    {
        if (pendingLateBind >= 0 && TrackOffers.Current is { } late) { int t = pendingLateBind; pendingLateBind = -1; Bind(late, t); if (matcher.Settled) settleWanted = 1; }
        if (Volatile.Read(ref settleWanted) == 0 || bindState != 1) return;
        Volatile.Write(ref settleWanted, 0);
        var slot = boundSlot;
        if (slot == null) return;
        if (Settle(slot, TrackBinding.ByNotes) is { } why) { trackNote = $"plays {slot.Info.Name}, but stays where FL put it: {why}"; HostLog.Text(InstanceNo, "not settled: " + why); }
    }

    /// <summary>Moves this instance to the tracks' emulator in preset mode (UI thread or the worker). Null when done, else why not.</summary>
    private string? Settle(TrackSlot slot, int how)
    {
        bool nes = slot.Preset?.Play.Console == Instruments.ConsoleId.Nes;
        int id = TrackOffers.TracksEmulator(nes);
        if (id == 0) return $"all {EmulatorHub.MaxEmulators} emulators are in use (a project saved now keeps the track: it plays it again when reopened)";
        Volatile.Write(ref Values[P.Emulator], id);
        Volatile.Write(ref Values[P.Channel], 0);
        Volatile.Write(ref Values[P.Voice], 0);
        Volatile.Write(ref Values[P.Recipe], 0);
        EmulatorHub.Resolve(this);
        var emu = Emulator;
        if (emu == null) return "it found no emulator";
        if (emu.EnterPresetMode(nes ? "NES" : "SNES") is { } why) return why;
        binding = new TrackBinding(slot.Held.Session.Id, slot.Held.Session.Name, slot.Info.Index, slot.Info.Name, how);
        configured = true;
        pristine = false;
        trackNote = null;
        if (how == TrackBinding.ByNotes) bindState = 2;
        HostLog.Text(InstanceNo, $"settled on emulator #{id}: plays {binding.Describe()}");
        TrackOffers.Changed();
        return null;
    }

    /// <summary>"Play here" in the Record window (UI thread), or the channel's name naming the track (<see cref="TrackBinding.ByName"/>): this instance plays a track of an offered recording,
    /// whatever it played before.</summary>
    internal string? PickTrack(TrackSlot slot, int how = TrackBinding.ByHand)
    {
        if (how == TrackBinding.ByHand && RecMutants.On(RecMutants.ManualPickIgnored)) return null;            // (the self-test's deliberate bug: says done, does nothing)
        if (slot.Preset == null) return $"{slot.Info.Name} has no preset: {slot.Problem}";
        ForgetTrack();
        link = LinkNone;                                                         // (it takes a track now: not "waiting" any more, and loading the track's preset is not "leaving")
        MarkConfigured(how == TrackBinding.ByName ? "its channel's name names a track" : "a track was picked by hand");
        if (AdoptPreset(slot.Preset.File, null, out _) is { } why) return why;          // (the standard path: its engine is prepared here, on the UI thread)
        CountOn(slot);
        exprOffset = 0; exprOwnClock = false;
        expression = slot.Expression;
        exprOn = TrackOffers.ExpressionFromRecording;
        if (Settle(slot, how) is { } why2) { binding = new TrackBinding(slot.Held.Session.Id, slot.Held.Session.Name, slot.Info.Index, slot.Info.Name, how); TrackOffers.Changed(); return "it holds the track's preset, but " + why2; }
        return null;
    }

    /// <summary>What the window says about this instance and the tracks on offer.</summary>
    internal string TrackStatus()
    {
        var b = binding;
        if (b != null) return "This channel plays " + b.Describe() + (trackNote != null ? "; " + trackNote : "") + ".";
        if (bindState == 1 && boundSlot is { } s) return $"This channel plays track {s.Info.Index + 1} ({s.Info.Name}){(matcher.Unsure ? $": {matcher.Candidates} tracks play the same notes so far; it plays the first of them until a note tells them apart" : "")}; it moves to the tracks' emulator in a moment.";
        if (trackNote != null) return trackNote;
        var snap = TrackOffers.Current;
        if (snap == null) return "No tracks are on offer: record, or reopen a project with a recording.";
        return OfferRole(snap) switch
        {
            1 => "This channel's emulator made the recording: it keeps playing the game.",
            3 => "This channel is linked to the recording: press Play in FL and it finds its track from its first note (or press \"Play here\" on a row).",
            _ => $"This channel is not linked ({linkNote ?? "its own settings"}): press \"Link all\" above, \"Link to recording\" in its editor, or \"Play here\" on a row.",
        };
    }

    // ---- the project: section TRK1 ----

    internal const uint TrackTag = 0x314B5254;                   // "TRK1"

    // u16 version (2) | u16 flags (bit 0: bends and volume from the recording) | u64 the recording's session id | i32 track | u8 how | u16 + UTF-8 name | u16 + UTF-8 recording
    // | i32 + the track's expression (TrackExpression.ToBytes; 0 bytes: none) | u32 CRC-32 of everything before it. Version 1 (no flags meaning, no expression) is read too.
    private const int TrackVersion = 2;

    private byte[]? TrackSectionBytes()
    {
        var b = binding;
        if (b == null || RecMutants.On(RecMutants.TrackSectionDropped)) return null;
        var name = Encoding.UTF8.GetBytes(b.Name.Length > 200 ? b.Name[..200] : b.Name);
        var rec = Encoding.UTF8.GetBytes(b.Recording.Length > 200 ? b.Recording[..200] : b.Recording);
        var ex = expression?.ToBytes() ?? [];
        var s = new byte[2 + 2 + 8 + 4 + 1 + 2 + name.Length + 2 + rec.Length + 4 + ex.Length + 4];
        int o = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(o), TrackVersion); o += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(o), (ushort)(exprOn ? 1 : 0)); o += 2;
        BinaryPrimitives.WriteUInt64LittleEndian(s.AsSpan(o), b.SessionId); o += 8;
        BinaryPrimitives.WriteInt32LittleEndian(s.AsSpan(o), b.Track); o += 4;
        s[o++] = (byte)b.How;
        BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(o), (ushort)name.Length); o += 2; name.CopyTo(s, o); o += name.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(o), (ushort)rec.Length); o += 2; rec.CopyTo(s, o); o += rec.Length;
        BinaryPrimitives.WriteInt32LittleEndian(s.AsSpan(o), ex.Length); o += 4; ex.CopyTo(s, o); o += ex.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(o), Music.Crc32.Of(s.AsSpan(0, o)));
        return s;
    }

    /// <summary>A project's TRK1: which track this instance played, its expression and whether it plays it. Binding null when it cannot be read (a newer or damaged section is dropped: the preset
    /// it holds still plays).</summary>
    internal static (TrackBinding? Binding, TrackExpression? Expression, bool On) ReadTrackSection(ReadOnlySpan<byte> s)
    {
        if (s.Length < 25) return (null, null, false);
        int version = BinaryPrimitives.ReadUInt16LittleEndian(s);
        if (version is < 1 or > TrackVersion) return (null, null, false);
        if (BinaryPrimitives.ReadUInt32LittleEndian(s[^4..]) != Music.Crc32.Of(s[..^4])) return (null, null, false);
        int flags = BinaryPrimitives.ReadUInt16LittleEndian(s[2..]);
        int o = 4;
        ulong id = BinaryPrimitives.ReadUInt64LittleEndian(s[o..]); o += 8;
        int track = BinaryPrimitives.ReadInt32LittleEndian(s[o..]); o += 4;
        int how = s[o++];
        int nl = BinaryPrimitives.ReadUInt16LittleEndian(s[o..]); o += 2;
        if (o + nl + 2 > s.Length - 4) return (null, null, false);
        string name = Encoding.UTF8.GetString(s.Slice(o, nl)); o += nl;
        int rl = BinaryPrimitives.ReadUInt16LittleEndian(s[o..]); o += 2;
        if (o + rl > s.Length - 4) return (null, null, false);
        string rec = Encoding.UTF8.GetString(s.Slice(o, rl)); o += rl;
        TrackExpression? ex = null;
        if (version >= 2)
        {
            if (o + 4 > s.Length - 4) return (null, null, false);
            int el = BinaryPrimitives.ReadInt32LittleEndian(s[o..]); o += 4;
            if (el < 0 || o + el != s.Length - 4) return (null, null, false);
            if (el > 0) ex = TrackExpression.FromBytes(s.Slice(o, el));
        }
        else if (o != s.Length - 4) return (null, null, false);
        if (how is not (TrackBinding.ByNotes or TrackBinding.ByHand or TrackBinding.ByName) || track < 0) return (null, null, false);
        return (new TrackBinding(id, rec, track, name, how), ex, (flags & 1) != 0);
    }

    /// <summary>After a restore with TRK1: the binding is shown again, the expression played as saved; an instance whose emulator is not in preset mode (it was never settled) plays its preset
    /// on the bound path.</summary>
    private void RestoreTrack((TrackBinding? Binding, TrackExpression? Expression, bool On) t)
    {
        if (t.Binding == null) return;
        binding = t.Binding;
        expression = t.Expression;
        exprOn = t.On && t.Expression != null;
        exprOffset = 0; exprOwnClock = false;
        configured = true;
        pristine = false;
        link = LinkNone;
        if (preset != null && Emulator?.PresetMode != true) bindState = 1;
        TrackOffers.Changed();
    }

    // ---- test hooks 700-719 ----

    private nint TrackTestHook(int n, nint index, nint value)
    {
        var emu = assignment.Emulator;
        switch (n)
        {
            case 700: return TrackOffers.Current == null ? -1 : TrackOffers.Ready ? TrackOffers.Current.Slots.Length : 0;                 // tracks on offer (-1 none, 0 engines not built yet)
            case 701: { if (value == 0) TrackOffers.WithdrawAll(); else if (emu?.Recording is { } h) TrackOffers.Offer(emu.Id, h); else return 1; return 0; }   // withdraw all (0) / offer this emulator's recording (1)
            case 702: return binding?.Track ?? (bindState == 1 && boundSlot is { } bsl ? bsl.Info.Index : -1);                                                       // the track this instance plays (-1 none)
            case 703: return bindState;                                                                                                   // 0 none, 1 bound (not settled), 2 settled
            case 704: return binding?.How ?? 0;                                                                                           // 1 by its notes, 2 by hand
            case 705: return configured ? 1 : 0;
            case 706: return matcher.Notes;
            case 707: return matcher.Candidates;
            case 708: { if (TrackOffers.Current is not { } s || (uint)index >= (uint)s.Slots.Length) return 1; var why = PickTrack(s.Slots[(int)index]); lastRecordResult = why ?? "picked"; return why == null ? 0 : 1; }   // pick track <index> by hand
            case 709: return CopyText(TrackStatus(), index, value);
            case 710: HostLog.Set(value != 0); return 0;
            case 711: return HostLog.On ? 1 : 0;
            case 712: return (nint)engineWaits;
            case 713: return TrackOffers.Current is { } s2 && (uint)index < (uint)s2.Slots.Length ? s2.Slots[(int)index].Playing : -1;
            case 714: return InstanceNo;
            case 715: return matcher.How;
            case 716: return matcher.Unsure ? 1 : 0;
            case 717: { if (value >= 0) TrackOffers.SetExpression(value != 0); return TrackOffers.ExpressionFromRecording ? 1 : 0; }      // bends and volume from the recording, for every track channel (value < 0 reads)
            case 718: return TrackExpressionOn ? 1 : 0;                                                                                    // this channel plays its track's expression
            case 719: return TrackOffers.Current?.Index.Count ?? -1;                                                                        // notes in the offer's index (-1: no offer)
            default: return 0;
        }
    }
}
