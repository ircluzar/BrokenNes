using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace NesEmulator.X86;

// nano98 install flow (N98Install.cs): the seat made for an install session, and the tool / folder helpers it shares with packages.
public sealed partial class N98Seat
{
    /// <summary>The install shell's seat: the seat base (or base + layers), the blank standard D: (the game is installed onto it; the warm-boot
    /// swap puts a copy-on-write drive over the same blank bytes), the disc, and the launcher command that starts C:\N98\N98INST.EXE.</summary>
    public static N98Seat ForInstall(string machine, string sound, string? disc, string[] layers)
    {
        string cache = Environment.GetEnvironmentVariable("BROKENNES_N98_CACHE") is { Length: > 0 } c ? c : LocalRoot;
        var s = new N98Seat { Id = "install", Title = "Install a Windows game", V2 = true, Machine = PcMachinePreset.Find(machine).Key, Sound = sound, CacheDir = Path.Combine(cache, "install") };
        Directory.CreateDirectory(s.CacheDir);
        string baseImg = FindBase();
        if (layers.Length > 0) baseImg = Variant(baseImg, layers, "install", cache);
        s.ImagePath = baseImg;
        Report("reading Windows 98", 0);
        s.Image = DiskImage.ReadImageFile(baseImg);
        s.WarmDir = WarmDirFor(baseImg, s.Image, cache, s.Machine, sound, s.EffectiveP2Cpi);
        s.BlankDrive = MakeBlankDrive(StandardDriveMb);
        s.GameDrive = s.BlankDrive;   // (DiskImage never writes its base: the install's writes land in the game drive's overlay)
        string bh = Path.Combine(s.WarmDir, "blank.p256");
        if (File.Exists(bh) && File.ReadAllText(bh).Trim() is { } bt && bt.StartsWith("p256:", StringComparison.Ordinal)) DiskImage.SeedHash(s.BlankDrive, bt);
        s.Image2 = s.BlankDrive;
        s.CdPath = disc;
        s.Command = Encoding.Latin1.GetBytes("N98CMD1\r\nrun=C:\\N98\\N98INST.EXE\r\nexit=poweroff\r\n");
        s.SystemFiles.Add((@"C:\N98\N98INST.EXE", File.ReadAllBytes(InstallShell())));
        // "N98R": Windows' setup files source = E:\WIN98 (the Windows 98 SE files disc, N98Install.WindowsFilesDisc, put in when Windows asks for its CD)
        var ops = new MemoryStream(); var w = new BinaryWriter(ops);
        void S(string x) { var b = Encoding.Latin1.GetBytes(x); w.Write((uint)b.Length); w.Write(b); }
        w.Write(0x5238394Eu);
        foreach (var name in new[] { "SourcePath", "ServicePackSourcePath" })
        {
            w.Write((byte)2); w.Write((byte)1); S(@"Software\Microsoft\Windows\CurrentVersion\Setup"); S(name); w.Write(1u);
            var data = Encoding.Latin1.GetBytes(@"E:\WIN98\" + "\0"); w.Write((uint)data.Length); w.Write(data);
        }
        w.Write((byte)0); w.Flush();
        s.SystemOps = ops.ToArray();
        s.SavesDir = Path.Combine(s.CacheDir, "nosaves");
        return s;
    }

    /// <summary>The nano98 tools folder (tools, nano98).</summary>
    public static string ToolsDir() => Tools();
    /// <summary>The Python 3 command (exe, arguments before the script).</summary>
    public static (string exe, string pre) PythonCmd() => Python();
    /// <summary>Is there a seat base (the Windows 98 every game runs on)? The install wizard offers to build it from the user's own disc when not.</summary>
    public static bool HasBase() { try { FindBase(); return true; } catch (FileNotFoundException) { return false; } }

    /// <summary>N98INST.EXE: the app's x86\nano98 folder, else the tools' launcher folder.</summary>
    private static string InstallShell()
    {
        string a = Path.Combine(AppContext.BaseDirectory, "x86", "nano98", "N98INST.EXE");
        if (File.Exists(a)) return a;
        string t = Path.Combine(Tools(), "launcher", "N98INST.EXE");
        if (File.Exists(t)) return t;
        throw new FileNotFoundException("the nano98 install shell (N98INST.EXE) was not found in the app's x86\\nano98 folder or tools\\nano98\\launcher");
    }

    /// <summary>Where the app keeps the user's disc images (the packages name them by file name): %LOCALAPPDATA%\BrokenNes\nano98\cds.</summary>
    public static string DiscFolder => Path.Combine(LocalRoot, "cds");
    /// <summary>Where the app keeps system layers: %LOCALAPPDATA%\BrokenNes\nano98\layers.</summary>
    public static string LayerFolder => Path.Combine(LocalRoot, "layers");

    /// <summary>A disc a package names: found by the app from then on (a copy in the discs folder unless it is already in a disc folder).</summary>
    public static void RememberDisc(string path)
    {
        string name = Path.GetFileName(path);
        if (CdDirs("").Any(d => d.Length > 0 && File.Exists(Path.Combine(d, name)) && Path.GetFullPath(Path.Combine(d, name)).Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))) return;
        Directory.CreateDirectory(DiscFolder);
        string to = Path.Combine(DiscFolder, name);
        if (!File.Exists(to) || new FileInfo(to).Length != new FileInfo(path).Length) File.Copy(path, to, true);
        if (path.EndsWith(".cue", StringComparison.OrdinalIgnoreCase))   // (its .bin files beside it)
            foreach (var l in File.ReadAllLines(path))
                if (l.TrimStart().StartsWith("FILE", StringComparison.OrdinalIgnoreCase) && l.Split('"') is { Length: >= 3 } q && File.Exists(Path.Combine(Path.GetDirectoryName(path)!, q[1])))
                    File.Copy(Path.Combine(Path.GetDirectoryName(path)!, q[1]), Path.Combine(DiscFolder, q[1]), true);
    }

    public static void RememberLayer(string path)
    {
        Directory.CreateDirectory(LayerFolder);
        string to = Path.Combine(LayerFolder, Path.GetFileName(path));
        if (!Path.GetFullPath(to).Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) File.Copy(path, to, true);
        N98WarmPrebuild.Kick();   // packages that name this layer get their new variant's warm state in the background
    }

    /// <summary>n98.py VERB ARGS, its stdout (throws with stderr on failure).</summary>
    public static string RunToolOut(string verb, string args)
    {
        var (py, pre) = Python();
        bool own = verb.StartsWith("..", StringComparison.Ordinal);   // "..NAME": pkg\NAME.py itself (winfiles.py)
        string script = Path.Combine(Tools(), "pkg", own ? verb[2..] + ".py" : "n98.py");
        var psi = new ProcessStartInfo(py, $"{pre}-I \"{script}\" {(own ? "" : verb)} {args}")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        using var pr = Process.Start(psi) ?? throw new InvalidOperationException("Python 3 was not found (set BROKENNES_PYTHON)");
        var err = pr.StandardError.ReadToEndAsync(); string o = pr.StandardOutput.ReadToEnd(); pr.WaitForExit();
        if (pr.ExitCode != 0)
        {
            string tail = o.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("N98INSTALL ", StringComparison.Ordinal)) ?? "";
            throw new InvalidOperationException($"n98.py {verb} failed ({pr.ExitCode}): {(tail.Length > 0 ? tail : err.Result.Trim())}");
        }
        return o;
    }
}
