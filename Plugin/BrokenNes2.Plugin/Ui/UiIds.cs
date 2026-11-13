// Control ids of the Instruments and Presets tabs, and the test-only fault switches.
//
// Id map of the editor (a control's id is what the control record, the hover state and the self-test's rectangle query (Fpd.TestBase + 30) use):
//     1 - 9      Emulator tab: emulator, mode, chip, game, channel            (Bn2Editor)
//     10 - 14    channel cells, 20 + parameter = sliders, 40/41 About, 50 - 54 consoles, 60 Reset, 70 About link, 80 - 86 Inputs / Runaway,
//     90/91      instrument and duty dropdowns, 100 + item = the open dropdown's items, 200 - 251 Inputs window, 400 - 420 Sega voice cells,
//     500 - 524  Corruption window                                          (all Bn2Editor; never reuse)
//     1000 - 1999  THE TABS (this file): 1000 - 1009 the tab strip, 1100 - 1599 the Instruments tab, 1600 - 1799 the Presets tab,
//                  1800 - 1999 reserved for later tab work.
//     2000 - 2019  the Test MIDI export button and window (round 7, Bn2Editor.Song.cs)
//     2020 - 2079  the Record button and window (round 8, Bn2Editor.Record.cs: 2020-2035, 2040-2044 and 2050-2054 used, the rest reserved)
// Test hooks the tabs answer through the editor (Fpd.TestBase + n): 400 - 479 (see Bn2Editor.Tabs.cs); the plugin's own hooks stay below 400, except Test MIDI export's:
// plugin 600 - 619 (Bn2Plugin.Song.cs) and editor 620 - 629 (Bn2Editor.Song.cs), and the recording's: plugin 640 - 669 and 680 - 699 (Bn2Plugin.Record.cs; 680+ the live
// MIDI out), 700 - 719 (Bn2Plugin.Tracks.cs: the tracks on offer) and editor 670 - 679 (Bn2Editor.Record.cs); a NES game's recording (round 8, record-nes): plugin 740 - 779
// (Bn2Plugin.RecordNes.cs), editor 780 - 789 and control ids 2080 - 2119 reserved (the NES recording shows in the Record window's own controls).
// Layout B (2026-11-13): control ids 2200 - 2219 the song bar, 2220 - 2259 the Game view's column (game list rows, freeze-later), 2260 - 2299 the Runaway tree (Bn2Editor.Layout.cs,
// Bn2Editor.Tree.cs), 62 the strip's "show all consoles" tick; editor test hooks 890 - 899. The view buttons reuse 1000 - 1002 (Game, Instruments, Presets), 500 (Corrupt), 2020 (Record);
// the song bar's facts button is 2000 (it opens the Song view, the old Test MIDI export window).
// Round 9 (capture): control ids 2120 - 2139 (Capture from FL in the Test MIDI export window, Bn2Editor.Capture.cs: 2120-2122 used), plugin hooks 840 - 859 (Bn2Plugin.Capture.cs),
// editor hooks 860 - 869 (Bn2Editor.Capture.cs); (790 - 819 are the reload agent's, Bn2Plugin.Reload.cs).
namespace BrokenNes2.Ui;

/// <summary>Geometry shared by the editor and the views (layout "B", 2026-11-13: channel strip + song bar). The window is ONE fixed size on every view (no resize call to the host any more):
///   header (0 .. HeaderH)       logo, the view buttons (Game, Instruments, Presets, Corrupt, Record), Inputs, the emulator, About
///   strip  (0 .. StripW)        this channel, console, chip, instrument, the sliders, Direct/ROM, Reset Console: always there
///   view   (ViewX .. ViewRight, ViewY .. ViewBottom)  the Game view (screen + game column), Instruments, Presets, Corrupt, Record, Song
///   song bar (SongBarY .. Height)  the song, its game, Play / Restart / Stop / Reload, the loop position, the warnings (they open the Song view)</summary>
internal static class UiLayout
{
    public const int Width = 1000, Height = 700;
    /// <summary>Kept for code written for the old resizing tabs: the window no longer changes size (BaseWidth = WideWidth = Width).</summary>
    public const int BaseWidth = Width, WideExtra = 0, WideWidth = Width;
    public const int HeaderH = 44, StripW = 248, ViewX = 256, ViewY = 52, ViewRight = Width - 8, ViewBottom = 644, SongBarY = 652;
    public const int ViewW = ViewRight - ViewX, ViewH = ViewBottom - ViewY;
    public const int PanelX = ViewX, PanelY = ViewY, PanelRight = ViewRight, PanelW = PanelRight - PanelX, PanelBottom = ViewBottom;
    /// <summary>Where the header's view buttons start (right of the logo).</summary>
    public const int StripX = 168, StripY = 9, StripH = 26;
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
    public const int SendName = 1194, SendOk = 1195, SendCancel = 1196;       // the name field that "Send to Presets" opens (round 6, presets)
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
    public const int CaptureNote = 1683;          // instrument extraction: "Capture next note" (ROM mode only)
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
    public const int RemoveCallsPush = 1 << 17;      // "Remove from game" pushes instead (round 6, inject)
    public const int NoticeHidden = 1 << 18;         // the model's standing note is not drawn (round 6, inject)
    // ---- the layout overhaul (2026-11-13, layout B): checks `layout`, `chip-picker`, `runaway-tree` (SelfTest.Layout.cs) ----
    public const int LayoutResizes = 1 << 19;        // switching views resizes the window again (the old wide tabs)
    public const int SongBarHidden = 1 << 20;        // the song bar is left out on the views other than Game
    public const int StripHidden = 1 << 21;          // the channel strip is left out on the views other than Game
    public const int ChipsUngrouped = 1 << 22;       // the chip list keeps index order instead of stock / ported / nano / presets
    public const int ShowAllDead = 1 << 23;          // "show all consoles" adds no other console's chip
    public const int TreeNoFold = 1 << 24;           // the Runaway tree lists duplicates one by one
    public const int TreeKindsWrong = 1 << 25;       // every sample of the Runaway tree is filed under Samples (no Drums / Sound effects)
    public const int RenameNotSaved = 1 << 26;       // a renamed instrument's name is left out of the project
    public const int AuditionSilent = 1 << 27;       // a row's play button plays nothing

    public static bool Has(int bit) => (Mask & bit) != 0;
}
