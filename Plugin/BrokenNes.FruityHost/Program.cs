// BrokenNes.FruityHost: the plugin test bench.
//
//   selftest <plugin.dll> [--rom game.nes] [--only id,id] [--json report.json] [--md report.md]   run the certification suite (--rom or BROKENNES_TEST_ROM enables the ROM-mode tests)
//   render   <plugin.dll> <fixture.json|name> <out.wav> [--rate 44100] [--block 256] [--chip FIX]
//   fixture  <name> <out.json>                                                    write a built-in fixture (for the FL project generator)
//   analyze  <audio.wav> <fixture.json> [--json out.json] [--offset-ms 0]         certify audio rendered by FL Studio against a fixture
//   track    <audio.wav> <fromMs> <toMs> [--ref 440] [--window 40] [--hop 10]    print the pitch track (Hz and cents from --ref)
//   regtrace <plugin.dll> <fixture.json> <channel 0-3> [--fromMs a --toMs b]      print every change of a channel's period register (dev aid)
//   state    <plugin.dll> <out.bin> [--channel 1-5]                                               write the plugin's default saved-state blob (for the FL project generator)
//   list                                                                          list built-in fixtures
// Exit code 0 = everything passed (warnings allowed), 1 = a check failed, 2 = bad usage.
using System.Text;
using System.Text.Json;
using BrokenNes.FruityHost;

static string? Opt(string[] a, string name) { int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }

if (args.Length == 0) { Console.Error.WriteLine("usage: selftest|render|fixture|analyze|list (see the header of Program.cs)"); return 2; }

switch (args[0])
{
    case "logprobe":
        SelfTest.LogProbe(args[1]);
        return 0;

    case "track":
    {
        var (data, ch, rate) = Audio.ReadWav(args[1]);
        var mono = Audio.Mono(data, ch);
        double refHz = double.Parse(Opt(args, "--ref") ?? "440"), win = double.Parse(Opt(args, "--window") ?? "40"), hop = double.Parse(Opt(args, "--hop") ?? "10");
        foreach (var (ms, hz) in Audio.Track(mono, rate, double.Parse(args[2]), double.Parse(args[3]), win, hop))
            Console.WriteLine($"{ms,8:0.0} ms  {hz,9:0.00} Hz  {(double.IsNaN(hz) ? double.NaN : Audio.Cents(hz, refHz)),8:+0.0;-0.0} cents");
        return 0;
    }

    case "regtrace":
    {
        var lib = new PluginLibrary(args[1]);
        var scenario = Scenario.Load(args[2]);
        int ch = int.Parse(args[3]);
        double fromMs = double.Parse(Opt(args, "--fromMs") ?? "0"), toMs = double.Parse(Opt(args, "--toMs") ?? "1e9");
        using var plugin = lib.Create();
        long last = -1;
        var mixer = new Mixer { SampleRate = 44100, BlockSize = 64, AfterBlock = sample =>
        {
            long period = plugin.Test(1, ch);
            double ms = sample * 1000.0 / 44100;
            if (period != last && ms >= fromMs && ms <= toMs) Console.WriteLine($"{ms,8:0.0} ms  period {period,4}  hi {(period >> 8) & 7}  $400{ch * 4 + 3:X}={plugin.Test(6, ch * 4 + 3):X2}");
            last = period;
        } };
        mixer.Run(plugin, scenario);
        return 0;
    }

    case "state":
    {
        using var plugin = new PluginLibrary(args[1]).Create();
        if (Opt(args, "--channel") is { } sc) plugin.SetParam(6, int.Parse(sc));   // 1 pulse 1, 2 pulse 2, 3 triangle, 4 noise, 5 mix
        File.WriteAllBytes(args[2], plugin.SaveState());
        Console.WriteLine($"wrote {args[2]} ({new FileInfo(args[2]).Length} bytes)");
        return 0;
    }

    case "editor-shot":
    {
        // editor-shot <plugin.dll> <out.bmp> [--mode rom|direct] [--rom game.nes] [--seconds 3] [--console 0-2] [--chip QN]
        //             [--channel 1-5] [--emulator 0-8] [--instances N] [--open chip|game|channel|emulator|inputs|instrument|duty] [--runaway] [--sampler]
        // opens the editor the way FL would, lets the game run a little, and captures the window
        var lib = new PluginLibrary(args[1]);
        string? rom = Opt(args, "--rom");
        // the About window: shown only with --about (the plugin's memory of having shown it lives in a temp file here, not the registry)
        string aboutFile = Path.Combine(Path.GetTempPath(), $"bn2_shot_about_{Environment.ProcessId}.txt");
        if (args.Contains("--about")) File.Delete(aboutFile); else File.WriteAllText(aboutFile, "AboutRevisionSeen=99\n");
        Environment.SetEnvironmentVariable("BROKENNES_PREFS_FILE", aboutFile);
        var extras = new List<PluginInstance>();
        for (int i = 1; i < int.Parse(Opt(args, "--instances") ?? "1"); i++) extras.Add(lib.Create());   // other Auto instances: the channel strip shows them
        using var plugin = lib.Create();
        void SetByName(int param, string? id) { if (id == null) return; for (int i = 0; ; i++) { var n = plugin.GetName(BrokenNes.Fruity.Fpn.ParamValue, param, i); if (n is "?" or "") throw new ArgumentException($"no value {id} for parameter {param}"); if (n == id) { plugin.SetParam(param, i); return; } } }
        plugin.Dispatcher(BrokenNes.Fruity.Fpd.SetSampleRate, 0, 48000);
        if (Opt(args, "--emulator") is { } em) plugin.SetParam(9, int.Parse(em));
        if (Opt(args, "--channel") is { } chn) plugin.SetParam(6, int.Parse(chn));
        if (Opt(args, "--console") is { } cn) plugin.SetParam(10, int.Parse(cn));
        SetByName(0, Opt(args, "--chip"));
        if (Opt(args, "--mode") == "rom" || rom != null)
        {
            plugin.SetParam(8, 1);
            if (rom != null)
            {
                var st = plugin.SaveState();
                var keep = st.Take(12 + 4 * plugin.NumParams).ToList(); var path = System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(rom));
                keep.AddRange(BitConverter.GetBytes(path.Length)); keep.AddRange(path);
                plugin.LoadState(keep.ToArray());
            }
        }
        nint parent = Win32Host.CreateParent();
        plugin.ShowEditor(parent);
        var buf = new float[1024];
        if (args.Contains("--runaway") || args.Contains("--sampler")) { plugin.Test(47, 0, 1); if (args.Contains("--sampler")) plugin.Test(46); }
        var until = Environment.TickCount64 + 600;
        while (plugin.Test(9) != 1 && Opt(args, "--mode") == "rom" && Environment.TickCount64 < until + 20000) System.Threading.Thread.Sleep(10);
        unsafe { fixed (float* f = buf) for (int b = 0; b < (int)(double.Parse(Opt(args, "--seconds") ?? "3") * 48000 / 512); b++) { plugin.NewTick(); plugin.GenRender(f, 512); if (b % 4 == 0) Win32Host.Pump(8); } }
        Win32Host.Pump(300);
        if (Opt(args, "--open") is { } open)
        {
            int id = open switch { "chip" => 4, "game" => 7, "channel" => 9, "emulator" => 1, "inputs" => 80, "instrument" => 90, "duty" => 91, _ => throw new ArgumentException("--open chip|game|channel|emulator|inputs|instrument|duty") };
            long r = plugin.Test(30, id);
            Win32Host.Click(plugin.EditorHandle, (int)(r & 0xFFFF) + (int)((r >> 32) & 0xFFFF) / 2, (int)((r >> 16) & 0xFFFF) + (int)((r >> 48) & 0xFFFF) / 2);
            Win32Host.Pump(100);
        }
        Win32Host.CaptureWindow(parent, plugin.EditorHandle, 920, 670, args[2]);
        if (Opt(args, "--hold") is { } hold)
        {
            // keep the window open and the game running so a screen grab from outside can be compared with the capture
            var stop = Environment.TickCount64 + (long)(double.Parse(hold) * 1000);
            unsafe { fixed (float* f = buf) while (Environment.TickCount64 < stop) { plugin.NewTick(); plugin.GenRender(f, 512); Win32Host.Pump(8); } }
        }
        plugin.HideEditor();
        Win32Host.DestroyParent(parent);
        foreach (var e in extras) e.Destroy();
        Console.WriteLine($"wrote {args[2]}");
        return 0;
    }

    case "romtrace":
    {
        // romtrace <plugin.dll> <seconds> [--set param value --at sec] [--seq param:value,...]: per-second level and frame count of ROM mode (dev aid)
        var lib = new PluginLibrary(args[1]);
        using var plugin = lib.Create();
        plugin.Dispatcher(BrokenNes.Fruity.Fpd.SetSampleRate, 0, 48000);
        plugin.SetParam(8, 1);
        var until = Environment.TickCount64 + 20000;
        while (plugin.Test(9) != 1 && Environment.TickCount64 < until) System.Threading.Thread.Sleep(10);
        var buf = new float[1024];
        int secs = int.Parse(args[2]);
        // --set <param> <value name> --at <sec>: change a parameter by value name at that second (e.g. --set 9 LOW --at 2)
        int setParam = Opt(args, "--set") is { } sp ? int.Parse(sp) : -1;
        string? setName = setParam >= 0 ? args[Array.IndexOf(args, "--set") + 2] : null;
        int at = int.Parse(Opt(args, "--at") ?? "2");
        unsafe { fixed (float* f = buf)
            for (int q = 0; q < secs * 4; q++)
            {
                if (Opt(args, "--seq") is { } seq && q >= 4 && q % 4 == 0 && (q / 4 - 1) < seq.Split(',').Length)
                {
                    var (pp, nn) = (int.Parse(seq.Split(',')[q / 4 - 1].Split(':')[0]), seq.Split(',')[q / 4 - 1].Split(':')[1]);
                    for (int i = 0; ; i++) { var nm = plugin.GetName(BrokenNes.Fruity.Fpn.ParamValue, pp, i); if (nm is "?" or "") break; if (nm == nn) { plugin.SetParam(pp, i); Console.WriteLine($"--- set {pp} = {nn}"); break; } }
                }
                if (setParam >= 0 && q == at * 4)
                    for (int i = 0; ; i++) { var nm = plugin.GetName(BrokenNes.Fruity.Fpn.ParamValue, setParam, i); if (nm is "?" or "") break; if (nm == setName) { plugin.SetParam(setParam, i); Console.WriteLine($"--- set {setParam} = {setName}"); break; } }
                double sum = 0; int n = 0;
                for (int b = 0; b < 48000 / 4 / 512 + 1; b++) { plugin.NewTick(); plugin.GenRender(f, 512); for (int i = 0; i < 1024; i++) { sum += buf[i] * buf[i]; n++; } }
                Console.WriteLine($"{(q + 1) * 0.25,6:0.00} s  rms {Math.Sqrt(sum / n):0.0000}  frames {plugin.Test(11)}  crashed {plugin.Test(12)}  console {plugin.Test(15)}");
            } }
        return 0;
    }

    case "makerom":
    {
        // makerom gb|gbtone|sfc <out>: the tiny ROMs the tests use (a Game Boy / SNES game to try ROM mode with; gbtone plays a tone)
        File.WriteAllBytes(args[2], args[1] == "gb" ? SelfTest.MakeGbRom() : args[1] == "gbtone" ? SelfTest.MakeGbRom(tone: true) : SelfTest.MakeSnesRom());
        Console.WriteLine($"wrote {args[2]}");
        return 0;
    }

    case "list":
        foreach (var n in Fixtures.Names) Console.WriteLine(n);
        return 0;

    case "fixture":
    {
        var s = Fixtures.ByName(args[1]) ?? throw new ArgumentException($"no built-in fixture '{args[1]}'");
        s.Save(args[2]);
        Console.WriteLine($"wrote {args[2]} ({s.Notes.Count} notes, {s.LengthBeats:0.##} beats at {s.Tempo} bpm)");
        return 0;
    }

    case "selftest":
    {
        if (args.Length < 2 || !File.Exists(args[1])) { Console.Error.WriteLine($"plugin DLL not found: {(args.Length > 1 ? args[1] : "(none)")}"); return 2; }
        var suite = new SelfTest(args[1], Opt(args, "--rom") ?? Environment.GetEnvironmentVariable("BROKENNES_TEST_ROM"));
        Console.WriteLine($"BrokenNes.FruityHost selftest: {Path.GetFullPath(args[1])}");
        var results = suite.Run(Opt(args, "--only"), r =>
            Console.WriteLine($"  {(r.Outcome == Outcome.Pass ? "PASS" : r.Outcome == Outcome.Warn ? "WARN" : "FAIL")}  {r.Id,-12} {r.Name}  [{r.Ms / 1000:0.0}s]" +
                              (r.Detail.Length > 0 ? $"\n        {r.Detail}" : "")));
        int fail = results.Count(r => r.Outcome == Outcome.Fail), warn = results.Count(r => r.Outcome == Outcome.Warn);
        Console.WriteLine($"{results.Count - fail - warn} passed, {warn} warnings, {fail} failed");
        if (Opt(args, "--json") is { } jp)
            File.WriteAllText(jp, JsonSerializer.Serialize(new { plugin = Path.GetFullPath(args[1]), when = DateTime.Now, passed = results.Count - fail - warn, warnings = warn, failed = fail, results },
                new JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
        if (Opt(args, "--md") is { } mp)
        {
            var sb = new StringBuilder($"# BrokenNes2 self-test\n\n`{Path.GetFileName(args[1])}`, {DateTime.Now:yyyy-MM-dd HH:mm}: **{results.Count - fail - warn} passed, {warn} warnings, {fail} failed**\n\n| | Test | Detail |\n|---|---|---|\n");
            foreach (var r in results) sb.AppendLine($"| {r.Outcome} | {r.Name} | {r.Detail.Replace("|", "/")} |");
            File.WriteAllText(mp, sb.ToString());
        }
        return fail == 0 ? 0 : 1;
    }

    case "render":
    {
        var lib = new PluginLibrary(args[1]);
        var scenario = File.Exists(args[2]) ? Scenario.Load(args[2]) : Fixtures.ByName(args[2]) ?? throw new ArgumentException($"no fixture '{args[2]}'");
        int rate = int.Parse(Opt(args, "--rate") ?? "44100"), block = int.Parse(Opt(args, "--block") ?? "256");
        using var plugin = lib.Create();
        if (Opt(args, "--chip") is { } chip)
            for (int i = 0; ; i++) { var n = plugin.GetName(BrokenNes.Fruity.Fpn.ParamValue, 0, i); if (n is "?" or "") throw new ArgumentException($"no chip {chip}"); if (n == chip) { plugin.SetParam(0, i); break; } }
        var mixer = new Mixer { SampleRate = rate, BlockSize = block };
        var audio = mixer.Run(plugin, scenario);
        Audio.WriteWav(args[3], audio, 2, rate);
        Console.WriteLine($"wrote {args[3]}: {audio.Length / 2 / (double)rate:0.0} s at {rate} Hz");
        return 0;
    }

    case "analyze":
    {
        var (data, ch, rate) = Audio.ReadWav(args[1]);
        var scenario = Scenario.Load(args[2]);
        var report = Certify.Analyze(Audio.Mono(data, ch), rate, scenario, double.Parse(Opt(args, "--offset-ms") ?? "0"));
        Console.WriteLine(report.Text);
        if (Opt(args, "--json") is { } jp) File.WriteAllText(jp, JsonSerializer.Serialize(report.Rows, new JsonSerializerOptions { WriteIndented = true }));
        return report.Failed ? 1 : 0;
    }
}

Console.Error.WriteLine($"unknown command '{args[0]}'");
return 2;
