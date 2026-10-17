// Round 6 (inject): the library-level checks of "push an instrument into the running game". `FruityHost inject-test [--only id,id] [--no-mutants]` needs no DLL: it runs real games from the user's library
// (Super Mario World on the SNES, Sonic the Hedgehog on the Genesis; never committed) straight on their console sessions, pushes an edited instrument with the library the plugin uses (Instruments/GamePush*.cs) and
// measures the GAME'S OWN audio (the session's samples, not Runaway's sampler): pitch by FFT peak, amplitude profile, spectrum, bit-identity against a run without the push, push + remove, the saved form.
// Every check has a mutant (a deliberate bug) that it must catch; a check that needs a ROM that is not there says "not tested", never "pass".
using BrokenNes2;
using BrokenNes2.Instruments;
using NesEmulator.Sega;
using NesEmulator.Snes;
using NesEmulator.Systems;

namespace BrokenNes.FruityHost;

internal static partial class InjectChecks
{
    private static int pass, fail, skipped;
    private static readonly List<string> failures = new();

    private static void Ok(string name, string detail) { pass++; Console.WriteLine($"  PASS  {name}  {detail}"); }
    private static void Bad(string name, string detail) { fail++; failures.Add(name); Console.WriteLine($"  FAIL  {name}  {detail}"); }
    private static void Skip(string name, string why) { skipped++; Console.WriteLine($"  SKIP  {name}  not tested: {why}"); }
    private static void Check(bool cond, string name, string detail) { if (cond) Ok(name, detail); else Bad(name, detail); }

    public static int Main(string[] args)
    {
        string? only = Opt(args, "--only");
        bool mutants = !args.Contains("--no-mutants");
        var ids = only?.Split(',').Select(x => x.Trim().ToLowerInvariant()).ToHashSet();
        var groups = new (string Id, string Title, Action Run)[]
        {
            ("snes-explore", "SNES: what the game plays (exploration, prints only)", () => SnesExplore(args)),
            ("snes", "SNES: Super Mario World", () => SnesChecks(mutants)),
            ("genesis-explore", "Genesis: what the game plays (exploration, prints only)", () => GenesisExplore(args)),
            ("genesis", "Genesis: Sonic the Hedgehog", () => GenesisChecks(mutants)),
            ("gb-explore", "Game Boy: what the game does with its wave RAM (exploration, prints only)", () => GbExplore(args)),
            ("sms-explore", "Master System FM: what the game does with the user instrument (exploration, prints only)", () => SmsExplore(args)),
            ("other", "Game Boy wave and YM2413 user instrument on bare chips", () => OtherChecks(mutants)),
            ("gb", "Game Boy: Link's Awakening", () => GbGameChecks(mutants)),
            ("sms", "Master System, FM unit: Galactic Protector", () => SmsGameChecks(mutants)),
        };
        foreach (var (id, title, run) in groups)
        {
            if (ids != null ? !ids.Contains(id) : id.EndsWith("-explore")) continue;
            Console.WriteLine($"== {title}");
            try { run(); }
            catch (Exception e) { Bad(id, "threw " + e); }
        }
        Console.WriteLine($"== {pass} passed, {fail} failed, {skipped} not tested");
        foreach (var f in failures) Console.WriteLine("   failed: " + f);
        return fail == 0 ? 0 : 1;
    }

    private static string? Opt(string[] a, string name) { int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
}
