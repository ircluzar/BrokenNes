using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
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
public sealed class N98Seat
{
    public string Id = "", Title = "", Machine = "win98-p166-nano", CacheDir = "", ImagePath = "";
    public byte[] Image = Array.Empty<byte>(), Command = Array.Empty<byte>();
    public string? CdPath;
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
        var s = new N98Seat { Id = id, CacheDir = Path.Combine(root, id) };
        string? dName = null;
        Directory.CreateDirectory(s.CacheDir);
        string pkgPath = Path.Combine(s.CacheDir, "package.n98");
        if (!File.Exists(pkgPath)) File.WriteAllBytes(pkgPath, pkg);

        using (var z = new ZipArchive(new MemoryStream(pkg), ZipArchiveMode.Read))
        {
            var me = z.GetEntry("manifest.json") ?? throw new InvalidDataException($"{fileName}: not a .n98 package (no manifest.json)");
            using var doc = JsonDocument.Parse(me.Open());
            var r = doc.RootElement;
            if (r.TryGetProperty("schema", out var sc) && sc.GetInt32() > 1) throw new InvalidDataException($"{fileName}: package schema {sc.GetInt32()} is newer than this app knows (1)");
            s.Title = r.TryGetProperty("title", out var t) ? t.GetString() ?? id : id;
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
                : new[] { Path.GetDirectoryName(baseImg), Environment.GetEnvironmentVariable("BROKENNES_N98_CDS") }.Where(x => !string.IsNullOrEmpty(x))
                    .Select(x => Path.Combine(x!, dName)).FirstOrDefault(File.Exists);
            if (baseD == null || !File.Exists(baseD)) throw new FileNotFoundException($"{fileName} writes drive D: and its D base image ({dName}) was not found; set BROKENNES_N98_BASE_D");
            s.Image2Path = Path.Combine(s.CacheDir, "D.img");
        }
        string cmdPath = Path.Combine(s.CacheDir, "command.txt");
        if (!File.Exists(s.ImagePath) || (s.Image2Path != null && !File.Exists(s.Image2Path)) || File.GetLastWriteTimeUtc(s.ImagePath) < File.GetLastWriteTimeUtc(baseImg))
        {
            string outDir = Path.Combine(s.CacheDir, "apply.tmp");
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
            RunTool("apply", $"\"{pkgPath}\" --base C=\"{baseImg}\"" + (baseD != null ? $" D=\"{baseD}\"" : "") + $" --out \"{outDir}\" --full");
            if (s.Image2Path != null) File.Move(Path.Combine(outDir, "D.img"), s.Image2Path, true);
            if (File.Exists(Path.Combine(outDir, "C.img"))) File.Move(Path.Combine(outDir, "C.img"), s.ImagePath, true);
            else File.Copy(baseImg, s.ImagePath, true);   // a package that changes nothing on C: (a program already on the base)
            Directory.Delete(outDir, true);
            File.Delete(cmdPath);
            foreach (var old in Directory.GetDirectories(s.CacheDir)) Directory.Delete(old, true);   // resume snapshots of the old disk
        }
        if (!File.Exists(cmdPath)) RunTool("cmd", $"\"{pkgPath}\" \"{cmdPath}\" --exit poweroff");
        s.Command = File.ReadAllBytes(cmdPath);
        s.Image = DiskImage.ReadImageFile(s.ImagePath);   // (+ its cached base hash: the resume snapshot's disk diff is against it)
        if (s.Image2Path != null) s.Image2 = DiskImage.ReadImageFile(s.Image2Path);
        return s;
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
        foreach (var c in tried) if (File.Exists(c)) return c;
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
