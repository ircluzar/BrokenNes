// Butchering replay experiment (proof of concept for replayable corruptions, 2026-11-07). One process per console:
//   breplay <nes|gb|gbc|sms|gg|md|snes|wm> <outdir> [--frames N] [--game I]
// For the control (everything off) and every effect of the catalog switched on alone, four runs from one entry state, the same
// scripted input, picture AND sound hashed every frame:
//   A  load state, apply the effect, run                       (a "blast" from a state)
//   B  load state again WITHOUT re-applying, run               (Reload State in the app: does the effect's own state carry over?)
//   C  apply the same settings again, load state, run          (does re-applying reset the effect?)
//   D  a brand-new machine, same warm-up, load, apply, run     (the reference: a fresh replay)
// A == D: the effect replays from a fresh start. B == A: it replays on a plain reload. C == A: re-applying resets it.
// <outdir>/<console>.txt: one line per effect; also the first differing frame and whether picture, sound or both differ.
using System.Text.Json;
using System.Reflection;
using NesEmulator;
using NesEmulator.Systems;

string console = args[0]; string outDir = args[1];
int Opt(string n, int d) { int i = Array.IndexOf(args, "--" + n); return i >= 0 && i + 1 < args.Length ? int.Parse(args[i + 1]) : d; }
int frames = Opt("frames", 600), gameIx = Opt("game", 0), masterSeed = Opt("seed", 0);
Directory.CreateDirectory(outDir);
var report = new StreamWriter(Path.Combine(outDir, console + ".txt")) { AutoFlush = true };
void Line(string s) { report.WriteLine(s); Console.WriteLine($"[{console}] {s}"); }

string repo = Environment.CurrentDirectory;
for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent) if (File.Exists(Path.Combine(d.FullName, "UAT", "run-suite.ps1"))) { repo = d.FullName; break; }
string corpus = Path.Combine(repo, "SubProjects/65-o-borked/bin/Debug/net8.0/roms");
var lib = new Dictionary<string, (ConsoleKind k, string rom)>
{
    ["nes"] = (ConsoleKind.Nes, Path.Combine(corpus, "Super Mario Bros. (JU) (PRG0) [!].nes")),
    ["nes2"] = (ConsoleKind.Nes, Path.Combine(corpus, "Mega Man 2 (U).nes")),
    ["gb"] = (ConsoleKind.GameBoy, "X:/EMULATION/GameBoy/Super Mario Land (W) (V1.1) [!].zip"),
    ["gbc"] = (ConsoleKind.GameBoyColor, "X:/EMULATION/GameBoy Color/1942 (USA).gbc"),
    ["sms"] = (ConsoleKind.MasterSystem, "X:/EMULATION/Sega Master System/Sonic The Hedgehog 2 (Europe) (v1.1).sms"),
    ["gg"] = (ConsoleKind.GameGear, "X:/EMULATION/Sega Game Gear/Sonic The Hedgehog (U) (V1.1) [!].gg"),
    ["md"] = (ConsoleKind.Genesis, "X:/EMULATION/Sega Genesis/Sonic the Hedgehog (JUE) [!].bin"),
    ["snes"] = (ConsoleKind.Snes, "X:/EMULATION/Super Nintendo/Super Nintendo (U)/Super Mario World (U) [!].smc"),
    ["wm"] = (ConsoleKind.WideMess, "X:/EMULATION/Super Nintendo/Super Nintendo (U)/Super Mario World (U) [!].smc"),
};
var (kind, rom) = lib[console];
if (kind == ConsoleKind.WideMess) Consoles.WideMessEnabled = true;
if (!File.Exists(rom)) { Line($"SKIP no ROM {rom}"); return 0; }

IM Make()
{
    byte[] raw = File.ReadAllBytes(rom);
    if (kind == ConsoleKind.Nes) { var n = new NES(); n.LoadROM(raw); n.SetCpuCore("FIX"); n.SetPpuCore("FIX"); n.SetApuCore("FIX"); return new NesM(n); }
    byte[] data = RomDetect.Unwrap(raw, rom, out string inner);
    return new SesM(ConsoleSessions.Create(kind, data, CoreCatalog.Default(kind, CoreSlot.Cpu), CoreCatalog.Default(kind, CoreSlot.Ppu), CoreCatalog.Default(kind, CoreSlot.Apu), null, inner));
}
// the same input every run: Start pulses during the warm-up, then a seeded walk of the pad
PadButtons Pad(int f) { if (f < 300) return f % 40 < 4 ? PadButtons.Start : PadButtons.None; uint x = (uint)(f / 8) * 2654435761u; x ^= x >> 15; return (PadButtons)(x & 0xFF & ~(int)PadButtons.Select & ~(int)PadButtons.Start); }
// per-frame hashes (picture, sound)
(ulong[] pic, ulong[] snd) Run(IM m, int n, int f0)
{
    var pic = new ulong[n]; var snd = new ulong[n];
    for (int f = 0; f < n; f++)
    {
        m.Pad(Pad(f0 + f)); m.Run();
        ulong h = 1469598103934665603UL; var fr = m.Frame; int v = Math.Min(fr.Length, m.Visible);
        for (int i = 0; i < v; i++) { h ^= fr[i]; h *= 1099511628211UL; }
        pic[f] = h; snd[f] = m.AudioHash();
    }
    return (pic, snd);
}
void Warm(IM m) { for (int f = 0; f < 300; f++) { m.Pad(Pad(f)); m.Run(); m.AudioHash(); } }

// an effect switched on alone, by reflection over the settings tree: On = true, rates raised, the first bits / channels on,
// two pin lines faulted, numbers at the top of the tree set to 1
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
ButcherSettings With(string path)
{
    var s = new ButcherSettings { Enabled = true };
    object owner = s; PropertyInfo? prop = null;
    foreach (var seg in path.Split('.'))
    {
        if (prop != null) owner = prop.GetValue(owner)!;
        prop = owner.GetType().GetProperty(char.ToUpperInvariant(seg[0]) + seg[1..], BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (prop == null) throw new InvalidOperationException("no " + path);
    }
    if (prop!.PropertyType == typeof(int)) { prop.SetValue(owner, 1); return s; }
    if (prop.PropertyType == typeof(bool)) { prop.SetValue(owner, true); return s; }
    var fx = prop.GetValue(owner)!;
    foreach (var p in fx.GetType().GetProperties())
    {
        if (!p.CanWrite && !p.PropertyType.IsArray) continue;
        if (p.Name == "On" && p.PropertyType == typeof(bool)) p.SetValue(fx, true);
        else if (p.Name == "Rate" && p.PropertyType == typeof(int)) p.SetValue(fx, 60);
        else if (p.Name == "Rate" && p.PropertyType == typeof(double)) p.SetValue(fx, 60.0);
        else if (p.PropertyType == typeof(bool[]) && p.CanWrite)
        {   // (empty by default: the panel sizes them to the console's bits / channels)
            var b = p.GetValue(fx) as bool[]; if (b == null || b.Length < 5) { b = new bool[5]; p.SetValue(fx, b); }
            b[0] = true; b[1] = true;
        }
        else if (p.PropertyType == typeof(int[]) && p.CanWrite)
        {
            var a = p.GetValue(fx) as int[]; if (a == null || a.Length < 8) { a = new int[p.Name == "Address" ? 13 : 8]; p.SetValue(fx, a); }
            a[1] = 1; a[2] = 2;
        }
    }
    return s;
}

var paths = new List<string> { "(off)" };
paths.AddRange(ButcherCatalog.SettingsPaths());
Line($"# {console}: {Path.GetFileName(rom)}, seed {masterSeed}, {frames} frames per run, scripted input; A load+apply, B reload only, C re-apply+load, D fresh machine");
Line($"# {"effect",-28} {"fresh(A=D)",-12} {"reload(B=A)",-12} {"reapply(C=A)",-13} note");
int fresh = 0, reload = 0, reapply = 0, total = 0, inert = 0;
var m0 = Make(); Warm(m0); var entry = m0.Save();
(ulong[] pic, ulong[] snd) offRun = default;
foreach (var path in paths)
{
    ButcherSettings s;
    try { s = path == "(off)" ? new ButcherSettings() : With(path); s.Seed = masterSeed; } catch (Exception e) { Line($"  {path,-28} SKIP {e.Message}"); continue; }
    try
    {
        var m = m0;
        m.Host.ApplyButcher(new ButcherSettings());
        m.Load(entry); m.Host.ApplyButcher(s); var a = Run(m, frames, 300);
        m.Load(entry); var b = Run(m, frames, 300);
        m.Host.ApplyButcher(s); m.Load(entry); var c = Run(m, frames, 300);
        var md = Make(); Warm(md); md.Load(entry); md.Host.ApplyButcher(s); var dd = Run(md, frames, 300);
        m.Host.ApplyButcher(new ButcherSettings());
        if (path == "(off)") offRun = a;
        string Cmp((ulong[] pic, ulong[] snd) x, (ulong[] pic, ulong[] snd) y)
        {
            int fp = Enumerable.Range(0, frames).FirstOrDefault(i => x.pic[i] != y.pic[i], -1);
            int fs = Enumerable.Range(0, frames).FirstOrDefault(i => x.snd[i] != y.snd[i], -1);
            if (fp < 0 && fs < 0) return "same";
            return (fp >= 0 ? $"pic@{fp}" : "") + (fp >= 0 && fs >= 0 ? "+" : "") + (fs >= 0 ? $"snd@{fs}" : "");
        }
        string ad = Cmp(a, dd), ba = Cmp(b, a), ca = Cmp(c, a);
        bool effectSeen = path == "(off)" || Cmp(a, offRun) != "same";
        total++; if (ad == "same") fresh++; if (ba == "same") reload++; if (ca == "same") reapply++; if (!effectSeen) inert++;
        Line($"  {path,-28} {ad,-12} {ba,-12} {ca,-13} {(effectSeen ? "" : "no visible/audible change in this run (inert here)")}");
    }
    catch (Exception e) { Line($"  {path,-28} CRASH {e.GetType().Name}: {e.Message}"); }
}
Line($"# {total} runs: fresh-repeatable {fresh}, reload-repeatable {reload}, reapply-repeatable {reapply}; {inert} showed no change in {frames} frames");
return 0;

interface IM { IButcherHost Host { get; } uint[] Frame { get; } int Visible { get; } void Run(); void Pad(PadButtons b); ulong AudioHash(); byte[] Save(); void Load(byte[] s); }
sealed class NesM(NES n) : IM
{
    public IButcherHost Host => n; public int Visible => int.MaxValue;
    public uint[] Frame { get { var b = n.GetFrameBuffer(); var u = new uint[b.Length / 4]; Buffer.BlockCopy(b, 0, u, 0, u.Length * 4); return u; } }
    public void Run() => n.RunFrame();
    public void Pad(PadButtons b) { int v = (int)b; n.SetInput(Enumerable.Range(0, 8).Select(i => (v >> i & 1) != 0).ToArray()); }
    public ulong AudioHash() { ulong h = 1469598103934665603UL; foreach (var f in n.GetAudioBuffer()) { h ^= (ulong)BitConverter.SingleToInt32Bits(f); h *= 1099511628211UL; } return h; }
    public byte[] Save() => System.Text.Encoding.UTF8.GetBytes(n.SaveState());
    public void Load(byte[] s) => n.LoadState(System.Text.Encoding.UTF8.GetString(s));
}
sealed class SesM(IConsoleSession s) : IM
{
    readonly short[] buf = new short[65536];
    public IButcherHost Host => (IButcherHost)(s is NesEmulator.Mix.SegaPictureSession sp ? sp.Inner : s);
    public uint[] Frame => s.Frame; public int Visible => s.FrameWidth * s.FrameHeight;
    public void Run() => s.RunFrame(); public void Pad(PadButtons b) => s.SetPad(0, b);
    public ulong AudioHash() { ulong h = 1469598103934665603UL; int k; while ((k = s.ReadSamples(buf)) > 0) for (int i = 0; i < k; i++) { h ^= (ushort)buf[i]; h *= 1099511628211UL; } return h; }
    public byte[] Save() => s.SaveState(); public void Load(byte[] st) => s.LoadState(st);
}
