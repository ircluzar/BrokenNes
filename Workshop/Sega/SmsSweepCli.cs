using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NesEmulator.Sega;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// <c>--sms-sweep &lt;dir|list|file&gt; [--frames N] [--jobs 4] [--out results.tsv] [--cache dir] [--shots dir] [--sheets] [--filter text] [--limit N] [--input spec]</c>: runs every Master System, Game Gear and
/// SG-1000 game of a folder (or of a list file, one path per line) headless on the same session the desktop app builds, for a fixed number of frames with a scripted button pattern, and classifies each
/// one: <c>crash</c> (an exception in the core), <c>hung</c> (the Z80 is dead: halted or looping on a few addresses with interrupts off and no port read, or running from memory nothing answers), <c>no-picture</c>
/// (the display never showed two colours), <c>blank</c> (a picture that went away), <c>static</c> (an unchanging picture to the end of a long run), <c>silent</c> (a picture but not one PSG write) or <c>ok</c>.
/// Every row also carries the evidence behind the class (what the program wrote to ROM space, what it did with the pad lines, which VDP features it used) so the causes can be triaged by what they share.
/// <para>Archives (<c>.7z</c> through 7-Zip, <c>.zip</c>) are unpacked into the cache folder (default: <c>%TEMP%\bn-sms-sweep-cache</c>); a file named in a list or found in a folder is run as it is. The raw results are a TSV
/// (kept out of the repository); <c>--shots dir</c> writes the last frame of every game as a PNG, <c>--sheets</c> assembles contact sheets per class from them.</para>
/// <para>Nothing here needs a BIOS: the board starts in the documented post-boot state, and the images tagged <c>[BIOS]</c> in the library are run as cartridges like everything else.</para>
/// </summary>
internal static class SmsSweepCli
{
    /// <summary>--model: every Master System game of the sweep runs as this board (to see which games behave differently on the original VDP).</summary>
    internal static MasterSystemModel? ForcedModel;

    public static int Run(string[] args)
    {
        string? Opt(string name) { int i = Array.FindIndex(args, a => a.Equals("--" + name, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        bool Flag(string name) => args.Any(a => a.Equals("--" + name, StringComparison.OrdinalIgnoreCase));
        string? target = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : Opt("src");
        int frames = int.Parse(Opt("frames") ?? "2400");
        if (Flag("selftest")) return SweepSelfTest.Run(Flag("poison"));
        // --print-pad sms|gg: the sweep's pad pattern as an input script (the same input can then drive the oracle)
        if (Flag("print-pad")) { Console.WriteLine(SweepProbe.PadSpec(frames, args.Any(a => a.Equals("gg", StringComparison.OrdinalIgnoreCase)))); return 0; }
        if (target == null)
        {
            Console.WriteLine("usage: --sms-sweep <dir|list.txt|rom|archive> [--frames N=2400] [--jobs 4] [--out results.tsv] [--cache dir] [--shots dir] [--sheets] [--filter text] [--limit N] [--input spec]");
            Console.WriteLine("       --sms-sweep --print-pad sms|gg [--frames N]      the scripted pad pattern as an input script");
            return 2;
        }
        if (Opt("model") is { } mdl) SmsSweepCli.ForcedModel = Enum.Parse<MasterSystemModel>(mdl, true);      // run every Master System game as this board (the Game Gear is always itself)
        int jobs = Math.Clamp(int.Parse(Opt("jobs") ?? "4"), 1, 8);
        string? outTsv = Opt("out"), shots = Opt("shots");
        string cache = Opt("cache") ?? Path.Combine(Path.GetTempPath(), "bn-sms-sweep-cache");
        string? filter = Opt("filter");
        int limit = int.Parse(Opt("limit") ?? "0");
        var script = Opt("input") is { } spec ? SmsRomIo.ParseInput(spec) : null;

        var items = SweepSource.Collect(target, cache, filter);
        if (limit > 0) items = items.Take(limit).ToList();
        if (items.Count == 0) { Console.WriteLine("no games found"); return 2; }
        Console.WriteLine($"sweep: {items.Count} games, {frames} frames each, {jobs} jobs" + (script != null ? ", custom input" : ", scripted pattern"));
        if (shots != null) Directory.CreateDirectory(shots);

        var rows = new ConcurrentBag<SweepRow>();
        StreamWriter? live = null;
        if (outTsv != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outTsv))!);
            live = new StreamWriter(outTsv + ".partial") { AutoFlush = true };
            live.WriteLine(SweepRow.TsvHeader);
        }
        int done = 0;
        var sw = Stopwatch.StartNew();
        Parallel.ForEach(items, new ParallelOptions { MaxDegreeOfParallelism = jobs }, item =>
        {
            var row = SweepProbe.Probe(item, frames, script, shots);
            rows.Add(row);
            if (live != null) lock (live) live.WriteLine(row.ToTsv());
            int n = Interlocked.Increment(ref done);
            if (n % 25 == 0 || n == items.Count) Console.WriteLine($"  {n}/{items.Count}  {sw.Elapsed.TotalSeconds:F0}s");
        });
        live?.Dispose();
        var sorted = rows.OrderBy(r => r.Console, StringComparer.Ordinal).ThenBy(r => r.File, StringComparer.OrdinalIgnoreCase).ToList();
        if (outTsv != null)
        {
            File.WriteAllLines(outTsv, new[] { SweepRow.TsvHeader }.Concat(sorted.Select(r => r.ToTsv())));
            try { File.Delete(outTsv + ".partial"); } catch { }
            Console.WriteLine($"wrote {outTsv}");
        }
        Summary.Print(sorted, sw.Elapsed.TotalSeconds, frames);
        if (shots != null && Flag("sheets")) Sheets.Make(sorted, shots);
        return sorted.Any(r => r.Class == "crash") ? 1 : 0;
    }
}

/// <summary>A game to run: the file on disk (unpacked if it came from an archive), the name the app would see, and the archive it came from.</summary>
internal sealed record SweepItem(string Path, string Name, string Archive);

internal static class SweepSource
{
    private static readonly string[] RomExt = { ".sms", ".gg", ".sg", ".sc" };

    public static List<SweepItem> Collect(string target, string cache, string? filter)
    {
        var items = new List<SweepItem>();
        if (Directory.Exists(target)) AddDirectory(items, target, cache);
        else if (File.Exists(target) && target.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || target.EndsWith(".lst", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var line in File.ReadLines(target).Select(l => l.Trim()).Where(l => l.Length > 0 && l[0] != '#'))
            {
                if (Directory.Exists(line)) AddDirectory(items, line, cache);
                else AddFile(items, line, cache);
            }
        }
        else if (File.Exists(target)) AddFile(items, target, cache);
        else throw new FileNotFoundException("nothing at " + target);
        if (!string.IsNullOrEmpty(filter)) items = items.Where(i => i.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || i.Archive.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        return items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void AddDirectory(List<SweepItem> items, string dir, string cache)
    {
        // archives first (several can sit beside loose files); a folder is walked whole
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase)) AddFile(items, f, cache);
    }

    private static void AddFile(List<SweepItem> items, string path, string cache)
    {
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (RomExt.Contains(ext)) items.Add(new SweepItem(path, System.IO.Path.GetFileName(path), ""));
        else if (ext == ".zip")
        {
            using var zip = ZipFile.OpenRead(path);
            foreach (var e in zip.Entries.Where(e => RomExt.Contains(System.IO.Path.GetExtension(e.Name).ToLowerInvariant())))
            {
                string dest = System.IO.Path.Combine(cache, Sanitize(System.IO.Path.GetFileNameWithoutExtension(path)), e.Name);
                if (!File.Exists(dest)) { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!); e.ExtractToFile(dest, true); }
                items.Add(new SweepItem(dest, e.Name, System.IO.Path.GetFileName(path)));
            }
        }
        else if (ext == ".7z")
        {
            string dest = System.IO.Path.Combine(cache, Sanitize(System.IO.Path.GetFileNameWithoutExtension(path)));
            if (!Directory.Exists(dest) || !Directory.EnumerateFiles(dest, "*", SearchOption.AllDirectories).Any()) SevenZip(path, dest);
            foreach (var f in Directory.EnumerateFiles(dest, "*", SearchOption.AllDirectories).Where(f => RomExt.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant())).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                items.Add(new SweepItem(f, System.IO.Path.GetFileName(f), System.IO.Path.GetFileName(path)));
        }
        // anything else (a .txt note, a Genesis file) is not ours
    }

    private static string Sanitize(string name) => string.Concat(name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private static string? SevenZipExe()
    {
        foreach (var p in new[] { @"C:\Program Files\7-Zip\7z.exe", @"C:\Program Files (x86)\7-Zip\7z.exe" }) if (File.Exists(p)) return p;
        return null;
    }

    internal static void SevenZip(string archive, string dest)
    {
        string exe = SevenZipExe() ?? throw new FileNotFoundException("7-Zip (7z.exe) is needed to unpack " + archive + ": install it, or unpack the archives and sweep the folder");
        Directory.CreateDirectory(dest);
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("x"); psi.ArgumentList.Add("-y"); psi.ArgumentList.Add("-o" + dest); psi.ArgumentList.Add("--"); psi.ArgumentList.Add(archive);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidDataException($"7z failed ({p.ExitCode}) on {archive}");
    }
}

/// <summary>Everything the sweep records about one game. Public fields in column order.</summary>
internal sealed class SweepRow
{
    public string Console = "", File = "", Archive = "", Sha1 = "", Model = "", Region = "", Mapper = "", Header = "";
    public long Size;
    public string Class = "", Detail = "", Flags = "";
    public int Frames;
    public double MsPerFrame, XRealTime;
    public int DisplayFirst = -1, PictureFirst = -1, Colors, DistinctPictures, StaticTail, AudioActive, PsgWrites, FmWrites;
    public int AudioPeak, DarkTail;
    public double Noise;
    public string PcEnd = "", Iff = "", WindowPcs = "", Ports = "", RomWrites = "", CartRam = "", Battery = "", Modes = "", FinalHash = "";

    public static readonly string TsvHeader = string.Join('\t', "console", "file", "archive", "bytes", "sha1", "model", "region", "mapper", "header", "class", "detail", "flags", "frames", "ms_per_frame", "x_realtime",
        "display_first", "picture_first", "colors_end", "distinct_pictures", "static_tail", "dark_tail", "noise", "audio_peak", "audio_active_frames", "psg_writes", "fm_writes", "pc_end", "iff", "window_pcs", "ports", "rom_writes", "cart_ram_writes", "battery", "vdp_modes", "final_hash");

    public string ToTsv() => string.Join('\t', Console, Clean(File), Clean(Archive), Size, Sha1, Model, Region, Mapper, Header, Class, Clean(Detail), Flags, Frames, MsPerFrame.ToString("F2"), XRealTime.ToString("F1"),
        DisplayFirst, PictureFirst, Colors, DistinctPictures, StaticTail, DarkTail, Noise.ToString("F2"), AudioPeak, AudioActive, PsgWrites, FmWrites, PcEnd, Iff, WindowPcs, Ports, RomWrites, CartRam, Battery, Modes, FinalHash);

    private static string Clean(string s) => s.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
}

internal static class SweepProbe
{
    /// <summary>The scripted pattern: quiet while a game starts, then a tap of button 1 (and, on the Game Gear, Start) every few seconds to get past title screens, then a stretch of walking with jumps and a button 2 tap.
    /// Pause (the Master System's NMI button) is never pressed: it would freeze half the library.</summary>
    public static PadButtons PadAt(int f, bool gg)
    {
        if (f < 90) return 0;
        int t = f % 600;
        PadButtons Tap(int at, PadButtons b) => t >= at && t < at + 5 ? b : 0;
        PadButtons b = 0;
        b |= Tap(90, PadButtons.A) | Tap(150, gg ? PadButtons.Start : PadButtons.A) | Tap(210, PadButtons.A) | Tap(270, gg ? PadButtons.Start : PadButtons.B);
        if (t >= 300 && t < 420) b |= PadButtons.Right;
        b |= Tap(335, PadButtons.A) | Tap(380, PadButtons.A);
        if (t >= 420 && t < 450) b |= PadButtons.Left;
        b |= Tap(455, PadButtons.B);
        if (t >= 470 && t < 500) b |= PadButtons.Down;
        if (t >= 510 && t < 530) b |= PadButtons.Up;
        b |= Tap(540, PadButtons.A);
        if (t >= 560) b |= PadButtons.Right;
        return b;
    }

    private static ulong Fnv(uint[] px, int n)
    {
        ulong h = 0xcbf29ce484222325;
        for (int i = 0; i < n; i++) { h ^= px[i]; h *= 0x100000001b3; }
        return h;
    }

    /// <summary>The pad pattern as an input script ("frame:Buttons,..." for <c>--sms-run --input</c> and the oracle's capture script), so the oracle can be given the very same input.</summary>
    public static string PadSpec(int frames, bool gg)
    {
        var parts = new List<string>();
        PadButtons last = 0;
        for (int f = 0; f < frames; f++)
        {
            var b = PadAt(f, gg);
            if (b == last) continue;
            parts.Add($"{f}:{string.Join('+', Enum.GetValues<PadButtons>().Where(x => x != 0 && (b & x) == x && System.Numerics.BitOperations.PopCount((uint)x) == 1).Select(x => x.ToString()))}");
            last = b;
        }
        return string.Join(',', parts);
    }

    /// <summary>The fraction of horizontally adjacent pixel pairs that differ: a menu or a scene is well under a third, noise (a mapper that fetches the wrong bank) is near one.</summary>
    private static double Transitions(uint[] px, int w, int h)
    {
        long diff = 0, pairs = 0;
        for (int y = 0; y < h; y += 2)
            for (int x = 1; x < w; x++) { pairs++; if (px[y * w + x] != px[y * w + x - 1]) diff++; }
        return pairs == 0 ? 0 : diff / (double)pairs;
    }

    /// <summary>The fraction of pixels that are black.</summary>
    private static double BlackShare(uint[] px, int n)
    {
        int black = 0;
        for (int i = 0; i < n; i += 4) if ((px[i] & 0xFFFFFF) == 0) black++;
        return black / (double)((n + 3) / 4);
    }

    /// <summary>The number of distinct colours in a frame, up to a cap.</summary>
    private static int Colors(uint[] px, int n, int cap = 16)
    {
        Span<uint> seen = stackalloc uint[cap];
        int count = 0;
        for (int i = 0; i < n && count < cap; i += 3)
        {
            uint c = px[i];
            bool found = false;
            for (int k = 0; k < count; k++) if (seen[k] == c) { found = true; break; }
            if (!found) seen[count++] = c;
        }
        return count;
    }

    public static SweepRow Probe(SweepItem item, int frames, Dictionary<int, PadButtons>? custom, string? shotsDir)
    {
        byte[] raw;
        try { raw = File.ReadAllBytes(item.Path); }
        catch (Exception e) { return new SweepRow { File = item.Name, Archive = item.Archive, Class = "crash", Detail = $"{e.GetType().Name}: {e.Message}" }; }
        return ProbeBytes(raw, item.Name, item.Archive, frames, custom, shotsDir);
    }

    public static SweepRow ProbeBytes(byte[] raw, string name, string archive, int frames, Dictionary<int, PadButtons>? custom, string? shotsDir)
    {
        var item = new SweepItem("", name, archive);
        var row = new SweepRow { File = item.Name, Archive = item.Archive };
        try
        {
            row.Size = raw.Length;
            byte[] rom = RomDetect.Unwrap(raw, item.Name, out string inner);
            row.Sha1 = Convert.ToHexString(SHA1.HashData(SegaRomFormat.StripCopierHeader(rom))).ToLowerInvariant();
            var kind = RomDetect.Detect(rom, inner);
            if (kind is not (ConsoleKind.MasterSystem or ConsoleKind.GameGear))
            {
                row.Console = kind?.ToString() ?? "?";
                row.Class = "not-sms"; row.Detail = $"detected as {row.Console}";
                return row;
            }
            row.Console = kind == ConsoleKind.GameGear ? "GG" : "SMS";
            bool gg = kind == ConsoleKind.GameGear;
            var header = SmsHeader.Find(SegaRomFormat.StripCopierHeader(rom));
            row.Header = header is { } h ? $"{h.RegionName};prod{h.ProductCode};v{h.Version};size{h.SizeNibble:X}" : "none";

            using var session = SmsSweepCli.ForcedModel is { } forced && kind == ConsoleKind.MasterSystem
                ? new SmsSession(rom, kind.Value, CoreCatalog.Default(kind.Value, CoreSlot.Cpu), CoreCatalog.Default(kind.Value, CoreSlot.Ppu), CoreCatalog.Default(kind.Value, CoreSlot.Apu), inner, forced, null)
                : SmsRomIo.Concrete(ConsoleSessions.Create(kind.Value, rom, CoreCatalog.Default(kind.Value, CoreSlot.Cpu), CoreCatalog.Default(kind.Value, CoreSlot.Ppu), CoreCatalog.Default(kind.Value, CoreSlot.Apu), null, inner));
            var board = session.Board;
            row.Model = session.Model.ToString(); row.Region = board.Region.ToString().ToUpperInvariant(); row.Mapper = board.Cart.Mapper.ToString();
            bool staticBattery = board.Cart.HasBattery;

            // ---- evidence collected while the game runs
            var romWrites = new Dictionary<ushort, int>();
            int glassesWrites = 0, ioCtlWrites = 0, hcReads = 0, vcReads = 0, padReads = 0, statReads = 0, psg = 0, psgLoud = 0, fm = 0, thMask = 0, dataW = 0;
            var ioCtlValues = new HashSet<byte>();
            long winPortReads = 0, winPadReads = 0;
            bool inWindow = false;
            board.Cart.RomSpaceWriteHook = (addr, val) => { if (romWrites.Count < 32 || romWrites.ContainsKey(addr)) romWrites[addr] = romWrites.GetValueOrDefault(addr) + 1; };
            var ramWrites = new Dictionary<ushort, int>(); int ramWriteTotal = 0; int ramValueMask = 0;
            board.Cart.RamWriteHook = (addr, val) => { ramWriteTotal++; ramValueMask |= val; if (ramWrites.Count < 64 || ramWrites.ContainsKey(addr)) ramWrites[addr] = ramWrites.GetValueOrDefault(addr) + 1; };
            board.MemWriteHook = (addr, val, line, cyc) => { if (addr >= 0xFFF8 && addr <= 0xFFFB) glassesWrites++; };
            board.PortHook = (write, port, value, line, cyc) =>
            {
                int p = port & 0xFF;
                if (inWindow && !write) { winPortReads++; if ((p & 0xC0) == 0xC0 || (gg && p < 7)) winPadReads++; }
                if (write)
                {
                    if (p >= 0xF0) fm++;
                    else if (gg && p < 7) { }
                    else switch (p & 0xC1)
                        {
                            case 0x01: ioCtlWrites++; if (ioCtlValues.Count < 16) ioCtlValues.Add(value); thMask |= value & 0xAA; break;
                            case 0x40: case 0x41: psg++; if ((value & 0x90) == 0x90 && (value & 0x0F) != 0x0F) psgLoud++; break;
                            case 0x80: dataW++; break;
                        }
                }
                else switch (p & 0xC1)
                    {
                        case 0x40: vcReads++; break;
                        case 0x41: hcReads++; break;
                        case 0x81: statReads++; break;
                        case 0xC0: case 0xC1: padReads++; break;
                    }
            };

            var hashes = new ulong[frames];
            var buf = new short[16384];
            int firstDisplay = -1, firstPicture = -1, picturesSeen = 0, lastChange = 0, audioActive = 0, audioPeak = 0, colorsEnd = 0, unmappedEnd = 0;
            int modeBits = 0, maxFrameH = 0, darkIdx = 0, darkEver = 0;
            var darkRing = new bool[30];                 // the last 30 samples (one every 20 frames: 600 frames): was the picture black
            double noiseEnd = 0, noiseMax = 0;
            ulong prev = 0;
            var distinct = new HashSet<ulong>();
            int windowStart = Math.Max(0, frames - 90);
            var windowPcs = new HashSet<ushort>();
            int windowPcOverflow = 0; long windowInstr = 0; int windowUnmapped = 0; bool windowHalted = false;
            var sw = new Stopwatch();
            Exception? crash = null; int crashFrame = -1;
            PadButtons held = 0;
            int f;
            for (f = 0; f < frames; f++)
            {
                if (f == windowStart)
                {
                    inWindow = true;
                    board.InstructionHook = pc =>
                    {
                        windowInstr++;
                        if (windowPcs.Count < 64) windowPcs.Add(pc); else windowPcOverflow++;
                        if (board.IsUnmapped(pc)) windowUnmapped++;
                        if (board.Cpu.Halted) windowHalted = true;
                    };
                }
                held = custom != null ? (custom.TryGetValue(f, out var cb) ? (held = cb) : held) : PadAt(f, gg);
                try
                {
                    session.SetPad(0, held);
                    sw.Start();
                    session.RunFrame();
                    sw.Stop();
                }
                catch (Exception e) { sw.Stop(); crash = e; crashFrame = f; break; }
                int n = session.FrameWidth * session.FrameHeight;
                var px = session.Frame;
                ulong hsh = Fnv(px, n);
                hashes[f] = hsh;
                if (distinct.Count < 100000) distinct.Add(hsh);
                if (hsh != prev) { lastChange = f; prev = hsh; }
                var vdp = board.Vdp;
                if (firstDisplay < 0 && vdp.DisplayEnabled) firstDisplay = f;
                if (f % 20 == 0 || f == frames - 1)
                {
                    int c = Colors(px, n);
                    colorsEnd = c;
                    if (c >= 2) { picturesSeen++; if (firstPicture < 0) firstPicture = f; }
                    bool dark = BlackShare(px, n) >= 0.995;
                    darkRing[darkIdx++ % darkRing.Length] = dark; if (dark) darkEver++;
                    noiseEnd = Transitions(px, session.FrameWidth, session.FrameHeight);
                    noiseMax = Math.Max(noiseMax, noiseEnd);
                    // the VDP features in use (register values at the frame's end)
                    int r0 = vdp.Regs[0], r1 = vdp.Regs[1];
                    if ((r0 & 0x04) != 0) modeBits |= 1;           // Mode 4
                    else modeBits |= 1 << 1;                       // TMS9918 modes
                    if ((r1 & 0x02) != 0) modeBits |= 1 << 2;      // 8x16 sprites
                    if ((r1 & 0x01) != 0) modeBits |= 1 << 3;      // zoomed sprites
                    if ((r0 & 0x20) != 0) modeBits |= 1 << 4;      // left column blanked
                    if ((r0 & 0x10) != 0) modeBits |= 1 << 5;      // line interrupt on
                    if ((r0 & 0x40) != 0) modeBits |= 1 << 6;      // horizontal scroll lock (top two rows)
                    if ((r0 & 0x80) != 0) modeBits |= 1 << 7;      // vertical scroll lock (right columns)
                    if (vdp.ActiveHeight != 192) modeBits |= 1 << 8;
                    if ((r0 & 0x01) != 0) modeBits |= 1 << 9;      // mode control bit 0 (the 315-5124's extra bit)
                }
                maxFrameH = Math.Max(maxFrameH, vdp.ActiveHeight);
                if (board.IsUnmapped(board.Cpu.PC)) unmappedEnd++;
                int ns, framePeak = 0;
                while ((ns = session.ReadSamples(buf)) > 0) for (int i = 0; i < ns; i++) framePeak = Math.Max(framePeak, Math.Abs((int)buf[i]));
                if (framePeak > 200) audioActive++;
                audioPeak = Math.Max(audioPeak, framePeak);
            }
            board.InstructionHook = null; board.PortHook = null; board.MemWriteHook = null; board.Cart.RomSpaceWriteHook = null; board.Cart.RamWriteHook = null;
            row.CartRam = ramWriteTotal == 0 ? "" : $"{ramWriteTotal}w/{ramWrites.Count}a/mask{ramValueMask:X2}:{string.Join(' ', ramWrites.OrderByDescending(kv => kv.Value).Take(3).Select(kv => kv.Key.ToString("X4")))}";
            row.Frames = f;
            double secs = sw.Elapsed.TotalSeconds;
            row.MsPerFrame = f > 0 ? secs * 1000 / f : 0;
            row.XRealTime = secs > 0 ? f / secs / session.FramesPerSecond : 0;
            row.DisplayFirst = firstDisplay; row.PictureFirst = firstPicture; row.Colors = colorsEnd; row.DistinctPictures = distinct.Count;
            row.StaticTail = f > 0 ? f - 1 - lastChange : 0;
            row.AudioPeak = audioPeak; row.AudioActive = audioActive; row.PsgWrites = psg; row.FmWrites = fm;
            var cpu = board.Cpu;
            row.PcEnd = $"{cpu.PC:X4}"; row.Iff = cpu.IFF1 ? "on" : "off";
            row.WindowPcs = windowPcs.Count >= 64 ? "64+" : windowPcs.Count.ToString();
            row.FinalHash = f > 0 ? hashes[f - 1].ToString("X16") : "";
            row.Battery = $"{(staticBattery ? "scan" : "-")}/{(board.Cart.HasBattery ? "yes" : "no")}";
            row.Modes = ModeString(modeBits);
            row.RomWrites = string.Join(' ', romWrites.OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"{kv.Key:X4}x{kv.Value}"));
            row.Ports = $"pad{padReads} stat{statReads} hc{hcReads} vc{vcReads} th{ioCtlWrites}/{ioCtlValues.Count} vdpw{dataW}";
            int darkTail = darkRing.Count(d => d);
            row.DarkTail = Math.Min(darkIdx, darkRing.Length) == 0 ? 0 : darkTail;
            row.Noise = noiseEnd;

            // ---- the evidence in words, then the class
            var flags = new List<string>();
            var peripheral = Peripheral(item.Name, hcReads, ioCtlWrites, ioCtlValues, thMask, glassesWrites, f);
            if (peripheral != null) flags.Add("peripheral:" + peripheral);
            if (MapperGuess(romWrites, board.Cart.Mapper) is { } mg) flags.Add("rom-writes:" + mg);
            if (!staticBattery && board.Cart.HasBattery) flags.Add("battery-found-late");
            if (item.Name.Contains("[BIOS]", StringComparison.OrdinalIgnoreCase)) flags.Add("bios-image");
            if (item.Name.Contains("[b", StringComparison.OrdinalIgnoreCase)) flags.Add("bad-dump-tag");
            if (header == null && rom.Length >= 0x8000 && !SegaRomFormat.StripCopierHeader(rom).AsSpan(0, Math.Min(rom.Length, 0x8000)).ContainsAnyExcept((byte)0xFF)) flags.Add("blank-rom");
            if (gg == false && item.Name.Contains("GG2SMS", StringComparison.OrdinalIgnoreCase)) flags.Add("gg2sms-hack");
            row.Flags = string.Join(' ', flags);

            if (crash != null) { row.Class = "crash"; row.Detail = $"frame {crashFrame}: {crash.GetType().Name}: {crash.Message} at {crash.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}"; return row; }

            bool tinyLoop = windowPcs.Count <= 6 && windowPcOverflow == 0;
            bool polls = winPortReads > 0;
            if (windowUnmapped > windowInstr / 2 && windowInstr > 0) { row.Class = "hung"; row.Detail = $"running from memory nothing answers (PC {cpu.PC:X4}, {windowUnmapped} of {windowInstr} instructions in the last 90 frames)"; }
            else if (unmappedEnd > frames / 2) { row.Class = "hung"; row.Detail = $"PC in unmapped memory at the end of {unmappedEnd} frames"; }
            else if (!cpu.IFF1 && windowInstr > 0 && tinyLoop && !polls && row.StaticTail >= 60) { row.Class = "hung"; row.Detail = $"Z80 dead: {windowPcs.Count} address(es) executed in the last 90 frames, interrupts off, no port read (PC {cpu.PC:X4}{(windowHalted ? ", HALT" : "")})"; }
            else if (!cpu.IFF1 && cpu.Halted) { row.Class = "hung"; row.Detail = $"HALT with interrupts off (PC {cpu.PC:X4})"; }
            else if (firstPicture < 0) { row.Class = "no-picture"; row.Detail = firstDisplay < 0 ? "the display was never switched on" : $"display on from frame {firstDisplay} but never two colours"; }
            else if (colorsEnd < 2 && f - 1 - lastChange >= 300) { row.Class = "blank"; row.Detail = $"the picture (from frame {firstPicture}) is a single colour at the end, unchanged for {row.StaticTail} frames"; }
            else if (row.DarkTail >= 25 && darkEver < frames / 20) { row.Class = "dark-end"; row.Detail = $"black in {row.DarkTail} of the last 30 samples (a picture was shown from frame {firstPicture}; {darkEver} black samples in all)"; }
            else if (row.DarkTail >= 25) { row.Class = "dark"; row.Detail = $"black in {row.DarkTail} of the last 30 samples and in {darkEver} of {frames / 20} overall"; }
            else if (noiseEnd > 0.55) { row.Class = "noisy"; row.Detail = $"{noiseEnd:P0} of neighbouring pixels differ at the end (a scene is under a third; noise is near all)"; }
            else if (row.StaticTail >= 900) { row.Class = "static"; row.Detail = $"picture unchanged for the last {row.StaticTail} frames ({row.WindowPcs} addresses executed, {(polls ? "polls ports" : "no port reads")}, interrupts {row.Iff})"; }
            else if (psg == 0 && fm == 0) { row.Class = "silent"; row.Detail = "not one PSG write in the whole run"; }
            else if (psgLoud == 0 && fm == 0) { row.Class = "silent"; row.Detail = $"{psg} PSG writes, all of them silence (volume off): no sound ever started in {f} frames"; }
            else if (audioPeak < 100) { row.Class = "audio-bug?"; row.Detail = $"{psgLoud} PSG volume writes above silence but the output never exceeded {audioPeak}"; }
            else { row.Class = "ok"; row.Detail = ""; }

            if (shotsDir != null)
            {
                string safe = string.Concat((row.Console + "_" + Path.GetFileNameWithoutExtension(item.Name)).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                SmsPng.Save(session.Frame, session.FrameWidth, session.FrameHeight, Path.Combine(shotsDir, safe + ".png"));
            }
        }
        catch (Exception e)
        {
            row.Class = row.Class.Length > 0 ? row.Class : "crash";
            row.Detail = $"{e.GetType().Name}: {e.Message} at {e.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}";
        }
        return row;
    }

    private static string ModeString(int bits)
    {
        string[] names = { "M4", "TMS", "S16", "zoom", "col0", "lineirq", "hlock", "vlock", "H224/240", "r0b0" };
        return string.Join(',', names.Where((n, i) => (bits >> i & 1) != 0));
    }

    /// <summary>Peripherals the plan leaves out, by the name's tag and by what the program does with the pad lines.</summary>
    private static string? Peripheral(string name, int hcReads, int ioCtlWrites, HashSet<byte> ioCtlValues, int thMask, int glassesWrites, int frames)
    {
        string n = name.ToLowerInvariant();
        if (n.Contains("light phaser") || n.Contains("(phaser")) return "light-phaser(name)";
        if (n.Contains("paddle")) return "paddle(name)";
        if (n.Contains("sports pad")) return "sports-pad(name)";
        if (n.Contains("3-d") || n.Contains("3d)")) return "3d-glasses(name)";
        if (n.Contains("keyboard")) return "keyboard(name)";
        if (glassesWrites > 20) return "3d-glasses(writes-to-FFF8)";
        if (hcReads > frames) return "light-phaser(H-counter-reads)";
        if (ioCtlWrites > frames * 2) return "paddle-or-sports-pad?(TH-toggled-" + ioCtlWrites + "x)";
        return null;
    }

    /// <summary>The cartridge a program's writes into ROM space look like it expects (null when it writes nothing there): a hint for the triage, named by the register addresses the mappers use.</summary>
    internal static string? MapperGuess(Dictionary<ushort, int> writes, SmsMapperKind selected)
    {
        if (writes.Count == 0) return null;
        bool Has(int a) => writes.ContainsKey((ushort)a);
        var list = string.Join(',', writes.OrderByDescending(kv => kv.Value).Take(4).Select(kv => kv.Key.ToString("X4")));
        if (Has(0x3FFE) || Has(0x7FFF) || Has(0xBFFF)) return $"4PAK[{list}]";
        if (Has(0x0000) && Has(0x4000) && Has(0x8000)) return selected == SmsMapperKind.Codemasters ? null : $"Codemasters[{list}]";
        if (Has(0xA000) && !Has(0x8000)) return $"KoreanA000[{list}]";
        if (Has(0x0000) || Has(0x0001) || Has(0x0002) || Has(0x0003)) return selected is SmsMapperKind.MsxKorean or SmsMapperKind.Nemesis ? null : selected == SmsMapperKind.Codemasters ? null : $"MSX/Nemesis[{list}]";
        if (selected == SmsMapperKind.Codemasters) return null;
        return $"other[{list}]";
    }
}

/// <summary>
/// <c>--sms-sweep --selftest [--poison]</c>: the classifier run on hand-assembled programs that are each one of the sweep's classes (and on a clean one), so a change that makes it call everything "ok" is seen. <c>--poison</c> replaces every
/// verdict by "ok" before the comparison: the proof that this check fails when the classifier is broken.
/// </summary>
internal static class SweepSelfTest
{
    private static readonly int[] Regs = { 0x04, 0x80, 0x80, 0x81, 0xFF, 0x82, 0xFF, 0x83, 0xFF, 0x84, 0xFF, 0x85, 0xFF, 0x86, 0x00, 0x87, 0x00, 0x88, 0x00, 0x89, 0xFF, 0x8A };

    /// <summary>Common start: DI, IM 1, stack, the VDP registers, a palette of two colours, one solid tile and a name table of stripes; <paramref name="displayOn"/> then sets R1.</summary>
    private static Z80Asm Start(bool displayOn)
    {
        var a = new Z80Asm(0x100);
        a.Db(0xF3, 0xED, 0x56, 0x31, 0xF0, 0xDF);
        a.Db(0x0E, 0xBF, 0x06, Regs.Length).LdHl("regs").Db(0xED, 0xB3);
        a.OutImm(0xBF, 0x00).OutImm(0xBF, 0xC0).OutImm(0xBE, 0x00).OutImm(0xBE, 0x30).OutImm(0xBE, 0x03);           // colours 0 black, 1 blue, 2 red
        a.OutImm(0xBF, 0x20).OutImm(0xBF, 0x40);                                                                    // VRAM $0020: tile 1, solid colour 1
        for (int row = 0; row < 8; row++) a.OutImm(0xBE, 0xFF).OutImm(0xBE, 0x00).OutImm(0xBE, 0x00).OutImm(0xBE, 0x00);
        a.OutImm(0xBF, 0x00).OutImm(0xBF, 0x78);                                                                    // name table $3800: every other tile column
        a.Db(0x11, 0x00, 0x03);                                                                                      // LD DE,768
        a.Label("nt").Db(0x7B, 0xE6, 0x01).OutN(0xBE).Db(0xAF).OutN(0xBE).Db(0x1B, 0x7A, 0xB3).JrNz("nt");           // tile (E & 1), high byte 0
        if (displayOn) a.OutImm(0xBF, 0xC0).OutImm(0xBF, 0x81);
        return a;
    }

    private static byte[] Rom(Z80Asm a) { a.Label("regs").Bytes(Regs.Select(x => (byte)x)); return SmsProg.Rom(a.Assemble(), null, null, 0x100); }

    /// <summary>Scrolls every frame (the picture keeps changing) and plays a tone on the PSG: a clean game.</summary>
    private static byte[] Healthy(bool tone)
    {
        var a = Start(true);
        if (tone) a.OutImm(0x7F, 0x8A).OutImm(0x7F, 0x0A).OutImm(0x7F, 0x90);              // tone 1, a period, full volume
        else a.OutImm(0x7F, 0x9F);                                                           // volume off: all silence
        a.Db(0x16, 0x00);                                                                    // LD D,0
        a.Label("v").InN(0x7E).Db(0xFE, 0xC0).JrNz("v");                                     // wait for line 192
        a.Db(0x14, 0x7A).OutN(0xBF).OutImm(0xBF, 0x88);                                      // INC D; LD A,D; OUT: horizontal scroll = D
        a.Label("w").InN(0x7E).Db(0xFE, 0xC0).JrZ("w").Jr("v");
        return Rom(a);
    }

    private static byte[] DeadLoop() { var a = Start(true); a.Label("here").Jr("here"); return Rom(a); }                                 // display on, DI, JR $: the Z80 is dead
    private static byte[] NoDisplay() { var a = Start(false); a.Db(0xFB); a.Label("here").Jr("here"); return Rom(a); }                  // EI, JR $, the display never switched on
    private static byte[] StaticPicture() { var a = Start(true); a.Db(0xFB); a.Label("here").Jr("here"); return Rom(a); }                  // a picture, EI, JR $: nothing ever changes
    private static byte[] RunsOff() { var a = Start(true); a.OutImm(0x3E, 0xE8); a.Db(0xC3, 0x00, 0x40); return Rom(a); }                  // switches the cartridge slot off (port $3E bit 6) and keeps running into memory nothing answers

    public static int Run(bool poison)
    {
        var cases = new (string Name, byte[] Rom, string Expect)[]
        {
            ("a clean game (animated picture, a tone)", Healthy(true), "ok"),
            ("a game that never starts a sound (volume off only)", Healthy(false), "silent"),
            ("a program that halts with interrupts off", DeadLoop(), "hung"),
            ("a program that never switches the display on", NoDisplay(), "no-picture"),
            ("a program whose picture never changes", StaticPicture(), "static"),
            ("a program that runs into memory nothing answers", RunsOff(), "hung"),
        };
        int bad = 0;
        foreach (var (name, rom, expect) in cases)
        {
            var row = SweepProbe.ProbeBytes(rom, "selftest.sms", "", 1500, null, null);
            string got = poison ? "ok" : row.Class;
            bool pass = got == expect;
            if (!pass) bad++;
            Console.WriteLine($"{(pass ? "PASS" : "FAIL")}  {name}: wanted {expect}, got {got}{(pass ? "" : "  (" + row.Detail + ")")}");
        }
        Console.WriteLine(bad == 0 ? "sweep selftest: every class recognised" : $"sweep selftest: {bad} of {cases.Length} wrong");
        return bad == 0 ? 0 : 1;
    }
}

internal static class Summary
{
    public static void Print(List<SweepRow> rows, double seconds, int frames)
    {
        Console.WriteLine();
        Console.WriteLine($"=== sweep summary: {rows.Count} games, {frames} frames each, {seconds:F0} s ===");
        foreach (var g in rows.GroupBy(r => r.Console).OrderBy(g => g.Key))
        {
            Console.WriteLine($"-- {g.Key}: {g.Count()} games");
            foreach (var c in g.GroupBy(r => r.Class).OrderByDescending(c => c.Count())) Console.WriteLine($"   {c.Key,-11} {c.Count(),4}");
        }
        var timed = rows.Where(r => r.XRealTime > 0).Select(r => r.XRealTime).OrderBy(x => x).ToList();
        if (timed.Count > 0) Console.WriteLine($"speed (per game, jobs run side by side): median {timed[timed.Count / 2]:F0}x real time, slowest {timed[0]:F0}x, fastest {timed[^1]:F0}x");
        Console.WriteLine("-- flags");
        foreach (var kv in rows.SelectMany(r => r.Flags.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(f => f.Split(':')[0] + (f.Contains(':') ? ":" + f.Split(':')[1].Split('[')[0].Split('(')[0] : ""))).GroupBy(x => x).OrderByDescending(g => g.Count()))
            Console.WriteLine($"   {kv.Key,-34} {kv.Count(),4}");
        Console.WriteLine("-- mappers selected");
        foreach (var g in rows.GroupBy(r => r.Mapper).OrderByDescending(g => g.Count())) Console.WriteLine($"   {g.Key,-14} {g.Count(),4}");
        Console.WriteLine("-- not ok");
        foreach (var r in rows.Where(r => r.Class != "ok" && r.Class != "silent").Take(400)) Console.WriteLine($"   [{r.Class}] {r.Console} {r.File}: {r.Detail} {r.Flags}");
    }
}

/// <summary>Contact sheets of the sweep's last frames, one set per class (so a human can look at all the pictures of a class in a few images).</summary>
internal static class Sheets
{
    public static void Make(List<SweepRow> rows, string shotsDir)
    {
        foreach (var group in rows.GroupBy(r => r.Class))
        {
            var list = group.OrderBy(r => r.File, StringComparer.OrdinalIgnoreCase).ToList();
            const int cols = 6, perSheet = 36, tw = 160, th = 120, label = 30;
            for (int s = 0; s * perSheet < list.Count; s++)
            {
                var chunk = list.Skip(s * perSheet).Take(perSheet).ToList();
                int rowsN = (chunk.Count + cols - 1) / cols;
                using var bmp = new Bitmap(cols * tw, rowsN * (th + label));
                using var g = Graphics.FromImage(bmp);
                g.Clear(Color.FromArgb(24, 24, 28));
                using var font = new Font("Segoe UI", 7f);
                for (int i = 0; i < chunk.Count; i++)
                {
                    var r = chunk[i];
                    string safe = string.Concat((r.Console + "_" + Path.GetFileNameWithoutExtension(r.File)).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                    string png = Path.Combine(shotsDir, safe + ".png");
                    int x = i % cols * tw, y = i / cols * (th + label);
                    if (File.Exists(png))
                    {
                        using var img = Image.FromFile(png);
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                        float k = Math.Min(tw / (float)img.Width, th / (float)img.Height);
                        g.DrawImage(img, x + (tw - img.Width * k) / 2, y, img.Width * k, img.Height * k);
                    }
                    string cap = (r.Console + " " + Path.GetFileNameWithoutExtension(r.File));
                    g.DrawString(cap.Length > 52 ? cap[..52] : cap, font, Brushes.White, new RectangleF(x + 2, y + th + 1, tw - 4, label - 2));
                }
                string path = Path.Combine(shotsDir, $"sheet_{group.Key.Replace('?', 'q')}_{s + 1}.png");   // the class "audio-bug?" has a character a file name cannot (GDI+ then failed with a generic error and ended the whole sweep run)
                bmp.Save(path, ImageFormat.Png);
            }
        }
        Console.WriteLine("contact sheets written to " + shotsDir);
    }
}
