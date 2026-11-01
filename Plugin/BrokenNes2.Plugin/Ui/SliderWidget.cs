// A horizontal slider over an integer range: a rounded track filled up to a knob, like the Emulator tab's parameter sliders.
// The owner keeps the value; the slider draws it, registers its area under its id, and turns a mouse x into a value (a click or a drag).
using BrokenNes.Fruity;

namespace BrokenNes2.Ui;

internal sealed class Slider
{
    public readonly int Id;
    private RECT track;
    private int min, max;

    public Slider(int id) { Id = id; }

    /// <summary>Draws the track in <paramref name="area"/> (the hit area is the whole area, a little wider than the track so the knob at either end can be grabbed).</summary>
    public void Draw(UiCanvas c, nint dc, RECT area, int value, int min, int max, bool enabled = true, bool centred = false)
    {
        this.min = min; this.max = max;
        int mid = (area.Top + area.Bottom) / 2;
        track = UiCanvas.R(area.Left + 6, mid - 3, Math.Max(1, UiCanvas.W(area) - 12), 6);
        bool hot = enabled && c.IsHot(Id);
        double range = Math.Max(1, max - min), frac = Math.Clamp((value - min) / range, 0, 1), centre = Math.Clamp((0 - min) / range, 0, 1);
        int tw = UiCanvas.W(track), tx = track.Left;
        c.Box(dc, track, UiCanvas.Field, -1, 3);
        int kx = tx + (int)Math.Round(frac * tw);
        int from = centred ? tx + (int)Math.Round(centre * tw) : tx;
        if (kx != from) c.Box(dc, UiCanvas.R(Math.Min(from, kx), mid - 3, Math.Abs(kx - from), 6), enabled ? (hot ? UiCanvas.Accent : UiCanvas.AccentDim) : UiCanvas.Faint, -1, 3);
        if (centred) c.Ln(dc, from, mid - 6, from, mid + 7, UiCanvas.Line);
        c.Dot(dc, kx, mid, hot ? 7 : 6, enabled ? (hot ? UiCanvas.Text : UiCanvas.Dim) : UiCanvas.Faint);
        c.AddHit(Id, area);
    }

    /// <summary>The value under mouse x (clamped to the range) for the track as last drawn.</summary>
    public int ValueAt(int x)
    {
        double f = Math.Clamp((x - track.Left) / (double)Math.Max(1, UiCanvas.W(track)), 0, 1);
        int v = min + (int)Math.Round(f * (max - min));
        return UiMutate.Has(UiMutate.SliderOffByOne) ? Math.Min(max, v + 1) : v;
    }
}
