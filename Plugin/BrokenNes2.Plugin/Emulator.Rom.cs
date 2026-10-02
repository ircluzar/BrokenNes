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
    private readonly Ring[] rings = Enumerable.Range(0, Ch.Count).Select(_ => new Ring()).ToArray();
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
    private void StartLoad()
    {
        int gen = Interlocked.Increment(ref loadGeneration);
        ClearRunaway();                       // another game: whatever was frozen is gone
        int console = Console;
        string? path = romPaths[console];
        string name = RomDisplayName;
        int chipIdx = Chip;
        var chip = Cx.Chips[chipIdx];
        if (path == null && console != Cx.Nes)
        {
            Interlocked.Exchange(ref pendingGame, null);
            Volatile.Write(ref unloadRequested, 1);
            loading = false;
            romStatus = $"No {Cx.Names[console]} game loaded: choose one in the Game list";
            return;
        }
        loading = true;
        romStatus = "loading " + name + " ...";
        Task.Run(() =>
        {
            IGame? g = null;
            try
            {
                int rate = EmulatorHub.HostRate;
                if (console == Cx.Nes) g = new NesGame(path == null ? BuiltInRom() : RomDetect.Unwrap(File.ReadAllBytes(path), path, out _), chip, rate);
                else
                {
                    var rom = RomDetect.Unwrap(File.ReadAllBytes(path!), path!, out string inner);
                    var kind = RomDetect.Detect(rom, inner) ?? Cx.Kind(console);
                    g = new SessionGame(rom, kind, chip, rate);
                }
                g.ChipIndex = chipIdx;
                if (gen != Volatile.Read(ref loadGeneration)) { g.Dispose(); return; }
                Interlocked.Exchange(ref pendingGame, g);
                romStatus = "running " + name;
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
                game?.Dispose();
                game = fresh;
                Array.Clear(stems);
                appliedChip = fresh.ChipIndex;
                crashReported = false;
                Interlocked.Increment(ref epoch);
            }
        }
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
        if (Runaway)
        {
            // frozen: the game does not run, the picture stays, and every channel is silent for as long as a frame would have lasted
            int silent = Math.Clamp((int)Math.Round(g.HostSampleRate / g.FramesPerSecond), 1, zeros.Length);
            foreach (var r in rings) r.Append(zeros.AsSpan(0, silent));
            return;
        }
        g.SetPad(PadBits);
        var nes = g.Nes;
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
                    rings[c].Origin = rings[c].Write;
                }
                catch (Exception e) { BrokenNes.Fruity.Diag.Log($"stem for {Ch.ShortNames[c]} failed: {e.Message}"); }
            }
            else if (!want) stems[c] = null;
        }

        g.RunFrame();
        CaptureFrame(g);

        int n;
        while ((n = g.ReadAvailable(scratch)) > 0) rings[Ch.Mix].Append(scratch.AsSpan(0, n));
        if (nes == null) return;
        for (int c = 0; c < Ch.ToneCount; c++)
        {
            var s = stems[c];
            if (s == null) continue;
            s.Replay(nes.ApuLog, nes.ApuClock);
            while ((n = s.ReadAvailable(scratch)) > 0) rings[c].Append(scratch.AsSpan(0, n));
        }
    }

    /// <summary>Test hook: a tone on the running SNES game's sound unit.</summary>
    internal void PokeSnesToneForTests()
    {
        lock (romLock) { ApplyPending(); (game as SessionGame)?.PokeSnesTone(); }
    }

    /// <summary>For an open editor (GUI thread): when no instance is pulling audio (FL stopped, the plugin idle) the picture still has to
    /// move, so the game is advanced here in real time. Its sound goes into the rings, which a returning reader skips over. Several
    /// editors on one emulator share one clock, so the game never runs faster than real time.</summary>
    public void PumpPicture()
    {
        if (Mode != 1) return;
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

            var ring = rings[channel];
            int e = Volatile.Read(ref epoch);
            if (ringEpoch != e)
            {
                foreach (var r in rings) r.Origin = r.Write;      // everything that was buffered belongs to the old epoch
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
