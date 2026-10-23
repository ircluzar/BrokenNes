// Round 6 (runaway): Instrument Runaway listens to what a game's sound driver does with its instruments, and keeps it.
//
// The user's report: on games whose instruments sound corrupted in the game, the same instruments sounded clean once taken into the plugin with Instrument Runaway, because Runaway copied the sound memory and the chip's
// registers at one instant and played FL notes on a private chip over that copy, while a corrupted game keeps most of its character in what its sound program DOES with the chip over time. The plugin now records every write the
// SPC700 makes to the S-DSP and turns the recent ones into "game voices" (recipes: the settings the driver keyed a voice on with, and a macro of the pitch, volume and envelope changes it made while the note sounded).
//
// This check drives the real DLL: a synthetic SNES game whose sound driver bends its voice's pitch and fades its volume; Runaway's picker lists the game voice; played from FL, the voice bends and fades (and the plain instrument
// does not); the Corruption window with "play for" learns a corrupted bend that freezing at once cannot; a project keeps the game voices; nothing allocates on the audio thread; damaged recipe sections are survived.
using System.Runtime.InteropServices;
using BrokenNes.Fruity;
using BrokenNes.SynthHost;
using BrokenNes2;
using BrokenNes2.Instruments;
using NesEmulator.Corruption;
using NesEmulator.Systems;
using SynthSnes = BrokenNes.Workshop.Corruption.SynthSnesRom;

namespace BrokenNes.FruityHost;

public sealed unsafe partial class SelfTest
{
    private const int HkPlayFor = 122, HkListening = 123, HkRecipes = 124, HkRecipeField = 125, HkRecipeVoices = 126, HkSpcLog = 127, HkListenNote = 128, HkRecipeParam = 129;
    private const int PRecipe = 13;

    private static string RecText(PluginInstance p, int hook)
    {
        int n = (int)p.Test(hook);
        if (n <= 0) return "";
        int take = Math.Min(n, 4000);
        var buf = new char[take];
        fixed (char* c = buf) p.Test(hook, take, (nint)c);
        return new string(buf);
    }

    private static long RecField(PluginInstance p, int recipe, int field) => p.Test(HkRecipeField, (field << 16) | recipe, 0);

    /// <summary>A note through the picker's current choice: the audio of FL note 60 held for <paramref name="seconds"/>, without the first and last moments.</summary>
    private float[] RecNote(PluginInstance p, double seconds = 0.6)
    {
        var m = new Mixer { SampleRate = 48000 };
        var audio = Mono(m.Run(p, One(60, 0, seconds)));
        return audio[m.Log[0].StartSample..m.Log[0].EndSample];
    }

    /// <summary>How much of the note's energy is at the bent pitch ($1100 over $1000 of a 2 kHz tone: 2,125 Hz) against its own 2 kHz: near 0 for a steady tone, large for one that bends between them.</summary>
    private static double Bend(float[] x) => Goertzel(x, 48000, 2125, 3000, x.Length - 3000) / Math.Max(1e-12, Goertzel(x, 48000, 2000, 3000, x.Length - 3000));

    /// <summary>The level at the start of the note against the level at its end (the vibrato game fades its voice): about 1 for a steady tone.</summary>
    private static double Fade(float[] x) => Audio.Rms(x, 3000, 3000 + 9600) / Math.Max(1e-9, Audio.Rms(x, x.Length - 9600 - 3000, x.Length - 3000));

    private void RunawayRecipes(T t)
    {
        string dir = Path.Combine(Path.GetTempPath(), "bn2_rec_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string rom = Path.Combine(dir, "vibrato.sfc");
        File.WriteAllBytes(rom, SynthSnes.Build(vibrato: true));
        using (var h = lib.Create()) h.Test(120, 0, 1);              // the editor does not run the games to keep their pictures moving: the test counts frames
        try
        {
            RecGame(t, rom);
            RecWindow(t, dir, rom);
            RecDamaged(t, rom);
            RecGenesis(t);
            RecEdited(t);
        }
        finally
        {
            using (var h = lib.Create()) h.Test(120, 0, 0);
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    // ------------------------------------------------------------------------------------------------
    // the driver's note, learned, listed, played, kept

    private void RecGame(T t, string rom)
    {
        byte[] project;
        double bendRecipe, fadeRecipe, rmsPlain, rmsRecipe;
        using (var p = SegaGame(rom, 2, "SNES"))
        {
            p.Test(HkRun, 0, 150);                           // 2.5 s: the loader, the upload, the driver's note
            t.Expect(p.Test(HkSpcLog) > 300, $"recipes: the game's chip writes are not logged ({p.Test(HkSpcLog)})");
            p.Test(47, 0, 1);
            t.Expect(p.Test(45) == 1 && p.Test(48) == 1, "recipes: the SNES game did not enter Instrument Runaway");
            t.Expect(p.Test(HkRecipes) == 1, $"recipes: {p.Test(HkRecipes)} game voices, expected the driver's one");
            if (p.Test(HkRecipes) != 1) return;
            t.Expect(RecField(p, 0, 0) == 0 && RecField(p, 0, 1) == 0xFF && RecField(p, 0, 2) == 0xE0 && RecField(p, 0, 4) == 0x1000, $"recipes: the voice's settings are sample {RecField(p, 0, 0)}, ADSR {RecField(p, 0, 1):X2}{RecField(p, 0, 2):X2}, pitch ${RecField(p, 0, 4):X4}");
            t.Expect(RecField(p, 0, 9) >= 150 && RecField(p, 0, 11) >= 100 && RecField(p, 0, 13) is >= 100 and <= 110, $"recipes: the macro holds {RecField(p, 0, 9)} pitch changes (largest {RecField(p, 0, 13)} cents) and {RecField(p, 0, 11)} volume changes");
            string note = RecText(p, HkListenNote);
            t.Expect(note.StartsWith("Listened for") && note.Contains("1 game voice"), $"recipes: the window's note says '{note}'");

            // the picker lists the plain instrument and the game voice; picking the game voice sets the Recipe parameter
            nint parent = Win32Host.CreateParent();
            try
            {
                p.ShowEditor(parent);
                for (int k = 0; k < 3; k++) Win32Host.Pump(40);
                var hwnd = p.EditorHandle;
                t.Expect(p.Test(30, 90) != 0, "recipes: no instrument picker while the SNES game is frozen");
                ClickControl(t, p, 90, "recipes: instrument picker");
                t.Expect(p.Test(31) == 90 && p.Test(32) == 2, $"recipes: the picker lists {p.Test(32)} entries, expected the game's sample and its game voice");
                var second = Centre(p, 101);
                t.Expect(second != null, "recipes: the picker has no second entry");
                if (second != null) Win32Host.Click(hwnd, second.Value.X, second.Value.Y);
                t.Expect(p.GetParam(PRecipe) == 1 && p.Test(HkRecipeParam) == 1, $"recipes: picking the game voice set the Recipe parameter to {p.GetParam(PRecipe)}");
                var shot = Win32Host.CapturePixels(hwnd, out int w, out int h);
                t.Expect(BrightPixels(shot, w, 645, 572, 240, 26) > 150, "recipes: the Instrument row shows no text");
                p.HideEditor();
            }
            finally { Win32Host.DestroyParent(parent); }

            // played from FL: the game voice bends and fades, the plain instrument is a steady tone
            p.SetParam(PRecipe, 0);
            var plain = RecNote(p);
            p.SetParam(PRecipe, 1);
            var voice = RecNote(p);
            double bendPlain = Bend(plain), fadePlain = Fade(plain);
            bendRecipe = Bend(voice); fadeRecipe = Fade(voice);
            rmsPlain = Audio.Rms(plain, 3000, plain.Length - 3000); rmsRecipe = Audio.Rms(voice, 3000, voice.Length - 3000);
            t.Expect(bendPlain < 0.05 && fadePlain < 1.1, $"recipes: the plain instrument bends ({bendPlain:0.000}) or fades ({fadePlain:0.00}); it must be a steady tone");
            t.Expect(bendRecipe > 0.3 && fadeRecipe > 1.2, $"recipes: the game voice should bend (side band {bendRecipe:0.000}) and fade ({fadeRecipe:0.00}x) as the game's driver did");
            t.Expect(rmsRecipe > 0.2 * rmsPlain && rmsRecipe < 3 * rmsPlain, $"recipes: the game voice is much louder or quieter than the plain instrument (rms {rmsRecipe:0.0000} against {rmsPlain:0.0000})");
            t.Note($"recipes: plain instrument side band {bendPlain:0.000}, fade {fadePlain:0.00}x, rms {rmsPlain:0.0000}; game voice side band {bendRecipe:0.000}, fade {fadeRecipe:0.00}x, rms {rmsRecipe:0.0000}");

            // a bend from FL on top: the game's bend is added to FL's pitch (a note a fifth up: the bend sits around 3,000 Hz)
            var up = new Mixer { SampleRate = 48000 };
            var fifth = Mono(up.Run(p, One(67, 0, 0.5)));
            double f67 = Audio.Frequency(fifth, 48000, up.Log[0].StartSample + 6000, up.Log[0].EndSample - 3000);
            t.Expect(f67 > 2900 && f67 < 3300, $"recipes: FL note 67 plays the game voice at {f67:0} Hz (expected near 3,000 Hz plus the bend)");

            // polyphony and no allocation on the audio thread: three notes at once, a plain instrument and a game voice measured the same way (between blocks, inside the mixer)
            long Chord(int recipe)
            {
                p.SetParam(PRecipe, recipe);
                long before = -1, after = -1;
                var chord = new Mixer { SampleRate = 48000, AfterBlock = sm => { if (sm >= 48000 * 0.3 && before < 0) before = AllocStart(p); if (sm >= 48000 * 1.0 && after < 0) after = p.Test(4); } };
                var three = new Scenario { Name = "three", Tempo = 60, LengthBeats = 1.2 };
                foreach (var (key, start, length) in new[] { (60.0, 0.0, 0.8), (64.0, 0.1, 0.7), (67.0, 0.2, 0.6) }) three.Notes.Add(new SimNote { StartBeat = start, LengthBeats = length, Key = key, Channel = 0, Velocity = 0.78 });
                Mono(chord.Run(p, three));
                return after - before;
            }
            long allocPlain = Chord(0), allocRecipe = Chord(1);
            t.Note($"recipes: bytes allocated on the audio thread while three notes play: plain instrument {allocPlain:N0}, game voice {allocRecipe:N0}");
            t.Expect(allocRecipe == 0, $"recipes: {allocRecipe:N0} bytes allocated on the audio thread while three game voices played (must be 0; the plain instrument allocated {allocPlain:N0})");
            t.Expect(p.Test(HkRecipeVoices) == 0, $"recipes: {p.Test(HkRecipeVoices)} voices still hold a recipe after the notes ended");
            p.SetParam(PRecipe, 1);

            project = p.SaveState();
        }

        // the project keeps the game voices: the section is there, a fresh instance has the same voice and plays the same sound
        t.Expect(FindSection(project, "SPR1") >= 0 && FindSection(project, "SPC1") >= 0, "recipes: the project lacks its recipe or sound-memory section");
        using (var q = lib.Create())
        {
            q.Dispatcher(Fpd.SetSampleRate, 0, 48000);
            q.LoadState(project);
            WaitForRom(q);
            t.Expect(q.Test(45) == 1 && q.Test(48) == 1, "recipes: the saved project did not come back in Instrument Runaway");
            t.Expect(q.Test(HkRecipes) == 1 && q.GetParam(PRecipe) == 1, $"recipes: the reopened project has {q.Test(HkRecipes)} game voices, Recipe parameter {q.GetParam(PRecipe)}");
            var again = RecNote(q);
            double bend2 = Bend(again), fade2 = Fade(again), rms2 = Audio.Rms(again, 3000, again.Length - 3000);
            t.Expect(Math.Abs(bend2 - bendRecipe) < 0.05 * Math.Max(1, bendRecipe) && Math.Abs(fade2 - fadeRecipe) < 0.05 * fadeRecipe && Math.Abs(rms2 / rmsRecipe - 1) < 0.02, $"recipes: the reopened game voice sounds different (side band {bend2:0.000} against {bendRecipe:0.000}, fade {fade2:0.00} against {fadeRecipe:0.00}, rms {rms2:0.0000} against {rmsRecipe:0.0000})");
        }
        t.Note($"recipes: the project's recipe section is {SectionLength(project, "SPR1"):N0} bytes (the sound memory next to it is {SectionLength(project, "SPC1"):N0})");
    }

    private static int SectionLength(byte[] project, string tag) { int at = FindSection(project, tag); return at < 0 ? -1 : BitConverter.ToInt32(project, at + 4); }

    // ------------------------------------------------------------------------------------------------
    // the Corruption window: play for N seconds, then Instrument Runaway switches on by itself

    private void RecWindow(T t, string dir, string rom)
    {
        // farm a corruption of the driver's code: the vibrato depth byte ($11 -> $1F), 45 frames into the note
        var family = new CorrFamily("Vibrato SNES", 2, "SNES", ConsoleKind.Snes, ".sfc", File.ReadAllBytes(rom), 45, 40, [new("ARAM", 0x0258, 0x1F), new("WRAM", 0x1F0, 0x5A)], "WRAM", true);
        var made = CorrFarm(t, dir, family);
        if (made == null) return;

        long Largest(PluginInstance p) => p.Test(HkRecipes) > 0 ? RecField(p, 0, 13) : -1;
        long atOnceBend, afterBend, atOnceEvents, afterEvents;

        // 1. frozen at once (play for 0): the corrupted game has not run yet, so nothing of what its driver does is known
        using (var p = SegaGame(made.Rom, 2, "SNES"))
        {
            p.Test(HkRun, 0, 25);
            p.Test(HkPlayFor, 0, 0);
            RecStart(t, p, made, "at once");
            t.Expect(p.Test(45) == 1, $"recipes (at once): Runaway should be on the moment the corrupted game exists ({CorText(p, 0)})");
            t.Expect(CorText(p, 0).Contains("did not play first"), $"recipes (at once): the result should say that nothing of the driver was learned: '{CorText(p, 0)}'");
            atOnceBend = Largest(p); atOnceEvents = p.Test(HkRecipes) > 0 ? RecField(p, 0, 9) : -1;
        }

        // 2. the corrupted game plays for one second first, then Runaway switches itself on with what the driver did
        byte[] project;
        using (var p = SegaGame(made.Rom, 2, "SNES"))
        {
            p.Test(HkRun, 0, 25);
            p.Test(HkPlayFor, 0, 1);
            RecStart(t, p, made, "play for 1 s");
            t.Expect(p.Test(45) == 0 && p.Test(HkListening) == 1, $"recipes (play for): right after the start Runaway is {p.Test(45)} and listening {p.Test(HkListening)}: the corrupted game should be playing");
            t.Expect(CorText(p, 0).Contains("plays for 1 s"), $"recipes (play for): the result says '{CorText(p, 0)}'");
            // the game plays by itself (the audio thread renders it); Runaway switches on once a second of game time has gone
            long begin = Environment.TickCount64;
            while (p.Test(45) == 0 && Environment.TickCount64 - begin < 20000) { RenderBlocks(p, 0.05); Win32Host.Pump(5); }
            t.Expect(p.Test(45) == 1 && p.Test(HkListening) == 0, $"recipes (play for): Runaway never switched on by itself (runaway {p.Test(45)}, listening {p.Test(HkListening)}, result '{CorText(p, 0)}')");
            afterBend = Largest(p); afterEvents = p.Test(HkRecipes) > 0 ? RecField(p, 0, 9) : -1;
            t.Expect(CorText(p, 0).Contains("Instrument Runaway is on after 1 s"), $"recipes (play for): the result says '{CorText(p, 0)}'");
            t.Expect(RecField(p, 0, 6) == 1, "recipes (play for): the voice sounding when the corrupted game began is a held-voice recipe");
            p.SetParam(PRecipe, 1);
            var voice = RecNote(p, 0.5);
            t.Expect(voice.All(float.IsFinite) && Audio.Rms(voice, 2000, voice.Length - 2000) > 0.003, "recipes (play for): the corrupted game voice is silent or not finite");
            project = p.SaveState();
        }
        t.Expect(atOnceEvents == 0 && atOnceBend <= 0, $"recipes: frozen at once the game voice should hold no pitch changes (control: the check must be able to fail): {atOnceEvents} pitch changes, largest {atOnceBend} cents");
        t.Expect(afterEvents >= 30 && afterBend >= 1000 && afterBend <= 1200, $"recipes (play for): the corrupted bend should be learned: {afterEvents} pitch changes, largest {afterBend} cents (about 1,040 to 1,147 depending on the pitch the held voice had when listening began; the clean bend is 105)");
        t.Note($"recipes: the corrupted driver's bend, frozen at once: {atOnceEvents} pitch changes (largest {atOnceBend} cents); after playing 1 s: {afterEvents} pitch changes (largest {afterBend} cents)");

        // 3. the project keeps the corrupted game voice, and the Corruption section still says where it came from
        t.Expect(FindSection(project, "SPR1") >= 0 && FindSection(project, "CRP1") >= 0, "recipes: the project lacks its recipe or corruption section");
        using (var q = lib.Create())
        {
            q.Dispatcher(Fpd.SetSampleRate, 0, 48000);
            q.LoadState(project);
            WaitForRom(q);
            t.Expect(q.Test(45) == 1 && q.Test(HkRecipes) == 1 && Largest(q) == afterBend, $"recipes: the reopened project holds {q.Test(HkRecipes)} game voices (largest bend {Largest(q)} cents, expected {afterBend})");
        }
    }

    private void RecStart(T t, PluginInstance p, CorrMade made, string name)
    {
        nint parent = Win32Host.CreateParent();
        try
        {
            p.ShowEditor(parent);
            for (int k = 0; k < 3; k++) Win32Host.Pump(40);
            ClickControl(t, p, IdCor, $"recipes ({name}): Corruption button");
            CorPick(p, made.Stock);
            ClickControl(t, p, IdCorOpenBtn, $"recipes ({name}): Open stockpile");
            t.Expect(CorWait(() => CorIdle(p)) && p.Test(HkCorCount) == 3, $"recipes ({name}): the stockpile did not open");
            ClickControl(t, p, IdCorRow, $"recipes ({name}): row 0");
            ClickControl(t, p, IdCorFreeze, $"recipes ({name}): freeze tick");
            ClickControl(t, p, IdCorStart, $"recipes ({name}): Start");
            t.Expect(CorWait(() => CorIdle(p), 90000) && p.Test(HkCorGood) == 1, $"recipes ({name}): the start said '{CorText(p, 0)}'");
            ClickControl(t, p, IdCorClose, $"recipes ({name}): Close");
            p.HideEditor();
        }
        finally { Win32Host.DestroyParent(parent); }
    }

    // ------------------------------------------------------------------------------------------------
    // damaged recipe sections are survived, not trusted

    private void RecDamaged(T t, string rom)
    {
        byte[] project;
        using (var p = SegaGame(rom, 2, "SNES"))
        {
            p.Test(HkRun, 0, 150);
            p.Test(47, 0, 1);
            project = p.SaveState();
        }
        int at = FindSection(project, "SPR1");
        t.Expect(at >= 0, "recipes (damaged): the project has no recipe section to damage");
        if (at < 0) return;
        int length = BitConverter.ToInt32(project, at + 4);
        var rnd = new Random(7);
        int survived = 0, kept = 0;
        for (int i = 0; i < 25; i++)
        {
            var bad = (byte[])project.Clone();
            for (int k = 0; k < 8; k++) bad[at + 8 + rnd.Next(length)] = (byte)rnd.Next(256);
            using var q = lib.Create();
            q.Dispatcher(Fpd.SetSampleRate, 0, 48000);
            q.LoadState(bad);
            WaitForRom(q);
            survived += q.Test(12) == 0 ? 1 : 0;                        // the game did not crash
            kept += q.Test(48) == 1 ? 1 : 0;                            // and the sound memory still came back (the recipes were taken or dropped, never trusted)
        }
        t.Expect(survived == 25 && kept == 25, $"recipes (damaged): of 25 projects with damaged recipe sections {survived} loaded without a crash and {kept} kept their sound memory");
        // a section cut short and a recipe count that lies
        var lying = (byte[])project.Clone();
        lying[at + 8 + 1] = 200;
        using (var q = lib.Create())
        {
            q.Dispatcher(Fpd.SetSampleRate, 0, 48000);
            q.LoadState(lying);
            WaitForRom(q);
            t.Expect(q.Test(45) == 1 && q.Test(48) == 1 && q.Test(HkRecipes) == 0, $"recipes (damaged): a recipe count of 200 should drop the recipes and keep the instruments (runaway {q.Test(45)}, recipes {q.Test(HkRecipes)})");
        }
        // an older project (no recipe section at all) loads exactly as before
        var without = project.Take(at).Concat(project.Skip(at + 8 + length)).ToArray();
        using (var q = lib.Create())
        {
            q.Dispatcher(Fpd.SetSampleRate, 0, 48000);
            q.LoadState(without);
            WaitForRom(q);
            var plainNote = RecNote(q);
            t.Expect(q.Test(45) == 1 && q.Test(48) == 1 && q.Test(HkRecipes) == 0 && Audio.Rms(plainNote, 3000, plainNote.Length - 3000) > 0.005, $"recipes (older project): runaway {q.Test(45)}, sampler {q.Test(48)}, recipes {q.Test(HkRecipes)}, the Recipe parameter {q.GetParam(PRecipe)} with no recipes plays the plain instrument at rms {Audio.Rms(plainNote, 3000, plainNote.Length - 3000):0.0000}");
        }
    }

    // ------------------------------------------------------------------------------------------------
    // the Instruments tab's model (round 6 imodel) writes an edited instrument into a COPY of the snapshot: the game's voices must come along (the first merge of the two lost them without a word)

    private void RecEdited(T t)
    {
        var snes = ImodelData.SnesWithSamples();
        snes.Recipes = [new SpcRecipe { Srcn = 0, Held = false }];
        snes.ListenNote = "Game voices: 1 (from the test)";
        var src = InstrumentSources.For(snes)!;
        var first = src.List().FirstOrDefault(i => i.Editable);
        t.Expect(first != null, "recipes (edited instrument): the SNES test snapshot lists no editable instrument");
        if (first != null)
        {
            var edit = first.Clone();
            var vol = edit.Field("vol.l");
            t.Expect(vol != null, "recipes (edited instrument): the SNES instrument has no vol.l field to edit");
            if (vol != null)
            {
                vol.Value = vol.Value == 100 ? 90 : 100;
                var written = src.Write(edit);
                var copy = written.Snapshot as SpcSnapshot;
                t.Expect(written.Ok && copy != null && !ReferenceEquals(copy, snes) && copy.Overlay != null, $"recipes (edited instrument): the volume edit should give a changed copy with an overlay ({written.Reason})");
                t.Expect(copy != null && copy.Recipes.Length == 1 && ReferenceEquals(copy.Recipes, snes.Recipes) && copy.ListenNote == snes.ListenNote, $"recipes (edited instrument): the SNES copy has {copy?.Recipes.Length} game voices and the note \"{copy?.ListenNote}\": the edit lost them");
                var again = src.Write(first.Clone());
                t.Expect(again.Ok && again.Snapshot is SpcSnapshot s2 && s2.Recipes.Length == 1, "recipes (edited instrument): an edit that changes nothing should keep the game voices too");
            }
        }
        var sega = ImodelData.PsgOnly();
        sega.Recipes = [new GenRecipe { Kind = GenRecipe.KindPsgTone, Period = 254, Att = 0 }];
        sega.ListenNote = "Game voices: 1 (from the test)";
        var segaCopy = SegaInstruments.Copy(sega);
        t.Expect(segaCopy.Recipes.Length == 1 && segaCopy.ListenNote == sega.ListenNote, $"recipes (edited instrument): the Sega copy has {segaCopy.Recipes.Length} game voices and the note \"{segaCopy.ListenNote}\": the copy lost them");
        var psgSrc = InstrumentSources.For(sega)!;
        var tone = psgSrc.List().FirstOrDefault(i => i.Editable);
        if (tone != null)
        {
            var e2 = tone.Clone();
            var f = e2.Fields.FirstOrDefault(x => x.Max > x.Min);
            if (f != null) f.Value = f.Value == f.Min ? f.Max : f.Min;
            var w2 = psgSrc.Write(e2);
            t.Expect(w2.Ok && w2.Snapshot is SegaSnapshot c2 && c2.Recipes.Length == 1, $"recipes (edited instrument): a PSG instrument edit should keep the game voices ({w2.Reason})");
        }
    }

    // ------------------------------------------------------------------------------------------------
    // the Genesis: the YM2612 and PSG notes of a game, learned and played

    private static string RecGenText(PluginInstance p) => RecText(p, 152);

    private void RecGenesis(T t)
    {
        string md = TempGame(".md", MakeMdRom(fm: true, dac: false));        // two FM notes of one patch (261.63 Hz and 392 Hz) and two PSG tones (440 Hz and 331 Hz), keyed at the start
        try
        {
            byte[] project;
            int fmRecipe = -1, psgRecipe = -1, count;
            double fmHz, fmFifth, psgHz;
            long[] hashes;
            using (var p = SegaGame(md, 4, "MD"))
            {
                RenderBlocks(p, 1.0);
                t.Expect(p.Test(12) == 0 && p.Test(11) > 30, "Genesis recipes: the test game did not run");
                p.Test(47, 0, 1);
                t.Expect(p.Test(45) == 1 && p.Test(71) == 3, $"Genesis recipes: Runaway on the Genesis game (runaway {p.Test(45)}, snapshot kind {p.Test(71)})");
                count = (int)p.Test(150);
                for (int i = 0; i < count; i++) { long kind = p.Test(151, i); if (kind == 0 && fmRecipe < 0) fmRecipe = i; if (kind == 1 && psgRecipe < 0) psgRecipe = i; }
                t.Expect(fmRecipe >= 0 && psgRecipe >= 0, $"Genesis recipes: the game's FM note and PSG tone should each be a game voice ({count} recipes, FM {fmRecipe}, PSG {psgRecipe})");
                if (fmRecipe < 0 || psgRecipe < 0) return;
                string note = RecText(p, 152);
                t.Expect(note.StartsWith("Listened for") && note.Contains("game voice"), $"Genesis recipes: the window's note says '{note}'");

                // the picker lists Auto, the game's instruments, then the game voices
                nint parent = Win32Host.CreateParent();
                try
                {
                    p.ShowEditor(parent);
                    for (int k = 0; k < 3; k++) Win32Host.Pump(40);
                    ClickControl(t, p, 90, "Genesis recipes: instrument picker");
                    long expected = 1 + p.Test(72) + count;
                    t.Expect(p.Test(31) == 90 && p.Test(32) == expected, $"Genesis recipes: the picker lists {p.Test(32)} entries, expected {expected} (Auto, {p.Test(72)} instruments, {count} game voices)");
                    var item = Centre(p, 100 + (int)(1 + p.Test(72)) + fmRecipe);
                    t.Expect(item != null, "Genesis recipes: the picker has no entry for the FM game voice");
                    if (item != null) Win32Host.Click(p.EditorHandle, item.Value.X, item.Value.Y);
                    t.Expect(p.GetParam(PRecipe) == fmRecipe + 1 && p.Test(HkRecipeParam) == fmRecipe + 1, $"Genesis recipes: picking the FM game voice set the Recipe parameter to {p.GetParam(PRecipe)} (expected {fmRecipe + 1})");
                    p.HideEditor();
                }
                finally { Win32Host.DestroyParent(parent); }

                // played from FL: the game's own pitch at FL's C5, and FL's pitch on top
                float[] Note(double key) { var m = new Mixer { SampleRate = 48000 }; var a = Mono(m.Run(p, One(key, 0, 0.5))); return a[(m.Log[0].StartSample + 6000)..(m.Log[0].EndSample - 3000)]; }
                p.SetParam(PRecipe, fmRecipe + 1);
                fmHz = Audio.Frequency(Note(60), 48000, 0, 24000 - 1);
                fmFifth = Audio.Frequency(Note(67), 48000, 0, 24000 - 1);
                t.Expect(Math.Abs(fmHz / 261.63 - 1) < 0.012 || Math.Abs(fmHz / 392.0 - 1) < 0.012, $"Genesis recipes: the FM game voice at FL's C5 plays {fmHz:0.0} Hz (the game's notes were 261.6 and 392.0 Hz)");
                t.Expect(Math.Abs(fmFifth / fmHz - 1.4983) < 0.012, $"Genesis recipes: FL's note 67 plays {fmFifth:0.0} Hz against {fmHz:0.0} Hz at 60: a fifth is 1.498");
                p.SetParam(PRecipe, psgRecipe + 1);
                psgHz = Audio.Frequency(Note(60), 48000, 0, 24000 - 1);
                t.Expect(Math.Abs(psgHz / 440.4 - 1) < 0.02 || Math.Abs(psgHz / 330.9 - 1) < 0.02, $"Genesis recipes: the PSG game voice at FL's C5 plays {psgHz:0.0} Hz (the game's tones were 440.4 and 330.9 Hz)");

                // chords on both, no allocation on the audio thread
                long Chord(int recipe)
                {
                    p.SetParam(PRecipe, recipe);
                    long before = -1, after = -1;
                    var mx = new Mixer { SampleRate = 48000, AfterBlock = sm => { if (sm >= 48000 * 0.3 && before < 0) before = AllocStart(p); if (sm >= 48000 * 1.0 && after < 0) after = p.Test(4); } };
                    var three = new Scenario { Name = "three", Tempo = 60, LengthBeats = 1.2 };
                    foreach (var (key, start, length) in new[] { (60.0, 0.0, 0.8), (64.0, 0.1, 0.7), (67.0, 0.2, 0.6) }) three.Notes.Add(new SimNote { StartBeat = start, LengthBeats = length, Key = key, Channel = 0, Velocity = 0.78 });
                    Mono(mx.Run(p, three));
                    return after - before;
                }
                long aFm = Chord(fmRecipe + 1), aPsg = Chord(psgRecipe + 1);
                t.Note($"Genesis recipes: bytes allocated on the audio thread while three notes play: FM game voice {aFm:N0}, PSG game voice {aPsg:N0}");
                t.Expect(aFm == 0 && aPsg == 0, $"Genesis recipes: {aFm:N0} (FM) and {aPsg:N0} (PSG) bytes allocated on the audio thread while three game voices played (must be 0)");
                p.SetParam(PRecipe, fmRecipe + 1);
                hashes = Enumerable.Range(0, count).Select(i => p.Test(151, (5 << 16) | i)).ToArray();
                project = p.SaveState();
            }

            // the project keeps the game voices
            t.Expect(FindSection(project, "GNR1") >= 0 && FindSection(project, "OPN1") >= 0, "Genesis recipes: the project lacks its recipe or chip section");
            using (var q = lib.Create())
            {
                q.Dispatcher(Fpd.SetSampleRate, 0, 48000);
                q.LoadState(project);
                WaitForRom(q);
                t.Expect(q.Test(45) == 1 && q.Test(150) == count && q.GetParam(PRecipe) == fmRecipe + 1, $"Genesis recipes: the reopened project has {q.Test(150)} game voices (expected {count}), Recipe parameter {q.GetParam(PRecipe)}");
                bool same = Enumerable.Range(0, count).All(i => q.Test(151, (5 << 16) | i) == hashes[i]);
                t.Expect(same, "Genesis recipes: a game voice came back from the project different from the one that was saved");
                var m2 = new Mixer { SampleRate = 48000 };
                var a2 = Mono(m2.Run(q, One(60, 0, 0.5)));
                double hz2 = Audio.Frequency(a2[(m2.Log[0].StartSample + 6000)..(m2.Log[0].EndSample - 3000)], 48000, 0, 24000 - 1);
                t.Expect(Math.Abs(hz2 / fmHz - 1) < 0.003, $"Genesis recipes: the reopened FM game voice plays {hz2:0.0} Hz, it played {fmHz:0.0} Hz");
            }
            t.Note($"Genesis recipes: the project's recipe section is {SectionLength(project, "GNR1"):N0} bytes (the chip registers next to it: {SectionLength(project, "OPN1"):N0})");

            // damaged sections are survived
            int at = FindSection(project, "GNR1");
            int length = BitConverter.ToInt32(project, at + 4);
            var rnd = new Random(11);
            int survived = 0;
            for (int i = 0; i < 20; i++)
            {
                var bad = (byte[])project.Clone();
                for (int k = 0; k < 8; k++) bad[at + 8 + rnd.Next(length)] = (byte)rnd.Next(256);
                using var q = lib.Create();
                q.Dispatcher(Fpd.SetSampleRate, 0, 48000);
                q.LoadState(bad);
                WaitForRom(q);
                if (q.Test(12) == 0 && q.Test(45) == 1 && q.Test(71) == 3) survived++;
            }
            t.Expect(survived == 20, $"Genesis recipes (damaged): of 20 projects with damaged recipe sections {survived} loaded without a crash and kept their chips");
            var lying = (byte[])project.Clone();
            lying[at + 8 + 1] = 200;
            using (var q = lib.Create())
            {
                q.Dispatcher(Fpd.SetSampleRate, 0, 48000);
                q.LoadState(lying);
                WaitForRom(q);
                t.Expect(q.Test(45) == 1 && q.Test(71) == 3 && q.Test(150) == 0, $"Genesis recipes (damaged): a recipe count of 200 should drop the recipes and keep the chips (runaway {q.Test(45)}, recipes {q.Test(150)})");
            }
            var without = project.Take(at).Concat(project.Skip(at + 8 + length)).ToArray();
            using (var q = lib.Create())
            {
                q.Dispatcher(Fpd.SetSampleRate, 0, 48000);
                q.LoadState(without);
                WaitForRom(q);
                t.Expect(q.Test(45) == 1 && q.Test(71) == 3 && q.Test(150) == 0, $"Genesis recipes (older project): runaway {q.Test(45)}, snapshot {q.Test(71)}, recipes {q.Test(150)}");
            }
        }
        finally { File.Delete(md); }
    }
}
