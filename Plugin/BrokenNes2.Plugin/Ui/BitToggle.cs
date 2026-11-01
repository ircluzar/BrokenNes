// A row of bit toggles for a bit field, most significant bit on the left (as the hardware registers are written). One hit area per bit,
// <c>baseId + bit</c>. The owner keeps the value; a press flips the bit (<see cref="Flip"/>).
using BrokenNes.Fruity;

namespace BrokenNes2.Ui;

internal sealed class BitToggle
{
    public const int Cell = 18;
    public readonly int BaseId;

    public BitToggle(int baseId) { BaseId = baseId; }

    /// <summary>Width a row of <paramref name="bits"/> toggles takes.</summary>
    public static int WidthOf(int bits) => bits * Cell;

    public void Draw(UiCanvas c, nint dc, int x, int y, int h, int value, int bits, bool enabled = true)
    {
        for (int i = 0; i < bits; i++)
        {
            int bit = bits - 1 - i;
            var r = UiCanvas.R(x + i * Cell, y, Cell - 2, h);
            bool on = ((value >> bit) & 1) != 0, hot = enabled && c.Hot == BaseId + bit;
            c.Box(dc, r, on ? (enabled ? UiCanvas.Accent : UiCanvas.Faint) : (hot ? UiCanvas.FieldHot : UiCanvas.Field), on ? (enabled ? UiCanvas.Accent : UiCanvas.Faint) : UiCanvas.Line, 4);
            c.Txt(dc, bit.ToString(), r, Win32.DT_CENTER | UiCanvas.Mid, on ? UiCanvas.White : UiCanvas.Faint, c.FontSmall);
            c.AddHit(BaseId + bit, r);
        }
    }

    public bool Owns(int id, int bits) => id >= BaseId && id < BaseId + bits;
    public int BitOf(int id) => id - BaseId;

    /// <summary>The value with <paramref name="bit"/> flipped.</summary>
    public static int Flip(int value, int bit) => UiMutate.Has(UiMutate.BitToggleDead) ? value : value ^ (1 << bit);
}
