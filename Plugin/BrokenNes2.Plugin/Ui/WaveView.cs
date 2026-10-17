// A waveform view for a sample the model supplies as 16-bit samples: a min/max envelope per pixel column (a step plot when there are fewer samples
// than columns), the zero line, and a loop-marker overlay (the looped part shaded, a marker at each end). Read-only: the loop points are fields of the
// instrument, edited in the fields table.
using BrokenNes.Fruity;

namespace BrokenNes2.Ui;

internal sealed class WaveView
{
    public readonly int Id;
    public RECT Rect;

    /// <summary>Pixels drawn in the wave colour on the last paint (what the self-test cross-checks against a screenshot).</summary>
    public int DrawnColumns { get; private set; }

    public WaveView(int id) { Id = id; }

    public void Draw(UiCanvas c, nint dc, RECT r, short[] samples, int loopStart, int loopEnd, string? empty = null)
    {
        Rect = r;
        DrawnColumns = 0;
        c.Box(dc, r, UiCanvas.Black, UiCanvas.Line, 5);
        var plot = UiCanvas.R(r.Left + 4, r.Top + 4, UiCanvas.W(r) - 8, UiCanvas.H(r) - 8);
        int w = UiCanvas.W(plot), h = UiCanvas.H(plot), mid = plot.Top + h / 2;
        c.AddHit(Id, r);
        if (UiMutate.Has(UiMutate.WaveBlank)) return;
        if (samples.Length == 0)
        {
            c.Txt(dc, empty ?? "no waveform to show", r, Win32.DT_CENTER | UiCanvas.Mid, UiCanvas.Faint, c.FontSmall);
            return;
        }
        int n = samples.Length;
        // the loop: shaded between the markers
        bool loops = loopStart >= 0 && loopEnd > loopStart && loopStart < n;
        int lx0 = 0, lx1 = 0;
        if (loops)
        {
            lx0 = plot.Left + (int)((long)loopStart * w / n);
            lx1 = plot.Left + (int)((long)Math.Min(loopEnd, n) * w / n);
            c.Fill(dc, UiCanvas.R(lx0, plot.Top, Math.Max(1, lx1 - lx0), h), UiCanvas.WaveLoop);
        }
        c.Ln(dc, plot.Left, mid, plot.Right, mid, UiCanvas.Line);
        // the envelope
        int prevY = mid;
        double scale = (h / 2 - 1) / 32768.0;
        for (int x = 0; x < w; x++)
        {
            int a = (int)((long)x * n / w), b = Math.Max(a + 1, (int)((long)(x + 1) * n / w));
            int lo = short.MaxValue, hi = short.MinValue;
            for (int i = a; i < b && i < n; i++) { if (samples[i] < lo) lo = samples[i]; if (samples[i] > hi) hi = samples[i]; }
            int yTop = mid - (int)(hi * scale), yBot = mid - (int)(lo * scale);
            if (x > 0) { yTop = Math.Min(yTop, prevY); yBot = Math.Max(yBot, prevY); }      // join the steps
            prevY = mid - (int)(samples[Math.Min(n - 1, b - 1)] * scale);
            c.Ln(dc, plot.Left + x, yTop, plot.Left + x, yBot + 1, UiCanvas.Wave);
            DrawnColumns++;
        }
        if (loops)
        {
            c.Ln(dc, lx0, plot.Top, lx0, plot.Bottom, UiCanvas.Accent);
            c.Ln(dc, Math.Min(lx1, plot.Right - 1), plot.Top, Math.Min(lx1, plot.Right - 1), plot.Bottom, UiCanvas.Accent);
        }
    }
}
