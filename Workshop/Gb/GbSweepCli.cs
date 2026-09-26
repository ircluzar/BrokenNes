using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NesEmulator.Gb;

namespace BrokenNes.Workshop.Gb;

/// <summary>
/// Breadth check over a ROM folder: `--gbsweep --dir X:\EMULATION\GameBoy [--frames 300] [--model dmg|cgb] [--out sweep.csv] [--png-dir dir]`.
/// Each ROM runs headless with a few Start/A presses; the CSV records how far it got:
///   unsupported (cartridge type), exception, locked (CPU hit an illegal opcode), blank (single-colour screen at the end),
///   silent (no audio), alive (picture and sound).
/// </summary>
internal static class GbSweepCli
{
    public static int Run(string[] args)
    {
        string Opt(string name, string def)
        {
            int i = Array.FindIndex(args, a => a.Equals("--" + name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : def;
        }
        string dir = Opt("dir", ".");
        int frames = int.Parse(Opt("frames", "300"));
        var model = Opt("model", "dmg").Equals("cgb", StringComparison.OrdinalIgnoreCase) ? GbModel.Cgb : GbModel.Dmg;
        string outCsv = Opt("out", "gbsweep.csv"), pngDir = Opt("png-dir", "");
        if (pngDir != "") Directory.CreateDirectory(pngDir);
        var script = GbRunCli.ParseInput($"{frames / 3}:Start,{frames / 3 + 6}:,{frames / 2}:A,{frames / 2 + 6}:,{frames * 2 / 3}:Start,{frames * 2 / 3 + 6}:");
        var files = Directory.GetFiles(dir).Where(f => f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".gb", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".gbc", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f).ToList();
        var rows = new List<string> { "file,title,mapper,result,detail,ms_per_frame" };
        var counts = new Dictionary<string, int>();
        var buf = new short[16384];
        int idx = 0;
        foreach (var f in files)
        {
            idx++;
            string name = Path.GetFileName(f), title = "", mapper = "", result, detail = "";
            double msPerFrame = 0;
            try
            {
                var rom = GbRunCli.LoadRom(f);
                GbCartridge cart;
                try { cart = GbCartridge.Load(rom); }
                catch (NotSupportedException ex) { result = "unsupported"; detail = ex.Message; goto done; }
                title = cart.Title; mapper = cart.MapperName;
                var board = new BOARD_GB(cart, model);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                double energy = 0;
                for (int fr = 0; fr < frames; fr++)
                {
                    if (script.TryGetValue(fr, out var b)) { board.Buttons = b; board.UpdateJoypadIrq(); }
                    board.RunFrame();
                    int n; while ((n = board.Apu.ReadSamples(buf)) > 0) for (int i = 0; i < n; i++) energy += Math.Abs((double)buf[i]);
                    if (board.Cpu.Locked) break;
                }
                msPerFrame = sw.Elapsed.TotalMilliseconds / frames;
                var fb = board.Ppu.FrameBuffer;
                int colours = fb.Distinct().Take(3).Count();
                if (board.Cpu.Locked) { result = "locked"; detail = $"illegal opcode near ${board.Cpu.PC:X4}"; }
                else if (colours < 2) result = "blank";
                else if (energy < 1000) result = "silent";
                else result = "alive";
                if (pngDir != "") GbTestCli.SaveFrame(board, Path.Combine(pngDir, $"{idx:D3}.png"));
            }
            catch (Exception ex) { result = "exception"; detail = ex.GetType().Name + ": " + ex.Message; }
        done:
            counts[result] = counts.GetValueOrDefault(result) + 1;
            rows.Add($"\"{name}\",\"{title}\",{mapper},{result},\"{detail.Replace("\"", "'")}\",{msPerFrame:F2}");
            if (result != "alive") Console.WriteLine($"{idx,4} {result,-11} {name}  {detail}");
        }
        File.WriteAllLines(outCsv, rows);
        Console.WriteLine(string.Join("  ", counts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}")) + $"  (of {files.Count})");
        return 0;
    }
}
