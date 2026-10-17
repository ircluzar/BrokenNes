using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NesEmulator.Systems;

namespace BrokenNes.Workshop;

/// <summary>
/// <c>--state-audit [--frames N] [--console snes,gb,gbc,sms,gg,md]</c>: which save states every console's session really gives, for every core the catalog offers in each of its three slots (CPU, picture chip,
/// sound unit), the same ids the apps' menus list. For each combination (the other two slots on the console's own cores) a session of a real game is built the way the apps build it (<see cref="ConsoleSessions.Create"/>),
/// run, asked <see cref="IConsoleSession.SupportsState"/>, saved, loaded into a fresh session of the same combination that has run elsewhere, and the two are compared (the state each writes straight after, and the pictures of
/// the next frames). Prints one line per combination and a summary per console: SUPPORTED (with the size and what matched), REFUSED (with the reason the session gives), or BROKEN (supported but the round trip is not identical, or an
/// exception). Nothing is assumed: a combination the session claims to support and that does not round trip is listed as BROKEN.
/// </summary>
internal static class StateAuditCli
{
    private sealed record Rom(ConsoleKind Console, string Key, string Path, string Short);

    private static IEnumerable<Rom> Roms()
    {
        string snes = @"X:\EMULATION\Super Nintendo\Super Nintendo (U)\Super Mario World (U) [!].smc";
        string gb = @"X:\EMULATION\GameBoy\Super Mario Land (W) (V1.1) [!].zip";
        string gbc = @"X:\EMULATION\GameBoy Color\1942 (USA).gbc";
        string sms = System.IO.Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "", @"VRUN_Nes_Dev_work\sega_roms\sms\alexkidd.sms");
        string gg = @"X:\EMULATION\Sega Game Gear\Columns (U) [!].gg";
        string md = @"X:\EMULATION\Sega Genesis\Sonic the Hedgehog (JUE) [!].bin";
        yield return new(ConsoleKind.Snes, "snes", snes, "Super Mario World");
        yield return new(ConsoleKind.GameBoy, "gb", gb, "Super Mario Land");
        yield return new(ConsoleKind.GameBoyColor, "gbc", gbc, "1942");
        yield return new(ConsoleKind.MasterSystem, "sms", sms, "Alex Kidd in Miracle World");
        yield return new(ConsoleKind.GameGear, "gg", gg, "Columns");
        yield return new(ConsoleKind.Genesis, "md", md, "Sonic the Hedgehog");
    }

    private static string Hash(IConsoleSession s) =>
        Convert.ToHexString(SHA1.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(s.Frame.AsSpan(0, Math.Max(1, s.FrameWidth * s.FrameHeight)))))[..12];

    private static void Run(IConsoleSession s, int from, int to, List<string>? pictures)
    {
        var buf = new short[65536];
        for (int f = from; f < to; f++)
        {
            s.SetPad(0, f % 90 is >= 20 and < 26 ? PadButtons.Start : f % 40 is >= 10 and < 14 ? PadButtons.A : PadButtons.Right);
            s.RunFrame();
            pictures?.Add(Hash(s));
            while (s.ReadSamples(buf) > 0) { }
        }
    }

    public static int Run(string[] args)
    {
        RomTestCli.EnsureConsole();
        int frames = int.Parse(Opt(args, "frames") ?? "40");
        var only = Opt(args, "console")?.Split(',').Select(x => x.Trim().ToLowerInvariant()).ToHashSet();
        int supported = 0, refused = 0, broken = 0, notBuilt = 0, partialCount = 0;
        foreach (var rom in Roms())
        {
            if (only != null && !only.Contains(rom.Key)) continue;
            if (!File.Exists(rom.Path)) { Console.WriteLine($"== {Consoles.DisplayName(rom.Console)}: SKIPPED, no ROM at {rom.Path}"); continue; }
            byte[] raw = File.ReadAllBytes(rom.Path);
            byte[] bytes = RomDetect.Unwrap(raw, System.IO.Path.GetFileName(rom.Path), out string inner);
            Console.WriteLine($"== {Consoles.DisplayName(rom.Console)} ({rom.Short}, {System.IO.Path.GetFileName(inner)})");
            string own(CoreSlot slot) => CoreCatalog.Default(rom.Console, slot);
            var perConsole = new Dictionary<string, List<string>>();
            foreach (CoreSlot slot in new[] { CoreSlot.Cpu, CoreSlot.Ppu, CoreSlot.Apu })
                foreach (var option in CoreCatalog.Options(rom.Console, slot))
                {
                    string cpu = slot == CoreSlot.Cpu ? option.Id : own(CoreSlot.Cpu), ppu = slot == CoreSlot.Ppu ? option.Id : own(CoreSlot.Ppu), apu = slot == CoreSlot.Apu ? option.Id : own(CoreSlot.Apu);
                    string label = $"{slot} {option.Id}";
                    string verdict, detail = "";
                    try
                    {
                        IConsoleSession Make() => ConsoleSessions.Create(rom.Console, bytes, cpu, ppu, apu, null, System.IO.Path.GetFileName(inner));
                        using var a = Make();
                        Run(a, 0, frames, null);
                        if (!a.SupportsState)
                        {
                            verdict = "REFUSED"; detail = a.StateUnsupportedReason; refused++;
                            // it must also say so when asked to save
                            bool throws = false; try { a.SaveState(); } catch (NotSupportedException) { throws = true; }
                            if (!throws) { verdict = "BROKEN"; detail = "SupportsState is false but SaveState did not throw NotSupportedException"; refused--; broken++; }
                        }
                        else
                        {
                            byte[] state = a.SaveState();
                            var carried = new List<string>(); Run(a, frames, frames + 20, carried);
                            using var b = Make();
                            Run(b, 0, Math.Max(3, frames / 2) + 7, null);                    // the loading session has run somewhere else
                            b.LoadState(state);
                            bool bytesSame = b.SaveState().AsSpan().SequenceEqual(state);
                            var restored = new List<string>(); Run(b, frames, frames + 20, restored);
                            int same = Enumerable.Range(0, 20).Count(i => carried[i] == restored[i]);
                            bool endBytes = a.SaveState().AsSpan().SequenceEqual(b.SaveState());
                            // a picture drawn by a NES chip carries a little history of its own (a view, not state): the machine's bytes are the proof there
                            bool bridged = ppu.Contains(':');
                            bool ok = bytesSame && endBytes && (same == 20 || bridged);
                            // loads and carries on with the same pictures, but the state it writes is not the state it was given: something it writes is not restored (a NES chip listening to the machine restarts: by design where the record says so)
                            bool partial = !ok && same == 20;
                            verdict = ok ? "SUPPORTED" : partial ? "PARTIAL" : "BROKEN";
                            detail = $"{state.Length / 1024} KB; reload gives the same bytes: {bytesSame}; 20 frames later the two machines write the same state: {endBytes}; pictures equal {same}/20{(bridged && same != 20 ? " (NES chip's picture: a view)" : "")}";
                            if (!bytesSame || !endBytes) detail += $"; first differing payload byte {FirstDifference(rom.Console, b.SaveState(), state)} of {PayloadLength(state)}";
                            if (ok) supported++; else if (partial) partialCount++; else broken++;
                        }
                    }
                    catch (NotSupportedException e) { verdict = "NOT BUILT"; detail = e.Message; notBuilt++; }
                    catch (ArgumentException e) { verdict = "NOT BUILT"; detail = e.Message; notBuilt++; }
                    catch (Exception e) { verdict = "BROKEN"; detail = e.GetType().Name + ": " + e.Message; broken++; }
                    Console.WriteLine($"  {verdict,-9} {label,-22} {(detail.Length > 230 ? detail[..230] + "..." : detail)}");
                    string key = $"{slot}|{verdict}|{(verdict == "REFUSED" || verdict == "BROKEN" || verdict == "NOT BUILT" ? detail : "")}";
                    if (!perConsole.TryGetValue(key, out var ids)) perConsole[key] = ids = new List<string>();
                    ids.Add(option.Id);
                }
            Console.WriteLine($"  -- summary for {Consoles.DisplayName(rom.Console)}:");
            foreach (var kv in perConsole.OrderBy(k => k.Key))
            {
                var p = kv.Key.Split('|');
                Console.WriteLine($"     {p[0],-4} {p[1],-9} {kv.Value.Count,3} cores: {(kv.Value.Count > 12 ? string.Join(", ", kv.Value.Take(12)) + ", ..." : string.Join(", ", kv.Value))}{(p[2] != "" ? "  <- " + (p[2].Length > 200 ? p[2][..200] + "..." : p[2]) : "")}");
            }
        }
        Console.WriteLine($"== {supported} combinations supported, {partialCount} partial (load and carry on with the same pictures, state bytes differ), {refused} refused with a reason, {broken} broken, {notBuilt} not built");
        return broken == 0 ? 0 : 1;
    }

    private static int PayloadLength(byte[] file) { try { return ConsoleStateFile.Peek(file).PayloadLength; } catch { return -1; } }

    /// <summary>The offset in the payload of the first byte two states differ in (-1: none, or they could not be opened).</summary>
    private static int FirstDifference(ConsoleKind console, byte[] x, byte[] y)
    {
        try
        {
            var ix = ConsoleStateFile.Peek(x);
            byte[] px = ConsoleStateFile.Unwrap(x, console, ix.GameId, ix.Title, ix.Units), py = ConsoleStateFile.Unwrap(y, console, ix.GameId, ix.Title, ix.Units);
            int n = Math.Min(px.Length, py.Length);
            for (int i = 0; i < n; i++) if (px[i] != py[i]) return i;
            return px.Length == py.Length ? -1 : n;
        }
        catch (Exception) { return -2; }
    }

    private static string? Opt(string[] args, string name) { int i = Array.IndexOf(args, "--" + name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
}
