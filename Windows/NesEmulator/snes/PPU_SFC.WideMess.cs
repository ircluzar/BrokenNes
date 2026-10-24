namespace NesEmulator.Snes;

/// <summary>What the WideMess picture chip (<see cref="PPU_WMS"/>, a subclass) needs of the SNES chip's private state: read-only views, so the SNES chip itself is unchanged.
/// Its wide compositor (WideMess.Picture.cs) draws the SNES modes 400 wide from these registers; nothing here writes the SNES chip's state except the sprite overflow flags, which the
/// wide compositor sets exactly where the SNES chip's own sprite pass would.</summary>
public partial class PPU_SFC
{
    /// <summary>INIDISP's brightness (0-15).</summary>
    protected int MasterBrightness => inidisp & 0x0F;

    /// <summary>True when the video memories take writes now: in vblank, in forced blank, or always when <see cref="DropVramWritesDuringDisplay"/> is off (the SNES rule, which WideMess's ports follow too).</summary>
    protected bool VideoPortsOpen => VramWritable;

    /// <summary>The shared BGR555 -> 0xAARRGGBB table for a brightness (1-15).</summary>
    protected static uint[] ColourTable(int brightness) => brightnessLut[brightness];

    // ---- the registers, as written (milestone 2: the SNES modes drawn wide)
    protected byte RegBgmode => bgmode;
    protected byte RegMosaic => mosaic;
    protected byte RegBgsc(int bg) => bgsc[bg];
    protected byte RegBg12nba => bg12nba;
    protected byte RegBg34nba => bg34nba;
    protected ushort RegHofs(int bg) => hofs[bg];
    protected ushort RegVofs(int bg) => vofs[bg];
    protected byte RegM7sel => m7sel;
    protected short RegM7a => m7a;
    protected short RegM7b => m7b;
    protected short RegM7c => m7c;
    protected short RegM7d => m7d;
    protected short RegM7x => m7x;
    protected short RegM7y => m7y;
    protected short RegM7hofs => m7hofs;
    protected short RegM7vofs => m7vofs;
    protected byte RegTm => tm;
    protected byte RegTs => ts;
    protected byte RegTmw => tmw;
    protected byte RegTsw => tsw;
    protected byte RegW12sel => w12sel;
    protected byte RegW34sel => w34sel;
    protected byte RegWobjsel => wobjsel;
    protected byte RegWh0 => wh0;
    protected byte RegWh1 => wh1;
    protected byte RegWh2 => wh2;
    protected byte RegWh3 => wh3;
    protected byte RegWbglog => wbglog;
    protected byte RegWobjlog => wobjlog;
    protected byte RegCgwsel => cgwsel;
    protected byte RegCgadsub => cgadsub;
    protected ushort RegColdata => coldata;
    protected byte RegSetini => setini;
    protected byte RegObsel => obsel;
    protected ushort RegOamadd => oamadd;

    /// <summary>STAT77's sprite flags, raised (never cleared) by a sprite pass the subclass draws for the SNES sprite table.</summary>
    protected void RaiseSpriteOverflow(bool range, bool time) { if (range) rangeOver = true; if (time) timeOver = true; }

    /// <summary>Milestone 6: a hi-res line the subclass drew (its 512 half-pixels written to <see cref="HiResBuffer"/>), so <see cref="GetHiResFrame"/> keeps it sharp.</summary>
    protected void MarkHiResRow(int row) { rowHiRes[row] = true; FrameHasHiRes = true; }

    /// <summary>Test (milestone 6): STAT77's sprite flags as they stand (range: more than 32 sprites on a line; time: more than 34 slivers), read without touching the chip's open bus.</summary>
    public (bool range, bool time) SpriteOverflowFlags => (rangeOver, timeOver);

    // ---- the SNES chip's constant tables, shared rather than copied
    /// <summary>A mode's front-to-back order as (layer, priority): layers 0-3 = BG1-4, 4 = OBJ; priority 2 on a BG marks "either priority" (Mode 7's BG1).</summary>
    protected static (byte layer, byte prio)[] SnesModeOrder(int mode, bool bg3Top) => mode switch
    {
        0 => Order0,
        1 => bg3Top ? Order1Bg3Top : Order1,
        6 => Order6,
        7 => Order7,
        _ => Order2To5,
    };
    protected static int SnesModeBpp(int mode, int bg) => ModeBpp[mode][bg];
    protected static (int w, int h) SnesObjSize(int sizeSelect, bool large) => large ? ObjLarge[sizeSelect] : ObjSmall[sizeSelect];
    /// <summary>Bit-plane byte -> 8 byte lanes (lane i = pixel i, left to right; the flipped table mirrors them).</summary>
    protected static ulong[] PlaneExpandTable(bool flipped) => flipped ? PlaneExpandFlipped : PlaneExpand;
    protected static ushort SnesDirectColour(int c, int pal) => DirectColor(c, pal);
}
