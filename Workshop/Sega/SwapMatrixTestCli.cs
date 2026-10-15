using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NesEmulator;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// <c>--swap-matrix-test [--only nes,snes,gb,gbc,sms,gg,md] [--frames N] [--nes-rom f] [--snes-rom f] [--gb-rom f] [--gbc-rom f] [--verbose]</c>: hot swapping, console by console, slot by slot, entry by entry. For every entry of the CPU, PPU and APU menu of each
/// console a fresh game runs a few frames on the default cores, the core is changed in the middle of the run (the way the apps do it: <c>IConsoleSession.TrySwapCore</c>, the NES core setters), and the game runs on. Each swap must be one of:
/// <list type="bullet">
/// <item><b>in place</b>: no exception, the session's description names the new core, the picture is still there (not blank) and sound can still be read;</item>
/// <item><b>refused with a reason</b>: <c>TrySwapCore</c> says no and <c>SwapRefusal</c> says why (the app restarts the game on the new core and prints the reason). Only the swaps listed in <see cref="KnownRestarts"/> may do this; any other refusal fails the test, so a new gap cannot appear quietly;</item>
/// <item>a CPU entry that is an instruction-set wall (<see cref="CoreOption.Wall"/>) is allowed to stop the game, because that is what it is, but it must still not throw.</item>
/// </list>
/// An id the menu does not list must be refused by every slot of every session (a swap to garbage is never accepted). Games: the Sega consoles run the hand-assembled programs of the smoke tests (they run anywhere), the NES the in-repo story ROM, the SNES and Game Boy the library's
/// Super Mario World / Super Mario Land / Link's Awakening DX when they are on this machine (otherwise that console is reported NOT RUN, not passed). Exit 0 = every check passed.
/// </summary>
internal static class SwapMatrixTestCli
{
    /// <summary>The swaps that restart the game today, each with the reason the session must give. Track B4b may make the Genesis sound unit swappable; an entry that stops refusing is not a failure.</summary>
    private static readonly (ConsoleKind Console, CoreSlot Slot, string ReasonContains)[] KnownRestarts =
    {
        (ConsoleKind.Genesis, CoreSlot.Apu, "sound unit"),
    };

    private static int passes, fails, notRun;
    private static bool verbose;
    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) passes++; else fails++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  [" + detail + "]" : "")}");
    }

    public static int Run(string[] args)
    {
        RomTestCli.EnsureConsole();
        string Opt(string n, string d) { int i = Array.FindIndex(args, a => a.Equals("--" + n, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : d; }
        verbose = args.Contains("--verbose", StringComparer.OrdinalIgnoreCase);
        int frames = int.Parse(Opt("frames", "20"));
        var only = Opt("only", "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Want(string c) => only.Count == 0 || only.Contains(c);

        string story = FindUp("Windows/Data/story/page1_binty.nes");
        if (Want("nes")) Guard("NES", () => Nes(Opt("nes-rom", story), frames));
        if (Want("snes")) Guard("SNES", () => Session(ConsoleKind.Snes, File.Exists(Opt("snes-rom", Lib(@"Super Nintendo\Super Nintendo (U)\Super Mario World (U) [!].smc"))) ? File.ReadAllBytes(Opt("snes-rom", Lib(@"Super Nintendo\Super Nintendo (U)\Super Mario World (U) [!].smc"))) : null, "smw.smc", frames, 150));
        if (Want("gb")) Guard("Game Boy", () => Session(ConsoleKind.GameBoy, LoadMaybe(Opt("gb-rom", Lib(@"GameBoy\Super Mario Land (W) (V1.0) [!].zip")), out string n1), n1, frames, 100));
        if (Want("gbc")) Guard("Game Boy Color", () => Session(ConsoleKind.GameBoyColor, LoadMaybe(Opt("gbc-rom", Lib(@"GameBoy Color\Legend of Zelda, The - Link's Awakening DX (USA, Europe) (Rev 2) (SGB Enhanced) (GB Compatible).gbc")), out string n2), n2, frames, 100));
        if (Want("sms")) Guard("Master System", () => Session(ConsoleKind.MasterSystem, SmokeProgs.Picture(false), "smoke.sms", frames, 10));
        if (Want("gg")) Guard("Game Gear", () => Session(ConsoleKind.GameGear, SmokeProgs.Picture(true), "smoke.gg", frames, 10));
        if (Want("md")) Guard("Genesis", () => Session(ConsoleKind.Genesis, GenesisSmoke(), "smoke.md", frames, 10));
        Console.WriteLine($"\n{passes} passed, {fails} failed{(notRun > 0 ? $", {notRun} console(s) NOT RUN (no game on this machine)" : "")}");
        return fails == 0 ? 0 : 1;
    }

    private static void Guard(string title, Action body)
    {
        Console.WriteLine($"\n== {title}");
        try { body(); }
        catch (Exception ex) { Check($"{title}: the group ran to the end", false, ex.GetType().Name + ": " + ex.Message.Split('\n')[0]); }
    }

    private static string Lib(string rel) => Path.Combine(@"X:\EMULATION", rel);
    private static byte[]? LoadMaybe(string path, out string name) { name = Path.GetFileName(path); return File.Exists(path) ? File.ReadAllBytes(path) : null; }

    private static string FindUp(string rel)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            string p = Path.Combine(d.FullName, rel.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(p)) return p;
        }
        return rel;
    }

    /// <summary>The Genesis smoke program of <c>UAT/entrypoints/sega-preview-smoke.ps1</c> (a 68000 program that sets the display up and writes the pad as the backdrop colour): a header, the vectors and 38 bytes of code.</summary>
    private static byte[] GenesisSmoke()
    {
        const string code = "33fc800400c0000433fc814400c0000433fc8c8100c0000433fc870000c0000433fc8f0200c0000413fc004000a1000913fc004000a10003103900a100030240003fe74823fcc000000000c0000433c000c0000060e2";
        var md = new byte[0x20000];
        md[1] = 0xFF; md[2] = 0xFE; md[3] = 0x00; md[6] = 0x02; md[7] = 0x00;
        System.Text.Encoding.ASCII.GetBytes("SEGA MEGA DRIVE ").CopyTo(md, 0x100);
        System.Text.Encoding.ASCII.GetBytes("GM 00000000-00").CopyTo(md, 0x180);
        for (int i = 0; i < 16; i++) md[0x1F0 + i] = 0x20; md[0x1F0] = (byte)'U';
        for (int i = 0; i < code.Length / 2; i++) md[0x200 + i] = Convert.ToByte(code.Substring(i * 2, 2), 16);
        return md;
    }

    private static int Colours(uint[] frame, int w, int h)
    {
        var set = new HashSet<uint>();
        for (int i = 0; i < w * h && set.Count < 8; i++) set.Add(frame[i] & 0xFFFFFF);
        return set.Count;
    }

    private static void Run(IConsoleSession s, int frames, PadButtons pad)
    {
        var buf = new short[65536];
        for (int f = 0; f < frames; f++)
        {
            s.SetPad(0, pad);
            s.RunFrame();
            while (s.ReadSamples(buf) > 0) { }
        }
    }

    // ------------------------------------------------------------------------------------------------------------------ sessions (SNES, Game Boy, Master System, Game Gear, Genesis)
    private static void Session(ConsoleKind console, byte[]? rom, string name, int frames, int startFrame)
    {
        string label = Consoles.DisplayName(console);
        if (rom == null) { notRun++; Console.WriteLine($"  NOT RUN  {label}: no game on this machine ({name})"); return; }
        byte[] data = RomDetect.Unwrap(rom, name, out string inner);
        string def(CoreSlot slot) => CoreCatalog.Default(console, slot);
        IConsoleSession Make() => ConsoleSessions.Create(console, data, def(CoreSlot.Cpu), def(CoreSlot.Ppu), def(CoreSlot.Apu), null, inner);

        foreach (var slot in new[] { CoreSlot.Cpu, CoreSlot.Ppu, CoreSlot.Apu })
        {
            var options = CoreCatalog.Options(console, slot);
            int inPlace = 0, restarts = 0; var bad = new List<string>(); var restartReasons = new HashSet<string>();
            var known = KnownRestarts.FirstOrDefault(k => k.Console == console && k.Slot == slot);
            foreach (var opt in options)
            {
                try
                {
                    using var s = Make();
                    Run(s, frames, PadButtons.Right);
                    int before = Colours(s.Frame, s.FrameWidth, s.FrameHeight);
                    bool ok = s.TrySwapCore(slot, opt.Id);
                    if (!ok)
                    {
                        string why = s.SwapRefusal(slot, opt.Id);
                        restarts++; restartReasons.Add(why);
                        if (known.ReasonContains == null) bad.Add($"{opt.Id}: refused ({why}) but this swap is expected to work in place");
                        else if (string.IsNullOrWhiteSpace(why) || !why.Contains(known.ReasonContains, StringComparison.OrdinalIgnoreCase)) bad.Add($"{opt.Id}: refused without a proper reason ('{why}')");
                        continue;
                    }
                    inPlace++;
                    Run(s, frames, PadButtons.Right);
                    int after = Colours(s.Frame, s.FrameWidth, s.FrameHeight);
                    bool isDefault = opt.Id.Equals(def(slot), StringComparison.OrdinalIgnoreCase);
                    if (!isDefault && !s.Description.Contains(opt.Id, StringComparison.OrdinalIgnoreCase)) bad.Add($"{opt.Id}: swapped, but the description does not say so ({s.Description})");
                    if (before > 1 && after <= 1 && !opt.Wall) bad.Add($"{opt.Id}: the picture is blank after the swap ({before} -> {after} colours)");
                }
                catch (Exception ex) { bad.Add($"{opt.Id}: threw {ex.GetType().Name}: {ex.Message.Split('\n')[0]}"); }
            }
            // an id the menu does not list is refused, never accepted or replaced
            try
            {
                using var s = Make(); Run(s, 2, PadButtons.None);
                bool accepted = s.TrySwapCore(slot, "NO-SUCH-CORE");
                if (accepted) bad.Add("an id the menu does not list was accepted (NO-SUCH-CORE)");
                else if (string.IsNullOrWhiteSpace(s.SwapRefusal(slot, "NO-SUCH-CORE"))) bad.Add("a refused id gives no reason");
            }
            catch (Exception ex) { bad.Add($"NO-SUCH-CORE: threw {ex.GetType().Name}: {ex.Message.Split('\n')[0]}"); }
            string summary = $"{options.Count} entries: {inPlace} in place, {restarts} restart{(restarts > 0 ? $" ({string.Join(" | ", restartReasons.Select(r => r.Length > 90 ? r[..90] + "..." : r).Take(1))})" : "")}, wall entries {options.Count(o => o.Wall)}";
            Check($"{label} {slot}: every menu entry swaps in place or is a documented restart with its reason, and a garbage id is refused", bad.Count == 0, bad.Count == 0 ? summary : string.Join("; ", bad.Take(4)) + (bad.Count > 4 ? $" (+{bad.Count - 4} more)" : ""));
        }
    }

    // ------------------------------------------------------------------------------------------------------------------ the NES (its own class: the core setters)
    private static void Nes(string romPath, int frames)
    {
        if (!File.Exists(romPath)) { notRun++; Console.WriteLine($"  NOT RUN  NES: ROM not found ({romPath})"); return; }
        byte[] rom = File.ReadAllBytes(romPath);
        foreach (var slot in new[] { CoreSlot.Cpu, CoreSlot.Ppu, CoreSlot.Apu })
        {
            var options = CoreCatalog.Options(ConsoleKind.Nes, slot).Where(o => o.Family != "NES" || o.Id.ToUpperInvariant() is "FIX" or "FIXS" or "FMC" or "FIXX").ToList();   // the bridges and the accuracy-target family: the gimmick cores are frozen and not this test's business
            int ok = 0; var bad = new List<string>();
            foreach (var opt in options)
            {
                try
                {
                    var nes = new NES { RomName = "swap.nes" };
                    nes.LoadROM(rom); nes.SetCpuCore("FIX"); nes.SetPpuCore("FIX"); nes.SetApuCore("FIX");
                    var bus = (Bus)typeof(NES).GetField("bus", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(nes)!;
                    for (int f = 0; f < frames; f++) nes.RunFrame();
                    if (slot == CoreSlot.Ppu && CoreCatalog.NesPpuNeedsPreciseTiming(opt.Id)) { bus.SpeedConfig.CpuCyclePrecisePpu = true; bus.SpeedConfig.NtscAccurateFrameRate = true; }
                    bool done = slot switch { CoreSlot.Cpu => nes.SetCpuCore(opt.Id), CoreSlot.Ppu => nes.SetPpuCore(opt.Id), _ => nes.SetApuCore(opt.Id) };
                    string now = slot switch { CoreSlot.Cpu => nes.GetCpuCoreId(), CoreSlot.Ppu => nes.GetPpuCoreId(), _ => nes.GetApuCoreId() };
                    if (!done || !now.EndsWith("_" + opt.Id, StringComparison.OrdinalIgnoreCase)) { bad.Add($"{opt.Id}: the core setter said {done}, the core is {now}"); continue; }
                    for (int f = 0; f < frames; f++) { nes.RunFrame(); if (nes.IsCrashed()) { if (!opt.Wall) bad.Add($"{opt.Id}: the core crashed after the swap ({nes.GetCrashInfo().Split('\n')[0]})"); break; } }
                    ok++;
                }
                catch (Exception ex) { bad.Add($"{opt.Id}: threw {ex.GetType().Name}: {ex.Message.Split('\n')[0]}"); }
            }
            bool garbage = true;
            try { var nes = new NES { RomName = "swap.nes" }; nes.LoadROM(rom); garbage = slot switch { CoreSlot.Cpu => nes.SetCpuCore("NO-SUCH-CORE"), CoreSlot.Ppu => nes.SetPpuCore("NO-SUCH-CORE"), _ => nes.SetApuCore("NO-SUCH-CORE") }; } catch (Exception) { garbage = false; }
            if (garbage) bad.Add("an id the registry does not know was accepted");
            Check($"NES {slot}: the bridge and FIX-family entries swap live ({ok} of {options.Count}, walls included), a garbage id is refused", bad.Count == 0, bad.Count == 0 ? "" : string.Join("; ", bad.Take(4)));
        }
    }
}
