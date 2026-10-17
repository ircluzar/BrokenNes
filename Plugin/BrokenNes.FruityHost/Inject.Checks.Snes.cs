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

        public static SnesRig? Make(int frame = SnesStateFrame)
        {
            var rom = InjectRun.Rom(InjectRun.Smw, out string name);
            if (rom == null) return null;
            var rig = new SnesRig { Rom = rom, Name = name, Frame = frame };
            var s = InjectRun.Create(ConsoleKind.Snes, rom, name);
            InjectRun.Frames(s, 0, frame, rig.Pad);
            rig.State = s.SaveState();
            s.Dispose();
            rig.Slot = SamplesPlayed(rig, SnesWindow).First().Slot;
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
        for (int a = 0; a + n <= mono.Length; a += n) r.Add(InjectSpectrum.PeakHz(mono, a, n, rate, 120, 6000, 2e-2));
        return r.ToArray();
    }

    private static void SnesExplore(string[] args)
    {
        int frame = int.TryParse(Opt(args, "--frame"), out int f) ? f : SnesStateFrame;
        var rig = SnesRig.Make(frame);
        if (rig == null) { Skip("snes-explore", "Super Mario World is not there"); return; }
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
    }

}
