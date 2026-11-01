namespace NesEmulator.Snes;

/// <summary>
/// WideMess milestone 6, better sound (docs/projects/widemess-spec.md section 6.1): the voices' samples interpolated with a 4-point cubic (Catmull-Rom) instead of the S-DSP's Gaussian filter.
/// The same four decoded samples the Gaussian reads, the same position between the second and third; only the weights differ. At a position of exactly 0 (a pitch of 1.0) the cubic gives the
/// sample itself, where the Gaussian blends three. Only what is mixed changes: the S-DSP's own output (OUTX, which a sound driver can read, and the next voice's pitch modulation) stays the
/// Gaussian's, so the game's sound program sees what a SNES gives it. Off (the default, and always on a SNES), <see cref="RunSample"/> is exactly as before: bit-identical.
/// </summary>
public sealed partial class DSP_SFC
{
    /// <summary>The console's better sound for a SNES game (WideMess console only): cubic interpolation of voices 0-7. Not part of a save state (a console setting).</summary>
    public bool CubicInterpolation;

    /// <summary>[offset * 4 + k]: the Catmull-Rom weights of samples p .. p + 3 at offset / 256 between p + 1 and p + 2, in the Gaussian's scale (2,048 = 1.0); each row sums to 2,048.</summary>
    private static readonly short[] Cubic = BuildCubic();

    private static short[] BuildCubic()
    {
        var c = new short[256 * 4];
        for (int o = 0; o < 256; o++)
        {
            double t = o / 256.0, t2 = t * t, t3 = t2 * t;
            int c0 = (int)System.Math.Round(2048 * (-t3 + 2 * t2 - t) / 2);
            int c2 = (int)System.Math.Round(2048 * (-3 * t3 + 4 * t2 + t) / 2);
            int c3 = (int)System.Math.Round(2048 * (t3 - t2) / 2);
            int c1 = 2048 - c0 - c2 - c3;   // the rounding goes to the nearest sample's weight, so a constant stays constant
            c[o * 4] = (short)c0; c[o * 4 + 1] = (short)c1; c[o * 4 + 2] = (short)c2; c[o * 4 + 3] = (short)c3;
        }
        return c;
    }

    private int InterpolateCubic(Voice v)
    {
        int p = (v.InterpPos >> 12) + v.BufPos;
        int o = ((v.InterpPos >> 4) & 0xFF) * 4;
        var b = v.Buf; var c = Cubic;
        int sum = c[o] * b[p] + c[o + 1] * b[p + 1] + c[o + 2] * b[p + 2] + c[o + 3] * b[p + 3];
        return Clamp16(sum >> 11) & ~1;
    }
}
