// ROM mode of an emulator: one game (NES, Game Boy or SNES) runs, however many FL instances listen to it.
//
// The game is advanced on demand, one frame at a time, by whichever instance needs sound it does not have yet (under
// romLock, so FL's mixer threads can all call in). Each frame yields the game's whole mix and, for a NES game, one stem
// per claimed channel: a bare chip that receives only that channel's register writes, replayed at the moments the game made
// them. The results go into one ring per channel; each instance reads its own channel's ring at its own pace.
using NesEmulator.Plugin;
using NesEmulator.Systems;

namespace BrokenNes2;

public sealed partial class Emulator
{
    private const int PictureBytes = 512 * 480 * 4;

    /// <summary>A power-of-two sample ring with a running write count; readers keep their own position.</summary>
    private sealed class Ring
    {
        public const int Size = 1 << 16;
        public readonly float[] Data = new float[Size];
        public long Write;
        /// <summary>Where this ring's stream began (the epoch started, or its stem was made): readers that start together start here.</summary>
        public long Origin;

        public void Append(ReadOnlySpan<float> s)
        {
            int pos = (int)(Write & (Size - 1));
            int first = Math.Min(s.Length, Size - pos);
            s.Slice(0, first).CopyTo(Data.AsSpan(pos));
            if (first < s.Length) s.Slice(first).CopyTo(Data);
            Write += s.Length;
        }

        public void Read(long from, Span<float> dest)
        {
            int pos = (int)(from & (Size - 1));
            int first = Math.Min(dest.Length, Size - pos);
            Data.AsSpan(pos, first).CopyTo(dest);
            if (first < dest.Length) Data.AsSpan(0, dest.Length - first).CopyTo(dest.Slice(first));
        }
    }

    private readonly object romLock = new();
    private IGame? game;                                   // romLock
    private IGame? pendingGame;                            // built on a worker thread, taken under romLock
    private readonly ApuStem?[] stems = new ApuStem?[Ch.ToneCount];
    private readonly Ring?[] rings = new Ring?[Ch.Total];                // made when somebody sits on the channel (a ring is 256 KB)
    private readonly bool[] stemLive = new bool[Ch.Total];               // a Sega stem that is being made (its ring's origin was set when it started)

    /// <summary>The ring of a channel, made on first use (under the hub's lock when an instance takes the channel, so the audio threads find it there).</summary>
    private Ring RingOf(int channel) => Volatile.Read(ref rings[channel]) ?? EnsureRing(channel);

    private Ring EnsureRing(int channel)
    {
        lock (rings)
        {
            var r = rings[channel];
            if (r == null) { r = new Ring(); Volatile.Write(ref rings[channel], r); }
            return r;
        }
    }
    private readonly float[] scratch = new float[4096];
    private readonly float[] zeros = new float[8192];
    private readonly byte[] latch = new byte[0x18];
    private const int AlignWindow = 8192;     // a reader that attaches within this many samples of a stream's origin starts at the origin
    private int epoch, ringEpoch, loadGeneration, appliedChip = -1, unloadRequested;
    private volatile bool loading;
    private volatile string romStatus = "ready";
    private bool crashReported;
    private long lastPullTick, lastPumpTick;

    // ---- the picture, shared with every editor on this emulator ----
    private readonly object frameLock = new();
    private readonly byte[] frame = new byte[PictureBytes];
    private int picW = 256, picH = 240, dispW = 256, dispH = 240;
    private long frameVersion;
    private bool frameValid;

    /// <summary>What the game is doing, in words ("running VRUN (built-in)", "could not load x.nes: ...").</summary>
    public string RomStatus { get => romStatus; internal set => romStatus = value; }

    public bool PictureValid { get { lock (frameLock) return frameValid; } }
    public long PictureVersion => Volatile.Read(ref frameVersion);

    /// <summary>Copies the picture as RGBA (<paramref name="w"/> x <paramref name="h"/> pixels, to be shown <paramref name="shownW"/> x <paramref name="shownH"/> in shape).</summary>
    public bool CopyPicture(byte[] rgba, out int w, out int h, out int shownW, out int shownH)
    {
        lock (frameLock)
        {
            w = picW; h = picH; shownW = dispW; shownH = dispH;
            if (!frameValid) return false;
            Buffer.BlockCopy(frame, 0, rgba, 0, Math.Min(rgba.Length, w * h * 4));
            return true;
        }
    }

    public long FramesRun => game?.FramesRun ?? 0;
    public bool RomLoaded => game != null || Volatile.Read(ref pendingGame) != null;
    public bool RomCrashed => game?.Crashed == true;
    /// <summary>The cores of the running game, for the status line (empty when none).</summary>
    public string RomDescription => game?.Describe() ?? "";
    /// <summary>The NES sound chip the running game uses, as "APU_FIX" (null: none, or not a NES game).</summary>
    public string? RomNesApuId => game?.Nes?.ApuCoreId;
    public int RomConsole => game?.Console ?? -1;

    /// <summary>A hash of the current picture (0 when there is none): tests use it to see the game move.</summary>
    public long PictureHash()
    {
        ulong h = 1469598103934665603UL;
        lock (frameLock)
        {
            if (!frameValid) return 0;
            int n = picW * picH * 4;
            for (int i = 0; i < n; i++) h = (h ^ frame[i]) * 1099511628211UL;
        }
        return (long)(h | 1);
    }

    private void CaptureFrame(IGame g)
    {
        lock (frameLock)
        {
            g.CopyFrame(frame, out picW, out picH, out dispW, out dispH);
            frameValid = true;
            frameVersion++;
        }
    }

    private void ClearFrame()
    {
        lock (frameLock) { frameValid = false; frameVersion++; }
    }

    // ---- loading ----

    private void EnsureRomLoaded()
    {
        int console = Console;
        bool right = game?.Console == console || Volatile.Read(ref pendingGame)?.Console == console;
        if (!right && !loading) StartLoad();
    }

    private static byte[] BuiltInRom()
    {
        using var s = typeof(Emulator).Assembly.GetManifestResourceStream("default.nes")
            ?? throw new InvalidOperationException("the built-in ROM is missing from the plugin");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Builds the game on a worker thread (it allocates a whole console) and hands it to the audio side. A bad file leaves
    /// the previous game running and says why in <see cref="RomStatus"/>. A console with no game chosen has nothing to run.</summary>
    private void StartLoad(RestoreRequest? request = null)
    {
        int gen = Interlocked.Increment(ref loadGeneration);
        ClearRunaway();                       // another game: whatever was frozen is gone
        ClearStateNote();
        Volatile.Write(ref stateRestore, 0);
        carriedSection = null;
        // a corruption started on the game that is being replaced is gone with it; one a project carried comes back with the game
        var carriedCrp = request?.Corruption is { Problem: null } wholeSection ? wholeSection : null;
        carriedCorruption = carriedCrp?.Raw;
        Corruption.Applied = carriedCrp?.Applied;
        Corruption.Problem = null;
        if (request?.Corruption?.Problem is { } damaged) Corruption.SetProblem($"The project's corruption record is {damaged}, so it was not used: the game is as the game-state section (if the project has one) or power-on left it");
        int console = Console;
        string? path = romPaths[console];
        string name = RomDisplayName;
        int chipIdx = Chip;
        var chip = Cx.Chips[chipIdx];
        // the Test MIDI export's song (Emulator.Song.cs): the template with the song in it, or a project's song that the loading thread converts first
        var songNow = console == Cx.Nes && songActive ? song : null;
        var songFromProject = console == Cx.Nes && songActive && songNow == null ? songPending : null;
        if (songNow != null || songFromProject != null) Interlocked.Exchange(ref standby, null);
        if (path == null && console != Cx.Nes)
        {
            Interlocked.Exchange(ref pendingGame, null);
            Volatile.Write(ref unloadRequested, 1);
            loading = false;
            romStatus = $"No {Cx.RomNames[console]} game loaded: choose one in the Game list";
            return;
        }
        // a project's game: it starts frozen when the project was saved in Instrument Runaway (the game must never run before its state is in place), and a save made before the game is built writes the project's state back as it came
        if (request?.Section is { } carried && carried.Console == console)
        {
            carriedSection = carried.Raw;
            if (carried.Frozen) Volatile.Write(ref runaway, 1);
        }
        loading = true;
        romStatus = "loading " + name + " ...";
        Task.Run(() =>
        {
            IGame? g = null;
            try
            {
                int rate = EmulatorHub.HostRate;
                if (songNow != null || songFromProject != null)
                {
                    var held = songNow ?? HeldForLoad(songFromProject!);
                    g = new NesGame(held.Rom, chip, rate) { IsSongTest = true };
                }
                else if (console == Cx.Nes) g = new NesGame(path == null ? BuiltInRom() : RomDetect.Unwrap(File.ReadAllBytes(path), path, out _), chip, rate);
                else
                {
                    var rom = RomDetect.Unwrap(File.ReadAllBytes(path!), path!, out string inner);
                    // a Master System console runs Master System, SG-1000 and Game Gear files: which one is in the file itself
                    var kind = RomDetect.Detect(rom, inner) ?? NesEmulator.Sega.SegaRomFormat.Detect(rom, inner) ?? Cx.Kind(console);
                    g = new SessionGame(rom, kind, chip, rate, Path.GetFileName(path!));
                }
                g.ChipIndex = chipIdx;
                if (request != null && gen == Volatile.Read(ref loadGeneration))
                {
                    // a project that carried a started corruption: when the machine does not come back with a saved state (the SNES), the game is rebuilt by replaying its history
                    if (g is SessionGame fresh && RebuildFromHistory(fresh, request, chip, chipIdx) is { } rebuilt)
                    {
                        g.Dispose();
                        g = rebuilt;
                        Volatile.Write(ref stateRestore, 1);
                        DropInjectRestore("the game was rebuilt by replaying its history, which does not include them");
                    }
                    else ApplyRestore(g, request);
                }
                if (gen != Volatile.Read(ref loadGeneration)) { g.Dispose(); return; }
                Interlocked.Exchange(ref pendingGame, g);
                romStatus = "running " + (g is NesGame { IsSongTest: true } && song is { } playing ? "MIDI test: " + playing.FileName : name);
                if (g is NesGame { IsSongTest: true }) { WantStandby(); }          // the copy a restart (Play, Restart, FL's transport) swaps in
            }
            catch (Exception e)
            {
                g?.Dispose();
                if (gen == Volatile.Read(ref loadGeneration)) romStatus = "could not load " + name + ": " + e.Message;
                BrokenNes.Fruity.Diag.Log("game load failed: " + e);
            }
            finally
            {
                if (gen == Volatile.Read(ref loadGeneration)) loading = false;
            }
        });
    }

    private void ResetOutputs()
    {
        Interlocked.Increment(ref epoch);
        ClearFrame();
    }

    private void DisposeRom()
    {
        Interlocked.Increment(ref loadGeneration);
        lock (romLock)
        {
            game?.Dispose();
            game = null;
            Interlocked.Exchange(ref pendingGame, null)?.Dispose();
            Array.Clear(stems);
        }
    }

    // ---- the audio side ----

    /// <summary>Takes a freshly built game, an unload, a changed sample rate and a changed sound chip. Runs under romLock, between frames.</summary>
    private void ApplyPending()
    {
        if (Interlocked.Exchange(ref unloadRequested, 0) != 0)
        {
            NotePushesLost(game, null);
            game?.Dispose();
            game = null;
            Array.Clear(stems);
            Interlocked.Increment(ref epoch);
        }
        var fresh = Interlocked.Exchange(ref pendingGame, null);
        if (fresh != null)
        {
            if (fresh.Console != Console) fresh.Dispose();        // the console changed while it loaded: stale
            else
            {
                NotePushesLost(game, fresh);
                game?.Dispose();
                game = fresh;
                carriedSection = null;                // the running game is the project's state now
                carriedCorruption = null;
                Array.Clear(stems);
                appliedChip = fresh.ChipIndex;
                crashReported = false;
                Interlocked.Increment(ref epoch);
            }
        }
        ApplySongRestart();                   // Play / Restart / FL's transport on the Test MIDI export's song (Emulator.Song.cs)
        var g = game;
        if (g == null) return;

        int rate = EmulatorHub.HostRate;
        if (g.HostSampleRate != rate)
        {
            g.HostSampleRate = rate;
            Array.Clear(stems);
            Interlocked.Increment(ref epoch);
        }

        int chipNow = Chip;
        if (chipNow == appliedChip) return;
        appliedChip = chipNow;
        var chip = Cx.Chips[chipNow];
        if (chip.Console != g.Console) return;                    // a console change: the game for it is on its way
        try
        {
            if (!g.TrySetChip(chip)) StartLoad();                 // this game has to start again on the new sound unit
            else foreach (var s in stems) s?.SetCore(chip.Id);
        }
        catch (Exception e) { BrokenNes.Fruity.Diag.Log($"sound chip switch to {chip.Id} failed: {e.Message}"); }
    }

    /// <summary>Runs one frame of the game and distributes its sound to the channel rings. Under romLock.</summary>
    private void Produce(IGame g)
    {
        RecordService(g);                     // a recording begins or ends between two frames (Emulator.Record.cs; allocation-free)
        if (Runaway || SongHolds(g))
        {
            // frozen (or the Test MIDI export's song stopped): the game does not run, the picture stays, and every channel is silent for as long as a frame would have lasted
            int silent = Math.Clamp((int)Math.Round(g.HostSampleRate / g.FramesPerSecond), 1, zeros.Length);
            foreach (var r in rings) r?.Append(zeros.AsSpan(0, silent));
            return;
        }
        g.SetPad(PadBits);
        var nes = g.Nes;
        int claimedNow = ClaimedChannels;
        var gs = g.Stems;
        gs?.Want(claimedNow);
        int wanted = nes != null ? ClaimedTones : 0;
        if (nes != null) nes.CaptureApuWrites = wanted != 0;
        int rate = g.HostSampleRate;
        string chipId = ChipId;
        for (int c = 0; c < Ch.ToneCount; c++)
        {
            bool want = (wanted & (1 << c)) != 0;
            if (want && stems[c] == null)
            {
                try
                {
                    uint written = nes!.CopyApuLatch(latch);
                    stems[c] = new ApuStem(chipId, c, rate, nes.ApuClock, latch, written);
                    RingOf(c).Origin = RingOf(c).Write;
                }
                catch (Exception e) { BrokenNes.Fruity.Diag.Log($"stem for {Ch.ShortNames[c]} failed: {e.Message}"); }
            }
            else if (!want) stems[c] = null;
        }

        g.RunFrame();
        CaptureFrame(g);
        RecordClock(g);

        int n;
        while ((n = g.ReadAvailable(scratch)) > 0) RingOf(Ch.Mix).Append(scratch.AsSpan(0, n));
        if (nes == null)
        {
            if (gs == null) return;
            // a Sega game: each voice somebody sits on has a stream from the sound unit, frame for frame with the mix
            for (int c = 0; c < Ch.Total; c++)
            {
                if (c == Ch.Mix || c == Ch.Sampler) continue;
                bool want = (claimedNow >> c & 1) != 0 && gs.Has(c);
                if (want && !stemLive[c]) { var ring = RingOf(c); ring.Origin = ring.Write; stemLive[c] = true; }
                else if (!want) { stemLive[c] = false; continue; }
                while ((n = gs.Read(c, scratch)) > 0) RingOf(c).Append(scratch.AsSpan(0, n));
            }
            return;
        }
        for (int c = 0; c < Ch.ToneCount; c++)
        {
            var s = stems[c];
            if (s == null) continue;
            s.Replay(nes.ApuLog, nes.ApuClock);
            while ((n = s.ReadAvailable(scratch)) > 0) RingOf(c).Append(scratch.AsSpan(0, n));
        }
    }

    /// <summary>Whether the running game's sound unit has a voice for this channel (Mix always; a NES game's tone channels; a Sega game's stems). False with no game.</summary>
    public bool StemAvailable(int channel)
    {
        if (channel == Ch.Mix) return game != null;
        var g = game;
        return g != null && (g.Nes != null ? channel < Ch.ToneCount : g.Stems?.Has(channel) == true);
    }

    /// <summary>Test hook: a tone on the running SNES game's sound unit.</summary>
    internal void PokeSnesToneForTests()
    {
        lock (romLock) { ApplyPending(); (game as SessionGame)?.PokeSnesTone(); }
    }

    // ---- test hooks and the corruption hook: the game's memory and exact frame stepping, under the emulator's lock ----

    /// <summary>Runs exactly <paramref name="frames"/> frames of the game, as if audio were pulled that long (nothing listens: the rings fill and readers skip over them). Returns the game's frame count, -1 with no game. A frozen game does not run.</summary>
    internal long RunFramesForTests(int frames)
    {
        lock (romLock)
        {
            ApplyPending();
            var g = game;
            if (g == null) return -1;
            for (int i = 0; i < frames && !g.Crashed; i++) Produce(g);
            return g.FramesRun;
        }
    }

    /// <summary>The byte at <paramref name="offset"/> of the running game's main work RAM (<see cref="IGame.PeekWorkRam"/>); -1 outside it or with no game.</summary>
    public int PeekWorkRam(int offset)
    {
        lock (romLock) { ApplyPending(); return game?.PeekWorkRam(offset) ?? -1; }
    }

    /// <summary>Writes a byte into the running game's main work RAM, between frames, whether the game runs or is frozen: this is the hook for corrupting a game (the change is part of the state a project keeps). False outside the RAM or with no game.</summary>
    public bool PokeWorkRam(int offset, byte value)
    {
        lock (romLock) { ApplyPending(); return game?.PokeWorkRam(offset, value) ?? false; }
    }

    /// <summary>1 when the running game can keep its state in a project, 0 when it cannot, -1 with no game.</summary>
    internal int GameStateSupportedForTests()
    {
        lock (romLock) { ApplyPending(); return game == null ? -1 : game.StateSupported ? 1 : 0; }
    }

    /// <summary>A hash of the whole machine as <see cref="IGame.SaveGameState"/> writes it (0: no game, or a game with no state): two machines in the same state hash alike.</summary>
    internal long GameStateHashForTests()
    {
        lock (romLock)
        {
            ApplyPending();
            var g = game;
            if (g == null || !g.StateSupported) return 0;
            ulong h = 1469598103934665603UL;
            foreach (byte b in g is NesGame nes ? nes.SaveComparableState() : g.SaveGameState()) h = (h ^ b) * 1099511628211UL;
            return (long)(h | 1);
        }
    }

    /// <summary>For an open editor (GUI thread): when no instance is pulling audio (FL stopped, the plugin idle) the picture still has to
    /// move, so the game is advanced here in real time. Its sound goes into the rings, which a returning reader skips over. Several
    /// editors on one emulator share one clock, so the game never runs faster than real time.</summary>
    /// <summary>Tests only (hook 120): the editor does not run the game to keep the picture moving, so a test that counts frames sees only the frames it asked for.</summary>
    internal static volatile bool NoPumpForTests;

    public void PumpPicture()
    {
        if (Mode != 1 || NoPumpForTests) return;
        long now = Environment.TickCount64;
        if (now - Volatile.Read(ref lastPullTick) < 250) { lastPumpTick = now; return; }
        if (!Monitor.TryEnter(romLock)) return;
        try
        {
            ApplyPending();
            var g = game;
            if (g == null || g.Crashed || lastPumpTick == 0) { lastPumpTick = now; return; }
            double fps = g.FramesPerSecond;
            int frames = (int)Math.Min(4, (now - lastPumpTick) * fps / 1000.0);
            if (frames <= 0) return;
            lastPumpTick += (long)(frames * 1000.0 / fps);
            for (int i = 0; i < frames; i++) Produce(g);
        }
        finally { Monitor.Exit(romLock); }
    }

    /// <summary>Gives an instance the next <c>dest.Length</c> samples of its channel, running the game as far as needed. An instance
    /// keeps its own read position and the emulator's epoch; a new position or a changed epoch starts from "now". Silence when there
    /// is no game (still loading, none chosen, crashed).</summary>
    public void PullRom(int channel, ref long readPos, ref int epochSeen, Span<float> dest)
    {
        Volatile.Write(ref lastPullTick, Environment.TickCount64);
        lock (romLock)
        {
            ApplyPending();
            var g = game;
            if (g == null) { dest.Clear(); return; }
            if (g.Crashed)
            {
                if (!crashReported) { crashReported = true; romStatus = "the game crashed: " + g.CrashInfo; }
                dest.Clear();
                return;
            }

            // a channel the game's sound unit has no voice for (an FM voice of a Game Gear game, say) is silent: it must never make the game run ahead looking for sound that will not come
            if (channel != Ch.Mix && !StemAvailable(channel)) { dest.Clear(); return; }
            var ring = RingOf(channel);
            int e = Volatile.Read(ref epoch);
            if (ringEpoch != e)
            {
                foreach (var r in rings) if (r != null) r.Origin = r.Write;      // everything that was buffered belongs to the old epoch
                ringEpoch = e;
            }
            if (readPos < 0 || e != epochSeen)
            {
                // readers that start together (a project starting to play) start at the same point of the game's sound; a late reader takes the newest
                readPos = ring.Write - ring.Origin <= AlignWindow ? ring.Origin : Math.Max(ring.Origin, ring.Write - dest.Length);
                epochSeen = e;
            }

            int frames = 0;
            while (ring.Write - readPos < dest.Length)
            {
                if (++frames > 12 || g.Crashed) { dest.Clear(); return; }
                Produce(g);
            }
            if (ring.Write - readPos > Ring.Size / 2) readPos = ring.Write - dest.Length - Ring.Size / 8;   // this reader fell far behind: skip ahead
            ring.Read(readPos, dest);
            readPos += dest.Length;
        }
    }
}
