// [cer64plus] SessionProbe compat mode: runs a pad script on a game and writes <outdir>/report.json (+ PNG screenshots,
// savestates). Usage: SessionProbe <rom> compat <script.txt> <outdir>. See compat/README.md for the script language.
// Writes only under <outdir> (it changes the working directory there, so core debug files under telemetry/ land there too).
// COMPAT_TWICE=1 reruns the script in a fresh session in the same process and compares every checkpoint hash.
// Turns on COR64_COMPAT_LOG through the environment of the launcher (run_game.sh); without it the unimplemented-feature
// log is empty and the report says so.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using cor64;
using cor64.PIF;
using cor64.Session;
using Btn = cor64.PIF.JoyController.ButtonPress;

static class CompatMode
{
    sealed class Cmd { public string Op; public string[] A; public int Line; }

    sealed class Checkpoint
    {
        public string name, kind; public int frame;
        public string video, frameHash, audio; public long samples; public double rms, peak;
        public bool solid; public double dominantFraction; public string dominantColor;
        public int width, height; public double msAvg, msMax; public string file;
        public long rdramHighNonZero;
        public long dpSyncs, fillTicks; // [quirk_fix2] RDP SYNC_FULLs (game-rendered frames) in this checkpoint's window
    }

    sealed class Pass
    {
        public List<Checkpoint> cps = new(); public int frames; public double totalMs; public List<double> frameMs = new();
        public string error; public int eepromNonZero; public string saveType; public int saveNonZero, saveSize;
    }

    static readonly Dictionary<string, Btn> s_Buttons = new(StringComparer.OrdinalIgnoreCase) {
        ["A"] = Btn.A, ["B"] = Btn.B, ["Z"] = Btn.Z, ["START"] = Btn.Start, ["L"] = Btn.ShoulderL, ["R"] = Btn.ShoulderR,
        ["CU"] = Btn.CUp, ["CD"] = Btn.CDown, ["CL"] = Btn.CLeft, ["CR"] = Btn.CRight,
        ["DU"] = Btn.DPadUp, ["DD"] = Btn.DPadDown, ["DL"] = Btn.DPadLeft, ["DR"] = Btn.DPadRight,
    };

    static Btn ParseButtons(string s, int line)
    {
        Btn b = Btn.None;
        foreach (var t in s.Split('+', StringSplitOptions.RemoveEmptyEntries)) {
            if (!s_Buttons.TryGetValue(t, out var x)) throw new FormatException($"line {line}: unknown button '{t}' (A B Z START L R CU CD CL CR DU DD DL DR)");
            b |= x;
        }
        return b;
    }

    static List<Cmd> ParseScript(string path)
    {
        var list = new List<Cmd>(); int n = 0;
        foreach (var raw in File.ReadAllLines(path)) {
            n++; var l = raw; int c = l.IndexOf('#'); if (c >= 0) l = l[..c]; l = l.Trim(); if (l.Length == 0) continue;
            var p = l.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            list.Add(new Cmd { Op = p[0].ToLowerInvariant(), A = p[1..], Line = n });
        }
        return list;
    }

    // first-chance exceptions: type -> count, first stack
    static readonly Dictionary<string, (long n, string stack, string msg)> s_Exc = new();
    static readonly object s_ExcLock = new();
    [ThreadStatic] static bool s_InHandler;

    private static readonly bool s_ViDump = Environment.GetEnvironmentVariable("COR64_COMPAT_VIDUMP") == "1";
    public static int Run(string[] args)
    {
        if (args.Length < 4) { Console.WriteLine("usage: SessionProbe <rom> compat <script.txt> <outdir>"); return 2; }
        string rom = Path.GetFullPath(args[0]), script = Path.GetFullPath(args[2]), outdir = Path.GetFullPath(args[3]);
        Directory.CreateDirectory(outdir);
        Environment.CurrentDirectory = outdir; // any core debug output (telemetry/...) stays in this run's folder
        // [boot_dkr] COR64_EVENTLOG=1: core event log (MI/SP/DP/AI events) into this run's folder (debug only)
        if (cor64.EventLog.Enabled) { cor64.EventLog.Open(Path.Combine(outdir, "events.log")); AppDomain.CurrentDomain.ProcessExit += (o, e) => cor64.EventLog.Flush(); }
        AppDomain.CurrentDomain.FirstChanceException += (o, e) => {
            if (s_InHandler) return; s_InHandler = true;
            try {
                var key = e.Exception.GetType().FullName + " @ " + (e.Exception.TargetSite?.DeclaringType?.Name ?? "?") + "." + (e.Exception.TargetSite?.Name ?? "?");
                lock (s_ExcLock) {
                    if (s_Exc.TryGetValue(key, out var v)) s_Exc[key] = (v.n + 1, v.stack, v.msg);
                    else s_Exc[key] = (1, new StackTrace(1, false).ToString(), e.Exception.Message);
                }
            } catch { } finally { s_InHandler = false; }
        };

        // [timing] COR64_EVENTLOG=1: device/interrupt event log in telemetry/events.log (debug aid; flushed at exit)
        if (cor64.EventLog.Enabled) { Directory.CreateDirectory("telemetry"); cor64.EventLog.Open("telemetry/events.log"); AppDomain.CurrentDomain.ProcessExit += (_, _) => cor64.EventLog.Flush(); }
        if (cor64.Mips.R4300I.Interpreter.s_PcHist) AppDomain.CurrentDomain.ProcessExit += (_, _) => { // [timing] debug aid
            var h = new List<KeyValuePair<uint, long>>(cor64.Mips.R4300I.Interpreter.PcHist);
            h.Sort((x, y) => y.Value.CompareTo(x.Value)); long tot = 0; foreach (var kv in h) tot += kv.Value;
            using var w = new StreamWriter("pchist.txt"); w.WriteLine($"TOTAL {tot}");
            foreach (var kv in h.GetRange(0, Math.Min(4000, h.Count))) { cor64.Mips.R4300I.Interpreter.PcInst.TryGetValue(kv.Key, out uint ins); w.WriteLine($"CPUPC {kv.Key:X8} {100.0 * kv.Value / tot,6:F2}% {ins:X8}"); } // [visp_fix2] was 200 entries
        };
        var cmds = ParseScript(script);
        var report = new Dictionary<string, object>();
        byte[] romBytes = File.ReadAllBytes(rom);
        var sw = Stopwatch.StartNew();
        Pass a = null, b = null;
        Dictionary<string, CompatLog.Entry> events = new(); Dictionary<string, long> ucodes = new();
        string title = "", id = "", cic = "", desc = ""; double fps = 60;
        // hang watchdog: a frame that never ends (RunFrame stuck) still gets a report, then the process exits
        int hangSec = int.TryParse(Environment.GetEnvironmentVariable("COMPAT_HANG_SECONDS"), out var hs) ? hs : 120;
        var wd = new System.Threading.Thread(() => {
            int lastFrames = -1; var since = Stopwatch.StartNew(); var total = Stopwatch.StartNew();
            int deadline = int.TryParse(Environment.GetEnvironmentVariable("COMPAT_DEADLINE_SECONDS"), out var dl) ? dl : 0;
            while (true) {
                System.Threading.Thread.Sleep(1000);
                var cur = s_Current; if (cur == null) { since.Restart(); continue; }
                bool late = deadline > 0 && total.Elapsed.TotalSeconds > deadline;
                if (!late && cur.frames != lastFrames) { lastFrames = cur.frames; since.Restart(); continue; }
                if (!late && since.Elapsed.TotalSeconds < hangSec) continue;
                lock (s_ReportLock) {
                    cur.error = late ? $"too slow: deadline {deadline} s reached after {cur.frames} frames ({cur.frames / total.Elapsed.TotalSeconds:F2} frames/s)"
                                     : $"hang: frame {cur.frames + 1} did not finish within {hangSec} s (RunFrame never returned)";
                    if (a == null) { a = cur; events = CompatLog.Events.ToDictionary(kv => kv.Key, kv => kv.Value); ucodes = CompatLog.Ucodes.ToDictionary(kv => kv.Key, kv => kv.Value); }
                    else b = cur;
                    WriteReport();
                }
                Environment.Exit(3);
            }
        }) { IsBackground = true, Name = "compat-watchdog" };
        wd.Start();
        try {
            var persist = SnapshotPersistentFiles(); // [quirk_fix1] pak/GB saves pass 1 writes must not leak into pass 2
            using (var s = Open(romBytes)) {
                title = s.Title; id = s.GameId; cic = s.Cic; desc = s.Description; fps = s.FramesPerSecond;
                a = RunScript(s, cmds, outdir, true, romBytes);
            }
            // the log is process-wide: keep the first pass's view, then clear it for the second pass
            events = CompatLog.Events.ToDictionary(kv => kv.Key, kv => kv.Value); ucodes = CompatLog.Ucodes.ToDictionary(kv => kv.Key, kv => kv.Value);
            CompatLog.Events.Clear(); CompatLog.Ucodes.Clear();
            if (Environment.GetEnvironmentVariable("COMPAT_TWICE") == "1") {
                RestorePersistentFiles(persist);
                ReleaseSession();
                using (var s2 = Open(romBytes)) b = RunScript(s2, cmds, outdir, false, romBytes);
            }
        } catch (Exception e) {
            a ??= new Pass { error = "session failed to open: " + e };
        }
        s_Current = null;
        lock (s_ReportLock) WriteReport();
        return a.error == null ? 0 : 1;

        // [quirk_fix1] Files the emulator writes as it runs (Controller Pak images *.mpk in the pak folder, the Transfer
        // Pak GB save): snapshot before pass 1, restore before pass 2 so COMPAT_TWICE compares like with like.
        // Cartridge battery saves live in memory per session (only "savefile" writes them out), so they need nothing.
        static Dictionary<string, byte[]> SnapshotPersistentFiles()
        {
            var m = new Dictionary<string, byte[]>();
            string dir = Environment.GetEnvironmentVariable("COR64_PIF_PAK_DIR") ?? Environment.CurrentDirectory;
            if (Directory.Exists(dir)) foreach (var f in Directory.GetFiles(dir, "*.mpk")) m[Path.GetFullPath(f)] = File.ReadAllBytes(f);
            m["\0dir"] = System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(dir));
            var gb = Environment.GetEnvironmentVariable("COR64_TPAK_GBSAVE");
            if (string.IsNullOrEmpty(gb)) { var r = Environment.GetEnvironmentVariable("COR64_TPAK_GBROM"); if (!string.IsNullOrEmpty(r)) gb = Path.ChangeExtension(r, ".sav"); }
            if (!string.IsNullOrEmpty(gb)) { gb = Path.GetFullPath(gb); m["\0gb"] = System.Text.Encoding.UTF8.GetBytes(gb); if (File.Exists(gb)) m[gb] = File.ReadAllBytes(gb); }
            return m;
        }
        static void RestorePersistentFiles(Dictionary<string, byte[]> m)
        {
            try {
                string dir = System.Text.Encoding.UTF8.GetString(m["\0dir"]);
                if (Directory.Exists(dir)) foreach (var f in Directory.GetFiles(dir, "*.mpk")) if (!m.ContainsKey(Path.GetFullPath(f))) File.Delete(f);
                if (m.TryGetValue("\0gb", out var g)) { var gb = System.Text.Encoding.UTF8.GetString(g); if (!m.ContainsKey(gb) && File.Exists(gb)) File.Delete(gb); }
                foreach (var kv in m) if (kv.Key[0] != '\0') File.WriteAllBytes(kv.Key, kv.Value);
            } catch (Exception) { }
        }

        void WriteReport() {
        report["rom"] = rom; report["script"] = script;
        report["title"] = title; report["gameId"] = id; report["cic"] = cic; report["description"] = desc;
        report["fpsTarget"] = fps;
        report["header"] = Header(romBytes);
        report["framesRun"] = a.frames;
        report["error"] = a.error;
        report["wallMs"] = Math.Round(sw.Elapsed.TotalMilliseconds);
        if (a.frameMs.Count > 0) {
            var sorted = a.frameMs.OrderBy(x => x).ToArray();
            double avg = a.frameMs.Average();
            report["frameTime"] = new Dictionary<string, object> {
                ["avgMs"] = Math.Round(avg, 3), ["p50Ms"] = Math.Round(sorted[sorted.Length / 2], 3),
                ["p99Ms"] = Math.Round(sorted[(int)(sorted.Length * 0.99)], 3), ["maxMs"] = Math.Round(sorted[^1], 3),
                ["realtimePercent"] = Math.Round(1000.0 / fps / avg * 100, 1),
                ["note"] = "includes JIT warm-up; the machine may be loaded: use bench.sh for speed claims",
            };
        }
        report["checkpoints"] = a.cps;
        report["eepromNonZeroBytes"] = a.eepromNonZero;
        report["saveType"] = a.saveType; report["saveSize"] = a.saveSize; report["saveNonFillBytes"] = a.saveNonZero;
        lock (s_ExcLock)
            report["firstChanceExceptions"] = s_Exc.Select(kv => new Dictionary<string, object> { ["type"] = kv.Key, ["count"] = kv.Value.n, ["message"] = kv.Value.msg, ["firstStack"] = kv.Value.stack }).ToList();
        report["compatLogEnabled"] = CompatLog.On;
        report["unimplemented"] = events.OrderBy(kv => kv.Key).Select(kv => new Dictionary<string, object> { ["key"] = kv.Key, ["count"] = kv.Value.Count, ["firstTick"] = kv.Value.FirstTick, ["detail"] = kv.Value.Detail }).ToList();
        var known = KnownUcodes(script);
        report["ucodes"] = ucodes.OrderBy(kv => kv.Key).Select(kv => new Dictionary<string, object> { ["id"] = kv.Key, ["tasks"] = kv.Value, ["known"] = known.TryGetValue(kv.Key, out var nm) ? nm : null }).ToList();
        report["unknownUcodes"] = ucodes.Keys.Where(k => !known.ContainsKey(k)).OrderBy(k => k).ToList();
        long hi = a.cps.Count > 0 ? a.cps.Max(c => c.rdramHighNonZero) : 0;
        report["expansionPakUsed"] = hi > 0; report["rdramHighNonZeroMax"] = hi;
        if (b != null) {
            var diffs = new List<string>();
            for (int i = 0; i < Math.Max(a.cps.Count, b.cps.Count); i++) {
                if (i >= a.cps.Count || i >= b.cps.Count) { diffs.Add($"checkpoint count {a.cps.Count} vs {b.cps.Count}"); break; }
                if (a.cps[i].video != b.cps[i].video || a.cps[i].audio != b.cps[i].audio) diffs.Add(a.cps[i].name);
            }
            report["determinism"] = new Dictionary<string, object> { ["checked"] = true, ["identical"] = diffs.Count == 0 && a.error == b.error, ["differing"] = diffs, ["secondError"] = b.error };
        } else report["determinism"] = new Dictionary<string, object> { ["checked"] = false };

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true });
        File.WriteAllText(Path.Combine(outdir, "report.json"), json);
        Console.WriteLine($"COMPAT title='{title}' frames={a.frames} checkpoints={a.cps.Count} unimpl={events.Count} exceptions={s_Exc.Count} error={(a.error == null ? "none" : "yes")} -> {Path.Combine(outdir, "report.json")}");
        }
    }
    static volatile Pass s_Current; static readonly object s_ReportLock = new();

    static N64Session Open(byte[] rom)
    {
        var rdp = Environment.GetEnvironmentVariable("COR64_RDP");
        var opt = new N64SessionOptions { Renderer = rdp == "software" ? N64Renderer.Software : rdp == "parallel" ? N64Renderer.Parallel : N64Renderer.Auto, Ppu = Environment.GetEnvironmentVariable("PROBE_PPU") };
        MemLog("before open"); var s = new N64Session(rom, opt); MemLog("after open");
        if (s_MemRelease) { System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce; GC.Collect(); MemLog("after open+collect"); } // [mem_fix] drop construction garbage
        // [cer64plus] lane pif_pads: Controller Pak images in the run folder unless COR64_PIF_PAK_DIR says otherwise
        var ports = s.System.PIF.Ports;
        ports.PakDir ??= Environment.CurrentDirectory;
        ports.GameKey = s.System.AttachedCartridge != null ? s.System.AttachedCartridge.Crc1.ToString("X8") : "game";
        unsafe { if (CompatLog.On) CompatLog.RdramPtr = (byte*)s.System.SystemMemory.RDRAM.GetRamPointer(0); } unsafe { cor64.Telemetry.RdramPtr = (byte*)s.System.SystemMemory.RDRAM.GetRamPointer(0); }
        return s;
    }

    // [mem_fix] COR64_MEM_RELEASE (default on): after a session is disposed (COMPAT_TWICE pass 1, reload), collect and
    // compact the large-object heap and run finalizers (UnmanagedBuffer frees its native memory only there), so the next
    // session reuses that memory instead of adding to it. GC timing does not affect emulation.
    static readonly bool s_MemRelease = Environment.GetEnvironmentVariable("COR64_MEM_RELEASE") != "0";
    static void ReleaseSession()
    {
        MemLog("before release");
        if (!s_MemRelease) return;
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        MemLog("after release");
    }

    // [mem_fix] COR64_MEM_LOG=1: managed heap vs process private bytes at session open and checkpoints
    static readonly bool s_MemLog = Environment.GetEnvironmentVariable("COR64_MEM_LOG") == "1";
    internal static void MemLog(string what)
    {
        if (!s_MemLog) return;
        var gi = GC.GetGCMemoryInfo(); var pr = System.Diagnostics.Process.GetCurrentProcess();
        Console.WriteLine($"MEM {what}: managedLive={GC.GetTotalMemory(false) >> 20}MB gcCommitted={gi.TotalCommittedBytes >> 20}MB private={pr.PrivateMemorySize64 >> 20}MB ws={pr.WorkingSet64 >> 20}MB devTables={cor64.IO.N64MemoryDevice.TableBytes >> 20}MB (big devices {cor64.IO.N64MemoryDevice.TableBytesLargest >> 20}MB)");
    }

    static Dictionary<string, object> Header(byte[] r)
    {
        var h = new Dictionary<string, object> { ["size"] = r.Length };
        if (r.Length < 0x40) return h;
        uint magic = (uint)(r[0] << 24 | r[1] << 16 | r[2] << 8 | r[3]);
        h["byteOrder"] = magic == 0x80371240 ? "z64" : magic == 0x37804012 ? "v64" : magic == 0x40123780 ? "n64" : $"unknown {magic:X8}";
        if (magic == 0x80371240) {
            h["name"] = Encoding.ASCII.GetString(r, 0x20, 20).Trim(' ', '\0');
            h["gameCode"] = Encoding.ASCII.GetString(r, 0x3B, 4);
            h["version"] = r[0x3F];
        }
        return h;
    }

    // compat/known_ucode.tsv next to compat/scripts/: "<id>\t<name>" lines
    static Dictionary<string, string> KnownUcodes(string script)
    {
        var d = new Dictionary<string, string>();
        var f = Environment.GetEnvironmentVariable("COMPAT_KNOWN_UCODE") ?? Path.Combine(Path.GetDirectoryName(script) ?? ".", "..", "known_ucode.tsv");
        if (File.Exists(f)) foreach (var l in File.ReadAllLines(f)) { var p = l.Split('\t'); if (p.Length >= 2 && !l.StartsWith("#")) d[p[0].Trim()] = p[1].Trim(); }
        return d;
    }

    static ulong Hash(uint[] px, int n, ulong h = 1469598103934665603UL)
    {
        for (int i = 0; i < n; i++) { h ^= px[i]; h *= 1099511628211UL; }
        return h;
    }

    static Pass RunScript(N64Session s, List<Cmd> cmds, string outdir, bool primary, byte[] rom)
    {
        var p = new Pass(); s_Current = p; var reloaded = new List<N64Session>();
        Btn held = Btn.None; sbyte sx = 0, sy = 0;
        ulong hv = 1469598103934665603UL, ha = hv; long samples = 0; double sumSq = 0, peak = 0; long winSamples = 0;
        int winStart = 0; double winMs = 0, winMax = 0;
        var buf = new short[16384];

        var pokeHolds = new List<(string, string)>();
        cor64.Debug.N64DebugTarget dbgT = null; // [title_fix2] watch/watchdump
        int mirror = 1; // [quirk_fix2] script "mirror N": P1's input also drives ports 2..N (multiplayer menus)
        void Frame(Btn buttons)
        {
            foreach (var (pa, ph) in pokeHolds) Poke(s, pa, ph); // [cert_scripts13] pokehold cheats
            s.SetPad(0, new N64Pad(buttons, sx, sy));
            for (int m = 1; m < mirror; m++) s.SetPad(m, new N64Pad(buttons, sx, sy));
            long t0 = Stopwatch.GetTimestamp();
            s.RunFrame();
            double ms = (Stopwatch.GetTimestamp() - t0) * 1e3 / Stopwatch.Frequency;
            p.frameMs.Add(ms); p.totalMs += ms; winMs += ms; if (ms > winMax) winMax = ms;
            p.frames++;
            hv = Hash(s.Frame, s.FrameWidth * s.FrameHeight, hv);
            int got;
            while ((got = s.ReadSamples(buf)) > 0) {
                samples += got; winSamples += got;
                for (int k = 0; k < got; k++) { ha ^= (ushort)buf[k]; ha *= 1099511628211UL; double v = buf[k] / 32768.0; sumSq += v * v; if (Math.Abs(v) > peak) peak = Math.Abs(v); }
            }
        }

        long winSyncs = cor64.Rdp.ParallelRdp.FullSyncs, winFill = cor64.Rdp.ParallelRdp.FillTicksTotal;
        Checkpoint Mark(string name, string kind)
        {
            int n = s.FrameWidth * s.FrameHeight; var px = s.Frame;
            var counts = new Dictionary<uint, int>(); int sampled = 0;
            for (int i = 0; i < n; i += 3) { uint c = px[i] & 0xFFFFFF; counts[c] = counts.TryGetValue(c, out var v) ? v + 1 : 1; sampled++; }
            var dom = counts.Count > 0 ? counts.OrderByDescending(kv => kv.Value).First() : default;
            bool solid = true; uint c0 = n > 0 ? px[0] & 0xFFFFFF : 0; for (int i = 1; i < n; i++) if ((px[i] & 0xFFFFFF) != c0) { solid = false; break; }
            int frames = p.frames - winStart;
            var cp = new Checkpoint {
                name = name, kind = kind, frame = p.frames, video = hv.ToString("X16"), frameHash = Hash(px, n).ToString("X16"), audio = ha.ToString("X16"),
                samples = winSamples, rms = Math.Round(winSamples > 0 ? Math.Sqrt(sumSq / winSamples) : 0, 5), peak = Math.Round(peak, 5),
                solid = solid, dominantFraction = Math.Round(sampled > 0 ? (double)dom.Value / sampled : 1, 4), dominantColor = dom.Key.ToString("X6"),
                width = s.FrameWidth, height = s.FrameHeight, msAvg = Math.Round(frames > 0 ? winMs / frames : 0, 3), msMax = Math.Round(winMax, 3),
                rdramHighNonZero = RdramHigh(s), dpSyncs = cor64.Rdp.ParallelRdp.FullSyncs - winSyncs, fillTicks = cor64.Rdp.ParallelRdp.FillTicksTotal - winFill,
            };
            winSyncs = cor64.Rdp.ParallelRdp.FullSyncs; winFill = cor64.Rdp.ParallelRdp.FillTicksTotal;
            winStart = p.frames; winMs = 0; winMax = 0; winSamples = 0; sumSq = 0; peak = 0;
            p.cps.Add(cp); MemLog("cp " + name);
            return cp;
        }

        try {
            foreach (var c in cmds) {
                int Arg(int i, int def) => c.A.Length > i ? int.Parse(c.A[i]) : def;
                switch (c.Op) {
                    case "run": case "frames": case "wait": { int n = Arg(0, 1); for (int i = 0; i < n; i++) Frame(held); break; }
                    case "seconds": { int n = (int)Math.Round(double.Parse(c.A[0], System.Globalization.CultureInfo.InvariantCulture) * s.FramesPerSecond); for (int i = 0; i < n; i++) Frame(held); break; }
                    case "press": { var bt = ParseButtons(c.A[0], c.Line); int on = Arg(1, 5), off = Arg(2, 5); for (int i = 0; i < on; i++) Frame(held | bt); for (int i = 0; i < off; i++) Frame(held); break; }
                    case "pulse": { var bt = ParseButtons(c.A[0], c.Line); int n = Arg(1, 60), per = Arg(2, 10); for (int i = 0; i < n; i++) Frame(i % per < per / 2 ? held | bt : held); break; }
                    // [ingame_fix4] COR64_INGFIX4_PAKCMD (default on; =0 ignores it): script "pak <port 1-4> <none|controller|rumble|transfer>"
                    // swaps the pak in a port mid-run (Snowboard Kids asks for a Rumble Pak; run_v2 forces a Controller Pak).
                    case "pak": if (Environment.GetEnvironmentVariable("COR64_INGFIX4_PAKCMD") != "0") s.SetPak(Math.Clamp(Arg(0, 1), 1, 4) - 1, JoybusPorts.ParsePak(c.A.Length > 1 ? c.A[1] : "none")); break;
                    case "hold": held |=ParseButtons(c.A[0], c.Line); break;
                    case "release": if (c.A.Length == 0) held = Btn.None; else held &= ~ParseButtons(c.A[0], c.Line); break;
                    case "stick": sx = (sbyte)Math.Clamp(Arg(0, 0), -128, 127); sy = (sbyte)Math.Clamp(Arg(1, 0), -128, 127); break;
                    case "checkpoint": Mark(c.A.Length > 0 ? c.A[0] : "cp" + p.cps.Count, "checkpoint"); break;
                    case "screenshot": {
                        var name = c.A.Length > 0 ? c.A[0] : "shot" + p.cps.Count;
                        var cp = Mark(name, "screenshot");
                        if (primary) { cp.file = name + ".png"; WritePng(Path.Combine(outdir, cp.file), s.Frame, s.FrameWidth, s.FrameHeight); }
                        if (s_ViDump) { var v = s.System.VI; Console.WriteLine($"VIDUMP {name} ctrl={v.ControlReg.RegisterValue:X8} origin={v.ReadFramebufferAddressSafe():X6} width={v.WidthReg} h={v.HStart:X8} v={v.VStart:X8} xs={v.XScale:X8} ys={v.YScale:X8}"); } // [emu_fix4] debug
                        break;
                    }
                    case "savestate": {
                        var name = c.A.Length > 0 ? c.A[0] : "state" + p.cps.Count;
                        var st = s.SaveState();
                        var cp = Mark(name, "savestate");
                        if (primary) { cp.file = name + ".state"; File.WriteAllBytes(Path.Combine(outdir, cp.file), st); }
                        else if (Environment.GetEnvironmentVariable("COMPAT_TWICE_STATES") == "1") File.WriteAllBytes(Path.Combine(outdir, name + ".b.state"), st); // [session_leak] debug: diff pass 2
                        break;
                    }
                    case "loadstate": s.LoadState(File.ReadAllBytes(c.A[0].EndsWith(".state") ? Path.GetFullPath(c.A[0]) : Path.Combine(outdir, c.A[0] + ".state"))); break; // a path ending in .state: any file (script development)
                    case "reset": s.Reset(); break;
                    case "wav": if (s.System.Apu is cor64.AiDac dac) dac.OpenWav(Path.Combine(outdir, (c.A.Length > 0 ? c.A[0] : "audio") + ".wav"), Arg(1, 10)); break; // [visp_fix2] script development: record the next N seconds of AI output
                    case "mirror": { int n = Math.Clamp(Arg(0, 1), 1, 4); for (int m = n; m < mirror; m++) s.SetPad(m, new N64Pad(Btn.None, 0, 0)); mirror = n; break; }
                    case "prenmi": s.PreNmi(); break; // [quirk_fix1] reset button pre-NMI interrupt alone (no reset)
                    // [cer64plus] reload: battery save -> new session (fresh power-on) -> import, like closing and reopening the app
                    case "reload": { var bat = s.ExportSave();
                        if (s_MemRelease && reloaded.Count > 0) { reloaded.Remove(s); try { s.Dispose(); } catch { } s = null; ReleaseSession(); } // [mem_fix] free the previous reload now
                        s = Open(rom); reloaded.Add(s); s.ImportSave(bat); break; }
                    case "clearsave": s.ImportSave(new byte[s.ExportSave().Length]); break; // control: blank battery
                    case "dumpram": unsafe { // script development: raw RDRAM (8 MB, host order words) to NAME.ram
                        byte* r = (byte*)s.System.SystemMemory.RDRAM.GetRamPointer(0);
                        File.WriteAllBytes(Path.Combine(outdir, c.A[0] + ".ram"), new ReadOnlySpan<byte>(r, 0x800000).ToArray()); break;
                    }
                    // [cert_scripts13] poke ADDR HEX: write game RAM between frames (cheat; list it in judgements.tsv)
                    case "poke": Poke(s, c.A[0], c.A[1]); break;
                    // pokeeach N ADDR HEX: rewrite before each of the next N frames (holds a value)
                    case "pokeeach": { int n = Arg(0, 1); for (int i = 0; i < n; i++) { Poke(s, c.A[1], c.A[2]); Frame(held); } break; }
                    // pokehold ADDR HEX: rewrite before every following frame (any command) until pokeoff [ADDR]
                    case "pokehold": pokeHolds.RemoveAll(h => h.Item1 == c.A[0]); pokeHolds.Add((c.A[0], c.A[1])); Poke(s, c.A[0], c.A[1]); break;
                    case "pokeoff": if (c.A.Length == 0) pokeHolds.Clear(); else pokeHolds.RemoveAll(h => h.Item1 == c.A[0]); break;
                    // [title_fix2] watch ADDR LEN [r|w|rw]: debugger memory watch (needs COR64_DBG_WATCH=1); watchdump NAME writes hits to NAME.watch
                    case "watch": { dbgT ??= new cor64.Debug.N64DebugTarget(s); var k = c.A.Length > 2 && c.A[2] == "w" ? cor64.Debug.WatchKind.Write : c.A.Length > 2 && c.A[2] == "rw" ? cor64.Debug.WatchKind.ReadWrite : cor64.Debug.WatchKind.Read;
                        dbgT.AddWatch(Convert.ToUInt64(c.A[0], 16), int.Parse(c.A[1]), k); break; }
                    case "watchdump": { if (dbgT == null) break; var hits = dbgT.DrainWatchHits(); var sbw = new StringBuilder();
                        foreach (var h in hits) sbw.AppendLine(h.ToString()); File.WriteAllText(Path.Combine(outdir, c.A[0] + ".watch"), sbw.ToString()); Console.WriteLine($"WATCH {c.A[0]} hits={hits.Count}"); break; }
                    case "peek": unsafe { // script development: print N bytes (default 4) of RAM
                        uint a = PhysAddr(c.A[0]); int n = c.A.Length > 1 ? int.Parse(c.A[1]) : 4; byte* r = (byte*)s.System.SystemMemory.RDRAM.GetRamPointer(0);
                        var sb = new StringBuilder(); for (int i = 0; i < n; i++) sb.Append(r[(a + i) ^ 3].ToString("X2"));
                        Console.WriteLine($"PEEK {c.A[0]} frame={p.frames} {sb}"); break;
                    }
                    case "savefile": {
                        var sv = s.ExportSave(); var nm = c.A.Length > 0 ? c.A[0] : "battery";
                        if (primary) File.WriteAllBytes(Path.Combine(outdir, nm + ".sav"), sv);
                        Console.WriteLine($"SAVE {nm} frame={p.frames} type={s.SaveType} size={sv.Length} sha1={Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(sv))[..12]} writesSincePowerOn={s.SaveWrites}");
                        break;
                    }
                    default: throw new FormatException($"line {c.Line}: unknown command '{c.Op}'");
                }
            }
        } catch (Exception e) {
            p.error = $"{e.GetType().Name}: {e.Message} (frame {p.frames})\n{e.StackTrace}";
            try { Mark("crash", "crash"); if (primary) { p.cps[^1].file = "crash.png"; WritePng(Path.Combine(outdir, "crash.png"), s.Frame, s.FrameWidth, s.FrameHeight); } } catch { }
        }
        try { var sv = s.ExportSave(); p.eepromNonZero = sv.Count(x => x != 0); p.saveType = s.SaveType; p.saveSize = sv.Length; byte fill = p.saveType == "FlashRam" ? (byte)0xFF : (byte)0; p.saveNonZero = sv.Count(x => x != fill); } catch { }
        foreach (var x in reloaded) try { x.Dispose(); } catch { }
        return p;
    }

    static uint PhysAddr(string t)
    {
        uint a = Convert.ToUInt32(t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? t[2..] : t, 16) & 0x1FFFFFFF;
        if (a >= 0x800000) throw new FormatException($"poke/peek address {t} outside RDRAM");
        return a;
    }
    static unsafe void Poke(N64Session s, string addr, string hex)
    {
        uint a = PhysAddr(addr); var b = Convert.FromHexString(hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex);
        if (a + b.Length > 0x800000) throw new FormatException($"poke {addr} runs past RDRAM");
        byte* r = (byte*)s.System.SystemMemory.RDRAM.GetRamPointer(0);
        for (int i = 0; i < b.Length; i++) r[(a + i) ^ 3] = b[i]; // RDRAM is host-order 32-bit words
    }

    static unsafe long RdramHigh(N64Session s)
    {
        byte* r = (byte*)s.System.SystemMemory.RDRAM.GetRamPointer(0);
        long nz = 0; for (int i = 0x400000; i < 0x800000; i += 4) if (*(uint*)(r + i) != 0) nz++;
        return nz;
    }

    // ---- minimal PNG writer (RGB, zlib via System.IO.Compression) ----
    static void WritePng(string path, uint[] px, int w, int h)
    {
        using var raw = new MemoryStream();
        for (int y = 0; y < h; y++) {
            raw.WriteByte(0);
            for (int x = 0; x < w; x++) { uint c = px[y * w + x]; raw.WriteByte((byte)(c >> 16)); raw.WriteByte((byte)(c >> 8)); raw.WriteByte((byte)c); }
        }
        using var z = new MemoryStream();
        using (var zs = new System.IO.Compression.ZLibStream(z, System.IO.Compression.CompressionLevel.Optimal, true)) { raw.Position = 0; raw.CopyTo(zs); }
        using var f = File.Create(path);
        f.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13]; BE(ihdr, 0, (uint)w); BE(ihdr, 4, (uint)h); ihdr[8] = 8; ihdr[9] = 2;
        Chunk(f, "IHDR", ihdr); Chunk(f, "IDAT", z.ToArray()); Chunk(f, "IEND", Array.Empty<byte>());
    }
    static void BE(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
    static void Chunk(Stream f, string type, byte[] data)
    {
        var len = new byte[4]; BE(len, 0, (uint)data.Length); f.Write(len);
        var td = new byte[4 + data.Length]; Encoding.ASCII.GetBytes(type, 0, 4, td, 0); Buffer.BlockCopy(data, 0, td, 4, data.Length);
        f.Write(td); var crc = new byte[4]; BE(crc, 0, Crc(td)); f.Write(crc);
    }
    static uint[] s_Crc;
    static uint Crc(byte[] d)
    {
        if (s_Crc == null) { s_Crc = new uint[256]; for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; s_Crc[n] = c; } }
        uint r = 0xFFFFFFFF; foreach (var b in d) r = s_Crc[(r ^ b) & 0xFF] ^ (r >> 8); return r ^ 0xFFFFFFFF;
    }
}
