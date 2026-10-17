// The drawing surface the tab widgets share: the editor's palette, fonts, and the few GDI shapes it is built from (a rounded box, a dot, a line,
// text), with a pen/brush cache. It is the same look as Bn2Editor's own primitives (kept apart so the widgets do not depend on the editor class and
// the editor's file stays untouched by the widgets). Hit areas are handed to the editor, which owns hover, capture and the control record the
// self-test reads (Fpd.TestBase + 30).
using BrokenNes.Fruity;

namespace BrokenNes2.Ui;

internal sealed unsafe class UiCanvas : IDisposable
{
    // palette: identical to Bn2Editor's
    public static readonly int Bg = Win32.Rgb(20, 22, 27), Panel = Win32.Rgb(28, 31, 38), Field = Win32.Rgb(37, 41, 52), FieldHot = Win32.Rgb(47, 52, 66),
        Line = Win32.Rgb(52, 57, 71), Text = Win32.Rgb(228, 231, 239), Dim = Win32.Rgb(140, 147, 166), Faint = Win32.Rgb(88, 94, 112),
        Accent = Win32.Rgb(255, 98, 76), AccentDim = Win32.Rgb(112, 46, 40), Good = Win32.Rgb(86, 208, 144), Warn = Win32.Rgb(255, 184, 76),
        Black = Win32.Rgb(8, 9, 12), White = Win32.Rgb(255, 255, 255), Wave = Win32.Rgb(86, 170, 255), WaveLoop = Win32.Rgb(34, 46, 70);

    public const uint Mid = Win32.DT_SINGLELINE | Win32.DT_VCENTER;

    private readonly Dictionary<int, nint> brushes = new(), pens = new();
    private readonly Action<int, RECT> addHit;

    public readonly nint FontLabel, FontSmall, FontSection, FontLead, FontMono;

    /// <summary>The control under the mouse and the one being dragged (set by the editor before each paint).</summary>
    public int Hot, Active;

    public UiCanvas(Action<int, RECT> addHit)
    {
        this.addHit = addHit;
        FontLabel = Win32.MakeFont("Segoe UI", 14, Win32.FW_NORMAL);
        FontSmall = Win32.MakeFont("Segoe UI", 12, Win32.FW_NORMAL);
        FontSection = Win32.MakeFont("Segoe UI", 12, Win32.FW_BOLD);
        FontLead = Win32.MakeFont("Segoe UI", 18, Win32.FW_SEMIBOLD);
        FontMono = Win32.MakeFont("Consolas", 12, Win32.FW_NORMAL);
    }

    public void Dispose()
    {
        foreach (var f in new[] { FontLabel, FontSmall, FontSection, FontLead, FontMono }) Win32.DeleteObject(f);
        foreach (var b in brushes.Values) Win32.DeleteObject(b);
        foreach (var p in pens.Values) Win32.DeleteObject(p);
        brushes.Clear(); pens.Clear();
    }

    public static RECT R(int l, int t, int w, int h) => new() { Left = l, Top = t, Right = l + w, Bottom = t + h };
    public static bool In(RECT r, int x, int y) => x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;
    public static int W(RECT r) => r.Right - r.Left;
    public static int H(RECT r) => r.Bottom - r.Top;

    /// <summary>Registers a control's area under <paramref name="id"/> (the editor's control record: hover, clicks and the self-test's rectangle query).</summary>
    public void AddHit(int id, RECT r) => addHit(id, r);
    public bool IsHot(int id) => Hot == id || Active == id;

    private nint Brush(int c) { if (!brushes.TryGetValue(c, out var b)) brushes[c] = b = Win32.CreateSolidBrush(c); return b; }
    private nint Pen(int c) { if (!pens.TryGetValue(c, out var p)) pens[c] = p = Win32.CreatePen(Win32.PS_SOLID, 1, c); return p; }

    public void Fill(nint dc, RECT r, int color) => Win32.FillRect(dc, &r, Brush(color));

    /// <summary>A rounded box with a 1 px border (border &lt; 0: none).</summary>
    public void Box(nint dc, RECT r, int fill, int border, int radius)
    {
        var oldB = Win32.SelectObject(dc, Brush(fill));
        var oldP = Win32.SelectObject(dc, border < 0 ? Win32.GetStockObject(Win32.NULL_PEN) : Pen(border));
        Win32.RoundRect(dc, r.Left, r.Top, r.Right + (border < 0 ? 1 : 0), r.Bottom + (border < 0 ? 1 : 0), radius * 2, radius * 2);
        Win32.SelectObject(dc, oldB);
        Win32.SelectObject(dc, oldP);
    }

    public void Dot(nint dc, int cx, int cy, int radius, int fill)
    {
        var oldB = Win32.SelectObject(dc, Brush(fill));
        var oldP = Win32.SelectObject(dc, Win32.GetStockObject(Win32.NULL_PEN));
        Win32.Ellipse(dc, cx - radius, cy - radius, cx + radius + 1, cy + radius + 1);
        Win32.SelectObject(dc, oldB);
        Win32.SelectObject(dc, oldP);
    }

    public void Ln(nint dc, int x0, int y0, int x1, int y1, int color)
    {
        var old = Win32.SelectObject(dc, Pen(color));
        Win32.MoveToEx(dc, x0, y0, null);
        Win32.LineTo(dc, x1, y1);
        Win32.SelectObject(dc, old);
    }

    public void Txt(nint dc, string s, RECT r, uint flags, int color, nint font)
    {
        if (s.Length == 0) return;
        Win32.SelectObject(dc, font);
        Win32.SetTextColor(dc, color);
        fixed (char* p = s) Win32.DrawText(dc, p, s.Length, &r, flags | Win32.DT_NOPREFIX);
    }

    public int TextWidth(nint dc, string s, nint font)
    {
        if (s.Length == 0) return 0;
        Win32.SelectObject(dc, font);
        var r = new RECT();
        fixed (char* p = s) Win32.DrawText(dc, p, s.Length, &r, Win32.DT_SINGLELINE | 0x400 /* DT_CALCRECT */ | Win32.DT_NOPREFIX);
        return r.Right - r.Left;
    }

    /// <summary>Width of the first <paramref name="count"/> characters of <paramref name="s"/> (for placing a caret).</summary>
    public int TextWidth(nint dc, string s, int count, nint font) => TextWidth(dc, s.Substring(0, Math.Clamp(count, 0, s.Length)), font);

    /// <summary>Starts a clip to <paramref name="r"/>; pass the result to <see cref="Unclip"/>.</summary>
    public int Clip(nint dc, RECT r)
    {
        int saved = Win32.SaveDC(dc);
        Win32.IntersectClipRect(dc, r.Left, r.Top, r.Right, r.Bottom);
        return saved;
    }

    public void Unclip(nint dc, int saved) => Win32.RestoreDC(dc, saved);

    /// <summary>A push button: the editor's own look. Registers its hit area under <paramref name="id"/>. A disabled button is drawn quiet and still answers a click (the answer says why it did nothing).</summary>
    public void Button(nint dc, int id, string label, RECT r, bool primary = false, bool enabled = true)
    {
        bool hot = Hot == id;
        if (enabled) Box(dc, r, primary ? (hot ? Accent : AccentDim) : (hot ? FieldHot : Field), primary ? Accent : Line, 5);
        else Box(dc, r, hot ? Field : Panel, Line, 5);
        Txt(dc, label, r, Win32.DT_CENTER | Mid | Win32.DT_END_ELLIPSIS, enabled ? White : Faint, FontLabel);
        AddHit(id, r);
    }

    /// <summary>A small check box with a label to its right. Registers the box plus label as the hit area.</summary>
    public void Check(nint dc, int id, string label, int x, int y, int labelW, bool on)
    {
        var box = R(x, y, 18, 18);
        bool hot = Hot == id;
        Box(dc, box, on ? Accent : (hot ? FieldHot : Field), on ? Accent : Line, 4);
        if (on) { Ln(dc, x + 4, y + 9, x + 7, y + 12, White); Ln(dc, x + 7, y + 12, x + 14, y + 5, White); Ln(dc, x + 4, y + 10, x + 7, y + 13, White); Ln(dc, x + 7, y + 13, x + 14, y + 6, White); }
        Txt(dc, label, R(x + 26, y - 3, labelW, 24), Win32.DT_LEFT | Mid | Win32.DT_END_ELLIPSIS, Text, FontLabel);
        AddHit(id, R(x, y - 2, 26 + labelW, 22));
    }

    /// <summary>A section heading: accent title on the left, a faint note on the right, a rule under it.</summary>
    public void Section(nint dc, string title, string? note, int x, int y, int w)
    {
        Txt(dc, title, R(x, y, w, 16), Win32.DT_LEFT | Win32.DT_SINGLELINE, Accent, FontSection);
        if (note != null) Txt(dc, note, R(x, y, w, 16), Win32.DT_RIGHT | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS, Faint, FontSmall);
        Ln(dc, x, y + 19, x + w, y + 19, Line);
    }
}
