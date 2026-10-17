// Round 6 (inject): the Game Boy and Master System (YM2413) checks. Both are best effort: the Game Boy wave RAM is written into the unit and substituted again when the wave channel is triggered while it still holds the game's own wave;
// the YM2413's user instrument is substituted at key-on. A real game from the user's library shows how often its driver loads those (so what a push can hold); the sound is checked on a bare chip, where one channel is all there is.
using BrokenNes2;
using BrokenNes2.Instruments;
using NesEmulator.Gb;
using NesEmulator.Sega;
using NesEmulator.Systems;

namespace BrokenNes.FruityHost;

internal static partial class InjectChecks
{
    private sealed class CountTriggers : IGbApuOverride
    {
        public int[] Triggers = new int[4];
        public int Wave0, WaveOther;
        public byte[]? Reference;
        public void OnTrigger(APU_GB apu, int channel)
        {
            Triggers[channel]++;
            if (channel == 2 && Reference != null) { if (apu.WaveRam.AsSpan().SequenceEqual(Reference)) Wave0++; else WaveOther++; }
        }
    }

    private static void GbExplore(string[] args)
    {
        string? zelda = ImodelGames.Find(@"X:\EMULATION\GameBoy", "Link's Awakening", ".zip");
        if (zelda == null) { Skip("gb-explore", "Link's Awakening is not there"); return; }
        var rom = ImodelGames.LoadRom(zelda, out string name)!;
        var s = InjectRun.Create(ConsoleKind.GameBoy, rom, name);
        var apu = (APU_GB)((GbSession)s).Apu;
        var c = new CountTriggers();
        apu.Override = c;
        var seen = new Dictionary<string, int>();
        var changes = 0; string last = "";
        InjectRun.Frames(s, 0, 3600, InjectRun.Pad("300:Start,310:,600:Start,610:,900:A,910:"), null, f =>
        {
            string w = Convert.ToHexString(apu.WaveRam);
            if (w != last) { changes++; last = w; seen[w] = seen.GetValueOrDefault(w) + 1; }
            if (f == 1500) c.Reference = (byte[])apu.WaveRam.Clone();
        });
        Console.WriteLine($"  Link's Awakening, 3600 frames: triggers per channel {string.Join(",", c.Triggers)}; the wave RAM changed {changes} times between frames, {seen.Count} distinct waves; after frame 1500 the wave channel was triggered with the wave of frame 1500 {c.Wave0} times and another {c.WaveOther} times");
    }


    private static void OtherChecks(bool mutants)
    {
        GbUnit(mutants);
        OpllUnit(mutants);
    }

    // ---- Game Boy ----

    private static void GbUnit(bool mutants)
    {
        byte[] orig = Convert.FromHexString("0123456789ABCDEFFEDCBA9876543210");
        byte[] edit = Convert.FromHexString("FFFFFFFF00000000FFFFFFFF00000000");
        static void Note(APU_GB apu)
        {
            apu.WriteRegister(0x26, 0x80); apu.WriteRegister(0x24, 0x77); apu.WriteRegister(0x25, 0x44);
            apu.WriteRegister(0x1A, 0x80); apu.WriteRegister(0x1C, 0x20);           // channel 3: DAC on, full level
            apu.WriteRegister(0x1D, 0x00); apu.WriteRegister(0x1E, 0x84);           // period 0x400, triggered
        }
        List<short> Render(APU_GB apu, int n)
        {
            var o = new List<short>(); var buf = new short[1 << 14];
            while (o.Count < n * 2) { apu.Tick(70224 / 4); int k; while ((k = apu.ReadSamples(buf)) > 0) for (int i = 0; i < k; i++) o.Add(buf[i]); }
            return o;
        }
        Run("gb-wave-unit", () =>
        {
            var probs = new List<string>();
            void Expect(string what, bool ok) { if (!ok) probs.Add(what); }
            // the hook: the game loads its wave, triggers the channel: the edited wave plays
            var rec = new PushRecord { Kind = PushKind.GbWave, Index = 2, Name = "t", A = orig, B = edit, Regions = { new PushRegion { Domain = "WAVE", Address = 0, Original = orig, Pushed = edit } } };
            var hook = new GbPushHook([rec]);
            var hooked = new APU_GB(GbModel.Dmg) { Override = hook };
            orig.CopyTo(hooked.WaveRam, 0); Note(hooked);
            Expect("the trigger writes the edited wave into the RAM", hooked.WaveRam.AsSpan().SequenceEqual(edit));
            var a = Render(hooked, 4000);
            // the same sound made by a bare unit that was handed the edited wave directly
            var direct = new APU_GB(GbModel.Dmg); edit.CopyTo(direct.WaveRam, 0); Note(direct);
            var b = Render(direct, 4000);
            var plain = new APU_GB(GbModel.Dmg); orig.CopyTo(plain.WaveRam, 0); Note(plain);
            var c = Render(plain, 4000);
            Expect("the hooked unit plays exactly what a unit given the edited wave plays", InjectRun.Hash(a) == InjectRun.Hash(b));
            Expect("and that is not what the game's own wave plays", InjectRun.Hash(a) != InjectRun.Hash(c));
            // another wave in the RAM (the game loaded one the push knows nothing about) is left alone
            var other = Convert.FromHexString("00112233445566778899AABBCCDDEEFF");
            var u = new APU_GB(GbModel.Dmg) { Override = hook }; other.CopyTo(u.WaveRam, 0); Note(u);
            Expect("a wave the push does not know is left alone", u.WaveRam.AsSpan().SequenceEqual(other));
            // the pulse channels do not touch the wave RAM
            var p = new APU_GB(GbModel.Dmg) { Override = hook }; orig.CopyTo(p.WaveRam, 0);
            p.WriteRegister(0x26, 0x80); p.WriteRegister(0x12, 0xF0); p.WriteRegister(0x13, 0x00); p.WriteRegister(0x14, 0x84);
            Expect("a pulse trigger does not touch the wave", p.WaveRam.AsSpan().SequenceEqual(orig));
            return (probs.Count == 0, probs.Count == 0 ? "a wave channel trigger while the RAM holds the game's wave loads the edited one, and the unit then plays exactly what a unit handed the edited wave plays; other waves and the pulse channels are left alone" : string.Join("; ", probs));
        }, PushMutants.GbNoSubstitution, mutants, "the trigger does not substitute");
    }

    // ---- YM2413 ----

    private static void OpllUnit(bool mutants)
    {
        byte[] orig = [0x21, 0x21, 0x1A, 0x07, 0xF0, 0xF1, 0x11, 0x12];
        byte[] edit = [0x01, 0x42, 0x30, 0x0D, 0xA2, 0xF8, 0x25, 0x47];
        Run("opll-user-unit", () =>
        {
            var probs = new List<string>();
            void Expect(string what, bool ok) { if (!ok) probs.Add(what); }
            var rec = new PushRecord { Kind = PushKind.OpllUser, Index = 0, Name = "t", A = orig, B = edit };
            var hook = new OpllPushHook([rec]);
            Ym2413 Make(bool hooked)
            {
                var chip = new Ym2413(3579545.0) { Override = hooked ? hook : null };
                for (int i = 0; i < 8; i++) chip.WriteRegister(i, orig[i]);
                chip.WriteRegister(0x30, 0x00);                         // channel 1: the user instrument, loudest
                chip.WriteRegister(0x10, 0xAC); chip.WriteRegister(0x20, 0x10 | 0x0A);       // note on
                return chip;
            }
            List<short> Render(Ym2413 chip, int n) { var o = new List<short>(); for (int i = 0; i < n; i++) o.Add((short)Math.Clamp(chip.Calc(), -32768, 32767)); return o; }
            var h = Make(true);
            bool regs = Enumerable.Range(0, 8).All(i => h.Register(i) == edit[i]);
            Expect("the key-on loads the edited instrument's eight registers", regs);
            var direct = new Ym2413(3579545.0);
            for (int i = 0; i < 8; i++) direct.WriteRegister(i, edit[i]);
            direct.WriteRegister(0x30, 0x00); direct.WriteRegister(0x10, 0xAC); direct.WriteRegister(0x20, 0x10 | 0x0A);
            Expect("the chip then plays exactly what a chip given the edited instrument plays", InjectRun.Hash(Render(h, 6000)) == InjectRun.Hash(Render(direct, 6000)));
            Expect("and that is not what the game's own instrument plays", InjectRun.Hash(Render(Make(false), 6000)) != InjectRun.Hash(Render(Make(true), 6000)));
            // a channel that plays a ROM instrument does not trigger it
            var rom = new Ym2413(3579545.0) { Override = hook };
            for (int i = 0; i < 8; i++) rom.WriteRegister(i, orig[i]);
            rom.WriteRegister(0x31, 0x30); rom.WriteRegister(0x11, 0xAC); rom.WriteRegister(0x21, 0x10 | 0x0A);
            Expect("a channel on a ROM instrument leaves the user instrument alone", Enumerable.Range(0, 8).All(i => rom.Register(i) == orig[i]));
            return (probs.Count == 0, probs.Count == 0 ? "a key-on on the user instrument while its registers are the game's loads the edited eight, and the chip then plays exactly what a chip handed the edited instrument plays; ROM instruments are left alone" : string.Join("; ", probs));
        }, PushMutants.OpllNoSubstitution, mutants, "the key-on does not substitute");
    }
}
