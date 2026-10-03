using System;
using System.Collections.Generic;
using System.IO;
using NesEmulator;
using NesEmulator.Sega;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// <c>--sega-contracts-test</c>: the Phase 0 contracts, by measurement. The region-parameterised master-clock timeline (both regions, exact integer cycle accounting),
/// the machine settings (region and Genesis model from the header, the name or the user), the optional-parts shape every board takes, the Genesis sound-unit handshake
/// against the stand-in unit, and the NES-register hub (PSG voices to NES registers to audio on a real NES APU core, with the pitch measured back).
/// Exit code 0 = every check passed.
/// </summary>
internal static class SegaContractsTestCli
{
    private static int failures, passes;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) passes++; else failures++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  [" + detail + "]" : "")}");
    }

    private static bool Near(double a, double b, double tol) => Math.Abs(a - b) <= tol;

    public static int Run(string[] args)
    {
        Console.WriteLine("== timeline"); Timeline();
        Console.WriteLine("\n== settings"); Settings();
        Console.WriteLine("\n== board parts"); BoardParts();
        Console.WriteLine("\n== genesis sound unit"); GenesisSound();
        Console.WriteLine("\n== NES hub"); Hub();
        Console.WriteLine($"\n{passes} passed, {failures} failed");
        return failures == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------------------------------------- timeline

    private static void Timeline()
    {
        foreach (var (region, master, lines, fps) in new[] { (SegaRegion.Ntsc, 53_693_175.0, 262, 59.9227), (SegaRegion.Pal, 53_203_424.0, 313, 49.7014) })
        {
            var t = SegaTimeline.Genesis(region);
            string r = region.ToString();
            Check($"Genesis {r}: master {master:N0} Hz, 3420 clocks x {lines} lines", t.MasterHz == master && t.MasterClocksPerLine == 3420 && t.LinesPerFrame == lines);
            Check($"Genesis {r}: {t.FramesPerSecond:F4} frames per second", Near(t.FramesPerSecond, fps, 0.0005));
            Check($"Genesis {r}: the Z80 gets exactly 228 cycles a line, the 68000 488 or 489", t.Z80.Due(0, 3420) == 228 && t.Cpu.Due(0, 3420) is 488 or 489);
            Check($"Genesis {r}: the PSG is fed the Z80 clock", t.Psg.Divider == t.Z80.Divider);
            Check($"Genesis {r}: YM2612 sample rate {t.Ym2612SampleRate:F1} Hz is master / 1008", Near(t.Ym2612SampleRate, master / 1008.0, 1e-6) && t.Ym2612SampleAt(1) == 1008);
            Check($"Genesis {r}: H32 pixel clock gives 342 pixels a line, H40 about 427", t.VdpPixelClock(false).Due(0, 3420) == 342 && t.VdpPixelClock(true).Due(0, 3420) is 427 or 428);
        }

        var ntsc = SegaTimeline.Genesis(SegaRegion.Ntsc);
        // The accounting is exact: however a span is cut up, the cycles add up to the same total, so a device never gains or loses one.
        var rng = new Random(12345);
        bool exact = true;
        foreach (var clock in new[] { ntsc.Cpu, ntsc.Z80, ntsc.Fm, ntsc.Vdp })
        {
            long at = 0; long sum = 0;
            for (int i = 0; i < 5000; i++) { long next = at + rng.Next(0, 5000); sum += clock.Due(at, next); at = next; }
            exact &= sum == clock.CyclesAt(at);
        }
        Check("cycle accounting is exact over 5,000 random cuts of the master clock, for each device clock", exact);
        long frame = ntsc.MasterClocksPerFrame;
        Check("one frame is 896,040 master clocks, about 128,005 68000 cycles and 59,736 Z80 cycles (NTSC)", frame == 896_040 && ntsc.Cpu.Due(0, frame) == 128_005 && ntsc.Z80.Due(0, frame) == 59_736);

        foreach (var (region, z80, fps) in new[] { (SegaRegion.Ntsc, 3_579_545.0, 59.9227), (SegaRegion.Pal, 3_546_895.0, 49.7014) })
        {
            var t = SegaTimeline.MasterSystem(region);
            string r = region.ToString();
            Check($"Master System {r}: master is three Z80 clocks ({t.MasterHz:N0} Hz)", t.MasterHz == z80 * 3);
            Check($"Master System {r}: 228 Z80 cycles and 342 pixels a line, {t.FramesPerSecond:F4} frames per second", t.Z80.Due(0, t.MasterClocksPerLine) == 228 && t.Vdp.Due(0, t.MasterClocksPerLine) == 342 && Near(t.FramesPerSecond, fps, 0.0005));
        }
        Check("the Game Gear shares the Master System timeline", SegaTimeline.MasterSystem(SegaRegion.Ntsc, ConsoleKind.GameGear).Console == ConsoleKind.GameGear);
        Check("the Genesis and Master System agree on the pixel clock (5.37 MHz NTSC)", Near(SegaTimeline.Genesis(SegaRegion.Ntsc).MasterHz / 10, SegaTimeline.MasterSystem(SegaRegion.Ntsc).MasterHz / 2, 1));
        bool threw = false; try { SegaTimeline.For(ConsoleKind.Nes, SegaRegion.Ntsc); } catch (ArgumentOutOfRangeException) { threw = true; }
        Check("a non-Sega console has no timeline", threw);
    }

    // ---------------------------------------------------------------------------------------------- settings

    private static byte[] FakeGenesis(string region, string productCode = "GM 00001009-00")
    {
        var rom = new byte[0x400];
        "SEGA MEGA DRIVE "u8.CopyTo(rom.AsSpan(0x100));
        System.Text.Encoding.ASCII.GetBytes(productCode).CopyTo(rom, 0x180);
        for (int i = 0; i < 16; i++) rom[0x1F0 + i] = (byte)' ';
        System.Text.Encoding.ASCII.GetBytes(region).CopyTo(rom, 0x1F0);
        return rom;
    }

    private static void Settings()
    {
        var md = ConsoleKind.Genesis;
        Check("Genesis header J, U and JUE are NTSC; E alone is PAL",
            SegaSettings.ResolveRegion(null, md, FakeGenesis("J")) == SegaRegion.Ntsc && SegaSettings.ResolveRegion(null, md, FakeGenesis("U")) == SegaRegion.Ntsc
            && SegaSettings.ResolveRegion(null, md, FakeGenesis("JUE")) == SegaRegion.Ntsc && SegaSettings.ResolveRegion(null, md, FakeGenesis("E")) == SegaRegion.Pal);
        Check("Genesis header as a hex mask: 8 (Europe) is PAL, 4 (Americas) and 1 (Japan) NTSC, F all",
            SegaSettings.ResolveRegion(null, md, FakeGenesis("8")) == SegaRegion.Pal && SegaSettings.ResolveRegion(null, md, FakeGenesis("4")) == SegaRegion.Ntsc
            && SegaSettings.ResolveRegion(null, md, FakeGenesis("1")) == SegaRegion.Ntsc && SegaSettings.ResolveRegion(null, md, FakeGenesis("F")) == SegaRegion.Ntsc);
        Check("a header with no region is NTSC", SegaSettings.ResolveRegion(null, md, FakeGenesis("")) == SegaRegion.Ntsc);
        Check("the user's region overrides the header either way",
            SegaSettings.ResolveRegion(RegionChoice.Pal, md, FakeGenesis("U")) == SegaRegion.Pal && SegaSettings.ResolveRegion(RegionChoice.Ntsc, md, FakeGenesis("E")) == SegaRegion.Ntsc);

        var none = Array.Empty<byte>();
        string[] pal = { "Alex Kidd (Europe).sms", "Sonic (E).sms", "Wonder Boy (Australia).sms", "Game (PAL).sms", "Fantasy Zone [E].sms" };
        string[] ntsc = { "Alex Kidd (USA).sms", "Sonic (Japan).sms", "Wonder Boy (USA, Europe).sms", "Game (World).sms", "Game (UE).sms", "Game (Brazil).sms", "Plain Name.sms", "Game (Rev 1).sms" };
        Check("Master System names with a European or Australian tag are PAL", Array.TrueForAll(pal, n => SegaSettings.ResolveRegion(null, ConsoleKind.MasterSystem, none, n) == SegaRegion.Pal));
        Check("Master System names with an American, Japanese, world or mixed tag, or none, are NTSC", Array.TrueForAll(ntsc, n => SegaSettings.ResolveRegion(null, ConsoleKind.MasterSystem, none, n) == SegaRegion.Ntsc));
        Check("the user's region overrides the name", SegaSettings.ResolveRegion(RegionChoice.Pal, ConsoleKind.MasterSystem, none, "Game (USA).sms") == SegaRegion.Pal);

        var saved = SegaSettings.GenesisModelDefault;
        try
        {
            SegaSettings.GenesisModelDefault = GenesisModelChoice.Auto;
            Check("Genesis model: Auto falls back to Model 1 (plan decision B)", SegaSettings.ResolveGenesisModel(null, FakeGenesis("U")) == GenesisModel.Model1);
            Check("Genesis model: a per-game choice wins", SegaSettings.ResolveGenesisModel(GenesisModelChoice.Model1, FakeGenesis("U")) == GenesisModel.Model1 && SegaSettings.ResolveGenesisModel(GenesisModelChoice.Model3, FakeGenesis("U")) == GenesisModel.Model3);
            SegaSettings.GenesisModelDefault = GenesisModelChoice.Model3;
            Check("Genesis model: the process default applies when the game has no choice, and a game's own Auto defers to it", SegaSettings.ResolveGenesisModel(null, FakeGenesis("U")) == GenesisModel.Model3 && SegaSettings.ResolveGenesisModel(GenesisModelChoice.Auto, FakeGenesis("U")) == GenesisModel.Model3);
            SegaSettings.GenesisModelDefault = GenesisModelChoice.Auto;
            SegaSettings.GenesisModelByProductCode["00001009-00"] = GenesisModel.Model3;
            Check("Genesis model: the per-title table is consulted by product code", SegaSettings.GenesisProductCode(FakeGenesis("U")) == "00001009-00" && SegaSettings.ResolveGenesisModel(null, FakeGenesis("U")) == GenesisModel.Model3 && SegaSettings.ResolveGenesisModel(null, FakeGenesis("U", "GM 00004049-01")) == GenesisModel.Model1);
        }
        finally { SegaSettings.GenesisModelDefault = saved; SegaSettings.GenesisModelByProductCode.Clear(); }

        Check("Master System model: Game Gear console is the Game Gear, .sg is SG-1000, .sc is SC-3000, anything else an SMS 2",
            SegaSettings.ResolveMasterSystemModel(null, ConsoleKind.GameGear, "a.gg") == MasterSystemModel.GameGear && SegaSettings.ResolveMasterSystemModel(null, ConsoleKind.MasterSystem, "a.sg") == MasterSystemModel.Sg1000
            && SegaSettings.ResolveMasterSystemModel(null, ConsoleKind.MasterSystem, "a.sc") == MasterSystemModel.Sc3000 && SegaSettings.ResolveMasterSystemModel(null, ConsoleKind.MasterSystem, "a.sms") == MasterSystemModel.Sms2
            && SegaSettings.ResolveMasterSystemModel(MasterSystemModel.Sms1, ConsoleKind.MasterSystem, "a.sms") == MasterSystemModel.Sms1);
    }

    // ---------------------------------------------------------------------------------------------- board parts

    private static void BoardParts()
    {
        var machine = new SegaMachine(SegaTimeline.Genesis(SegaRegion.Pal), GenesisModel.Model3, 48000);
        var stock = SegaBoardParts<string, int, double, string>.Stock;
        Check("stock parts: nothing replaced, the board's own builders run", stock.IsStock && stock.BuildCpu("bus", b => b.Length) == 3 && stock.BuildPpu(() => 1.5) == 1.5 && stock.BuildApu(machine, m => m.Region.ToString()) == "Pal");

        int cpuCalls = 0, apuCalls = 0; string? seenBus = null; SegaMachine? seenMachine = null;
        var custom = new SegaBoardParts<string, int, double, string>
        {
            Cpu = b => { cpuCalls++; seenBus = b; return 99; },
            Apu = m => { apuCalls++; seenMachine = m; return "custom"; },
        };
        int cpu = custom.BuildCpu("the-board", _ => -1); double ppu = custom.BuildPpu(() => 2.5); string apu = custom.BuildApu(machine, _ => "stock");
        Check("a CPU factory receives the board as the bus and replaces the stock CPU", !custom.IsStock && cpu == 99 && seenBus == "the-board" && cpuCalls == 1);
        Check("a part left null stays stock (the picture chip here)", ppu == 2.5);
        Check("a sound factory receives the machine (region, model, rate) and replaces the stock unit", apu == "custom" && apuCalls == 1 && seenMachine!.Region == SegaRegion.Pal && seenMachine.GenesisModel == GenesisModel.Model3 && seenMachine.SampleRate == 48000);
    }

    // ---------------------------------------------------------------------------------------------- Genesis sound unit

    private static byte[] Tone(int channel, int period) => new[] { (byte)(0x80 | channel << 5 | (period & 0xF)), (byte)(period >> 4 & 0x3F) };

    private static double ZeroCrossingHz(float[] x, int rate, int skipFrames)
    {
        double mean = 0; for (int i = skipFrames; i < x.Length; i++) mean += x[i]; mean /= Math.Max(1, x.Length - skipFrames);
        int crossings = 0;
        for (int i = skipFrames + 1; i < x.Length; i++) if (x[i - 1] - mean < 0 && x[i] - mean >= 0) crossings++;
        return crossings / ((x.Length - skipFrames) / (double)rate);
    }

    private static void GenesisSound()
    {
        const int rate = 44100;
        var timeline = SegaTimeline.Genesis(SegaRegion.Ntsc);
        var unit = new GenesisSoundStub(new SegaMachine(timeline, GenesisModel.Model2, rate));
        unit.Attach(new NullHost());
        unit.Reset();
        long t = 0;
        Check("power-on: the Z80 is held in reset and the bus is not granted", unit.IsInReset && !unit.IsBusGranted(t));

        unit.Write(0x0010, 0x42, t);
        Check("without the bus a write is lost and a read is open bus", unit.Read(0x0010, t) == 0xFF);
        unit.WriteBusRequest(true, t += 100);
        Check("request the bus: it is granted", unit.IsBusGranted(t));
        unit.Write(0x0010, 0x42, t += 100);
        Check("with the bus a write to Z80 RAM sticks and reads back, and the 8 KB mirrors at $2000", unit.Read(0x0010, t) == 0x42 && unit.Read(0x2010, t) == 0x42);
        unit.WriteBusRequest(false, t += 100);
        Check("give the bus back: the window closes again but the RAM keeps its contents", !unit.IsBusGranted(t) && unit.Read(0x0010, t) == 0xFF);
        unit.WriteBusRequest(true, t += 100);
        Check("... and reopens with the same byte", unit.Read(0x0010, t) == 0x42);
        unit.WriteReset(true, t += 100);
        Check("releasing RESET takes the Z80 out of reset", !unit.IsInReset);
        unit.WriteReset(false, t += 100);
        Check("asserting RESET puts it back", unit.IsInReset);

        int value = 0x1A5;
        for (int i = 0; i < 9; i++) unit.Write(0x6000, (byte)(value >> i & 1), t += 10);
        Check("the 9-bit bank register shifts in LSB first and shows 68000 space at bank << 15", unit.BankBase == value << 15, $"${unit.BankBase:X6}");

        // the PSG, written by the 68000 through the VDP, is the one part of the sound unit the stub really plays
        var samples = new List<float>(); var buf = new short[2048];
        unit.Reset(); t = 0;
        foreach (byte b in Tone(0, 254)) unit.WritePsg(b, t);
        unit.WritePsg(0x90, t);   // tone 1 volume 0 (full)
        for (long end = timeline.MasterClocksPerFrame; end <= timeline.MasterClocksPerFrame * 20; end += timeline.MasterClocksPerFrame)
        {
            unit.RunTo(end);
            int n = unit.ReadSamples(buf);
            for (int i = 0; i < n; i += 2) samples.Add(buf[i] / 32768f);
        }
        double hz = ZeroCrossingHz(samples.ToArray(), rate, 2000);
        double want = timeline.Z80.Divider == 15 ? 53_693_175.0 / 15 / (32 * 254) : 0;
        Check($"PSG tone 1 at period 254 plays {hz:F1} Hz (expect {want:F1})", Near(hz, want, want * 0.005));
        double perFrame = samples.Count / 20.0, wantPerFrame = rate / timeline.FramesPerSecond;
        Check($"audio keeps the master clock: {perFrame:F1} samples per NTSC frame (expect {wantPerFrame:F1})", Near(perFrame, wantPerFrame, 1.5));

        var pal = new GenesisSoundStub(new SegaMachine(SegaTimeline.Genesis(SegaRegion.Pal), GenesisModel.Model2, rate));
        pal.Reset(); foreach (byte b in Tone(0, 254)) pal.WritePsg(b, 0); pal.WritePsg(0x90, 0);
        long palFrame = SegaTimeline.Genesis(SegaRegion.Pal).MasterClocksPerFrame; int total = 0;
        for (int f = 1; f <= 20; f++) { pal.RunTo(palFrame * f); total += pal.ReadSamples(buf) / 2; }
        Check($"... and a PAL frame is longer: {total / 20.0:F1} samples per frame (expect {rate / SegaTimeline.Genesis(SegaRegion.Pal).FramesPerSecond:F1})", Near(total / 20.0, rate / SegaTimeline.Genesis(SegaRegion.Pal).FramesPerSecond, 1.5));

        // the Z80 window: the PSG at $7F11 is reachable once the 68000 holds the bus
        unit.Reset(); unit.WriteBusRequest(true, 0);
        long before = unit.PsgWrites;
        unit.Write(0x7F11, 0x90, 10);
        Check("the PSG port at $A07F11 reaches the PSG when the 68000 holds the bus", unit.PsgWrites == before + 1 && unit.Psg.Volume(0) == 0);

        // state round-trips
        unit.Reset(); unit.WriteBusRequest(true, 0); unit.Write(0x0123, 0x77, 0); unit.WritePsg(0x93, 0);
        var ms = new MemoryStream(); unit.SaveState(new BinaryWriter(ms));
        string before2 = unit.Describe();
        unit.Reset(); unit.WritePsg(0x9F, 0);
        ms.Position = 0; unit.LoadState(new BinaryReader(ms));
        unit.WriteBusRequest(true, 0);
        Check("state round-trips: RAM, handshake and PSG come back", unit.Read(0x0123, 0) == 0x77 && unit.Psg.Volume(0) == 3 && unit.Describe() == before2, unit.Describe());
        Check("the stub says what it is, so its silence is not mistaken for a working sound system", unit.CoreName == "STUB" && unit.Describe().Contains("no Z80"));
    }

    private sealed class NullHost : IGenesisSoundHost
    {
        public byte ReadBus(int address, long masterClock, out int waitMasterClocks) { waitMasterClocks = 0; return 0xFF; }
        public void WriteBus(int address, byte value, long masterClock, out int waitMasterClocks) { waitMasterClocks = 0; }
    }

    // ---------------------------------------------------------------------------------------------- NES hub

    private sealed class RecordingPort : INesRegisterPort
    {
        public int Banks { get; }
        public readonly List<(int Bank, ushort Address, byte Value)> Log = new();
        public readonly Dictionary<(int, ushort), byte> Last = new();
        public RecordingPort(int banks) { Banks = banks; }
        public void Write(int bank, ushort address, byte value) { Log.Add((bank, address, value)); Last[(bank, address)] = value; }
    }

    private static double PulseHzFromRegisters(RecordingPort p, int bank, ushort b) =>
        NesVoiceWriter.NesCpuHz / (16.0 * (((p.Last[(bank, (ushort)(b + 3))] & 7) << 8 | p.Last[(bank, (ushort)(b + 2))]) + 1));

    private static void Hub()
    {
        var voices = new HubVoice[8];
        var psg = new Sn76489(3_579_545.0, 44100);
        Span<HubVoice> v = voices;

        foreach (byte b in Tone(0, 254)) psg.Write(b);
        psg.Write(0x90);
        foreach (byte b in Tone(1, 127)) psg.Write(b);
        psg.Write(0xB4);                     // tone 2 volume 4
        foreach (byte b in Tone(2, 508)) psg.Write(b);
        psg.Write(0xD2);                     // tone 3 volume 2
        psg.Write(0xE0); psg.Write(0xF0);    // noise: periodic, fastest rate, full volume
        int n = psg.DescribeVoices(v);
        double tone1 = 3_579_545.0 / (32 * 254);
        Check("the PSG describes four voices: tone 1 and 2 as pulses, tone 3 as a triangle, noise as noise",
            n == 4 && voices[0].Kind == HubVoiceKind.Pulse && voices[1].Kind == HubVoiceKind.Pulse && voices[2].Kind == HubVoiceKind.Triangle && voices[3].Kind == HubVoiceKind.Noise);
        Check($"tone 1 reports {voices[0].Hz:F1} Hz at full level; tone 2 an octave up and quieter", Near(voices[0].Hz, tone1, 0.01) && voices[0].Level == 1f && Near(voices[1].Hz, tone1 * 2, 0.02) && voices[1].Level < voices[0].Level);
        Check("noise reports its shift rate (clock / 512) and the periodic mode as the short mode", Near(voices[3].Hz, 3_579_545.0 / 512, 0.5) && voices[3].ShortNoise);

        var port = new RecordingPort(1);
        var writer = new NesVoiceWriter(port);
        writer.Apply(voices.AsSpan(0, n));
        double back = PulseHzFromRegisters(port, 0, 0x4000);
        Check($"tone 1 on NES pulse 1: the timer registers decode to {back:F1} Hz (within 0.5% of {tone1:F1})", Near(back, tone1, tone1 * 0.005));
        Check("... at full volume, 50% duty, constant volume with length halted ($4000 = $BF)", port.Last[(0, 0x4000)] == 0xBF, $"${port.Last[(0, 0x4000)]:X2}");
        double tri = NesVoiceWriter.NesCpuHz / (32.0 * (((port.Last[(0, 0x400B)] & 7) << 8 | port.Last[(0, 0x400A)]) + 1));
        Check($"tone 3 on the NES triangle: {tri:F1} Hz (expect {voices[2].Hz:F1})", Near(tri, voices[2].Hz, voices[2].Hz * 0.005));
        Check("noise on the NES noise channel: the nearest rate (period index 9 = 254 cycles) and the short-mode bit", (port.Last[(0, 0x400E)] & 0x8F) == (0x80 | 9), $"${port.Last[(0, 0x400E)]:X2}");
        Check("the quiet tone 2 has a lower volume than tone 1", (port.Last[(0, 0x4004)] & 0x0F) < (port.Last[(0, 0x4000)] & 0x0F) && (port.Last[(0, 0x4004)] & 0x0F) > 0);

        long writes = writer.Writes;
        writer.Apply(voices.AsSpan(0, n));
        Check("a held note writes nothing (no phase restart, no cost)", writer.Writes == writes);

        psg.Write(0x9F);   // tone 1 off
        psg.DescribeVoices(v);
        writer.Apply(voices.AsSpan(0, 4));
        Check("a voice that goes quiet is silenced on its channel", (port.Last[(0, 0x4000)] & 0x0F) == 0 && !voices[0].Audible);

        // too many voices for one bank: the loudest keep the pulses, the rest are counted as dropped
        var crowd = new HubVoice[] { new(HubVoiceKind.Pulse, 220, 0.3f), new(HubVoiceKind.Pulse, 330, 0.9f), new(HubVoiceKind.Pulse, 440, 0.6f) };
        var one = new RecordingPort(1); var w1 = new NesVoiceWriter(one); w1.Apply(crowd);
        Check("three pulses on one bank: the two loudest play (in the order given), the quietest is dropped and counted",
            w1.Dropped == 1 && Near(PulseHzFromRegisters(one, 0, 0x4000), 330, 2) && Near(PulseHzFromRegisters(one, 0, 0x4004), 440, 3));
        var two = new RecordingPort(2); var w2 = new NesVoiceWriter(two); w2.Apply(crowd);
        Check("with an extension bank all three fit, each keeping its slot in the order given: the third pulse is on bank 1", w2.Dropped == 0 && w2.PulseSlots == 4 && Near(PulseHzFromRegisters(two, 0, 0x4000), 220, 2) && Near(PulseHzFromRegisters(two, 1, 0x4000), 440, 3));

        // Game Gear stereo and the DC period
        var gg = new Sn76489(3_579_545.0, 44100); foreach (byte b in Tone(0, 254)) gg.Write(b); gg.Write(0x90);
        gg.WriteStereo(0x00); gg.DescribeVoices(v);
        Check("a channel the Game Gear stereo port mutes on both sides is silent to the hub", !voices[0].Audible);
        gg.WriteStereo(0x11); gg.DescribeVoices(v);
        Check("... and audible again when either side is on", voices[0].Audible);
        var dc = new Sn76489(3_579_545.0, 44100); foreach (byte b in Tone(0, 1)) dc.Write(b); dc.Write(0x90); dc.DescribeVoices(v);
        Check("a period of 1 is a constant level on the Sega part, not a note: silent to the hub", !voices[0].Audible);

        // end to end: voices -> NES registers -> a real NES APU core -> audio, pitch measured back
        EndToEnd("pulse", 440.0, HubVoiceKind.Pulse, 3);
        EndToEnd("triangle", 220.0, HubVoiceKind.Triangle, 3);
    }

    private static void EndToEnd(string what, double hz, HubVoiceKind kind, int seconds)
    {
        var host = new NesEmulator.Mix.NesApuHost("FIX");
        var apu = host.Apu;
        var writer = new NesVoiceWriter(NesRegisterPorts.For(apu));
        writer.Apply(new[] { new HubVoice(kind, hz, 1f) });
        int rate = apu.GetSampleRate();
        var all = new List<float>();
        var buf = new float[8192];
        long cycles = (long)(NesVoiceWriter.NesCpuHz * seconds);
        for (long done = 0; done < cycles; done += 1000)
        {
            apu.Step(1000);
            int got = apu.ReadSamples(buf);
            for (int i = 0; i < got; i++) all.Add(buf[i]);
        }
        double measured = ZeroCrossingHz(all.ToArray(), rate, rate / 2);
        double amplitude = 0; for (int i = rate / 2; i < all.Count; i++) amplitude = Math.Max(amplitude, Math.Abs(all[i]));
        Check($"NES {what} core at {hz:F0} Hz through the hub measures {measured:F1} Hz (peak {amplitude:F2})", Near(measured, hz, hz * 0.01) && amplitude > 0.02);
    }
}
