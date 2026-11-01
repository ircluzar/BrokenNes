// A single-line text field for names and search: caret, selection of everything (Ctrl+A or a double click), typing replaces it, Backspace,
// Delete, Left, Right, Home, End, Enter commits, Esc cancels. The editor window delivers WM_CHAR and WM_KEYDOWN to the focused field.
// (No clipboard: pasting is not supported yet.)
using BrokenNes.Fruity;

namespace BrokenNes2.Ui;

internal sealed class TextInput
{
    public const int VkEnd = 0x23, VkHome = 0x24, VkLeft = 0x25, VkRight = 0x27, VkDelete = 0x2E;

    public readonly int Id;
    public string Placeholder = "";
    public int MaxLength = 48;
    public bool Focused { get; private set; }

    private string text = "";
    private int caret;
    private bool all;
    private int[] prefix = [0];        // x offset of each caret position, measured when the text last changed
    private bool measured;
    public RECT Rect;

    /// <summary>Raised on Enter / Esc with the field's text; the owner decides what they mean.</summary>
    public Action<string>? Committed, Cancelled;
    /// <summary>Raised after every edit.</summary>
    public Action<string>? Changed;

    public TextInput(int id) { Id = id; }

    public string Text
    {
        get => text;
        set { text = (value ?? "").Length > MaxLength ? value![..MaxLength] : value ?? ""; caret = Math.Min(caret, text.Length); all = false; measured = false; }
    }

    public void Focus(bool selectAll = false) { Focused = true; all = selectAll && text.Length > 0; caret = text.Length; }
    public void Blur() { Focused = false; all = false; }

    /// <summary>Folded into the editor's repaint signature: the blink phase and the caret.</summary>
    public long Signature() => Focused ? (Environment.TickCount64 / 500 & 1) + caret * 2 + (all ? 1000 : 0) + text.GetHashCode() : 0;

    public void Draw(UiCanvas c, nint dc, RECT r, bool enabled = true)
    {
        Rect = r;
        bool hot = c.IsHot(Id);
        c.Box(dc, r, hot || Focused ? UiCanvas.FieldHot : UiCanvas.Field, Focused ? UiCanvas.Accent : UiCanvas.Line, 5);
        var inner = UiCanvas.R(r.Left + 8, r.Top, UiCanvas.W(r) - 16, UiCanvas.H(r));
        if (!measured) Measure(c, dc);
        // scroll the text so the caret stays inside the box
        int caretX = prefix[Math.Min(caret, prefix.Length - 1)], shift = Math.Max(0, caretX - UiCanvas.W(inner) + 4);
        int saved = c.Clip(dc, inner);
        if (text.Length == 0 && !Focused) c.Txt(dc, Placeholder, inner, Win32.DT_LEFT | UiCanvas.Mid, UiCanvas.Faint, c.FontLabel);
        else
        {
            if (all) c.Fill(dc, UiCanvas.R(inner.Left - shift, inner.Top + 4, prefix[^1], UiCanvas.H(inner) - 8), UiCanvas.AccentDim);
            c.Txt(dc, text, UiCanvas.R(inner.Left - shift, inner.Top, prefix[^1] + 16, UiCanvas.H(inner)), Win32.DT_LEFT | UiCanvas.Mid, enabled ? UiCanvas.Text : UiCanvas.Faint, c.FontLabel);
            if (Focused && (Environment.TickCount64 / 500 & 1) == 0)
                c.Fill(dc, UiCanvas.R(inner.Left - shift + caretX, inner.Top + 5, 1, UiCanvas.H(inner) - 10), UiCanvas.White);
        }
        c.Unclip(dc, saved);
        c.AddHit(Id, r);
    }

    private void Measure(UiCanvas c, nint dc)
    {
        prefix = new int[text.Length + 1];
        for (int i = 1; i <= text.Length; i++) prefix[i] = c.TextWidth(dc, text, i, c.FontLabel);
        measured = true;
    }

    /// <summary>A press at mouse x: puts the caret between the characters nearest to it (a double click selects all).</summary>
    public void Click(int x, bool doubleClick)
    {
        Focused = true;
        if (doubleClick && text.Length > 0) { all = true; caret = text.Length; return; }
        all = false;
        int caretX = prefix[Math.Min(caret, prefix.Length - 1)], inner = UiCanvas.W(Rect) - 16, shift = Math.Max(0, caretX - inner + 4);
        int rel = x - (Rect.Left + 8) + shift, best = 0;
        for (int i = 0; i < prefix.Length; i++) if (Math.Abs(prefix[i] - rel) < Math.Abs(prefix[best] - rel)) best = i;
        caret = best;
    }

    /// <summary>A character from WM_CHAR. Returns true when the field used it.</summary>
    public bool Char(char ch)
    {
        if (!Focused || UiMutate.Has(UiMutate.TextInputDead)) return false;
        switch (ch)
        {
            case '\r': Committed?.Invoke(text); return true;
            case (char)27: Cancelled?.Invoke(text); return true;
            case (char)1: all = text.Length > 0; caret = text.Length; return true;          // Ctrl+A
            case '\b':
                if (all) Set("", 0);
                else if (caret > 0) Set(text.Remove(caret - 1, 1), caret - 1);
                return true;
        }
        if (ch < 32 || ch == 127) return false;
        if (all) Set(ch.ToString(), 1);
        else if (text.Length < MaxLength) Set(text.Insert(caret, ch.ToString()), caret + 1);
        return true;
    }

    /// <summary>A virtual key from WM_KEYDOWN. Returns true when the field used it.</summary>
    public bool Key(int vk)
    {
        if (!Focused) return false;
        switch (vk)
        {
            case VkLeft: caret = all ? 0 : Math.Max(0, caret - 1); all = false; return true;
            case VkRight: caret = all ? text.Length : Math.Min(text.Length, caret + 1); all = false; return true;
            case VkHome: caret = 0; all = false; return true;
            case VkEnd: caret = text.Length; all = false; return true;
            case VkDelete:
                if (all) Set("", 0);
                else if (caret < text.Length) Set(text.Remove(caret, 1), caret);
                return true;
        }
        return false;
    }

    private void Set(string value, int newCaret)
    {
        text = value; caret = Math.Clamp(newCaret, 0, text.Length); all = false; measured = false;
        Changed?.Invoke(text);
    }
}
