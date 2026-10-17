// Round 6 (inject): the SNES checks proper (the rig and helpers are in Inject.Checks.Snes.cs). Each check returns (passed, what it measured); with mutants on, each is run again with one deliberate bug of the library switched on
// (PushMutants) and must then fail.
using BrokenNes2;
using BrokenNes2.Instruments;
using NesEmulator.Snes;
using NesEmulator.Systems;

namespace BrokenNes.FruityHost;

internal static partial class InjectChecks
{
    /// <summary>Runs <paramref name="check"/>; with <paramref name="mutant"/> given and mutants wanted, runs it again with that bug on and demands it fail.</summary>
    private static void Run(string name, Func<(bool Ok, string Detail)> check, int mutant = 0, bool mutants = true, string? bugName = null)
    {
        var (ok, detail) = check();
        Check(ok, name, detail);
        if (mutant == 0 || !mutants) return;
        PushMutants.Active = mutant;
        try
        {
            var (mok, mdetail) = check();
            if (!mok) Ok(name + " [mutant]", $"the bug \"{bugName}\" is caught: {mdetail}");
            else Bad(name + " [mutant]", $"the bug \"{bugName}\" was NOT caught: {mdetail}");
        }
        finally { PushMutants.Active = 0; }
    }

    private static double Median(IEnumerable<double> v) { var a = v.OrderBy(x => x).ToArray(); return a.Length == 0 ? double.NaN : a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2; }

    private static (List<short> Pcm, PushOutcome? Outcome, IConsoleSession Session) SnesRun(SnesRig rig, Action<InstrumentHistory>? edit, bool solo, Action<IConsoleSession>? atStart = null)
    {
        var s = rig.Start();
        PushOutcome? o = null;
        IEnumerable<DspVoiceOverrides.Entry>? entries = null;
        if (edit != null)
        {
            (o, _, _) = SnesRig.EditAndPush(s, rig.Slot, edit);
            if (o.Ok) entries = GamePushes.Peek(s)?.Dsp?.Entries;
        }
        atStart?.Invoke(s);
        if (solo) SnesRig.Spc(s).Dsp.Overrides = Solo(rig.Slot, entries);
        var pcm = rig.Run(s, SnesWindow);
        return (pcm, o, s);
    }

    /// <summary>For each window of two runs with the same notes: the ratio of the strongest peaks, for the windows where both have one.</summary>
    private static double[] PeakRatios(List<short> a, List<short> b)
    {
        var pa = PeaksPerWindow(InjectRun.Mono(a), SnesRate); var pb = PeaksPerWindow(InjectRun.Mono(b), SnesRate);
        var r = new List<double>();
        for (int i = 0; i < Math.Min(pa.Length, pb.Length); i++) if (!double.IsNaN(pa[i]) && !double.IsNaN(pb[i]) && pa[i] < 1600) r.Add(pb[i] / pa[i]);
        return r.ToArray();
    }

    private static double SideRms(List<short> stereo, int side)
    {
        double s = 0; int n = stereo.Count / 2;
        for (int i = 0; i < n; i++) { double v = stereo[i * 2 + side] / 32768.0; s += v * v; }
        return Math.Sqrt(s / Math.Max(1, n));
    }

    /// <summary>The fraction of a window's spectral power that sits in its strongest bin and its neighbours: a tone has most of it there, noise very little.</summary>
    private static double PeakFraction(float[] mono, int from, int n)
    {
        var p = InjectSpectrum.Power(mono, from, n);
        int best = 2;
        for (int k = 2; k < p.Length - 2; k++) if (p[k] > p[best]) best = k;
        double near = 0, all = 0;
        for (int k = 1; k < p.Length; k++) { all += p[k]; if (Math.Abs(k - best) <= 3) near += p[k]; }
        return all <= 0 ? 0 : near / all;
    }

    private static void SnesChecks(bool mutants)
    {
        var rig = SnesRig.Make();
        if (rig == null) { Skip("snes", $"Super Mario World is not at {InjectRun.Smw}"); return; }
        Console.WriteLine($"  (SMW parked at frame {rig.Frame}; the checks use sample {rig.Slot}, the one its music keys on most in the next {SnesWindow} frames)");

        // ---- the machine the comparisons stand on ----
        var plain = SnesRun(rig, null, false);
        ulong h0 = InjectRun.Hash(plain.Pcm);
        var again = SnesRun(rig, null, false);
        Check(InjectRun.Hash(again.Pcm) == h0 && plain.Pcm.Count > 100000, "snes-determinism", $"a session loaded from the saved machine plays {plain.Pcm.Count / 2} frames of audio, twice, hash {h0:X16}: every comparison below is against this");

        // an override table that changes nothing: the DSP's override path is bit-exact
        Run("snes-identity", () =>
        {
            int weight = PushMutants.On(PushMutants.SnesDropsVolume) ? 126 : 127;       // (the bug stands in for a table whose weights are off by one)
            var s = rig.Start();
            var items = Enumerable.Range(0, 256).Select(i => new DspVoiceOverrides.Entry { Srcn = (byte)i, Flags = DspVoiceOverrides.HasVol, WeightL = (sbyte)weight, WeightR = (sbyte)weight });
            SnesRig.Spc(s).Dsp.Overrides = new DspVoiceOverrides(items);
            ulong h = InjectRun.Hash(rig.Run(s, SnesWindow));
            return (h == h0, $"a table with every sample's volume weights at 127 (the identity) gives hash {h:X16}, the plain run {h0:X16}");
        }, PushMutants.SnesDropsVolume, mutants, "the table's weights are off by one");

        var soloBase = SnesRun(rig, null, true);
        double rmsBase = InjectSpectrum.Rms(InjectRun.Mono(soloBase.Pcm), 0, soloBase.Pcm.Count / 2);

        // ---- the sample: audio RAM ----
        Run("snes-sample-bytes", () =>
        {
            var inst = SnesRig.Instrument(rig.Start(), rig.Slot);
            var (pcm, o, s) = SnesRun(rig, h => h.Randomise(0.15, 0xC0FFEEUL, keepStructure: true), true);
            var live = SnesRig.Spc(s).Aram.AsSpan(inst.Origin, inst.Blobs[0].Data.Length).ToArray();
            var work = SnesRig.Freeze(rig.Start());
            // what the push should have written: the edited sample
            var hh = new InstrumentHistory(inst); hh.Randomise(0.15, 0xC0FFEEUL, keepStructure: true);
            bool inRam = live.AsSpan().SequenceEqual(hh.Current.Blobs[0].Data) && !live.AsSpan().SequenceEqual(inst.Blobs[0].Data);
            var mb = InjectRun.Mono(soloBase.Pcm); var mp = InjectRun.Mono(pcm);
            double cb = InjectSpectrum.Centroid(mb, 4096 * 8, 16384, SnesRate), cp = InjectSpectrum.Centroid(mp, 4096 * 8, 16384, SnesRate);
            bool differs = InjectRun.Hash(pcm) != InjectRun.Hash(soloBase.Pcm) && Math.Abs(cp - cb) / cb > 0.05;
            return (o!.Ok && inRam && differs, $"{o.Message.Split(';')[0]}; the game's audio RAM holds the edited sample ({inRam}); the solo audio's spectral centroid {cb:0} Hz -> {cp:0} Hz");
        }, PushMutants.SnesDropsSample, mutants, "the push does not write the sample bytes");

        // ---- the pitch: tuning, forced on every voice that plays the sample, at every note ----
        foreach (var (cents, expect) in new[] { (1200, 2.0), (-1200, 0.5) })
        {
            int c = cents; double e = expect;
            Run($"snes-tuning{(c > 0 ? "+" : "")}{c}", () =>
            {
                var r = SnesRun(rig, h => h.Set("tune.cents", c), true);
                var ratios = PeakRatios(soloBase.Pcm, r.Pcm);
                double med = Median(ratios);
                int near = ratios.Count(x => Math.Abs(x / e - 1) < 0.03);
                return (r.Outcome!.Ok && ratios.Length >= 12 && Math.Abs(med / e - 1) < 0.02 && near >= ratios.Length * 0.45,
                    $"{c:+0;-0} cents on sample {rig.Slot}: the strongest FFT peak of the game's own audio moved by a median x{med:0.000} (expected x{e}) over {ratios.Length} windows, {near} within 3% of that");
            }, c > 0 ? PushMutants.SnesDropsTuning : 0, mutants, "the push drops the tuning");
        }
        Run("snes-tuning-control", () =>
        {
            var r = SnesRun(rig, h => h.Set("vol.l", 100), true);
            var ratios = PeakRatios(soloBase.Pcm, r.Pcm);
            int near = ratios.Count(x => Math.Abs(x - 1) < 0.01);
            return (ratios.Length >= 12 && near >= ratios.Length * 0.9, $"a push that does not touch the pitch (left weight 100): {near} of {ratios.Length} windows have the baseline's peak within 1%, so the ratio measure is calibrated");
        });

        // ---- the envelope: forced on every voice, in place of the driver's ----
        Run("snes-envelope-attack", () =>
        {
            var r = SnesRun(rig, h => { h.Set("env.adsr", 1); h.Set("env.ar", 1); }, true);
            double rms = InjectSpectrum.Rms(InjectRun.Mono(r.Pcm), 0, r.Pcm.Count / 2);
            return (r.Outcome!.Ok && rms < rmsBase * 0.5, $"the slowest attack but one (rate 1) forced on sample {rig.Slot}: the game's own audio has rms {rms:0.00000} against {rmsBase:0.00000} without the push ({rms / rmsBase:P0})");
        }, PushMutants.SnesForgetsEnvelope, mutants, "the push drops the envelope");
        Run("snes-envelope-gain", () =>
        {
            int observed = 0, atLevel = 0, observedBase = 0, atLevelBase = 0;
            var sb = rig.Start(); var sp = rig.Start();
            var o = SnesRig.EditAndPush(sp, rig.Slot, h => { h.Set("env.adsr", 0); h.Set("gain.mode", 0); h.Set("gain.curve", 1); h.Set("gain.rate", 16); });      // GAIN, direct level $30
            void Watch(IConsoleSession s, Action<int> note)
            {
                var dsp = SnesRig.Spc(s).Dsp;
                InjectRun.Frames(s, rig.Frame, rig.Frame + SnesWindow, rig.Pad, null, f => { for (int v = 0; v < 8; v++) if (dsp.Regs[v * 16 + 4] == rig.Slot && dsp.Regs[v * 16 + 8] != 0) note(dsp.Regs[v * 16 + 8]); });
            }
            Watch(sb, e => { observedBase++; if (e == 0x30) atLevelBase++; });
            Watch(sp, e => { observed++; if (e == 0x30) atLevel++; });
            return (o.Outcome.Ok && observed > 100 && observedBase > 100 && atLevel >= observed * 0.9 && atLevelBase <= observedBase * 0.1,
                $"GAIN direct level $30 forced on sample {rig.Slot}: the DSP's envelope reading (ENVX) of the voices playing it is $30 in {atLevel} of {observed} observations (the rest are voices still releasing the note they had when the machine was saved); without the push {atLevelBase} of {observedBase}");
        }, PushMutants.SnesForgetsEnvelope, mutants, "the push drops the envelope");

        // ---- the volume weights and the flags ----
        Run("snes-volume-weights", () =>
        {
            var r = SnesRun(rig, h => { h.Set("vol.l", 0); h.Set("vol.r", 127); }, true);
            double l = SideRms(r.Pcm, 0), rr = SideRms(r.Pcm, 1), l0 = SideRms(soloBase.Pcm, 0), r0 = SideRms(soloBase.Pcm, 1);
            return (r.Outcome!.Ok && l < rr * 0.15 && l0 > r0 * 0.4 && Math.Abs(rr / r0 - 1) < 0.15, $"left weight 0, right 127: left rms {l:0.00000}, right {rr:0.00000} (without the push {l0:0.00000} and {r0:0.00000})");
        }, PushMutants.SnesDropsVolume, mutants, "the push drops the volume weights");
        Run("snes-noise-flag", () =>
        {
            var r = SnesRun(rig, h => h.Set("mix.noise", 1), true);
            var mb = InjectRun.Mono(soloBase.Pcm); var mp = InjectRun.Mono(r.Pcm);
            // the noise generator replaces the sample: the tone the sample made is gone from the audio (its rate is the game's own FLG setting, which in this game is not set, so what remains is a flat level, not hiss)
            double sum = 0; int n = 0;
            for (int a = 4096 * 2; a + 4096 <= Math.Min(mb.Length, mp.Length) && n < 12; a += 4096)
            {
                var pb = InjectSpectrum.Power(mb, a, 4096); var pp = InjectSpectrum.Power(mp, a, 4096);
                int kb = 2; for (int k = 2; k < pb.Length - 4; k++) if (pb[k] > pb[kb]) kb = k;
                double eb = 0, ep = 0; for (int k = Math.Max(1, kb - 3); k <= kb + 3; k++) { eb += pb[k]; ep += pp[k]; }
                if (eb < 1e-3) continue;
                sum += ep / eb; n++;
            }
            double ratio = n == 0 ? double.NaN : sum / n;
            return (r.Outcome!.Ok && n >= 8 && ratio < 0.02, $"noise forced on sample {rig.Slot}: the power at the tone the sample played is {ratio:P2} of what it was without the push (over {n} windows)");
        }, PushMutants.SnesDropsMix, mutants, "the push drops the flags");

        // ---- the farm is told ----
        Run("snes-farm-writes", () =>
        {
            var s = rig.Start();
            var before = (byte[])SnesRig.Spc(s).Aram.Clone();
            var (o, _, _) = SnesRig.EditAndPush(s, rig.Slot, h => { h.Randomise(0.5, 0xBEEFUL, keepStructure: true); h.Set("tune.cents", 300); });
            // replaying the writes the push reports on a copy of the machine gives the game's memory as the push left it
            var copy = (byte[])before.Clone();
            foreach (var (dom, addr, val) in o.Writes) if (dom == "ARAM") copy[addr] = val;
            bool same = copy.AsSpan().SequenceEqual(SnesRig.Spc(s).Aram);
            return (o.Ok && o.Writes.Count > 0 && same && o.Poison != null, $"the push reports {o.Writes.Count} memory writes; replaying them on the machine as it was gives the memory the push left ({same}); it says it installed something a replay cannot repeat: {o.Poison != null}");
        }, PushMutants.SnesDropsSample, mutants, "the push writes fewer bytes than it reports");

        // ---- remove ----
        Run("snes-remove", () =>
        {
            var s = rig.Start();
            ulong ram0 = InjectRun.Hash(SnesRig.Spc(s).Aram);
            var (o, _, _) = SnesRig.EditAndPush(s, rig.Slot, h => { h.Randomise(0.5, 0xBEEFUL, keepStructure: true); h.Set("tune.cents", 700); h.Set("env.adsr", 1); h.Set("env.ar", 2); h.Set("vol.r", 40); });
            ulong ram1 = InjectRun.Hash(SnesRig.Spc(s).Aram);
            var rm = GamePush.Remove(s, PushKind.SnesSample, rig.Slot);
            ulong ram2 = InjectRun.Hash(SnesRig.Spc(s).Aram);
            ulong h = InjectRun.Hash(rig.Run(s, SnesWindow));
            bool gone = GamePush.Count(s) == 0 && SnesRig.Spc(s).Dsp.Overrides == null;
            return (o.Ok && rm.Ok && ram1 != ram0 && ram2 == ram0 && h == h0 && gone,
                $"push then remove: audio RAM hash {ram0:X8} -> {ram1:X8} -> {ram2:X8} (back); no override left ({gone}); the next {SnesWindow} frames of the game's audio hash {h:X16} against the untouched run's {h0:X16}; \"{rm.Message}\"");
        }, PushMutants.RemoveLeavesBytes, mutants, "remove leaves the bytes");
        Run("snes-remove-overrides", () =>
        {
            var s = rig.Start();
            SnesRig.EditAndPush(s, rig.Slot, h => { h.Set("tune.cents", 700); });
            GamePush.Remove(s, PushKind.SnesSample, rig.Slot);
            ulong h = InjectRun.Hash(rig.Run(s, SnesWindow));
            return (h == h0, $"push (tuning only) then remove: the game's audio hash {h:X16} against the untouched run's {h0:X16}");
        }, PushMutants.RemoveLeavesOverrides, mutants, "remove leaves the overrides");

        // ---- pushing again replaces ----
        Run("snes-replace", () =>
        {
            var s2 = rig.Start();
            SnesRig.EditAndPush(s2, rig.Slot, h => h.Set("tune.cents", 1200));
            var (o2, _, _) = SnesRig.EditAndPush(s2, rig.Slot, h => h.Set("tune.cents", 100));
            ulong h2 = InjectRun.Hash(rig.Run(s2, SnesWindow));
            var direct = SnesRun(rig, h => h.Set("tune.cents", 100), false);
            return (o2.Ok && GamePush.Count(s2) == 1 && h2 == InjectRun.Hash(direct.Pcm) && h2 != h0, $"+1200 pushed, then +100: one push is kept ({GamePush.Count(s2)}), the audio hash {h2:X16} equals a single +100 push's {InjectRun.Hash(direct.Pcm):X16}");
        });

        // ---- nothing to push ----
        Run("snes-nothing-to-push", () =>
        {
            var s = rig.Start();
            var work = SnesRig.Freeze(s);
            var inst = new SnesInstruments(work).Read(rig.Slot)!;
            var o = GamePush.Push(s, work, inst);
            return (!o.Ok && o.Message.Contains("nothing to push") && GamePush.Count(s) == 0, $"an unedited sample: \"{o.Message}\"");
        });

        // ---- allocation ----
        Run("snes-alloc", () =>
        {
            var s = rig.Start();
            var (o, _, _) = SnesRig.EditAndPush(s, rig.Slot, h => { h.Set("tune.cents", 500); h.Set("env.adsr", 1); h.Set("vol.l", 90); h.Set("mix.noise", 1); });
            var dsp = SnesRig.Spc(s).Dsp;
            for (int i = 0; i < 4000; i++) dsp.RunSample();             // (warm up)
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 64000; i++) dsp.RunSample();
            long used = GC.GetAllocatedBytesForCurrentThread() - a0;
            long c0 = GC.GetAllocatedBytesForCurrentThread();
            var garbage = new byte[4096]; GC.KeepAlive(garbage);
            long control = GC.GetAllocatedBytesForCurrentThread() - c0;
            return (o.Ok && used == 0 && control >= 4096, $"two seconds of the S-DSP with an override table in force allocate {used} bytes (the measurement sees a 4 KB array as {control} bytes)");
        });

        // ---- the saved form ----
        Run("snes-persist", () =>
        {
            var s = rig.Start();
            var (o, _, _) = SnesRig.EditAndPush(s, rig.Slot, h => { h.Randomise(0.4, 0x5EEDUL, keepStructure: true); h.Set("tune.cents", 400); h.Set("env.adsr", 1); h.Set("env.ar", 3); h.Set("vol.r", 60); h.Set("mix.noise", 1); });
            byte[] section = GamePush.Serialize(s)!, machine = s.SaveState();
            ulong direct = InjectRun.Hash(rig.Run(s, SnesWindow));
            // the project is reopened: a new game, the saved machine put back, the section applied
            var s2 = InjectRun.Create(ConsoleKind.Snes, rig.Rom, rig.Name);
            s2.LoadState(machine);
            string? problem = GamePush.Restore(s2, section, out var notes);
            ulong restored = InjectRun.Hash(rig.Run(s2, SnesWindow));
            // without the section the machine alone plays differently (the overrides are not in any machine state)
            var s3 = InjectRun.Create(ConsoleKind.Snes, rig.Rom, rig.Name);
            s3.LoadState(machine);
            ulong without = InjectRun.Hash(rig.Run(s3, SnesWindow));
            // and the pushes can be taken out of the reopened game
            var s4 = InjectRun.Create(ConsoleKind.Snes, rig.Rom, rig.Name);
            s4.LoadState(machine);
            GamePush.Restore(s4, section, out _);
            var rm = GamePush.Remove(s4, PushKind.SnesSample, rig.Slot);
            ulong ramBack = InjectRun.Hash(SnesRig.Spc(s4).Aram);
            ulong ramOriginal = InjectRun.Hash(SnesRig.Spc(rig.Start()).Aram);
            return (o.Ok && problem == null && notes.Count == 0 && restored == direct && without != direct && rm.Ok && ramBack == ramOriginal,
                $"section {section.Length} bytes; the reopened game plays hash {restored:X16} = the game that was never closed {direct:X16}; its machine without the section plays {without:X16}; removing from the reopened game puts the audio RAM back ({ramBack == ramOriginal})");
        }, PushMutants.RestoreSkipsHooks, mutants, "the reopened game gets no overrides");
        Run("snes-persist-damaged", () =>
        {
            var s = rig.Start();
            SnesRig.EditAndPush(s, rig.Slot, h => h.Set("tune.cents", 400));
            byte[] good = GamePush.Serialize(s)!;
            int refused = 0, total = 0;
            for (int i = 0; i < good.Length; i += Math.Max(1, good.Length / 120))
            {
                var bad = (byte[])good.Clone(); bad[i] ^= 0x40; total++;
                var t = InjectRun.Create(ConsoleKind.Snes, rig.Rom, rig.Name); t.LoadState(rig.State);
                if (GamePush.Restore(t, bad, out _) != null && GamePush.Count(t) == 0) refused++;
            }
            var newer = (byte[])good.Clone(); newer[0] = 9;
            // (a newer version with a fixed-up checksum is still refused, by its version)
            uint crc = InstrumentCrc.Of(newer.AsSpan(0, newer.Length - 4)); BitConverter.TryWriteBytes(newer.AsSpan(newer.Length - 4), crc);
            var t2 = InjectRun.Create(ConsoleKind.Snes, rig.Rom, rig.Name); t2.LoadState(rig.State);
            string? why = GamePush.Restore(t2, newer, out _);
            var cut = good[..(good.Length - 9)];
            var t3 = InjectRun.Create(ConsoleKind.Snes, rig.Rom, rig.Name); t3.LoadState(rig.State);
            string? whyCut = GamePush.Restore(t3, cut, out _);
            return (refused == total && why != null && why.Contains("newer") && whyCut != null, $"{refused} of {total} sections with one flipped byte are refused and install nothing; a newer version: \"{why}\"; a cut-short one: \"{whyCut}\"");
        }, PushMutants.CrcNotChecked, mutants, "the checksum is not checked");
    }
}
