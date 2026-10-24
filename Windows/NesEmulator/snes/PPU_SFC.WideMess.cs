namespace NesEmulator.Snes;

/// <summary>What the WideMess picture chip (<see cref="PPU_WMS"/>, a subclass) needs of the SNES chip's private state: read-only views, so the SNES chip itself is unchanged.</summary>
public partial class PPU_SFC
{
    /// <summary>INIDISP's brightness (0-15).</summary>
    protected int MasterBrightness => inidisp & 0x0F;

    /// <summary>True when the video memories take writes now: in vblank, in forced blank, or always when <see cref="DropVramWritesDuringDisplay"/> is off (the SNES rule, which WideMess's ports follow too).</summary>
    protected bool VideoPortsOpen => VramWritable;

    /// <summary>The shared BGR555 -> 0xAARRGGBB table for a brightness (1-15).</summary>
    protected static uint[] ColourTable(int brightness) => brightnessLut[brightness];
}
