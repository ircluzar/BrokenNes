// Round 6 (imodel): the REAL instrument model behind the real Instruments tab, driven with real mouse and key messages (the patterns of SelfTest.Ui.cs; that test runs the tab against stubs, this one against the model the product ships).
// A SNES game (a made-up sound memory with three samples) and a Game Boy game are frozen with Instrument Runaway, the tab lists their instruments, and clicks on steppers, sliders, the hex field, the lock, Randomise, Nudge, Undo and Redo change the sound memory
// FL's notes play: the working copy (read back through the plugin's hooks), the bytes of the whole sound memory (undo is bit-exact) and the notes themselves (measured). Each scenario is shown able to fail by a deliberate bug (hook 312).
using BrokenNes.Fruity;
using BrokenNes.SynthHost;
using BrokenNes2;
using BrokenNes2.Instruments;

namespace BrokenNes.FruityHost;

public sealed unsafe partial class SelfTest
{
    private const int HkInstFromBytes = 309, HkInstMutant = 312;

    private UiCtx OpenModelTab(int console, string rom, string chip)
    {
        PluginInstance p;
        if (console == 2)
        {
            byte[] st;
            using (var seed = lib.Create()) { seed.SetParam(PConsole, 2); seed.SetParam(PMode, 1); st = seed.SaveState(); }
            p = lib.Create();
            p.Dispatcher(Fpd.SetSampleRate, 0, 44100);
            p.LoadState(StateWithRom(st, rom));
            if (!WaitConsole(p, 2)) throw new InvalidOperationException("the SNES test game did not load");
        }
        else p = SegaGame(rom, console, chip, 44100);
        nint parent = Win32Host.CreateParent();
        p.ShowEditor(parent);
        p.Host.EditorResized = () => Win32Host.FitFrame(parent, p.EditorHandle);
        Win32Host.Pump(120);
        return new UiCtx(p, parent);
    }

    private static void UiFreeze(UiCtx c) { c.P.Test(47, 0, 1); Win32Host.Pump(150); }

    private static void InstallSnes(PluginInstance p, SpcSnapshot s)
    {
        var bytes = s.ToBytes();
        fixed (byte* b = bytes) p.Test(HkInstFromBytes, bytes.Length, (nint)b);
    }

    /// <summary>The field's value in the working copy the plugin plays (read through the plugin, not through the tab).</summary>
    private static int WorkingField(PluginInstance p, int instrument, string id) => DllRead(p, instrument)?.Get(id) ?? int.MinValue;

    private static byte[] WorkingBlob(PluginInstance p, int instrument, int blob = 0) => DllRead(p, instrument)?.Blobs[blob].Data ?? [];

    private static void UiScrollFieldsToEnd(UiCtx c)
    {
        var s = UiRect(c, UiFieldBase);
        if (s != null) Win32Host.WheelAt(c.Hwnd, -20, s.Value.X + 20, s.Value.Y + 10);
    }

    private static void UiScrollFieldsToTop(UiCtx c)
    {
        var s = UiRect(c, UiFieldBase);
        if (s != null) Win32Host.WheelAt(c.Hwnd, 20, s.Value.X + 20, s.Value.Y + 10);
    }

    private void ModelTabSnes(T t)
    {
        string sfc = Path.Combine(Path.GetTempPath(), $"bn2_{Guid.NewGuid():N}.sfc");
        File.WriteAllBytes(sfc, MakeSnesRom());
        try
        {
            using var c = OpenModelTab(2, sfc, "");
            var p = c.P;
            UiFreeze(c);
            t.Expect(p.Test(45) == 1 && p.Test(48) == 1, "Instrument Runaway did not come on for the SNES test game");
            InstallSnes(p, ImodelData.SnesWithSamples());
            UiTabTo(t, c, UiTab1, "Instruments");
            Win32Host.Pump(150);

            // ---- the list and the fields come from the real model ----
            t.Expect(UiText(p, 2) == "", $"the Instruments tab says '{UiText(p, 2)}' although the real model is connected and Runaway is on");
            t.Expect(p.Test(420, 1) == 3 && p.Test(420, 2) == 0, $"the list has {p.Test(420, 1)} rows, selected {p.Test(420, 2)}: expected the 3 samples of the made-up sound memory");
            t.Expect(UiText(p, 9) == "Sample 0" && UiText(p, 100) == "Envelope mode=ADSR", $"the first instrument shows '{UiText(p, 9)}' / '{UiText(p, 100)}'");
            t.Expect(p.Test(420, 4) == 15, $"the sample has {p.Test(420, 4)} fields, expected 15");
            t.Expect(UiText(p, 10) == "" && UiText(p, 11) == "", $"a fresh instrument shows history '{UiText(p, 10)}' and seed '{UiText(p, 11)}'");
            long hashClean = p.Test(HkInstHash);
            var clean = Note(p);
            double f0 = Audio.Frequency(clean.Mono, 44100, clean.Log.StartSample + 4000, clean.Log.EndSample - 2000);

            // ---- a stepper held down is one history step, and the note changes pitch: Tuning +100 cents in ten shift-clicks ----
            UiScrollFieldsToEnd(c);                                          // rows: Left weight, Right weight, Tuning, Echo, Noise, Pitch modulation, Loop pointer
            t.Expect(UiText(p, 102).StartsWith("Tuning="), $"after scrolling the third visible row is '{UiText(p, 102)}', expected Tuning");
            for (int i = 0; i < 10; i++) UiClick(t, c, UiFieldBase + 2 * 8 + 2, "Tuning plus", shift: true);
            t.Expect(WorkingField(p, 0, "tune.cents") == 100, $"ten shift-plus clicks left Tuning at {WorkingField(p, 0, "tune.cents")} in the sound memory, expected 100");
            t.Expect(UiText(p, 10) == "step 1 of 1" && UiText(p, 11) == "", $"ten clicks on one field are one step of the history: '{UiText(p, 10)}' (and no seed: it was by hand: '{UiText(p, 11)}')");
            var up = Note(p);
            t.NearCents("the note after Tuning +100 cents", Audio.Frequency(up.Mono, 44100, up.Log.StartSample + 4000, up.Log.EndSample - 2000), f0 * Math.Pow(2, 100 / 1200.0), 8);
            // Undo gives back the exact bytes; Redo gives them again
            UiClick(t, c, UiUndo, "Undo");
            t.Expect(WorkingField(p, 0, "tune.cents") == 0 && p.Test(HkInstHash) == hashClean && UiText(p, 10) == "step 0 of 1", $"Undo: tuning {WorkingField(p, 0, "tune.cents")}, sound memory hash equal to the clean one: {p.Test(HkInstHash) == hashClean}, history '{UiText(p, 10)}'");
            UiClick(t, c, UiRedo, "Redo");
            t.Expect(WorkingField(p, 0, "tune.cents") == 100 && UiText(p, 10) == "step 1 of 1", $"Redo: tuning {WorkingField(p, 0, "tune.cents")}, history '{UiText(p, 10)}'");
            UiClick(t, c, UiUndo, "Undo");

            // ---- a byte typed into the hex field: the sample in the sound memory changes, stays listed and plays ----
            var hex = UiRect(c, UiHex);
            t.Expect(hex != null, "no hex field");
            if (hex != null)
            {
                var before = WorkingBlob(p, 0);
                Win32Host.Click(c.Hwnd, hex.Value.X + 54, hex.Value.Y + 11);
                Win32Host.Text(c.Hwnd, "5B");
                var after = WorkingBlob(p, 0);
                t.Expect(after.Length == before.Length && after[0] == 0x5B && before[0] != 0x5B && after.Skip(1).SequenceEqual(before.Skip(1)), $"typing 5B over byte 0 wrote ${(after.Length > 0 ? after[0] : -1):X2}");
                t.Expect(p.Test(HkInstCount) == 3, $"the edited sample dropped out of the list ({p.Test(HkInstCount)} instruments)");
                var edited = Note(p);
                t.Expect(ImodelData.DiffRms(clean.Stereo, edited.Stereo) > 0.005, "the edited sample sounds the same as the clean one");
                Win32Host.Click(c.Hwnd, 20, 20);
                UiClick(t, c, UiUndo, "Undo the byte");
                t.Expect(WorkingBlob(p, 0).AsSpan().SequenceEqual(before) && p.Test(HkInstHash) == hashClean, "Undo did not give the sample its exact bytes back");
            }

            // ---- locks: Randomise at full strength leaves a locked field and a locked sample alone, and changes the rest ----
            UiScrollFieldsToTop(c);
            UiClick(t, c, UiFieldBase + 0 * 8 + 3, "lock of Envelope mode");
            UiClick(t, c, UiLockBlob, "lock of the sample data");
            var strength = UiRect(c, UiStrength)!.Value;
            Win32Host.Click(c.Hwnd, strength.X + strength.W - 4, strength.Y + strength.H / 2);
            t.Expect(p.Test(420, 9) >= 95, $"the strength slider says {p.Test(420, 9)} after a click at its right end");
            var instBefore = DllRead(p, 0)!;
            UiClick(t, c, UiRandomise, "Randomise");
            var instAfter = DllRead(p, 0)!;
            t.Expect(p.Test(420, 11) == 1, $"Randomise said '{UiText(p, 3)}'");
            t.Expect(instAfter.Get("env.adsr") == instBefore.Get("env.adsr"), "Randomise changed the locked Envelope mode");
            t.Expect(instAfter.Blobs[0].Data.AsSpan().SequenceEqual(instBefore.Blobs[0].Data), "Randomise changed the locked sample data");
            t.Expect(!instAfter.SameValues(instBefore), "Randomise at full strength changed nothing");
            t.Expect(UiText(p, 10) == "step 1 of 1" && UiText(p, 11).Length == 16, $"after Randomise the toolbar shows history '{UiText(p, 10)}' and seed '{UiText(p, 11)}'");
            long hashRandom = p.Test(HkInstHash);
            var randomised = Note(p);
            t.Expect(ImodelData.DiffRms(clean.Stereo, randomised.Stereo) > 0.001, "the randomised instrument sounds the same as the clean one");
            UiClick(t, c, UiUndo, "Undo the randomise");
            t.Expect(p.Test(HkInstHash) == hashClean, "Undo of a Randomise did not give back the exact bytes of the sound memory");
            UiClick(t, c, UiRedo, "Redo the randomise");
            t.Expect(p.Test(HkInstHash) == hashRandom, "Redo of a Randomise did not give the same corruption again (the seed is part of the operation)");
            UiClick(t, c, UiUndo, "Undo the randomise");

            // ---- Nudge: the chosen field moves by its step ----
            UiScrollFieldsToEnd(c);
            UiClick(t, c, UiFieldBase + 2 * 8 + 4, "choose Tuning");
            int tune0 = WorkingField(p, 0, "tune.cents");
            UiClick(t, c, UiNudgeUp, "Nudge +");
            t.Expect(WorkingField(p, 0, "tune.cents") == tune0 + 150, $"Nudge + moved Tuning from {tune0} to {WorkingField(p, 0, "tune.cents")}, expected +150 (a 64th of its range)");
            UiClick(t, c, UiNudgeDown, "Nudge -");
            t.Expect(WorkingField(p, 0, "tune.cents") == tune0, $"Nudge - did not bring Tuning back ({WorkingField(p, 0, "tune.cents")})");
            // a locked field refuses, with the reason
            UiScrollFieldsToTop(c);
            UiClick(t, c, UiFieldBase + 0 * 8 + 4, "choose Envelope mode");
            UiClick(t, c, UiNudgeUp, "Nudge + on a locked field");
            t.Expect(p.Test(420, 11) == 0 && UiText(p, 3).Contains("locked"), $"nudging a locked field said '{UiText(p, 3)}'");

            // ---- Push is honest: not built yet ----
            UiClick(t, c, UiPush, "Push into the game");
            t.Expect(p.Test(420, 11) == 0 && UiText(p, 3).Contains("not built"), $"Push into the game said '{UiText(p, 3)}'");

            // ---- a change made behind the tab's back shows on the tab (the Version moves) ----
            var ext = DllRead(p, 1)!;
            var h = new InstrumentHistory(ext); h.Set("tune.cents", 1200);
            DllApply(p, h.Current, out _);
            Win32Host.Pump(100);
            UiTabTo(t, c, UiTab0, "Emulator"); UiTabTo(t, c, UiTab1, "Instruments");
            UiSelectInstrument(t, c, 1);
            UiScrollFieldsToEnd(c);
            t.Expect(UiText(p, 102) == "Tuning=+1200", $"a change made outside the tab shows as '{UiText(p, 102)}' on the tab, expected Tuning=+1200");

            // ---- releasing Runaway and freezing again starts a new session from what the game has ----
            p.Test(47, 0, 0); Win32Host.Pump(100);
            t.Expect(UiText(p, 2).Contains("Instrument Runaway"), $"with Runaway off the tab says '{UiText(p, 2)}'");
        }
        finally { DeleteQuietly(sfc); }
    }

    private void ModelTabGameBoy(T t)
    {
        string gb = TempGame(".gb", MakeGbRom(tone: true));
        try
        {
            using var c = OpenModelTab(1, gb, "DMG");
            var p = c.P;
            RenderBlocks(p, 0.8, 44100);
            UiFreeze(c);
            t.Expect(p.Test(45) == 1 && p.Test(50) == 1, "Instrument Runaway did not come on for the Game Boy test game");
            UiTabTo(t, c, UiTab1, "Instruments");
            Win32Host.Pump(150);
            t.Expect(UiText(p, 2) == "" && p.Test(420, 1) == 4, $"the Game Boy list has {p.Test(420, 1)} rows ('{UiText(p, 2)}'), expected 4");
            t.Expect(UiText(p, 9) == "Pulse 1" && UiText(p, 103).StartsWith("Duty="), $"Game Boy instrument 0 shows '{UiText(p, 9)}' / row 3 '{UiText(p, 103)}'");
            int duty0 = WorkingField(p, 0, "duty");
            var before = Note(p, 60, 0.5, 44100);
            // the Duty stepper (row 3 of Pulse 1): plus
            UiClick(t, c, UiFieldBase + 3 * 8 + 2, "Duty plus");
            t.Expect(WorkingField(p, 0, "duty") == duty0 + 1, $"Duty plus: {WorkingField(p, 0, "duty")}, expected {duty0 + 1}");
            var after = Note(p, 60, 0.5, 44100);
            t.Expect(ImodelData.DiffRms(before.Stereo, after.Stereo) > 0.01, "a wider pulse sounds the same as the one before");
            UiClick(t, c, UiUndo, "Undo");
            t.Expect(WorkingField(p, 0, "duty") == duty0, "Undo did not bring the duty back");
            // the wave: select it, type into the hex field
            UiSelectInstrument(t, c, 2);
            t.Expect(UiText(p, 9) == "Wave", $"row 2 is '{UiText(p, 9)}'");
            var hex = UiRect(c, UiHex);
            if (hex != null)
            {
                var wave = WorkingBlob(p, 2);
                Win32Host.Click(c.Hwnd, hex.Value.X + 54, hex.Value.Y + 11);
                Win32Host.Text(c.Hwnd, "12");
                var now = WorkingBlob(p, 2);
                t.Expect(wave.Length == 16 && now.Length == 16 && now[0] == 0x12 && now.Skip(1).SequenceEqual(wave.Skip(1)), $"typing 12 over the first wave byte wrote ${(now.Length > 0 ? now[0] : -1):X2}");
            }
        }
        finally { DeleteQuietly(gb); }
    }

    /// <summary>The scenarios on the real model, then each again with one deliberate bug in the model's glue (hook 312) that the scenario must notice.</summary>
    private void ImodelUi(T t)
    {
        var plain = new T();
        try { ModelTabSnes(plain); ModelTabGameBoy(plain); }
        catch (Exception e) { plain.Failures.Add($"exception {e.GetType().Name}: {e.Message} @ {e.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}"); }
        foreach (var f in plain.Failures) t.Expect(false, f);
        t.Note("the real model behind the real tab: SNES (list, steppers, hex, locks, Randomise, Nudge, Undo/Redo bit-exact, a change from outside, Push) and Game Boy (list, stepper, hex) with real mouse and key messages");

        foreach (var (id, what) in new[] { (InstrumentMutants.ProviderSkipsApply, "an edit stays in the history and never reaches the game"), (InstrumentMutants.ProviderIgnoresLocks, "Randomise ignores the locks"), (InstrumentMutants.UndoNotApplied, "Undo changes the history but not the game") })
        {
            var tt = new T();
            string sfc = Path.Combine(Path.GetTempPath(), $"bn2_{Guid.NewGuid():N}.sfc");
            File.WriteAllBytes(sfc, MakeSnesRom());
            // the bug is switched on inside the DLL once a plugin exists: it is a static of the DLL
            using (var probe = lib.Create()) probe.Test(HkInstMutant, 0, id);
            try { ModelTabSnes(tt); }
            catch (Exception e) { tt.Failures.Add("exception " + e.Message); }
            finally { using var probe = lib.Create(); probe.Test(HkInstMutant, 0, 0); DeleteQuietly(sfc); }
            t.Expect(tt.Failures.Count > 0, $"the tab scenario did not notice the deliberate bug '{what}': it cannot fail on it");
            if (tt.Failures.Count > 0) t.Note($"deliberate bug '{what}' caught: {tt.Failures[0]}");
        }
    }
}
