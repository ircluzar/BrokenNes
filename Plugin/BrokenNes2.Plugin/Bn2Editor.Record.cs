// Round 8 (record): the "Record" button (in the header, right of Test MIDI export: every tab shows it) and its window. Record / Stop, the options (start with FL's Play, stop with FL's Stop,
// lanes), the state and what the transcription found, which track this channel plays, the tracks (each one a drag handle for its own MIDI file, and "Play here"), live MIDI out, the host log,
// the whole song's drag handle, Save MIDI..., Add presets, and the offer of the tracks to untouched channels (TrackOffer.cs). The work happens in Emulator.Record.cs and Bn2Plugin.Tracks.cs;
// this file draws and routes. Control ids 2020-2054 used (2020-2079 reserved); editor test hooks 670-679.
using BrokenNes.Fruity;
using BrokenNes2.Record;
using BrokenNes2.Ui;

namespace BrokenNes2;

public sealed unsafe partial class Bn2Editor
{
    public const int IdRec = 2020, IdRecClose = 2021, IdRecStart = 2022, IdRecStop = 2023, IdRecSave = 2024, IdRecPresets = 2025, IdRecDeal = 2026,
        IdRecOptStart = 2027, IdRecOptStop = 2028, IdRecOptLanes = 2029, IdRecDragAll = 2030, IdRecLive = 2031, IdRecPortDown = 2032, IdRecPortUp = 2033, IdRecLog = 2034, IdRecExpr = 2035,
        IdRecTrack = 2040, IdRecHere = 2050;          // + visible track row 0..RecRows-1 (the row's drag handle, its "Play here")
    private const int RecW = 820, RecH = 624, RecRows = 5;

    /// <summary>The recording the window lists: this emulator's, else the last one on offer (an instance that replaced an imported channel sits on another emulator).</summary>
    private static RecordHeld? ShownRecording(Emulator? emu) => emu?.Recording ?? TrackOffers.Current?.Recordings[^1].Held;

    private bool recOpen;
    private int recScroll, dragArm, dragX, dragY;
    private string? testSavePath;
    private string recMessage = "";
    private bool recMessageGood = true;

    private static bool IsRecId(int id) => id is >= IdRecClose and < IdRecHere + RecRows;
    private static bool IsRecDragId(int id) => id == IdRecDragAll || id is >= IdRecTrack and < IdRecTrack + RecRows;

    /// <summary>The header's button: right of Test MIDI export, clear of the tab strip.</summary>
    private void DrawRecordButton(nint dc, int x)
    {
        var emu = plugin.Emulator;
        int st = emu?.RecordUi.State ?? 0;
        int w = Math.Min(64, UiLayout.StripX - 4 - x);
        if (w < 40) return;
        Button(dc, IdRec, st == RecordPanel.Recording ? "REC" : st == RecordPanel.Armed ? "armed" : "Record", R(x, 13, w, 26), primary: st is RecordPanel.Recording or RecordPanel.Armed);
    }

    private RECT RecordBox => R((Width - RecW) / 2, (Height - RecH) / 2, RecW, RecH);

    private void DrawRecord(nint dc, Emulator? emu)
    {
        DimBackdrop(dc);
        hits.Clear();                                    // a modal: nothing behind it can be reached
        var box = RecordBox;
        Box(dc, R(box.Left + 4, box.Top + 6, RecW, RecH), Black, -1, 10);
        Box(dc, box, Panel, Accent, 10);
        int lx = box.Left + 30, iw = RecW - 60;
        var ui = emu?.RecordUi;
        var held = emu?.Recording;
        var rec = emu?.Recorder;
        int st = ui?.State ?? 0;

        Txt(dc, "Record the music", R(lx, box.Top + 14, 400, 34), Win32.DT_LEFT | Mid, Text, fontTitle);
        if (emu != null) Txt(dc, $"emulator #{emu.Id}", R(box.Right - 30 - 200, box.Top + 14, 200, 34), Win32.DT_RIGHT | Mid, Dim, fontSection);
        Txt(dc, "Record keeps every note the game's sound chip plays (SNES games), from Record to Stop: one MIDI track and one preset per instrument. Drag the MIDI file into FL, give each track to a BrokenNes2 on the Presets chip: it replays as the game played it.",
            R(lx, box.Top + 50, iw, 40), Win32.DT_LEFT | Win32.DT_WORDBREAK, Dim, fontSmall);
        Line_(dc, lx, box.Top + 94, box.Right - 30, box.Top + 94, Line);

        // Record, Stop, Save MIDI..., Add presets
        int y = box.Top + 104;
        bool busy = st == RecordPanel.Finishing;
        Button(dc, IdRecStart, st == RecordPanel.Armed ? "Armed" : "Record", R(lx, y, 110, 30), primary: st is RecordPanel.Idle or RecordPanel.Done, enabled: emu != null && st is RecordPanel.Idle or RecordPanel.Done);
        Button(dc, IdRecStop, "Stop", R(lx + 118, y, 90, 30), primary: st is RecordPanel.Recording or RecordPanel.Armed, enabled: st is RecordPanel.Recording or RecordPanel.Armed);
        Button(dc, IdRecSave, "Save MIDI...", R(lx + 216, y, 130, 30), primary: false, enabled: held != null && !busy);
        Button(dc, IdRecPresets, "Add presets", R(lx + 354, y, 130, 30), primary: false, enabled: held != null && !busy);
        Txt(dc, held == null ? "" : held.PresetNote, R(lx + 494, y - 2, iw - 494, 34), Win32.DT_LEFT | Win32.DT_WORDBREAK | Win32.DT_VCENTER | Win32.DT_END_ELLIPSIS, Faint, fontSmall);

        // the options
        y = box.Top + 144;
        Check(dc, IdRecOptStart, "Start with FL's Play", lx, y, ui?.StartWithTransport == true, null, labelW: 200, hitW: 230);
        Check(dc, IdRecOptStop, "Stop with FL's Stop", lx + 250, y, ui?.StopWithTransport == true, null, labelW: 200, hitW: 230);
        Check(dc, IdRecOptLanes, "Lanes (exact: more tracks)", lx + 500, y, ui?.Lanes == true, null, labelW: 230, hitW: 260);

        // the state, and what the transcription found
        var sb = R(lx, box.Top + 176, iw, 134);
        Box(dc, sb, Bg, Line, 6);
        string head; int headColor;
        switch (st)
        {
            case RecordPanel.Armed: head = "ARMED"; headColor = Warn; break;
            case RecordPanel.Recording: head = "RECORDING"; headColor = Accent; break;
            case RecordPanel.Finishing: head = "TRANSCRIBING"; headColor = Warn; break;
            case RecordPanel.Done: head = held?.Session.Name ?? "DONE"; headColor = White; break;
            default: head = emu == null ? "NO EMULATOR" : "READY"; headColor = Dim; break;
        }
        Txt(dc, head, R(sb.Left + 14, sb.Top + 6, iw - 28, 30), Win32.DT_LEFT | Mid | Win32.DT_END_ELLIPSIS, headColor, fontBig);
        int ly = sb.Top + 38;
        if (st is RecordPanel.Armed or RecordPanel.Recording or RecordPanel.Finishing || held == null)
        {
            string text = ui?.Status is { Length: > 0 } s0 ? s0 : emu == null ? "" : emu.Mode != 1 || emu.Console != Cx.Snes ? "Load a SNES game in ROM mode (the Game list), let the music play, then Record." : "Press Record: the recording starts at once (or with FL's Play, or when Instrument Runaway is switched off), Stop ends it.";
            Txt(dc, text, R(sb.Left + 14, ly, iw - 28, 88), Win32.DT_LEFT | Win32.DT_WORDBREAK, Text, fontLabel);
        }
        else
        {
            foreach (var line in held.Tr.Facts.Take(4))
            {
                bool bad = line.Contains("not replayed") || line.Contains("lost") || line.Contains("could not") || line.Contains("no steady") || line.Contains("off by") || line.Contains("cut short") || line.Contains("share");
                Dot(dc, sb.Left + 20, ly + 9, 3, bad ? Warn : Good);
                Txt(dc, line, R(sb.Left + 32, ly, iw - 46, 22), Win32.DT_LEFT | Mid | Win32.DT_END_ELLIPSIS, Text, fontSmall);
                ly += 23;
            }
        }

        // this channel: which track it plays, or how it will find it
        Txt(dc, plugin.TrackStatus(), R(lx, box.Top + 314, iw - 300, 24), Win32.DT_LEFT | Mid | Win32.DT_END_ELLIPSIS, plugin.Binding != null ? Good : Text, fontSmall);
        Check(dc, IdRecExpr, "Bends + volume from the recording", lx + iw - 290, box.Top + 317, TrackOffers.ExpressionFromRecording, null, labelW: 260, hitW: 290);

        // the tracks: each row is a drag handle for its own MIDI file (drop it on a channel's piano roll in FL); "Play here" gives that track to this channel by hand
        y = box.Top + 342;
        var shown = ShownRecording(emu);
        var offered = TrackOffers.Current?.Recordings.FirstOrDefault(r => ReferenceEquals(r.Held, shown));
        Txt(dc, "TRACKS", R(lx, y, 120, 18), Win32.DT_LEFT | Mid, Accent, fontSection);
        Txt(dc, shown == null ? "" : (ReferenceEquals(shown, held) ? "" : $"of \"{shown.Session.Name}\" (emulator #{offered?.EmulatorId}): ") + "drag a row onto a channel's piano roll, or the whole song below", R(lx + 120, y, iw - 120, 18), Win32.DT_RIGHT | Mid | Win32.DT_END_ELLIPSIS, Faint, fontSmall);
        y += 22;
        var tracks = shown?.Tr.Tracks ?? [];
        var mine = plugin.Binding;
        recScroll = Math.Clamp(recScroll, 0, Math.Max(0, tracks.Count - RecRows));
        for (int r = 0; r < RecRows; r++)
        {
            var row = R(lx, y + r * 26, iw - 104, 24);
            int k = recScroll + r;
            if (k >= tracks.Count) { if (r == 0 && shown == null) Txt(dc, "(the tracks of the recording are listed here)", row, Win32.DT_LEFT | Mid, Faint, fontSmall); continue; }
            var t = tracks[k];
            bool hotRow = hot == IdRecTrack + r || dragArm == IdRecTrack + r;
            bool here = mine != null && shown != null && mine.SessionId == shown.Session.Id && mine.Track == k;
            Box(dc, row, hotRow ? FieldHot : Field, here ? Accent : Line, 4);
            Txt(dc, "::", R(row.Left + 6, row.Top, 14, 24), Win32.DT_LEFT | Mid, Dim, fontLabel);
            Txt(dc, t.Name, R(row.Left + 22, row.Top, 320, 24), Win32.DT_LEFT | Mid | Win32.DT_END_ELLIPSIS, Text, fontSmall);
            int playing = offered != null && k < offered.Slots.Length ? offered.Slots[k].Playing : 0;
            string info = $"{t.Notes.Count} notes, ch {t.Channel + 1}, bend {t.BendRange}, {(t.Root.Found ? RecRoot.NoteName((int)Math.Round(t.Root.Midi)) + " root" : "no root")}{(playing > 0 ? $", {playing} channel{(playing == 1 ? "" : "s")}" : "")}";
            Txt(dc, info, R(row.Left + 346, row.Top, row.Right - row.Left - 352, 24), Win32.DT_RIGHT | Mid | Win32.DT_END_ELLIPSIS, Dim, fontSmall);
            AddHit(IdRecTrack + r, row, Kind.Button);
            Button(dc, IdRecHere + r, here ? "Plays here" : "Play here", R(lx + iw - 96, y + r * 26, 96, 24), primary: here, enabled: emu != null);
        }
        if (tracks.Count > RecRows) Txt(dc, $"{recScroll + 1}-{Math.Min(tracks.Count, recScroll + RecRows)} of {tracks.Count}: the wheel scrolls", R(lx, y + RecRows * 26, iw, 16), Win32.DT_RIGHT | Win32.DT_SINGLELINE, Faint, fontSmall);

        // live MIDI out (P2): the game's notes to FL's MIDI port while it plays
        y = box.Top + 506;
        var lv = emu?.Live;
        bool liveOn = lv is { Ended: false };
        Check(dc, IdRecLive, "Live MIDI out", lx, y + 3, liveOn, null, labelW: 130, hitW: 160);
        Button(dc, IdRecPortDown, "-", R(lx + 166, y, 26, 26), primary: false, enabled: emu != null);
        Txt(dc, $"port {emu?.LivePort ?? 0}", R(lx + 194, y, 66, 26), Win32.DT_CENTER | Mid, Text, fontSmall);
        Button(dc, IdRecPortUp, "+", R(lx + 262, y, 26, 26), primary: false, enabled: emu != null);
        string liveText = liveOn ? "live: " + lv!.Describe() : "the game's notes as it plays, one MIDI channel per instrument, to this MIDI port";
        Txt(dc, liveText, R(lx + 300, y, iw - 300 - 186, 26), Win32.DT_LEFT | Mid | Win32.DT_END_ELLIPSIS, liveOn ? Text : Faint, fontSmall);
        Check(dc, IdRecLog, "Log FL's calls", lx + iw - 176, y + 3, HostLog.On, null, labelW: 150, hitW: 176);

        // the whole song's drag handle, and the offer
        y = box.Top + 538;
        var drag = R(lx, y, 380, 34);
        bool canDrag = shown != null && !busy;
        Box(dc, drag, canDrag ? (hot == IdRecDragAll || dragArm == IdRecDragAll ? Accent : AccentDim) : Panel, canDrag ? Accent : Line, 6);
        Txt(dc, canDrag ? $"::  Drag {shown!.MidiName} into FL  ({tracks.Count} tracks)" : "::  the MIDI file to drag into FL", drag, Win32.DT_CENTER | Mid | Win32.DT_END_ELLIPSIS, canDrag ? White : Faint, fontLabel);
        AddHit(IdRecDragAll, drag, Kind.Button);
        int takenBy = offered?.Slots.Sum(s => s.Playing) ?? 0;
        Button(dc, IdRecDeal, offered != null ? $"Tracks on offer ({takenBy} channel{(takenBy == 1 ? "" : "s")}): stop" : "Offer the tracks to channels", R(lx + 392, y, iw - 392, 34), primary: offered != null, enabled: shown != null && !busy);

        // what happened, and Close
        string say = recMessage.Length > 0 ? recMessage : ui?.Result ?? "";
        bool good = recMessage.Length > 0 ? recMessageGood : ui?.ResultGood ?? true;
        if (emu?.RecordProblem is { } prob && say.Length == 0) { say = prob; good = false; }
        if (say.Length > 0)
        {
            Fill(dc, R(lx, box.Bottom - 46, 3, 34), good ? Good : Warn);
            Txt(dc, say, R(lx + 10, box.Bottom - 48, iw - 130, 38), Win32.DT_LEFT | Win32.DT_WORDBREAK | Win32.DT_VCENTER | Win32.DT_END_ELLIPSIS, Text, fontSmall);
        }
        Button(dc, IdRecClose, "Close", R(box.Right - 30 - 100, box.Bottom - 46, 100, 34), primary: true);
        _ = rec;
    }

    private void Say(string text, bool good) { recMessage = text; recMessageGood = good; }

    private void RecordClick(int id)
    {
        var emu = plugin.Emulator;
        if (id == IdRecClose) { recOpen = false; recMessage = ""; return; }
        if (emu == null) { Say("This instance has no emulator.", false); return; }
        var ui = emu.RecordUi;
        recMessage = "";
        switch (id)
        {
            case IdRecStart:
                plugin.MarkConfigured("Record");
                if (emu.StartRecording() is { } why) Say("Not recording: " + why, false);
                break;
            case IdRecStop:
                if (ui.State is RecordPanel.Armed or RecordPanel.Recording) emu.StopRecording();
                else Say("Nothing is being recorded.", false);
                break;
            case IdRecSave:
            {
                var held = emu.Recording;
                if (held == null) { Say("Nothing to save: record first.", false); break; }
                string? path = testSavePath;
                testSavePath = null;
                path ??= UiNative.ChooseSaveFile(Hwnd, "Save the recording as a MIDI file", "MIDI files (*.mid)|*.mid|All files (*.*)|*.*", "mid");
                if (path == null) { Say("Save cancelled.", true); break; }
                var (ok, msg) = emu.SaveRecordingMidi(path);
                Say(msg, ok);
                break;
            }
            case IdRecPresets: Say(emu.Recording == null ? "Record first." : "Presets: " + emu.AddRecordingPresets(), emu.Recording != null); break;
            case IdRecDeal:
            {
                var shown = ShownRecording(emu);
                var offered = TrackOffers.Current?.Recordings.FirstOrDefault(r => ReferenceEquals(r.Held, shown));
                if (offered != null) { TrackOffers.Withdraw(offered.EmulatorId); Say("The tracks are no longer on offer: channels that play one keep it; new ones do not pick one up from their notes.", true); }
                else { var (ok, msg) = emu.OfferTracks(); Say(msg, ok); }
                break;
            }
            case IdRecExpr:
                TrackOffers.SetExpression(!TrackOffers.ExpressionFromRecording);
                Say(TrackOffers.ExpressionFromRecording ? "Channels that play a track take its pitch bends and volume from the recording, at the song position (FL's MIDI import loses controller 7 and reads bends at 2 semitones)." : "Channels that play a track follow FL's own pitch and volume (the channel's events, which you can edit).", true);
                break;
            case IdRecLog:
                HostLog.Set(!HostLog.On);
                Say(HostLog.On ? $"Logging what FL sends to BrokenNes2 (every new channel's first calls, notes and song positions) into {HostLog.FilePath}. It stays on when FL restarts; untick it when done and send the file." : "The log is off.", true);
                break;
            case IdRecLive:
                if (emu.Live is { Ended: false }) { emu.StopLive(); Say("Live MIDI out is off (the sounding notes were ended).", true); }
                else if (plugin.StartLiveMidi() is { } whyLive) Say("No live MIDI out: " + whyLive, false);
                else Say($"Live MIDI out is on: the game's notes go to MIDI port {emu.LivePort} while it plays. In FL, set the MIDI input port of the channels that should play them to {emu.LivePort} (one MIDI channel per instrument).", true);
                break;
            case IdRecPortDown: emu.LivePort--; break;
            case IdRecPortUp: emu.LivePort++; break;
            case IdRecOptStart: ui.StartWithTransport = !ui.StartWithTransport; break;
            case IdRecOptStop: ui.StopWithTransport = !ui.StopWithTransport; break;
            case IdRecOptLanes:
                ui.Lanes = !ui.Lanes;
                if (emu.Recording != null && emu.Retranscribe() is { } why2) Say("The tracks are not made again: " + why2, false);
                break;
            default:
                if (IsRecDragId(id)) Say("Hold the button and drag the handle into FL (onto the channel rack, the playlist or a piano roll); or use Save MIDI...", true);
                else if (id is >= IdRecHere and < IdRecHere + RecRows) PlayHere(emu, recScroll + id - IdRecHere);
                break;
        }
    }

    /// <summary>"Play here": this channel plays a track of the listed recording, picked by hand (the recording's tracks are put on offer first when they are not).</summary>
    private void PlayHere(Emulator emu, int k)
    {
        var shown = ShownRecording(emu);
        if (shown == null) { Say("Record first: there are no tracks yet.", false); return; }
        var offered = TrackOffers.Current?.Recordings.FirstOrDefault(r => ReferenceEquals(r.Held, shown));
        if (offered == null && ReferenceEquals(shown, emu.Recording)) { emu.OfferTracks(); offered = TrackOffers.Current?.Recordings.FirstOrDefault(r => ReferenceEquals(r.Held, shown)); }
        if (offered == null || (uint)k >= (uint)offered.Slots.Length) { Say("That track is not on offer any more.", false); return; }
        var slot = offered.Slots[k];
        if (plugin.PickTrack(slot) is { } why) Say($"Not played here: {why}", false);
        else Say($"This channel plays track {k + 1} ({slot.Info.Name}) on emulator #{plugin.Emulator?.Id}, on the Presets chip.", true);
    }

    /// <summary>A drag handle was pressed and the mouse moved: the drag starts (it returns when the button is released).</summary>
    private void RecordDrag(int id)
    {
        var held = ShownRecording(plugin.Emulator);
        if (held == null) { Say("Record first: there is nothing to drag yet.", false); return; }
        string path;
        if (id == IdRecDragAll) path = RecMutants.On(RecMutants.DragWrongFile) && held.TrackPaths.Length > 0 ? held.TrackPaths[0] : held.MidiPath;      // (the self-test's deliberate bug)
        else
        {
            int k = recScroll + id - IdRecTrack;
            if ((uint)k >= (uint)held.TrackPaths.Length) return;
            path = held.TrackPaths[k];
        }
        var (ok, msg) = DragOut.DragFile(Hwnd, path);
        Say(msg, ok);
    }

    private long RecordSignature()
    {
        var e = plugin.Emulator;
        long h = recOpen ? 1 + recScroll : 0;
        h = h * 31 + dragArm; h = h * 31 + recMessage.GetHashCode();
        var offer = TrackOffers.Current;
        h = h * 31 + (offer?.Version ?? 0);
        if (recOpen && offer != null) foreach (var s in offer.Slots) h = h * 31 + s.Playing;
        h = h * 31 + (plugin.Binding?.Track ?? -1) + plugin.Matcher.Notes * 7 + (HostLog.On ? 3 : 5) + (TrackOffers.ExpressionFromRecording ? 11 : 13);
        if (e == null) return h;
        var u = e.RecordUi;
        h = h * 31 + u.State; h = h * 31 + u.ResultStamp; h = h * 31 + u.Status.GetHashCode();
        h = h * 31 + (u.StartWithTransport ? 1 : 2) + (u.StopWithTransport ? 4 : 8) + (u.Lanes ? 16 : 32);
        h = h * 31 + (e.Recording?.Generation ?? 0); h = h * 31 + (e.Recording?.PresetNote.GetHashCode() ?? 0);
        if (recOpen && e.Live is { } lv) { h = h * 31 + lv.NotesOn + lv.Instruments * 7919 + (lv.Ended ? 1 : 2); }
        h = h * 31 + e.LivePort;
        return h;
    }

    /// <summary>The window's test hooks: 670 open (1/0), 671 the next Save MIDI... writes to the UTF-16 path <c>value</c> instead of the dialog, 672 drag test mode (value 1/0: everything but the modal drag loop),
    /// 673 copy the last drag's file asked for / carried / outcome (field = index >> 16: 0 asked, 1 carried, 2 outcome; max = index &amp; 0xFFFF; returns the length), 674 drags started, 675 copy the window's message (as 673), 676 the track list's first row.</summary>
    private nint RecordTest(int n, nint index, nint value)
    {
        switch (n)
        {
            case 670: return recOpen ? 1 : 0;
            case 671: testSavePath = System.Runtime.InteropServices.Marshal.PtrToStringUni(value); return 0;
            case 672: DragOut.TestMode = value != 0; return 0;
            case 673:
            {
                int field = (int)(index >> 16), max = (int)(index & 0xFFFF);
                string text = field switch { 0 => DragOut.LastAsked, 1 => DragOut.LastCarried, _ => DragOut.LastResult };
                if (value != 0 && max > 0) text.AsSpan(0, Math.Min(text.Length, max)).CopyTo(new Span<char>((void*)value, max));
                return text.Length;
            }
            case 674: return DragOut.Drags;
            case 675:
            {
                int max = (int)(index & 0xFFFF);
                string text = recMessage.Length > 0 ? recMessage : plugin.Emulator?.RecordUi.Result ?? "";
                if (value != 0 && max > 0) text.AsSpan(0, Math.Min(text.Length, max)).CopyTo(new Span<char>((void*)value, max));
                return text.Length;
            }
            case 676: return recScroll;
            default: return 0;
        }
    }
}
