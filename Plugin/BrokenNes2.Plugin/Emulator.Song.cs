// Round 7 (midi): "Test MIDI export" on an emulator. The user composes in FL Studio, exports a MIDI file, opens it here: it is converted by VRUN's own converter
// (Music/Vrun), written into the template (VRUN's AudioPlayer, Music/MusicTemplate.cs) and played as this emulator's NES game in ROM mode, so the four NES
// channels arrive as the plugin's per-channel stems and the user hears what the game will play. Reload converts the same file again after another export.
//
// Threads: reading, converting and patching run on a worker (they allocate); the game is built on the loading thread like any game (StartLoad). Play, Stop,
// Restart and FL's transport only set flags; a restart swaps in a copy of the song that a worker built in advance, already run to just before its first
// decode and with its four stems made (Standby), so the audio thread allocates nothing and the song's first note lands in the first frame after the swap.
using BrokenNes2.Music;
using NesEmulator.Plugin;

namespace BrokenNes2;

/// <summary>What the Test MIDI export window needs between paints. Fields a worker sets are volatile.</summary>
internal sealed class SongPanel
{
    public volatile bool Busy;
    /// <summary>What a worker is doing now ("converting song.mid ..."); empty when idle.</summary>
    public volatile string Status = "";
    public volatile string Result = "";
    public volatile bool ResultGood;
    public int ResultStamp;
    public long ResultTick;
    /// <summary>What loading the song did to the game that was in this emulator (the user is never left to guess): over the picture for a while, and in the window.</summary>
    public volatile string? Note;
    public long NoteTick;
    /// <summary>Follow FL's transport: FL's Play restarts the song, FL's Stop stops it (on by default).</summary>
    public volatile bool Follow = true;

    public void SetResult(string text, bool good)
    {
        if (!good && MusicMutate.Has(MusicMutate.RefusalSilent)) return;            // (the self-test's deliberate bug: a refusal says nothing)
        Result = text; ResultGood = good; ResultTick = Environment.TickCount64;
        Interlocked.Increment(ref ResultStamp);
    }

    public void SetNote(string? text) { Note = text; NoteTick = Environment.TickCount64; Interlocked.Increment(ref ResultStamp); }
}

/// <summary>A song as an emulator holds it: the MIDI file's bytes and where it came from, what converting it did, and the template with it written in.</summary>
internal sealed record SongHeld(string Path, string Name, byte[] Midi, SongFacts Facts, byte[] Rom, long Generation)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>Builds the copies of the songs that a restart swaps in: one thread for the process. Asking for work only raises a flag (no event is set: setting a wait handle
/// allocated on the audio thread, measured 48 bytes a time), and the thread looks at the flag every few milliseconds.</summary>
internal static class SongWorker
{
    private const int PollMs = 10, IdleExitMs = 30_000;
    private static readonly object gate = new();
    private static Thread? thread;
    private static int work;

    /// <summary>Makes sure the thread runs (call it from a thread that may allocate: the UI or a loading thread).</summary>
    public static void Start()
    {
        lock (gate)
        {
            if (thread != null) return;
            thread = new Thread(Loop) { IsBackground = true, Name = "BrokenNes2 song standby" };
            thread.Start();
        }
    }

    /// <summary>Asks the thread to look at every emulator (a flag: safe on the audio thread, nothing allocated).</summary>
    public static void Wake() => Volatile.Write(ref work, 1);

    private static void Loop()
    {
        long idleSince = Environment.TickCount64;
        while (true)
        {
            Thread.Sleep(PollMs);
            if (Interlocked.Exchange(ref work, 0) == 0)
            {
                if (Environment.TickCount64 - idleSince < IdleExitMs) continue;
                idleSince = Environment.TickCount64;
                if (!EmulatorHub.All().Any(e => e.SongActive)) { lock (gate) thread = null; return; }      // idle with no song: a song opened later starts it again
                continue;
            }
            idleSince = Environment.TickCount64;
            foreach (var e in EmulatorHub.All())
                try { e.ServiceStandby(); }
                catch (Exception ex) { BrokenNes.Fruity.Diag.Log("song standby: " + ex); }
        }
    }
}

public sealed partial class Emulator
{
    internal SongPanel SongUi { get; } = new();
    private volatile SongHeld? song;
    private volatile bool songActive;
    /// <summary>A project's song that has not been converted yet (it is, on the loading thread, when the game is built).</summary>
    private volatile SongSection? songPending;
    private int songStopped, songRestart, standbyWanted, transport = -1;
    private long songGeneration;
    private Standby? standby;

    /// <summary>A fresh copy of the song, run to just before its first decode, with its four stems made: what a restart swaps in.</summary>
    private sealed class Standby
    {
        public required NesGame Game;
        public required ApuStem?[] Stems;
        public int Chip, Rate;
        public long Generation;
    }

    /// <summary>The song this emulator holds (null: none was opened).</summary>
    internal SongHeld? Song => song;
    /// <summary>The song is this emulator's NES game (rather than the built-in VRUN or a file).</summary>
    public bool SongActive => songActive;
    /// <summary>The running game is the song.</summary>
    internal bool SongRunning => game is NesGame { IsSongTest: true };
    /// <summary>The song's game is built and waits to be taken by the audio side (between frames).</summary>
    internal bool SongPending => Volatile.Read(ref pendingGame) is NesGame { IsSongTest: true };
    public bool SongStopped => Volatile.Read(ref songStopped) != 0;
    internal bool StandbyReady => Volatile.Read(ref standby) != null;

    // ---- opening, reloading ----

    /// <summary>Reads, converts and patches a MIDI file on a worker, then plays it as this emulator's NES game. Null when started, else why not.</summary>
    internal string? OpenSong(string path, bool reload = false)
    {
        if (SongUi.Busy) return "busy: a song is being converted";
        SongUi.Busy = true;
        string file = Path.GetFileName(path);
        SongUi.Status = (reload ? "reloading " : "converting ") + file + " ...";
        var cached = reload ? song : null;
        SongWorker.Start();
        Task.Run(() =>
        {
            try
            {
                byte[] midi;
                if (reload && MusicMutate.Has(MusicMutate.ReloadCached) && cached != null) midi = cached.Midi;      // (the self-test's deliberate bug)
                else
                {
                    try { midi = File.ReadAllBytes(path); }
                    catch (Exception e) { SongUi.SetResult($"{file} could not be read ({e.Message.TrimEnd('.')}): nothing changed", false); return; }
                }
                var held = MakeSong(path, midi, out string? why);
                if (held == null) { SongUi.SetResult($"{file} was not loaded: {why}. Nothing changed{(song != null ? ": the song that was playing goes on" : "")}", false); return; }
                bool same = reload && cached != null && cached.Midi.AsSpan().SequenceEqual(midi);
                Install(held, fromProject: false);
                SongUi.SetResult(reload ? (same ? $"Reloaded {file}: the file has not changed since it was loaded (export it again from FL, then Reload)" : $"Reloaded {file}: the new export plays from its start")
                                        : $"Loaded {file}: it plays from its start", true);
            }
            catch (Exception e)
            {
                BrokenNes.Fruity.Diag.Log("song open failed: " + e);
                SongUi.SetResult($"{file} was not loaded: {e.Message.TrimEnd('.')}", false);
            }
            finally { SongUi.Status = ""; SongUi.Busy = false; }
        });
        return null;
    }

    /// <summary>Reload: the same file again (after another export from FL).</summary>
    internal string? ReloadSong()
    {
        var held = song;
        if (held == null) { SongUi.SetResult("Nothing to reload: open a MIDI file first", false); return "no song"; }
        return OpenSong(held.Path, reload: true);
    }

    /// <summary>Converts and patches (worker thread). Null with the reason for the user.</summary>
    private SongHeld? MakeSong(string path, byte[] midi, out string? why)
    {
        var template = MusicTemplate.Builtin(out why);
        if (template == null) { why = "Test MIDI export cannot run in this build: " + why; return null; }
        var build = SongConvert.Convert(midi, out why);
        if (build == null) return null;
        var rom = template.Patch(build.Channels, build.Facts.LoopFrames, SongConvert.NameOf(path));
        return new SongHeld(path, SongConvert.NameOf(path), midi, build.Facts, rom, Interlocked.Increment(ref songGeneration));
    }

    /// <summary>The song becomes this emulator's NES game: NES console, ROM mode, the song loaded from power-on. What was here before is said (<see cref="SongPanel.Note"/>).</summary>
    private void Install(SongHeld held, bool fromProject)
    {
        lock (EmulatorHub.Gate)
        {
            string? note = fromProject ? null : DescribeReplaced(held);
            song = held;
            songPending = null;
            songActive = true;
            Volatile.Write(ref songStopped, 0);
            Volatile.Write(ref songRestart, 0);
            Interlocked.Exchange(ref standby, null);
            Volatile.Write(ref mode, 1);
            if (Console != Cx.Nes) Volatile.Write(ref chip, Cx.DefaultChip(Cx.Nes));
            else LeavePresetChipLocked();
            ResetOutputs();
            StartLoad();
            EmulatorHub.ReResolveAll(this);
            Changed();
            if (!fromProject) SongUi.SetNote(note);
        }
    }

    /// <summary>What the song replaces in this emulator, in words (null: it replaces only an earlier song). Under the hub's lock.</summary>
    private string? DescribeReplaced(SongHeld held)
    {
        var parts = new List<string>();
        int console = Console;
        if (Mode == 0) parts.Add($"Emulator #{Id} was in Direct mode (FL's notes played its {ChipId} chip): it is in ROM mode now, playing the song; switch Mode back to Direct to play notes again");
        else if (console != Cx.Nes)
        {
            string game = romPaths[console] != null ? Path.GetFileName(romPaths[console]!) : "no game";
            parts.Add($"Emulator #{Id} was on the {Cx.Names[console]} console ({game}): it is on the NES now, playing the song; pick {Cx.Names[console]} again under Console to go back");
        }
        else if (!songActive)
        {
            string game = romPaths[Cx.Nes] != null ? Path.GetFileName(romPaths[Cx.Nes]!) : "VRUN (built-in)";
            parts.Add($"The song replaced {game} in emulator #{Id}: pick it again in the Game list to go back");
        }
        if (Runaway) parts.Add("Instrument Runaway was switched off");
        string chipNote = Console == Cx.Nes && ChipId != "FIX" ? $"It plays on the {ChipId} sound chip you picked; VRUN itself uses FIX" : "";
        if (chipNote.Length > 0) parts.Add(chipNote);
        return parts.Count == 0 ? null : string.Join(". ", parts) + ".";
    }

    /// <summary>The song again as the NES game (the Game list's MIDI entry, or Play when another game runs).</summary>
    internal void UseSong()
    {
        var held = song;
        if (held == null) return;
        Install(held, fromProject: false);
    }

    /// <summary>The NES game is no longer the song (another game was chosen): the song stays held, so the window can play it again.</summary>
    private void DropSongLocked()
    {
        songActive = false;
        Volatile.Write(ref songRestart, 0);
        Interlocked.Exchange(ref standby, null);
    }

    // ---- play, stop, restart, FL's transport (flags only: any thread, no allocation) ----

    /// <summary>Play: from the start when stopped; the song is put back as the game when another runs. Returns what happened, for the window.</summary>
    internal string PlaySong()
    {
        if (MusicMutate.Has(MusicMutate.PlayCallsStop)) { StopSong(); return "stopped"; }     // (the self-test's deliberate bug)
        if (song == null) return "open a MIDI file first";
        if (!songActive || Mode != 1 || Console != Cx.Nes) { UseSong(); return "playing from its start"; }
        if (!SongStopped) return "already playing";
        RequestRestart();
        return "playing from its start";
    }

    internal void StopSong() => Volatile.Write(ref songStopped, 1);

    internal string RestartSong()
    {
        if (song == null) return "open a MIDI file first";
        if (!songActive || Mode != 1 || Console != Cx.Nes) { UseSong(); return "playing from its start"; }
        RequestRestart();
        return "restarted";
    }

    private void RequestRestart()
    {
        Volatile.Write(ref songRestart, 1);
        if (Volatile.Read(ref standby) == null) WantStandby();
    }

    private void WantStandby()
    {
        Volatile.Write(ref standbyWanted, 1);
        SongWorker.Wake();
    }

    /// <summary>FL's transport started or stopped (FPD_SetPlaying, from whichever instance FL tells first; the others repeat it). With Follow on, a start restarts the song and a stop stops it.
    /// Only the changes count: an unknown state that turns out to be "stopped" does not stop a song that was just opened.</summary>
    internal void TransportChanged(bool playing)
    {
        int before = Interlocked.Exchange(ref transport, playing ? 1 : 0);
        if (!SongUi.Follow || !songActive || MusicMutate.Has(MusicMutate.TransportIgnored)) return;
        if (playing && before != 1) RequestRestart();
        else if (!playing && before == 1) StopSong();
    }

    internal int Transport => Volatile.Read(ref transport);

    // ---- the audio side (under romLock, from ApplyPending / Produce) ----

    /// <summary>Whether the frame about to be made is silent because the song is stopped.</summary>
    private bool SongHolds(IGame g) => g is NesGame { IsSongTest: true } && Volatile.Read(ref songStopped) != 0;

    /// <summary>A restart that was asked for: the prepared copy takes over between frames (nothing allocated: the copy, its stems and the rings exist). When no copy is ready yet the
    /// request waits for the worker (and the song stays where it is, or silent when it was stopped).</summary>
    private void ApplySongRestart()
    {
        if (Volatile.Read(ref songRestart) == 0) return;
        if (!songActive) { Volatile.Write(ref songRestart, 0); return; }
        var g = game;
        if (g is not NesGame { IsSongTest: true }) return;                    // the song game is still being built: it starts from power-on anyway
        var sb = Interlocked.Exchange(ref standby, null);
        if (sb == null) { WantStandby(); return; }
        if (sb.Generation != Volatile.Read(ref songGeneration) || sb.Chip != Chip || sb.Rate != g.HostSampleRate) { WantStandby(); return; }      // stale: another song, chip or rate since it was made
        Volatile.Write(ref songRestart, 0);
        g.Dispose();
        game = sb.Game;
        sb.Game.ChipIndex = sb.Chip;
        appliedChip = sb.Chip;
        int claimedTones = ClaimedTones;
        for (int c = 0; c < Ch.ToneCount; c++) stems[c] = (claimedTones & (1 << c)) != 0 && !MusicMutate.Has(MusicMutate.RestartAllocates) ? sb.Stems[c] : null;
        crashReported = false;
        Volatile.Write(ref songStopped, 0);
        Interlocked.Increment(ref epoch);
        Interlocked.Increment(ref songRestarts);
        WantStandby();                                                         // the next restart's copy
    }

    private int songRestarts;
    internal int SongRestarts => Volatile.Read(ref songRestarts);

    /// <summary>Worker thread: builds the copy a restart swaps in, when one is wanted.</summary>
    internal void ServiceStandby()
    {
        if (Interlocked.Exchange(ref standbyWanted, 0) == 0) return;
        var held = song;
        if (held == null || !songActive) return;
        int chipIdx = Chip;
        var chip = Cx.Chips[chipIdx];
        if (chip.Console != Cx.Nes) return;
        int rate = game?.HostSampleRate ?? EmulatorHub.HostRate;
        var g = new NesGame(held.Rom, chip, rate) { IsSongTest = true, ChipIndex = chipIdx };
        var scratchBuf = new float[4096];
        for (int i = 0; i < MusicTemplate.Preroll; i++)
        {
            g.SetPad(0);
            g.RunFrame();
            while (g.ReadAvailable(scratchBuf) > 0) { }                        // the boot frames' sound is not heard: the song starts with the frame after them
        }
        var nes = g.Nes!;
        nes.CaptureApuWrites = true;                                           // (its write tap is made here, once: the audio thread then only switches it, allocating nothing)
        var latchNow = new byte[0x18];
        uint written = nes.CopyApuLatch(latchNow);
        var made = new ApuStem?[Ch.ToneCount];
        for (int c = 0; c < Ch.ToneCount; c++) made[c] = new ApuStem(chip.Id, c, rate, nes.ApuClock, latchNow, written);
        var sb = new Standby { Game = g, Stems = made, Chip = chipIdx, Rate = rate, Generation = held.Generation };
        if (held.Generation != Volatile.Read(ref songGeneration)) return;    // another song arrived meanwhile: it asks for its own
        Interlocked.Exchange(ref standby, sb);
    }

    // ---- the project ----

    /// <summary>The song section for a project save (null: no song held).</summary>
    internal byte[]? CaptureSongSection()
    {
        var held = song;
        if (held != null) return new SongSection(held.Path, held.Midi, songActive, SongUi.Follow, SongStopped).Build();
        var pending = songPending;                                             // a project's song that has not been converted yet goes back as it came
        return pending?.Build();
    }

    /// <summary>A project's song (from <see cref="Adopt"/>, under the hub's lock): held as a pending section until the loading thread converts it.</summary>
    private void AdoptSong(SongSection? s, string? problem)
    {
        if (problem != null) SongUi.SetResult($"The project's MIDI test song was not used: its section is {problem}", false);
        if (s == null) return;
        song = null;
        songPending = s;
        songActive = s.Active;
        SongUi.Follow = s.FollowTransport;
        Volatile.Write(ref songStopped, s.Stopped ? 1 : 0);
        SongWorker.Start();
    }

    /// <summary>On the loading thread: a project's song converted again (the template and converter are this build's). Throws with the reason when it cannot be.</summary>
    private SongHeld HeldForLoad(SongSection pending)
    {
        var held = MakeSong(pending.Path, pending.Midi, out string? why);
        if (held == null)
        {
            string text = $"the project's song {Path.GetFileName(pending.Path)} could not be played: {why}";
            SongUi.SetResult("The project's MIDI test song was not used: " + text[("the project's song ".Length)..], false);
            throw new InvalidOperationException(text);
        }
        song = held;
        songPending = null;
        SongUi.SetResult($"The project's song {held.FileName} was converted again from the MIDI the project keeps", true);
        return held;
    }

    // ---- tests ----

    /// <summary>The bytes of the running song ROM (null: the running game is not the song).</summary>
    internal byte[]? SongRomForTests() => SongRunning ? song?.Rom : null;

    /// <summary>Runs exactly <paramref name="frames"/> frames the way the audio thread does (a pending game or restart is applied between frames) and copies the APU register latch after each, 24 bytes a frame.
    /// Returns the frames run, -1 with no game. (Tests: the engine check reads the song's decoders frame by frame through it.)</summary>
    internal unsafe long RunFramesLatchForTests(int frames, byte* dest)
    {
        lock (romLock)
        {
            var latch = new byte[0x18];
            int done = 0;
            ApplyPending();                                                    // (also with 0 frames: a game that is built is taken, as the audio thread would)
            for (int i = 0; i < frames; i++)
            {
                ApplyPending();
                var g = game;
                if (g?.Nes == null || g.Crashed) break;
                Produce(g);
                g.Nes.CopyApuLatch(latch);
                if (dest != null) for (int k = 0; k < 0x18; k++) dest[i * 0x18 + k] = latch[k];
                done++;
            }
            return game == null ? -1 : done;
        }
    }
}
