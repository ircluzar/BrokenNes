using System;

namespace NesEmulator.Sega;

/// <summary>NTSC or PAL. Every Sega timing table takes a region from the start; NTSC is what ships first, PAL is certified headless (Overdrive 2 and about 20
/// Master System titles need it) and reaches the user interface later.</summary>
public enum SegaRegion { Ntsc, Pal }

/// <summary>The Genesis hardware revision an emulated console mimics. It carries the behaviours that differ between boards: YM2612 versus YM3438 sound
/// (the ladder effect, the status/busy-flag quirks), whether TAS writes back, the VSRAM size, TMSS, and so on. Selectable per game.</summary>
public enum GenesisModel { Model1, Model2, Model3 }

/// <summary>
/// The clock arithmetic of the Sega machines, in one place and region-parameterised, so no board ever hard-codes an NTSC number.
/// Master Clock figures and dividers from the Sega Genesis technical overview and MacDonald's gen-hw notes; Master System from the SMS Power wiki and
/// MacDonald's VDP document (228 CPU cycles per line, 342 pixels per line). Frame rates are derived, never typed in.
/// </summary>
public static class SegaTiming
{
    /// <summary>Genesis master clock: 53.693175 MHz NTSC, 53.203424 MHz PAL (colour-burst x 15 and x 12).</summary>
    public static double GenesisMasterHz(SegaRegion r) => r == SegaRegion.Ntsc ? 53_693_175.0 : 53_203_424.0;

    /// <summary>Master clocks per divider: the 68000 runs at master/7, the Z80 and the PSG at master/15, and the YM2612 takes master/7 then divides by 6
    /// and by its 24 operator slots, so one FM sample is 144 chip clocks = 1008 master clocks.</summary>
    public const int GenesisM68kDivider = 7, GenesisZ80Divider = 15, GenesisPsgDivider = 15, GenesisYm2612Divider = 7, GenesisYm2612MasterClocksPerSample = 1008;

    /// <summary>Every Genesis scanline is 3420 master clocks (488 68000 cycles, 228 Z80 cycles) in both H32 and H40.</summary>
    public const int GenesisMasterClocksPerLine = 3420;

    public static int GenesisLinesPerFrame(SegaRegion r) => r == SegaRegion.Ntsc ? 262 : 313;

    public static double GenesisFramesPerSecond(SegaRegion r) => GenesisMasterHz(r) / (GenesisMasterClocksPerLine * (double)GenesisLinesPerFrame(r));

    /// <summary>Master System / Game Gear Z80 clock: 3.579545 MHz NTSC, 3.546895 MHz PAL. 228 CPU cycles per line (342 pixels at two master clocks, three per
    /// CPU cycle); 262 lines NTSC, 313 PAL.</summary>
    public static double MasterSystemCpuHz(SegaRegion r) => r == SegaRegion.Ntsc ? 3_579_545.0 : 3_546_895.0;

    public const int MasterSystemCpuCyclesPerLine = 228;

    public static int MasterSystemLinesPerFrame(SegaRegion r) => r == SegaRegion.Ntsc ? 262 : 313;

    public static double MasterSystemFramesPerSecond(SegaRegion r) => MasterSystemCpuHz(r) / (MasterSystemCpuCyclesPerLine * (double)MasterSystemLinesPerFrame(r));

    /// <summary>The picture a console draws at its widest (what a session reports as its frame size).</summary>
    public static (int Width, int Height) FrameSize(Systems.ConsoleKind k, SegaRegion r) => k switch
    {
        Systems.ConsoleKind.MasterSystem => (256, 192),
        Systems.ConsoleKind.GameGear => (160, 144),
        Systems.ConsoleKind.Genesis => (320, r == SegaRegion.Ntsc ? 224 : 240),
        _ => throw new ArgumentOutOfRangeException(nameof(k)),
    };
}
