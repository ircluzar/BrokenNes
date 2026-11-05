using System;
using System.Collections.Generic;
using System.IO;
using NesEmulator;
using NesEmulator.Gb;
using NesEmulator.Mix;
using NesEmulator.Sega;
using NesEmulator.Snes;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// --apu-channels --state [--console X]: Foreign APU follow-up phase 2 (docs/projects/foreign-apu-next-primer.md). Per core: a chord is keyed,
/// 0.25 s plays, the state is SAVED mid-note, 0.25 s more plays (A); a fresh unit of the same core LOADS the state and plays 0.25 s (B).
/// A and B should match: err = rms(A - B) / rms(A) at the best lag within +-64 samples. Verdicts: exact / close (&lt; 0.05) / drifts (&lt; 0.5) /
/// DIFFERENT / SILENT AFTER LOAD. Negative control (first core of each console): skip the load (a fresh unkeyed unit) and require a fail verdict.
/// </summary>
internal static partial class ApuChannelsCli
{
    private interface IUnit { void Run(double sec, List<short> outp); byte[] Save(); void Load(byte[] st); long Clock { get; set; } }

    private static byte[] Ser(Action<BinaryWriter> save) { using var ms = new MemoryStream(); using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, true)) save(w); return ms.ToArray(); }
    private static void De(byte[] b, Action<BinaryReader> load) { using var r = new BinaryReader(new MemoryStream(b)); load(r); }

    private sealed class NesUnit : IUnit
    {
        public long Clock { get; set; }
        private readonly NES nes = new();
        public NesUnit(string id, bool keyed)
        {
            nes.LoadROM(NesRom()); if (!nes.SetApuCore(id)) throw new InvalidOperationException($"no NES APU core {id}");
            nes.RunFrame(); nes.GetAudioBuffer();
            if (!keyed) return;
            foreach (var (a, v) in new[] { (0x4017, 0x40), (0x4015, 0x1F), (0x4000, 0xBF), (0x4002, 0xFD), (0x4003, 0x08), (0x4004, 0x7F), (0x4006, 0xA9), (0x4007, 0x08),
                     (0x4008, 0xFF), (0x400A, 0x7E), (0x400B, 0x08), (0x4010, 0x4F), (0x4011, 0x20), (0x4012, 0), (0x4013, 0x04), (0x4015, 0x1F) })
                nes.PokeCpu((ushort)a, (byte)v);
        }
        public void Run(double sec, List<short> o)
        {
            for (int f = 0; f < sec * 60; f++) { nes.RunFrame(); foreach (var x in nes.GetAudioBuffer()) o.Add((short)Math.Clamp(x * 32767f, -32768f, 32767f)); }
        }
        public byte[] Save() => System.Text.Encoding.UTF8.GetBytes(nes.SaveState());
        public void Load(byte[] st) { nes.LoadState(System.Text.Encoding.UTF8.GetString(st)); nes.GetAudioBuffer(); }
    }

    private sealed class SnesUnit : IUnit
    {
        public long Clock { get => master; set => master = value; }   // a loaded unit continues on the saved unit's master clock
        private readonly APU_SFC apu; private long master; private readonly short[] buf = new short[8192];
        public SnesUnit(string id, bool keyed)
        {
            apu = ((SfcApuAdapter)SnesCores.CreateApu(id)).Inner;
            if (!keyed) return;
            void Ram(int a, int v) => apu.SmpWrite((ushort)a, (byte)v);
            Ram(0x200, 0x00); Ram(0x201, 0x03); Ram(0x202, 0x00); Ram(0x203, 0x03);
            Ram(0x300, 0xB0); for (int i = 1; i <= 8; i++) Ram(0x300 + i, 0x77);
            Ram(0x309, 0xB3); for (int i = 1; i <= 8; i++) Ram(0x309 + i, 0x88);
            void D(int reg, int v) { apu.SmpWrite(0xF2, (byte)reg); apu.SmpWrite(0xF3, (byte)v); }
            D(0x6C, 0x20); D(0x0C, 0x7F); D(0x1C, 0x7F); D(0x5D, 0x02); D(0x2D, 0); D(0x3D, 0); D(0x5C, 0); D(0x4D, 0); D(0x2C, 0); D(0x3C, 0);
            for (int v = 0; v < 3; v++) { int b = v * 16; D(b, 0x30); D(b + 1, 0x30); D(b + 2, 0x0A); D(b + 3, 0x07 + v * 2); D(b + 4, 0); D(b + 5, 0x8F); D(b + 6, 0xE0); }
            D(0x4C, 0x07);
        }
        public void Run(double sec, List<short> o)
        {
            const long Hz = 21_477_272; long end = master + (long)(sec * Hz);
            while (master < end) { master += Hz / 500; apu.RunTo(master); int n; while ((n = apu.ReadSamples(buf)) > 0) for (int i = 0; i < n; i += 2) o.Add(buf[i]); }
        }
        public byte[] Save() => Ser(apu.SaveState);
        public void Load(byte[] st) => De(st, apu.LoadState);
    }

    private sealed class GbUnit : IUnit
    {
        public long Clock { get; set; }
        private readonly IGbApu a; private readonly short[] buf = new short[16384]; private int seq;
        public GbUnit(string id, bool keyed)
        {
            a = GbCores.CreateApu(id, GbModel.Dmg); a.ResetPostBoot();
            a.WriteRegister(0x26, 0x80); a.WriteRegister(0x24, 0x77); a.WriteRegister(0x25, 0xFF);
            if (!keyed) return;
            foreach (var (r, v) in new[] { (0x10, 0), (0x11, 0x80), (0x12, 0xF0), (0x13, 0xD6), (0x14, 0x86), (0x16, 0x40), (0x17, 0xF0), (0x18, 0x00), (0x19, 0x87) })
                a.WriteRegister(r, (byte)v);
        }
        public void Run(double sec, List<short> o)
        {
            for (int done = 0; done < 4_194_304 * sec; done += 2048)
            {
                a.Tick(2048); if ((seq += 2048) >= 8192) { seq -= 8192; a.FrameSequencerStep(); }
                int k; while ((k = a.ReadSamples(buf)) > 0) for (int i = 0; i < k; i += 2) o.Add(buf[i]);
            }
        }
        public byte[] Save() => Ser(a.SaveState);
        public void Load(byte[] st) => De(st, a.LoadState);
    }

    private sealed class MdUnit : IUnit
    {
        public long Clock { get => master; set => master = value; }   // a loaded unit continues on the saved unit's master clock
        private readonly MdSoundUnit u; private long master; private readonly SegaTimeline t = SegaTimeline.Genesis(SegaRegion.Ntsc); private readonly short[] buf = new short[16384];
        public MdUnit(string id, bool keyed)
        {
            var machine = new SegaMachine(t, GenesisModel.Model1, 44100);
            u = id == "MD" ? new MdSoundUnit(machine) : (MdSoundUnit)GenesisSoundFactories.Create(id, machine);
            u.Reset();
            if (!keyed) return;
            void Ym(int part, int reg, int v) { u.WriteYm(part * 2, (byte)reg, master); u.WriteYm(part * 2 + 1, (byte)v, master); }
            FmPatch(Ym, 0); FmPatch(Ym, 4); Ym(1, 0xA5, 0x2A);
            Ym(0, 0x28, 0xF0); Ym(0, 0x28, 0xF5);
            u.WritePsg(0x80 | (254 & 15), master); u.WritePsg(254 >> 4, master); u.WritePsg(0x90, master);
        }
        public void Run(double sec, List<short> o)
        {
            long end = master + (long)(sec * t.MasterHz);
            while (master < end) { master += (long)(t.MasterHz / 500); u.RunTo(master); int k; while ((k = u.ReadSamples(buf)) > 0) for (int i = 0; i < k; i += 2) o.Add(buf[i]); }
        }
        public byte[] Save() => Ser(u.SaveState);
        public void Load(byte[] st) => De(st, u.LoadState);
    }

    private sealed class SmsUnit : IUnit
    {
        public long Clock { get; set; }
        private readonly ISmsSound u; private readonly short[] buf = new short[16384]; private readonly SegaTimeline t;
        public SmsUnit(string id, bool fm, bool keyed)
        {
            t = SegaTimeline.MasterSystem(SegaRegion.Ntsc);
            u = SmsSoundFactories.Create(id, new SmsSoundRequest(new SegaMachine(t, GenesisModel.Model1, 44100), ConsoleKind.MasterSystem, PsgVariant.Sega));
            u.Reset();
            if (fm) u.WriteAudioControl(1);
            if (!keyed) return;
            if (fm)
            {
                void W(int r, int v) { u.WriteFmAddress((byte)r); u.WriteFmData((byte)v); }
                W(0x30, 0x30); W(0x10, 0xAC); W(0x20, 0x18); W(0x31, 0x50); W(0x11, 0x20); W(0x21, 0x19);
            }
            else { u.WritePsg(0x80 | (254 & 15)); u.WritePsg(254 >> 4); u.WritePsg(0x90); u.WritePsg(0xA0 | 5); u.WritePsg(10); u.WritePsg(0xB2); }
        }
        public void Run(double sec, List<short> o)
        {
            int clocks = (int)(sec * t.MasterHz / t.Z80.Divider);
            for (int done = 0; done < clocks; done += 7000) { u.Run(7000); int k; while ((k = u.ReadSamples(buf)) > 0) for (int i = 0; i < k; i += 2) o.Add(buf[i]); }
        }
        public byte[] Save() => Ser(u.SaveState);
        public void Load(byte[] st) => De(st, u.LoadState);
    }

    private static int StateCheck(string[] args)
    {
        string? consoleArg = M68kTestCli.Arg(args, "--console")?.ToUpperInvariant();
        var sets = new (string con, string[] ids, Func<string, bool, IUnit> make)[]
        {
            ("NES", new[] { "FIX", "FCEU", "INES", "NSTC", "DARC", "TUXN", "VNES", "PNES" }, (id, k) => new NesUnit(id, k)),
            ("SNES", new[] { "SFC", "ZS15", "ZS151", "S916", "S9X1", "S951", "OSPC", "SNDS" }, (id, k) => new SnesUnit(id, k)),
            ("GB", new[] { "GB", "GNUB", "VBA", "VBAM", "TGBD", "GYOB" }, (id, k) => new GbUnit(id, k)),
            ("MD", new[] { "MD", "GENS", "PICO", "M036", "BLST", "GPGX" }, (id, k) => new MdUnit(id, k)),
            ("SMS", new[] { "SMS", "GENS", "PICO", "M036", "BLST", "GPGX", "DARC", "DEGA" }, (id, k) => new SmsUnit(id, false, k)),
            ("FM", new[] { "SMS-FM", "M036FM", "E055" }, (id, k) => new SmsUnit(id, true, k)),
        };
        int bad = 0, nc = 0;
        Console.WriteLine($"{"console",-6} {"core",-7} {"rms A",7} {"rms B",7} {"err",7} {"lag",4}  verdict");
        foreach (var (con, ids, make) in sets)
        {
            if (consoleArg != null && consoleArg != con) continue;
            foreach (var id in ids)
            {
                string verdict; double ra = 0, rb = 0, err = 0; int lag = 0;
                try
                {
                    (ra, rb, err, lag) = SaveLoadCompare(make(id, true), make(id, false), plant: false);
                    verdict = ra < 30 ? "NO SOUND BEFORE THE SAVE" : rb < ra * 0.1 ? "SILENT AFTER LOAD" : err < 1e-9 ? "exact" : err < 0.05 ? "close" : err < 0.5 ? "drifts" : "DIFFERENT";
                    if (id == ids[0])
                    {
                        var (_, nrb, nerr, _) = SaveLoadCompare(make(id, true), make(id, false), plant: true);
                        bool caught = nrb < ra * 0.1 || nerr >= 0.5;
                        if (!caught) nc++;
                        verdict += caught ? "   (negative control: skipped load caught)" : "   (NEGATIVE CONTROL MISSED)";
                    }
                }
                catch (Exception e) { verdict = $"CRASH {e.GetType().Name}: {e.Message.Split('\n')[0]}"; }
                if (verdict.StartsWith("SILENT") || verdict.StartsWith("DIFF") || verdict.StartsWith("CRASH") || verdict.StartsWith("NO SOUND")) bad++;
                Console.WriteLine($"{con,-6} {id,-7} {ra,7:F0} {rb,7:F0} {err,7:F3} {lag,4}  {verdict}");
            }
        }
        Console.WriteLine($"\n{bad} cores fail the save/load audio check; negative controls {(nc == 0 ? "all caught" : nc + " missed")}");
        return bad == 0 && nc == 0 ? 0 : 1;
    }

    private static (double ra, double rb, double err, int lag) SaveLoadCompare(IUnit a, IUnit fresh, bool plant)
    {
        var tmp = new List<short>();
        a.Run(0.25, tmp);
        byte[] st = a.Save(); long clockAtSave = a.Clock;
        var A = new List<short>(); a.Run(0.25, A);
        fresh.Clock = clockAtSave;
        if (!plant) fresh.Load(st);
        var B = new List<short>(); fresh.Run(0.25, B);
        int n = Math.Min(A.Count, B.Count) - 64;
        if (n <= 128) return (0, 0, 1, 0);
        double ra = RmsOf(A, n), rb = RmsOf(B, n);
        double best = double.MaxValue; int bestLag = 0;
        for (int lag = -64; lag <= 64; lag++)
        {
            double s = 0;
            for (int i = 64; i < n; i++) { double d = A[i] - B[i + lag]; s += d * d; }
            double e = Math.Sqrt(s / (n - 64)) / Math.Max(1, ra);
            if (e < best) { best = e; bestLag = lag; }
        }
        return (ra, rb, best, bestLag);
    }

    private static double RmsOf(List<short> x, int n) { double m = 0; for (int i = 0; i < n; i++) m += x[i]; m /= n; double s = 0; for (int i = 0; i < n; i++) s += (x[i] - m) * (x[i] - m); return Math.Sqrt(s / n); }
}
