using System;

namespace NesEmulator.Snes;

/// <summary>
/// Homemade DSP-1: a from-scratch reimplementation of the DSP-1 math coprocessor's command set in
/// C#, so DSP-1 games (Super Mario Kart, Pilotwings...) run without Nintendo's firmware. When the
/// real firmware is available, <see cref="NECDSP_SFC"/> runs it instead and is exact.
///
/// Written from the published command descriptions (inputs, outputs and what each computes) and
/// the camera geometry they describe, then tuned by black-box comparison against the real chip:
/// Workshop --dsp1-lab replays transactions recorded from the firmware-driven core and reports how
/// many outputs match. No Nintendo code or data tables are included: sines, square roots and
/// divisions are computed with ordinary math and rounded the way the measurements say.
///
/// Protocol as seen from the SNES (same ports as the real chip): a command byte in 8-bit mode,
/// then 16-bit parameters (low byte first), then 16-bit results. Command bytes with bit 7 set are
/// ignored. Raster streams four results per screen line until the SNES writes $8000 into all four
/// slots of a line.
/// </summary>
public sealed class DSP1_SFC : ISnesCoprocessor
{
    public string Name => "DSP-1 (homemade)";
    private readonly NECDSP_SFC.Mapping map;

    public DSP1_SFC(NECDSP_SFC.Mapping mapping) { map = mapping; Reset(); }

    // =====================================================================================
    //  Port protocol
    // =====================================================================================

    private enum Phase { Command, Input, Output }
    private Phase phase;
    private byte command;
    private readonly short[] input = new short[8];
    private int inputCount, inputNeeded;
    private readonly short[] output = new short[8];
    private int outputCount, outputPos;
    private bool lowHalf = true;        // 16-bit transfers: low byte first
    private ushort partial;
    private bool rasterMode, rasterStop;
    private short rasterLine;

    public void Reset()
    {
        phase = Phase.Command; lowHalf = true; rasterMode = false;
        attA = attB = attC = Identity();
        Array.Clear(input); Array.Clear(output);
    }

    public bool Owns(uint bank, uint offset) => map switch
    {
        NECDSP_SFC.Mapping.HiRom => (bank & 0x60) == 0 && offset >= 0x6000 && offset < 0x8000,
        NECDSP_SFC.Mapping.LoRom1MB => (bank & 0x70) == 0x30 && offset >= 0x8000,
        _ => (bank & 0x70) == 0x60 && offset < 0x8000,
    };

    private bool IsStatus(uint offset) => map == NECDSP_SFC.Mapping.HiRom ? (offset & 0x1000) != 0 : (offset & 0x4000) != 0;

    public byte Read(uint bank, uint offset, long masterClock)
    {
        // Status: RQM (bit 7) always set - this implementation answers instantly. DRC (bit 2 of
        // the high byte) tells 8-bit command mode from 16-bit data mode.
        if (IsStatus(offset)) return (byte)(0x80 | (phase == Phase.Command ? 0x04 : 0x00));
        if (phase != Phase.Output) return phase == Phase.Command ? (byte)0x80 : (byte)0;
        ushort word = (ushort)output[outputPos];
        if (lowHalf) { lowHalf = false; return (byte)word; }
        lowHalf = true;
        NextOutputSlot(written: false, value: 0);
        return (byte)(word >> 8);
    }

    public void Write(uint bank, uint offset, byte value, long masterClock)
    {
        if (IsStatus(offset)) return;
        if (phase == Phase.Command)
        {
            if ((value & 0x80) != 0) return;    // bit 7 set: not a command
            StartCommand(value);
            return;
        }
        if (lowHalf) { partial = value; lowHalf = false; return; }
        lowHalf = true;
        ushort word = (ushort)(partial | value << 8);
        if (phase == Phase.Input)
        {
            input[inputCount++] = (short)word;
            if (inputCount == inputNeeded) Execute();
        }
        else NextOutputSlot(written: true, value: word);    // a write consumes an output slot too
    }

    private void NextOutputSlot(bool written, ushort value)
    {
        outputPos++;
        if (rasterMode)
        {
            // The SNES ends a raster run by writing $8000 over a line's slots.
            if (written && (value & 0x8000) != 0) rasterStop = true;
            if (outputPos < 4) return;
            if (rasterStop) { rasterMode = false; phase = Phase.Command; return; }
            rasterLine++;
            RasterLine(rasterLine);
            outputPos = 0;
            return;
        }
        if (outputPos >= outputCount) phase = Phase.Command;
    }

    private void StartCommand(byte cmd)
    {
        command = cmd;
        inputCount = 0;
        inputNeeded = InputCount(cmd);
        lowHalf = true;
        if (inputNeeded == 0) Execute(); else phase = Phase.Input;
    }

    private void Emit(params short[] values)
    {
        values.CopyTo(output, 0);
        outputCount = values.Length;
        outputPos = 0;
        phase = outputCount > 0 ? Phase.Output : Phase.Command;
    }

    public string Describe() => $"{Name} phase={phase} cmd=${command:X2} in={inputCount}/{inputNeeded} out={outputPos}/{outputCount}{(rasterMode ? $" raster line {rasterLine}" : "")}";

    // =====================================================================================
    //  Commands
    // =====================================================================================

    /// <summary>Parameter words each command reads (commands are decoded on bits 0-5).</summary>
    private static int InputCount(byte cmd) => (cmd & 0x3F) switch
    {
        0x00 or 0x20 => 2,            // Multiply
        0x10 or 0x30 => 2,            // Inverse
        0x04 or 0x24 => 2,            // Triangle
        0x08 => 3,                    // Radius
        0x18 or 0x38 => 4,            // Range
        0x28 => 3,                    // Distance
        0x0C or 0x2C => 3,            // Rotate
        0x1C or 0x3C => 6,            // Polar
        0x02 or 0x12 or 0x22 or 0x32 => 7,   // Parameter
        0x0A or 0x1A or 0x2A or 0x3A => 1,   // Raster
        0x06 or 0x16 or 0x26 or 0x36 => 3,   // Project
        0x0E or 0x1E or 0x2E or 0x3E => 2,   // Target
        0x01 or 0x05 or 0x31 or 0x35 => 4,   // Attitude A
        0x11 or 0x15 => 4,            // Attitude B
        0x21 or 0x25 => 4,            // Attitude C
        0x0D or 0x09 or 0x39 or 0x3D => 3,   // Subjective A
        0x1D or 0x19 => 3,            // Subjective B
        0x2D or 0x29 => 3,            // Subjective C
        0x03 or 0x33 => 3,            // Objective A
        0x13 => 3,                    // Objective B
        0x23 => 3,                    // Objective C
        0x0B or 0x3B => 3,            // Scalar A
        0x1B => 3,                    // Scalar B
        0x2B => 3,                    // Scalar C
        0x0F => 1,                    // Memory test
        0x2F => 1,                    // Memory size
        0x1F => 1,                    // ROM dump (not provided: no Nintendo data here)
        _ => 0,
    };

    private void Execute()
    {
        short a = input[0], b = input[1], c = input[2];
        switch (command & 0x3F)
        {
            case 0x00: Emit(Multiply(a, b)); break;
            case 0x20: Emit((short)(Multiply(a, b) + 1)); break;
            case 0x10: case 0x30: { var (m, e) = Inverse(a, b); Emit(m, e); break; }
            case 0x04: case 0x24: Emit(MulTrunc(b, Sin(a)), MulTrunc(b, Cos(a))); break;
            case 0x08: { int r = a * a + b * b + c * c; Emit((short)r, (short)(r >> 16)); break; }
            case 0x18: Emit(Sat((a * a + b * b + c * c - input[3] * input[3]) >> 15)); break;
            case 0x38: Emit(Sat(((a * a + b * b + c * c - input[3] * input[3]) >> 15) + 1)); break;
            // The chip's square root runs a hair low (exact squares come out one under); scaling by
            // 1 - 2^-14 matches 98% of 1,900 recorded results, against 89% for the exact root.
            case 0x28: Emit(Sat((int)(Math.Sqrt((double)a * a + (double)b * b + (double)c * c) * (1 - 1.0 / 16384)))); break;
            case 0x0C: case 0x2C: Emit(Rot(b, c, a, out short y2), y2); break;
            case 0x1C: case 0x3C: Emit(Polar()); break;
            case 0x02: case 0x12: case 0x22: case 0x32: Emit(Parameter()); break;
            case 0x0A: case 0x1A: case 0x2A: case 0x3A:
                rasterMode = true; rasterStop = false; rasterLine = a;
                RasterLine(a);
                outputCount = 4; outputPos = 0; phase = Phase.Output;
                break;
            case 0x06: case 0x16: case 0x26: case 0x36: Emit(Project(a, b, c)); break;
            case 0x0E: case 0x1E: case 0x2E: case 0x3E: Emit(Target(a, b)); break;
            case 0x01: case 0x05: case 0x31: case 0x35: attA = Attitude(); Emit(); break;
            case 0x11: case 0x15: attB = Attitude(); Emit(); break;
            case 0x21: case 0x25: attC = Attitude(); Emit(); break;
            case 0x0D: case 0x09: case 0x39: case 0x3D: Emit(Subjective(attA, a, b, c)); break;
            case 0x1D: case 0x19: Emit(Subjective(attB, a, b, c)); break;
            case 0x2D: case 0x29: Emit(Subjective(attC, a, b, c)); break;
            case 0x03: case 0x33: Emit(Objective(attA, a, b, c)); break;
            case 0x13: Emit(Objective(attB, a, b, c)); break;
            case 0x23: Emit(Objective(attC, a, b, c)); break;
            case 0x0B: case 0x3B: Emit(Scalar(attA, a, b, c)); break;
            case 0x1B: Emit(Scalar(attB, a, b, c)); break;
            case 0x2B: Emit(Scalar(attC, a, b, c)); break;
            case 0x0F: Emit(0); break;                 // memory test: passes
            case 0x2F: Emit(0x0100); break;            // memory size
            case 0x1F: Emit(new short[0]); break;      // ROM dump: nothing to dump
            default: Emit(); break;
        }
    }

    /// <summary>For tests: run one non-raster command with the given parameters, return its results.</summary>
    public short[] Run(byte cmd, params short[] parameters)
    {
        command = cmd; inputNeeded = InputCount(cmd);
        Array.Copy(parameters, input, Math.Min(parameters.Length, input.Length));
        inputCount = inputNeeded;
        Execute();
        if (rasterMode) { rasterMode = false; phase = Phase.Command; return (short[])output[..4].Clone(); }
        var r = output[..outputCount];
        phase = Phase.Command;
        return r;
    }

    /// <summary>For tests: the four raster results for a line (after a Parameter command).</summary>
    public short[] RunRaster(short line) { RasterLine(line); return output[..4]; }

    // =====================================================================================
    //  Arithmetic helpers (Q15 fixed point; angles: $10000 = one turn)
    // =====================================================================================

    private static short Sat(int v) => (short)Math.Clamp(v, short.MinValue, short.MaxValue);
    private static short Sat(double v) => Sat((int)Math.Floor(v));
    private static short Multiply(short a, short b) => (short)((a * b) >> 15);
    private static short MulTrunc(short a, short b) => (short)((a * b) >> 15);

    private static double Rad(short angle) => angle * (Math.PI / 32768.0);
    private static short Sin(short angle) => Sat((int)Math.Round(Math.Sin(Rad(angle)) * 32768.0));
    private static short Cos(short angle) => Sat((int)Math.Round(Math.Cos(Rad(angle)) * 32768.0));

    private static short Rot(short x, short y, short angle, out short y2)
    {
        // Each Q15 product is rounded down on its own before the sum (measured).
        int s = Sin(angle), c = Cos(angle);
        y2 = Sat(((y * c) >> 15) - ((x * s) >> 15));
        return Sat(((x * c) >> 15) + ((y * s) >> 15));
    }

    /// <summary>1 / (m * 2^e) = m' * 2^e' with m' normalized to Q15.</summary>
    private static (short, short) Inverse(short m, short e)
    {
        if (m == 0) return (0x7FFF, 0x002F);
        double v = 1.0 / (m / 32768.0 * Math.Pow(2, e));
        int exp = 0;
        while (Math.Abs(v) >= 1.0) { v /= 2; exp++; }
        while (Math.Abs(v) < 0.5) { v *= 2; exp--; }
        return (Sat((int)Math.Round(v * 32768.0)), (short)exp);
    }

    // =====================================================================================
    //  Camera: Parameter / Raster / Project / Target
    // =====================================================================================
    // Global coordinates: X and Y span the ground, Z is height. The camera looks at the focus
    // point F from distance Lfe; Aas turns the view around the vertical axis, Azs tilts it away
    // from straight down. The screen sits Les in front of the eye.

    private double fx, fy, fz, lfe, les, aas, azs;
    private double ex, ey, ez;                         // eye
    private double dx, dy, dz;                         // view direction (unit)
    private double rx, ry;                             // screen right (horizontal, unit)
    private double ux, uy, uz;                         // screen up (unit)
    private double vva;
    private short input6;                              // Azs after the horizon limit
    private short azsAsked;                            // Azs as given
    private const short MaxAzs = 14533;

    /// <summary>Sign of the azimuth (which way positive Aas turns); settled by measurement.</summary>
    internal static int AzimuthSign = 1;

    private short[] Parameter()
    {
        fx = input[0]; fy = input[1]; fz = input[2];
        lfe = input[3]; les = input[4];
        // The chip will not tilt the view closer to the horizon than MaxAzs (measured: every probe
        // above 14537 was held, none at or below 14529). It renders the held view and reports in
        // Vof how many lines the horizon moved.
        short az = input[6];
        azsAsked = az;
        int vof = 0;
        if (az > MaxAzs)
        {
            vof = (int)Math.Floor(-les / Math.Tan(Rad(az)));
            az = MaxAzs;
        }
        aas = Rad(input[5]); azs = Rad(az); input6 = az;
        double sa = Math.Sin(aas) * AzimuthSign, ca = Math.Cos(aas), sz = Math.Sin(azs), cz = Math.Cos(azs);
        // Measured conventions: at Aas = 0 the camera faces -Y ("up" the Mode 7 map) with +X on
        // the right of the screen.
        dx = sz * sa; dy = -sz * ca; dz = -cz;
        rx = ca; ry = sa;
        // Screen up: perpendicular to both, pointing away from the ground.
        ux = sa * cz; uy = -ca * cz; uz = sz;
        // The eye sits Lfe back from F along the view asked for (before any horizon hold).
        double za = Rad(azsAsked), sza = Math.Sin(za), cza = Math.Cos(za);
        ex = fx - lfe * sza * sa; ey = fy + lfe * sza * ca; ez = fz + lfe * cza;
        // Horizon: where the view tilts up to horizontal.
        // Built from the same whole-number Les*cos Azs the raster uses (94% exact vs the chip).
        vva = Math.Floor(-Math.Floor(les * (double)Cos(az) / 32768.0) * 32768.0 / Sin(az));
        if (vof != 0) vof -= (int)Math.Floor(vva);
        // Screen centre on the ground: from the eye, forward by the whole-number height times
        // tan Azs, each step rounded down (the best of the rounding schemes tried against the chip;
        // results land within a few units).
        double qsa = Sin(input[5]) / 32768.0, qca = Cos(input[5]) / 32768.0;
        double qsa2 = qsa * AzimuthSign;
        int height = (int)fz + (int)Math.Floor(lfe * (double)Cos(azsAsked) / 32768.0);
        double dist = height * (double)Sin(az) / Cos(az);
        double qsz = Sin(azsAsked) / 32768.0;
        double gx = fx - lfe * qsz * qsa2, gy = fy + lfe * qsz * qca;
        double cx = Math.Floor(gx + Math.Floor(dist * qsa2)), cy = Math.Floor(gy - Math.Floor(dist * qca));
        return new[] { Sat(vof), Sat(vva), Sat(cx), Sat(cy) };
    }

    private void RasterLine(short line)
    {
        // The ray through screen line `line` (positive = down) meets the ground at distance
        // ez / (line*sin Azs + Les*cos Azs) per unit of screen distance. The chip forms that
        // denominator as two whole-number Q15 products, each rounded down: fitted against the real
        // chip over 1,700 random cameras this matches the line where the scale blows up (the
        // horizon) exactly, where the exact real-valued ray was off by up to a line and a half.
        // Lines above the horizon give a negative scale, as on the chip.
        int den = (int)Math.Floor(line * (double)Sin((short)input6) / 32768.0) + (int)Math.Floor(les * (double)Cos((short)input6) / 32768.0);
        double sa = Math.Sin(aas) * AzimuthSign, ca = Math.Cos(aas), cz = Math.Cos(azs);
        // The eye height is likewise a whole number: Fz + floor(Lfe * cos Azs), with the Azs asked
        // for - the eye is placed before the horizon limit holds the view's tilt.
        int height = (int)fz + (int)Math.Floor(lfe * (double)Cos(azsAsked) / 32768.0);
        double s = den == 0 ? 1e9 : height * 256.0 / den;  // Mode 7 matrix is 8.8 fixed point
        output[0] = Sat(s * ca);
        output[1] = Sat(-s * sa / cz);
        output[2] = Sat(s * sa);
        output[3] = Sat(s * ca / cz);
    }

    private short[] Project(short x, short y, short z)
    {
        double wx = x - ex, wy = y - ey, wz = z - ez;
        // Points behind the eye still project (mirrored, negative scale), as on the chip, and the
        // depth is a whole number (rounded down): both measured against the real chip.
        double depth = Math.Floor(wx * dx + wy * dy + wz * dz);
        if (depth == 0) depth = 1e-9;
        double h = les * (wx * rx + wy * ry) / depth;
        double v = -les * (wx * ux + wy * uy + wz * uz) / depth;
        double m = les / depth * 256.0;
        return new[] { Sat(h), Sat(v), Sat(m) };
    }

    private short[] Target(short h, short v)
    {
        double qx = les * dx + h * rx - v * ux, qy = les * dy + h * ry - v * uy, qz = les * dz - v * uz;
        if (qz >= 0) return new short[] { 0, 0 };
        double t = -ez / qz;
        return new[] { Sat(ex + t * qx), Sat(ey + t * qy) };
    }

    // =====================================================================================
    //  Attitude matrices (object <-> global)
    // =====================================================================================

    private double[] attA = Identity(), attB = Identity(), attC = Identity();
    private static double[] Identity() => new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };

    /// <summary>Attitude: scale S and rotations about Z, X, Y (inputs S, Az, Ax, Ay).</summary>
    private double[] Attitude()
    {
        double s = input[0] / 32768.0;
        double z = Rad(input[1]), x = Rad(input[2]), y = Rad(input[3]);
        double sz = Math.Sin(z), cz = Math.Cos(z), sx = Math.Sin(x), cx = Math.Cos(x), sy = Math.Sin(y), cy = Math.Cos(y);
        // R = Rz * Rx * Ry, scaled.
        return new[]
        {
            s * (cz * cy - sz * sx * sy), s * (-sz * cx), s * (cz * sy + sz * sx * cy),
            s * (sz * cy + cz * sx * sy), s * (cz * cx),  s * (sz * sy - cz * sx * cy),
            s * (-cx * sy),               s * sx,         s * (cx * cy),
        };
    }

    private static short[] Objective(double[] m, short x, short y, short z) => new[]
    {
        Sat(m[0] * x + m[3] * y + m[6] * z), Sat(m[1] * x + m[4] * y + m[7] * z), Sat(m[2] * x + m[5] * y + m[8] * z),
    };

    private static short[] Subjective(double[] m, short f, short l, short u) => new[]
    {
        Sat(m[0] * f + m[1] * l + m[2] * u), Sat(m[3] * f + m[4] * l + m[5] * u), Sat(m[6] * f + m[7] * l + m[8] * u),
    };

    private static short[] Scalar(double[] m, short x, short y, short z) => new[] { Sat(m[0] * x + m[3] * y + m[6] * z) };

    private short[] Polar()
    {
        double z = Rad(input[0]), x = Rad(input[1]), y = Rad(input[2]);
        double px = input[3], py = input[4], pz = input[5];
        // rotate about Z, then X, then Y
        double x1 = px * Math.Cos(z) + py * Math.Sin(z), y1 = -px * Math.Sin(z) + py * Math.Cos(z), z1 = pz;
        double y2 = y1 * Math.Cos(x) + z1 * Math.Sin(x), z2 = -y1 * Math.Sin(x) + z1 * Math.Cos(x), x2 = x1;
        double x3 = x2 * Math.Cos(y) - z2 * Math.Sin(y), z3 = x2 * Math.Sin(y) + z2 * Math.Cos(y);
        return new[] { Sat(x3), Sat(y2), Sat(z3) };
    }
}
