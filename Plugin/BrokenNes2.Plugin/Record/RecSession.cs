// Round 8 (record): a recording session of a SNES game's music. EVERY write the SPC700 makes to the S-DSP from Record to Stop is kept (the instrument ring of round 6, SpcWriteLog, keeps the last
// 262,144 and forgets the rest), together with what an instrument IS when it is first played: the audio RAM (where its BRR sample lives) at the moment of its first key-on. The transcription
// (RecTranscribe.cs) turns the session into MIDI tracks and presets; the project keeps the session itself (RecSection.cs), so a later build can transcribe it again.
//
// Real-time rules (the capture runs on FL's audio thread, inside the game's frame, under the emulator's lock):
//   * nothing is allocated there: the writes go into chunks of 65,536 that a feeder thread (or the UI thread) made BEFORE they are needed, and the audio RAM copies go into buffers made the same way;
//   * nothing is dropped silently: a write that finds no chunk ready is counted (Dropped) and the window says so; the memory cap stops the recording at a known point and says so;
//   * the S-DSP's own 32 kHz sample clock (APU_SFC.SamplesProduced) is the time base: a frozen game (Instrument Runaway) does not advance it, so a pause leaves no gap.
//
// What an instrument is: a sample number (SRCN) and the place its directory entry points at when it is keyed on. A game that loads another sample at another address under the same number gets
// a second "version"; a version change is logged in the stream as a marker (register 0x80) just before the key-on that needed it. A sample whose bytes change under the same address during
// the recording is noticed at Stop (the audio RAM then is compared with the copy) and said, not hidden.
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace BrokenNes2.Record;

/// <summary>One instrument of a session: a sample number and where its directory entry pointed when it was first keyed on, with the audio RAM copy that holds its bytes.</summary>
public readonly record struct RecVersion(int Srcn, int Start, int Loop, int Shot, uint FirstTime);

/// <summary>A copy of the audio RAM and the S-DSP registers at the moment an instrument was first keyed on.</summary>
public sealed class RecShot
{
    public readonly byte[] Aram = new byte[0x10000];
    public readonly byte[] Dsp = new byte[128];
}

/// <summary>Why a recording ended.</summary>
public enum RecEnd : byte { Stop = 0, MemoryCap = 1, GameReplaced = 2, Transport = 3, Unknown = 4 }

/// <summary>A finished recording: immutable once made (the transcription and the project read it).</summary>
public sealed class RecSession
{
    /// <summary>The marker register: a write of this "register" is not a DSP write but "sample number <c>value</c>'s current version changed" (the version index is the value; see <see cref="RecVersion"/>).</summary>
    public const int MarkerVersion = 0x80;

    public ulong Id;
    public string Name = "", Game = "", Sha1 = "";
    public long CreatedUnix;
    public long StartSample;
    /// <summary>How long the recording ran, in S-DSP samples (32 kHz).</summary>
    public uint Length;
    public readonly byte[] BaseRegs = new byte[128];
    public uint[] Times = [];
    public ushort[] RegVals = [];
    public RecVersion[] Versions = [];
    public RecShot[] Shots = [];
    public RecShot? End;
    public long Dropped;
    public int ShotsMissing, VersionsOver;
    public RecEnd EndedBy;
    /// <summary>Voices that were sounding when the recording began: those notes started before it and are not transcribed.</summary>
    public int HeldAtStart;

    public int Count => Times.Length;
    public double Seconds => Length / 32000.0;

    public void Get(int i, out uint t, out int reg, out int value) { t = Times[i]; reg = RegVals[i] >> 8; value = RegVals[i] & 0xFF; }

    /// <summary>The bytes of an instrument's sample as they were at its first key-on: from its start, block by block, until a block with the end flag (at most 4,096 blocks or the end of audio RAM).</summary>
    public byte[] SampleBytes(int version, out bool ended)
    {
        var v = Versions[version];
        var aram = v.Shot >= 0 && v.Shot < Shots.Length ? Shots[v.Shot].Aram : End?.Aram ?? new byte[0x10000];
        return SampleIn(aram, v.Start, out ended);
    }

    public static byte[] SampleIn(byte[] aram, int start, out bool ended)
    {
        ended = false;
        int a = start, blocks = 0;
        while (blocks < 4096 && a + 9 <= 0x10000)
        {
            blocks++;
            if ((aram[a] & 1) != 0) { ended = true; break; }
            a += 9;
        }
        var b = new byte[blocks * 9];
        Array.Copy(aram, start, b, 0, Math.Min(b.Length, 0x10000 - start));
        return b;
    }

    /// <summary>The instruments whose sample bytes were different in audio RAM at Stop than at their first key-on (the game loaded something else at the same place during the recording).</summary>
    public List<int> ChangedDuringRecording()
    {
        var list = new List<int>();
        if (End == null) return list;
        for (int i = 0; i < Versions.Length; i++)
        {
            var v = Versions[i];
            if (v.Shot < 0 || v.Shot >= Shots.Length) continue;
            var a = SampleIn(Shots[v.Shot].Aram, v.Start, out _);
            var b = SampleIn(End.Aram, v.Start, out _);
            if (!a.AsSpan().SequenceEqual(b)) list.Add(i);
        }
        return list;
    }

    // ---- saved form (the payload of the project section; RecSection adds the header, the deflate and the CRC) ----

    public const int PayloadVersion = 1;
    public const int MaxWrites = SpcRecorder.MaxChunks * SpcRecorder.ChunkWrites, MaxShots = SpcRecorder.MaxShots, MaxText = 260;

    public byte[] ToPayload()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)PayloadVersion);
        Str(w, Name); Str(w, Game); Str(w, Sha1);
        w.Write(Id); w.Write(CreatedUnix); w.Write(StartSample); w.Write(Length); w.Write(Dropped);
        w.Write((ushort)ShotsMissing); w.Write((ushort)VersionsOver); w.Write((byte)EndedBy); w.Write((byte)HeldAtStart);
        w.Write(BaseRegs);
        w.Write(Times.Length);
        uint last = 0;
        for (int i = 0; i < Times.Length; i++) { Varint(w, Times[i] - last); last = Times[i]; }
        for (int i = 0; i < RegVals.Length; i++) w.Write(RegVals[i]);
        w.Write((ushort)Versions.Length);
        foreach (var v in Versions) { w.Write((byte)v.Srcn); w.Write((ushort)v.Start); w.Write((ushort)v.Loop); w.Write((short)v.Shot); w.Write(v.FirstTime); }
        w.Write((ushort)Shots.Length);
        foreach (var s in Shots) { w.Write(s.Aram); w.Write(s.Dsp); }
        w.Write(End != null);
        if (End != null) { w.Write(End.Aram); w.Write(End.Dsp); }
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Reads a payload; null with the reason for anything that is not one (every count and range checked, never trusted).</summary>
    public static RecSession? FromPayload(byte[] b, out string? problem)
    {
        problem = null;
        try
        {
            using var r = new BinaryReader(new MemoryStream(b));
            int pv = r.ReadByte();
            if (pv > PayloadVersion) { problem = $"made by a newer BrokenNes2 (recording format {pv})"; return null; }
            if (pv != PayloadVersion) { problem = "of an unknown recording format"; return null; }
            var s = new RecSession { Name = ReadStr(r), Game = ReadStr(r), Sha1 = ReadStr(r) };
            s.Id = r.ReadUInt64(); s.CreatedUnix = r.ReadInt64(); s.StartSample = r.ReadInt64(); s.Length = r.ReadUInt32(); s.Dropped = r.ReadInt64();
            s.ShotsMissing = r.ReadUInt16(); s.VersionsOver = r.ReadUInt16();
            int e = r.ReadByte(); if (e > (int)RecEnd.Unknown) { problem = "damaged (how it ended)"; return null; }
            s.EndedBy = (RecEnd)e; s.HeldAtStart = r.ReadByte();
            if (s.Dropped < 0 || s.HeldAtStart > 8) { problem = "damaged (its counters)"; return null; }
            r.Read(s.BaseRegs, 0, 128);
            int n = r.ReadInt32();
            if (n < 0 || n > MaxWrites) { problem = "damaged (the number of writes)"; return null; }
            s.Times = new uint[n]; s.RegVals = new ushort[n];
            uint t = 0;
            for (int i = 0; i < n; i++) { t += ReadVarint(r); if (t > s.Length) { problem = "damaged (a write after the end)"; return null; } s.Times[i] = t; }
            for (int i = 0; i < n; i++)
            {
                ushort rv = r.ReadUInt16();
                int reg = rv >> 8;
                if (reg > MarkerVersion) { problem = "damaged (a write to no register)"; return null; }
                s.RegVals[i] = rv;
            }
            int vc = r.ReadUInt16();
            if (vc > SpcRecorder.MaxVersions) { problem = "damaged (the number of instruments)"; return null; }
            s.Versions = new RecVersion[vc];
            for (int i = 0; i < vc; i++)
            {
                int srcn = r.ReadByte(), start = r.ReadUInt16(), loop = r.ReadUInt16(), shot = r.ReadInt16();
                uint first = r.ReadUInt32();
                if (shot < -1 || first > s.Length) { problem = "damaged (an instrument)"; return null; }
                s.Versions[i] = new RecVersion(srcn, start, loop, shot, first);
            }
            int sc = r.ReadUInt16();
            if (sc > MaxShots) { problem = "damaged (the number of sound-memory copies)"; return null; }
            s.Shots = new RecShot[sc];
            for (int i = 0; i < sc; i++) { var sh = new RecShot(); if (r.Read(sh.Aram, 0, 0x10000) != 0x10000 || r.Read(sh.Dsp, 0, 128) != 128) { problem = "cut short"; return null; } s.Shots[i] = sh; }
            foreach (var v in s.Versions) if (v.Shot >= sc) { problem = "damaged (an instrument points at no copy)"; return null; }
            if (r.ReadBoolean())
            {
                var end = new RecShot();
                if (r.Read(end.Aram, 0, 0x10000) != 0x10000 || r.Read(end.Dsp, 0, 128) != 128) { problem = "cut short"; return null; }
                s.End = end;
            }
            // the markers name versions that exist
            for (int i = 0; i < n; i++) if (s.RegVals[i] >> 8 == MarkerVersion && (s.RegVals[i] & 0xFF) >= vc) { problem = "damaged (a marker names no instrument)"; return null; }
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
/// The capture: it receives every S-DSP write of the running game (from <see cref="SpcWriteLog.OnWrite"/>, on the audio thread, under the emulator's lock) and keeps it.
/// Lifecycle: <see cref="Arm"/> (UI thread: the first chunks and copies are made) -> <see cref="Begin"/> (between frames: the registers as they are now are the starting point) ->
/// writes -> <see cref="Halt"/> (between frames, or the memory cap) -> <see cref="Finish"/> (any thread but the audio one: the session is built). <see cref="Feed"/> keeps the spare chunks and copies
/// ready (a feeder thread calls it every few milliseconds while the recording runs).
/// </summary>
public sealed class SpcRecorder
{
    public const int ChunkWrites = 1 << 16;
    /// <summary>The memory cap: 160 chunks of 65,536 writes (6 bytes each): 10.5 million writes, 63 MB. A busy driver makes 1,000 to 3,000 writes a second (an hour at least); a corrupted one can make 20,000 (nine minutes).</summary>
    public const int MaxChunks = 160;
    public const int SpareChunks = 4, FirstChunks = 6;
    public const int MaxShots = 64, SpareShots = 4, FirstShots = 6;
    public const int MaxVersions = 255;

    private sealed class Chunk
    {
        public readonly uint[] Time = new uint[ChunkWrites];
        public readonly ushort[] RegVal = new ushort[ChunkWrites];
    }

    private readonly Chunk?[] chunks = new Chunk?[MaxChunks];
    private int chunksReady;                     // chunks[0 .. chunksReady-1] exist (the feeder publishes with a volatile write)
    private int chunk, fill;                     // the chunk being filled and how many writes it holds (audio thread)
    private readonly RecShot?[] shots = new RecShot?[MaxShots];
    private int shotsReady, shotsUsed;
    private readonly RecVersion[] versions = new RecVersion[MaxVersions];
    private int versionCount;
    private readonly short[] current = new short[256];     // the version each sample number has now (-1: not keyed yet)
    private long count, dropped;
    private int shotsMissing, versionsOver;
    private long startSample, lastSample;
    private readonly byte[] baseRegs = new byte[128];
    private byte[]? aram, dsp;                   // the running unit's audio RAM and registers (for the copies)
    private int state;                           // 0 armed, 1 recording, 2 halted
    private int heldAtStart;
    private RecEnd endedBy = RecEnd.Unknown;
    private int feederStalled;                   // tests: the feeder makes nothing (a starved capture must count what it lost)

    /// <summary>The audio RAM copies the recording made so far and how many chunk-fulls of writes: what the window shows.</summary>
    public long Count => Volatile.Read(ref count);
    public long Dropped => Volatile.Read(ref dropped);
    public int Instruments => Volatile.Read(ref versionCount);
    public bool Recording => Volatile.Read(ref state) == 1;
    public bool Halted => Volatile.Read(ref state) == 2;
    public RecEnd EndedBy => endedBy;
    /// <summary>The recording's length so far, in S-DSP samples.</summary>
    public long Samples => Math.Max(0, Volatile.Read(ref lastSample) - startSample);
    /// <summary>Bytes the capture holds (chunks and copies made).</summary>
    public long MemoryBytes => (long)Volatile.Read(ref chunksReady) * ChunkWrites * 6 + (long)Volatile.Read(ref shotsReady) * 0x10080;
    public bool CapReached => endedBy == RecEnd.MemoryCap;

    /// <summary>UI thread: makes the first chunks and copies (so the first seconds never wait for the feeder).</summary>
    public void Arm()
    {
        for (int i = 0; i < FirstChunks; i++) chunks[i] = new Chunk();
        Volatile.Write(ref chunksReady, FirstChunks);
        for (int i = 0; i < FirstShots; i++) shots[i] = new RecShot();
        Volatile.Write(ref shotsReady, FirstShots);
        Array.Fill(current, (short)-1);
    }

    /// <summary>Tests: the feeder makes nothing from now on (value true), so a long take runs out of chunks and must count what it lost.</summary>
    public void StallFeeder(bool on) => Volatile.Write(ref feederStalled, on ? 1 : 0);

    /// <summary>The feeder (any thread but the audio one): keeps <see cref="SpareChunks"/> chunks and <see cref="SpareShots"/> copies ahead of the capture, up to the caps. Returns true while there is work to come.</summary>
    public bool Feed()
    {
        if (Volatile.Read(ref feederStalled) != 0) return state != 2;
        int ready = Volatile.Read(ref chunksReady);
        while (ready < MaxChunks && ready - Volatile.Read(ref chunk) <= SpareChunks)
        {
            chunks[ready] = new Chunk();
            Volatile.Write(ref chunksReady, ++ready);
        }
        int sready = Volatile.Read(ref shotsReady);
        while (sready < MaxShots && sready - Volatile.Read(ref shotsUsed) < SpareShots)
        {
            shots[sready] = new RecShot();
            Volatile.Write(ref shotsReady, ++sready);
        }
        return Volatile.Read(ref state) != 2;
    }

    /// <summary>Between frames (audio thread or not, under the emulator's lock): the recording starts now, from the unit's registers as they are. Allocation-free.</summary>
    public void Begin(byte[] unitAram, byte[] unitRegs, long now)
    {
        aram = unitAram; dsp = unitRegs;
        Array.Copy(unitRegs, baseRegs, 128);
        startSample = now; lastSample = now;
        int held = 0;
        for (int v = 0; v < 8; v++) if (unitRegs[v * 16 + 8] != 0) held++;
        heldAtStart = held;
        Volatile.Write(ref state, 1);
    }

    /// <summary>The unit was swapped for another (a sound-chip change): the copies are taken from the new one.</summary>
    public void Rebind(byte[] unitAram, byte[] unitRegs) { aram = unitAram; dsp = unitRegs; }

    /// <summary>Between frames: the S-DSP's clock now (a stretch with no write still counts as recorded time).</summary>
    public void Clock(long now) { if (state == 1 && now > lastSample) Volatile.Write(ref lastSample, now); }

    /// <summary>Between frames: the recording ends now (<paramref name="why"/>). The audio RAM at the end is copied by <see cref="Finish"/>, from <paramref name="unitAram"/> as it is then.</summary>
    public void Halt(RecEnd why)
    {
        if (Volatile.Read(ref state) == 2) return;
        endedBy = why;
        Volatile.Write(ref state, 2);
    }

    /// <summary>One S-DSP write (audio thread, inside the game's frame). Allocation-free; a write that finds no chunk is counted, never lost without a word.</summary>
    public void OnWrite(byte reg, byte value, long sample)
    {
        if (state != 1) return;
        lastSample = sample;
        if (reg == 0x4C && value != 0 && aram != null) KeyOn(value, sample);
        if (RecMutants.Active != 0 && Mutated(reg)) return;
        Put((uint)(sample - startSample), (ushort)(reg << 8 | value));
    }

    private long mutCount;
    /// <summary>The self-test's deliberate bugs in the capture: every 1,000th write lost without a word; an allocation at every key-on (the allocation meter must see it).</summary>
    private bool Mutated(byte reg)
    {
        if (RecMutants.On(RecMutants.CaptureLosesWrites) && ++mutCount % 1000 == 0) return true;
        if (RecMutants.On(RecMutants.CaptureAllocates) && reg == 0x4C) MutantSink = new byte[64];
        return false;
    }
    public static object? MutantSink;

    private void Put(uint t, ushort rv)
    {
        if (fill == ChunkWrites)
        {
            int next = chunk + 1;
            if (next >= MaxChunks) { endedBy = RecEnd.MemoryCap; Volatile.Write(ref state, 2); return; }
            if (next >= Volatile.Read(ref chunksReady)) { dropped++; return; }       // the feeder is late: counted (the window says so)
            Volatile.Write(ref chunk, next);
            fill = 0;
        }
        var c = chunks[chunk]!;
        c.Time[fill] = t; c.RegVal[fill] = rv;
        fill++;
        Volatile.Write(ref count, count + 1);
    }

    /// <summary>A key-on: for each voice keyed, the instrument it plays is its sample number and the address its directory entry points at; a new one gets a version (and a copy of the audio RAM), and a marker goes into the stream before the key-on.</summary>
    private void KeyOn(byte value, long sample)
    {
        var regs = dsp!; var ram = aram!;
        int dir = regs[0x5D] << 8;
        for (int v = 0; v < 8; v++)
        {
            if ((value >> v & 1) == 0) continue;
            int srcn = regs[v * 16 + 4];
            int e = (dir + srcn * 4) & 0xFFFF;
            int start = ram[e] | ram[(e + 1) & 0xFFFF] << 8, loop = ram[(e + 2) & 0xFFFF] | ram[(e + 3) & 0xFFFF] << 8;
            int cur = current[srcn];
            if (cur >= 0 && versions[cur].Start == start) continue;
            // this sample number at this address seen before (a game that switches between two sample sets): that version again
            int found = -1;
            for (int i = 0; i < versionCount; i++) if (versions[i].Srcn == srcn && versions[i].Start == start) { found = i; break; }
            if (found < 0)
            {
                if (versionCount >= MaxVersions) { versionsOver++; continue; }
                int shot = TakeShot(srcn, start);
                found = versionCount;
                versions[versionCount++] = new RecVersion(srcn, start, loop, shot, (uint)(sample - startSample));
            }
            current[srcn] = (short)found;
            if (RecMutants.On(RecMutants.KeyOnVersionLost) && found > 0) continue;      // (the deliberate bug: a new instrument's marker never reaches the stream)
            Put((uint)(sample - startSample), (ushort)(RecSession.MarkerVersion << 8 | found));
        }
    }

    /// <summary>The copy of the audio RAM an instrument's bytes are taken from: the newest copy when the sample's bytes there are still the same (most instruments of a song are loaded together), else a new one. -1 when no copy is ready (counted).</summary>
    private int TakeShot(int srcn, int start)
    {
        var ram = aram!;
        if (shotsUsed > 0)
        {
            var last = shots[shotsUsed - 1]!.Aram;
            bool same = true;
            int a = start, blocks = 0;
            while (blocks < 4096 && a + 9 <= 0x10000)
            {
                for (int i = 0; i < 9; i++) if (last[a + i] != ram[a + i]) { same = false; break; }
                if (!same || (ram[a] & 1) != 0) break;
                a += 9; blocks++;
            }
            if (same) return shotsUsed - 1;
        }
        if (shotsUsed >= Volatile.Read(ref shotsReady)) { shotsMissing++; return -1; }
        var s = shots[shotsUsed]!;
        Buffer.BlockCopy(ram, 0, s.Aram, 0, 0x10000);
        Buffer.BlockCopy(dsp!, 0, s.Dsp, 0, 128);
        Volatile.Write(ref shotsUsed, shotsUsed + 1);
        return shotsUsed - 1;
    }

    /// <summary>After <see cref="Halt"/> (not on the audio thread): the session. <paramref name="endAram"/> / <paramref name="endRegs"/> are the unit's memory now (null: none).</summary>
    public RecSession Finish(byte[]? endAram, byte[]? endRegs, string name, string game, string sha1, long createdUnix, ulong id)
    {
        var s = new RecSession { Name = name, Game = game, Sha1 = sha1, CreatedUnix = createdUnix, Id = id, StartSample = startSample, EndedBy = endedBy, HeldAtStart = heldAtStart };
        s.Length = (uint)Math.Clamp(lastSample - startSample, 0, uint.MaxValue);
        Array.Copy(baseRegs, s.BaseRegs, 128);
        long n = count;
        s.Times = new uint[n]; s.RegVals = new ushort[n];
        for (long i = 0; i < n; i++)
        {
            var c = chunks[i / ChunkWrites]!;
            s.Times[i] = c.Time[i % ChunkWrites]; s.RegVals[i] = c.RegVal[i % ChunkWrites];
        }
        if (n > 0 && s.Times[n - 1] > s.Length) s.Length = s.Times[n - 1];
        s.Versions = versions.AsSpan(0, versionCount).ToArray();
        s.Shots = shots.AsSpan(0, shotsUsed).ToArray()!;
        s.Dropped = dropped; s.ShotsMissing = shotsMissing; s.VersionsOver = versionsOver;
        if (endAram != null && endRegs != null) { var e = new RecShot(); Buffer.BlockCopy(endAram, 0, e.Aram, 0, 0x10000); Buffer.BlockCopy(endRegs, 0, e.Dsp, 0, 128); s.End = e; }
        // instruments with no copy of their own (the copies ran out) take their bytes from the memory at the end: the transcription says so (ShotsMissing)
        return s;
    }
}
