; far.asm: this game's work RAM above the SNES's 128 KB (the WideMess map's banks $70-$7D; see hdr.asm, written by the build).
; C sees each label as an ordinary array:  extern u8 far_frames[];
; Bank $70 $0000-$07FF is the WideMess library's sprite table; the rest of banks $70-$7D is the game's (896 KB).

.include "hdr.asm"

.BASE $00
.RAMSECTION "far_template" BANK $71 SLOT 3
far_frames dsb 16
.ENDS
