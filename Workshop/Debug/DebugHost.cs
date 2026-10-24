using System;
using System.IO;
using NesEmulator;
using NesEmulator.Debugging;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Debug;

/// <summary>A game under the debugger, headless: the NES or a console session, its debugger and the runner that drives its frames.</summary>
internal sealed class DebugHost : IDisposable
{
    public NES? Nes { get; private set; }
    public IConsoleSession? Session { get; private set; }
    public ConsoleKind Console { get; private set; }
    public DebugMachine Machine { get; private set; } = null!;
    public Debugger Dbg { get; private set; } = null!;
    public DebugRunner Runner { get; private set; } = null!;
    private Action runFrame = null!;

    /// <summary>Opens a ROM. <paramref name="debugger"/> false: the plain machine (no debugger object at all), for the negative controls.</summary>
    public static DebugHost Open(byte[] rom, string fileName, ConsoleKind? console = null, bool debugger = true, string cpu = "", string ppu = "", string apu = "")
    {
        var h = new DebugHost();
        var c = console ?? RomDetect.Detect(rom, fileName) ?? throw new InvalidDataException($"{fileName}: unknown ROM type");
        h.Console = c;
        if (c == ConsoleKind.Nes)
        {
            var nes = new NES { RomName = Path.GetFileName(fileName) };
            nes.LoadROM(rom);
            // the desktop app's defaults: the FIX family
            if (!nes.SetCpuCore(cpu.Length > 0 ? cpu : "FIX") | !nes.SetPpuCore(ppu.Length > 0 ? ppu : "FIX") | !nes.SetApuCore(apu.Length > 0 ? apu : "FIX"))
                throw new InvalidOperationException("a NES core was not applied");
            h.Nes = nes;
            h.runFrame = nes.RunFrame;
            if (debugger) h.Machine = DebugMachines.For(nes);
        }
        else
        {
            var s = ConsoleSessions.Create(c, rom, CoreCatalog.Resolve(c, CoreSlot.Cpu, cpu), CoreCatalog.Resolve(c, CoreSlot.Ppu, ppu), CoreCatalog.Resolve(c, CoreSlot.Apu, apu), null, Path.GetFileName(fileName));
            h.Session = s;
            h.runFrame = s.RunFrame;
            if (debugger) h.Machine = DebugMachines.For(s);
        }
        if (debugger)
        {
            h.Dbg = new Debugger(h.Machine);
            h.Runner = new DebugRunner(h.Dbg, h.runFrame);
        }
        return h;
    }

    /// <summary>Runs frames on this thread (no debugger: the negative control).</summary>
    public void RunPlain(int frames) { for (int i = 0; i < frames; i++) runFrame(); }

    /// <summary>FNV-1a 64 over the picture (the NES framebuffer as RomTestCli hashes it, a session's ARGB frame).</summary>
    public ulong PictureHash()
    {
        ulong h = 14695981039346656037;
        if (Nes != null) { foreach (byte b in Nes.GetFrameBuffer()) { h ^= b; h *= 1099511628211; } return h; }
        var s = Session!;
        for (int i = 0; i < s.FrameWidth * s.FrameHeight; i++) { h ^= s.Frame[i]; h *= 1099511628211; }
        return h;
    }

    /// <summary>FNV-1a 64 over the main work RAM (NES 2 KB, SNES 128 KB, Game Boy WRAM + HRAM).</summary>
    public ulong RamHash()
    {
        ulong h = 14695981039346656037;
        void Add(byte[] a) { foreach (byte b in a) { h ^= b; h *= 1099511628211; } }
        if (Nes != null) { var r = new byte[2048]; for (int i = 0; i < 2048; i++) r[i] = Nes.PeekSystemRam(i); Add(r); }
        else if (Session is SnesSession sn) Add(sn.Board.Wram);
        else if (Session is NesEmulator.Systems.GbSession gb) { Add(gb.Board.Wram); Add(gb.Board.Hram); }
        return h;
    }

    public void Dispose()
    {
        Runner?.Dispose();
        Session?.Dispose();
    }
}
