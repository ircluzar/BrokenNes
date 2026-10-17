// Round 6 (presets): the game voices a SNES preset carries, through the real plugin DLL: a frozen SNES game that holds game voices (what `runaway` learned from its driver) is ripped with the Instruments tab's Send to Presets (real mouse and keys, the real library in a scratch
// folder); the file holds the voices that can play on the sample alone; an instance on its own emulator loads it, the Emulator tab offers them as "Voice", and the voice FL's notes play is bit for bit the voice Runaway plays from the game; the project keeps them
// when the library file is gone; a preset whose note behaviour is of a later version still plays its instrument.
using BrokenNes.Fruity;
using BrokenNes.SynthHost;
using BrokenNes2;
using BrokenNes2.Instruments;
using BrokenNes2.Presets;

namespace BrokenNes.FruityHost;

public sealed unsafe partial class SelfTest
{
    private const int HkPVoices = 359;

    private void PresetsVoices(T t)
    {
        string folder = Environment.GetEnvironmentVariable("BROKENNES_PRESETS_DIR") ?? throw new InvalidOperationException("no scratch library");
        foreach (var f in Directory.EnumerateFiles(folder, "*.bn2preset", SearchOption.AllDirectories)) File.Delete(f);
        string sfc = Path.Combine(Path.GetTempPath(), $"bn2_{Guid.NewGuid():N}.sfc");
        File.WriteAllBytes(sfc, MakeSnesRom());
        const int Slot = 2;                                          // (a loud sample of the made-up game: sample 1 is nearly silent under a slow envelope)
        var all = PresetChecks.HandRecipes(Slot);
        var playing = new[] { all[0], all[4] };
        try
        {
            using var a = OpenModelTab(2, sfc, "");
            var pa = a.P;
            UiFreeze(a);
            InstallSnes(pa, PresetChecks.WithoutLeftovers(ImodelData.SnesWithSamples()));
            var five = SpcRecipe.Write(all);
            fixed (byte* b = five) t.Expect(pa.Test(HkPVoices, five.Length, (nint)b) == 0, "the frozen game did not take its game voices");

            // ---- rip a sample with its game voices: the voices that cannot play on the sample alone are left out ----
            UiTabTo(t, a, UiTab1, "Instruments");
            UiSelectInstrument(t, a, Slot);
            UiScrollFieldsToEnd(a);
            for (int i = 0; i < 10; i++) UiClick(t, a, UiFieldBase + 2 * 8 + 2, "Tuning plus", shift: true);
            UiClick(t, a, UiSendToPresets, "Send to Presets");
            Win32Host.Text(a.Hwnd, "Voiced lead");
            Win32Host.Char(a.Hwnd, '\r');
            var files = Directory.EnumerateFiles(folder, "*.bn2preset", SearchOption.AllDirectories).ToList();
            t.Expect(files.Count == 1, $"the library holds {files.Count} presets after Send to Presets, expected 1 ('{UiText(pa, 3)}')");
            if (files.Count != 1) return;
            var fileBytes = File.ReadAllBytes(files[0]);
            var rd = PresetFile.Read(fileBytes);
            t.Expect(rd.Ok, "the file the tab wrote does not read: " + rd.Problem);
            if (rd.Data == null) return;
            var carried = PresetNote.SnesList(rd.Data);
            t.Expect(rd.Data.NoteVersion == PresetNote.SnesRecipes && carried != null && carried.Length == 2 && rd.Data.NoteBehaviour.AsSpan().SequenceEqual(SpcRecipe.Write(playing)),
                $"the preset holds note behaviour version {rd.Data.NoteVersion} with {carried?.Length ?? 0} game voices; expected version 1 with the 2 that play on the sample alone (the frozen game held 5)");

            // ---- an instance of its own loads it; the Emulator tab offers the voices ----
            using var b2 = OpenPlainEditor(3, 0);
            var pb = b2.P;
            UiTabTo(t, b2, UiTab2, "Presets");
            Win32Host.Pump(100);
            UiClick(t, b2, UiPresetList + 0, "the preset's row");
            UiClick(t, b2, UiLoad, "Load");
            t.Expect(pb.Test(HkPState) == 1 && pb.Test(HkPMode) == 1 && pb.GetParam(PRecipe) == 0, $"Load: preset held {pb.Test(HkPState)}, preset mode {pb.Test(HkPMode)}, Recipe {pb.GetParam(PRecipe)} (a new preset starts at its plain sample); said '{UiText(pb, 5)}'");
            t.Expect(DllText(pb, HkPSummary).Contains("2 game voices"), $"the summary says '{DllText(pb, HkPSummary)}'");
            UiTabTo(t, b2, UiTab0, "Emulator");
            Win32Host.Pump(100);
            t.Expect(pb.Test(30, 9) != 0, "no Voice dropdown on the Emulator tab for a preset that carries game voices");
            if (ClickControl(t, pb, 9, "Voice dropdown"))
            {
                t.Expect(pb.Test(31) == 9 && pb.Test(32) == 3, $"the Voice list has {pb.Test(32)} entries, expected the plain sample and the 2 game voices");
                Win32Host.Click(pb.EditorHandle, 20, 20);          // (closes it)
            }
            PickItem(t, pb, 9, 1, "Voice: game voice 1");
            t.Expect(pb.GetParam(PRecipe) == 1, $"picking the first game voice set the Recipe parameter to {pb.GetParam(PRecipe)}");
            Win32Host.Pump(100);
            if (Environment.GetEnvironmentVariable("BROKENNES_SHOT_DIR") is { Length: > 0 } shots)          // (only to look at the screen by eye: nothing is compared)
            {
                Directory.CreateDirectory(shots);
                Win32Host.CaptureWindow(b2.Parent, pb.EditorHandle, 920, 670, Path.Combine(shots, "preset-voice-emulator.bmp"));
                UiTabTo(t, b2, UiTab2, "Presets"); Win32Host.Pump(100);
                Win32Host.CaptureWindow(b2.Parent, pb.EditorHandle, 1324, 670, Path.Combine(shots, "preset-voice-presets.bmp"));
                UiTabTo(t, b2, UiTab0, "Emulator");
            }

            // every sound below is played by an instance of its own (a sampler keeps its chip's state from one play to the next: only the first play of a fresh instance is the same every time)
            float[] PlayPreset(byte[] file, int voice, int emulator)
            {
                using var q = NewOn(emulator, 0);
                int r = PLoad(q, file, out string text);
                if (r != 0) { t.Expect(false, $"voice {voice}: the preset was not loaded: {text}"); return []; }
                q.SetParam(PRecipe, voice);
                return PlayScript(q);
            }
            // ---- the voice FL's notes play is the voice Runaway plays from the game (the frozen copy holds the corrupted sample the tab edited) ----
            var edited = DllRead(pa, Slot)!;
            float[] PlayGame(int recipe)
            {
                using var run = NewOn(6, 2);
                var bytes = PresetChecks.WithoutLeftovers(ImodelData.SnesWithSamples()).ToBytes();
                fixed (byte* bp = bytes) if (run.Test(309, bytes.Length, (nint)bp) != 0) { t.Expect(false, "the SNES snapshot was not accepted"); return []; }
                fixed (byte* fp = five) if (run.Test(HkPVoices, five.Length, (nint)fp) != 0) { t.Expect(false, "the game voices were not accepted"); return []; }
                if (run.Test(45) != 1) { t.Expect(false, "Runaway did not come on"); return []; }
                int ar = DllApply(run, edited, out string text);
                if (ar != 0) { t.Expect(false, "the edit was not written into the frozen game: " + text); return []; }
                run.SetParam(PInstrument, Slot);
                run.SetParam(PRecipe, recipe);
                return PlayScript(run);
            }
            var plain = PlayPreset(fileBytes, 0, 7);
            int compared = 0, exact = 0;
            foreach (int voice in new[] { 1, 2 })
            {
                var theirs = PlayGame(voice == 1 ? 1 : 5);                  // (the game holds all five: the preset's second voice is the game's fifth)
                var mine = PlayPreset(fileBytes, voice, 7);
                compared++;
                bool same = SamePcm(theirs, mine);
                if (same) exact++;
                t.Expect(same, $"game voice {voice}: the preset through the plugin renders other PCM than Runaway playing the same voice from the game (difference rms {ImodelData.DiffRms(theirs, mine):0.000000} over {theirs.Length} values)");
                t.Expect(ImodelData.Rms(mine) > 1e-3 && !SamePcm(mine, plain) && ImodelData.DiffRms(mine, plain) > 1e-3, $"game voice {voice}: silent, or the same as the plain sample (rms {ImodelData.Rms(mine):0.0000}, difference from plain {ImodelData.DiffRms(mine, plain):0.0000})");
            }
            t.Note($"{exact} of {compared} game voices played from the preset are bit-identical to the game's own through the plugin (FL notes with a glide and an overlap); the Voice dropdown lists the plain sample and the 2 voices");

            // ---- the project keeps them: the library file is gone, the voice still plays ----
            byte[] saved; float[] voiced;
            using (var q = NewOn(7, 0))
            {
                PLoad(q, fileBytes, out _);
                q.SetParam(PRecipe, 2);
                voiced = PlayScript(q);
                saved = q.SaveState();
            }
            File.Delete(files[0]);
            using (var c = NewOn(4, 0))
            {
                c.LoadState(saved);
                var again = PlayScript(c);
                t.Expect(c.Test(HkPState) == 1 && c.GetParam(PRecipe) == 2 && SamePcm(again, voiced), $"a project reopened with the library file gone: preset held {c.Test(HkPState)}, Recipe {c.GetParam(PRecipe)}, same sound {SamePcm(again, voiced)}");
            }

            // ---- note behaviour of a later version: the instrument plays, the voices do not exist ----
            var future = PresetRaw.WithHeader(PresetRaw.WithBlob(fileBytes, PresetFile.TagNote, _ => [7, 0, 1, 2, 3]), h => h.Replace($"\"noteBehaviour\":{PresetNote.SnesRecipes}", "\"noteBehaviour\":7"));
            var futurePlain = PlayPreset(future, 0, 7);
            var futureVoice = PlayPreset(future, 1, 7);
            t.Expect(futurePlain.Length > 0 && SamePcm(futurePlain, futureVoice) && ImodelData.Rms(futurePlain) > 1e-3 && SamePcm(futurePlain, plain), "a preset with a later version's note behaviour must play its plain sample whatever the Recipe parameter says (and the same one as without that block)");
        }
        finally { try { File.Delete(sfc); } catch (IOException) { } }
    }
}
