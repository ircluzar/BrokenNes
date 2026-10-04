using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// <c>--ym-render --compare-logs a.vgm b.vgm [--skip N]</c>: compares what two emulators wrote to the sound chips while running the same game, from their VGM logs (ours from <c>--md-run --vgm</c>, BlastEm's from its own VGM logger, used as a program, never
/// read). The two runs do not start at the same instant, so nothing is compared by time. Two things are compared: the <b>notes</b> (every FM key-on as channel, operator mask, F-number and block, in order, from the first key-on of each log) and the <b>DAC
/// stream</b> (the values written to register $2A in order: the longest stretch of one log found unbroken in the other). A game's sound driver is deterministic, so two correct emulators play the same notes and read the same sample bytes; where they differ
/// the two emulators ran the driver differently (a timing, a handshake, or a bank-window difference). Exit code 0 = the notes agree on every event of the shorter log, 1 = they do not, 2 = usage.
/// </summary>
internal static class VgmCompare
{
    internal readonly record struct Note(int Channel, int Mask, int Fnum, int Block);

    private static List<Note> Notes(VgmFile v)
    {
        var low = new int[6]; var high = new int[6];
        var notes = new List<Note>();
        foreach (var e in v.Events)
        {
            if (e.Kind == VgmKind.Psg) continue;
            int part = e.Part(), reg = e.A, val = e.B;
            if (reg is 0xA0 or 0xA1 or 0xA2) low[reg - 0xA0 + 3 * part] = val;
            else if (reg is 0xA4 or 0xA5 or 0xA6) high[reg - 0xA4 + 3 * part] = val;
            else if (part == 0 && reg == 0x28 && (val & 0xF0) != 0)
            {
                int c = val & 7;
                if ((c & 3) == 3) continue;
                int ch = (c & 3) + ((c & 4) != 0 ? 3 : 0);
                notes.Add(new Note(ch, val >> 4, (high[ch] & 7) << 8 | low[ch], high[ch] >> 3 & 7));
            }
        }
        return notes;
    }

    private static byte[] Dac(VgmFile v) => v.Events.Where(e => e.Kind == VgmKind.Ym0 && e.A == 0x2A).Select(e => (byte)e.B).ToArray();

    public static int Run(string[] args)
    {
        if (args.Length < 4) { Console.WriteLine("usage: --ym-render --compare-logs a.vgm b.vgm [--skip N]"); return 2; }
        VgmFile a, b;
        try { a = VgmFile.Load(args[2]); b = VgmFile.Load(args[3]); }
        catch (Exception ex) { Console.WriteLine($"cannot read: {ex.Message}"); return 2; }
        int skip = int.TryParse(M68kTestCli.Arg(args, "--skip"), out int s) ? s : 0;
        string na = Path.GetFileName(args[2]), nb = Path.GetFileName(args[3]);
        Console.WriteLine($"   a {na}: {a.Events.Count:N0} writes, {a.EndSample / 44100.0:F1} s; b {nb}: {b.Events.Count:N0} writes, {b.EndSample / 44100.0:F1} s");

        var notesA = Notes(a).Skip(skip).ToList(); var notesB = Notes(b).Skip(skip).ToList();
        int agree = 0;
        while (agree < notesA.Count && agree < notesB.Count && notesA[agree] == notesB[agree]) agree++;
        int shorter = Math.Min(notesA.Count, notesB.Count);
        Console.WriteLine($"   notes: {notesA.Count:N0} FM key-ons in a, {notesB.Count:N0} in b (from key-on {skip}); they agree for the first {agree:N0} of {shorter:N0}");
        if (agree < shorter)
            Console.WriteLine($"   first difference at key-on {agree}: a {notesA[agree]} versus b {notesB[agree]}; after it, a: {string.Join(" ", notesA.Skip(agree + 1).Take(3))} | b: {string.Join(" ", notesB.Skip(agree + 1).Take(3))}");

        var da = Dac(a); var db = Dac(b);
        if (da.Length >= 64 && db.Length >= 64)
        {
            int bestLen = 0, bestA = -1, bestB = -1;
            for (int start = 0; start + 48 <= da.Length && start < 4000 && bestLen < 2000; start += 37)
            {
                int at = IndexOf(db, da.AsSpan(start, 48));
                if (at < 0) continue;
                int n = 0;
                while (start + n < da.Length && at + n < db.Length && da[start + n] == db[at + n]) n++;
                if (n > bestLen) { bestLen = n; bestA = start; bestB = at; }
            }
            if (bestLen == 0) Console.WriteLine($"   DAC: {da.Length:N0} writes in a, {db.Length:N0} in b; no common stretch of 48 values");
            else
            {
                int overlap = Math.Min(da.Length - bestA, db.Length - bestB);
                int equal = 0; for (int i = 0; i < overlap; i++) if (da[bestA + i] == db[bestB + i]) equal++;
                Console.WriteLine($"   DAC: {da.Length:N0} writes in a, {db.Length:N0} in b; longest unbroken common stretch {bestLen:N0} values (a from {bestA}, b from {bestB}); in that alignment {equal:N0} of {overlap:N0} values over the whole overlap are equal ({100.0 * equal / Math.Max(1, overlap):F1}%)");
            }
        }
        else Console.WriteLine($"   DAC: {da.Length:N0} writes in a, {db.Length:N0} in b (too few to compare)");
        return agree >= shorter ? 0 : 1;
    }

    private static int IndexOf(byte[] hay, ReadOnlySpan<byte> needle) => hay.AsSpan().IndexOf(needle);
}
