// Round 6 (inject): the Genesis checks. Sonic the Hedgehog runs to a place where its music plays (a state is saved there), the FM channel whose patch the music plays most is chosen, and the game's own FM channel (the sound unit's
// stem for it, which is the game's output, not Runaway's sampler) is measured with and without the push.
using BrokenNes2;
using BrokenNes2.Instruments;
using NesEmulator.Sega;
using NesEmulator.Systems;

namespace BrokenNes.FruityHost;

internal static partial class InjectChecks
{
    private const int GenesisStateFrame = 1400, GenesisWindow = 300;

    private sealed class GenesisRig
    {
        public byte[] Rom = [];
        public string Name = "";
        public byte[] State = [];
        public int Frame;
        public SegaSnapshot Snapshot = null!;          // what Instrument Runaway would take at the saved place (DAC sounds found in the writes before it)
        public int Channel;                            // the FM channel whose patch the music plays most in the window
        public int KeyOns;
        public double MasterHz;
        public Func<int, int> Pad = InjectRun.Pad(InjectRun.SonicScript);

        public static GenesisRig? Make(int frame = GenesisStateFrame)
        {
            var rom = InjectRun.Rom(InjectRun.Sonic1, out string name);
            if (rom == null) return null;
            var rig = new GenesisRig { Rom = rom, Name = name, Frame = frame };
            var s = InjectRun.Create(ConsoleKind.Genesis, rom, name);
            var rec = new DacRecorder();
            var unit = Unit(s);
            unit.YmPortTap = rec.OnWrite;
            InjectRun.Frames(s, 0, frame, rig.Pad);
            rig.MasterHz = ((MdSession)Inner(s)).Board.Timeline.MasterHz;
            rig.Snapshot = SegaSnapshot.FromOpn(unit, rig.MasterHz, rec);
            rig.State = s.SaveState();
            s.Dispose();
            rig.PickChannel();
            return rig;
        }

        public static IConsoleSession Inner(IConsoleSession s) => s is NesEmulator.Mix.SegaPictureSession w ? w.Inner : s;
        public static MdSoundUnit Unit(IConsoleSession s) => (MdSoundUnit)((MdSession)Inner(s)).Board.Sound;

        public IConsoleSession Start()
        {
            var s = InjectRun.Create(ConsoleKind.Genesis, Rom, Name);
            s.LoadState(State);
            return s;
        }

        public SegaSnapshot Freeze(IConsoleSession s) => Snapshot;

        /// <summary>Runs the window; the audio of the whole mix (shorts, stereo) and of the stems asked for (mono floats) come back.</summary>
        public (List<short> Mix, Dictionary<int, List<float>> Stems) Run(IConsoleSession s, int frames, IEnumerable<int> stems, Action<int>? before = null)
        {
            var unit = Unit(s);
            long mask = 0; foreach (int st in stems) mask |= 1L << st;
            unit.StemMask = mask;
            var mix = new List<short>();
            var res = stems.ToDictionary(x => x, _ => new List<float>());
            var buf = new short[1 << 16]; var tmp = new float[1 << 14];
            for (int f = Frame; f < Frame + frames; f++)
            {
                before?.Invoke(f);
                s.SetPad(0, (PadButtons)Pad(f));
                s.RunFrame();
                int n;
                while ((n = s.ReadSamples(buf)) > 0) for (int i = 0; i < n; i++) mix.Add(buf[i]);
                foreach (int st in res.Keys)
                {
                    int k;
                    while ((k = unit.ReadStem(st, tmp)) > 0) for (int i = 0; i < k; i++) res[st].Add(tmp[i]);
                }
            }
            return (mix, res);
        }

        /// <summary>Counts, for the window after the saved place, how often each channel is keyed on while it holds the patch it holds now; the channel with the most (and a patch whose multiples can all be doubled) is the one the checks edit.</summary>
        private void PickChannel()
        {
            var s = Start();
            var unit = Unit(s);
            var ym = unit.Fm;
            var now = new byte[6][];
            for (int c = 0; c < 6; c++) { now[c] = new byte[30]; OpnPushHook.ReadPatch(ym, c, now[c]); }
            var count = new int[6];
            var carrier = new Func<int, int, bool>((alg, n) => alg switch { <= 3 => n == 3, 4 => n >= 2, 5 or 6 => n >= 1, _ => true });
            ym.Override = new CountKeyOns(now, count, carrier);
            Run(s, GenesisWindow, []);
            var ok = Enumerable.Range(0, 6).Where(c => PatchCanBeDoubled(now[c])).OrderByDescending(c => count[c]).ToList();
            Channel = ok.Count > 0 ? ok[0] : 0;
            KeyOns = count[Channel];
            s.Dispose();
        }

        private sealed class CountKeyOns(byte[][] now, int[] count, Func<int, int, bool> carrier) : IYm2612Override
        {
            public void OnKeyOn(Ym2612 chip, int channel)
            {
                Span<byte> cur = stackalloc byte[30];
                OpnPushHook.ReadPatch(chip, channel, cur);
                int alg = now[channel][28] & 7;
                for (int i = 0; i < 29; i++)
                {
                    if (i < 28 && i % 7 == 1 && carrier(alg, i / 7)) continue;
                    if (cur[i] != now[channel][i]) return;
                }
                count[channel]++;
            }
            public byte OnDacWrite(byte value, long chipClock) => value;
        }

        public static bool PatchCanBeDoubled(byte[] p)
        {
            for (int n = 0; n < 4; n++) { int mul = p[n * 7] & 15; if (mul > 7) return false; }
            return (p[28] & 7) is >= 0 and <= 7 && p.Take(28).Any(b => b != 0);
        }
    }

    private static void GenesisExplore(string[] args)
    {
        int frame = int.TryParse(Opt(args, "--frame"), out int f) ? f : GenesisStateFrame;
        var rig = GenesisRig.Make(frame);
        if (rig == null) { Skip("genesis-explore", "Sonic the Hedgehog is not there"); return; }
        Console.WriteLine($"  at frame {frame}: channel {rig.Channel + 1} (key-ons with its patch in the next {GenesisWindow} frames: {rig.KeyOns}); DAC sounds found: {rig.Snapshot.Dac.Length}");
        if (args.Contains("--trace"))
        {
            // the writes the driver makes to the chosen channel: key-ons, total levels, the other patch registers, frequency, in order
            var ts = rig.Start();
            var tu = GenesisRig.Unit(ts);
            int a0 = 0, a1 = 0, shown = 0, fr = rig.Frame;
            int ch = rig.Channel, c3 = ch % 3, part = ch / 3;
            tu.YmPortTap = (m, port, v) =>
            {
                if (port == 0) { a0 = v; return; }
                if (port == 2) { a1 = v; return; }
                int reg = port == 1 ? a0 : 0x100 | a1; int low = reg & 0xFF, pt = reg >> 8;
                string? what = null;
                if (reg == 0x28 && (v & 3) != 3 && ((v & 3) + (v >> 2 & 1) * 3) == ch) what = (v >> 4) != 0 ? $"KEY ON  ({v >> 4:X})" : "key off";
                else if (pt == part && low >= 0x30 && low <= 0x9F && (low & 3) == c3) what = $"{(low >= 0x40 && low < 0x50 ? "TL" : "patch")} reg ${low:X2} = {v & 0x7F} (raw {v})";
                else if (pt == part && low is >= 0xA0 and <= 0xA6 && (low & 3) == c3) what = $"freq reg ${low:X2} = {v:X2}";
                else if (pt == part && low is >= 0xB0 and <= 0xB6 && (low & 3) == c3) what = $"channel reg ${low:X2} = {v:X2}";
                if (what != null && shown < 70) { shown++; Console.WriteLine($"    frame {fr}: {what}"); }
            };
            InjectRun.Frames(ts, rig.Frame, rig.Frame + 120, rig.Pad, null, fn => fr = fn);
            return;
        }
        for (int c = 0; c < 6; c++)
        {
            var p = rig.Snapshot.OpnPatch[c];
            Console.WriteLine($"    FM{c + 1}: alg {p[28] & 7} fb {p[28] >> 3} mul {string.Join(",", Enumerable.Range(0, 4).Select(n => p[n * 7] & 15))} tl {string.Join(",", Enumerable.Range(0, 4).Select(n => p[n * 7 + 1]))} keyed={rig.Snapshot.OpnKeyed[c]}");
        }
        for (int i = 0; i < rig.Snapshot.Dac.Length; i++) Console.WriteLine($"    DAC {i + 1}: {rig.Snapshot.Dac[i].Data.Length} samples at {rig.Snapshot.Dac[i].Rate} Hz, played {rig.Snapshot.Dac[i].Repeats}x, loops={rig.Snapshot.Dac[i].Loops}");
        var s = rig.Start();
        var (mix, stems) = rig.Run(s, GenesisWindow, [SegaStems.MdFm1 + rig.Channel, SegaStems.MdDac]);
        var fm = stems[SegaStems.MdFm1 + rig.Channel].ToArray(); var dac = stems[SegaStems.MdDac].ToArray();
        Console.WriteLine($"  mix {mix.Count / 2} frames hash {InjectRun.Hash(mix):X16}; FM{rig.Channel + 1} stem {fm.Length} samples rms {InjectSpectrum.Rms(fm, 0, fm.Length):0.00000}; DAC stem rms {InjectSpectrum.Rms(dac, 0, dac.Length):0.00000}");
    }
}
