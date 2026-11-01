using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NesEmulator.Systems;

namespace NesEmulator.Corruption;

/// <summary>
/// A pad per frame as text: the frames at which the held buttons change, "60:Start,90:Right+A,120:" (frame 60 on, Start is held; from frame 90 Right and A; from frame 120 nothing). The frame is the 0-based number of the
/// <c>RunFrame</c> call it first applies to; before the first entry nothing is held. This is the syntax of the Workshop's <c>--console-run --input</c>, so a recorded script can be replayed there.
/// </summary>
public static class InputScript
{
    private static readonly (PadButtons Button, string Name)[] Names =
    {
        (PadButtons.Up, "Up"), (PadButtons.Down, "Down"), (PadButtons.Left, "Left"), (PadButtons.Right, "Right"),
        (PadButtons.A, "A"), (PadButtons.B, "B"), (PadButtons.X, "X"), (PadButtons.Y, "Y"), (PadButtons.L, "L"), (PadButtons.R, "R"),
        (PadButtons.Start, "Start"), (PadButtons.Select, "Select"), (PadButtons.C, "C"), (PadButtons.Z, "Z"), (PadButtons.Mode, "Mode"),
    };

    public static string Name(PadButtons b)
    {
        if (b == PadButtons.None) return "";
        var sb = new StringBuilder();
        foreach (var (button, name) in Names) if ((b & button) != 0) { if (sb.Length > 0) sb.Append('+'); sb.Append(name); }
        return sb.ToString();
    }

    public static bool TryParse(string? text, out List<(int Frame, ushort Bits)> script, out string error)
    {
        script = new List<(int, ushort)>(); error = "";
        if (string.IsNullOrWhiteSpace(text)) return true;
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int colon = part.IndexOf(':');
            if (colon < 0 || !int.TryParse(part.AsSpan(0, colon), out int frame) || frame < 0) { error = $"'{part}' is not frame:buttons"; return false; }
            ushort bits = 0;
            foreach (var name in part[(colon + 1)..].Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!Enum.TryParse<PadButtons>(name, true, out var b) || !Enum.IsDefined(b)) { error = $"'{name}' is not a button"; return false; }
                bits |= (ushort)b;
            }
            script.Add((frame, bits));
        }
        // sorted by frame; a later entry for the same frame wins
        script = script.Select((e, i) => (e, i)).OrderBy(x => x.e.Frame).ThenBy(x => x.i).Select(x => x.e).ToList();
        return true;
    }

    public static string Format(IReadOnlyList<(int Frame, ushort Bits)> script) =>
        string.Join(",", script.Select(e => $"{e.Frame}:{Name((PadButtons)e.Bits)}"));

    /// <summary>The buttons held at <paramref name="frame"/> (a binary search; <see cref="Player"/> walks a script in order without one).</summary>
    public static ushort BitsAt(IReadOnlyList<(int Frame, ushort Bits)> script, int frame)
    {
        int lo = 0, hi = script.Count - 1, found = -1;
        while (lo <= hi) { int mid = (lo + hi) / 2; if (script[mid].Frame <= frame) { found = mid; lo = mid + 1; } else hi = mid - 1; }
        return found < 0 ? (ushort)0 : script[found].Bits;
    }

    /// <summary>Walks a parsed script frame by frame.</summary>
    public sealed class Player
    {
        private readonly List<(int Frame, ushort Bits)> script;
        private int next;
        private ushort held;
        public Player(List<(int Frame, ushort Bits)> script) { this.script = script; }
        /// <summary>The buttons for the given frame; frames must be asked for in rising order.</summary>
        public ushort Bits(int frame)
        {
            while (next < script.Count && script[next].Frame <= frame) held = script[next++].Bits;
            return held;
        }
    }
}

/// <summary>
/// Remembers the pad a game was handed, frame by frame, as the changes only. The arrays are made once, so recording a frame never allocates (it is called from an audio thread). When the log fills, or something
/// happens that a replay could not reproduce, the recording is <see cref="Poisoned"/> with the reason, and stays so until <see cref="Reset"/>.
/// </summary>
public sealed class InputRecorder
{
    private readonly int[] frames_;
    private readonly ushort[] bits;
    private int count;
    private ushort last;
    private string? poison;
    private int framesSeen;

    public InputRecorder(int capacity = 4096) { frames_ = new int[capacity]; bits = new ushort[capacity]; }

    /// <summary>Why the recording cannot be replayed (null: it can).</summary>
    public string? Poisoned => poison;
    /// <summary>The number of frames recorded so far (the next frame's number).</summary>
    public int Frames => framesSeen;
    public int Changes => count;

    public void Reset() { count = 0; last = 0; poison = null; framesSeen = 0; }

    public void Poison(string why) { poison ??= why; }

    /// <summary>The pad handed to the game for the next frame. Call once per <c>RunFrame</c>, before it.</summary>
    public void Record(ushort pad)
    {
        if (pad != last)
        {
            if (count == frames_.Length) { poison ??= "the input log is full"; }
            else { frames_[count] = framesSeen; bits[count] = pad; count++; last = pad; }
        }
        framesSeen++;
    }

    /// <summary>
    /// Starts the recording as if the pad had followed <paramref name="script"/> for <paramref name="frames"/> frames: used when a machine was rebuilt by replaying that script, so that what is played next
    /// is recorded after it. A script longer than the log can hold poisons the recording.
    /// </summary>
    public void Prime(IReadOnlyList<(int Frame, ushort Bits)> script, int frames)
    {
        Reset();
        foreach (var (f, b) in script)
        {
            if (f >= frames) break;
            if (count == frames_.Length) { poison ??= "the input log is full"; break; }
            this.frames_[count] = f; bits[count] = b; count++;
        }
        last = InputScript.BitsAt(script, Math.Max(0, frames - 1));
        framesSeen = frames;
    }

    public List<(int Frame, ushort Bits)> ToList()
    {
        var l = new List<(int, ushort)>(count);
        for (int i = 0; i < count; i++) l.Add((frames_[i], bits[i]));
        return l;
    }

    public string ToScript() => InputScript.Format(ToList());
}
