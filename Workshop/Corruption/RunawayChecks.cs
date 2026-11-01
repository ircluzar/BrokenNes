using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BrokenNes2;
using NesEmulator.Corruption;
using NesEmulator.Snes;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Corruption;

/// <summary>
/// --runaway-test [--only a,b] [--smw rom] [--sonic rom] [--sor2 rom]: the checks of Instrument Runaway's listening (round 6): the recipes learned from a game's sound driver. Each check that can be fooled has a control: the same
/// check on a sabotaged capture, which it must catch.
/// </summary>
internal static partial class RunawayChecks
{
    public static int Run(string[] args)
    {
        var c = new Checks();
        string only = args.SkipWhile(a => a != "--only").Skip(1).FirstOrDefault() ?? "";
        bool Want(string g) => only.Length == 0 || only.Split(',').Contains(g);
        string Path(string name, string env, string fallback) => args.SkipWhile(a => a != "--" + name).Skip(1).FirstOrDefault() ?? Environment.GetEnvironmentVariable(env) ?? fallback;
        string sonic = Path("sonic", "BROKENNES_TEST_SONIC", @"X:\EMULATION\Sega Genesis\Sonic the Hedgehog (JUE) [!].bin");
        string smw = Path("smw", "BROKENNES_TEST_SMW", @"X:\EMULATION\Super Nintendo\Super Nintendo (U)\Super Mario World (U) [!].smc");
        // --write-vibrato-rom <file>: write the synthetic game with the bending sound driver to a file (for the plugin's editor screenshots), then stop
        int wi = Array.IndexOf(args, "--write-vibrato-rom");
        if (wi >= 0 && wi + 1 < args.Length) { File.WriteAllBytes(args[wi + 1], SynthSnesRom.Build(vibrato: true)); Console.WriteLine($"written: {args[wi + 1]}"); return 0; }

        if (Want("snes-recipe")) { Section("SNES: a sound driver that bends its voice, learned and played back"); Guard(c, "snes-recipe", () => SnesRecipe(c)); }
        if (Want("persist")) { Section("recipes in a project: written, read, and the hostile cases"); Guard(c, "persist", () => Persist(c)); }
        if (Want("md-recipe")) { Section("Genesis: FM notes of a made-up driver, learned and played back against the chip they were written to"); Guard(c, "md-recipe", () => MdRecipe(c)); }
        if (Want("md-psg")) { Section("Genesis: PSG notes of a made-up driver"); Guard(c, "md-psg", () => MdPsg(c)); }
        if (Want("md-persist")) { Section("Genesis recipes in a project: written, read, and the hostile cases"); Guard(c, "md-persist", () => MdPersist(c)); }
        if (Want("md-sonic")) { Section("Genesis: Sonic the Hedgehog, when its ROM is present"); Guard(c, "md-sonic", () => MdSonic(c, sonic, "Sonic 1", "480:Start,490:", 1200)); }
        if (Want("snes-smw")) { Section("SNES: Super Mario World, when its ROM is present"); Guard(c, "snes-smw", () => SnesReal(c, smw)); }
        return c.Finish();
    }

    private static void Section(string name) { Console.WriteLine(); Console.WriteLine("== " + name); }

    private static void Guard(Checks c, string group, Action run)
    {
        try { run(); }
        catch (Exception e) { c.Ok($"group '{group}' ran to the end", false, e.GetType().Name + ": " + e.ToString().Split('\n').Take(4).Aggregate((a, b) => a + " / " + b)); }
    }

    // ===================================================================================================================== helpers

    /// <summary>A SNES game with its SPC700's DSP writes logged from the first one.</summary>
    internal sealed class LoggedSnes : IDisposable
    {
        public readonly SnesRun Run;
        public readonly SpcWriteLog Log = new();
        public readonly List<short> Pcm = new();
        public LoggedSnes(byte[] rom) { Run = new SnesRun(rom); Log.Attach(Run.Spc); }
        public APU_SFC Spc => Run.Spc;
        public void Play(int frames, Func<int, PadButtons>? pad = null)
        {
            var buf = new short[32768];
            for (int i = 0; i < frames; i++)
            {
                Run.Session.SetPad(0, pad?.Invoke(Run.Frames) ?? PadButtons.None);
                Run.Session.RunFrame();
                Run.Frames++;
                int n;
                while ((n = Run.Session.ReadSamples(buf)) > 0) Pcm.AddRange(new ArraySegment<short>(buf, 0, n));
            }
        }
        public SpcSnapshot Capture(double seconds = SpcRecipes.ListenWindowSeconds)
        {
            var snap = new SpcSnapshot(Spc.Aram, Spc.Dsp.Regs);
            snap.Recipes = SpcRecipes.Derive(Log, Spc.SamplesProduced, (long)(seconds * 32000), Spc.Dsp.Regs, out _);
            return snap;
        }
        public void Dispose() => Run.Dispose();
    }

    /// <summary>The recipe sampler's output for one note: the recipe played at its own pitch (the root key) at the level that gives the game's volumes back, <paramref name="frames"/> frames at 32 kHz (no resampling), left channel.</summary>
    internal static float[] RenderRecipe(SpcSnapshot snap, SpcRecipe r, int frames, bool release = false)
    {
        var s = new SnesSampler(snap, 32000);
        s.NoteOnRecipe(0, r, SnesSampler.PitchFor(0, r.Pitch), r.PeakVolume);
        var outp = new float[frames];
        var block = new float[128 * 2];
        for (int done = 0; done < frames; done += 128)
        {
            int n = Math.Min(128, frames - done);
            s.Render(block, n);
            for (int i = 0; i < n; i++) outp[done + i] = block[i * 2];
        }
        return outp;
    }

    /// <summary>The same list of writes with the ones <paramref name="drop"/> names left out: a capture that misses something.</summary>
    internal static SpcWriteLog Without(SpcWriteLog log, Func<byte, bool> drop)
    {
        var copy = new SpcWriteLog();
        copy.Begin(log.BaseRegs);
        for (long i = log.Dropped; i < log.Count; i++)
        {
            log.Get(i, out uint t, out byte reg, out byte val);
            if (!drop(reg)) copy.OnWrite(reg, val, t);
        }
        return copy;
    }

    private static int FindAram(byte[] aram, params byte[] pattern)
    {
        for (int i = 0; i + pattern.Length <= aram.Length; i++)
        {
            bool ok = true;
            for (int k = 0; k < pattern.Length && ok; k++) ok = aram[i + k] == pattern[k];
            if (ok) return i;
        }
        return -1;
    }

    private static long KeyOnAt(SpcWriteLog log)
    {
        for (long i = log.Dropped; i < log.Count; i++) { log.Get(i, out uint t, out byte reg, out byte val); if (reg == 0x4C && (val & 1) != 0) return t; }
        return -1;
    }

    // ===================================================================================================================== SNES: the vibrato driver

    private static void SnesRecipe(Checks c)
    {
        var rom = SynthSnesRom.Build(vibrato: true);
        using var clean = new LoggedSnes(rom);
        clean.Play(150);                                   // 2.5 s: the loader, the upload, then the driver's note
        long kon = KeyOnAt(clean.Log);
        c.Ok("the vibrato game keys a voice on", kon > 0, $"key-on at DSP sample {kon}, {clean.Log.Count:N0} chip writes logged");

        var snap = clean.Capture();
        var recipes = snap.Recipes;
        c.Ok("one recipe, from the one voice the driver plays", recipes.Length == 1, $"{recipes.Length} recipes");
        if (recipes.Length != 1) return;
        var r = recipes[0];
        int pitchEvents = r.Count(SpcEv.Pitch), volEvents = r.Count(SpcEv.VolL) + r.Count(SpcEv.VolR);
        c.Ok("the key-on settings are the driver's (sample 0, envelope $FFE0, pitch $1000, volume $50)", r.Srcn == 0 && r.Adsr1 == 0xFF && r.Adsr2 == 0xE0 && r.Pitch == 0x1000 && r.VolL == 0x50 && r.VolR == 0x50, $"srcn {r.Srcn}, ADSR {r.Adsr1:X2}{r.Adsr2:X2}, pitch ${r.Pitch:X4}, vol {r.VolL}/{r.VolR}");
        c.Ok("the macro holds the pitch bends (about every 9 ms) and the volume fade", pitchEvents >= 150 && volEvents >= 100, $"{pitchEvents} pitch events, {volEvents} volume events over {r.Seconds:0.00} s");
        int maxCents = r.Events.Where(e => e.Kind == SpcEv.Pitch).Select(e => e.Value).DefaultIfEmpty(0).Max();
        c.Ok("the bend is $1000 to $1100: 105 cents", Math.Abs(maxCents - 105) <= 2, $"largest bend {maxCents} cents");

        // fidelity: the game's own note against the recipe played back at the same pitch and level
        int from = (int)kon, frames = 32000;               // one second of the note (stereo frames in the PCM = DSP samples)
        var game = new float[frames];
        for (int i = 0; i < frames; i++) game[i] = (clean.Pcm[(from + i) * 2] + clean.Pcm[(from + i) * 2 + 1]) * (0.5f / 32768f);
        var played = RenderRecipe(snap, r, frames);
        double dRecipe = LabCommon.SpectralDistance(game, played, 32000);
        c.Ok("the recipe played back is the game's note (spectral distance under 0.15)", dRecipe < 0.15, $"distance {dRecipe:0.000}; game rms {LabCommon.Rms(game):0.0000}, recipe rms {LabCommon.Rms(played):0.0000}");

        // the plain instrument (what Instrument Runaway played before) against the game's note
        var plain = RenderPlain(snap, 0, frames, r.VolL);
        double dPlain = LabCommon.SpectralDistance(game, plain, 32000);
        c.Ok("the plain instrument is not: it has no bend and no fade", dPlain > 0.5, $"distance {dPlain:0.000}");

        // controls: captures that miss something must be told apart from the game
        var noPitch = SnapshotFrom(clean, Without(clean.Log, reg => (reg & 15) is 2 or 3 && reg < 0x80));
        var rNoPitch = noPitch.Recipes.FirstOrDefault();
        double dNoPitch = rNoPitch == null ? 99 : LabCommon.SpectralDistance(game, RenderRecipe(noPitch, rNoPitch, frames), 32000);
        c.Control("a capture that missed the pitch writes is told apart from the game", dNoPitch > 0.5 && dNoPitch > 4 * dRecipe, $"distance {dNoPitch:0.000} against {dRecipe:0.000}");
        var stripped = new SpcRecipe { Srcn = r.Srcn, Adsr1 = r.Adsr1, Adsr2 = r.Adsr2, Gain = r.Gain, VolL = r.VolL, VolR = r.VolR, Pitch = r.Pitch, Uses = 1, LengthSamples = r.LengthSamples, Events = r.Events.Where(e => e.Kind != SpcEv.VolL && e.Kind != SpcEv.VolR).ToArray() };
        double dFlat = LabCommon.SpectralDistance(game, RenderRecipe(snap, stripped, frames), 32000);
        c.Control("a recipe without the volume fade is told apart from the game", dFlat > 3 * dRecipe && dFlat > 0.2, $"distance {dFlat:0.000} against {dRecipe:0.000}");

        // the sampler allocates nothing while it plays (a plain note, and a recipe note with its macro)
        foreach (bool recipe in new[] { false, true })
        {
            var sp = new SnesSampler(snap, 48000);
            var blk = new float[256 * 2];
            if (recipe) sp.NoteOnRecipe(0, r, SnesSampler.PitchFor(0, r.Pitch), 80); else sp.NoteOn(0, 0, SnesSampler.PitchFor(0), 80, 80);
            sp.Render(blk, 256);
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 400; i++)
            {
                if (recipe) sp.Update(0, SnesSampler.PitchFor(i, r.Pitch), 80, 80); else sp.Update(0, SnesSampler.PitchFor(i), 80, 80);
                sp.Render(blk, 128);
            }
            long grown = GC.GetAllocatedBytesForCurrentThread() - a0;
            c.Ok($"the sampler allocates nothing while a {(recipe ? "recipe" : "plain")} note plays (400 blocks)", grown == 0, $"{grown:N0} bytes");
        }

        // listening for a moment too short sees nothing; the old capture sees none of it either way
        var none = clean.Capture(0);
        c.Ok("a window of zero seconds learns nothing (no hidden fallback to the whole log)", none.Recipes.Length == 0, $"{none.Recipes.Length} recipes");

        // ---- the corruption the old capture misses: the driver's vibrato depth ($11 -> $1F in its code) ----
        int at = FindAram(clean.Spc.Aram, 0x8F, 0x03, 0xF2, 0x8F, 0x11, 0xF3);
        c.Ok("found the vibrato depth in the driver's code", at > 0, $"at ${at + 4:X4}");
        using var bad = new LoggedSnes(rom);
        bad.Play(45);                                       // the driver is running; now the corruption lands, as a corruption on a saved machine does
        bad.Spc.Aram[at + 4] = 0x1F;
        bad.Play(105);
        var badSnap = bad.Capture();
        // the old capture, at once and later: the same static instruments, bit for bit
        var cleanOld = OldRenderHash(clean.Spc, 150);
        var badOld = OldRenderHash(bad.Spc, 150);
        c.Ok("the old capture cannot see it: the plain instrument renders bit-identical, clean and corrupted", cleanOld == badOld, $"{cleanOld:X16} / {badOld:X16}");
        var rBad = badSnap.Recipes.FirstOrDefault();
        int badCents = rBad == null ? 0 : rBad.Events.Where(e => e.Kind == SpcEv.Pitch).Select(e => e.Value).DefaultIfEmpty(0).Max();
        c.Ok("the new capture sees it: the corrupted bend is about 1,147 cents (clean 105)", rBad != null && Math.Abs(badCents - 1147) <= 5, $"largest bend {badCents} cents");
        int whole = rBad == null ? frames : Math.Min(r.LengthSamples, rBad.LengthSamples);        // the corruption lands 0.7 s into a note that has faded out by 1.5 s: compare the whole note, not its clean first part
        double dCorrupt = rBad == null ? 0 : LabCommon.SpectralDistance(RenderRecipe(snap, r, whole), RenderRecipe(badSnap, rBad, whole), 32000);
        var wa = RenderRecipe(snap, r, whole); var wb = rBad == null ? wa : RenderRecipe(badSnap, rBad, whole);
        int firstDiff = Enumerable.Range(0, wa.Length).FirstOrDefault(i => wa[i] != wb[i], -1);
        c.Ok("and what Instrument Runaway plays from it differs from the clean game's recipe", dCorrupt > 0.5, $"distance {dCorrupt:0.000} over {whole:N0} samples; first differing sample {firstDiff}; rms {LabCommon.Rms(wa):0.0000} / {LabCommon.Rms(wb):0.0000}; bad events at {string.Join(",", rBad?.Events.Where(e => e.Kind == SpcEv.Pitch && e.Value > 500).Take(3).Select(e => e.T) ?? [])}; clean has {r.Events.Count(e => e.Kind == SpcEv.Pitch && e.Value > 500)} such events, hashes {r.Hash():X} / {rBad?.Hash():X}, same object {ReferenceEquals(r, rBad)}, snaps same {ReferenceEquals(snap, badSnap)}");
        c.Control("the clean game's recipe against itself is identical (the difference is the corruption's)", LabCommon.Hash(RenderRecipe(snap, r, frames)) == LabCommon.Hash(RenderRecipe(snap, r, frames)));

        // ---- a dead driver: the voice it keyed keeps sounding and nothing touches the chip any more (a corrupted game stuck on one noise) ----
        using var stuck = new LoggedSnes(rom);
        stuck.Play(30);
        int head = FindAram(stuck.Spc.Aram, 0x8F, 0x03, 0xF2, 0x8F, 0x10, 0xF3);
        stuck.Spc.Aram[head] = 0x2F; stuck.Spc.Aram[head + 1] = 0xFE;           // BRA to itself: the driver stops where it is
        stuck.Play(40);
        var stuckSnap = stuck.Capture(1.0);
        var held = stuckSnap.Recipes.FirstOrDefault();
        c.Ok("a voice that sounds when the listening ends, with no key-on inside it, becomes a held-voice recipe", stuckSnap.Recipes.Length == 1 && held is { Held: true, Srcn: 0 }, $"{stuckSnap.Recipes.Length} recipes; held {held?.Held}");
        c.Ok("and its settings are the ones the voice has", held != null && held.Pitch is >= 0x1000 and <= 0x1100 && held.VolL is > 0 and <= 0x50, $"pitch ${held?.Pitch:X4}, volume {held?.VolL}");
        var shortWindow = stuck.Capture(0);
        c.Ok("with no window there is nothing to hold", shortWindow.Recipes.Length == 0);
        var back = SpcRecipe.Read(SpcRecipe.Write(stuckSnap.Recipes));
        c.Ok("a held-voice recipe survives the project section", back is { Length: 1 } && back[0].Held && back[0].Hash() == held!.Hash());
    }

    /// <summary>A snapshot of a game's memory with recipes derived from a different log.</summary>
    private static SpcSnapshot SnapshotFrom(LoggedSnes g, SpcWriteLog log)
    {
        var snap = new SpcSnapshot(g.Spc.Aram, g.Spc.Dsp.Regs);
        snap.Recipes = SpcRecipes.Derive(log, g.Spc.SamplesProduced, (long)(SpcRecipes.ListenWindowSeconds * 32000), g.Spc.Dsp.Regs, out _);
        return snap;
    }

    /// <summary>What the plain (pre-recipe) path plays: the sample of a slot at the root pitch, held, at 32 kHz.</summary>
    private static float[] RenderPlain(SpcSnapshot snap, int slot, int frames, int level)
    {
        var s = new SnesSampler(snap, 32000);
        s.NoteOn(0, slot, SnesSampler.PitchFor(0), level, level);
        var outp = new float[frames];
        var block = new float[128 * 2];
        for (int done = 0; done < frames; done += 128)
        {
            int n = Math.Min(128, frames - done);
            s.Render(block, n);
            for (int i = 0; i < n; i++) outp[done + i] = block[i * 2];
        }
        return outp;
    }

    /// <summary>A hash of the plain instruments' renders of a game's current memory (the pre-recipe behaviour).</summary>
    private static ulong OldRenderHash(APU_SFC spc, int level)
    {
        var snap = new SpcSnapshot(spc.Aram, spc.Dsp.Regs);
        ulong h = 1469598103934665603UL;
        foreach (var inst in snap.Instruments().Take(8)) h = (h ^ LabCommon.Hash(RenderPlain(snap, inst.Slot, 8000, Math.Min(level, 127)))) * 1099511628211UL;
        return h;
    }

    // ===================================================================================================================== project section

    private static void Persist(Checks c)
    {
        var rom = SynthSnesRom.Build(vibrato: true);
        using var g = new LoggedSnes(rom);
        g.Play(150);
        var snap = g.Capture();
        var bytes = SpcRecipe.Write(snap.Recipes);
        var back = SpcRecipe.Read(bytes);
        c.Ok("a recipe list written and read is the same", back != null && back.Length == snap.Recipes.Length && back.Zip(snap.Recipes).All(p => p.First.Hash() == p.Second.Hash()), $"{bytes.Length:N0} bytes for {snap.Recipes.Length} recipe(s) with {snap.Recipes.Sum(r => r.Events.Length)} events");
        c.Ok("the project section is far smaller than the sound memory next to it", bytes.Length < 20_000, $"{bytes.Length:N0} bytes");

        // the rendering of a restored recipe is the same as the original's
        if (back != null && back.Length > 0)
        {
            var restored = new SpcSnapshot(snap.Aram, snap.Dsp) { Recipes = back };
            c.Ok("a restored recipe plays bit-identically", LabCommon.Hash(RenderRecipe(snap, snap.Recipes[0], 16000)) == LabCommon.Hash(RenderRecipe(restored, back[0], 16000)));
        }

        // hostile input: each is refused (null), never trusted
        c.Ok("empty", SpcRecipe.Read([]) == null);
        c.Ok("a different version", Tweaked(bytes, 0, 99) == null);
        c.Ok("too many recipes claimed", Tweaked(bytes, 1, 200) == null);
        c.Ok("cut short", SpcRecipe.Read(bytes[..(bytes.Length / 2)]) == null);
        var trailing = bytes.Concat(new byte[] { 1, 2, 3 }).ToArray();
        c.Ok("trailing bytes", SpcRecipe.Read(trailing) == null);
        var big = new byte[SpcRecipe.MaxSection + 1];
        c.Ok("longer than any real section", SpcRecipe.Read(big) == null);
        // an event kind out of range, a pitch of zero, a huge event count
        var bad = (byte[])bytes.Clone();
        int pitchAt = 2 + 6;                          // after version, count, srcn, adsr1, adsr2, gain, volL, volR
        bad[pitchAt] = 0; bad[pitchAt + 1] = 0;
        c.Ok("a key-on pitch of zero", SpcRecipe.Read(bad) == null);
        var rnd = new SeededRandom(99);
        int accepted = 0, tried = 400;
        for (int i = 0; i < tried; i++)
        {
            var fuzz = (byte[])bytes.Clone();
            for (int k = 0; k < 6; k++) fuzz[rnd.Next(fuzz.Length)] = (byte)rnd.Next(256);
            var got = SpcRecipe.Read(fuzz);                // may be valid (a changed value), but never throws and never exceeds the limits
            if (got != null && (got.Length > SpcRecipe.MaxRecipes || got.Any(r => r.Events.Length > SpcRecipe.MaxEvents))) accepted += 1000;
            if (got != null) accepted++;
        }
        c.Ok("400 randomly damaged lists never throw and never exceed the limits", accepted < 1000, $"{accepted} of {tried} still parsed (a changed value can be valid)");
        c.Control("a clean list still parses after the same fuzz harness (the harness can accept)", SpcRecipe.Read(bytes) != null);
    }

    private static SpcRecipe[]? Tweaked(byte[] b, int index, byte value) { var t = (byte[])b.Clone(); t[index] = value; return SpcRecipe.Read(t); }

    // ===================================================================================================================== SNES: a real game

    private static void SnesReal(Checks c, string smwPath)
    {
        if (!File.Exists(smwPath)) { c.Skip("Super Mario World", $"no ROM at {smwPath}"); return; }
        var rom = LabCommon.ReadRom(smwPath, out _, out _);
        var game = new LabSnes.Game("SMW", rom, "", 900);
        var state = LabSnes.Setup(game);

        // clean: ten seconds of the title music
        using var clean = LabSnes.Fresh(game, state);
        var logClean = new SpcWriteLog();
        logClean.Attach(clean.Spc);
        LabCommon.RunPcm(clean.Session, 600);
        var snapClean = new SpcSnapshot(clean.Spc.Aram, clean.Spc.Dsp.Regs);
        snapClean.Recipes = SpcRecipes.Derive(logClean, clean.Spc.SamplesProduced, 10 * 32000L, clean.Spc.Dsp.Regs, out var statsClean);
        c.Ok("a clean game's ten seconds give recipes", snapClean.Recipes.Length >= 3, statsClean.ToString());
        c.Ok("every key-on of the window became a note (none lost to settings the window did not show)", statsClean.Notes == statsClean.KeyOns, $"{statsClean.Notes} notes from {statsClean.KeyOns} key-ons");
        c.Ok("the recipes name samples the game has", snapClean.Recipes.All(r => snapClean.HasSample(r.Srcn)), string.Join(",", snapClean.Recipes.Select(r => "#" + r.Srcn)));

        // the driver's instrument table: the most played recipe's four settings sit side by side in audio RAM; corrupt its sustain byte on the saved machine
        var r0 = snapClean.Recipes.Where(r => (r.Adsr1 & 0x80) != 0).OrderByDescending(r => r.Uses).First();
        using var at0 = LabSnes.Fresh(game, state);
        int p = FindAram(at0.Spc.Aram, (byte)r0.Srcn, (byte)r0.Adsr1, (byte)r0.Adsr2, (byte)r0.Gain);
        c.Ok("the most played instrument's settings are in the driver's table", p > 0, $"sample #{r0.Srcn}, ADSR {r0.Adsr1:X2}{r0.Adsr2:X2} played {r0.Uses}x, at ${p:X4}");
        if (p <= 0) return;
        byte corrupted = (byte)(r0.Adsr2 ^ 0xFF);
        var oldAtOnceClean = OldHash(new SpcSnapshot(at0.Spc.Aram, at0.Spc.Dsp.Regs));
        at0.Spc.Aram[p + 2] = corrupted;                                                // a corruption of the driver's table (the sort of write a blast makes)
        var oldAtOnceBad = OldHash(new SpcSnapshot(at0.Spc.Aram, at0.Spc.Dsp.Regs));
        c.Ok("frozen at once, the old capture sees nothing of it (the plain instruments render bit-identical)", oldAtOnceClean == oldAtOnceBad, $"{oldAtOnceClean:X16} / {oldAtOnceBad:X16}");

        var logBad = new SpcWriteLog();
        logBad.Attach(at0.Spc);
        LabCommon.RunPcm(at0.Session, 600);                                             // the corrupted game plays for ten seconds
        var snapBad = new SpcSnapshot(at0.Spc.Aram, at0.Spc.Dsp.Regs);
        snapBad.Recipes = SpcRecipes.Derive(logBad, at0.Spc.SamplesProduced, 10 * 32000L, at0.Spc.Dsp.Regs, out var statsBad);
        var seen = snapBad.Recipes.FirstOrDefault(r => r.Srcn == r0.Srcn && r.Adsr1 == r0.Adsr1 && r.Adsr2 == corrupted);
        c.Ok("after ten seconds the new capture has the corrupted setting as a recipe", seen != null, $"{statsBad}");
        c.Ok("and the clean game has no such recipe", !snapClean.Recipes.Any(r => r.Srcn == r0.Srcn && r.Adsr1 == r0.Adsr1 && r.Adsr2 == corrupted));
        if (seen != null)
        {
            double d = LabCommon.SpectralDistance(RenderRecipe(snapClean, r0, 48000), RenderRecipe(snapBad, seen, 48000), 32000);
            c.Ok("and what it plays differs from the clean game's recipe of that instrument", d > 0.3, $"distance {d:0.000}");
        }
        c.Control("the clean game's own recipes are all found again in a second clean run (the comparison can come out equal)", SecondCleanRunMatches(game, state, snapClean.Recipes));

        // a static corruption (the instrument's own sample) is still carried, by the plain instrument and by its recipe
        using var sample = LabSnes.Fresh(game, state);
        var cs = new SpcSnapshot(sample.Spc.Aram, sample.Spc.Dsp.Regs);
        var info = cs.Instruments().First(i => i.Slot == r0.Srcn);
        var rnd = new SeededRandom(5);
        for (int i = 0; i < 40; i++) sample.Spc.Aram[info.Start + rnd.Next(info.Blocks * 9)] = (byte)rnd.Next(256);
        var ss = new SpcSnapshot(sample.Spc.Aram, sample.Spc.Dsp.Regs) { Recipes = snapClean.Recipes };
        double dPlain = LabCommon.SpectralDistance(LabSnes.RenderSlot(cs, r0.Srcn), LabSnes.RenderSlot(ss, r0.Srcn), 48000);
        double dRecipe = LabCommon.SpectralDistance(RenderRecipe(snapClean, r0, 48000), RenderRecipe(ss, r0, 48000), 32000);
        c.Ok("a corrupted sample is still carried by the plain instrument", dPlain > 0.5, $"distance {dPlain:0.00}");
        c.Ok("and by the recipe that plays it", dRecipe > 0.5, $"distance {dRecipe:0.00}");
    }

    private static ulong OldHash(SpcSnapshot snap)
    {
        ulong h = 1469598103934665603UL;
        foreach (var inst in snap.Instruments().Take(12)) h = (h ^ LabCommon.Hash(LabSnes.RenderSlot(snap, inst.Slot))) * 1099511628211UL;
        return h;
    }

    private static bool SecondCleanRunMatches(LabSnes.Game game, byte[] state, SpcRecipe[] first)
    {
        using var again = LabSnes.Fresh(game, state);
        var log = new SpcWriteLog();
        log.Attach(again.Spc);
        LabCommon.RunPcm(again.Session, 600);
        var recipes = SpcRecipes.Derive(log, again.Spc.SamplesProduced, 10 * 32000L, again.Spc.Dsp.Regs, out _);
        return recipes.Length == first.Length && recipes.Zip(first).All(p => p.First.Hash() == p.Second.Hash());
    }
}
