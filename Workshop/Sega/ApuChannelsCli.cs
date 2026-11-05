using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NesEmulator;
using NesEmulator.Gb;
using NesEmulator.Mix;
using NesEmulator.Sega;
using NesEmulator.Snes;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// --apu-channels [--console NES|SNES|GB|MD|SMS|GG|FM] [--only ID,ID] [--wav DIR]: Foreign APU follow-up phase 1 (docs/projects/foreign-apu-next-primer.md).
/// Every channel of every ported sound core is driven alone by direct register writes ("beep boop") and measured: RMS, peak, and a zero-crossing pitch,
/// compared with the stock core given the same writes. Verdicts: sounds / SILENT / WRONG PITCH (ratio to stock) / LOUDNESS (far off stock, informative).
/// Each cell's render is written as a WAV so a person can listen. A built-in negative control replays one cell per console with its key-on writes
/// skipped (a planted mute) and requires the check to call it silent. Exit 0 = no SILENT/WRONG PITCH cell outside the documented intended list, and
/// every negative control caught.
/// </summary>
internal static class ApuChannelsCli
{
    private sealed record Render(short[] Stereo, int Rate);
    private sealed record Cell(string Console, string Channel, bool Pitched, Func<string, bool, Render> Drive, bool StereoCheck = false);

    private static string wavDir = "";

    public static int Run(string[] args)
    {
        string? consoleArg = M68kTestCli.Arg(args, "--console")?.ToUpperInvariant();
        var only = M68kTestCli.Arg(args, "--only")?.ToUpperInvariant().Split(',');
        wavDir = M68kTestCli.Arg(args, "--wav") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VRUN_Nes_Dev_work", "fapu_build", "wav");
        Directory.CreateDirectory(wavDir);

        var consoles = new (string name, string stock, string[] ids, List<Cell> cells)[]
        {
            ("NES", "FIX", new[] { "FCEU", "INES", "NSTC", "DARC", "TUXN", "VNES", "PNES" }, NesCells()),
            ("SNES", "SFC", new[] { "ZS15", "ZS151", "S916", "S9X1", "S951", "OSPC", "SNDS" }, SnesCells()),
            ("GB", "GB", new[] { "GNUB", "VBA", "VBAM", "TGBD", "GYOB" }, GbCells()),
            ("MD", "MD", new[] { "GENS", "PICO", "M036", "BLST", "GPGX" }, MdCells()),
            ("SMS", "SMS", new[] { "GENS", "PICO", "M036", "BLST", "GPGX", "DARC", "DEGA" }, PsgCells(ConsoleKind.MasterSystem)),
            ("GG", "SMS", new[] { "GENS", "PICO", "M036", "BLST", "GPGX", "DARC", "DEGA" }, PsgCells(ConsoleKind.GameGear)),
            ("FM", "SMS-FM", new[] { "M036FM", "E055" }, FmCells()),
        };

        int bad = 0, cellsRun = 0, ncFail = 0, intended = 0;
        foreach (var (name, stock, ids, cells) in consoles)
        {
            if (consoleArg != null && consoleArg != name) continue;
            Console.WriteLine($"== {name} (stock {stock}) ==");
            Console.WriteLine($"{"channel",-14} {"core",-7} {"rms",7} {"peak",6} {"Hz",8}  verdict");
            foreach (var cell in cells)
            {
                var sref = Measure(cell, stock, false, out double refHz, out double refRms, out _);
                Console.WriteLine($"{cell.Channel,-14} {stock,-7} {refRms,7:F0} {Peak(sref.Stereo),6} {Hz(refHz),8}  reference");
                foreach (var id in ids)
                {
                    if (only != null && Array.IndexOf(only, id) < 0) continue;
                    cellsRun++;
                    string verdict;
                    double rms = 0, hz = 0; int peak = 0;
                    try
                    {
                        var r = Measure(cell, id, false, out hz, out rms, out string stereoNote);
                        peak = Peak(r.Stereo);
                        verdict = Judge(cell, rms, hz, refRms, refHz) + stereoNote;
                    }
                    catch (Exception e) { verdict = $"CRASH {e.GetType().Name}: {e.Message.Split('\n')[0]}"; }
                    bool flagged = verdict.StartsWith("SILENT") || verdict.StartsWith("WRONG") || verdict.StartsWith("CRASH") || verdict.Contains("PAN");
                    string? why = flagged && !verdict.StartsWith("CRASH") ? Intended(name, id, cell.Channel) : null;
                    if (why != null) { verdict += $"  [intended: {why}]"; intended++; }
                    else if (flagged) bad++;
                    Console.WriteLine($"{cell.Channel,-14} {id,-7} {rms,7:F0} {peak,6} {Hz(hz),8}  {verdict}");
                }
            }
            // negative control: the first cell with its key-on skipped must be judged silent, for the stock core and the first port
            var nc = cells[0];
            foreach (var id in new[] { stock, ids[0] })
            {
                Measure(nc, id, true, out double h, out double rr, out _);
                Measure(nc, stock, false, out double rh, out double rRef, out _);
                bool caught = Judge(nc, rr, h, rRef, rh).StartsWith("SILENT");
                if (!caught) ncFail++;
                Console.WriteLine($"negative control {name} {nc.Channel} {id}: planted mute {(caught ? "caught" : "NOT CAUGHT")}");
            }
        }
        Console.WriteLine($"\n{cellsRun} cells, {bad} flagged (SILENT / WRONG PITCH / CRASH / PAN), {intended} intended deviations (the original's sound), negative controls {(ncFail == 0 ? "all caught" : ncFail + " missed")}; WAVs in {wavDir}");
        return ncFail == 0 && bad == 0 ? 0 : 1;
    }

    /// <summary>Deviations that are the original emulator's sound (the primer's fidelity rule), each with the line in the port that documents it.
    /// A flagged cell listed here is reported but does not fail the run; anything else that deviates does.</summary>
    private static string? Intended(string console, string id, string channel) => (console, id) switch
    {
        ("NES", "NSTC") when channel.StartsWith("pulse") || channel == "triangle" => "NESticle pitch = 0x20000/period (NesticleSound.cs header)",
        ("NES", "NSTC") when channel is "noise" or "dmc" => "NESticle has no noise and no DMC (NesticleSound.cs, APU_NSTC.cs)",
        ("NES", "DARC") when channel is "noise" or "dmc" => "DarcNES 1.50: noise renderer #if 0, no DMC (DarcNesPsg.cs header)",
        ("NES", "INES") when channel == "dmc" => "InfoNES DMC rate constant ~10x small (InfoNesApu.cs header)",
        ("SNES", "ZS15") when channel.StartsWith("voice") => "ZSNES 0.15 restarts a loop one BRR block early: the 2-block test loop plays 3 blocks (Zs15Dsp.cs header)",
        ("SNES", "ZS15") when channel == "echo tail" => "ZSNES 0.15 has no echo (Zs15Dsp.cs header)",
        ("FM", "M036FM") when channel.StartsWith("fm") => "MAME 0.36 OPLL->OPL F-number unshifted: an octave low (Mame036Ym2413.cs header)",
        ("FM", "M036FM") when channel.StartsWith("rhythm") => "MAME 0.36 rhythm: one piano-like patch on channels 6-8 (Mame036Ym2413.cs header)",
        ("GG", "DEGA") when channel == "tone 1 right" => "Dega plays a side-muted channel at 1/4 (DegaPsg.cs header)",
        ("GG", _) when channel == "tone 1 right" => "this original has no Game Gear stereo (mono; see the SmsSound* summaries, FA-S-BLST-3)",
        _ => null,
    };

    private static string Hz(double hz) => hz <= 0 ? "-" : hz.ToString("F1");
    private static int Peak(short[] s) { int p = 0; foreach (var x in s) p = Math.Max(p, Math.Abs((int)x)); return p; }

    private static string Judge(Cell cell, double rms, double hz, double refRms, double refHz)
    {
        if (refRms < 30) return rms < 30 ? "silent (stock too)" : "sounds (stock silent)";
        if (rms < Math.Max(30, refRms * 0.03)) return "SILENT";
        if (cell.Channel == "echo tail" && rms < refRms * 0.25) return $"SILENT (no echo tail, x{rms / refRms:F2} of stock: the key-off release only)";
        string loud = rms > refRms * 8 ? $" (loud x{rms / refRms:F1})" : rms < refRms / 8 ? $" (quiet x{rms / refRms:F2})" : "";
        if (cell.Pitched && refHz > 0)
        {
            if (hz <= 0) return "WRONG PITCH (none found)" + loud;
            double ratio = hz / refHz;
            if (ratio < 0.94 || ratio > 1.06)
            {
                double oct = Math.Log2(ratio);
                string tag = Math.Abs(oct - Math.Round(oct)) < 0.08 ? $"{Math.Round(oct):+0;-0} octave" : $"x{ratio:F3}";
                return $"WRONG PITCH ({tag})" + loud;
            }
        }
        return "sounds" + loud;
    }

    /// <summary>Renders the cell on a core, writes the WAV, measures the window after 60 ms.</summary>
    private static Render Measure(Cell cell, string id, bool mute, out double hz, out double rms, out string stereoNote)
    {
        var r = cell.Drive(id, mute);
        if (!mute) WriteWav(Path.Combine(wavDir, $"{cell.Console}_{cell.Channel.Replace(' ', '_')}_{id}.wav"), r);
        int frames = r.Stereo.Length / 2, skip = cell.Pitched ? Math.Min(frames, r.Rate * 60 / 1000) : 0;   // drums and tails decay: unpitched cells measure from the key-on
        var mono = new double[frames - skip];
        double sumL = 0, sumR = 0;
        for (int i = skip; i < frames; i++)
        {
            double l = r.Stereo[2 * i], rr = r.Stereo[2 * i + 1];
            mono[i - skip] = (l + rr) / 2; sumL += l * l; sumR += rr * rr;
        }
        double mean = mono.Length == 0 ? 0 : mono.Average();
        double ss = 0; foreach (var x in mono) ss += (x - mean) * (x - mean);
        rms = mono.Length == 0 ? 0 : Math.Sqrt(ss / mono.Length);
        hz = cell.Pitched ? ZeroCrossHz(mono, mean, rms, r.Rate) : 0;
        stereoNote = "";
        if (cell.StereoCheck)
        {
            double rl = Math.Sqrt(sumL / Math.Max(1, mono.Length)), rr = Math.Sqrt(sumR / Math.Max(1, mono.Length));
            // the cell pans tone 1 to the right only: left should be (near) silent
            stereoNote = rr < 30 ? "" : rl > rr * 0.25 ? $" PAN IGNORED (L {rl:F0} R {rr:F0})" : $" panned (L {rl:F0} R {rr:F0})";
        }
        return r;
    }

    private static double ZeroCrossHz(double[] x, double mean, double rms, int rate)
    {
        if (rms < 1 || x.Length < 2) return 0;
        double th = rms * 0.3; bool armed = false; int first = -1, last = -1, n = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double v = x[i] - mean;
            if (v < -th) armed = true;
            else if (armed && v > th) { armed = false; if (first < 0) first = i; else n++; last = i; }
        }
        return n < 2 ? 0 : n * (double)rate / (last - first);
    }

    private static void WriteWav(string path, Render r)
    {
        using var w = new BinaryWriter(File.Create(path));
        int bytes = r.Stereo.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2);
        w.Write(r.Rate); w.Write(r.Rate * 4); w.Write((short)4); w.Write((short)16); w.Write("data"u8); w.Write(bytes);
        foreach (var s in r.Stereo) w.Write(s);
    }

    private const double Seconds = 0.5;

    // ---------------- NES ----------------
    private static byte[]? nesRom;
    /// <summary>NROM, 16 KB: an idle loop at $8100 (NMI off), the DMC sample at $C000 (= PRG offset 0): two $FF bytes, two $00 bytes, repeated.</summary>
    private static byte[] NesRom()
    {
        if (nesRom != null) return nesRom;
        var rom = new byte[16 + 16384 + 8192];
        rom[0] = (byte)'N'; rom[1] = (byte)'E'; rom[2] = (byte)'S'; rom[3] = 0x1A; rom[4] = 1; rom[5] = 1;
        int p = 16;
        for (int i = 0; i < 0x100; i++) rom[p + i] = (i & 2) == 0 ? (byte)0xFF : (byte)0x00;
        rom[p + 0x100] = 0x78; rom[p + 0x101] = 0x4C; rom[p + 0x102] = 0x01; rom[p + 0x103] = 0x81;  // SEI; loop: JMP loop
        rom[p + 0x200] = 0x40;                                                                         // RTI
        void Vec(int off, int a) { rom[p + off] = (byte)a; rom[p + off + 1] = (byte)(a >> 8); }
        Vec(0x3FFA, 0x8200); Vec(0x3FFC, 0x8100); Vec(0x3FFE, 0x8200);
        return nesRom = rom;
    }

    private static Render NesDrive(string id, (int addr, int v)[] setup, (int addr, int v)[] keyOn, bool mute)
    {
        var nes = new NES();
        nes.LoadROM(NesRom());
        if (!nes.SetApuCore(id)) throw new InvalidOperationException($"no NES APU core {id}");
        nes.RunFrame(); nes.GetAudioBuffer();
        nes.PokeCpu(0x4017, 0x40);
        foreach (var (a, v) in setup) nes.PokeCpu((ushort)a, (byte)v);
        if (!mute) foreach (var (a, v) in keyOn) nes.PokeCpu((ushort)a, (byte)v);
        var outp = new List<short>();
        for (int f = 0; f < Seconds * 60; f++)
        {
            nes.RunFrame();
            foreach (var x in nes.GetAudioBuffer()) { short s = (short)Math.Clamp(x * 32767f, -32768f, 32767f); outp.Add(s); outp.Add(s); }
        }
        return new Render(outp.ToArray(), nes.GetAudioSampleRate());   // each port outputs at its own rate (VNES 22,050 ...)
    }

    private static List<Cell> NesCells() => new()
    {
        new("NES", "pulse 1", true, (id, m) => NesDrive(id, new[] { (0x4015, 0x0F), (0x4000, 0xBF), (0x4001, 0x08), (0x4002, 0xFD) }, new[] { (0x4003, 0x08) }, m)),
        new("NES", "pulse 2", true, (id, m) => NesDrive(id, new[] { (0x4015, 0x0F), (0x4004, 0xBF), (0x4005, 0x08), (0x4006, 0xFD) }, new[] { (0x4007, 0x08) }, m)),
        new("NES", "triangle", true, (id, m) => NesDrive(id, new[] { (0x4015, 0x0F), (0x4008, 0xFF), (0x400A, 0x7E) }, new[] { (0x400B, 0x08) }, m)),
        new("NES", "noise", false, (id, m) => NesDrive(id, new[] { (0x4015, 0x0F), (0x400C, 0x3F), (0x400E, 0x04) }, new[] { (0x400F, 0x08) }, m)),
        new("NES", "dmc", true, (id, m) => NesDrive(id, new[] { (0x4015, 0x0F), (0x4010, 0x4F), (0x4011, 0x20), (0x4012, 0x00), (0x4013, 0x04) }, new[] { (0x4015, 0x1F) }, m)),
    };

    // ---------------- SNES ----------------
    private static Render SnesDrive(string id, int voice, bool echo, bool mute)
    {
        var apu = ((SfcApuAdapter)SnesCores.CreateApu(id)).Inner;
        // directory at $0200 (entry 0: start $0300, loop $0300); sample: 16 x +7 then 16 x -8, shift 11, loops (a 32-sample square)
        void Ram(int a, int v) => apu.SmpWrite((ushort)a, (byte)v);
        Ram(0x200, 0x00); Ram(0x201, 0x03); Ram(0x202, 0x00); Ram(0x203, 0x03);
        Ram(0x300, 0xB0); for (int i = 1; i <= 8; i++) Ram(0x300 + i, 0x77);
        Ram(0x309, 0xB3); for (int i = 1; i <= 8; i++) Ram(0x309 + i, 0x88);
        void D(int reg, int v) { apu.SmpWrite(0xF2, (byte)reg); apu.SmpWrite(0xF3, (byte)v); }
        const long MasterHz = 21_477_272;
        long master = 0; var outp = new List<short>(); var buf = new short[8192];
        void Run(double sec) { long end = master + (long)(sec * MasterHz); while (master < end) { master += MasterHz / 500; apu.RunTo(master); int n; while ((n = apu.ReadSamples(buf)) > 0) for (int i = 0; i < n; i++) outp.Add(buf[i]); } }
        D(0x6C, echo ? 0x00 : 0x20); D(0x0C, 0x7F); D(0x1C, 0x7F); D(0x5D, 0x02);
        D(0x2D, 0); D(0x3D, 0); D(0x5C, 0); D(0x4D, echo ? 1 << voice : 0);
        D(0x2C, echo ? 0x50 : 0); D(0x3C, echo ? 0x50 : 0);
        if (echo) { D(0x6D, 0x80); D(0x7D, 0x03); D(0x0D, 0x40); D(0x0F, 0x7F); for (int k = 1; k < 8; k++) D(k * 0x10 + 0x0F, 0); }
        int b = voice * 0x10;
        D(b + 0, 0x7F); D(b + 1, 0x7F); D(b + 2, 0x0A); D(b + 3, 0x07); D(b + 4, 0); D(b + 5, 0x8F); D(b + 6, 0xE0);
        Run(0.02); outp.Clear();
        if (!mute) D(0x4C, 1 << voice);
        if (!echo) { Run(Seconds); return new Render(outp.ToArray(), 32000); }
        // echo cell: key the voice off after 150 ms; what remains 60..400 ms after the key-off is the echo tail
        Run(0.15); D(0x5C, 1 << voice); D(0x4C, 0); outp.Clear(); Run(Seconds);
        return new Render(outp.ToArray(), 32000);
    }

    private static List<Cell> SnesCells()
    {
        var l = new List<Cell>();
        for (int v = 0; v < 8; v++) { int vv = v; l.Add(new("SNES", $"voice {v}", true, (id, m) => SnesDrive(id, vv, false, m))); }
        l.Add(new("SNES", "echo tail", false, (id, m) => SnesDrive(id, 0, true, m)));
        return l;
    }

    // ---------------- Game Boy ----------------
    private static Render GbDrive(string id, (int reg, int v)[] setup, (int reg, int v)[] keyOn, bool mute)
    {
        var a = GbCores.CreateApu(id, GbModel.Dmg); a.ResetPostBoot();
        a.WriteRegister(0x26, 0x80); a.WriteRegister(0x24, 0x77); a.WriteRegister(0x25, 0xFF);
        foreach (int dac in new[] { 0x12, 0x17, 0x21 }) a.WriteRegister(dac, 0x00); a.WriteRegister(0x1A, 0x00);   // every DAC off first: no post-boot residue (GYOB re-keys the boot note) in a cell
        foreach (var (r, v) in setup) a.WriteRegister(r, (byte)v);
        if (!mute) foreach (var (r, v) in keyOn) a.WriteRegister(r, (byte)v);
        var outp = new List<short>(); var buf = new short[16384]; int seq = 0;
        for (int done = 0; done < 4_194_304 * Seconds; done += 2048)
        {
            a.Tick(2048); if ((seq += 2048) >= 8192) { seq -= 8192; a.FrameSequencerStep(); }
            int k; while ((k = a.ReadSamples(buf)) > 0) for (int i = 0; i < k; i++) outp.Add(buf[i]);
        }
        return new Render(outp.ToArray(), a.SampleRate);
    }

    private static List<Cell> GbCells()
    {
        var wave = new List<(int, int)> { (0x1A, 0x00) };
        for (int i = 0; i < 16; i++) wave.Add((0x30 + i, i < 8 ? 0xFF : 0x00));   // one square cycle per 32 steps
        wave.Add((0x1A, 0x80)); wave.Add((0x1C, 0x20)); wave.Add((0x1D, 0x6B));
        return new()
        {
            new("GB", "square 1", true, (id, m) => GbDrive(id, new[] { (0x10, 0x00), (0x11, 0x80), (0x12, 0xF0), (0x13, 0xD6) }, new[] { (0x14, 0x86) }, m)),
            new("GB", "square 1 sweep", false, (id, m) => GbDrive(id, new[] { (0x10, 0x27), (0x11, 0x80), (0x12, 0xF0), (0x13, 0x00) }, new[] { (0x14, 0x84) }, m)),
            new("GB", "square 2", true, (id, m) => GbDrive(id, new[] { (0x16, 0x80), (0x17, 0xF0), (0x18, 0xD6) }, new[] { (0x19, 0x86) }, m)),
            new("GB", "wave", true, (id, m) => GbDrive(id, wave.ToArray(), new[] { (0x1E, 0x87) }, m)),
            new("GB", "noise", false, (id, m) => GbDrive(id, new[] { (0x21, 0xF0), (0x22, 0x50) }, new[] { (0x23, 0x80) }, m)),
        };
    }

    // ---------------- Genesis ----------------
    private static Render MdDrive(string id, Action<Action<int, int, int>, Action<int>> setup, Action<Action<int, int, int>, Action<int>> keyOn, bool mute, bool dac = false)
    {
        var timeline = SegaTimeline.Genesis(SegaRegion.Ntsc);
        var machine = new SegaMachine(timeline, GenesisModel.Model1, 44100);
        var u = id == "MD" ? new MdSoundUnit(machine) : (MdSoundUnit)GenesisSoundFactories.Create(id, machine);
        u.Reset();
        long master = 0;
        void Ym(int part, int reg, int v) { u.WriteYm(part * 2, (byte)reg, master); u.WriteYm(part * 2 + 1, (byte)v, master); }
        void Psg(int v) => u.WritePsg((byte)v, master);
        foreach (int c in new[] { 0x9F, 0xBF, 0xDF, 0xFF }) Psg(c);
        setup(Ym, Psg);
        if (!mute) keyOn(Ym, Psg);
        var outp = new List<short>(); var buf = new short[16384];
        long end = (long)(Seconds * timeline.MasterHz), step = (long)(timeline.MasterHz / (dac ? 1760 : 500)); bool hi = false;
        while (master < end)
        {
            if (dac && !mute) { hi = !hi; Ym(0, 0x2A, hi ? 0xE0 : 0x20); }
            master += step; u.RunTo(master);
            int k; while ((k = u.ReadSamples(buf)) > 0) for (int i = 0; i < k; i++) outp.Add(buf[i]);
        }
        return new Render(outp.ToArray(), u.SampleRate);
    }

    private static void FmPatch(Action<int, int, int> ym, int ch)
    {
        int part = ch / 3, off = ch % 3;
        ym(part, 0xB0 + off, 0x07); ym(part, 0xB4 + off, 0xC0);
        foreach (int op in new[] { 0, 4, 8, 12 })
        { ym(part, 0x30 + op + off, 0x01); ym(part, 0x40 + op + off, 0x10); ym(part, 0x50 + op + off, 0x1F); ym(part, 0x60 + op + off, 0); ym(part, 0x70 + op + off, 0); ym(part, 0x80 + op + off, 0x0F); ym(part, 0x90 + op + off, 0); }
        ym(part, 0xA4 + off, 0x22); ym(part, 0xA0 + off, 0x69);
    }

    private static List<Cell> MdCells()
    {
        var l = new List<Cell>();
        for (int c = 0; c < 6; c++)
        {
            int cc = c;
            l.Add(new("MD", $"fm {c + 1}", true, (id, m) => MdDrive(id, (ym, _) => { ym(0, 0x27, 0x00); ym(0, 0x2B, 0x00); FmPatch(ym, cc); }, (ym, _) => ym(0, 0x28, 0xF0 | (cc < 3 ? cc : cc + 1)), m)));
        }
        l.Add(new("MD", "fm 3 special", false, (id, m) => MdDrive(id, (ym, _) =>
        {
            ym(0, 0x27, 0x40); FmPatch(ym, 2);
            ym(0, 0xAD, 0x1A); ym(0, 0xA9, 0x00); ym(0, 0xAE, 0x22); ym(0, 0xAA, 0x69); ym(0, 0xAC, 0x2A); ym(0, 0xA8, 0x00);
        }, (ym, _) => ym(0, 0x28, 0xF2), m)));
        l.Add(new("MD", "dac (fm 6)", true, (id, m) => MdDrive(id, (ym, _) => { ym(1, 0xB6, 0xC0); ym(0, 0x2A, 0x80); }, (ym, _) => ym(0, 0x2B, 0x80), m, dac: true)));
        for (int t = 0; t < 3; t++) { int tt = t; l.Add(new("MD", $"psg tone {t + 1}", true, (id, m) => MdDrive(id, (_, psg) => { psg(0x80 | tt << 5 | (254 & 15)); psg(254 >> 4); }, (_, psg) => psg(0x90 | tt << 5), m))); }
        l.Add(new("MD", "psg noise", false, (id, m) => MdDrive(id, (_, psg) => psg(0xE4), (_, psg) => psg(0xF0), m)));
        return l;
    }

    // ---------------- Master System / Game Gear PSG, YM2413 ----------------
    private static Render SmsDrive(string id, ConsoleKind kind, bool fm, Action<ISmsSound> setup, Action<ISmsSound> keyOn, bool mute)
    {
        var timeline = SegaTimeline.MasterSystem(SegaRegion.Ntsc, kind);
        var machine = new SegaMachine(timeline, GenesisModel.Model1, 44100);
        var u = SmsSoundFactories.Create(id, new SmsSoundRequest(machine, kind, PsgVariant.Sega));
        u.Reset();
        if (fm) u.WriteAudioControl(1);
        foreach (int c in new[] { 0x9F, 0xBF, 0xDF, 0xFF }) u.WritePsg((byte)c);
        setup(u);
        if (!mute) keyOn(u);
        var outp = new List<short>(); var buf = new short[16384];
        int clocks = (int)(Seconds * timeline.MasterHz / timeline.Z80.Divider);
        for (int done = 0; done < clocks; done += 7000)
        {
            u.Run(7000);
            int k; while ((k = u.ReadSamples(buf)) > 0) for (int i = 0; i < k; i++) outp.Add(buf[i]);
        }
        return new Render(outp.ToArray(), u.SampleRate);
    }

    private static List<Cell> PsgCells(ConsoleKind kind)
    {
        string con = kind == ConsoleKind.GameGear ? "GG" : "SMS";
        var l = new List<Cell>();
        for (int t = 0; t < 3; t++) { int tt = t; l.Add(new(con, $"tone {t + 1}", true, (id, m) => SmsDrive(id, kind, false, u => { u.WritePsg((byte)(0x80 | tt << 5 | (254 & 15))); u.WritePsg(254 >> 4); }, u => u.WritePsg((byte)(0x90 | tt << 5)), m))); }
        l.Add(new(con, "noise", false, (id, m) => SmsDrive(id, kind, false, u => u.WritePsg(0xE4), u => u.WritePsg(0xF0), m)));
        if (kind == ConsoleKind.GameGear)
            l.Add(new(con, "tone 1 right", true, (id, m) => SmsDrive(id, kind, false, u => { u.WriteStereo(0x01); u.WritePsg(0x80 | (254 & 15)); u.WritePsg(254 >> 4); }, u => u.WritePsg(0x90), m), StereoCheck: true));
        return l;
    }

    private static List<Cell> FmCells()
    {
        var l = new List<Cell>();
        static void W(ISmsSound u, int r, int v) { u.WriteFmAddress((byte)r); u.WriteFmData((byte)v); }
        for (int c = 0; c < 9; c++)
        {
            int cc = c;
            l.Add(new("FM", $"fm {c + 1}", true, (id, m) => SmsDrive(id, ConsoleKind.MasterSystem, true, u => { W(u, 0x0E, 0x00); W(u, 0x30 + cc, 0x30); W(u, 0x10 + cc, 0xAC); }, u => W(u, 0x20 + cc, 0x18), m)));
        }
        string[] drums = { "bass drum", "snare", "tom", "cymbal", "hi-hat" };
        for (int d = 0; d < 5; d++)
        {
            int bit = 0x10 >> d;
            l.Add(new("FM", "rhythm " + drums[d], false, (id, m) => SmsDrive(id, ConsoleKind.MasterSystem, true, u =>
            {
                W(u, 0x16, 0x20); W(u, 0x17, 0x50); W(u, 0x18, 0xC0); W(u, 0x26, 0x05); W(u, 0x27, 0x05); W(u, 0x28, 0x01);
                W(u, 0x36, 0x00); W(u, 0x37, 0x00); W(u, 0x38, 0x00); W(u, 0x0E, 0x20);
            }, u => W(u, 0x0E, 0x20 | bit), m)));
        }
        return l;
    }
}
