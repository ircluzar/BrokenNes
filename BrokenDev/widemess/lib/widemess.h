/*---------------------------------------------------------------------------------
    widemess.h: the WideMess extensions to PVSnesLib (BrokenDev's WideMess devkit).

    The hardware contract is docs/projects/widemess-spec.md (spec v1); the section numbers below are its sections.
    A WideMess game is a PVSnesLib game built on the WideMess map (game-file map 2, HiROM-shaped) by BrokenDev's
    wmsbuild.ps1, which links this library (widemess.c, wmsfar.asm) and, when the game plays sound, the sound driver
    (wmssound.h). The devkit guide is docs/projects/widemess-devkit.md.

    Rules the library follows (and a game should too):
      - The extensions are invisible until wmsInit() unlocks them; a game checks its return value and the features it uses.
      - Video RAM, colours and the sprite table may be written only in vblank or forced blank (spec 3, "Video RAM timing").
        The copy functions run a DMA at once: call them in forced blank (setScreenOff) or right after WaitForVBlank().
      - The library's DMA uses channel 0 from the main program and channel 1 from wmsVblank() (inside the NMI). Keep HDMA off
        those two channels.
---------------------------------------------------------------------------------*/
#ifndef WIDEMESS_H
#define WIDEMESS_H

#include <snes.h>

/* ---- 1. identity and unlock (CPU registers, banks $00-$3F / $80-$BF) ---------------------------------------- */
#define REG_WMUNLOCK   (*(vuint8 *)0x4220)   /* W: $57 then $4D unlocks until the next reset */
#define REG_WMID       (*(vuint8 *)0x4221)   /* R: $57 once unlocked */
#define REG_WMVER      (*(vuint8 *)0x4222)   /* R: spec major (1) */
#define REG_WMREV      (*(vuint8 *)0x4223)   /* R: spec minor (0) */
#define REG_WMFEAT     (*(vuint8 *)0x4224)   /* R: features (WMFEAT_*) */
#define REG_WMSPEED    (*(vuint8 *)0x4225)   /* R/W: bit 0 fast CPU (section 2) */
#define REG_WMDMA      (*(vuint8 *)0x4226)   /* R/W: bit 0 fast general DMA (section 2) */

#define WMS_ID           0x57
#define WMFEAT_PICTURE   0x01   /* wide output, the 8-layer mode */
#define WMFEAT_SPRITES   0x02   /* the 256-sprite table, windows and colour math at 400, the SNES modes wide */
#define WMFEAT_SOUND     0x04   /* 32 voices, 16-bit PCM, 1 MB sound RAM */
#define WMFEAT_FASTCPU   0x08
#define WMFEAT_BIGWRAM   0x10   /* 1 MB work RAM (on the WideMess map) */
#define WMFEAT_FASTDMA   0x20
#define WMFEAT_DEBUG     0x80

/* ---- 2. the WRAM port: 20-bit work-RAM addresses once unlocked ---------------------------------------------- */
#define REG_WMWDATA    (*(vuint8 *)0x2180)
#define REG_WMWADDL    (*(vuint8 *)0x2181)
#define REG_WMWADDM    (*(vuint8 *)0x2182)
#define REG_WMWADDH    (*(vuint8 *)0x2183)   /* bits 0-3 once unlocked */

/* ---- 3.3 sprites ($21A0-$21AF) ------------------------------------------------------------------------------ */
#define REG_WMOBJ      (*(vuint8 *)0x21A0)   /* bit 0: the WideMess table */
#define REG_WMOBJBASE  (*(vuint8 *)0x21A1)   /* bits 0-2: tiles at this x 64 KB of video RAM */
#define REG_WMOAMADDL  (*(vuint8 *)0x21A2)
#define REG_WMOAMADDH  (*(vuint8 *)0x21A3)
#define REG_WMOAMDATA  (*(vuint8 *)0x21A4)
#define REG_WMOBJSTAT  (*(vuint8 *)0x21A5)   /* bit 6: over 64 sprites on a line; bit 7: over 128 slivers */
#define REG_WMOBJPRI0  (*(vuint8 *)0x21A6)   /* + priority: BG slots in front of the sprites of that priority (0-16) */

/* ---- 3.4 windows and colour math at 400 ($21B0-$21BF) ------------------------------------------------------- */
#define REG_WMWH0      (*(vuint8 *)0x21B0)   /* 9 bits, written twice */
#define REG_WMWH1      (*(vuint8 *)0x21B1)
#define REG_WMWH2      (*(vuint8 *)0x21B2)
#define REG_WMWH3      (*(vuint8 *)0x21B3)
#define REG_WMWCTL     (*(vuint8 *)0x21B4)   /* bit 0: the windows' positions are WMWH0-3 */
#define REG_WMTS       (*(vuint8 *)0x21B5)
#define REG_WMTMW      (*(vuint8 *)0x21B6)
#define REG_WMTSW      (*(vuint8 *)0x21B7)
#define REG_WMW12SEL   (*(vuint8 *)0x21B8)
#define REG_WMW34SEL   (*(vuint8 *)0x21B9)
#define REG_WMW56SEL   (*(vuint8 *)0x21BA)
#define REG_WMW78SEL   (*(vuint8 *)0x21BB)
#define REG_WMWLOG14   (*(vuint8 *)0x21BC)
#define REG_WMWLOG58   (*(vuint8 *)0x21BD)
#define REG_WMCMATH    (*(vuint8 *)0x21BE)

/* ---- 3. the picture ($21C0-$21EF) --------------------------------------------------------------------------- */
#define REG_WMCTRL     (*(vuint8 *)0x21C0)   /* WMCTRL_WIDE | WMCTRL_LAYERS8 */
#define REG_WMTM       (*(vuint8 *)0x21C1)   /* BG1-BG8 on the main screen */
#define REG_WMVADDL    (*(vuint8 *)0x21C2)
#define REG_WMVADDM    (*(vuint8 *)0x21C3)
#define REG_WMVADDH    (*(vuint8 *)0x21C4)   /* bits 0-2: 19-bit byte address */
#define REG_WMVDATA    (*(vuint8 *)0x21C5)
#define REG_WMCGADDL   (*(vuint8 *)0x21C6)
#define REG_WMCGADDH   (*(vuint8 *)0x21C7)   /* bits 0-1: 10-bit colour entry */
#define REG_WMCGDATA   (*(vuint8 *)0x21C8)
#define WMS_REG_BGMAP  0x21D0                /* + layer: map at value x 2 KB */
#define WMS_REG_BGCFG  0x21D8                /* + layer */
#define WMS_REG_BGHOFS 0x21E0                /* + 2 x layer, written twice */
#define WMS_REG_BGVOFS 0x21E1

#define WMCTRL_WIDE     0x01
#define WMCTRL_LAYERS8  0x02

/* WMBGnCFG */
#define WMS_MAP_64x32   0x00
#define WMS_MAP_64x64   0x01
#define WMS_MAP_128x32  0x02
#define WMS_MAP_128x64  0x03
#define WMS_BANK(b)     (((b) & 3) << 2)      /* colours b x 256 .. + 255 */
#define WMS_TILES(t)    (((t) & 7) << 4)      /* tiles start at t x 64 KB of video RAM */

/* a map entry: tile (0-4095) | WMS_PRIO | WMS_HFLIP | WMS_VFLIP */
#define WMS_PRIO        0x1000
#define WMS_HFLIP       0x4000
#define WMS_VFLIP       0x8000

/* ---- 4. the sound RAM port ($21F0-$21F3) -------------------------------------------------------------------- */
#define REG_WMSADDL    (*(vuint8 *)0x21F0)
#define REG_WMSADDM    (*(vuint8 *)0x21F1)
#define REG_WMSADDH    (*(vuint8 *)0x21F2)   /* bits 0-3: 20-bit sound RAM address */
#define REG_WMSDATA    (*(vuint8 *)0x21F3)

/* ---- the screen ------------------------------------------------------------------------------------------- */
#define WMS_WIDTH   400
#define WMS_HEIGHT  224

/* ---- sprites: byte 6 and byte 7 of an entry (spec 3.3) ---------------------------------------------------- */
#define WMS_SPR_PAL(p)    ((p) & 0x3F)       /* 4 bpp: colours p x 16 ..; 8 bpp: bank p / 16 */
#define WMS_SPR_HFLIP     0x40
#define WMS_SPR_VFLIP     0x80
#define WMS_SPR_W8        0x00
#define WMS_SPR_W16       0x01
#define WMS_SPR_W32       0x02
#define WMS_SPR_W64       0x03
#define WMS_SPR_H8        0x00
#define WMS_SPR_H16       0x04
#define WMS_SPR_H32       0x08
#define WMS_SPR_H64       0x0C
#define WMS_SPR_PRIO(p)   (((p) & 3) << 4)
#define WMS_SPR_8BPP      0x40
#define WMS_SPR_MATH      0x80
#define WMS_SPRITES       256

/* ---- the library ---------------------------------------------------------------------------------------- */

extern u8 wms_present;    /* 1 after wmsInit() found a WideMess */
extern u8 wms_features;   /* WMFEAT, after wmsInit() */

/* Unlocks the extensions and checks the ID (spec 1). Returns 1 on a WideMess (then wms_features holds WMFEAT), 0 elsewhere. */
u8 wmsInit(void);
/* 1 when every feature bit asked for is present. */
u8 wmsHas(u8 features);

/* Section 2: fast CPU (10.74 MHz; I/O registers keep their SNES timing) and fast general DMA (a byte every 4 master clocks). */
void wmsFastCpu(u8 on);
void wmsFastDma(u8 on);

/* Section 3: the picture. ctrl = WMCTRL_WIDE | WMCTRL_LAYERS8. */
void wmsScreen(u8 ctrl);
void wmsLayersMain(u8 mask);
/* Layer n (0-7): its map's byte address in video RAM (a multiple of 2 KB) and its WMBGnCFG (WMS_MAP_* | WMS_BANK(b) | WMS_TILES(t)). */
void wmsLayer(u8 n, u32 mapAddr, u8 cfg);
/* Layer n's scroll, 0-1023. Kept and written by wmsVblank(); wmsScrollNow() writes it at once (forced blank, vblank). */
void wmsScroll(u8 n, u16 x, u16 y);
void wmsScrollNow(u8 n, u16 x, u16 y);

/* DMA copies (forced blank, or right after WaitForVBlank()). size 0 means 65,536 bytes. */
void wmsVramCopy(u32 vramAddr, u8 *src, u16 size);
void wmsVramFill(u32 vramAddr, u8 value, u16 size);
/* Runs an upload table in forced blank: wmsgfx.py's gfx_uploads (gfx.h: gfxLoadAll()) or a game's own (records of 12 bytes,
   described in wmslib.asm): maps, tiles, colours, the layers' settings, the sprite table's base, WMTM, work RAM, sound RAM. */
void wmsUpload(u8 *table);
/* count colours (2 bytes each, 15-bit BGR) from entry first (0-1023). */
void wmsPalCopy(u16 first, u8 *src, u16 count);
void wmsColor(u16 entry, u16 bgr15);

/* The WideMess sprite table (spec 3.3). wmsSpritesInit turns it on (WMOBJ), sets its tiles' 64 KB unit and hides all 256.
   The table is kept in work RAM ($70:0000, see wmsfar.asm) and uploaded by wmsVblank() when it changed. */
void wmsSpritesInit(u8 tileBase64k);
/* attr6 = WMS_SPR_PAL(p) | flips; attr7 = size | WMS_SPR_PRIO(p) | WMS_SPR_8BPP | WMS_SPR_MATH. x, y: picture column and row (-512..511). */
void wmsSpriteSet(u8 id, s16 x, s16 y, u16 tile, u8 attr6, u8 attr7);
void wmsSpriteMove(u8 id, s16 x, s16 y);
void wmsSpriteHide(u8 id);
void wmsSpriteHideFrom(u16 first);
/* Sprites on the main screen (the SNES's TM bit 4, which the WideMess mode keeps for its sprites; it ignores TM's BG bits). */
void wmsSpritesShow(u8 on);
/* WMOBJPRIn: how many of the 16 BG slots are in front of sprites of priority prio. */
void wmsSpritePri(u8 prio, u8 slots);
/* Uploads the sprite table now (forced blank, vblank). */
void wmsSpritesUpload(void);

/* Call from your nmi handler (nmiSet): uploads the sprite table if it changed and writes the scrolls wmsScroll() kept. */
void wmsVblank(void);

/* Section 2: the 1 MB work RAM. A CPU address in banks $70-$7F as a 20-bit work-RAM address, and DMA through the WRAM port. */
u32 wmsWramAddr(u32 cpuAddr);
void wmsWramCopy(u32 wramAddr, u8 *src, u16 size);
void wmsWramFill(u32 wramAddr, u8 value, u16 size);

/* Section 4: the 1 MB sound RAM through the main CPU's port (DMA). */
void wmsSoundCopy(u32 soundAddr, u8 *src, u16 size);
void wmsSoundFill(u32 soundAddr, u8 value, u16 size);

#endif
