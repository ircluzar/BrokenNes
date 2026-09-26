using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NesEmulator.Gb;

namespace BrokenNes.Workshop.Gb;

/// <summary>
/// Game Boy test-ROM verifier.
///   --gbtest [--suite id[,id]] [--out results-dir] [--verbose]      run the manifest (Workshop/Gb/gb-test-manifest.json)
///   --gbtest --rom file.gb [--kind blargg|mooneye|image] [--model dmg|cgb] [--seconds n] [--png out.png] [--expect ref.png]
/// Pass rules: Blargg = "Passed" on the serial port or the $A000 text protocol; Mooneye = LD B,B with
/// B,C,D,E,H,L = 3,5,8,13,21,34 (fail = all $42); image = the frame matches the reference screenshot.
/// </summary>
internal static class GbTestCli
{
    public static int Run(string[] args)
    {
        string Opt(string name, string def)
        {
            int i = Array.FindIndex(args, a => a.Equals("--" + name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : def;
        }
        bool verbose = args.Any(a => a.Equals("--verbose", StringComparison.OrdinalIgnoreCase));

        string rom = Opt("rom", "");
        if (rom != "")
        {
            var r = RunOne(rom, Opt("kind", GuessKind(rom)), ParseModel(Opt("model", rom.EndsWith(".gbc") ? "cgb" : "dmg")),
                double.Parse(Opt("seconds", "30")), Opt("expect", ""), Opt("png", ""));
            Console.WriteLine($"{(r.Pass ? "PASS" : "FAIL")}  {Path.GetFileName(rom)}  {r.Detail}");
            return r.Pass ? 0 : 1;
        }

        string repo = FindRepoRoot();
        string manifestPath = Path.Combine(repo, "Workshop", "Gb", "gb-test-manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        string root = Path.Combine(repo, manifest["root"]!.GetValue<string>());
        var only = Opt("suite", "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string outDir = Opt("out", "");
        if (outDir != "") Directory.CreateDirectory(outDir);

        int totalPass = 0, total = 0;
        foreach (var s in manifest["suites"]!.AsArray())
        {
            string id = s!["id"]!.GetValue<string>();
            if (only.Count > 0 && !only.Contains(id)) continue;
            string kind = s["kind"]!.GetValue<string>();
            string modelName = s["model"]?.GetValue<string>() ?? "";
            double seconds = s["seconds"]!.GetValue<double>();
            var images = s["images"] as JsonObject;
            var items = new JsonArray();
            int pass = 0, n = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            foreach (var romNode in s["roms"]!.AsArray())
            {
                string rel = romNode!.GetValue<string>();
                string path = Path.Combine(root, rel);
                var model = ParseModel(modelName != "" ? modelName : rel.EndsWith(".gbc") ? "cgb" : "dmg");
                string? imgRel = images?[rel]?.GetValue<string>();
                string expect = imgRel != null ? Path.Combine(root, imgRel) : ExpectedImageFor(path, model);
                Result r;
                try { r = RunOne(path, imgRel != null ? "image" : kind, model, seconds, expect, ""); }
                catch (Exception ex) { r = new Result(false, "exception: " + ex.GetType().Name + ": " + ex.Message); }
                n++; if (r.Pass) pass++;
                string name = Path.GetFileNameWithoutExtension(rel);
                items.Add(new JsonObject { ["n"] = name + (r.Pass ? "" : " - " + Trim(r.Detail, 90)), ["s"] = r.Pass ? "p" : "f" });
                if (verbose || !r.Pass) Console.WriteLine($"  {(r.Pass ? "pass" : "FAIL")}  {rel}  {Trim(r.Detail, 160)}");
            }
            totalPass += pass; total += n;
            Console.WriteLine($"{id,-18} {pass,3}/{n,-3} ({sw.Elapsed.TotalSeconds:F1}s)");
            if (outDir != "")
            {
                var doc = new JsonObject
                {
                    ["name"] = s["name"]!.GetValue<string>(), ["group"] = s["group"]!.GetValue<string>(), ["order"] = s["order"]!.GetValue<int>(),
                    ["note"] = $"{pass}/{n} passing", ["items"] = items,
                };
                File.WriteAllText(Path.Combine(outDir, id + ".json"), doc.ToJsonString());
            }
        }
        Console.WriteLine($"TOTAL {totalPass}/{total}");
        return 0;
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    private static GbModel ParseModel(string s) => s.Equals("cgb", StringComparison.OrdinalIgnoreCase) ? GbModel.Cgb : GbModel.Dmg;

    private static string GuessKind(string rom)
    {
        string p = rom.Replace('\\', '/').ToLowerInvariant();
        if (p.Contains("mooneye")) return "mooneye";
        if (p.Contains("acid") || p.Contains("mealybug")) return "image";
        return "blargg";
    }

    private static string ExpectedImageFor(string romPath, GbModel model)
    {
        string dir = Path.GetDirectoryName(romPath)!, stem = Path.GetFileNameWithoutExtension(romPath);
        string[] candidates = model == GbModel.Cgb
            ? new[] { stem + "_cgb_c.png", stem + "-cgb.png", stem + ".png" }
            : new[] { stem + "_dmg_blob.png", stem + "-dmg.png", stem + "_dmg_b.png", stem + ".png" };
        foreach (var c in candidates) if (File.Exists(Path.Combine(dir, c))) return Path.Combine(dir, c);
        return "";
    }

    internal static string FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "BrokenNes.sln"))) d = d.Parent;
        return d?.FullName ?? Directory.GetCurrentDirectory();
    }

    internal readonly record struct Result(bool Pass, string Detail);

    internal static Result RunOne(string romPath, string kind, GbModel model, double seconds, string expectPng, string pngOut)
    {
        var board = new BOARD_GB(GbCartridge.Load(File.ReadAllBytes(romPath)), model);
        int frames = (int)(seconds * 60);
        Result? verdict = null;

        if (kind == "mooneye")
        {
            board.InstructionHook = pc =>
            {
                if (verdict != null || board.Peek(pc) != 0x40) return;
                var c = board.Cpu;
                if (c.B == 3 && c.C == 5 && c.D == 8 && c.E == 13 && c.H == 21 && c.L == 34) verdict = new Result(true, "fibonacci registers");
                else if (c.B == 0x42 && c.C == 0x42 && c.D == 0x42 && c.E == 0x42 && c.H == 0x42 && c.L == 0x42) verdict = new Result(false, "test reported failure ($42 registers)");
            };
        }

        int f;
        for (f = 0; f < frames && verdict == null; f++)
        {
            board.RunFrame();
            if (board.Cpu.Locked) { verdict = new Result(false, $"CPU locked up at ${board.Cpu.PC:X4}"); break; }
            if (kind == "blargg") verdict = BlarggVerdict(board, final: false);
        }

        if (verdict == null)
        {
            if (kind == "blargg") verdict = BlarggVerdict(board, final: true);
            else if (kind == "image") verdict = CompareImage(board, expectPng);
            else verdict = new Result(false, $"no verdict after {seconds:F0}s (PC=${board.Cpu.PC:X4})");
        }
        if (pngOut != "") SaveFrame(board, pngOut);
        return verdict.Value;
    }

    private static Result? BlarggVerdict(BOARD_GB board, bool final)
    {
        string serial = board.SerialOut.ToString();
        if (serial.Contains("Passed")) return new Result(true, OneLine(serial));
        if (serial.Contains("Failed")) return new Result(false, OneLine(serial));
        // $A000 protocol: $A001-$A003 = DE B0 61, $A000 = $80 while running, else the result code; text from $A004.
        if (board.Peek(0xA001) == 0xDE && board.Peek(0xA002) == 0xB0 && board.Peek(0xA003) == 0x61)
        {
            byte status = board.Peek(0xA000);
            if (status != 0x80)
            {
                var sb = new StringBuilder();
                for (ushort a = 0xA004; a < 0xB000; a++) { byte c = board.Peek(a); if (c == 0) break; sb.Append((char)c); }
                return new Result(status == 0, $"code {status}: {OneLine(sb.ToString())}");
            }
        }
        return final ? new Result(false, "no result: " + (serial.Length > 0 ? OneLine(serial) : "(no serial output)")) : null;
    }

    private static string OneLine(string s) => string.Join(" | ", s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static Result CompareImage(BOARD_GB board, string expectPng)
    {
        if (expectPng == "" || !File.Exists(expectPng)) return new Result(false, "no reference image");
        using var bmp = new Bitmap(expectPng);
        if (bmp.Width != PPU_GB.Width || bmp.Height != PPU_GB.Height) return new Result(false, $"reference is {bmp.Width}x{bmp.Height}");
        bool cgb = board.CgbMode;
        int bad = 0, firstX = -1, firstY = -1;
        for (int y = 0; y < PPU_GB.Height; y++)
            for (int x = 0; x < PPU_GB.Width; x++)
            {
                var e = bmp.GetPixel(x, y);
                bool match;
                if (cgb)
                {
                    uint g = board.Ppu.FrameBuffer[y * PPU_GB.Width + x];
                    int r = (int)(g >> 16 & 0xFF), gg = (int)(g >> 8 & 0xFF), b = (int)(g & 0xFF);
                    match = Math.Abs(r - e.R) <= 8 && Math.Abs(gg - e.G) <= 8 && Math.Abs(b - e.B) <= 8;
                }
                else
                {
                    int expectShade = 3 - (int)Math.Round((e.R * 0.3 + e.G * 0.59 + e.B * 0.11) / 255.0 * 3);
                    match = board.Ppu.ShadeBuffer[y * PPU_GB.Width + x] == expectShade;
                }
                if (!match) { if (bad++ == 0) { firstX = x; firstY = y; } }
            }
        return bad == 0 ? new Result(true, "frame matches") : new Result(false, $"{bad} pixels differ (first at {firstX},{firstY})");
    }

    internal static void SaveFrame(BOARD_GB board, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var bmp = new Bitmap(PPU_GB.Width, PPU_GB.Height, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, PPU_GB.Width, PPU_GB.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        var px = board.Ppu.FrameBuffer.Select(u => unchecked((int)u)).ToArray();
        System.Runtime.InteropServices.Marshal.Copy(px, 0, data.Scan0, px.Length);
        bmp.UnlockBits(data);
        bmp.Save(path, ImageFormat.Png);
    }
}
