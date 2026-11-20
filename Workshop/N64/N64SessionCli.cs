// [brokennes_int2] Workshop CLI for the N64 console; lands at Workshop/N64/N64SessionCli.cs (battery.ps1 4c runs it).
// --n64session rom [--frames N (default 1000: SM64 is black from power-on until about frame 700)] [--ppu MADCAT|PRDP]: ROM detection, N frames, the picture, a save state round trip
// (the frames after a load give the same pictures and sound as the first time), the battery, and the honest refusals. Exit 0 = all passed.

using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NesEmulator.Systems;
using NesEmulator.N64;

namespace BrokenNes.Workshop.N64;

internal static class N64SessionCli
{
    private static string? Arg(string[] a, string k) { int i = Array.IndexOf(a, k); return i > 0 && i + 1 < a.Length ? a[i + 1] : null; }

    public static int Run(string[] args)
    {
        string path = args.Length > 1 ? args[1] : throw new ArgumentException("--n64session rom");
        int frames = Arg(args, "--frames") is string fr ? int.Parse(fr) : 1000;
        N64Host.Register();
        int fail = 0;
        void Check(bool ok, string what) { Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {what}"); if (!ok) fail++; }
        var data = File.ReadAllBytes(path);
        string name = Path.GetFileName(path);
        Check(RomDetect.Detect(data, name) == ConsoleKind.N64, $"{name} is detected as the Nintendo 64");
        Check(Consoles.FromKey("n64") == ConsoleKind.N64 && Consoles.Key(ConsoleKind.N64) == "n64", "key n64");
        string ppu = Arg(args, "--ppu") ?? CoreCatalog.Default(ConsoleKind.N64, CoreSlot.Ppu);
        using var s = new N64ConsoleSession(data, name, ppu, CoreCatalog.Default(ConsoleKind.N64, CoreSlot.Apu));
        Console.WriteLine($"  session: {s.Title} | {s.Description}, {s.FramesPerSecond:F2} fps, save {s.SaveType}");
        Check(s.GameId == Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant(), "GameId is the ROM's SHA-1");

        var buf = new short[16384];
        // Pictures and sound are hashed as two streams: after a load cor64 can hand a frame's samples out one ReadSamples
        // call later (same samples, same order), so a per-frame interleaved hash would differ where the machine does not.
        string Hash(int n, bool run)
        {
            using var hv = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            using var ha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            long samples = 0;
            void Drain() { int k; while ((k = s.ReadSamples(buf)) > 0) { samples += k; ha.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(buf.AsSpan(0, k))); } }
            for (int f = 0; f < n; f++)
            {
                s.SetPad(0, (f / 30 % 2) == 0 ? PadButtons.Start : PadButtons.A);   // a fixed input script
                s.RunFrame();
                Drain();
                hv.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(s.Frame.AsSpan(0, s.FrameWidth * s.FrameHeight)));
            }
            s.RunFrame(); Drain();   // one more frame, so samples handed out late are in both hashes
            return Convert.ToHexString(hv.GetHashAndReset())[..8] + Convert.ToHexString(ha.GetHashAndReset())[..8] + "/" + samples;
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Hash(frames, true);
        Console.WriteLine($"  {frames} frames in {sw.Elapsed.TotalSeconds:F1} s ({frames / sw.Elapsed.TotalSeconds:F1} fps); picture {s.FrameWidth}x{s.FrameHeight} shown {s.DisplayWidth} wide, {s.SampleRate} Hz");
        Check(s.Frame.Take(s.FrameWidth * s.FrameHeight).Distinct().Count() > 1, "the picture is not blank");

        Check(s.SupportsState, "save states supported");
        var st = s.SaveState();
        while (s.ReadSamples(buf) > 0) { }   // sound from before the snapshot (taking it flushes the AI) is not in it; cor64's SessionProbe check does the same
        Check(ConsoleStateFile.Peek(st).Console == ConsoleKind.N64, $"the state is an N64 ConsoleStateFile ({st.Length:N0} bytes)");
        string h1 = Hash(120, true);
        s.LoadState(st);
        string h2 = Hash(120, true);
        Check(h1 == h2, $"120 frames after a load are the same ({h1} / {h2})");
        bool refused = false;
        try { var bad = (byte[])st.Clone(); bad[bad.Length / 2] ^= 0xFF; s.LoadState(bad); } catch (InvalidDataException) { refused = true; }
        Check(refused, "a damaged state is refused with InvalidDataException");

        var save = s.ExportSave();
        Check(s.HasBattery && save != null && save.Length > 0, $"battery export ({save?.Length ?? 0} bytes)");
        s.ImportSave(save!);
        Check(s.ExportSave().SequenceEqual(save!), "battery import round trip");

        Check(!s.TrySwapCore(CoreSlot.Cpu, "COR64_X") && s.SwapRefusal(CoreSlot.Cpu, "COR64_X").Length > 0, $"CPU swap refused: {s.SwapRefusal(CoreSlot.Cpu, "COR64_X")}");
        Check(!s.TrySwapCore(CoreSlot.Ppu, "VGA"), $"another console's chip refused: {s.SwapRefusal(CoreSlot.Ppu, "VGA")}");
        Console.WriteLine(fail == 0 ? "  n64session: ok" : $"  n64session: {fail} FAILED");
        return fail == 0 ? 0 : 1;
    }
}
