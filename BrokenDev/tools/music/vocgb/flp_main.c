/* The .flp test ROM (Vaults GB): plays song 0 from power-on with the game's own music driver, looping, nothing else.
   RAM for tests: flp_frames (vblanks since power-on). */
#include <gb/gb.h>
#include <stdint.h>
#include <stdio.h>
#include "voc_music.h"

void voc_sfx_init(void) BANKED;
volatile uint16_t flp_frames;

static void vbl(void)
{
    flp_frames++;
    voc_music_update();
}

void main(void)
{
    voc_sfx_init();
    voc_music_init();
    printf("VRUN VAULTS GB\nFL PROJECT TEST\n\nthe game's own\nmusic driver\n");
    voc_music_play_song(0);
    disable_interrupts();
    add_VBL(vbl);
    set_interrupts(VBL_IFLAG);
    enable_interrupts();
    for (;;) wait_vbl_done();
}
