// Round 7 (midi): the "Test MIDI export" button (in the header, next to the plugin's name: every tab shows it) and its window. Open MIDI... (the standard file dialog), the song's name and the
// short facts that explain what will sound different from FL, Play / Restart / Stop, Reload (the same file again after another export from FL), and "follow FL's transport".
// The work happens in Emulator.Song.cs; this file only draws and routes clicks. Control ids 2000-2019 (outside the Emulator tab's < 1000 and the tabs' 1000-1999); editor test hooks 620-629.
using BrokenNes.Fruity;
using BrokenNes2.Music;

namespace BrokenNes2;

public sealed unsafe partial class Bn2Editor
{
    public const int IdMid = 2000, IdMidClose = 2001, IdMidOpen = 2002, IdMidPlay = 2003, IdMidRestart = 2004, IdMidStop = 2005, IdMidReload = 2006, IdMidFollow = 2007;
    private const int MidW = 780, MidH = 650, MidNoteSeconds = 12;      // (650 since round 9: the Capture from FL row and strip, Bn2Editor.Capture.cs)

    private bool midOpen, midNoteShown;
    private string? testMidiPath;

    private static bool IsMidId(int id) => id is >= IdMidClose and <= IdMidFollow;

    /// <summary>The header's button (left of the emulator badge, after the plugin's name and its line).</summary>
    private void DrawMidiButton(nint dc, int x)
    {
        var r = R(Math.Max(x, 330), 13, 150, 26);
        Button(dc, IdMid, "Test MIDI export", r, primary: plugin.Emulator?.SongRunning == true);
    }

    private RECT MidiBox => R((Width - MidW) / 2, (Height - MidH) / 2, MidW, MidH);

    /// <summary>True while the song note should be over the picture: what loading the song did to the game that was there, for <see cref="MidNoteSeconds"/> seconds (never while the window is open: it says the same).</summary>
    private bool SongNoteWanted(Emulator? emu) => emu != null && !midOpen && emu.SongUi.Note is { Length: > 0 } && Environment.TickCount64 - emu.SongUi.NoteTick < MidNoteSeconds * 1000L;

    private void DrawSongNote(nint dc, RECT inner, Emulator? emu)
    {
        midNoteShown = false;
        if (corNoteShown || !SongNoteWanted(emu)) return;
        var box = R(inner.Left, inner.Top, 512, 54);
        Fill(dc, box, Win32.Rgb(46, 32, 12));
        Line_(dc, box.Left, box.Bottom - 1, box.Right, box.Bottom - 1, Warn);
        Txt(dc, "Test MIDI export: " + emu!.SongUi.Note, R(box.Left + 12, box.Top + 4, 488, 46), Win32.DT_LEFT | Win32.DT_WORDBREAK | Win32.DT_VCENTER | Win32.DT_END_ELLIPSIS, Text, fontSmall);
        midNoteShown = true;
    }

    private void DrawMidi(nint dc, Emulator? emu)
    {
        DimBackdrop(dc);
        hits.Clear();                                    // a modal: nothing behind it can be reached
        var box = MidiBox;
        Box(dc, R(box.Left + 4, box.Top + 6, MidW, MidH), Black, -1, 10);
        Box(dc, box, Panel, Accent, 10);
        int lx = box.Left + 30, iw = MidW - 60;
        var ui = emu?.SongUi;
        var held = emu?.Song;
        bool busy = ui?.Busy == true;

        Txt(dc, "Test MIDI export", R(lx, box.Top + 14, 400, 34), Win32.DT_LEFT | Mid, Text, fontTitle);
        if (emu != null) Txt(dc, $"emulator #{emu.Id}", R(box.Right - 30 - 200, box.Top + 14, 200, 34), Win32.DT_RIGHT | Mid, Dim, fontSection);
        Txt(dc, "Capture your song straight from FL (Capture from FL: your BrokenNes2 channels as they are, no MIDI Out channels), or export it from FL as a MIDI file and open it here. BrokenNes2 converts it with VRUN's own music converter and plays it in VRUN's sound-test player in this emulator (NES, ROM mode): what you hear is what the game will play.",
            R(lx, box.Top + 50, iw, 48), Win32.DT_LEFT | Win32.DT_WORDBREAK, Dim, fontSmall);
        Line_(dc, box.Left + 30, box.Top + 102, box.Right - 30, box.Top + 102, Line);

        // the file: Open MIDI..., Capture from FL, Save MIDI... (Bn2Editor.Capture.cs)
        int y = box.Top + 112;
        Button(dc, IdMidOpen, "Open MIDI...", R(lx, y, 150, 30), primary: held == null, enabled: emu != null && !busy);
        DrawCaptureButtons(dc, lx + 160, y, emu);
        string fileText; int fileColor = Text;
        if (emu == null) { fileText = "no emulator"; fileColor = Warn; }
        else if (busy) { fileText = ui!.Status.Length > 0 ? ui.Status : "converting..."; fileColor = Warn; }
        else if (held == null) { fileText = "no MIDI file open"; fileColor = Faint; }
        else fileText = held.Path;
        Txt(dc, fileText, R(lx + 484, y, iw - 484, 30), Win32.DT_LEFT | Mid | 0x4000 /* DT_PATH_ELLIPSIS */, fileColor, fontSmall);
        DrawCaptureStrip(dc, lx, iw, box.Top + 150);

        // the song: its name, where it plays, and the facts that make it sound different from FL
        var song = R(lx, box.Top + 268, iw, 196);
        Box(dc, song, Bg, Line, 6);
        if (held == null)
        {
            Txt(dc, MusicTemplate.Builtin(out var why) == null ? "Test MIDI export cannot run in this build: " + why
                    : "No song yet. Press Capture from FL, then Play in FL from the start and Stop at the end; or in FL Studio: File > Export > MIDI file..., then Open MIDI... here (after a change, export again over the same file and press Reload).",
                R(song.Left + 30, song.Top + 70, iw - 60, 110), Win32.DT_CENTER | Win32.DT_WORDBREAK | Win32.DT_VCENTER, MusicTemplate.Builtin(out _) == null ? Warn : Faint, fontLabel);
        }
        else
        {
            bool running = emu!.SongRunning, stopped = emu.SongStopped;
            string state = !emu.SongActive || emu.Mode != 1 || emu.Console != Cx.Nes ? "NOT THE GAME NOW" : !running ? "LOADING" : emu.Runaway ? "FROZEN (RUNAWAY)" : stopped ? "STOPPED" : "PLAYING";
            int stateColor = state == "PLAYING" ? Good : state == "LOADING" ? Dim : Warn;
            Txt(dc, held.Name, R(song.Left + 14, song.Top + 8, iw - 220, 30), Win32.DT_LEFT | Mid | Win32.DT_END_ELLIPSIS, White, fontBig);
            Txt(dc, state, R(song.Right - 14 - 190, song.Top + 8, 190, 30), Win32.DT_RIGHT | Mid, stateColor, fontSection);
            string where = state == "NOT THE GAME NOW" ? "Another game runs in this emulator: Play puts the song back (NES, ROM mode)"
                : $"plays in emulator #{emu.Id}: NES, ROM mode, sound chip {emu.ChipId}, frame {emu.FramesRun}" + (emu.ChipId != "FIX" ? "   (VRUN itself uses FIX)" : "");
            Txt(dc, where, R(song.Left + 14, song.Top + 40, iw - 28, 18), Win32.DT_LEFT | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS, Dim, fontSmall);
            int ly = song.Top + 64;
            foreach (var line in held.Facts.Lines())
            {
                bool bad = line.StartsWith("truncated") || line.Contains("dropped:") || line.Contains("thinned") || line.Contains("ignored") || line.Contains("not played") || line.Contains("lowest pitch") || line.Contains(" silent") || line.Contains("silent;");
                Dot(dc, song.Left + 20, ly + 10, 3, bad ? Warn : Good);
                Txt(dc, line, R(song.Left + 32, ly, iw - 46, 20), Win32.DT_LEFT | Mid | Win32.DT_END_ELLIPSIS, Text, fontLabel);
                ly += 22;                                                       // (six lines at most: 64 + 6 x 22 = 196, the box's height)
            }
        }

        // Play, Restart, Stop; Reload
        y = box.Top + 474;
        bool canPlay = emu != null && held != null && !busy;
        Button(dc, IdMidPlay, "Play", R(lx, y, 100, 34), primary: true, enabled: canPlay);
        Button(dc, IdMidRestart, "Restart", R(lx + 110, y, 100, 34), primary: false, enabled: canPlay);
        Button(dc, IdMidStop, "Stop", R(lx + 220, y, 100, 34), primary: false, enabled: canPlay && emu!.SongRunning);
        Button(dc, IdMidReload, "Reload", R(lx + 350, y, 110, 34), primary: false, enabled: canPlay);
        Txt(dc, held == null ? "" : $"reads {held.FileName} again: export from FL over it, then Reload", R(lx + 472, y - 2, iw - 472, 38), Win32.DT_LEFT | Win32.DT_WORDBREAK | Win32.DT_VCENTER, Faint, fontSmall);

        y = box.Top + 520;
        Check(dc, IdMidFollow, "Follow FL's transport: FL's Play restarts the song from its start, FL's Stop stops it", lx, y, ui?.Follow == true, null, labelW: 640, hitW: 660);

        // what happened
        y = box.Top + 548;
        string say = ""; int sayColor = Dim;
        if (ui != null)
        {
            if (busy) { say = ui.Status; sayColor = Warn; }
            else if (ui.Result.Length > 0) { say = ui.Result + (ui.ResultGood && ui.Note is { Length: > 0 } n && Environment.TickCount64 - ui.NoteTick < 60_000 ? "   " + n : ""); sayColor = ui.ResultGood ? Good : Warn; }
        }
        if (say.Length > 0)
        {
            Fill(dc, R(lx, y, 3, 44), sayColor);
            Txt(dc, say, R(lx + 12, y, iw - 12, 44), Win32.DT_LEFT | Win32.DT_WORDBREAK | Win32.DT_VCENTER | Win32.DT_END_ELLIPSIS, Text, fontSmall);
        }
        Line_(dc, box.Left + 30, box.Bottom - 54, box.Right - 30, box.Bottom - 54, Line);
        Txt(dc, "The song is kept in the FL project (the MIDI file's bytes). VRUN's engine plays no pitch bend: a captured file keeps them, the game drops them. Whether it replays well is your call.", R(lx, box.Bottom - 48, iw - 120, 36), Win32.DT_LEFT | Win32.DT_WORDBREAK | Win32.DT_VCENTER, Faint, fontSmall);
        Button(dc, IdMidClose, "Close", R(box.Right - 30 - 100, box.Bottom - 46, 100, 34), primary: true);
    }

    /// <summary>The Open button: the file dialog (a test can name the file instead), then the song is read and converted on a worker thread.</summary>
    private void ChooseMidi()
    {
        var emu = plugin.Emulator;
        if (emu == null) return;
        string? path = testMidiPath;
        testMidiPath = null;
        if (path == null)
        {
            string? start = Prefs.Get("MidiFolder");
            path = Win32.ChooseFile(Hwnd, "Open a MIDI file exported from FL Studio", "MIDI files (*.mid;*.midi)|*.mid;*.midi|All files (*.*)|*.*", !string.IsNullOrEmpty(start) && Directory.Exists(start) ? start : null);
            if (path != null && Path.GetDirectoryName(path) is { Length: > 0 } folder) Prefs.Set("MidiFolder", folder);
        }
        if (path == null) return;
        plugin.MarkConfigured("a MIDI test song was opened");
        if (emu.OpenSong(path) is { } refused) emu.SongUi.SetResult(refused, false);
    }

    private void MidiClick(int id)
    {
        var emu = plugin.Emulator;
        if (id == IdMidClose) { midOpen = false; return; }
        if (emu == null) return;
        var ui = emu.SongUi;
        switch (id)
        {
            case IdMidOpen: if (!ui.Busy) ChooseMidi(); break;
            case IdMidPlay: if (emu.Song != null && !ui.Busy) ui.SetResult("Play: " + emu.PlaySong(), true); break;
            case IdMidRestart: if (emu.Song != null && !ui.Busy) ui.SetResult("Restart: " + emu.RestartSong(), true); break;
            case IdMidStop: if (emu.SongRunning) { emu.StopSong(); ui.SetResult("Stopped: Play starts the song again from its start", true); } break;
            case IdMidReload: if (emu.Song != null && !ui.Busy && emu.ReloadSong() is { } why) ui.SetResult("Reload: " + why, false); break;
            case IdMidFollow: ui.Follow = !ui.Follow; break;
        }
    }

    private long SongSignature()
    {
        var e = plugin.Emulator;
        if (e == null) return midOpen ? 1 : 0;
        var u = e.SongUi;
        long h = (midOpen ? 1 : 2) * 31 + u.ResultStamp;
        h = h * 31 + (u.Busy ? 1 : 2); h = h * 31 + u.Status.GetHashCode(); h = h * 31 + (u.Follow ? 1 : 2);
        h = h * 31 + (e.SongStopped ? 1 : 2); h = h * 31 + (e.SongRunning ? 1 : 2); h = h * 31 + (SongNoteWanted(e) ? 1 : 2); h = h * 31 + (e.Song?.Generation ?? 0);
        return h;
    }

    /// <summary>The window's test hooks: 620 open (1/0), 621 the next click on "Open MIDI..." opens the file at the UTF-16 path <c>value</c> instead of the dialog, 622 the song note is drawn over the picture (1/0).</summary>
    private nint SongTest(int n, nint index, nint value) => n switch
    {
        620 => midOpen ? 1 : 0,
        621 => SetTestMidi(System.Runtime.InteropServices.Marshal.PtrToStringUni(value)),
        622 => midNoteShown ? 1 : 0,
        _ => 0,
    };

    private nint SetTestMidi(string? path) { testMidiPath = path; return 0; }
}
