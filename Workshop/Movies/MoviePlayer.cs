using System;
using System.Collections.Generic;
using System.Linq;
using NesEmulator.Debugging.Movies;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Movies;

/// <summary>What a replay found.</summary>
internal sealed class PlayReport
{
    public long FramesRun;
    public bool Crashed; public string CrashInfo = "";
    public bool SameRom;
    public int KeysChecked, KeysMatched;
    /// <summary>The first keyframe that did not match (frames run when checked), or null.</summary>
    public long? OffCourseFrame;
    public string OffCourseWhat = "";
    /// <summary>The first keyframe whose course variables did not match the recording (bisect's "can't tell"), or null.</summary>
    public long? CourseLostFrame;
    public string CourseLostWhat = "";
    /// <summary>Why the course variables were not checked (no symbols, no debugger), or null when they were (or the movie has none).</summary>
    public string? CourseNote;
    public bool InSync => OffCourseFrame == null && !Crashed;
}

/// <summary>Records and replays <see cref="Movie"/>s on a <see cref="MovieMachine"/>.</summary>
internal static class MoviePlayer
{
    /// <summary>
    /// Records a movie from pad scripts (BrokenDev verify's "120:Start,132:" syntax) for <paramref name="frames"/> frames on a freshly opened
    /// machine: the input, the keyframe hashes and the course variables. <paramref name="fromState"/>: a state loaded first (the movie then
    /// starts from it).
    /// </summary>
    public static Movie Record(MovieMachine m, string script1, string script2, long frames, int keyEvery, IEnumerable<(string, string)> vars, string build, byte[]? fromState = null)
    {
        var mv = NewFor(m, frames, keyEvery, build);
        foreach (var v in vars) mv.Vars.Add(v);
        if (fromState != null) { m.Host.LoadState(fromState); mv.Start = MovieStart.State; mv.StartState = fromState; }
        var s1 = Movie.ParseScript(script1); var s2 = Movie.ParseScript(script2);
        if (s1.Keys.Concat(s2.Keys).Any(f => f >= frames)) throw new ArgumentException($"an input step is at or past frame {frames} (the movie's last frame is {frames - 1})");
        PadButtons p1 = PadButtons.None, p2 = PadButtons.None;
        for (long f = 0; f < frames; f++)
        {
            if (s1.TryGetValue(f, out var a)) p1 = a;
            if (s2.TryGetValue(f, out var b)) p2 = b;
            mv.SetPads(f, p1, p2);
            m.Frame(p1, p2);
            if (m.Crashed) throw new InvalidOperationException($"the game crashed at frame {f + 1} while recording: {m.CrashInfo}");
            if (Movie.IsKeyFrame(f + 1, frames, keyEvery)) mv.Keys.Add(new MovieKey { Frame = f + 1, Picture = m.Picture(), Ram = m.Ram(), Vars = m.EvalVars(mv) });
        }
        return mv;
    }

    /// <summary>A movie's header for this machine (no input yet).</summary>
    public static Movie NewFor(MovieMachine m, long frames, int keyEvery, string build) => new()
    {
        Console = m.ConsoleKey, RomName = m.RomName, RomSha256 = m.RomSha256, RomBytes = m.RomFile.Length, Build = build,
        Emulator = MovieMachine.EmulatorBuild(), Cores = m.Cores, NtscTiming = m.PowerOnNtsc, PowerOn = m.PowerOn, Frames = frames, KeyEvery = keyEvery,
    };

    /// <summary>
    /// Replays a movie on a machine opened for it (<see cref="MovieMachine.OpenFor"/>), checking every keyframe: the picture and RAM hashes
    /// (off course) and the course variables (course lost). <paramref name="frames"/>: stop early (-1: the whole movie).
    /// <paramref name="afterFrame"/> runs after each frame with the number of frames run (a test reads the machine there).
    /// </summary>
    public static PlayReport Play(Movie mv, MovieMachine m, long frames = -1, Action<long>? afterFrame = null, bool stopWhenOffCourse = false)
    {
        var r = new PlayReport { SameRom = m.RomSha256.Equals(mv.RomSha256, StringComparison.OrdinalIgnoreCase) };
        long end = frames < 0 ? mv.Frames : Math.Min(frames, mv.Frames);
        int ki = 0;
        var cursor = 0;
        bool course = mv.Vars.Count > 0;
        if (course && !m.HasDebugger) { course = false; r.CourseNote = "the course is not checked: the debugger does not cover this console"; }
        else if (course)
        {
            try { m.EvalVars(mv); }
            catch (Exception e) when (e is ArgumentException or FormatException or InvalidOperationException) { course = false; r.CourseNote = $"the course is not checked: {e.Message} (give the game's symbols)"; }
        }
        PadButtons p1 = PadButtons.None, p2 = PadButtons.None;
        for (long f = 0; f < end; f++)
        {
            while (cursor < mv.Inputs.Count && mv.Inputs[cursor].Frame <= f) { p1 = mv.Inputs[cursor].P1; p2 = mv.Inputs[cursor].P2; cursor++; }
            m.Frame(p1, p2);
            r.FramesRun = f + 1;
            if (m.Crashed) { r.Crashed = true; r.CrashInfo = m.CrashInfo; break; }
            while (ki < mv.Keys.Count && mv.Keys[ki].Frame < f + 1) ki++;
            if (ki < mv.Keys.Count && mv.Keys[ki].Frame == f + 1)
            {
                var k = mv.Keys[ki];
                r.KeysChecked++;
                ulong pic = m.Picture(), ram = m.Ram();
                bool ok = pic == k.Picture && ram == k.Ram;
                if (ok) r.KeysMatched++;
                else if (r.OffCourseFrame == null)
                {
                    r.OffCourseFrame = f + 1;
                    r.OffCourseWhat = (pic != k.Picture ? $"picture {pic:X16} (recorded {k.Picture:X16})" : "") + (pic != k.Picture && ram != k.Ram ? ", " : "") + (ram != k.Ram ? $"RAM {ram:X16} (recorded {k.Ram:X16})" : "");
                }
                if (course && r.CourseLostFrame == null)
                {
                    var now = m.EvalVars(mv);
                    for (int i = 0; i < now.Length; i++)
                        if (now[i] != k.Vars[i]) { r.CourseLostFrame = f + 1; r.CourseLostWhat = $"{mv.Vars[i].Name} = {now[i]} (recorded {k.Vars[i]})"; break; }
                }
                if (stopWhenOffCourse && r.OffCourseFrame != null) { afterFrame?.Invoke(f + 1); break; }
            }
            afterFrame?.Invoke(f + 1);
        }
        return r;
    }

    /// <summary>The pads a movie holds at each frame, as a running cursor (cheaper than <see cref="Movie.PadsAt"/> frame by frame).</summary>
    public sealed class Cursor
    {
        private readonly Movie mv; private int i; public PadButtons P1, P2;
        public Cursor(Movie m) { mv = m; }
        public void At(long f)
        {
            if (i > 0 && mv.Inputs[i - 1].Frame > f) { i = 0; P1 = P2 = PadButtons.None; }
            while (i < mv.Inputs.Count && mv.Inputs[i].Frame <= f) { P1 = mv.Inputs[i].P1; P2 = mv.Inputs[i].P2; i++; }
        }
    }
}
