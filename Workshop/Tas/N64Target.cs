// TAS workline T1: the N64 target. Opens cor64's N64Session exactly as SessionProbe's compat mode does (same renderer choice, the
// Controller Pak folder, the pad word and stick, RDRAM pokes) so a compat script run here gives SessionProbe's checkpoint hashes.
using System;
using System.Collections.Generic;
using System.IO;
using BrokenNes.Workshop.Tas.Script;
using cor64.Session;
using NesEmulator.N64;
using Btn = cor64.PIF.JoyController.ButtonPress;

namespace BrokenNes.Workshop.Tas;

internal sealed class N64Target : ITasTarget
{
    private N64Session s; private readonly byte[] rom; private readonly TasOpenOptions o;
    private readonly List<N64Session> old = new();
    // TasButtonMap.N64 order: A B Z START L R CU CD CL CR DU DD DL DR
    private static readonly Btn[] Bits = { Btn.A, Btn.B, Btn.Z, Btn.Start, Btn.ShoulderL, Btn.ShoulderR, Btn.CUp, Btn.CDown, Btn.CLeft, Btn.CRight, Btn.DPadUp, Btn.DPadDown, Btn.DPadLeft, Btn.DPadRight };
    private static readonly HashSet<string> Ops = new(StringComparer.OrdinalIgnoreCase) { "pak", "prenmi", "reload", "clearsave", "dumpram" };
    public static bool IsOp(string op) => Ops.Contains(op);

    public N64Target(string romPath, TasOpenOptions opt)
    {
        // compat scripts are timed for real VI pace (UAT/n64/compat/tools/run_game.sh: COR64_VI_SPEEDUP=1, the certification timing);
        // read once by cor64's Telemetry, so it is set before the first cor64 type is touched. An explicit value is kept.
        if (Environment.GetEnvironmentVariable("COR64_VI_SPEEDUP") == null) Environment.SetEnvironmentVariable("COR64_VI_SPEEDUP", "1");
        N64Host.Register();
        o = opt; rom = File.ReadAllBytes(romPath); RomSha1 = TasTargets.Sha1(rom);
        s = OpenSession();
    }

    private N64Session OpenSession()
    {
        var rdp = o.N64Renderer ?? Environment.GetEnvironmentVariable("COR64_RDP");
        var opt = new N64SessionOptions { Renderer = rdp == "software" ? N64Renderer.Software : rdp == "parallel" ? N64Renderer.Parallel : N64Renderer.Auto, Ppu = Environment.GetEnvironmentVariable("PROBE_PPU") };
        var n = new N64Session(rom, opt);
        var ports = n.System.PIF.Ports;
        ports.PakDir ??= o.PakDir ?? Environment.CurrentDirectory;
        ports.GameKey = n.System.AttachedCartridge != null ? n.System.AttachedCartridge.Crc1.ToString("X8") : "game";
        return n;
    }

    public string Console => "n64"; public TasButtonMap Map => TasButtonMap.N64; public double Fps => s.FramesPerSecond;
    public string Title => s.Title; public string RomSha1 { get; }
    public string Cores => $"{s.Cpu.Id}/{s.Ppu.Id}/{s.Apu.Id}";
    public void SetPads(TasPad[] p)
    {
        for (int i = 0; i < p.Length && i < 4; i++)
        {
            Btn b = Btn.None; for (int k = 0; k < Bits.Length; k++) if ((p[i].Buttons & (1UL << k)) != 0) b |= Bits[k];
            s.SetPad(i, new N64Pad(b, p[i].X, p[i].Y));
        }
    }
    public void RunFrame() => s.RunFrame();
    public uint[] Argb => s.Frame; public int Width => s.FrameWidth; public int Height => s.FrameHeight;
    public ulong PictureHash() => TasTargets.Fnv(s.Frame, s.FrameWidth * s.FrameHeight);
    public unsafe ulong RamHash()
    {
        // parallel-rdp (deferred mode) lands a frame's RDP writes in RDRAM later, at a GPU-timing-dependent moment: drain first, as a save state does
        s.System.DeviceRcp.DeviceRdp?.Drain();
        byte* r = (byte*)s.System.SystemMemory.RDRAM.GetRamPointer(0); ulong h = 1469598103934665603UL;
        for (int i = 0; i < 0x800000; i += 4) { h ^= *(uint*)(r + i); h *= 1099511628211UL; }
        return h;
    }
    public int SampleRate => s.SampleRate;
    public int ReadSamples(short[] b) => s.ReadSamples(b);
    public byte[] SaveState() => s.SaveState();
    public void LoadState(byte[] st) => s.LoadState(st);
    public void Reset() => s.Reset();

    private static uint Phys(string t)
    {
        uint a = Convert.ToUInt32(t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? t[2..] : t.TrimStart('$'), 16) & 0x1FFFFFFF;
        if (a >= 0x800000) throw new FormatException($"poke/peek address {t} outside RDRAM");
        return a;
    }
    public unsafe void Poke(string addr, byte[] b)
    {
        uint a = Phys(addr); if (a + b.Length > 0x800000) throw new FormatException($"poke {addr} runs past RDRAM");
        byte* r = (byte*)s.System.SystemMemory.RDRAM.GetRamPointer(0);
        for (int i = 0; i < b.Length; i++) r[(a + i) ^ 3] = b[i];   // RDRAM is host-order 32-bit words
    }
    public unsafe byte[] Peek(string addr, int n)
    {
        uint a = Phys(addr); byte* r = (byte*)s.System.SystemMemory.RDRAM.GetRamPointer(0); var o2 = new byte[n];
        for (int i = 0; i < n; i++) o2[i] = r[(a + i) ^ 3];
        return o2;
    }
    public byte[] ExportSave() => s.ExportSave();

    public bool ConsoleOp(string op, string[] a, string dir)
    {
        switch (op)
        {
            case "pak": s.SetPak(Math.Clamp(a.Length > 0 ? int.Parse(a[0]) : 1, 1, 4) - 1, cor64.PIF.JoybusPorts.ParsePak(a.Length > 1 ? a[1] : "none")); return true;
            case "prenmi": s.PreNmi(); return true;
            case "reload": { var bat = s.ExportSave(); old.Add(s); s = OpenSession(); polls = 0; s.ImportSave(bat); return true; }
            case "clearsave": s.ImportSave(new byte[s.ExportSave().Length]); return true;
            case "dumpram": unsafe { byte* r = (byte*)s.System.SystemMemory.RDRAM.GetRamPointer(0); File.WriteAllBytes(Path.Combine(dir, a[0] + ".ram"), new ReadOnlySpan<byte>(r, 0x800000).ToArray()); } return true;
        }
        return false;
    }

    private long polls;
    public int InputPolls() { long n = s.System.PIF.Ports.PadReads; int d = (int)Math.Max(0, n - polls); polls = n; return d; }
    public void Dispose() { foreach (var x in old) try { x.Dispose(); } catch { } s.Dispose(); }
}
