using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using NesEmulator.Snes;

namespace BrokenNes.Workshop;

/// <summary>
/// Headless spec verifier for the SFC core family, driven by gilyon/snes-tests
/// (Windows/Resources/snes-test-roms). The ROM reports through VRAM text, so this reads the tilemap
/// directly - no renderer involved in the verdict:
///   word $032  "Success" / "Failed"        word $06E  current test number (4 hex digits)
///   word $0A1  "A = xxxx" .. $121 "S = xxxx"  (registers the failing test produced)
///   word $0A1  "Invalid test order"         (errant jump - aborts the run)
/// On a failure the ROM waits for button A; this runner presses it and keeps going, so one run
/// collects EVERY failing test instead of stopping at the first.
///
/// Usage:
///   --snestest --rom &lt;cputest-basic.sfc&gt; [--max-frames N] [--tests tests-basic.txt]
///              [--png final.png] [--json] [--out result.json]
/// --tests defaults to tests-basic.txt / tests-full.txt next to the ROM, when present, and is only
/// used to print each failing test's description and expected values.
///
/// Exit codes: 0 Success with zero failures | 1 finished with failures | 2 usage/IO error
///             3 did not finish within --max-frames | 4 aborted (invalid test order / CPU stopped)
///             5 unexpected exception
/// </summary>
internal static class SnesTestCli
{
    private const string Usage =
        "Usage: --snestest --rom <path.sfc> [--max-frames N] [--tests tests.txt] [--png out.png] [--json] [--out result.json]";

    private sealed class Failure
    {
        public int Test { get; set; }
        public string Got { get; set; } = "";
        public string? Description { get; set; }
    }

    private sealed class Report
    {
        public string Rom { get; set; } = "";
        public string Title { get; set; } = "";
        public string Verdict { get; set; } = "";
        public int LastTest { get; set; }
        public int Failures => FailureList.Count;
        public List<Failure> FailureList { get; set; } = new();
        public long Frames { get; set; }
        public long Instructions { get; set; }
        public double Seconds { get; set; }
    }

    public static int Run(string[] args)
    {
        RomTestCli.EnsureConsole();
        string? romPath = null, testsPath = null, pngPath = null, outPath = null;
        int maxFrames = 60 * 60 * 10;
        bool json = false;
        try
        {
            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--rom": romPath = args[++i]; break;
                    case "--max-frames": maxFrames = int.Parse(args[++i]); break;
                    case "--tests": testsPath = args[++i]; break;
                    case "--png": pngPath = args[++i]; break;
                    case "--out": outPath = args[++i]; break;
                    case "--json": json = true; break;
                    default: Console.Error.WriteLine($"Unknown argument: {args[i]}\n{Usage}"); return 2;
                }
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or FormatException)
        {
            Console.Error.WriteLine($"Bad arguments: {ex.Message}\n{Usage}");
            return 2;
        }
        if (romPath == null) { Console.Error.WriteLine(Usage); return 2; }

        try
        {
            byte[] file;
            try { file = File.ReadAllBytes(romPath); }
            catch (Exception ex) { Console.Error.WriteLine($"Cannot read ROM: {ex.Message}"); return 2; }

            testsPath ??= GuessTestsFile(romPath);
            var descriptions = testsPath != null && File.Exists(testsPath) ? LoadDescriptions(testsPath) : new Dictionary<int, string>();

            var board = new BOARD_SFC(SnesCartridge.Load(file));
            var report = new Report { Rom = Path.GetFileName(romPath), Title = board.Cart.Title };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int exit = 3;
            report.Verdict = "timeout";

            int pressFrames = 0;           // >0: holding A to acknowledge a failure
            int lastRecorded = -1;         // back-to-back failures can land in one frame, so dedupe by test number

            for (int frame = 0; frame < maxFrames; frame++)
            {
                board.Pads[0] = pressFrames > 0 ? (ushort)SnesButtons.A : (ushort)0;
                if (pressFrames > 0) pressFrames--;
                board.RunFrame();
                report.Frames = frame + 1;

                if (board.Cpu.Stopped) { report.Verdict = "cpu stopped (STP / errant jump)"; exit = 4; break; }

                string status = ReadText(board.Ppu, 0x32, 7);
                if (status.StartsWith("Success")) { report.Verdict = "Success"; exit = report.FailureList.Count == 0 ? 0 : 1; break; }
                if (!status.StartsWith("Failed") || pressFrames > 0) continue;

                int test = ParseHex(ReadText(board.Ppu, 0x6E, 4));
                if (test == lastRecorded) continue;
                report.LastTest = test;
                if (ReadText(board.Ppu, 0xA1, 18) == "Invalid test order") { report.Verdict = $"aborted: invalid test order at test {test:x4}"; exit = 4; break; }

                // The register dump is written after "Failed"; wait until the ROM reaches the key prompt.
                if (!ReadText(board.Ppu, 0x341, 5).StartsWith("Press")) continue;
                string got = string.Join(" ", new[] { 0xA1, 0xC1, 0xE1, 0x101, 0x121 }.Select(a => ReadText(board.Ppu, a, 8).Replace(" = ", "=")));
                report.FailureList.Add(new Failure { Test = test, Got = got, Description = descriptions.GetValueOrDefault(test) });
                pressFrames = 3;
                lastRecorded = test;
            }

            report.Seconds = Math.Round(sw.Elapsed.TotalSeconds, 2);
            report.Instructions = board.Cpu.InstructionCount;
            if (exit is 0 or 1 or 3) report.LastTest = ParseHex(ReadText(board.Ppu, 0x6E, 4));

            if (pngPath != null) SavePng(board.Ppu, pngPath);
            string jsonText = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            if (outPath != null) File.WriteAllText(outPath, jsonText);
            Console.WriteLine(json ? jsonText : Humanize(report));
            return exit;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Unexpected: {ex}");
            return 5;
        }
    }

    private static string ReadText(PPU_SFC ppu, int wordAddress, int length)
    {
        var sb = new StringBuilder(length);
        for (int i = 0; i < length; i++)
        {
            int c = ppu.ReadVramWord(wordAddress + i) & 0xFF;
            sb.Append(c is >= 0x20 and < 0x7F ? (char)c : ' ');
        }
        return sb.ToString();
    }

    private static int ParseHex(string s) => int.TryParse(s.Trim(), System.Globalization.NumberStyles.HexNumber, null, out int v) ? v : -1;

    private static string? GuessTestsFile(string romPath)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(romPath)) ?? ".";
        string name = Path.GetFileNameWithoutExtension(romPath);
        foreach (var candidate in new[] { name.Replace("cputest", "tests") + ".txt", "tests.txt" })
        {
            string p = Path.Combine(dir, candidate);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>tests-*.txt: "Test 00ab: op ..." followed by indented Input/Expected lines.</summary>
    private static Dictionary<int, string> LoadDescriptions(string path)
    {
        var map = new Dictionary<int, string>();
        int current = -1;
        var sb = new StringBuilder();
        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith("Test ") && line.Length > 9 && line[9] == ':')
            {
                if (current >= 0) map[current] = sb.ToString().TrimEnd();
                current = ParseHex(line.Substring(5, 4));
                sb.Clear().AppendLine(line.Substring(11));
            }
            else if (current >= 0 && line.Length > 0) sb.AppendLine("    " + line.Trim());
        }
        if (current >= 0) map[current] = sb.ToString().TrimEnd();
        return map;
    }

    private static string Humanize(Report r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{r.Rom} ({r.Title})");
        sb.AppendLine($"verdict: {r.Verdict}   failures: {r.Failures}   last test: {r.LastTest:x4}   frames: {r.Frames}   instructions: {r.Instructions:N0}   {r.Seconds}s");
        foreach (var f in r.FailureList)
        {
            sb.AppendLine($"  FAIL {f.Test:x4}  got {f.Got}");
            if (f.Description != null) sb.AppendLine("       " + f.Description.Replace("\n", "\n       "));
        }
        return sb.ToString().TrimEnd();
    }

    private static void SavePng(PPU_SFC ppu, string path)
    {
        using var bmp = new Bitmap(PPU_SFC.Width, PPU_SFC.Height, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.WriteOnly, bmp.PixelFormat);
        var pixels = new int[ppu.FrameBuffer.Length];
        Buffer.BlockCopy(ppu.FrameBuffer, 0, pixels, 0, pixels.Length * 4);
        for (int y = 0; y < bmp.Height; y++)
            Marshal.Copy(pixels, y * bmp.Width, data.Scan0 + y * data.Stride, bmp.Width);
        bmp.UnlockBits(data);
        bmp.Save(path, ImageFormat.Png);
    }
}
