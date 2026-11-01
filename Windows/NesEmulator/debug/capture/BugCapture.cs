using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NesEmulator.Corruption;
using NesEmulator.Debugging.Movies;
using NesEmulator.Systems;

namespace NesEmulator.Debugging.Capture;

/// <summary>
/// What the bug capture reads of the running game (milestone D6 of docs/projects/brokendev-debugging-plan.md): its save state, its picture
/// and the hashes a movie's keyframes hold (<see cref="MovieHash"/>, the same ones Workshop's replay checks). One per game; the hosts (the
/// desktop app, Lite, the Workshop tests) make it with <see cref="For(NES)"/> or <see cref="For(IConsoleSession)"/>. Every call is made
/// between frames, on the thread that runs them.
/// </summary>
public sealed class BugCaptureSource
{
    /// <summary>The movie's console key (nes, snes, wms, gb, gbc, sms, gg, md).</summary>
    public string ConsoleKey { get; }
    /// <summary>A state of the machine as the movie format stores it (the NES: its JSON state as UTF-8; a session: its state), or null when it has none.</summary>
    public Func<byte[]>? SaveState { get; }
    /// <summary>Why <see cref="SaveState"/> is null, in words for the bundle.</summary>
    public string NoStateWhy { get; }
    private readonly Func<ulong> pictureHash, ramHash;
    private readonly Action<BugPicture> copyPicture;
    /// <summary>
    /// What the machine runs as (the cores, the NES's frame timing), read once a second: when it changes (a core swapped while the game
    /// runs, the timing switched) the frames before cannot replay into the ones after, so the history starts again. Null: not watched.
    /// </summary>
    public Func<string>? Machine { get; }

    public BugCaptureSource(string consoleKey, Func<byte[]>? saveState, string noStateWhy, Func<ulong> pictureHash, Func<ulong> ramHash, Action<BugPicture> copyPicture, Func<string>? machine = null)
    {
        ConsoleKey = consoleKey; SaveState = saveState; NoStateWhy = noStateWhy;
        this.pictureHash = pictureHash; this.ramHash = ramHash; this.copyPicture = copyPicture; Machine = machine;
    }

    public ulong PictureHash() => pictureHash();
    public ulong RamHash() => ramHash();
    public void CopyPicture(BugPicture into) => copyPicture(into);

    /// <summary>The NES: its JSON state (as Workshop's movie machine loads it), its RGBA picture, its 2 KB of work RAM; its cores and timing watched.</summary>
    public static BugCaptureSource For(NES nes)
    {
        IReadOnlyList<IMemoryDomain> domains = MachineDomains.For(nes);
        return new BugCaptureSource("nes",
            () => Encoding.UTF8.GetBytes(nes.SaveState()), "",
            () => MovieHash.Picture(nes.GetFrameBuffer()),
            () => MovieHash.Ram(domains),
            p => p.FromRgba(nes.GetFrameBuffer(), 256, 240),
            () => $"cores {NesCores(nes)}, timing {(NesNtsc(nes) ? "ntsc" : "60")}");
    }

    /// <summary>The NES's cores as a movie names them ("FIX/FIX/FIX": the ids SetCpuCore takes, not the getters' "CPU_FIX").</summary>
    public static string NesCores(NES nes)
    {
        static string Id(string full, string prefix) => full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full[prefix.Length..] : full;
        return $"{Id(nes.GetCpuCoreId(), "CPU_")}/{Id(nes.GetPpuCoreId(), "PPU_")}/{Id(nes.GetApuCoreId(), "APU_")}";
    }

    /// <summary>Whether the NES runs NTSC's frame length (60.0988) rather than BrokenNes's 60 fps frames.</summary>
    public static bool NesNtsc(NES nes) => nes.GetSpeedConfig()?.NtscAccurateFrameRate == true;

    /// <summary>A console session: its own state (or why it has none), its ARGB picture, its work-RAM domains; <paramref name="machine"/>: its cores, watched.</summary>
    public static BugCaptureSource For(IConsoleSession s, Func<string>? machine = null)
    {
        IReadOnlyList<IMemoryDomain> domains = MachineDomains.For(s);
        bool states = s.SupportsState;
        return new BugCaptureSource(Consoles.Key(s.Console),
            states ? s.SaveState : null, states ? "" : s.StateUnsupportedReason,
            () => MovieHash.Picture(s.Frame, s.FrameWidth * s.FrameHeight),
            () => MovieHash.Ram(domains),
            p => p.FromArgb(s.Frame, s.FrameWidth, s.FrameHeight), machine);
    }
}

/// <summary>A picture kept by the capture: 0xAARRGGBB pixels in a buffer that is reused (made again only when the size grows).</summary>
public sealed class BugPicture
{
    public uint[] Pixels = Array.Empty<uint>();
    public int Width, Height;
    /// <summary>Frames run when it was taken (the picture of the frame before that one).</summary>
    public long Frame;

    private void Size(int w, int h)
    {
        Width = w; Height = h;
        if (Pixels.Length < w * h) Pixels = new uint[w * h];
    }

    /// <summary>The NES framebuffer: R, G, B, A bytes per pixel.</summary>
    public void FromRgba(byte[] rgba, int w, int h)
    {
        if (rgba.Length < w * h * 4) { Size(0, 0); return; }
        Size(w, h);
        for (int i = 0, j = 0; i < w * h; i++, j += 4)
            Pixels[i] = 0xFF000000u | ((uint)rgba[j] << 16) | ((uint)rgba[j + 1] << 8) | rgba[j + 2];
    }

    public void FromArgb(uint[] argb, int w, int h)
    {
        if (w <= 0 || h <= 0 || argb.Length < w * h) { Size(0, 0); return; }
        Size(w, h);
        Array.Copy(argb, Pixels, w * h);
    }

    public BugPicture Clone()
    {
        var c = new BugPicture { Width = Width, Height = Height, Frame = Frame, Pixels = new uint[Width * Height] };
        Array.Copy(Pixels, c.Pixels, Width * Height);
        return c;
    }
}

/// <summary>
/// The bug capture's rolling buffer (milestone D6): the last <see cref="Seconds"/> seconds of a game, kept cheaply so that pressing the key
/// can save them. Every <see cref="Every"/> frames (one second at 60 frames a second) it keeps a save state, a picture and the keyframe hashes;
/// every frame the two pads' buttons (two bytes each); every sound sample (mono floats, what the player hears). All of it lives in buffers made
/// once (the pads, the sound, the pictures; the states are the machine's own byte arrays, one a second): the frames in between allocate nothing.
///
/// <para>The host calls <see cref="Frame"/> before each frame with the pads the game is about to be handed, <see cref="Audio"/> with the sound
/// it queued, <see cref="Break"/> when something outside the game changed it (a state load, a reset, a core swap, a memory write, a
/// corruption: the movie could not replay across it, so the history starts again after it), and <see cref="Capture"/> between frames.
/// Nothing runs unless a host makes one: the emulator never knows.</para>
/// </summary>
public sealed class BugCaptureRing
{
    /// <summary>The seconds a capture holds (the plan's decision: the last 30 s).</summary>
    public const int Seconds = 30;

    private sealed class Slot
    {
        public long Frame = -1;          // frames run when taken (the state is the start of that frame)
        public byte[]? State;
        public ulong Picture, Ram;       // keyframe hashes after Frame frames
        public long AudioPos;            // sound samples queued before it
        public readonly BugPicture Shot = new();
    }

    private BugCaptureSource? src;
    private readonly Slot[] slots;
    private int slotCount, slotHead;     // slotHead: the index of the oldest
    private ushort[] pads = Array.Empty<ushort>();   // P1, P2 per frame, frame f at (f % padFrames) * 2
    private int padFrames;
    private float[] audio = Array.Empty<float>();
    private long audioPos;
    private string? machineNow;          // the source's Machine when the last slot was taken
    private long frame;                  // frames run since the history began
    private long historyStart;           // the first frame the history holds input for
    private bool powerOnStart;           // the history began at the game's power-on (frame 0 of a fresh game)

    /// <summary>Frames between states (and pictures, and keyframes).</summary>
    public int Every { get; }
    public int SampleRate { get; private set; }
    public double FramesPerSecond { get; private set; } = 60;
    public bool Attached => src != null;
    public string ConsoleKey => src?.ConsoleKey ?? "";
    /// <summary>Frames run since the game was attached (the history's own count).</summary>
    public long FrameCount => frame;
    /// <summary>Why the history last started again, and at which frame ("" when it holds everything since the game started).</summary>
    public string BreakReason { get; private set; } = "";
    public long BreakFrame { get; private set; } = -1;
    /// <summary>Why states stopped being taken (the machine refused one), or "".</summary>
    public string StateError { get; private set; } = "";
    /// <summary>How long the last state took (ms), and the longest so far: the capture's cost, said by the status.</summary>
    public double LastStateMs { get; private set; }
    public double MaxStateMs { get; private set; }
    public int LastStateBytes { get; private set; }

    public BugCaptureRing(int every = 60)
    {
        Every = Math.Max(1, every);
        slots = new Slot[Seconds + 2];
        for (int i = 0; i < slots.Length; i++) slots[i] = new Slot();
    }

    /// <summary>
    /// A new game (or the same one started again): the history starts empty. <paramref name="powerOn"/>: the game has run no frame yet (its
    /// power-on), so a console without save states can still be replayed while the history reaches back to it.
    /// </summary>
    public void Attach(BugCaptureSource source, int sampleRate, double framesPerSecond, bool powerOn)
    {
        src = source;
        SampleRate = Math.Max(1, sampleRate);
        FramesPerSecond = framesPerSecond is > 20 and < 200 ? framesPerSecond : 60;
        int need = Every * (slots.Length + 1);
        if (pads.Length < need * 2) pads = new ushort[need * 2];
        padFrames = need;
        int samples = (int)(SampleRate * (Seconds + 4.0) * Math.Max(1.0, 60.0 / Every));
        if (audio.Length < samples) audio = new float[samples];
        frame = 0; audioPos = 0; StateError = ""; MaxStateMs = 0; LastStateMs = 0; machineNow = null;
        BreakReason = ""; BreakFrame = -1;
        Clear(0);
        powerOnStart = powerOn;
    }

    public void Detach() { src = null; slotCount = 0; }

    private void Clear(long from)
    {
        for (int i = 0; i < slots.Length; i++) { slots[i].Frame = -1; slots[i].State = null; }
        slotCount = 0; slotHead = 0;
        historyStart = from;
        powerOnStart = false;
    }

    /// <summary>
    /// Something outside the game changed it (<paramref name="why"/>: "a state was loaded", "the game was reset", ...): nothing before this
    /// point can be replayed into what follows, so the history starts again here (a state is taken before the next frame).
    /// </summary>
    public void Break(string why)
    {
        if (src == null) return;
        BreakReason = why; BreakFrame = frame;
        Clear(frame);
    }

    /// <summary>Before a frame runs (between frames, on the emulation thread): the pads the game is about to be handed.</summary>
    public void Frame(PadButtons p1, PadButtons p2)
    {
        var s = src;
        if (s == null) return;
        if (slotCount == 0 || frame - Newest.Frame >= Every)
        {
            if (s.Machine != null)
            {
                string m = s.Machine();
                if (machineNow != null && m != machineNow) Break($"the machine changed ({machineNow} to {m})");
                machineNow = m;
            }
            TakeSlot(s);
        }
        int i = (int)(frame % padFrames) * 2;
        pads[i] = (ushort)p1; pads[i + 1] = (ushort)p2;
        frame++;
    }

    /// <summary>The NES's pads as the board takes them (A, B, Select, Start, Up, Down, Left, Right).</summary>
    public void Frame(bool[]? p1, bool[]? p2) => Frame(FromNes(p1), FromNes(p2));

    // ---- notes: the debug port's markers, prints and asserts (D7), told by the host while a frame runs; the last 512 are kept
    private readonly (long Frame, string Kind, string Text)[] notes = new (long, string, string)[512];
    private long noteCount;

    /// <summary>A debug-port event of the frame running now (<paramref name="kind"/>: marker, print, assert, break, error). Kept with the
    /// frame, so a capture's bundle has the markers in its movie and the events in bug.json.</summary>
    public void Note(string kind, string text)
    {
        if (src == null) return;
        notes[noteCount % notes.Length] = (Math.Max(0, frame - 1), kind, text);
        noteCount++;
    }

    public static PadButtons FromNes(bool[]? b)
    {
        if (b == null || b.Length < 8) return PadButtons.None;
        var p = PadButtons.None;
        if (b[0]) p |= PadButtons.A; if (b[1]) p |= PadButtons.B; if (b[2]) p |= PadButtons.Select; if (b[3]) p |= PadButtons.Start;
        if (b[4]) p |= PadButtons.Up; if (b[5]) p |= PadButtons.Down; if (b[6]) p |= PadButtons.Left; if (b[7]) p |= PadButtons.Right;
        return p;
    }

    /// <summary>Sound the host queued after a frame (mono, <see cref="SampleRate"/>).</summary>
    public void Audio(float[] samples, int count)
    {
        if (src == null || samples == null) return;
        count = Math.Min(count, samples.Length);
        int n = audio.Length;
        if (count >= n) { Array.Copy(samples, count - n, audio, 0, n); audioPos += count; return; }
        int at = (int)(audioPos % n);
        int first = Math.Min(count, n - at);
        Array.Copy(samples, 0, audio, at, first);
        if (count > first) Array.Copy(samples, first, audio, 0, count - first);
        audioPos += count;
    }

    private Slot Newest => slots[(slotHead + slotCount - 1) % slots.Length];

    private void TakeSlot(BugCaptureSource s)
    {
        Slot slot;
        if (slotCount < slots.Length) { slot = slots[(slotHead + slotCount) % slots.Length]; slotCount++; }
        else { slot = slots[slotHead]; slotHead = (slotHead + 1) % slots.Length; }
        slot.Frame = frame;
        slot.AudioPos = audioPos;
        slot.State = null;
        if (s.SaveState != null && StateError.Length == 0)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try { slot.State = s.SaveState(); }
            catch (Exception e) when (e is NotSupportedException or InvalidOperationException or InvalidDataException) { StateError = $"no states after frame {frame}: {e.Message}"; }
            LastStateMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (LastStateMs > MaxStateMs) MaxStateMs = LastStateMs;
            LastStateBytes = slot.State?.Length ?? 0;
        }
        slot.Picture = s.PictureHash();
        slot.Ram = s.RamHash();
        s.CopyPicture(slot.Shot);
        slot.Shot.Frame = frame;
        // the history's input begins at the oldest slot kept
        var oldest = slots[slotHead];
        if (oldest.Frame > historyStart) { historyStart = oldest.Frame; powerOnStart = false; }
    }

    /// <summary>The seconds the history holds now (at most a little over <see cref="Seconds"/>).</summary>
    public double HeldSeconds => src == null ? 0 : (frame - historyStart) / FramesPerSecond;

    /// <summary>
    /// The capture (between frames, on the emulation thread): copies of the last <see cref="Seconds"/> seconds (or all the history holds):
    /// the movie's start (a state, or the power-on), every frame's pads, the keyframes, a picture a second, the sound, and the last picture
    /// with its hashes. The ring goes on as before; writing the files is <see cref="BugBundle"/>'s, on any thread.
    /// </summary>
    public BugCaptureData Capture()
    {
        var s = src ?? throw new InvalidOperationException("no game is running");
        if (frame == historyStart) throw new InvalidOperationException(BreakReason.Length > 0 ? $"nothing to capture yet: the history started again at this frame ({BreakReason})" : "nothing to capture yet: no frame has run");
        long want = frame - (long)Math.Round(Seconds * FramesPerSecond);
        // the start: the newest state at or before (now - 30 s), else the oldest state kept, else the power-on
        int startIdx = -1;
        for (int k = 0; k < slotCount; k++)
        {
            var sl = slots[(slotHead + k) % slots.Length];
            if (sl.State == null) continue;
            if (startIdx < 0 || sl.Frame <= want) startIdx = k;
            if (sl.Frame > want) break;
        }
        var d = new BugCaptureData { ConsoleKey = s.ConsoleKey, SampleRate = SampleRate, FramesPerSecond = FramesPerSecond, BreakReason = BreakReason, StateError = StateError };
        long start;
        if (startIdx >= 0)
        {
            var sl = slots[(slotHead + startIdx) % slots.Length];
            start = sl.Frame; d.Start = MovieStart.State; d.StartState = sl.State;
        }
        else if (powerOnStart && historyStart == 0)
        {
            // a console without save states, while the history still reaches its power-on: the movie starts there
            start = 0; d.Start = MovieStart.PowerOn;
        }
        else
        {
            // no state to start from: the pictures and the sound are kept, the movie cannot be
            start = slotCount > 0 ? slots[slotHead].Frame : historyStart;
            d.NoMovieWhy = s.SaveState == null ? $"the movie needs a save state to start from, and {Lower(s.NoStateWhy)}"
                         : StateError.Length > 0 ? $"the machine refused a save state ({StateError})" : "no save state was taken yet";
        }
        d.HistoryStartFrame = start;
        d.FrameAtCapture = frame;
        long n = frame - start;
        d.Frames = n;
        d.Pads = new ushort[n * 2];
        for (long f = start; f < frame; f++)
        {
            int i = (int)(f % padFrames) * 2;
            d.Pads[(f - start) * 2] = pads[i]; d.Pads[(f - start) * 2 + 1] = pads[i + 1];
        }
        long audioStart = 0;   // the power-on: the first sample
        for (int k = 0; k < slotCount; k++)
        {
            var sl = slots[(slotHead + k) % slots.Length];
            if (sl.Frame < start) continue;
            if (sl.Frame == start) audioStart = sl.AudioPos;
            if (sl.Frame > start) d.Keys.Add(new MovieKey { Frame = sl.Frame - start, Picture = sl.Picture, Ram = sl.Ram });
            if (sl.Shot.Width > 0) { var c = sl.Shot.Clone(); c.Frame = sl.Frame - start; d.Pictures.Add(c); }
        }
        // the debug port's events since the start (frames counted from it)
        for (long k = Math.Max(0, noteCount - notes.Length); k < noteCount; k++)
        {
            var (nf, kind, text) = notes[k % notes.Length];
            if (nf >= start && nf < frame) d.Notes.Add((nf - start, kind, text));
        }
        // the last keyframe and picture: now
        d.Keys.Add(new MovieKey { Frame = n, Picture = s.PictureHash(), Ram = s.RamHash() });
        var last = new BugPicture(); s.CopyPicture(last); last.Frame = n;
        d.Last = last;
        // the sound since the start (what the ring still holds of it)
        long have = Math.Min(audioPos - audioStart, audio.Length);
        if (have < 0) have = 0;
        d.Audio = new float[have];
        d.AudioMissingLead = Math.Max(0, audioPos - audioStart - have);
        long from = audioPos - have;
        for (long k = 0; k < have; k++) d.Audio[k] = audio[(from + k) % audio.Length];
        return d;
    }

    private static string Lower(string s) => string.IsNullOrEmpty(s) ? "this console has none" : char.ToLowerInvariant(s[0]) + s[1..].TrimEnd('.');
}

/// <summary>What <see cref="BugCaptureRing.Capture"/> copied out: everything a bundle is made of, owned by the caller.</summary>
public sealed class BugCaptureData
{
    public string ConsoleKey = "";
    public MovieStart Start;
    public byte[]? StartState;
    /// <summary>Why there is no replayable movie (no state to start from), or null.</summary>
    public string? NoMovieWhy;
    public long HistoryStartFrame, FrameAtCapture, Frames;
    /// <summary>Pad 1, pad 2 (<see cref="PadButtons"/>) for each frame of the capture.</summary>
    public ushort[] Pads = Array.Empty<ushort>();
    public readonly List<MovieKey> Keys = new();
    public readonly List<BugPicture> Pictures = new();
    /// <summary>The debug port's events of the capture (D7): the frame from the capture's start, the kind, the text.</summary>
    public readonly List<(long Frame, string Kind, string Text)> Notes = new();
    public BugPicture? Last;
    public float[] Audio = Array.Empty<float>();
    /// <summary>Samples at the start of the capture the sound buffer no longer held (0 normally): the sound file starts that late.</summary>
    public long AudioMissingLead;
    public int SampleRate;
    public double FramesPerSecond;
    public string BreakReason = "", StateError = "";
    public double Seconds => Frames / (FramesPerSecond > 0 ? FramesPerSecond : 60);
}
