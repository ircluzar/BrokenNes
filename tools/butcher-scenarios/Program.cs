// Butchering-mode scenario tests (docs/projects/butchering-fuzz-plan.md, "battery"). One process per console:
//   bscen <nes|gb|gbc|sms|gg|md|snes|wm> <outdir> [--games N] [--soak FRAMES] [--seed S]
// S0 catalog       every effect in ButcherSettings has a purity score (ButcherCatalog), none orphaned, all 0-100.
// Scenarios (each line of <outdir>/<console>.txt is PASS / FAIL / SKIP with a reason; exit code 1 when anything failed):
//   S1 preset-undo   every built-in preset x several games: enter (state) -> preset -> play -> Reload State -> play -> Off + reload
//                    must replay the clean continuation bit-exact (visible picture + state).
//   S2 soak          one long run with settings re-randomised every 500 frames, Off / Reload State now and then: no crash, no
//                    frame over 2 s, managed memory bounded.
//   S3 core-swap     every core option of every slot swapped in while a heavy preset is on: no crash.
//   S4 hostile-json  byte-mangled preset JSON: a parse error is fine (the API answers 400), anything that parses must apply and run.
using System.Diagnostics;
using System.Text.Json;
using NesEmulator;
using NesEmulator.Systems;

string console = args[0]; string outDir = args[1];
int Opt(string n, int d) { int i = Array.IndexOf(args, "--" + n); return i >= 0 && i + 1 < args.Length ? int.Parse(args[i + 1]) : d; }
int gamesN = Opt("games", 6), soakFrames = Opt("soak", 20000), seed = Opt("seed", 1);
Directory.CreateDirectory(outDir);
var report = new StreamWriter(Path.Combine(outDir, console + ".txt")) { AutoFlush = true };
int fails = 0;
void Line(string status, string scen, string what) { if (status == "FAIL") fails++; report.WriteLine($"{status,-5} {scen,-13} {what}"); Console.WriteLine($"[{console}] {status} {scen} {what}"); }

string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
foreach (var up in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
    for (var d = new DirectoryInfo(up); d != null; d = d.Parent) if (File.Exists(Path.Combine(d.FullName, "UAT", "run-suite.ps1"))) { repo = d.FullName; goto found; }
found:
string corpus = Path.Combine(repo, "SubProjects/65-o-borked/bin/Debug/net8.0/roms");
var lib = new Dictionary<string, (ConsoleKind k, string dir, string[] ext, string presets, string[] known)>
{
    ["nes"] = (ConsoleKind.Nes, corpus, new[] { ".nes" }, "nes", new[] { "Super Mario Bros. (JU) (PRG0) [!].nes", "Mega Man 2 (U).nes", "Super Mario Bros. 3 (U) (PRG1) [!].nes", "Battletoads (U).nes", "Legend of Zelda, The (U) (PRG1).nes" }),
    ["gb"] = (ConsoleKind.GameBoy, "X:/EMULATION/GameBoy", new[] { ".zip", ".gb" }, "gb", new[] { "Super Mario Land (W) (V1.1) [!].zip", "Tetris (W) (V1.1) [!].zip" }),
    ["gbc"] = (ConsoleKind.GameBoyColor, "X:/EMULATION/GameBoy Color", new[] { ".gbc", ".zip" }, "gb", new[] { "1942 (USA).gbc" }),
    ["sms"] = (ConsoleKind.MasterSystem, "X:/EMULATION/Sega Master System", new[] { ".sms", ".zip" }, "sms", new[] { "Sonic The Hedgehog 2 (Europe) (v1.1).sms" }),
    ["gg"] = (ConsoleKind.GameGear, "X:/EMULATION/Sega Game Gear", new[] { ".gg", ".zip" }, "gg", new[] { "Sonic The Hedgehog (U) (V1.1) [!].gg", "Columns (U) [!].gg" }),
    ["md"] = (ConsoleKind.Genesis, "X:/EMULATION/Sega Genesis", new[] { ".bin", ".md", ".gen" }, "md", new[] { "Sonic the Hedgehog (JUE) [!].bin" }),
    ["snes"] = (ConsoleKind.Snes, "X:/EMULATION/Super Nintendo/Super Nintendo (U)", new[] { ".smc", ".sfc" }, "snes", new[] { "Super Mario World (U) [!].smc", "Super Mario Kart (U) [!].smc", "Star Fox (U) (V1.2) [!].smc" }),
    ["wm"] = (ConsoleKind.WideMess, "X:/EMULATION/Super Nintendo/Super Nintendo (U)", new[] { ".smc", ".sfc" }, "snes", new[] { "Super Mario World (U) [!].smc" }),
};
var (kind, dir, exts, presetKey, known) = lib[console];
if (kind == ConsoleKind.WideMess) Consoles.WideMessEnabled = true;
if (!Directory.Exists(dir)) { Line("SKIP", "all", $"no ROM folder {dir}"); return 0; }
var pool = Directory.EnumerateFiles(dir).Where(f => exts.Contains(Path.GetExtension(f).ToLowerInvariant()) && !f.Contains("32X")).OrderBy(f => f).ToArray();
var rng = new Random(seed);
var games = known.Select(g => Path.Combine(dir, g)).Where(File.Exists).Concat(pool.OrderBy(_ => rng.Next()).Take(Math.Max(0, gamesN - known.Length))).Distinct().Take(gamesN).ToList();
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var presetFile = Path.Combine(repo, "Windows/Webmodules/ButcheringMode/builtins", presetKey + ".json");
var presetText = File.ReadAllText(presetFile);
var presets = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(presetText)!.ToDictionary(kv => kv.Key, kv => kv.Value.GetRawText());
ButcherSettings P(string name) => JsonSerializer.Deserialize<ButcherSettings>(presets[name], json)!;

// ---------- machine adapter (as bfuzz) ----------
IM Make(string rom, string? cpu = null, string? ppu = null, string? apu = null)
{
    byte[] raw = File.ReadAllBytes(rom);
    if (kind == ConsoleKind.Nes) { var n = new NES(); n.LoadROM(raw); n.SetCpuCore(cpu ?? "FIX"); n.SetPpuCore(ppu ?? "FIX"); n.SetApuCore(apu ?? "FIX"); return new NesM(n); }
    byte[] data = RomDetect.Unwrap(raw, rom, out string inner);
    return new SesM(ConsoleSessions.Create(kind, data, cpu ?? CoreCatalog.Default(kind, CoreSlot.Cpu), ppu ?? CoreCatalog.Default(kind, CoreSlot.Ppu), apu ?? CoreCatalog.Default(kind, CoreSlot.Apu), null, inner));
}
PadButtons Pad(Random r, int f) { var v = (PadButtons)r.Next(0, 4096); return f < 200 ? (f % 40 < 4 ? PadButtons.Start : PadButtons.None) : v; }
ulong Run(IM m, Random r, int frames, int f0, bool hash = true)
{
    ulong h = 1469598103934665603UL;
    for (int f = 0; f < frames; f++) { m.Pad(Pad(r, f0 + f)); m.Run(); m.Audio(); if (hash) { var fr = m.Frame; int n = Math.Min(fr.Length, m.Visible); for (int i = 0; i < n; i++) { h ^= fr[i]; h *= 1099511628211UL; } } }
    return h;
}
string G(string rom) => Path.GetFileName(rom);

// ---------- S0 catalog: every effect has a purity score, and no score points at an effect that is gone ----------
{
    var unscored = ButcherCatalog.Unscored(); var orphans = ButcherCatalog.Orphans();
    var bad = ButcherCatalog.Effects.Where(e => e.Purity is < 0 or > 100 || (e.ByConsole?.Values.Any(v => v is < 0 or > 100) ?? false)).Select(e => e.Path).ToList();
    var dup = ButcherCatalog.Effects.GroupBy(e => e.Path, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
    bool ok = unscored.Count == 0 && orphans.Count == 0 && bad.Count == 0 && dup.Count == 0;
    Line(ok ? "PASS" : "FAIL", "S0", ok ? $"catalog: all {ButcherCatalog.SettingsPaths().Count} effects scored ({ButcherCatalog.Effects.Count} entries)"
        : $"catalog: unscored [{string.Join(", ", unscored)}] orphans [{string.Join(", ", orphans)}] out of range [{string.Join(", ", bad)}] duplicates [{string.Join(", ", dup)}]");
}

// ---------- S1 preset-undo ----------
foreach (var rom in games)
{
    IM m;
    try { m = Make(rom); } catch (Exception e) { Line("SKIP", "S1", $"{G(rom)}: cannot start ({e.GetType().Name})"); continue; }
    if (!m.CanState) { Line("SKIP", "S1", $"{G(rom)}: no save states with these cores"); continue; }
    var pr = new Random(7);
    try { Run(m, pr, 300, 0, false); } catch (Exception e) { Line("FAIL", "S1", $"{G(rom)}: warm-up crashed {e.GetType().Name}: {e.Message}"); continue; }
    var entry = m.Save();
    m.Load(entry); ulong clean = Run(m, new Random(11), 120, 300); var cleanState = m.Payload();
    foreach (var name in presets.Keys)
    {
        string where = "apply";
        try
        {
            m.Load(entry); m.Host.ApplyButcher(P(name));
            where = "play"; Run(m, new Random(13), 300, 300, false);
            where = "reload-with-effects"; m.Load(entry); Run(m, new Random(17), 60, 300, false);
            where = "off+reload"; m.Host.ApplyButcher(new ButcherSettings()); m.Load(entry);
            ulong after = Run(m, new Random(11), 120, 300); var afterState = m.Payload();
            bool same = after == clean && afterState.AsSpan().SequenceEqual(cleanState);
            Line(same ? "PASS" : "FAIL", "S1", $"{G(rom)} preset '{name}': {(same ? "undo exact" : after == clean ? $"picture exact, state differs ({Enumerable.Range(0, Math.Min(afterState.Length, cleanState.Length)).Count(i => afterState[i] != cleanState[i])} of {cleanState.Length} bytes, first at {Enumerable.Range(0, Math.Min(afterState.Length, cleanState.Length)).FirstOrDefault(i => afterState[i] != cleanState[i], -1)}{(afterState.Length != cleanState.Length ? $", length {afterState.Length}" : "")})" : "picture differs after Off + reload")}");
        }
        catch (Exception e) { Line("FAIL", "S1", $"{G(rom)} preset '{name}': crashed at {where}: {e.GetType().Name}: {e.Message} @ {e.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}"); }
    }
}

// ---------- S2 soak ----------
{
    var rom = games.FirstOrDefault();
    if (rom != null)
    {
        string what = $"{G(rom)} {soakFrames} frames";
        try
        {
            var m = Make(rom); var r = new Random(seed * 31 + 5); var pr = new Random(3);
            Run(m, pr, 300, 0, false);
            var entry = m.CanState ? m.Save() : null;
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); long mem0 = GC.GetTotalMemory(true);
            var names = presets.Keys.ToArray(); double worst = 0; int worstAt = 0; var sw = new Stopwatch();
            for (int f = 0; f < soakFrames; f++)
            {
                if (f % 500 == 0) m.Host.ApplyButcher(P(names[r.Next(names.Length)]));
                if (f % 1700 == 850) m.Host.ApplyButcher(new ButcherSettings());
                if (entry != null && f % 2300 == 1150) m.Load(entry);
                sw.Restart(); m.Pad(Pad(pr, 300 + f)); m.Run(); m.Audio(); sw.Stop();
                if (sw.Elapsed.TotalMilliseconds > worst) { worst = sw.Elapsed.TotalMilliseconds; worstAt = f; }
            }
            m.Host.ApplyButcher(new ButcherSettings());
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); long mem1 = GC.GetTotalMemory(true);
            long grew = (mem1 - mem0) / (1024 * 1024);
            bool ok = worst < 2000 && grew < 64;
            Line(ok ? "PASS" : "FAIL", "S2", $"{what}: worst frame {worst:F0} ms (frame {worstAt}), managed memory {(grew >= 0 ? "+" : "")}{grew} MB");
        }
        catch (Exception e) { Line("FAIL", "S2", $"{what}: crashed {e.GetType().Name}: {e.Message} @ {e.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}"); }
    }
}

// ---------- S3 core-swap ----------
{
    var rom = games.FirstOrDefault();
    var heavy = presets.Keys.Last();
    if (rom != null && kind != ConsoleKind.Nes)
    {
        foreach (var slot in new[] { CoreSlot.Cpu, CoreSlot.Ppu, CoreSlot.Apu })
            foreach (var o in CoreCatalog.Options(kind, slot))
            {
                if (o.Wall) continue;
                try
                {
                    var m = Make(rom); var pr = new Random(5); Run(m, pr, 240, 0, false);
                    m.Host.ApplyButcher(P(heavy)); Run(m, pr, 60, 240, false);
                    var s = ((SesM)m).S; bool swapped = s.TrySwapCore(slot, o.Id);
                    var host2 = ((SesM)m).Host; host2.ApplyButcher(P(heavy));
                    Run(m, pr, 120, 300, false);
                    host2.ApplyButcher(new ButcherSettings()); Run(m, pr, 30, 420, false);
                    Line("PASS", "S3", $"{G(rom)} {slot}={o.Id} ({(swapped ? "swapped live" : "refused, restart path")}) under '{heavy}'");
                }
                catch (Exception e) { Line("FAIL", "S3", $"{G(rom)} {slot}={o.Id} under '{heavy}': {e.GetType().Name}: {e.Message} @ {e.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}"); }
            }
    }
    else if (rom != null)
    {
        var n0 = new NES(); n0.LoadROM(File.ReadAllBytes(rom));
        foreach (var (slot, ids) in new[] { ("cpu", n0.GetCpuCoreIds()), ("ppu", n0.GetPpuCoreIds()), ("apu", n0.GetApuCoreIds()) })
            foreach (var id in ids)
            {
                try
                {
                    var nm = (NesM)Make(rom); var n = nm.N; var pr = new Random(5); Run(nm, pr, 240, 0, false);
                    n.ApplyButcher(P(heavy)); Run(nm, pr, 60, 240, false);
                    if (slot == "cpu") n.SetCpuCore(id); else if (slot == "ppu") n.SetPpuCore(id); else n.SetApuCore(id);
                    Run(nm, pr, 120, 300, false); n.ApplyButcher(new ButcherSettings()); Run(nm, pr, 30, 420, false);
                    Line("PASS", "S3", $"{G(rom)} {slot}={id} under '{heavy}'");
                }
                catch (Exception e) { Line("FAIL", "S3", $"{G(rom)} {slot}={id} under '{heavy}': {e.GetType().Name}: {e.Message} @ {e.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}"); }
            }
    }
}

// ---------- S4 hostile-json ----------
{
    var rom = games.FirstOrDefault();
    if (rom != null)
    {
        var r = new Random(seed * 7 + 1); int parsed = 0, rejected = 0, crashed = 0; string? firstCrash = null;
        var m = Make(rom); Run(m, new Random(1), 240, 0, false);
        var bodies = presets.Values.ToArray();
        for (int t = 0; t < 300; t++)
        {
            var b = bodies[r.Next(bodies.Length)].ToCharArray();
            int edits = 1 + r.Next(6);
            for (int e = 0; e < edits; e++)
            {
                int at = r.Next(b.Length);
                b[at] = "0123456789-.eE,:[]{}\"tfn x9"[r.Next(27)];
            }
            ButcherSettings? s;
            try { s = JsonSerializer.Deserialize<ButcherSettings>(new string(b), json); } catch (JsonException) { rejected++; continue; } catch (Exception e) { crashed++; firstCrash ??= $"deserialize: {e.GetType().Name}: {e.Message}"; continue; }
            if (s == null) { rejected++; continue; }
            parsed++;
            try { m.Host.ApplyButcher(s); Run(m, new Random(t), 20, 240 + t * 20, false); }
            catch (Exception e) { crashed++; firstCrash ??= $"apply/run: {e.GetType().Name}: {e.Message} @ {e.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}\n      body: {new string(b)[..Math.Min(400, b.Length)]}"; m = Make(rom); Run(m, new Random(1), 240, 0, false); }
        }
        Line(crashed == 0 ? "PASS" : "FAIL", "S4", $"{G(rom)}: 300 mangled presets, {parsed} parsed and ran, {rejected} rejected, {crashed} crashed{(firstCrash != null ? " - first: " + firstCrash : "")}");
    }
}

report.WriteLine($"DONE {fails} failure(s)");
Console.WriteLine($"[{console}] DONE {fails} failure(s)");
return fails > 0 ? 1 : 0;

interface IM { IButcherHost Host { get; } uint[] Frame { get; } int Visible { get; } bool CanState { get; } void Run(); void Pad(PadButtons b); void Audio(); byte[] Save(); void Load(byte[] s); byte[] Payload(); }
sealed class NesM(NES n) : IM
{
    public NES N => n;
    public IButcherHost Host => n; public uint[] Frame => MemoryCast(n.GetFrameBuffer()); public int Visible => int.MaxValue; public bool CanState => true;
    static uint[] MemoryCast(byte[] b) { var u = new uint[b.Length / 4]; Buffer.BlockCopy(b, 0, u, 0, u.Length * 4); return u; }
    public void Run() => n.RunFrame();
    public void Pad(PadButtons b) { int v = (int)b; n.SetInput(Enumerable.Range(0, 8).Select(i => (v >> i & 1) != 0).ToArray()); }
    public void Audio() => n.GetAudioBuffer();
    public byte[] Save() => System.Text.Encoding.UTF8.GetBytes(n.SaveState());
    public void Load(byte[] s) => n.LoadState(System.Text.Encoding.UTF8.GetString(s));
    public byte[] Payload() => Save();
}
sealed class SesM(IConsoleSession s) : IM
{
    readonly short[] buf = new short[65536];
    public IConsoleSession S => s;
    public IButcherHost Host => (IButcherHost)(s is NesEmulator.Mix.SegaPictureSession sp ? sp.Inner : s);
    public uint[] Frame => s.Frame; public int Visible => s.FrameWidth * s.FrameHeight; public bool CanState => s.SupportsState;
    public void Run() => s.RunFrame(); public void Pad(PadButtons b) => s.SetPad(0, b);
    public void Audio() { while (s.ReadSamples(buf) > 0) { } }
    public byte[] Save() => s.SaveState(); public void Load(byte[] st) => s.LoadState(st); public byte[] Payload() => s.SaveStatePayload();
}
