/* The .flp test ROM (Vaults GG): the game's own sound unit (VRUN_VOC_GG_dev src/voc_sound.c + src/sfx/voc_sfx_psg.c, compiled unchanged)
   plays song 0 from power-on, the game's vblank order (effects, then the music's frame), nothing else. The critical sections are
   gg_vdp.c's (copied: the rest of that file is the picture chip). RAM for tests: flp_frames. */
#include <stdint.h>
#include "SMSlib.h"
#include "gg.h"
#include "voc_sound.h"

volatile uint16_t flp_frames;
volatile uint8_t __at(0xFFFF) voc_slot2;   /* the Sega mapper's slot-2 bank register (the game defines it in voc_screen_vbl.c) */

uint8_t gg_crit_enter(void) __naked
{
    __asm
        ld a, i
        jp pe, 00001$
        ld a, i
        jp pe, 00001$
        di
        xor a, a
        ret
    00001$:
        di
        ld a, #1
        ret
    __endasm;
}

void gg_crit_leave(uint8_t was_on) __naked
{
    (void)was_on;
    __asm
        or a, a
        ret z
        ei
        ret
    __endasm;
}

static void vbl(void)
{
    flp_frames++;
    voc_sfx_update();
    snd_frame();
}

void main(void)
{
    snd_init();
    mus_play(MUS_TITLE);
    SMS_setFrameInterruptHandler(vbl);
    SMS_displayOn();
    for (;;) SMS_waitForVBlank();
}

SMS_EMBED_SEGA_ROM_HEADER(9999, 0);
SMS_EMBED_SDSC_HEADER(1, 0, 2026, 11, 10, "Redscientist Labs", "VRUN Vaults GG FLP test", "The game's sound unit playing an FL project");
