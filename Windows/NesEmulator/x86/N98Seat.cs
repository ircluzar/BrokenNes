using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Linq;
using System.Text.Json;

namespace NesEmulator.X86;

/// <summary>
/// nano98 "game seat": a .n98 package (docs/projects/nano98-format.md) booted straight into its game.
/// The package is applied onto the seat base image (a Win98 SE nano98 disk with C:\N98\N98LAUNCH.EXE as the shell) by the
/// package tools (tools/nano98/pkg/n98.py apply --full), once per package, into a cache folder; the seat command (COMMAND PORT text)
/// comes from n98.py cmd. The cache folder also holds the resume snapshot (see <see cref="PcSession"/>), kept per emulator build.
/// Settings: BROKENNES_N98_BASE (seat base image; default &lt;app&gt;\x86\nano98\seat_base.img), BROKENNES_N98_TOOLS (folder with
/// pkg\n98.py; default: found above the app folder, i.e. the repo's tools\nano98), BROKENNES_PYTHON (default python),
/// BROKENNES_N98_CDS (folder with the user's disc images the packages name), BROKENNES_N98_CACHE (default %LOCALAPPDATA%\BrokenNes\nano98).
/// </summary>
public sealed partial class N98Seat
{
    public string Id = "", Title = "", Machine = "win98-p166-nano", CacheDir = "", ImagePath = "";
    public byte[] Image = Array.Empty<byte>(), Command = Array.Empty<byte>();
    public string? CdPath;
    /// <summary>The game's save files, kept by the host as files: (saves root)\N98Saves\n98(id)\files\(drive)\(path).</summary>
    public string SavesDir = "";
    /// <summary>The manifest's save patterns ("C:/GAME/SAVE/*").</summary>
    public string[] SavePatterns = Array.Empty<string>();
    /// <summary>The seat's second disk (primary slave) when the package writes D: (StarCraft's install does not fit on the seat C:).</summary>
    public byte[]? Image2; public string? Image2Path;

    public static bool IsPackage(string fileName) => fileName.EndsWith(".n98", StringComparison.OrdinalIgnoreCase);

    /// <summary>This emulator build: resume snapshots made by another build are not used (they are rebuilt from a cold boot).</summary>
    public static string EmulatorVersion => typeof(PcMachine).Assembly.ManifestModule.ModuleVersionId.ToString("N")[..16];

    public static N98Seat Prepare(byte[] pkg, string fileName)
    {
        string id = Convert.ToHexString(SHA1.HashData(pkg))[..16].ToLowerInvariant();
        string root = Environment.GetEnvironmentVariable("BROKENNES_N98_CACHE") is { Length: > 0 } c ? c
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrokenNes", "nano98");
        var s = new N98Seat { Id = id, CacheDir = Environment.GetEnvironmentVariable("BROKENNES_N98_CACHEDIR") is { Length: > 0 } cd0 ? cd0 : Path.Combine(root, id) };   // (CACHEDIR: this one package's folder, for tests)
        string? dName = null;
        Directory.CreateDirectory(s.CacheDir);
        if (SchemaOf(pkg) >= 2) return PrepareV2(pkg, fileName, s);
        string pkgPath = Path.Combine(s.CacheDir, "package.n98");
        if (!File.Exists(pkgPath)) File.WriteAllBytes(pkgPath, pkg);

        using (var z = new ZipArchive(new MemoryStream(pkg), ZipArchiveMode.Read))
        {
            var me = z.GetEntry("manifest.json") ?? throw new InvalidDataException($"{fileName}: not a .n98 package (no manifest.json)");
            using var doc = JsonDocument.Parse(me.Open());
            var r = doc.RootElement;
            if (r.TryGetProperty("schema", out var sc) && sc.GetInt32() > 1) throw new InvalidDataException($"{fileName}: package schema {sc.GetInt32()} is newer than this app knows (1)");
            s.Title = r.TryGetProperty("title", out var t) ? t.GetString() ?? id : id;
            if (r.TryGetProperty("saves", out var sv) && sv.ValueKind == JsonValueKind.Array)
                s.SavePatterns = sv.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 3).ToArray();
            string hw = r.TryGetProperty("hardware", out var h) && h.TryGetProperty("machine", out var mm) ? mm.GetString() ?? "" : "";
            // the machine the package names (an optional "3d" note does not switch the card on); the seat is always the floppy-less machine
            bool voodoo = hw.Contains("VOODOO", StringComparison.OrdinalIgnoreCase);
            s.Machine = voodoo ? "win98-p166-voodoo-nano" : "win98-p166-nano";
            if (Environment.GetEnvironmentVariable("BROKENNES_N98_MACHINE") is { Length: > 0 } mo) s.Machine = mo;   // (a test / user override)
            if (r.TryGetProperty("cd", out var cds) && cds.ValueKind == JsonValueKind.Array)
                foreach (var cd in cds.EnumerateArray())
                    if (cd.TryGetProperty("ref", out var rf) && rf.GetString() is { Length: > 0 } name)
                        foreach (var dir in CdDirs(s.CacheDir))
                            if (!string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, name))) { s.CdPath ??= Path.Combine(dir, name); break; }
            if (r.TryGetProperty("base", out var bs) && bs.TryGetProperty("drives", out var dv) && dv.TryGetProperty("D", out var dd))
                dName = dd.TryGetProperty("image", out var di) ? di.GetString() : null;
        }

        string baseImg = FindBase();
        s.ImagePath = Path.Combine(s.CacheDir, "C.img");
        // D: base (BROKENNES_N98_BASE_D, else the manifest's D image beside the seat base / in BROKENNES_N98_CDS)
        string? baseD = null;
        if (dName != null)
        {
            baseD = Environment.GetEnvironmentVariable("BROKENNES_N98_BASE_D") is { Length: > 0 } bd ? bd
                : new[] { Path.GetDirectoryName(baseImg), Environment.GetEnvironmentVariable("BROKENNES_N98_CDS"), LocalRoot, @"C:\BrokenNes-work\sc98" }.Where(x => !string.IsNullOrEmpty(x))
                    .Select(x => Path.Combine(x!, dName)).FirstOrDefault(File.Exists);
            if (baseD == null || !File.Exists(baseD)) throw new FileNotFoundException($"{fileName} writes drive D: and its D base image ({dName}) was not found; set BROKENNES_N98_BASE_D");
            s.Image2Path = Path.Combine(s.CacheDir, "D.img");
        }
        string cmdPath = Path.Combine(s.CacheDir, "command.txt"), stamp = Path.Combine(s.CacheDir, "layers.ok");
        // the applied game is kept as copy-on-write LAYERS over the seat base (C.n98l / D.n98l, only the sectors the package changes),
        // not as whole disks: the drive is composed in memory at start (base + layer), its identity hash cached beside the layer
        DateTime newest = new[] { File.GetLastWriteTimeUtc(baseImg), baseD != null ? File.GetLastWriteTimeUtc(baseD) : DateTime.MinValue, File.GetLastWriteTimeUtc(pkgPath) }.Max();
        bool prebuilt = File.Exists(stamp) && Environment.GetEnvironmentVariable("BROKENNES_N98_PREBUILT") == "1";   // (a cache built elsewhere and copied: file times are not kept)
        if (!prebuilt && (!File.Exists(stamp) || File.GetLastWriteTimeUtc(stamp) < newest))
        {
            string outDir = Path.Combine(s.CacheDir, "apply.tmp");
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
            RunTool("apply", $"\"{pkgPath}\" --base C=\"{baseImg}\"" + (baseD != null ? $" D=\"{baseD}\"" : "") + $" --out \"{outDir}\"");
            foreach (var dl in new[] { "C", "D" })
            {
                string from = Path.Combine(outDir, dl + ".n98l"), to = Path.Combine(s.CacheDir, dl + ".n98l");
                if (File.Exists(from)) File.Move(from, to, true); else File.Delete(to);
                File.Delete(Path.Combine(s.CacheDir, dl + ".p256"));
            }
            Directory.Delete(outDir, true);
            foreach (var old in new[] { "C.img", "D.img" }) File.Delete(Path.Combine(s.CacheDir, old));   // (whole disks from older builds)
            File.Delete(cmdPath);
            foreach (var old in Directory.GetDirectories(s.CacheDir)) Directory.Delete(old, true);   // resume snapshots of the old disk
            File.WriteAllText(stamp, DateTime.UtcNow.ToString("o"));
        }
        if (!File.Exists(cmdPath)) RunTool("cmd", $"\"{pkgPath}\" \"{cmdPath}\" --exit poweroff");
        s.Command = File.ReadAllBytes(cmdPath);
        string savesRoot = Environment.GetEnvironmentVariable("BROKENNES_SAVES_DIR") is { Length: > 0 } sr ? sr
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BrokenNes");
        s.SavesDir = Path.Combine(savesRoot, "N98Saves", "n98" + s.Id, "files");
        s.ImagePath = baseImg;
        s.Image = Compose(baseImg, Path.Combine(s.CacheDir, "C.n98l"), Path.Combine(s.CacheDir, "C.p256"));
        if (baseD != null) { s.Image2Path = baseD; s.Image2 = Compose(baseD, Path.Combine(s.CacheDir, "D.n98l"), Path.Combine(s.CacheDir, "D.p256")); }
        return s;
    }

    /// <summary>A drive = its base image with the package's layer applied (a new array; the base file is only read). No layer: the base itself
    /// (with its own cached hash). The composed drive's identity hash (what savestates name it by) comes from PATH.p256 when that still matches
    /// the base and the layer (size + time), else it is made once in the background and written there.</summary>
    private static byte[] Compose(string basePath, string layerPath, string hashPath)
    {
        if (!File.Exists(layerPath)) return DiskImage.ReadImageFile(basePath);
        var bi = new FileInfo(basePath); var li = new FileInfo(layerPath);
        string key = $"{bi.Length}|{bi.LastWriteTimeUtc.Ticks}|{li.Length}|{li.LastWriteTimeUtc.Ticks}";
        byte[] img;
        if (DiskImage.IsSparse(basePath)) { img = DiskImage.ReadSparse(basePath); }
        else
        {
            img = new byte[bi.Length];
            using var f = File.OpenRead(basePath); int at = 0; while (at < img.Length) { int n = f.Read(img, at, Math.Min(1 << 24, img.Length - at)); if (n <= 0) break; at += n; }
        }
        using (var r = new BinaryReader(File.OpenRead(layerPath)))
        {
            if (new string(r.ReadChars(4)) != "N98L" || r.ReadInt32() != 1) throw new InvalidDataException($"{layerPath}: not a version 1 N98L layer");
            if (r.ReadInt64() != img.LongLength) throw new InvalidDataException($"{layerPath}: made for a base of another size");
            r.ReadBytes(r.ReadByte());   // the base's hash (the applier checked it)
            for (int runs = r.ReadInt32(); runs > 0; runs--)
            {
                long first = r.ReadInt64(); int count = r.ReadInt32();
                int got = r.Read(img, checked((int)(first * 512)), count * 512);
                if (got != count * 512) throw new InvalidDataException($"{layerPath}: truncated");
            }
        }
        try
        {
            var l = File.Exists(hashPath) ? File.ReadAllLines(hashPath) : Array.Empty<string>();
            if (l.Length >= 2 && l[0] == key && l[1].StartsWith("p256:", StringComparison.Ordinal)) DiskImage.SeedHash(img, l[1]);
            else System.Threading.Tasks.Task.Run(() => { string h = DiskImage.HashOf(img); try { File.WriteAllLines(hashPath, new[] { key, h }); } catch (IOException) { } });
        }
        catch (IOException) { }
        return img;
    }

    /// <summary>The command port's bytes: the command text + the save patterns, a 0, then (when withFiles) the kept save files as
    /// [u32 path length][path][u32 size][bytes] records (the launcher writes them back before the game starts), ended by a 0 length.</summary>
    public byte[] CommandWith(bool withFiles)
    {
        var ms = new MemoryStream();
        string text = System.Text.Encoding.Latin1.GetString(Command).TrimEnd((char)0);
        if (!text.EndsWith("\n")) text += "\r\n";
        foreach (var pat in SavePatterns) text += "save=" + pat.Replace('/', '\\') + "\r\n";
        ms.Write(System.Text.Encoding.Latin1.GetBytes(text)); ms.WriteByte(0);
        var w = new BinaryWriter(ms);
        foreach (var (guest, data) in SystemFiles)   // (schema 2: the package's files on C:, every start)
        {
            var pb = System.Text.Encoding.Latin1.GetBytes(guest);
            w.Write((uint)pb.Length); w.Write(pb); w.Write((uint)data.Length); w.Write(data);
        }
        if (withFiles && Directory.Exists(SavesDir))
            foreach (var f in Directory.GetFiles(SavesDir, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(SavesDir, f);   // C\GAME\SAVE\x.sav
                if (rel.Length < 3 || rel[1] != Path.DirectorySeparatorChar) continue;
                string guest = rel[0] + ":\\" + rel[2..];
                var pb = System.Text.Encoding.Latin1.GetBytes(guest); var data = File.ReadAllBytes(f);
                w.Write((uint)pb.Length); w.Write(pb); w.Write((uint)data.Length); w.Write(data);
            }
        w.Write(0u);
        if (V2) w.Write(SystemOps);   // "N98R": the package's registry + INI changes (the launcher applies them before the game)
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>The resume snapshot's folder for this emulator build.</summary>
    public string ResumeDir => Path.Combine(CacheDir, "resume-" + EmulatorVersion);

    private static string LocalRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrokenNes", "nano98");

    /// <summary>The seat base: BROKENNES_N98_BASE, else seat_base.img in the app's x86\nano98 folder, %LOCALAPPDATA%\BrokenNes\nano98, or the dev work folder.</summary>
    private static string FindBase()
    {
        var tried = new System.Collections.Generic.List<string>();
        if (Environment.GetEnvironmentVariable("BROKENNES_N98_BASE") is { Length: > 0 } b) tried.Add(b);
        tried.Add(Path.Combine(AppContext.BaseDirectory, "x86", "nano98", "seat_base.img"));
        tried.Add(Path.Combine(LocalRoot, "seat_base.img"));
        tried.Add(@"C:\BrokenNes-work\nano98_work\seat_base.img");
        foreach (var c in tried) { if (File.Exists(c)) return c; if (File.Exists(Path.ChangeExtension(c, ".n98d"))) return Path.ChangeExtension(c, ".n98d"); }
        throw new FileNotFoundException("The Windows 98 seat (the base disk every .n98 game runs on) was not found. It is made once from your own Windows 98 SE disc "
            + @"(tools\nano98\build_base.py, then seat.py install). Put seat_base.img in one of:" + Environment.NewLine + string.Join(Environment.NewLine, tried)
            + Environment.NewLine + "(or set BROKENNES_N98_BASE to its path).");
    }

    /// <summary>Where the user's disc images are looked for: BROKENNES_N98_CDS, the app's x86\cds folder, %LOCALAPPDATA%\BrokenNes\nano98\cds, the dev disc folder, the package's cache folder.</summary>
    private static System.Collections.Generic.IEnumerable<string> CdDirs(string cache)
    {
        if (Environment.GetEnvironmentVariable("BROKENNES_N98_CDS") is { Length: > 0 } c) yield return c;
        yield return Path.Combine(AppContext.BaseDirectory, "x86", "cds");
        yield return Path.Combine(LocalRoot, "cds");
        yield return @"C:\BrokenNes-work\x86_cds";
        yield return cache;
    }

    private static string Tools()
    {
        if (Environment.GetEnvironmentVariable("BROKENNES_N98_TOOLS") is { Length: > 0 } t) return t;
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "tools", "nano98", "pkg", "n98.py"))) return Path.Combine(d.FullName, "tools", "nano98");
        string bundled = Path.Combine(AppContext.BaseDirectory, "x86", "nano98", "tools");
        if (File.Exists(Path.Combine(bundled, "pkg", "n98.py"))) return bundled;
        throw new FileNotFoundException(@"The nano98 package tools (tools\nano98\pkg\n98.py) were not found above the app's folder or in its x86\nano98\tools folder; set BROKENNES_N98_TOOLS.");
    }

    /// <summary>Python 3: BROKENNES_PYTHON, else the py launcher (py -3), else python on PATH.</summary>
    private static (string exe, string pre) Python()
    {
        if (Environment.GetEnvironmentVariable("BROKENNES_PYTHON") is { Length: > 0 } p) return (p, "");
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try { if (dir.Length > 0 && File.Exists(Path.Combine(dir, "py.exe"))) return (Path.Combine(dir, "py.exe"), "-3 "); }
            catch (ArgumentException) { }
        }
        if (File.Exists(@"C:\Windows\py.exe")) return (@"C:\Windows\py.exe", "-3 ");
        return ("python", "");
    }

    private static void RunTool(string verb, string args)
    {
        var (py, pre) = Python();
        var psi = new ProcessStartInfo(py, $"{pre}-I \"{Path.Combine(Tools(), "pkg", "n98.py")}\" {verb} {args}")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        Process? started;
        try { started = Process.Start(psi); }
        catch (System.ComponentModel.Win32Exception) { started = null; }
        using var pr = started ?? throw new InvalidOperationException("Python 3 is needed to prepare a .n98 game the first time and was not found (no py launcher, no python on PATH). Install Python 3 from python.org, or set BROKENNES_PYTHON.");
        string err = pr.StandardError.ReadToEnd(); pr.StandardOutput.ReadToEnd(); pr.WaitForExit();
        if (pr.ExitCode != 0) throw new InvalidOperationException($"n98.py {verb} failed ({pr.ExitCode}): {err.Trim()}");
    }
}
