| Nested NesGen runtime (68000, GNU as, --register-prefix-optional).  Included at the top of the generated game.s,
| which then defines: MIRBIT (10 vertical / 11 horizontal mirroring), nmi_entry, reset_entry, nescol (64 Genesis
| colours), prg (32 KB PRG image), chrdata/chrdata_end (1024 converted tiles), disp (32 K longs) and the L<addr> code.
|
| Registers while 6502 code runs: d0 A, d1 X, d2 Y, d3.w NZ, d4 C, d6 S; temps d5 d7 a0 a1; a2 = $FF0100 (stack page),
| a3 = VDP data, a4 = VDP control, a5 = $FF0000 (NES RAM), a6 = VARS.

	.equ	VARS,    0xFF8000
	.equ	vflag,   0x00
	.equ	iflag,   0x01
	.equ	ppuctrl, 0x02
	.equ	ppumask, 0x03
	.equ	ppustat, 0x04
	.equ	wlatch,  0x05
	.equ	oamaddr, 0x06
	.equ	rdbuf,   0x07
	.equ	ppuaddr, 0x08        | word
	.equ	ppuinc,  0x0A        | word 1 or 32
	.equ	scrollx, 0x0C
	.equ	scrolly, 0x0D
	.equ	padnes,  0x0E        | current pad, NES bit order (bit0 A ... bit7 Right)
	.equ	padsh,   0x0F        | the shift register $4016 reads
	.equ	frames,  0x10        | long, VInts seen
	.equ	nmis,    0x14        | long, NMIs run
	.equ	icount,  0x18        | long, 6502 instructions run (count builds only)
	.equ	nmi_ic,  0x1C        | long, instructions in the last NMI
	.equ	in_nmi,  0x20
	.equ	nmi_v0,  0x22        | word: V counter when the last NMI started
	.equ	nmi_v1,  0x24        | word: V counter when it returned
	.equ	nmi_late,0x26        | word: VInt-pending flag seen at its return (it ran past the next VInt)
	.equ	nmi_ic0, 0x28        | long
	.equ	busy_sum,0x2C        | long: sum of decoded NMI lengths in lines
	.equ	busy_max,0x30        | word: longest NMI in lines
	.equ	sbase,   0x38        | long: 68000 stack base of the running context (JSR check frames sit above it)
	.equ	ic_sum,  0x34        | long: 6502 instructions run inside NMIs (count builds)
	.equ	missed,  0x40        | word: 6502 address of a dispatch miss
	.equ	missflag,0x42        | $A5 after a miss
	.equ	apu,     0x44        | 24 bytes: $4000-$4017 as written
	.equ	sq,      0x5C        | 4 channels x 8 bytes: +0 length, +1 env vol, +2 env acc, +3 linear, +4 last atten, +5 last noise ctl
	.equ	oam,     0x100       | 256 bytes
	.equ	palsh,   0x200       | 32 bytes
	.equ	ntsh,    0x1000      | 2 KB nametable shadow

	.equ	VDP_DATA, 0xC00000
	.equ	VDP_CTRL, 0xC00004
	.equ	PSG,      0xC00011

	.text
	.long	0x00FFFE00, start, exc, exc, exc, exc, exc, exc
	.long	exc, exc, exc, exc, exc, exc, exc, exc
	.long	exc, exc, exc, exc, exc, exc, exc, exc
	.long	exc, exc, exc, exc, hint, exc, vint, exc
	.long	exc, exc, exc, exc, exc, exc, exc, exc
	.long	exc, exc, exc, exc, exc, exc, exc, exc
	.long	exc, exc, exc, exc, exc, exc, exc, exc
	.long	exc, exc, exc, exc, exc, exc, exc, exc
	.ascii	"SEGA MEGA DRIVE "
	.ascii	"(C)NEST 2026.NOV"
	.ascii	"NESTED NESGEN                                   "
	.ascii	"NESTED NESGEN                                   "
	.ascii	"GM 00000000-00"
	.word	0
	.ascii	"J               "
	.long	0, 0x1FFFF, 0xFF0000, 0xFFFFFF
	.ascii	"            "
	.ascii	"            "
	.ascii	"                                        "
	.ascii	"JUE             "

exc:	move.w	#0x2700,sr
	bra.s	exc
hint:	rte

vdpregs:
	.byte	0x04, 0x24, 0x30, 0x2C, 0x07, 0x7C, 0x00, 0x00, 0x00, 0x00, 0xFF, 0x00, 0x00, 0x3F, 0x00, 0x02
	.byte	0x10, 0x00, 0x00
	.even

start:	move.w	#0x2700,sr
	move.b	0xA10001,d0
	andi.b	#0x0F,d0
	beq.s	1f
	move.l	#0x53454741,0xA14000         | TMSS
1:	lea	0xFF0000,a0
	move.w	#0x3FFF,d0
2:	clr.l	(a0)+
	dbra	d0,2b
	lea	VDP_CTRL,a4
	lea	VDP_DATA,a3
	move.w	(a4),d0
	lea	vdpregs(pc),a0
	move.w	#0x8000,d1
	moveq	#18,d0
3:	move.b	(a0)+,d1
	move.w	d1,(a4)
	addi.w	#0x0100,d1
	dbra	d0,3b
	move.l	#0x40000000,(a4)              | clear 64 KB VRAM
	move.w	#0x7FFF,d0
4:	move.w	#0,(a3)
	dbra	d0,4b
	move.l	#0xC0000000,(a4)              | clear CRAM
	moveq	#63,d0
5:	move.w	#0,(a3)
	dbra	d0,5b
	move.l	#0x40000010,(a4)              | VSRAM: 8 lines down (the NES's top 8 lines are cropped)
	move.w	#8,(a3)
	move.w	#8,(a3)
	move.l	#0x60000003,(a4)              | plane B ($E000) -> blank tile 1024 (tile 0 is a real NES tile)
	move.w	#2047,d0
7:	move.w	#0x0400,(a3)
	dbra	d0,7b
	move.l	#0x40000000,(a4)              | tiles -> VRAM $0000
	lea	chrdata,a0
	move.w	#(0x8000/4)-1,d0
6:	move.l	(a0)+,(a3)
	dbra	d0,6b
	move.b	#0x40,0xA10009                | pad 1 TH as output
	move.b	#0x40,0xA10003
	lea	PSG,a0                         | PSG silent
	move.b	#0x9F,(a0)
	move.b	#0xBF,(a0)
	move.b	#0xDF,(a0)
	move.b	#0xFF,(a0)
	lea	VARS,a6
	move.w	#1,ppuinc(a6)
	lea	0xFF0000,a5
	lea	0xFF0100,a2
	moveq	#0,d0
	moveq	#0,d1
	moveq	#0,d2
	moveq	#0,d3
	moveq	#0,d4
	moveq	#0,d5
	move.l	#0xFD,d6
	moveq	#0,d7
	move.l	#-1,-(sp)                     | sentinel: a JSR check frame never matches the base
	move.l	#-1,-(sp)
	move.l	sp,sbase(a6)
	move.w	#0x8164,(a4)                  | display on, VInt on
	move.w	#0x2000,sr
	jra	reset_entry

| ---------------------------------------------------------------- VInt = NES vblank (+ NMI)
vint:	movem.l	d0-d7/a0-a6,-(sp)
	lea	VARS,a6
	addq.l	#1,frames(a6)
	ori.b	#0x80,ppustat(a6)
	bsr	read_pad
	bsr	apu_frame
	btst	#7,ppuctrl(a6)
	beq	vint_out
	tst.b	in_nmi(a6)
	bne	vint_out
	st	in_nmi(a6)
	addq.l	#1,nmis(a6)
	move.l	icount(a6),nmi_ic0(a6)
	move.w	VDP_CTRL+4,d7                 | HV counter
	lsr.w	#8,d7
	move.w	d7,nmi_v0(a6)
	movem.l	(sp),d0-d7/a0-a6              | the interrupted 6502 state
	move.b	#0,0(a2,d6.w)                 | push PC $0000 (the marker RTI looks for)
	subq.b	#1,d6
	move.b	#0,0(a2,d6.w)
	subq.b	#1,d6
	jsr	php_build
	andi.b	#0xEF,d7
	move.b	d7,0(a2,d6.w)
	subq.b	#1,d6
	move.b	#1,iflag(a6)
	move.l	sbase(a6),-(sp)
	move.l	#-1,-(sp)
	move.l	#-1,-(sp)
	move.l	sp,sbase(a6)
	jra	nmi_entry
nmi_return:                                   | RTI popped the marker
	lea	VARS,a6
	move.l	sbase(a6),sp                  | drop JSR frames the NMI left (a PLA PLA exit)
	addq.l	#8,sp
	move.l	(sp)+,sbase(a6)
	move.w	VDP_CTRL,d7
	andi.w	#0x80,d7                      | VInt pending = ran into the next frame's vblank
	move.w	d7,nmi_late(a6)
	move.w	VDP_CTRL+4,d7
	lsr.w	#8,d7
	move.w	d7,nmi_v1(a6)
	| length in lines: from the V counters at start and end (the 224-line V counter runs 0..$EA then $E5..$FF;
	| $E5-$EA are read as the second run, a <=6 line error), plus a frame when the VInt-pending flag says a
	| line-224 crossing happened that the two samples do not show
	move.w	nmi_v1(a6),d5
	bsr	vdecode
	move.w	d5,d7
	move.w	nmi_v0(a6),d5
	bsr	vdecode                       | d5 = l0, d7 = l1
	sub.w	d5,d7
	bpl.s	1f
	addi.w	#262,d7                       | d7 = d
1:	add.w	d7,d5                         | d5 = l0 + d
	cmpi.w	#224,d5
	bcs.s	2f                            | l0 + d < 224: no crossing seen
	sub.w	d7,d5
	cmpi.w	#224,d5
	bcs.s	3f                            | l0 < 224 <= l0 + d: crossing seen
	add.w	d7,d5
	cmpi.w	#224+262,d5
	bcc.s	3f
2:	tst.w	nmi_late(a6)
	beq.s	3f
	addi.w	#262,d7
3:	move.w	d7,d5
3:	ext.l	d5
	add.l	d5,busy_sum(a6)
	cmp.w	busy_max(a6),d5
	bls.s	4f
	move.w	d5,busy_max(a6)
4:	move.l	icount(a6),d5
	sub.l	nmi_ic0(a6),d5
	move.l	d5,nmi_ic(a6)
	add.l	d5,ic_sum(a6)
	sf	in_nmi(a6)
vint_out:
	movem.l	(sp)+,d0-d7/a0-a6
	rte

| ---------------------------------------------------------------- dispatch (d5.w = 6502 address)
rts_slow:                                     | S was already moved past the return address
	move.l	sbase(a6),sp
	move.b	0(a2,d6.w),d5
	lsl.w	#8,d5
	move.b	-1(a2,d6.w),d5
	addq.w	#1,d5
	bra	dispatch
do_rts:	move.b	1(a2,d6.w),d7                 | read both bytes before S moves
	move.b	2(a2,d6.w),d5
	addq.b	#2,d6
	lsl.w	#8,d5
	move.b	d7,d5
	addq.w	#1,d5
dispatch:
	moveq	#0,d7
	move.w	d5,d7
	bclr	#15,d7
	beq.s	miss
	lsl.l	#2,d7
	lea	disp,a0
	move.l	0(a0,d7.l),a0
	jmp	(a0)
do_rti:	addq.b	#1,d6
	move.b	0(a2,d6.w),d7
	bsr	plp_unpack
	move.b	#0,iflag(a6)
	addq.b	#1,d6
	move.b	0(a2,d6.w),d7
	addq.b	#1,d6
	move.b	0(a2,d6.w),d5
	lsl.w	#8,d5
	move.b	d7,d5
	tst.w	d5
	beq	nmi_return
	bra.s	dispatch
vdecode:
	cmpi.w	#0xE5,d5
	bcs.s	1f
	addq.w	#6,d5
1:	rts
brk_hit:
miss:	lea	VARS,a6
	move.w	d5,missed(a6)
	move.b	#0xA5,missflag(a6)
	move.w	#0x2700,sr
	move.l	#0xC0000000,VDP_CTRL
	move.w	#0x000E,VDP_DATA
	move.w	#0x8700,VDP_CTRL
	move.w	#0x8144,VDP_CTRL
9:	bra.s	9b

| ---------------------------------------------------------------- flags
php_build:                                    | -> d7 = NV..DIZC (B/U left to the caller)
	moveq	#0,d7
	tst.w	d3
	bpl.s	1f
	ori.b	#0x80,d7
1:	tst.b	vflag(a6)
	beq.s	2f
	ori.b	#0x40,d7
2:	tst.b	iflag(a6)
	beq.s	3f
	ori.b	#0x04,d7
3:	tst.b	d3
	bne.s	4f
	ori.b	#0x02,d7
4:	or.b	d4,d7
	rts
plp_unpack:                                   | d7 = P
	move.b	d7,d4
	andi.b	#1,d4
	btst	#6,d7
	sne	vflag(a6)
	btst	#2,d7
	sne	iflag(a6)
	andi.b	#1,iflag(a6)
	move.w	#0x0001,d3
	btst	#1,d7
	beq.s	1f
	moveq	#0,d3
1:	btst	#7,d7
	beq.s	2f
	ori.w	#0xFF80,d3
	btst	#1,d7
	beq.s	2f
	andi.w	#0xFF00,d3
2:	rts

| ---------------------------------------------------------------- generic access (d5.w = address, d7 = value)
gen_rd:	cmpi.w	#0x8000,d5
	bcs.s	1f
	move.l	a0,-(sp)
	move.w	d5,-(sp)
	andi.w	#0x7FFF,d5
	lea	prg,a0
	move.b	0(a0,d5.w),d7
	move.w	(sp)+,d5
	move.l	(sp)+,a0
	rts
1:	cmpi.w	#0x2000,d5
	bcc.s	2f
	move.w	d5,-(sp)
	andi.w	#0x7FF,d5
	move.b	0(a5,d5.w),d7
	move.w	(sp)+,d5
	rts
2:	cmpi.w	#0x4000,d5
	bcc.s	3f
	move.w	d5,d7
	andi.w	#7,d7
	cmpi.b	#2,d7
	beq	io_rd_2002
	cmpi.b	#4,d7
	beq	io_rd_2004
	cmpi.b	#7,d7
	beq	io_rd_2007
	move.b	#0x20,d7
	rts
3:	cmpi.w	#0x4015,d5
	beq	io_rd_4015
	cmpi.w	#0x4016,d5
	beq	io_rd_4016
	cmpi.w	#0x4017,d5
	beq	io_rd_4017
	move.w	d5,d7
	lsr.w	#8,d7
	rts
gen_wr:	cmpi.w	#0x2000,d5
	bcs	9f
	cmpi.w	#0x4000,d5
	bcc.s	3f
	move.w	d5,-(sp)
	andi.w	#7,d5
	add.w	d5,d5
	add.w	d5,d5
	jsr	ppu_wtab(pc,d5.w)
	move.w	(sp)+,d5
	rts
ppu_wtab:
	bra.w	io_wr_2000
	bra.w	io_wr_2001
	bra.w	9f
	bra.w	io_wr_2003
	bra.w	io_wr_2004
	bra.w	io_wr_2005
	bra.w	io_wr_2006
	bra.w	io_wr_2007
3:	cmpi.w	#0x4018,d5
	bcc.s	9f
	move.w	d5,-(sp)
	subi.w	#0x4000,d5
	move.b	d7,apu(a6,d5.w)
	move.w	(sp)+,d5
	cmpi.w	#0x4014,d5
	beq	io_wr_4014
	cmpi.w	#0x4015,d5
	beq	io_wr_4015
	cmpi.w	#0x4016,d5
	beq	io_wr_4016
	cmpi.w	#0x4003,d5
	beq	io_wr_4003
	cmpi.w	#0x4007,d5
	beq	io_wr_4007
	cmpi.w	#0x400B,d5
	beq	io_wr_400b
	cmpi.w	#0x400F,d5
	beq	io_wr_400f
9:	rts

| ---------------------------------------------------------------- PPU registers (d7 = value; keep d0-d6 a2-a6)
io_wr_2000:
	move.b	d7,ppuctrl(a6)
	move.w	#1,ppuinc(a6)
	btst	#2,d7
	beq.s	1f
	move.w	#32,ppuinc(a6)
1:	bra	apply_scroll
io_wr_2001:
	move.b	d7,ppumask(a6)
	andi.b	#0x18,d7
	beq.s	1f
	move.w	#0x8164,(a4)
	rts
1:	move.w	#0x8124,(a4)
	rts
io_wr_2003:
	move.b	d7,oamaddr(a6)
	rts
io_wr_2004:
	move.l	d5,-(sp)
	moveq	#0,d5
	move.b	oamaddr(a6),d5
	lea	oam(a6),a0
	move.b	d7,0(a0,d5.w)
	addq.b	#1,oamaddr(a6)
	move.l	(sp)+,d5
	rts
io_wr_2005:
	not.b	wlatch(a6)
	beq.s	1f
	move.b	d7,scrollx(a6)
	rts
1:	move.b	d7,scrolly(a6)
apply_scroll:                                 | plane A scroll from scrollx/y and $2000 bits 0-1
	movem.l	d0/d5,-(sp)
	moveq	#0,d0
	move.b	scrollx(a6),d0
	neg.w	d0
	move.l	#0x7C000003,(a4)              | HScroll table $FC00
	move.w	d0,(a3)
	moveq	#0,d0
	move.b	scrolly(a6),d0
	addq.w	#8,d0
	btst	#1,ppuctrl(a6)
	beq.s	1f
	addi.w	#256,d0
1:	move.l	#0x40000010,(a4)
	move.w	d0,(a3)
	movem.l	(sp)+,d0/d5
	rts
io_wr_2006:
	not.b	wlatch(a6)
	beq.s	1f
	andi.b	#0x3F,d7
	move.b	d7,ppuaddr(a6)
	rts
1:	move.b	d7,ppuaddr+1(a6)
	rts
io_rd_2002:
	move.b	ppustat(a6),d7
	andi.b	#0x7F,ppustat(a6)
	sf	wlatch(a6)
	ori.b	#0x40,d7                      | sprite 0 always "hit" (Donkey Kong does not wait on it)
	rts
io_rd_2004:
	move.l	d5,-(sp)
	moveq	#0,d5
	move.b	oamaddr(a6),d5
	lea	oam(a6),a0
	move.b	0(a0,d5.w),d7
	move.l	(sp)+,d5
	rts
io_rd_2007:                                   | buffered read: CHR from the raw CHR copy, names from the shadow
	movem.l	d5/a0,-(sp)
	move.w	ppuaddr(a6),d5
	move.w	ppuinc(a6),d7
	add.w	d5,d7
	andi.w	#0x3FFF,d7
	move.w	d7,ppuaddr(a6)
	cmpi.w	#0x3F00,d5
	bcc.s	8f
	move.b	rdbuf(a6),d7
	lea	chrraw,a0
	cmpi.w	#0x2000,d5
	bcs.s	1f
	bsr	nt_index
	lea	ntsh(a6),a0
1:	move.b	0(a0,d5.w),rdbuf(a6)
	movem.l	(sp)+,d5/a0
	rts
8:	andi.w	#0x1F,d5
	lea	palsh(a6),a0
	move.b	0(a0,d5.w),d7
	movem.l	(sp)+,d5/a0
	rts
nt_index:                                     | d5 = $2000-$3EFF address -> shadow offset (2 KB)
	move.w	d5,-(sp)
	andi.w	#0x3FF,d5
	btst	#(MIRBIT-8),0(sp)
	beq.s	1f
	ori.w	#0x400,d5
1:	addq.l	#2,sp
	rts

io_wr_2007:
	movem.l	d0-d2/d5/a0,-(sp)
	move.w	ppuaddr(a6),d5
	move.w	ppuinc(a6),d0
	add.w	d5,d0
	andi.w	#0x3FFF,d0
	move.w	d0,ppuaddr(a6)
	cmpi.w	#0x3F00,d5
	bcc	wr_pal
	cmpi.w	#0x2000,d5
	bcs	9f                            | CHR-ROM: ignore
	btst	#MIRBIT,d5
	sne	d2                            | d2.b = half ($FF = second)
	andi.w	#0x3FF,d5
	lea	ntsh(a6),a0
	tst.b	d2
	beq.s	1f
	lea	0x400(a0),a0
1:	move.b	d7,0(a0,d5.w)
	cmpi.w	#0x3C0,d5
	bcc	wr_attr
	bsr	nt_entry                      | d0.w = plane word for cell d5 (a0 = half base)
	bsr	vram_cell
	move.w	d0,(a3)
9:	movem.l	(sp)+,d0-d2/d5/a0
	rts
vram_cell:                                    | set the VDP write address for cell d5 of half d2
	movem.l	d5,-(sp)
	add.w	d5,d5
	tst.b	d2
	beq.s	1f
	addi.w	#0x800,d5
1:	addi.w	#0x4000,d5                    | $C000 & $3FFF = $0000 ... plane at $C000: (addr & $3FFF) | $4000, top bits 3
	swap	d5
	move.w	#3,d5
	move.l	d5,(a4)
	movem.l	(sp)+,d5
	rts
nt_entry:                                     | d5 = cell 0..959, a0 = half base -> d0.w entry
	movem.l	d1/d5,-(sp)
	moveq	#0,d0
	move.b	0(a0,d5.w),d0
	btst	#4,ppuctrl(a6)
	beq.s	1f
	addi.w	#256,d0
1:	move.w	d5,d1                         | attribute byte: $3C0 + (row>>2)*8 + (col>>2)
	lsr.w	#7,d1
	lsl.w	#3,d1
	andi.w	#0x1F,d5
	lsr.w	#2,d5
	add.w	d5,d1
	lea	0x3C0(a0),a1
	move.b	0(a1,d1.w),d1
	move.w	6(sp),d5                      | the cell again (saved d5 low word)
	btst	#6,d5                         | row bit 1 -> shift 4
	beq.s	2f
	lsr.b	#4,d1
2:	btst	#1,d5                         | col bit 1 -> shift 2
	beq.s	3f
	lsr.b	#2,d1
3:	andi.w	#3,d1
	ror.w	#3,d1                         | palette -> bits 13-14
	or.w	d1,d0
	movem.l	(sp)+,d1/d5
	rts
wr_attr:                                      | rewrite the 4x4 cells under attribute byte d5 ($3C0..$3FF)
	subi.w	#0x3C0,d5
	move.w	d5,d1
	andi.w	#7,d1
	lsl.w	#2,d1                         | col0
	lsr.w	#3,d5
	lsl.w	#7,d5                         | row0*32
	add.w	d1,d5                         | first cell
	moveq	#3,d1
1:	cmpi.w	#960,d5
	bcc.s	3f
	bsr	vram_cell
	moveq	#3,d7
2:	bsr	nt_entry
	move.w	d0,(a3)
	addq.w	#1,d5
	dbra	d7,2b
	addi.w	#28,d5
	dbra	d1,1b
3:	movem.l	(sp)+,d0-d2/d5/a0
	rts
wr_pal:	andi.w	#0x1F,d5
	lea	palsh(a6),a0
	move.b	d7,0(a0,d5.w)
	move.w	d5,d0
	andi.w	#3,d0
	bne.s	1f
	andi.w	#0x0F,d5
	bne	9b                            | $3F04/8/C: not shown
	move.l	#0xC0000000,(a4)              | backdrop = CRAM 0
	bra.s	2f
1:	move.w	d5,d0                         | CRAM index = p*16 + s*4 + c
	andi.w	#0x0C,d0
	lsl.w	#2,d0
	btst	#4,d5
	beq.s	3f
	addq.w	#4,d0
3:	move.w	d5,d1
	andi.w	#3,d1
	add.w	d1,d0
	add.w	d0,d0
	swap	d0
	clr.w	d0
	ori.l	#0xC0000000,d0
	move.l	d0,(a4)
2:	moveq	#0x3F,d0
	and.b	d7,d0
	add.w	d0,d0
	lea	nescol,a0
	move.w	0(a0,d0.w),(a3)
	bra	9b

| ---------------------------------------------------------------- sprites ($4014)
io_wr_4014:                                   | 64 NES sprites -> the SAT (Y+121, link, attr|tile, X+128)
	movem.l	d0-d5/a0,-(sp)
	andi.w	#7,d7
	lsl.w	#8,d7
	lea	0(a5,d7.w),a0
	lea	sprattr,a1
	move.l	#0x78000003,(a4)              | SAT at $F800
	move.w	#512,d5                       | sprite pattern base: 512 + (bit3 ? 256 : 0)
	btst	#3,ppuctrl(a6)
	beq.s	1f
	addi.w	#256,d5
1:	moveq	#1,d4
	moveq	#62,d3
	moveq	#0,d0
	moveq	#0,d1
	moveq	#121,d2
2:	move.b	(a0)+,d0                      | Y
	add.w	d2,d0
	move.w	d0,(a3)
	move.w	d4,(a3)                       | size 1x1, link
	addq.w	#1,d4
	move.b	(a0)+,d0                      | tile
	add.w	d5,d0
	move.b	(a0)+,d1                      | attributes
	add.w	d1,d1
	or.w	0(a1,d1.w),d0
	move.w	d0,(a3)
	moveq	#0,d0
	moveq	#0,d1
	move.b	(a0)+,d0                      | X
	addi.w	#128,d0
	move.w	d0,(a3)
	moveq	#0,d0
	dbra	d3,2b
	move.b	(a0)+,d0                      | the last one links to 0
	add.w	d2,d0
	move.w	d0,(a3)
	move.w	#0,(a3)
	moveq	#0,d0
	move.b	(a0)+,d0
	add.w	d5,d0
	move.b	(a0)+,d1
	add.w	d1,d1
	or.w	0(a1,d1.w),d0
	move.w	d0,(a3)
	moveq	#0,d0
	move.b	(a0)+,d0
	addi.w	#128,d0
	move.w	d0,(a3)
	movem.l	(sp)+,d0-d5/a0
	rts

| ---------------------------------------------------------------- pad ($4016)
read_pad:                                     | Genesis pad 1 -> padnes (A=C, B=B, Select=A, Start=Start)
	lea	0xA10003,a0
	move.b	#0x40,(a0)
	nop
	nop
	move.b	(a0),d0                       | TH=1: ..CBRLDU
	move.b	#0x00,(a0)
	nop
	nop
	move.b	(a0),d1                       | TH=0: ..SA00DU
	move.b	#0x40,(a0)
	not.b	d0
	not.b	d1
	moveq	#0,d2
	btst	#5,d0
	beq.s	1f
	bset	#0,d2                         | A <- C
1:	btst	#4,d0
	beq.s	2f
	bset	#1,d2                         | B <- B
2:	btst	#4,d1
	beq.s	3f
	bset	#2,d2                         | Select <- A
3:	btst	#5,d1
	beq.s	4f
	bset	#3,d2                         | Start
4:	btst	#0,d0
	beq.s	5f
	bset	#4,d2
5:	btst	#1,d0
	beq.s	6f
	bset	#5,d2
6:	btst	#2,d0
	beq.s	7f
	bset	#6,d2
7:	btst	#3,d0
	beq.s	8f
	bset	#7,d2
8:	move.b	d2,padnes(a6)
	rts
io_wr_4016:
	btst	#0,d7
	beq.s	1f
	move.b	padnes(a6),padsh(a6)
1:	rts
io_rd_4016:
	move.l	d5,-(sp)
	move.b	padsh(a6),d5
	moveq	#1,d7
	and.b	d5,d7
	lsr.b	#1,d5
	bset	#7,d5
	move.b	d5,padsh(a6)
	move.l	(sp)+,d5
	ori.b	#0x40,d7
	rts
io_rd_4017:
	move.b	#0x40,d7
	rts

| ---------------------------------------------------------------- APU -> PSG
lentab:	.byte	10,254,20,2,40,4,80,6,160,8,60,10,14,12,26,14,12,16,24,18,48,20,96,22,192,24,72,26,16,28,32,30
	.even
io_wr_4003:
	move.b	d7,apu+3(a6)
	move.l	d0,-(sp)
	moveq	#0,d0
	bra.s	len_load
io_wr_4007:
	move.b	d7,apu+7(a6)
	move.l	d0,-(sp)
	moveq	#8,d0
	bra.s	len_load
io_wr_400b:
	move.b	d7,apu+11(a6)
	move.l	d0,-(sp)
	moveq	#16,d0
	move.b	apu+8(a6),d7
	andi.b	#0x7F,d7
	move.b	d7,sq+3+16(a6)
	move.b	apu+11(a6),d7
	bra.s	len_load
io_wr_400f:
	move.b	d7,apu+15(a6)
	move.l	d0,-(sp)
	moveq	#24,d0
len_load:                                     | d0 = channel*8, d7 = the length/period-high write
	move.l	a0,-(sp)
	lea	sq(a6,d0.w),a0
	move.b	#15,1(a0)                     | envelope restart
	clr.b	2(a0)
	lsr.b	#3,d7
	andi.w	#0x1F,d7
	move.l	d1,-(sp)
	move.w	d0,d1
	lsr.w	#3,d1
	btst	d1,apu+0x15(a6)
	beq.s	1f
	move.b	lentab(pc,d7.w),(a0)
1:	move.l	(sp)+,d1
	move.l	(sp)+,a0
	move.l	(sp)+,d0
	rts
io_wr_4015:
	move.b	d7,apu+0x15(a6)
	btst	#0,d7
	bne.s	1f
	clr.b	sq+0(a6)
1:	btst	#1,d7
	bne.s	2f
	clr.b	sq+8(a6)
2:	btst	#2,d7
	bne.s	3f
	clr.b	sq+16(a6)
3:	btst	#3,d7
	bne.s	4f
	clr.b	sq+24(a6)
4:	rts
io_rd_4015:
	moveq	#0,d7
	tst.b	sq+0(a6)
	beq.s	1f
	bset	#0,d7
1:	tst.b	sq+8(a6)
	beq.s	2f
	bset	#1,d7
2:	tst.b	sq+16(a6)
	beq.s	3f
	bset	#2,d7
3:	tst.b	sq+24(a6)
	beq.s	4f
	bset	#3,d7
4:	rts

| once a frame: length counters (2 half-frame clocks), envelopes (4 quarter-frame clocks), then the PSG
apu_frame:
	lea	PSG,a1
	moveq	#0,d6                         | channel*8
	moveq	#0,d5                         | apu register offset (channel*4)
ch_loop:
	lea	sq(a6,d6.w),a0
	move.b	apu(a6,d5.w),d4               | reg 0 of the channel
	| length counter
	cmpi.w	#16,d6
	bne.s	1f
	btst	#7,d4                         | triangle: halt = control bit 7
	bra.s	2f
1:	btst	#5,d4
2:	bne.s	3f
	moveq	#0,d0
	move.b	(a0),d0
	subq.w	#2,d0
	bpl.s	4f
	moveq	#0,d0
4:	move.b	d0,(a0)
3:	cmpi.w	#16,d6
	beq	tri
	| envelope: 4 clocks of divider P+1
	moveq	#15,d1
	and.b	d4,d1                         | P (or the constant volume)
	btst	#4,d4
	bne.s	env_done                      | constant volume: d1 = volume
	moveq	#3,d2
5:	addq.b	#1,2(a0)
	move.b	2(a0),d0
	subq.b	#1,d0
	cmp.b	d1,d0
	bcs.s	7f
	clr.b	2(a0)
	tst.b	1(a0)
	beq.s	6f
	subq.b	#1,1(a0)
	bra.s	7f
6:	btst	#5,d4                         | loop
	beq.s	7f
	move.b	#15,1(a0)
7:	dbra	d2,5b
	move.b	1(a0),d1
env_done:
	tst.b	(a0)
	bne.s	1f
	moveq	#0,d1
1:	cmpi.w	#24,d6
	beq	noise
	| pulse: period from regs 2/3
	moveq	#7,d0
	and.b	apu+3(a6,d5.w),d0
	lsl.w	#8,d0
	move.b	apu+2(a6,d5.w),d0
	cmpi.w	#8,d0
	bcc.s	2f
	moveq	#0,d1
2:	addq.w	#1,d0                         | PSG N = t + 1
	bra	tone_out
tri:	moveq	#0,d1
	btst	#7,d4
	bne.s	1f
	tst.b	3(a0)
	beq.s	1f
	subq.b	#4,3(a0)
	bcc.s	1f
	clr.b	3(a0)
1:	tst.b	(a0)
	beq.s	2f
	tst.b	3(a0)
	beq.s	2f
	moveq	#12,d1                        | triangle: fixed level
2:	moveq	#7,d0
	and.b	apu+11(a6),d0
	lsl.w	#8,d0
	move.b	apu+10(a6),d0
	addq.w	#1,d0
	add.w	d0,d0                         | PSG N = 2 (t + 1)
tone_out:
	cmpi.w	#0x3FF,d0
	bls.s	1f
	move.w	#0x3FF,d0
1:	move.w	d6,d2                         | PSG channel = d6/8 -> bits 5-6
	lsl.b	#2,d2
	move.b	d0,d3
	andi.b	#0x0F,d3
	or.b	d2,d3
	ori.b	#0x80,d3
	move.b	d3,(a1)
	lsr.w	#4,d0
	andi.b	#0x3F,d0
	move.b	d0,(a1)
vol_out:
	moveq	#15,d3
	sub.b	d1,d3
	move.w	d6,d2
	lsl.b	#2,d2
	or.b	d2,d3
	ori.b	#0x90,d3
	move.b	d3,(a1)
	addq.w	#8,d6
	addq.w	#4,d5
	cmpi.w	#32,d6
	bcs	ch_loop
	rts
noise:	moveq	#15,d0
	and.b	apu+14(a6),d0
	moveq	#0,d2
	cmpi.b	#5,d0
	bcs.s	1f
	moveq	#1,d2
	cmpi.b	#9,d0
	bcs.s	1f
	moveq	#2,d2
1:	ori.b	#0xE4,d2                      | white noise, rate
	cmp.b	5(a0),d2
	beq	vol_out
	move.b	d2,5(a0)
	move.b	d2,(a1)
	bra	vol_out

