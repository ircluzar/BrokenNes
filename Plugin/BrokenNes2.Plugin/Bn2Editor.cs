// The Bogue :: BrokenNes 2 editor: one dark, self-drawn window (no Win32 controls). Immediate mode: every paint lays the window out
// again and records where each control is; mouse events look the control up in that record. Everything runs on FL's GUI
// thread, from a 30 ms timer that repaints only when something changed.
//
//   left   the emulator's screen (the game's picture in ROM mode, a message in Direct mode) and a status line
//   right  EMULATOR    (shared by every instance on it): which emulator, console, Direct/ROM, sound chip, game,
//                      and the channel strip showing who sits on which channel
//          THIS INSTANCE: its channel and its own sliders
//   About  a modal window about how the plugin was made; it opens by itself the first time the plugin is opened
using System.Runtime.InteropServices;
using BrokenNes.Fruity;

namespace BrokenNes2;

public sealed unsafe class Bn2Editor : IFruityEditor
{
    // ---- control ids (also what the test hooks address) ----
    public const int IdEmulator = 1, IdModeDirect = 2, IdModeRom = 3, IdChip = 4, IdGame = 7, IdChannel = 9;
    public const int IdCell = 10;      // + channel 0..4
    public const int IdSlider = 20;    // + parameter index
    public const int IdAbout = 40, IdAboutClose = 41;
    public const int IdReset = 60;
    public const int IdConsole = 50;   // + console 0..2
    public const int IdItem = 100;     // + item index of the open list
    public const int IdAboutLink = 70;                                   // + link index in the About text
    public const int IdInputs = 80, IdRunaway = 81, IdInputsClose = 82, IdInputsKeyboard = 83, IdInputsGamepad = 84, IdInputsDefaults = 85;
    public const int IdInputKey = 200, IdInputPad = 220, IdPadButton = 240;   // + pad button index 0..11
    public const int IdInstrument = 90, IdGbDuty = 91;

    private const string ClassName = "BogueBrokenNes2Editor";
    public const int Width = 904, Height = 664;
    private const int Margin = 16, HeaderH = 52;
    private const int ScreenX = Margin, ScreenY = 64, ScreenW = 520, ScreenH = 488;
    private const int ColX = 556, ColW = 332;
    private const nuint TimerId = 1;
    private const int RowH = 28, RowPitch = 34, ItemH = 24, MaxRows = 12;
    private const int AboutW = 720, AboutH = 542, InputsW = 780, InputsH = 560, HowH = 246, PlogueH = 150;

    private enum Kind { Button, Dropdown, Slider, Item, Cell }
    private readonly record struct Hit(int Id, RECT Rect, Kind Kind);
    private enum BlockKind { Heading, Para, Bullet, Lead, Link }
    private readonly record struct Block(BlockKind Kind, string Text);

    // palette
    private static readonly int Bg = Win32.Rgb(20, 22, 27), Panel = Win32.Rgb(28, 31, 38), Field = Win32.Rgb(37, 41, 52), FieldHot = Win32.Rgb(47, 52, 66),
        Line = Win32.Rgb(52, 57, 71), Text = Win32.Rgb(228, 231, 239), Dim = Win32.Rgb(140, 147, 166), Faint = Win32.Rgb(88, 94, 112),
        Accent = Win32.Rgb(255, 98, 76), AccentDim = Win32.Rgb(112, 46, 40), Good = Win32.Rgb(86, 208, 144), Warn = Win32.Rgb(255, 184, 76),
        Black = Win32.Rgb(8, 9, 12), White = Win32.Rgb(255, 255, 255);

    private static bool registered;

    private readonly Bn2Plugin plugin;
    private readonly GCHandle self;
    private readonly List<Hit> hits = new();
    private readonly Dictionary<int, nint> brushes = new(), pens = new();
    private readonly byte[] rgba = new byte[512 * 480 * 4];
    private readonly uint[] bgra = new uint[512 * 480];
    private nint fontTitle, fontSection, fontLabel, fontSmall, fontBig, fontLead, fontLink;
    private nint memDc, memBmp, memOld, dimDc, dimBmp, dimOld;
    private long shownPicture = -1, lastSignature, paints;
    private bool hasPicture, trackingLeave;
    private int picW = 256, picH = 240, shownW = 256, shownH = 240;
    private int hot, active, mouseX, mouseY;

    // the open dropdown list: item texts, the parameter value (or action) each stands for, which one is current
    private int openId;
    private (string Text, string? Note)[] openItems = [];
    private int[] openValues = [];
    private int openSelected, openHover = -1, openScroll;
    private RECT openAnchor;

    // the About window
    private bool aboutOpen;
    private int aboutScroll;
    private Block[]? aboutBlocks;
    private long resetFlashUntil;

    // the Inputs window: rebinding, the on-screen pad, and what the three sources hold right now
    private bool inputsOpen, captureIsKey, captureWait, padConnected;
    private int captureIndex = -1, heldPad = -1, liveBits;
    private Emulator? padEmulator;

    public nint Hwnd { get; }

    public Bn2Editor(Bn2Plugin plugin, nint parent)
    {
        this.plugin = plugin;
        var module = OwnModule();
        EnsureClass(module);
        Hwnd = Win32.CreateWindowEx(0, ClassName, Bn2Plugin.DisplayName, Win32.WS_CHILD | Win32.WS_VISIBLE | Win32.WS_CLIPCHILDREN, 0, 0, Width, Height, parent, 0, module, 0);
        self = GCHandle.Alloc(this);
        Win32.SetWindowLongPtr(Hwnd, Win32.GWLP_USERDATA, GCHandle.ToIntPtr(self));
        fontTitle = Win32.MakeFont("Segoe UI", 24, Win32.FW_BOLD);
        fontBig = Win32.MakeFont("Segoe UI", 20, Win32.FW_SEMIBOLD);
        fontSection = Win32.MakeFont("Segoe UI", 12, Win32.FW_BOLD);
        fontLabel = Win32.MakeFont("Segoe UI", 14, Win32.FW_NORMAL);
        fontSmall = Win32.MakeFont("Segoe UI", 12, Win32.FW_NORMAL);
        fontLead = Win32.MakeFont("Segoe UI", 18, Win32.FW_SEMIBOLD);
        fontLink = Win32.MakeFont("Segoe UI", 15, Win32.FW_SEMIBOLD, underline: true);
        aboutOpen = About.ClaimFirstShowing();      // the first time the plugin opens on this machine, the About window opens by itself
        Win32.SetTimer(Hwnd, TimerId, 30, 0);
    }

    public void Destroy()
    {
        Win32.KillTimer(Hwnd, TimerId);
        Win32.SetWindowLongPtr(Hwnd, Win32.GWLP_USERDATA, 0);
        Win32.DestroyWindow(Hwnd);
        self.Free();
        plugin.Emulator?.ClearPad(this);
        foreach (var f in new[] { fontTitle, fontBig, fontSection, fontLabel, fontSmall, fontLead, fontLink }) Win32.DeleteObject(f);
        foreach (var b in brushes.Values) Win32.DeleteObject(b);
        foreach (var p in pens.Values) Win32.DeleteObject(p);
        if (memDc != 0) { Win32.SelectObject(memDc, memOld); Win32.DeleteObject(memBmp); Win32.DeleteDC(memDc); }
        if (dimDc != 0) { Win32.SelectObject(dimDc, dimOld); Win32.DeleteObject(dimBmp); Win32.DeleteDC(dimDc); }
    }

    // ---------------------------------------------------------------- drawing primitives

    private static RECT R(int l, int t, int w, int h) => new() { Left = l, Top = t, Right = l + w, Bottom = t + h };
    private static bool In(RECT r, int x, int y) => x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;

    private nint Brush(int c) { if (!brushes.TryGetValue(c, out var b)) brushes[c] = b = Win32.CreateSolidBrush(c); return b; }
    private nint Pen(int c) { if (!pens.TryGetValue(c, out var p)) pens[c] = p = Win32.CreatePen(Win32.PS_SOLID, 1, c); return p; }

    private void Fill(nint dc, RECT r, int color) => Win32.FillRect(dc, &r, Brush(color));

    /// <summary>A rounded box with a 1 px border (border < 0: none).</summary>
    private void Box(nint dc, RECT r, int fill, int border, int radius)
    {
        var oldB = Win32.SelectObject(dc, Brush(fill));
        var oldP = Win32.SelectObject(dc, border < 0 ? Win32.GetStockObject(Win32.NULL_PEN) : Pen(border));
        Win32.RoundRect(dc, r.Left, r.Top, r.Right + (border < 0 ? 1 : 0), r.Bottom + (border < 0 ? 1 : 0), radius * 2, radius * 2);
        Win32.SelectObject(dc, oldB);
        Win32.SelectObject(dc, oldP);
    }

    private void Dot(nint dc, int cx, int cy, int radius, int fill)
    {
        var oldB = Win32.SelectObject(dc, Brush(fill));
        var oldP = Win32.SelectObject(dc, Win32.GetStockObject(Win32.NULL_PEN));
        Win32.Ellipse(dc, cx - radius, cy - radius, cx + radius + 1, cy + radius + 1);
        Win32.SelectObject(dc, oldB);
        Win32.SelectObject(dc, oldP);
    }

    private void Line_(nint dc, int x0, int y0, int x1, int y1, int color)
    {
        var old = Win32.SelectObject(dc, Pen(color));
        Win32.MoveToEx(dc, x0, y0, null);
        Win32.LineTo(dc, x1, y1);
        Win32.SelectObject(dc, old);
    }

    private void Txt(nint dc, string s, RECT r, uint flags, int color, nint font)
    {
        if (s.Length == 0) return;
        Win32.SelectObject(dc, font);
        Win32.SetTextColor(dc, color);
        fixed (char* p = s) Win32.DrawText(dc, p, s.Length, &r, flags | Win32.DT_NOPREFIX);
    }

    private int TextWidth(nint dc, string s, nint font)
    {
        if (s.Length == 0) return 0;
        Win32.SelectObject(dc, font);
        var r = new RECT();
        fixed (char* p = s) Win32.DrawText(dc, p, s.Length, &r, Win32.DT_SINGLELINE | 0x400 /* DT_CALCRECT */ | Win32.DT_NOPREFIX);
        return r.Right - r.Left;
    }

    /// <summary>The height text needs when wrapped to <paramref name="width"/>.</summary>
    private int WrappedHeight(nint dc, string s, int width, nint font)
    {
        if (s.Length == 0) return 0;
        Win32.SelectObject(dc, font);
        var r = new RECT { Right = width };
        fixed (char* p = s) Win32.DrawText(dc, p, s.Length, &r, Win32.DT_WORDBREAK | 0x400 /* DT_CALCRECT */ | Win32.DT_NOPREFIX);
        return r.Bottom - r.Top;
    }

    private const uint Mid = Win32.DT_SINGLELINE | Win32.DT_VCENTER;

    private void Chevron(nint dc, int cx, int cy, int color)
    {
        Line_(dc, cx - 4, cy - 2, cx, cy + 2, color);
        Line_(dc, cx, cy + 2, cx + 5, cy - 3, color);
        Line_(dc, cx - 4, cy - 1, cx, cy + 3, color);
        Line_(dc, cx, cy + 3, cx + 5, cy - 2, color);
    }

    /// <summary>Darkens the whole window behind a modal.</summary>
    private void DimBackdrop(nint dc)
    {
        if (dimDc == 0)
        {
            dimDc = Win32.CreateCompatibleDC(dc);
            dimBmp = Win32.CreateCompatibleBitmap(dc, 1, 1);
            dimOld = Win32.SelectObject(dimDc, dimBmp);
            Fill(dimDc, R(0, 0, 1, 1), Win32.Rgb(0, 0, 0));
        }
        uint blend = (uint)(0 | (0 << 8) | (170 << 16) | (0 << 24));   // AC_SRC_OVER, constant alpha 170 of 255
        Win32.AlphaBlend(dc, 0, 0, Width, Height, dimDc, 0, 0, 1, 1, blend);
    }

    // ---------------------------------------------------------------- widgets (each records its hit area)

    private void AddHit(int id, RECT r, Kind k) => hits.Add(new Hit(id, r, k));

    private void Section(nint dc, string title, string? note, int x, int y, int w)
    {
        Txt(dc, title, R(x, y, w, 16), Win32.DT_LEFT | Win32.DT_SINGLELINE, Accent, fontSection);
        if (note != null) Txt(dc, note, R(x, y, w, 16), Win32.DT_RIGHT | Win32.DT_SINGLELINE, Faint, fontSmall);
        Line_(dc, x, y + 19, x + w, y + 19, Line);
    }

    /// <summary>Label on the left, a dropdown box on the right. Returns the box.</summary>
    private RECT Dropdown(nint dc, int id, string label, int x, int y, int w, int labelW, string value, bool dim = false)
    {
        var box = R(x + labelW, y, w - labelW, RowH);
        bool isHot = hot == id || openId == id;
        if (labelW > 0) Txt(dc, label, R(x, y, labelW, RowH), Win32.DT_LEFT | Mid, Dim, fontLabel);
        Box(dc, box, isHot ? FieldHot : Field, openId == id ? Accent : Line, 5);
        Txt(dc, value, R(box.Left + 10, y, box.Right - box.Left - 32, RowH), Win32.DT_LEFT | Mid | Win32.DT_END_ELLIPSIS, dim ? Dim : Text, fontLabel);
        Chevron(dc, box.Right - 14, y + RowH / 2, isHot ? Text : Dim);
        AddHit(id, box, Kind.Dropdown);
        return box;
    }

    private void Button(nint dc, int id, string label, RECT r, bool primary = false)
    {
        bool isHot = hot == id;
        Box(dc, r, primary ? (isHot ? Accent : AccentDim) : (isHot ? FieldHot : Field), primary ? Accent : Line, 5);
        Txt(dc, label, r, Win32.DT_CENTER | Mid, White, fontLabel);
        AddHit(id, r, Kind.Button);
    }

    /// <summary>A row of exclusive choices, one segment each (the selected one lit). Ids are <paramref name="firstId"/> + index.</summary>
    private void Segments(nint dc, string label, int x, int y, int w, int firstId, string[] names, int selected)
    {
        Txt(dc, label, R(x, y, 86, RowH), Win32.DT_LEFT | Mid, Dim, fontLabel);
        int sx = x + 86, sw = (w - 86) / names.Length;
        for (int i = 0; i < names.Length; i++)
        {
            var seg = R(sx + i * sw, y, sw, RowH);
            bool on = selected == i, isHot = hot == firstId + i;
            Box(dc, seg, on ? Accent : (isHot ? FieldHot : Field), on ? Accent : Line, 5);
            Txt(dc, names[i], seg, Win32.DT_CENTER | Mid, on ? White : Dim, on ? fontSection : fontLabel);
            AddHit(firstId + i, seg, Kind.Button);
        }
    }

    private void Slider(nint dc, int param, string label, int x, int y, int w, bool dim = false)
    {
        int id = IdSlider + param;
        var def = plugin.Params[param];
        int v = plugin.Get(param);
        bool isHot = hot == id || active == id;
        const int labelW = 86, valueW = 78;
        Txt(dc, label, R(x, y, labelW, RowH), Win32.DT_LEFT | Mid, dim ? Faint : Dim, fontLabel);
        Txt(dc, def.Format(v), R(x + w - valueW, y, valueW, RowH), Win32.DT_RIGHT | Mid, dim ? Faint : Text, fontLabel);
        int tx = x + labelW, tw = w - labelW - valueW - 8, ty = y + RowH / 2;
        var track = R(tx, ty - 3, tw, 6);
        Box(dc, track, Field, -1, 3);
        double range = Math.Max(1, def.Max - def.Min);
        double frac = (v - def.Min) / range, centre = (0 - def.Min) / range;
        bool centred = (def.InfoFlags & Pi.Centered) != 0;
        int kx = tx + (int)Math.Round(frac * tw);
        int from = centred ? tx + (int)Math.Round(centre * tw) : tx;
        if (kx != from) Box(dc, R(Math.Min(from, kx), ty - 3, Math.Abs(kx - from), 6), dim ? Faint : (isHot ? Accent : AccentDim), -1, 3);
        if (centred) Line_(dc, tx + (int)Math.Round(centre * tw), ty - 6, tx + (int)Math.Round(centre * tw), ty + 7, Line);
        Dot(dc, kx, ty, isHot ? 7 : 6, dim ? Faint : (isHot ? Text : Dim));
        AddHit(id, R(x + labelW - 6, y, tw + 12, RowH), Kind.Slider);
    }

    // ---------------------------------------------------------------- the window

    private void Draw(nint dc)
    {
        hits.Clear();
        paints++;
        Win32.SetBkMode(dc, Win32.TRANSPARENT);
        Fill(dc, R(0, 0, Width, Height), Bg);

        var a = plugin.Assignment;
        var emu = a.Emulator;
        int mode = plugin.Get(P.Mode);
        int console = plugin.Get(P.Console);

        DrawHeader(dc, emu);
        DrawScreen(dc, emu);
        DrawStatus(dc, a, emu);

        // EMULATOR
        int x = ColX, y = 64, w = ColW;
        Section(dc, "EMULATOR", "shared by every instance on it", x, y, w);
        y += 30;
        int er = plugin.Get(P.Emulator);
        Dropdown(dc, IdEmulator, "Emulator", x, y, w, 86, er == 0 ? (emu != null ? $"Auto  ->  #{emu.Id}" : "Auto  (none free)") : $"#{er}");
        y += RowPitch;

        Segments(dc, "Console", x, y, w, IdConsole, Cx.Names, console);
        y += RowPitch;

        Segments(dc, "Mode", x, y, w, IdModeDirect, ModeLabels, mode);
        y += RowPitch;

        Dropdown(dc, IdChip, "Sound chip", x, y, w, 86, plugin.Params[P.Core].Format(plugin.Get(P.Core)));
        y += RowPitch;

        // the game ROM mode runs
        Dropdown(dc, IdGame, "Game", x, y, w, 86, emu?.RomDisplayName ?? "-", dim: mode != 1);
        y += RowPitch + 4;

        // channel strip (or, in Instrument Runaway, a banner)
        bool frozen = emu?.Runaway == true, sampler = emu?.SamplerMode == true, gbSampler = emu?.GbSamplerMode == true;
        Txt(dc, frozen ? "Instrument Runaway" : "Channels", R(x, y, 200, 16), Win32.DT_LEFT | Win32.DT_SINGLELINE, frozen ? Accent : Dim, fontSmall);
        y += 18;
        if (frozen) DrawRunawayBanner(dc, sampler ? 1 : gbSampler ? 2 : 0, x, y, w); else DrawCells(dc, emu, a, x, y, w);
        y += 56 + 14;

        // THIS INSTANCE
        Section(dc, "THIS INSTANCE", null, x, y, w);
        y += 30;
        if (sampler)
        {
            Txt(dc, "Channel", R(x, y, 86, RowH), Win32.DT_LEFT | Mid, Dim, fontLabel);
            Box(dc, R(x + 86, y, w - 86, RowH), Panel, Line, 5);
            Txt(dc, "Sampler  -  game's instruments", R(x + 96, y, w - 106, RowH), Win32.DT_LEFT | Mid | Win32.DT_END_ELLIPSIS, Dim, fontLabel);
        }
        else
        {
            int cr = plugin.Get(P.Channel);
            string chText = gbSampler ? (cr == 0 ? "Auto  ->  Pulse 1" : GbChannelNames[Bn2Plugin.GbChannelOf(cr)])
                          : cr == 0 ? (a.Channel >= 0 ? $"Auto  ->  {Ch.Label(a.Channel)}" : "Auto") : Bn2Params.ChannelNames[cr];
            Dropdown(dc, IdChannel, "Channel", x, y, w, 86, chText);
        }
        y += RowPitch;
        if (a.Problem != null) Txt(dc, a.Problem, R(x, y - 2, w, 30), Win32.DT_LEFT | Win32.DT_WORDBREAK, Warn, fontSmall);
        y += 30;
        Slider(dc, P.Volume, "Volume", x, y, w); y += 30;
        Slider(dc, P.Pan, "Pan", x, y, w); y += 30;
        Slider(dc, P.Coarse, "Coarse", x, y, w); y += 30;
        Slider(dc, P.Fine, "Fine", x, y, w); y += 30;
        int ch = a.Channel;
        if (sampler)
        {
            // the picker lists what the game has loaded, read from its sample table
            var snap = emu!.Snapshot!;
            var found = snap.Instruments();
            int ins = snap.Resolve(plugin.Get(P.Instrument));
            var current = found.FirstOrDefault(i => i.Slot == ins);
            string value = found.Count == 0 ? "no instruments found" : current.Blocks > 0 ? $"#{ins}   ${current.Start:X4}" : $"#{ins}   (empty slot)";
            Dropdown(dc, IdInstrument, "Instrument", x, y, w, 86, value);
            string info = current.Blocks > 0 ? $"{current.Milliseconds:0} ms, {(current.Loops ? "loops" : "one-shot")}{(current.Sounding ? ", the game was playing it" : "")}   ({found.Count} found)"
                : found.Count == 0 ? "the game's sample table has no sample the plugin can read" : $"nothing in this slot   ({found.Count} found: pick one from the list)";
            Txt(dc, info, R(x + 86, y + RowH + 1, w - 86, 16), Win32.DT_LEFT | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS, Faint, fontSmall);
        }
        else if (gbSampler)
        {
            int gch = Bn2Plugin.GbChannelOf(plugin.Get(P.Channel));
            var gb = emu!.GbState!;
            if (gch < 2)
            {
                int d = plugin.Get(P.Instrument);
                Dropdown(dc, IdGbDuty, "Duty", x, y, w, 86, d is >= 1 and <= 4 ? GbDutyNames[d - 1] : $"the game's ({GbDutyNames[gb.Duty(gch)]})");
            }
            else if (gch == 3) Slider(dc, P.NoiseMode, "Noise mode", x, y, w);
            else Txt(dc, "the game's own waveform (its wave RAM)", R(x + 86, y, w - 86, RowH), Win32.DT_LEFT | Mid, Faint, fontSmall);
        }
        else if (ch == Ch.Pulse1 || ch == Ch.Pulse2) Slider(dc, P.Duty, "Pulse duty", x, y, w);
        else if (ch == Ch.Noise) Slider(dc, P.NoiseMode, "Noise mode", x, y, w);

        // Inputs and Instrument Runaway, bottom left
        int by = Height - Margin - 30;
        var inputsBtn = R(ScreenX, by, 96, 30);
        Button(dc, IdInputs, "Inputs", inputsBtn);
        var runBtn = R(inputsBtn.Right + 8, by, 206, 30);
        bool canRun = emu?.CanRunaway == true;
        if (canRun || frozen) Button(dc, IdRunaway, frozen ? "Runaway ON  -  release" : "Instrument Runaway", runBtn, primary: frozen);
        else { Box(dc, runBtn, Panel, Line, 5); Txt(dc, "Instrument Runaway", runBtn, Win32.DT_CENTER | Mid, Faint, fontLabel); }
        string runHint = frozen ? (sampler ? "FL notes play its samples" : gbSampler ? "FL notes play its channels" : "frozen and silent") : canRun ? "freeze it, keep its sounds" : "needs ROM mode and a game";
        Txt(dc, runHint, R(runBtn.Right + 10, by, ColX - runBtn.Right - 20, 30), Win32.DT_LEFT | Mid | Win32.DT_END_ELLIPSIS, Faint, fontSmall);

        // Reset Console, bottom right: starts this emulator over for every instance on it
        var reset = R(Width - Margin - 150, Height - Margin - 30, 150, 30);
        bool flash = Environment.TickCount64 < resetFlashUntil;
        if (emu != null)
        {
            Txt(dc, $"restarts emulator #{emu.Id}", R(ColX, reset.Top, reset.Left - ColX - 10, 30), Win32.DT_RIGHT | Mid | Win32.DT_END_ELLIPSIS, Faint, fontSmall);
            Button(dc, IdReset, flash ? "Reset done" : "Reset Console", reset, primary: flash);
        }
        else Box(dc, reset, Panel, Line, 5);

        if (openId != 0) DrawList(dc);
        if (aboutOpen) DrawAbout(dc);
        else if (inputsOpen) DrawInputs(dc);
    }

    /// <summary>Instead of the channel strip while the game is frozen: <paramref name="kind"/> 0 silent (NES), 1 SNES instruments, 2 Game Boy channels.</summary>
    private void DrawRunawayBanner(nint dc, int kind, int x, int y, int w)
    {
        var r = R(x, y, w, 56);
        Box(dc, r, AccentDim, Accent, 6);
        string title = kind == 1 ? "GAME FROZEN  -  INSTRUMENTS LIVE" : kind == 2 ? "GAME FROZEN  -  GAME BOY CHANNELS LIVE" : "GAME FROZEN  -  SILENT";
        string text = kind == 1 ? "Pick one of the game's own instruments below." : kind == 2 ? "Pick a Game Boy channel for each instance." : "Instrument playing works on SNES and Game Boy games.";
        Txt(dc, title, R(r.Left + 12, r.Top + 8, w - 24, 18), Win32.DT_LEFT | Win32.DT_SINGLELINE, White, fontSection);
        Txt(dc, text, R(r.Left + 12, r.Top + 28, w - 24, 24), Win32.DT_LEFT | Win32.DT_WORDBREAK, Win32.Rgb(240, 200, 195), fontSmall);
    }

    private static readonly string[] GbChannelNames = ["Pulse 1", "Pulse 2", "Wave", "Noise"];
    private static readonly string[] GbDutyNames = ["12.5%", "25%", "50%", "75%"];

    private static readonly string[] ModeLabels = ["Direct", "ROM"];

    private void DrawHeader(nint dc, Emulator? emu)
    {
        // Bogue :: BrokenNes 2
        int x = Margin;
        Txt(dc, "Bogue", R(x, 8, 200, 36), Win32.DT_LEFT | Mid, Dim, fontTitle);
        x += TextWidth(dc, "Bogue", fontTitle) + 8;
        Txt(dc, "::", R(x, 8, 60, 36), Win32.DT_LEFT | Mid, Faint, fontTitle);
        x += TextWidth(dc, "::", fontTitle) + 8;
        Txt(dc, "Broken", R(x, 8, 200, 36), Win32.DT_LEFT | Mid, Text, fontTitle);
        x += TextWidth(dc, "Broken", fontTitle);
        Txt(dc, "Nes 2", R(x, 8, 200, 36), Win32.DT_LEFT | Mid, Accent, fontTitle);
        x += TextWidth(dc, "Nes 2", fontTitle);
        Txt(dc, "one instance = one channel", R(x + 16, 8, 300, 36), Win32.DT_LEFT | Mid, Faint, fontSmall);

        // About, and which emulator this instance sits on
        var about = R(Width - Margin - 74, 13, 74, 26);
        Button(dc, IdAbout, "About", about);
        string badge = emu == null ? "no emulator" : $"EMULATOR #{emu.Id}  -  {emu.MemberCount} instance{(emu.MemberCount == 1 ? "" : "s")}";
        Txt(dc, badge, R(about.Left - 12 - 230, 8, 230, 36), Win32.DT_RIGHT | Mid, emu == null ? Warn : Dim, fontSection);
        Line_(dc, 0, HeaderH + 4, Width, HeaderH + 4, Line);
    }

    private void DrawScreen(nint dc, Emulator? emu)
    {
        var frame = R(ScreenX, ScreenY, ScreenW, ScreenH);
        Box(dc, frame, Black, Line, 8);
        var inner = R(ScreenX + 4, ScreenY + 4, 512, 480);

        string? message = emu == null ? "No emulator" : emu.PictureMessage;
        if (emu != null && message == null)
        {
            long v = emu.PictureVersion;
            if (v != shownPicture)
            {
                shownPicture = v;
                hasPicture = emu.CopyPicture(rgba, out picW, out picH, out shownW, out shownH);
                if (hasPicture) ToBgra();
            }
            if (!hasPicture) message = emu.RomStatus;
        }
        if (message == null)
        {
            // the picture, scaled by a whole number to fill the screen (a Game Boy 3x, a NES or SNES 2x), centred
            int scale = Math.Max(1, Math.Min(512 / Math.Max(1, shownW), 480 / Math.Max(1, shownH)));
            int dw = shownW * scale, dh = shownH * scale;
            var bmi = new BITMAPINFOHEADER { biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = picW, biHeight = -picH, biPlanes = 1, biBitCount = 32 };
            Win32.SetStretchBltMode(dc, Win32.COLORONCOLOR);
            Fill(dc, inner, Black);
            fixed (uint* bits = bgra)
                Win32.StretchDIBits(dc, inner.Left + (512 - dw) / 2, inner.Top + (480 - dh) / 2, dw, dh, 0, 0, picW, picH, bits, &bmi, 0, Win32.SRCCOPY);
            return;
        }

        shownPicture = -1;
        // no picture: a dark screen with faint scanlines and the message in the middle
        for (int yy = inner.Top; yy < inner.Bottom; yy += 4) Fill(dc, R(inner.Left, yy, 512, 1), Win32.Rgb(14, 16, 21));
        var line = R(inner.Left + 24, inner.Top + 200, 512 - 48, 80);
        Txt(dc, message, line, Win32.DT_CENTER | Win32.DT_WORDBREAK | Win32.DT_VCENTER, Text, fontBig);
        // corner brackets
        int bl = 18; var c = Accent;
        foreach (var (cx, cy, dx, dy) in new[] { (inner.Left + 14, inner.Top + 14, 1, 1), (inner.Right - 14, inner.Top + 14, -1, 1), (inner.Left + 14, inner.Bottom - 14, 1, -1), (inner.Right - 14, inner.Bottom - 14, -1, -1) })
        {
            Line_(dc, cx, cy, cx + dx * bl, cy, c);
            Line_(dc, cx, cy, cx, cy + dy * bl, c);
        }
    }

    private void ToBgra()
    {
        int n = Math.Min(bgra.Length, picW * picH);
        for (int i = 0, o = 0; i < n; i++, o += 4)
            bgra[i] = (uint)(rgba[o + 2] | (rgba[o + 1] << 8) | (rgba[o] << 16) | (0xFF << 24));
    }

    private void DrawStatus(nint dc, Assignment a, Emulator? emu)
    {
        string s;
        if (emu == null) s = a.Problem ?? "";
        else if (emu.Runaway) s = $"Instrument Runaway: {emu.RomDisplayName} frozen at frame {emu.FramesRun}   |   " + (emu.SamplerMode ? "sound memory captured" : emu.GbSamplerMode ? "sound settings captured" : "silent");
        else if (emu.Mode == 1)
        {
            s = emu.RomLoaded
                ? $"{emu.RomStatus}   |   frame {emu.FramesRun}   |   {emu.RomDescription}"
                : emu.RomStatus;
        }
        else
        {
            var owner = a.Channel is >= 0 and < Ch.ToneCount ? Ch.ShortNames[a.Channel] : "no channel";
            s = $"{Cx.Names[emu.Console]} chip {emu.ChipId}   |   {owner}   |   " + Describe(a);
        }
        Txt(dc, s, R(ScreenX + 4, ScreenY + ScreenH + 8, ScreenW - 8, 20), Win32.DT_LEFT | Mid | Win32.DT_END_ELLIPSIS, Dim, fontSmall);
        if (emu != null && emu.Mode == 1 && emu.RomCrashed) Txt(dc, "crashed", R(ScreenX + 4, ScreenY + ScreenH + 8, ScreenW - 8, 20), Win32.DT_RIGHT | Mid, Warn, fontSmall);
    }

    private string Describe(Assignment a)
    {
        int c = a.Channel;
        if (c < 0 || c >= Ch.ToneCount) return "silent";
        int level = plugin.Emulator?.Activity(c) ?? 0;
        if (level == 0) return "idle";
        return $"playing, level {level}";
    }

    private void DrawCells(nint dc, Emulator? emu, Assignment a, int x, int y, int w)
    {
        int gap = 6, cw = (w - gap * (Ch.Count - 1)) / Ch.Count;
        int mode = plugin.Get(P.Mode), console = plugin.Get(P.Console);
        for (int c = 0; c < Ch.Count; c++)
        {
            var r = R(x + c * (cw + gap), y, cw, 56);
            var owner = emu?.Owner(c);
            bool mine = owner != null && owner == plugin;
            bool unavailable = c == Ch.Mix ? mode != 1 : (mode == 1 && console != Cx.Nes);
            bool taken = owner != null && !mine;
            int id = IdCell + c;
            bool isHot = hot == id && !unavailable;
            Box(dc, r, mine ? AccentDim : (isHot ? FieldHot : Field), mine ? Accent : Line, 6);
            Txt(dc, Ch.ShortNames[c], R(r.Left, r.Top + 6, cw, 16), Win32.DT_CENTER | Win32.DT_SINGLELINE, unavailable ? Faint : Text, fontSmall);
            string state = unavailable ? (c == Ch.Mix ? "ROM only" : "NES only") : mine ? "this" : taken ? "in use" : "free";
            Txt(dc, state, R(r.Left, r.Top + 22, cw, 14), Win32.DT_CENTER | Win32.DT_SINGLELINE, unavailable ? Faint : mine ? Text : taken ? Warn : Good, fontSmall);
            // activity meter
            int level = owner != null && emu != null ? emu.Activity(c) : 0;
            var bar = R(r.Left + 8, r.Bottom - 12, cw - 16, 4);
            Box(dc, bar, Bg, -1, 2);
            if (level > 0) Box(dc, R(bar.Left, bar.Top, Math.Max(3, (bar.Right - bar.Left) * level / 15), 4), mine ? Accent : Good, -1, 2);
            if (!unavailable) AddHit(id, r, Kind.Cell);
        }
    }

    // ---------------------------------------------------------------- the dropdown list (drawn over everything)

    private RECT ListRect()
    {
        int rows = Math.Min(openItems.Length, MaxRows);
        int w = Math.Max(openAnchor.Right - openAnchor.Left, 220);
        int h = rows * ItemH + 8;
        int l = Math.Min(openAnchor.Left, Width - w - 8);
        int t = openAnchor.Bottom + 3;
        if (t + h > Height - 8) t = Math.Max(8, openAnchor.Top - 3 - h);
        return R(l, t, w, h);
    }

    private void DrawList(nint dc)
    {
        var box = ListRect();
        Box(dc, R(box.Left + 3, box.Top + 4, box.Right - box.Left, box.Bottom - box.Top), Black, -1, 8);   // shadow
        Box(dc, box, Panel, Accent, 7);
        int rows = Math.Min(openItems.Length, MaxRows);
        bool scroll = openItems.Length > rows;
        for (int r = 0; r < rows; r++)
        {
            int i = openScroll + r;
            if (i >= openItems.Length) break;
            var row = R(box.Left + 4, box.Top + 4 + r * ItemH, box.Right - box.Left - 8 - (scroll ? 6 : 0), ItemH);
            bool selected = i == openSelected, isHot = i == openHover;
            if (isHot) Box(dc, row, FieldHot, -1, 4);
            if (selected) Fill(dc, R(row.Left, row.Top + 5, 3, ItemH - 10), Accent);
            var (text, note) = openItems[i];
            Txt(dc, text, R(row.Left + 10, row.Top, row.Right - row.Left - 14, ItemH), Win32.DT_LEFT | Mid, selected ? Text : (isHot ? Text : Win32.Rgb(200, 204, 216)), selected ? fontSection : fontLabel);
            if (note != null)
                Txt(dc, note, R(row.Left + 10, row.Top, row.Right - row.Left - 18, ItemH), Win32.DT_RIGHT | Mid, Faint, fontSmall);
            AddHit(IdItem + i, row, Kind.Item);
        }
        if (scroll)
        {
            int trackH = rows * ItemH;
            int thumbH = Math.Max(18, trackH * rows / openItems.Length);
            int thumbY = box.Top + 4 + (trackH - thumbH) * openScroll / Math.Max(1, openItems.Length - rows);
            Box(dc, R(box.Right - 9, thumbY, 4, thumbH), Line, -1, 2);
        }
    }

    // ---------------------------------------------------------------- the Inputs window

    private RECT InputsBox => R((Width - InputsW) / 2, (Height - InputsH) / 2, InputsW, InputsH);

    private void Check(nint dc, int id, string label, int x, int y, bool on, string? note)
    {
        var box = R(x, y, 20, 20);
        bool isHot = hot == id;
        Box(dc, box, on ? Accent : (isHot ? FieldHot : Field), on ? Accent : Line, 4);
        if (on) { Line_(dc, x + 4, y + 10, x + 8, y + 14, White); Line_(dc, x + 8, y + 14, x + 16, y + 5, White); Line_(dc, x + 4, y + 11, x + 8, y + 15, White); Line_(dc, x + 8, y + 15, x + 16, y + 6, White); }
        Txt(dc, label, R(x + 30, y - 2, 120, 24), Win32.DT_LEFT | Mid, Text, fontLabel);
        if (note != null) Txt(dc, note, R(x + 30, y + 20, 330, 16), Win32.DT_LEFT | Win32.DT_SINGLELINE, Faint, fontSmall);
        AddHit(id, R(x, y - 2, 160, 24), Kind.Button);
    }

    private void PadKey(nint dc, int index, RECT r, string label)
    {
        bool down = (liveBits & (int)Inputs.Buttons[index]) != 0, isHot = hot == IdPadButton + index;
        Box(dc, r, down ? Accent : (isHot ? FieldHot : Field), down ? Accent : Line, 6);
        Txt(dc, label, r, Win32.DT_CENTER | Mid, down ? White : Text, fontSmall);
        AddHit(IdPadButton + index, r, Kind.Button);
    }

    private void DrawInputs(nint dc)
    {
        DimBackdrop(dc);
        hits.Clear();                                    // a modal: nothing behind it can be reached
        var box = InputsBox;
        Box(dc, R(box.Left + 4, box.Top + 6, InputsW, InputsH), Black, -1, 10);
        Box(dc, box, Panel, Accent, 10);
        int lx = box.Left + 30;

        Txt(dc, "Inputs", R(lx, box.Top + 20, 300, 34), Win32.DT_LEFT | Mid, Text, fontTitle);
        Txt(dc, "Player 1 controls for the game running in this emulator (NES, Game Boy and SNES)", R(lx, box.Top + 58, InputsW - 60, 20), Win32.DT_LEFT | Mid, Dim, fontSmall);
        Line_(dc, box.Left + 30, box.Top + 84, box.Right - 30, box.Top + 84, Line);

        int y = box.Top + 100;
        Check(dc, IdInputsKeyboard, "Keyboard", lx, y, Inputs.KeyboardEnabled, "only while this window is the active one");
        Check(dc, IdInputsGamepad, "Gamepad", lx + 230, y, Inputs.GamepadEnabled, !Inputs.GamepadEnabled ? "off" : padConnected ? "controller connected" : "no controller found");

        // the table
        int ty = box.Top + 168;
        Txt(dc, "Button", R(lx, ty - 22, 80, 20), Win32.DT_LEFT | Mid, Faint, fontSmall);
        Txt(dc, "Keyboard", R(lx + 80, ty - 22, 150, 20), Win32.DT_LEFT | Mid, Faint, fontSmall);
        Txt(dc, "Gamepad", R(lx + 240, ty - 22, 190, 20), Win32.DT_LEFT | Mid, Faint, fontSmall);
        for (int i = 0; i < 12; i++)
        {
            int ry = ty + i * 27;
            bool down = (liveBits & (int)Inputs.Buttons[i]) != 0;
            Dot(dc, lx + 4, ry + 12, 4, down ? Accent : Field);
            Txt(dc, Inputs.Names[i], R(lx + 16, ry, 64, 24), Win32.DT_LEFT | Mid, Text, fontLabel);
            var kc = R(lx + 80, ry, 150, 24);
            bool capK = captureIndex == i && captureIsKey;
            Box(dc, kc, capK ? AccentDim : (hot == IdInputKey + i ? FieldHot : Field), capK ? Accent : Line, 5);
            Txt(dc, capK ? "press a key (Esc cancels)" : Inputs.KeyName(Inputs.Keys[i]), kc, Win32.DT_CENTER | Mid | Win32.DT_END_ELLIPSIS, capK ? White : (Inputs.Keys[i] == 0 ? Faint : Text), fontSmall);
            AddHit(IdInputKey + i, kc, Kind.Button);
            var pc = R(lx + 240, ry, 190, 24);
            bool capP = captureIndex == i && !captureIsKey;
            Box(dc, pc, capP ? AccentDim : (hot == IdInputPad + i ? FieldHot : Field), capP ? Accent : Line, 5);
            Txt(dc, capP ? "press a button" : Inputs.PadName(Inputs.Pads[i]), pc, Win32.DT_CENTER | Mid | Win32.DT_END_ELLIPSIS, capP ? White : (Inputs.Pads[i] == 0 ? Faint : Text), fontSmall);
            AddHit(IdInputPad + i, pc, Kind.Button);
        }

        // the on-screen pad: hold a button with the mouse
        int px = box.Left + 480, py = box.Top + 112;
        Txt(dc, "On-screen pad", R(px, py - 4, 240, 18), Win32.DT_LEFT | Mid, Faint, fontSmall);
        PadKey(dc, 8, R(px + 10, py + 22, 84, 28), "L");
        PadKey(dc, 9, R(px + 196, py + 22, 84, 28), "R");
        PadKey(dc, 0, R(px + 52, py + 76, 38, 38), "Up");
        PadKey(dc, 1, R(px + 52, py + 152, 38, 38), "Down");
        PadKey(dc, 2, R(px + 14, py + 114, 38, 38), "Left");
        PadKey(dc, 3, R(px + 90, py + 114, 38, 38), "Right");
        PadKey(dc, 6, R(px + 212, py + 76, 38, 38), "X");
        PadKey(dc, 7, R(px + 174, py + 114, 38, 38), "Y");
        PadKey(dc, 4, R(px + 250, py + 114, 38, 38), "A");
        PadKey(dc, 5, R(px + 212, py + 152, 38, 38), "B");
        PadKey(dc, 11, R(px + 52, py + 224, 84, 28), "Select");
        PadKey(dc, 10, R(px + 164, py + 224, 84, 28), "Start");
        Txt(dc, "The NES and Game Boy use A, B, Start, Select and the D-pad; the SNES uses them all.", R(px, py + 268, 290, 40), Win32.DT_LEFT | Win32.DT_WORDBREAK, Faint, fontSmall);

        // footer
        Line_(dc, box.Left + 30, box.Bottom - 74, box.Right - 30, box.Bottom - 74, Line);
        Button(dc, IdInputsDefaults, "Reset to defaults", R(lx, box.Bottom - 60, 150, 34));
        Txt(dc, "Click a binding, then press a key or a gamepad button; right-click clears it. FL also plays notes from typed keys: use a gamepad, or switch off FL's typing keyboard to piano keys.",
            R(lx + 166, box.Bottom - 64, InputsW - 60 - 166 - 110, 44), Win32.DT_LEFT | Win32.DT_WORDBREAK | Win32.DT_VCENTER, Faint, fontSmall);
        Button(dc, IdInputsClose, "Close", R(box.Right - 30 - 90, box.Bottom - 60, 90, 34), primary: true);
    }

    /// <summary>Called every 30 ms: what the keyboard (while this window is active), the gamepad and the on-screen pad hold goes to the game
    /// of the emulator this instance sits on; and a binding being rebound waits for its key or button.</summary>
    private void PollInputs()
    {
        var emu = plugin.Emulator;
        if (!ReferenceEquals(emu, padEmulator)) { padEmulator?.ClearPad(this); padEmulator = emu; }
        if (emu == null) { liveBits = 0; return; }

        if (captureIndex >= 0)
        {
            int i = captureIndex;
            if (captureIsKey)
            {
                int k = Inputs.KeyNow();
                if (captureWait) { if (k == 0) captureWait = false; }
                else if (k == -1) captureIndex = -1;
                else if (k > 0)
                {
                    for (int j = 0; j < 12; j++) if (Inputs.Keys[j] == k) Inputs.Keys[j] = 0;
                    Inputs.Keys[i] = k; Inputs.Save(); captureIndex = -1;
                }
            }
            else
            {
                int m = Inputs.PadMaskNow();
                if (captureWait) { if (m == 0) captureWait = false; }
                else if (m != 0)
                {
                    int bit = m & -m;
                    for (int j = 0; j < 12; j++) if (Inputs.Pads[j] == bit) Inputs.Pads[j] = 0;
                    Inputs.Pads[i] = bit; Inputs.Save(); captureIndex = -1;
                }
            }
            liveBits = 0;
            emu.SetPad(this, 0);
            return;
        }

        NesEmulator.Systems.PadButtons bits = 0;
        if (Inputs.KeyboardEnabled && Inputs.IsForeground(Hwnd)) bits |= Inputs.PollKeyboard();
        if (Inputs.GamepadEnabled) bits |= Inputs.PollGamepad(out padConnected); else padConnected = false;
        if (heldPad >= 0) bits |= Inputs.Buttons[heldPad];
        liveBits = (int)bits;
        emu.SetPad(this, liveBits);
    }

    private static void OpenLink(string url)
    {
        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) Win32.ShellExecute(0, "open", url, null, null, 1);
    }

    // ---------------------------------------------------------------- the About window

    private RECT AboutBox => R((Width - AboutW) / 2, (Height - AboutH) / 2, AboutW, AboutH);
    private RECT AboutText => R(AboutBox.Left + 30, AboutBox.Top + 58, AboutW - 60, HowH);

    private Block[] Blocks()
    {
        if (aboutBlocks != null) return aboutBlocks;
        var list = new List<Block>();
        var para = new System.Text.StringBuilder();
        void Flush() { if (para.Length > 0) { list.Add(new Block(BlockKind.Para, para.ToString())); para.Clear(); } }
        foreach (var raw in About.Text().Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) { Flush(); continue; }
            if (line.StartsWith("# ")) { Flush(); list.Add(new Block(BlockKind.Heading, line[2..])); }
            else if (line.StartsWith("* ")) { Flush(); list.Add(new Block(BlockKind.Lead, line[2..])); }
            else if (line.StartsWith("@ ")) { Flush(); list.Add(new Block(BlockKind.Link, line[2..])); }
            else if (line.StartsWith("- ")) { Flush(); list.Add(new Block(BlockKind.Bullet, line[2..])); }
            else { if (para.Length > 0) para.Append(' '); para.Append(line); }
        }
        Flush();
        return aboutBlocks = list.ToArray();
    }

    private int AboutContentHeight(nint dc, int width)
    {
        int h = 0;
        foreach (var b in Blocks()) h += BlockHeight(dc, b, width) + BlockGap(b);
        return h;
    }

    private int BlockHeight(nint dc, Block b, int width) => b.Kind switch
    {
        BlockKind.Heading => 18,
        BlockKind.Lead => WrappedHeight(dc, b.Text, width, fontLead),
        BlockKind.Link => 24,
        BlockKind.Bullet => WrappedHeight(dc, b.Text, width - 18, fontLabel) + 2,
        _ => WrappedHeight(dc, b.Text, width, fontLabel),
    };

    private static int BlockGap(Block b) => b.Kind switch { BlockKind.Heading => 4, BlockKind.Bullet => 4, BlockKind.Lead => 6, BlockKind.Link => 8, _ => 10 };

    private void DrawAbout(nint dc)
    {
        DimBackdrop(dc);
        hits.Clear();                                    // a modal: nothing behind it can be reached
        var box = AboutBox;
        Box(dc, R(box.Left + 4, box.Top + 6, AboutW, AboutH), Black, -1, 12);
        Box(dc, box, Panel, Accent, 12);

        // a small title: the whole point of this window is below it
        int tx = box.Left + 28;
        Txt(dc, "Bogue", R(tx, box.Top + 14, 200, 38), Win32.DT_LEFT | Mid, Dim, fontBig);
        int x = tx + TextWidth(dc, "Bogue", fontBig) + 8;
        Txt(dc, "::", R(x, box.Top + 14, 40, 38), Win32.DT_LEFT | Mid, Faint, fontBig);
        x += TextWidth(dc, "::", fontBig) + 8;
        Txt(dc, "Broken", R(x, box.Top + 14, 160, 38), Win32.DT_LEFT | Mid, Text, fontBig);
        x += TextWidth(dc, "Broken", fontBig);
        Txt(dc, "Nes 2", R(x, box.Top + 14, 160, 38), Win32.DT_LEFT | Mid, Accent, fontBig);
        Txt(dc, $"version {About.Version}", R(box.Right - 28 - 200, box.Top + 14, 200, 38), Win32.DT_RIGHT | Mid, Faint, fontSmall);

        // how it was made: transparency, plainly
        var area = AboutText;
        int width = area.Right - area.Left - 12;
        int content = AboutContentHeight(dc, width), visible = area.Bottom - area.Top;
        aboutScroll = Math.Clamp(aboutScroll, 0, Math.Max(0, content - visible));
        int saved = Win32.SaveDC(dc);
        Win32.IntersectClipRect(dc, area.Left, area.Top, area.Right, area.Bottom);
        int y = area.Top - aboutScroll;
        foreach (var b in Blocks())
        {
            int h = BlockHeight(dc, b, width);
            if (y + h >= area.Top && y <= area.Bottom)
            {
                switch (b.Kind)
                {
                    case BlockKind.Lead: Txt(dc, b.Text, R(area.Left, y, width, h), Win32.DT_LEFT | Win32.DT_WORDBREAK, White, fontLead); break;
                    case BlockKind.Heading: Txt(dc, b.Text.ToUpperInvariant(), R(area.Left, y, width, h), Win32.DT_LEFT | Win32.DT_SINGLELINE, Accent, fontSection); break;
                    case BlockKind.Bullet:
                        Txt(dc, "\u2022", R(area.Left + 2, y, 14, h), Win32.DT_LEFT | Win32.DT_SINGLELINE, Accent, fontLabel);
                        Txt(dc, b.Text, R(area.Left + 18, y, width - 18, h), Win32.DT_LEFT | Win32.DT_WORDBREAK, Win32.Rgb(208, 212, 224), fontLabel);
                        break;
                    default: Txt(dc, b.Text, R(area.Left, y, width, h), Win32.DT_LEFT | Win32.DT_WORDBREAK, Win32.Rgb(208, 212, 224), fontLabel); break;
                }
            }
            y += h + BlockGap(b);
        }
        Win32.RestoreDC(dc, saved);
        if (content > visible)
        {
            int thumbH = Math.Max(24, visible * visible / content);
            int thumbY = area.Top + (visible - thumbH) * aboutScroll / Math.Max(1, content - visible);
            Box(dc, R(area.Right - 4, area.Top, 3, visible), Field, -1, 1);
            Box(dc, R(area.Right - 4, thumbY, 3, thumbH), Dim, -1, 1);
        }

        // the shoutout, below: plain and quiet, but there
        var plogue = R(box.Left + 28, box.Top + 58 + HowH + 10, AboutW - 56, PlogueH);
        Box(dc, plogue, Field, Line, 10);
        int px = plogue.Left + 20;
        Txt(dc, About.ShoutLabel, R(px, plogue.Top + 10, 420, 18), Win32.DT_LEFT | Win32.DT_SINGLELINE, Accent, fontSection);
        Txt(dc, About.ShoutHeading, R(px, plogue.Top + 28, 420, 26), Win32.DT_LEFT | Mid, White, fontLead);
        Txt(dc, About.ShoutLine, R(px, plogue.Top + 60, plogue.Right - plogue.Left - 40, 22), Win32.DT_LEFT | Mid | Win32.DT_END_ELLIPSIS, Text, fontLabel);
        Txt(dc, About.ShoutBody, R(px, plogue.Top + 84, plogue.Right - plogue.Left - 40, 22), Win32.DT_LEFT | Mid | Win32.DT_END_ELLIPSIS, Dim, fontLabel);
        Txt(dc, About.ShoutNote, R(px, plogue.Top + 114, plogue.Right - plogue.Left - 40, 24), Win32.DT_LEFT | Mid | Win32.DT_END_ELLIPSIS, Faint, fontSmall);
        var link = R(plogue.Right - 20 - 170, plogue.Top + 26, 170, 28);
        bool linkHot = hot == IdAboutLink;
        Box(dc, link, linkHot ? FieldHot : Panel, Accent, 6);
        Txt(dc, About.ShoutUrl.Replace("https://", "").TrimEnd('/'), link, Win32.DT_CENTER | Mid, linkHot ? White : Accent, fontLabel);
        AddHit(IdAboutLink, link, Kind.Button);

        // footer
        Line_(dc, box.Left + 28, box.Bottom - 62, box.Right - 28, box.Bottom - 62, Line);
        Txt(dc, About.Footer, R(box.Left + 28, box.Bottom - 54, AboutW - 56 - 110, 36), Win32.DT_LEFT | Win32.DT_WORDBREAK | Win32.DT_VCENTER, Faint, fontSmall);
        Button(dc, IdAboutClose, "Close", R(box.Right - 28 - 90, box.Bottom - 54, 90, 36), primary: true);
    }

    // ---------------------------------------------------------------- state, events

    private static int ParamOf(int id) => id switch
    {
        IdEmulator => P.Emulator, IdChip => P.Core, IdChannel => P.Channel, IdInstrument or IdGbDuty => P.Instrument,
        IdModeDirect or IdModeRom => P.Mode,
        >= IdConsole and < IdConsole + Cx.Count => P.Console,
        >= IdSlider and < IdSlider + P.Count => id - IdSlider,
        _ => -1,
    };

    private long Signature()
    {
        var a = plugin.Assignment;
        var e = a.Emulator;
        long h = 17;
        void Mix(long v) { h = unchecked(h * 1000003 + v); }
        for (int i = 0; i < plugin.Params.Length; i++) Mix(plugin.Get(i));
        Mix(e?.Id ?? 0); Mix(a.Channel); Mix(a.Problem?.GetHashCode() ?? 0);
        Mix(hot); Mix(active); Mix(openId); Mix(openHover); Mix(openScroll); Mix(aboutOpen ? 1 + aboutScroll : 0); Mix(Environment.TickCount64 < resetFlashUntil ? 1 : 0);
        Mix(inputsOpen ? 1 + liveBits : 0); Mix(captureIndex); Mix(padConnected ? 1 : 0); Mix(Inputs.KeyboardEnabled ? 3 : 4); Mix(Inputs.GamepadEnabled ? 5 : 6); Mix(e?.Runaway == true ? 7 : 8);
        if (e != null)
        {
            Mix(e.ConfigVersion); Mix(e.PictureVersion); Mix(e.MemberCount); Mix(e.RomStatus.GetHashCode()); Mix(e.FramesRun / 4);
            for (int c = 0; c < Ch.Count; c++) { Mix(e.Activity(c)); Mix(e.Owner(c)?.GetHashCode() ?? 0); }
        }
        return h;
    }

    private void Tick()
    {
        plugin.Emulator?.PumpPicture();
        PollInputs();
        long sig = Signature();
        if (sig != lastSignature) Refresh();
    }

    /// <summary>Repaints now (so the control record matches what is on screen before the next event).</summary>
    private void Refresh()
    {
        lastSignature = Signature();
        Win32.InvalidateRect(Hwnd, null, false);
        Win32.UpdateWindow(Hwnd);
    }

    private Hit? HitAt(int x, int y)
    {
        if (openId != 0)
        {
            for (int i = hits.Count - 1; i >= 0; i--) if (hits[i].Kind == Kind.Item && In(hits[i].Rect, x, y)) return hits[i];
            return null;
        }
        for (int i = hits.Count - 1; i >= 0; i--) if (In(hits[i].Rect, x, y)) return hits[i];
        return null;
    }

    private void Paint(nint printDc = 0)
    {
        PAINTSTRUCT ps = default;
        nint hdc = printDc != 0 ? printDc : Win32.BeginPaint(Hwnd, &ps);
        if (memDc == 0)
        {
            memDc = Win32.CreateCompatibleDC(hdc);
            memBmp = Win32.CreateCompatibleBitmap(hdc, Width, Height);
            memOld = Win32.SelectObject(memDc, memBmp);
        }
        Draw(memDc);
        Win32.BitBlt(hdc, 0, 0, Width, Height, memDc, 0, 0, Win32.SRCCOPY);
        if (printDc == 0) Win32.EndPaint(Hwnd, &ps);
    }

    /// <summary>The items of a dropdown, the parameter value (or Game list action) each stands for, and the value that is current.</summary>
    private ((string, string?)[] Items, int[] Values, int Current) BuildItems(int id)
    {
        var emu = plugin.Assignment.Emulator;
        switch (id)
        {
            case IdEmulator:
            {
                var list = new List<(string, string?)>();
                var cur = plugin.Assignment.Emulator;
                list.Add(("Auto", cur != null ? $"fewest emulators  (now #{cur.Id})" : "fewest emulators"));
                for (int i = 1; i <= EmulatorHub.MaxEmulators; i++)
                {
                    var e = EmulatorHub.Get(i);
                    list.Add(($"#{i}", e == null ? "new" : $"{Cx.Names[e.Console]} {e.ChipId}  -  {(e.Mode == 1 ? "ROM" : "Direct")}  -  {e.MemberCount} instance{(e.MemberCount == 1 ? "" : "s")}"));
                }
                return (list.ToArray(), Enumerable.Range(0, list.Count).ToArray(), plugin.Get(P.Emulator));
            }
            case IdChip:
            {
                // only the chips of the console picked above
                var idx = Cx.ChipsOf(plugin.Get(P.Console));
                return (idx.Select(i => (Cx.Chips[i].Id, Cx.Chips[i].Note)).ToArray(), idx, plugin.Get(P.Core));
            }
            case IdGame:
            {
                // values are actions: 0 the built-in game, 1 the game already loaded, 2 choose a file
                var items = new List<(string, string?)>();
                var vals = new List<int>();
                bool nes = plugin.Get(P.Console) == Cx.Nes;
                string? path = emu?.RomPath;
                if (nes) { items.Add(("VRUN (built-in)", path == null ? "loaded" : null)); vals.Add(0); }
                if (path != null) { items.Add((emu!.RomDisplayName, "loaded")); vals.Add(1); }
                items.Add(("Load a game file...", ".nes  .gb  .gbc  .sfc  .smc  .zip")); vals.Add(2);
                int current = path == null ? (nes ? 0 : -1) : 1;
                return (items.ToArray(), vals.ToArray(), current);
            }
            case IdInstrument:
            {
                var found = emu?.Snapshot?.Instruments() ?? [];
                var items = found.Select(i => ($"#{i.Slot}   ${i.Start:X4}", (string?)$"{i.Milliseconds:0} ms, {(i.Loops ? "loops" : "one-shot")}{(i.Sounding ? ", game was playing it" : "")}")).ToArray();
                return (items, found.Select(i => i.Slot).ToArray(), emu?.Snapshot?.Resolve(plugin.Get(P.Instrument)) ?? plugin.Get(P.Instrument));
            }
            case IdGbDuty:
            {
                int gch = Bn2Plugin.GbChannelOf(plugin.Get(P.Channel));
                string own = emu?.GbState != null ? $"the game's ({GbDutyNames[emu.GbState.Duty(gch)]})" : "the game's";
                var items = new (string, string?)[] { (own, "as the game had it"), ("12.5%", null), ("25%", null), ("50%", null), ("75%", null) };
                return (items, [0, 1, 2, 3, 4], plugin.Get(P.Instrument) is >= 1 and <= 4 ? plugin.Get(P.Instrument) : 0);
            }
            case IdChannel when emu?.GbSamplerMode == true:
            {
                var items = new (string, string?)[] { ("Auto", "pulse 1"), ("Pulse 1", null), ("Pulse 2", null), ("Wave", "the game's waveform"), ("Noise", null) };
                return (items, [0, 1, 2, 3, 4], plugin.Get(P.Channel) is >= 0 and <= 4 ? plugin.Get(P.Channel) : 0);
            }
            case IdChannel:
            {
                var list = new List<(string, string?)> { ("Auto", "first free") };
                for (int c = 0; c < Ch.Count; c++)
                {
                    var owner = emu?.Owner(c);
                    string? note = owner == plugin ? "this instance" : owner != null ? "in use"
                        : c == Ch.Mix ? ((emu?.Mode ?? 0) != 1 ? "ROM only" : null)
                        : ((emu?.Mode ?? 0) == 1 && (emu?.Console ?? 0) != Cx.Nes ? "NES games only" : null);
                    list.Add((Ch.Names[c].Replace(" (ROM only)", " (whole game)"), note));
                }
                return (list.ToArray(), Enumerable.Range(0, list.Count).ToArray(), plugin.Get(P.Channel));
            }
        }
        return ([], [], 0);
    }

    private void OpenList(int id, RECT anchor)
    {
        openId = id;
        openAnchor = anchor;
        var (items, values, current) = BuildItems(id);
        openItems = items;
        openValues = values;
        openSelected = Array.IndexOf(values, current);
        openHover = -1;
        int rows = Math.Min(openItems.Length, MaxRows);
        openScroll = Math.Clamp(openSelected - rows / 2, 0, Math.Max(0, openItems.Length - rows));
        Refresh();
    }

    private void CloseList()
    {
        openId = 0;
        openHover = -1;
        Refresh();
    }

    private void Pick(int index)
    {
        int id = openId;
        int value = index >= 0 && index < openValues.Length ? openValues[index] : index;
        openId = 0;
        openHover = -1;
        if (id == IdGame) PickGame(value);
        else plugin.SetFromUi(ParamOf(id), value);
        Refresh();
    }

    private void Activate(Hit h)
    {
        switch (h.Kind)
        {
            case Kind.Dropdown:
                OpenList(h.Id, h.Rect);
                break;
            case Kind.Cell:
                plugin.SetFromUi(P.Channel, h.Id - IdCell + 1);
                Refresh();
                break;
            case Kind.Button:
                if (h.Id == IdAbout) { aboutOpen = true; aboutScroll = 0; }
                else if (h.Id == IdAboutClose) aboutOpen = false;
                else if (h.Id == IdAboutLink) OpenLink(About.ShoutUrl);
                else if (h.Id == IdInputs) inputsOpen = true;
                else if (h.Id == IdInputsClose) { inputsOpen = false; captureIndex = -1; }
                else if (h.Id == IdInputsKeyboard) { Inputs.KeyboardEnabled = !Inputs.KeyboardEnabled; Inputs.Save(); }
                else if (h.Id == IdInputsGamepad) { Inputs.GamepadEnabled = !Inputs.GamepadEnabled; Inputs.Save(); }
                else if (h.Id == IdInputsDefaults) { Inputs.Defaults(); Inputs.Save(); captureIndex = -1; }
                else if (h.Id >= IdInputKey && h.Id < IdInputKey + 12) { captureIndex = h.Id - IdInputKey; captureIsKey = true; captureWait = true; }
                else if (h.Id >= IdInputPad && h.Id < IdInputPad + 12) { captureIndex = h.Id - IdInputPad; captureIsKey = false; captureWait = true; }
                else if (h.Id >= IdPadButton && h.Id < IdPadButton + 12) { heldPad = h.Id - IdPadButton; Win32.SetCapture(Hwnd); }
                else if (h.Id == IdRunaway) { var re = plugin.Emulator; if (re != null) re.SetRunaway(!re.Runaway); }
                else if (h.Id == IdReset) { plugin.Emulator?.Reset(); resetFlashUntil = Environment.TickCount64 + 900; }
                else if (h.Id is IdModeDirect or IdModeRom) plugin.SetFromUi(P.Mode, h.Id - IdModeDirect);
                else if (h.Id >= IdConsole && h.Id < IdConsole + Cx.Count) plugin.SetFromUi(P.Console, h.Id - IdConsole);
                Refresh();
                break;
            case Kind.Slider:
                active = h.Id;
                Win32.SetCapture(Hwnd);
                DragSlider(h, mouseX);
                break;
        }
    }

    /// <summary>The Game list's actions: 0 the built-in game, 1 the game already loaded (nothing to do), 2 a file dialog.</summary>
    private void PickGame(int action)
    {
        var emu = plugin.Emulator;
        if (emu == null) return;
        if (action == 0) emu.UseBuiltInRom();
        else if (action == 2)
        {
            var path = Win32.ChooseFile(Hwnd, "Load a game", Cx.RomFilter);
            if (path != null) emu.LoadRomFile(path);
        }
    }

    private void DragSlider(Hit h, int mx)
    {
        int param = h.Id - IdSlider;
        var def = plugin.Params[param];
        const int labelW = 86, valueW = 78;
        int w = ColW;
        int tx = ColX + labelW, tw = w - labelW - valueW - 8;
        double f = Math.Clamp((mx - tx) / (double)tw, 0, 1);
        plugin.SetFromUi(param, def.Min + (int)Math.Round(f * (def.Max - def.Min)));
        Refresh();
    }

    private void OnMouse(uint msg, nint wParam, nint lParam)
    {
        int x = (short)(lParam & 0xFFFF), y = (short)((lParam >> 16) & 0xFFFF);
        mouseX = x; mouseY = y;
        switch (msg)
        {
            case Win32.WM_MOUSEMOVE:
            {
                if (!trackingLeave)
                {
                    var t = new TRACKMOUSEEVENT { cbSize = (uint)sizeof(TRACKMOUSEEVENT), dwFlags = Win32.TME_LEAVE, hwndTrack = Hwnd };
                    Win32.TrackMouseEvent(&t);
                    trackingLeave = true;
                }
                var h = HitAt(x, y);
                if (active != 0) { var s = hits.FirstOrDefault(k => k.Id == active); if (s.Id != 0) DragSlider(s, x); break; }
                if (openId != 0) { openHover = h is { Kind: Kind.Item } it ? it.Id - IdItem : -1; hot = 0; }
                else hot = h?.Id ?? 0;
                break;
            }
            case Win32.WM_LBUTTONDOWN:
            case Win32.WM_LBUTTONDBLCLK:
            {
                var h = HitAt(x, y);
                if (aboutOpen || inputsOpen)
                {
                    captureIndex = -1;                         // a click anywhere ends a rebinding that was waiting
                    if (h is { } mh) Activate(mh);             // a modal: only its own controls answer
                    Refresh();
                    return;
                }
                if (openId != 0)
                {
                    if (h is { Kind: Kind.Item } it) Pick(it.Id - IdItem); else CloseList();
                    return;
                }
                if (h is { } hit)
                {
                    if (msg == Win32.WM_LBUTTONDBLCLK && hit.Kind == Kind.Slider)
                    {
                        int p = hit.Id - IdSlider;
                        plugin.SetFromUi(p, plugin.Params[p].Default);
                        Refresh();
                    }
                    else Activate(hit);
                }
                break;
            }
            case Win32.WM_LBUTTONUP:
                if (active != 0) { active = 0; Win32.ReleaseCapture(); }
                if (heldPad >= 0) { heldPad = -1; Win32.ReleaseCapture(); }
                break;
            case Win32.WM_RBUTTONUP:
            {
                if (aboutOpen) return;
                if (inputsOpen)
                {
                    var ih = HitAt(x, y);
                    if (ih is { } b)
                    {
                        if (b.Id >= IdInputKey && b.Id < IdInputKey + 12) { Inputs.Keys[b.Id - IdInputKey] = 0; Inputs.Save(); }
                        else if (b.Id >= IdInputPad && b.Id < IdInputPad + 12) { Inputs.Pads[b.Id - IdInputPad] = 0; Inputs.Save(); }
                    }
                    break;
                }
                if (openId != 0) { CloseList(); return; }
                var h = HitAt(x, y);
                if (h is { } hit && ParamOf(hit.Id) >= 0) ParamMenu(ParamOf(hit.Id));
                break;
            }
            case Win32.WM_MOUSEWHEEL:
            {
                int delta = (short)((wParam >> 16) & 0xFFFF) / 120;
                if (aboutOpen) { aboutScroll = Math.Max(0, aboutScroll - delta * 48); break; }
                if (inputsOpen) break;
                if (openId != 0)
                {
                    int rows = Math.Min(openItems.Length, MaxRows);
                    openScroll = Math.Clamp(openScroll - delta, 0, Math.Max(0, openItems.Length - rows));
                    break;
                }
                // the wheel arrives at screen coordinates: use the last known mouse position
                var h = HitAt(mouseX, mouseY);
                if (h is { Kind: Kind.Slider } s)
                {
                    int p = s.Id - IdSlider;
                    var def = plugin.Params[p];
                    plugin.SetFromUi(p, plugin.Get(p) + delta * Math.Max(1, (def.Max - def.Min) / 50));
                }
                else if (h is { Kind: Kind.Dropdown } d && ParamOf(d.Id) is var pp && pp >= 0 && d.Id is not (IdChip or IdInstrument or IdGbDuty))
                    plugin.SetFromUi(pp, plugin.Get(pp) - delta);
                break;
            }
            case Win32.WM_MOUSELEAVE:
                trackingLeave = false;
                hot = 0; openHover = -1;
                break;
        }
        Refresh();
    }

    /// <summary>FL's own parameter menu (link to a controller, create automation...) for the parameter under the mouse.</summary>
    private void ParamMenu(int index)
    {
        var menu = Win32.CreatePopupMenu();
        for (int item = 0; ; item++)
        {
            var entry = (TParamMenuEntry*)plugin.Host.Dispatcher(plugin.HostTag, Fhd.GetParamMenuEntry, index, item);
            if (entry == null || entry->Name == null) break;
            uint flags = entry->Name[0] == (byte)'-' && entry->Name[1] == 0 ? Win32.MF_SEPARATOR : Win32.MF_STRING;
            if ((entry->Flags & Fhp.Disabled) != 0) flags |= Win32.MF_GRAYED;
            if ((entry->Flags & Fhp.Checked) != 0) flags |= Win32.MF_CHECKED;
            Win32.AppendMenu(menu, flags, (nuint)(item + 1), entry->Name);
        }
        POINT pt;
        Win32.GetCursorPos(&pt);
        int chosen = Win32.TrackPopupMenu(menu, Win32.TPM_RETURNCMD | Win32.TPM_RIGHTBUTTON, pt.X, pt.Y, 0, Hwnd, 0);
        Win32.DestroyMenu(menu);
        if (chosen > 0) plugin.Host.Dispatcher(plugin.HostTag, Fhd.ParamMenu, index, chosen - 1);
    }

    // ---------------------------------------------------------------- test hooks

    /// <summary>The editor questions the test host asks (Fpd.TestBase + n): 30 packed rectangle of control <paramref name="index"/>
    /// (x | y &lt;&lt; 16 | w &lt;&lt; 32 | h &lt;&lt; 48; 0 when the control is not on screen), 31 id of the open dropdown (0 none),
    /// 32 its item count, 33 number of paints, 34 id under the mouse, 35 picture shown (1/0), 36 About window open (1/0).</summary>
    public nint Test(int n, nint index, nint value)
    {
        switch (n)
        {
            case 30:
                foreach (var h in hits)
                    if (h.Id == index)
                        return (nint)((long)h.Rect.Left | ((long)h.Rect.Top << 16) | ((long)(h.Rect.Right - h.Rect.Left) << 32) | ((long)(h.Rect.Bottom - h.Rect.Top) << 48));
                return 0;
            case 31: return openId;
            case 32: return openItems.Length;
            case 33: return (nint)paints;
            case 34: return hot;
            case 35: return hasPicture && plugin.Emulator?.PictureMessage == null ? 1 : 0;
            case 36: return aboutOpen ? 1 : 0;
            case 38: return inputsOpen ? 1 : 0;
            case 39: return liveBits;
            case 37: return picW | (picH << 16);
        }
        return 0;
    }

    // ---------------------------------------------------------------- window plumbing

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        var handle = Win32.GetWindowLongPtr(hwnd, Win32.GWLP_USERDATA);
        if (handle != 0 && GCHandle.FromIntPtr(handle).Target is Bn2Editor ed)
        {
            try
            {
                switch (msg)
                {
                    case Win32.WM_PAINT: ed.Paint(wParam); return 0;
                    case Win32.WM_PRINTCLIENT: ed.Paint(wParam); return 0;
                    case Win32.WM_ERASEBKGND: return 1;
                    case Win32.WM_TIMER: ed.Tick(); return 0;
                    case Win32.WM_MOUSEMOVE:
                    case Win32.WM_LBUTTONDOWN:
                    case Win32.WM_LBUTTONDBLCLK:
                    case Win32.WM_LBUTTONUP:
                    case Win32.WM_RBUTTONUP:
                    case Win32.WM_MOUSEWHEEL:
                    case Win32.WM_MOUSELEAVE:
                        ed.OnMouse(msg, wParam, lParam);
                        return 0;
                    case Win32.WM_CAPTURECHANGED:
                        ed.active = 0; ed.heldPad = -1;
                        return 0;
                    case Win32.WM_SETCURSOR:
                        if ((int)(lParam & 0xFFFF) == 1 /* HTCLIENT */)
                        {
                            bool hand = ed.hot != 0 || ed.openHover >= 0 || ed.active != 0;
                            Win32.SetCursor(Win32.LoadCursor(0, hand ? Win32.IDC_HAND : Win32.IDC_ARROW));
                            return 1;
                        }
                        break;
                }
            }
            catch (Exception e) { Diag.Log($"Bn2Editor exception: {e}"); }
        }
        return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static void EnsureClass(nint module)
    {
        if (registered) return;
        fixed (char* name = ClassName)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                style = Win32.CS_DBLCLKS,
                lpfnWndProc = (delegate* unmanaged<nint, uint, nint, nint, nint>)&WndProc,
                hInstance = module,
                hCursor = Win32.LoadCursor(0, Win32.IDC_ARROW),
                lpszClassName = name,
            };
            Win32.RegisterClassEx(&wc);
        }
        registered = true;
    }

    private static nint OwnModule()
    {
        nint module;
        var addr = (nint)(delegate* unmanaged<nint, uint, nint, nint, nint>)&WndProc;
        Win32.GetModuleHandleEx(Win32.GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | Win32.GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, addr, &module);
        return module;
    }
}
