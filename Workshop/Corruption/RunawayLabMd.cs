using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BrokenNes2;
using NesEmulator.Corruption;
using NesEmulator.Mix;
using NesEmulator.Sega;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Corruption;

/// <summary>The Genesis half of the Instrument Runaway lab.</summary>
internal static class LabMd
{
    public const int HostRate = 48000;

    internal sealed record Game(string Name, byte[] Rom, string FileName, string Input, int SetupFrames);

    internal static IConsoleSession Create(byte[] rom, string fileName) =>
        ConsoleSessions.Create(ConsoleKind.Genesis, (byte[])rom.Clone(), CoreCatalog.Default(ConsoleKind.Genesis, CoreSlot.Cpu), CoreCatalog.Default(ConsoleKind.Genesis, CoreSlot.Ppu), CoreCatalog.Default(ConsoleKind.Genesis, CoreSlot.Apu), null, fileName);

    internal static MdSession Md(IConsoleSession s) => (s is NesEmulator.Mix.SegaPictureSession w ? w.Inner : s) as MdSession ?? throw new InvalidOperationException("not a Genesis session");
    internal static MdSoundUnit Unit(IConsoleSession s) => Md(s).Board.Sound as MdSoundUnit ?? throw new InvalidOperationException("the Genesis sound unit is not the real one");

    /// <summary>A Genesis game with the plugin's recorder of the YM2612's DAC writes on it (what a SessionGame does).</summary>
    internal sealed class Rig : IDisposable
    {
        public readonly IConsoleSession Session;
        public readonly DacRecorder Dac = new();
        public readonly GenWriteLog Log = new();
        public Rig(Game g, byte[]? state)
        {
            Session = Create(g.Rom, g.FileName);
            if (state != null) { Session.LoadState(state); LabCommon.Flush(Session); }
            Log.Begin(Sound, MasterHz);
            Sound.YmPortTap = (m, port, v) => { Dac.OnWrite(m, port, v); Log.OnYm(m, port, v); };
            Sound.PsgPortTap = Log.OnPsg;
        }
        public MdSoundUnit Sound => Unit(Session);
        public double MasterHz => Md(Session).Board.Timeline.MasterHz;

        /// <summary>The snapshot Instrument Runaway would take now: the chips' registers, the DAC sounds found, and (unless <paramref name="listen"/> is 0) the game voices of the last <paramref name="listen"/> seconds, as SessionGame.CaptureSega does it.</summary>
        public SegaSnapshot Capture(double listen = 0)
        {
            var snap = SegaSnapshot.FromOpn(Sound, MasterHz, Dac);
            if (listen > 0) snap.Recipes = Recipes(listen, out _);
            return snap;
        }

        public GenRecipe[] Recipes(double seconds, out GenDeriveStats stats)
        {
            var (fm, psg) = Sounding();
            return GenRecipes.Derive(Log, Sound.Clock, (long)(seconds * 32000), fm, psg, out stats);
        }

        public (bool[] Fm, bool[] Psg) Sounding()
        {
            Span<HubVoice> voices = stackalloc HubVoice[Ym2612.VoiceCount];
            Sound.Fm.DescribeVoices(voices);
            var fm = new bool[6]; var psg = new bool[4];
            for (int i = 0; i < 6; i++) fm[i] = voices[i].Level > 0;
            for (int i = 0; i < 4; i++) psg[i] = Sound.Psg.Volume(i) < 15;
            return (fm, psg);
        }
        public void Dispose() => Session.Dispose();
    }

    internal static byte[] Setup(Game g)
    {
        InputScript.TryParse(g.Input, out var script, out _);
        var player = new InputScript.Player(script);
        using var s = Create(g.Rom, g.FileName) as IDisposable ?? throw new InvalidOperationException();
        var session = (IConsoleSession)s;
        LabCommon.RunPcm(session, g.SetupFrames, f => (PadButtons)player.Bits(f));
        return session.SaveState();
    }

    // ------------------------------------------------------------------------------------------------- what Instrument Runaway plays

    /// <summary>One note of an instrument of an OPN snapshot as the plugin plays it (FM: its channel's patch at middle C; a DAC sound: at middle C or its own pitch; the PSG: a tone at A440), 1.5 s at 48 kHz, held 1.2 s. Mono (left).</summary>
    internal static float[] RenderInstrument(SegaSnapshot snap, int instrument)
    {
        int frames = (int)(HostRate * 1.5);
        var outp = new float[frames];
        var s = new OpnSampler(snap, HostRate);
        int slot;
        double hz = 261.6256;
        if (instrument < 6) slot = instrument;
        else if (snap.IsDacInstrument(instrument)) slot = OpnSampler.DacBase;
        else { slot = snap.SlotBase(instrument); hz = 440; }
        s.NoteOn(slot, instrument, hz, 0.8);
        var block = new float[256 * 2];
        int off = (int)(HostRate * 1.2);
        for (int done = 0; done < frames; done += 256)
        {
            int n = Math.Min(256, frames - done);
            if (done <= off && done + n > off) s.NoteOff(slot);
            s.Render(block, n);
            for (int i = 0; i < n; i++) outp[done + i] = block[i * 2];
        }
        return outp;
    }

    /// <summary>One second and a half of a recipe played at its own pitch, full velocity, 48 kHz mono (left).</summary>
    internal static float[] RenderRecipe(SegaSnapshot snap, GenRecipe r, int rate = HostRate, double hold = 1.2)
    {
        int frames = (int)(rate * (hold + 0.3));
        var outp = new float[frames];
        var s = new OpnSampler(snap, rate);
        int slot = r.IsFm ? 0 : OpnSampler.PsgBase + (r.Kind == GenRecipe.KindPsgNoise ? 3 : 0);
        s.NoteOnRecipe(slot, r, 261.6256, 1.0);
        var block = new float[128 * 2];
        int off = (int)(rate * hold);
        for (int done = 0; done < frames; done += 128)
        {
            int n = Math.Min(128, frames - done);
            if (done <= off && done + n > off) s.NoteOff(slot);
            s.Render(block, n);
            for (int i = 0; i < n; i++) outp[done + i] = block[i * 2];
        }
        return outp;
    }

    internal static string RecipeNovelty(SegaSnapshot clean, List<float[]> cleanSounds, SegaSnapshot bad)
    {
        if (bad.Recipes.Length == 0) return $"0 recipes (the game keyed nothing; clean had {clean.Recipes.Length})";
        var hashes = new HashSet<ulong>(clean.Recipes.Select(r => r.Hash()));
        int novel = 0; double sum = 0;
        foreach (var r in bad.Recipes)
        {
            if (!hashes.Contains(r.Hash())) novel++;
            var snd = RenderRecipe(bad, r);
            sum += cleanSounds.Count == 0 ? 20 : cleanSounds.Min(c => LabCommon.SpectralDistance(c, snd, HostRate));
        }
        return $"{bad.Recipes.Length} recipes ({clean.Recipes.Length} clean), {novel} unlike any clean one, mean distance to nearest clean {LabCommon.F(sum / bad.Recipes.Length)}";
    }

    internal sealed record Diff(int Instruments, int Differ, int Beyond, double Mean, double Max)
    {
        public override string ToString() => $"{Beyond}/{Instruments} differ (mean {LabCommon.F(Mean)}, max {LabCommon.F(Max)}; {Differ} not bit-identical)";
    }

    internal static Diff Compare(SegaSnapshot clean, SegaSnapshot bad)
    {
        // the instruments the clean snapshot offers that make a sound: the FM channels that were ever set up or sounding, the DAC sounds, the PSG's tones
        var list = new List<int>();
        for (int c = 0; c < 6; c++) if (clean.OpnPatchUsed(c) || clean.OpnKeyed[c] || bad.OpnPatchUsed(c) || bad.OpnKeyed[c]) list.Add(c);
        for (int d = 0; d < Math.Max(clean.Dac.Length, bad.Dac.Length); d++) list.Add(6 + d);
        list.Add(clean.PsgInstrument + (clean.Dac.Length != bad.Dac.Length ? bad.Dac.Length - clean.Dac.Length : 0) * 0);
        int differ = 0, beyond = 0; double sum = 0, max = 0;
        foreach (int i in list)
        {
            float[] a = i < clean.InstrumentCount && !(i >= 6 && i < 6 + clean.Dac.Length && false) ? SafeRender(clean, i) : new float[(int)(HostRate * 1.5)];
            float[] b = i < bad.InstrumentCount ? SafeRender(bad, i) : new float[(int)(HostRate * 1.5)];
            if (LabCommon.Hash(a) != LabCommon.Hash(b)) differ++;
            double d = LabCommon.SpectralDistance(a, b, HostRate);
            if (d >= 0.5) beyond++;
            sum += d; max = Math.Max(max, d);
        }
        return new Diff(list.Count, differ, beyond, list.Count == 0 ? 0 : sum / list.Count, max);
    }

    private static float[] SafeRender(SegaSnapshot snap, int i)
    {
        try { return RenderInstrument(snap, i); }
        catch (Exception) { return new float[(int)(HostRate * 1.5)]; }
    }

    // ------------------------------------------------------------------------------------------------- classes

    internal sealed record Cls(string Name, string Domain, int[] Addresses, string Note);

    internal static List<Cls> Classes(Game g, byte[] state, int window, out string description)
    {
        // what the sound driver reads and writes over the window
        using var obs = new Rig(g, state);
        var unit = obs.Sound;
        var banks = new HashSet<int>();
        unit.BankReadTap = a => banks.Add(a);
        long ymWrites = 0, keyOns = 0;
        var prevTap = unit.YmPortTap;
        int addr0 = 0;
        unit.YmPortTap = (m, port, v) =>
        {
            prevTap?.Invoke(m, port, v);
            ymWrites++;
            if (port == 0) addr0 = v;
            else if (port == 1 && addr0 == 0x28 && (v & 0xF0) != 0) keyOns++;
        };
        LabCommon.RunPcm(obs.Session, window);
        var rom = Md(obs.Session).Board.Cart.Rom;
        var ram = Md(obs.Session).Board.Ram;
        var zram = unit.Z80Ram;

        // the DAC sound's bytes: a 16-byte run of the cartridge that is a run of the DAC stream
        var (clocks, values) = obs.Dac.Recent();
        var windows = new HashSet<ulong>();
        for (int i = 0; i + 16 <= values.Length; i += 1) windows.Add(Hash16(values, i));
        var dacBytes = new HashSet<int>();
        var sortedBank = banks.OrderBy(a => a).ToArray();
        foreach (int a in sortedBank)
            if (a + 16 <= rom.Length && windows.Contains(Hash16(rom, a))) for (int k = 0; k < 16; k++) dacBytes.Add(a + k);
        // extend the DAC runs: a bank-read address next to a DAC byte
        foreach (int a in sortedBank) if (dacBytes.Contains(a - 1) || dacBytes.Contains(a + 1)) dacBytes.Add(a);
        var music = sortedBank.Where(a => !dacBytes.Contains(a) && a < rom.Length).ToArray();

        var zNonZero = Enumerable.Range(0, zram.Length).Where(i => zram[i] != 0).ToArray();
        var ramNonZero = Enumerable.Range(0, ram.Length).Where(i => ram[i] != 0).ToArray();
        var list = new List<Cls>
        {
            new("ROM bytes the Z80 reads: DAC sample data", "ROM", dacBytes.Where(a => a < rom.Length).OrderBy(a => a).ToArray(), "static"),
            new("ROM bytes the Z80 reads: music, patches, tables", "ROM", music, "driver"),
            new("Z80 RAM (driver code, variables, tables)", "ZRAM", zNonZero, "driver"),
            new("68000 work RAM (game variables, sound queue)", "RAM", ramNonZero, "game"),
            new("ROM: the whole cartridge (code, graphics, data)", "ROM", Enumerable.Range(0, rom.Length).ToArray(), "game"),
        };
        description = $"cartridge {rom.Length / 1024} KB; the unit counted {unit.BankReads:N0} bank reads in all; over {window} frames the Z80 read {banks.Count:N0} distinct bytes through its bank window ({dacBytes.Count:N0} are DAC sample data, {music.Length:N0} music / patches / tables), made {ymWrites:N0} YM2612 writes ({keyOns:N0} key-ons) and {values.Length:N0} DAC writes; Z80 RAM {zNonZero.Length:N0} non-zero bytes, 68000 RAM {ramNonZero.Length:N0}";
        return list;
    }

    private static ulong Hash16(byte[] d, int at)
    {
        ulong h = 1469598103934665603UL;
        for (int k = 0; k < 16; k++) h = (h ^ d[at + k]) * 1099511628211UL;
        return h;
    }

    private static List<CorruptionWrite> MakeWrites(Cls c, int count, long seed)
    {
        var rnd = new SeededRandom(seed);
        var pool = c.Addresses.ToArray();
        count = Math.Min(count, pool.Length);
        for (int i = 0; i < count; i++) { int j = i + rnd.Next(pool.Length - i); (pool[i], pool[j]) = (pool[j], pool[i]); }
        var w = new List<CorruptionWrite>();
        for (int i = 0; i < count; i++) w.Add(new CorruptionWrite(c.Domain, pool[i], (byte)rnd.Next(256)));
        return w;
    }

    private static int CountFor(Cls c) => c.Domain switch
    {
        "ROM" when c.Name.Contains("whole") => 60,
        "RAM" => 300,
        _ => Math.Clamp(c.Addresses.Length / 50, 8, 400),
    };

    /// <summary>--runaway-lab md-matrix --rom X [--name N] [--input script] [--setup N] [--window N] [--seeds N]</summary>
    public static int Matrix(string[] args)
    {
        string Opt(string n, string d) { int i = Array.IndexOf(args, "--" + n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : d; }
        string romPath = Opt("rom", "");
        var rom = LabCommon.ReadRom(romPath, out _, out _);
        var game = new Game(Opt("name", Path.GetFileNameWithoutExtension(romPath)), rom, Path.GetFileName(romPath), Opt("input", ""), int.Parse(Opt("setup", "900")));
        int window = int.Parse(Opt("window", "600")), seeds = int.Parse(Opt("seeds", "2"));
        Console.WriteLine($"== Genesis: {game.Name}  setup {game.SetupFrames} frames, window {window} frames, {seeds} seed(s)");
        var state = Setup(game);
        var classes = Classes(game, state, window, out string desc);
        Console.WriteLine("machine: " + desc);

        using var clean = new Rig(game, state);
        var cleanOnce = clean.Capture();
        int rate = clean.Session.SampleRate;
        var cleanPcm = LabCommon.RunPcm(clean.Session, window);
        var cleanLate = clean.Capture(window / 60.0 + 1);
        var cleanSounds = cleanLate.Recipes.Select(r => RenderRecipe(cleanLate, r)).ToList();
        Console.WriteLine($"clean recipes: {cleanLate.Recipes.Length} ({cleanLate.Recipes.Count(r => r.IsFm)} FM, {cleanLate.Recipes.Count(r => !r.IsFm)} PSG)");
        var cleanMono = LabCommon.ToMono(cleanPcm);
        Console.WriteLine($"clean game over the window: peak {LabCommon.Peak(cleanMono):0.000}, rms {LabCommon.Rms(cleanMono):0.0000}; Runaway captures {cleanOnce.InstrumentCount} instruments at once ({cleanOnce.Dac.Length} DAC sounds) and {cleanLate.InstrumentCount} after the window ({cleanLate.Dac.Length} DAC sounds)");

        foreach (var c in classes)
        {
            if (c.Addresses.Length == 0) { Console.WriteLine($"-- {c.Name}: empty class on this game, skipped"); continue; }
            double gd = 0, gr = 0; int ident = 0, n = 0;
            var onceD = new List<Diff>(); var lateD = new List<Diff>(); var recipeNotes = new List<string>();
            int count = CountFor(c);
            for (int seed = 1; seed <= seeds; seed++)
            {
                var writes = MakeWrites(c, count, seed * 7919L + c.Name.Length);
                using var bad = new Rig(game, state);
                CorruptionApplier.Apply(MachineDomains.For(bad.Session), writes);
                var badOnce = bad.Capture();
                var badPcm = LabCommon.RunPcm(bad.Session, window);
                var badLate = bad.Capture(window / 60.0 + 1);
                recipeNotes.Add(RecipeNovelty(cleanLate, cleanSounds, badLate));
                var badMono = LabCommon.ToMono(badPcm);
                gd += LabCommon.SpectralDistance(cleanMono, badMono, rate);
                gr += LabCommon.Rms(badMono) / Math.Max(1e-9, LabCommon.Rms(cleanMono));
                if (LabCommon.Hash(cleanPcm) == LabCommon.Hash(badPcm)) ident++;
                n++;
                onceD.Add(Compare(cleanOnce, badOnce));
                lateD.Add(Compare(cleanLate, badLate));
            }
            Diff Mean(List<Diff> l) => new((int)l.Average(x => x.Instruments), (int)Math.Round(l.Average(x => x.Differ)), (int)Math.Round(l.Average(x => x.Beyond)), l.Average(x => x.Mean), l.Max(x => x.Max));
            Console.WriteLine($"{c.Name,-52} writes {count,4}  game: dist {LabCommon.F(gd / n),6}{(ident == n ? " (identical)" : "")} level x{LabCommon.F(gr / n)}   Runaway at once: {Mean(onceD)}   after {window / 60} s: {Mean(lateD)}");
            Console.WriteLine($"{"",-52} new capture (recipes, {window / 60} s): {string.Join("  |  ", recipeNotes)}");
        }
        return 0;
    }

    internal enum Resynth { Macro, Exemplar, Plain }

    /// <summary>The game's own FM and PSG notes played again on a private chip set at their original times, from each note's own macro (Macro) or from the macro of the recipe it belongs to (Exemplar), against the game's own sound with its DAC muted.</summary>
    internal static float[] Resynthesize(SegaSnapshot snap, List<GenRecipes.GenNote> notes, GenRecipe[] recipes, int frames, int rate, Resynth mode)
    {
        var outp = new float[frames];
        var s = new OpnSampler(snap, rate) { KeepGamePan = true };
        var order = notes.OrderBy(n => n.Start).ToList();
        int next = 0;
        var endAt = new List<(long Frame, int Slot)>();
        var block = new float[16 * 2];
        for (int done = 0; done < frames; done += 16)
        {
            int n = Math.Min(16, frames - done);
            long t32 = (long)((done + n) * 32000.0 / rate);
            while (next < order.Count && order[next].Start <= t32)
            {
                var note = order[next++];
                var r = note.Recipe;
                var use = r;
                if (mode == Resynth.Plain)
                {
                    // the way Instrument Runaway played before recipes: the channel's patch as it stands in the snapshot, the note's pitch, no macro
                    if (r.IsFm)
                    {
                        double chanHz = NesEmulator.Mix.FmPatchBank.Hz(r.Block, r.Fnum, snap.OpnClock);
                        s.NoteOn(note.Channel, note.Channel, chanHz * OpnSampler.CarrierFundamental(snap.OpnPatch[note.Channel]), 1.0);
                        if (note.KeyedOff) endAt.Add(((long)(note.End * rate / 32000.0), note.Channel));
                    }
                    else
                    {
                        int pslot = OpnSampler.PsgBase + (r.Kind == GenRecipe.KindPsgNoise ? 3 : Math.Min(note.Channel, 2));
                        double hz = r.Kind == GenRecipe.KindPsgNoise ? 440 : snap.PsgClock / (32.0 * Math.Max(1, r.Period));
                        s.NoteOn(pslot, snap.PsgInstrument + (r.Kind == GenRecipe.KindPsgNoise ? 1 : 0), hz, Math.Pow(10, -Math.Min(r.Att, 15) * 2 / 20.0));
                        if (note.KeyedOff) endAt.Add(((long)(note.End * rate / 32000.0), pslot));
                    }
                    continue;
                }
                if (mode == Resynth.Exemplar)
                {
                    var ex = recipes.FirstOrDefault(x => x.Kind == r.Kind && x.Held == r.Held && x.Noise == r.Noise && (r.IsFm ? x.Patch[28] == r.Patch[28] && x.Patch[0] == r.Patch[0] && x.Patch[7] == r.Patch[7] && x.OpMask == r.OpMask : true));
                    if (ex != null) use = new GenRecipe { Kind = r.Kind, Patch = r.Patch, Block = r.Block, Fnum = r.Fnum, OpMask = r.OpMask, Lfo = r.Lfo, Period = r.Period, Att = r.Att, Noise = r.Noise, Events = ex.Events, Uses = 1 };
                }
                int slot = r.IsFm ? note.Channel : OpnSampler.PsgBase + (r.Kind == GenRecipe.KindPsgNoise ? 3 : Math.Min(note.Channel, 2));
                s.NoteOnRecipe(slot, use, 261.6256, 1.0);
                if (note.KeyedOff) endAt.Add(((long)(note.End * rate / 32000.0), slot));
            }
            for (int i = endAt.Count - 1; i >= 0; i--) if (endAt[i].Frame <= done + n) { s.NoteOff(endAt[i].Slot); endAt.RemoveAt(i); }
            s.Render(block, n);
            for (int i = 0; i < n; i++) outp[done + i] = (block[i * 2] + block[i * 2 + 1]) * 0.5f;
        }
        return outp;
    }

    /// <summary>The delay (in samples, 0 to 60 ms) at which <paramref name="b"/> played later best agrees with <paramref name="a"/>, judged on their envelopes (the rectified sound averaged over 2 ms, the mean taken out) over the first four seconds:
    /// notes that sound alike but are a few milliseconds apart must not decide it.</summary>
    internal static int BestLag(float[] a, float[] b, int rate)
    {
        int w = Math.Max(1, rate / 500);
        float[] Env(float[] x)
        {
            var e = new float[x.Length];
            double acc = 0;
            for (int i = 0; i < x.Length; i++) { acc += Math.Abs(x[i]); if (i >= w) acc -= Math.Abs(x[i - w]); e[i] = (float)(acc / w); }
            return e;
        }
        var ea = Env(a); var eb = Env(b);
        int len = Math.Min(Math.Min(ea.Length, eb.Length), 4 * rate) - rate / 10;
        double ma = 0, mb = 0; for (int i = 0; i < len; i++) { ma += ea[i]; mb += eb[i]; }
        ma /= len; mb /= len;
        double best = double.MinValue; int at = 0;
        for (int lag = 0; lag <= rate * 60 / 1000; lag++)
        {
            double sab = 0, saa = 0, sbb = 0;
            for (int i = lag; i < len; i++) { double x = ea[i] - ma, y = eb[i - lag] - mb; sab += x * y; saa += x * x; sbb += y * y; }
            double c = sab / Math.Sqrt(Math.Max(1e-18, saa * sbb));
            if (c > best) { best = c; at = lag; }
        }
        return at;
    }

    internal static float[] Shift(float[] x, int lag)
    {
        var r = new float[x.Length];
        for (int i = lag; i < x.Length; i++) r[i] = x[i - lag];
        return r;
    }

    /// <summary>--runaway-lab md-resynth --rom X [--name N] [--input script] [--setup N] [--window N] [--seeds N] [--writes N] [--domain ZRAM|RAM|ROM]: for a clean game and some corrupted ones, the game's FM and PSG sound (DAC muted) against its notes played again from the recorded macros.</summary>
    public static int ResynthLab(string[] args)
    {
        string Opt(string n, string d) { int i = Array.IndexOf(args, "--" + n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : d; }
        string romPath = Opt("rom", "");
        var rom = LabCommon.ReadRom(romPath, out _, out _);
        var game = new Game(Opt("name", Path.GetFileNameWithoutExtension(romPath)), rom, Path.GetFileName(romPath), Opt("input", ""), int.Parse(Opt("setup", "1200")));
        int window = int.Parse(Opt("window", "600")), seeds = int.Parse(Opt("seeds", "4")), nw = int.Parse(Opt("writes", "120"));
        string domain = Opt("domain", "ZRAM"), only = Opt("only", "");        // --only fm: the PSG is muted in the game and left out of the notes; --only psg: the FM chip is
        var state = Setup(game);
        Console.WriteLine($"== Genesis resynthesis: {game.Name}, window {window} frames, corruptions of {nw} random {domain} writes");
        Console.WriteLine("distance of the game's FM+PSG sound (DAC muted) to: [macro] each note with its own recorded macro, [exemplar] each note with its recipe's macro, [silence]");
        for (int seed = 0; seed <= seeds; seed++)
        {
            using var rig = new Rig(game, state);
            string label = "clean game";
            if (seed > 0)
            {
                var rnd = new SeededRandom(3000 + seed);
                var d = MachineDomains.Find(MachineDomains.For(rig.Session), domain)!;
                var pool = Enumerable.Range(0, d.Size).Where(i => d.Peek(i) != 0 || domain == "ROM").ToArray();
                var writes = new List<CorruptionWrite>();
                for (int i = 0; i < nw; i++) writes.Add(new CorruptionWrite(domain, pool[rnd.Next(pool.Length)], (byte)rnd.Next(256)));
                CorruptionApplier.Apply(MachineDomains.For(rig.Session), writes);
                label = $"corruption, seed {seed}";
            }
            int chOnly = only.StartsWith("ch") ? int.Parse(only[2..]) : -1;
            rig.Sound.FmMuteMask = only == "psg" ? 0x7F : chOnly >= 0 ? 0x7F & ~(1 << chOnly) : 0x40;       // the DAC is not a note: left out of the game's sound
            if (only == "fm" || chOnly >= 0) rig.Sound.Psg.Gain = 0;
            // restart the log from here (the corruption is in), then play the window
            rig.Log.Begin(rig.Sound, rig.MasterHz);
            long m0 = rig.Sound.Clock;
            var pcm = LabCommon.RunPcm(rig.Session, window);
            long m1 = rig.Sound.Clock;
            int rate = rig.Session.SampleRate;
            var game32 = LabCommon.ToMono(pcm);
            var (fm, psg) = rig.Sounding();
            long windowSamples = (long)((m1 - m0) * 32000.0 / rig.MasterHz);
            var notes = GenRecipes.Notes(rig.Log, m1, windowSamples, fm, psg, out var stats, out _);
            if (only == "fm") notes = notes.Where(n => n.Recipe.IsFm).ToList();
            if (only.StartsWith("ch")) notes = notes.Where(n => n.Recipe.IsFm && n.Channel == int.Parse(only[2..])).ToList();
            if (only == "psg") notes = notes.Where(n => !n.Recipe.IsFm).ToList();
            var recipes = GenRecipes.Derive(rig.Log, m1, windowSamples, fm, psg, out _);
            var snap = rig.Capture();
            snap.Recipes = recipes;
            string line = $"{label,-24} rms {LabCommon.Rms(game32):0.0000}, {stats.KeyOns} key-ons, {recipes.Length} recipes ({notes.Count(n => n.Recipe.Held)} held):";
            if (notes.Count == 0) { Console.WriteLine(line + " no notes at all"); continue; }
            int skip = game32.Length / 5;
            // the game's sound comes out of its pipeline a little after the register writes that made it (a constant delay, found here by correlation over the macro resynthesis); the comparison is made after allowing for it
            var reMacro = Resynthesize(snap, notes, recipes, game32.Length, rate, Resynth.Macro);
            int gameLag = BestLag(game32, reMacro, rate);
            line += $" (the game's sound lags its writes by {gameLag * 1000.0 / rate:0.0} ms)";
            foreach (var mode in new[] { Resynth.Macro, Resynth.Exemplar, Resynth.Plain })
            {
                var re = mode == Resynth.Macro ? reMacro : Resynthesize(snap, notes, recipes, game32.Length, rate, mode);
                var shifted = Shift(re, gameLag);
                line += $"  [{mode.ToString().ToLowerInvariant()}] {LabCommon.F(LabCommon.SpectralDistance(game32[skip..], shifted[skip..], rate))}";
            }
            line += $"  [silence] {LabCommon.F(LabCommon.SpectralDistance(game32[skip..], new float[game32.Length - skip], rate))}";
            Console.WriteLine(line);
            if (only.StartsWith("ch") && args.Contains("--dump"))
            {
                var re = Resynthesize(snap, notes, recipes, game32.Length, rate, Resynth.Macro);
                for (int sec = 0; sec * rate < game32.Length; sec++)
                {
                    int a = sec * rate, n = Math.Min(rate, game32.Length - a);
                    Console.WriteLine($"   s{sec}: game rms {LabCommon.Rms(game32, a, n):0.0000} {LabCommon.DominantHz(game32[a..(a + n)], rate):0} Hz   resynth rms {LabCommon.Rms(re, a, n):0.0000} {LabCommon.DominantHz(re[a..(a + n)], rate):0} Hz");
                }
                // the lag at which the two waveforms agree best, over the first two seconds
                int len = Math.Min(2 * rate, game32.Length) - 3000;
                double best = -2, atLag = 0, zero = 0;
                for (int lag = -1500; lag <= 1500; lag++)
                {
                    double sab = 0, saa = 0, sbb = 0;
                    for (int i = 1500; i < len; i++) { double a = game32[i], b = re[i + lag]; sab += a * b; saa += a * a; sbb += b * b; }
                    double c = sab / Math.Sqrt(Math.Max(1e-18, saa * sbb));
                    if (lag == 0) zero = c;
                    if (c > best) { best = c; atLag = lag; }
                }
                int OnsetOf(float[] x) { for (int i = 0; i < x.Length; i++) if (Math.Abs(x[i]) > 0.002f) return i; return -1; }
                Console.WriteLine($"   first sound: game at {OnsetOf(game32) * 1000.0 / rate:0.00} ms, resynthesis at {OnsetOf(re) * 1000.0 / rate:0.00} ms; first note starts at {(notes.Count > 0 ? notes.Min(n => n.Start) / 32.0 : -1):0.00} ms (window starts at master clock {m0:N0}, the unit's clock before the first frame)");
                Console.WriteLine($"   correlation of the game and its resynthesis over 2 s: {zero:0.000} at lag 0, best {best:0.000} at lag {atLag} samples ({atLag * 1000.0 / rate:0.00} ms)");
                // per note: where the sound of the game and of the resynthesis rises, against the note's logged start (the quiet before each note is found by looking 60 ms before it for the last moment under 5 percent of the peak)
                foreach (var nt in notes.Skip(3).Take(6))
                {
                    int st = (int)(nt.Start * rate / 32000.0);
                    double Rise(float[] x)
                    {
                        int a = Math.Max(0, st - rate / 16), b = Math.Min(x.Length, st + rate / 10);
                        float peak = 0; for (int i = a; i < b; i++) peak = Math.Max(peak, Math.Abs(x[i]));
                        for (int i = a; i < b; i++) if (Math.Abs(x[i]) > 0.25f * peak) return (i - st) * 1000.0 / rate;
                        return double.NaN;
                    }
                    Console.WriteLine($"   note at {nt.Start / 32.0:0.0} ms: game rises {Rise(game32):0.0} ms after the logged start, resynthesis {Rise(re):0.0} ms");
                }
                {
                    var first = notes.OrderBy(n => n.Start).Skip(3).First();
                    int st = (int)(first.Start * rate / 32000.0), step = rate / 200;      // 5 ms
                    var gl = new List<string>(); var rl = new List<string>();
                    for (int k = -2; k < 24; k++) { int a = st + k * step; if (a < 0 || a + step > game32.Length) continue; gl.Add($"{LabCommon.Rms(game32, a, step) * 1000:0}"); rl.Add($"{LabCommon.Rms(re, a, step) * 1000:0}"); }
                    Console.WriteLine($"   envelope (rms x1000 per 5 ms from 10 ms before the logged key-on at {first.Start / 32.0:0} ms; alg {first.Recipe.Patch[28] & 7}):");
                    Console.WriteLine("     game      " + string.Join(" ", gl));
                    Console.WriteLine("     resynth   " + string.Join(" ", rl));
                    var pt = first.Recipe.Patch;
                    Console.WriteLine("     patch: " + string.Join(" ", Enumerable.Range(0, 4).Select(o => $"op{o + 1}[mul/dt {pt[o * 7]:X2} tl {pt[o * 7 + 1]:X2} rs/ar {pt[o * 7 + 2]:X2} d1r {pt[o * 7 + 3]:X2} d2r {pt[o * 7 + 4]:X2} sl/rr {pt[o * 7 + 5]:X2} ssg {pt[o * 7 + 6]:X2}]")) + $" B0 {pt[28]:X2} B4 {pt[29]:X2}");
                }
                foreach (var nt in notes.Take(8)) Console.WriteLine($"   note ch{nt.Channel} at {nt.Start / 32000.0:0.00} s for {nt.Recipe.Seconds:0.00} s: alg {nt.Recipe.Patch[28] & 7} block {nt.Recipe.Block} fnum {nt.Recipe.Fnum} ops {nt.Recipe.OpMask:X} events {nt.Recipe.Events.Length}");
            }
        }
        return 0;
    }

    /// <summary>--runaway-lab md-probe --rom X [--frames N] [--input script]: what a Genesis game's sound driver does (per second: peak, YM key-ons, YM and PSG and DAC writes).</summary>
    public static int Probe(string[] args)
    {
        string Opt(string n, string d) { int i = Array.IndexOf(args, "--" + n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : d; }
        string romPath = Opt("rom", "");
        int frames = int.Parse(Opt("frames", "1800"));
        InputScript.TryParse(Opt("input", ""), out var script, out _);
        var rom = LabCommon.ReadRom(romPath, out _, out _);
        var g = new Game(Path.GetFileNameWithoutExtension(romPath), rom, Path.GetFileName(romPath), "", 0);
        using var rig = new Rig(g, null);
        var unit = rig.Sound;
        long ym = 0, keyOns = 0, psg = 0, dac = 0;
        int addr0 = 0;
        var prev = unit.YmPortTap;
        unit.YmPortTap = (m, port, v) =>
        {
            prev?.Invoke(m, port, v);
            ym++;
            if (port == 0) addr0 = v;
            else if (port == 1 && addr0 == 0x28 && (v & 0xF0) != 0) keyOns++;
            else if (port == 1 && addr0 == 0x2A) dac++;
        };
        unit.PsgPortTap = (m, v) => psg++;
        var player = new InputScript.Player(script);
        Console.WriteLine($"game {Path.GetFileName(romPath)}  {frames} frames");
        for (int sec = 0; sec * 60 < frames; sec++)
        {
            var pcm = LabCommon.RunPcm(rig.Session, Math.Min(60, frames - sec * 60), f => (PadButtons)player.Bits(f), sec * 60);
            var mono = LabCommon.ToMono(pcm);
            Console.WriteLine($"  s{sec,3}: peak {LabCommon.Peak(mono):0.000} rms {LabCommon.Rms(mono):0.0000}  FM key-ons so far {keyOns,5}  YM writes {ym,7}  DAC writes {dac,7}  PSG writes {psg,6}");
        }
        return 0;
    }
}
