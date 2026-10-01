// BrokenNes.FruityHost: the plugin test bench.
//
//   selftest <plugin.dll> [--only id,id] [--json report.json] [--md report.md]   run the certification suite
//   render   <plugin.dll> <fixture.json|name> <out.wav> [--rate 44100] [--block 256] [--chip FIX]
//   fixture  <name> <out.json>                                                    write a built-in fixture (for the FL project generator)
//   analyze  <audio.wav> <fixture.json> [--json out.json] [--offset-ms 0]         certify audio rendered by FL Studio against a fixture
//   track    <audio.wav> <fromMs> <toMs> [--ref 440] [--window 40] [--hop 10]    print the pitch track (Hz and cents from --ref)
//   regtrace <plugin.dll> <fixture.json> <channel 0-3> [--fromMs a --toMs b]      print every change of a channel's period register (dev aid)
//   state    <plugin.dll> <out.bin>                                                  write the plugin's default saved-state blob (for the FL project generator)
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
        File.WriteAllBytes(args[2], plugin.SaveState());
        Console.WriteLine($"wrote {args[2]} ({new FileInfo(args[2]).Length} bytes)");
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
        var suite = new SelfTest(args[1]);
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
