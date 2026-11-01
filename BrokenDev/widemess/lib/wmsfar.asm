; wmsfar.asm: the WideMess library's memory (BrokenDev's WideMess devkit, see widemess.h).
;
; Far RAM: the sprite table's shadow lives in the 1 MB work RAM (bank $70, SLOT 3: see hdr.asm), so it costs the SNES's 128 KB nothing.
; ROM: the 256 byte values, the source of the library's fixed-address fill DMAs.
;
; A far RAM section needs .BASE $00 first: hdr.asm sets .BASE $C0 (FastROM) or $40 for the code, and WLA adds the base to a RAMSECTION's
; bank too ($70 + $C0 would land in bank $30, which mirrors the SNES's low RAM: the first build of this file wrote its sprite table over
; PVSnesLib's nmi_handler that way).

.include "hdr.asm"

.BASE $00
.RAMSECTION "wms_far_lib" BANK $70 SLOT 3 ORGA $0000 FORCE
wmsOamShadow dsb 2048
.ENDS

.ifdef FASTROM
.BASE $C0
.else
.BASE $40
.endif
.SECTION ".wms_rodata" SUPERFREE
wms_byte_values:
.REPT 256 INDEX v
  .db v
.ENDR
.ENDS
