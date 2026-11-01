using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NesEmulator;
using NesEmulator.Mix;
using NesEmulator.Sega;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// The YM2413-on-NES-chips half of <c>--sega-bridge-test</c> (round 4, track C4b): group <c>smsfmnes</c>, the Japanese console's PSG and FM chip replayed on a NES sound chip (<see cref="SmsFmSoundOnNes"/>, id <c>NES-FM:&lt;chip&gt;</c>) and the voice-slot
/// allocator it shares with the Genesis bridge (<see cref="HubReplayer"/>). Figures are measured in captured audio against the stock <see cref="SmsFmSound"/> driven by the same register writes.
/// </summary>
internal static class SmsFmOnNesTests
{
    private static int passes, failures;
    private static bool verbose;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) passes++; else failures++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  [" + detail + "]" : "")}");
    }
    private static void Guard(Action group, string? name = null)
    {
        try { group(); }
        catch (Exception e) { Check($"{name ?? group.Method.Name} ran to the end", false, e.GetType().Name + ": " + e.Message); }
    }

    public static (int Passes, int Failures) Run(Func<string, bool> want, bool verboseOutput)
    {
        verbose = verboseOutput; passes = failures = 0;
        if (want("smsfmnes"))
        {
            Console.WriteLine("\n== NES-FM: the Japanese console's PSG and YM2413 on a NES chip, and the shared voice-slot allocator");
            Guard(Factory); Guard(Pitch); Guard(Level); Guard(Allocation); Guard(Stealing); Guard(Determinism); Guard(Rhythm); Guard(Gating); Guard(State); Guard(NoAllocation); Guard(Speed);
        }
        return (passes, failures);
    }

    // ---------------------------------------------------------------------------------------------- a rig that drives a unit like the board does
    private sealed class Rig
    {
        public readonly ISmsSound Unit;
        public readonly double ClockHz;
        private readonly List<float> samples = new();
        private readonly short[] buf = new short[8192];
        public Rig(string id, string? detailOverride = null)
        {
            var machine = new SegaMachine(SegaTimeline.MasterSystem(SegaRegion.Ntsc, ConsoleKind.MasterSystem), GenesisModel.Model1, 44100);
            var req = new SmsSoundRequest(machine, ConsoleKind.MasterSystem, PsgVariant.Sega);
            ClockHz = req.CpuClockHz;
            Unit = SmsSoundFactories.Create(detailOverride ?? id, req);
            Unit.Reset();
        }
        public SmsFmSound Real => Unit as SmsFmSound ?? ((SmsFmSoundOnNes)Unit).Inner;
        public void Fm(int reg, int value) { Unit.WriteFmAddress((byte)reg); Unit.Run(24); Unit.WriteFmData((byte)value); Unit.Run(100); }
        public void Psg(int value) { Unit.WritePsg((byte)value); Unit.Run(16); }
        /// <summary>FM channel <paramref name="ch"/> (0 to 8) key on at the pitch whose F-number is nearest to <paramref name="hz"/> (the carrier's own frequency also depends on the instrument's multiplier).</summary>
        public void Note(int ch, int instrument, int volume, double hz)
        {
            OpllPatchBank.TryPitch(hz, ClockHz, out int blk, out int fnum, out _);
            Fm(0x30 + ch, instrument << 4 | volume);
            Fm(0x10 + ch, fnum & 0xFF);
            Fm(0x20 + ch, 0x10 | blk << 1 | fnum >> 8);
        }
        public void KeyOff(int ch) { OpllPatchBank.TryPitch(440, ClockHz, out int blk, out int fnum, out _); Fm(0x20 + ch, blk << 1 | fnum >> 8); }
        public void Run(double seconds, bool keep = true)
        {
            long total = (long)(ClockHz * seconds), done = 0;
            int lines = 0;
            while (done < total) { int c = (int)Math.Min(228, total - done); Unit.Run(c); done += c; if (++lines % 16 == 0) Drain(keep); }
            Drain(keep);
        }
        public float[] Take() { var a = samples.ToArray(); samples.Clear(); return a; }
        private void Drain(bool keep)
        {
            int n;
            while ((n = Unit.ReadSamples(buf)) > 0)
                if (keep) for (int i = 0; i + 1 < n; i += 2) samples.Add((buf[i] + buf[i + 1]) / 65536f);
        }
    }

    private static HubReplayer Replayer(Rig r) => ((SmsFmSoundOnNes)r.Unit).Replayer;

    // ---------------------------------------------------------------------------------------------- the groups
    private static void Factory()
    {
        var r = new Rig("NES-FM:FIX");
        var u = r.Unit as SmsFmSoundOnNes;
        Check("NES-FM:FIX builds the FM bridge, NES-FM alone means FIX, and NES-FM:FIXS builds one on the enhanced chip (7 banks: every one of the 18 voices on a channel of its own)", u != null && u.BackApuId == "FIX" && new Rig("NES-FM").Unit is SmsFmSoundOnNes { BackApuId: "FIX" } && new Rig("NES-FM:FIXS").Unit is SmsFmSoundOnNes { BackApuId: "FIXS", Writer.Banks: 7 } && u.Writer.Banks == 1);
        Check("the unit has the FM chip (a game's detection finds it) and says what it is", u!.HasFm && u.CoreName.StartsWith("NES-FM:") && u.CoreName.Contains("YM2413"), u.CoreName);
        Check("the audio control register reads back what was written (the detection routine's test)", Detect(u));
        bool threw = false; try { new Rig("NES-FM:NOSUCHCHIP"); } catch (ArgumentException) { threw = true; }
        Check("an unknown NES chip is an error, never a quiet substitute", threw);
        var plain = new Rig("NES:FIX").Unit;
        Check("the plain NES unit still has no FM (the export console), so the two ids are different consoles", !plain.HasFm && plain.ReadAudioControl() == 0xFF);
    }

    private static bool Detect(ISmsSound u)
    {
        for (int i = 0; i < 7; i++) { u.WriteAudioControl(0); if (u.ReadAudioControl() != 0) return false; u.WriteAudioControl(1); if (u.ReadAudioControl() != 1) return false; }
        u.WriteAudioControl(0);
        return true;
    }

    private static void Pitch()
    {
        var worst = 0.0; var rows = new List<string>(); bool allFound = true;
        foreach (double hz in new[] { 130.0, 220.0, 440.0, 880.0, 1760.0 })
        {
            var r = new Rig("NES-FM:FIX"); r.Unit.WriteAudioControl(1);
            r.Note(0, 1, 0, hz); r.Run(0.3, false); r.Run(0.5);
            double chip = r.Real.Fm.ChannelHz(0);
            var x = r.Take();
            // the NES pulse's timer has 11 bits, so the note lands on the nearest period: 27 cents between periods at 1760 Hz, 14 at 880, 7 at 440, 3.5 at 220
            int period = (int)Math.Round(NesVoiceWriter.NesCpuHz / (16.0 * chip));
            double nesHz = NesVoiceWriter.NesCpuHz / (16.0 * period);
            var (found, amp) = SegaMeasure.Peak(x, 44100, nesHz, 4000, x.Length - 4000, 0.06);
            double c = SegaMeasure.Cents(found, nesHz), quantisation = SegaMeasure.Cents(nesHz, chip);
            allFound &= amp > 0.003;
            worst = Math.Max(worst, Math.Abs(c));
            rows.Add($"{chip:F1} Hz played {c:+0.00;-0.00} cent off its timer period, {quantisation:+0.0;-0.0} cent off the chip's");
            allFound &= Math.Abs(quantisation) <= 0.5 * 1200 * Math.Log2(1 + 1.0 / period) + 0.01;
        }
        Check($"an FM note's pitch on the NES chip is the nearest timer period to the chip's own (never more than half a period step off): {string.Join("; ", rows)}", allFound && worst < 0.8, $"worst {worst:F2} cent");
        var psg = new Rig("NES-FM:FIX");
        psg.Psg(0x80 | 0x00 | (254 & 0x0F)); psg.Psg(254 >> 4); psg.Psg(0x90);       // tone 1 period 254, volume 0
        psg.Run(0.3, false); psg.Run(0.4);
        var y = psg.Take();
        double psgHz = 3579545.0 / (32.0 * 254);
        var (fp, pa) = SegaMeasure.Peak(y, 44100, psgHz, 4000, y.Length - 4000, 0.06);
        Check($"a PSG tone comes through at its pitch too ({psgHz:F1} Hz)", pa > 0.003 && Math.Abs(SegaMeasure.Cents(fp, psgHz)) < 2.0, $"{SegaMeasure.Cents(fp, psgHz):+0.00;-0.00} cent");
    }

    /// <summary>The same writes on the stock unit and the bridge: the bridge replays each voice at the loudness the stock unit gives it, scaled by the one common factor (the NES pulse's loudest step against a full PSG tone).</summary>
    private static void Level()
    {
        double scale = APU_MD.PulseRms(2, 15) / (0.5 * new Sn76489(3579545, 44100, PsgVariant.Sega).Gain);
        scale = Math.Min(1.0, scale);
        var rows = new List<string>(); double worst = 0;
        foreach (int vol in new[] { 0, 1, 2, 3 })
        {
            double RmsOf(string id)
            {
                var r = new Rig(id); r.Unit.WriteAudioControl(1);
                r.Note(0, 3, vol, 330);
                r.Run(0.25, false); r.Run(0.3);
                var x = r.Take();
                return SegaMeasure.Rms(x, 2000, x.Length - 2000);
            }
            double stock = RmsOf("SMS-FM"), nes = RmsOf("NES-FM:FIX");
            double want = stock * scale, err = SegaMeasure.Db(nes, want);
            worst = Math.Max(worst, Math.Abs(err));
            rows.Add($"vol {vol}: stock {stock:F4} x {scale:F2} = {want:F4}, bridge {nes:F4} ({err:+0.0;-0.0} dB)");
        }
        // an FM instrument is not a sine (its waveform and its envelope move the figure a few dB) and the NES has 15 steps: the bound is the one the Genesis bridge keeps for a whole soundtrack
        Check($"FM levels follow the stock unit's to within 3 dB (instrument 3, the four loudest volumes): {string.Join("; ", rows)}", worst < 3.0, $"worst {worst:F2} dB");
        // the NES pulse's quietest step (volume 1) is 23.5 dB under its loudest; a voice more than 6 dB under that is not played, and counted
        {
            var q = new Rig("NES-FM:FIX"); q.Unit.WriteAudioControl(1); q.Note(0, 3, 9, 330); q.Run(0.3);
            var quiet = q.Take(); var u = (SmsFmSoundOnNes)q.Unit;
            Check("an FM voice below the NES's quietest step is silent on it and counted (volume 9 is 27 dB down)", SegaMeasure.Rms(quiet, 2000, quiet.Length - 2000) < 0.0003 && u.VoicesBelowNesFloor > 50, $"{u.VoicesBelowNesFloor} voice-syncs below the floor");
        }
        double PsgRms(string id, int vol)
        {
            var r = new Rig(id); r.Psg(0x80 | (254 & 0x0F)); r.Psg(254 >> 4); r.Psg(0x90 | vol);
            r.Run(0.2, false); r.Run(0.3); var x = r.Take(); return SegaMeasure.Rms(x, 2000, x.Length - 2000);
        }
        double worstPsg = 0; var prow = new List<string>();
        foreach (int vol in new[] { 0, 4, 8 })
        {
            double err = SegaMeasure.Db(PsgRms("NES-FM:FIX", vol), PsgRms("SMS-FM", vol) * scale);
            worstPsg = Math.Max(worstPsg, Math.Abs(err)); prow.Add($"vol {vol}: {err:+0.0;-0.0} dB");
        }
        Check($"PSG levels follow it too: {string.Join(", ", prow)}", worstPsg < 3.1, $"worst {worstPsg:F2} dB");
    }

    /// <summary>Nine FM channels and the PSG's three tones on NES:FIX (two pulses and a triangle for tones): only the loudest three are given channels, the rest are dropped and counted.</summary>
    private static void Allocation()
    {
        var r = new Rig("NES-FM:FIX"); r.Unit.WriteAudioControl(1);
        // channels 0 to 8 at volumes 0, 1, 2 ... 8 (increasingly quiet: 3 dB a step), all well above the pulse range
        for (int ch = 0; ch < 9; ch++) r.Note(ch, 1, ch, 220 * Math.Pow(2, ch / 12.0));
        r.Run(0.2);
        var rp = Replayer(r); var u = (SmsFmSoundOnNes)r.Unit;
        int[] owners = (int[])rp.Allocator.TonalOwner.Clone();
        var held = owners.Where(o => o >= 0).Select(o => o - 4).OrderBy(o => o).ToArray();    // FM channels (voices 4 to 12)
        Check($"3 tonal channels on NES:FIX hold the 3 loudest of nine FM voices ({string.Join(",", held)})", owners.Count(o => o >= 0) >= 2 && held.All(c => c <= 2), $"owners {string.Join(",", owners)}");
        Check("the others are dropped and counted, every sync", u.VoicesWithoutChannel > 100, $"{u.VoicesWithoutChannel} voice-syncs without a channel");
        r.Run(0.3);
        var again = rp.Allocator.TonalOwner;
        Check("held voices keep their channels while they sound (nothing flutters between slots)", owners.SequenceEqual(again), $"{string.Join(",", owners)} then {string.Join(",", again)}");
        var big = new Rig("NES-FM:FIXS"); big.Unit.WriteAudioControl(1);
        for (int ch = 0; ch < 9; ch++) big.Note(ch, 1, ch, 220 * Math.Pow(2, ch / 12.0));
        big.Run(0.2);
        var owners8 = Replayer(big).Allocator.TonalOwner; int bigPulses = ((SmsFmSoundOnNes)big.Unit).Writer.PulseSlots;
        int pulses = owners8.Take(bigPulses).Count(o => o >= 0), triangles = owners8.Skip(bigPulses).Count(o => o >= 0);
        Check($"NES:FIXS (enhanced: {bigPulses} pulses, {owners8.Length - bigPulses} triangles) carries all nine voices on pulses ({pulses}), none without a channel, and no triangle takes one ({triangles}): a pulse has the volume the triangle lacks", pulses == 9 && triangles == 0 && ((SmsFmSoundOnNes)big.Unit).VoicesWithoutChannel == 0, $"{string.Join(",", owners8)}; {((SmsFmSoundOnNes)big.Unit).VoicesWithoutChannel} without a channel");
        var quietVoices = new Rig("NES-FM:FIXS"); quietVoices.Unit.WriteAudioControl(1);
        for (int ch = 0; ch < 9; ch++) quietVoices.Note(ch, 1, 6 + ch % 3, 220 * Math.Pow(2, ch / 12.0));
        quietVoices.Run(0.2);
        Check("quiet voices never go to a triangle (it would play them far too loud)", Replayer(quietVoices).Allocator.TonalOwner.Skip(((SmsFmSoundOnNes)quietVoices.Unit).Writer.PulseSlots).All(o => o < 0), $"{string.Join(",", Replayer(quietVoices).Allocator.TonalOwner)}");
    }

    private static void Stealing()
    {
        var r = new Rig("NES-FM:FIX"); r.Unit.WriteAudioControl(1);
        for (int ch = 0; ch < 4; ch++) r.Note(ch, 1, 10, 220 * Math.Pow(2, ch / 12.0));      // four quiet voices on three channels
        r.Run(0.2);
        var rp = Replayer(r); var u = (SmsFmSoundOnNes)r.Unit;
        long stolen0 = u.VoicesStolen;
        int[] before = (int[])rp.Allocator.TonalOwner.Clone();
        r.Note(5, 1, 0, 440);                                                               // a much louder newcomer (30 dB)
        r.Run(0.1);
        int[] after = rp.Allocator.TonalOwner;
        Check("a newcomer 6 dB louder than the quietest held voice takes its channel (counted)", u.VoicesStolen > stolen0 && after.Contains(4 + 5), $"stolen {u.VoicesStolen - stolen0}; {string.Join(",", before)} -> {string.Join(",", after)}");
        Check("and only that one channel changes hands", Enumerable.Range(0, before.Length).Count(i => before[i] != after[i]) == 1, $"{string.Join(",", before)} -> {string.Join(",", after)}");
        // a newcomer that is not louder enough does not displace anyone
        var q = new Rig("NES-FM:FIX"); q.Unit.WriteAudioControl(1);
        for (int ch = 0; ch < 4; ch++) q.Note(ch, 1, 6, 220 * Math.Pow(2, ch / 12.0));
        q.Run(0.2); long s0 = ((SmsFmSoundOnNes)q.Unit).VoicesStolen;
        q.Note(5, 1, 5, 440); q.Run(0.1);
        Check("a newcomer only 3 dB louder takes nothing", ((SmsFmSoundOnNes)q.Unit).VoicesStolen == s0);
    }

    private static void Determinism()
    {
        string Digest()
        {
            var r = new Rig("NES-FM:FIX"); r.Unit.WriteAudioControl(1);
            for (int ch = 0; ch < 9; ch++) r.Note(ch, 1 + ch, ch % 4, 130 * Math.Pow(2, ch / 7.0));
            r.Psg(0x80 | 10); r.Psg(0x10); r.Psg(0x90); r.Psg(0xE5); r.Psg(0xF0);
            r.Run(0.4);
            var x = r.Take(); var u = (SmsFmSoundOnNes)r.Unit;
            ulong h = 14695981039346656037UL; foreach (float f in x) h = (h ^ (uint)BitConverter.SingleToInt32Bits(f)) * 1099511628211UL;
            return $"{h:X16}/{u.Writer.Writes}/{u.VoicesWithoutChannel}/{u.VoicesStolen}";
        }
        string a = Digest(), b = Digest();
        Check("the same writes give the same samples, register writes and counters (the policy is deterministic)", a == b, $"{a} vs {b}");
    }

    private static void Rhythm()
    {
        var r = new Rig("NES-FM:FIXS"); r.Unit.WriteAudioControl(1);
        // rhythm mode on, bass drum and snare and hi-hat keyed: instruments' volumes for the rhythm channels at $36-$38
        r.Fm(0x16, 0x20); r.Fm(0x26, 0x05); r.Fm(0x17, 0x50); r.Fm(0x27, 0x05); r.Fm(0x18, 0xC0); r.Fm(0x28, 0x01);
        r.Fm(0x36, 0x00); r.Fm(0x37, 0x00); r.Fm(0x38, 0x00);
        r.Fm(0x0E, 0x20 | 0x10 | 0x08 | 0x01);
        var u = (SmsFmSoundOnNes)r.Unit; var rp = Replayer(r);
        r.Run(0.05);
        var seen = new HashSet<int>();
        for (int i = 0; i < 40; i++) { r.Run(0.003); foreach (int o in rp.Allocator.TonalOwner.Concat(rp.Allocator.NoiseOwner)) if (o >= 0) seen.Add(o); }
        bool bass = seen.Contains(4 + 9), snare = seen.Contains(4 + 10), hat = seen.Contains(4 + 13);
        Check($"rhythm voices get channels (bass drum {bass}, snare {snare}, hi-hat {hat}) and are counted", (bass || snare || hat) && u.RhythmSyncs > 0, $"voices seen {string.Join(",", seen.OrderBy(x => x))}; rhythm syncs {u.RhythmSyncs}");
        r.Fm(0x0E, 0x20);
        r.Run(0.3, false);
        Check("with the rhythm keys released they fall silent and the channels are freed", Replayer(r).Allocator.NoiseOwner.All(o => o < 0));
    }

    private static void Gating()
    {
        var r = new Rig("NES-FM:FIX");
        r.Note(0, 1, 0, 440); r.Run(0.2);
        Check("FM off in the audio control register: the FM voices are silent (no NES channel held)", Replayer(r).Allocator.TonalOwner.All(o => o < 0) && !r.Real.FmEnabled);
        r.Unit.WriteAudioControl(1); r.Run(0.2);
        Check("FM on: the voice takes a channel", Replayer(r).Allocator.TonalOwner.Any(o => o == 4));
        r.Unit.WriteAudioControl(3); r.Psg(0x80 | 10); r.Psg(0x10); r.Psg(0x90); r.Run(0.2);
        Check("PSG muted by bit 1: the PSG's voices are silent while FM goes on", !Replayer(r).Allocator.TonalOwner.Any(o => o is >= 0 and < 3) && Replayer(r).Allocator.TonalOwner.Any(o => o == 4));
    }

    private static void State()
    {
        var a = new Rig("NES-FM:FIX"); a.Unit.WriteAudioControl(1); a.Note(0, 4, 2, 330); a.Note(1, 7, 5, 392); a.Run(0.2);
        byte[] state; using (var ms = new MemoryStream()) { using (var w = new BinaryWriter(ms)) a.Unit.SaveState(w); state = ms.ToArray(); }
        var b = new Rig("NES-FM:FIX");
        using (var r = new BinaryReader(new MemoryStream(state))) b.Unit.LoadState(r);
        b.Run(0.1);
        Check("a saved state loads into a fresh bridge and the FM notes keep sounding on it", b.Real.FmEnabled && Replayer(b).Allocator.TonalOwner.Count(o => o >= 0) == 2, $"owners {string.Join(",", Replayer(b).Allocator.TonalOwner)}");
        var stock = new Rig("SMS-FM"); stock.Unit.WriteAudioControl(1); stock.Note(0, 4, 2, 330); stock.Run(0.1);
        byte[] stockState; using (var ms = new MemoryStream()) { using (var w = new BinaryWriter(ms)) stock.Unit.SaveState(w); stockState = ms.ToArray(); }
        var c = new Rig("NES-FM:FIX");
        using (var r = new BinaryReader(new MemoryStream(stockState))) c.Unit.LoadState(r);
        c.Run(0.1);
        Check("and a state from the stock SMS-FM unit loads too (a hot swap carries the chips as they are)", Replayer(c).Allocator.TonalOwner.Any(o => o >= 0));
    }

    private static void NoAllocation()
    {
        var r = new Rig("NES-FM:FIX"); r.Unit.WriteAudioControl(1);
        for (int ch = 0; ch < 6; ch++) r.Note(ch, 1 + ch, ch, 200 * Math.Pow(2, ch / 9.0));
        r.Run(0.5, false);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 4; i++) { r.Run(0.5, false); r.Fm(0x10 + i, 0x40 + i * 7); }
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Check($"no allocation while eleven voices play and the pitch moves (2 s: {bytes} bytes)", bytes < 4096);
    }

    private static void Speed()
    {
        var r = new Rig("NES-FM:FIX"); r.Unit.WriteAudioControl(1);
        for (int ch = 0; ch < 9; ch++) r.Note(ch, 1 + ch, 2, 150 * Math.Pow(2, ch / 7.0));
        r.Run(0.3, false);
        // processor time, not wall time: a busy shared machine stretches the wall clock but not the work (the plugin's tests do the same)
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var cpu0 = proc.TotalProcessorTime; var sw = System.Diagnostics.Stopwatch.StartNew();
        r.Run(4.0, false);
        proc.Refresh();
        double cpu = (proc.TotalProcessorTime - cpu0).TotalSeconds, x = 4.0 / Math.Max(cpu, 1e-3), wall = 4.0 / sw.Elapsed.TotalSeconds;
        if (verbose) Console.WriteLine($"        speed: {x:F1}x real time of processor time ({wall:F1}x of wall time) for the FM bridge with nine FM voices");
        Check($"the FM bridge runs at {x:F1}x real time in processor time (needs 8x or better for the unit alone, like APU_SMSFM: the console's Z80 and VDP come on top; wall time {wall:F1}x)", x > 8);
    }
}
