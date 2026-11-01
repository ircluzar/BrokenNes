// Round 6 (presets): the game voices a SNES preset carries in the reserved note-behaviour block (Presets/PresetNote.cs). What `runaway` learns from a game's sound driver (recipes) rides in the preset for the instrument's own sample, so that the preset played
// with no ROM can sound the way the game played it. These checks use recipes made by hand (a real driver's are only in a live game): five for one sample, of which two play on a preset alone and three must be left out.
using BrokenNes2;
using BrokenNes2.Instruments;
using BrokenNes2.Presets;

namespace BrokenNes.FruityHost;

internal static partial class PresetChecks
{
    /// <summary>Recipes for sample <paramref name="slot"/>: [0] a note with envelope, pitch and volume changes, [1] a recipe of another sample, [2] one that switches the voice to another sample, [3] a silent one, [4] a loud one with echo and a pitch bend.
    /// Only [0] and [4] can play on a preset of the sample alone.</summary>
    internal static SpcRecipe[] HandRecipes(int slot)
    {
        int other = (slot + 1) & 0xFF;
        return
        [
            new SpcRecipe { Srcn = slot, Adsr1 = 0xFA, Adsr2 = 0xE4, Gain = 0, VolL = 100, VolR = 70, Pitch = 0x1000, Uses = 9, LengthSamples = 24000, KeyedOff = true,
                Events = [new(2400, SpcEv.Pitch, 60), new(4800, SpcEv.VolL, 45), new(7200, SpcEv.VolR, 20), new(9600, SpcEv.Pitch, -40), new(12000, SpcEv.Adsr2, 0xC0)] },
            new SpcRecipe { Srcn = other, Adsr1 = 0xFA, Adsr2 = 0xE4, VolL = 90, VolR = 90, Pitch = 0x1000, Uses = 4, LengthSamples = 16000 },
            new SpcRecipe { Srcn = slot, Adsr1 = 0x8F, Adsr2 = 0xE0, VolL = 80, VolR = 80, Pitch = 0x0800, Uses = 2, LengthSamples = 12000, Events = [new(3000, SpcEv.Srcn, other)] },
            new SpcRecipe { Srcn = slot, Adsr1 = 0xFF, Adsr2 = 0xE0, VolL = 0, VolR = 0, Pitch = 0x1000, Uses = 1, LengthSamples = 8000 },
            new SpcRecipe { Srcn = slot, Adsr1 = 0xFF, Adsr2 = 0xF0, VolL = 127, VolR = -90, Pitch = 0x2000, Eon = true, Uses = 3, LengthSamples = 16000, Events = [new(1600, SpcEv.VolL, 100), new(6400, SpcEv.Pitch, 200)] },
        ];
    }

    /// <summary>Notes on a SNES sampler that play a game voice: two voices, a glide, a release (the same shape as <see cref="SnesNotes"/>). Level 0-127 as the plugin sets it from FL's velocity.</summary>
    internal static float[] SnesRecipeNotes(SpcSnapshot snap, SpcRecipe recipe)
    {
        var s = new SnesSampler(snap, 48000);
        var all = new List<float>();
        var buf = new float[512];
        void Run(int blocks) { for (int i = 0; i < blocks; i++) { s.Render(buf, 256); all.AddRange(buf); } }
        s.NoteOnRecipe(0, recipe, SnesSampler.PitchFor(0, recipe.Pitch), 100); Run(10);
        s.NoteOnRecipe(1, recipe, SnesSampler.PitchFor(500, recipe.Pitch), 70); Run(10);
        s.Update(0, SnesSampler.PitchFor(200, recipe.Pitch), 60, 60); Run(10);
        s.NoteOff(1); Run(6);
        s.NoteOff(0); Run(30);
        return all.ToArray();
    }

    private static PresetData WithRecipes(PresetSource src, Instrument inst, int steps, ulong seed, SpcRecipe[] recipes)
    {
        var d = PresetFixtures.Make(src, inst, steps, seed);
        PresetNote.Attach(d, recipes, d.Index);
        return d;
    }

    private static IEnumerable<(PresetSource Src, Instrument Inst)> SnesInstrumentsToTry(ImodelReport r)
    {
        foreach (var src in Sources(r))
        {
            if (src.Snapshot is not SpcSnapshot) continue;
            foreach (var inst in PresetFixtures.Instruments(src).Where(i => i.Editable).Take(4)) yield return (src, inst);
        }
    }

    // =====================================================================================================================================================================
    // the block: what goes in, what comes back, what is refused
    // =====================================================================================================================================================================

    public static void NoteBlock(ImodelReport r)
    {
        int made = 0;
        foreach (var (src, inst) in SnesInstrumentsToTry(r))
            foreach (int steps in new[] { 0, 3 })
            {
                string what = $"{src.Name} #{inst.Index} after {steps} steps";
                var all = HandRecipes(inst.Index);
                var d = WithRecipes(src, inst, steps, (ulong)inst.Index + 9, all);
                var expected = SpcRecipe.Write([all[0], all[4]]);
                r.Expect(d.NoteVersion == PresetNote.SnesRecipes && d.NoteBehaviour.SequenceEqual(expected), $"{what}: the block holds {(d.NoteVersion == 0 ? "nothing" : d.NoteBehaviour.Length + " bytes")}, expected the two recipes that play on the sample alone ({expected.Length} bytes, version 1)");
                var file = PresetFile.TryWrite(d, out string? why);
                if (file == null) { r.Expect(false, $"{what}: could not be written: {why}"); continue; }
                var again = PresetFile.TryWrite(d, out _);
                r.Expect(again != null && again.AsSpan().SequenceEqual(file), $"{what}: the bytes of a preset with game voices are not the same every time");
                var rd = PresetFile.Read(file);
                if (!rd.Ok) { r.Expect(false, $"{what}: read back refused: {rd.Problem}"); continue; }
                var back = PresetNote.SnesList(rd.Data!);
                r.Expect(back != null && back.Length == 2 && SpcRecipe.Write(back).AsSpan().SequenceEqual(expected), $"{what}: the game voices came back changed");
                r.Expect(rd.Data!.HasNoteBehaviour && PresetNote.Describe(rd.Data).StartsWith("2 game voices"), $"{what}: the preset does not say it carries game voices ('{PresetNote.Describe(rd.Data)}')");
                var rewritten = PresetFile.TryWrite(rd.Data, out _);
                r.Expect(rewritten != null && rewritten.AsSpan().SequenceEqual(file), $"{what}: a read and a write changed the bytes");
                // an instrument with no recipe of its own sample carries nothing, and says nothing
                var none = WithRecipes(src, inst, steps, (ulong)inst.Index + 9, [all[1], all[2], all[3]]);
                r.Expect(none.NoteVersion == 0 && none.NoteBehaviour.Length == 0, $"{what}: recipes that cannot play on the sample alone were kept ({none.NoteBehaviour.Length} bytes)");
                made++;
            }

        // refusals: the container is fine, the block is wrong (with a valid checksum, which only the deep read can see)
        var snes = PresetFixtures.Synthetic().First(s => s.Name == "snes-samples");
        var si = PresetFixtures.Instruments(snes)[1];
        var plain = PresetFile.TryWrite(PresetFixtures.Make(snes, si, 1, 4), out _)!;
        byte[] Block(int version, byte[] data) => [(byte)version, (byte)(version >> 8), .. data];
        byte[] WithBlock(byte[] file, int version, byte[] data) => PresetRaw.WithHeader(PresetRaw.WithBlob(file, PresetFile.TagNote, _ => Block(version, data)), h => h.Replace("\"noteBehaviour\":0", $"\"noteBehaviour\":{version}"));
        void Refused(string what, byte[] file, string word)
        {
            var rd = PresetFile.Read(file);
            r.Expect(!rd.Ok && rd.Problem!.Contains(word), $"{what}: {(rd.Ok ? "accepted" : "refused for '" + rd.Problem + "'")}, expected a refusal naming '{word}'");
        }
        Refused("game voices of another sample", WithBlock(plain, 1, SpcRecipe.Write([HandRecipes(si.Index)[1]])), "another sample");
        Refused("a recipe that switches the voice to another sample", WithBlock(plain, 1, SpcRecipe.Write([HandRecipes(si.Index)[2]])), "another sample");
        Refused("version 1 holding something that is not a recipe list", WithBlock(plain, 1, [9, 9, 9, 9, 9]), "not a recipe list");
        Refused("version 1 holding an empty list", WithBlock(plain, 1, SpcRecipe.Write([])), "not a recipe list");
        var goodBlock = SpcRecipe.Write([HandRecipes(si.Index)[0]]);
        Refused("version 1 holding a recipe list cut short", WithBlock(plain, 1, goodBlock[..^3]), "not a recipe list");
        var gb = PresetFixtures.Synthetic().First(s => s.Name == "gb-dmg");
        var gbFile = PresetFile.TryWrite(PresetFixtures.Make(gb, PresetFixtures.Instruments(gb)[0], 0, 4), out _)!;
        Refused("SNES game voices in a Game Boy preset", WithBlock(gbFile, 1, goodBlock), "another console");
        // a version this code does not know: read, kept byte for byte, not played, and said so
        var future = WithBlock(plain, 7, [1, 2, 3, 4, 5]);
        var rf = PresetFile.Read(future);
        r.Expect(rf.Ok && rf.Data!.NoteVersion == 7 && PresetNote.SnesList(rf.Data) == null && PresetNote.Describe(rf.Data).Contains("cannot play"), "a later version's note behaviour must load, play nothing, and be called what it is");
        var fw = rf.Ok ? PresetFile.TryWrite(rf.Data!, out _) : null;
        r.Expect(fw != null && fw.AsSpan().SequenceEqual(future), "a later version's note behaviour did not survive a rewrite byte for byte");
        var fp = rf.Ok ? PresetPlayable.Build(rf.Data!, out _) : null;
        r.Expect(fp != null && fp.Recipes.Length == 0 && fp.CannotPlay == null, "a preset with a later version's note behaviour must still play its instrument");
        // a preset without a block has none
        var rp = PresetFile.Read(plain);
        r.Expect(rp.Ok && PresetNote.SnesList(rp.Data!) == null && PresetNote.Describe(rp.Data!) == "", "a preset without note behaviour reports some");
        r.Note($"{made} presets with game voices written, read back and rewritten unchanged; 6 wrong blocks refused with the reason, a later version kept");
    }

    // =====================================================================================================================================================================
    // the sound: a game voice played from the preset equals the same voice played from the game
    // =====================================================================================================================================================================

    public static void NotePlays(ImodelReport r)
    {
        int compared = 0, exact = 0, silent = 0; long samples = 0;
        foreach (var (src, inst) in SnesInstrumentsToTry(r))
            foreach (int steps in new[] { 0, 3 })
            {
                string what = $"{src.Name} #{inst.Index} after {steps} steps";
                var all = HandRecipes(inst.Index);
                var h = PresetFixtures.Corrupt(inst, steps, (ulong)inst.Index + 9);
                var d = WithRecipes(src, inst, steps, (ulong)inst.Index + 9, all);
                var play = PresetPlayable.Build(d, out string? why);
                if (play == null) { r.Expect(false, $"{what}: {why}"); continue; }
                r.Expect(play.Recipes.Length == 2 && ReferenceEquals(play.Snes!.Recipes, play.Recipes), $"{what}: the preset carries {play.Recipes.Length} game voices, expected 2 (and in its snapshot)");
                var written = ImodelChecks.Src(src.Snapshot).Write(h.Current);
                if (!written.Ok) { r.Expect(false, $"{what}: the model refused its own instrument: {written.Reason}"); continue; }
                var game = PresetChecks.WithoutLeftovers((SpcSnapshot)written.Snapshot!);
                var plainSound = SnesNotes(play.Snes!, play.InstrumentValue);
                for (int k = 0; k < 2; k++)
                {
                    var theirs = all[k == 0 ? 0 : 4];                                    // (the game's own recipe object, as the frozen game holds it)
                    if (VoiceReferenceOff) { theirs = SpcRecipe.Read(SpcRecipe.Write([theirs]))![0]; theirs.Adsr2 ^= 0x04; }
                    var mine = SnesRecipeNotes(play.Snes!, play.Recipes[k]);
                    var reference = SnesRecipeNotes(game, theirs);
                    bool same = Same(mine, reference);
                    compared++; samples += mine.Length; if (same) exact++;
                    if (ImodelData.Rms(mine) < 1e-6) silent++;
                    var (rms, max) = Error(mine, reference);
                    r.Expect(same, $"{what}: game voice {k + 1} from the preset renders other PCM than the game's own voice ({mine.Length} vs {reference.Length} values; difference rms {rms:0.000000}, largest {max:0.000000})");
                    // a game voice is not the plain sample: if it were, this check would prove nothing about the voices
                    r.Expect(!Same(mine, plainSound) && Error(mine, plainSound).Rms > 1e-4, $"{what}: game voice {k + 1} sounds the same as the plain sample");
                }
            }
        r.Expect(compared >= 8, $"only {compared} game voices were compared");
        r.Expect(silent * 3 < compared, $"{silent} of {compared} comparisons were of silence");
        r.Note($"{exact} of {compared} game voices ({samples:N0} values) render bit-identical PCM from the preset and from the game's own sound memory; none equals the plain sample");
    }
}
