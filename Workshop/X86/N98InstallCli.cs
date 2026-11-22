using System.Text.Json;
using System.Text.Json.Nodes;
using NesEmulator.X86;

namespace BrokenNes.Workshop.X86;

/// <summary>
/// --n98install: the nano98 install flow headless (the same N98Install the desktop wizard, the HTTP API and the MCP server use).
///   --n98install (--disc ISO | --drive E: [--rip-to ISO]) [--machine win98-p166-nano] [--sound SB16_D072] [--layers ID...]
///                [--script FILE | --do "STEP; STEP"] [--out PKG.n98 --title T [--exe auto|D:/GAME/X.EXE] [--args A] [--layer-id ID] [--saves GLOB...]]
///                [--settle-layer ID OUT.n98] [--shotdir DIR] [--frames-max N] [--json]
/// Steps (one per line, or ';'-separated in --do):
///   wait ready | wait off | wait exit | wait frames N | wait status TEXT [N]   (N = timeout frames, default 60000)
///   setups                         print the disc's setup candidates (JSON)
///   run PATH [ARGS]                start a program in the guest (setup)  | runsetup  (the disc's first setup candidate)
///   keys TEXT                      keys / mouse tokens (N98Install.Input: {enter} {alt+n} {m:dx,dy} {click} {wait:N}...)
///   shot NAME.png                  the screen (into --shotdir)          | text  (the text-mode screen)
///   winfiles                       put the Windows 98 SE files disc in (Windows asks for its CD; --make-winfiles WIN98SE.ISO makes it once)
///   disc PATH | disc none          swap the disc                         | poweroff | restart | explorer
///   save STATE.bin | load STATE.bin   a machine savestate (iterate on a long install)
/// After the steps: when --out is given and the machine is off, the drives are captured into the package (--exe auto = the biggest likely program).
/// Every result is one JSON line prefixed "n98install: ".
/// </summary>
internal static class N98InstallCli
{
    private static int[] Shown(PcSession s) => System.Runtime.InteropServices.MemoryMarshal.Cast<uint, int>(s.Frame.AsSpan(0, s.FrameWidth * s.FrameHeight)).ToArray();
    private static void Out(JsonNode n) => Console.WriteLine("n98install: " + n.ToJsonString());

    public static int Run(string[] args)
    {
        string? A(string k) { int i = Array.IndexOf(args, k); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        string[] Many(string k) { int i = Array.IndexOf(args, k); if (i < 0) return Array.Empty<string>(); return args.Skip(i + 1).TakeWhile(x => !x.StartsWith("--")).ToArray(); }
        var o = new N98Install.InstallOptions { Disc = A("--disc"), Machine = A("--machine") ?? "win98-p166-nano", Sound = A("--sound") ?? "SB16_D072", Layers = Many("--layers") };
        if (A("--drive") is { } drv)
        {
            string iso = A("--rip-to") ?? Path.Combine(N98Seat.DiscFolder, $"disc_{DateTime.Now:yyyyMMdd_HHmmss}.iso");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(iso))!);
            long last = -1;
            N98Install.RipDisc(drv, iso, (d, t) => { if (d * 20 / t != last) { last = d * 20 / t; Out(new JsonObject { ["rip"] = $"{d}/{t}" }); } });
            o.Disc = iso; Out(new JsonObject { ["ripped"] = iso });
        }
        if (A("--build-base") is { } biso)   // --build-base WIN98SE.ISO --key-env VAR [--base-out PATH]: the product key from an environment variable only
        {
            string key = Environment.GetEnvironmentVariable(A("--key-env") ?? "N98_PRODUCT_KEY") ?? "";
            var br = N98BaseBuild.Run(biso, key, A("--base-out"), null, l => Console.WriteLine("n98base: " + l));
            Out(br); if (br["ok"]?.GetValue<bool>() != true) return 2;
            if (A("--disc") == null && A("--drive") == null) return 0;
        }
        if (A("--make-winfiles") is { } w98) { var wr = N98Install.MakeWindowsFilesDisc(w98); Out(wr); if (wr["ok"]?.GetValue<bool>() != true) return 2; }
        string shotDir = A("--shotdir") ?? ".";
        Directory.CreateDirectory(shotDir);
        var steps = new List<string>();
        if (A("--script") is { } sf) steps.AddRange(File.ReadAllLines(sf));
        if (A("--do") is { } d0) steps.AddRange(d0.Split(';'));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var ins = N98Install.Start(o);
        ins.Session.Machine.StatusLine += l => Out(new JsonObject { ["status"] = l, ["frame"] = ins.Frames, ["s"] = Math.Round(sw.Elapsed.TotalSeconds, 1) });
        Out(new JsonObject { ["started"] = true, ["base"] = ins.BasePath, ["machine"] = o.Machine, ["disc"] = o.Disc, ["warm"] = ins.Session.Seat!.WarmDir });
        long max = long.TryParse(A("--frames-max"), out var fm) ? fm : 2_000_000;
        bool Until(Func<bool> cond, long timeout)
        {
            long end = ins.Frames + timeout;
            while (!cond()) { if (ins.Frames >= end || ins.Frames >= max) return false; ins.RunFrame(); }
            return true;
        }
        void Drain() => Until(() => ins.InputPending == 0, 100000);
        Until(() => ins.ShellReady, 60000);
        Out(new JsonObject { ["ready"] = ins.ShellReady, ["frame"] = ins.Frames, ["s"] = Math.Round(sw.Elapsed.TotalSeconds, 1) });
        foreach (var raw in steps)
        {
            string st = raw.Trim(); if (st.Length == 0 || st.StartsWith('#')) continue;
            string verb = st.Split(' ', 2)[0].ToLowerInvariant(), rest = st.Contains(' ') ? st[(st.IndexOf(' ') + 1)..].Trim() : "";
            bool ok = true;
            switch (verb)
            {
                case "wait":
                    var w = rest.Split(' ', 2); string what = w[0].ToLowerInvariant(); string arg = w.Length > 1 ? w[1] : "";
                    long tmo = 60000;
                    if (what == "frames") { long n = long.Parse(arg); ok = Until(() => false, n) || true; break; }
                    if (what == "status") { var a2 = arg.Split(' '); if (a2.Length > 1 && long.TryParse(a2[^1], out var t2)) { tmo = t2; arg = string.Join(' ', a2[..^1]); } int from = ins.Log.Count; ok = Until(() => { lock (ins.Log) return ins.Log.Skip(from).Any(l => l.Contains(arg, StringComparison.OrdinalIgnoreCase)); }, tmo); break; }
                    if (long.TryParse(arg, out var t3)) tmo = t3;
                    ok = what switch
                    {
                        "ready" => Until(() => ins.ShellReady && ins.Phase == "ready", tmo),
                        "off" => Until(() => ins.PoweredOff, tmo),
                        "exit" => Until(() => ins.Phase == "ready", tmo),
                        _ => throw new ArgumentException("wait " + what),
                    };
                    break;
                case "setups": Out(new JsonObject { ["setups"] = new JsonArray(N98Install.SetupCandidates(ins.Disc!).Select(x => (JsonNode)x).ToArray()) }); break;
                case "runsetup": { var c = N98Install.SetupCandidates(ins.Disc!); if (c.Count == 0) throw new InvalidOperationException("no setup program on the disc"); ins.Run(c[0]); Until(() => false, 8); break; }
                case "run": { var r = rest.Split(' ', 2); ins.Run(r[0], r.Length > 1 ? r[1] : ""); Until(() => false, 8); break; }
                case "keys": ins.Input(rest); Drain(); break;
                case "shot": X86Shots.SavePng(Shown(ins.Session), ins.Session.FrameWidth, ins.Session.FrameHeight, Path.Combine(shotDir, rest)); break;
                case "text": Out(new JsonObject { ["text"] = ins.Session.TextScreen() }); break;
                case "disc": ins.SwapDisc(rest.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : rest); break;
                case "poweroff": ins.PowerOff(); ok = Until(() => ins.PoweredOff, 30000); break;
                case "winfiles": ins.InsertWindowsFilesDisc(); break;
                case "restart": ins.Restart(); break;
                case "explorer": ins.Explorer(); break;
                case "save": File.WriteAllBytes(rest, ins.Session.SaveState()); break;
                case "load": ins.Session.LoadState(File.ReadAllBytes(rest)); break;
                default: throw new ArgumentException("unknown step: " + st);
            }
            Out(new JsonObject { ["step"] = st, ["ok"] = ok, ["frame"] = ins.Frames, ["phase"] = ins.Phase, ["s"] = Math.Round(sw.Elapsed.TotalSeconds, 1) });
            if (!ok) { Out(ins.Status()); X86Shots.SavePng(Shown(ins.Session), ins.Session.FrameWidth, ins.Session.FrameHeight, Path.Combine(shotDir, "failed.png")); return 2; }
        }
        if (args.Contains("--settle-layer"))
        {
            var sl = Many("--settle-layer");
            if (!ins.PoweredOff) { ins.PowerOff(); Until(() => ins.PoweredOff, 30000); }
            Out(ins.CaptureLayer(sl[0], sl[1]));
        }
        if (A("--out") is { } pkg)
        {
            if (!ins.PoweredOff) { ins.PowerOff(); if (!Until(() => ins.PoweredOff, 30000)) { Out(new JsonObject { ["ok"] = false, ["error"] = "Windows did not power off" }); return 2; } }
            var exes = ins.GamePrograms();
            Out(new JsonObject { ["programs"] = exes.DeepClone() });
            string exe = A("--exe") ?? "auto";
            if (exe == "auto") exe = exes.FirstOrDefault()?["path"]?.GetValue<string>() ?? throw new InvalidOperationException("no program on D:\\GAME: the install did not reach the game drive");
            var res = ins.Capture(A("--title") ?? Path.GetFileNameWithoutExtension(pkg), exe, pkg, A("--args") ?? "", A("--layer-id"), Many("--saves"));
            Out(res);
        }
        Out(new JsonObject { ["done"] = true, ["frames"] = ins.Frames, ["s"] = Math.Round(sw.Elapsed.TotalSeconds, 1) });
        return 0;
    }
}
