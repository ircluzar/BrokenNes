// Round 6 (inject): the Game Boy and Master System checks on real games. A Game Boy wave and a YM2413 user instrument are pushed into the running game (Link's Awakening on a Game Boy, Galactic Protector on the Master System's FM unit),
// the game runs on, and the chip's own memory shows whether the edit holds and what the driver does to it.
using BrokenNes2;
using BrokenNes2.Instruments;
using NesEmulator.Gb;
using NesEmulator.Sega;
using NesEmulator.Systems;

namespace BrokenNes.FruityHost;

internal static partial class InjectChecks
{
    private sealed class CountOpll : IYm2413Override
    {
        public int KeyOns;
        public readonly HashSet<string> Sets = new();
        public void OnKeyOn(Ym2413 chip, int channel) { KeyOns++; Sets.Add(Convert.ToHexString(Enumerable.Range(0, 8).Select(i => chip.Register(i)).ToArray())); }
    }

    private static (byte[] Rom, string Name)? SmsFmRom()
    {
        string? argos = ImodelGames.Find(@"X:\EMULATION\Sega Master System", "Galactic Protector", ".7z");
        if (argos == null) return null;
        var rom = ImodelGames.LoadRom(argos, out string name);
        return rom == null ? null : (rom, name);
    }

    private static void SmsExplore(string[] args)
    {
        if (Opt(args, "--games") is { } list)
        {
            // which of these games keys the YM2413's user instrument (instrument 0) while its FM sound plays
            foreach (var frag in list.Split(','))
            {
                string? path = ImodelGames.Find(@"X:\EMULATION\Sega Master System", frag.Trim(), ".7z", ".sms", ".zip");
                if (path == null) { Console.WriteLine($"  {frag}: not found"); continue; }
                var rom0 = ImodelGames.LoadRom(path, out string nm);
                if (rom0 == null) { Console.WriteLine($"  {frag}: could not read"); continue; }
                var ss = InjectRun.Create(ConsoleKind.MasterSystem, rom0, nm, "SMS-FM");
                var inn = ss is NesEmulator.Mix.SegaPictureSession ww ? ww.Inner : ss;
                var fmu = ((SmsSession)inn).Board.Sound as SmsFmSound;
                if (fmu == null) { Console.WriteLine($"  {nm}: no FM unit"); continue; }
                var cc = new CountOpll(); fmu.Fm.Override = cc;
                InjectRun.Frames(ss, 0, 3600, InjectRun.Pad("200:Start,210:,500:Start,510:,800:A,810:,1200:Start,1210:,1600:A,1610:"));
                Console.WriteLine($"  {nm}: user-instrument key-ons {cc.KeyOns}, {cc.Sets.Count} register sets; FM enabled={fmu.FmEnabled}");
            }
            return;
        }
        var r = SmsFmRom();
        if (r == null) { Skip("sms-explore", "Galactic Protector is not there"); return; }
        var s = InjectRun.Create(ConsoleKind.MasterSystem, r.Value.Rom, r.Value.Name, "SMS-FM");
        var inner = s is NesEmulator.Mix.SegaPictureSession w ? w.Inner : s;
        var fm = ((SmsSession)inner).Board.Sound as SmsFmSound;
        if (fm == null) { Console.WriteLine("  not an FM unit: " + s.Description); return; }
        var c = new CountOpll();
        fm.Fm.Override = c;
        int loads = 0; string last = "";
        InjectRun.Frames(s, 0, 3000, InjectRun.Pad("200:Start,210:,500:Start,510:,800:A,810:"), null, f =>
        {
            string regs = Convert.ToHexString(Enumerable.Range(0, 8).Select(i => fm.Fm.Register(i)).ToArray());
            if (regs != last) { loads++; last = regs; }
        });
        Console.WriteLine($"  Galactic Protector, 3000 frames: user-instrument key-ons {c.KeyOns}, the user instrument's registers changed {loads} times between frames, {c.Sets.Count} distinct sets at key-on; now {last}");
    }

    // ---- Game Boy ----

    private static (byte[] Rom, string Name)? GbRom()
    {
        string? zelda = ImodelGames.Find(@"X:\EMULATION\GameBoy", "Link's Awakening", ".zip");
        if (zelda == null) return null;
        var rom = ImodelGames.LoadRom(zelda, out string name);
        return rom == null ? null : (rom, name);
    }

    private static void GbGameChecks(bool mutants)
    {
        var r = GbRom();
        if (r == null) { Skip("gb-wave-game", "Link's Awakening is not there"); return; }
        const string script = "300:Start,310:,600:Start,610:,900:A,910:";
        const int stateFrame = 1500, window = 600;
        var pad = InjectRun.Pad(script);
        var s0 = InjectRun.Create(ConsoleKind.GameBoy, r.Value.Rom, r.Value.Name);
        InjectRun.Frames(s0, 0, stateFrame, pad);
        byte[] state = s0.SaveState();
        var apu0 = (APU_GB)((GbSession)s0).Apu;
        var snap = GbSnapshot.FromApu(apu0);
        var wave0 = (byte[])apu0.WaveRam.Clone();
        IConsoleSession Start() { var x = InjectRun.Create(ConsoleKind.GameBoy, r.Value.Rom, r.Value.Name); x.LoadState(state); return x; }
        // the wave the game has when it freezes, edited with the model: every sample halved toward the middle (the channel plays at half amplitude)
        var inst = new GbInstruments(snap).Read(2)!;
        var half = new byte[16];
        for (int i = 0; i < 16; i++) { int hi = inst.Blobs[0].Data[i] >> 4, lo = inst.Blobs[0].Data[i] & 15; half[i] = (byte)(((8 + (hi - 8) / 2) << 4) | (8 + (lo - 8) / 2)); }

        (List<short> Pcm, int Frames, int InPlace, IConsoleSession S, PushOutcome? O) Play(bool push, bool removeAtOnce = false)
        {
            var s = Start();
            var apu = (APU_GB)((GbSession)s).Apu;
            PushOutcome? o = null;
            if (push)
            {
                var h = new InstrumentHistory(inst);
                h.SetBytes("wave", 0, half);
                var w = new GbInstruments(snap).Write(h.Current);
                o = GamePush.Push(s, w.Snapshot, h.Current, h.Clean);
                if (removeAtOnce) GamePush.Remove(s, PushKind.GbWave, 2);
            }
            var pcm = new List<short>(); int frames = 0, inPlace = 0;
            InjectRun.Frames(s, stateFrame, stateFrame + window, pad, pcm, f => { frames++; if (apu.WaveRam.AsSpan().SequenceEqual(half)) inPlace++; });
            return (pcm, frames, inPlace, s, o);
        }
        var baseRun = Play(false);
        ulong h0 = InjectRun.Hash(baseRun.Pcm);
        Check(h0 == InjectRun.Hash(Play(false).Pcm), "gb-determinism", $"a session loaded from the saved machine plays {baseRun.Pcm.Count / 2} frames of audio twice, hash {h0:X16}");
        Run("gb-wave-game", () =>
        {
            var p = Play(true);
            ulong h = InjectRun.Hash(p.Pcm);
            var apu = (APU_GB)((GbSession)p.S).Apu;
            return (p.O!.Ok && h != h0 && p.InPlace >= p.Frames * 0.9, $"the edited wave pushed into Link's Awakening: the wave RAM held it at {p.InPlace} of {p.Frames} frame starts (the game itself loads its waves only when an instrument changes), the game's audio hash {h:X16} against {h0:X16}; \"{p.O.Message}\"");
        }, PushMutants.GbNoSubstitution, mutants, "the push writes and substitutes nothing");
        Run("gb-wave-remove", () =>
        {
            var p = Play(true, removeAtOnce: true);
            ulong h = InjectRun.Hash(p.Pcm);
            return (p.O!.Ok && h == h0 && p.InPlace == 0, $"pushed and removed at once: the game's audio hash {h:X16} against the untouched run's {h0:X16}, the edited wave never in the RAM ({p.InPlace} frame starts)");
        });
    }

    // ---- Master System, FM unit ----

    private static void SmsGameChecks(bool mutants)
    {
        var r = SmsFmRom();
        if (r == null) { Skip("opll-user-game", "Galactic Protector is not there"); return; }
        const int stateFrame = 1200, window = 600;
        var pad = InjectRun.Pad("200:Start,210:,500:Start,510:,800:A,810:");
        IConsoleSession New() => InjectRun.Create(ConsoleKind.MasterSystem, r.Value.Rom, r.Value.Name, "SMS-FM");
        var s0 = New();
        InjectRun.Frames(s0, 0, stateFrame, pad);
        byte[] state = s0.SaveState();
        var inner0 = s0 is NesEmulator.Mix.SegaPictureSession w0 ? w0.Inner : s0;
        var fm0 = (SmsFmSound)((SmsSession)inner0).Board.Sound;
        var t = ((SmsSession)inner0).Board.Timeline;
        var snap = SegaSnapshot.FromOpll(fm0, t.MasterHz / t.Z80.Divider);
        IConsoleSession Start() { var x = New(); x.LoadState(state); return x; }
        var inst = new SegaInstruments(snap).Read(0)!;
        var c0 = new CountOpll();
        var probe = Start(); ((SmsFmSound)((SmsSession)(probe is NesEmulator.Mix.SegaPictureSession w1 ? w1.Inner : probe)).Board.Sound).Fm.Override = c0;
        InjectRun.Frames(probe, stateFrame, stateFrame + window, pad);
        Console.WriteLine($"  (Galactic Protector parked at frame {stateFrame}: its user instrument is keyed {c0.KeyOns} times in the next {window} frames with {c0.Sets.Count} different register sets; now {Convert.ToHexString(snap.Opll[..8])})");
        if (c0.KeyOns < 5) { Skip("opll-user-game", $"the game keys the user instrument only {c0.KeyOns} times in the window"); return; }
        var edit = new InstrumentHistory(inst);
        edit.Set("mod.mul", (inst.Get("mod.mul") + 3) & 15); edit.Set("car.mul", (inst.Get("car.mul") + 1) & 15);
        (ulong Hash, PushOutcome? O) Play(bool push, bool removeAtOnce = false)
        {
            var s = Start();
            PushOutcome? o = null;
            if (push)
            {
                var h = new InstrumentHistory(inst);
                h.Set("mod.mul", (inst.Get("mod.mul") + 3) & 15); h.Set("car.mul", (inst.Get("car.mul") + 1) & 15);
                var w = new SegaInstruments(snap).Write(h.Current);
                o = GamePush.Push(s, w.Snapshot, h.Current, h.Clean);
                if (removeAtOnce) GamePush.Remove(s, PushKind.OpllUser, 0);
            }
            var pcm = new List<short>();
            InjectRun.Frames(s, stateFrame, stateFrame + window, pad, pcm);
            return (InjectRun.Hash(pcm), o);
        }
        ulong h0 = Play(false).Hash;
        Check(h0 == Play(false).Hash, "opll-determinism", $"the machine plays the same twice, hash {h0:X16}");
        Run("opll-user-game", () =>
        {
            var p = Play(true);
            return (p.O!.Ok && p.Hash != h0, $"the edited user instrument pushed into Galactic Protector: the game's audio hash {p.Hash:X16} against {h0:X16}; \"{p.O.Message}\"");
        }, PushMutants.OpllNoSubstitution, mutants, "the key-on does not substitute");
        Run("opll-user-remove", () =>
        {
            var p = Play(true, removeAtOnce: true);
            return (p.O!.Ok && p.Hash == h0, $"pushed and removed at once: the game's audio hash {p.Hash:X16} against the untouched run's {h0:X16}");
        });
    }
}
