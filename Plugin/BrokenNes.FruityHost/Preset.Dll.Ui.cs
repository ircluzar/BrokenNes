// Round 6 (presets): the whole path a user takes, with real mouse and key messages against the REAL library (a scratch folder named by BROKENNES_PRESETS_DIR): rip a corrupted instrument from a frozen SNES game with the Instruments tab, name it, send it to Presets,
// find it in the Presets tab, refuse to load it into the instance that runs the game, load it into an instance on its own emulator (the chip switches to the Presets chip), hear it, rename it, delete it (the project still holds it and the tab says so),
// export that copy, import it again, audition it, search and filter, and pick presets from the Emulator tab's picker under a Presets chip and under a console's shortcut.
using BrokenNes.Fruity;
using BrokenNes.SynthHost;
using BrokenNes2;
using BrokenNes2.Instruments;
using BrokenNes2.Presets;

namespace BrokenNes.FruityHost;

public sealed unsafe partial class SelfTest
{
    private static int PresetFiles(string folder) => Directory.EnumerateFiles(folder, "*.bn2preset", SearchOption.AllDirectories).Count();

    private UiCtx OpenPlainEditor(int emulator, int console)
    {
        var p = NewOn(emulator, console);
        nint parent = Win32Host.CreateParent();
        p.ShowEditor(parent);
        p.Host.EditorResized = () => Win32Host.FitFrame(parent, p.EditorHandle);
        Win32Host.Pump(120);
        return new UiCtx(p, parent);
    }

    private void PresetsUiFlow(T t)
    {
        string folder = Environment.GetEnvironmentVariable("BROKENNES_PRESETS_DIR") ?? throw new InvalidOperationException("no scratch library");
        foreach (var f in Directory.EnumerateFiles(folder, "*.bn2preset", SearchOption.AllDirectories)) File.Delete(f);      // (the check's own scratch folder)
        string sfc = Path.Combine(Path.GetTempPath(), $"bn2_{Guid.NewGuid():N}.sfc");
        File.WriteAllBytes(sfc, MakeSnesRom());
        try
        {
            using var a = OpenModelTab(2, sfc, "");
            var pa = a.P;
            UiFreeze(a);
            InstallSnes(pa, ImodelData.SnesWithSamples());

            // ---- rip a corrupted instrument: Randomise, Send to Presets, give it a name ----
            UiTabTo(t, a, UiTab1, "Instruments");
            UiSelectInstrument(t, a, 1);
            // (a corruption by hand, so that the check is the same every run: Randomise draws a fresh seed each time and may make a nearly silent sample)
            UiScrollFieldsToEnd(a);
            for (int i = 0; i < 10; i++) UiClick(t, a, UiFieldBase + 2 * 8 + 2, "Tuning plus", shift: true);
            t.Expect(pa.Test(420, 11) == 1 && WorkingField(pa, 1, "tune.cents") == 100, $"ten shift-clicks on Tuning's plus left it at {WorkingField(pa, 1, "tune.cents")}: '{UiText(pa, 3)}'");
            var ripped = DllRead(pa, 1)!;
            UiClick(t, a, UiSendToPresets, "Send to Presets");
            t.Expect(pa.Test(420, 12) == 1 && UiText(pa, 14).Contains("Sample 1") && PresetFiles(folder) == 0, $"Send to Presets: name field open {pa.Test(420, 12)}, offered '{UiText(pa, 14)}', files in the library {PresetFiles(folder)}");
            Win32Host.Text(a.Hwnd, "Crushed lead");
            Win32Host.Char(a.Hwnd, '\r');
            t.Expect(pa.Test(420, 12) == 0 && pa.Test(420, 11) == 1 && UiText(pa, 3).Contains("Crushed lead") && UiText(pa, 3).Contains("Added"), $"after Enter: name field open {pa.Test(420, 12)}, said '{UiText(pa, 3)}'");
            var files = Directory.EnumerateFiles(folder, "*.bn2preset", SearchOption.AllDirectories).ToList();
            t.Expect(files.Count == 1 && Path.GetFileName(files[0]) == "Crushed lead.bn2preset", $"the library holds {string.Join(", ", files.Select(f => Path.GetRelativePath(folder, f)))}");
            if (files.Count != 1) return;
            var firstBytes = File.ReadAllBytes(files[0]);
            var rd = PresetFile.Read(firstBytes);
            t.Expect(rd.Ok, "the file the tab wrote does not read: " + rd.Problem);
            if (rd.Data == null) return;
            var d = rd.Data;
            t.Expect(d.Name == "Crushed lead" && d.Console == ConsoleId.Snes && d.Index == 1 && d.Steps >= 1 && d.SourceGame == Path.GetFileName(sfc) && (d.SourceSha1.Length == 0 || d.SourceSha1.Length == 40),
                $"the preset says: {d.Name}, {d.Console}, instrument {d.Index}, {d.Steps} steps, game '{d.SourceGame}', SHA-1 '{d.SourceSha1}'");
            t.Expect(InstrumentCodec.Unpack(d.Instrument, out _)!.SameValues(ripped), "the preset does not hold the instrument as the frozen game has it now (the corrupted one)");
            t.Expect(InstrumentCodec.Unpack(d.Clean, out _)!.SameValues(DllReadClean(ImodelData.SnesWithSamples(), 1)), "the preset's clean instrument is not the one the game had");
            t.Expect(firstBytes.Length < 20_000 && !ContainsRun(firstBytes, File.ReadAllBytes(sfc)), $"the preset file ({firstBytes.Length:N0} bytes) must not hold the ROM");

            // ---- the Presets tab: the preset is listed; the instance that runs the game refuses it, with the reason ----
            UiTabTo(t, a, UiTab2, "Presets");
            Win32Host.Pump(100);
            t.Expect(pa.Test(421, 0) == 1, $"the Presets tab lists {pa.Test(421, 0)} presets, expected the one just added");
            UiClick(t, a, UiPresetList + 0, "the preset's row");
            UiClick(t, a, UiLoad, "Load (into the instance that runs the game)");
            t.Expect(pa.Test(421, 5) == 0 && UiText(pa, 5).Contains("runs a game") && pa.Test(HkPState) == 0 && pa.Test(HkPMode) == 0, $"Load into the instance that runs the game said '{UiText(pa, 5)}' (preset held {pa.Test(HkPState)}, preset mode {pa.Test(HkPMode)})");

            // ---- an instance of its own: Load switches its emulator to the Presets chip, and FL's notes play the preset ----
            using var b = OpenPlainEditor(3, 0);
            var pb = b.P;
            UiTabTo(t, b, UiTab2, "Presets");
            Win32Host.Pump(100);
            t.Expect(pb.Test(421, 0) == 1, $"the other instance sees {pb.Test(421, 0)} presets in the library");
            UiClick(t, b, UiPresetList + 0, "the preset's row");
            UiClick(t, b, UiLoad, "Load");
            t.Expect(pb.Test(421, 5) == 1 && UiText(pb, 5).Contains("Loaded") && pb.Test(HkPState) == 1 && pb.Test(HkPMode) == 1 && pb.GetName(Fpn.ParamValue, PCore, pb.GetParam(PCore)) == "PRESETS",
                $"Load said '{UiText(pb, 5)}' (preset held {pb.Test(HkPState)}, preset mode {pb.Test(HkPMode)}, chip {pb.GetName(Fpn.ParamValue, PCore, pb.GetParam(PCore))})");
            string libId = DllText(pb, HkPLibId);
            t.Expect(libId.EndsWith("/Crushed lead.bn2preset"), $"the instance holds the library file '{libId}'");
            var played = PlayScript(pb);
            using (var c = NewOn(4, 0))
            {
                PLoad(c, firstBytes, out _);
                t.Expect(SamePcm(played, PlayScript(c)) && ImodelData.Rms(played) > 1e-3, $"the preset the tab loaded does not sound like the file's bytes loaded directly (rms {ImodelData.Rms(played):0.0000})");
            }

            // ---- rename, from the tab ----
            UiTabTo(t, b, UiTab2, "Presets");
            UiClick(t, b, UiPresetList + 0, "the preset's row");
            UiClick(t, b, UiRename, "Rename");
            t.Expect(pb.Test(421, 2) == 1, "Rename did not open the name field");
            Win32Host.Char(b.Hwnd, (char)1); Win32Host.Text(b.Hwnd, "Crushed lead 2"); Win32Host.Char(b.Hwnd, '\r');
            var renamed = Directory.EnumerateFiles(folder, "*.bn2preset", SearchOption.AllDirectories).Select(f => Path.GetFileName(f)).ToList();
            t.Expect(renamed.SequenceEqual(["Crushed lead 2.bn2preset"]) && pb.Test(421, 5) == 1, $"after Rename the library holds {string.Join(", ", renamed)}; said '{UiText(pb, 5)}'");
            var renamedRd = PresetFile.Read(File.ReadAllBytes(Directory.EnumerateFiles(folder, "*.bn2preset", SearchOption.AllDirectories).First()));
            t.Expect(renamedRd.Ok && renamedRd.Data!.Name == "Crushed lead 2" && renamedRd.Data.Instrument.AsSpan().SequenceEqual(d.Instrument) && renamedRd.Data.History.AsSpan().SequenceEqual(d.History), "the renamed file changed more than its name");
            // the instance plays this very file, so it follows the rename (no second, old spelling of the preset in the project); what it plays is the same instrument
            t.Expect(DllText(pb, HkPName) == "Crushed lead 2" && pb.Test(421, 0) == 1 && ImodelData.Rms(PlayScript(pb)) > 1e-3, $"after the rename the instance holds '{DllText(pb, HkPName)}' and the tab lists {pb.Test(421, 0)} presets");
            string renamedPath = Directory.EnumerateFiles(folder, "*.bn2preset", SearchOption.AllDirectories).First();
            var renamedBytes = File.ReadAllBytes(renamedPath);
            t.Expect(pb.Test(HkPCrc) == PresetFile.Checksum(renamedBytes), "the instance does not hold the renamed file's bytes");

            // ---- delete: two clicks; the file goes; the instance still plays it and the tab lists it as kept in this project ----
            UiClick(t, b, UiPresetList + 0, "the preset's row");
            UiClick(t, b, UiDelete, "Delete");
            t.Expect(PresetFiles(folder) == 1, "the first click on Delete deleted");
            UiClick(t, b, UiDelete, "Delete (second click)");
            t.Expect(PresetFiles(folder) == 0 && pb.Test(421, 5) == 1, $"after the second click the library holds {PresetFiles(folder)} files; said '{UiText(pb, 5)}'");
            Win32Host.Pump(120);
            t.Expect(pb.Test(421, 0) == 1 && pb.Test(HkPState) == 1 && pb.Test(HkPCrc) == PresetFile.Checksum(renamedBytes) && ImodelData.Rms(PlayScript(pb)) > 1e-3, $"the project's own copy: the tab lists {pb.Test(421, 0)} presets; the instance holds {pb.Test(HkPState)} (CRC {pb.Test(HkPCrc)}) and plays at rms {ImodelData.Rms(PlayScript(pb)):0.0000}");
            UiClick(t, b, UiPresetList + 0, "the project's copy");
            t.Expect(UiText(pb, 7).StartsWith("project:"), $"the remaining row is '{UiText(pb, 7)}', expected the project's copy");
            // export that copy, then import it into the library: the library has it again and the project's entry is not listed twice
            string exported = Path.Combine(Path.GetTempPath(), $"bn2_export_{Guid.NewGuid():N}.bn2preset");
            try
            {
                fixed (char* s = exported) pb.Test(414, 0, (nint)s);
                UiClick(t, b, UiExport, "Export");
                t.Expect(File.Exists(exported) && File.ReadAllBytes(exported).AsSpan().SequenceEqual(renamedBytes), $"the exported project copy is not the preset's bytes (said '{UiText(pb, 5)}')");
                fixed (char* s = exported) pb.Test(414, 0, (nint)s);
                UiClick(t, b, UiImport, "Import");
                Win32Host.Pump(120);
                t.Expect(PresetFiles(folder) == 1 && pb.Test(421, 0) == 1 && pb.Test(421, 5) == 1, $"after Import the library holds {PresetFiles(folder)} files and the tab lists {pb.Test(421, 0)}; said '{UiText(pb, 5)}'");
                UiClick(t, b, UiPresetList + 0, "the imported preset");
                t.Expect(!UiText(pb, 7).StartsWith("project:") && UiText(pb, 7).EndsWith(".bn2preset"), $"the row is '{UiText(pb, 7)}': the project's copy should be listed once, as the library file");
                // importing the same file again, and a file that is not a preset
                fixed (char* s = exported) pb.Test(414, 0, (nint)s);
                UiClick(t, b, UiImport, "Import again");
                t.Expect(pb.Test(421, 5) == 0 && UiText(pb, 5).Contains("already") && PresetFiles(folder) == 1, $"importing a preset twice said '{UiText(pb, 5)}'");
                string junk = Path.Combine(Path.GetTempPath(), $"bn2_junk_{Guid.NewGuid():N}.bn2preset");
                File.WriteAllBytes(junk, new byte[200]);
                try
                {
                    fixed (char* s = junk) pb.Test(414, 0, (nint)s);
                    UiClick(t, b, UiImport, "Import a file that is not a preset");
                    t.Expect(pb.Test(421, 5) == 0 && PresetFiles(folder) == 1, $"importing a file that is not a preset said '{UiText(pb, 5)}'");
                }
                finally { DeleteQuietly(junk); }
            }
            finally { DeleteQuietly(exported); }

            // ---- audition: plays over the instance's output, once, and ends ----
            UiClick(t, b, UiPresetList + 0, "the preset's row");
            UiClick(t, b, UiAudition, "Audition");
            t.Expect(pb.Test(421, 5) == 1 && pb.Test(HkPAuditioning) == 1, $"Audition said '{UiText(pb, 5)}' (sounding {pb.Test(HkPAuditioning)})");
            var heard = RenderBlocks(pb, 1.0, 44100);
            t.Expect(ImodelData.Rms(heard) > 0.002, $"the audition is silent (rms {ImodelData.Rms(heard):0.00000})");
            RenderBlocks(pb, 2.5, 44100);
            t.Expect(pb.Test(HkPAuditioning) == 0, "the audition did not end");
            var after = RenderBlocks(pb, 0.3, 44100);
            // (not exactly zero: the S-DSP's echo loop rounds towards minus infinity, so a sample's echo leaves a fraction of a least-significant bit circulating for ever, as the chip does; 1e-4 is -80 dB)
            t.Expect(ImodelData.Rms(after) < 1e-4, $"the instance is not silent after the audition (rms {ImodelData.Rms(after):0.000000})");

            // ---- a second preset, from another console, to search and filter ----
            var md = PresetFixtures.Synthetic().First(s => s.Name == "opn");
            string mdFile = Path.Combine(Path.GetTempPath(), $"bn2_md_{Guid.NewGuid():N}.bn2preset");
            try
            {
                File.WriteAllBytes(mdFile, PresetFileOf(md, PresetFixtures.Instruments(md)[0], 1, 3, "Electric piano"));
                fixed (char* s = mdFile) pb.Test(414, 0, (nint)s);
                UiClick(t, b, UiImport, "Import the Genesis preset");
                Win32Host.Pump(120);
                t.Expect(PresetFiles(folder) == 2 && pb.Test(421, 0) == 2, $"two presets expected, the library holds {PresetFiles(folder)} and the tab lists {pb.Test(421, 0)}");
            }
            finally { DeleteQuietly(mdFile); }
            UiClick(t, b, UiSearch, "Search box");
            Win32Host.Text(b.Hwnd, "piano");
            t.Expect(pb.Test(421, 0) == 1, $"searching 'piano' lists {pb.Test(421, 0)}");
            for (int i = 0; i < 5; i++) Win32Host.Char(b.Hwnd, '\b');
            Win32Host.Text(b.Hwnd, "corrupted");
            t.Expect(pb.Test(421, 0) == 2, $"searching the tag 'corrupted' lists {pb.Test(421, 0)}, expected both");
            UiClick(t, b, UiSearchClear, "Clear");
            UiClick(t, b, UiFilter + 3, "SNES filter");
            t.Expect(pb.Test(421, 0) == 1, $"the SNES filter lists {pb.Test(421, 0)}");
            UiClick(t, b, UiFilter + 5, "MD filter");
            t.Expect(pb.Test(421, 0) == 1, $"the MD filter lists {pb.Test(421, 0)}");
            UiClick(t, b, UiFilter + 2, "GB filter");
            t.Expect(pb.Test(421, 0) == 0, $"the GB filter lists {pb.Test(421, 0)}");
            UiClick(t, b, UiFilter + 0, "All filter");

            // ---- the Emulator tab's preset picker: the chip decides which presets it lists ----
            UiTabTo(t, b, UiTab0, "Emulator");
            Win32Host.Pump(100);
            t.Expect(pb.Test(30, 90) != 0 && pb.Test(30, 10) == 0, "on the Presets chip the Emulator tab has no channel strip but has the preset picker");
            var chips = Chips(pb);
            int PickerCount(string chip)
            {
                pb.SetParam(PCore, chips.IndexOf(chip));
                Win32Host.Pump(100);
                var r = UiRect(b, 90);
                if (r == null) return -1;
                Win32Host.Click(b.Hwnd, r.Value.X + r.Value.W / 2, r.Value.Y + r.Value.H / 2);
                Win32Host.Pump(60);
                int n = pb.Test(31) == 90 ? (int)pb.Test(32) : -2;
                Win32Host.Click(b.Hwnd, 20, 20);                                                   // (closes the list again)
                Win32Host.Pump(30);
                return n;
            }
            t.Expect(PickerCount("PRESETS") == 2, "the PRESETS chip's picker does not list both presets");
            t.Expect(PickerCount("PRE-SNES") == 1, "the PRE-SNES shortcut's picker does not list the one SNES preset");
            PickItem(t, pb, 90, 0, "the SNES preset in the picker");
            t.Expect(UiText(pb, 15).Contains("Loaded") && pb.Test(HkPState) == 1 && DllText(pb, HkPSummary).StartsWith("SNES"), $"picking from the PRE-SNES picker said '{UiText(pb, 15)}', holds '{DllText(pb, HkPSummary)}'");
            t.Expect(PickerCount("PRE-MD") == 1, "the PRE-MD shortcut's picker does not list the one Genesis preset");
            PickItem(t, pb, 90, 0, "the Genesis preset in the picker");
            t.Expect(DllText(pb, HkPSummary).StartsWith("MD") && DllText(pb, HkPName) == "Electric piano", $"picking from the PRE-MD picker: holds '{DllText(pb, HkPSummary)}' / '{DllText(pb, HkPName)}'");
            t.Expect(PickerCount("PRE-GB") == 1, "the PRE-GB shortcut's picker (no Game Boy preset) should show its one explanatory row");
            PickItem(t, pb, 90, 0, "the explanatory row");
            t.Expect(UiText(pb, 15).Contains("no preset for this chip") && DllText(pb, HkPName) == "Electric piano", $"the empty picker said '{UiText(pb, 15)}'");
            pb.SetParam(PCore, 0);                                                                  // back to a NES chip: the strip comes back
            Win32Host.Pump(100);
            t.Expect(pb.Test(30, 10) != 0 && pb.Test(30, 90) == 0, "the channel strip did not come back when the chip went back to FIX");
        }
        finally { DeleteQuietly(sfc); }
    }

    private static bool ContainsRun(byte[] haystack, byte[] needleSource)
    {
        // a 24-byte run of the ROM that is not all one value, searched in the preset (a preset holds instrument data, never the game)
        for (int at = 0; at + 24 <= needleSource.Length; at += 24)
        {
            var run = needleSource.AsSpan(at, 24);
            if (run.IndexOfAnyExcept(run[0]) < 0) continue;
            if (haystack.AsSpan().IndexOf(run) >= 0) return true;
        }
        return false;
    }

    /// <summary>The instrument as the made-up SNES sound memory has it (what a preset's clean part must hold).</summary>
    private static Instrument DllReadClean(SpcSnapshot snap, int slot) => ImodelChecks.Src(snap).Read(slot)!;
}
