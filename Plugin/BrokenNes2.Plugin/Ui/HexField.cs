// A hex editor for a blob: 16 bytes a row with an offset column, one byte selected (a click, or the arrow keys), two hex digits typed over it write
// it and move on. Rows scroll with the wheel and a bar. The owner holds the bytes: an edit is reported (offset, new value) and the owner decides what it does
// (the Instruments tab hands it to the model, which bumps its Version, and the field is then given the model's bytes again).
using BrokenNes.Fruity;

namespace BrokenNes2.Ui;

internal sealed class HexField
{
    public const int Cols = 16, RowH = 14;
    public const int VkEnd = 0x23, VkHome = 0x24, VkLeft = 0x25, VkUp = 0x26, VkRight = 0x27, VkDown = 0x28;

    public readonly int Id, BarId;
    private readonly VScrollBar bar = new();
    private byte[] data = [];
    private int sel = -1, pending = -1, charW = 7, offW;
    private RECT grid;
    private int rows;

    public bool Focused { get; private set; }
    public bool Editable = true;
    public RECT Rect;

    /// <summary>Raised with (offset, new byte value) when a typed byte completes.</summary>
    public Action<int, byte>? Edited;

    public HexField(int id, int barId) { Id = id; BarId = barId; }

    public int Selected => sel;
    public int FirstRow { get => bar.Pos; set => bar.Pos = Math.Max(0, value); }
    public int VisibleRows { get; private set; }

    public void SetData(byte[] bytes)
    {
        data = bytes;
        if (sel >= data.Length) sel = data.Length - 1;
        pending = -1;
    }

    public void Focus() { Focused = true; if (sel < 0 && data.Length > 0) sel = 0; }
    public void Blur() { Focused = false; pending = -1; }
    public long Signature() => (Focused ? 1 : 0) + sel * 4L + (pending + 2) * 100000L + bar.Pos * 1000L;

    public void Draw(UiCanvas c, nint dc, RECT r)
    {
        Rect = r;
        c.Box(dc, r, UiCanvas.Bg, Focused ? UiCanvas.Accent : UiCanvas.Line, 5);
        charW = Math.Max(5, c.TextWidth(dc, "0000000000", c.FontMono) / 10);
        offW = charW * 6;
        rows = (data.Length + Cols - 1) / Cols;
        VisibleRows = Math.Max(1, (UiCanvas.H(r) - 8) / RowH);
        bar.Layout(UiCanvas.R(r.Right - VScrollBar.Width - 3, r.Top + 4, VScrollBar.Width, UiCanvas.H(r) - 8), rows, VisibleRows);
        grid = UiCanvas.R(r.Left + 8, r.Top + 4, UiCanvas.W(r) - 16, VisibleRows * RowH);
        if (data.Length == 0)
        {
            c.Txt(dc, "no bytes", r, Win32.DT_CENTER | UiCanvas.Mid, UiCanvas.Faint, c.FontSmall);
            return;
        }
        int saved = c.Clip(dc, grid);
        for (int row = 0; row < VisibleRows; row++)
        {
            int first = (bar.Pos + row) * Cols;
            if (first >= data.Length) break;
            int y = grid.Top + row * RowH;
            c.Txt(dc, first.ToString("X4"), UiCanvas.R(grid.Left, y, offW, RowH), Win32.DT_LEFT | UiCanvas.Mid, UiCanvas.Faint, c.FontMono);
            for (int col = 0; col < Cols && first + col < data.Length; col++)
            {
                int i = first + col, x = grid.Left + offW + col * 3 * charW + (col >= 8 ? charW : 0);
                var cell = UiCanvas.R(x - 1, y, charW * 2 + 2, RowH);
                bool isSel = i == sel;
                if (isSel) c.Box(dc, cell, Focused ? UiCanvas.AccentDim : UiCanvas.Field, Focused ? UiCanvas.Accent : -1, 2);
                string s = isSel && pending >= 0 ? pending.ToString("X") + "_" : data[i].ToString("X2");
                c.Txt(dc, s, cell, Win32.DT_CENTER | UiCanvas.Mid, isSel ? UiCanvas.White : UiCanvas.Text, c.FontMono);
            }
        }
        c.Unclip(dc, saved);
        c.AddHit(Id, UiCanvas.R(r.Left, r.Top, UiCanvas.W(r) - (bar.Needed ? VScrollBar.Width + 4 : 0), UiCanvas.H(r)));
        if (bar.Needed)
        {
            bar.Draw(c, dc, c.IsHot(BarId));
            c.AddHit(BarId, UiCanvas.R(bar.Track.Left - 2, bar.Track.Top, VScrollBar.Width + 4, UiCanvas.H(bar.Track)));
        }
    }

    /// <summary>A press on the grid: selects the byte under it (when there is one) and takes the keyboard.</summary>
    public void Click(int x, int y)
    {
        Focus();
        pending = -1;
        int row = (y - grid.Top) / RowH;
        if (row < 0 || row >= VisibleRows) return;
        int rel = x - (grid.Left + offW);
        if (rel < 0) return;
        int col = rel / (3 * charW);
        if (col >= 8) col = (rel - charW) / (3 * charW);
        col = Math.Clamp(col, 0, Cols - 1);
        int i = (bar.Pos + row) * Cols + col;
        if (i < data.Length) sel = i;
    }

    public bool Wheel(int delta)
    {
        if (UiMutate.Has(UiMutate.ListWheelDead)) return false;
        int before = bar.Pos;
        bar.Pos = Math.Clamp(bar.Pos - delta, 0, bar.MaxPos);
        return bar.Pos != before;
    }

    public bool BarDown(int y) => bar.Down(y);
    public void BarDrag(int y) => bar.Drag(y);
    public void BarUp() => bar.Up();

    private void Select(int i)
    {
        if (data.Length == 0) return;
        sel = Math.Clamp(i, 0, data.Length - 1);
        pending = -1;
        int row = sel / Cols;
        if (row < bar.Pos) bar.Pos = row; else if (row >= bar.Pos + VisibleRows) bar.Pos = row - VisibleRows + 1;
    }

    /// <summary>A hex digit types over the selected byte (high nibble first); Esc drops a half-typed byte. True when used.</summary>
    public bool Char(char ch)
    {
        if (!Focused || sel < 0) return false;
        if (ch == (char)27) { bool had = pending >= 0; pending = -1; return had; }
        int d = ch is >= '0' and <= '9' ? ch - '0' : ch is >= 'a' and <= 'f' ? ch - 'a' + 10 : ch is >= 'A' and <= 'F' ? ch - 'A' + 10 : -1;
        if (d < 0) return false;
        if (!Editable) return true;
        if (pending < 0) { pending = d; return true; }
        byte v = (byte)((pending << 4) | d);
        pending = -1;
        if (!UiMutate.Has(UiMutate.HexEditDead)) Edited?.Invoke(sel, v);
        Select(sel + 1);
        return true;
    }

    public bool Key(int vk)
    {
        if (!Focused || sel < 0) return false;
        switch (vk)
        {
            case VkLeft: Select(sel - 1); return true;
            case VkRight: Select(sel + 1); return true;
            case VkUp: Select(sel - Cols); return true;
            case VkDown: Select(sel + Cols); return true;
            case VkHome: Select(0); return true;
            case VkEnd: Select(data.Length - 1); return true;
        }
        return false;
    }
}
