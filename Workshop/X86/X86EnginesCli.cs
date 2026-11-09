using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NesEmulator.X86;

namespace BrokenNes.Workshop.X86;

/// <summary>
/// <c>--x86engines [--only ID,ID] [--wav dir]</c>: every AdLib engine (ymfm = "YMFM", then <see cref="OplEngines.Factories"/>) plays the same test through the card (Opl2 with the engine set):
/// each of the 9 melodic channels alone (a 440 Hz-ish note, then key-off), then the 5 rhythm instruments, then a save/load in the middle of a held note.
/// Verdicts per cell: sounds (rms above a floor), SILENT, CRASH; key-off must fall quiet (STUCK otherwise); after the load the note must continue sample-exact (FA-ST-1: the engine's own state is saved).
/// Exit code 0 = no flagged cell.
/// </summary>
internal static class X86EnginesCli
{
    private const long Pit = 1193182;

    private sealed class Rig
    {
        public long Clock;   // PIT ticks
        public readonly Opl2 Chip;
        public Rig(IOplEngine? e) { Chip = new Opl2(() => Clock) { Enabled = true }; Chip.Restart(); Chip.Engine = e; }
        public void W(int r, int v) { Chip.WriteAddress((byte)r); Chip.WriteData((byte)v); }
        public short[] Run(double seconds)
        {
            Clock += (long)(seconds * Pit);
            Chip.CatchUp();
            var b = new short[Chip.Pending];
            int n = Chip.MixInto(b, b.Length);
            return b.Take(n).Where((_, i) => i % 2 == 0).ToArray();
        }
    }

    private static double Rms(short[] s) => s.Length == 0 ? 0 : Math.Sqrt(s.Average(x => (double)x * x));

    private static void Patch(Rig r, int ch)
    {
        int[] op = { 0, 1, 2, 8, 9, 10, 16, 17, 18 };
        int m = op[ch], c = m + 3;
        r.W(0x20 + m, 0x01); r.W(0x20 + c, 0x01);
        r.W(0x40 + m, 0x10); r.W(0x40 + c, 0x00);
        r.W(0x60 + m, 0xF0); r.W(0x60 + c, 0xF0);
        r.W(0x80 + m, 0x77); r.W(0x80 + c, 0x77);
        r.W(0xE0 + m, 0); r.W(0xE0 + c, 0);
        r.W(0xC0 + ch, 0x00);
    }

    private static void KeyOn(Rig r, int ch) { r.W(0xA0 + ch, 0x41); r.W(0xB0 + ch, 0x32); }   // fnum 0x241 block 4 ~ 440 Hz
    private static void KeyOff(Rig r, int ch) => r.W(0xB0 + ch, 0x12);

    public static int Run(string[] args)
    {
        var only = BrokenNes.Workshop.Sega.M68kTestCli.Arg(args, "--only")?.ToUpperInvariant().Split(',');
        string? wavDir = BrokenNes.Workshop.Sega.M68kTestCli.Arg(args, "--wav");
        var ids = new List<string> { "YMFM" }.Concat(OplEngines.Factories.Keys).Where(i => only == null || only.Contains(i.ToUpperInvariant())).ToList();
        int flagged = 0;
        Console.WriteLine("engine   cell        rms     verdict");
        foreach (var id in ids)
        {
            IOplEngine? Make() => id == "YMFM" ? null : OplEngines.Factories[id]();
            var all = new List<short>();
            try
            {
                for (int ch = 0; ch < 9; ch++)
                {
                    var r = new Rig(Make());
                    r.W(0x01, 0x20);
                    Patch(r, ch); KeyOn(r, ch);
                    var on = r.Run(0.4); KeyOff(r, ch); r.Run(0.3); var off = r.Run(0.2);
                    all.AddRange(on);
                    string v = Rms(on) < 100 ? "SILENT" : Rms(off) > 60 ? "STUCK" : "sounds";
                    if (v != "sounds") flagged++;
                    Console.WriteLine($"{id,-8} ch{ch,-9} {Rms(on),7:F0}  {v}{(v == "STUCK" ? $" (rms {Rms(off):F0} after key-off)" : "")}");
                }
                string[] drums = { "bass drum", "snare", "tom", "cymbal", "hi-hat" };
                for (int d = 0; d < 5; d++)
                {
                    var r = new Rig(Make());
                    r.W(0x01, 0x20);
                    for (int ch = 6; ch < 9; ch++) Patch(r, ch);
                    r.W(0xA6, 0x41); r.W(0xB6, 0x09); r.W(0xA7, 0x41); r.W(0xB7, 0x09); r.W(0xA8, 0x41); r.W(0xB8, 0x09);
                    r.W(0xBD, 0x20 | (0x10 >> d));
                    var on = r.Run(0.3);
                    all.AddRange(on);
                    string v = Rms(on) < 40 ? "SILENT" : "sounds";
                    if (v != "sounds") flagged++;
                    Console.WriteLine($"{id,-8} {drums[d],-11} {Rms(on),7:F0}  {v}");
                }
                {
                    var r = new Rig(Make());
                    r.W(0x01, 0x20); Patch(r, 0); KeyOn(r, 0);
                    var a = r.Run(0.3);
                    var st = new MemoryStream(); var sw = new BinaryWriter(st); r.Chip.Save(sw); r.Chip.SaveEngine(sw); sw.Flush();
                    r.Chip.Restart();   // the saved machine goes on as a loaded one does (the drawn samples dropped), so A and B start alike
                    var a2 = r.Run(0.3);
                    var r2 = new Rig(Make()); r2.Clock = r.Clock - (long)(0.3 * Pit); st.Position = 0; var sr = new BinaryReader(st); r2.Chip.Load(sr); r2.Chip.LoadEngine(sr); r2.Chip.Restart();
                    var b = r2.Run(0.3);
                    // FA-ST-1: the engine's own state is saved, so the loaded chip plays the saved one's samples exactly
                    int n = Math.Min(a2.Length, b.Length); double d = 0; for (int i = 0; i < n; i++) d += (double)(a2[i] - b[i]) * (a2[i] - b[i]);
                    double err = n == 0 ? 1 : Math.Sqrt(d / n) / Math.Max(1, Rms(a2));
                    string v = Rms(b) < 100 ? "SILENT" : err == 0 && a2.Length == b.Length ? "exact" : $"NOT EXACT (err {err:F3})";
                    if (v != "exact") flagged++;
                    Console.WriteLine($"{id,-8} {"state",-11} {Rms(b),7:F0}  {v} (before the save {Rms(a):F0})");
                }
            }
            catch (Exception e) { flagged++; Console.WriteLine($"{id,-8} CRASH {e.GetType().Name}: {e.Message}"); }
            if (wavDir != null) { Directory.CreateDirectory(wavDir); WriteWav(Path.Combine(wavDir, $"adlib_{id}.wav"), all); }
        }
        // the menu: every x86 APU option swaps live on a running session and survives a save/load (FreeDOS boot disk, when the toolkit has it)
        string fd = PcDisks.FindFreeDos() ?? "";
        if (File.Exists(fd))
        {
            var opts = NesEmulator.Systems.CoreCatalog.Options(NesEmulator.Systems.ConsoleKind.Pc, NesEmulator.Systems.CoreSlot.Apu).Select(o => o.Id).ToList();
            foreach (var id in opts)
            {
                try
                {
                    var s = new PcSession(File.ReadAllBytes(fd), "freedos.img", id);
                    for (int f = 0; f < 30; f++) s.RunFrame();
                    bool swap = s.TrySwapCore(NesEmulator.Systems.CoreSlot.Apu, opts[(opts.IndexOf(id) + 1) % opts.Count]) && s.TrySwapCore(NesEmulator.Systems.CoreSlot.Apu, id);
                    var st = s.SaveState(); for (int f = 0; f < 10; f++) s.RunFrame(); s.LoadState(st); s.RunFrame();
                    bool ok = swap && s.ApuId == id.ToUpperInvariant();
                    if (!ok) flagged++;
                    Console.WriteLine($"menu     {id,-14} {(ok ? "boots, swaps live, loads a state" : "FAIL")}");
                }
                catch (Exception e) { flagged++; Console.WriteLine($"menu     {id,-14} CRASH {e.Message}"); }
            }
        }
        else Console.WriteLine("menu: no FreeDOS disk here, session check not run");
        Console.WriteLine($"\n{ids.Count} engines, {flagged} flagged");
        return flagged == 0 ? 0 : 1;
    }

    private static void WriteWav(string path, List<short> s)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + s.Count * 2); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(44100); w.Write(88200); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(s.Count * 2); foreach (var x in s) w.Write(x);
    }
}
