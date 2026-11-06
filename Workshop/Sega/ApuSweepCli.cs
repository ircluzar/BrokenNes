using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NesEmulator;
using NesEmulator.Mix;
using NesEmulator.Sega;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// <c>--apu-sweep [--console NES|SNES|GB|SMS|GG|MD|WMS] [--only ID,ID] [--plant silent|stuck|crash|menu|stockpick] [--tsv file]</c>: foreign APU phase 3 (docs/projects/foreign-apu-next-primer.md), the cross-console sweep
/// weighted to the 27 ported sound engines. Each cell is one engine reached one way, run inside a real game (the in-repo NES music test, Super Mario World, Super Mario Land, the hand-assembled Sega tone programs):
/// <list type="bullet">
/// <item><b>native</b>: the engine on its own console's APU menu, swapped in live mid-song (<c>TrySwapCore</c> / <c>NES.SetApuCore</c>);</item>
/// <item><b>bridge</b>: the engine behind another console's bridge (a NES game on <c>APU_SNES</c> / <c>APU_DMG</c> with <c>MixConfig.SnesApu</c> / <c>GbApu</c>, a SNES / Game Boy / Sega game on <c>NES:&lt;port&gt;</c>, a Master System
/// game on <c>NES:DMG</c> / <c>NES:SNES</c> backed by a port). The NES ports are not on the other consoles' menus (KNOWN_GAPS FA-BR-1), so those cells build the session on the bridge directly, as the mix lab does.</item>
/// </list>
/// Phases per cell, each measured as the RMS of 2 s of the session's output: <b>swap</b> (live, not silent, the description names the engine; a refusal must give a reason), <b>state</b> (save, play, load, play: the level after the load
/// within 2x of the level after the save), <b>butcher</b> (four Butchering-mode sound effects on the engine for 2 s: no exception; then off and the entry state reloaded, as the mode's exit does: sound again), <b>stuck</b> (the game's
/// sound is keyed off through the console's own registers: as quiet as the stock control after the same writes), <b>back</b> (a live swap back to the stock unit: within 2x of the control run's level at the same point, so nothing
/// the engine did is carried back). The control cell of each console is the stock unit through the same phases. <c>--plant</c> is the negative control: <c>silent</c> zeroes the cell's output after the state load,
/// <c>stuck</c> skips the key-off writes on the cell (not on the control), <c>crash</c> throws inside the butcher phase; each must turn its check to FAIL.
/// </summary>
internal static class ApuSweepCli
{
    private const double Quiet = 40;          // RMS (16-bit) below which a window counts as silence
    private const int Warm = 420, Window = 120;   // the Game Boy and SNES games start their music after Start (frame 240)
    private static string plant = "";

    private static readonly string[] NesPorts = { "FCEU", "INES", "NSTC", "DARC", "TUXN", "VNES", "PNES" };
    private static readonly string[] SnesPorts = { "ZS15", "ZS151", "S916", "S9X1", "S951", "OSPC", "SNDS" };
    private static readonly string[] GbPorts = { "GNUB", "VBA", "VBAM", "TGBD", "GYOB" };
    private static readonly string[] PsgPorts = { "GENS", "PICO", "M036", "BLST", "GPGX", "DARC", "DEGA" };
    private static readonly string[] FmPorts = { "M036FM", "E055" };
    private static readonly string[] MdPorts = { "GENS", "PICO", "M036", "BLST", "GPGX" };

    /// <summary>One way of reaching an engine: <paramref name="Start"/> is the unit the session is built on, <paramref name="SwapTo"/> the id swapped in live (null: the session starts on the engine, no live swap in),
    /// <paramref name="Setup"/> the mix-lab configuration the bridge reads, <paramref name="Stock"/> the id swapped back to.</summary>
    private sealed record Cell(string Console, string Engine, string Route, string Start, string? SwapTo, string Stock, Action? Setup = null) { public string Group { get; init; } = ""; public bool IsControl => Route.StartsWith("control"); }

    private sealed class Result
    {
        public string Swap = "-", State = "-", Butcher = "-", Stuck = "-", Back = "-";
        public double R0, R1, R2, R3, R4, R5, R6;
        /// <summary>A hash of the samples of the window after the swap (R1): equal to the control's means the pick played the stock bridge's sound sample for sample.</summary>
        public ulong H1;
        public readonly List<string> Fails = new(), Notes = new();
        public bool Ok => Fails.Count == 0;
    }

    // ---------------------------------------------------------------------------------------------------- the machines

    private interface IRig : IDisposable
    {
        void Frame(List<short>? into);
        bool Swap(string id, out string why);
        bool SupportsState { get; }
        string NoStateReason { get; }
        byte[] Save();
        void Load(byte[] s);
        void Butcher(ButcherSettings s);
        void KeyOff();
        string Description { get; }
    }

    private sealed class NesRig : IRig
    {
        private readonly NES nes; private int frame;
        public NesRig(byte[] rom, string apu, Action? setup)
        {
            nes = new NES { RomName = "musictest.nes" }; nes.LoadROM(rom); nes.SetCpuCore("FIX"); nes.SetPpuCore("FIX");
            setup?.Invoke();
            if (!nes.SetApuCore(apu)) throw new InvalidOperationException($"the NES has no APU core {apu}");
        }
        public void Frame(List<short>? into)
        {
            nes.SetInput(new bool[8]); frame++;   // no Start: the music test plays from power-on, Start stops it
            nes.RunFrame();
            if (nes.IsCrashed()) throw new InvalidOperationException("the NES crashed: " + nes.GetCrashInfo().Split('\n')[0]);
            var a = nes.GetAudioBuffer();
            if (into != null) foreach (var x in a) { if (float.IsNaN(x) || float.IsInfinity(x)) throw new InvalidOperationException("NaN / Inf sample"); into.Add((short)Math.Clamp(x * 32767, -32768, 32767)); }
        }
        public bool Swap(string id, out string why)
        {
            bool ok = nes.SetApuCore(id); string now = nes.GetApuCoreId();
            why = ok ? "" : $"SetApuCore({id}) said no, the core is {now}";
            if (ok && !now.EndsWith("_" + id, StringComparison.OrdinalIgnoreCase)) { why = $"SetApuCore({id}) said yes but the core is {now}"; return false; }
            return ok;
        }
        public bool SupportsState => true;
        public string NoStateReason => "";
        public byte[] Save() => System.Text.Encoding.UTF8.GetBytes(nes.SaveState());
        public void Load(byte[] s) { nes.LoadState(System.Text.Encoding.UTF8.GetString(s)); nes.GetAudioBuffer(); }
        public void Butcher(ButcherSettings s) => nes.ApplyButcher(s);
        public void KeyOff() => nes.PokeCpu(0x4015, 0);
        public string Description => nes.GetApuCoreId();
        public void Dispose() { }
    }

    private sealed class SessionRig : IRig
    {
        private readonly IConsoleSession s; private readonly ConsoleKind kind; private int frame; private readonly short[] buf = new short[65536];
        public SessionRig(ConsoleKind kind, byte[] rom, string name, string apu, Action? setup)
        {
            this.kind = kind; setup?.Invoke();
            byte[] data = RomDetect.Unwrap(rom, name, out string inner);
            s = ConsoleSessions.Create(kind, data, CoreCatalog.Default(kind, CoreSlot.Cpu), CoreCatalog.Default(kind, CoreSlot.Ppu), apu, null, inner);
        }
        private object Inner => s is SegaPictureSession sp ? sp.Inner : s;
        public void Frame(List<short>? into)
        {
            s.SetPad(0, frame >= 240 && frame < 246 ? PadButtons.Start : PadButtons.None); frame++;
            s.RunFrame();
            int n; while ((n = s.ReadSamples(buf)) > 0) if (into != null) for (int i = 0; i + 1 < n; i += 2) into.Add((short)((buf[i] + buf[i + 1]) / 2));
        }
        public bool Swap(string id, out string why) { bool ok = s.TrySwapCore(CoreSlot.Apu, id); why = ok ? "" : s.SwapRefusal(CoreSlot.Apu, id); return ok; }
        public bool SupportsState => s.SupportsState;
        public string NoStateReason => s.StateUnsupportedReason;
        public byte[] Save() => s.SaveState();
        public void Load(byte[] st) { s.LoadState(st); while (s.ReadSamples(buf) > 0) { } }
        public void Butcher(ButcherSettings b) => (ButcherCli.Host(s) ?? throw new InvalidOperationException("no Butchering host")).ApplyButcher(b);
        public void KeyOff()
        {
            switch (Inner)
            {
                case SnesSession sn when sn.Spc is { } spc:
                    spc.SmpWrite(0xF2, 0x5C); spc.SmpWrite(0xF3, 0xFF);   // key every voice off
                    spc.SmpWrite(0xF2, 0x6C); spc.SmpWrite(0xF3, 0x60);   // and mute the DSP (FLG bit 6)
                    break;
                case GbSession gb: gb.Apu.WriteRegister(0x26, 0x00); break;   // NR52: the sound off
                case SmsSession sms: foreach (byte v in new byte[] { 0x9F, 0xBF, 0xDF, 0xFF }) sms.Board.Sound.WritePsg(v); break;
                case MdSession md:
                    foreach (byte v in new byte[] { 0x9F, 0xBF, 0xDF, 0xFF }) md.Board.Sound.WritePsg(v, md.Board.MasterClock);
                    break;
                default: throw new InvalidOperationException($"no key-off path for {Inner.GetType().Name}");
            }
        }
        public string Description => s.Description;
        public void Dispose() => s.Dispose();
    }

    // ---------------------------------------------------------------------------------------------------- the games

    private static byte[]? nesRom, snesRom, gbRom;
    private static string FindUp(string rel)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent) { string p = Path.Combine(d.FullName, rel.Replace('/', Path.DirectorySeparatorChar)); if (File.Exists(p)) return p; }
        string cwd = Path.GetFullPath(rel); return cwd;
    }

    private static IRig Make(Cell c, string apu) => c.Console switch
    {
        "NES" => new NesRig(nesRom!, apu, c.Setup),
        "SNES" => new SessionRig(ConsoleKind.Snes, snesRom!, "smw.smc", apu, c.Setup),
        "GB" => new SessionRig(ConsoleKind.GameBoy, gbRom!, "sml.gb", apu, c.Setup),
        "SMS" => new SessionRig(ConsoleKind.MasterSystem, SwapSuite.ToneScroller(false), "tone.sms", apu, c.Setup),
        "GG" => new SessionRig(ConsoleKind.GameGear, SwapSuite.ToneScroller(true), "tone.gg", apu, c.Setup),
        "MD" => new SessionRig(ConsoleKind.Genesis, SwapMatrixTestCli.GenesisTone(), "tone.md", apu, c.Setup),
        "WMS" => new SessionRig(ConsoleKind.WideMess, snesRom!, "smw.smc", apu, c.Setup),   // a SNES game on the WideMess console (its menu's NES:<id> entries)
        _ => throw new ArgumentException(c.Console),
    };

    private static string Stock(string console) => console switch { "GG" => CoreCatalog.Default(ConsoleKind.GameGear, CoreSlot.Apu), "SMS" => CoreCatalog.Default(ConsoleKind.MasterSystem, CoreSlot.Apu), _ => console switch { "NES" => "FIX", "SNES" => "SFC", "GB" => "GB", "WMS" => "WMS", _ => "MD" } };

    /// <summary>Every cell, its control first: a control (route "control ...") is the same way in with the stock engine; the cells after it, with the same <see cref="Cell.Group"/>, are judged against it.</summary>
    private static List<Cell> Cells()
    {
        var l = new List<Cell>();
        void Group(string console, string group, string route, Func<string, Cell> make, string stockEngine, IEnumerable<string> ports)
        {
            l.Add(make(stockEngine) with { Route = "control " + route, Group = group, Engine = stockEngine });
            foreach (var p in ports) l.Add(make(p) with { Route = route, Group = group });
        }
        // native: the engine on its console's own menu, swapped in live mid-song
        foreach (var (con, ports) in new[] { ("NES", NesPorts), ("SNES", SnesPorts), ("GB", GbPorts), ("SMS", PsgPorts.Concat(FmPorts).ToArray()), ("GG", PsgPorts), ("MD", MdPorts) })
        {
            string st = Stock(con);
            Group(con, "native", "native", p => new(con, p, "", st, p, st), st, ports);
        }
        // bridges: a NES game on another console's chip, backed by a port (the mix lab's MixConfig), swapped in live
        Group("NES", "snes", "NES game -> APU_SNES", p => new("NES", p, "", "FIX", "SNES", "FIX", () => MixConfig.SnesApu = p), "SFC", SnesPorts);
        Group("NES", "dmg", "NES game -> APU_DMG", p => new("NES", p, "", "FIX", "DMG", "FIX", () => MixConfig.GbApu = p), "GB", GbPorts);
        // every port behind the NES's bridges as the menus offer them (FA-BR-1 and the all-APU matrix): the engine fixed in the NES core id (SNES_<port>, DMG_<port>, SMS_<port>, MD_<port>), no mix-lab
        // setting; on the NES menu directly, on every other console as NES:<id> through the NES registers (swapped in live). The control is the family's stock bridge (SNES, DMG, SMS, MD).
        foreach (var con in MenuHosts)
            foreach (var (fam, pre, stock, ports) in PortFamilies)
            {
                if (fam == OwnFamily(con)) continue;
                string hop = con == "NES" ? "" : "NES:";
                Group(con, pre.ToLowerInvariant() + "menu", $"{con} menu {hop}{pre}<port>", p => new(con, p, "", Stock(con), p == stock || plant == "stockpick" ? hop + pre.TrimEnd('_') : hop + pre + p, Stock(con)), stock, ports);
            }
        // another console's game on a NES port (the menus list them as NES:<port>): swapped in live, and the game started on it (the app's restart path)
        foreach (var con in new[] { "SNES", "GB", "SMS", "GG", "MD" })
        {
            Group(con, "nes", $"{con} game -> NES:<port> live", p => new(con, p, "", Stock(con), "NES:" + p, Stock(con)), "FIX", NesPorts);
            Group(con, "nesboot", $"{con} game on NES:<port>", p => new(con, p, "", "NES:" + p, null, Stock(con)), "FIX", NesPorts);
        }
        // two hops: a Master System / Game Boy game through a NES onto a Game Boy / SNES chip that is a port
        Group("SMS", "nesdmg", "SMS game -> NES:DMG", p => new("SMS", p, "", Stock("SMS"), "NES:DMG", Stock("SMS"), () => MixConfig.GbApu = p), "GB", GbPorts);
        Group("SMS", "nessnes", "SMS game -> NES:SNES", p => new("SMS", p, "", Stock("SMS"), "NES:SNES", Stock("SMS"), () => MixConfig.SnesApu = p), "SFC", SnesPorts);
        Group("GB", "nessnes", "GB game -> NES:SNES", p => new("GB", p, "", "GB", "NES:SNES", "GB", () => MixConfig.SnesApu = p), "SFC", SnesPorts);
        return l;
    }

    /// <summary>The ported engines' families behind the NES bridges: the menu family, the NES id prefix (the stock bridge's id without the underscore), the stock engine (the control) and the ports.</summary>
    private static readonly (string Family, string Prefix, string Stock, string[] Ports)[] PortFamilies =
    {
        ("SNES", "SNES_", "SFC", SnesPorts), ("Game Boy", "DMG_", "GB", GbPorts), ("Master System", "SMS_", "SMS", PsgPorts), ("Master System", "SMSFM_", "SMSFM", FmPorts), ("Genesis", "MD_", "MD", MdPorts),
    };
    /// <summary>The consoles whose APU menus offer the ported engines behind the NES bridges (the WideMess runs Super Mario World, as the SNES does).</summary>
    private static readonly string[] MenuHosts = { "NES", "SNES", "GB", "SMS", "GG", "MD", "WMS" };
    /// <summary>The family whose ports a console has natively, not through the NES (the WideMess's native sound units are the SNES's).</summary>
    private static string OwnFamily(string con) => con switch { "NES" => "NES", "SNES" or "WMS" => "SNES", "GB" => "Game Boy", "SMS" or "GG" => "Master System", _ => "Genesis" };
    private static ConsoleKind Kind(string con) => con switch { "NES" => ConsoleKind.Nes, "SNES" => ConsoleKind.Snes, "GB" => ConsoleKind.GameBoy, "SMS" => ConsoleKind.MasterSystem, "GG" => ConsoleKind.GameGear, "WMS" => ConsoleKind.WideMess, _ => ConsoleKind.Genesis };

    private static void ResetMix() { MixConfig.SnesApu = "SFC"; MixConfig.GbApu = "GB"; MixConfig.NesBackApu = "FIX"; MixConfig.GbBackNesApu = "FIX"; }

    // ---------------------------------------------------------------------------------------------------- the run

    public static int Run(string[] args)
    {
        RomTestCli.EnsureConsole();
        string? console = M68kTestCli.Arg(args, "--console")?.ToUpperInvariant();
        var only = M68kTestCli.Arg(args, "--only")?.ToUpperInvariant().Split(',');
        plant = M68kTestCli.Arg(args, "--plant")?.ToLowerInvariant() ?? "";
        string? tsv = M68kTestCli.Arg(args, "--tsv");
        string nesPath = FindUp("Plugin/BrokenNes2.Plugin/Resources/musictest.nes");
        if (File.Exists(nesPath)) nesRom = File.ReadAllBytes(nesPath);
        string smw = @"X:\EMULATION\Super Nintendo\Super Nintendo (U)\Super Mario World (U) [!].smc";
        if (File.Exists(smw)) snesRom = File.ReadAllBytes(smw);
        string sml = @"X:\EMULATION\GameBoy\Super Mario Land (W) (V1.0) [!].zip";
        if (File.Exists(sml)) gbRom = RomDetect.Unwrap(File.ReadAllBytes(sml), Path.GetFileName(sml), out _);

        if (M68kTestCli.Flag(args, "--nes-load-diff")) return NesLoadDiff();
        string? probe = M68kTestCli.Arg(args, "--probe");     // --probe APU: the level every 30 frames of a fresh session on that unit (picks the windows)
        if (probe != null && console != null)
        {
            var chain = probe.Split('>');      // FIX>FCEU>FIX: start on the first, swap live to each next one every 120 frames
            using var rig = Make(new Cell(console, chain[0], "probe", chain[0], null, chain[0]), chain[0]);
            var t = new List<string>(); byte[]? saved = null;
            for (int i = 0; i < 40; i++)
            {
                if (i > 0 && i % 4 == 0 && i / 4 < chain.Length)
                {
                    string step = chain[i / 4];
                    if (step == "S") { saved = rig.Save(); t.Add("[save]"); }
                    else if (step == "L") { rig.Load(saved!); t.Add("[load]"); }
                    else if (step == "K") { rig.KeyOff(); t.Add("[keyoff]"); }
                    else if (step == "B") { var bs = new ButcherSettings { Enabled = true }; bs.Sound.BitFlip.Bits = new[] { true, true, true, true, true }; bs.Sound.BitFlip.Rate = 70; rig.Butcher(bs); t.Add("[butcher]"); }
                    else if (step == "b") { rig.Butcher(new ButcherSettings { Enabled = false }); t.Add("[butcher off]"); }
                    else t.Add(rig.Swap(step, out string why) ? $"[{step}]" : $"[refused {why}]");
                }
                t.Add(Play(rig, 30).ToString("F0"));
            }
            Console.WriteLine($"{console} {probe}: {string.Join(" ", t)}"); return 0;
        }
        int menuFails = MenuCheck(console);
        var cells = Cells().Where(c => console == null || c.Console == console).Where(c => only == null || only.Contains(c.Engine) || c.IsControl).ToList();
        var controls = new Dictionary<string, Result>();
        var rows = new List<(Cell c, Result r)>();
        int fails = 0, notRun = 0;
        Console.WriteLine($"{"console",-5} {"engine",-7} {"route",-31} {"swap",-10} {"state",-10} {"butcher",-10} {"stuck",-10} {"back",-10} verdict");
        foreach (var c in cells)
        {
            if ((c.Console == "NES" && nesRom == null) || ((c.Console == "SNES" || c.Console == "WMS") && snesRom == null) || (c.Console == "GB" && gbRom == null)) { notRun++; continue; }
            ResetMix();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Result r;
            try { r = RunCell(c, controls.GetValueOrDefault(c.Console + "|" + c.Group)); }
            catch (Exception e) { r = new Result(); r.Fails.Add($"threw {e.GetType().Name}: {e.Message.Split('\n')[0]}"); }
            finally { ResetMix(); }
            if (c.IsControl) controls[c.Console + "|" + c.Group] = r;
            if (!r.Ok) fails++;
            rows.Add((c, r));
            Console.WriteLine($"{c.Console,-5} {c.Engine,-7} {Trim(c.Route, 31),-31} {r.Swap,-10} {r.State,-10} {r.Butcher,-10} {r.Stuck,-10} {r.Back,-10} {(r.Ok ? "ok" : "FAIL " + string.Join("; ", r.Fails))}{(r.Notes.Count > 0 ? "  (" + string.Join("; ", r.Notes) + ")" : "")}  [{sw.ElapsedMilliseconds / 1000.0:F1} s]");
        }
        if (tsv != null)
            File.WriteAllLines(tsv, new[] { "console\tengine\troute\tswap\tstate\tbutcher\tstuck\tback\tr0\tr1\tr2\tr3\tr4\tr5\tr6\tverdict" }
                .Concat(rows.Select(x => $"{x.c.Console}\t{x.c.Engine}\t{x.c.Route}\t{x.r.Swap}\t{x.r.State}\t{x.r.Butcher}\t{x.r.Stuck}\t{x.r.Back}\t{x.r.R0:F0}\t{x.r.R1:F0}\t{x.r.R2:F0}\t{x.r.R3:F0}\t{x.r.R4:F0}\t{x.r.R5:F0}\t{x.r.R6:F0}\t{(x.r.Ok ? "ok" : "FAIL " + string.Join("; ", x.r.Fails))}")));
        Console.WriteLine($"\n{rows.Count - fails} of {rows.Count} cells passed{(notRun > 0 ? $", {notRun} not run (game not on this machine)" : "")}{(plant != "" ? $" [planted fault: {plant}]" : "")}");
        return fails == 0 && menuFails == 0 ? 0 : 1;
    }

    /// <summary>Every console's APU menu offers every port of every other family behind the NES bridges, in that family's submenu under "Ported", labelled with the engine's id and short name
    /// (FA-BR-1 on the NES; NES:&lt;id&gt; elsewhere). Planted (--plant menu): the first port of each family is looked up under a wrong id, so each must be reported.</summary>
    private static int MenuCheck(string? console)
    {
        int bad = 0, all = 0;
        foreach (var con in MenuHosts)
        {
            if (console != null && console != con) continue;
            var opts = CoreCatalog.Options(Kind(con), CoreSlot.Apu);
            foreach (var (fam, pre, _, ports) in PortFamilies)
            {
                if (fam == OwnFamily(con)) continue;
                foreach (var p in ports)
                {
                    all++;
                    string id = (con == "NES" ? "" : "NES:") + pre + p + (plant == "menu" && p == ports[0] ? "?" : "");
                    var o = opts.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                    string? why = o == null ? "not on the menu" : CoreCatalog.MenuConsole(o) != fam ? $"in the {CoreCatalog.MenuConsole(o)} submenu" : !CoreCatalog.IsPorted(o.Id) ? "not under Ported"
                        : !CoreCatalog.MenuLabel(o).StartsWith(p + " ") ? $"label '{CoreCatalog.MenuLabel(o)}'" : null;
                    if (why != null) { bad++; Console.WriteLine($"MENU FAIL {con} APU {id}: {why}"); }
                }
            }
        }
        // the Game Gear has no FM chip: its menu must not offer the YM2413 engines in any form, and a swap to one must be refused with a reason
        if (console == null || console == "GG")
        {
            var gg = CoreCatalog.Options(ConsoleKind.GameGear, CoreSlot.Apu);
            foreach (var p in FmPorts)
            {
                all++;
                var hit = gg.FirstOrDefault(x => x.Id.EndsWith(p, StringComparison.OrdinalIgnoreCase));
                string? why = hit != null ? $"offered as {hit.Id}" : null;
                if (why == null)
                {
                    using var rig = new SessionRig(ConsoleKind.GameGear, SwapSuite.ToneScroller(true), "tone.gg", Stock("GG"), null);
                    foreach (var id in new[] { p, "NES:SMSFM_" + p })
                    {
                        bool ok = rig.Swap(id, out string r);
                        if (ok) why = $"the swap to {id} was taken";
                        else if (string.IsNullOrWhiteSpace(r)) why = $"the swap to {id} was refused without a reason";
                        else if (!r.Contains("no YM2413", StringComparison.OrdinalIgnoreCase)) why = $"the swap to {id} was refused without saying the Game Gear has no FM chip ({r})";
                        else Console.WriteLine($"menu: GG {id} refused: {r}");
                        if (why != null) break;
                    }
                }
                if (why != null) { bad++; Console.WriteLine($"MENU FAIL GG APU {p}: {why} (the Game Gear has no FM chip)"); }
            }
        }
        Console.WriteLine($"menu: {all - bad} of {all} ports offered behind the NES bridges (and the FM engines kept off the Game Gear)");
        return bad;
    }

    /// <summary>The cells that are silent in the measured windows because the original emulator was (evidence from the port's header): reported as silent*, not a failure.</summary>
    private static string? IntendedSilent(Cell c) => (c.Console, c.Group, c.Engine) switch
    {
        ("SMS" or "GG" or "MD", "nes" or "nesboot", "NSTC") => "NESticle holds a note 0.5 s at most ('hold note' bit = 0.5 s, NesticleSound.cs header); the Sega tone program keys its tone once, so it fades after half a second; KNOWN_GAPS FA-SW-1",
        _ => null,
    };

    /// <summary>The menu cells whose swap window is sample-for-sample the stock bridge's because the port only differs where the program does not go: reported, not a failure.</summary>
    private static string? IntendedSame(Cell c) => (c.Console, c.Group, c.Engine) switch
    {
        ("SMS" or "GG", "snes_menu", "OSPC") => "the Sega tone program holds one keyed tone (the bridge's direct GAIN, no noise, no echo); OpenSPC differs from the stock S-DSP in envelopes, KON timing, noise and echo only, and differs on every other host",
        _ => null,
    };

    /// <summary>The cells whose sound goes on after the key-off writes because the original emulator did that (the evidence is the port's header, from the original's source): not a failure, reported as stuck*.
    /// On the SNES the key-off is KOFF plus the FLG mute bit, and Super Mario World's driver keys its next notes at once, so only an engine that ignores the mute bit keeps sounding.</summary>
    private static string? IntendedStuck(Cell c) => (c.Console, c.Group, c.Engine) switch
    {
        ("SNES", "native", "ZS15") => "ZSNES 0.15 ignores FLG (Zs15Dsp.cs header: 'FLG ignored'); KNOWN_GAPS FA-SW-4",
        ("SNES", "native", "S9X1") => "Snes9x 1.43 never reads the mute bit (S9x1Dsp.cs: 'so.mute_sound = byte & APU_MUTE: nothing reads it'); FA-SW-4",
        ("SNES", "native", "SNDS") => "SNEmulDS's mixer ignores FLG mute/reset (SnemulDsDsp.cs header); FA-SW-4",
        ("SNES", "native", "ZS151") => "ZSNES 1.51's FLG mute only stops the voices at the write (Zs151Dsp.cs case 0x6C); the driver's next key-on sounds; FA-SW-4",
        _ => null,
    };
    /// <summary>--apu-sweep --nes-load-diff: the music test on stock FIX, saved at frame 300; the same machine plays on (A), a fresh one loads the state and plays (B):
    /// the level of both, the first sample that differs, and the APU state fields that differ right after the load (the stock NES's own save/load exactness, KNOWN_GAPS FA-ST-1).</summary>
    private static int NesLoadDiff()
    {
        NES Make() { var n = new NES { RomName = "musictest.nes" }; n.LoadROM(nesRom!); n.SetCpuCore("FIX"); n.SetPpuCore("FIX"); n.SetApuCore("FIX"); return n; }
        var a = Make(); for (int f = 0; f < 300; f++) { a.RunFrame(); a.GetAudioBuffer(); }
        string st = a.SaveState();
        var b = Make(); b.LoadState(st); b.GetAudioBuffer();
        var ja = System.Text.Json.JsonDocument.Parse(NES_ApuJson(a)).RootElement; var jb = System.Text.Json.JsonDocument.Parse(NES_ApuJson(b)).RootElement;
        foreach (var p in ja.EnumerateObject()) { string vb = jb.TryGetProperty(p.Name, out var q) ? q.GetRawText() : "(missing)"; if (vb != p.Value.GetRawText()) Console.WriteLine($"  apu field {p.Name}: saved {p.Value.GetRawText()} loaded {vb}"); }
        var A = new List<float>(); var B = new List<float>();
        for (int f = 0; f < 30; f++) { a.RunFrame(); A.AddRange(a.GetAudioBuffer()); b.RunFrame(); B.AddRange(b.GetAudioBuffer()); }
        int first = -1; for (int i = 0; i < Math.Min(A.Count, B.Count); i++) if (Math.Abs(A[i] - B[i]) > 1e-6) { first = i; break; }
        double Rf(List<float> x, int from, int to) { double s = 0; for (int i = from; i < to; i++) s += x[i] * x[i]; return Math.Sqrt(s / Math.Max(1, to - from)) * 32767; }
        Console.WriteLine($"samples A {A.Count} B {B.Count}; first difference at {first}; rms A {Rf(A, 0, A.Count):F0} B {Rf(B, 0, B.Count):F0}; last half A {Rf(A, A.Count / 2, A.Count):F0} B {Rf(B, B.Count / 2, B.Count):F0}");
        return 0;
    }

    private static string NES_ApuJson(NES n) { string s = n.SaveState(); using var d = System.Text.Json.JsonDocument.Parse(s); return d.RootElement.GetProperty("apu").GetString() ?? "{}"; }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "~";

    private static double Rms(List<short> x)
    {
        if (x.Count == 0) return 0;
        double m = 0; foreach (var v in x) m += v; m /= x.Count;
        double s = 0; foreach (var v in x) s += (v - m) * (v - m);
        return Math.Sqrt(s / x.Count);
    }

    private static double Play(IRig rig, int frames, bool zero = false) => Play(rig, frames, out _, zero);

    private static double Play(IRig rig, int frames, out ulong hash, bool zero = false)
    {
        var a = new List<short>(frames * 800);
        for (int f = 0; f < frames; f++) rig.Frame(a);
        if (zero) a.Clear();
        hash = 14695981039346656037UL;
        foreach (var v in a) { hash ^= (ushort)v; hash *= 1099511628211UL; }
        return Rms(a);
    }

    private static bool Near(double a, double b) => (a < Quiet && b < Quiet) || (a >= Quiet && b >= Quiet && a / b < 2.0 && b / a < 2.0);

    private static Result RunCell(Cell c, Result? control)
    {
        var r = new Result();
        bool isControl = c.IsControl;
        using var rig = Make(c, c.Start);
        for (int f = 0; f < Warm - Window; f++) rig.Frame(null);
        r.R0 = Play(rig, Window);

        // swap in live
        if (c.SwapTo != null)
        {
            c.Setup?.Invoke();
            if (!rig.Swap(c.SwapTo, out string why))
            {
                if (string.IsNullOrWhiteSpace(why)) { r.Swap = "refused"; r.Fails.Add("swap refused without a reason"); }
                else { r.Swap = "refused"; r.Fails.Add($"swap refused: {why}"); }
                return r;
            }
            if (!rig.Description.Contains(c.SwapTo, StringComparison.OrdinalIgnoreCase)) r.Fails.Add($"the description does not name {c.SwapTo} ({rig.Description})");
            r.R1 = Play(rig, Window, out r.H1);
            r.Swap = r.R1 >= Quiet ? "sounds" : r.R0 < Quiet ? "quiet*" : IntendedSilent(c) != null ? "silent*" : "SILENT";
            if (r.Swap == "silent*") r.Notes.Add("intended: " + IntendedSilent(c));
            if (r.Swap == "SILENT") r.Fails.Add($"silent after the live swap (rms {r.R0:F0} -> {r.R1:F0})");
            else if (r.R0 >= Quiet && (r.R1 / r.R0 > 8 || r.R0 / r.R1 > 8)) r.Notes.Add($"level jump at the swap x{r.R1 / r.R0:F2}");
            // a menu pick must play the port, not the family's stock bridge: the samples after the swap differ from the control's (--plant stockpick swaps to the stock bridge instead, so every such cell must fail here)
            if (!isControl && c.Group.EndsWith("menu") && control != null && control.R1 >= Quiet && r.H1 == control.H1 && IntendedSame(c) is { } same && plant != "stockpick")
                r.Notes.Add("same samples as stock, intended: " + same);
            else if (!isControl && c.Group.EndsWith("menu") && control != null && control.R1 >= Quiet && r.H1 == control.H1)
                r.Fails.Add($"plays the stock bridge's samples exactly (rms {r.R1:F1}, the control {control.R1:F1})");
            else if (!isControl && c.Group.EndsWith("menu") && control != null && control.R1 >= Quiet && Math.Abs(r.R1 / control.R1 - 1) < 0.002)
                r.Notes.Add($"level within 0.2% of the stock bridge's (rms {r.R1:F1} / {control.R1:F1}), samples differ");
        }
        else if (r.R0 < Quiet && IntendedSilent(c) is { } why0) { r.R1 = r.R0; r.Swap = "silent*"; r.Notes.Add("intended: " + why0); }
        else { r.R1 = r.R0; r.Swap = r.R0 >= Quiet ? "built-on" : "SILENT"; if (r.R0 < Quiet) r.Fails.Add($"silent on the bridge (rms {r.R0:F0})"); }

        // save, play, load, play
        byte[]? entry = null;
        if (rig.SupportsState)
        {
            entry = rig.Save();
            r.R2 = Play(rig, Window);
            rig.Load(entry);
            r.R3 = Play(rig, Window, zero: plant == "silent" && !isControl);
            r.State = Near(r.R2, r.R3) ? "kept" : r.R3 < Quiet ? "SILENT" : "LEVEL";
            if (r.State != "kept") r.Fails.Add($"after the state load rms {r.R3:F0}, after the save {r.R2:F0}");
        }
        else
        {
            r.State = "refused";
            if (string.IsNullOrWhiteSpace(rig.NoStateReason)) r.Fails.Add("no save states and no reason given");
            else r.Notes.Add("no states: " + rig.NoStateReason);
        }

        // Butchering mode on the engine, then off and the entry state back (the mode's exit)
        {
            var b = new ButcherSettings { Enabled = true };
            b.Sound.BitFlip.Bits = new[] { true, true, true, true, true }; b.Sound.BitFlip.Rate = 70;
            b.Sound.Stutter.On = true; b.Sound.Stutter.Rate = 80; b.Sound.Stutter.Duration = 60;
            b.Sound.Drops.Channels = Enumerable.Repeat(true, 16).ToArray(); b.Sound.Drops.Rate = 70; b.Sound.Drops.Duration = 20;
            b.Sound.Nudge.On = true; b.Sound.Nudge.Rate = 80;
            try
            {
                rig.Butcher(b);
                var a = new List<short>();
                for (int f = 0; f < Window; f++) { rig.Frame(a); if (plant == "crash" && !isControl && f == 10) throw new InvalidOperationException("planted crash"); }
                rig.Butcher(new ButcherSettings { Enabled = false });
                if (entry != null)
                {
                    rig.Load(entry);
                    r.R4 = Play(rig, Window);
                    r.Butcher = r.R4 >= Quiet || r.R3 < Quiet ? "recovers" : "SILENT";
                    if (r.Butcher == "SILENT") r.Fails.Add($"silent after Butchering off + reload (rms {r.R4:F0})");
                }
                else { r.R4 = Play(rig, Window); r.Butcher = "ran"; }
            }
            catch (Exception e) { r.Butcher = "THREW"; r.Fails.Add($"butcher phase threw {e.GetType().Name}: {e.Message.Split('\n')[0]}"); return r; }
        }

        // key the game's sound off through the console's registers: quiet, as the stock control is after the same writes (a session with no save states gets a fresh copy of the cell for this,
        // since nothing could undo the key-off before the swap back)
        {
            IRig k = rig; IRig? fresh = null;
            if (entry == null)
            {
                fresh = k = Make(c, c.Start);
                for (int f = 0; f < Warm; f++) fresh.Frame(null);
                if (c.SwapTo != null) { c.Setup?.Invoke(); fresh.Swap(c.SwapTo, out _); }
                for (int f = 0; f < Window; f++) fresh.Frame(null);
            }
            byte[]? before = entry != null ? rig.Save() : null;
            if (!(plant == "stuck" && !isControl)) k.KeyOff();
            if (Environment.GetEnvironmentVariable("APUSWEEP_TRACE") == "1") { var t = new List<string>(); for (int f = 0; f < 30; f++) { var a1 = new List<short>(); k.Frame(a1); t.Add(Rms(a1).ToString("F0")); } Console.WriteLine($"  key-off trace {c.Engine}: {string.Join(" ", t)}"); }
            for (int f = 0; f < 30; f++) k.Frame(null);   // half a second: the stock units' DC blockers and DEGA's echo die away in that time
            r.R6 = Play(k, 12);
            fresh?.Dispose();
            bool ctlQuiet = control == null || control.R6 < Quiet;
            string? intended = IntendedStuck(c);
            r.Stuck = r.R6 < Quiet ? "quiet" : isControl ? "rekeys" : !ctlQuiet ? "game-rekeys" : intended != null ? "stuck*" : "STUCK";
            if (r.Stuck == "STUCK") r.Fails.Add($"still sounding after the key-off writes (rms {r.R6:F0}; the stock control {(control == null ? "-" : control.R6.ToString("F0"))})");
            if (r.Stuck == "stuck*") r.Notes.Add("intended: " + intended);
            if (before != null) rig.Load(before);
        }
        // swap back to the stock unit: the control run's level at the same point
        {
            if (!rig.Swap(c.Stock, out string why)) { r.Back = "refused"; r.Fails.Add($"swap back to {c.Stock} refused: {why}"); return r; }
        }
        r.R5 = Play(rig, Window);
        if (control != null && !isControl)
        {
            bool ok = Near(r.R5, control.R5);
            r.Back = ok ? "clean" : r.R5 < Quiet ? "SILENT" : "LEVEL";
            if (!ok) r.Fails.Add($"back on {c.Stock}: rms {r.R5:F0}, the control {control.R5:F0}");
        }
        else r.Back = r.R5 >= Quiet ? "sounds" : "SILENT";
        if (isControl && r.R5 < Quiet) r.Fails.Add("the control is silent at the end");
        return r;
    }
}
