// The editor's tabs (round 6): the tab strip and the window size on each tab, the Emulator tab unchanged, the Instruments tab (gates, list, fields, waveform, hex,
// corruption tools) and the Presets tab (library, search, buttons), all driven through the real editor with real mouse and key messages, against the demonstration
// models (Ui/StubModels.cs, installed by the test hook 403: the real models are other agents'). Test hooks: Plugin/BrokenNes2.Plugin/Ui/Bn2Editor.Tabs.cs (400 - 499).
//
// "ui" runs every scenario. "ui-proof" runs each check again with a deliberate fault switched on (hook 499, the UiMutate switches in the plugin) and demands
// that the check FAILS: a check that cannot fail would say nothing. The faults are single tests of a mask at the place each check watches; the mask is zero in normal use.
using BrokenNes.Fruity;

namespace BrokenNes.FruityHost;

public sealed unsafe partial class SelfTest
{
    // ---- ids of the tabs (Ui/UiIds.cs) ----
    private const int UiTab0 = 1000, UiTab1 = 1001, UiTab2 = 1002;
    private const int UiInstList = 1100, UiInstListBar = 1140, UiBlobChip = 1160, UiWave = 1170, UiHex = 1171, UiLockBlob = 1173;
    private const int UiStrength = 1180, UiRandomise = 1181, UiNudgeDown = 1182, UiNudgeUp = 1183, UiLockAll = 1184, UiUnlockAll = 1185, UiUndo = 1187, UiRedo = 1188, UiSendToPresets = 1189, UiPush = 1190;
    private const int UiSendName = 1191, UiSendOk = 1192, UiSendCancel = 1193;
    private const int UiFieldBase = 1200, UiFieldBits = 1300;
    private const int UiPresetList = 1600, UiSearch = 1650, UiSearchClear = 1651, UiFilter = 1655, UiLoad = 1670, UiRename = 1671, UiDelete = 1672, UiExport = 1673, UiImport = 1674, UiAudition = 1675;
    private const int WideExtra = 420;

    // every control the Emulator tab draws in its default state (NES, Direct, Pulse 1): id, x, y, w, h, captured from the plugin as it was BEFORE the tabs existed
    // (FruityHost editor-golden on the unmodified DLL), so a layout that moved by a pixel fails
    private static readonly (int Id, int X, int Y, int W, int H)[] EmulatorGolden =
    [
        (1, 642, 94, 246, 28),
        (2, 642, 162, 123, 28),
        (3, 765, 162, 123, 28),
        (4, 642, 196, 246, 28),
        (7, 642, 230, 246, 28),
        (9, 642, 386, 246, 28),
        (10, 556, 286, 61, 56),
        (11, 623, 286, 61, 56),
        (12, 690, 286, 61, 56),
        (13, 757, 286, 61, 56),
        (21, 636, 450, 172, 28),
        (22, 636, 480, 172, 28),
        (23, 636, 510, 172, 28),
        (24, 636, 540, 172, 28),
        (25, 636, 570, 172, 28),
        (40, 814, 13, 74, 26),
        (50, 642, 128, 49, 28),
        (51, 691, 128, 49, 28),
        (52, 740, 128, 49, 28),
        (53, 789, 128, 49, 28),
        (54, 838, 128, 49, 28),
        (60, 738, 618, 150, 30),
        (80, 16, 618, 76, 30),
        (500, 98, 618, 100, 30),
    ];

    private sealed class UiCtx : IDisposable
    {
        public readonly PluginInstance P;
        public readonly nint Parent;
        public nint Hwnd => P.EditorHandle;
        public UiCtx(PluginInstance p, nint parent) { P = p; Parent = parent; }
        public void Dispose()
        {
            try { P.Test(499, 0, 0); P.Test(403, 0, 0); } catch (Exception) { }
            try { P.HideEditor(); } catch (Exception) { }
            Win32Host.DestroyParent(Parent);
            P.Dispose();
        }
    }

    private delegate void UiCheck(T t, UiCtx c);
    private sealed record UiScenario(string Name, bool Rom, bool Demo, bool Freeze, UiCheck Body);

    private UiCtx OpenUi(bool rom, bool demo, bool freeze)
    {
        var p = rom ? RomPlugin() : lib.Create();
        p.Dispatcher(Fpd.SetSampleRate, 0, 48000);
        if (!rom) p.SetParam(PChannel, 1);                          // Pulse 1: the state the golden layout was taken in
        nint parent = Win32Host.CreateParent();
        p.ShowEditor(parent);
        p.Host.EditorResized = () => Win32Host.FitFrame(parent, p.EditorHandle);      // what FL does when a plugin says its editor was resized
        Win32Host.Pump(120);
        p.Test(403, 0, demo ? 1 : 2);                                // the demonstration models, or none at all (whatever the real models are, the scenario sees only this)
        if (freeze) { p.Test(47, 0, 1); Win32Host.Pump(150); }
        return new UiCtx(p, parent);
    }

    // ------------------------------------------------------------------------------------------------ helpers

    private static (int W, int H) Unpack(long v) => ((int)(v & 0xFFFF), (int)((v >> 16) & 0xFFFF));

    private static string UiText(PluginInstance p, int field)
    {
        long len = p.Test(412, field << 16, 0);
        if (len < 0) return "<none>";
        if (len == 0) return "";
        var buf = new char[len];
        fixed (char* b = buf) p.Test(412, (field << 16) | (int)len, (nint)b);
        return new string(buf);
    }

    private static (int X, int Y, int W, int H)? UiRect(UiCtx c, int id) { long r = c.P.Test(30, id); return r == 0 ? null : RectOf(r); }

    private bool UiClick(T t, UiCtx c, int id, string what, double fx = 0.5, double fy = 0.5, bool shift = false)
    {
        var r = UiRect(c, id);
        if (r == null) { t.Expect(false, $"{what}: control {id} is not on screen"); return false; }
        int x = r.Value.X + (int)(r.Value.W * fx), y = r.Value.Y + (int)(r.Value.H * fy);
        if (shift) Win32Host.ShiftClick(c.Hwnd, x, y); else Win32Host.Click(c.Hwnd, x, y);
        return true;
    }

    private static int CountRgb(byte[] bgra, int imgW, (int X, int Y, int W, int H) r, int red, int green, int blue)
    {
        int n = 0;
        for (int y = r.Y; y < r.Y + r.H; y++)
            for (int x = r.X; x < r.X + r.W; x++)
            {
                int o = (y * imgW + x) * 4;
                if (o + 2 < bgra.Length && bgra[o] == blue && bgra[o + 1] == green && bgra[o + 2] == red) n++;
            }
        return n;
    }

    private static long HashRect(byte[] bgra, int imgW, (int X, int Y, int W, int H) r)
    {
        long h = 17;
        for (int y = r.Y; y < r.Y + r.H; y++)
            for (int x = r.X; x < r.X + r.W; x++)
            {
                int o = (y * imgW + x) * 4;
                if (o + 2 < bgra.Length) h = h * 1000003 + (bgra[o] | (bgra[o + 1] << 8) | (bgra[o + 2] << 16));
            }
        return h;
    }

    private static bool Overlap((int X, int Y, int W, int H) a, (int X, int Y, int W, int H) b) => a.X < b.X + b.W && b.X < a.X + a.W && a.Y < b.Y + b.H && b.Y < a.Y + a.H;

    private void UiTabTo(T t, UiCtx c, int tabId, string what) { UiClick(t, c, tabId, what); Win32Host.Pump(30); }

    private void UiSelectInstrument(T t, UiCtx c, int row) { for (int guard = 0; guard < 20 && c.P.Test(420, 0) > row; guard++) Win32Host.WheelAt(c.Hwnd, 5, 600, 200); UiClick(t, c, UiInstList + (int)(row - c.P.Test(420, 0)), $"instrument row {row}"); }

    // ------------------------------------------------------------------------------------------------ the scenarios

    private UiScenario[] UiSuite() =>
    [
        new("tabs", false, false, false, CheckTabs),
        new("emulator-unchanged", false, false, false, CheckEmulatorUnchanged),
        new("gate-direct", false, true, false, CheckGateDirect),
        new("gate-runaway", true, false, false, CheckGateRunaway),
        new("inst-list", true, true, true, CheckInstList),
        new("inst-select", true, true, true, CheckInstSelect),
        new("inst-fields-scroll", true, true, true, CheckFieldsScroll),
        new("inst-slider", true, true, true, CheckSlider),
        new("inst-stepper", true, true, true, CheckStepper),
        new("inst-bits", true, true, true, CheckBits),
        new("inst-locks", true, true, true, CheckLocks),
        new("inst-wave", true, true, true, CheckWave),
        new("inst-hex", true, true, true, CheckHex),
        new("inst-tools", true, true, true, CheckTools),
        new("inst-undo", true, true, true, CheckUndoRedo),
        new("inst-send-push", true, true, true, CheckSendPush),
        new("model-failure", true, true, true, CheckModelFailure),
        new("presets-nolib", false, false, false, CheckPresetsNoLibrary),
        new("presets-list", false, true, false, CheckPresetsList),
        new("presets-search", false, true, false, CheckPresetsSearch),
        new("presets-buttons", false, true, false, CheckPresetsButtons),
        new("keyboard-guard", false, true, false, CheckKeyboardGuard),
    ];

    private List<string> RunUi(UiScenario s, int fault)
    {
        var t = new T();
        UiCtx? c = null;
        try
        {
            c = OpenUi(s.Rom, s.Demo, s.Freeze);
            if (s.Freeze && c.P.Test(45) != 1) t.Expect(false, "Instrument Runaway did not come on for the test game");
            if (fault != 0) c.P.Test(499, 0, fault);
            s.Body(t, c);
        }
        catch (Exception e) { t.Failures.Add($"exception {e.GetType().Name}: {e.Message} @ {e.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}"); }
        finally { c?.Dispose(); }
        return t.Failures.Select(f => $"{s.Name}: {f}").ToList();
    }

    private void UiTabsTest(T t)
    {
        int n = 0;
        foreach (var s in UiSuite())
        {
            var failures = RunUi(s, 0);
            t.Failures.AddRange(failures);
            n++;
        }
        t.Note($"{n} scenarios");
    }

    /// <summary>(scenario name, fault switch from Ui/UiIds.cs UiMutate, why that fault must be caught).</summary>
    private static readonly (string Scenario, int Fault, string Why)[] UiFaults =
    [
        ("tabs", 1 << 0, "switching tabs leaves the window as it is"),
        ("tabs", 1 << 1, "the host is not told about the resize"),
        ("emulator-unchanged", 1 << 9, "the Emulator tab's column is drawn one pixel lower"),
        ("gate-runaway", 1 << 14, "the Instruments tab ignores that Instrument Runaway is off"),
        ("inst-list", 1 << 2, "the wheel does not scroll lists"),
        ("inst-select", 1 << 8, "a click selects the next instrument"),
        ("inst-fields-scroll", 1 << 13, "the fields table does not scroll"),
        ("inst-slider", 1 << 3, "a slider writes its value + 1"),
        ("inst-stepper", 1 << 4, "the stepper's plus subtracts"),
        ("inst-bits", 1 << 5, "a bit toggle does not flip"),
        ("inst-wave", 1 << 6, "the waveform view draws nothing"),
        ("inst-hex", 1 << 10, "typing in the hex field writes nothing"),
        ("inst-undo", 1 << 12, "Undo calls Redo"),
        ("presets-list", 1 << 2, "the wheel does not scroll lists"),
        ("presets-search", 1 << 11, "the text input ignores characters"),
        ("presets-buttons", 1 << 7, "Export calls Import"),
        ("model-failure", 1 << 16, "a model that throws is swallowed without a word on the tab"),
        ("keyboard-guard", 1 << 15, "typing in a text field still presses the game's buttons"),
    ];

    private void UiProofTest(T t)
    {
        var suite = UiSuite().ToDictionary(s => s.Name);
        int caught = 0;
        foreach (var (name, fault, why) in UiFaults)
        {
            var failures = RunUi(suite[name], fault);
            if (failures.Count == 0) t.Expect(false, $"{name} did not fail with the fault '{why}' switched on: that check cannot fail");
            else { caught++; t.Note($"fault '{why}' -> {failures.Count} failure(s), first: {failures[0]}"); }
        }
        // the checks that have no fault switch are shown able to fail by asking them a question whose answer is false
        foreach (var (name, rom, check) in new (string, bool, UiCheck)[] { ("presets-nolib while a library is installed", false, CheckPresetsNoLibrary), ("gate-direct while a game is loaded", true, CheckGateDirect) })
        {
            var tt = new T();
            using (var c = OpenUi(rom, true, false)) { try { check(tt, c); } catch (Exception e) { tt.Failures.Add(e.Message); } }
            if (tt.Failures.Count == 0) t.Expect(false, $"negative control '{name}' did not fail");
            else { caught++; t.Note($"negative control '{name}' -> first failure: {tt.Failures[0]}"); }
        }
        t.Note($"{caught} of {UiFaults.Length + 2} faults and negative controls were caught by their checks");
    }

    // ------------------------------------------------------------------------------------------------ tabs, sizes, the Emulator tab

    private void ExpectGolden(T t, UiCtx c, string when)
    {
        foreach (var (id, x, y, w, h) in EmulatorGolden)
        {
            var r = UiRect(c, id);
            t.Expect(r != null && r.Value == (x, y, w, h), $"{when}: control {id} is at {(r == null ? "nowhere" : $"{r.Value.X},{r.Value.Y} {r.Value.W}x{r.Value.H}")}, expected {x},{y} {w}x{h}");
        }
    }

    private void CheckTabs(T t, UiCtx c)
    {
        var p = c.P;
        t.Expect(p.Test(400) == 0, "the editor does not open on the Emulator tab");
        t.Expect(Unpack(p.Test(401)) == (904, 664), $"the Emulator tab is {Unpack(p.Test(401))}, expected 904 x 664");
        // the strip: three tabs on the header rule, clear of the About button and of every control the Emulator tab has
        var tabs = new[] { UiTab0, UiTab1, UiTab2 }.Select(id => UiRect(c, id)).ToArray();
        t.Expect(tabs.All(r => r != null), "the tab strip is not drawn");
        if (tabs.All(r => r != null))
        {
            var about = UiRect(c, 40)!.Value;
            foreach (var r in tabs)
            {
                t.Expect(!Overlap(r!.Value, about), "a tab overlaps the About button");
                t.Expect(r!.Value.X >= 556 && r.Value.Y >= 30 && r.Value.Y + r.Value.H <= 57, $"a tab is outside the strip's place ({r.Value})");
                foreach (var g in EmulatorGolden) t.Expect(!Overlap(r.Value, (g.X, g.Y, g.W, g.H)), $"a tab overlaps control {g.Id}");
            }
        }
        ExpectGolden(t, c, "before any tab");
        t.Expect(UiRect(c, UiInstList) == null && UiRect(c, UiPresetList) == null, "the other tabs' controls are on screen on the Emulator tab");

        int calls = p.Host.EditorResizedCalls;
        UiTabTo(t, c, UiTab1, "Instruments tab");
        t.Expect(p.Test(400) == 1, "clicking Instruments did not switch to it");
        t.Expect(Unpack(p.Test(401)) == (904 + WideExtra, 664), $"the Instruments tab is {Unpack(p.Test(401))}, expected {904 + WideExtra} x 664");
        t.Expect(p.Host.EditorResizedCalls == calls + 1, $"the host was told {p.Host.EditorResizedCalls - calls} times that the editor was resized (expected once)");
        int wide = Win32Host.ClientSize(c.Parent).W;
        t.Expect(wide >= 904 + WideExtra - 4, $"the host's frame did not grow to hold the wide editor (client {wide})");
        t.Expect(UiRect(c, 60) == null && UiRect(c, 21) == null && UiRect(c, 1) == null, "the Emulator tab's controls (Reset Console, sliders, dropdowns) are still on the Instruments tab");
        t.Expect(UiRect(c, 80) != null && UiRect(c, 500) != null, "the Inputs and Corruption buttons (left column) disappeared on the Instruments tab");
        var about2 = UiRect(c, 40)!.Value;
        t.Expect(about2.X + about2.W <= 904 + WideExtra, "the About button is outside the wide window");

        UiTabTo(t, c, UiTab2, "Presets tab");
        t.Expect(p.Test(400) == 2 && Unpack(p.Test(401)) == (904 + WideExtra, 664), "the Presets tab is not wide");
        t.Expect(p.Host.EditorResizedCalls == calls + 1, "going from one wide tab to the other told the host again although the size did not change");

        UiTabTo(t, c, UiTab0, "Emulator tab");
        t.Expect(p.Test(400) == 0 && Unpack(p.Test(401)) == (904, 664), $"back on the Emulator tab the window is {Unpack(p.Test(401))}, expected 904 x 664");
        t.Expect(p.Host.EditorResizedCalls == calls + 2, "the host was not told when the editor went back to its own size");
        int narrow = Win32Host.ClientSize(c.Parent).W;
        t.Expect(wide - narrow == WideExtra, $"the host's frame changed by {wide - narrow} pixels between the tabs, expected {WideExtra}");
        ExpectGolden(t, c, "after visiting the other tabs");

        // a modal opened on a wide tab stays inside the window and closes
        UiTabTo(t, c, UiTab2, "Presets tab");
        UiClick(t, c, 40, "About button");
        t.Expect(p.Test(36) == 1, "the About window did not open from a wide tab");
        var close = UiRect(c, 41);
        t.Expect(close != null && close.Value.X + close.Value.W <= 904 + WideExtra, "the About window's Close button is outside the wide window");
        UiClick(t, c, 41, "About Close");
        t.Expect(p.Test(36) == 0, "the About window did not close");
        UiTabTo(t, c, UiTab0, "Emulator tab");
    }

    private void CheckEmulatorUnchanged(T t, UiCtx c)
    {
        var p = c.P;
        ExpectGolden(t, c, "default state");
        Win32Host.MouseMove(c.Hwnd, 20, 20);
        Win32Host.Pump(60);
        var before = Win32Host.CapturePixels(c.Hwnd, out int w, out int h);
        t.Expect(w == 904 && h == 664, $"the Emulator tab's picture is {w} x {h}");
        // a round trip through both tabs leaves not one pixel of the Emulator tab different
        UiTabTo(t, c, UiTab1, "Instruments"); UiTabTo(t, c, UiTab2, "Presets"); UiTabTo(t, c, UiTab0, "Emulator");
        Win32Host.MouseMove(c.Hwnd, 20, 20);
        Win32Host.Pump(60);
        var after = Win32Host.CapturePixels(c.Hwnd, out w, out h);
        int diff = 0;
        if (before.Length == after.Length) for (int i = 0; i < before.Length; i += 4) if (before[i] != after[i] || before[i + 1] != after[i + 1] || before[i + 2] != after[i + 2]) diff++;
        t.Expect(before.Length == after.Length && diff == 0, $"{diff} pixels of the Emulator tab changed after a visit to the other tabs");
        ExpectGolden(t, c, "after the round trip");
    }

    private void CheckGateDirect(T t, UiCtx c)
    {
        UiTabTo(t, c, UiTab1, "Instruments");
        Win32Host.Pump(60);
        t.Expect(UiText(c.P, 2) == "Load a game in ROM mode, then switch Instrument Runaway on to see its instruments", $"in Direct mode the Instruments tab says '{UiText(c.P, 2)}'");
        t.Expect(UiRect(c, UiInstList) == null && UiRect(c, UiRandomise) == null, "the Instruments tab shows controls although there is no game");
    }

    private void CheckGateRunaway(T t, UiCtx c)
    {
        var p = c.P;
        UiTabTo(t, c, UiTab1, "Instruments");
        p.Test(403, 0, 1);                                         // models connected, Runaway still off
        Win32Host.Pump(80);
        t.Expect(UiText(p, 2) == "Switch Instrument Runaway on to see the game's instruments", $"with Runaway off the Instruments tab says '{UiText(p, 2)}'");
        t.Expect(UiRect(c, UiInstList) == null, "the instrument list is shown although Instrument Runaway is off");
        p.Test(47, 0, 1);                                          // Runaway on: the list appears
        Win32Host.Pump(150);
        t.Expect(p.Test(45) == 1, "Instrument Runaway did not come on");
        t.Expect(UiText(p, 2) == "" && UiRect(c, UiInstList) != null && p.Test(420, 1) == 30, $"with Runaway on the list is not shown ({UiText(p, 2)}, {p.Test(420, 1)} rows)");
        p.Test(403, 0, 2);                                         // models gone: a message, not an empty list
        Win32Host.Pump(150);
        t.Expect(UiText(p, 2) == "The instrument model is not connected in this build of BrokenNes2.", $"without a model the tab says '{UiText(p, 2)}'");
        p.Test(47, 0, 0);                                          // Runaway off again: back to the first message
        p.Test(403, 0, 1);
        Win32Host.Pump(150);
        t.Expect(UiText(p, 2) == "Switch Instrument Runaway on to see the game's instruments", "releasing Runaway did not bring the first message back");
    }

    // ------------------------------------------------------------------------------------------------ the Instruments tab

    private void CheckInstList(T t, UiCtx c)
    {
        var p = c.P;
        UiTabTo(t, c, UiTab1, "Instruments");
        t.Expect(p.Test(420, 1) == 30 && p.Test(420, 0) == 0, $"the list has {p.Test(420, 1)} rows from {p.Test(420, 0)}, expected 30 from 0");
        var row0 = UiRect(c, UiInstList);
        t.Expect(row0 != null && UiRect(c, UiInstList + 17) != null && UiRect(c, UiInstList + 18) == null, "the list does not show exactly 18 rows");
        if (row0 == null) return;
        int x = row0.Value.X + 20, y = row0.Value.Y + 20;
        Win32Host.WheelAt(c.Hwnd, -2, x, y);
        t.Expect(p.Test(420, 0) == 6, $"two wheel notches down moved the list to row {p.Test(420, 0)}, expected 6");
        Win32Host.WheelAt(c.Hwnd, 5, x, y);
        t.Expect(p.Test(420, 0) == 0, $"the wheel up did not bring the list back to the top ({p.Test(420, 0)})");
        Win32Host.WheelAt(c.Hwnd, -20, x, y);
        t.Expect(p.Test(420, 0) == 12, $"the list scrolled to {p.Test(420, 0)}, the end is row 12 (30 rows, 18 visible)");
        Win32Host.WheelAt(c.Hwnd, 20, x, y);
        // the scroll bar: drag the thumb down and up, and a click in the track pages
        var bar = UiRect(c, UiInstListBar);
        t.Expect(bar != null, "the list has no scroll bar");
        if (bar == null) return;
        int bx = bar.Value.X + bar.Value.W / 2;
        Win32Host.Drag(c.Hwnd, bx, bar.Value.Y + 6, bx, bar.Value.Y + bar.Value.H - 1);
        t.Expect(p.Test(420, 0) == 12, $"dragging the thumb to the bottom left the list at row {p.Test(420, 0)}");
        bar = UiRect(c, UiInstListBar)!;
        Win32Host.Drag(c.Hwnd, bx, bar.Value.Y + bar.Value.H - 6, bx, bar.Value.Y - 20);
        t.Expect(p.Test(420, 0) == 0, $"dragging the thumb back to the top left the list at row {p.Test(420, 0)}");
        Win32Host.Click(c.Hwnd, bx, bar.Value.Y + bar.Value.H - 4);
        t.Expect(p.Test(420, 0) == 12, $"a click in the track below the thumb moved the list to {p.Test(420, 0)}, expected a page down to the end (12)");
    }

    private void CheckInstSelect(T t, UiCtx c)
    {
        var p = c.P;
        UiTabTo(t, c, UiTab1, "Instruments");
        t.Expect(UiText(p, 9) == "Sample 00" && UiText(p, 100) == "Volume left=60", $"the first instrument shows '{UiText(p, 9)}' / '{UiText(p, 100)}'");
        var row0 = UiRect(c, UiInstList)!.Value;
        Win32Host.WheelAt(c.Hwnd, -2, row0.X + 20, row0.Y + 20);       // the list now starts at row 6
        UiClick(t, c, UiInstList + 4, "list row 4");
        t.Expect(p.Test(420, 2) == 10 && UiText(p, 9) == "Sample 0A", $"clicking the fifth visible row selected instrument {p.Test(420, 2)} ('{UiText(p, 9)}'), expected 10 (Sample 0A)");
        t.Expect(UiText(p, 100) == "Volume left=70", $"the fields do not show the selected instrument's own values ('{UiText(p, 100)}', expected Volume left=70)");
        UiClick(t, c, UiInstList + 1, "list row 1");
        t.Expect(p.Test(420, 2) == 7 && UiText(p, 100) == "Volume left=67", $"selecting instrument 7: '{UiText(p, 9)}' / '{UiText(p, 100)}'");
        for (int i = 0; i < 7; i++) t.Expect(UiText(p, 100 + i).Length > 0, $"field row {i} is empty");
        t.Expect(UiText(p, 103) == "Loop=loops" && UiText(p, 106) == "Voice flags=$21", $"enum and bit-field rows read '{UiText(p, 103)}' / '{UiText(p, 106)}'");
    }

    private void CheckFieldsScroll(T t, UiCtx c)
    {
        var p = c.P;
        UiTabTo(t, c, UiTab1, "Instruments");
        t.Expect(p.Test(420, 4) == 12 && p.Test(420, 3) == 0, $"{p.Test(420, 4)} fields from row {p.Test(420, 3)}");
        var s = UiRect(c, UiFieldBase)!.Value;
        Win32Host.WheelAt(c.Hwnd, -2, s.X + 20, s.Y + 10);
        t.Expect(p.Test(420, 3) == 2, $"two wheel notches over the fields moved them to row {p.Test(420, 3)}, expected 2");
        t.Expect(UiText(p, 100).StartsWith("Tuning"), $"the first visible field after scrolling is '{UiText(p, 100)}'");
        Win32Host.WheelAt(c.Hwnd, -9, s.X + 20, s.Y + 10);
        t.Expect(p.Test(420, 3) == 5, $"the fields scrolled to {p.Test(420, 3)}, the end is row 5 (12 fields, 7 visible)");
        Win32Host.WheelAt(c.Hwnd, 9, s.X + 20, s.Y + 10);
        t.Expect(p.Test(420, 3) == 0, "the wheel up did not bring the fields back");
        // a field below the fold can be edited after scrolling to it
        Win32Host.WheelAt(c.Hwnd, -9, s.X + 20, s.Y + 10);
        t.Expect(UiText(p, 106).StartsWith("Pitch base"), $"the last row is '{UiText(p, 106)}', expected Pitch base");
    }

    private void CheckSlider(T t, UiCtx c)
    {
        var p = c.P;
        UiTabTo(t, c, UiTab1, "Instruments");
        int before = (int)p.Test(410, 1);
        UiClick(t, c, UiFieldBase + 0, "Volume left slider");            // the middle of the track of a 0..127 field: 64 exactly
        t.Expect(p.Test(410, 1) == before + 1, "the slider click did not reach the model");
        t.Expect(p.Test(410, 12, 0) == 64, $"clicking the middle of a 0..127 slider wrote {p.Test(410, 12, 0)}, expected 64");
        t.Expect(UiText(p, 100) == "Volume left=64", $"the row still reads '{UiText(p, 100)}'");
        var r = UiRect(c, UiFieldBase + 0)!.Value;
        int y = r.Y + r.H / 2;
        Win32Host.Drag(c.Hwnd, r.X + r.W / 2, y, r.X + r.W - 3, y);
        t.Expect(p.Test(410, 12, 0) >= 124, $"dragging to the right end wrote {p.Test(410, 12, 0)}");
        Win32Host.Drag(c.Hwnd, r.X + r.W / 2, y, r.X, y);
        t.Expect(p.Test(410, 12, 0) <= 2, $"dragging to the left end wrote {p.Test(410, 12, 0)}");
        // a signed field has its zero in the middle
        var tune = UiRect(c, UiFieldBase + 2 * 8)!.Value;
        Win32Host.Click(c.Hwnd, tune.X + tune.W / 2, tune.Y + tune.H / 2);
        t.Expect(Math.Abs(p.Test(410, 12, 2)) <= 1, $"the middle of a signed slider wrote {p.Test(410, 12, 2)}, expected about 0");
        // the strength slider (0..100): the middle is 50 exactly
        UiClick(t, c, UiStrength, "Strength slider");
        t.Expect(p.Test(420, 9) == 50, $"the middle of the strength slider is {p.Test(420, 9)}, expected 50");
    }

    private void CheckStepper(T t, UiCtx c)
    {
        var p = c.P;
        UiTabTo(t, c, UiTab1, "Instruments");
        int minus = UiFieldBase + 1 * 8 + 1, plus = UiFieldBase + 1 * 8 + 2;        // Volume right: 100
        t.Expect(p.Test(410, 12, 1) == 100, "Volume right does not start at 100");
        UiClick(t, c, plus, "plus"); t.Expect(p.Test(410, 12, 1) == 101, $"plus made it {p.Test(410, 12, 1)}");
        UiClick(t, c, plus, "plus"); UiClick(t, c, minus, "minus"); t.Expect(p.Test(410, 12, 1) == 101, $"plus, plus, minus made it {p.Test(410, 12, 1)}, expected 101");
        UiClick(t, c, plus, "shift plus", shift: true); t.Expect(p.Test(410, 12, 1) == 111, $"shift plus made it {p.Test(410, 12, 1)}, expected 111 (steps of ten)");
        UiClick(t, c, minus, "shift minus", shift: true); UiClick(t, c, minus, "minus");
        t.Expect(p.Test(410, 12, 1) == 100, $"shift minus and minus made it {p.Test(410, 12, 1)}, expected 100");
        // the enum: the buttons step, the box in between steps forward and wraps
        int eminus = UiFieldBase + 3 * 8 + 1, emid = UiFieldBase + 3 * 8 + 0;      // Loop: loops (1)
        UiClick(t, c, eminus, "enum minus"); t.Expect(p.Test(410, 12, 3) == 0 && UiText(p, 103) == "Loop=one-shot", $"enum minus: {p.Test(410, 12, 3)} / '{UiText(p, 103)}'");
        UiClick(t, c, emid, "enum box"); t.Expect(p.Test(410, 12, 3) == 1, $"the box stepped the enum to {p.Test(410, 12, 3)}, expected 1");
        UiClick(t, c, emid, "enum box"); t.Expect(p.Test(410, 12, 3) == 0, $"the box did not wrap the enum ({p.Test(410, 12, 3)})");
        // the ends: plus at the top changes nothing (no model call)
        int calls = (int)p.Test(410, 1);
        UiClick(t, c, UiFieldBase + 3 * 8 + 2, "enum plus"); UiClick(t, c, UiFieldBase + 3 * 8 + 2, "enum plus");
        t.Expect(p.Test(410, 12, 3) == 1 && p.Test(410, 1) == calls + 1, "plus at the top of an enum should step once, then do nothing");
    }

    private void CheckBits(T t, UiCtx c)
    {
        var p = c.P;
        UiTabTo(t, c, UiTab1, "Instruments");
        int slot6 = UiFieldBits + 6 * 16;                                          // Voice flags: 0x21 = bits 0 and 5
        t.Expect(p.Test(410, 12, 6) == 0x21, "Voice flags do not start at $21");
        UiClick(t, c, slot6 + 1, "bit 1"); t.Expect(p.Test(410, 12, 6) == 0x23, $"setting bit 1 gave ${p.Test(410, 12, 6):X2}, expected $23");
        UiClick(t, c, slot6 + 5, "bit 5"); t.Expect(p.Test(410, 12, 6) == 0x03, $"clearing bit 5 gave ${p.Test(410, 12, 6):X2}, expected $03");
        UiClick(t, c, slot6 + 7, "bit 7"); t.Expect(p.Test(410, 12, 6) == 0x83, $"setting bit 7 gave ${p.Test(410, 12, 6):X2}, expected $83");
        t.Expect(UiText(p, 106) == "Voice flags=$83", $"the row reads '{UiText(p, 106)}'");
        t.Expect(UiRect(c, slot6 + 8) == null, "an 8-bit field shows a ninth toggle");
        // the 14-bit field (Pitch base) has fourteen toggles
        var s = UiRect(c, UiFieldBase)!.Value;
        Win32Host.WheelAt(c.Hwnd, -9, s.X + 20, s.Y + 10);
        int last = UiFieldBits + 6 * 16;
        t.Expect(UiRect(c, last + 13) != null && UiRect(c, last + 14) == null, "the 14-bit Pitch base field does not show exactly 14 toggles");
    }

    private void CheckLocks(T t, UiCtx c)
    {
        var p = c.P;
        UiTabTo(t, c, UiTab1, "Instruments");
        UiClick(t, c, UiFieldBase + 3, "lock of the first field");
        t.Expect(p.Test(410, 14, 0) == 1 && p.Test(410, 2) == 1, "the lock toggle did not reach the model");
        UiClick(t, c, UiFieldBase + 3, "lock of the first field");
        t.Expect(p.Test(410, 14, 0) == 0, "the second click did not unlock");
        UiClick(t, c, UiLockAll, "Lock all");
        bool all = true; for (int f = 0; f < 12; f++) all &= p.Test(410, 14, f) == 1;
        t.Expect(all, "Lock all did not lock every field");
        UiClick(t, c, UiUnlockAll, "Unlock all");
        bool none = true; for (int f = 0; f < 12; f++) none &= p.Test(410, 14, f) == 0;
        t.Expect(none, "Unlock all did not unlock every field");
        // the locks are honoured by Randomise (the model's side, reached through the tab)
        UiClick(t, c, UiLockAll, "Lock all");
        var values = Enumerable.Range(0, 12).Select(f => p.Test(410, 12, f)).ToArray();
        UiClick(t, c, UiStrength, "Strength"); Win32Host.Click(c.Hwnd, UiRect(c, UiStrength)!.Value.X + UiRect(c, UiStrength)!.Value.W - 5, UiRect(c, UiStrength)!.Value.Y + 10);
        UiClick(t, c, UiRandomise, "Randomise (all locked)");
        t.Expect(Enumerable.Range(0, 12).All(f => p.Test(410, 12, f) == values[f]), "Randomise changed a locked field");
        UiClick(t, c, UiUnlockAll, "Unlock all");
        UiClick(t, c, UiRandomise, "Randomise (unlocked)");
        t.Expect(Enumerable.Range(0, 12).Any(f => p.Test(410, 12, f) != values[f]), "Randomise at full strength changed nothing");
        // the sample data's own lock
        UiClick(t, c, UiLockBlob, "Lock sample data");
        t.Expect(UiText(p, 3).Contains("locked"), $"locking the sample data said '{UiText(p, 3)}'");
    }

    private void CheckWave(T t, UiCtx c)
    {
        var p = c.P;
        UiTabTo(t, c, UiTab1, "Instruments");
        Win32Host.MouseMove(c.Hwnd, 20, 20);
        Win32Host.Pump(60);
        var rect = UiRect(c, UiWave);
        t.Expect(rect != null, "no waveform view");
        if (rect == null) return;
        var shot = Win32Host.CapturePixels(c.Hwnd, out int w, out _);
        int wave = CountRgb(shot, w, rect.Value, 86, 170, 255), marks = CountRgb(shot, w, rect.Value, 255, 98, 76);
        t.Expect(wave > 400, $"the waveform view of a 600-sample blob drew {wave} wave pixels (expected hundreds)");
        t.Expect(marks >= 60, $"the loop markers drew {marks} pixels (two vertical lines of about 52)");
        t.Expect(p.Test(420, 8) > 400, $"the view reports {p.Test(420, 8)} columns drawn");
        // a blob with no bytes draws nothing
        UiSelectInstrument(t, c, 2);
        var shot2 = Win32Host.CapturePixels(c.Hwnd, out w, out _);
        int wave2 = CountRgb(shot2, w, UiRect(c, UiWave)!.Value, 86, 170, 255);
        t.Expect(wave2 == 0, $"an empty blob drew {wave2} wave pixels");
        // an instrument with two blobs: the chips switch between them and the picture changes
        UiSelectInstrument(t, c, 1);
        t.Expect(UiRect(c, UiBlobChip) != null && UiRect(c, UiBlobChip + 1) != null && UiRect(c, UiBlobChip + 2) == null, "instrument 1 should show two blob chips");
        var a = Win32Host.CapturePixels(c.Hwnd, out w, out _);
        long ha = HashRect(a, w, UiRect(c, UiWave)!.Value);
        UiClick(t, c, UiBlobChip + 1, "second blob chip");
        t.Expect(p.Test(420, 7) == 1, "the second chip did not select the second blob");
        var b = Win32Host.CapturePixels(c.Hwnd, out w, out _);
        t.Expect(HashRect(b, w, UiRect(c, UiWave)!.Value) != ha, "the waveform did not change when the other blob was chosen");
    }

    private void CheckHex(T t, UiCtx c)
    {
        var p = c.P;
        UiTabTo(t, c, UiTab1, "Instruments");
        var hex = UiRect(c, UiHex)!.Value;
        Win32Host.MouseMove(c.Hwnd, 20, 20);
        var before = Win32Host.CapturePixels(c.Hwnd, out int w, out _);
        long hw0 = HashRect(before, w, UiRect(c, UiWave)!.Value);
        Win32Host.Click(c.Hwnd, hex.X + 54, hex.Y + 11);                              // the first byte
        t.Expect(p.Test(420, 5) == 0 && p.Test(420, 10) == 1, $"clicking the first byte selected {p.Test(420, 5)}, keyboard taken: {p.Test(420, 10)}");
        Win32Host.Text(c.Hwnd, "FF");
        t.Expect(p.Test(410, 13, 0) == 0xFF, $"typing FF over byte 0 wrote ${p.Test(410, 13, 0):X2}");
        t.Expect(p.Test(420, 5) == 1, $"after a byte the selection is {p.Test(420, 5)}, expected 1");
        var after = Win32Host.CapturePixels(c.Hwnd, out w, out _);
        t.Expect(HashRect(after, w, UiRect(c, UiWave)!.Value) != hw0, "editing a byte did not change the waveform");
        Win32Host.Text(c.Hwnd, "ab");
        t.Expect(p.Test(410, 13, 1) == 0xAB, $"lower-case hex wrote ${p.Test(410, 13, 1):X2}, expected $AB");
        Win32Host.Key(c.Hwnd, Win32Host.VkLeft); Win32Host.Key(c.Hwnd, Win32Host.VkLeft);
        t.Expect(p.Test(420, 5) == 0, $"two Left keys from byte 2 selected {p.Test(420, 5)}");
        Win32Host.Key(c.Hwnd, Win32Host.VkDown);
        t.Expect(p.Test(420, 5) == 16, $"Down selected {p.Test(420, 5)}, expected 16 (one row of sixteen)");
        long b16 = p.Test(410, 13, 16), writes = p.Test(410, 3);
        Win32Host.Text(c.Hwnd, "z");                                                  // not a hex digit: ignored
        Win32Host.Char(c.Hwnd, 'C'); Win32Host.Char(c.Hwnd, (char)27);               // a half-typed byte is dropped by Esc
        t.Expect(p.Test(410, 13, 16) == b16 && p.Test(410, 3) == writes && p.Test(420, 5) == 16, "a half-typed byte or a non-hex key changed something");
        Win32Host.Char(c.Hwnd, '1'); Win32Host.Char(c.Hwnd, '2');
        t.Expect(p.Test(410, 13, 16) == 0x12 && p.Test(410, 3) == writes + 1, $"after Esc, typing 12 wrote ${p.Test(410, 13, 16):X2} in {p.Test(410, 3) - writes} writes");
        // the wheel scrolls the rows, the window's own keyboard handling is not disturbed by a click elsewhere
        Win32Host.WheelAt(c.Hwnd, -2, hex.X + 30, hex.Y + 20);
        t.Expect(p.Test(420, 6) == 2, $"two wheel notches over the hex field moved it to row {p.Test(420, 6)}, expected 2");
        Win32Host.Click(c.Hwnd, 20, 20);
        t.Expect(p.Test(420, 10) == 0, "a click elsewhere left the hex field holding the keyboard");
    }

    private void CheckTools(T t, UiCtx c)
    {
        var p = c.P;
        UiTabTo(t, c, UiTab1, "Instruments");
        t.Expect(UiText(p, 10) == "no history yet" || UiText(p, 10) == "", $"a fresh instrument shows history '{UiText(p, 10)}'");
        UiClick(t, c, UiStrength, "Strength");
        var before = Enumerable.Range(0, 12).Select(f => p.Test(410, 12, f)).ToArray();
        long hash0 = p.Test(410, 19);
        UiClick(t, c, UiRandomise, "Randomise");
        t.Expect(p.Test(410, 4) == 1, "Randomise did not reach the model");
        t.Expect(p.Test(410, 10) == 500, $"Randomise was given strength {p.Test(410, 10) / 10.0}%, the slider said 50%");
        t.Expect(Enumerable.Range(0, 12).Any(f => p.Test(410, 12, f) != before[f]) || p.Test(410, 19) != hash0, "Randomise at 50% changed nothing");
        t.Expect(UiText(p, 10).StartsWith("step 1 of 1") && UiText(p, 11).Length == 16, $"after a corruption the toolbar shows history '{UiText(p, 10)}' and seed '{UiText(p, 11)}'");
        // nudge: the chosen field, up and down; with none chosen the model picks
        int v = (int)p.Test(410, 12, 1);
        UiClick(t, c, UiFieldBase + 1 * 8 + 4, "name of Volume right");
        UiClick(t, c, UiNudgeUp, "Nudge +");
        t.Expect(p.Test(410, 5) == 1 && p.Test(410, 11) == 1 && p.Test(410, 17) == 1, $"Nudge + reached the model as direction {p.Test(410, 11)} on field {p.Test(410, 17)}");
        int up = Math.Min(127, v + 3);
        t.Expect(p.Test(410, 12, 1) == up, $"Nudge + changed Volume right from {v} to {p.Test(410, 12, 1)}, expected {up}");
        UiClick(t, c, UiNudgeDown, "Nudge -");
        t.Expect(p.Test(410, 11) == -1 && p.Test(410, 12, 1) == Math.Max(0, up - 3), $"Nudge - gave direction {p.Test(410, 11)} and value {p.Test(410, 12, 1)}, expected -1 and {Math.Max(0, up - 3)}");
        UiClick(t, c, UiFieldBase + 1 * 8 + 4, "name of Volume right (again: unchoose)");
        UiClick(t, c, UiNudgeUp, "Nudge + on any field");
        t.Expect(p.Test(410, 17) == -1, $"with no field chosen Nudge named field {p.Test(410, 17)}, expected the model's own pick (-1)");
        // a nudge of a locked field is refused with a reason
        UiClick(t, c, UiLockAll, "Lock all"); UiClick(t, c, UiFieldBase + 1 * 8 + 4, "choose Volume right");
        UiClick(t, c, UiNudgeUp, "Nudge + on a locked field");
        t.Expect(p.Test(420, 11) == 0 && UiText(p, 3).Contains("locked"), $"nudging a locked field said '{UiText(p, 3)}' (ok flag {p.Test(420, 11)})");
    }

    private void CheckUndoRedo(T t, UiCtx c)
    {
        var p = c.P;
        UiTabTo(t, c, UiTab1, "Instruments");
        UiClick(t, c, UiUndo, "Undo with no history");
        t.Expect(p.Test(410, 6) == 0 && p.Test(420, 11) == 0, "Undo with nothing to undo reached the model or claimed success");
        UiClick(t, c, UiFieldBase + 1 * 8 + 2, "plus");                                // Volume right 100 -> 101
        UiClick(t, c, UiFieldBase + 1 * 8 + 2, "plus");                                // -> 102
        t.Expect(p.Test(410, 12, 1) == 102 && p.Test(410, 15) == 3, $"two edits left value {p.Test(410, 12, 1)} and {p.Test(410, 15)} history entries");
        UiClick(t, c, UiUndo, "Undo");
        t.Expect(p.Test(410, 6) == 1 && p.Test(410, 12, 1) == 101, $"Undo gave value {p.Test(410, 12, 1)} after {p.Test(410, 6)} undo calls, expected 101");
        t.Expect(UiText(p, 10) == "step 1 of 2", $"the history reads '{UiText(p, 10)}', expected 'step 1 of 2'");
        UiClick(t, c, UiUndo, "Undo");
        t.Expect(p.Test(410, 12, 1) == 100, $"a second Undo gave {p.Test(410, 12, 1)}");
        UiClick(t, c, UiRedo, "Redo");
        t.Expect(p.Test(410, 7) == 1 && p.Test(410, 12, 1) == 101, $"Redo gave value {p.Test(410, 12, 1)} after {p.Test(410, 7)} redo calls, expected 101");
        UiClick(t, c, UiFieldBase + 1 * 8 + 1, "minus");                               // a new edit drops what could have been redone
        UiClick(t, c, UiRedo, "Redo after a new edit");
        t.Expect(p.Test(420, 11) == 0, "Redo after a new edit should be refused");
    }

    /// <summary>The name the demonstration library was last given a preset under ("Add <name>" is its last call).</summary>
    private static string AddedName(PluginInstance p) { string last = UiText(p, 0); return last.StartsWith("Add ") ? last[4..] : last; }

    private void CheckSendPush(T t, UiCtx c)
    {
        var p = c.P;
        UiTabTo(t, c, UiTab1, "Instruments");
        UiClick(t, c, UiSendToPresets, "Send to Presets");
        // it asks for a name first (round 6, presets): the model's own name is in the field with everything selected, nothing is in the library yet
        t.Expect(p.Test(420, 12) == 1 && p.Test(420, 10) == 1 && p.Test(411, 8) == 0, $"Send to Presets did not open a name field (open {p.Test(420, 12)}, keys {p.Test(420, 10)}, library took {p.Test(411, 8)})");
        string offered = UiText(p, 14);
        t.Expect(offered.Length > 0, "the name field is empty although the model offered a name");
        Win32Host.Char(c.Hwnd, (char)27);                                              // Esc: nothing is added
        t.Expect(p.Test(420, 12) == 0 && p.Test(411, 8) == 0 && UiText(p, 3).Contains("Cancelled"), $"Esc in the name field: open {p.Test(420, 12)}, library took {p.Test(411, 8)}, said '{UiText(p, 3)}'");
        UiClick(t, c, UiSendToPresets, "Send to Presets (again)");
        Win32Host.Text(c.Hwnd, "My Lead");                                             // typing replaces the selected name
        t.Expect(UiText(p, 14) == "My Lead", $"typing over the offered name gave '{UiText(p, 14)}'");
        Win32Host.Char(c.Hwnd, '\r');
        t.Expect(p.Test(410, 8) == 2 && p.Test(411, 8) == 1, $"Send to Presets: the model gave {p.Test(410, 8)} draft(s), the library took {p.Test(411, 8)}");
        t.Expect(p.Test(420, 11) == 1 && UiText(p, 3).Contains("in the library") && p.Test(420, 12) == 0, $"Send to Presets said '{UiText(p, 3)}'");
        t.Expect(AddedName(p) == "My Lead", $"the library was given the name '{AddedName(p)}', expected the one typed");
        UiClick(t, c, UiSendToPresets, "Send to Presets (third)");
        Win32Host.Char(c.Hwnd, '\b'); Win32Host.Char(c.Hwnd, (char)1); Win32Host.Char(c.Hwnd, '\b');
        Win32Host.Char(c.Hwnd, '\r');                                                  // an empty name is refused and the field stays
        t.Expect(p.Test(420, 12) == 1 && p.Test(420, 11) == 0 && UiText(p, 3).Contains("needs a name"), $"an empty name: open {p.Test(420, 12)}, said '{UiText(p, 3)}'");
        Win32Host.Text(c.Hwnd, "Second");
        UiClick(t, c, UiSendOk, "Add button");
        t.Expect(p.Test(420, 12) == 0 && p.Test(411, 8) == 2 && AddedName(p) == "Second", $"the Add button: open {p.Test(420, 12)}, library took {p.Test(411, 8)}, name '{AddedName(p)}'");
        UiClick(t, c, UiPush, "Push into the game");
        t.Expect(p.Test(410, 9) == 1 && p.Test(420, 11) == 1 && UiText(p, 3).Contains("pushed"), $"Push into the game said '{UiText(p, 3)}'");
        UiSelectInstrument(t, c, 9);
        UiClick(t, c, UiPush, "Push into the game (refused)");
        t.Expect(p.Test(420, 11) == 0 && UiText(p, 3).Contains("cannot push on this console"), $"a refused push said '{UiText(p, 3)}' (ok flag {p.Test(420, 11)})");
        // the models are gone: the tab says so instead of showing an empty list
        p.Test(403, 0, 2);
        Win32Host.Pump(100);
        t.Expect(UiText(p, 2).Contains("not connected"), "without models the Instruments tab should say so");
    }

    private void CheckModelFailure(T t, UiCtx c)
    {
        var p = c.P;
        UiTabTo(t, c, UiTab1, "Instruments");
        t.Expect(UiRect(c, UiInstList) != null && UiText(p, 12) == "<none>", "the tab does not start healthy");
        // reads that throw while the tab paints: the tab says so, the window stays alive and usable
        p.Test(404, 0, 1);
        Win32Host.Pump(100);
        t.Expect(UiText(p, 12).Contains("told to fail on a read"), $"a model that throws while the tab paints left '{UiText(p, 12)}' on the tab");
        var shot = Win32Host.CapturePixels(c.Hwnd, out int w, out _);
        t.Expect(CountRgb(shot, w, (556, 110, 752, 360), 255, 184, 76) > 500, "the failure box (amber border) is not on the screen");
        t.Expect(UiRect(c, UiInstList) == null, "the instrument list is still drawn although its model fails");
        UiTabTo(t, c, UiTab0, "Emulator");
        t.Expect(p.Test(400) == 0 && UiRect(c, 21) != null, "the window did not stay usable while a model failed");
        UiTabTo(t, c, UiTab1, "Instruments");
        p.Test(404, 0, 0);
        Win32Host.Pump(100);
        t.Expect(UiText(p, 12) == "<none>" && UiRect(c, UiInstList) != null, "the tab did not recover when the model worked again");
        // writes that throw while an event is handled: the status line says so and nothing changes
        long before = p.Test(410, 12, 1);
        p.Test(404, 0, 2);
        UiClick(t, c, UiFieldBase + 1 * 8 + 2, "plus");
        t.Expect(UiText(p, 3).Contains("A model behind this tab failed") && UiText(p, 3).Contains("told to fail on a write") && p.Test(420, 11) == 0, $"a failing write said '{UiText(p, 3)}'");
        t.Expect(p.Test(410, 12, 1) == before, "a failing write changed the value");
        p.Test(404, 0, 0);
        UiClick(t, c, UiFieldBase + 1 * 8 + 2, "plus");
        t.Expect(p.Test(410, 12, 1) == before + 1, "the model did not work again after the failure");
    }

    // ------------------------------------------------------------------------------------------------ the Presets tab

    private void CheckPresetsNoLibrary(T t, UiCtx c)
    {
        UiTabTo(t, c, UiTab2, "Presets");
        Win32Host.Pump(60);
        t.Expect(UiText(c.P, 4) == "The preset library is not connected in this build of BrokenNes2.", $"without a library the Presets tab says '{UiText(c.P, 4)}'");
        t.Expect(UiRect(c, UiPresetList) == null && UiRect(c, UiLoad) == null, "the Presets tab shows controls without a library");
    }

    private void CheckPresetsList(T t, UiCtx c)
    {
        var p = c.P;
        UiTabTo(t, c, UiTab2, "Presets");
        t.Expect(UiText(p, 4) == "" && p.Test(421, 0) == 14, $"the library list has {p.Test(421, 0)} rows ('{UiText(p, 4)}'), expected 14");
        var rows = Enumerable.Range(0, 11).Count(i => UiRect(c, UiPresetList + i) != null);
        t.Expect(rows == 11 && UiRect(c, UiPresetList + 11) == null, $"the list shows {rows} rows, expected 11 of 14");
        var r0 = UiRect(c, UiPresetList)!.Value;
        Win32Host.WheelAt(c.Hwnd, -1, r0.X + 20, r0.Y + 10);
        t.Expect(p.Test(421, 1) == 3, $"one wheel notch moved the library list to row {p.Test(421, 1)}, expected 3");
        Win32Host.WheelAt(c.Hwnd, -9, r0.X + 20, r0.Y + 10);
        t.Expect(p.Test(421, 1) == 3, $"the library list scrolled to {p.Test(421, 1)}, the end is row 3 (14 rows, 11 visible)");
        Win32Host.WheelAt(c.Hwnd, 9, r0.X + 20, r0.Y + 10);
        t.Expect(p.Test(421, 1) == 0, "the wheel up did not bring the library list back");
        UiClick(t, c, UiPresetList + 2, "row 2");
        t.Expect(UiText(p, 7) == "p008", $"the third row selected '{UiText(p, 7)}', expected p008 (Dream Strings)");
        t.Expect(UiRect(c, UiLoad) != null && UiRect(c, UiRename) != null && UiRect(c, UiDelete) != null && UiRect(c, UiExport) != null && UiRect(c, UiImport) != null && UiRect(c, UiAudition) != null, "a button of the Presets tab is not on screen");
    }

    private void CheckPresetsSearch(T t, UiCtx c)
    {
        var p = c.P;
        UiTabTo(t, c, UiTab2, "Presets");
        UiClick(t, c, UiSearch, "Search box");
        t.Expect(p.Test(421, 4) == 1, "clicking the search box did not give it the keyboard");
        Win32Host.Text(c.Hwnd, "bass");
        t.Expect(UiText(p, 6) == "bass" && p.Test(421, 0) == 2, $"searching 'bass': text '{UiText(p, 6)}', {p.Test(421, 0)} rows (expected 2: Fuzz Bass, Slap Bass)");
        for (int i = 0; i < 4; i++) Win32Host.Char(c.Hwnd, '\b');
        t.Expect(UiText(p, 6) == "" && p.Test(421, 0) == 14, $"after four Backspaces: text '{UiText(p, 6)}', {p.Test(421, 0)} rows, expected all 14");
        Win32Host.Text(c.Hwnd, "abc"); Win32Host.Key(c.Hwnd, Win32Host.VkHome); Win32Host.Key(c.Hwnd, Win32Host.VkDelete);
        t.Expect(UiText(p, 6) == "bc", $"Home then Delete on 'abc' left '{UiText(p, 6)}'");
        Win32Host.Key(c.Hwnd, Win32Host.VkEnd); Win32Host.Key(c.Hwnd, Win32Host.VkLeft); Win32Host.Text(c.Hwnd, "X");
        t.Expect(UiText(p, 6) == "bXc", $"End, Left, X on 'bc' gave '{UiText(p, 6)}'");
        Win32Host.Char(c.Hwnd, (char)1); Win32Host.Text(c.Hwnd, "zzz");                // Ctrl+A, then typing replaces everything
        t.Expect(UiText(p, 6) == "zzz" && p.Test(421, 0) == 0, $"Ctrl+A then 'zzz': text '{UiText(p, 6)}', {p.Test(421, 0)} rows, expected none");
        UiClick(t, c, UiSearchClear, "Clear");
        t.Expect(UiText(p, 6) == "" && p.Test(421, 0) == 14, "the clear button did not empty the search");
        // the console filter
        UiClick(t, c, UiFilter + 3, "SNES filter");
        t.Expect(p.Test(421, 3) == 3 && p.Test(421, 0) == 4, $"the SNES filter shows {p.Test(421, 0)} rows, expected 4");
        UiClick(t, c, UiFilter + 0, "All filter");
        t.Expect(p.Test(421, 0) == 14, "the All filter did not bring every preset back");
        // search and filter together: bass on GB leaves Fuzz Bass only
        UiClick(t, c, UiSearch, "Search box"); Win32Host.Text(c.Hwnd, "bass"); UiClick(t, c, UiFilter + 2, "GB filter");
        t.Expect(p.Test(421, 0) == 1, $"'bass' on GB shows {p.Test(421, 0)} rows, expected 1");
        // a click outside lets go of the keyboard
        Win32Host.Click(c.Hwnd, 20, 20);
        t.Expect(p.Test(421, 4) == 0, "a click elsewhere left the search box holding the keyboard");
    }

    private void CheckPresetsButtons(T t, UiCtx c)
    {
        var p = c.P;
        UiTabTo(t, c, UiTab2, "Presets");
        // with nothing chosen the buttons refuse and the library hears nothing
        UiClick(t, c, UiLoad, "Load with nothing chosen");
        t.Expect(p.Test(411, 2) == 0 && p.Test(421, 5) == 0 && UiText(p, 5).Contains("Pick a preset"), $"Load with no preset said '{UiText(p, 5)}', library loads: {p.Test(411, 2)}");
        UiClick(t, c, UiPresetList + 2, "row 2 (Dream Strings)");
        UiClick(t, c, UiLoad, "Load");
        t.Expect(p.Test(411, 2) == 1 && UiText(p, 0) == "Load p008", $"Load: the library heard '{UiText(p, 0)}'");
        UiClick(t, c, UiAudition, "Audition");
        t.Expect(p.Test(411, 7) == 1 && UiText(p, 0) == "Audition p008", $"Audition: the library heard '{UiText(p, 0)}'");
        // export and import name a file (a test names it instead of the dialog)
        string path = Path.Combine(Path.GetTempPath(), "bn2_ui_test.bn2preset");
        fixed (char* s = path) p.Test(414, 0, (nint)s);
        UiClick(t, c, UiExport, "Export");
        t.Expect(p.Test(411, 5) == 1 && UiText(p, 0) == $"Export p008 {path}", $"Export: the library heard '{UiText(p, 0)}'");
        fixed (char* s = path) p.Test(414, 0, (nint)s);
        UiClick(t, c, UiImport, "Import");
        t.Expect(p.Test(411, 6) == 1 && UiText(p, 0) == $"Import {path}", $"Import: the library heard '{UiText(p, 0)}'");
        fixed (char* s = "") p.Test(414, 0, (nint)s);                                  // the user cancels the dialog
        UiClick(t, c, UiExport, "Export (cancelled)");
        t.Expect(p.Test(411, 5) == 1 && UiText(p, 5).Contains("cancelled"), "a cancelled Export dialog should not reach the library");
        // rename: type, Enter
        UiClick(t, c, UiRename, "Rename");
        t.Expect(p.Test(421, 2) == 1 && p.Test(421, 4) == 1, "Rename did not open the name field with the keyboard");
        Win32Host.Text(c.Hwnd, "Glass Harp"); Win32Host.Char(c.Hwnd, '\r');
        t.Expect(p.Test(411, 3) == 1 && UiText(p, 0) == "Rename p008 Glass Harp" && p.Test(421, 2) == 0, $"Rename: the library heard '{UiText(p, 0)}'");
        UiClick(t, c, UiRename, "Rename again"); Win32Host.Text(c.Hwnd, "nope"); Win32Host.Char(c.Hwnd, (char)27);
        t.Expect(p.Test(411, 3) == 1 && p.Test(421, 2) == 0, "Esc in the name field should cancel the rename");
        // delete takes two clicks, and a click elsewhere disarms it
        UiClick(t, c, UiDelete, "Delete");
        t.Expect(p.Test(411, 4) == 0 && p.Test(421, 0) == 14, "the first click on Delete deleted");
        UiClick(t, c, UiPresetList + 0, "another row");
        UiClick(t, c, UiDelete, "Delete");
        t.Expect(p.Test(411, 4) == 0, "Delete stayed armed after another preset was chosen");
        UiClick(t, c, UiDelete, "Delete (second click)");
        t.Expect(p.Test(411, 4) == 1 && p.Test(421, 0) == 13, $"the second click deleted {p.Test(411, 4)} preset(s), {p.Test(421, 0)} rows left (expected 1 and 13)");
        t.Expect(UiText(p, 7) == "<none>" || UiText(p, 7) == "", "the deleted preset is still selected");
    }

    private void CheckKeyboardGuard(T t, UiCtx c)
    {
        var p = c.P;
        UiTabTo(t, c, UiTab2, "Presets");
        p.Test(130, 1, 0xFFF);                                                         // every pad button's key is held "down" as far as the editor can tell
        Win32Host.Pump(150);
        t.Expect(p.Test(39) != 0, "the simulated held keys did not reach the pad when no text field has focus (the test itself is not working)");
        UiClick(t, c, UiSearch, "Search box");
        Win32Host.Pump(150);
        t.Expect(p.Test(421, 4) == 1 && p.Test(39) == 0, $"typing in a text field must not press the game's buttons (pad bits {p.Test(39)}, keyboard taken {p.Test(421, 4)})");
        Win32Host.Click(c.Hwnd, 20, 20);
        Win32Host.Pump(150);
        t.Expect(p.Test(39) != 0, "the pad did not get its keyboard back when the text field let go");
        p.Test(130, 0);
    }
}
