// Round 9 (capture): "Capture from FL" in the Test MIDI export window: arm / cancel, Save MIDI..., the take's status and the facts that matter most (the bends VRUN's engine drops, the converter's own
// choice of channels, anything lost), and "send my notes as MIDI out". The work is in CaptureHub.cs and Capture/; this file only draws and routes clicks. Control ids 2120-2139 (2120-2122 used);
// editor test hooks 860-869.
using BrokenNes.Fruity;
using BrokenNes2.Capture;

namespace BrokenNes2;

public sealed unsafe partial class Bn2Editor
{
    public const int IdCapArm = 2120, IdCapSave = 2121, IdCapLive = 2122;
    private static bool IsCapId(int id) => id is >= IdCapArm and <= IdCapLive;
    private string? testCapturePath;
    private string capDrawn = "";
    private int capFactsDrawn;
    private bool capStripShown;

    /// <summary>The capture's two buttons on the window's first row (beside Open MIDI...).</summary>
    private void DrawCaptureButtons(nint dc, int x, int y, Emulator? emu)
    {
        var s = CaptureSession.Current;
        int st = s?.State ?? -1;
        bool listening = st is CaptureSession.Armed or CaptureSession.Recording;
        string label = listening ? "Cancel capture" : st == CaptureSession.Finishing ? "Building..." : "Capture from FL";
        Button(dc, IdCapArm, label, R(x, y, 170, 30), primary: !listening && emu?.Song == null, enabled: emu != null && st != CaptureSession.Finishing);
        bool canSave = s?.Result is { Problem: null, Midi.Length: > 0 };
        Button(dc, IdCapSave, "Save MIDI...", R(x + 180, y, 130, 30), primary: false, enabled: canSave);
    }

    /// <summary>The take's status, its most important facts, and the live MIDI out's tick box (from <paramref name="y"/>, 112 pixels).</summary>
    private void DrawCaptureStrip(nint dc, int lx, int iw, int y)
    {
        capStripShown = true;
        var s = CaptureSession.Current;
        int st = s?.State ?? -1;
        string status = CaptureHub.StatusText();
        int color = st switch { CaptureSession.Armed => Warn, CaptureSession.Recording => Good, CaptureSession.Finishing => Warn, CaptureSession.Done => s?.Result?.Problem == null ? Good : Warn, _ => Dim };
        Fill(dc, R(lx, y, 3, 38), color);
        Txt(dc, status, R(lx + 12, y, iw - 12, 38), Win32.DT_LEFT | Win32.DT_WORDBREAK | Win32.DT_END_ELLIPSIS, Text, fontSmall);
        var drawn = new List<string> { status };
        int fy = y + 42;
        capFactsDrawn = 0;
        if (st == CaptureSession.Done && s?.Result is { Problem: null } r)
        {
            // the facts after the summary (the window's song box below has the converter's own facts): what VRUN drops, the converter's choice of channels, anything lost or guessed
            foreach (var f in r.Facts.Skip(1).Take(3))
            {
                bool bad = f.Contains("no pitch bend") || f.Contains("LOST") || f.Contains("ASSUMED") || f.Contains("chooses") || f.Contains("not played") || f.Contains("early") || f.Contains("clamped");
                Dot(dc, lx + 8, fy + 8, 3, bad ? Warn : Good);
                Txt(dc, f, R(lx + 20, fy, iw - 20, 17), Win32.DT_LEFT | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS, Text, fontSmall);
                drawn.Add(f);
                capFactsDrawn++;
                fy += 18;
            }
        }
        else if (CaptureHub.LastWords.Length > 0 && st != CaptureSession.Done)
        {
            Txt(dc, CaptureHub.LastWords, R(lx + 20, fy, iw - 20, 17), Win32.DT_LEFT | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS, Faint, fontSmall);
            drawn.Add(CaptureHub.LastWords);
        }
        Check(dc, IdCapLive, $"Send my notes and pitch bends as MIDI out (port {Bn2Plugin.LivePort}; Pulse 1, 2, Triangle, Noise on MIDI channels 1, 2, 3, 10)", lx, y + 96, CaptureHub.LiveOn, null, labelW: 640, hitW: 660);
        capDrawn = string.Join("\n", drawn);
    }

    private void CaptureClick(int id)
    {
        switch (id)
        {
            case IdCapArm:
            {
                var s = CaptureSession.Current;
                if (s != null && s.Listening) { CaptureHub.Cancel("cancelled from the window"); CaptureHub.LastWords = "Capture cancelled: nothing was kept"; break; }
                var why = CaptureHub.Arm(plugin);
                CaptureHub.LastWords = why == null ? "Armed: press Play in FL from the start" : "Capture from FL is not possible here: " + why;
                break;
            }
            case IdCapSave:
            {
                string? path = testCapturePath;
                testCapturePath = null;
                path ??= Ui.UiNative.ChooseSaveFile(Hwnd, "Save the captured song as a MIDI file (with its pitch bends)", "MIDI files (*.mid)|*.mid|All files (*.*)|*.*", "mid");
                if (path == null) break;
                var why = CaptureHub.SaveMidi(path);
                CaptureHub.LastWords = why == null ? $"Saved {Path.GetFileName(path)}" : "Not saved: " + why;
                break;
            }
            case IdCapLive: CaptureHub.SetLive(!CaptureHub.LiveOn); break;
        }
    }

    private long CaptureSignature()
    {
        var s = CaptureSession.Current;
        long h = CaptureHub.Stamp * 31L + (s?.State ?? -1);
        if (s != null && s.State == CaptureSession.Recording) h = h * 31 + (long)(s.Seconds * 2);
        h = h * 31 + (CaptureHub.LiveOn ? 1 : 2);
        h = h * 31 + CaptureHub.LastWords.GetHashCode();
        return h;
    }

    /// <summary>The capture's editor hooks: 860 copy what the strip drew (lines joined by '\n'), 861 the next Save MIDI... writes the UTF-16 path <c>value</c> instead of the dialog, 862 the strip was drawn
    /// in the last paint (1/0), 863 how many facts it drew.</summary>
    private nint CaptureEditorTest(int n, nint index, nint value)
    {
        switch (n)
        {
            case 860:
            {
                var text = capDrawn;
                if (value != 0 && index > 0) text.AsSpan(0, Math.Min(text.Length, (int)index)).CopyTo(new Span<char>((void*)value, (int)index));
                return text.Length;
            }
            case 861: testCapturePath = System.Runtime.InteropServices.Marshal.PtrToStringUni(value); return 0;
            case 862: return midOpen && capStripShown ? 1 : 0;
            case 863: return capFactsDrawn;
            default: return 0;
        }
    }
}
