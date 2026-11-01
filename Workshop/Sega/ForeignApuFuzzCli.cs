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
    private static readonly string[] SnesIds = { "SFC", "ZS15", "S9X1", "OSPC", "S951", "S916", "ZS151", "SNDS" };
    private static readonly string[] GbIds = { "GB", "GNUB", "VBA", "VBAM", "TGBD", "GYOB" };
    private static readonly string[] MdIds = { "MD", "GENS", "PICO", "M036", "BLST", "GPGX" };
    private static readonly string[] SmsIds = { "SMS", "SMS-FM", "GENS", "PICO", "M036", "BLST", "GPGX", "DARC", "DEGA", "M036FM", "E055" };
    private static readonly string[] NesIds = { "FIX", "FCEU", "INES", "NSTC", "DARC", "TUXN", "VNES", "PNES" };

    public static int Run(string[] args)
    {
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
            string verdict;
            try
            {
                var (n, bad) = body(new Random(seed));
                verdict = n == 0 ? "FAIL no samples" : bad > 0 ? $"FAIL {bad} bad samples" : $"ok   {n:N0} samples";
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
