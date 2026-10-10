using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using NesEmulator.Sega;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// <c>--md-sweep &lt;dir|list.txt|rom&gt; [--frames N] [--jobs J] [--out results.tsv] [--script default|mash|none|start] [--filter text] [--limit N] [--skip N] [--thumbs dir] [--watchdog S] [--summary results.tsv] [--region ntsc|pal] [--model 1|2|3] [--apu id]</c>:
/// runs every Genesis ROM of a folder (or of a list file, one path per line) headless for a fixed number of frames with a scripted pad, classifies what happened to each, and writes one row per ROM
/// to a TSV (kept out of git) plus a summary on the console: counts per class, the causes behind the classes, the slowest games. Zipped ROMs (the first ROM-like entry) are read in memory.
/// <para>Classes, in the order they are decided: <b>crashed</b> (an exception left the emulator: always a bug, even for a bad dump), <b>hung</b> (no frame completed for <c>--watchdog</c> seconds: an emulator loop), <b>rejected</b> (the
/// loader said, in a clean error, that this is not a ROM it can run: a labelled outcome), <b>unsupported</b> (the file is for hardware this emulator does not have and says so: 32X, Pico, an SVP game, a mapper nobody wrote),
/// <b>halted</b> (the 68000 stopped in a double fault), <b>exception-loop</b> (an address-error or illegal-instruction storm), <b>no-picture</b> (the picture never shows more than one colour), <b>frozen</b> (the picture stops changing
/// for the last quarter of the run although the script keeps pressing buttons), <b>silent</b> (the sound unit never makes a sample above a whisper), <b>ok</b>. "ok" means alive: it is not a claim that the game plays right.</para>
/// <para>The pad script is the same for every game, so a class is comparable across games. <c>default</c> pulses Start and C early (title screens, menus), then walks right and jumps, attacks and ducks in rotation; <c>mash</c> presses
/// pseudo-random subsets of A, B, C and the directions every few frames (Start rarely), for menus the default does not get through; <c>start</c> only Start pulses; <c>none</c> no input.</para>
/// </summary>
internal static class MdSweepCli
{
    private static readonly string[] RomExt = { ".bin", ".gen", ".md", ".smd", ".me", ".rom" };

    public sealed class Result
    {
        public string File = "", Name = "", Title = "", Serial = "", Regions = "", IoSupport = "", Device = "";
        public long Size;
        public string Class = "", Cause = "", Detail = "";
        public int FramesRun;
        public double Seconds, Fps;
        public string Mapper = "";
        public int DistinctPictures, FirstShowFrame = -1, LastChangeFrame = -1, LastColours;
        public long AudioPeak, AudioNonZero;
        public long PsgWrites, YmWrites, BusRequests, Z80Ticks;
        public long AddressErrors, Illegal, LineA, LineF, Priv, DivZero, Chk, TrapV;
        public uint Pc;
        public long StrayWrites, OtherMapperWrites, OpenBusReads, UnclaimedWrites, TmssWrites;
        public uint LastStray, LastOtherMapper, LastOpenBus, LastUnclaimed;
        public string Model = "", RegionUsed = "", PicHash = "";
        public bool HeaderOk;
        public string ThumbPath = "";
    }

    // =================================================================================================================
    //  The pad scripts
    // =================================================================================================================

    internal static PadButtons PadAt(string script, int f)
    {
        switch (script)
        {
            case "none": return PadButtons.None;
            case "start": return (f >= 100 && (f - 100) % 150 < 6) ? PadButtons.Start : PadButtons.None;
            case "mash":
            {
                // eight-frame slots; each slot a pseudo-random subset (the slot number is the seed), Start in about one slot in twenty
                uint x = (uint)(f / 8) * 2654435761u + 12345u; x ^= x >> 15; x *= 2246822519u; x ^= x >> 13;
                PadButtons b = PadButtons.None;
                if ((x & 3) == 0) b |= PadButtons.A;
                if ((x >> 2 & 3) == 0) b |= PadButtons.B;
                if ((x >> 4 & 3) == 0) b |= PadButtons.C;
                if ((x >> 6 & 7) < 3) b |= PadButtons.Right;
                if ((x >> 9 & 7) == 0) b |= PadButtons.Left;
                if ((x >> 12 & 7) == 0) b |= PadButtons.Down;
                if ((x >> 15 & 7) == 0) b |= PadButtons.Up;
                if (f >= 120 && (x >> 18 & 31) == 0) b |= PadButtons.Start;
                return b;
            }
            default:
            {
                if (f < 90) return PadButtons.None;
                if (f < 1100)
                {
                    // title screens and menus: Start at the top of each 120-frame cycle, C halfway
                    int c = (f - 90) % 120;
                    if (c < 6) return PadButtons.Start;
                    if (c >= 60 && c < 66) return PadButtons.C;
                    if (c >= 90 && c < 96) return PadButtons.A;
                    return PadButtons.None;
                }
                // play: a 240-frame cycle of walking, jumping, attacking, ducking; a Start only every 960 frames to leave a game-over or continue screen
                int p = (f - 1100) % 240;
                if ((f - 1100) % 960 < 6 && f > 1100) return PadButtons.Start;
                if (p < 70) return PadButtons.Right;
                if (p < 90) return PadButtons.Right | PadButtons.B;
                if (p < 110) return PadButtons.Right | PadButtons.C;
                if (p < 126) return PadButtons.A;
                if (p < 150) return PadButtons.Down | PadButtons.B;
                if (p < 175) return PadButtons.Left;
                if (p < 195) return PadButtons.Left | PadButtons.B;
                if (p < 215) return PadButtons.Up;
                return PadButtons.Right | PadButtons.A | PadButtons.B;
            }
        }
    }

    // =================================================================================================================
    //  Running one ROM
    // =================================================================================================================

    private sealed class Worker
    {
        public volatile string Current = "";
        public long LastProgressTicks;
        public int Frame;
        public volatile bool Abandoned;
        public Thread? Thread;
    }

    private static ulong Fnv(uint[] px, int n)
    {
        ulong h = 14695981039346656037UL;
        for (int i = 0; i < n; i++) { h ^= px[i]; h *= 1099511628211UL; }
        return h;
    }

    private static bool SingleColour(uint[] px, int n)
    {
        uint c = px[0];
        for (int i = 1; i < n; i++) if (px[i] != c) return false;
        return true;
    }

    private static int CountColours(uint[] px, int n)
    {
        var set = new HashSet<uint>();
        for (int i = 0; i < n && set.Count < 64; i++) set.Add(px[i]);
        return set.Count;
    }

    /// <summary>What the header says about the cartridge's hardware, and whether this emulator has it. Null when nothing is wrong.</summary>
    internal static (string cause, string detail)? HardwareVerdict(byte[] rom, MdCartridge? cart)
    {
        byte[] md = SegaRomFormat.NormalizeGenesis(rom);
        string Ascii(int at, int len) { var sb = new StringBuilder(); for (int i = 0; i < len && at + i < md.Length; i++) { byte c = md[at + i]; sb.Append(c >= 0x20 && c < 0x7F ? (char)c : ' '); } return sb.ToString(); }
        string dev = md.Length >= 0x110 ? Ascii(0x100, 16) : "";
        if (dev.Contains("32X")) return ("32X", "header says SEGA 32X: the 32X add-on is not emulated (and not planned)");
        if (dev.Contains("PICO")) return ("Pico", "header says SEGA PICO: the Pico is not emulated (and not planned)");
        if (dev.Contains("TERA68K") || dev.Contains("TERA286")) return ("Teradrive", "header names the Tera Drive: not emulated");
        return null;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];

    private static Result RunOne(string label, byte[] data, string script, int frames, Worker w, short[] drain, string? thumbDir, string apu, RegionChoice? region, GenesisModelChoice? model)
    {
        var r = new Result { File = label, Name = Path.GetFileNameWithoutExtension(label), Size = data.Length };
        // 1. what the file claims to be
        var kind = SegaRomFormat.Detect(data, label);
        var hw = HardwareVerdict(data, null);
        if (kind != ConsoleKind.Genesis)
        {
            if (hw != null) { r.Class = "unsupported"; r.Cause = hw.Value.cause; r.Detail = hw.Value.detail; return r; }
            if (kind == ConsoleKind.MasterSystem || kind == ConsoleKind.GameGear) { r.Class = "rejected"; r.Cause = "not a Genesis ROM"; r.Detail = $"detected as {kind}"; return r; }
            // no SEGA at $100: a headerless dump (older cartridges and many unlicensed ones never had it). The app cannot tell it from other data; the sweep runs it anyway and says so
            r.HeaderOk = false;
        }
        else r.HeaderOk = true;

        MdSession session;
        try { session = new MdSession(data, "M68K", "MD", apu, region, model); }
        catch (InvalidDataException e) { r.Class = "rejected"; r.Cause = "loader error"; r.Detail = e.Message; return r; }
        catch (Exception e) { r.Class = "crashed"; r.Cause = "constructor: " + e.GetType().Name; r.Detail = Truncate(e.Message.Replace('\t', ' ').Replace('\n', ' '), 200) + " @ " + FirstFrame(e); return r; }

        var board = session.Board;
        var cart = board.Cart;
        r.Title = cart.OverseasTitle.Length > 0 ? cart.OverseasTitle : cart.DomesticTitle;
        r.Serial = cart.Serial; r.Regions = cart.Regions; r.Device = cart.Device;
        r.Mapper = cart.Mapper;
        r.Model = board.Model.ToString(); r.RegionUsed = board.Region.ToString();
        {
            byte[] md = SegaRomFormat.NormalizeGenesis(data);
            r.IoSupport = md.Length >= 0x1A0 ? new string(Enumerable.Range(0x190, 16).Select(i => md[i] >= 0x20 && md[i] < 0x7F ? (char)md[i] : ' ').ToArray()).Trim() : "";
        }

        var picHashes = new HashSet<ulong>();
        ulong lastHash = 0; int lastChange = -1;
        int firstShow = -1;
        long peak = 0, nonZero = 0;
        var sw = Stopwatch.StartNew();
        PadButtons pad = PadButtons.None;
        int i = 0;
        uint[]? lastPicture = null; int lastW = 0, lastH = 0;
        int halfHashFrame = frames * 3 / 4; ulong hashAtThreeQuarters = 0;
        try
        {
            for (; i < frames; i++)
            {
                pad = PadAt(script, i);
                session.SetPad(0, pad);
                session.RunFrame();
                // audio: drain what the sound unit made and keep the loudest sample
                int got;
                while ((got = session.ReadSamples(drain)) > 0)
                {
                    for (int k = 0; k < got; k++) { int a = drain[k]; if (a < 0) a = -a; if (a > peak) peak = a; if (a > 32) nonZero++; }
                }
                int n = session.FrameWidth * session.FrameHeight;
                ulong h = Fnv(session.Frame, n) ^ (ulong)n;
                if (picHashes.Count < 100000) picHashes.Add(h);
                if (h != lastHash) { lastHash = h; lastChange = i; }
                if (i == halfHashFrame) hashAtThreeQuarters = h;
                if (firstShow < 0 && !SingleColour(session.Frame, n)) firstShow = i;
                w.Frame = i; Interlocked.Exchange(ref w.LastProgressTicks, Stopwatch.GetTimestamp());
                if (board.Cpu.Halted) { i++; break; }
            }
        }
        catch (Exception e)
        {
            sw.Stop();
            r.FramesRun = i; r.Seconds = sw.Elapsed.TotalSeconds;
            r.Class = "crashed"; r.Cause = "run: " + e.GetType().Name; r.Detail = Truncate(e.Message.Replace('\t', ' ').Replace('\n', ' '), 200) + " @ " + FirstFrame(e) + $" (frame {i}, pc {board.Cpu.Pc - 4:X6})";
            Fill(r, board, session);
            return r;
        }
        sw.Stop();
        r.FramesRun = i; r.Seconds = sw.Elapsed.TotalSeconds; r.Fps = i / Math.Max(1e-6, r.Seconds);
        Fill(r, board, session);
        r.AudioPeak = peak; r.AudioNonZero = nonZero;
        r.DistinctPictures = picHashes.Count; r.FirstShowFrame = firstShow; r.LastChangeFrame = lastChange;
        int nn = session.FrameWidth * session.FrameHeight;
        r.LastColours = CountColours(session.Frame, nn);
        r.PicHash = $"{lastHash:X16}";
        bool lastBlank = SingleColour(session.Frame, nn);
        lastPicture = session.Frame; lastW = session.FrameWidth; lastH = session.FrameHeight;

        // 2. classify
        long exceptionsLoop = r.Illegal + r.LineA + r.LineF + r.Priv;
        if (board.Cpu.Halted)
        {
            r.Class = "halted"; r.Cause = "double fault";
            r.Detail = $"68000 halted at frame {i} pc {r.Pc:X6}: an address error while stacking an exception (address errors {r.AddressErrors}, illegal {r.Illegal})";
        }
        else if (hw != null) { r.Class = "unsupported"; r.Cause = hw.Value.cause; r.Detail = hw.Value.detail; }
        else if (r.AddressErrors > 200 || exceptionsLoop > 200)
        {
            r.Class = "exception-loop";
            r.Cause = r.AddressErrors > 200 ? "address-error storm" : r.Illegal > 200 ? "illegal-instruction storm" : r.LineA > 200 ? "line-A storm" : r.LineF > 200 ? "line-F storm" : "privilege storm";
            r.Detail = $"address errors {r.AddressErrors}, illegal {r.Illegal}, line-A {r.LineA}, line-F {r.LineF}, privilege {r.Priv}; pc {r.Pc:X6}";
        }
        else if (firstShow < 0 || (lastBlank && r.DistinctPictures <= 2))
        {
            r.Class = "no-picture";
            r.Cause = !board.Vdp.DisplayEnabled ? "display never enabled" : "picture is one colour";
            r.Detail = $"display {(board.Vdp.DisplayEnabled ? "on" : "off")}, vint {(board.Vdp.VintEnabled ? "on" : "off")}, pc {r.Pc:X6}, distinct pictures {r.DistinctPictures}, 68000 {(board.Cpu.Stopped ? "stopped" : "running")}";
            if (r.StrayWrites > 0) r.Detail += $", {r.StrayWrites} writes into cartridge space (last ${r.LastStray:X6})";
            if (r.OtherMapperWrites > 0) r.Detail += $", {r.OtherMapperWrites} writes to unknown mapper registers (last ${r.LastOtherMapper:X6})";
            if (r.OpenBusReads > 1000) r.Detail += $", {r.OpenBusReads} open-bus reads (last ${r.LastOpenBus:X6})";
        }
        else if (frames >= 600 && lastChange < frames - frames / 4)
        {
            r.Class = "frozen";
            r.Cause = board.Cpu.Stopped ? "68000 stopped (STOP)" : "picture unchanged";
            r.Detail = $"last picture change at frame {lastChange} of {i}; pc {r.Pc:X6}; display {(board.Vdp.DisplayEnabled ? "on" : "off")} vint {(board.Vdp.VintEnabled ? "on" : "off")}; distinct pictures {r.DistinctPictures}";
            if (r.OpenBusReads > 1000) r.Detail += $"; {r.OpenBusReads} open-bus reads (last ${r.LastOpenBus:X6})";
        }
        else if (peak < 24)
        {
            r.Class = "silent";
            r.Cause = r.PsgWrites + r.YmWrites == 0 ? (r.Z80Ticks == 0 ? "Z80 never started, no sound writes" : "no sound writes (Z80 ran)") : "sound writes made no sound";
            r.Detail = $"peak {peak}, psg writes {r.PsgWrites}, ym writes {r.YmWrites}, bus requests {r.BusRequests}, z80 ticks {r.Z80Ticks}";
        }
        else { r.Class = "ok"; r.Cause = ""; r.Detail = $"{r.DistinctPictures} distinct pictures, {r.LastColours} colours at the end, peak {peak}"; }

        if (thumbDir != null && r.Class != "ok" && lastPicture != null)
        {
            try
            {
                Directory.CreateDirectory(thumbDir);
                string safe = new string(r.Name.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
                r.ThumbPath = Path.Combine(thumbDir, $"{r.Class}_{safe}.png");
                WritePng(lastPicture, lastW, lastH, r.ThumbPath);
            }
            catch { r.ThumbPath = ""; }
        }
        return r;
    }

    private static string FirstFrame(Exception e)
    {
        var st = new StackTrace(e, true);
        for (int k = 0; k < st.FrameCount; k++)
        {
            var fr = st.GetFrame(k);
            var m = fr?.GetMethod();
            if (m == null) continue;
            string t = m.DeclaringType?.Name ?? "?";
            return $"{t}.{m.Name}:{fr!.GetFileLineNumber()}";
        }
        return "?";
    }

    private static void Fill(Result r, BOARD_MD board, MdSession session)
    {
        var c = board.Cpu;
        r.Pc = c.Pc - 4;
        r.AddressErrors = c.AddressErrorCount;
        r.Illegal = c.ExceptionCounts[M68k<MdBus>.VecIllegal]; r.LineA = c.ExceptionCounts[M68k<MdBus>.VecLineA]; r.LineF = c.ExceptionCounts[M68k<MdBus>.VecLineF];
        r.Priv = c.ExceptionCounts[M68k<MdBus>.VecPrivilege]; r.DivZero = c.ExceptionCounts[M68k<MdBus>.VecDivideByZero]; r.Chk = c.ExceptionCounts[M68k<MdBus>.VecChk]; r.TrapV = c.ExceptionCounts[M68k<MdBus>.VecTrapV];
        r.StrayWrites = board.Cart.StrayWrites; r.LastStray = board.Cart.LastStrayWrite;
        r.OtherMapperWrites = board.Cart.OtherMapperWrites; r.LastOtherMapper = board.Cart.LastOtherMapperWrite;
        r.OpenBusReads = board.OpenBusReads; r.LastOpenBus = board.LastOpenBusAddress;
        r.UnclaimedWrites = board.UnclaimedWrites; r.LastUnclaimed = board.LastUnclaimedWrite; r.TmssWrites = board.TmssWrites;
        if (board.Sound is MdSoundUnit u) { r.PsgWrites = u.PsgWrites; r.YmWrites = u.YmWrites; r.BusRequests = u.BusRequests; r.Z80Ticks = u.Z80Ticks; }
    }

    private static void WritePng(uint[] pixels, int width, int height, string path)
    {
        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var ints = new int[width];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++) ints[x] = (int)pixels[y * width + x];
                System.Runtime.InteropServices.Marshal.Copy(ints, 0, data.Scan0 + y * data.Stride, width);
            }
        }
        finally { bmp.UnlockBits(data); }
        bmp.Save(path, ImageFormat.Png);
    }

    // =================================================================================================================
    //  Gathering the files
    // =================================================================================================================

    private static IEnumerable<string> Gather(string input)
    {
        if (Directory.Exists(input))
        {
            foreach (var f in Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                string e = Path.GetExtension(f).ToLowerInvariant();
                if (RomExt.Contains(e) || e == ".zip") yield return f;
            }
        }
        else if (File.Exists(input) && Path.GetExtension(input).Equals(".txt", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var line in File.ReadAllLines(input)) { var t = line.Trim(); if (t.Length > 0 && !t.StartsWith('#')) yield return t; }
        }
        else if (File.Exists(input)) yield return input;
    }

    private static (string label, byte[]? data, string? error) Load(string path)
    {
        try
        {
            if (Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var zip = ZipFile.OpenRead(path);
                var entry = zip.Entries.Where(e => RomExt.Contains(Path.GetExtension(e.Name).ToLowerInvariant())).OrderByDescending(e => e.Length).FirstOrDefault();
                if (entry == null) return (path, null, "zip holds no ROM-like file");
                using var ms = new MemoryStream();
                using (var s = entry.Open()) s.CopyTo(ms);
                return (Path.Combine(path, entry.Name), ms.ToArray(), null);
            }
            return (path, File.ReadAllBytes(path), null);
        }
        catch (Exception e) { return (path, null, e.GetType().Name + ": " + e.Message); }
    }

    // =================================================================================================================
    //  The command
    // =================================================================================================================

    private const string Header = "class\tcause\tname\tsize\ttitle\tserial\tregions\tio\tdevice\tmapper\tmodel\tregion\tframes\tsecs\tfps\tdistinct_pics\tfirst_show\tlast_change\tcolours\tpic_hash\taudio_peak\taudio_nonzero\tpsg_w\tym_w\tbusreq\tz80_ticks\taddr_err\tillegal\tlineA\tlineF\tpriv\tdivzero\tchk\ttrapv\tpc\tstray_w\tlast_stray\tmapper_w\tlast_mapper\topenbus_r\tlast_openbus\tunclaimed_w\tlast_unclaimed\ttmss_w\thdr_ok\tdetail\tthumb\tfile";

    private static string Row(Result r)
    {
        string S(string s) => s.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
        string X(uint v) => v == 0 ? "" : v.ToString("X6");
        return string.Join('\t', new[]
        {
            r.Class, S(r.Cause), S(r.Name), r.Size.ToString(), S(r.Title), S(r.Serial), S(r.Regions), S(r.IoSupport), S(r.Device), S(r.Mapper), r.Model, r.RegionUsed,
            r.FramesRun.ToString(), r.Seconds.ToString("F2"), r.Fps.ToString("F0"), r.DistinctPictures.ToString(), r.FirstShowFrame.ToString(), r.LastChangeFrame.ToString(), r.LastColours.ToString(), r.PicHash,
            r.AudioPeak.ToString(), r.AudioNonZero.ToString(), r.PsgWrites.ToString(), r.YmWrites.ToString(), r.BusRequests.ToString(), r.Z80Ticks.ToString(),
            r.AddressErrors.ToString(), r.Illegal.ToString(), r.LineA.ToString(), r.LineF.ToString(), r.Priv.ToString(), r.DivZero.ToString(), r.Chk.ToString(), r.TrapV.ToString(), r.Pc.ToString("X6"),
            r.StrayWrites.ToString(), X(r.LastStray), r.OtherMapperWrites.ToString(), X(r.LastOtherMapper), r.OpenBusReads.ToString(), X(r.LastOpenBus), r.UnclaimedWrites.ToString(), X(r.LastUnclaimed), r.TmssWrites.ToString(),
            r.HeaderOk ? "1" : "0", S(r.Detail), S(r.ThumbPath), S(r.File),
        });
    }

    /// <summary><c>--md-sweep --thumb-sheet dir [--prefix frozen_] [--page 0] [--per 30] --out sheet.png</c>: puts the sweep's last-frame thumbnails (one PNG per non-ok game) into labelled contact sheets, so a class can be looked at in a few pictures.</summary>
    private static int ThumbSheet(string dir, string? prefix, int page, int per, string outPath)
    {
        var files = Directory.GetFiles(dir, (prefix ?? "") + "*.png").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Skip(page * per).Take(per).ToList();
        if (files.Count == 0) { Console.WriteLine("no thumbnails"); return 2; }
        const int cols = 6, cw = 200, ch = 150;
        int rows = (files.Count + cols - 1) / cols;
        using var sheet = new Bitmap(cols * cw, rows * ch, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(sheet);
        g.Clear(Color.FromArgb(32, 32, 32));
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
        using var font = new Font("Consolas", 7f);
        for (int k = 0; k < files.Count; k++)
        {
            int cx = k % cols * cw, cy = k / cols * ch;
            using var bmp = new Bitmap(files[k]);
            g.DrawImage(bmp, new Rectangle(cx + 1, cy + 11, cw - 2, ch - 12));
            string label = Path.GetFileNameWithoutExtension(files[k]); if (prefix != null && label.StartsWith(prefix)) label = label[prefix.Length..];
            g.DrawString(Truncate(label, 36), font, Brushes.Yellow, cx + 1, cy);
        }
        sheet.Save(outPath, ImageFormat.Png);
        Console.WriteLine($"wrote {outPath}: {files.Count} thumbnails (page {page}, of {Directory.GetFiles(dir, (prefix ?? "") + "*.png").Length})");
        return 0;
    }

    public static int Run(string[] args)
    {
        if (M68kTestCli.Arg(args, "--thumb-sheet") is { } tdir)
            return ThumbSheet(tdir, M68kTestCli.Arg(args, "--prefix"), int.TryParse(M68kTestCli.Arg(args, "--page"), out int pg) ? pg : 0, int.TryParse(M68kTestCli.Arg(args, "--per"), out int pr) ? pr : 30, M68kTestCli.Arg(args, "--out") ?? "sheet.png");
        string? summaryOnly = M68kTestCli.Arg(args, "--summary");
        if (summaryOnly != null) { Summarise(ReadTsv(summaryOnly), summaryOnly); return 0; }
        string? input = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null;
        if (input == null) { Console.WriteLine("usage: --md-sweep <dir|list.txt|rom> [--frames N] [--jobs J] [--out results.tsv] [--script default|mash|none|start] [--filter text] [--limit N] [--skip N] [--thumbs dir] [--watchdog S] [--region ntsc|pal] [--model 1|2|3] [--apu id]   |   --md-sweep --summary results.tsv"); return 2; }
        int frames = int.TryParse(M68kTestCli.Arg(args, "--frames"), out int fr) ? fr : 2400;
        int jobs = Math.Clamp(int.TryParse(M68kTestCli.Arg(args, "--jobs"), out int j) ? j : 4, 1, 8);
        string script = (M68kTestCli.Arg(args, "--script") ?? "default").ToLowerInvariant();
        string? outPath = M68kTestCli.Arg(args, "--out"), thumbs = M68kTestCli.Arg(args, "--thumbs"), filter = M68kTestCli.Arg(args, "--filter");
        int limit = int.TryParse(M68kTestCli.Arg(args, "--limit"), out int lim) ? lim : int.MaxValue;
        int skip = int.TryParse(M68kTestCli.Arg(args, "--skip"), out int sk) ? sk : 0;
        double watchdog = double.TryParse(M68kTestCli.Arg(args, "--watchdog"), out double wd) ? wd : 60;
        string apu = M68kTestCli.Arg(args, "--apu") ?? "MD";
        RegionChoice? region = M68kTestCli.Arg(args, "--region")?.ToLowerInvariant() switch { "ntsc" => RegionChoice.Ntsc, "pal" => RegionChoice.Pal, _ => null };
        GenesisModelChoice? model = M68kTestCli.Arg(args, "--model") switch { "1" => GenesisModelChoice.Model1, "2" => GenesisModelChoice.Model2, "3" => GenesisModelChoice.Model3, _ => null };

        var files = Gather(input).Where(f => filter == null || f.Contains(filter, StringComparison.OrdinalIgnoreCase)).Skip(skip).Take(limit).ToList();
        if (files.Count == 0) { Console.WriteLine($"no ROMs found in {input}"); return 2; }
        Console.WriteLine($"== md-sweep: {files.Count} files, {frames} frames each, script '{script}', {jobs} job(s), watchdog {watchdog:F0} s");

        var queue = new ConcurrentQueue<(int index, string path)>(files.Select((p, k) => (k, p)));
        var results = new Result?[files.Count];
        int done = 0;
        var total = Stopwatch.StartNew();
        var workers = new List<Worker>();
        object lockObj = new();
        StreamWriter? tsv = null;
        if (outPath != null) { tsv = new StreamWriter(outPath, false, new UTF8Encoding(false)); tsv.WriteLine(Header); tsv.Flush(); }
        int hungCount = 0;

        void Record(int index, Result res)
        {
            lock (lockObj)
            {
                if (results[index] != null) return;
                results[index] = res;
                done++;
                tsv?.WriteLine(Row(res)); tsv?.Flush();
                Console.WriteLine($"[{done,4}/{files.Count}] {res.Class,-14} {Truncate(res.Name, 56),-56} {(res.Fps > 0 ? $"{res.Fps,5:F0} fps" : "")}{(res.Class != "ok" ? "  " + Truncate(res.Cause, 50) : "")}");
            }
        }

        void Start(Worker w)
        {
            w.Thread = new Thread(() =>
            {
                var drain = new short[8192];
                while (!w.Abandoned && queue.TryDequeue(out var item))
                {
                    var (label, data, error) = Load(item.path);
                    w.Current = item.path; w.Frame = 0; Interlocked.Exchange(ref w.LastProgressTicks, Stopwatch.GetTimestamp());
                    Result res;
                    if (data == null) res = new Result { File = item.path, Name = Path.GetFileNameWithoutExtension(item.path), Class = "rejected", Cause = "unreadable file", Detail = error ?? "" };
                    else
                    {
                        try { res = RunOne(label, data, script, frames, w, drain, thumbs, apu, region, model); }
                        catch (Exception e) { res = new Result { File = label, Name = Path.GetFileNameWithoutExtension(label), Size = data.Length, Class = "crashed", Cause = "sweep: " + e.GetType().Name, Detail = e.Message + " @ " + FirstFrame(e) }; }
                    }
                    if (w.Abandoned) return;      // the watchdog already recorded this ROM as hung
                    res.File = item.path;
                    Record(item.index, res);
                    w.Current = "";
                }
            }) { IsBackground = true, Name = "sweep" };
            w.Thread.Start();
        }

        for (int k = 0; k < jobs; k++) { var w = new Worker(); workers.Add(w); Start(w); }

        // the watchdog: a worker that completes no frame for --watchdog seconds is in an emulator loop; record the ROM, abandon the thread (a .NET thread cannot be killed) and start another worker
        while (done < files.Count)
        {
            Thread.Sleep(500);
            foreach (var w in workers.ToArray())
            {
                if (w.Abandoned || w.Current.Length == 0) continue;
                double idle = (Stopwatch.GetTimestamp() - Interlocked.Read(ref w.LastProgressTicks)) / (double)Stopwatch.Frequency;
                if (idle > watchdog)
                {
                    string path = w.Current;
                    int idx = files.IndexOf(path);
                    w.Abandoned = true; hungCount++;
                    Record(idx, new Result { File = path, Name = Path.GetFileNameWithoutExtension(path), Class = "hung", Cause = "no frame completed", Detail = $"stuck for {idle:F0} s after frame {w.Frame}" });
                    var nw = new Worker(); workers.Add(nw); Start(nw);
                }
            }
            if (queue.IsEmpty && workers.All(w => w.Abandoned || w.Current.Length == 0) && done < files.Count) { Thread.Sleep(200); if (done >= files.Count) break; if (workers.All(w => w.Abandoned || !(w.Thread?.IsAlive ?? false))) break; }
        }
        total.Stop();
        tsv?.Dispose();

        var all = results.Where(x => x != null).Select(x => x!).ToList();
        Console.WriteLine($"== {all.Count} ROMs in {total.Elapsed.TotalMinutes:F1} min");
        Summarise(all.Select(ToDict).ToList(), outPath ?? "(not saved)");
        if (hungCount > 0) { Console.WriteLine($"   {hungCount} abandoned thread(s) are still spinning: exiting the process"); Console.Out.Flush(); Environment.Exit(all.Any(r => r.Class is "crashed" or "hung") ? 1 : 0); }
        return all.Any(r => r.Class is "crashed" or "hung") ? 1 : 0;
    }

    // =================================================================================================================
    //  Summary
    // =================================================================================================================

    private static Dictionary<string, string> ToDict(Result r)
    {
        var cols = Header.Split('\t'); var vals = Row(r).Split('\t');
        var d = new Dictionary<string, string>();
        for (int k = 0; k < cols.Length && k < vals.Length; k++) d[cols[k]] = vals[k];
        return d;
    }

    private static List<Dictionary<string, string>> ReadTsv(string path)
    {
        var lines = File.ReadAllLines(path);
        var cols = lines[0].Split('\t');
        var list = new List<Dictionary<string, string>>();
        foreach (var l in lines.Skip(1))
        {
            var v = l.Split('\t'); var d = new Dictionary<string, string>();
            for (int k = 0; k < cols.Length && k < v.Length; k++) d[cols[k]] = v[k];
            list.Add(d);
        }
        return list;
    }

    private static void Summarise(List<Dictionary<string, string>> rows, string source)
    {
        string G(Dictionary<string, string> d, string k) => d.TryGetValue(k, out var v) ? v : "";
        Console.WriteLine($"== summary of {rows.Count} ROMs ({source})");
        foreach (var g in rows.GroupBy(r => G(r, "class")).OrderByDescending(g => g.Count()))
            Console.WriteLine($"   {g.Key,-15} {g.Count(),4}");
        Console.WriteLine("-- causes behind the non-ok classes");
        foreach (var g in rows.Where(r => G(r, "class") != "ok").GroupBy(r => G(r, "class") + " / " + G(r, "cause")).OrderByDescending(g => g.Count()))
        {
            var names = g.Select(r => G(r, "name")).Take(8);
            Console.WriteLine($"   {g.Count(),4}  {g.Key}   e.g. {string.Join("; ", names.Select(n => Truncate(n, 40)))}");
        }
        var timed = rows.Where(r => double.TryParse(G(r, "fps"), out double f) && f > 0).Select(r => (name: G(r, "name"), fps: double.Parse(G(r, "fps")))).OrderBy(t => t.fps).ToList();
        if (timed.Count > 0)
        {
            var s = timed.Select(t => t.fps).OrderBy(x => x).ToList();
            Console.WriteLine($"-- speed (frames/s with {"the sweep's per-frame hashing and audio drain"}, other jobs running): median {s[s.Count / 2]:F0}, slowest {s[0]:F0} ({timed[0].name}), fastest {s[^1]:F0}");
            Console.WriteLine("   slowest five: " + string.Join("; ", timed.Take(5).Select(t => $"{Truncate(t.name, 36)} {t.fps:F0}")));
        }
    }
}
