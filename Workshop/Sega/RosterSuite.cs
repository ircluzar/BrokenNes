using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NesEmulator.Mix;
using NesEmulator.Sega;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Sega;

/// <summary>The 20 Master System and 20 Game Gear roster games (docs/projects/sega-rosters.md) by the file names the libraries use, and the pad scripts that play them (Workshop/Sega/mesen/roster-scripts.tsv).</summary>
internal static class RosterSuite
{
    public static readonly (string Folder, string File)[] Roster =
    {
        ("sms", "Astro Warrior (USA).sms"), ("sms", "Fantasy Zone (World) (v1.2).sms"), ("sms", "Alex Kidd in Miracle World (USA, Europe) (v1.1).sms"), ("sms", "Black Belt (USA, Europe).sms"),
        ("sms", "Columns (USA, Europe).sms"), ("sms", "Bubble Bobble (Europe).sms"), ("sms", "Penguin Land (USA, Europe).sms"), ("sms", "Wonder Boy (USA, Europe).sms"),
        ("sms", "Psycho Fox (USA, Europe).sms"), ("sms", "Shinobi (USA, Europe).sms"), ("sms", "Out Run (USA, Europe).sms"), ("sms", "Space Harrier (USA, Europe).sms"),
        ("sms", "Sonic The Hedgehog (USA, Europe).sms"), ("sms", "R-Type (USA, Europe).sms"), ("sms", "Phantasy Star (USA, Europe) (v1.3).sms"), ("sms", "Golden Axe Warrior (USA, Europe).sms"),
        ("sms", "Wonder Boy III - The Dragon's Trap (USA, Europe).sms"), ("sms", "Sonic The Hedgehog 2 (Europe).sms"), ("sms", "Micro Machines (Europe).sms"), ("sms", "Streets of Rage II (Europe).sms"),
        ("gg", "GG Demo by Charles MacDonald (PD).gg"), ("gg", "Columns (U) [!].gg"), ("gg", "Fantasy Zone Gear (JU) [!].gg"), ("gg", "Bubble Bobble (U) [!].gg"),
        ("gg", "Pac-Man (J).gg"), ("gg", "Wonder Boy (E) [!].gg"), ("gg", "Castle of Illusion Starring Mickey Mouse (U) [S][!].gg"), ("gg", "Land of Illusion Starring Mickey Mouse (U) [!].gg"),
        ("gg", "Sonic The Hedgehog (U) (V1.1) [!].gg"), ("gg", "Dragon Crystal (U) [!].gg"), ("gg", "GG Shinobi (E) [!].gg"), ("gg", "Shinobi II - The Silent Fury (U) [!].gg"),
        ("gg", "Sonic Chaos (U) [!].gg"), ("gg", "Sonic The Hedgehog 2 (U) [!].gg"), ("gg", "Tails' Adventures (U) [!].gg"), ("gg", "Defenders of Oasis (U) [!].gg"),
        ("gg", "Ristar the Shooting Star (U) [!].gg"), ("gg", "Micro Machines (E).gg"), ("gg", "Wonder Boy - The Dragon's Trap (E) [!].gg"), ("gg", "Shining Force II - The Sword of Hajya (U) [!].gg"),
    };

    /// <summary>The roster's pad script file, found by walking up from the current folder.</summary>
    public static string? ScriptFile()
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir != null; dir = dir.Parent)
        {
            string p = Path.Combine(dir.FullName, "Workshop", "Sega", "mesen", "roster-scripts.tsv");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>The pad script and length that play a roster game: the row of roster-scripts.tsv, or the sweep pattern for 2,400 frames.</summary>
    public static (string Pad, int Frames) Script(string file, bool gg)
    {
        if (ScriptFile() is { } tsv)
            foreach (var line in File.ReadLines(tsv))
            {
                if (line.StartsWith('#') || line.Length == 0) continue;
                var c = line.Split('\t');
                if (c.Length >= 4 && c[0] == file && c[1].Length > 0) return (c[1], int.Parse(c[3]));
            }
        return (SweepProbe.PadSpec(2400, gg), 2400);
    }

    /// <summary>The ROM of a roster game under <c>--roms</c> (folders <c>sms</c> and <c>gg</c>), or null.</summary>
    public static string? Find(SmsSuiteContext ctx, string folder, string file)
    {
        if (ctx.RomDir == null) return null;
        string p = Path.Combine(ctx.RomDir, folder, file);
        return File.Exists(p) ? p : null;
    }
}

/// <summary>
/// Save states (F7 save, F5 load) on every roster game, through the session the apps build: the game is played with its roster script to frame 1,000, the state is taken, and a fresh session that loads it runs on exactly as the
/// session that carried on does (the picture of each of 120 frames, every audio sample, the work RAM, the Z80). A state is refused, with the machine left as it was, by a session whose sound unit is another one (a fresh
/// session on another unit, and the same session after a hot swap of the sound unit).
/// </summary>
internal static class RosterStateSuite
{
    private const int Warm = 1000, Window = 120;

    private sealed record Trace(string[] Pictures, string Audio, string Ram, ushort Pc, long Cycles, string AtLoad);

    private static IConsoleSession Make(ConsoleKind console, byte[] rom, string name, string? apu = null) =>
        ConsoleSessions.Create(console, rom, CoreCatalog.Default(console, CoreSlot.Cpu), CoreCatalog.Default(console, CoreSlot.Ppu), apu ?? CoreCatalog.Default(console, CoreSlot.Apu), null, name);

    private static string Sha(ReadOnlySpan<byte> b) => Convert.ToHexString(SHA1.HashData(b))[..16];

    private static void Frames(IConsoleSession s, Dictionary<int, PadButtons> script, int from, int count, IncrementalHash? audio, List<string>? pictures)
    {
        PadButtons held = 0; var buf = new short[16384];
        foreach (var kv in script.Where(k => k.Key <= from).OrderBy(k => k.Key)) held = kv.Value;
        for (int f = from; f < from + count; f++)
        {
            if (script.TryGetValue(f, out var h)) held = h;
            s.SetPad(0, held);
            s.RunFrame();
            pictures?.Add(GoldenSuite.Hash(s));
            int n; while ((n = s.ReadSamples(buf)) > 0) audio?.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(buf.AsSpan(0, n)));
        }
    }

    private static Trace Capture(IConsoleSession s, Dictionary<int, PadButtons> script, int from, string atLoad)
    {
        var pics = new List<string>(); using var audio = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        Frames(s, script, from, Window, audio, pics);
        var b = SmsRomIo.Concrete(s).Board;
        return new Trace(pics.ToArray(), Convert.ToHexString(audio.GetHashAndReset())[..16], Sha(b.Ram), b.Cpu.PC, b.Cycles, atLoad);
    }

    public static IEnumerable<SmsCheck> Run(SmsSuiteContext ctx)
    {
        if (ctx.RomDir == null) { yield return SmsCheck.Skipped("roster save states", "no ROM directory (--roms <dir> with sms and gg folders)"); yield break; }
        foreach (var (folder, file) in RosterSuite.Roster)
        {
            string? path = RosterSuite.Find(ctx, folder, file);
            if (path == null) { yield return SmsCheck.Skipped(file, "ROM not found"); continue; }
            bool gg = folder == "gg";
            var console = gg ? ConsoleKind.GameGear : ConsoleKind.MasterSystem;
            byte[] rom = SmsRomIo.Load(path, out string inner);
            var (pad, _) = RosterSuite.Script(file, gg);
            var script = SmsRomIo.ParseInput(pad);
            var problems = new List<string>();

            var a = Make(console, rom, inner);
            Frames(a, script, 0, Warm, null, null);
            if (!a.SupportsState) { yield return new SmsCheck($"{file}: save state", false, a.StateUnsupportedReason); continue; }
            byte[] state = a.SaveState();
            string pictureAtSave = GoldenSuite.Hash(a);
            var carried = Capture(a, script, Warm, pictureAtSave);

            var b = Make(console, rom, inner);
            b.LoadState(state);
            if (ctx.Poison == "rosterstate") SmsRomIo.Concrete(b).Board.Ram[0x10] ^= 0x5A;   // the proof that this suite can fail: a work RAM byte the load "forgot"
            string atLoad = GoldenSuite.Hash(b);
            var restored = Capture(b, script, Warm, atLoad);
            if (restored.AtLoad != pictureAtSave) problems.Add("the picture right after the load is not the saved one");
            int bad = -1;
            for (int i = 0; i < Window && bad < 0; i++) if (carried.Pictures[i] != restored.Pictures[i]) bad = i;
            if (bad >= 0) problems.Add($"the picture differs from frame {Warm + 1 + bad} on");
            if (carried.Audio != restored.Audio) problems.Add("the audio differs");
            if (carried.Ram != restored.Ram) problems.Add("the work RAM differs");
            if (carried.Pc != restored.Pc || carried.Cycles != restored.Cycles) problems.Add($"the Z80 differs (PC {carried.Pc:X4}/{restored.Pc:X4}, cycles {carried.Cycles}/{restored.Cycles})");

            // a state is refused, with the machine left as it was, by another sound unit
            string other = gg ? "NES:FIX" : "SMS-FM";
            var c = Make(console, rom, inner, apu: other);
            Frames(c, script, 0, 5, null, null);
            byte[] before = c.SaveState();
            string how = "";
            try { c.LoadState(state); how = "loaded"; } catch (InvalidDataException) { how = "refused"; }
            if (how != "refused") problems.Add($"a session on {other} loaded a state saved on another sound unit");
            else if (!c.SaveState().SequenceEqual(before)) problems.Add("the refusing session was changed");
            var d = Make(console, rom, inner);
            Frames(d, script, 0, 5, null, null);
            if (!d.TrySwapCore(CoreSlot.Apu, other)) problems.Add("the sound swap was refused");
            else
            {
                byte[] beforeSwap = d.SaveState();
                try { d.LoadState(state); problems.Add("a session whose sound unit was swapped loaded a state saved on the stock unit"); } catch (InvalidDataException) { if (!d.SaveState().SequenceEqual(beforeSwap)) problems.Add("the swapped session was changed by the refusal"); }
            }
            yield return new SmsCheck($"{file}: state at frame {Warm} ({state.Length:N0} bytes) round trips for {Window} frames (picture, audio, RAM, Z80) and another sound unit refuses it", problems.Count == 0, string.Join("; ", problems));
        }
    }
}
