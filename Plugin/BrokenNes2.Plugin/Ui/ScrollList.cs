// A vertical scroll bar (the math and the drawing) and the scrolling list built on it: mouse wheel, a draggable thumb, a click in the track pages.
using BrokenNes.Fruity;

namespace BrokenNes2.Ui;

/// <summary>A thin vertical scroll bar. Position and page are in whatever unit the owner scrolls by (rows, mostly).</summary>
internal sealed class VScrollBar
{
    public const int Width = 10;
    private int grab = -1;

    public RECT Track;
    public int Total, Page, Pos;

    public bool Needed => Total > Page && Page > 0;
    public int MaxPos => Math.Max(0, Total - Page);
    public bool Dragging => grab >= 0;

    private int ThumbLen => Math.Max(22, UiCanvas.H(Track) * Page / Math.Max(1, Total));
    private int ThumbTop => Track.Top + (UiCanvas.H(Track) - ThumbLen) * Pos / Math.Max(1, MaxPos);

    public void Layout(RECT track, int total, int page)
    {
        Track = track; Total = total; Page = page;
        Pos = Math.Clamp(Pos, 0, MaxPos);
    }

    public void Draw(UiCanvas c, nint dc, bool hot)
    {
        if (!Needed) return;
        c.Box(dc, Track, UiCanvas.Field, -1, 4);
        c.Box(dc, UiCanvas.R(Track.Left + 1, ThumbTop, UiCanvas.W(Track) - 2, ThumbLen), hot || grab >= 0 ? UiCanvas.Dim : UiCanvas.Faint, -1, 3);
    }

    /// <summary>A press at <paramref name="y"/>: on the thumb it starts a drag (returns true), in the track it pages.</summary>
    public bool Down(int y)
    {
        if (!Needed) return false;
        int top = ThumbTop, len = ThumbLen;
        if (y >= top && y < top + len) { grab = y - top; return true; }
        Pos = Math.Clamp(Pos + (y < top ? -Page : Page), 0, MaxPos);
        return false;
    }

    public void Drag(int y)
    {
        if (grab < 0) return;
        int room = Math.Max(1, UiCanvas.H(Track) - ThumbLen);
        Pos = Math.Clamp((int)Math.Round((y - grab - Track.Top) * (double)MaxPos / room), 0, MaxPos);
    }

    public void Up() => grab = -1;
}

/// <summary>
/// A list of equal rows in a bordered panel. The owner draws each row (<see cref="Draw"/> calls back with the row's rectangle), decides what a
/// click means (<see cref="RowOf"/> turns a control id into a row), and forwards wheel, scroll bar presses and drags.
/// Rows register hit ids <c>rowBaseId + visible slot</c>; the scroll bar registers <c>barId</c>.
/// </summary>
internal sealed class ScrollList
{
    public const int WheelRows = 3;

    private readonly int rowBaseId, barId;
    private readonly VScrollBar bar = new();

    public int RowH = 24;
    public int Count, Selected = -1;
    public RECT Rect;

    public ScrollList(int rowBaseId, int barId) { this.rowBaseId = rowBaseId; this.barId = barId; }

    public int Scroll { get => bar.Pos; set => bar.Pos = Math.Max(0, value); }
    public int Visible { get; private set; }
    public bool BarNeeded => bar.Needed;
    public bool Dragging => bar.Dragging;

    /// <summary>The row (absolute index) a control id of this list stands for, or -1.</summary>
    public int RowOf(int id)
    {
        int slot = id - rowBaseId;
        if (slot < 0 || slot >= Visible) return -1;
        int row = bar.Pos + slot;
        return row < Count ? row : -1;
    }

    public bool OwnsBar(int id) => id == barId;
    public bool Owns(int id) => id == barId || (id >= rowBaseId && id < rowBaseId + Math.Max(1, Visible));

    public void EnsureVisible(int row)
    {
        if (row < 0 || Visible <= 0) return;
        if (row < bar.Pos) bar.Pos = row;
        else if (row >= bar.Pos + Visible) bar.Pos = row - Visible + 1;
        bar.Pos = Math.Clamp(bar.Pos, 0, bar.MaxPos);
    }

    public void Draw(UiCanvas c, nint dc, RECT rect, int count, Action<int, RECT, bool, bool> drawRow)
    {
        Rect = rect; Count = count;
        c.Box(dc, rect, UiCanvas.Panel, UiCanvas.Line, 6);
        var inner = UiCanvas.R(rect.Left + 3, rect.Top + 3, UiCanvas.W(rect) - 6, UiCanvas.H(rect) - 6);
        Visible = Math.Max(1, UiCanvas.H(inner) / RowH);
        bar.Layout(UiCanvas.R(inner.Right - VScrollBar.Width, inner.Top, VScrollBar.Width, UiCanvas.H(inner)), count, Visible);
        int rowW = UiCanvas.W(inner) - (bar.Needed ? VScrollBar.Width + 2 : 0);
        int saved = c.Clip(dc, inner);
        for (int slot = 0; slot < Visible; slot++)
        {
            int row = bar.Pos + slot;
            if (row >= count) break;
            var rr = UiCanvas.R(inner.Left, inner.Top + slot * RowH, rowW, RowH);
            int id = rowBaseId + slot;
            drawRow(row, rr, row == Selected, c.Hot == id);
            c.AddHit(id, rr);
        }
        c.Unclip(dc, saved);
        if (bar.Needed)
        {
            bar.Draw(c, dc, c.IsHot(barId));
            c.AddHit(barId, UiCanvas.R(bar.Track.Left - 2, bar.Track.Top, bar.Track.Right - bar.Track.Left + 4, UiCanvas.H(bar.Track)));
        }
    }

    /// <summary>The wheel (positive = away from the user = up). False when the list did not move.</summary>
    public bool Wheel(int delta)
    {
        if (UiMutate.Has(UiMutate.ListWheelDead)) return false;
        int before = bar.Pos;
        bar.Pos = Math.Clamp(bar.Pos - delta * WheelRows, 0, bar.MaxPos);
        return bar.Pos != before;
    }

    /// <summary>A press on the scroll bar; true when it started a drag (the editor then captures the mouse).</summary>
    public bool BarDown(int y) => bar.Down(y);
    public void BarDrag(int y) => bar.Drag(y);
    public void BarUp() => bar.Up();
}
