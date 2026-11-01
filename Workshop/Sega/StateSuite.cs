using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NesEmulator.Sega;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// The board's save state (<c>BOARD_SMS.SaveState/LoadState</c>: work RAM, ports, cartridge mapper and RAM, VDP including its pending-write pipeline, the sound unit, the Z80): a round trip must be invisible.
/// A session runs to a point, the state is saved, the session runs on for a window and records every frame's picture hash, every audio sample and the work RAM; a second session on the same ROM loads the state and runs
/// the same window with the same input; the two records must be equal, frame for frame and sample for sample. Nothing in the app calls the state yet (IConsoleSession has no save-state member, and neither the
/// GB nor the SNES session offers one), so this checks the board's own mechanism, which an app-level feature would use.
/// </summary>
internal static class StateSuite
{
    private sealed record Case(string Name, Func<SmsSuiteContext, SmsSession> Make, string Pad, int SaveAt, int Window);

    private static byte[] Save(SmsSession s)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true)) s.Board.SaveState(w);
        return ms.ToArray();
    }

    private static (List<string> Hashes, List<short> Audio, byte[] Ram, ushort Pc, long Cycles) Window(SmsSession s, Dictionary<int, PadButtons> script, int from, int frames)
    {
        var hashes = new List<string>(); var audio = new List<short>(); var buf = new short[16384];
        PadButtons held = 0;
        foreach (var kv in script.Where(k => k.Key <= from).OrderBy(k => k.Key)) held = kv.Value;
        for (int f = from; f < from + frames; f++)
        {
            if (script.TryGetValue(f, out var h)) held = h;
            s.SetPad(0, held);
            s.RunFrame();
            hashes.Add(GoldenSuite.Hash(s));
            int n; while ((n = s.ReadSamples(buf)) > 0) for (int i = 0; i < n; i++) audio.Add(buf[i]);
        }
        return (hashes, audio, (byte[])s.Board.Ram.Clone(), s.Board.Cpu.PC, s.Board.Cycles);
    }

    public static IEnumerable<SmsCheck> Run(SmsSuiteContext ctx)
    {
        var cases = new List<Case>
        {
            new("Master System picture program, scrolling", c => c.Session(SmokeProgs.Picture(false), "state.sms"), "0:Right", 40, 40),
            new("Game Gear picture program, scrolling", c => c.Session(SmokeProgs.Picture(true), "state.gg", ConsoleKind.GameGear), "0:Right", 40, 40),
            new("Master System, SMS-FM with nine FM channels sounding (the sound unit's state inside the board's)", c => c.Session(FmProbes.NineChannels(out _), "state.sms", ConsoleKind.MasterSystem, null, "SMS-FM"), "", 30, 40),
        };
        if (ctx.RomDir is { } dir)
        {
            foreach (var (console, file) in new[] { ("sms", "Astro Warrior (USA).sms"), ("sms", "Shinobi (USA, Europe).sms"), ("gg", "Columns (U) [!].gg"), ("gg", "Fantasy Zone Gear (JU) [!].gg") })
            {
                string path = Path.Combine(dir, console, file);
                if (!File.Exists(path)) { continue; }
                bool gg = console == "gg";
                string name = file;
                cases.Add(new($"{file} (scripted play-through)", c => { byte[] rom = SmsRomIo.Load(path, out string inner); return c.Session(rom, inner, gg ? ConsoleKind.GameGear : ConsoleKind.MasterSystem); }, gg ? GoldenSuite.PadGg : GoldenSuite.PadSms, 420, 90));
            }
        }
        foreach (var c in cases)
        {
            var script = SmsRomIo.ParseInput(c.Pad);
            var a = c.Make(ctx);
            Window(a, script, 0, c.SaveAt);
            byte[] state = Save(a);
            var carried = Window(a, script, c.SaveAt, c.Window);                 // what the running session does next

            var b = c.Make(ctx);
            if (ctx.Poison == "state") { Window(b, script, 0, 1); }              // the poison: the second session has run a frame before the state arrives (a loader that does not replace everything leaves it showing)
            using (var r = new BinaryReader(new MemoryStream(state))) b.Board.LoadState(r);
            if (ctx.Poison == "state") b.Board.Ram[0x100] ^= 0x5A;               // and a work RAM byte the loader "forgot"
            var restored = Window(b, script, c.SaveAt, c.Window);

            int firstPicture = Enumerable.Range(0, c.Window).FirstOrDefault(i => carried.Hashes[i] != restored.Hashes[i], -1);
            yield return new SmsCheck($"{c.Name}: the picture after a state load equals the carried-on session's for {c.Window} frames", firstPicture < 0, firstPicture < 0 ? "" : $"first difference at window frame {firstPicture}");
            bool sameAudio = carried.Audio.Count == restored.Audio.Count && carried.Audio.SequenceEqual(restored.Audio);
            yield return new SmsCheck($"{c.Name}: the audio is the same, sample for sample ({carried.Audio.Count} shorts)", sameAudio, sameAudio ? "" : $"{carried.Audio.Count} vs {restored.Audio.Count} shorts");
            yield return new SmsCheck($"{c.Name}: work RAM, Z80 PC and cycle count are the same at the window's end", carried.Ram.AsSpan().SequenceEqual(restored.Ram) && carried.Pc == restored.Pc && carried.Cycles == restored.Cycles, $"PC {carried.Pc:X4}/{restored.Pc:X4}, cycles {carried.Cycles}/{restored.Cycles}");
        }
        if (ctx.RomDir == null) yield return SmsCheck.Skipped("state round trip on roster games", "no ROM directory (pass --roms <dir> with sms and gg folders): only the programs built here ran");
    }
}
