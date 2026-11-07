using System;
using System.Collections.Generic;
using System.Diagnostics;
using NesEmulator;
using NesEmulator.Gb;
using NesEmulator.Mix;
using NesEmulator.Sega;
using NesEmulator.Snes;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// --apu-fuzz [--seconds S] [--seed N] [--only ID,ID]: robustness check for the foreign APU cores (docs/projects/foreign-apu-project.md). BrokenNes2's
/// corruption tools write garbage into chip registers and sound RAM, so every alternate sound core is driven here with random register writes (and, for
/// the SNES units, random sound RAM) for a few emulated seconds; a core that throws, returns NaN / out-of-range samples, or produces nothing fails.
/// The stock cores run too, as controls. Exit code 0 = every core survived.
/// </summary>
internal static class ForeignApuFuzzCli
{
    private static readonly string[] SnesIds = { "SFC", "ZS15", "S9X1", "OSPC", "S951", "S916", "ZS151", "SNDS", "PSPC3" };
    private static readonly string[] GbIds = { "GB", "GNUB", "VBA", "VBAM", "TGBD", "GYOB", "MGB" };
    private static readonly string[] MdIds = { "MD", "GENS", "PICO", "M036", "BLST", "GPGX", "GWEN" };
    private static readonly string[] SmsIds = { "SMS", "SMS-FM", "GENS", "PICO", "M036", "BLST", "GPGX", "GWEN", "DARC", "DEGA", "M036FM", "E055" };
    private static readonly string[] NesIds = { "FIX", "FCEU", "INES", "NSTC", "DARC", "TUXN", "VNES", "PNES", "NSDS", "PCEG", "POKY", "AY124", "NOFR", "RSID16", "FSID" };

    public static int Run(string[] args)
    {
        if (M68kTestCli.Flag(args, "--swap")) return GbSwap();
        if (M68kTestCli.Flag(args, "--md-reload")) return MdReload();
        if (M68kTestCli.Flag(args, "--fm-reload")) return FmReload();
        double seconds = double.TryParse(M68kTestCli.Arg(args, "--seconds"), out double s) ? s : 3;
        int seed = int.TryParse(M68kTestCli.Arg(args, "--seed"), out int sd) ? sd : 1234;
        var only = M68kTestCli.Arg(args, "--only")?.ToUpperInvariant().Split(',');
        bool Want(string id) => only == null || Array.IndexOf(only, id) >= 0;
        int fails = 0, runs = 0;
        void Report(string console, string id, Func<Random, (long samples, int bad)> body)
        {
            if (!Want(id)) return;
            runs++;
            var sw = Stopwatch.StartNew();
            long alloc0 = GC.GetAllocatedBytesForCurrentThread();
            string verdict;
            try
            {
                var (n, bad) = body(new Random(seed));
                double kbPerSec = (GC.GetAllocatedBytesForCurrentThread() - alloc0) / 1024.0 / seconds;
                verdict = n == 0 ? "FAIL no samples" : bad > 0 ? $"FAIL {bad} bad samples" : $"ok   {n:N0} samples, {kbPerSec:N0} KB allocated per emulated second";
            }
            catch (Exception e) { verdict = $"FAIL {e.GetType().Name}: {e.Message.Split('\n')[0]} @ {Where(e)}"; }
            if (verdict.StartsWith("FAIL")) fails++;
            Console.WriteLine($"{console,-8} {id,-7} {verdict}  ({sw.ElapsedMilliseconds} ms)");
        }

        foreach (var id in SnesIds) Report("SNES", id, rnd => FuzzSnes(id, rnd, seconds));
        foreach (var id in GbIds) Report("GB", id, rnd => FuzzGb(id, rnd, seconds));
        foreach (var id in MdIds) Report("Genesis", id, rnd => FuzzMd(id, rnd, seconds));
        foreach (var id in SmsIds) Report("SMS", id, rnd => FuzzSms(id, rnd, seconds));
        foreach (var id in NesIds) Report("NES", id, rnd => FuzzNes(id, rnd, seconds));
        Console.WriteLine($"\n{runs - fails} of {runs} cores survived {seconds} s of random register writes (seed {seed})");
        return fails == 0 ? 0 : 1;
    }

    /// <summary>--apu-fuzz --swap: a held square note on one Game Boy sound chip, then a live swap to another (as GbSession.TrySwapCore does):
    /// is the note still heard in the next 200 ms, without and with GbSession.RetriggerSounding (KNOWN_GAPS FA-4)?</summary>
    private static int GbSwap()
    {
        static double Rms(IGbApu a, int tCycles)
        {
            var buf = new short[16384]; double sum = 0; long n = 0;
            for (int done = 0, seq = 0; done < tCycles; done += 4096)
            {
                a.Tick(4096); if ((seq += 4096) >= 8192) { seq -= 8192; a.FrameSequencerStep(); }
                int k; while ((k = a.ReadSamples(buf)) > 0) for (int i = 0; i < k; i++) { sum += (double)buf[i] * buf[i]; n++; }
            }
            return n == 0 ? 0 : Math.Sqrt(sum / n);
        }
        IGbApu Playing(string id)
        {
            var a = GbCores.CreateApu(id, GbModel.Dmg); a.ResetPostBoot();
            a.WriteRegister(0x26, 0x80); a.WriteRegister(0x24, 0x77); a.WriteRegister(0x25, 0xFF);
            a.WriteRegister(0x11, 0x80); a.WriteRegister(0x12, 0xF0); a.WriteRegister(0x13, 0x00); a.WriteRegister(0x14, 0x86);   // held square, ~ 440 Hz, no length
            Rms(a, 4_194_304 / 10);
            return a;
        }
        string[] ids = { "GB", "GNUB", "VBA", "VBAM", "TGBD", "GYOB", "MGB" };
        int bad = 0;
        foreach (var from in ids)
            foreach (var to in ids)
            {
                if (from == to) continue;
                double[] rms = new double[2];
                for (int fix = 0; fix < 2; fix++)
                {
                    var old = Playing(from); var next = GbCores.CreateApu(to, GbModel.Dmg);
                    using (var ms = new System.IO.MemoryStream())
                    {
                        using (var w = new System.IO.BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true)) old.SaveState(w);
                        ms.Position = 0; using var r = new System.IO.BinaryReader(ms); next.LoadState(r);
                    }
                    if (fix == 1) NesEmulator.Systems.GbSession.RetriggerSounding(old, next);
                    rms[fix] = Rms(next, 4_194_304 / 5);
                }
                bool heard = rms[1] > 200;
                if (!heard) bad++;
                Console.WriteLine($"{from,-5} -> {to,-5} note after swap: rms {rms[0],6:F0} without retrigger, {rms[1],6:F0} with{(heard ? "" : "   <- still silent")}");
            }
        Console.WriteLine(bad == 0 ? "every swap keeps the note sounding" : $"{bad} swaps still lose the note");
        return bad == 0 ? 0 : 1;
    }

    /// <summary>--apu-fuzz --md-reload: a game sets up an FM patch, the state is saved and loaded into a fresh unit of the same kind (a save-state load),
    /// then the game keys the next note on without rewriting the patch: does it sound? (KNOWN_GAPS FA-S-GENS-3 / FA-S-PICO-2 / FA-S-M036-3)</summary>
    private static int MdReload()
    {
        string[] ids = { "MD", "GENS", "PICO", "M036", "BLST", "GPGX", "GWEN" };
        int bad = 0;
        foreach (var id in ids)
        {
            var timeline = SegaTimeline.Genesis(SegaRegion.Ntsc);
            var machine = new SegaMachine(timeline, GenesisModel.Model1, 44100);
            MdSoundUnit Make() => id == "MD" ? new MdSoundUnit(machine) : (MdSoundUnit)GenesisSoundFactories.Create(id, machine);
            long master = 0; long step = (long)(timeline.MasterHz / 100);
            void W(MdSoundUnit u, int reg, byte v) { u.WriteYm(0, (byte)reg, master); u.WriteYm(1, v, master); }
            var a = Make(); a.Reset();
            W(a, 0xB0, 0x07); W(a, 0xB4, 0xC0);                                               // algorithm 7: all four operators heard
            foreach (int op in new[] { 0, 4, 8, 12 }) { W(a, 0x30 + op, 0x01); W(a, 0x40 + op, 0x10); W(a, 0x50 + op, 0x1F); W(a, 0x60 + op, 0); W(a, 0x70 + op, 0); W(a, 0x80 + op, 0x0F); }
            W(a, 0xA4, 0x22); W(a, 0xA0, 0x69);                                               // ~440 Hz
            for (int i = 0; i < 10; i++) { master += step; a.RunTo(master); a.ReadSamples(new short[16384]); }
            var b = Make();
            using (var ms = new System.IO.MemoryStream())
            {
                using (var w = new System.IO.BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true)) a.SaveState(w);
                ms.Position = 0; using var r = new System.IO.BinaryReader(ms); b.LoadState(r);
            }
            W(b, 0x28, 0xF0);                                                                  // the next note: key on, patch not rewritten
            double sum = 0; long n = 0; var buf = new short[16384];
            for (int i = 0; i < 20; i++) { master += step; b.RunTo(master); int k; while ((k = b.ReadSamples(buf)) > 0) for (int j = 0; j < k; j++) { sum += (double)buf[j] * buf[j]; n++; } }
            double rms = n == 0 ? 0 : Math.Sqrt(sum / n);
            bool ok = rms > 200; if (!ok) bad++;
            Console.WriteLine($"{id,-5} note after a state load: rms {rms,6:F0}{(ok ? "" : "   <- silent (the patch was lost)")}");
        }
        Console.WriteLine(bad == 0 ? "every Genesis unit keeps the game's instruments across a state load" : $"{bad} units lose the instruments");
        return bad == 0 ? 0 : 1;
    }

    /// <summary>--apu-fuzz --fm-reload: a held YM2413 note, the state saved and loaded into a fresh unit of the same kind: is the note still heard?</summary>
    private static int FmReload()
    {
        int bad = 0;
        foreach (var id in new[] { "SMS-FM", "M036FM", "E055" })
        {
            var timeline = SegaTimeline.MasterSystem(SegaRegion.Ntsc);
            var machine = new SegaMachine(timeline, GenesisModel.Model1, 44100);
            ISmsSound Make() => SmsSoundFactories.Create(id, new SmsSoundRequest(machine, ConsoleKind.MasterSystem, PsgVariant.Sega));
            var a = Make(); a.Reset(); a.WriteAudioControl(1);
            void W(ISmsSound u, int r, int v) { u.WriteFmAddress((byte)r); u.WriteFmData((byte)v); }
            W(a, 0x30, 0x30); W(a, 0x10, 0xAC); W(a, 0x20, 0x18);                     // piano, ~440 Hz, key on (held)
            var buf = new short[16384];
            for (int i = 0; i < 10; i++) { a.Run(10000); while (a.ReadSamples(buf) > 0) { } }
            var b = Make();
            using (var ms = new System.IO.MemoryStream())
            {
                using (var w = new System.IO.BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true)) a.SaveState(w);
                ms.Position = 0; using var r = new System.IO.BinaryReader(ms); b.LoadState(r);
            }
            double sum = 0; long n = 0;
            for (int i = 0; i < 10; i++) { b.Run(10000); int k; while ((k = b.ReadSamples(buf)) > 0) for (int j = 0; j < k; j++) { sum += (double)buf[j] * buf[j]; n++; } }
            double rms = n == 0 ? 0 : Math.Sqrt(sum / n);
            bool ok = rms > 100; if (!ok) bad++;
            Console.WriteLine($"{id,-7} held note after a state load: rms {rms,6:F0}{(ok ? "" : "   <- silent")}");
        }
        Console.WriteLine(bad == 0 ? "every FM unit keeps its held notes across a state load" : $"{bad} FM units go silent");
        return bad == 0 ? 0 : 1;
    }

    private static string Where(Exception e)
    {
        var st = new StackTrace(e, true);
        foreach (var f in st.GetFrames() ?? Array.Empty<StackFrame>())
        {
            var m = f.GetMethod();
            if (m?.DeclaringType != null && !(m.DeclaringType.Namespace ?? "").StartsWith("System") && !m.DeclaringType.Name.StartsWith("ThrowHelper")) return $"{m.DeclaringType.Name}.{m.Name}:{f.GetFileLineNumber()}";
        }
        return "?";
    }

    private static int Bad(ReadOnlySpan<short> _) => 0;     // 16-bit output cannot be out of range; kept for symmetry

    private static (long, int) FuzzSnes(string id, Random rnd, double seconds)
    {
        var unit = SnesCores.CreateApu(id);
        var apu = (unit as SfcApuAdapter)?.Inner ?? throw new InvalidOperationException($"{id} is not an APU_SFC unit");
        rnd.NextBytes(apu.Aram);
        long master = 0, samples = 0; var buf = new short[8192];
        const long MasterHz = 21_477_272;
        long end = (long)(seconds * MasterHz);
        while (master < end)
        {
            for (int k = 0; k < 8; k++)
            {
                byte reg = (byte)rnd.Next(0x80), val = (byte)rnd.Next(256);
                if (reg == 0x6C) val &= 0x7F;                            // keep the DSP out of soft reset most of the time
                apu.SmpWrite(0xF2, reg); apu.SmpWrite(0xF3, val);
            }
            if (rnd.Next(64) == 0) apu.Aram[rnd.Next(0x10000)] = (byte)rnd.Next(256);
            master += MasterHz / 500;
            apu.RunTo(master);
            int n; while ((n = apu.ReadSamples(buf)) > 0) samples += n;
        }
        // state: same core, stock -> this, this -> stock (what a save state and a live core swap do)
        APU_SFC Make(string i) => ((SfcApuAdapter)SnesCores.CreateApu(i)).Inner;
        RoundTrip(apu.SaveState, Make(id).LoadState);
        RoundTrip(Make("SFC").SaveState, Make(id).LoadState);
        RoundTrip(apu.SaveState, Make("SFC").LoadState);
        return (samples, 0);
    }

    /// <summary>Saves one unit and loads the bytes into another: throws if the load does.</summary>
    private static void RoundTrip(Action<System.IO.BinaryWriter> save, Action<System.IO.BinaryReader> load)
    {
        using var ms = new System.IO.MemoryStream();
        using (var w = new System.IO.BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true)) save(w);
        ms.Position = 0;
        using var r = new System.IO.BinaryReader(ms);
        load(r);
    }

    private static (long, int) FuzzGb(string id, Random rnd, double seconds)
    {
        var apu = GbCores.CreateApu(id, GbModel.Dmg);
        apu.ResetPostBoot();
        apu.WriteRegister(0x26, 0x80);
        long samples = 0; var buf = new short[8192];
        int ticks = (int)(seconds * 4_194_304), done = 0, seq = 0;
        while (done < ticks)
        {
            for (int k = 0; k < 6; k++) apu.WriteRegister(0x10 + rnd.Next(0x30), (byte)rnd.Next(256));
            if (rnd.Next(200) == 0) apu.WriteRegister(0x26, 0x80);      // keep the APU powered most of the time
            apu.Tick(2048); done += 2048;
            if ((seq += 2048) >= 8192) { seq -= 8192; apu.FrameSequencerStep(); }
            int n; while ((n = apu.ReadSamples(buf)) > 0) samples += n;
        }
        RoundTrip(apu.SaveState, GbCores.CreateApu(id, GbModel.Dmg).LoadState);
        RoundTrip(GbCores.CreateApu("GB", GbModel.Dmg).SaveState, GbCores.CreateApu(id, GbModel.Dmg).LoadState);
        RoundTrip(apu.SaveState, GbCores.CreateApu("GB", GbModel.Dmg).LoadState);
        return (samples, 0);
    }

    private static (long, int) FuzzMd(string id, Random rnd, double seconds)
    {
        var timeline = SegaTimeline.Genesis(SegaRegion.Ntsc);
        var machine = new SegaMachine(timeline, GenesisModel.Model1, 44100);
        var unit = GenesisSoundFactories.Create(id, machine) as MdSoundUnit ?? throw new InvalidOperationException($"{id} is not an MdSoundUnit");
        unit.Reset();
        long master = 0, samples = 0, end = (long)(seconds * timeline.MasterHz); var buf = new short[8192];
        while (master < end)
        {
            for (int k = 0; k < 6; k++)
            {
                int part = rnd.Next(2);
                unit.WriteYm(part * 2, (byte)rnd.Next(0x21, 0xB8), master);
                unit.WriteYm(part * 2 + 1, (byte)rnd.Next(256), master);
            }
            unit.WritePsg((byte)rnd.Next(256), master);
            master += (long)(timeline.MasterHz / 500);
            unit.RunTo(master);
            int n; while ((n = unit.ReadSamples(buf)) > 0) samples += n;
        }
        MdSoundUnit MakeMd(string i) => i == "MD" ? new MdSoundUnit(machine) : (MdSoundUnit)GenesisSoundFactories.Create(i, machine);
        RoundTrip(unit.SaveState, MakeMd(id).LoadState);
        RoundTrip(MakeMd("MD").SaveState, MakeMd(id).LoadState);
        RoundTrip(unit.SaveState, MakeMd("MD").LoadState);
        return (samples, 0);
    }

    private static (long, int) FuzzSms(string id, Random rnd, double seconds)
    {
        var timeline = SegaTimeline.MasterSystem(SegaRegion.Ntsc);
        var machine = new SegaMachine(timeline, GenesisModel.Model1, 44100);
        var unit = SmsSoundFactories.Create(id, new SmsSoundRequest(machine, ConsoleKind.MasterSystem, PsgVariant.Sega));
        unit.Reset();
        if (unit.HasFm) unit.WriteAudioControl(1);
        long samples = 0; var buf = new short[8192];
        int cpu = (int)(seconds * timeline.MasterHz / timeline.Z80.Divider), done = 0;
        while (done < cpu)
        {
            for (int k = 0; k < 4; k++) unit.WritePsg((byte)rnd.Next(256));
            if (rnd.Next(4) == 0) unit.WriteStereo((byte)rnd.Next(256));
            if (unit.HasFm) for (int k = 0; k < 4; k++) { unit.WriteFmAddress((byte)rnd.Next(0x40)); unit.WriteFmData((byte)rnd.Next(256)); }
            unit.Run(7000); done += 7000;
            int n; while ((n = unit.ReadSamples(buf)) > 0) samples += n;
        }
        ISmsSound MakeSms(string i) => SmsSoundFactories.Create(i, new SmsSoundRequest(machine, ConsoleKind.MasterSystem, PsgVariant.Sega));
        string stock = unit.HasFm ? "SMS-FM" : "SMS";
        RoundTrip(unit.SaveState, MakeSms(id).LoadState);
        RoundTrip(MakeSms(stock).SaveState, MakeSms(id).LoadState);
        RoundTrip(unit.SaveState, MakeSms(stock).LoadState);
        return (samples, 0);
    }

    private static (long, int) FuzzNes(string id, Random rnd, double seconds)
    {
        var rom = System.IO.File.ReadAllBytes("Plugin/BrokenNes2.Plugin/Resources/musictest.nes");
        var nes = new NES();
        nes.LoadROM(rom);
        if (!nes.SetApuCore(id)) throw new InvalidOperationException($"no NES APU core {id}");
        long samples = 0;
        int frames = (int)(seconds * 60);
        for (int f = 0; f < frames; f++)
        {
            for (int k = 0; k < 12; k++)
            {
                ushort addr = (ushort)(0x4000 + rnd.Next(0x18));
                if (addr == 0x4014 || addr == 0x4016) continue;                // OAM DMA / controller strobe are not sound
                nes.PokeCpu(addr, (byte)rnd.Next(256));
            }
            nes.RunFrame();
            var a = nes.GetAudioBuffer();
            foreach (var x in a) if (float.IsNaN(x) || float.IsInfinity(x)) throw new InvalidOperationException("NaN/Inf sample");
            samples += a.Length;
        }
        string st = nes.SaveState();
        var other = new NES(); other.LoadROM(rom); other.SetApuCore(id); other.LoadState(st); other.RunFrame();
        var stockNes = new NES(); stockNes.LoadROM(rom); stockNes.SetApuCore("FIX"); stockNes.LoadState(st); stockNes.RunFrame();
        return (samples, 0);
    }
}
