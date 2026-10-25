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
    /// <summary>The cores that run (CPU/PPU/APU ids).</summary>
    public string Cores { get; private set; } = "";
    public DebugMachine Machine { get; private set; } = null!;
    public Debugger Dbg { get; private set; } = null!;
    public DebugRunner Runner { get; private set; } = null!;
    private Action runFrame = null!;

    /// <summary>Opens a ROM. <paramref name="debugger"/> false: the plain machine (no debugger object at all), for the negative controls.</summary>
    public static DebugHost Open(byte[] rom, string fileName, ConsoleKind? console = null, bool debugger = true, string cpu = "", string ppu = "", string apu = "",
                                 string powerOn = "fceux", bool ntsc = false)
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
            if (ntsc) { var speed = nes.GetSpeedConfig() ?? throw new InvalidOperationException("this NES has no speed config for NTSC frame timing"); speed.NtscAccurateFrameRate = true; }
            // the power-on RAM: BrokenNes's own fill (FCEUX's 00 00 00 00 FF FF FF FF), or all zeros / all ones (BrokenDev verify's powerOnRam)
            switch (powerOn)
            {
                case "fceux": break;
                case "zeros": for (int i = 0; i < 2048; i++) nes.PokeSystemRam(i, 0x00); break;
                case "ones": for (int i = 0; i < 2048; i++) nes.PokeSystemRam(i, 0xFF); break;
                default: throw new ArgumentException($"power-on RAM '{powerOn}' is not fceux, zeros or ones");
            }
            // the ids SetCpuCore and its siblings take ("FIX", not the "CPU_FIX" the getters return)
            static string Id(string full, string prefix) => full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full[prefix.Length..] : full;
            h.Cores = $"{Id(nes.GetCpuCoreId(), "CPU_")}/{Id(nes.GetPpuCoreId(), "PPU_")}/{Id(nes.GetApuCoreId(), "APU_")}";
            h.Nes = nes;
            h.runFrame = nes.RunFrame;
            if (debugger) h.Machine = DebugMachines.For(nes);
        }
        else
        {
            if (ntsc) throw new ArgumentException("NTSC frame timing is a NES setting");
            if (powerOn != "fceux" && powerOn != "zeros") throw new ArgumentException($"the {Consoles.DisplayName(c)} powers on with its own RAM (power-on '{powerOn}' is a NES setting)");
            string ci = CoreCatalog.Resolve(c, CoreSlot.Cpu, cpu), pi = CoreCatalog.Resolve(c, CoreSlot.Ppu, ppu), ai = CoreCatalog.Resolve(c, CoreSlot.Apu, apu);
            var s = ConsoleSessions.Create(c, rom, ci, pi, ai, null, Path.GetFileName(fileName));
            h.Cores = $"{ci}/{pi}/{ai}";
            h.Session = s;
            h.runFrame = s.RunFrame;
            if (debugger) h.Machine = DebugMachines.For(s);
        }
        if (debugger)
        {
            h.Dbg = new Debugger(h.Machine);
            h.Runner = new DebugRunner(h.Dbg, h.Frame);
        }
        return h;
    }

    // ---- the history (D5: step back): a state every few frames and every frame's input, replayed to go back

    public NesEmulator.Debugging.Movies.DebugHistory? History { get; private set; }
    private PadButtons live1, live2;

    /// <summary>Starts the history from now (states every <paramref name="every"/> frames, the last <paramref name="keep"/>); pad input then goes through it.</summary>
    public NesEmulator.Debugging.Movies.DebugHistory EnableHistory(int every = 60, int keep = 30)
    {
        History = new NesEmulator.Debugging.Movies.DebugHistory(SaveState, Dbg, every, keep, 60, () => (MoviePicture(), MovieRam()));
        return History;
    }

    /// <summary>The frame the runner (or <see cref="FrameOnce"/>) runs: through the history when there is one.</summary>
    private void Frame()
    {
        var hist = History;
        if (hist == null) { runFrame(); return; }
        var (a, b) = hist.BeforeFrame(live1, live2);
        ApplyPads(a, b);
        runFrame();
        hist.AfterFrame();
    }

    private void ApplyPads(PadButtons p1, PadButtons p2)
    {
        if (Nes != null) Nes.SetInputs(NesPad(p1), NesPad(p2));
        else { Session!.SetPad(0, p1); Session.SetPad(1, p2); }
    }

    /// <summary>Finishes the frame the machine is parked in (breakpoints off for it), so a state can be loaded between frames.</summary>
    public void EnsureBetweenFrames()
    {
        if (Dbg == null || !Dbg.IsParked) return;
        for (int tries = 0; tries < 4 && Dbg.IsParked; tries++)
        {
            var on = new System.Collections.Generic.List<int>();
            foreach (var bp in Dbg.Breakpoints) if (bp.Enabled) { on.Add(bp.Id); Dbg.SetEnabled(bp.Id, false); }
            try { Runner.Run(1); }
            finally { foreach (var id in on) Dbg.SetEnabled(id, true); }
        }
        if (Dbg.IsParked) throw new InvalidOperationException("the machine stays parked; run some frames first");
    }

    /// <summary>Holds pad 1's buttons from now on ("Right", "A"...; none releases them): set between runs, while the emulation is parked.</summary>
    public void SetPad(System.Collections.Generic.IEnumerable<string> buttons)
    {
        if (History != null)
        {
            var hp = PadButtons.None;
            foreach (var b in buttons) hp |= Enum.TryParse<PadButtons>(b, true, out var v) && v != PadButtons.None ? v : throw new ArgumentException($"unknown button '{b}'");
            History.Branch();   // a new input while behind: the future is rewritten from here
            live1 = hp;
            return;
        }
        if (Nes != null)
        {
            var names = new[] { "A", "B", "Select", "Start", "Up", "Down", "Left", "Right" };
            var held = new bool[8];
            foreach (var b in buttons) { int i = Array.FindIndex(names, n => n.Equals(b, StringComparison.OrdinalIgnoreCase)); if (i < 0) throw new ArgumentException($"unknown NES button '{b}'"); held[i] = true; }
            Nes.SetInputs(held, null);
            return;
        }
        PadButtons p = PadButtons.None;
        foreach (var b in buttons) p |= Enum.TryParse<PadButtons>(b, true, out var v) ? v : throw new ArgumentException($"unknown button '{b}'");
        Session!.SetPad(0, p);
    }

    /// <summary>Both pads' buttons for the next frames (the movie player's input; the NES maps the console-neutral buttons onto its pad).</summary>
    public void SetPads(PadButtons p1, PadButtons p2)
    {
        live1 = p1; live2 = p2;
        if (Nes != null) { Nes.SetInputs(NesPad(p1), NesPad(p2)); return; }
        Session!.SetPad(0, p1); Session.SetPad(1, p2);
    }

    public static bool[] NesPad(PadButtons b) => new[]
    {
        (b & PadButtons.A) != 0, (b & PadButtons.B) != 0, (b & PadButtons.Select) != 0, (b & PadButtons.Start) != 0,
        (b & PadButtons.Up) != 0, (b & PadButtons.Down) != 0, (b & PadButtons.Left) != 0, (b & PadButtons.Right) != 0,
    };

    /// <summary>One frame on this thread (the debugger's runner must be idle: not parked, no run in progress).</summary>
    public void FrameOnce() => Frame();

    /// <summary>A save state of the machine (the NES's own text state as UTF-8; a session's state). Between frames only.</summary>
    public byte[] SaveState()
    {
        if (Nes != null) return System.Text.Encoding.UTF8.GetBytes(Nes.SaveState());
        if (!Session!.SupportsState) throw new NotSupportedException(Session.StateUnsupportedReason);
        return Session.SaveState();
    }

    public void LoadState(byte[] state)
    {
        if (Nes != null) { Nes.LoadState(System.Text.Encoding.UTF8.GetString(state)); return; }
        Session!.LoadState(state);
    }

    /// <summary>The memory domains (corruption's view: true sizes, plain peeks).</summary>
    public System.Collections.Generic.IReadOnlyList<NesEmulator.Corruption.IMemoryDomain> Domains() =>
        Nes != null ? NesEmulator.Corruption.MachineDomains.For(Nes) : NesEmulator.Corruption.MachineDomains.For(Session!);

    /// <summary>The movie keyframe hashes (<see cref="NesEmulator.Debugging.Movies.MovieHash"/>).</summary>
    public ulong MoviePicture() => Nes != null ? NesEmulator.Debugging.Movies.MovieHash.Picture(Nes.GetFrameBuffer()) : NesEmulator.Debugging.Movies.MovieHash.Picture(Session!.Frame, Session.FrameWidth * Session.FrameHeight);
    public ulong MovieRam() => NesEmulator.Debugging.Movies.MovieHash.Ram(Domains());

    public bool Crashed => Nes != null && Nes.IsCrashed();
    public string CrashInfo => Nes != null ? Nes.GetCrashInfo() : "";

    /// <summary>Runs frames on this thread (no debugger: the negative control).</summary>
    public void RunPlain(int frames) { for (int i = 0; i < frames; i++) Frame(); }

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
