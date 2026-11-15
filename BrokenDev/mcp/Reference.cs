using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using NesEmulator.Systems;

namespace BrokenNes.Mcp;

/// <summary>
/// The live reference behind play_step (Great Compatibility Catalog P0, scope item 5): a reference emulator opened on the same ROM and driven in
/// lock step with a headless session. Mesen 2 (NES, SNES, Game Boy / Color, Master System, Game Gear) through its test runner and a Lua script
/// that talks to this server over TCP (BrokenDev\tools\refstep\mesen_refstep.lua); BizHawk (EmuHawk, Genplus-gx: the Genesis) through its socket
/// server and BrokenDev\tools\refstep\bizhawk_refstep.lua. Both are BrokenDev's installed copies (BROKENDEV_HOME\emulators), each run from a
/// private home in this session's folder: the install is never written.
/// The reference is parked between steps (inside a Lua callback, or between frameadvance calls), so it never runs ahead of BrokenNes.
/// Frames: both sides count frames completed since power-on; the reference is asked to reach (BrokenNes frame + offset). The offset is set at
/// open (offset) or found by align (a boot with no input, the pictures compared frame by frame).
/// </summary>
internal sealed class RefEmu : IDisposable
{
    public string Emulator { get; }
    public string Console { get; }
    public long Frame { get; private set; }
    public long Offset { get; set; }
    public string Hello { get; private set; } = "";
    public string Home { get; }
    private readonly Process proc;
    private readonly TcpListener listener;
    private TcpClient? client;
    private NetworkStream? net;
    private readonly bool framed;          // BizHawk's socket framing ("<length> <text>"); Mesen: one line each way
    private readonly string shotPath;
    private readonly byte[] rbuf = new byte[65536];
    private readonly MemoryStream pending = new();

    public static string DefaultEmulator(string consoleKey) => consoleKey == "md" ? "bizhawk" : "mesen";

    private RefEmu(string emulator, string console, string home, Process p, TcpListener l, bool framed, string shot)
    {
        Emulator = emulator; Console = console; Home = home; proc = p; listener = l; this.framed = framed; shotPath = shot;
    }

    private static string EmuRoot => Path.Combine(BrokenDevBridge.Home, "emulators");
    private static string RefTools => Path.Combine(BrokenDevBridge.Repo, "BrokenDev", "tools", "refstep");

    /// <summary>Opens the reference on <paramref name="rom"/>: starts it, waits for its script to connect (its hello line).</summary>
    public static RefEmu Open(string emulator, string consoleKey, string rom, string sessionFolder, int timeoutMs = 90_000)
    {
        emulator = emulator.ToLowerInvariant();
        if (emulator is "" or "auto") emulator = DefaultEmulator(consoleKey);
        if (consoleKey == "wms") throw new ToolError("WideMess has no reference emulator (it is BrokenNes's own console)");
        if (consoleKey == "x86") throw new ToolError("the x86 PC has no live reference");
        if (emulator == "mesen" && consoleKey == "md") throw new ToolError("Mesen 2.1.1 does not run the Genesis: emulator bizhawk (Genplus-gx)");
        if (emulator is not ("mesen" or "bizhawk")) throw new ToolError($"reference emulator '{emulator}': mesen or bizhawk");
        string home = Path.Combine(sessionFolder, "reference-" + emulator);
        Directory.CreateDirectory(home);
        string shot = Path.Combine(home, emulator == "mesen" ? "shot.bin" : "shot.png");
        try { File.Delete(shot); } catch { }
        // a ROM BizHawk could not tell the console of (.bin) goes in as a copy with the console's own extension
        string romArg = rom;
        if (emulator == "bizhawk")
        {
            string want = consoleKey switch { "md" => ".md", "sms" => ".sms", "gg" => ".gg", "gb" => ".gb", "gbc" => ".gbc", "nes" => ".nes", "snes" => ".sfc", _ => Path.GetExtension(rom) };
            if (!Path.GetExtension(rom).Equals(want, StringComparison.OrdinalIgnoreCase) && !rom.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                romArg = Path.Combine(home, "rom" + want);
                File.Copy(rom, romArg, true);
            }
        }
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        var psi = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = home };
        psi.Environment["BNREF_PORT"] = port.ToString(CultureInfo.InvariantCulture);
        psi.Environment["BNREF_SHOT"] = shot;
        psi.Environment["BNREF_CONSOLE"] = consoleKey;
        if (emulator == "mesen")
        {
            string inst = Path.Combine(EmuRoot, "mesen-2.1.1", "Mesen.exe");
            if (!File.Exists(inst)) throw new ToolError($"Mesen is not installed at {inst} (brokendev.ps1 emulators install)");
            string exe = Path.Combine(home, "Mesen.exe");
            if (!File.Exists(exe) || new FileInfo(exe).Length != new FileInfo(inst).Length) File.Copy(inst, exe, true);
            File.WriteAllText(Path.Combine(home, "settings.json"), MesenSettings(consoleKey));
            psi.FileName = exe;
            foreach (var a in new[] { romArg, "--luaScriptsToLoad", Path.Combine(RefTools, "mesen_refstep.lua"), "--testRunner", "--timeout=86400" }) psi.ArgumentList.Add(a);
            // Mesen's test runner never starts its script with standard output on a pipe (BrokenDev's adapter measured it): no redirect
        }
        else
        {
            string dir = Path.Combine(EmuRoot, "bizhawk-2.11.1");
            string exe = Path.Combine(dir, "EmuHawk.exe");
            if (!File.Exists(exe)) throw new ToolError($"BizHawk is not installed at {exe} (brokendev.ps1 emulators install)");
            var (sys, core) = consoleKey switch
            {
                "md" => ("GEN", "Genplus-gx"), "sms" => ("SMS", "SMSHawk"), "gg" => ("GG", "SMSHawk"), "nes" => ("NES", "NesHawk"),
                "snes" => ("SNES", "BSNESv115+"), "gb" or "gbc" => ("GB", "Gambatte"), _ => throw new ToolError($"no BizHawk core for '{consoleKey}'"),
            };
            string cfg = Path.Combine(home, "config.ini");
            var pc = new ProcessStartInfo("pwsh", new[] { "-NoProfile", "-File", Path.Combine(RefTools, "bizhawk-config.ps1"), "-System", sys, "-Core", core, "-Scratch", home, "-InstallDir", dir, "-Out", cfg })
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            using (var p = Process.Start(pc)!)
            {
                string err = p.StandardError.ReadToEnd(); p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(60_000) || p.ExitCode != 0) { l.Stop(); throw new ToolError($"the BizHawk config was not written: {err.Trim()}"); }
            }
            psi.FileName = exe;
            foreach (var a in new[] { "--config=" + cfg, "--chromeless", "--socket_ip=127.0.0.1", "--socket_port=" + port, "--lua=" + Path.Combine(RefTools, "bizhawk_refstep.lua"), romArg }) psi.ArgumentList.Add(a);
        }
        var proc = Process.Start(psi) ?? throw new ToolError($"{emulator} did not start");
        var r = new RefEmu(emulator, consoleKey, home, proc, l, emulator == "bizhawk", shot);
        try
        {
            var accept = l.AcceptTcpClientAsync();
            if (!accept.Wait(timeoutMs)) throw new ToolError($"{emulator} did not connect within {timeoutMs / 1000} s (its script did not start: is the ROM one it opens?)");
            r.client = accept.Result; r.client.NoDelay = true;
            r.net = r.client.GetStream(); r.net.ReadTimeout = timeoutMs;
            r.Hello = r.ReadMsg();
            if (!r.Hello.StartsWith("hello", StringComparison.Ordinal)) throw new ToolError($"{emulator} said '{r.Hello}' instead of hello");
            int d = r.Hello.IndexOf(" done=", StringComparison.Ordinal);
            if (d >= 0) r.Frame = long.Parse(r.Hello[(d + 6)..].Split(' ')[0], CultureInfo.InvariantCulture);
        }
        catch { r.Dispose(); throw; }
        return r;
    }

    private static string MesenSettings(string consoleKey)
    {
        var s = new JsonObject
        {
            ["Version"] = "2.1.1", ["ConfigUpgrade"] = 5,
            ["Audio"] = new JsonObject { ["EnableAudio"] = false },
            ["Nes"] = new JsonObject { ["Port1"] = new JsonObject { ["Type"] = "NesController" }, ["Port2"] = new JsonObject { ["Type"] = "NesController" } },
            ["Snes"] = new JsonObject { ["DisableFrameSkipping"] = true, ["Port1"] = new JsonObject { ["Type"] = "SnesController" }, ["Port2"] = new JsonObject { ["Type"] = "SnesController" } },
            ["Sms"] = new JsonObject { ["Port1"] = new JsonObject { ["Type"] = "SmsController" }, ["Port2"] = new JsonObject { ["Type"] = "SmsController" } },
            ["Preferences"] = new JsonObject
            {
                ["SingleInstance"] = false, ["AutomaticallyCheckForUpdates"] = false, ["PauseWhenInBackground"] = false, ["PauseWhenInMenusAndConfig"] = false, ["EnableAutoSaveState"] = false,
            },
            // the script writes its picture file (IO) and talks to this server (network: LuaSocket's core)
            ["Debug"] = new JsonObject { ["ScriptWindow"] = new JsonObject { ["AllowIoOsAccess"] = true, ["AllowNetworkAccess"] = true } },
        };
        if (consoleKey is "gb" or "gbc")
            s["Gameboy"] = new JsonObject { ["Model"] = consoleKey == "gbc" ? "GameboyColor" : "Gameboy", ["Controller"] = new JsonObject { ["Type"] = "GameboyController" } };
        return s.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    // ---------------------------------------------------------------------------------------------------------------- the wire

    private void Send(string text)
    {
        byte[] b = Encoding.ASCII.GetBytes(text);
        byte[] msg = framed ? Encoding.ASCII.GetBytes(b.Length.ToString(CultureInfo.InvariantCulture) + " ").Concat(b).ToArray() : b.Concat(new byte[] { 10 }).ToArray();
        net!.Write(msg);
    }

    private string ReadMsg()
    {
        while (true)
        {
            var buf = pending.ToArray();
            if (framed)
            {
                int sp = Array.IndexOf(buf, (byte)' ');
                if (sp > 0 && int.TryParse(Encoding.ASCII.GetString(buf, 0, sp), out int n) && buf.Length >= sp + 1 + n)
                {
                    string m = Encoding.ASCII.GetString(buf, sp + 1, n);
                    Rest(buf, sp + 1 + n);
                    return m;
                }
            }
            else
            {
                int nl = Array.IndexOf(buf, (byte)10);
                if (nl >= 0) { string m = Encoding.ASCII.GetString(buf, 0, nl).TrimEnd('\r'); Rest(buf, nl + 1); return m; }
            }
            int got;
            try { got = net!.Read(rbuf, 0, rbuf.Length); }
            catch (IOException e) { throw new ToolError($"the reference ({Emulator}) did not answer: {e.Message}"); }
            if (got <= 0) throw new ToolError($"the reference ({Emulator}) closed the connection{(proc.HasExited ? $" (it exited, code {proc.ExitCode})" : "")}");
            pending.Write(rbuf, 0, got);
        }
    }

    private void Rest(byte[] buf, int from) { pending.SetLength(0); pending.Write(buf, from, buf.Length - from); }

    public bool Alive => !proc.HasExited && client?.Connected == true;

    /// <summary>Runs the reference until it has completed <paramref name="target"/> frames, the pads held; then its picture (half size with
    /// <paramref name="half"/>) and the bytes at the addresses.</summary>
    public (long frame, long[] values, Pic? pic) RunTo(long target, PadButtons p1, PadButtons p2, IReadOnlyList<long> addrs, bool shot, bool half = false)
    {
        if (!Alive) throw new ToolError($"the reference ({Emulator}) is not running any more: close it and open it again");
        string list = addrs.Count == 0 ? "-" : string.Join(",", addrs.Select(a => a.ToString(CultureInfo.InvariantCulture)));
        Send($"run {Math.Max(0, target).ToString(CultureInfo.InvariantCulture)} {(int)p1} {(int)p2} {(shot ? (half ? 2 : 1) : 0)} {list}");
        string r = ReadMsg();
        if (!r.StartsWith("ok ", StringComparison.Ordinal)) throw new ToolError($"the reference ({Emulator}) said: {r}");
        var parts = r.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Frame = long.Parse(parts[1], CultureInfo.InvariantCulture);
        var vals = parts.Skip(2).Select(p => long.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : -1).ToArray();
        var pic = shot ? ReadShot() : null;
        if (pic != null && half && Emulator != "mesen") pic = pic.Half();   // Mesen halves it itself
        return (Frame, vals, pic);
    }

    private Pic ReadShot()
    {
        if (Emulator == "mesen")
        {
            byte[] b = File.ReadAllBytes(shotPath);
            int nl = Array.IndexOf(b, (byte)10);
            var wh = Encoding.ASCII.GetString(b, 0, nl).Split(' ');
            int w = int.Parse(wh[0], CultureInfo.InvariantCulture), h = int.Parse(wh[1], CultureInfo.InvariantCulture);
            var px = new uint[w * h];
            for (int i = 0; i < px.Length && nl + 1 + i * 4 + 3 < b.Length; i++) px[i] = 0xFF000000u | BitConverter.ToUInt32(b, nl + 1 + i * 4);
            return new Pic(px, w, h);
        }
        using var bmp = new System.Drawing.Bitmap(new MemoryStream(File.ReadAllBytes(shotPath)));
        var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var a = new int[bmp.Width * bmp.Height];
        for (int y = 0; y < bmp.Height; y++) System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, a, y * bmp.Width, bmp.Width);
        bmp.UnlockBits(data);
        return new Pic(a.Select(v => 0xFF000000u | (uint)v).ToArray(), bmp.Width, bmp.Height);
    }

    public JsonObject Describe() => new()
    {
        ["emulator"] = Emulator, ["console"] = Console, ["frame"] = Frame, ["offset"] = Offset, ["alive"] = Alive, ["hello"] = Hello, ["home"] = Home,
        ["pid"] = proc.HasExited ? null : proc.Id,
    };

    public void Dispose()
    {
        try { if (client?.Connected == true) Send("quit"); } catch { }
        try { if (!proc.WaitForExit(5000)) proc.Kill(true); } catch { }
        try { client?.Dispose(); } catch { }
        try { listener.Stop(); } catch { }
        try { proc.Dispose(); } catch { }
    }
}

/// <summary>A picture (0xAARRGGBB pixels).</summary>
internal sealed record Pic(uint[] Px, int W, int H)
{
    /// <summary>Half size (2x2 boxes averaged), for small screenshots.</summary>
    public Pic Half()
    {
        int w = Math.Max(1, W / 2), h = Math.Max(1, H / 2);
        var o = new uint[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                uint r = 0, g = 0, b = 0;
                for (int dy = 0; dy < 2; dy++) for (int dx = 0; dx < 2; dx++)
                {
                    uint p = Px[Math.Min(H - 1, y * 2 + dy) * W + Math.Min(W - 1, x * 2 + dx)];
                    r += (p >> 16) & 255; g += (p >> 8) & 255; b += p & 255;
                }
                o[y * w + x] = 0xFF000000u | (r / 4) << 16 | (g / 4) << 8 | b / 4;
            }
        return new Pic(o, w, h);
    }

    public byte[] Png(double scale)
    {
        if (scale < 1) { var h = Half(); return BrokenNes.Mcp.Png.FromArgb(h.Px, h.W, h.H, 1); }
        return BrokenNes.Mcp.Png.FromArgb(Px, W, H, (int)Math.Round(scale));
    }
}

/// <summary>
/// The difference score of two pictures from two emulators. Their palettes differ (every NES emulator has its own), so colours are not compared:
/// both are reduced to brightness on a common grid (the smaller of the two sizes), then compared two ways. pixel: the share of grid cells whose
/// brightness differs by more than 1/6 of the range. edges: the shapes (where brightness steps between neighbours), as 1 - the overlap of the
/// two edge maps (palette-blind). score = the mean of the two: 0 is the same picture, about 0.02 and under is the same up to palette and
/// filtering, a different screen is typically 0.3 and over.
/// </summary>
internal static class PicDiff
{
    public static JsonObject Compare(Pic a, Pic b)
    {
        int w = Math.Min(a.W, b.W), h = Math.Min(a.H, b.H);
        var la = Luma(a, w, h); var lb = Luma(b, w, h);
        int diff = 0;
        for (int i = 0; i < la.Length; i++) if (Math.Abs(la[i] - lb[i]) > 42) diff++;
        var ea = Edges(la, w, h); var eb = Edges(lb, w, h);
        int both = 0, either = 0;
        for (int i = 0; i < ea.Length; i++) { if (ea[i] && eb[i]) both++; if (ea[i] || eb[i]) either++; }
        double pixel = (double)diff / la.Length, edges = either == 0 ? 0 : 1 - (double)both / either;
        double score = Math.Round((pixel + edges) / 2, 4);
        return new JsonObject
        {
            ["score"] = score, ["pixel"] = Math.Round(pixel, 4), ["edges"] = Math.Round(edges, 4), ["grid"] = $"{w}x{h}",
            ["sizes"] = $"brokennes {a.W}x{a.H}, reference {b.W}x{b.H}",
            ["verdict"] = score <= 0.02 ? "same" : score <= 0.12 ? "close" : "different",
        };
    }

    public static double Score(Pic a, Pic b) => (double)Compare(a, b)["score"]!;

    /// <summary>Brightness 0-255 sampled on a w x h grid (nearest pixel: a picture of another size is scaled onto it).</summary>
    private static int[] Luma(Pic p, int w, int h)
    {
        var o = new int[w * h];
        for (int y = 0; y < h; y++)
        {
            int sy = (int)((long)y * p.H / h);
            for (int x = 0; x < w; x++)
            {
                uint c = p.Px[sy * p.W + (int)((long)x * p.W / w)];
                o[y * w + x] = (int)((((c >> 16) & 255) * 299 + ((c >> 8) & 255) * 587 + (c & 255) * 114) / 1000);
            }
        }
        return o;
    }

    private static bool[] Edges(int[] l, int w, int h)
    {
        var e = new bool[w * h];
        for (int y = 0; y < h - 1; y++)
            for (int x = 0; x < w - 1; x++)
            {
                int i = y * w + x;
                e[i] = Math.Abs(l[i] - l[i + 1]) > 24 || Math.Abs(l[i] - l[i + w]) > 24;
            }
        return e;
    }
}
