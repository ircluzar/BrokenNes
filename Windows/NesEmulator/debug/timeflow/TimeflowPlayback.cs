using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NesEmulator.Systems;

namespace NesEmulator.Debugging.Timeflow;

/// <summary>What a replay from a restored frame found: in sync with the recording to its end (or the frames asked), or the first frame off.</summary>
public sealed class TimeflowReplayReport
{
    public long From, To;
    public long FramesRun;
    public long Checked;
    /// <summary>The first frame whose picture, RAM or state differs from the recording's, or null.</summary>
    public long? OffFrame;
    public string OffWhat = "";
    public bool InSync => OffFrame == null;
    /// <summary>The NES's first picture after the jump was the one before it (a NES state holds no picture): not compared, said.</summary>
    public bool PictureHeld;
}

/// <summary>
/// Rewind and resume (milestone T2 of docs/projects/brokendev-timeflow-plan.md): put a machine at any recorded frame (<see cref="Jump"/>: the
/// state restored from its keyframe and deltas, loaded, and taken again to prove the machine holds exactly the recorded state), then go on
/// with the recorded inputs (<see cref="Replay"/>, checked frame by frame against the recording) or with live input (a branch: the host starts
/// a new recording with <see cref="BranchOptions"/>, whose parent is this timeline at that frame).
/// </summary>
public sealed class TimeflowPlayback : IDisposable
{
    public TimelineReader Reader { get; }
    public TimelineMeta Meta => Reader.Meta;
    public string Dir => Reader.Dir;

    public TimeflowPlayback(string timelineDir) { Reader = new TimelineReader(timelineDir); }

    /// <summary>Refuses a machine that is not the timeline's: another console, another ROM (by sha256).</summary>
    public void CheckMachine(TimeflowSource target, string romSha256)
    {
        if (!string.Equals(target.ConsoleKey, Meta.Console, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"timeline {Meta.Id} was recorded on the {Meta.Console}, this machine is the {target.ConsoleKey}");
        if (!string.Equals(romSha256, Meta.RomSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"timeline {Meta.Id} was recorded on {Meta.RomName} ({Meta.RomSha256[..12]}), this machine runs another ROM ({(romSha256.Length >= 12 ? romSha256[..12] : romSha256)}): rewinding into another build is T5's hotpatch, not a jump");
    }

    /// <summary>
    /// Puts <paramref name="target"/> at the start of <paramref name="frame"/>: restores the state, loads it, and takes it again: the machine's
    /// own state must hash the same as the recorded one (else <see cref="InvalidDataException"/> with both hashes: never a silent near miss).
    /// </summary>
    public void Jump(TimeflowSource target, long frame)
    {
        var st = Reader.State(frame);
        target.ForgetHeader();
        target.Restore(st);
        var again = target.Capture();
        ulong want = Reader.Row(frame).State, got = again.Hash();
        if (got != want)
            throw new InvalidDataException($"timeline {Meta.Id}: the machine loaded frame {frame}'s state, but its own state then hashes {got:x16}, not the recorded {want:x16}{Explain(st, again)}");
        TimeflowStore.Touch(Dir);
    }

    private static string Explain(TimeflowState want, TimeflowState got)
    {
        if (!want.SameLayout(got)) return " (the layout differs: the machine is not the one recorded)";
        if (!want.Skeleton.AsSpan().SequenceEqual(got.Skeleton)) return $" (the registers / timing differ: {Where(want.Skeleton, got.Skeleton, true)})";
        for (int i = 0; i < want.Sections.Length; i++)
            if (!want.Sections[i].AsSpan().SequenceEqual(got.Sections[i])) return $" (section {want.Names[i]} differs: {Where(want.Sections[i], got.Sections[i], want.Names[i] != "payload" && false)})";
        return "";
    }

    /// <summary>Where two byte strings first differ: the offset, and (text) the recorded and the loaded text around it.</summary>
    public static string Where(byte[] a, byte[] b, bool text)
    {
        int n = Math.Min(a.Length, b.Length), i = 0;
        while (i < n && a[i] == b[i]) i++;
        int diffs = 0; for (int k = 0; k < n; k++) if (a[k] != b[k]) diffs++;
        if (!text) return $"first at byte {i} of {a.Length}, {diffs} bytes differ";
        int from = Math.Max(0, i - 60);
        string Snip(byte[] x) => System.Text.Encoding.UTF8.GetString(x, from, Math.Min(x.Length - from, 120)).Replace('\u0001', '#');
        return $"at {i}: recorded ...{Snip(a)}... loaded ...{Snip(b)}...";
    }

    /// <summary>
    /// After a <see cref="Jump"/> to <paramref name="from"/>: runs the recorded inputs up to <paramref name="to"/> (exclusive; at most the
    /// recording's end) with <paramref name="runFrame"/> (sets both pads, runs one frame), and checks each frame's picture and RAM hashes, and
    /// the whole state at every keyframe, against the recording. Stops at the first frame off when <paramref name="stopWhenOff"/>.
    /// </summary>
    public TimeflowReplayReport Replay(TimeflowSource target, long from, long to, Action<PadButtons, PadButtons> runFrame, bool stopWhenOff = true)
    {
        to = Math.Min(to, Reader.EndFrame);
        var r = new TimeflowReplayReport { From = from, To = to };
        for (long f = from; f < to; f++)
        {
            var row = Reader.Row(f);
            if (f > from && row.IsKey)
            {
                var now = target.Capture();
                ulong h = now.Hash();
                r.Checked++;
                if (h != row.State)
                {
                    string why = "";
                    try { why = Explain(Reader.State(f), now); } catch (InvalidDataException) { }
                    Off(r, f, "the whole state at a keyframe" + why);
                    if (stopWhenOff) break;
                }
            }
            runFrame((PadButtons)row.P1, (PadButtons)row.P2);
            r.FramesRun++;
            ulong pic = target.PictureHash(), ram = target.RamHash();
            r.Checked++;
            // the NES does not keep its picture in a state (the render target is not saved): after a load it shows the last whole picture
            // until the loaded machine has drawn one, so the first frame's picture is not compared (its RAM is; from the second frame on, both)
            bool heldPicture = f == from && Meta.Console == "nes" && pic != row.Picture && ram == row.Ram;
            if (heldPicture) { r.PictureHeld = true; continue; }
            if (pic != row.Picture || ram != row.Ram)
            {
                Off(r, f, (pic != row.Picture ? "the picture" : "") + (pic != row.Picture && ram != row.Ram ? " and " : "") + (ram != row.Ram ? "the work RAM" : "") + " after the frame");
                if (stopWhenOff) break;
            }
        }
        TimeflowStore.Touch(Dir);
        return r;
    }

    private static void Off(TimeflowReplayReport r, long f, string what) { if (r.OffFrame == null) { r.OffFrame = f; r.OffWhat = what; } }

    /// <summary>The options of a branch: a new timeline whose first frame is <paramref name="frame"/> of this one (the host fills in the machine's parts).</summary>
    public TimeflowOptions BranchOptions(long frame, byte[] rom)
    {
        var o = new TimeflowOptions
        {
            Rom = rom, RomName = Meta.RomName, Cores = Meta.Cores, PowerOn = Meta.PowerOn, Ntsc = Meta.Ntsc, GameDir = TimeflowStore.GameDirOf(Dir),
            StartFrame = frame, PowerOnStart = Meta.PowerOnStart && frame == Meta.StartFrame, ParentId = Meta.Id, ParentFrame = frame,
            KeyEvery = Meta.KeyEvery, PageSize = Meta.PageSize, Compression = Meta.Compression, Level = Meta.Level, VarNames = Meta.Vars.ToList(),
        };
        return o;
    }

    public void Dispose() => Reader.Dispose();

    /// <summary>A listing of a game's timelines as a tree of branches (a branch under its parent, by frame).</summary>
    public static IEnumerable<(TimelineInfo T, int Depth)> Tree(IReadOnlyList<TimelineInfo> all)
    {
        var byParent = all.Where(t => t.Meta.ParentId != null).GroupBy(t => t.Meta.ParentId!).ToDictionary(g => g.Key, g => g.OrderBy(t => t.Meta.ParentFrame).ToList());
        var ids = all.Select(t => t.Meta.Id).ToHashSet();
        IEnumerable<(TimelineInfo, int)> Walk(TimelineInfo t, int d)
        {
            yield return (t, d);
            if (byParent.TryGetValue(t.Meta.Id, out var kids)) foreach (var k in kids) foreach (var x in Walk(k, d + 1)) yield return x;
        }
        foreach (var root in all.Where(t => t.Meta.ParentId == null || !ids.Contains(t.Meta.ParentId)).OrderBy(t => t.Meta.CreatedUtc))
            foreach (var x in Walk(root, 0)) yield return x;
    }
}
