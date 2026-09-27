using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NesEmulator;

namespace BrokenNes.Workshop.MixLab;

/// <summary>
/// MIX LAB sweep: runs every requested (game, CPU, PPU, IRQ mode, front PPU) combination for N frames and classifies it.
///   --mixlab sweep --roms a.nes,b.nes --cpus FIX,SNES --ppus FIX,LQ,SNES --latch 0,1 --fronts FIX --frames 900 --out-dir D
/// Classes: refused (core not applied) | crashed | dead (every sample one colour) | frozen (never changes after the
/// first sample) | alive. The last frame of every run is saved as a PNG thumbnail; results go to sweep.csv.
/// </summary>
internal static class MixSweep
{
    public static int Run(Func<string, string, string> opt)
    {
        var roms = opt("roms", "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        var cpus = opt("cpus", "FIX").Split(',', StringSplitOptions.RemoveEmptyEntries);
        var ppus = opt("ppus", "FIX").Split(',', StringSplitOptions.RemoveEmptyEntries);
        var latches = opt("latch", "0").Split(',', StringSplitOptions.RemoveEmptyEntries);
        var fronts = opt("fronts", "FIX").Split(',', StringSplitOptions.RemoveEmptyEntries);
        int frames = int.Parse(opt("frames", "900"));
        string outDir = opt("out-dir", "."); Directory.CreateDirectory(outDir);
        string input = opt("input", "120:Start,126:,300:Start,306:,500:Start,506:,700:A,706:,800:Right,900:");
        var script = new Dictionary<int, bool[]>();
        string[] names = { "A", "B", "SELECT", "START", "UP", "DOWN", "LEFT", "RIGHT" };
        foreach (var step in input.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = step.Split(':'); var h = new bool[8];
            foreach (var b in p[1].Split('+', StringSplitOptions.RemoveEmptyEntries)) h[Array.IndexOf(names, b.ToUpperInvariant())] = true;
            script[int.Parse(p[0])] = h;
        }
        var csv = new StringBuilder("game,cpu,ppu,latch,front,class,colors,changes,crash,detail\n");
        foreach (var rom in roms)
        foreach (var cpu in cpus)
        foreach (var ppu in ppus)
        foreach (var latch in (cpu == "SNES" ? latches : new[] { "-" }))
        foreach (var front in (ppu == "SNES" ? fronts : new[] { "-" }))
        {
            string game = Path.GetFileNameWithoutExtension(rom);
            CPU_SNES.LatchIrq = latch == "1";
            if (front != "-") MixConfig.NesFrontPpu = front;
            string cls, detail = ""; int colors = 0, changes = 0; string crash = "";
            try
            {
                var nes = new NES { RomName = Path.GetFileName(rom) };
                nes.LoadROM(File.ReadAllBytes(rom));
                bool ok = nes.SetCpuCore(cpu) && nes.GetCpuCoreId().EndsWith("_" + cpu, StringComparison.OrdinalIgnoreCase)
                        & nes.SetPpuCore(ppu) && nes.GetPpuCoreId().EndsWith("_" + ppu, StringComparison.OrdinalIgnoreCase);
                if (!ok) { cls = "refused"; detail = $"cpu={nes.GetCpuCoreId()} ppu={nes.GetPpuCoreId()}"; }
                else
                {
                    var held = new bool[8]; ulong lastHash = 0; bool first = true; var seen = new HashSet<ulong>();
                    int sampleEvery = Math.Max(1, frames / 20);
                    byte[] fb = Array.Empty<byte>();
                    for (int f = 0; f < frames; f++)
                    {
                        if (script.TryGetValue(f, out var h)) held = h;
                        nes.SetInputs(held, null);
                        nes.RunFrame();
                        if (nes.IsCrashed()) { crash = $"f{f}: {nes.GetCrashInfo()}".Replace(',', ';').Replace('\n', ' '); break; }
                        if (f % sampleEvery == sampleEvery - 1 && f >= frames / 4)
                        {
                            fb = nes.GetFrameBuffer();
                            ulong hsh = Hash(fb); seen.Add(hsh);
                            if (!first && hsh != lastHash) changes++;
                            first = false; lastHash = hsh;
                            colors = Math.Max(colors, Colors(fb));
                        }
                    }
                    fb = nes.GetFrameBuffer();
                    MixLabCli.SaveRgba(fb, 256, 240, Path.Combine(outDir, $"{game}__{cpu}{(latch == "1" ? "L" : "")}__{ppu}{(front != "-" ? "-" + front : "")}.png"));
                    cls = crash != "" ? "crashed" : colors <= 1 ? "dead" : changes == 0 ? "frozen" : "alive";
                }
            }
            catch (Exception ex) { cls = "exception"; detail = ex.GetType().Name + ": " + ex.Message.Replace(',', ';').Replace('\n', ' '); }
            csv.Append($"{game},{cpu},{ppu},{latch},{front},{cls},{colors},{changes},{crash},{detail}\n");
            Console.WriteLine($"{game,-11} cpu={cpu}{(latch == "1" ? "(latch)" : "")} ppu={ppu}{(front != "-" ? "/" + front : "")} -> {cls} colors={colors} changes={changes} {crash}{detail}");
        }
        File.WriteAllText(Path.Combine(outDir, "sweep.csv"), csv.ToString());
        return 0;
    }

    private static ulong Hash(byte[] b) { ulong h = 14695981039346656037; foreach (var x in b) { h ^= x; h *= 1099511628211; } return h; }
    private static int Colors(byte[] fb) { var s = new HashSet<int>(); for (int i = 0; i + 3 < fb.Length; i += 4) { s.Add(fb[i] | fb[i + 1] << 8 | fb[i + 2] << 16); if (s.Count > 64) break; } return s.Count; }
}
