// Control ids of the Instruments and Presets tabs, and the test-only fault switches.
//
// Id map of the editor (a control's id is what the control record, the hover state and the self-test's rectangle query (Fpd.TestBase + 30) use):
//     1 - 9      Emulator tab: emulator, mode, chip, game, channel            (Bn2Editor)
//     10 - 14    channel cells, 20 + parameter = sliders, 40/41 About, 50 - 54 consoles, 60 Reset, 70 About link, 80 - 86 Inputs / Runaway,
//     90/91      instrument and duty dropdowns, 100 + item = the open dropdown's items, 200 - 251 Inputs window, 400 - 420 Sega voice cells,
//     500 - 524  Corruption window                                          (all Bn2Editor; never reuse)
//     1000 - 1999  THE TABS (this file): 1000 - 1009 the tab strip, 1100 - 1599 the Instruments tab, 1600 - 1799 the Presets tab,
//                  1800 - 1999 reserved for later tab work.
// Test hooks the tabs answer through the editor (Fpd.TestBase + n): 400 - 479 (see Bn2Editor.Tabs.cs); the plugin's own hooks stay below 400.
namespace BrokenNes2.Ui;

/// <summary>Geometry shared by the editor and the tabs. The window is BaseWidth x Height on the Emulator tab and grows to the right by <see cref="WideExtra"/> on the others
/// (the game picture and the left column stay where they are; the tab panel starts at the right column's own left edge).</summary>
internal static class UiLayout
{
    public const int BaseWidth = 904, Height = 664;
    /// <summary>How much wider the window is on the Instruments and Presets tabs (904 + 420 = 1324 fits a 1366-wide screen with FL's frame).</summary>
    public const int WideExtra = 420;
    public const int WideWidth = BaseWidth + WideExtra;
    public const int PanelX = 556, PanelY = 64, PanelRight = WideWidth - 16, PanelW = PanelRight - PanelX, PanelBottom = Height - 16;
    /// <summary>The tab strip: it sits on the header rule above the right column, left edge at the column's, so nothing of the Emulator tab moves.</summary>
    public const int StripX = PanelX, StripY = 36, StripH = 20;
}

internal static class UiIds
{
    public const int Tab = 1000;                  // + tab 0..2

    // ---- Instruments tab ----
    public const int InstList = 1100;             // + visible row 0..39 of the instrument list
    public const int InstListBar = 1140;          // the list's scroll bar
    public const int FieldsBar = 1150;            // the fields table's scroll bar
    public const int BlobChip = 1160;             // + blob 0..7
    public const int Wave = 1170, Hex = 1171, HexBar = 1172, LockBlob = 1173;
    public const int Strength = 1180, Randomise = 1181, NudgeDown = 1182, NudgeUp = 1183, LockAll = 1184, UnlockAll = 1185,
        Undo = 1187, Redo = 1188, SendToPresets = 1189, Push = 1190, Remove = 1191;
    public const int FieldBase = 1200;            // + visible row 0..11 * FieldStride + part
    public const int FieldStride = 8;
    public const int PartControl = 0, PartMinus = 1, PartPlus = 2, PartLock = 3, PartSelect = 4;
    public const int FieldBits = 1300;            // + visible row 0..11 * 16 + bit 0..15 (bit-field toggles)
    public const int MaxFieldRows = 12;

    // ---- Presets tab ----
    public const int PresetList = 1600;           // + visible row 0..39
    public const int PresetListBar = 1640;
    public const int Search = 1650, SearchClear = 1651;
    public const int Filter = 1655;               // + 0..5 (All, NES, GB, SNES, SMS, MD)
    public const int Load = 1670, Rename = 1671, Delete = 1672, Export = 1673, Import = 1674, Audition = 1675;
    public const int RenameInput = 1680, RenameOk = 1681, RenameCancel = 1682;
}

/// <summary>Deliberate faults the self-test switches on (hook 499) to prove each of its checks can fail. All zero in normal use: every one is a single
/// test of this mask at the place named, and the mask is only ever set by that test hook.</summary>
internal static class UiMutate
{
    public static int Mask;

    public const int TabNoResize = 1 << 0;        // switching tabs leaves the window as it is
    public const int TabNoNotify = 1 << 1;        // the host is not told the editor was resized
    public const int ListWheelDead = 1 << 2;      // the mouse wheel does not scroll lists
    public const int SliderOffByOne = 1 << 3;     // a slider writes its value + 1
    public const int StepperReversed = 1 << 4;    // the stepper's plus subtracts
    public const int BitToggleDead = 1 << 5;      // a bit toggle does not flip
    public const int WaveBlank = 1 << 6;          // the waveform view draws nothing
    public const int PresetExportImport = 1 << 7; // Export calls Import
    public const int RowOffByOne = 1 << 8;        // clicking an instrument row selects the next one
    public const int EmulatorShift = 1 << 9;      // the Emulator tab's right column is drawn one pixel lower
    public const int HexEditDead = 1 << 10;       // typing in the hex field writes nothing
    public const int TextInputDead = 1 << 11;     // the text input ignores characters
    public const int UndoRedoSwapped = 1 << 12;   // Undo calls Redo
    public const int FieldsNoScroll = 1 << 13;    // the fields table does not scroll
    public const int IgnoreRunaway = 1 << 14;     // the Instruments tab shows the instruments although Instrument Runaway is off
    public const int KeyGuardOff = 1 << 15;       // typing in a text field still presses the game's buttons
    public const int SilentModelFailure = 1 << 16;   // a model that throws is swallowed without a word on the tab

    public static bool Has(int bit) => (Mask & bit) != 0;
}
