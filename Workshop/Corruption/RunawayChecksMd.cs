using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BrokenNes2;
using NesEmulator;
using NesEmulator.Corruption;
using NesEmulator.Mix;
using NesEmulator.Sega;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Corruption;

/// <summary>The Genesis half of --runaway-test: scripted notes against the chips they were written to, the project section, and a real game.</summary>
internal static partial class RunawayChecks
{
    private const double MasterHz = 53_693_175.0;
    private static readonly double OpnClock = Math.Round(MasterHz / 7);

    // ===================================================================================================================== ground truth: the chips fed a script

    /// <summary>A YM2612 with the sampler's own mixing path, fed a script of timed writes: what a game's driver makes the chip do, to compare a recipe's replay with.</summary>
    private sealed class YmRig : IYm2612Sink
    {
        private readonly Ym2612 ym;
        private readonly BandLimitedMixer mixL;
        private float lastL;
        private long samples, clock;
        private readonly List<float> output = new();
        private readonly float[] tmp = new float[2048];
        public YmRig(int hostRate)
        {
            ym = new Ym2612(false, OpnClock);
            ym.PowerOn();
            ym.Sink = this;
            mixL = new BandLimitedMixer(OpnClock / Ym2612.ClocksPerSample, hostRate);
        }
        void IYm2612Sink.Sample(int left, int right)
        {
            float l = left * (1f / 32768f);
            samples++;
            if (l != lastL) { mixL.Step(samples, l - lastL); lastL = l; }
        }
        // the mixer keeps a short ring (a block's worth): it is emptied after every step
        private void Step(long to) { clock = to; ym.RunTo(clock); mixL.Advance(samples); int n; while ((n = Math.Min(tmp.Length, mixL.Available)) > 0) { mixL.Read(tmp.AsSpan(0, n)); output.AddRange(new ArraySegment<float>(tmp, 0, n)); } }
        public void AdvanceTo(long target)
        {
            while (clock + 8L * Ym2612.ClocksPerSample <= target) Step(clock + 8L * Ym2612.ClocksPerSample);
            if (clock < target) Step(target);
        }
        public void Write(long at, int port, byte v) { AdvanceTo(at); ym.Write(port, v, at); }
        public float[] Output(int frames)
        {
            var o = new float[frames];
            for (int i = 0; i < Math.Min(frames, output.Count); i++) o[i] = output[i];
            return o;
        }
    }

    private static long Ym(double seconds) => (long)(seconds * OpnClock);

    /// <summary>One note of a made-up driver on FM channel 1: a patch (algorithm 4, feedback 3), a key-on at 0.10 s, a pitch bend every 10 ms (fnum <paramref name="fnumA"/> and <paramref name="fnumB"/> in turn) from 0.15 s,
    /// a fade of the second carrier's total level every 20 ms, a key-off at 1.2 s. (YM clock time, port, value) in order.</summary>
    private static List<(long At, int Port, byte V)> FmScript(int fnumA, int fnumB)
    {
        var w = new List<(long, int, byte)>();
        var used = new Dictionary<long, int>();
        // a register write is the address, then the data: writes made at the same moment are spaced so that no address lands between another's address and data
        void Reg(double t, int reg, byte v)
        {
            long at = Ym(t); used.TryGetValue(at, out int k); used[at] = k + 1;
            at += k;                                         // (as the sampler writes: the address and the data at the same chip clock)
            w.Add((at, 0, (byte)reg)); w.Add((at, 1, v));
        }
        int[] off = [0, 8, 4, 12];
        int[] mul = [1, 2, 1, 1], tl = [0x20, 0x10, 0x28, 0x08];
        for (int n = 0; n < 4; n++)
        {
            Reg(0, 0x30 + off[n], (byte)mul[n]); Reg(0, 0x40 + off[n], (byte)tl[n]); Reg(0, 0x50 + off[n], 0x1F); Reg(0, 0x60 + off[n], 0x08); Reg(0, 0x70 + off[n], 0x02); Reg(0, 0x80 + off[n], 0x4A);
        }
        Reg(0, 0xB0, 0x1C); Reg(0, 0xB4, 0xC0);
        Reg(0, 0xA4, (byte)(4 << 3 | fnumA >> 8)); Reg(0, 0xA0, (byte)(fnumA & 0xFF));
        Reg(0.10, 0x28, 0xF0);
        int level = 0x08;
        for (int k = 0; k < 95; k++)
        {
            double t = 0.15 + 0.01 * k;
            int f = k % 2 == 0 ? fnumB : fnumA;
            Reg(t, 0xA4, (byte)(4 << 3 | f >> 8)); Reg(t + 0.0001, 0xA0, (byte)(f & 0xFF));
            if (k % 2 == 1 && level < 0x30) { level++; Reg(t + 0.0002, 0x4C, (byte)level); }
        }
        Reg(1.2, 0x28, 0x00);
        return w.OrderBy(x => x.Item1).ToList();
    }

    private static GenWriteLog LogOf(List<(long At, int Port, byte V)> script)
    {
        var log = new GenWriteLog();
        log.Begin(new byte[512], new int[3], [15, 15, 15, 15], 0, MasterHz);
        foreach (var (at, port, v) in script) log.OnYm(at * 7, port, v);
        return log;
    }

    private static SegaSnapshot OpnSnapshot(GenRecipe[]? recipes = null, byte[]? patch0 = null)
    {
        var snap = new SegaSnapshot(SegaSnapshot.KindOpn, 3_579_545, PsgVariant.Sega) { OpnClock = (int)OpnClock };
        if (patch0 != null) patch0.CopyTo(snap.OpnPatch[0], 0);
        if (recipes != null) snap.Recipes = recipes;
        return snap;
    }

    private static float[] RenderOpn(SegaSnapshot snap, Action<OpnSampler> start, double seconds, double? offAt = null, Action<OpnSampler>? off = null, int rate = 48000)
    {
        var s = new OpnSampler(snap, rate) { KeepGamePan = true };
        start(s);
        int frames = (int)(seconds * rate);
        var o = new float[frames];
        var block = new float[48 * 2];
        for (int done = 0; done < frames; done += 48)
        {
            int n = Math.Min(48, frames - done);
            if (offAt != null && done <= offAt * rate && done + n > offAt * rate) off?.Invoke(s);
            s.Render(block, n);
            for (int i = 0; i < n; i++) o[done + i] = block[i * 2];
        }
        return o;
    }

    // ===================================================================================================================== FM

    private static void MdRecipe(Checks c)
    {
        var script = FmScript(600, 632);
        var truth = new YmRig(48000);
        foreach (var (at, port, v) in script) truth.Write(at, port, v);
        truth.AdvanceTo(Ym(1.7));
        var game = truth.Output(48000 * 17 / 10);

        var log = LogOf(script);
        var recipes = GenRecipes.Derive(log, Ym(1.7) * 7, 32000 * 2, new bool[6], new bool[4], out var stats);
        c.Ok("the scripted driver's one note becomes one FM recipe", recipes.Length == 1 && recipes[0].IsFm, stats.ToString());
        if (recipes.Length != 1) return;
        var r = recipes[0];
        int[] off = [0, 8, 4, 12];
        bool patchOk = true;
        int[] mul = [1, 2, 1, 1], tl = [0x20, 0x10, 0x28, 0x08];
        for (int n = 0; n < 4; n++) patchOk &= r.Patch[n * 7] == mul[n] && r.Patch[n * 7 + 1] == tl[n] && r.Patch[n * 7 + 2] == 0x1F && r.Patch[n * 7 + 3] == 0x08 && r.Patch[n * 7 + 4] == 0x02 && r.Patch[n * 7 + 5] == 0x4A;
        c.Ok("the key-on settings are the driver's patch, algorithm 4 feedback 3, block 4 fnum 600, all four operators keyed", patchOk && r.Patch[28] == 0x1C && r.Patch[29] == 0xC0 && r.Block == 4 && r.Fnum == 600 && r.OpMask == 0xF, $"patch ok {patchOk}, B0 {r.Patch[28]:X2}, block {r.Block}, fnum {r.Fnum}");
        int pitch = r.Count(GenEv.Pitch), tlEv = r.Events.Count(e => e.Kind == GenEv.Param && e.Value >> 8 == 3 * 7 + 1);
        int bend = r.Events.Where(e => e.Kind == GenEv.Pitch).Select(e => Math.Abs(e.Value)).DefaultIfEmpty(0).Max();
        c.Ok("the macro holds the pitch bends (every 10 ms) and the level fade of the second carrier", pitch >= 90 && tlEv >= 40, $"{pitch} pitch changes, {tlEv} total-level changes over {r.Seconds:0.00} s");
        c.Ok("the bend is fnum 600 to 632: 90 cents", Math.Abs(bend - 90) <= 2, $"largest {bend} cents");
        c.Ok("the note's length is the driver's key time (1.1 s)", Math.Abs(r.Seconds - 1.1) < 0.02 && r.KeyedOff, $"{r.Seconds:0.000} s, keyed off {r.KeyedOff}");

        // fidelity: the recipe played back against the chip fed the driver's own writes (the game's note starts at 0.10 s)
        var snap = OpnSnapshot([r]);
        var played = RenderOpn(snap, s => s.NoteOnRecipe(0, r, 261.6256, 1.0), 1.4, 1.1, s => s.NoteOff(0));
        var truthNote = game[(int)(0.10 * 48000)..][..played.Length];
        double dRecipe = LabCommon.SpectralDistance(truthNote, played, 48000);
        c.Ok("the recipe played back is the driver's note (spectral distance under 0.2)", dRecipe < 0.2, $"distance {dRecipe:0.000}; truth rms {LabCommon.Rms(truthNote):0.0000}, replay rms {LabCommon.Rms(played):0.0000}");
        // the plain instrument (what Instrument Runaway played before): the patch at the pitch, no bend and no fade
        double chanHz = FmPatchBank.Hz(4, 600, OpnClock);
        var plainSnap = OpnSnapshot(null, r.Patch);
        var plain = RenderOpn(plainSnap, s => s.NoteOn(0, 0, chanHz * OpnSampler.CarrierFundamental(r.Patch), 1.0), 1.4, 1.1, s => s.NoteOff(0));
        double dPlain = LabCommon.SpectralDistance(truthNote, plain, 48000);
        c.Ok("the plain instrument is not: it has no bend and no fade", dPlain > 0.5 && dPlain > 4 * dRecipe, $"distance {dPlain:0.000}");
        // controls: a recipe that missed something is told apart
        var noPitch = new GenRecipe { Kind = r.Kind, Patch = r.Patch, Block = r.Block, Fnum = r.Fnum, OpMask = r.OpMask, Lfo = r.Lfo, Uses = 1, Events = r.Events.Where(e => e.Kind != GenEv.Pitch).ToArray() };
        var dNoPitch = LabCommon.SpectralDistance(truthNote, RenderOpn(OpnSnapshot([noPitch]), s => s.NoteOnRecipe(0, noPitch, 261.6256, 1.0), 1.4, 1.1, s => s.NoteOff(0)), 48000);
        c.Control("a recipe without the pitch bends is told apart from the driver's note", dNoPitch > 2 * dRecipe && dNoPitch > 0.2, $"distance {dNoPitch:0.000} against {dRecipe:0.000}");
        var noFade = new GenRecipe { Kind = r.Kind, Patch = r.Patch, Block = r.Block, Fnum = r.Fnum, OpMask = r.OpMask, Lfo = r.Lfo, Uses = 1, Events = r.Events.Where(e => e.Kind != GenEv.Param).ToArray() };
        var dNoFade = LabCommon.SpectralDistance(truthNote, RenderOpn(OpnSnapshot([noFade]), s => s.NoteOnRecipe(0, noFade, 261.6256, 1.0), 1.4, 1.1, s => s.NoteOff(0)), 48000);
        c.Control("a recipe without the level fade is told apart from the driver's note", dNoFade > 3 * dRecipe && dNoFade > 0.15, $"distance {dNoFade:0.000} against {dRecipe:0.000}");
        var cut = GenRecipes.Derive(log, Ym(1.7) * 7, 0, new bool[6], new bool[4], out _);
        c.Ok("a window of zero seconds learns nothing", cut.Length == 0);
        c.Ok("FL's pitch is added to the game's: a note a fifth up plays 3/2 of the frequency (and the bend rides on it)", FifthIsUp(snap, r), "");

        // ---- a corruption of the driver's bend depth: the old capture is blind to it, the new one is not ----
        var deep = FmScript(600, 700);
        var logDeep = LogOf(deep);
        var recipesDeep = GenRecipes.Derive(logDeep, Ym(1.7) * 7, 32000 * 2, new bool[6], new bool[4], out _);
        var rd = recipesDeep.Single();
        int bendDeep = rd.Events.Where(e => e.Kind == GenEv.Pitch).Select(e => Math.Abs(e.Value)).DefaultIfEmpty(0).Max();
        // the old capture at the end of either script: the channel's patch registers, which are the same in both (the bend is in the frequency, which the old snapshot did not keep for a patch)
        bool samePatch = r.Patch.SequenceEqual(rd.Patch);
        var oldA = RenderOpn(plainSnap, s => s.NoteOn(0, 0, chanHz * OpnSampler.CarrierFundamental(r.Patch), 1.0), 1.0);
        var oldB = RenderOpn(OpnSnapshot(null, rd.Patch), s => s.NoteOn(0, 0, chanHz * OpnSampler.CarrierFundamental(rd.Patch), 1.0), 1.0);
        c.Ok("the old capture cannot see a deeper bend: the plain instrument renders bit-identical", samePatch && LabCommon.Hash(oldA) == LabCommon.Hash(oldB), $"{LabCommon.Hash(oldA):X16} / {LabCommon.Hash(oldB):X16}");
        c.Ok("the new capture sees it: 266 cents against 90", Math.Abs(bendDeep - 266) <= 3, $"largest bend {bendDeep} cents");
        var playedDeep = RenderOpn(OpnSnapshot([rd]), s => s.NoteOnRecipe(0, rd, 261.6256, 1.0), 1.4, 1.1, s => s.NoteOff(0));
        c.Ok("and what Instrument Runaway plays from it differs from the clean driver's recipe", LabCommon.SpectralDistance(played, playedDeep, 48000) > 0.3, $"distance {LabCommon.SpectralDistance(played, playedDeep, 48000):0.000}");
        c.Control("the same recipe against itself is identical (the difference is the corruption's)", LabCommon.Hash(played) == LabCommon.Hash(RenderOpn(snap, s => s.NoteOnRecipe(0, r, 261.6256, 1.0), 1.4, 1.1, s => s.NoteOff(0))));

        // ---- no allocation while a recipe plays ----
        var sampler = new OpnSampler(snap, 48000);
        sampler.NoteOnRecipe(0, r, 261.6256, 1.0);
        var blk = new float[256 * 2];
        sampler.Render(blk, 256);
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 400; i++) { sampler.Update(0, 261.6256 * Math.Pow(2, i / 1200.0), 0.9); sampler.Render(blk, 128); }
        long grown = GC.GetAllocatedBytesForCurrentThread() - a0;
        c.Ok("the FM sampler allocates nothing while a recipe note plays (400 blocks)", grown == 0, $"{grown:N0} bytes");

        // ---- a channel that sounds when the listening ends, with no key-on in the window: a held voice ----
        var held = new List<(long, int, byte)>(script.Where(x => x.Item1 < Ym(0.09) || x.Item1 >= Ym(0.09)).Where(x => x.Item1 != Ym(0.10) && x.Item1 != Ym(0.10) + 24));   // the key-on is left out
        var heldLog = LogOf(held.Where(x => !(x.Item1 >= Ym(1.2))).ToList());
        var fmNow = new bool[6]; fmNow[0] = true;
        var heldRecipes = GenRecipes.Derive(heldLog, Ym(1.15) * 7, 32000, fmNow, new bool[4], out _);
        c.Ok("a channel still sounding with no key-on inside the window becomes a held-voice recipe", heldRecipes.Length == 1 && heldRecipes[0].Held && heldRecipes[0].IsFm && heldRecipes[0].Count(GenEv.Pitch) > 20, $"{heldRecipes.Length} recipes, {heldRecipes.FirstOrDefault()?.Count(GenEv.Pitch)} pitch changes");
    }

    private static bool FifthIsUp(SegaSnapshot snap, GenRecipe r)
    {
        // a note played at C5 and one a fifth above, no bend: the second's pitch is 3/2 of the first's (measured on the zero crossings of a sine recipe)
        var sine = new GenRecipe { Kind = GenRecipe.KindFm, Block = 4, Fnum = 600, OpMask = 0xF, Uses = 1 };
        // algorithm 7, every operator a carrier: operator 4 a sine at multiple 1, the others silent
        for (int n = 0; n < 4; n++) { sine.Patch[n * 7] = 1; sine.Patch[n * 7 + 1] = n == 3 ? (byte)0 : (byte)127; sine.Patch[n * 7 + 2] = 0x1F; sine.Patch[n * 7 + 5] = 0x0F; }
        sine.Patch[28] = 0x07; sine.Patch[29] = 0xC0;
        float[] At(double hz) => RenderOpn(OpnSnapshot([sine]), s => s.NoteOnRecipe(0, sine, hz, 1.0), 0.5);
        double f1 = LabCommon.DominantHz(At(261.6256)[4000..], 48000), f2 = LabCommon.DominantHz(At(261.6256 * 1.4983)[4000..], 48000);
        return f1 > 100 && Math.Abs(f2 / f1 - 1.4983) < 0.01;
    }

    // ===================================================================================================================== PSG

    private static void MdPsg(Checks c)
    {
        // a made-up driver on PSG tone 1: key-on = attenuation from 15 to 0 at 0.05 s, the period bends every 10 ms (254 and 240), the attenuation fades one step every 40 ms, off at 0.5 s
        const int PsgClock = 3_579_545, Rate = 48000;
        var writes = new List<(double T, byte V)>();
        void Tone(double t, int period) { writes.Add((t, (byte)(0x80 | period & 15))); writes.Add((t + 0.00001, (byte)(period >> 4 & 0x3F))); }
        void Att(double t, int att) => writes.Add((t, (byte)(0x90 | att)));
        Tone(0, 254); Att(0.00002, 15);
        Att(0.05, 0);
        for (int k = 0; k < 45; k++) { Tone(0.06 + 0.01 * k, k % 2 == 0 ? 240 : 254); if (k % 4 == 3) Att(0.06 + 0.01 * k + 0.0001, Math.Min(14, 1 + k / 4)); }
        Att(0.5, 15);
        writes = writes.OrderBy(x => x.T).ToList();

        // ground truth: the PSG chip fed the script, 1 ms at a time, mixed the way the sampler does
        var psg = new Sn76489(PsgClock, Rate, PsgVariant.Sega) { Gain = APU_SMS.PsgGain };
        psg.WriteStereo(0xFF);
        var truth = new List<float>();
        int next = 0; double carry = 0; var pcm = new short[1024];
        for (int ms = 0; ms < 700; ms++)
        {
            while (next < writes.Count && writes[next].T <= ms / 1000.0) psg.Write(writes[next++].V);
            carry += Rate / 1000.0 * PsgClock / Rate;
            int clocks = (int)carry; carry -= clocks;
            psg.Run(clocks);
            int got; while ((got = psg.ReadSamples(pcm)) > 0) for (int i = 0; i + 1 < got; i += 2) truth.Add(pcm[i] * (1f / 32768f));
        }

        var log = new GenWriteLog();
        log.Begin(new byte[512], new int[3], [15, 15, 15, 15], 0, MasterHz);
        foreach (var (t, v) in writes) log.OnPsg((long)(t * MasterHz), v);
        var recipes = GenRecipes.Derive(log, (long)(0.7 * MasterHz), 32000 * 2, new bool[6], new bool[4], out var stats);
        c.Ok("the scripted PSG note becomes one tone recipe", recipes.Length == 1 && recipes[0].Kind == GenRecipe.KindPsgTone, stats.ToString());
        if (recipes.Length != 1) return;
        var r = recipes[0];
        c.Ok("its settings are the driver's: period 254, attenuation 0; the macro has the bends and the fade", r.Period == 254 && r.Att == 0 && r.Count(GenEv.Pitch) >= 40 && r.Count(GenEv.Vol) >= 8, $"period {r.Period}, att {r.Att}, {r.Count(GenEv.Pitch)} pitch changes, {r.Count(GenEv.Vol)} volume changes, {r.Seconds:0.000} s");

        var snap = new SegaSnapshot(SegaSnapshot.KindPsg, PsgClock, PsgVariant.Sega);
        float[] Play(GenRecipe rec, int sampleRate = Rate)
        {
            var s = new PsgSampler(snap, sampleRate);
            s.NoteOnRecipe(0, rec, 261.6256, 1.0);
            var o = new List<float>(); var blk = new float[48 * 2];
            for (int ms = 0; ms < 650; ms++)
            {
                if (ms == 450) s.NoteOff(0);
                s.Render(blk, 48);
                for (int i = 0; i < 48; i++) o.Add(blk[i * 2]);
            }
            return o.ToArray();
        }
        var played = Play(r);
        var start = (int)(0.05 * Rate);
        var truthNote = truth.Skip(start).Take(played.Length).ToArray();
        double d = LabCommon.SpectralDistance(truthNote, played, Rate);
        c.Ok("the recipe played back is the driver's PSG note (spectral distance under 0.5)", d < 0.5, $"distance {d:0.000}; truth rms {LabCommon.Rms(truthNote):0.0000}, replay rms {LabCommon.Rms(played):0.0000}");
        var flat = new GenRecipe { Kind = r.Kind, Period = r.Period, Att = r.Att, Uses = 1 };
        double dFlat = LabCommon.SpectralDistance(truthNote, Play(flat), Rate);
        c.Control("a recipe with neither bend nor fade is told apart", dFlat > 2 * d && dFlat > 0.3, $"distance {dFlat:0.000} against {d:0.000}");
        var s2 = new PsgSampler(snap, Rate);
        s2.NoteOnRecipe(0, r, 261.6256, 1.0);
        var b2 = new float[256 * 2];
        s2.Render(b2, 256);
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 400; i++) { s2.Update(0, 261.6256 * Math.Pow(2, i / 1200.0), 0.9); s2.Render(b2, 128); }
        long grown = GC.GetAllocatedBytesForCurrentThread() - a0;
        c.Ok("the PSG sampler allocates nothing while a recipe note plays (400 blocks)", grown == 0, $"{grown:N0} bytes");
    }

    // ===================================================================================================================== the project section

    private static void MdPersist(Checks c)
    {
        var script = FmScript(600, 632);
        var recipes = GenRecipes.Derive(LogOf(script), Ym(1.7) * 7, 32000 * 2, new bool[6], new bool[4], out _);
        var bytes = GenRecipe.Write(recipes);
        var back = GenRecipe.Read(bytes);
        c.Ok("a recipe list written and read is the same", back != null && back.Length == recipes.Length && back.Zip(recipes).All(p => p.First.Hash() == p.Second.Hash()), $"{bytes.Length:N0} bytes for {recipes.Length} recipe(s) with {recipes.Sum(r => r.Events.Length)} events");
        c.Ok("a restored recipe plays bit-identically", back != null && LabCommon.Hash(RenderOpn(OpnSnapshot(recipes), s => s.NoteOnRecipe(0, recipes[0], 261.6256, 1.0), 0.8)) == LabCommon.Hash(RenderOpn(OpnSnapshot(back), s => s.NoteOnRecipe(0, back[0], 261.6256, 1.0), 0.8)));
        c.Ok("empty", GenRecipe.Read([]) == null);
        c.Ok("a different version", Tweak(bytes, 0, 99) == null);
        c.Ok("too many recipes claimed", Tweak(bytes, 1, 200) == null);
        c.Ok("cut short", GenRecipe.Read(bytes[..(bytes.Length / 2)]) == null);
        c.Ok("trailing bytes", GenRecipe.Read(bytes.Concat(new byte[] { 1, 2, 3 }).ToArray()) == null);
        c.Ok("longer than any real section", GenRecipe.Read(new byte[GenRecipe.MaxSection + 1]) == null);
        c.Ok("a kind that does not exist", Tweak(bytes, 2, 9) == null);
        var rnd = new SeededRandom(77);
        int parsed = 0, over = 0;
        for (int i = 0; i < 400; i++)
        {
            var fuzz = (byte[])bytes.Clone();
            for (int k = 0; k < 6; k++) fuzz[rnd.Next(fuzz.Length)] = (byte)rnd.Next(256);
            var got = GenRecipe.Read(fuzz);
            if (got != null) { parsed++; if (got.Length > GenRecipe.MaxRecipes || got.Any(r => r.Events.Length > GenRecipe.MaxEvents)) over++; }
        }
        c.Ok("400 randomly damaged lists never throw and never exceed the limits", over == 0, $"{parsed} of 400 still parsed (a changed value can be valid)");
        c.Control("a clean list still parses after the same fuzz harness", GenRecipe.Read(bytes) != null);
    }

    private static GenRecipe[]? Tweak(byte[] b, int index, byte value) { var t = (byte[])b.Clone(); t[index] = value; return GenRecipe.Read(t); }

    // ===================================================================================================================== a real game

    private static void MdSonic(Checks c, string path, string name, string input, int setup)
    {
        if (!File.Exists(path)) { c.Skip(name, $"no ROM at {path}"); return; }
        var rom = LabCommon.ReadRom(path, out _, out _);
        var game = new LabMd.Game(name, rom, Path.GetFileName(path), input, setup);
        var state = LabMd.Setup(game);

        using var clean = new LabMd.Rig(game, state);
        var cleanOnce = clean.Capture();
        var cleanPcm = LabCommon.RunPcm(clean.Session, 600);
        var cleanLate = clean.Capture(10);
        int fm = cleanLate.Recipes.Count(r => r.IsFm), psg = cleanLate.Recipes.Count(r => !r.IsFm);
        c.Ok($"{name}: ten seconds of the game give FM and PSG recipes", fm >= 3 && psg >= 1, $"{cleanLate.Recipes.Length} recipes ({fm} FM, {psg} PSG); {cleanLate.ListenNote}");
        c.Ok($"{name}: every recipe is one the chips can play (no silent or empty ones)", cleanLate.Recipes.All(r => !r.Silent));

        // a second clean run learns the same recipes (the lab is deterministic: the difference below is the corruption's)
        using var again = new LabMd.Rig(game, state);
        LabCommon.RunPcm(again.Session, 600);
        var againLate = again.Capture(10);
        c.Control($"{name}: a second clean run learns exactly the same recipes", againLate.Recipes.Length == cleanLate.Recipes.Length && againLate.Recipes.Zip(cleanLate.Recipes).All(p => p.First.Hash() == p.Second.Hash()));

        // corruptions of the sound driver's RAM: what each capture sees
        int detected = 0, audible = 0, blind = 0;
        var notes = new List<string>();
        for (int seed = 1; seed <= 8; seed++)
        {
            using var bad = new LabMd.Rig(game, state);
            var d = MachineDomains.Find(MachineDomains.For(bad.Session), "ZRAM")!;
            var rnd = new SeededRandom(500 + seed);
            var pool = Enumerable.Range(0, d.Size).Where(i => d.Peek(i) != 0).ToArray();
            var writes = Enumerable.Range(0, 120).Select(_ => new CorruptionWrite("ZRAM", pool[rnd.Next(pool.Length)], (byte)rnd.Next(256))).ToList();
            CorruptionApplier.Apply(MachineDomains.For(bad.Session), writes);
            var badOnce = bad.Capture();
            var pcm = LabCommon.RunPcm(bad.Session, 600);
            var badLate = bad.Capture(10);
            double dist = LabCommon.SpectralDistance(LabCommon.ToMono(cleanPcm), LabCommon.ToMono(pcm), clean.Session.SampleRate);
            bool once = LabMd.Compare(cleanOnce, badOnce).Differ == 0;
            bool recipesDiffer = badLate.Recipes.Length != cleanLate.Recipes.Length || badLate.Recipes.Zip(cleanLate.Recipes).Any(p => p.First.Hash() != p.Second.Hash());
            if (once) blind++;
            if (dist >= 0.3) { audible++; if (recipesDiffer) detected++; }
            notes.Add($"seed {seed}: game {dist:0.00}{(once ? ", blind at once" : "")}{(recipesDiffer ? ", recipes differ" : "")}");
        }
        c.Ok($"{name}: frozen at once the old capture sees none of the 8 driver corruptions", blind == 8, $"{blind}/8 identical");
        c.Ok($"{name}: where the corrupted driver changes the game's sound, the recipes differ from the clean game's", audible >= 1 && detected == audible, $"{detected} of {audible} audible corruptions; " + string.Join("; ", notes));
    }
}
