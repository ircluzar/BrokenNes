using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace NesEmulator.Mix;

/// <summary>
/// The losses of a picture bridge, counted and named, never hidden (the project rule: a lossy translation says what it dropped). Every bridge adds to
/// its counters while it translates a frame; the totals are what the status line and the HTTP API show, the per-frame values are for tools and tests.
/// A counter is created the first time it is added to, so a bridge that never loses a thing has an empty list - and says so. Safe to read from another thread
/// (the app's HTTP server reads while the emulation thread counts).
/// </summary>
public sealed class BridgeCounters
{
    private readonly object gate = new();
    private readonly Dictionary<string, long> total = new(StringComparer.Ordinal), frame = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> notes = new(StringComparer.Ordinal);
    private long frames;

    /// <summary>Frames translated so far.</summary>
    public long Frames { get { lock (gate) return frames; } }

    /// <summary>Start a frame: the per-frame values reset, the totals keep growing.</summary>
    public void BeginFrame() { lock (gate) { frame.Clear(); frames++; } }

    /// <summary>Count <paramref name="n"/> of <paramref name="name"/> (nothing is recorded for zero).</summary>
    public void Add(string name, long n = 1)
    {
        if (n == 0) return;
        lock (gate)
        {
            total[name] = total.GetValueOrDefault(name) + n;
            frame[name] = frame.GetValueOrDefault(name) + n;
        }
    }

    /// <summary>Set a counter for this frame only to <paramref name="n"/> (a level, not an event: "tiles in use"); the totals keep the largest.</summary>
    public void Level(string name, long n)
    {
        lock (gate)
        {
            if (n == 0 && !frame.ContainsKey(name)) return;
            frame[name] = n;
            if (n > total.GetValueOrDefault(name)) total[name] = n;
        }
    }

    /// <summary>One line of text for a thing worth saying but not worth counting.</summary>
    public void Note(string key, string text) { lock (gate) notes[key] = text; }

    public long Total(string name) { lock (gate) return total.GetValueOrDefault(name); }
    public long Frame(string name) { lock (gate) return frame.GetValueOrDefault(name); }
    /// <summary>A copy of the totals (safe to enumerate).</summary>
    public IReadOnlyDictionary<string, long> Totals { get { lock (gate) return new Dictionary<string, long>(total); } }
    /// <summary>A copy of this frame's values.</summary>
    public IReadOnlyDictionary<string, long> FrameValues { get { lock (gate) return new Dictionary<string, long>(frame); } }
    public IReadOnlyDictionary<string, string> Notes { get { lock (gate) return new Dictionary<string, string>(notes); } }

    public void Clear() { lock (gate) { total.Clear(); frame.Clear(); notes.Clear(); frames = 0; } }

    /// <summary>"name total" for every counter that is not zero, largest first - the status-line form.</summary>
    public string Summary(int max = 8)
    {
        lock (gate)
        {
            if (total.Count == 0 && notes.Count == 0) return frames == 0 ? "no frames yet" : "nothing lost";
            var sb = new StringBuilder();
            foreach (var kv in total.OrderByDescending(k => k.Value).Take(max)) sb.Append(sb.Length == 0 ? "" : ", ").Append(kv.Key).Append(' ').Append(kv.Value);
            if (total.Count > max) sb.Append(", +").Append(total.Count - max).Append(" more");
            foreach (var n in notes.Values) sb.Append(sb.Length == 0 ? "" : "; ").Append(n);
            return sb.ToString();
        }
    }

    public override string ToString() => Summary(int.MaxValue);
}

/// <summary>
/// What a session or a core exposes when its picture crosses a bridge: a name, the counters, and the two pictures a fidelity measurement compares - the
/// bridge's own and the source chip's, placed on the same canvas: the NES's 256 x 240 on a stock NES chip, the guest's own picture size on an enhanced chip.
/// </summary>
public interface IPictureBridgeInfo
{
    /// <summary>What the bridge is, in a few words ("Master System on NES:FIXS").</summary>
    string BridgeName { get; }
    BridgeCounters Counters { get; }
    /// <summary>The source chip's own picture (the Sega VDP's, or the front NES chip's), placed on the bridge's canvas: 0xAARRGGBB, <see cref="CanvasWidth"/> x <see cref="CanvasHeight"/>.</summary>
    uint[] ReferenceCanvas { get; }
    /// <summary>The bridged picture, same canvas and format.</summary>
    uint[] BridgeCanvas { get; }
    int CanvasWidth { get; }
    int CanvasHeight { get; }
    /// <summary>The part of the canvas the bridge draws into and the source picture covers: x, y, width, height.</summary>
    (int X, int Y, int W, int H) Region { get; }
    /// <summary>True when the reference was drawn from the very state the bridged picture was made from (so the two can be compared frame for frame).</summary>
    bool ReferenceIsSameFrame { get; }
}
