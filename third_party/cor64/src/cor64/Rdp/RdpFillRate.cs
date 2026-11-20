using System;

namespace cor64.Rdp
{
    // [quirk_fix2] RDP fill-rate timing (COR64_RDP_FILLRATE=1, default OFF: it changes golden/certified frames).
    // A rough per-primitive cycle cost (our own model from the documented RDP pixel rates: fill and copy 4 px/clk,
    // 1-cycle 1 px/clk, 2-cycle 2 clk/px, plus a fixed setup cost per primitive). The batch's total delays the
    // SYNC_FULL DP interrupt, so scenes that are RDP-bound on hardware (MK64 3-4P, GoldenEye smoke, MM file menu,
    // SF64 Andross) slow down here too. RDP clock 62.5 MHz -> CPU ticks x1.5. COR64_RDP_FILLRATE_SCALE scales it (default 2).
    public static class RdpFillRate
    {
        public static readonly bool On = Environment.GetEnvironmentVariable("COR64_RDP_FILLRATE") == "1";
        private static readonly double s_Scale = double.TryParse(Environment.GetEnvironmentVariable("COR64_RDP_FILLRATE_SCALE"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0 ? v : 2.0; // 2.0 calibrated: MM file menu 60->30 fps (hardware 30), MK64 1P race and SF64 Corneria stay ~30
        public const int TriSetup = 40, RectSetup = 10;

        public static int CycleType; // from the last set_other_modes
        public static double Cycles;  // RDP cycles accumulated since the last SYNC_FULL
        public static long Prims, Pixels;

        private static double PerPixel => CycleType switch { 0 => 1.0, 1 => 2.0, _ => 0.25 };

        public static unsafe void Add(uint op, uint* w)
        {
            switch (op) {
                case 0x2F: CycleType = (int)(w[0] >> 20) & 3; break;
                case 0x24: case 0x25: case 0x36: {
                    int xl = (int)(w[0] >> 12) & 0xFFF, yl = (int)w[0] & 0xFFF, xh = (int)(w[1] >> 12) & 0xFFF, yh = (int)w[1] & 0xFFF;
                    double wpx = Math.Max(0, (xl - xh) >> 2) + 1, hpx = Math.Max(0, (yl - yh) >> 2) + 1;
                    double px = wpx * hpx;
                    Cycles += RectSetup + px * (op == 0x36 ? 0.25 : PerPixel); Prims++; Pixels += (long)px; break;
                }
                default:
                    if (op >= 0x08 && op <= 0x0F) {
                        int yl = (int)(w[0] << 18) >> 18, yh = (int)(w[1] << 18) >> 18;
                        int xl = (int)w[2] >> 16, xh = (int)w[4] >> 16, xm = (int)w[6] >> 16;
                        double h = Math.Max(0, yl - yh) / 4.0, wd = Math.Max(Math.Abs(xl - xh), Math.Abs(xm - xh));
                        double px = h * wd * 0.5;
                        Cycles += TriSetup + px * PerPixel; Prims++; Pixels += (long)px;
                    }
                    break;
            }
        }

        // CPU ticks the SYNC_FULL interrupt waits for; resets the batch.
        public static long TakeTicks() { long t = (long)(Cycles * 1.5 * s_Scale); Cycles = 0; return t; }
    }
}
