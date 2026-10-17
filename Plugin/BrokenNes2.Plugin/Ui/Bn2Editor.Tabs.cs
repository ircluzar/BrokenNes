// The editor's tab framework (a partial of Bn2Editor, so the editor's own file only gains the few lines that call into it).
//
//   Emulator | Instruments | Presets     the strip sits on the header rule above the right column (UiLayout.StripX/Y): the Emulator tab keeps every pixel it had.
//   Emulator   today's right column, untouched (904 x 664).
//   Instruments, Presets   the window grows to the right by UiLayout.WideExtra; the picture and the left column stay put. The editor resizes its own
//              window, then tells the host (FHD_EditorResized, Fhd.EditorResized): FL fits its frame around the editor window again.
//
// Events: the editor's mouse handler looks the control up by id as always; ids from 1000 (Kind.Ui) belong to the tabs and are forwarded here.
// Keyboard: WM_CHAR / WM_KEYDOWN reach the tab that has a text field or the hex field in focus; while one has, the pad polling of the Inputs window is off
// (typing a name must not press the game's buttons).
//
// Test hooks (Fpd.TestBase + n), 400 - 499:
//   400 tab (0..2)                      401 client size of the editor window (w | h << 16)
//   403 models: value 1 installs the demonstration models (UiServices; the stubs in StubModels.cs), 2 leaves none, 0 puts back what was registered before (the real ones, once they exist)
//   404 the demonstration instrument model fails: bit 0 its reads throw, bit 1 its writes throw (0 = works again)
//   410 instrument stub counter <index> (argument <value>)       411 preset stub counter <index>
//   412 copy a text into the UTF-16 buffer <value> (<index> = field in the high 16 bits, max chars in the low 16): 0 preset stub's last call, 1 instrument stub's last call,
//       2 Instruments message, 3 Instruments status, 4 Presets message, 5 Presets status, 6 search text, 7 selected preset id, 8 Instruments status ok flag as "1"/"0", 9 selected instrument's name,
//       10 the history text drawn, 11 the seed text drawn, 12 the failure text the tab shows for a model that threw while painting (-1 none), 100 + n "Name=value" of the n-th visible field row; returns the length (-1 none)
//   414 the next Export / Import file dialog answers with the UTF-16 path <value> ("" = cancelled)
//   420 Instruments panel number <index>: 0 list first row, 1 list rows, 2 selected, 3 fields first row, 4 field rows, 5 hex selected byte, 6 hex first row, 7 blob, 8 waveform columns, 9 strength, 10 wants keys, 11 status ok
//   421 Presets panel number <index>: 0 rows, 1 first row, 2 renaming, 3 filter, 4 wants keys, 5 status ok
//   499 set the fault mask (UiMutate): the self-test's proof that each check can fail
using System.Runtime.InteropServices;
using BrokenNes.Fruity;
using BrokenNes2.Ui;

namespace BrokenNes2;

public sealed unsafe partial class Bn2Editor
{
    private int tab;                                  // 0 Emulator, 1 Instruments, 2 Presets
    private UiCanvas? canvas;
    private InstrumentsPanel? instPanel;
    private PresetsPanel? presetPanel;
    private bool uiShift, uiDbl;
    private string? testTabFile;

    internal static StubInstrumentModel? DemoModel;
    internal static StubPresetLibrary? DemoLibrary;
    private static InstrumentProviderFactory? savedInstruments;
    private static IPresetLibrary? savedPresets;
    private static bool demoSaved;

    /// <summary>The window's width: the Emulator tab's own, or wider on the tabs that need room.</summary>
    private int Width => tab == 0 ? UiLayout.BaseWidth : UiLayout.WideWidth;

    private UiCanvas Canvas() => canvas ??= new UiCanvas((id, r) => AddHit(id, r, Kind.Ui));
    private InstrumentsPanel Inst() => instPanel ??= new InstrumentsPanel(Canvas());
    private PresetsPanel Presets() => presetPanel ??= new PresetsPanel(Canvas()) { ChooseFile = ChooseTabFile };

    private void UiDispose()
    {
        canvas?.Dispose();
        canvas = null;
    }

    // ---------------------------------------------------------------- drawing

    /// <summary>The tab strip: three labels on the header rule, the current one underlined in the accent colour.</summary>
    private void DrawTabStrip(nint dc)
    {
        int x = UiLayout.StripX;
        for (int i = 0; i < UiServices.TabNames.Length; i++)
        {
            string label = UiServices.TabNames[i];
            int w = TextWidth(dc, label, fontSection) + 24;
            var r = R(x, UiLayout.StripY, w, UiLayout.StripH);
            bool on = tab == i, isHot = hot == UiIds.Tab + i;
            Txt(dc, label, R(r.Left, r.Top, w, UiLayout.StripH - 3), Win32.DT_CENTER | Mid, on ? White : isHot ? Text : Dim, fontSection);
            if (on) Fill(dc, R(r.Left + 6, r.Bottom - 3, w - 12, 3), Accent);
            AddHit(UiIds.Tab + i, r, Kind.Ui);
            x += w + 2;
        }
    }

    /// <summary>The right column of the Instruments and Presets tabs.</summary>
    private void DrawTabContent(nint dc, Emulator? emu)
    {
        var cv = Canvas();
        cv.Hot = hot; cv.Active = active;
        PresetHost.Current = plugin;                  // the preset library acts on the instance whose editor this is
        int saved = Win32.SaveDC(dc);
        try
        {
            if (tab == 1) Inst().Draw(dc, emu); else Presets().Draw(dc);
            tabError = null;
        }
        catch (Exception e)
        {
            // a model that throws while the tab is painted must not leave the window half drawn (a paint that never finishes is repainted without end)
            Diag.Log($"tab {tab} draw: {e}");
            Win32.RestoreDC(dc, saved); saved = Win32.SaveDC(dc);
            if (UiMutate.Has(UiMutate.SilentModelFailure)) return;       // (the self-test's fault: nothing is said)
            tabError = e.Message;
            var box = R(UiLayout.PanelX, 110, UiLayout.PanelW, 360);
            Box(dc, box, Panel, Warn, 8);
            Txt(dc, "A model behind this tab failed: " + e.Message, R(box.Left + 40, box.Top + 100, box.Right - box.Left - 80, 120), Win32.DT_CENTER | Win32.DT_WORDBREAK | Win32.DT_VCENTER, Text, fontLead);
            Txt(dc, "BrokenNes2 shows this instead of the tab until the model works again.", R(box.Left + 40, box.Top + 230, box.Right - box.Left - 80, 40), Win32.DT_CENTER | Win32.DT_WORDBREAK, Dim, fontLabel);
        }
        finally { Win32.RestoreDC(dc, saved); }
    }

    private string? tabError;

    /// <summary>Runs an event handler of a tab; a model that throws is logged and shown on the tab instead of taking the window's message handling with it.</summary>
    private T UiGuard<T>(Func<T> f, T fallback)
    {
        PresetHost.Current = plugin;                  // the preset library acts on the instance whose editor this is
        try { return f(); }
        catch (Exception e)
        {
            Diag.Log($"tab {tab} event: {e}");
            if (!UiMutate.Has(UiMutate.SilentModelFailure)) { if (tab == 1) Inst().Fail(e.Message); else if (tab == 2) Presets().Fail(e.Message); }
            return fallback;
        }
    }

    // ---------------------------------------------------------------- tabs and the window size

    private void SetTab(int t)
    {
        t = Math.Clamp(t, 0, UiServices.TabNames.Length - 1);
        if (t == tab) return;
        openId = 0; openHover = -1; active = 0;
        UiCancel();
        int before = Width;
        tab = t;
        if (Width != before) ApplyWindowSize();          // Instruments <-> Presets keep the wide window: the host is told only when the size changed
        Refresh();
    }

    /// <summary>Resizes this window to the current tab's width and tells the host (FL fits its frame around the editor window again).</summary>
    private void ApplyWindowSize()
    {
        if (!UiMutate.Has(UiMutate.TabNoResize))
            UiNative.SetWindowPos(Hwnd, 0, 0, 0, Width, Height, UiNative.SWP_NOMOVE | UiNative.SWP_NOZORDER | UiNative.SWP_NOACTIVATE);
        if (!UiMutate.Has(UiMutate.TabNoNotify))
            plugin.Host.Dispatcher(plugin.HostTag, Fhd.EditorResized, 0, 0);
    }

    // ---------------------------------------------------------------- events

    private bool UiWantsKeys => !UiMutate.Has(UiMutate.KeyGuardOff) && (tab == 1 ? instPanel?.WantsKeys == true : tab == 2 && presetPanel?.WantsKeys == true);

    private long UiSignature()
    {
        long s = tab * 7919L + (UiServices.Instruments != null ? 1 : 0) + (UiServices.Presets != null ? 2 : 0);      // models connecting or leaving repaint the tab
        s += UiGuard(() => tab == 1 && instPanel != null ? instPanel.Signature() : tab == 2 && presetPanel != null ? presetPanel.Signature() : 0, 0L);
        return s;
    }

    private void UiDown(Hit h, int x, int y)
    {
        int id = h.Id;
        if (id >= UiIds.Tab && id < UiIds.Tab + UiServices.TabNames.Length) { SetTab(id - UiIds.Tab); return; }
        bool capture = UiGuard(() => tab == 1 ? Inst().Down(id, x, y, uiShift, uiDbl) : tab == 2 && Presets().Down(id, x, y, uiDbl), false);
        if (capture) { active = id; Win32.SetCapture(Hwnd); }
        if (UiWantsKeys) UiNative.SetFocus(Hwnd);
        else UiReleaseFocus();
    }

    /// <summary>A press that is not on one of the tabs' controls: text fields let go of the keyboard.</summary>
    private void UiBlur()
    {
        instPanel?.Blur(); presetPanel?.Blur();
        UiReleaseFocus();
    }

    /// <summary>Gives the keyboard back to the host's frame when this window held it for a text field.</summary>
    private void UiReleaseFocus()
    {
        if (UiWantsKeys || UiNative.GetFocus() != Hwnd) return;
        nint parent = UiNative.GetParent(Hwnd);
        if (parent != 0) UiNative.SetFocus(parent);
    }

    private void UiCancel()
    {
        instPanel?.Cancel(); presetPanel?.Cancel();
        UiReleaseFocus();
    }

    private void UiMove(int x, int y)
    {
        UiGuard(() => { if (tab == 1) Inst().Move(x, y); else if (tab == 2) Presets().Move(x, y); return 0; }, 0);
    }

    private void UiUp()
    {
        instPanel?.Up(); presetPanel?.Up();
    }

    private void UiWheel(int delta, int x, int y)
    {
        UiGuard(() => tab == 1 ? Inst().Wheel(delta, x, y) : tab == 2 && Presets().Wheel(delta, x, y), false);
    }

    private bool UiChar(char ch)
    {
        bool used = UiGuard(() => tab == 1 ? Inst().Char(ch) : tab == 2 && Presets().Char(ch), false);
        if (used) Refresh();
        if (!UiWantsKeys) UiReleaseFocus();
        return used;
    }

    private bool UiKey(int vk)
    {
        bool used = UiGuard(() => tab == 1 ? Inst().Key(vk) : tab == 2 && Presets().Key(vk), false);
        if (used) Refresh();
        return used;
    }

    /// <summary>The Export / Import dialogs (a test names the file instead; "" stands for the user cancelling).</summary>
    private string? ChooseTabFile(bool save, string title, string filter)
    {
        string? named = testTabFile;
        testTabFile = null;
        if (named != null) return named.Length == 0 ? null : named;
        return save ? UiNative.ChooseSaveFile(Hwnd, title, filter, "bn2preset") : Win32.ChooseFile(Hwnd, title, filter);
    }

    // ---------------------------------------------------------------- test hooks

    private static string? TextFromPointer(nint p) => Marshal.PtrToStringUni(p);

    private nint UiTest(int n, nint index, nint value)
    {
        switch (n)
        {
            case 400: return tab;
            case 401:
            {
                RECT r;
                Win32.GetClientRect(Hwnd, &r);
                return (nint)((long)(r.Right - r.Left) | ((long)(r.Bottom - r.Top) << 16));
            }
            case 403:
                // value 1: the demonstration models; 2: no models at all; 0: whatever was registered before the first of these calls (the real models, when they exist)
                if (!demoSaved) { savedInstruments = UiServices.Instruments; savedPresets = UiServices.Presets; demoSaved = true; }
                if (value == 1) { DemoModel = new StubInstrumentModel(); DemoLibrary = new StubPresetLibrary(); var model = DemoModel; UiServices.Instruments = _ => model; UiServices.Presets = DemoLibrary; }
                else if (value == 2) { UiServices.Instruments = null; UiServices.Presets = null; DemoModel = null; DemoLibrary = null; }
                else { UiServices.Instruments = savedInstruments; UiServices.Presets = savedPresets; DemoModel = null; DemoLibrary = null; demoSaved = false; }
                instPanel = null; presetPanel = null;
                Refresh();
                return 0;
            case 404:
                DemoModel?.SetFailure((value & 1) != 0, (value & 2) != 0);
                Refresh();
                return 0;
            case 410: return (nint)(DemoModel?.Probe((int)index, (int)value) ?? -1);
            case 411: return (nint)(DemoLibrary?.Probe((int)index) ?? -1);
            case 412:
            {
                int field = (int)(index >> 16), max = (int)(index & 0xFFFF);
                string? text = field switch
                {
                    0 => DemoLibrary?.LastCall,
                    1 => DemoModel?.LastCall,
                    2 => instPanel?.Message,
                    3 => instPanel?.Status,
                    4 => presetPanel?.Message,
                    5 => presetPanel?.Status,
                    6 => presetPanel?.SearchText,
                    7 => presetPanel?.SelectedId,
                    8 => instPanel == null ? null : instPanel.StatusOk ? "1" : "0",
                    9 => instPanel?.SelectedName,
                    14 => instPanel?.NameText,                // the name field "Send to Presets" opens (round 6, presets)
                    12 => tabError,
                    10 => instPanel?.ShownHistory,
                    11 => instPanel?.ShownSeed,
                    >= 100 and < 120 => instPanel?.FieldText(field - 100),          // 100 + visible row: "Name=value" of that field row
                    _ => null,
                };
                if (text == null) return -1;
                if (value != 0 && max > 0) text.AsSpan(0, Math.Min(text.Length, max)).CopyTo(new Span<char>((void*)value, max));
                return text.Length;
            }
            case 414: testTabFile = TextFromPointer(value) ?? ""; return 0;
            case 420:
                if (instPanel == null) return -1;
                return index switch
                {
                    0 => instPanel.ListFirstRow, 1 => instPanel.ListCount, 2 => instPanel.SelectedInstrument, 3 => instPanel.FieldFirstRow, 4 => instPanel.FieldCount,
                    5 => instPanel.HexSelected, 6 => instPanel.HexFirstRow, 7 => instPanel.BlobIndex, 8 => instPanel.WaveColumns, 9 => instPanel.Strength,
                    10 => instPanel.WantsKeys ? 1 : 0, 11 => instPanel.StatusOk ? 1 : 0, 12 => instPanel.Naming ? 1 : 0, _ => -1,
                };
            case 421:
                if (presetPanel == null) return -1;
                return index switch
                {
                    0 => presetPanel.RowCount, 1 => presetPanel.FirstRow, 2 => presetPanel.Renaming ? 1 : 0, 3 => presetPanel.FilterIndex, 4 => presetPanel.WantsKeys ? 1 : 0,
                    5 => presetPanel.StatusOk ? 1 : 0, _ => -1,
                };
            case 499: UiMutate.Mask = (int)value; return 0;
        }
        return 0;
    }
}
