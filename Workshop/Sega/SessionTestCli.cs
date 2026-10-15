using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// <c>--session-test --rom X [--console snes|gb|gbc|sms|gg|md] [--cpu id] [--ppu id] [--apu id] [--frames N] [--input "f:Buttons,..."] [--swap "f:slot:id,..."] [--region auto|ntsc|pal] [--genesis-model auto|1|2|3] [--sms-model auto|markiii|sms1|sms2] [--json]</c>:
/// the console counterpart of <c>--romtest</c> for the UAT suite (<c>UAT/run-suite.ps1</c>, case kind <c>session</c>): one ROM on one console session built the way the apps build it (<see cref="ConsoleSessions.Create"/>),
/// run for a fixed number of frames with a scripted pad, then the picture's hash, the sound's hash and level, the number of colours seen and whether anything crashed. Everything is deterministic, so a golden can be recorded
/// and compared: it proves "nothing moved", not "this is right" (see <c>UAT/README.md</c>). A swap (<c>--swap "200:apu:NES:FIX"</c>) changes a core in the middle of the run through <c>TrySwapCore</c> and the result says whether it
/// was done in place. Exit code: 0 clean, 1 the session threw, 2 usage or IO, 4 the console could not be built (a refused core id, an unknown ROM).
/// </summary>
internal static class SessionTestCli
{
    public static int Run(string[] args)
    {
        RomTestCli.EnsureConsole();
        string Opt(string n, string d) { int i = Array.FindIndex(args, a => a.Equals("--" + n, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : d; }
        bool json = args.Contains("--json", StringComparer.OrdinalIgnoreCase);
        string romPath = Opt("rom", "");
        if (romPath == "" || !File.Exists(romPath)) { Console.Error.WriteLine($"ROM not found: {romPath}"); return 2; }
        int frames = int.Parse(Opt("frames", "600"));
        var script = PictureBridgeCli.ParseInput(Opt("input", ""));
        var swaps = Opt("swap", "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Split(':', 3)).Select(p => (frame: int.Parse(p[0]), spec: p[1], id: p[2])).ToList();
        var result = new Dictionary<string, object?>();
        int exit = 0;
        try
        {
            byte[] rom = RomDetect.Unwrap(File.ReadAllBytes(romPath), romPath, out string inner);
            var console = Opt("console", "") is { Length: > 0 } c ? Consoles.FromKey(c) : RomDetect.Detect(rom, inner) ?? throw new InvalidDataException("Unknown ROM type");
            string cpu = Opt("cpu", ""), ppu = Opt("ppu", ""), apu = Opt("apu", "");
            cpu = cpu == "" ? CoreCatalog.Default(console, CoreSlot.Cpu) : cpu; ppu = ppu == "" ? CoreCatalog.Default(console, CoreSlot.Ppu) : ppu; apu = apu == "" ? CoreCatalog.Default(console, CoreSlot.Apu) : apu;
            // an id the console's menu does not list is refused, as the apps refuse it (they would fall back to the default: a rescue the suite does not make)
            foreach (var (slot, id) in new[] { (CoreSlot.Cpu, cpu), (CoreSlot.Ppu, ppu), (CoreSlot.Apu, apu) })
                if (!CoreCatalog.Options(console, slot).Any(o => o.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException($"the {Consoles.DisplayName(console)} {slot} menu does not offer {id}");
            var sega = new NesEmulator.Sega.SegaOptions(NesEmulator.Sega.SegaOptions.RegionFromKey(Opt("region", "auto")), NesEmulator.Sega.SegaOptions.GenesisModelFromKey(Opt("genesis-model", "auto")), NesEmulator.Sega.SegaOptions.MasterSystemModelFromKey(Opt("sms-model", "auto")));
            using var session = ConsoleSessions.Create(console, rom, cpu, ppu, apu, null, inner, sega);
            ulong picHash = 0; ulong audioHash = 14695981039346656037UL; double sumSq = 0, sum = 0; long n = 0;
            var colours = new HashSet<uint>(); var buf = new short[65536];
            var held = PadButtons.None; var swapLog = new List<string>(); int ran = 0;
            for (int f = 1; f <= frames; f++)
            {
                foreach (var sw in swaps.Where(s => s.frame == f - 1))
                {
                    var slot = sw.spec.ToLowerInvariant() switch { "cpu" => CoreSlot.Cpu, "ppu" => CoreSlot.Ppu, _ => CoreSlot.Apu };
                    bool ok = session.TrySwapCore(slot, sw.id);
                    swapLog.Add($"{sw.frame}:{sw.spec}:{sw.id}={(ok ? "in place" : "refused: " + session.SwapRefusal(slot, sw.id))}");
                }
                if (script.TryGetValue(f - 1, out var h)) held = h;
                session.SetPad(0, held);
                session.RunFrame(); ran = f;
                int got;
                while ((got = session.ReadSamples(buf)) > 0)
                    for (int i = 0; i < got; i++)
                    {
                        audioHash = (audioHash ^ (ushort)buf[i]) * 1099511628211UL;
                        if ((i & 1) == 0 && i + 1 < got) { double x = (buf[i] + buf[i + 1]) / 65536.0; sum += x; sumSq += x * x; n++; }
                    }
                if (f % 10 == 0 || f == frames)
                {
                    ulong ph = 14695981039346656037UL; int len = session.FrameWidth * session.FrameHeight; var fr = session.Frame;
                    for (int i = 0; i < len; i++) { ph = (ph ^ fr[i]) * 1099511628211UL; if (colours.Count <= 4096) colours.Add(fr[i] & 0xFFFFFF); }
                    picHash = ph;
                }
            }
            double mean = n > 0 ? sum / n : 0, rms = n > 0 ? Math.Sqrt(Math.Max(0, sumSq / n - mean * mean)) : 0;
            result["Console"] = Consoles.Key(console); result["Title"] = session.Title; result["Description"] = session.Description;
            result["FramesRun"] = ran; result["FrameBufferHash"] = picHash.ToString("X16"); result["AudioHash"] = audioHash.ToString("X16"); result["AudioRms"] = Math.Round(rms, 5);
            result["DistinctColors"] = colours.Count; result["Width"] = session.FrameWidth; result["Height"] = session.FrameHeight;
            result["Swaps"] = string.Join(" ", swapLog); result["Result"] = "ok"; result["CrashInfo"] = "";
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or InvalidDataException)
        {
            result["Result"] = "refused"; result["CrashInfo"] = ex.GetType().Name + ": " + ex.Message; exit = 4;
        }
        catch (Exception ex)
        {
            result["Result"] = "crashed"; result["CrashInfo"] = ex.GetType().Name + ": " + ex.Message; exit = 1;
        }
        if (json) Console.WriteLine(JsonSerializer.Serialize(result));
        else foreach (var kv in result) Console.WriteLine($"{kv.Key}: {kv.Value}");
        return exit;
    }
}
