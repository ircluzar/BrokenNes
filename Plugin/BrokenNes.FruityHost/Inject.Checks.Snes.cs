// Round 6 (inject): the SNES checks. Super Mario World runs to a place where its music plays (a state is saved there so every experiment starts from the same machine), an instrument is chosen from what the music actually plays,
// edited with the model's own tools, written into a copy of the sound memory the way the plugin does, pushed into the running game, and the game's own audio is measured.
using BrokenNes2;
using BrokenNes2.Instruments;
using NesEmulator.Snes;
using NesEmulator.Systems;

namespace BrokenNes.FruityHost;

internal static partial class InjectChecks
{
    private const int SnesStateFrame = 2200, SnesWindow = 360, SnesRate = 32000;

    /// <summary>Super Mario World parked at a place where the music plays: the ROM, the saved machine, and how to start a session from it.</summary>
    private sealed class SnesRig
    {
        public byte[] Rom = [];
        public string Name = "";
        public byte[] State = [];
        public int Frame;
        /// <summary>The sample the music plays most in the window after the saved place (the instrument the checks edit).</summary>
        public int Slot;
        public Func<int, int> Pad = InjectRun.Pad(InjectRun.SmwScript);

        public static SnesRig? Make(int frame = SnesStateFrame, string? romPath = null, string? script = null)
        {
            var rom = InjectRun.Rom(romPath ?? InjectRun.Smw, out string name);
            if (rom == null) return null;
            var rig = new SnesRig { Rom = rom, Name = name, Frame = frame };
            if (script != null) rig.Pad = InjectRun.Pad(script);
            var s = InjectRun.Create(ConsoleKind.Snes, rom, name);
            InjectRun.Frames(s, 0, frame, rig.Pad);
            rig.State = s.SaveState();
            s.Dispose();
            // the sample the music plays most that loops and sounds like a tone in the middle of the range (a bass rumble or a hiss cannot have its pitch measured by an FFT peak)
            var played = SamplesPlayed(rig, SnesWindow);
            var s2 = rig.Start();
            var loops = new SnesInstruments(Freeze(s2)).List().Where(i => i.Blobs[0].Data.Length >= 9 && (i.Blobs[0].Data[^9] & 2) != 0).Select(i => i.Index).ToHashSet();
            s2.Dispose();
            var candidates = played.Where(p => loops.Contains(p.Slot) && p.KeyOns >= 8).Select(p => p.Slot).Take(6).ToList();
            if (candidates.Count == 0) candidates.Add(played.First().Slot);
            rig.Slot = candidates[0];
            foreach (int cand in candidates)
            {
                rig.Slot = cand;
                var solo = SnesRun(rig, null, true);
                var mono = InjectRun.Mono(solo.Pcm);
                double sum = 0; int n = 0;
                for (int a = 4096 * 2; a + 4096 <= mono.Length && n < 12; a += 4096, n++) sum += InjectSpectrum.Centroid(mono, a, 4096, SnesRate);
                double centroid = n == 0 ? 0 : sum / n;
                if (centroid is >= 300 and <= 3000) break;
                rig.Slot = candidates[0];
            }
            return rig;
        }

        /// <summary>A session in the saved machine.</summary>
        public IConsoleSession Start()
        {
            var s = InjectRun.Create(ConsoleKind.Snes, Rom, Name);
            s.LoadState(State);
            return s;
        }

        public static APU_SFC Spc(IConsoleSession s) => (APU_SFC)((SnesSession)s).Apu;

        /// <summary>The frozen copy Instrument Runaway would take of this machine.</summary>
        public static SpcSnapshot Freeze(IConsoleSession s) { var spc = Spc(s); return new SpcSnapshot(spc.Aram, spc.Dsp.Regs); }

        public List<short> Run(IConsoleSession s, int frames, Action<int>? before = null)
        {
            var pcm = new List<short>();
            InjectRun.Frames(s, Frame, Frame + frames, Pad, pcm, before);
            return pcm;
        }

        /// <summary>The instrument as Runaway lists it for this machine.</summary>
        public static Instrument Instrument(IConsoleSession s, int slot) => new SnesInstruments(Freeze(s)).Read(slot)!;

        /// <summary>Edits <paramref name="slot"/> with the model's own history (Set), writes it into a copy of the frozen sound memory as the plugin's Apply does, and pushes it: returns the outcome and the edited instrument.</summary>
        public static (PushOutcome Outcome, Instrument Edited, SpcSnapshot Work) EditAndPush(IConsoleSession s, int slot, Action<InstrumentHistory> edit)
        {
            var work = Freeze(s);
            var h = new InstrumentHistory(new SnesInstruments(work).Read(slot)!);
            edit(h);
            var w = new SnesInstruments(work).Write(h.Current);
            if (!w.Ok) throw new InvalidOperationException("the edit was refused by the model: " + w.Reason);
            var work2 = (SpcSnapshot)w.Snapshot!;
            return (GamePush.Push(s, work2, h.Current, h.Clean), h.Current, work2);
        }
    }

    /// <summary>Which samples the music plays in the window, how often.</summary>
    private static List<(int Slot, int KeyOns)> SamplesPlayed(SnesRig rig, int frames)
    {
        var s = rig.Start();
        var dsp = SnesRig.Spc(s).Dsp;
        var counts = new int[256];
        dsp.WriteTap = (addr, value) =>
        {
            if (addr != 0x4C) return;
            for (int v = 0; v < 8; v++) if ((value >> v & 1) != 0) counts[dsp.Regs[v * 16 + 4]]++;
        };
        rig.Run(s, frames);
        s.Dispose();
        return Enumerable.Range(0, 256).Where(i => counts[i] > 0).Select(i => (i, counts[i])).OrderByDescending(x => x.Item2).ToList();
    }

    /// <summary>Every sample but <paramref name="keep"/> muted (weights 0), plus the entries of the push: only the instrument under test is heard, and it is heard the way the push makes it.</summary>
    private static DspVoiceOverrides Solo(int keep, IEnumerable<DspVoiceOverrides.Entry>? pushed = null)
    {
        var items = new List<DspVoiceOverrides.Entry>();
        for (int slot = 0; slot < 256; slot++)
            if (slot != keep) items.Add(new DspVoiceOverrides.Entry { Srcn = (byte)slot, Flags = DspVoiceOverrides.HasVol, WeightL = 0, WeightR = 0 });
        if (pushed != null) items.AddRange(pushed);
        return new DspVoiceOverrides(items);
    }

    /// <summary>The strongest spectral peak of each window of a mono signal (NaN for a quiet one).</summary>
    private static double[] PeaksPerWindow(float[] mono, int rate, int n = 4096)
    {
        var r = new List<double>();
        for (int a = 0; a + n <= mono.Length; a += n) r.Add(InjectSpectrum.PeakHz(mono, a, n, rate, 50, 6000, 2e-2));
        return r.ToArray();
    }

    private static void SnesExplore(string[] args)
    {
        int frame = int.TryParse(Opt(args, "--frame"), out int f) ? f : SnesStateFrame;
        string? dkcPath = Opt(args, "--game") == "dkc" ? ImodelGames.Find(@"X:\EMULATION\Super Nintendo\Super Nintendo (U)", "Donkey Kong Country (U) (V1.2)", ".smc") : null;
        var rig = dkcPath != null ? SnesRig.Make(int.TryParse(Opt(args, "--frame"), out int f2) ? f2 : 1500, dkcPath, "") : SnesRig.Make(frame);
        if (rig == null) { Skip("snes-explore", "the game is not there"); return; }
        {
            // every candidate: key-ons and the spectral centroid of its solo audio
            var pl = SamplesPlayed(rig, SnesWindow);
            int keep = rig.Slot;
            foreach (var (slot, keyOns) in pl.Take(8))
            {
                rig.Slot = slot;
                var solo = SnesRun(rig, null, true);
                var mono = InjectRun.Mono(solo.Pcm);
                double sum = 0; int n = 0;
                for (int a = 4096 * 2; a + 4096 <= mono.Length && n < 12; a += 4096, n++) sum += InjectSpectrum.Centroid(mono, a, 4096, SnesRate);
                Console.WriteLine($"    sample {slot}: {keyOns} key-ons, solo rms {InjectSpectrum.Rms(mono, 0, mono.Length):0.00000}, centroid {(n == 0 ? 0 : sum / n):0} Hz");
            }
            rig.Slot = keep;
        }
        var played = SamplesPlayed(rig, SnesWindow);
        Console.WriteLine($"  at frame {frame}, the next {SnesWindow} frames key on: " + string.Join(", ", played.Take(10).Select(p => $"sample {p.Slot} x{p.KeyOns}")) + $"  -> the checks use sample {rig.Slot}");
        // solo baseline vs +1200 cents
        var baseS = rig.Start();
        SnesRig.Spc(baseS).Dsp.Overrides = Solo(rig.Slot);
        var basePcm = rig.Run(baseS, SnesWindow);
        var pushS = rig.Start();
        var (o, edited, _) = SnesRig.EditAndPush(pushS, rig.Slot, h => h.Set("tune.cents", 1200));
        Console.WriteLine("  push: " + o.Ok + " " + o.Message);
        var st = GamePushes.Peek(pushS)!;
        SnesRig.Spc(pushS).Dsp.Overrides = Solo(rig.Slot, st.Dsp!.Entries);
        var pushPcm = rig.Run(pushS, SnesWindow);
        var mb = InjectRun.Mono(basePcm); var mp = InjectRun.Mono(pushPcm);
        var pb = PeaksPerWindow(mb, SnesRate); var pp = PeaksPerWindow(mp, SnesRate);
        Console.WriteLine($"  solo rms base {InjectSpectrum.Rms(mb, 0, mb.Length):0.0000}, pushed {InjectSpectrum.Rms(mp, 0, mp.Length):0.0000}");
        for (int i = 0; i < pb.Length; i++) Console.WriteLine($"    window {i,2}: base {pb[i],8:0.0} Hz   pushed {pp[i],8:0.0} Hz   ratio {pp[i] / pb[i]:0.000}");
        // the five strongest spectral peaks of a few windows, to see what a measure that finds no change is looking at
        foreach (int w in new[] { 4, 10 })
        {
            string Top(float[] m)
            {
                var pw = InjectSpectrum.Power(m, w * 4096, 4096);
                var idx = Enumerable.Range(3, pw.Length - 6).Where(k => pw[k] >= pw[k - 1] && pw[k] >= pw[k + 1]).OrderByDescending(k => pw[k]).Take(6).Select(k => $"{k * (double)SnesRate / 4096:0}Hz:{10 * Math.Log10(pw[k] + 1e-12):0}dB");
                return string.Join("  ", idx);
            }
            Console.WriteLine($"    window {w} peaks   base: {Top(mb)}");
            Console.WriteLine($"    window {w} peaks pushed: {Top(mp)}");
        }
    }

}
