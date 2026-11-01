// A stepper: [ - ] value [ + ]. Two hit areas, <c>minusId</c> and <c>minusId + 1</c>; the owner keeps the value and applies the step
// (the owner decides the step size: one, or ten with Shift held).
using BrokenNes.Fruity;

namespace BrokenNes2.Ui;

internal sealed class Stepper
{
    public const int ButtonW = 24;
    public readonly int MinusId;
    public int PlusId => MinusId + 1;

    public Stepper(int minusId) { MinusId = minusId; }

    /// <summary>Draws the two buttons at the ends of <paramref name="area"/> and, between them, <paramref name="text"/> when given.</summary>
    public void Draw(UiCanvas c, nint dc, RECT area, string? text, bool canMinus, bool canPlus, bool enabled = true)
    {
        var minus = UiCanvas.R(area.Left, area.Top, ButtonW, UiCanvas.H(area));
        var plus = UiCanvas.R(area.Right - ButtonW, area.Top, ButtonW, UiCanvas.H(area));
        Face(c, dc, minus, "-", MinusId, enabled && canMinus);
        Face(c, dc, plus, "+", PlusId, enabled && canPlus);
        if (text != null)
        {
            var mid = UiCanvas.R(minus.Right + 4, area.Top, UiCanvas.W(area) - ButtonW * 2 - 8, UiCanvas.H(area));
            c.Box(dc, mid, UiCanvas.Bg, UiCanvas.Line, 5);
            c.Txt(dc, text, mid, Win32.DT_CENTER | UiCanvas.Mid | Win32.DT_END_ELLIPSIS, enabled ? UiCanvas.Text : UiCanvas.Faint, c.FontLabel);
        }
        c.AddHit(MinusId, minus);
        c.AddHit(PlusId, plus);
    }

    private static void Face(UiCanvas c, nint dc, RECT r, string glyph, int id, bool live)
    {
        bool hot = live && c.Hot == id;
        c.Box(dc, r, hot ? UiCanvas.FieldHot : UiCanvas.Field, UiCanvas.Line, 5);
        c.Txt(dc, glyph, r, Win32.DT_CENTER | UiCanvas.Mid, live ? UiCanvas.White : UiCanvas.Faint, c.FontLabel);
    }

    /// <summary>The step a press on <paramref name="id"/> stands for: -1, +1 (times ten with <paramref name="big"/>); 0 when the id is not this stepper's.</summary>
    public int StepOf(int id, bool big)
    {
        int s = id == MinusId ? -1 : id == PlusId ? 1 : 0;
        if (UiMutate.Has(UiMutate.StepperReversed)) s = -s;
        return s * (big ? 10 : 1);
    }
}
