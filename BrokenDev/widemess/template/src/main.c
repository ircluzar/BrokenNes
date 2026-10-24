/*---------------------------------------------------------------------------------
    {{TITLE}}: a WideMess game, made from BrokenDev's template (brokendev new widemess {{NAME}}).

    The devkit guide: docs/projects/widemess-devkit.md (in the BrokenNes repository). The hardware: docs/projects/widemess-spec.md.
    Build, check and play it:  brokendev build {{NAME}} / verify {{NAME}} / run {{NAME}}

    What this starter shows, one feature each:
      - the unlock and the feature check (a SNES gets a red screen instead);
      - three of the 8 layers at 400 x 224 (wms.json "graphics" -> gfx.h; gfxLoadAll() uploads it all);
      - a sprite of the 256-sprite table, moved with the d-pad;
      - fast mode (10.74 MHz) and a counter in the 1 MB work RAM (src/far.asm);
      - a song on the WideMess sound chip (wms.json "sound" -> snd.h; the sound driver plays it).
---------------------------------------------------------------------------------*/
#include <snes.h>
#include "widemess.h"
#include "wmssound.h"
#include "gfx.h"
#include "snd.h"

/* src/far.asm: work RAM in bank $71, above the SNES's 128 KB */
extern u8 far_frames[];

static s16 ship_x, ship_y;
u16 game_frame;                        /* global: BrokenDev compat reads it by its symbol */

static void vblank(void)
{
    wmsVblank();
}

static void not_a_widemess(void)
{
    /* a SNES, or an emulator without WideMess: a red screen in the SNES's own colours */
    setPaletteColor(0, RGB5(31, 0, 0));
    setScreenOn();
    while (1) WaitForVBlank();
}

int main(void)
{
    u16 pad;

    consoleInit();
    setScreenOff();
    if (!wmsInit() || !wmsHas(WMFEAT_PICTURE | WMFEAT_SPRITES | WMFEAT_SOUND | WMFEAT_FASTCPU | WMFEAT_BIGWRAM)) not_a_widemess();

    wmsFastCpu(1);                      /* 10.74 MHz from here on */
    gfxLoadAll();                       /* maps, tiles, 1,024 colours, the layers and WMTM */
    wmsSpritesInit(GFX_SPRITE_UNIT);    /* the WideMess sprite table, every sprite hidden */
    wmsSpritesShow(1);
    wmsScreen(WMCTRL_WIDE | WMCTRL_LAYERS8);

    /* the sound driver and the samples, through the sound RAM port; if the sound unit does not answer, say so (a red backdrop) */
    if (!sndInit()) wmsColor(0, RGB5(31, 0, 0));
    sndPlaySong(SND_SONG_HELLO);

    ship_x = 192;
    ship_y = 120;
    nmiSet(vblank);
    setScreenOn();

    while (1)
    {
        pad = pad_keys[0];
        if ((pad & KEY_LEFT) && ship_x > 0) ship_x -= 2;
        if ((pad & KEY_RIGHT) && ship_x < WMS_WIDTH - 16) ship_x += 2;
        if ((pad & KEY_UP) && ship_y > 0) ship_y -= 2;
        if ((pad & KEY_DOWN) && ship_y < WMS_HEIGHT - 16) ship_y += 2;
        if (pad_keysdown[0] & KEY_A) sndPlayEffect(SND_SFX_DING);
        if (pad_keysdown[0] & KEY_START) sndPlaySong(SND_SONG_HELLO);

        wmsSpriteSet(0, ship_x, ship_y, GFX_SHIP_TILE((game_frame >> 3) & 1), GFX_SHIP_ATTR6, GFX_SHIP_ATTR7 | WMS_SPR_PRIO(3));
        wmsScroll(GFX_HILLS_LAYER, game_frame >> 1, 0);
        wmsScroll(GFX_TITLE_LAYER, 0, (u16)(-24 - (s16)((game_frame >> 2) & 15)));

        far_frames[0]++;                /* proof of life in the 1 MB work RAM */
        game_frame++;
        WaitForVBlank();
    }
    return 0;
}
