// Round 8 `reload` (2026-10-18): the instance's side of FL's real conventions (Record/FlNotes.cs) and of following a track's notes without a song position (Record/RecTracks.cs). The test hooks
// 790-819: the channel's base key, what the matcher made of the last note, a log of every note (which recorded note each FL voice was), a recording offered from its files.
using BrokenNes2.Record;

namespace BrokenNes2;

/// <summary>Every note an instance was asked to play and what it made of it (the self-test reads it): preallocated, filled by the audio thread without allocating.</summary>
internal sealed class NoteFollowLog
{
    public const int Fields = 8;
    public readonly int[] Data;
    public int Count;
    public long Lost;
    public NoteFollowLog(int capacity) { Data = new int[Math.Max(1, capacity) * Fields]; }

    public void Add(int handle, int recNote, int inTrack, int trackKey, int track, int baseKey, int flags, int tick)
    {
        if ((Count + 1) * Fields > Data.Length) { Lost++; return; }
        int o = Count * Fields;
        Data[o] = handle; Data[o + 1] = recNote; Data[o + 2] = inTrack; Data[o + 3] = trackKey; Data[o + 4] = track; Data[o + 5] = baseKey; Data[o + 6] = flags; Data[o + 7] = tick;
        Count++;
    }
}

public sealed unsafe partial class Bn2Plugin
{
    private volatile NoteFollowLog? noteLog;
    private static byte[]? gcGarbage;                     // (hook 810: the last large array made, so the runtime cannot skip making it)

    /// <summary>The note log's line for voice <paramref name="v"/> (the audio thread, after OnNote).</summary>
    private void LogFollow(Voice v, TrackIndex? idx)
    {
        var l = noteLog;
        if (l == null) return;
        int inTrack = idx != null && matcher.NoteAt >= 0 && matcher.Bound >= 0 ? NoteInTrack(idx, matcher.Bound, matcher.NoteAt) : -1;
        int flags = (matcher.Settled ? 1 : 0) | (matcher.NoteSure ? 2 : 0) | (matcher.Aligned ? 4 : 0) | (matcher.ByColour ? 8 : 0) | (matcher.BaseSure ? 16 : 0) | (matcher.Unsure ? 32 : 0);
        int tick = idx != null && matcher.NoteAt >= 0 ? (int)Math.Round(idx.Beat[matcher.NoteAt] * RecMidi.Ppq) : -1;
        l.Add((int)(v.Handle & 0xFFFF), matcher.NoteAt, inTrack, v.TrackKey, matcher.Bound, pitchBase, flags, tick);
    }

    /// <summary>790 the channel's base key, 791 the matcher's base (-1), 792 whether it is sure, 793 the recorded note of the last note (TrackIndex, -1), 794 jumps (seeds after the first), 795 notes the
    /// recording does not have, 796 the colour named the candidates, 797 the last seed was aligned with FL's position or the song's start, 798 settled, 799 how (1 colour, 2 keys, 3 given),
    /// 800 offer a recording from its files (value: a UTF-16 "midi path|preset folder" (no folder: the library's); 0 offered, 1 refused: the words in 643 field 8), 801 the note log on (value:
    /// capacity; 0 off), 802 read it (index: max records, value: a buffer of 8 ints per note: handle, the recorded note the matcher named (-1 none), its number in the track, the key played with the
    /// recorded expression (-1: FL's own pitch), track, base, flags, the recorded note's tick; returns the count),
    /// 803 the note log's lost count, 804 following a known track (1/0), 805 FL's transport playing (1/0), 806 the song position FL gives now x 1000 (-1: none), 807 the matcher's candidates.</summary>
    private nint ReloadTestHook(int n, nint index, nint value)
    {
        var emu = assignment.Emulator;
        switch (n)
        {
            case 790: return pitchBase;
            case 791: return matcher.Base;
            case 792: return matcher.BaseSure ? 1 : 0;
            case 793: return matcher.NoteAt;
            case 794: return matcher.Jumps;
            case 795: return matcher.Unexplained;
            case 796: return matcher.ByColour ? 1 : 0;
            case 797: return matcher.Aligned ? 1 : 0;
            case 798: return matcher.Settled ? 1 : 0;
            case 799: return matcher.How;
            case 800:
            {
                var arg = System.Runtime.InteropServices.Marshal.PtrToStringUni(value) ?? "";
                var parts = arg.Split('|');
                if (emu == null) { lastRecordResult = "no emulator"; return 1; }
                MarkConfigured("it holds a recording read back from its files");
                var (ok, msg) = emu.OfferFromFiles(parts[0], parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null);
                lastRecordResult = msg;
                return ok ? 0 : 1;
            }
            case 801: noteLog = value > 0 ? new NoteFollowLog((int)value) : null; return 0;
            case 802:
            {
                var l = noteLog;
                if (l == null) return -1;
                if (value == 0) return l.Count;
                int nrec = Math.Min(l.Count, (int)index);
                new ReadOnlySpan<int>(l.Data, 0, nrec * NoteFollowLog.Fields).CopyTo(new Span<int>((void*)value, nrec * NoteFollowLog.Fields));
                return nrec;
            }
            case 803: return (nint)(noteLog?.Lost ?? -1);
            case 804: return follow != null ? 1 : 0;
            case 805: return transportPlaying ? 1 : 0;
            case 806: { double p = SongPosition(out _, out _); return double.IsNaN(p) ? -1 : (nint)Math.Round(p * 1000); }
            case 807: return matcher.Candidates;
            case 808: GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); return 0;                                                       // a collection in the DLL's runtime (before an allocation window: the per-thread counter is not exact across one)
            case 809: return GC.CollectionCount(index is >= 0 and <= 2 ? (int)index : 0);                                                          // the DLL runtime's collections of generation <index> so far
            case 810: { for (int i = 0; i < value; i++) gcGarbage = new byte[128 * 1024]; return 0; }                                               // <value> large arrays made and dropped (they start the runtime's own collections, background ones included)
            case 811: { if (assignment.Emulator is not { } e) return 1; e.HoldRomLockForTests((int)value); return 0; }                               // holds this instance's emulator's game lock for <value> ms (the caller's thread)
            default: return 0;
        }
    }
}
