// Round 9 (capture): one BrokenNes2 instance's part of a capture: every note FL starts on it, every change of the pitch it plays, every release, on a clock of its own made of the samples FL asked it
// to render since FL's transport started (FL does not tell a plugin its song position: the user's real FL log has "song position none" on every note). Preallocated: the audio thread writes into
// chunks a worker made before they were needed and allocates nothing; an event that finds no chunk ready is counted, never lost without a word.
//
// Threads: FL may call TriggerVoice / Voice_Release / FPD_SetPlaying from its mixer thread or another one; every write and the clock are under the lane's own lock (uncontended in practice: FL's
// mixer thread does all of it). The worker reads a lane only after it stopped (Snapshot).
namespace BrokenNes2.Capture;

/// <summary>What an instance is, for its track: its role (which NES channel VRUN's converter should give it), the track's name, and where it sat.</summary>
public enum CapRole { Pulse1, Pulse2, Triangle, Noise, Dmc, Other }

public sealed record LaneIdentity(int InstanceNo, CapRole Role, string Name, string Detail, int EmulatorId)
{
    public static string RoleName(CapRole r) => r switch { CapRole.Pulse1 => "Pulse 1", CapRole.Pulse2 => "Pulse 2", CapRole.Triangle => "Triangle", CapRole.Noise => "Noise", CapRole.Dmc => "DMC", _ => "Other" };
}

/// <summary>Event kinds (the low 4 bits of a code; the voice's id is above them).</summary>
public static class CapEv
{
    /// <summary>A note FL started: A = InitLevels.Pitch (cents from MIDI 0), B = the pitch played (FinalLevels.Pitch + the instance's tuning, cents from C5), C = FL's velocity (0..1, FPV_GetVelocity).
    /// (<see cref="OnLevelVelocity"/> is the same with C = the voice's InitLevels.Vol, when FL gave no velocity.)</summary>
    public const int On = 1;
    /// <summary>The pitch a sounding voice plays changed: A = cents from C5 (FinalLevels.Pitch + tuning).</summary>
    public const int Pitch = 2;
    /// <summary>FL released the voice (or killed it before releasing it).</summary>
    public const int Off = 3;
    /// <summary>The instance's tuning (Coarse x 100 + Fine) at a note-on: A = cents.</summary>
    public const int Tuning = 4;
    /// <summary>FL said its song position changed (FPD_SongPosChanged) while the take ran.</summary>
    public const int SongPos = 5;
    /// <summary>A note-on whose velocity FL did not give (FPV_GetVelocity answered nothing usable): C is the voice's InitLevels.Vol.</summary>
    public const int OnLevelVelocity = 6;

    public static int Code(int kind, int voice) => (voice & 0x07FFFFFF) << 4 | kind;
    public static int Kind(int code) => code & 0xF;
    public static int Voice(int code) => (int)((uint)code >> 4);
}

/// <summary>A lane's events once it stopped (the builder's input; pure arrays).</summary>
public sealed class LaneData
{
    public required LaneIdentity Identity;
    public required double[] T;
    public required int[] Code;
    public required float[] A, B, C;
    public int Count;
    /// <summary>Events that found no chunk ready (counted, not in the arrays).</summary>
    public long Lost;
    public bool Started, Stopped, JoinedLate;
    /// <summary>The lane's clock when FL stopped (seconds since FL's Play).</summary>
    public double StopSeconds;
    /// <summary>Blocks FL rendered on this instance during the take, and the rates it rendered them at.</summary>
    public long Blocks;
    public int RateChanges;
}

public sealed class CaptureLane
{
    public const int ChunkEvents = 1 << 14;
    /// <summary>The cap: 64 chunks of 16,384 events (a million events, 25 MB; a song with slides on every note makes a few thousand a minute).</summary>
    public const int MaxChunks = 64;
    public const int FirstChunks = 2, SpareChunks = 2;

    private sealed class Chunk
    {
        public readonly double[] T = new double[ChunkEvents];
        public readonly int[] Code = new int[ChunkEvents];
        public readonly float[] A = new float[ChunkEvents], B = new float[ChunkEvents], C = new float[ChunkEvents];
    }

    private readonly Chunk?[] chunks = new Chunk?[MaxChunks];
    private int ready;                 // chunks[0 .. ready-1] exist (the worker publishes with a volatile write)
    private int chunk, fill;           // the chunk being filled, how many events it holds
    private long lost, blocks;
    private int pitchSeen;             // (the deliberate bug that loses events silently)
    private readonly object gate = new();
    private double seconds, stopSeconds;
    private int state;                 // 0 waiting for FL's Play, 1 running, 2 stopped
    private int lastRate, rateChanges;
    private bool joinedLate, started;

    public LaneIdentity Identity { get; set; }

    public CaptureLane(LaneIdentity identity)
    {
        Identity = identity;
        for (int i = 0; i < FirstChunks; i++) chunks[i] = new Chunk();
        Volatile.Write(ref ready, FirstChunks);
    }

    /// <summary>0 waiting for FL's Play, 1 running, 2 stopped.</summary>
    public int State => Volatile.Read(ref state);
    public bool Running => Volatile.Read(ref state) == 1;
    /// <summary>The lane's clock: seconds since FL's Play (the samples rendered since, at the rate they were rendered at).</summary>
    public double Seconds { get { lock (gate) return seconds; } }
    public long Lost => Interlocked.Read(ref lost);
    public int Count { get { lock (gate) return chunk * ChunkEvents + fill; } }
    public long Bytes => (long)Volatile.Read(ref ready) * ChunkEvents * 24;

    /// <summary>The worker (never the audio thread): keeps <see cref="SpareChunks"/> chunks ahead of the one being filled, up to the cap.</summary>
    public void Feed()
    {
        int r = Volatile.Read(ref ready);
        while (r < MaxChunks && r - Volatile.Read(ref chunk) <= SpareChunks)
        {
            chunks[r] = new Chunk();
            Volatile.Write(ref ready, ++r);
        }
    }

    /// <summary>FL's transport started (FPD_SetPlaying 1): the clock starts at 0. <paramref name="at"/> > 0: the lane joins a take already running at that clock (it had no Play of its own).</summary>
    public void Start(double at = 0, bool late = false)
    {
        lock (gate)
        {
            if (state != 0) return;
            seconds = at;
            joinedLate = late;
            started = true;
            state = 1;
        }
    }

    /// <summary>FL's transport stopped (FPD_SetPlaying 0): the take ends at the clock's time.</summary>
    public void Stop()
    {
        lock (gate)
        {
            if (state != 1) { if (state == 0) state = 2; return; }
            stopSeconds = seconds;
            state = 2;
        }
    }

    /// <summary>A block was rendered (the end of Gen_Render): the clock moves on by its length at FL's rate.</summary>
    public void Advance(int length, int rate)
    {
        lock (gate)
        {
            if (state != 1 || rate <= 0) return;
            if (CaptureMutants.On(CaptureMutants.ClockFixedRate)) rate = 44100;          // (the deliberate bug)
            if (lastRate != 0 && rate != lastRate) rateChanges++;
            lastRate = rate;
            seconds += (double)length / rate;
            blocks++;
        }
    }

    /// <summary>One event at the clock's time now (any FL thread, the audio thread included; nothing allocated).</summary>
    public void Add(int kind, int voice, float a, float b = 0, float c = 0)
    {
        lock (gate)
        {
            if (state != 1) return;
            if (kind == CapEv.Pitch)
            {
                if (CaptureMutants.On(CaptureMutants.SlideIgnored)) return;                  // (the deliberate bug: only the pitch a note starts with)
                if (CaptureMutants.On(CaptureMutants.EventsLostSilently) && ++pitchSeen % 40 == 0) return;    // (the deliberate bug: lost without a word)
            }
            if (CaptureMutants.On(CaptureMutants.CaptureAllocates)) GC.KeepAlive(new float[8]);   // (the deliberate bug: an allocation per event)
            if (fill == ChunkEvents)
            {
                int next = chunk + 1;
                if (next >= MaxChunks || next >= Volatile.Read(ref ready)) { lost++; return; }   // the cap, or the worker is late: counted (the window says so)
                Volatile.Write(ref chunk, next);
                fill = 0;
            }
            var ch = chunks[chunk]!;
            ch.T[fill] = seconds; ch.Code[fill] = CapEv.Code(kind, voice); ch.A[fill] = a; ch.B[fill] = b; ch.C[fill] = c;
            fill++;
        }
    }

    /// <summary>The lane's events as arrays (the worker, after the lane stopped; allocates).</summary>
    public LaneData Snapshot()
    {
        lock (gate)
        {
            int n = chunk * ChunkEvents + fill;
            var d = new LaneData
            {
                Identity = Identity, T = new double[n], Code = new int[n], A = new float[n], B = new float[n], C = new float[n], Count = n, Lost = lost,
                Started = started, Stopped = state == 2, JoinedLate = joinedLate, StopSeconds = state == 2 ? stopSeconds : seconds, Blocks = blocks, RateChanges = rateChanges,
            };
            for (int k = 0; k <= chunk && k < MaxChunks; k++)
            {
                var c = chunks[k];
                if (c == null) break;
                int m = k < chunk ? ChunkEvents : fill, at = k * ChunkEvents;
                Array.Copy(c.T, 0, d.T, at, m); Array.Copy(c.Code, 0, d.Code, at, m);
                Array.Copy(c.A, 0, d.A, at, m); Array.Copy(c.B, 0, d.B, at, m); Array.Copy(c.C, 0, d.C, at, m);
            }
            return d;
        }
    }
}
