using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using NesEmulator.Gb;

namespace BrokenNes.Workshop.Gb;

/// <summary>
/// Headless Game Boy runs for the compatibility climb.
///   --gbrun --rom game.gb|game.zip [--model dmg|cgb] [--frames N] [--png-at f1,f2,...] [--out dir] [--tag name]
///           [--input "f:Btn+Btn,f:,..."] [--wav out.wav] [--sav file] [--sav-out file] [--blank-sram 00] [--hash]
/// Buttons: Right Left Up Down A B Select Start. "--input 120:Start,126:" holds Start on frames 120-125.
/// </summary>
internal static class GbRunCli
{
    public static int Run(string[] args)
    {
        string Opt(string name, string def)
        {
            int i = Array.FindIndex(args, a => a.Equals("--" + name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : def;
        }
        string rom = Opt("rom", "");
        var model = Opt("model", "dmg").Equals("cgb", StringComparison.OrdinalIgnoreCase) ? GbModel.Cgb : GbModel.Dmg;
        int frames = int.Parse(Opt("frames", "600"));
        var shots = Opt("png-at", "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToHashSet();
        string outDir = Opt("out", "."), tag = Opt("tag", Path.GetFileNameWithoutExtension(rom));
        var script = ParseInput(Opt("input", ""));
        string wav = Opt("wav", ""), sav = Opt("sav", "");
        bool hash = args.Contains("--hash");
        // --reglog FF47,C000:C0FF@1200-1500 : print those addresses (Peek) after every frame in the range
        var reglog = Opt("reglog", "");
        ushort[] logAddrs = Array.Empty<ushort>(); int logFrom = 0, logTo = -1;
        if (reglog != "")
        {
            var parts = reglog.Split("@");
            logAddrs = parts[0].Split(",").SelectMany(a => a.Contains(":")
                ? Enumerable.Range(Convert.ToUInt16(a.Split(":")[0], 16), Convert.ToUInt16(a.Split(":")[1], 16) - Convert.ToUInt16(a.Split(":")[0], 16) + 1).Select(v => (ushort)v)
                : new[] { Convert.ToUInt16(a, 16) }).ToArray();
            var range = parts.Length > 1 ? parts[1].Split("-") : new[] { "0", "999999" };
            logFrom = int.Parse(range[0]); logTo = int.Parse(range[1]);
        }

        var board = new BOARD_GB(GbCartridge.Load(LoadRom(rom)), model);
        // --blank-sram XX: start from cartridge RAM filled with XX (hardware powers up with garbage; Mesen uses 00)
        string blank = Opt("blank-sram", "");
        if (blank != "") System.Array.Fill(board.Cart.Ram, (byte)(Convert.ToByte(blank, 16) | (board.Cart.MapperName == "MBC2" ? 0xF0 : 0)));
        if (sav != "" && File.Exists(sav)) board.ImportSave(File.ReadAllBytes(sav));
        var samples = new List<short>();
        var buf = new short[16384];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ulong h = 1469598103934665603;
        for (int f = 0; f < frames; f++)
        {
            if (script.TryGetValue(f, out var held)) { board.Buttons = held; board.UpdateJoypadIrq(); }
            board.RunFrame();
            int n; while ((n = board.Apu.ReadSamples(buf)) > 0) if (wav != "") for (int i = 0; i + 1 < n; i += 2) samples.Add((short)((buf[i] + buf[i + 1]) / 2));
            if (f + 1 >= logFrom && f + 1 <= logTo) Console.WriteLine($"{f + 1} " + string.Join(" ", logAddrs.Select(a => board.Peek(a).ToString("X2"))));
            if (hash) foreach (var p in board.Ppu.FrameBuffer) { h ^= p; h *= 1099511628211; }
            if (shots.Contains(f + 1)) GbTestCli.SaveFrame(board, Path.Combine(outDir, $"{tag}_f{f + 1}.png"));
            if (board.Cpu.Locked) { Console.WriteLine($"CPU LOCKED at frame {f + 1}, PC=${board.Cpu.PC:X4}"); break; }
        }
        if (wav != "") MixLabWav.Write(wav, samples.ToArray(), board.Apu.SampleRate);
        string savOut = Opt("sav-out", sav);
        if (savOut != "" && board.HasBattery) File.WriteAllBytes(savOut, board.ExportSave());
        var c = board.Cpu;
        Console.WriteLine($"{board.Cart.Title} [{board.Cart.MapperName}] model={model}{(board.CgbMode ? " (colour)" : board.Ppu.CompatMode ? " (compat)" : "")} frames={board.FrameCount} " +
            $"PC=${c.PC:X4} instr={c.Instructions:N0} {sw.Elapsed.TotalMilliseconds / Math.Max(1, frames):F2} ms/frame" + (hash ? $" hash={h:X16}" : ""));
        var pp = board.Ppu; Console.WriteLine($"LCDC={pp.Lcdc:X2} BGP={pp.Bgp:X2} OBP0={pp.Obp0:X2} OBP1={pp.Obp1:X2} SCX={pp.Scx} SCY={pp.Scy} WX={pp.Wx} WY={pp.Wy} LY={pp.LY}");
        if (board.SerialOut.Length > 0) Console.WriteLine("serial: " + board.SerialOut.ToString().Replace("\n", " | "));
        return 0;
    }

    /// <summary>ROM bytes from a .gb/.gbc file or the first ROM inside a .zip.</summary>
    internal static byte[] LoadRom(string path)
    {
        if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return File.ReadAllBytes(path);
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".gb", StringComparison.OrdinalIgnoreCase) || e.Name.EndsWith(".gbc", StringComparison.OrdinalIgnoreCase) || e.Name.EndsWith(".sgb", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("No .gb/.gbc inside " + path);
        using var s = entry.Open(); using var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray();
    }

    internal static Dictionary<int, GbButtons> ParseInput(string spec)
    {
        var d = new Dictionary<int, GbButtons>();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split(':');
            GbButtons b = 0;
            if (kv.Length > 1) foreach (var name in kv[1].Split('+', StringSplitOptions.RemoveEmptyEntries)) b |= Enum.Parse<GbButtons>(name, true);
            d[int.Parse(kv[0])] = b;
        }
        return d;
    }
}

internal static class MixLabWav
{
    public static void Write(string path, short[] mono, int rate)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + mono.Length * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(mono.Length * 2);
        foreach (var s in mono) w.Write(s);
    }
}
