using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NesEmulator.X86;

/// <summary>
/// nano98 INSTALL FLOW (2026-11-21): the user's own game disc -> a schema-2 .n98 that loads instantly. Shared by the desktop wizard
/// (Windows/DebugWindows/N98InstallWizard.cs), the Workshop CLI (--n98install), the desktop HTTP API (/n98/install/...) and the MCP server, so every
/// step is the same call with the same JSON result whoever drives it.
///   1. source: a disc image (.iso / .cue / .bin) or the host's CD/DVD drive (<see cref="RipDisc"/>: read to a local ISO, never past a read error:
///      a disc whose protection blocks reading is reported, never worked around).
///   2. machine: a nano98 seat preset (win98-p166-nano default) + sound card.
///   3. <see cref="Start"/>: the seat base boots the INSTALL SHELL (C:\N98\N98INST.EXE, tools/nano98/launcher/n98inst.c) with the blank standard D:
///      and the disc in E:, resuming from the base's shared warm-boot state when there is one. <see cref="Run"/> starts the disc's setup (or any
///      program), <see cref="Input"/> types keys / moves the mouse (headless and agent driving; the desktop passes the real keyboard and mouse).
///      The user installs to D:\GAME and clears any Add New Hardware wizard.
///   4. <see cref="PowerOff"/> (Windows shuts down cleanly: its disk caches are written), then <see cref="GamePrograms"/> + <see cref="Capture"/>:
///      the drives as left -> n98.py install -> the package (+ a driver layer for hardware changes).
///   5. the app opens the package (warm state, instant from then on).
/// Settle mode (<see cref="InstallOptions.Settle"/>): no game, the user clears the hardware wizard of a new machine preset once, the C: changes
/// become a system layer (<see cref="CaptureLayer"/>).
/// </summary>
public sealed class N98Install : IDisposable
{
    public sealed class InstallOptions
    {
        /// <summary>The disc image (.iso, .cue, .bin), or null (settle mode / no disc).</summary>
        public string? Disc;
        public string Machine = "win98-p166-nano";
        public string Sound = "SB16_D072";
        /// <summary>Settle mode: no game, only the machine's hardware wizard (the result is a system layer).</summary>
        public bool Settle;
        /// <summary>The game drive's size in MB (2000 = the standard; larger for big games: its own blank drive and warm state).</summary>
        public int DriveMb = N98Seat.StandardDriveMb;
        /// <summary>System layers to run on (ids; base + layers = the variant the install runs on and the package names).</summary>
        public string[] Layers = Array.Empty<string>();
    }

    public PcSession Session { get; }
    public InstallOptions Options { get; }
    public string BasePath { get; }
    public string? Disc { get; private set; }
    public readonly List<string> Discs = new();
    /// <summary>Every status line of the seat (launcher "N98 ..." and install shell "N98 I ..."), in order.</summary>
    public readonly List<string> Log = new();
    public string Phase { get; private set; } = "booting";
    public bool ShellReady { get; private set; }
    public bool PoweredOff => Session.SeatPoweredOff;
    public long Frames { get; private set; }
    private int _seq;
    private readonly long _nonce = (DateTime.UtcNow.Ticks / 10_000 % 1_000_000) * 1000;   // (request numbers differ from any a loaded savestate's shell has seen)
    private readonly Queue<Action<PcMachine>> _input = new();
    private string _workDir;

    public static readonly string[] Note = {
        "Install the game to D:\\GAME (type that folder in the setup's destination box).",
        "If Windows shows Add New Hardware, click Next until it finishes (let it search the disc if it asks); then carry on.",
    };

    private N98Install(PcSession s, InstallOptions o, string basePath, string work)
    {
        Session = s; Options = o; BasePath = basePath; _workDir = work; Disc = o.Disc;
        if (o.Disc != null) Discs.Add(o.Disc);
        s.Machine.StatusLine += OnStatus;
        s.BeforeFrame = Pump;
    }

    /// <summary>The nano98 seat presets (the wizard's machine step).</summary>
    public static IEnumerable<(string key, string label)> Machines() => PcMachinePreset.All.Where(p => p.Key.EndsWith("-nano", StringComparison.Ordinal)).Select(p => (p.Key, p.Label));

    /// <summary>Boots the install shell (step 3). Heavy: reads the base, loads the warm state.</summary>
    public static N98Install Start(InstallOptions o)
    {
        if (!PcMachinePreset.Known(o.Machine) || !o.Machine.EndsWith("-nano", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException($"'{o.Machine}' is not a nano98 seat machine ({string.Join(", ", Machines().Select(m => m.key))})");
        if (o.Disc != null && !File.Exists(o.Disc)) throw new FileNotFoundException("the disc image was not found", o.Disc);
        var seat = N98Seat.ForInstall(o.Machine, o.Sound, o.Disc, o.Layers, o.DriveMb);
        var s = new PcSession(seat.Image, "install.n98", o.Sound, "I386", "VGA", machine: seat.Machine, preparedSeat: seat);
        string work = Path.Combine(seat.CacheDir, "work");
        Directory.CreateDirectory(work);
        return new N98Install(s, o, seat.ImagePath, work);
    }

    private void OnStatus(string line)
    {
        lock (Log) Log.Add(line);
        if (line.StartsWith("N98 I IDLE", StringComparison.Ordinal)) { ShellReady = true; if (Phase is "booting" or "restarting") Phase = "ready"; }
        else if (line.StartsWith("N98 I RUN ", StringComparison.Ordinal)) Phase = "running";
        else if (line.StartsWith("N98 I EXIT", StringComparison.Ordinal) || line.StartsWith("N98 I FAIL", StringComparison.Ordinal)) Phase = "ready";
        else if (line == "N98 I POWEROFF") Phase = "powering off";
        else if (line == "N98 I RESTART" || line == "N98 BOOT") { if (line == "N98 I RESTART") Phase = "restarting"; ShellReady = line == "N98 BOOT" ? ShellReady : false; }
    }

    /// <summary>One frame (the CLI / HTTP / MCP drivers; the desktop runs the session itself and the hook pumps the input).</summary>
    public void RunFrame() { Session.RunFrame(); }

    private void Pump()
    {
        Frames++;
        if (PoweredOff && Phase != "off") Phase = "off";
        Action<PcMachine>? a = null;
        lock (_input) if (_input.Count > 0) a = _input.Dequeue();
        a?.Invoke(Session.Machine);
    }

    /// <summary>Input still queued (frames).</summary>
    public int InputPending { get { lock (_input) return _input.Count; } }

    // ---- requests to the install shell (the command port's iseq / iact lines) ----
    private void Request(string act, string run = "", string args = "", string dir = "")
    {
        if (PoweredOff) throw new InvalidOperationException("the machine is off");
        var t = new StringBuilder("N98CMD1\r\nrun=C:\\N98\\N98INST.EXE\r\nexit=poweroff\r\n");
        t.Append("iseq=").Append(_nonce + ++_seq).Append("\r\n").Append("iact=").Append(act).Append("\r\n");
        if (run.Length > 0) t.Append("irun=").Append(run.Replace('/', '\\')).Append("\r\n");
        if (args.Length > 0) t.Append("iargs=").Append(args).Append("\r\n");
        if (dir.Length > 0) t.Append("idir=").Append(dir.Replace('/', '\\')).Append("\r\n");
        Session.Seat!.Command = Encoding.Latin1.GetBytes(t.ToString());
        Session.Machine.Command = Session.Seat.CommandWith(withFiles: false);
    }

    /// <summary>Starts a program in the guest (the disc's setup: E:\SETUP.EXE). The shell takes it within a quarter second.</summary>
    public void Run(string path, string args = "", string? dir = null)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("no program");
        string d = dir ?? (path.Replace('/', '\\') is var w && w.LastIndexOf('\\') is int k and > 1 ? w[..k] + (k == 2 ? "\\" : "") : "");
        Request("run", path, args, d);
    }
    public void Explorer() => Request("explorer");
    public void Restart() => Request("restart");
    /// <summary>Windows shuts down cleanly (its write cache reaches the drives), then the machine is off: ready for <see cref="Capture"/>.</summary>
    public void PowerOff() => Request("poweroff");

    /// <summary>Puts another disc in (multi-disc games) or takes it out (null): the drive reports the media change, like a real one.</summary>
    public void SwapDisc(string? path)
    {
        if (path != null && !File.Exists(path)) throw new FileNotFoundException("the disc image was not found", path);
        Session.InsertCd(path != null ? CdImage.Open(path) : null);
        Disc = path;
        if (path != null && !Discs.Contains(path)) Discs.Add(path);
    }

    // ---- input: keys and the PS/2 mouse, a step per frame (the same tokens as the Workshop's x86boot --step) ----
    /// <summary>Queues keys / mouse: plain text is typed; {enter} {tab} {esc} {space} {bs} {up} {down} {left} {right} {home} {end} {pgup} {pgdn} {del} {f1}..{f12},
    /// combos {alt+f4} {ctrl+esc} {shift+tab} {win}; {wait:N} frames; {m:dx,dy[,buttons]} mouse move (relative, buttons 1 left 2 right);
    /// {click} / {click:dx,dy} a held left click (10 frames: setup buttons miss shorter ones); {rclick}; {{ = a literal brace.</summary>
    public void Input(string script)
    {
        var steps = new List<Action<PcMachine>>();
        void Nop(int n) { for (int i = 0; i < n; i++) steps.Add(_ => { }); }
        void Key(byte[] mk) { steps.Add(m => m.Kbc.KeyBytes(mk)); var br = (byte[])mk.Clone(); br[^1] |= 0x80; steps.Add(m => m.Kbc.KeyBytes(br)); }
        for (int i = 0; i < script.Length; i++)
        {
            char ch = script[i];
            if (ch == '{' && i + 1 < script.Length && script[i + 1] == '{') { i++; }
            else if (ch == '{' && script.IndexOf('}', i) is int e and > 0)
            {
                string tok = script[(i + 1)..e].ToLowerInvariant(); i = e;
                if (tok.StartsWith("wait:")) { Nop(int.Parse(tok[5..])); continue; }
                if (tok.StartsWith("at:"))   // absolute: to the top-left corner, then small steps (below the acceleration threshold: 1 count = 2 pixels on the seat, measured)
                {
                    var v = tok[3..].Split(','); int x = int.Parse(v[0]), y = int.Parse(v[1]);
                    for (int q = 0; q < 12; q++) { steps.Add(m => m.Kbc.MouseInput(-120, -120, 0)); Nop(1); }
                    for (int left = x / 2; left > 0; left -= 3) { int dx = Math.Min(3, left); steps.Add(m => m.Kbc.MouseInput(dx, 0, 0)); Nop(1); }
                    for (int left = y / 2; left > 0; left -= 3) { int dy = Math.Min(3, left); steps.Add(m => m.Kbc.MouseInput(0, dy, 0)); Nop(1); }
                    Nop(2);
                    continue;
                }
                if (tok.StartsWith("m:")) { var v = tok[2..].Split(','); int dx = int.Parse(v[0]), dy = int.Parse(v[1]), b = v.Length > 2 ? int.Parse(v[2]) : 0; steps.Add(m => m.Kbc.MouseInput(dx, dy, b)); Nop(1); continue; }
                if (tok == "click" || tok == "rclick" || tok.StartsWith("click:"))
                {
                    int b = tok == "rclick" ? 2 : 1;
                    if (tok.StartsWith("click:")) { var v = tok[6..].Split(','); int dx = int.Parse(v[0]), dy = int.Parse(v[1]); steps.Add(m => m.Kbc.MouseInput(dx, dy, 0)); Nop(1); }
                    steps.Add(m => m.Kbc.MouseInput(0, 0, b)); Nop(10); steps.Add(m => m.Kbc.MouseInput(0, 0, 0)); Nop(2); continue;
                }
                var parts = tok.Split('+'); var mods = parts[..^1].Select(Mod).ToList();
                foreach (var md in mods) steps.Add(m => m.Kbc.KeyBytes(md));
                Key(TokenKey(parts[^1]));
                for (int q = mods.Count - 1; q >= 0; q--) { var mb = (byte[])mods[q].Clone(); mb[^1] |= 0x80; steps.Add(m => m.Kbc.KeyBytes(mb)); }
                Nop(1);
                continue;
            }
            if (ch == '\n') { Key(new byte[] { 0x1c }); continue; }
            if (!Keys.TryGetValue(ch, out var k)) continue;
            if (k.shift) steps.Add(m => m.Kbc.KeyBytes(0x2a));
            Key(new[] { k.code });
            if (k.shift) steps.Add(m => m.Kbc.KeyBytes(0xaa));
        }
        lock (_input) foreach (var s in steps) _input.Enqueue(s);
    }

    private static byte[] Mod(string p) => p switch { "ctrl" => new byte[] { 0x1d }, "alt" => new byte[] { 0x38 }, "shift" => new byte[] { 0x2a }, "win" => new byte[] { 0xe0, 0x5b }, _ => throw new ArgumentException("key modifier " + p) };
    private static readonly Dictionary<char, (byte code, bool shift)> Keys = BuildKeys();
    private static Dictionary<char, (byte, bool)> BuildKeys()
    {
        var d = new Dictionary<char, (byte, bool)>();
        const string normal = "\0\x1b" + "1234567890-=\b\t" + "qwertyuiop[]\n\0" + "asdfghjkl;'`\0\\" + "zxcvbnm,./";
        const string shifted = "\0\x1b" + "!@#$%^&*()_+\b\0" + "QWERTYUIOP{}\n\0" + "ASDFGHJKL:\"~\0|" + "ZXCVBNM<>?";
        for (int i = 1; i < normal.Length; i++) { if (normal[i] != '\0') d.TryAdd(normal[i], ((byte)i, false)); if (shifted[i] != '\0') d.TryAdd(shifted[i], ((byte)i, true)); }
        d[' '] = (0x39, false);
        return d;
    }
    private static byte[] TokenKey(string k)
    {
        if (k.Length > 1 && k[0] == 'f' && int.TryParse(k[1..], out int fn)) return new[] { (byte)(fn <= 10 ? 0x3a + fn : 0x57 + fn - 11) };
        return k switch
        {
            "win" => new byte[] { 0xe0, 0x5b }, "menu" => new byte[] { 0xe0, 0x5d }, "esc" => new byte[] { 0x01 }, "tab" => new byte[] { 0x0f },
            "enter" => new byte[] { 0x1c }, "space" => new byte[] { 0x39 }, "bs" => new byte[] { 0x0e }, "alt" => new byte[] { 0x38 },
            "up" => new byte[] { 0xe0, 0x48 }, "down" => new byte[] { 0xe0, 0x50 }, "left" => new byte[] { 0xe0, 0x4b }, "right" => new byte[] { 0xe0, 0x4d },
            "home" => new byte[] { 0xe0, 0x47 }, "end" => new byte[] { 0xe0, 0x4f }, "pgup" => new byte[] { 0xe0, 0x49 }, "pgdn" => new byte[] { 0xe0, 0x51 },
            "del" => new byte[] { 0xe0, 0x53 }, "ins" => new byte[] { 0xe0, 0x52 },
            _ when k.Length == 1 && Keys.TryGetValue(k[0], out var kk) => new[] { kk.code },
            _ => throw new ArgumentException("key token " + k),
        };
    }

    // ---- the disc: what setup programs it offers ----
    /// <summary>The setup candidates on a disc: AUTORUN.INF's open= first, then SETUP/INSTALL programs in the root and one level down, then the other root programs.</summary>
    public static List<string> SetupCandidates(string disc)
    {
        var cd = CdImage.Open(disc);
        var files = Iso9660.List(cd, 2);
        var outp = new List<string>();
        var inf = files.FirstOrDefault(f => f.path.Equals("AUTORUN.INF", StringComparison.OrdinalIgnoreCase));
        if (inf.path != null)
        {
            string text = Encoding.Latin1.GetString(Iso9660.Read(cd, inf.lba, inf.size));
            foreach (var l in text.Split('\n'))
                if (l.Trim().StartsWith("open=", StringComparison.OrdinalIgnoreCase))
                {
                    string o = l.Trim()[5..].Trim().Split(' ')[0].TrimStart('\\');
                    if (o.Length > 0) outp.Add("E:\\" + o.ToUpperInvariant());
                }
        }
        foreach (var f in files.Where(f => f.path.EndsWith(".EXE", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.path.Count(c => c == '\\')))
        {
            string n = Path.GetFileName(f.path).ToUpperInvariant();
            if (n.Contains("SETUP") || n.Contains("INSTAL")) outp.Add("E:\\" + f.path.ToUpperInvariant());
        }
        foreach (var f in files.Where(f => f.path.EndsWith(".EXE", StringComparison.OrdinalIgnoreCase) && !f.path.Contains('\\'))) outp.Add("E:\\" + f.path.ToUpperInvariant());
        return outp.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Reads a disc in the host's CD/DVD drive (a drive letter, "E:") to an ISO: the data track's sectors, as the drive gives them. A read error stops
    /// it (a scratched disc, or a copy protection that blocks reading: reported, never worked around). progress: (sectors done, total).</summary>
    public static string RipDisc(string drive, string outIso, Action<long, long>? progress = null, System.Threading.CancellationToken ct = default)
    {
        string letter = drive.TrimEnd('\\', ':');
        if (letter.Length != 1 || !char.IsLetter(letter[0])) throw new ArgumentException("a drive letter (E:)");
        var di = new DriveInfo(letter + ":");
        if (di.DriveType != DriveType.CDRom) throw new ArgumentException($"{letter}: is not a CD/DVD drive");
        if (!di.IsReady) throw new IOException($"there is no disc in {letter}:");
        using var h = File.OpenHandle(@"\\.\" + letter + ":", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var pvd = new byte[2048];
        if (RandomAccess.Read(h, pvd, 16 * 2048L) != 2048 || pvd[0] != 1 || Encoding.ASCII.GetString(pvd, 1, 5) != "CD001") throw new InvalidDataException($"the disc in {letter}: is not an ISO 9660 data disc");
        long total = BitConverter.ToUInt32(pvd, 80);
        string tmp = outIso + ".part";
        using (var o = File.Create(tmp))
        {
            var buf = new byte[2048 * 32];
            for (long at = 0; at < total;)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(32, total - at);
                int got;
                try { got = RandomAccess.Read(h, buf.AsSpan(0, n * 2048), at * 2048); }
                catch (IOException e) { throw new IOException($"the disc could not be read at sector {at} of {total} ({e.Message}). It may be scratched or dirty; if it is copy protected, BrokenNes does not work around that.", e); }
                if (got <= 0) throw new IOException($"the disc ended at sector {at} of {total}");
                o.Write(buf, 0, got); at += got / 2048;
                progress?.Invoke(at, total);
            }
        }
        File.Move(tmp, outIso, true);
        return outIso;
    }

    /// <summary>The host's CD/DVD drives that hold a disc (letter, volume label).</summary>
    public static IEnumerable<(string drive, string label)> HostDiscDrives() => DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.CDRom && d.IsReady).Select(d => (d.Name.TrimEnd('\\'), SafeLabel(d)));
    private static string SafeLabel(DriveInfo d) { try { return d.VolumeLabel; } catch (IOException) { return ""; } }

    // ---- the Windows 98 SE files disc: Windows asks for its own CD when a setup installs a Windows component (Age of Empires: mciavi.drv),
    // and the seat base has no setup CABs (slim.py). Made once from the user's own Windows 98 SE disc; the seat's setup source is E:\WIN98. ----
    /// <summary>%LOCALAPPDATA%\BrokenNes\nano98\win98se_files.iso (or BROKENNES_N98_WINFILES).</summary>
    public static string WindowsFilesDiscPath => Environment.GetEnvironmentVariable("BROKENNES_N98_WINFILES") is { Length: > 0 } p ? p : Path.Combine(Path.GetDirectoryName(N98Seat.DiscFolder)!, "win98se_files.iso");
    public static bool HasWindowsFilesDisc => File.Exists(WindowsFilesDiscPath);
    /// <summary>Makes the Windows 98 SE files disc from the user's Windows 98 SE ISO (a real SE disc, or a compilation holding WIN98SE.ZIP); any other disc is refused.</summary>
    public static JsonObject MakeWindowsFilesDisc(string win98Iso, string? outIso = null)
    {
        outIso ??= WindowsFilesDiscPath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outIso))!);
        string o;
        try { o = N98Seat.RunToolOut("..winfiles", $"\"{win98Iso}\" \"{outIso}\""); }
        catch (InvalidOperationException e) when (e.Message.Contains("WINFILES ")) { return (JsonObject)JsonNode.Parse(e.Message[(e.Message.IndexOf("WINFILES ") + 9)..])!; }
        string line = o.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("WINFILES ", StringComparison.Ordinal)) ?? throw new InvalidOperationException("winfiles.py gave no result");
        return (JsonObject)JsonNode.Parse(line[9..])!;
    }
    /// <summary>Windows asked for "the Windows 98 Second Edition CD-ROM": put the files disc in, then the user clicks OK (put the game disc back after).</summary>
    public void InsertWindowsFilesDisc()
    {
        if (!HasWindowsFilesDisc) throw new FileNotFoundException("the Windows 98 SE files disc is not made yet: give your Windows 98 SE disc image once", WindowsFilesDiscPath);
        SwapDisc(Path.GetFullPath(WindowsFilesDiscPath));
    }
    public static bool IsWindowsFilesDisc(string path)
    {
        try
        {
            if (!path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return false;
            using var f = File.OpenRead(path); var p = new byte[2048]; f.Seek(16 * 2048, SeekOrigin.Begin); f.ReadExactly(p);
            return Encoding.ASCII.GetString(p, 8, 9) == "BROKENNES" && Encoding.ASCII.GetString(p, 40, 7) == "WIN98SE";
        }
        catch (IOException) { return false; }
    }

    // ---- capture ----
    private (string c, string d) WriteDrives()
    {
        if (!PoweredOff) throw new InvalidOperationException("power the machine off first (Windows writes its caches to the drives as it shuts down)");
        string c = Path.Combine(_workDir, "C.img"), d = Path.Combine(_workDir, "D.img");
        var m = Session.Machine;
        m.Hdd[0]!.WriteFile(c);
        m.Hdd[1]!.WriteFile(d);   // (a large game drive is written by chunks)
        return (c, d);
    }

    /// <summary>The programs on the game drive (step 4's pick), likely game programs first: [{path, size, likely}].</summary>
    public JsonArray GamePrograms()
    {
        var (_, d) = WriteDrives();
        string o = N98Seat.RunToolOut("exes", $"\"{d}\"");
        string line = o.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("N98EXES ", StringComparison.Ordinal)) ?? throw new InvalidOperationException("n98.py exes gave no list");
        return (JsonArray)JsonNode.Parse(line[8..])!;
    }

    /// <summary>Step 4: the drives -> OUT.n98 (schema 2). layerId: promote hardware changes (drivers, Enum/Config) to a system layer of that id beside the
    /// package (also copied to the nano98 layers folder so the app finds it). Returns n98.py's JSON result.</summary>
    public JsonObject Capture(string title, string exe, string outPkg, string args = "", string? layerId = null, string[]? saves = null, double p2cpi = 0, string[]? drop = null)
    {
        var (c, d) = WriteDrives();
        var sb = new StringBuilder($"\"{outPkg}\" --title \"{title}\" --base \"{BasePath}\" --c \"{c}\" --d \"{d}\" --exe \"{exe}\" --machine {Options.Machine.ToUpperInvariant()} --sound {Options.Sound} --work \"{_workDir}\"");
        if (args.Length > 0) sb.Append($" --args=\"{args}\"");
        if (Discs.Any(x => !IsWindowsFilesDisc(x))) sb.Append(" --cd ").Append(string.Join(" ", Discs.Where(x => !IsWindowsFilesDisc(x)).Select(x => $"\"{Path.GetFileName(x)}\"")));
        if (Options.Layers.Length > 0) sb.Append(" --layers ").Append(string.Join(" ", Options.Layers));
        if (layerId != null) sb.Append(" --layer-id ").Append(layerId);
        if (drop is { Length: > 0 }) sb.Append(" --drop ").Append(string.Join(" ", drop.Select(x => $"\"{x}\"")));
        if (Options.DriveMb != N98Seat.StandardDriveMb) sb.Append(" --d-size-mb ").Append(Options.DriveMb);
        if (p2cpi >= 1.0) sb.Append(" --p2cpi ").Append(p2cpi.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (saves is { Length: > 0 }) sb.Append(" --saves ").Append(string.Join(" ", saves.Select(x => $"\"{x}\"")));
        string o = N98Seat.RunToolOut("install", sb.ToString());
        string line = o.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("N98INSTALL ", StringComparison.Ordinal)) ?? throw new InvalidOperationException("n98.py install gave no result");
        var res = (JsonObject)JsonNode.Parse(line[11..])!;
        // the discs the package names: kept where the app looks for them (the user's own copies, local only)
        foreach (var disc in Discs.Where(x => !IsWindowsFilesDisc(x))) N98Seat.RememberDisc(disc);
        if (res["layer"]?.GetValue<string>() is { Length: > 0 } lp) N98Seat.RememberLayer(lp);
        if (Environment.GetEnvironmentVariable("BROKENNES_N98_KEEPDRIVES") != "1") try { File.Delete(c); File.Delete(d); } catch (IOException) { }   // (KEEPDRIVES: keep them to re-run n98.py install by hand)
        return res;
    }

    /// <summary>Settle mode's capture: the C: changes (drivers the hardware wizard installed) -> a system layer OUT (schema 1, kind layer).</summary>
    public JsonObject CaptureLayer(string id, string outLayer)
    {
        var (c, _) = WriteDrives();
        string s1 = Path.Combine(_workDir, id + ".capture.n98");
        N98Seat.RunToolOut("capture", $"\"{s1}\" --title \"{id}\" --id {id} --before C=\"{BasePath}\" --after C=\"{c}\" --keep-reg-noise --exclude \"N98/*\"");
        N98Seat.RunToolOut("layer", $"\"{s1}\" \"{outLayer}\" --id {id}");
        File.Delete(s1); try { File.Delete(c); } catch (IOException) { }
        N98Seat.RememberLayer(outLayer);
        return new JsonObject { ["ok"] = true, ["layer"] = Path.GetFullPath(outLayer), ["id"] = id };
    }

    /// <summary>The state as JSON (every driver shows the same thing).</summary>
    public JsonObject Status()
    {
        string[] tail; lock (Log) tail = Log.Skip(Math.Max(0, Log.Count - 12)).ToArray();
        return new JsonObject
        {
            ["phase"] = Phase, ["shell_ready"] = ShellReady, ["powered_off"] = PoweredOff, ["frames"] = Frames, ["input_pending"] = InputPending,
            ["machine"] = Options.Machine, ["sound"] = Options.Sound, ["disc"] = Disc, ["settle"] = Options.Settle,
            ["screen"] = $"{Session.FrameWidth}x{Session.FrameHeight}", ["status"] = new JsonArray(tail.Select(t => (JsonNode)t).ToArray()),
            ["note"] = new JsonArray(Note.Select(t => (JsonNode)t).ToArray()),
        };
    }

    public void Dispose() { Session.BeforeFrame = null; Session.Dispose(); }

    // ---- one JSON command surface for every driver (desktop HTTP API /api/n98/install/ACTION, MCP n98_install, scripts) ----
    /// <summary>The actions that need no install session: machines, drives (the host's CD/DVD drives with a disc), setups {disc}, rip {drive, out},
    /// make-winfiles {iso}, winfiles-status. Null when the action needs a session.</summary>
    public static JsonObject? DoStatic(string action, JsonObject a)
    {
        string? S(string k) => a[k]?.GetValue<string>();
        switch (action)
        {
            case "machines": return new JsonObject { ["ok"] = true, ["machines"] = new JsonArray(Machines().Select(m => (JsonNode)new JsonObject { ["key"] = m.key, ["label"] = m.label }).ToArray()),
                                                     ["sounds"] = new JsonArray(PcSound.CardOptions().Select(c => (JsonNode)c.id).ToArray()), ["default"] = "win98-p166-nano" };
            case "drives": return new JsonObject { ["ok"] = true, ["drives"] = new JsonArray(HostDiscDrives().Select(d => (JsonNode)new JsonObject { ["drive"] = d.drive, ["label"] = d.label }).ToArray()) };
            case "setups": return new JsonObject { ["ok"] = true, ["setups"] = new JsonArray(SetupCandidates(S("disc") ?? throw new ArgumentException("disc")).Select(x => (JsonNode)x).ToArray()) };
            case "rip":
                {
                    string drive = S("drive") ?? throw new ArgumentException("drive");
                    string outIso = S("out") ?? Path.Combine(N98Seat.DiscFolder, $"disc_{drive.TrimEnd(':')}_{DateTime.Now:yyyyMMdd_HHmmss}.iso");
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outIso))!);
                    return new JsonObject { ["ok"] = true, ["iso"] = RipDisc(drive, outIso) };
                }
            case "make-winfiles": return MakeWindowsFilesDisc(S("iso") ?? throw new ArgumentException("iso"));
            case "base-status": return new JsonObject { ["ok"] = true, ["ready"] = N98Seat.HasBase() };
            case "build-base": return N98BaseBuild.Start(S("iso") ?? throw new ArgumentException("iso"), S("key") ?? throw new ArgumentException("key"), S("out"));   // returns at once
            case "base-build-status": return N98BaseBuild.JobStatus();
            case "winfiles-status": return new JsonObject { ["ok"] = true, ["ready"] = HasWindowsFilesDisc, ["path"] = Path.GetFullPath(WindowsFilesDiscPath) };
            default: return null;
        }
    }

    /// <summary>The session actions: status, setups, run {path, args, dir}, runsetup, keys {text}, disc {path|null}, winfiles, explorer, restart, poweroff,
    /// programs, capture {title, exe, out, args, layer_id, saves[]}, capture-layer {id, out}. Every result is a JSON object with "ok".</summary>
    public JsonObject Do(string action, JsonObject a)
    {
        string? S(string k) => a[k]?.GetValue<string>();
        JsonObject Ok() { var st = Status(); st["ok"] = true; return st; }
        switch (action)
        {
            case "status": return Ok();
            case "setups": return new JsonObject { ["ok"] = true, ["setups"] = Disc == null ? new JsonArray() : new JsonArray(SetupCandidates(Disc).Select(x => (JsonNode)x).ToArray()) };
            case "run": Run(S("path") ?? throw new ArgumentException("path"), S("args") ?? "", S("dir")); return Ok();
            case "runsetup": { var c = Disc == null ? new List<string>() : SetupCandidates(Disc); if (c.Count == 0) throw new InvalidOperationException("no setup program on the disc"); Run(c[0]); var r = Ok(); r["started"] = c[0]; return r; }
            case "keys": Input(S("text") ?? ""); return Ok();
            case "disc": SwapDisc(S("path")); return Ok();
            case "winfiles": InsertWindowsFilesDisc(); return Ok();
            case "explorer": Explorer(); return Ok();
            case "restart": Restart(); return Ok();
            case "poweroff": PowerOff(); return Ok();
            case "programs": return new JsonObject { ["ok"] = true, ["programs"] = GamePrograms() };
            case "capture":
                {
                    string title = S("title") ?? throw new ArgumentException("title");
                    string exe = S("exe") ?? "auto";
                    if (exe == "auto") exe = GamePrograms().FirstOrDefault()?["path"]?.GetValue<string>() ?? throw new InvalidOperationException("no program on D:\\GAME");
                    string outPkg = S("out") ?? Path.Combine(DefaultPackageFolder, SafeName(title) + ".n98");
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPkg))!);
                    var saves = a["saves"] is JsonArray sa ? sa.Select(x => x!.GetValue<string>()).ToArray() : null;
                    return Capture(title, exe, outPkg, S("args") ?? "", S("layer_id"), saves, a["p2cpi"]?.GetValue<double>() ?? 0, a["drop"] is JsonArray dr ? dr.Select(x => x!.GetValue<string>()).ToArray() : null);   // drop: game-drive globs left out (unused HQ assets)   // p2cpi: the game's Pentium II cost
                }
            case "capture-layer":
                {
                    string id = S("id") ?? throw new ArgumentException("id");
                    return CaptureLayer(id, S("out") ?? Path.Combine(N98Seat.LayerFolder, id + ".n98"));
                }
            default: return DoStatic(action, a) ?? throw new ArgumentException($"unknown action '{action}'");
        }
    }

    /// <summary>Where new packages go by default: Documents\BrokenNes\Windows games.</summary>
    public static string DefaultPackageFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BrokenNes", "Windows games");
    public static string SafeName(string t) { var s = new string(t.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch).ToArray()).Trim(); return s.Length > 0 ? s : "game"; }
}

/// <summary>A minimal ISO 9660 directory reader (the install wizard's setup picker): primary volume descriptor, then the directory tree.</summary>
public static class Iso9660
{
    public static List<(string path, long lba, long size)> List(CdImage cd, int depth)
    {
        var pvd = cd.ReadData(16, 1);
        if (pvd[0] != 1 || Encoding.ASCII.GetString(pvd, 1, 5) != "CD001") throw new InvalidDataException("not an ISO 9660 data disc");
        var outp = new List<(string, long, long)>();
        void Dir(long lba, long size, string prefix, int left)
        {
            var b = Read(cd, lba, size);
            for (int at = 0; at < b.Length;)
            {
                int len = b[at];
                if (len == 0) { at = (at / 2048 + 1) * 2048; continue; }
                long el = BitConverter.ToUInt32(b, at + 2), es = BitConverter.ToUInt32(b, at + 10); byte flags = b[at + 25]; int nl = b[at + 32];
                string name = Encoding.ASCII.GetString(b, at + 33, nl);
                at += len;
                if (nl == 1 && (name[0] == '\0' || name[0] == '\x01')) continue;
                int semi = name.IndexOf(';'); if (semi >= 0) name = name[..semi];
                name = name.TrimEnd('.');
                if ((flags & 2) != 0) { if (left > 0) Dir(el, es, prefix + name + "\\", left - 1); }
                else outp.Add((prefix + name, el, es));
            }
        }
        Dir(BitConverter.ToUInt32(pvd, 156 + 2), BitConverter.ToUInt32(pvd, 156 + 10), "", depth - 1);
        return outp;
    }

    public static byte[] Read(CdImage cd, long lba, long size)
    {
        int n = (int)((size + 2047) / 2048);
        var b = cd.ReadData(lba, Math.Max(1, n));
        return size < b.Length ? b[..(int)size] : b;
    }
}
