// Butchering-mode stress fuzzer. Usage: bfuzz <console> <seconds> <outdir> [startSeed]
// One process per console (process isolation catches static-state leaks between consoles too).
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using NesEmulator;
using NesEmulator.Systems;

var console = args[0]; int budget = int.Parse(args[1]); string outDir = args[2]; int seed0 = args.Length > 3 ? int.Parse(args[3]) : Environment.TickCount;
Directory.CreateDirectory(outDir);
var Zeroed = new HashSet<string>();
var log = new StreamWriter(Path.Combine(outDir, Environment.GetEnvironmentVariable("REPRO_CHILD") != null ? $"{console}.child{Environment.ProcessId}.log" : $"{console}.log")) { AutoFlush = true };
var findings = new Dictionary<string, (int count, string first)>();
void Find(string kind, string sig, string detail)
{
    string key = kind + " | " + sig;
    lock (findings)
    {
        if (findings.TryGetValue(key, out var f)) findings[key] = (f.count + 1, f.first);
        else { findings[key] = (1, detail); log.WriteLine($"NEW {key}\n{detail}\n"); }
    }
}

// ---------- ROM pool ----------
string corpus = "C:/Users/philt/OneDrive/Documents/PROJECTS/BrokenNes/SubProjects/65-o-borked/bin/Debug/net8.0/roms";
var dirs = new Dictionary<string, (ConsoleKind k, string dir, string[] ext)>
{
    ["nes"] = (ConsoleKind.Nes, corpus, new[] { ".nes" }),
    ["gb"] = (ConsoleKind.GameBoy, "X:/EMULATION/GameBoy", new[] { ".zip", ".gb" }),
    ["gbc"] = (ConsoleKind.GameBoyColor, "X:/EMULATION/GameBoy Color", new[] { ".gbc", ".zip" }),
    ["sms"] = (ConsoleKind.MasterSystem, "X:/EMULATION/Sega Master System", new[] { ".sms", ".zip" }),
    ["gg"] = (ConsoleKind.GameGear, "X:/EMULATION/Sega Game Gear", new[] { ".gg", ".zip" }),
    ["md"] = (ConsoleKind.Genesis, "X:/EMULATION/Sega Genesis", new[] { ".bin", ".md", ".gen", ".zip" }),
    ["snes"] = (ConsoleKind.Snes, "X:/EMULATION/Super Nintendo/Super Nintendo (U)", new[] { ".smc", ".sfc", ".zip" }),
    ["wm"] = (ConsoleKind.WideMess, "X:/EMULATION/Super Nintendo/Super Nintendo (U)", new[] { ".smc", ".sfc" }),
};
var (kind, dir, exts) = dirs[console];
if (kind == ConsoleKind.WideMess) Consoles.WideMessEnabled = true;
var roms = Directory.EnumerateFiles(dir).Where(f => exts.Contains(Path.GetExtension(f).ToLowerInvariant()) && !f.Contains("32X")).ToArray();
log.WriteLine($"{console}: {roms.Length} roms, seed0 {seed0}, budget {budget}s");

// ---------- settings randomizer (reflection over the whole tree, so every effect is reached) ----------
void Randomize(object o, Random r, double density, bool hostile, int depth = 0)
{
    foreach (var p in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
    {
        if (!p.CanWrite || !p.CanRead) continue;
        var t = p.PropertyType;
        if (p.Name == "Enabled") { p.SetValue(o, true); continue; }
        if (t == typeof(bool)) p.SetValue(o, r.NextDouble() < density);
        else if (t == typeof(double)) p.SetValue(o, hostile && r.Next(6) == 0 ? new[] { -50.0, 0, 1e9, 100, 1e-9, -1e9 }[r.Next(6)] : r.NextDouble() * 100);
        else if (t == typeof(int)) p.SetValue(o, hostile && r.Next(6) == 0 ? new[] { -1, 0, int.MaxValue, int.MinValue, 100000, 255 }[r.Next(6)] : r.Next(0, p.Name is "Duration" ? 120 : p.Name is "Bits" ? 9 : 64));
        else if (t == typeof(int[])) { int n = hostile && r.Next(5) == 0 ? r.Next(0, 80) : r.Next(0, 25); var a = new int[n]; for (int i = 0; i < n; i++) a[i] = r.NextDouble() < density ? (hostile && r.Next(8) == 0 ? r.Next(-3, 300) : r.Next(1, 4)) : 0; p.SetValue(o, a); }
        else if (t == typeof(bool[])) { int n = r.Next(0, hostile ? 70 : 20); var a = new bool[n]; for (int i = 0; i < n; i++) a[i] = r.NextDouble() < density; p.SetValue(o, a); }
        else if (t.IsClass && t != typeof(string) && depth < 6) { var v = p.GetValue(o) ?? Activator.CreateInstance(t); if (v != null) { Randomize(v, r, density, hostile, depth + 1); p.SetValue(o, v); } }
    }
}
ButcherSettings Rand(Random r, bool hostile)
{
    var s = new ButcherSettings(); double d = new[] { 0.02, 0.08, 0.25, 0.6 }[r.Next(4)];
    Randomize(s, r, d, hostile);
    foreach (var path in Zeroed) { object o = s; var parts = path.Split('.'); for (int i = 0; i < parts.Length - 1; i++) o = o.GetType().GetProperty(parts[i])!.GetValue(o)!; var pp = o.GetType().GetProperty(parts[^1])!; pp.SetValue(o, pp.PropertyType.IsArray ? Array.CreateInstance(pp.PropertyType.GetElementType()!, 0) : Activator.CreateInstance(pp.PropertyType)); }
    s.Enabled = Environment.GetEnvironmentVariable("FUZZ_CONTROL") != "1"; return s;
}

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

// ---------- machine adapter ----------
IMachine Make(string rom, Random r, out string desc)
{
    byte[] raw = File.ReadAllBytes(rom);
    if (kind == ConsoleKind.Nes)
    {
        var n = new NES(); n.LoadROM(raw);
        string Pick(IReadOnlyList<string> ids, string fix) => r.Next(3) == 0 && ids.Count > 0 ? ids[r.Next(ids.Count)] : fix;
        string c = Pick(n.GetCpuCoreIds(), "FIX"), p = Pick(n.GetPpuCoreIds(), "FIX"), a = Pick(n.GetApuCoreIds(), "FIX");
        n.SetCpuCore(c); n.SetPpuCore(p); n.SetApuCore(a);
        desc = $"{Path.GetFileName(rom)} cpu={c} ppu={p} apu={a}";
        return new NesM(n);
    }
    byte[] data = RomDetect.Unwrap(raw, rom, out string inner);
    string Opt(CoreSlot sl) { var o = CoreCatalog.Options(kind, sl).Where(x => !x.Wall).ToList(); return r.Next(3) == 0 && o.Count > 0 ? o[r.Next(o.Count)].Id : CoreCatalog.Default(kind, sl); }
    string cpu = Opt(CoreSlot.Cpu), ppu = Opt(CoreSlot.Ppu), apu = Opt(CoreSlot.Apu);
    desc = $"{Path.GetFileName(rom)} cpu={cpu} ppu={ppu} apu={apu}";
    return new SesM(ConsoleSessions.Create(kind, data, cpu, ppu, apu, null, inner));
}

// ---------- one trial ----------
ulong HashP(IMachine m, ulong h) { foreach (var p in m.Frame) { h ^= p; h *= 1099511628211UL; } m.Audio(); return h; }
ulong Hash(IMachine m, ulong h) { foreach (var p in m.Frame) { h ^= p; h *= 1099511628211UL; } foreach (var s in m.Audio()) { h ^= (ushort)s; h *= 1099511628211UL; } return h; }
PadButtons Pad(Random r, int f) { var v = (PadButtons)r.Next(0, 4096); return f < 200 ? (f % 40 < 4 ? PadButtons.Start : PadButtons.None) : v; }

(ulong h, string? oracle) Trial(int seed, string rom, bool deep)
{
    var r = new Random(seed); var pr = new Random(seed ^ 0x5A5A);
    string desc = "";
    IMachine m;
    try { m = Make(rom, r, out desc); } catch (Exception e) { Find("create", Sig(e), $"seed {seed} {desc} {rom}\n{e}"); return (0, null); }
    if (Environment.GetEnvironmentVariable("FTRACE") == "1") Console.WriteLine("DESC " + desc);
    bool hostile = r.Next(4) == 0;
    var s = Rand(r, hostile);
    string sj = JsonSerializer.Serialize(s, json);
    // oracle: JSON round-trip
    if (JsonSerializer.Serialize(JsonSerializer.Deserialize<ButcherSettings>(sj, json), json) != sj) Find("oracle", "json-roundtrip", $"seed {seed}");
    ulong h = 1469598103934665603UL; string ctx = $"seed {seed} hostile={hostile} {desc}";
    int applyAt = 150 + r.Next(100), total = applyAt + 250;
    byte[]? before = null, refState = null; ulong cleanRef = 0; int refLen = 60;
    for (int f = 0; f < total; f++)
    {
        string step = "run";
        try
        {
            if (f == applyAt)
            {
                if (deep && m.CanState)
                {
                    step = "save-before"; before = m.Save(); m.Load(before); m.Audio();
                    var prA = new Random(seed ^ 0x5A5A); for (int i = 0; i < applyAt; i++) Pad(prA, i);
                    ulong c = 0; for (int i = 0; i < refLen; i++) { m.Pad(Pad(prA, 1000 + i)); m.Run(); c = HashP(m, c); } cleanRef = c; refState = m.CanState ? m.Save() : null;
                    m.Load(before); m.Audio();
                    var prB = new Random(seed ^ 0x5A5A); for (int i = 0; i < applyAt; i++) Pad(prB, i);
                    ulong c2 = 0; for (int i = 0; i < refLen; i++) { m.Pad(Pad(prB, 1000 + i)); m.Run(); c2 = HashP(m, c2); }
                    if (c2 != cleanRef) { Find("info", "savestate-not-replayable " + desc.Split(' ').Skip(1).Aggregate("", (a, b) => a + " " + b), ctx); before = null; }
                    step = "load-before"; if (before != null) m.Load(before); m.Audio();
                }
                step = "apply"; if (Environment.GetEnvironmentVariable("FTRACE") == "1") Console.WriteLine("APPLY " + sj); m.Host.ApplyButcher(s);
            }
            if (f > applyAt)
            {
                int ev = r.Next(200); if ((Environment.GetEnvironmentVariable("NOEV") ?? "").Contains((char)('0' + Math.Min(ev, 9)))) ev = 199; if (ev < 6 && Environment.GetEnvironmentVariable("FTRACE") == "1") Console.WriteLine($"EV{ev} f{f}");
                if (ev == 0) { step = "mutate"; s = Rand(r, hostile); if (Environment.GetEnvironmentVariable("FTRACE") == "1") Console.WriteLine("MUTATE " + JsonSerializer.Serialize(s, json)); m.Host.ApplyButcher(s); sj = JsonSerializer.Serialize(s, json); }
                else if (ev == 1) { step = "off-on"; m.Host.ApplyButcher(new ButcherSettings()); m.Host.ApplyButcher(s); }
                else if (ev == 2 && m.CanState) { step = "state-roundtrip-butchered"; var st = m.Save(); m.Load(st); }
                else if (ev == 3 && !deep) { step = "swap-core"; if (Environment.GetEnvironmentVariable("FTRACE") == "1" && m.CanState) { File.WriteAllBytes("pre-swap.state", m.Save()); File.WriteAllText("pre-swap.json", sj); } m.TrySwap(r); }
                else if (ev == 4) { step = "reset"; m.Reset(); }
                else if (ev == 5) { step = "reach"; m.Host.GetButcherReach(); }
            }
            step = "run"; if (Environment.GetEnvironmentVariable("FTRACE") == "1") { Console.WriteLine($"f{f} {DateTime.Now:HH:mm:ss.fff}"); if (m.CanState && f > applyAt) { File.WriteAllBytes("pre.state", m.Save()); File.WriteAllText("pre.json", JsonSerializer.Serialize(s, json)); } } m.Pad(Pad(pr, f)); m.Run(); h = Hash(m, h);
            if (m.Frame.Length == 0) Find("oracle", "empty-frame", ctx + $" frame {f}");
        }
        catch (Exception e) { Find("crash:" + step, Sig(e), $"{ctx} frame {f}\nsettings {sj}\n{e}"); return (h, "crash"); }
    }
    // oracle: Reload State + Off undoes everything
    if (deep && before != null)
    {
        try
        {
            m.Load(before); m.Host.ApplyButcher(new ButcherSettings()); m.Audio();
            var pr2 = new Random(seed ^ 0x5A5A); // the pad stream used for the reference was drawn after the warm-up; redo it identically
            for (int i = 0; i < applyAt; i++) Pad(pr2, i);
            ulong c = 0; for (int i = 0; i < refLen; i++) { m.Pad(Pad(pr2, 1000 + i)); m.Run(); c = HashP(m, c); }
            if (c != cleanRef) { var ns = m.Save(); bool same = refState != null && ns.AsSpan().SequenceEqual(refState); string where = ""; if (!same && refState != null) { int i0 = 0; while (i0 < Math.Min(ns.Length, refState.Length) && ns[i0] == refState[i0]) i0++; var t1 = System.Text.Encoding.UTF8.GetString(refState); if (t1.Length > i0 && t1.StartsWith("{")) where = " near " + t1.Substring(Math.Max(0, i0 - 40), Math.Min(50, t1.Length - Math.Max(0, i0 - 40))).Replace("\\", ""); else where = $" at byte {i0}/{refState.Length}"; } Find("oracle", "undo-mismatch " + (same ? "picture-only " : "STATE" + where + " ") + (hostile ? "hostile" : "normal"), $"{ctx}\nsettings {sj}"); return (h, "undo"); }
            if (m.Host.Butcher is { Idle: false } bb) { var names = "RamBusFaults IrqActive irqLeft CiramFault PaletteBusFaults VideoBusFaults PadActive SoundBus SampleFaults DacActive Dma Copro Console Cart CpuPins SoundCpu SpritesActive SoundActive BandsActive dropMask RamActive PaletteActive RotActive".Split(' '); var on = names.Where(n => { const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance; object? v = bb.GetType().GetField(n, F)?.GetValue(bb) ?? bb.GetType().GetProperty(n, F)?.GetValue(bb); return v is true || v is int i && i != 0 || v is uint u && u != 0 || (v != null && v is not bool && v is not int && v is not uint); }); Find("oracle", "engine-not-idle-after-off: " + string.Join(",", on), ctx); }
        }
        catch (Exception e) { Find("crash:undo", Sig(e), $"{ctx}\n{e}"); }
    }
    return (h, null);
}

string Sig(Exception e)
{
    var fr = new StackTrace(e, false).GetFrames()?.Select(x => x.GetMethod()).Where(x => x != null).Take(2).Select(x => x!.DeclaringType?.Name + "." + x.Name);
    return e.GetType().Name + " @ " + string.Join(" < ", fr ?? Array.Empty<string>());
}

// ---------- repro / minimize: REPRO=<seed> ----------
if (Environment.GetEnvironmentVariable("REPRO") is string rs)
{
    int sd = int.Parse(rs); var rom = roms[new Random(sd * 7919).Next(roms.Length)];
    bool undo = Environment.GetEnvironmentVariable("REPRO_UNDO") == "1";
    if (Environment.GetEnvironmentVariable("REPRO_CHILD") is string zl) { foreach (var z in zl.Split(';', StringSplitOptions.RemoveEmptyEntries)) Zeroed.Add(z); var res = Trial(sd, rom, undo || Environment.GetEnvironmentVariable("REPRO_DEEP") == "1"); Environment.Exit(res.oracle == (undo ? "undo" : "crash") ? 1 : 0); }
    bool Crashes() { var psi = new ProcessStartInfo(Environment.ProcessPath!, string.Join(" ", args.Select(a => "\"" + a + "\""))) { UseShellExecute = false, RedirectStandardOutput = true }; psi.Environment["REPRO_CHILD"] = string.Join(";", Zeroed) + ";"; var pc = Process.Start(psi)!; pc.StandardOutput.ReadToEndAsync(); if (!pc.WaitForExit(25000)) { pc.Kill(); return true; } return pc.ExitCode == 1; }
    Console.WriteLine($"repro seed {sd} {rom}: crashes={Crashes()}");
    var paths = new List<string>();
    void Walk(Type t, string pre, int d) { foreach (var p in t.GetProperties()) { if (!p.CanWrite || p.Name == "Enabled") continue; var pt = p.PropertyType; if (pt.IsClass && pt != typeof(string) && !pt.IsArray && d < 1) Walk(pt, pre + p.Name + ".", d + 1); else if (pt.IsClass && pt != typeof(string) && !pt.IsArray || pt.IsArray) paths.Add(pre + p.Name); } }
    Walk(typeof(ButcherSettings), "", 0);
    foreach (var pth in paths) { Zeroed.Add(pth); if (!Crashes()) Zeroed.Remove(pth); }
    Console.WriteLine("needed: " + string.Join(", ", paths.Where(x => !Zeroed.Contains(x))));
    Environment.Exit(0);
}
// ---------- main loop with a hang watchdog ----------
var sw = Stopwatch.StartNew(); int trials = 0, seed = seed0;
while (sw.Elapsed.TotalSeconds < budget)
{
    int sd = seed++; var rom = roms[new Random(sd * 7919).Next(roms.Length)];
    bool deep = sd % 2 == 0, det = sd % 5 == 0;
    var task = Task.Run(() => Trial(sd, rom, deep));
    if (!task.Wait(TimeSpan.FromSeconds(90)) && Environment.GetEnvironmentVariable("TRAP") == "1") { try { task.Wait(10000); } catch (Exception e) { Console.WriteLine(e.InnerException); } Environment.Exit(3); }
    if (!task.IsCompleted) { Find("hang", Path.GetFileName(rom), $"seed {sd} {rom} (thread abandoned)"); trials++; continue; }
    if (det && task.Result.oracle == null && task.Result.h != 0)
    {
        var t2 = Task.Run(() => Trial(sd, rom, deep));
        if (t2.Wait(TimeSpan.FromSeconds(90)) && t2.Result.h != task.Result.h) Find("oracle", "nondeterministic", $"seed {sd} {rom}");
    }
    trials++;
    if (trials % 20 == 0) log.WriteLine($"... {trials} trials, {sw.Elapsed.TotalSeconds:F0}s, {findings.Count} distinct findings");
}
log.WriteLine($"\nDONE {trials} trials in {sw.Elapsed.TotalSeconds:F0}s");
foreach (var (k, v) in findings.OrderByDescending(x => x.Value.count)) log.WriteLine($"{v.count,5}  {k}");
log.Flush(); Environment.Exit(0);

interface IMachine
{
    IButcherHost Host { get; } uint[] Frame { get; } bool CanState { get; }
    void Run(); void Pad(PadButtons b); short[] Audio(); byte[] Save(); void Load(byte[] s); void Reset(); void TrySwap(Random r);
}
sealed class NesM(NES n) : IMachine
{
    public IButcherHost Host => n; public uint[] Frame => n.GetFrameBuffer().Select(x => (uint)x).ToArray(); public bool CanState => true;
    public void Run() => n.RunFrame();
    public void Pad(PadButtons b) { int v = (int)b; n.SetInput(Enumerable.Range(0, 8).Select(i => (v >> i & 1) != 0).ToArray()); }
    public short[] Audio() => n.GetAudioBuffer().Select(x => (short)Math.Clamp(x * 32767, -32768, 32767)).ToArray();
    public byte[] Save() => System.Text.Encoding.UTF8.GetBytes(n.SaveState());
    public void Load(byte[] s) => n.LoadState(System.Text.Encoding.UTF8.GetString(s));
    public void Reset() => n.Reset();
    public void TrySwap(Random r) { var ids = n.GetPpuCoreIds(); if (ids.Count > 0) n.SetPpuCore(ids[r.Next(ids.Count)]); }
}
sealed class SesM(IConsoleSession s) : IMachine
{
    readonly short[] buf = new short[65536];
    public IButcherHost Host => (IButcherHost)(s is NesEmulator.Mix.SegaPictureSession sp ? sp.Inner : s);
    public uint[] Frame => s.Frame; public bool CanState => s.SupportsState;
    public void Run() => s.RunFrame(); public void Pad(PadButtons b) => s.SetPad(0, b);
    public short[] Audio() { var l = new List<short>(); int g; while ((g = s.ReadSamples(buf)) > 0) l.AddRange(buf.AsSpan(0, g).ToArray()); return l.ToArray(); }
    public byte[] Save() => s.SaveState(); public void Load(byte[] st) => s.LoadState(st); public void Reset() => s.Reset();
    public void TrySwap(Random r) { var sl = (CoreSlot)r.Next(3); var o = CoreCatalog.Options(s.Console, sl); if (o.Count > 0) { var id = o[r.Next(o.Count)].Id; bool ok = s.TrySwapCore(sl, id); if (Environment.GetEnvironmentVariable("FTRACE") == "1") Console.WriteLine($"SWAP {sl} {id} {ok}"); } }
}
