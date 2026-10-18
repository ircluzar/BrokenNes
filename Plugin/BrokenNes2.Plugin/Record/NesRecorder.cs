// Round 8 (record-nes): a recording session of a NES game's music. EVERY write the game's CPU makes to the sound chip ($4000-$4017) from Record to Stop is kept with its APU clock (CPU cycles),
// together with the state the chip was in when the recording began (every counter: APU_FIX.ProbeControl, so the transcription's model of the chip starts exactly where the game's was), a mark at
// the end of every frame, and a copy of each DMC sample the game starts (its bytes live in the cartridge, which may switch banks later). The transcription (NesTranscribe.cs) turns the session
// into MIDI tracks and presets; the project keeps the session (NesSessionCodec), so a later build can transcribe it again.
//
// Real-time rules, as for the SNES capture (RecSession.cs): the capture runs on FL's audio thread inside the game's frame; nothing is allocated there (writes go into chunks made beforehand by
// a feeder thread, DMC copies into buffers made the same way); nothing is dropped silently (a write that finds no chunk is counted; the memory and length caps stop the recording at a known
// point and say so).
using System.Text;
using NesEmulator;

namespace BrokenNes2.Record;

/// <summary>A copy of a DMC sample's bytes as the game started it: the CPU address and length the registers named, the bytes, and when it was first started.</summary>
public sealed class NesDmcCopy
{
    public const int MaxBytes = 4081;
    public int Address, Length;
    public uint FirstTime;
    public readonly byte[] Bytes = new byte[MaxBytes];
    public ReadOnlySpan<byte> Data => Bytes.AsSpan(0, Length);
}

/// <summary>A finished NES recording: immutable once made (the transcription and the project read it).</summary>
public sealed class NesSession
{
    /// <summary>Marker "registers" in the stream (not chip writes): a DMC sample copy (value = its index) just before the $4015 write that started it, and the end of a frame (value 0).</summary>
    public const int MarkerDmc = 0x80, MarkerFrame = 0x90;
    public const double CpuHz = 1789773.0;

    public ulong Id;
    public string Name = "", Game = "", Sha1 = "", Chip = "";
    public long CreatedUnix;
    /// <summary>The game's APU clock when the recording began (for reference: times are counted from it).</summary>
    public long StartClock;
    /// <summary>How long the recording ran, in CPU cycles.</summary>
    public uint Length;
    /// <summary>The chip's state at the start (<see cref="NesApuModel.Save"/> layout) and whether it is the chip's own (APU_FIX) or only rebuilt from the registers last written (another chip).</summary>
    public int[] BaseState = new int[NesApuModel.StateInts];
    public bool BaseExact;
    public uint[] Times = [];
    public ushort[] RegVals = [];
    public NesDmcCopy[] Dmc = [];
    public long Dropped;
    public int DmcMissing, DmcOver;
    public RecEnd EndedBy;
    /// <summary>The recording stopped because it reached the longest a recording can be (its times are 32-bit cycle counts: <see cref="NesRecorder.MaxCycles"/>).</summary>
    public bool TimeCap;

    public int Count => Times.Length;
    public double Seconds => Length / CpuHz;
    public void Get(int i, out uint t, out int reg, out int value) { t = Times[i]; reg = RegVals[i] >> 8; value = RegVals[i] & 0xFF; }

    // ---- saved form (the payload a project section carries) ----

    public const int PayloadVersion = 1, MaxText = 260;
    /// <summary>The first byte of a NES payload: tells it apart from a SNES one (whose first byte is its version, 1) in a section that can hold either.</summary>
    public const byte PayloadKind = 0x4E;     // 'N'

    public byte[] ToPayload()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(PayloadKind); w.Write((byte)PayloadVersion);
        Str(w, Name); Str(w, Game); Str(w, Sha1); Str(w, Chip);
        w.Write(Id); w.Write(CreatedUnix); w.Write(StartClock); w.Write(Length); w.Write(Dropped);
        w.Write((ushort)DmcMissing); w.Write((ushort)DmcOver); w.Write((byte)EndedBy); w.Write((byte)((BaseExact ? 1 : 0) | (TimeCap ? 2 : 0)));
        foreach (int v in BaseState) w.Write(v);
        w.Write(Times.Length);
        uint last = 0;
        for (int i = 0; i < Times.Length; i++) { Varint(w, Times[i] - last); last = Times[i]; }
        for (int i = 0; i < RegVals.Length; i++) w.Write(RegVals[i]);
        w.Write((ushort)Dmc.Length);
        foreach (var d in Dmc) { w.Write((ushort)d.Address); w.Write((ushort)d.Length); w.Write(d.FirstTime); w.Write(d.Bytes, 0, d.Length); }
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Reads a payload; null with the reason for anything that is not one (every count and range checked, never trusted).</summary>
    public static NesSession? FromPayload(byte[] b, out string? problem)
    {
        problem = null;
        try
        {
            using var r = new BinaryReader(new MemoryStream(b));
            if (r.ReadByte() != PayloadKind) { problem = "not a NES recording"; return null; }
            int pv = r.ReadByte();
            if (pv > PayloadVersion) { problem = $"made by a newer BrokenNes2 (NES recording format {pv})"; return null; }
            if (pv != PayloadVersion) { problem = "of an unknown NES recording format"; return null; }
            var s = new NesSession { Name = ReadStr(r), Game = ReadStr(r), Sha1 = ReadStr(r), Chip = ReadStr(r) };
            s.Id = r.ReadUInt64(); s.CreatedUnix = r.ReadInt64(); s.StartClock = r.ReadInt64(); s.Length = r.ReadUInt32(); s.Dropped = r.ReadInt64();
            s.DmcMissing = r.ReadUInt16(); s.DmcOver = r.ReadUInt16();
            int e = r.ReadByte(); if (e > (int)RecEnd.Unknown) { problem = "damaged (how it ended)"; return null; }
            s.EndedBy = (RecEnd)e;
            int flags = r.ReadByte(); if ((flags & ~3) != 0) { problem = "damaged (its flags)"; return null; }
            s.BaseExact = (flags & 1) != 0; s.TimeCap = (flags & 2) != 0;
            if (s.Dropped < 0) { problem = "damaged (its counters)"; return null; }
            for (int i = 0; i < NesApuModel.StateInts; i++) s.BaseState[i] = r.ReadInt32();
            if (NesApuModel.Load(s.BaseState) == null) { problem = "damaged (the chip's starting state)"; return null; }
            int n = r.ReadInt32();
            if (n < 0 || n > NesRecorder.MaxChunks * NesRecorder.ChunkWrites) { problem = "damaged (the number of writes)"; return null; }
            s.Times = new uint[n]; s.RegVals = new ushort[n];
            uint t = 0;
            for (int i = 0; i < n; i++) { t += ReadVarint(r); if (t > s.Length) { problem = "damaged (a write after the end)"; return null; } s.Times[i] = t; }
            for (int i = 0; i < n; i++)
            {
                ushort rv = r.ReadUInt16();
                int reg = rv >> 8;
                if (reg > 0x17 && reg != MarkerDmc && reg != MarkerFrame) { problem = "damaged (a write to no register)"; return null; }
                s.RegVals[i] = rv;
            }
            int dc = r.ReadUInt16();
            if (dc > NesRecorder.MaxDmc) { problem = "damaged (the number of DMC samples)"; return null; }
            s.Dmc = new NesDmcCopy[dc];
            for (int i = 0; i < dc; i++)
            {
                var d = new NesDmcCopy { Address = r.ReadUInt16(), Length = r.ReadUInt16(), FirstTime = r.ReadUInt32() };
                if (d.Address < 0xC000 || d.Length is < 1 or > NesDmcCopy.MaxBytes || d.FirstTime > s.Length) { problem = "damaged (a DMC sample)"; return null; }
                if (r.Read(d.Bytes, 0, d.Length) != d.Length) { problem = "cut short"; return null; }
                s.Dmc[i] = d;
            }
            for (int i = 0; i < n; i++) if (s.RegVals[i] >> 8 == MarkerDmc && (s.RegVals[i] & 0xFF) >= dc) { problem = "damaged (a mark names no DMC sample)"; return null; }
            if (r.BaseStream.Position != b.Length) { problem = "damaged (bytes after the end)"; return null; }
            return s;
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or ArgumentException or DecoderFallbackException)
        {
            problem = "damaged (" + ex.Message + ")";
            return null;
        }
    }

    private static void Str(BinaryWriter w, string s)
    {
        var b = Encoding.UTF8.GetBytes(s.Length > MaxText ? s[..MaxText] : s);
        w.Write((ushort)b.Length); w.Write(b);
    }

    private static string ReadStr(BinaryReader r)
    {
        int n = r.ReadUInt16();
        if (n > MaxText * 4) throw new ArgumentException("a text is too long");
        return new UTF8Encoding(false, true).GetString(r.ReadBytes(n));
    }

    private static void Varint(BinaryWriter w, uint v) { while (v >= 0x80) { w.Write((byte)(v | 0x80)); v >>= 7; } w.Write((byte)v); }

    private static uint ReadVarint(BinaryReader r)
    {
        uint v = 0; int shift = 0;
        while (true)
        {
            byte b = r.ReadByte();
            v |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return v;
            shift += 7;
            if (shift > 28) throw new ArgumentException("a number is too long");
        }
    }
}

/// <summary>
/// The capture: every write of the running game to $4000-$4017 (from the bus's APU write tap, on the audio thread, under the emulator's lock) with its APU clock. Lifecycle as the SNES capture's:
/// <see cref="Arm"/> (UI thread: the first chunks and DMC buffers) -> <see cref="Begin"/> (between frames: the chip's state now) -> writes and frame ends -> <see cref="Halt"/> -> <see cref="Finish"/>.
/// <see cref="Feed"/> keeps the spare chunks and DMC buffers ready (a feeder thread).
/// </summary>
public sealed class NesRecorder
{
    public const int ChunkWrites = 1 << 16;
    /// <summary>The memory cap: 160 chunks of 65,536 entries (6 bytes each): 10.5 million, 63 MB. A driver makes 20 to 1,000 writes a second (and one entry per frame); one that streams raw PCM through $4011 can make 8,000 (22 minutes).</summary>
    public const int MaxChunks = 160;
    public const int SpareChunks = 4, FirstChunks = 4;
    public const int MaxDmc = 255, SpareDmc = 4, FirstDmc = 4;
    /// <summary>The longest recording: its times are 32-bit cycle counts (39 minutes and 59 seconds at 1,789,773 a second; stopped a little before).</summary>
    public const uint MaxCycles = 4_290_000_000;

    private sealed class Chunk
    {
        public readonly uint[] Time = new uint[ChunkWrites];
        public readonly ushort[] RegVal = new ushort[ChunkWrites];
    }

    private readonly Chunk?[] chunks = new Chunk?[MaxChunks];
    private int chunksReady, chunk, fill;
    private readonly NesDmcCopy?[] dmc = new NesDmcCopy?[MaxDmc];
    private int dmcReady, dmcUsed;
    private long count, dropped;
    private int dmcMissing, dmcOver;
    private long startClock, lastClock;
    private readonly int[] baseState = new int[NesApuModel.StateInts];
    private readonly NesApuModel scratch = new();                // (another chip than FIX: its starting state is rebuilt here, on the audio thread, without allocating)
    private bool baseExact, timeCap;
    private readonly byte[] regs = new byte[0x18];
    private int state;                           // 0 armed, 1 recording, 2 halted
    private RecEnd endedBy = RecEnd.Unknown;
    private int feederStalled;
    private NES? nes;
    private string chip = "";

    public long Count => Volatile.Read(ref count);
    public long Dropped => Volatile.Read(ref dropped);
    public int DmcSamples => Volatile.Read(ref dmcUsed);
    public bool Recording => Volatile.Read(ref state) == 1;
    public bool Halted => Volatile.Read(ref state) == 2;
    public RecEnd EndedBy => endedBy;
    /// <summary>The recording's length so far, in CPU cycles.</summary>
    public long Cycles => Math.Max(0, Volatile.Read(ref lastClock) - startClock);
    public double Seconds => Cycles / NesSession.CpuHz;
    public long MemoryBytes => (long)Volatile.Read(ref chunksReady) * ChunkWrites * 6 + (long)Volatile.Read(ref dmcReady) * NesDmcCopy.MaxBytes;

    /// <summary>UI thread: makes the first chunks and DMC buffers.</summary>
    public void Arm()
    {
        for (int i = 0; i < FirstChunks; i++) chunks[i] = new Chunk();
        Volatile.Write(ref chunksReady, FirstChunks);
        for (int i = 0; i < FirstDmc; i++) dmc[i] = new NesDmcCopy();
        Volatile.Write(ref dmcReady, FirstDmc);
    }

    /// <summary>Tests: the feeder makes nothing from now on, so a long take runs out of chunks and must count what it lost.</summary>
    public void StallFeeder(bool on) => Volatile.Write(ref feederStalled, on ? 1 : 0);

    /// <summary>The feeder (any thread but the audio one): spare chunks and DMC buffers ahead of the capture. True while there is work to come.</summary>
    public bool Feed()
    {
        if (Volatile.Read(ref feederStalled) != 0) return state != 2;
        int ready = Volatile.Read(ref chunksReady);
        while (ready < MaxChunks && ready - Volatile.Read(ref chunk) <= SpareChunks) { chunks[ready] = new Chunk(); Volatile.Write(ref chunksReady, ++ready); }
        int dready = Volatile.Read(ref dmcReady);
        while (dready < MaxDmc && dready - Volatile.Read(ref dmcUsed) < SpareDmc) { dmc[dready] = new NesDmcCopy(); Volatile.Write(ref dmcReady, ++dready); }
        return Volatile.Read(ref state) != 2;
    }

    /// <summary>Between frames (under the emulator's lock): the recording starts now, from the chip's state as it is (APU_FIX's own counters; another chip's state is rebuilt from the registers it was
    /// last written: <paramref name="latch"/> / <paramref name="written"/>). <paramref name="chipId"/> names the chip. Allocation-free.</summary>
    public void Begin(NES machine, byte[] latch, uint written, string chipId)
    {
        nes = machine;
        chip = chipId;
        startClock = lastClock = machine.ApuClock;
        Array.Copy(latch, regs, Math.Min(latch.Length, regs.Length));
        if (machine.ActiveApu is APU_FIX fix) { NesApuModel.Fill(fix.ProbeControl(), baseState); baseExact = true; }
        else { NesApuModel.FillFromLatch(latch, written, baseState, scratch); baseExact = false; }
        Volatile.Write(ref state, 1);
    }

    /// <summary>One write to $4000-$4017 (audio thread, inside the game's frame; the bus calls it before the chip takes the write). Allocation-free; a write that finds no chunk is counted.</summary>
    public void OnWrite(ushort address, byte value, long clock)
    {
        if (state != 1) return;
        int reg = address - 0x4000;
        if ((uint)reg >= 0x18) return;
        long t = clock - startClock;
        if (t > MaxCycles) { timeCap = true; endedBy = RecEnd.MemoryCap; Volatile.Write(ref state, 2); return; }
        lastClock = clock;
        if (reg == 0x15 && (value & 0x10) != 0) DmcStart((uint)t);
        regs[reg] = value;
        if (RecMutants.Active != 0 && NesMutated(reg)) return;
        Put((uint)t, (ushort)(reg << 8 | value));
    }

    private long mutCount;
    private bool NesMutated(int reg)
    {
        if (RecMutants.On(NesRecMutants.CaptureLosesWrites) && ++mutCount % 1000 == 0) return true;
        if (RecMutants.On(NesRecMutants.CaptureAllocates) && (reg & 3) == 3) MutantSink = new byte[64];
        return false;
    }
    public static object? MutantSink;

    /// <summary>After a frame (under the emulator's lock): its end goes into the stream (so a stretch with no write still counts as recorded time, and the transcription knows the frames).</summary>
    public void FrameEnd(long clock)
    {
        if (state != 1) return;
        long t = clock - startClock;
        if (t > MaxCycles) { timeCap = true; endedBy = RecEnd.MemoryCap; Volatile.Write(ref state, 2); return; }
        if (clock > lastClock) Volatile.Write(ref lastClock, clock);
        Put((uint)t, NesSession.MarkerFrame << 8);
    }

    public void Halt(RecEnd why)
    {
        if (Volatile.Read(ref state) == 2) return;
        endedBy = why;
        Volatile.Write(ref state, 2);
    }

    private void Put(uint t, ushort rv)
    {
        if (fill == ChunkWrites)
        {
            int next = chunk + 1;
            if (next >= MaxChunks) { endedBy = RecEnd.MemoryCap; Volatile.Write(ref state, 2); return; }
            if (next >= Volatile.Read(ref chunksReady)) { dropped++; return; }
            Volatile.Write(ref chunk, next);
            fill = 0;
        }
        var c = chunks[chunk]!;
        c.Time[fill] = t; c.RegVal[fill] = rv;
        fill++;
        Volatile.Write(ref count, count + 1);
    }

    /// <summary>$4015 with the DMC bit: when it starts a sample (APU_FIX: no bytes left to play; another chip: any such write), the bytes the registers name are copied (once per distinct sample) and a mark names the copy.</summary>
    private void DmcStart(uint t)
    {
        var m = nes;
        if (m == null) return;
        if (m.ActiveApu is IApuStateProbe probe && probe.ProbeApuState().DmcBytesRemaining > 0) return;     // (already playing: the write does not restart it)
        int addr = 0xC000 + (regs[0x12] << 6), len = (regs[0x13] << 4) + 1;
        int found = -1;
        for (int i = 0; i < dmcUsed && found < 0; i++)
        {
            var d = dmc[i]!;
            if (d.Address != addr || d.Length != len) continue;
            bool same = true;
            for (int k = 0; k < len && same; k++) same = Peek(m, addr + k, out byte v) && v == d.Bytes[k];
            if (same) found = i;
        }
        if (found < 0)
        {
            if (dmcUsed >= MaxDmc) { dmcOver++; return; }
            if (dmcUsed >= Volatile.Read(ref dmcReady)) { dmcMissing++; return; }
            var d = dmc[dmcUsed]!;
            d.Address = addr; d.Length = len; d.FirstTime = t;
            for (int k = 0; k < len; k++) { if (!Peek(m, addr + k, out d.Bytes[k])) { dmcMissing++; return; } }
            found = dmcUsed;
            Volatile.Write(ref dmcUsed, dmcUsed + 1);
        }
        Put(t, (ushort)(NesSession.MarkerDmc << 8 | found));
    }

    /// <summary>A byte of the cartridge as the CPU sees it at <paramref name="addr"/> ($8000-$FFFF; the DMC's address wraps from $FFFF to $8000), read through the mapper without touching the bus (no open-bus or timing side effect).</summary>
    private static bool Peek(NES m, int addr, out byte v)
    {
        ushort a = (ushort)(addr > 0xFFFF ? 0x8000 + (addr - 0x10000) : addr);
        if (m.TryCpuToPrgIndex(a, out int idx) && idx >= 0) { v = m.PeekPrg(idx); return true; }
        v = 0;
        return false;
    }

    /// <summary>After <see cref="Halt"/> (not on the audio thread): the session.</summary>
    public NesSession Finish(string name, string game, string sha1, long createdUnix, ulong id)
    {
        var s = new NesSession { Name = name, Game = game, Sha1 = sha1, Chip = chip, CreatedUnix = createdUnix, Id = id, StartClock = startClock, EndedBy = endedBy, BaseExact = baseExact, TimeCap = timeCap };
        s.Length = (uint)Math.Clamp(lastClock - startClock, 0, uint.MaxValue);
        Array.Copy(baseState, s.BaseState, baseState.Length);
        long n = count;
        s.Times = new uint[n]; s.RegVals = new ushort[n];
        for (long i = 0; i < n; i++)
        {
            var c = chunks[i / ChunkWrites]!;
            s.Times[i] = c.Time[i % ChunkWrites]; s.RegVals[i] = c.RegVal[i % ChunkWrites];
        }
        if (n > 0 && s.Times[n - 1] > s.Length) s.Length = s.Times[n - 1];
        s.Dmc = dmc.AsSpan(0, dmcUsed).ToArray()!;
        s.Dropped = dropped; s.DmcMissing = dmcMissing; s.DmcOver = dmcOver;
        nes = null;
        return s;
    }
}

/// <summary>The NES recording's deliberate bugs (codes in <see cref="RecMutants"/>' space, above the SNES ones: only the self-test sets them).</summary>
public static class NesRecMutants
{
    public const int CaptureLosesWrites = 101, CaptureAllocates = 102, DmcCopyLost = 103;
}
