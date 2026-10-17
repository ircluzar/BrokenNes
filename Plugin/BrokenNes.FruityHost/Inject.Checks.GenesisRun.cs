// Round 6 (inject): the Genesis checks proper (the rig is in Inject.Checks.Genesis.cs): the game's own FM channel and DAC stems, a hook tested on a bare YM2612, the saved form.
using BrokenNes2;
using BrokenNes2.Instruments;
using NesEmulator.Sega;
using NesEmulator.Systems;

namespace BrokenNes.FruityHost;

internal static partial class InjectChecks
{
    private static bool IsCarrierOp(int alg, int n) => alg switch { <= 3 => n == 3, 4 => n >= 2, 5 or 6 => n >= 1, _ => true };

    private static double[] PeakRatiosMono(float[] a, float[] b, int rate, double maxBaseHz = 1600)
    {
        var pa = new List<double>(); var pb = new List<double>();
        for (int s = 0; s + 4096 <= Math.Min(a.Length, b.Length); s += 4096) { pa.Add(InjectSpectrum.PeakHz(a, s, 4096, rate, 120, 6000, 2e-2)); pb.Add(InjectSpectrum.PeakHz(b, s, 4096, rate, 120, 6000, 2e-2)); }
        var r = new List<double>();
        for (int i = 0; i < pa.Count; i++) if (!double.IsNaN(pa[i]) && !double.IsNaN(pb[i]) && pa[i] < maxBaseHz) r.Add(pb[i] / pa[i]);
        return r.ToArray();
    }

    /// <summary>Starts a session in the saved machine, pushes the edit (or not), runs the window and returns the game's audio.</summary>
    private static (List<short> Mix, Dictionary<int, List<float>> Stems, PushOutcome? Outcome, IConsoleSession Session) GenesisRun(GenesisRig rig, Action<IConsoleSession>? push, IEnumerable<int> stems, int frames = GenesisWindow)
    {
        var s = rig.Start();
        PushOutcome? o = null;
        if (push != null) { push(s); o = lastPush; }
        var (mix, st) = rig.Run(s, frames, stems);
        return (mix, st, o, s);
    }

    private static PushOutcome? lastPush;

    /// <summary>Edits instrument <paramref name="index"/> of the frozen copy with the model's own history, writes it into a copy the way the plugin's Apply does, and pushes it.</summary>
    private static Action<IConsoleSession> GenesisPush(GenesisRig rig, int index, Action<InstrumentHistory> edit) => s =>
    {
        var work = rig.Snapshot;
        var h = new InstrumentHistory(new SegaInstruments(work).Read(index)!);
        edit(h);
        var w = new SegaInstruments(work).Write(h.Current);
        if (!w.Ok) throw new InvalidOperationException("the edit was refused by the model: " + w.Reason);
        lastPush = GamePush.Push(s, w.Snapshot, h.Current, h.Clean);
    };

    private static void GenesisChecks(bool mutants)
    {
        var rig = GenesisRig.Make();
        if (rig == null) { Skip("genesis", $"Sonic the Hedgehog is not at {InjectRun.Sonic1}"); return; }
        int ch = rig.Channel, fmStem = SegaStems.MdFm1 + ch, rate = GenesisRig.Unit(rig.Start()).SampleRate;
        Console.WriteLine($"  (Sonic parked at frame {rig.Frame}; the checks edit FM {ch + 1}'s patch, keyed {rig.KeyOns} times with it in the next {GenesisWindow} frames; {rig.Snapshot.Dac.Length} DAC sounds were found in the writes before it)");
        int[] stems = [fmStem, SegaStems.MdDac];

        var baseRun = GenesisRun(rig, null, stems);
        ulong h0 = InjectRun.Hash(baseRun.Mix);
        var again = GenesisRun(rig, null, stems);
        Check(InjectRun.Hash(again.Mix) == h0 && baseRun.Mix.Count > 100000, "genesis-determinism", $"a session loaded from the saved machine plays {baseRun.Mix.Count / 2} frames of audio twice, hash {h0:X16}");
        var baseFm = baseRun.Stems[fmStem].ToArray();
        double rmsFm = InjectSpectrum.Rms(baseFm, 0, baseFm.Length);

        // ---- the FM patch: every multiple doubled, so the channel plays an octave up ----
        Run("genesis-fm-octave", () =>
        {
            var r = GenesisRun(rig, GenesisPush(rig, ch, h =>
            {
                for (int n = 1; n <= 4; n++) { int m = h.Current.Get($"op{n}.mul"); h.Set($"op{n}.mul", m == 0 ? 1 : Math.Min(15, m * 2)); }
            }), stems);
            var fm = r.Stems[fmStem].ToArray();
            var ratios = PeakRatiosMono(baseFm, fm, rate);
            double med = Median(ratios); int near = ratios.Count(x => Math.Abs(x / 2 - 1) < 0.03);
            var hook = GamePushes.Peek(r.Session)?.Opn;
            return (r.Outcome!.Ok && ratios.Length >= 8 && Math.Abs(med / 2 - 1) < 0.03 && near >= ratios.Length * 0.4 && hook != null && hook.Substituted >= 1,
                $"every operator multiple of FM {ch + 1} doubled: the strongest FFT peak of the game's own FM {ch + 1} moved by a median x{med:0.000} (expected x2) over {ratios.Length} windows, {near} within 3% of that; the hook loaded the edited patch at {hook?.Substituted} key-ons and found it already loaded at {hook?.AlreadyEdited}");
        }, PushMutants.OpnNoSubstitution, mutants, "no key-on loads the edited patch");

        Run("genesis-fm-level", () =>
        {
            var inst = new SegaInstruments(rig.Snapshot).Read(ch)!;
            int alg = inst.Get("alg");
            var r = GenesisRun(rig, GenesisPush(rig, ch, h =>
            {
                for (int n = 1; n <= 4; n++) if (IsCarrierOp(alg, n - 1)) h.Set($"op{n}.tl", Math.Min(127, h.Current.Get($"op{n}.tl") + 24));
            }), stems);
            var fm = r.Stems[fmStem].ToArray();
            double ratio = InjectSpectrum.Rms(fm, 0, fm.Length) / rmsFm;
            // 24 steps of 0.75 dB = 18 dB = x0.126 (the driver's own volume is kept on top of it)
            return (r.Outcome!.Ok && ratio > 0.07 && ratio < 0.2, $"the carriers' total level of FM {ch + 1} raised by 24 (18 dB of attenuation): the game's FM {ch + 1} has rms {InjectSpectrum.Rms(fm, 0, fm.Length):0.00000} against {rmsFm:0.00000} without the push: x{ratio:0.000} (expected about x0.126)");
        }, PushMutants.OpnNoSubstitution, mutants, "no key-on loads the edited patch");

        // a push that changes nothing audible leaves the game's audio bit for bit as it was (the hook itself is neutral)
        Run("genesis-neutral-push", () =>
        {
            var r = GenesisRun(rig, GenesisPush(rig, ch, h => h.Set("ams", (h.Current.Get("ams") + 1) % 4)), stems);
            var hook = GamePushes.Peek(r.Session)?.Opn;
            ulong h = InjectRun.Hash(r.Mix);
            return (r.Outcome!.Ok && h == h0 && hook != null && hook.Substituted + hook.AlreadyEdited >= 1, $"AMS of FM {ch + 1} changed (the LFO is off, so nothing is heard): the game's audio hash {h:X16} against {h0:X16} without the push; the hook ran at {hook?.Substituted + hook?.AlreadyEdited} key-ons");
        });

        // ---- remove ----
        Run("genesis-remove", () =>
        {
            var s = rig.Start();
            ulong ram0 = GenesisPatchHash(s);
            GenesisPush(rig, ch, h => { h.Set("op1.mul", (h.Current.Get("op1.mul") + 3) & 15); h.Set("alg", (h.Current.Get("alg") + 1) & 7); })(s);
            var o = lastPush!;
            var rm = GamePush.Remove(s, PushKind.OpnPatch, ch);
            ulong h = InjectRun.Hash(rig.Run(s, GenesisWindow, []).Mix);
            return (o.Ok && rm.Ok && GamePush.Count(s) == 0 && GenesisRig.Unit(s).Fm.Override == null && h == h0, $"push then remove at once: no hook left, the game's audio hash {h:X16} against the untouched run's {h0:X16}; \"{rm.Message}\"");
        }, PushMutants.RemoveLeavesOverrides, mutants, "remove leaves the hook");
        Run("genesis-remove-registers", () =>
        {
            // after the edited patch has been loaded into the chip by the hook, removing puts the original registers back on that channel
            var edit = GenesisPush(rig, ch, h => { h.Set("op1.mul", (h.Current.Get("op1.mul") + 3) & 15); h.Set("op4.tl", Math.Min(127, h.Current.Get("op4.tl") + 10)); });
            var s = rig.Start(); edit(s);
            var ctl = rig.Start();
            var rs = new byte[30]; var rc = new byte[30];
            rig.Run(s, 40, []); rig.Run(ctl, 40, []);
            OpnPushHook.ReadPatch(GenesisRig.Unit(s).Fm, ch, rs); OpnPushHook.ReadPatch(GenesisRig.Unit(ctl).Fm, ch, rc);
            bool loaded = !rs.AsSpan().SequenceEqual(rc);
            var rm = GamePush.Remove(s, PushKind.OpnPatch, ch);
            OpnPushHook.ReadPatch(GenesisRig.Unit(s).Fm, ch, rs);
            bool back = rs.AsSpan().SequenceEqual(rc);
            return (edit != null && loaded && rm.Ok && back, $"after 40 frames the edited patch is in the chip's registers of FM {ch + 1} ({loaded}); after Remove they are the untouched game's ({back}); \"{rm.Message}\"");
        }, PushMutants.OpnRemoveKeepsPatch, mutants, "remove leaves the edited registers in the chip");

        // ---- the saved form ----
        // saved at once after the push: the first key-on of the reopened game must still load the edited patch (the table is in no machine state)
        Run("genesis-persist", () =>
        {
            var s = rig.Start();
            GenesisPush(rig, ch, h => { for (int n = 1; n <= 4; n++) { int m = h.Current.Get($"op{n}.mul"); h.Set($"op{n}.mul", m == 0 ? 1 : Math.Min(15, m * 2)); } h.Set("op4.tl", Math.Min(127, h.Current.Get("op4.tl") + 12)); })(s);
            var section = GamePush.Serialize(s)!; var machine = s.SaveState();
            var cont = RunFrom(rig, s, rig.Frame, GenesisWindow);
            var s2 = InjectRun.Create(ConsoleKind.Genesis, rig.Rom, rig.Name);
            s2.LoadState(machine);
            string? problem = GamePush.Restore(s2, section, out var notes);
            var restored = RunFrom(rig, s2, rig.Frame, GenesisWindow);
            var s3 = InjectRun.Create(ConsoleKind.Genesis, rig.Rom, rig.Name); s3.LoadState(machine);
            var without = RunFrom(rig, s3, rig.Frame, GenesisWindow);
            ulong hc = InjectRun.Hash(cont), hr = InjectRun.Hash(restored), hw = InjectRun.Hash(without);
            return (problem == null && hr == hc && hw != hc && hc != h0, $"section {section.Length} bytes; the reopened game plays hash {hr:X16} = the game that stayed open {hc:X16}; the machine without the section plays {hw:X16}; the untouched game {h0:X16}");
        }, PushMutants.RestoreSkipsHooks, mutants, "the reopened game gets no hook");
        // saved after the hook has loaded the patch and remembers the volume it wrote: the memory must come back too, or the change is added a second time
        Run("genesis-persist-volume-memory", () =>
        {
            var s = rig.Start();
            GenesisPush(rig, ch, h => { for (int n = 1; n <= 4; n++) { int m = h.Current.Get($"op{n}.mul"); h.Set($"op{n}.mul", m == 0 ? 1 : Math.Min(15, m * 2)); } h.Set("op4.tl", Math.Min(127, h.Current.Get("op4.tl") + 12)); })(s);
            RunFrom(rig, s, rig.Frame, 60);
            var section = GamePush.Serialize(s)!; var machine = s.SaveState();
            var cont = RunFrom(rig, s, rig.Frame + 60, GenesisWindow - 60);
            var s2 = InjectRun.Create(ConsoleKind.Genesis, rig.Rom, rig.Name);
            s2.LoadState(machine);
            string? problem = GamePush.Restore(s2, section, out _);
            var restored = RunFrom(rig, s2, rig.Frame + 60, GenesisWindow - 60);
            ulong hc = InjectRun.Hash(cont), hr = InjectRun.Hash(restored);
            return (problem == null && hr == hc, $"saved 60 frames after the push (section {section.Length} bytes): the reopened game plays hash {hr:X16} = the game that stayed open {hc:X16}");
        }, PushMutants.OpnStateNotRestored, mutants, "the hook's volume memory is not restored");

        // ---- no allocation ----
        Run("genesis-alloc", () =>
        {
            var s = rig.Start(); var s0 = rig.Start();
            GenesisPush(rig, ch, h => h.Set("op1.mul", (h.Current.Get("op1.mul") + 1) & 15))(s);
            var buf = new short[1 << 16];
            long Measure(IConsoleSession x)
            {
                for (int f = 0; f < 30; f++) { x.RunFrame(); while (x.ReadSamples(buf) > 0) { } }
                long a0 = GC.GetAllocatedBytesForCurrentThread();
                for (int f = 0; f < 120; f++) { x.RunFrame(); while (x.ReadSamples(buf) > 0) { } }
                return GC.GetAllocatedBytesForCurrentThread() - a0;
            }
            long with = Measure(s), without = Measure(s0);
            return (with <= without, $"120 frames of the game with the hook in force allocate {with} bytes, without it {without}: the hook adds {with - without}");
        });

        // ---- the hook alone, on a bare chip ----
        GenesisHookUnit(mutants);
        GenesisDac(rig, mutants);
    }

    private static List<short> RunFrom(GenesisRig rig, IConsoleSession s, int from, int frames)
    {
        var pcm = new List<short>();
        InjectRun.Frames(s, from, from + frames, rig.Pad, pcm);
        return pcm;
    }

    private static ulong GenesisPatchHash(IConsoleSession s)
    {
        var ym = GenesisRig.Unit(s).Fm;
        ulong h = 14695981039346656037UL;
        Span<byte> p = stackalloc byte[30];
        for (int c = 0; c < 6; c++) { OpnPushHook.ReadPatch(ym, c, p); foreach (byte b in p) h = (h ^ b) * 1099511628211UL; }
        return h;
    }

    private static byte[] MakePatch(int alg, int tlCarrier, int pan = 0xC0)
    {
        var p = new byte[30];
        for (int n = 0; n < 4; n++) { p[n * 7] = (byte)(0x01 + n); p[n * 7 + 1] = (byte)(20 + n); p[n * 7 + 2] = 0x1F; p[n * 7 + 3] = 0x08; p[n * 7 + 4] = 0x02; p[n * 7 + 5] = 0x14; }
        p[3 * 7 + 1] = (byte)tlCarrier;                  // operator 4 is the carrier of algorithm 0
        p[28] = (byte)alg; p[29] = (byte)pan;
        return p;
    }

    private static void SetPatch(Ym2612 ym, int ch, byte[] p)
    {
        int part = ch / 3 * 0x100, c = ch % 3;
        int[] off = [0, 8, 4, 12];
        for (int n = 0; n < 4; n++) for (int k = 0; k < 7; k++) ym.OverrideRegister(part + 0x30 + k * 0x10 + off[n] + c, p[n * 7 + k]);
        ym.OverrideRegister(part + 0xB0 + c, p[28]); ym.OverrideRegister(part + 0xB4 + c, p[29]);
    }

    private static void KeyOn(Ym2612 ym, int ch, long clock)
    {
        int sel = ch < 3 ? ch : ch + 1;
        ym.Write(0, 0x28, clock); ym.Write(1, (byte)(0xF0 | sel), clock + 10);
    }

    private static void GenesisHookUnit(bool mutants)
    {
        Run("genesis-hook-patch", () =>
        {
            var orig = MakePatch(0, 40);
            var edit = (byte[])orig.Clone();
            edit[0] = 0x04; edit[7] = 0x03; edit[28] = 0x04 | 0x28;            // multiples, a different algorithm and feedback
            edit[3 * 7 + 1] = 40 + 12;                                          // the carrier's level + 12
            edit[29] = 0xC0 | 0x13;                                             // AMS 1, FMS 3; the pan stays as it was
            edit[1 * 7 + 1] = 20 + 1 + 5;                                       // a modulator's level + 5
            var rec = new PushRecord { Kind = PushKind.OpnPatch, Index = 0, Name = "t", A = [.. orig, (byte)0], B = [.. edit, (byte)0], P = [0] };
            var hook = new OpnPushHook([rec]);
            var ym = new Ym2612(true) { Override = hook };
            long clock = 0;
            var probs = new List<string>();
            var cur = new byte[30];
            void Expect(string what, bool ok) { if (!ok) probs.Add(what); }
            // 1. the driver loads the original patch on channel 1 and a volume of its own (carrier level 40 + 20), then keys on: the edited patch is loaded, the volume kept
            var driver = (byte[])orig.Clone(); driver[3 * 7 + 1] = 60; driver[29] = 0x80;          // the driver pans this note left only
            SetPatch(ym, 1, driver); KeyOn(ym, 1, clock += 1000);
            OpnPushHook.ReadPatch(ym, 1, cur);
            Expect("the first key-on loads the edited operators", cur[0] == 0x04 && cur[7] == 0x03 && cur[28] == edit[28]);
            Expect($"the carrier keeps the driver's volume and takes the edit's change (60 + 12 = 72, got {cur[3 * 7 + 1]})", cur[3 * 7 + 1] == 72);
            Expect($"the modulator takes the edit's level (got {cur[7 + 1]})", cur[1 * 7 + 1] == 26);
            Expect($"the driver's pan stays; AMS and FMS come from the edit (got ${cur[29]:X2})", cur[29] == (0x80 | 0x13 & 0x37));
            // 2. another key-on with nothing rewritten: nothing is added again
            KeyOn(ym, 1, clock += 1000);
            OpnPushHook.ReadPatch(ym, 1, cur);
            Expect($"a second key-on does not add the change again (got {cur[3 * 7 + 1]})", cur[3 * 7 + 1] == 72);
            // 3. the driver changes the volume only (carrier level 50): the change is made again on top of it
            ym.OverrideRegister(0x40 + 12 + 1, 50);       // operator 4 of channel 2's total level register
            KeyOn(ym, 1, clock += 1000);
            OpnPushHook.ReadPatch(ym, 1, cur);
            Expect($"after a volume change by the driver (50) the edit's change is on top of it (62, got {cur[3 * 7 + 1]})", cur[3 * 7 + 1] == 62);
            // 4. the driver loads the original patch again on another channel: that channel is edited too
            SetPatch(ym, 4, orig); KeyOn(ym, 4, clock += 1000);
            OpnPushHook.ReadPatch(ym, 4, cur);
            Expect("the same patch on another channel is edited as well", cur[0] == 0x04 && cur[3 * 7 + 1] == 52);
            // 5. a different patch is left alone
            var other = MakePatch(3, 33); other[0] = 0x09;
            SetPatch(ym, 2, other); KeyOn(ym, 2, clock += 1000);
            var after = new byte[30]; OpnPushHook.ReadPatch(ym, 2, after);
            Expect("a different patch is not touched", after.AsSpan().SequenceEqual(other));
            return (probs.Count == 0, probs.Count == 0 ? "the key-on substitution loads the edited patch, keeps the driver's volume and pan, repeats after a reload, covers every channel that holds the patch and leaves other patches alone" : string.Join("; ", probs));
        }, PushMutants.OpnIgnoresVolume, mutants, "the edit's absolute level replaces the driver's volume");
    }

    private static void GenesisDac(GenesisRig rig, bool mutants)
    {
        // ---- the hook alone: the stream the driver writes, and what the chip is handed instead ----
        Run("genesis-dac-stream", () =>
        {
            var rnd = new Random(7);
            var orig = new byte[400]; for (int i = 0; i < orig.Length; i++) orig[i] = (byte)(128 + (int)(90 * Math.Sin(i * 0.31) + rnd.Next(-12, 12)));
            var edit = new byte[300]; for (int i = 0; i < edit.Length; i++) edit[i] = (byte)(128 + (int)(100 * Math.Sin(i * 0.7)));
            var probs = new List<string>();
            void Expect(string what, bool ok) { if (!ok) probs.Add(what); }
            // equal rates: the edited sound is handed over from the 12th sample on; past its end: silence
            var rec = new PushRecord { Kind = PushKind.OpnDac, Index = 6, Name = "t", A = orig, B = edit, P = [8000, 8000, 0, 0, 0, 0, 0, 0] };
            var hook = new OpnPushHook([rec]);
            long clock = 0;
            var junk = new byte[30]; rnd.NextBytes(junk);
            foreach (byte j in junk) { byte o = hook.OnDacWrite(j, clock += 800); if (o != j) { probs.Add("a sample before the sound was changed"); break; } }
            bool headOk = true, tailOk = true, bodyOk = true;
            for (int i = 0; i < orig.Length; i++)
            {
                byte o = hook.OnDacWrite(orig[i], clock += 800);
                if (i < 11) headOk &= o == orig[i];
                else if (i < edit.Length) bodyOk &= o == edit[i];
                else tailOk &= o == 0x80;
            }
            Expect("the first 11 samples pass as the game wrote them", headOk);
            Expect("from the 12th the edited samples are handed over", bodyOk);
            Expect("past the end of the edited sound the chip gets silence", tailOk);
            Expect("the sound was recognised once", hook.DacMatches[0] == 1);
            // after the sound ends and a gap, the next play is recognised again; a different stream is not changed
            clock += 100_000;
            for (int i = 0; i < orig.Length; i++) { byte o = hook.OnDacWrite(orig[i], clock += 800); if (i >= 11 && i < edit.Length && o != edit[i]) { probs.Add("the second play was not edited"); break; } }
            clock += 100_000;
            bool untouched = true;
            for (int i = 0; i < 100; i++) { byte v = (byte)(100 + i); untouched &= hook.OnDacWrite(v, clock += 800) == v; }
            Expect("a different stream is left alone", untouched);
            // a faster edited rate plays the edited sound through quicker (sample n of the driver's stream is edited sample n x 2)
            var rec2 = new PushRecord { Kind = PushKind.OpnDac, Index = 6, Name = "t", A = orig, B = edit, P = [8000, 16000, 0, 0, 0, 0, 0, 0] };
            var hook2 = new OpnPushHook([rec2]);
            clock += 100_000;
            bool rateOk = true;
            for (int i = 0; i < orig.Length; i++)
            {
                byte o = hook2.OnDacWrite(orig[i], clock += 800);
                if (i >= 11) { long idx = i * 2L; rateOk &= o == (idx < edit.Length ? edit[idx] : (byte)0x80); }
            }
            Expect("at twice the rate the driver's n-th sample is the edited 2n-th", rateOk);
            // an edited loop keeps the sound going past the recorded stretch
            var rec3 = new PushRecord { Kind = PushKind.OpnDac, Index = 6, Name = "t", A = orig, B = edit, P = [8000, 8000, 1, 100, 200, 0, 0, 0] };
            var hook3 = new OpnPushHook([rec3]);
            clock += 100_000;
            bool loopOk = true;
            for (int i = 0; i < orig.Length; i++)
            {
                byte o = hook3.OnDacWrite(orig[i], clock += 800);
                if (i >= 11) { long idx = i; if (idx >= 200) idx = 100 + (idx - 100) % 100; loopOk &= o == edit[idx]; }
            }
            Expect("an edited loop wraps inside the edited sound", loopOk);
            return (probs.Count == 0, probs.Count == 0 ? "the DAC stream hook recognises a sound by its first 12 samples, hands over the edited ones (at the edited rate, with its loop, silence past its end), recognises it again on the next play and leaves other streams alone" : string.Join("; ", probs));
        }, PushMutants.DacRateIgnored, mutants, "the edited rate is ignored");

        // ---- the game's own DAC stem ----
        if (rig.Snapshot.Dac.Length == 0) { Skip("genesis-dac", "no DAC sound was found in the writes before the saved place"); return; }
        var stems = new[] { SegaStems.MdDac };
        var baseRun = GenesisRun(rig, null, stems);
        var baseDac = baseRun.Stems[SegaStems.MdDac].ToArray();
        double rmsDac = InjectSpectrum.Rms(baseDac, 0, baseDac.Length);
        Action<IConsoleSession> PushAll(Func<DacSample, int, byte[]> make) => s =>
        {
            var work = rig.Snapshot;
            PushOutcome? o = null;
            for (int d = 0; d < rig.Snapshot.Dac.Length; d++)
            {
                var h = new InstrumentHistory(new SegaInstruments(work).Read(6 + d)!);
                var snd = rig.Snapshot.Dac[d];
                h.SetBytes("pcm", 0, make(snd, d));
                var w = new SegaInstruments(work).Write(h.Current);
                if (!w.Ok) throw new InvalidOperationException(w.Reason);
                work = (SegaSnapshot)w.Snapshot!;
                o = GamePush.Push(s, work, h.Current, h.Clean);
                if (!o.Ok) break;
            }
            lastPush = o;
        };
        int played = 0;
        Run("genesis-dac-amplitude", () =>
        {
            var r = GenesisRun(rig, PushAll((snd, d) => snd.Data.Select(v => (byte)(128 + (v - 128) / 2)).ToArray()), stems);
            var dac = r.Stems[SegaStems.MdDac].ToArray();
            var hook = GamePushes.Peek(r.Session)?.Opn;
            played = hook == null ? 0 : (int)hook.DacMatches.Sum();
            double ratio = InjectSpectrum.Rms(dac, 0, dac.Length) / rmsDac;
            return (r.Outcome!.Ok && played >= 1 && ratio > 0.4 && ratio < 0.65, $"every DAC sound found ({rig.Snapshot.Dac.Length}) pushed at half amplitude: the game's DAC stem has rms {InjectSpectrum.Rms(dac, 0, dac.Length):0.00000} against {rmsDac:0.00000} without the push: x{ratio:0.00} (expected about x0.5); {played} sounds were recognised in the stream");
        }, PushMutants.DacNoSubstitution, mutants, "the stream is not substituted");
        Run("genesis-dac-tone", () =>
        {
            var r = GenesisRun(rig, PushAll((snd, d) => Enumerable.Range(0, snd.Data.Length).Select(i => (byte)(128 + (int)(100 * Math.Sin(2 * Math.PI * 1000.0 * i / snd.Rate)))).ToArray()), stems);
            var dac = r.Stems[SegaStems.MdDac].ToArray();
            int rate = GenesisRig.Unit(r.Session).SampleRate;
            double Band(float[] x)
            {
                int n = 1 << 15; double sum = 0;
                for (int a = 0; a + n <= x.Length; a += n) { var p = InjectSpectrum.Power(x, a, n); int lo = (int)(900.0 * n / rate), hi = (int)(1100.0 * n / rate); for (int k = lo; k <= hi; k++) sum += p[k]; }
                return sum;
            }
            double b0 = Band(baseDac), b1 = Band(dac);
            return (r.Outcome!.Ok && b1 > b0 * 20, $"every DAC sound replaced by a 1 kHz tone: the power between 900 and 1100 Hz in the game's DAC stem grew {b1 / Math.Max(b0, 1e-12):0.0} times (the drums had {b0:0.0000}, now {b1:0.0000})");
        }, PushMutants.DacNoSubstitution, mutants, "the stream is not substituted");
    }
}
