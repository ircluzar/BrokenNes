using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NesEmulator.Snes;

namespace BrokenNes.Workshop;

/// <summary>
/// Measures the homemade DSP-1 (<see cref="DSP1_SFC"/>) against the real chip.
///
///   --dsp1-lab replay &lt;transactions.txt&gt; [--show CMD] [--max N]
///       Replays transactions recorded from the firmware-driven core ("cmd|in,in|out,out" per line,
///       from --snesrun --dsp-log + the parser) through the homemade DSP-1, in order (Parameter
///       state carries over), and reports per command and per result how many match exactly, are
///       off by one, or worse.
///   --dsp1-lab probe &lt;dsp1b.rom&gt; --cmd XX --n N --out file [--seed S]
///       Drives the real firmware (NECDSP_SFC) with random parameters for one command and records
///       the results in the same format, for commands the recorded games never use.
/// </summary>
internal static class Dsp1LabCli
{
    public static int Run(string[] args)
    {
        RomTestCli.EnsureConsole();
        if (args.Length < 3) { Console.Error.WriteLine("Usage: --dsp1-lab replay <tx.txt> [--show CMD] | probe <dsp1b.rom> --cmd XX --n N --out file"); return 2; }
        return args[1] switch
        {
            "replay" => Replay(args),
            "probe" => Probe(args),
            _ => 2,
        };
    }

    private static string? Opt(string[] args, string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

    private sealed class Stat { public long Total, Exact, Off1, Worse; public int MaxErr; }

    private static int Replay(string[] args)
    {
        string path = args[2];
        int? show = Opt(args, "--show") is { } s ? Convert.ToInt32(s, 16) : null;
        int maxShow = int.Parse(Opt(args, "--max") ?? "12");
        var dsp = new DSP1_SFC(NECDSP_SFC.Mapping.HiRom);
        var stats = new SortedDictionary<string, Stat>();
        int shown = 0;
        string lastParam = "";
        foreach (var line in File.ReadLines(path))
        {
            var parts = line.Split('|');
            byte cmd = Convert.ToByte(parts[0], 16);
            short[] inputs = parts[1].Length == 0 ? Array.Empty<short>() : parts[1].Split(',').Select(h => (short)Convert.ToUInt16(h, 16)).ToArray();
            short[] expect = parts[2].Length == 0 ? Array.Empty<short>() : parts[2].Split(',').Select(h => (short)Convert.ToUInt16(h, 16)).ToArray();
            if ((cmd & 0x80) != 0) continue;
            if ((cmd & 0x0F) == 0x02) lastParam = string.Join(' ', inputs.Select(v => v.ToString()));
            List<short> got = new();
            if ((cmd & 0x0F) == 0x0A)
            {
                // The real chip streams lines slower than SMK reads them, so its recorded stream
                // repeats lines further in; only the first four lines compare one to one.
                if (expect.Length > 16) expect = expect[..16];
                got.AddRange(dsp.Run(cmd, inputs[0]));
                for (int ln = 1; got.Count < expect.Length; ln++) got.AddRange(dsp.RunRaster((short)(inputs[0] + ln)));
            }
            else got.AddRange(dsp.Run(cmd, inputs));
            bool bad = false;
            for (int k = 0; k < expect.Length && k < got.Count; k++)
            {
                string key = $"{cmd:X2}[{((cmd & 0x0F) == 0x0A ? k % 4 : k)}]";
                if (!stats.TryGetValue(key, out var st)) stats[key] = st = new Stat();
                int err = Math.Abs(expect[k] - got[k]);
                st.Total++;
                if (err == 0) st.Exact++; else if (err == 1) st.Off1++; else { st.Worse++; bad = true; }
                st.MaxErr = Math.Max(st.MaxErr, err);
            }
            if (bad && show == cmd && shown++ < maxShow)
                Console.WriteLine($"  {cmd:X2} in [{string.Join(' ', inputs.Select(v => v.ToString()))}] expect [{string.Join(' ', expect.Take(8).Select(v => v.ToString()))}] got [{string.Join(' ', got.Take(8).Select(v => v.ToString()))}]{((cmd & 0x0F) == 0x0A ? "  after 02 [" + lastParam + "]" : "")}");
        }
        Console.WriteLine($"{"result",-8} {"total",9} {"exact",8} {"off-by-1",9} {"worse",8} {"max err",8}");
        foreach (var (k, st) in stats)
            Console.WriteLine($"{k,-8} {st.Total,9} {100.0 * st.Exact / st.Total,7:F2}% {100.0 * st.Off1 / st.Total,8:F2}% {100.0 * st.Worse / st.Total,7:F2}% {st.MaxErr,8}");
        return 0;
    }

    private static int Probe(string[] args)
    {
        byte[] fw = File.ReadAllBytes(args[2]);
        byte cmd = Convert.ToByte(Opt(args, "--cmd") ?? "00", 16);
        int n = int.Parse(Opt(args, "--n") ?? "1000");
        string outPath = Opt(args, "--out") ?? "probe.txt";
        var rng = new Random(int.Parse(Opt(args, "--seed") ?? "1"));
        int inCount = int.Parse(Opt(args, "--in") ?? "2"), outCount = int.Parse(Opt(args, "--outs") ?? "1");
        var dsp = new NECDSP_SFC(fw, NECDSP_SFC.Mapping.HiRom, "probe");
        long clock = 0;
        void Wr(byte v) { clock += 20000; dsp.Write(0, 0x6000, v, clock); }
        byte Rd() { clock += 20000; return dsp.Read(0, 0x6000, clock); }
        using var w = new StreamWriter(outPath);
        void Word(short v) { Wr((byte)v); Wr((byte)(v >> 8)); }
        short RdWord() { int lo = Rd(); return (short)(lo | Rd() << 8); }
        string Hex(IEnumerable<short> vs) => string.Join(',', vs.Select(v => ((ushort)v).ToString("X4")));
        if (cmd == 0x02)
        {
            // Camera sweep: a random Parameter, then 4 raster lines from a random start and 4 random
            // Project calls, each recorded as its own transaction (Parameter first, so replay keeps state).
            for (int t = 0; t < n; t++)
            {
                short[] prm =
                {
                    (short)rng.Next(-4096, 4096), (short)rng.Next(-4096, 4096), (short)rng.Next(0, 512),
                    (short)rng.Next(16, 4096), (short)rng.Next(64, 1024), (short)rng.Next(-32768, 32768), (short)rng.Next(0x0800, 0x3C00),
                };
                Wr(0x02); foreach (var v in prm) Word(v);
                var po = new short[4]; for (int k = 0; k < 4; k++) po[k] = RdWord();
                w.WriteLine($"02|{Hex(prm)}|{Hex(po)}");
                short vs = (short)rng.Next(-100, 100);
                Wr(0x0A); Word(vs);
                var ro = new List<short>();
                for (int k = 0; k < 16; k++) ro.Add(RdWord());
                for (int k = 0; k < 4; k++) Word(unchecked((short)0x8000));   // stop the raster
                w.WriteLine($"0A|{Hex(new[] { vs })}|{Hex(ro)}");
                for (int k = 0; k < 4; k++)
                {
                    short[] p3 = { (short)(prm[0] + rng.Next(-2048, 2048)), (short)(prm[1] + rng.Next(-2048, 2048)), (short)rng.Next(0, 256) };
                    Wr(0x06); foreach (var v in p3) Word(v);
                    var o3 = new short[3]; for (int q = 0; q < 3; q++) o3[q] = RdWord();
                    w.WriteLine($"06|{Hex(p3)}|{Hex(o3)}");
                }
            }
            Console.WriteLine($"wrote {n} camera setups to {outPath}");
            return 0;
        }
        for (int t = 0; t < n; t++)
        {
            var ins = new short[inCount];
            for (int k = 0; k < inCount; k++) ins[k] = (short)rng.Next(-32768, 32768);
            Wr(cmd);
            foreach (var v in ins) { Wr((byte)v); Wr((byte)(v >> 8)); }
            var outs = new short[outCount];
            for (int k = 0; k < outCount; k++) { int lo = Rd(); outs[k] = (short)(lo | Rd() << 8); }
            w.WriteLine($"{cmd:X2}|{string.Join(',', ins.Select(v => ((ushort)v).ToString("X4")))}|{string.Join(',', outs.Select(v => ((ushort)v).ToString("X4")))}");
        }
        Console.WriteLine($"wrote {n} x {cmd:X2} to {outPath}");
        return 0;
    }
}
