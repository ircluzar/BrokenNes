using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NesEmulator.Mix;
using NesEmulator.Sega;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// <c>--md-save-test [--games name,name] [--frames N]</c>: battery saves of real games through the app's own session path (<c>ConsoleSessions.Create</c>, <c>HasBattery</c>, <c>ExportSave</c>, <c>ImportSave</c>, a file at the
/// desktop's <c>MdSaves\&lt;game id&gt;.srm</c> path). For each game: it is played with a scripted pad until it has written its battery (the image is no longer blank), the image is written to a file the way the app does and
/// read back into a new session (the image is identical), and the game <b>reads it</b>: a session started from the file and one started blank, given the same pad script, show different pictures (the game found its data:
/// a saved slot on its file screen) while two blank sessions show the same ones. A damaged file (every byte flipped) is not accepted as a save game: the game reacts to it (a picture different from the good file's run, or
/// the data formatted again). The comparison of these files with BlastEm's, byte for byte, after the same scripted play, is <c>oracle-tools/md-picture/rbatch.ps1 -Saves</c>.
/// </summary>
internal static class MdSaveTest
{
    private static int pass, fail, skipped;
    private static string poison = "";

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) pass++; else fail++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}{(detail == "" ? "" : "  (" + detail + ")")}");
    }

    /// <summary>A game with a battery, the library's file name, the pad script that makes it write its save, and how long to play.</summary>
    private sealed record Case(string Label, string File, string Pad, int Frames, string Kind, bool PictureShowsIt = true, int Every = 20);

    private static readonly Case[] Cases =
    {
        new("Sonic the Hedgehog 3", "Sonic The Hedgehog 3 (U) [!].gen", "default", 3000, "SRAM 512 bytes"),
        new("Sonic & Knuckles + Sonic 3", "Sonic and Knuckles & Sonic 3 (JUE) [!].bin", "default", 3000, "lock-on SRAM"),
        new("Phantasy Star IV", "Phantasy Star IV (4) [!].bin", "default", 3000, "SRAM 8 KB"),
        new("Shining Force", "Shining Force (U) [!].bin", "mash", 3600, "SRAM 8 KB"),
        new("NBA Jam Tournament Edition", "NBA Jam Tournament Edition (JUE) (REV 00) [!].bin", "default", 3600, "I2C EEPROM (24C08)", false),
        new("College Slam", "College Slam (4) [!].bin", "default", 3600, "I2C EEPROM (24C02)", false),
        new("Evander Holyfield's Real Deal Boxing", "Evander Holyfield's Real Deal Boxing (U) [!].bin", "default", 3600, "I2C EEPROM (X24C01)"),
        new("Phantasy Star II", "Phantasy Star II (UE) (REV 02) [!].bin", "default", 3000, "SRAM 8 KB"),
        new("Shining Force II", "Shining Force II (E) [!].bin", "mash", 3600, "SRAM 8 KB"),
        new("Landstalker", "Landstalker - The Treasures of King Nole (E) (Eng) [!].bin", "default", 3000, "SRAM 8 KB", false),
        new("Mega Man: The Wily Wars", "Mega Man - The Wily Wars (E).bin", "default", 1800, "3-wire EEPROM (93C46)"),
        new("Madden NFL 95", "Madden NFL 95 (JUE) [!].bin", "default", 3600, "headerless battery RAM", false),      // the one picture that differs between a file and a blank battery at the first frames moves with the interrupt timing (1 of 600 on the old sampling, 0 with BlastEm's): the evidence that it reads the file is the damaged-file check
    };

    public static int Run(string[] args)
    {
        pass = fail = skipped = 0;
        poison = M68kTestCli.Arg(args, "--poison") ?? "";       // "import": the loading session is not given the file (the check must notice)
        var only = M68kTestCli.Arg(args, "--games")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (M68kTestCli.Arg(args, "--slot-set") is { } slotSet) MdRunCli.ApplySlotSet(slotSet);        // experiment switches (e.g. "braipl=0;movemipl=0": the old interrupt sampling)
        string tmp =Path.Combine(Path.GetTempPath(), "mdsave-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var c in Cases)
            {
                if (only != null && !only.Any(o => c.Label.Contains(o, StringComparison.OrdinalIgnoreCase))) continue;
                string path = Path.Combine(MdRoster.Dir, c.File);
                if (!File.Exists(path)) { skipped++; Console.WriteLine($"  SKIP  {c.Label}: {path} not found"); continue; }
                try { OneGame(c, File.ReadAllBytes(path), tmp); }
                catch (Exception e) { Check($"{c.Label}: ran to its end", false, e.ToString().Replace('\n', ' ')); }
            }
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
        Console.WriteLine($"== {pass} passed, {fail} failed, {skipped} skipped");
        return fail == 0 ? 0 : 1;
    }

    private static IConsoleSession Make(Case c, byte[] rom) => ConsoleSessions.Create(ConsoleKind.Genesis, rom, "M68K", "MD", "MD", null, c.File);

    private static List<string> Play(IConsoleSession s, string pad, int frames, int pictureEvery)
    {
        var pics = new List<string>(); var buf = new short[16384];
        for (int f = 0; f < frames; f++)
        {
            s.SetPad(0, MdSweepCli.PadAt(pad, f));
            s.RunFrame();
            if (f % pictureEvery == 0) pics.Add(Convert.ToHexString(SHA1.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(s.Frame.AsSpan(0, s.FrameWidth * s.FrameHeight))))[..12]);
            while (s.ReadSamples(buf) > 0) { }
        }
        return pics;
    }

    private static string Describe(byte[] img) => $"{img.Length} bytes, {img.Count(b => b != 0 && b != 0xFF)} neither 00 nor FF";

    private static void OneGame(Case c, byte[] rom, string tmp)
    {
        string tag = c.Label;
        var a = Make(c, rom);
        bool blankBefore = a.HasBattery;
        byte[] initial = a.ExportSave();
        Play(a, c.Pad, c.Frames, 1000);
        byte[] save = a.ExportSave();
        bool written = a.HasBattery && !save.AsSpan().SequenceEqual(initial);
        Check($"{tag} ({c.Kind}): played {c.Frames} frames, the battery was written and the session reports it", written, written ? Describe(save) : $"HasBattery {a.HasBattery}, image {(save.AsSpan().SequenceEqual(initial) ? "unchanged" : "changed")}: {Describe(save)}");
        if (!written) return;

        // the desktop's file: MdSaves\<game id>.srm
        string file = Path.Combine(tmp, "MdSaves", a.GameId + ".srm");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, save);
        var b = Make(c, rom);
        if (poison != "import") b.ImportSave(File.ReadAllBytes(file));
        Check($"{tag}: a new session given that file reports a battery and exports the same bytes", b.HasBattery && b.ExportSave().AsSpan().SequenceEqual(save), Describe(b.ExportSave()));

        // the game reads it: with the file and without, the same script shows different pictures; with the file twice the same ones
        const int look = 2400;
        var withSave = Play(b, c.Pad, look, c.Every);
        var blank1 = Play(Make(c, rom), c.Pad, look, c.Every);
        var blank2 = Play(Make(c, rom), c.Pad, look, c.Every);
        var again = Make(c, rom); if (poison != "import") again.ImportSave(save); var withSave2 = Play(again, c.Pad, look, c.Every);
        int differing = Enumerable.Range(0, Math.Min(withSave.Count, blank1.Count)).Count(i => withSave[i] != blank1[i]);
        Check($"{tag}: two blank sessions show the same pictures under the same script (the comparison is deterministic)", blank1.SequenceEqual(blank2));
        Check($"{tag}: two sessions started from the file show the same pictures", withSave.SequenceEqual(withSave2));
        if (c.PictureShowsIt) Check($"{tag}: the game reads the file: a session from it and a blank one show different pictures under the same script", differing > 0, $"{differing} of {withSave.Count} sampled pictures differ");
        else Console.WriteLine($"  INFO  {tag}: the pad script does not reach a screen that shows the save ({differing} of {withSave.Count} sampled pictures differ): not claimed");

        // a damaged file is not taken for a save game
        var bad = (byte[])save.Clone(); for (int i = 0; i < bad.Length; i++) bad[i] ^= 0xFF;
        var d = Make(c, rom); d.ImportSave(bad); var damaged = Play(d, c.Pad, look, c.Every);
        byte[] afterDamaged = d.ExportSave();
        bool reacted = !damaged.SequenceEqual(withSave) || !afterDamaged.AsSpan().SequenceEqual(bad);
        Check($"{tag}: a damaged file (every byte flipped) makes the game react (other pictures than the good file's, or the data formatted again)", reacted, $"pictures {(damaged.SequenceEqual(withSave) ? "same" : "different")}, image {(afterDamaged.AsSpan().SequenceEqual(bad) ? "left as it was" : "rewritten")}");
    }
}
