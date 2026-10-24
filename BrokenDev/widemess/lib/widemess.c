/*---------------------------------------------------------------------------------
    widemess.c: the WideMess extensions to PVSnesLib (see widemess.h and docs/projects/widemess-devkit.md).

    Built into every WideMess game by wmsbuild.ps1 (816-tcc). C over the fixed-address registers of docs/projects/widemess-spec.md;
    the register stores that need an index (a layer's registers, DMA channels) are in wmslib.asm.
    -D WMS_NOUNLOCK builds the negative control the devkit's checks use: wmsInit() skips the unlock writes but reports success,
    so the game writes every WideMess register while the console is locked, and a WideMess must ignore all of it.

    816-tcc notes (both cost a crash or a failed link before they were understood):
      - a pointer made from a computed address ((vuint8 *)(base + n)) gets a stale bank: index registers in wmslib.asm instead;
      - (u32)pointer keeps only the 16-bit address (sign-extended): pass pointers as pointers (wms_dma takes u8 *);
      - a function that reads a u32 parameter must have a local variable (816-tcc reads it through __<function>_locals, which it
        defines only for functions with locals): those below copy the parameter into a local first.
---------------------------------------------------------------------------------*/
#include "widemess.h"

u8 wms_present;
u8 wms_features;

/* wmslib.asm */
extern void wms_poke(u16 addr, u8 value);
extern void wms_dma(u8 ch, u8 mode, u8 bbad, u8 *src, u16 size);
/* wmsfar.asm: the sprite table's shadow (2 KB in bank $70) and the 256 byte values in ROM (the fill DMAs' source; ROM because the
   WRAM port cannot be filled from work RAM) */
extern u8 wmsOamShadow[];
extern u8 wms_byte_values[];

u8 wms_oam_dirty;          /* not static: wmslib.asm's sprite routines set it */
static u8 wms_scroll_dirty;
static u16 wms_hofs[8];
static u16 wms_vofs[8];

u8 wmsInit(void)
{
#ifndef WMS_NOUNLOCK
    REG_WMUNLOCK = 0x57;   /* 'W' */
    REG_WMUNLOCK = 0x4D;   /* 'M' */
    if (REG_WMID != WMS_ID)
    {
        wms_present = 0;
        wms_features = 0;
        return 0;
    }
    wms_present = 1;
    wms_features = REG_WMFEAT;
#else
    /* the negative control: no unlock, and the game is told it worked */
    wms_present = 1;
    wms_features = 0xFF;
#endif
    return 1;
}

u8 wmsHas(u8 features)
{
    return (wms_features & features) == features ? 1 : 0;
}

void wmsFastCpu(u8 on)
{
    REG_WMSPEED = on ? 1 : 0;
}

void wmsFastDma(u8 on)
{
    REG_WMDMA = on ? 1 : 0;
}

void wmsScreen(u8 ctrl)
{
    REG_WMCTRL = ctrl;
}

void wmsLayersMain(u8 mask)
{
    REG_WMTM = mask;
}

void wmsLayer(u8 n, u32 mapAddr, u8 cfg)
{
    u32 a = mapAddr;
    n &= 7;
    wms_poke(WMS_REG_BGMAP + n, (u8)(a >> 11));
    wms_poke(WMS_REG_BGCFG + n, cfg);
}

void wmsScroll(u8 n, u16 x, u16 y)
{
    n &= 7;
    wms_hofs[n] = x;
    wms_vofs[n] = y;
    wms_scroll_dirty = 1;
}

/* three consecutive address registers (low, middle, high) from a 24-bit value */
static void wms_addr3(u16 reg, u32 value)
{
    u32 v = value;
    wms_poke(reg, (u8)v);
    wms_poke(reg + 1, (u8)(v >> 8));
    wms_poke(reg + 2, (u8)(v >> 16));
}

void wmsVramCopy(u32 vramAddr, u8 *src, u16 size)
{
    u32 a = vramAddr;
    wms_addr3(0x21C2, a);
    wms_dma(0, 0x00, 0xC5, src, size);
}

void wmsVramFill(u32 vramAddr, u8 value, u16 size)
{
    u32 a = vramAddr;
    wms_addr3(0x21C2, a);
    wms_dma(0, 0x08, 0xC5, &wms_byte_values[value], size);
}

void wmsPalCopy(u16 first, u8 *src, u16 count)
{
    REG_WMCGADDL = (u8)first;
    REG_WMCGADDH = (u8)(first >> 8);
    wms_dma(0, 0x00, 0xC8, src, count + count);
}

void wmsColor(u16 entry, u16 bgr15)
{
    REG_WMCGADDL = (u8)entry;
    REG_WMCGADDH = (u8)(entry >> 8);
    REG_WMCGDATA = (u8)bgr15;
    REG_WMCGDATA = (u8)(bgr15 >> 8);
}

/* wmsSpriteSet, wmsSpriteMove, wmsSpriteHide: wmslib.asm (a game calls them a hundred times a frame) */

void wmsSpriteHideFrom(u16 first)
{
    u16 p;
    for (p = first << 3; p < WMS_SPRITES * 8; p += 8)
    {
        wmsOamShadow[p + 2] = 240;
        wmsOamShadow[p + 3] = 0;
    }
    wms_oam_dirty = 1;
}

void wmsSpritesShow(u8 on)
{
    *(vuint8 *)0x212C = on ? 0x10 : 0x00;   /* TM */
}

void wmsSpritePri(u8 prio, u8 slots)
{
    wms_poke(0x21A6 + (prio & 3), slots);
}

void wmsSpritesInit(u8 tileBase64k)
{
    u16 p;
    for (p = 0; p < WMS_SPRITES * 8; p++) wmsOamShadow[p] = 0;
    wmsSpriteHideFrom(0);
    REG_WMOBJBASE = tileBase64k & 7;
    REG_WMOBJ = 1;
    wmsSpritesUpload();
}

static void wms_oam_upload(u8 ch)
{
    REG_WMOAMADDL = 0;
    REG_WMOAMADDH = 0;
    wms_dma(ch, 0x00, 0xA4, wmsOamShadow, WMS_SPRITES * 8);
    wms_oam_dirty = 0;
}

void wmsSpritesUpload(void)
{
    wms_oam_upload(0);
}

void wmsVblank(void)
{
    u8 n;
    if (wms_oam_dirty) wms_oam_upload(1);
    if (wms_scroll_dirty)
    {
        for (n = 0; n < 8; n++) wmsScrollNow(n, wms_hofs[n], wms_vofs[n]);
        wms_scroll_dirty = 0;
    }
}

u32 wmsWramAddr(u32 cpuAddr)
{
    u32 a = cpuAddr;
    u16 bank = (u16)(a >> 16);
    return ((u32)((bank + 2) & 0x0F) << 16) | (a & 0xFFFF);
}

void wmsWramCopy(u32 wramAddr, u8 *src, u16 size)
{
    u32 a = wramAddr;
    wms_addr3(0x2181, a);
    wms_dma(0, 0x00, 0x80, src, size);
}

void wmsWramFill(u32 wramAddr, u8 value, u16 size)
{
    u32 a = wramAddr;
    wms_addr3(0x2181, a);
    wms_dma(0, 0x08, 0x80, &wms_byte_values[value], size);
}

void wmsSoundCopy(u32 soundAddr, u8 *src, u16 size)
{
    u32 a = soundAddr;
    wms_addr3(0x21F0, a);
    wms_dma(0, 0x00, 0xF3, src, size);
}

void wmsSoundFill(u32 soundAddr, u8 value, u16 size)
{
    u32 a = soundAddr;
    wms_addr3(0x21F0, a);
    wms_dma(0, 0x08, 0xF3, &wms_byte_values[value], size);
}
