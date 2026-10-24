; far.asm: Wide Blaster's work RAM above the SNES's 128 KB (the WideMess map's banks $70-$7D; hdr.asm is written by the build).
; Every pool of the game lives here, so the SNES's 128 KB keeps only PVSnesLib's own variables.
;   bank $71: the actors (bullets, enemies, explosions, sparkles, the boss)
;   bank $72: the enemies' flight path, 1,024 steps, computed at power-on
;   bank $73: the flight recorder: the pad, every frame, 32,768 frames (9 minutes) round

.include "hdr.asm"

.BASE $00
.RAMSECTION "far_actors" BANK $71 SLOT 3
bul_x   dsw 24
bul_y   dsw 24
bul_on  dsb 24
en_x    dsw 20
en_y0   dsw 20
en_t    dsw 20
en_on   dsb 20
en_kind dsb 20
fx_x    dsw 32
fx_y    dsw 32
fx_t    dsb 32
sp_x    dsw 120
sp_y    dsw 120
sp_v    dsb 120
.ENDS

.RAMSECTION "far_path" BANK $72 SLOT 3
path_y  dsw 1024
.ENDS

.RAMSECTION "far_recorder" BANK $73 SLOT 3
rec_pad dsw 32768
.ENDS
