// Layout B, "channel strip + song bar" (the user's pick, 2026-11-13; prototype tab B of the layout-overhauls artifact). One fixed window (Ui/UiIds.cs UiLayout: 1000 x 700, no
// resize call to the host any more):
//
//   header     BrokenNes2 | Game  Instruments  Presets  Corrupt  Record |            Inputs  [emulator]  About
//   strip      (left, always) this channel + its cells, console, Direct/ROM, the chip (grouped; "show every console's chips"), the instrument, the sliders, Reset Console
//   view       Game: the screen, the status line, and a column with the game, Instrument Runaway (+ play N s then freeze) and the game list, or the Runaway tree when frozen
//              Instruments / Presets: the old tabs.  Corrupt / Record / Song: the old modal windows, now views (their controls and ids unchanged; Close goes back to Game).
//   song bar   (bottom, always) the song (.flp or MIDI), Open..., "in <game>", Play in game, Restart, Stop, Reload, the loop position, the facts count (opens the Song view)
//
// Nothing is lost from the old windows: Capture from FL, Save MIDI, Follow FL's transport, Open MIDI, This FL project / Pick .flp, Export sidecar, Reset Console, the Record and Corrupt
// controls all keep their ids and places inside their views. Control ids: Ui/UiIds.cs. Editor test hooks 890 - 899 (LayoutTest below).
using BrokenNes.Fruity;
using BrokenNes2.Music.Flp;
using BrokenNes2.Ui;

namespace BrokenNes2;

public sealed unsafe partial class Bn2Editor
{
    public const int IdAllChips = 62;
    public const int IdBarOpen = 2200, IdBarTarget = 2201, IdBarPlay = 2202, IdBarRestart = 2203, IdBarStop = 2204, IdBarReload = 2205;
    public const int IdFreezeLater = 2220, IdFreezeSeconds = 2221, IdGameRow = 2230;        // + visible row 0..19 of the game list
    private const int GameRows = 20;
    private const int ViewCount = 6;
    /// <summary>The box an old modal window is laid out in as a view: its left margin of 30 lands 8 pixels into the view, its title row (36 pixels) lies under the header and is not drawn.</summary>
    private const int ViewBoxW = UiLayout.ViewW + 30;
    private RECT ViewBox(int w, int h) => R(UiLayout.ViewX - 22, UiLayout.ViewY - 36, w, h);

    private bool showAllChips;
    private int freezeSeconds = 5;
    private string gameColumnMessage = "";

    // the old modal windows are views now: their flags are the view
    private bool corOpen { get => view == 3; set { if (value) SetView(3); else if (view == 3) SetView(0); } }
    private bool recOpen { get => view == 4; set { if (value) SetView(4); else if (view == 4) SetView(0); } }
    private bool midOpen { get => view == 5; set { if (value) SetView(5); else if (view == 5) SetView(0); } }

    // ---------------------------------------------------------------- header

    private static readonly string[] ViewNames = ["Game", "Instruments", "Presets", "Corrupt", "Record"];

    private void ViewButton(nint dc, int id, string label, RECT r, bool on, bool alert = false)
    {
        bool isHot = hot == id;
        Box(dc, r, on ? AccentDim : isHot ? FieldHot : Field, on || alert ? Accent : Line, 5);
        Txt(dc, label, r, Win32.DT_CENTER | Mid, on ? White : alert ? Accent : isHot ? Text : Dim, on ? fontSection : fontLabel);
        AddHit(id, r, id >= UiIds.Tab && id < UiIds.Tab + 3 ? Kind.Ui : Kind.Button);
        if (on) Fill(dc, R(r.Left + 8, r.Bottom + 3, r.Right - r.Left - 16, 3), Accent);
    }

    private void DrawHeader(nint dc, Emulator? emu)
    {
        int x = 12;
        Txt(dc, "Broken", R(x, 4, 200, 36), Win32.DT_LEFT | Mid, Text, fontTitle);
        x += TextWidth(dc, "Broken", fontTitle);
        Txt(dc, "Nes2", R(x, 4, 200, 36), Win32.DT_LEFT | Mid, Accent, fontTitle);

        // the views: Game, Instruments, Presets (the old tab ids), Corrupt (the old Corruption button's id), Record (the old Record button's id)
        int vx = UiLayout.StripX;
        int[] widths = [64, 100, 78, 80, 0];
        for (int i = 0; i < 4; i++)
        {
            var r = R(vx, UiLayout.StripY, widths[i], UiLayout.StripH);
            if (i < 3) ViewButton(dc, UiIds.Tab + i, ViewNames[i], r, view == i);
            else ViewButton(dc, IdCor, "Corrupt", r, view == 3, alert: emu?.Corruption.Applied != null);
            vx += widths[i] + 6;
        }
        DrawRecordButton(dc, R(vx, UiLayout.StripY, 110, UiLayout.StripH));

        // right: Inputs (drops its panel down), the emulator, About
        var about = R(Width - 12 - 64, UiLayout.StripY, 64, UiLayout.StripH);
        Button(dc, IdAbout, "About", about);
        int er = plugin.Get(P.Emulator);
        string emuText = er == 0 ? (emu != null ? $"Auto -> #{emu.Id}" : "Auto (none free)") : $"#{er}";
        if (emu != null) emuText += $"  ({emu.MemberCount})";
        var emuBox = R(about.Left - 8 - 150, UiLayout.StripY - 1, 150, RowH);
        Dropdown(dc, IdEmulator, "", emuBox.Left, emuBox.Top, 150, 0, emuText);
        Button(dc, IdInputs, "Inputs", R(emuBox.Left - 8 - 70, UiLayout.StripY, 70, UiLayout.StripH), primary: inputsOpen);
        Line_(dc, 0, UiLayout.HeaderH, Width, UiLayout.HeaderH, Line);
    }

    // ---------------------------------------------------------------- the chip picker

    /// <summary>The chip's group in the picker: 0 stock, 1 ported (another emulator's engine, bugs kept), 2 nano (the web library's engines), 3 presets.</summary>
    internal static int ChipGroup(Chip c) => c.IsPreset ? 3 : c.IsNanoGb || c.IsNanoSms || c.NanoMore > 0 ? 2 : c.Note != null && (c.Note.StartsWith("ported", StringComparison.OrdinalIgnoreCase) || c.Note.StartsWith("enhanced bridge", StringComparison.OrdinalIgnoreCase)) || Ported(c.Id) ? 1 : 0;
    private static bool Ported(string id) => id.StartsWith("PC_ADLIB_", StringComparison.OrdinalIgnoreCase) && !id.Equals("PC_ADLIB", StringComparison.OrdinalIgnoreCase) || NesEmulator.Systems.CoreCatalog.PortedBridgeEngine(id) != null;
    internal static readonly string[] ChipGroupNames = ["stock", "ported", "nano", "presets"];

    private string ChipValue()
    {
        int c = Math.Clamp(plugin.Get(P.Core), 0, Cx.Chips.Count - 1);
        return $"{Cx.Chips[c].Id}   ({ChipGroupNames[ChipGroup(Cx.Chips[c])]})";
    }

    /// <summary>Whether a chip of ANOTHER console can play this channel in Direct mode: its console offers the channel this instance has (any tone channel when it has none).
    /// Picking one moves the whole emulator to that console (every instance on it), as the console buttons do.</summary>
    private bool UsableHere(int chip)
    {
        var c = Cx.Chips[chip];
        if (c.IsPreset) return false;                                   // (each console's Presets chip is the console's own: the one on NES lists every console's presets)
        var chans = Cx.Channels(c.Console, 0, c.Id);
        int mine = plugin.Assignment.Channel;
        return mine is >= 0 and < Ch.ToneCount ? chans.Contains(mine) : chans.Any(ch => ch < Ch.ToneCount);
    }

    /// <summary>The Sound chip list: this console's chips grouped stock / ported / nano / presets (index order inside a group); with "show every console's chips" (Direct mode only)
    /// the other consoles' chips that can play this channel follow, console by console. The values are chip indices, so nothing about saved chip numbers changes.</summary>
    private ((string, string?)[] Items, int[] Values, int Current) ChipItems()
    {
        int console = plugin.Get(P.Console), mode = plugin.Get(P.Mode);
        var own = Cx.ChipsOf(console);
        if (mode == 1) own = own.Where(i => !Cx.IsPresetChip(i)).ToArray();      // the Presets chips play without a game
        if (!UiMutate.Has(UiMutate.ChipsUngrouped)) own = own.OrderBy(i => ChipGroup(Cx.Chips[i])).ThenBy(i => i).ToArray();
        var items = own.Select(i => (Cx.Chips[i].Id, (string?)(ChipGroupNames[ChipGroup(Cx.Chips[i])] + (Cx.Chips[i].Note is { } nt ? "  -  " + nt : "")))).ToList();
        var values = own.ToList();
        openGroupStarts = new HashSet<int>(Enumerable.Range(1, Math.Max(0, own.Length - 1)).Where(k => ChipGroup(Cx.Chips[own[k]]) != ChipGroup(Cx.Chips[own[k - 1]])));
        if (showAllChips && mode == 0 && !UiMutate.Has(UiMutate.ShowAllDead))
        {
            for (int other = 0; other < Cx.Count; other++)
            {
                if (other == console) continue;
                var more = Cx.ChipsOf(other).Where(UsableHere).OrderBy(i => ChipGroup(Cx.Chips[i])).ThenBy(i => i).ToArray();
                if (more.Length == 0) continue;
                openGroupStarts.Add(items.Count);
                foreach (int i in more)
                {
                    items.Add(($"{Cx.ShortNames[other]}: {Cx.Chips[i].Id}", $"{ChipGroupNames[ChipGroup(Cx.Chips[i])]}  -  moves the emulator to {Cx.Names[other]}"));
                    values.Add(i);
                }
            }
        }
        return (items.ToArray(), values.ToArray(), plugin.Get(P.Core));
    }

    /// <summary>Rows of the open list where a new group starts (a rule is drawn above them). Empty for every list but the chip list.</summary>
    private HashSet<int> openGroupStarts = new();

    // ---------------------------------------------------------------- the song bar

    /// <summary>A fact of the song that means it will sound different from FL (the Song view's amber dots).</summary>
    internal static bool IsBadFact(string line) => line.StartsWith("truncated") || line.Contains("dropped:") || line.Contains("thinned") || line.Contains("ignored") || line.Contains("not played") || line.Contains("lowest pitch") || line.Contains(" silent") || line.Contains("silent;");

    private void DrawSongBar(nint dc, Emulator? emu)
    {
        int top = UiLayout.SongBarY, y = top + 9, h = 30;
        Fill(dc, R(0, top, Width, Height - top), Panel);
        Line_(dc, 0, top, Width, top, Line);
        var ui = emu?.SongUi;
        var held = emu?.Song;
        bool busy = ui?.Busy == true;
        int x = 12;
        Txt(dc, "SONG", R(x, y, 44, h), Win32.DT_LEFT | Mid, Accent, fontSection);
        x += 46;
        string name = busy ? (ui!.Status.Length > 0 ? ui.Status : "converting...") : held != null ? held.FileName : plugin.ProjectFlpPath is { } pf ? Path.GetFileName(pf) : "no song";
        Txt(dc, name, R(x, y, 150, h), Win32.DT_LEFT | Mid | Win32.DT_END_ELLIPSIS, busy ? Warn : held != null ? Text : Faint, fontLabel);
        x += 154;
        Button(dc, IdBarOpen, held == null ? "Open..." : "Change", R(x, y, 70, h), primary: false, enabled: emu != null && !busy);
        x += 76;
        Dropdown(dc, IdBarTarget, "", x, y, 150, 0, "in " + (emu?.FlpTarget.Label ?? "VRUN NES"));
        x += 156;
        bool canPlay = emu != null && !busy;
        bool playing = emu?.SongRunning == true && emu.SongActive && !emu.SongStopped;
        Button(dc, IdBarPlay, playing ? "Playing" : "Play in game", R(x, y, 110, h), primary: true, enabled: canPlay);
        x += 114;
        Button(dc, IdBarRestart, "Restart", R(x, y, 68, h), primary: false, enabled: canPlay && held != null);
        x += 72;
        Button(dc, IdBarStop, "Stop", R(x, y, 52, h), primary: false, enabled: canPlay && emu!.SongRunning);
        x += 56;
        Button(dc, IdBarReload, "Reload", R(x, y, 64, h), primary: false, enabled: canPlay && held != null);
        x += 70;

        // the warnings: how many facts say the song will sound different from FL; the button opens the Song view (the old Test MIDI export window: all the facts, Capture from FL, Save MIDI...)
        int bad = held == null ? 0 : held.Facts.Lines().Count(IsBadFact);
        var facts = R(Width - 12 - 92, y, 92, h);
        Button(dc, IdMid, held == null ? "Song view" : bad == 0 ? "facts: OK" : $"{bad} warning{(bad == 1 ? "" : "s")}", facts, primary: view == 5);
        if (bad > 0 && view != 5) Box(dc, R(facts.Left, facts.Bottom - 3, facts.Right - facts.Left, 3), Warn, -1, 1);

        // the loop position: frames since the song started, over its loop length
        var meter = R(x, y + 12, facts.Left - 8 - 70 - x, 6);
        Box(dc, meter, Bg, -1, 3);
        string time = "";
        if (held != null && emu!.SongRunning && emu.SongActive && held.Facts.LoopFrames > 0)
        {
            long f = Math.Max(0, emu.FramesRun - Music.MusicTemplate.Preroll);
            long pos = f % held.Facts.LoopFrames;
            int fill = (int)((meter.Right - meter.Left) * pos / held.Facts.LoopFrames);
            if (fill > 0) Box(dc, R(meter.Left, meter.Top, fill, 6), playing ? Good : Dim, -1, 3);
            time = $"{Clock(pos)} / {Clock(held.Facts.LoopFrames)}";
            songBarFraction = (int)(1000 * pos / held.Facts.LoopFrames);
        }
        else songBarFraction = -1;
        Txt(dc, time, R(meter.Right + 4, y, 70, h), Win32.DT_RIGHT | Mid, Dim, fontSmall);
    }

    private int songBarFraction = -1;
    private static string Clock(long frames) { long s = (long)(frames / 60.0988); return $"{s / 60}:{s % 60:00}"; }

    private void SongBarClick(int id)
    {
        var emu = plugin.Emulator;
        if (emu == null) return;
        var ui = emu.SongUi;
        switch (id)
        {
            case IdBarOpen: if (!ui.Busy) ChooseMidi(); break;
            case IdBarPlay:
                // "Play in game": the song held plays (again); none yet: this FL project's .flp when one was picked, else the file dialog
                if (ui.Busy) break;
                if (emu.Song != null) MidiClick(IdMidPlay);
                else if (plugin.ProjectFlpPath != null) UseProjectFlp(change: false);
                else ChooseMidi();
                break;
            case IdBarRestart: MidiClick(IdMidRestart); break;
            case IdBarStop: MidiClick(IdMidStop); break;
            case IdBarReload: MidiClick(IdMidReload); break;
        }
    }

    // ---------------------------------------------------------------- the Game view's column

    private const int GameColX = UiLayout.ViewX + ScreenW + 8, GameColW = UiLayout.ViewRight - GameColX;

    private void DrawGameColumn(nint dc, Emulator? emu)
    {
        int x = GameColX, w = GameColW, y = UiLayout.ViewY;
        int mode = plugin.Get(P.Mode);
        Section(dc, "GAME", mode == 1 ? "ROM mode" : "Direct mode", x, y, w);
        y += 26;
        Dropdown(dc, IdGame, "", x, y, w, 0, emu?.RomDisplayName ?? "-", dim: mode != 1);
        y += RowH + 4;
        string about = emu == null ? "" : mode != 1 ? "Direct mode: the chip plays FL's notes. ROM mode runs a game." : emu.RomLoaded ? emu.RomDescription : emu.RomStatus;
        Txt(dc, about, R(x, y, w, 32), Win32.DT_LEFT | Win32.DT_WORDBREAK | Win32.DT_END_ELLIPSIS, Faint, fontSmall);
        y += 36;

        // Instrument Runaway, and "play N s, then freeze" (the game voices are learned from what the sound driver does meanwhile)
        bool frozen = emu?.Runaway == true, canRun = emu?.CanRunaway == true;
        Section(dc, "INSTRUMENT RUNAWAY", null, x, y, w);
        y += 26;
        var runBtn = R(x, y, w, 30);
        if (canRun || frozen) Button(dc, IdRunaway, frozen ? "Runaway ON - release" : "Instrument Runaway", runBtn, primary: frozen);
        else { Box(dc, runBtn, Panel, Line, 5); Txt(dc, "Instrument Runaway", runBtn, Win32.DT_CENTER | Mid, Faint, fontLabel); }
        y += 34;
        bool listening = emu?.Corruption.Listening.Length > 0;
        Button(dc, IdFreezeLater, listening ? "listening..." : $"Play {freezeSeconds} s, then freeze", R(x, y, w - 52, 26), primary: false, enabled: canRun && !frozen && !listening);
        Button(dc, IdFreezeSeconds, $"{freezeSeconds} s", R(x + w - 48, y, 48, 26), primary: false);
        y += 30;
        string hint = gameColumnMessage.Length > 0 ? gameColumnMessage : frozen ? (emu!.SamplerMode || emu.WmsSamplerMode ? "FL notes play its samples" : emu.GbSamplerMode ? "FL notes play its channels" : emu.SegaSamplerMode ? "FL notes play its chips" : "frozen and silent") : canRun ? "freeze it, keep its sounds" : "needs ROM mode and a game";
        Txt(dc, hint, R(x, y, w, 30), Win32.DT_LEFT | Win32.DT_WORDBREAK | Win32.DT_END_ELLIPSIS, Faint, fontSmall);
        y += 34;

        if (frozen && DrawRunawayTree(dc, emu!, x, y, w, UiLayout.ViewBottom - y)) return;     // the tree of the frozen game's instruments (Bn2Editor.Tree.cs)

        // the game list: the Game dropdown's choices, one click each
        Section(dc, "GAME LIST", null, x, y, w);
        y += 26;
        var (items, values, current) = BuildItems(IdGame);
        gameRowValues = values;
        for (int r = 0; r < items.Length && r < GameRows && y + 24 <= UiLayout.ViewBottom; r++, y += 26)
        {
            var row = R(x, y, w, 24);
            bool on = values[r] == current, isHot = hot == IdGameRow + r;
            Box(dc, row, isHot ? FieldHot : Field, on ? Accent : Line, 4);
            Txt(dc, items[r].Item1, R(row.Left + 8, row.Top, w - 16, 24), Win32.DT_LEFT | Mid | Win32.DT_END_ELLIPSIS, on ? White : Text, fontSmall);
            AddHit(IdGameRow + r, row, Kind.Button);
        }
    }

    private int[] gameRowValues = [];

    // ---------------------------------------------------------------- events, signature, test hooks

    /// <summary>The new controls' clicks (from <see cref="Activate"/>). False when the id is not one of them.</summary>
    private bool LayoutClick(int id)
    {
        if (id == IdAllChips) { showAllChips = !showAllChips; return true; }
        if (id is >= IdBarOpen and <= IdBarReload) { SongBarClick(id); return true; }
        if (id == IdFreezeSeconds) { int at = Array.IndexOf(CorruptionPanel.PlaySteps, freezeSeconds); freezeSeconds = CorruptionPanel.PlaySteps[(at + 1) % CorruptionPanel.PlaySteps.Length]; if (freezeSeconds == 0) freezeSeconds = CorruptionPanel.PlaySteps[(at + 2) % CorruptionPanel.PlaySteps.Length]; return true; }
        if (id == IdFreezeLater) { var e = plugin.Emulator; if (e != null) { plugin.MarkConfigured("Instrument Runaway"); gameColumnMessage = e.FreezeLater(freezeSeconds); } return true; }
        if (id >= IdGameRow && id < IdGameRow + GameRows) { int r = id - IdGameRow; if (r < gameRowValues.Length) PickGame(gameRowValues[r]); return true; }
        if (IsTreeId(id)) { TreeClick(id); return true; }
        return false;
    }

    private long LayoutSignature()
    {
        var e = plugin.Emulator;
        long h = view * 31 + (showAllChips ? 7 : 3) + freezeSeconds * 101 + gameColumnMessage.GetHashCode();
        if (e != null) { h = h * 31 + e.Corruption.Listening.GetHashCode(); h = h * 31 + (e.FramesRun / 15); h = h * 31 + e.RunawayNamesVersion; h = h * 31 + (e.Song?.Generation ?? 0); }
        return h * 31 + TreeSignature();
    }

    /// <summary>Editor test hooks 890 - 899: 890 the view (0 Game, 1 Instruments, 2 Presets, 3 Corrupt, 4 Record, 5 Song); 891 set the view to <c>value</c>; 892 "show every console's chips" (1/0);
    /// 893 the song bar's loop position (0..1000, -1 none); 894 the open list's group starts as a bit mask of its first 31 rows; 895 .. 899 the Runaway tree (Bn2Editor.Tree.cs).</summary>
    private nint LayoutTest(int n, nint index, nint value)
    {
        switch (n)
        {
            case 890: return view;
            case 891: SetView((int)value); return 0;
            case 892: return showAllChips ? 1 : 0;
            case 893: return songBarFraction;
            case 894: { long m = 0; foreach (int k in openGroupStarts) if (k < 31) m |= 1L << k; return (nint)m; }
            default: return TreeTest(n, index, value);
        }
    }
}
