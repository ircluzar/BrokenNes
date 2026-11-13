// Layout B (2026-11-13): the checks of the new pieces, through the real editor with real mouse and key messages.
//   chip-picker   the Sound chip list is grouped stock / ported / nano / presets (on the NES console exactly two group starts; the first ported row is DARC, chip 31);
//                 "show every console's chips" adds other consoles' chips and picking one moves the emulator to that console
//   song-bar      the song bar opens a MIDI file (as Open MIDI... does), the song plays in the game, its loop position moves, Stop and Play in game work, the "in <game>" picker lists the games
//   runaway-tree  a made-up sound memory (SpcSnapshot.TreeTestSample: a looping sample, its duplicate, a 5 ms one-shot, a 500 ms one-shot): Samples 1 (x2: the duplicate folded), Drums 1,
//                 Sound effects 1; waveforms drawn; a click picks the instrument; a group folds; Rename with typed keys; the name survives a saved and reopened project; the play button sounds
// Each one is then run again with a deliberate bug switched on (UiMutate through editor hook 499) and must FAIL: ChipsUngrouped, ShowAllDead; TreeNoFold, TreeKindsWrong, RenameNotSaved, AuditionSilent.
// (The view switching, the fixed window size, and the strip and song bar staying on every view are `ui` scenario `tabs`, with its own three bugs in `ui-proof`.)
using BrokenNes.Fruity;

namespace BrokenNes.FruityHost;

public sealed unsafe partial class SelfTest
{
    private const int LyAllChips = 62, LyBarOpen = 2200, LyBarTarget = 2201, LyBarPlay = 2202, LyBarStop = 2204, LyTreeRename = 2250, LyTreeRow = 2260, LyTreePlay = 2280;
    private const int MutChipsUngrouped = 1 << 22, MutShowAllDead = 1 << 23, MutTreeNoFold = 1 << 24, MutTreeKindsWrong = 1 << 25, MutRenameNotSaved = 1 << 26, MutAuditionSilent = 1 << 27;

    private static int Bits(long m) { int n = 0; for (; m != 0; m &= m - 1) n++; return n; }

    /// <summary>Clicks row <paramref name="k"/> of the open dropdown list, scrolling it with the wheel until the row is drawn.</summary>
    private bool PickListItem(T t, PluginInstance p, int k, string what)
    {
        for (int g = 0; g < 80 && p.Test(30, 100 + k) == 0; g++) { Win32Host.Wheel(p.EditorHandle, -1); Win32Host.Pump(10); }
        return ClickControl(t, p, 100 + k, what);
    }

    private void LayoutChecks(T t)
    {
        var scenarios = new (string Name, Action<T> Body, (int Fault, string Why)[] Faults)[]
        {
            ("chip-picker", ChipPickerScenario, [(MutChipsUngrouped, "the chip list keeps index order"), (MutShowAllDead, "\"show every console's chips\" adds nothing")]),
            ("song-bar", SongBarScenario, []),
            ("runaway-tree", RunawayTreeScenario, [(MutTreeNoFold, "duplicates are not folded"), (MutTreeKindsWrong, "every sample is filed under Samples"), (MutRenameNotSaved, "a name is left out of the project"), (MutAuditionSilent, "the play button plays nothing")]),
        };
        int caught = 0, faults = 0;
        foreach (var (name, body, list) in scenarios)
        {
            var clean = new T();
            try { body(clean); } catch (Exception e) { clean.Failures.Add($"exception {e.GetType().Name}: {e.Message}"); }
            finally { SetUiFault(0); }
            t.Failures.AddRange(clean.Failures.Select(f => $"{name}: {f}"));
            foreach (var n in clean.Info) t.Note($"{name}: {n}");
            foreach (var (fault, why) in list)
            {
                faults++;
                var bad = new T();
                try { SetUiFault(fault); body(bad); } catch (Exception e) { bad.Failures.Add(e.Message); }
                finally { SetUiFault(0); }
                if (bad.Failures.Count == 0) t.Expect(false, $"{name} did not fail with the deliberate bug '{why}': that check cannot fail");
                else { caught++; t.Note($"deliberate bug '{why}' -> {bad.Failures.Count} failure(s), first: {bad.Failures[0]}"); }
            }
        }
        t.Note($"{caught} of {faults} deliberate bugs caught");
    }

    /// <summary>The fault mask lives in the DLL (UiMutate, a static): any instance's editor hook sets it for the whole process.</summary>
    private void SetUiFault(int mask)
    {
        using var p = lib.Create();
        nint parent = Win32Host.CreateParent();
        try { p.ShowEditor(parent); p.Test(499, 0, mask); p.HideEditor(); }
        finally { Win32Host.DestroyParent(parent); }
    }

    private (PluginInstance P, nint Parent) LayoutEditor(PluginInstance p)
    {
        nint parent = Win32Host.CreateParent();
        p.ShowEditor(parent);
        for (int k = 0; k < 3; k++) Win32Host.Pump(40);
        return (p, parent);
    }

    // ------------------------------------------------------------------------------------------------ the chip picker

    private void ChipPickerScenario(T t)
    {
        using var p = lib.Create();
        p.Dispatcher(Fpd.SetSampleRate, 0, 48000);
        p.SetParam(PChannel, 1);
        var (_, parent) = LayoutEditor(p);
        try
        {
            ClickControl(t, p, 4, "Sound chip dropdown"); Win32Host.Pump(30);
            int own = (int)p.Test(32);
            long mask = p.Test(894);
            t.Expect(p.Test(31) == 4 && own >= 30, $"the NES chip list has {own} rows (open: {p.Test(31)})");
            // NES: stock (FIX ... ULQ, the enhanced bridges), ported (DARC ... FSID, NSTRJ, NSTRDC), presets (PRESETS, PRE-NES): two group starts
            t.Expect(Bits(mask) == 2, $"the NES chip list has {Bits(mask)} group starts (mask {mask:X}), expected 2 (stock / ported / presets)");
            int firstPorted = mask == 0 ? -1 : System.Numerics.BitOperations.TrailingZeroCount((ulong)mask);
            if (firstPorted > 0 && PickListItem(t, p, firstPorted, "the first ported chip"))
            {
                Win32Host.Pump(30);
                t.Expect(p.GetParam(PCore) == 31 && p.GetName(Fpn.ParamValue, PCore, 31) == "DARC", $"the first row of the ported group is chip {p.GetParam(PCore)}, expected DARC (31)");
            }
            p.SetParam(PCore, 0);
            Win32Host.Pump(30);

            // show every console's chips: more rows, and one of them moves the emulator to its console
            ClickControl(t, p, LyAllChips, "show every console's chips"); Win32Host.Pump(30);
            t.Expect(p.Test(892) == 1, "the tick did not switch on");
            ClickControl(t, p, 4, "Sound chip dropdown (all consoles)"); Win32Host.Pump(30);
            int all = (int)p.Test(32);
            t.Expect(all > own + 20, $"with every console's chips the list has {all} rows, expected well over the NES's {own}");
            if (all > own && PickListItem(t, p, own, "the first chip of another console"))
            {
                Win32Host.Pump(60);
                t.Expect(p.GetParam(PConsole) != 0, $"picking another console's chip left the console at {p.GetParam(PConsole)}");
                t.Note($"NES list {own} rows, with every console's {all}; picking row {own} moved the emulator to console {p.GetParam(PConsole)} (chip {p.GetName(Fpn.ParamValue, PCore, p.GetParam(PCore))})");
            }
            else if (all <= own) Win32Host.Click(p.EditorHandle, 1, 1);
            // ROM mode: the tick adds nothing (another console's chip would replace the game)
            p.SetParam(PConsole, 0);
            p.HideEditor();
        }
        finally { Win32Host.DestroyParent(parent); }
    }

    // ------------------------------------------------------------------------------------------------ the song bar

    private void SongBarScenario(T t)
    {
        string mid = WriteMid("song bar", MidiFixtures.Synthetic());
        using var p = SongPlugin();
        var (_, parent) = LayoutEditor(p);
        try
        {
            t.Expect(p.Test(30, LyBarOpen) != 0 && p.Test(30, LyBarPlay) != 0 && p.Test(30, 2000) != 0, "the song bar's Open / Play in game / Song view buttons are not drawn");
            fixed (char* c = mid + "\0") p.Test(EdSPick, 0, (nint)c);
            ClickControl(t, p, LyBarOpen, "the song bar's Open...");
            t.Expect(SWait(p, () => p.Test(HkSRunning) == 1), $"the song did not become the game: '{SText(p, 0)}'");
            var buf = new float[512];
            long f0 = p.Test(893);
            for (int b = 0; b < 48000 * 2 / 256; b++) { fixed (float* f = buf) { p.NewTick(); p.GenRender(f, 256); } if (b % 8 == 0) Win32Host.Pump(5); }
            Win32Host.Pump(60);
            long f1 = p.Test(893);
            t.Expect(f1 is > 0 and <= 1000 && f1 != f0, $"the loop position did not move ({f0} -> {f1})");
            ClickControl(t, p, LyBarStop, "the song bar's Stop"); Win32Host.Pump(30);
            t.Expect(p.Test(HkSStopped) == 1, "Stop did not stop the song");
            ClickControl(t, p, LyBarPlay, "the song bar's Play in game"); Win32Host.Pump(30);
            t.Expect(SWait(p, () => p.Test(HkSStopped) == 0), "Play in game did not start the song again");
            ClickControl(t, p, LyBarTarget, "the song bar's game picker"); Win32Host.Pump(30);
            t.Expect(p.Test(31) == LyBarTarget && p.Test(32) >= 1, $"the game picker lists {p.Test(32)} games (open: {p.Test(31)})");
            if (p.Test(31) == LyBarTarget) ClickControl(t, p, 100, "VRUN NES in the game picker");
            ClickControl(t, p, 2000, "the song bar's facts button");
            t.Expect(p.Test(890) == 5 && p.Test(620) == 1, "the facts button did not show the Song view");
            t.Note($"loop position {f0} -> {f1} (per mille) after 2 s; '{SText(p, 0)}'");
            p.HideEditor();
        }
        finally { Win32Host.DestroyParent(parent); }
    }

    // ------------------------------------------------------------------------------------------------ the Runaway tree

    private string TreeName(PluginInstance p, int item)
    {
        var c = new char[64];
        fixed (char* cp = c) { long n = p.Test(897, item, (nint)cp); return n < 0 ? "<none>" : new string(c, 0, (int)Math.Min(n, 64)); }
    }

    private void RunawayTreeScenario(T t)
    {
        byte[] blob;
        var p = RomPlugin();
        var (_, parent) = LayoutEditor(p);
        try
        {
            p.Test(120, 0, 1);
            p.Test(46, 1);                         // the made-up sound memory (SpcSnapshot.TreeTestSample), Instrument Runaway on
            Win32Host.Pump(200);
            t.Expect(p.Test(895, 4) == 3 && p.Test(895, 0) == 1 && p.Test(895, 1) == 1 && p.Test(895, 2) == 0 && p.Test(895, 3) == 1,
                $"the tree has {p.Test(895, 4)} instruments: Samples {p.Test(895, 0)}, Drums {p.Test(895, 1)}, Game voices {p.Test(895, 2)}, Sound effects {p.Test(895, 3)} (expected 3: 1, 1, 0, 1)");
            t.Expect(p.Test(895, 5) == 1 && p.Test(896, 0) == 2, $"the duplicate is not folded ({p.Test(895, 5)} folded, the first row counts {p.Test(896, 0)})");
            t.Expect(p.Test(899, 4, 2) >= 20, $"the 500 ms one-shot's waveform has {p.Test(899, 4, 2)} columns with ink");
            t.Expect(p.Test(899, 2) >= 3, $"{p.Test(899, 2)} thumbnails drawn");

            // a click on the Drums row picks slot 2 for this channel
            long r1 = p.Test(898, 1);
            if (r1 >= 0 && ClickControl(t, p, LyTreeRow + (int)r1, "the Drums row")) { Win32Host.Pump(30); t.Expect(p.GetParam(PInstrument) == 2, $"the Drums row picked instrument {p.GetParam(PInstrument)}, expected slot 2"); }
            else t.Expect(false, "the Drums row is not drawn");
            // a group folds and unfolds
            long hd = p.Test(898, -2);                     // the Drums header
            int rows = (int)p.Test(895, 6);
            if (hd >= 0 && ClickControl(t, p, LyTreeRow + (int)hd, "the Drums header")) { Win32Host.Pump(30); t.Expect(p.Test(895, 9) == 1 && p.Test(895, 6) == rows - 1, $"folding Drums: folded {p.Test(895, 9)}, rows {rows} -> {p.Test(895, 6)}"); ClickControl(t, p, LyTreeRow + (int)hd, "the Drums header again"); Win32Host.Pump(30); }
            else t.Expect(false, "the Drums header is not drawn");
            // rename the picked row with typed keys
            r1 = p.Test(898, 1);
            if (r1 >= 0) ClickControl(t, p, LyTreeRow + (int)r1, "the Drums row");
            ClickControl(t, p, LyTreeRename, "Rename"); Win32Host.Pump(30);
            t.Expect(p.Test(895, 7) == 1, "Rename did not start typing");
            Win32Host.Text(p.EditorHandle, new string('\b', 30) + "Kick");
            Win32Host.Key(p.EditorHandle, 0x0D);
            Win32Host.Pump(30);
            t.Expect(p.Test(895, 7) == 0 && TreeName(p, 1) == "Kick", $"after typing the row is called '{TreeName(p, 1)}' (typing {p.Test(895, 7)})");
            // the play button: an audition sounds over this instance's output
            long r0 = p.Test(898, 0);
            if (r0 >= 0 && ClickControl(t, p, LyTreePlay + (int)r0, "the Samples row's play button"))
            {
                var mono = RenderBlocks(p, 0.5);
                double rms = Math.Sqrt(mono.Select(v => (double)v * v).Average());
                t.Expect(rms > 1e-3, $"the play button sounds nothing (rms {rms:0.000000}); the tree says '{TreeMessage(p)}'");
                t.Note($"audition rms {rms:0.0000}");
            }
            else t.Expect(false, "the Samples row's play button is not drawn");
            blob = p.SaveState();
            p.HideEditor();
        }
        finally { Win32Host.DestroyParent(parent); p.Dispose(); }

        // the name is in the project: a fresh instance that opens it shows it (the instance that saved it is gone first: a living one would share its emulator)
        using var p2 = lib.Create();
        p2.Dispatcher(Fpd.SetSampleRate, 0, 48000);
        p2.LoadState(blob);
        WaitForRom(p2);
        var (_, parent2) = LayoutEditor(p2);
        try
        {
            p2.Test(120, 0, 1);
            p2.Test(46, 1);
            Win32Host.Pump(200);
            t.Expect(TreeName(p2, 1) == "Kick", $"after reopening the project the Drums row is called '{TreeName(p2, 1)}', expected Kick");
            p2.HideEditor();
        }
        finally { Win32Host.DestroyParent(parent2); }
    }

    private static string TreeMessage(PluginInstance p)
    {
        var c = new char[200];
        fixed (char* cp = c) { long n = p.Test(899, 0, (nint)cp); return n < 0 ? "" : new string(c, 0, (int)Math.Min(n, 200)); }
    }
}
