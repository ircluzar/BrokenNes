using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using NesEmulator.Corruption;
using NesEmulator.Systems;

namespace NesEmulator.Debugging.Movies;

/// <summary>
/// The name and file extension of BrokenNes's own movie format (milestone D5 of docs/projects/brokendev-debugging-plan.md). Open question 2
/// of the plan: the name is a WORKING name, kept here and nowhere else, so renaming the format is a change of these constants (the reader
/// also accepts the files written under an earlier name listed in <see cref="FormerMagics"/>).
/// </summary>
public static class MovieFormat
{
    /// <summary>What the format is called in messages and documents.</summary>
    public const string Name = "BrokenNes movie";
    /// <summary>The file extension, with its dot.</summary>
    public const string Extension = ".bnm";
    /// <summary>The first word of the file's first line.</summary>
    public const string Magic = "bnmovie";
    public const int Version = 1;
    /// <summary>First words the reader also accepts (files written before a rename).</summary>
    public static readonly string[] FormerMagics = Array.Empty<string>();
}

/// <summary>Where a movie starts.</summary>
public enum MovieStart { PowerOn, State }

/// <summary>A pad change: from <see cref="Frame"/> on (0 = the first frame run), pad 1 and pad 2 hold these buttons.</summary>
public readonly record struct MovieInput(long Frame, PadButtons P1, PadButtons P2);

/// <summary>A keyframe: after <see cref="Frame"/> frames have run, the picture and work-RAM hashes (and the course variables, if any).</summary>
public sealed class MovieKey
{
    public long Frame;
    public ulong Picture, Ram;
    public long[] Vars = Array.Empty<long>();
}

/// <summary>A marker at a frame (the debug port's markers of D7; any text).</summary>
public readonly record struct MovieMark(long Frame, string Text);

/// <summary>
/// A movie in BrokenNes's own format (<see cref="MovieFormat"/>): the ROM and the build it was made with, the console, the cores, the power-on
/// RAM, the start point (power-on or a save state), every frame's input for two pads, and every <see cref="KeyEvery"/> frames a keyframe: the
/// picture and work-RAM hashes (<see cref="MovieHash"/>), and the values of the movie's course variables (expressions on the game's symbols),
/// so a replay knows the frame it went off course. Plain text, one fact per line, so a promoted movie reads well in a diff:
/// <code>
/// bnmovie 1
/// console nes                      the console key (nes, snes, wms, gb, gbc, sms, gg, md)
/// rom vrun_game.nes                the ROM's file name, its sha256 and size
/// romSha256 9f86...                (a replay on another ROM is allowed and said: see BrokenDev bisect)
/// romBytes 524304
/// build &lt;text&gt;                     the game's build (a git commit, a date): free text
/// emulator &lt;text&gt;                  the BrokenNes build that recorded it
/// cores FIX/FIX/FIX                CPU/PPU/APU ids
/// timing 60 | ntsc                 the NES's frame length (BrokenNes's 60 fps frames, or NTSC's 60.0988)
/// powerOn fceux | zeros | ones     the NES's power-on RAM (the other consoles: their own)
/// start power-on | state           with a state: one 'state' line, gzip + base64
/// frames 2400                      the movie's length
/// keyEvery 60
/// var hp [player_hp]               a course variable: a name and a debugger expression
/// i 0 - -                          input: from frame 0, pad 1 holds nothing, pad 2 nothing
/// i 120 Start -                    from frame 120, pad 1 holds Start
/// k 60 1A2B..(16) 3C4D..(16) 0 1   keyframe after 60 frames: picture, RAM, the course variables in order
/// m 300 boss room                  a marker
/// comment free text
/// </code>
/// </summary>
public sealed class Movie
{
    public string Console = "nes";
    public string RomName = "";
    public string RomSha256 = "";
    public long RomBytes;
    public string Build = "";
    public string Emulator = "";
    public string Cores = "";
    public bool NtscTiming;
    public string PowerOn = "fceux";
    public MovieStart Start = MovieStart.PowerOn;
    public byte[]? StartState;
    public long Frames;
    public int KeyEvery = 60;
    public readonly List<(string Name, string Expr)> Vars = new();
    public readonly List<MovieInput> Inputs = new();
    public readonly List<MovieKey> Keys = new();
    public readonly List<MovieMark> Marks = new();
    public readonly List<string> Comments = new();

    /// <summary>Keyframes after these numbers of frames: every <see cref="KeyEvery"/>, and the last frame.</summary>
    public static bool IsKeyFrame(long framesDone, long total, int every) => framesDone > 0 && (every > 0 && framesDone % every == 0 || framesDone == total);

    public MovieKey? KeyAt(long framesDone) => Keys.FirstOrDefault(k => k.Frame == framesDone);

    // ------------------------------------------------------------------------------------------------ input

    /// <summary>Adds a pad state for a frame (frames must come in order); a frame whose pads equal the previous change adds nothing.</summary>
    public void SetPads(long frame, PadButtons p1, PadButtons p2)
    {
        if (Inputs.Count > 0)
        {
            var last = Inputs[^1];
            if (frame < last.Frame) throw new ArgumentException($"movie input out of order: frame {frame} after {last.Frame}");
            if (last.P1 == p1 && last.P2 == p2) return;
            if (last.Frame == frame) { Inputs[^1] = new MovieInput(frame, p1, p2); return; }
        }
        else if (p1 == PadButtons.None && p2 == PadButtons.None && frame == 0) { Inputs.Add(new MovieInput(0, p1, p2)); return; }
        Inputs.Add(new MovieInput(frame, p1, p2));
    }

    /// <summary>The pads held during a frame.</summary>
    public (PadButtons P1, PadButtons P2) PadsAt(long frame)
    {
        int lo = 0, hi = Inputs.Count - 1, found = -1;
        while (lo <= hi) { int mid = (lo + hi) >> 1; if (Inputs[mid].Frame <= frame) { found = mid; lo = mid + 1; } else hi = mid - 1; }
        return found < 0 ? (PadButtons.None, PadButtons.None) : (Inputs[found].P1, Inputs[found].P2);
    }

    /// <summary>Whether pad 2 is ever pressed.</summary>
    public bool UsesPad2 => Inputs.Any(i => i.P2 != PadButtons.None);

    /// <summary>One pad's input as BrokenDev verify's script ("120:Start,132:,400:Right").</summary>
    public string ToScript(int player)
    {
        var parts = new List<string>();
        PadButtons prev = PadButtons.None;
        foreach (var i in Inputs)
        {
            var b = player == 2 ? i.P2 : i.P1;
            if (b == prev) continue;
            parts.Add($"{i.Frame.ToString(CultureInfo.InvariantCulture)}:{ButtonsText(b, "+", "")}");
            prev = b;
        }
        return string.Join(",", parts);
    }

    /// <summary>Reads a verify-style script ("120:Start,132:,400:Right+B") for a pad into <paramref name="into"/> (frame -> buttons).</summary>
    public static SortedDictionary<long, PadButtons> ParseScript(string script)
    {
        var d = new SortedDictionary<long, PadButtons>();
        foreach (string part in (script ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int c = part.IndexOf(':');
            if (c < 0 || !long.TryParse(part[..c], NumberStyles.Integer, CultureInfo.InvariantCulture, out long f) || f < 0)
                throw new FormatException($"bad input step '{part}' (expected frame:Buttons, e.g. 120:Start or 132:)");
            d[f] = ParseButtons(part[(c + 1)..]);
        }
        return d;
    }

    /// <summary>"Right+B", "A B", "-" or "" (nothing).</summary>
    public static PadButtons ParseButtons(string text)
    {
        var p = PadButtons.None;
        foreach (string n in (text ?? "").Split(new[] { '+', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (n == "-") continue;
            if (!Enum.TryParse<PadButtons>(n, true, out var v) || v == PadButtons.None || !Enum.IsDefined(v))
                throw new FormatException($"unknown button '{n}' (A, B, X, Y, L, R, C, Z, Mode, Start, Select, Up, Down, Left, Right)");
            p |= v;
        }
        return p;
    }

    private static readonly PadButtons[] Order =
    {
        PadButtons.Up, PadButtons.Down, PadButtons.Left, PadButtons.Right, PadButtons.A, PadButtons.B, PadButtons.X, PadButtons.Y,
        PadButtons.L, PadButtons.R, PadButtons.C, PadButtons.Z, PadButtons.Mode, PadButtons.Start, PadButtons.Select,
    };

    /// <summary>The buttons by name in a fixed order, joined; <paramref name="none"/> when nothing is held.</summary>
    public static string ButtonsText(PadButtons b, string sep = "+", string none = "-")
    {
        if (b == PadButtons.None) return none;
        return string.Join(sep, Order.Where(x => (b & x) != 0).Select(x => x.ToString()));
    }

    // ------------------------------------------------------------------------------------------------ text

    public static Movie Load(string path)
    {
        try { return Parse(File.ReadAllText(path)); }
        catch (FormatException e) { throw new FormatException($"{Path.GetFileName(path)}: {e.Message}", e); }
    }

    public void Save(string path)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir != null) Directory.CreateDirectory(dir);
        File.WriteAllText(path, ToText(), new UTF8Encoding(false));
    }

    public string ToText()
    {
        var sb = new StringBuilder();
        void L(string k, string v) => sb.Append(k).Append(' ').Append(v).Append('\n');
        sb.Append(MovieFormat.Magic).Append(' ').Append(MovieFormat.Version.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("# ").Append(MovieFormat.Name).Append(" (").Append(MovieFormat.Extension).Append("): every frame's input from a start point, keyframes every ")
          .Append(KeyEvery.ToString(CultureInfo.InvariantCulture)).Append(" frames\n");
        L("console", Console);
        L("rom", RomName);
        L("romSha256", RomSha256);
        L("romBytes", RomBytes.ToString(CultureInfo.InvariantCulture));
        if (Build.Length > 0) L("build", OneLine(Build));
        if (Emulator.Length > 0) L("emulator", OneLine(Emulator));
        L("cores", Cores);
        if (Console == "nes") { L("timing", NtscTiming ? "ntsc" : "60"); L("powerOn", PowerOn); }
        L("start", Start == MovieStart.PowerOn ? "power-on" : "state");
        L("frames", Frames.ToString(CultureInfo.InvariantCulture));
        L("keyEvery", KeyEvery.ToString(CultureInfo.InvariantCulture));
        foreach (var c in Comments) L("comment", OneLine(c));
        foreach (var (n, e) in Vars) L("var", n + " " + OneLine(e));
        if (Start == MovieStart.State)
        {
            if (StartState == null) throw new InvalidOperationException("a movie that starts from a state needs the state");
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionLevel.Optimal, true)) gz.Write(StartState, 0, StartState.Length);
            L("state", Convert.ToBase64String(ms.ToArray()));
        }
        foreach (var i in Inputs)
            sb.Append("i ").Append(i.Frame.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(ButtonsText(i.P1)).Append(' ').Append(ButtonsText(i.P2)).Append('\n');
        foreach (var k in Keys)
        {
            sb.Append("k ").Append(k.Frame.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(k.Picture.ToString("X16")).Append(' ').Append(k.Ram.ToString("X16"));
            foreach (var v in k.Vars) sb.Append(' ').Append(v.ToString(CultureInfo.InvariantCulture));
            sb.Append('\n');
        }
        foreach (var m in Marks) sb.Append("m ").Append(m.Frame.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(OneLine(m.Text)).Append('\n');
        sb.Append("end\n");
        return sb.ToString();
    }

    private static string OneLine(string s) => s.Replace("\r", " ").Replace("\n", " ").Trim();

    public static Movie Parse(string text)
    {
        var m = new Movie();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        int n = 0;
        bool sawMagic = false, sawEnd = false, sawFrames = false;
        string? state = null;
        foreach (string raw in lines)
        {
            n++;
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int sp = line.IndexOf(' ');
            string key = sp < 0 ? line : line[..sp], val = sp < 0 ? "" : line[(sp + 1)..].Trim();
            if (!sawMagic)
            {
                if (key != MovieFormat.Magic && !MovieFormat.FormerMagics.Contains(key))
                    throw new FormatException($"not a {MovieFormat.Name}: the first line must start with '{MovieFormat.Magic}' (found '{key}')");
                if (!int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ver) || ver < 1 || ver > MovieFormat.Version)
                    throw new FormatException($"{MovieFormat.Name} version '{val}' is not one this build reads (1 to {MovieFormat.Version})");
                sawMagic = true; continue;
            }
            if (sawEnd) throw new FormatException($"line {n}: text after 'end'");
            try
            {
                switch (key)
                {
                    case "console": m.Console = val.ToLowerInvariant(); break;
                    case "rom": m.RomName = val; break;
                    case "romSha256": m.RomSha256 = val.ToLowerInvariant(); break;
                    case "romBytes": m.RomBytes = long.Parse(val, CultureInfo.InvariantCulture); break;
                    case "build": m.Build = val; break;
                    case "emulator": m.Emulator = val; break;
                    case "cores": m.Cores = val; break;
                    case "timing": m.NtscTiming = val switch { "ntsc" => true, "60" => false, _ => throw new FormatException($"timing '{val}' (60 or ntsc)") }; break;
                    case "powerOn": m.PowerOn = val is "fceux" or "zeros" or "ones" ? val : throw new FormatException($"powerOn '{val}' (fceux, zeros or ones)"); break;
                    case "start": m.Start = val switch { "power-on" => MovieStart.PowerOn, "state" => MovieStart.State, _ => throw new FormatException($"start '{val}' (power-on or state)") }; break;
                    case "frames": m.Frames = long.Parse(val, CultureInfo.InvariantCulture); sawFrames = true; break;
                    case "keyEvery": m.KeyEvery = int.Parse(val, CultureInfo.InvariantCulture); break;
                    case "comment": m.Comments.Add(val); break;
                    case "var":
                    {
                        int s2 = val.IndexOf(' ');
                        if (s2 <= 0) throw new FormatException("var needs a name and an expression");
                        m.Vars.Add((val[..s2], val[(s2 + 1)..].Trim()));
                        break;
                    }
                    case "state": state = val; break;
                    case "i":
                    {
                        var p = val.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (p.Length < 2) throw new FormatException("an input line is 'i <frame> <pad 1> [<pad 2>]'");
                        long f = long.Parse(p[0], CultureInfo.InvariantCulture);
                        if (m.Inputs.Count > 0 && f <= m.Inputs[^1].Frame) throw new FormatException($"input frames must rise ({f} after {m.Inputs[^1].Frame})");
                        m.Inputs.Add(new MovieInput(f, ParseButtons(p[1]), p.Length > 2 ? ParseButtons(p[2]) : PadButtons.None));
                        break;
                    }
                    case "k":
                    {
                        var p = val.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (p.Length < 3) throw new FormatException("a keyframe line is 'k <frame> <picture hash> <RAM hash> [variables]'");
                        var k = new MovieKey
                        {
                            Frame = long.Parse(p[0], CultureInfo.InvariantCulture),
                            Picture = ulong.Parse(p[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                            Ram = ulong.Parse(p[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                            Vars = p.Skip(3).Select(x => long.Parse(x, CultureInfo.InvariantCulture)).ToArray(),
                        };
                        if (m.Keys.Count > 0 && k.Frame <= m.Keys[^1].Frame) throw new FormatException($"keyframes must rise ({k.Frame} after {m.Keys[^1].Frame})");
                        m.Keys.Add(k);
                        break;
                    }
                    case "m":
                    {
                        int s2 = val.IndexOf(' ');
                        m.Marks.Add(new MovieMark(long.Parse(s2 < 0 ? val : val[..s2], CultureInfo.InvariantCulture), s2 < 0 ? "" : val[(s2 + 1)..]));
                        break;
                    }
                    case "end": sawEnd = true; break;
                    default: throw new FormatException($"unknown line '{key}'");
                }
            }
            catch (Exception e) when (e is FormatException or OverflowException)
            {
                throw new FormatException($"line {n}: {e.Message}", e);
            }
        }
        if (!sawMagic) throw new FormatException($"empty file: not a {MovieFormat.Name}");
        if (!sawEnd) throw new FormatException("the movie has no 'end' line (cut short?)");
        if (!sawFrames || m.Frames <= 0) throw new FormatException("the movie has no length ('frames N', N > 0)");
        if (m.Inputs.Count > 0 && m.Inputs[^1].Frame >= m.Frames) throw new FormatException($"an input at frame {m.Inputs[^1].Frame} is past the movie's {m.Frames} frames");
        if (m.Keys.Any(k => k.Vars.Length != m.Vars.Count)) throw new FormatException($"a keyframe has a different number of values than the movie's {m.Vars.Count} course variable(s)");
        if (m.Start == MovieStart.State)
        {
            if (state == null) throw new FormatException("'start state' without a 'state' line");
            using var ms = new MemoryStream(Convert.FromBase64String(state));
            using var gz = new GZipStream(ms, CompressionMode.Decompress);
            using var o = new MemoryStream();
            gz.CopyTo(o);
            m.StartState = o.ToArray();
        }
        else if (state != null) throw new FormatException("a 'state' line in a movie that starts at power-on");
        return m;
    }
}

/// <summary>
/// The hashes a movie's keyframes hold. FNV-1a 64 over the picture (the NES's RGBA framebuffer bytes; a console session's ARGB pixels, the
/// same hash BrokenDev verify's checkpoints and Workshop's --romtest record) and over the console's work RAM (every memory domain of kind
/// Work, in the domains' order: the NES's 2 KB, the SNES's 128 KB, the Game Boy's WRAM and HRAM, ...).
/// </summary>
public static class MovieHash
{
    private const ulong Basis = 14695981039346656037UL, Prime = 1099511628211UL;

    public static ulong Picture(byte[] nesFrameBuffer)
    {
        ulong h = Basis;
        foreach (byte b in nesFrameBuffer) { h ^= b; h *= Prime; }
        return h;
    }

    public static ulong Picture(uint[] frame, int pixels)
    {
        ulong h = Basis;
        for (int i = 0; i < pixels; i++) h = (h ^ frame[i]) * Prime;
        return h;
    }

    public static ulong Ram(IReadOnlyList<IMemoryDomain> domains)
    {
        ulong h = Basis;
        foreach (var d in domains)
        {
            if (d.Kind != DomainKind.Work || d.Unavailable != null) continue;
            for (int i = 0; i < d.Size; i++) { h ^= d.Peek(i); h *= Prime; }
        }
        return h;
    }

    /// <summary>The domains <see cref="Ram"/> covers, in words ("RAM 2048").</summary>
    public static string RamDomains(IReadOnlyList<IMemoryDomain> domains) =>
        string.Join(", ", domains.Where(d => d.Kind == DomainKind.Work && d.Unavailable == null).Select(d => $"{d.Id} {d.Size}"));
}
