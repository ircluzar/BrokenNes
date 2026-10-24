; The test cartridge's names: the SNES's and WideMess's registers (spec sections 1-4), work RAM, the results table (README.md).

; ---- SNES registers
.DEFINE INIDISP  $2100
.DEFINE OBSEL    $2101
.DEFINE OAMADDL  $2102
.DEFINE OAMADDH  $2103
.DEFINE OAMDATA  $2104
.DEFINE BGMODE   $2105
.DEFINE MOSAIC   $2106
.DEFINE BG1SC    $2107
.DEFINE BG2SC    $2108
.DEFINE BG3SC    $2109
.DEFINE BG4SC    $210A
.DEFINE BG12NBA  $210B
.DEFINE BG34NBA  $210C
.DEFINE BG1HOFS  $210D
.DEFINE BG1VOFS  $210E
.DEFINE BG2HOFS  $210F
.DEFINE BG2VOFS  $2110
.DEFINE BG3HOFS  $2111
.DEFINE BG3VOFS  $2112
.DEFINE VMAIN    $2115
.DEFINE VMADDL   $2116
.DEFINE VMADDH   $2117
.DEFINE VMDATAL  $2118
.DEFINE VMDATAH  $2119
.DEFINE M7SEL    $211A
.DEFINE M7A      $211B
.DEFINE M7B      $211C
.DEFINE M7C      $211D
.DEFINE M7D      $211E
.DEFINE M7X      $211F
.DEFINE M7Y      $2120
.DEFINE CGADD    $2121
.DEFINE CGDATA   $2122
.DEFINE W12SEL   $2123
.DEFINE W34SEL   $2124
.DEFINE WOBJSEL  $2125
.DEFINE WH0      $2126
.DEFINE WH1      $2127
.DEFINE WH2      $2128
.DEFINE WH3      $2129
.DEFINE WBGLOG   $212A
.DEFINE WOBJLOG  $212B
.DEFINE TM       $212C
.DEFINE TS       $212D
.DEFINE TMW      $212E
.DEFINE TSW      $212F
.DEFINE CGWSEL   $2130
.DEFINE CGADSUB  $2131
.DEFINE COLDATA  $2132
.DEFINE SETINI   $2133
.DEFINE STAT77   $213E
.DEFINE APUIO0   $2140
.DEFINE APUIO1   $2141
.DEFINE APUIO2   $2142
.DEFINE APUIO3   $2143
.DEFINE WMDATA   $2180
.DEFINE WMADDL   $2181
.DEFINE WMADDM   $2182
.DEFINE WMADDH   $2183
.DEFINE NMITIMEN $4200
.DEFINE MDMAEN   $420B
.DEFINE HDMAEN   $420C
.DEFINE MEMSEL   $420D
.DEFINE RDNMI    $4210
.DEFINE HVBJOY   $4212
.DEFINE RDDIVL   $4214
.DEFINE JOY1     $4218
.DEFINE DMAP0    $4300
.DEFINE BBAD0    $4301
.DEFINE A1T0     $4302
.DEFINE A1B0     $4304
.DEFINE DAS0     $4305

; ---- WideMess (spec section 1: CPU side, $4220-$422F)
.DEFINE WMUNLOCK $4220
.DEFINE WMID     $4221
.DEFINE WMVER    $4222
.DEFINE WMREV    $4223
.DEFINE WMFEAT   $4224
.DEFINE WMSPEED  $4225
.DEFINE WMDMA    $4226
; the picture (section 3)
.DEFINE WMOBJ     $21A0
.DEFINE WMOBJBASE $21A1
.DEFINE WMOAMADDL $21A2
.DEFINE WMOAMADDH $21A3
.DEFINE WMOAMDATA $21A4
.DEFINE WMOBJSTAT $21A5
.DEFINE WMOBJPRI  $21A6
.DEFINE WMWH0     $21B0
.DEFINE WMWH1     $21B1
.DEFINE WMWH2     $21B2
.DEFINE WMWH3     $21B3
.DEFINE WMWCTL    $21B4
.DEFINE WMTS      $21B5
.DEFINE WMTMW     $21B6
.DEFINE WMTSW     $21B7
.DEFINE WMW12SEL  $21B8
.DEFINE WMWLOG14  $21BC
.DEFINE WMWLOG58  $21BD
.DEFINE WMCMATH   $21BE
.DEFINE WMCTRL    $21C0
.DEFINE WMTM      $21C1
.DEFINE WMVADDL   $21C2
.DEFINE WMVADDM   $21C3
.DEFINE WMVADDH   $21C4
.DEFINE WMVDATA   $21C5
.DEFINE WMCGADDL  $21C6
.DEFINE WMCGADDH  $21C7
.DEFINE WMCGDATA  $21C8
.DEFINE WMBGMAP   $21D0
.DEFINE WMBGCFG   $21D8
.DEFINE WMBGHOFS  $21E0
.DEFINE WMBGVOFS  $21E1
; the sound RAM port (section 4)
.DEFINE WMSADDL   $21F0
.DEFINE WMSADDM   $21F1
.DEFINE WMSADDH   $21F2
.DEFINE WMSDATA   $21F3

; string codes (Print)
.DEFINE S_WHITE  $80
.DEFINE S_GREY   $81
.DEFINE S_GREEN  $82
.DEFINE S_RED    $83
.DEFINE S_YELLOW $84
.DEFINE S_CYAN   $85
.DEFINE S_ORANGE $86
.DEFINE S_VIOLET $87
.DEFINE HEX8     $F0
.DEFINE HEX16    $F1
.DEFINE DEC16    $F2
.DEFINE DEC8     $F3
.DEFINE STATUS   $F4
.DEFINE NL       $FE
.DEFINE EOS      $FF
.DEFINE G_BLOCK  1
.DEFINE G_CHECK  2
.DEFINE G_CROSS  3
.DEFINE G_RIGHT  4
.DEFINE G_LEFT   5
.DEFINE G_UP     6
.DEFINE G_DOWN   7
.DEFINE G_NOTE   8
.DEFINE G_DOT    16
.DEFINE G_SQUARE 17

; WMFEAT bits
.DEFINE FEAT_PICTURE $01
.DEFINE FEAT_SPRITES $02
.DEFINE FEAT_SOUND   $04
.DEFINE FEAT_FASTCPU $08
.DEFINE FEAT_BIGWRAM $10
.DEFINE FEAT_FASTDMA $20

; the pad ($4218 as 16 bits)
.DEFINE PAD_B      $8000
.DEFINE PAD_Y      $4000
.DEFINE PAD_SELECT $2000
.DEFINE PAD_START  $1000
.DEFINE PAD_UP     $0800
.DEFINE PAD_DOWN   $0400
.DEFINE PAD_LEFT   $0200
.DEFINE PAD_RIGHT  $0100
.DEFINE PAD_A      $0080
.DEFINE PAD_X      $0040
.DEFINE PAD_L      $0020
.DEFINE PAD_R      $0010

; ---- video RAM: the font (64 KB unit 1), the title and menu tiles (unit 2), the maps from $60000 (BG n+1 at $60000 + n x $1000)
.DEFINE VR_FONT   $10000
.DEFINE VR_UI     $20000
.DEFINE VR_MAPS   $60000
.DEFINE MAPREG    $C0           ; WMBGnMAP for $60000 (x 2 KB)

; ---- the screens
.DEFINE SC_TITLE   0
.DEFINE SC_MENU    1
.DEFINE SC_TEST1   2            ; tests 1-8 are screens 2-9
.DEFINE SC_RUNALL  10
.DEFINE SC_SUMMARY 11
.DEFINE SC_NA      12
.DEFINE SC_REF     13           ; a reference picture (tests 2-4)
.DEFINE SC_COUNT   14

; the cartridge's state (RES_STATE)
.DEFINE ST_BOOT      0
.DEFINE ST_MENU      1
.DEFINE ST_RUNALL    2
.DEFINE ST_DONE      3
.DEFINE ST_NOTWMS    4
.DEFINE ST_NOPICTURE 5

; ---- work RAM, bank $00 / $7E (direct page from $0000)
.ENUM $0000
  nmiflag     dw                ; + 1 every NMI (the speed test's loops wait for it)
  counter     dw                ; the speed test's loop counter
  dmabytes    dw
  nmijump     dw                ; the NMI handler: NmiFull, or NmiCount while the speed test counts
  vbhook      dw                ; the screen's vblank work
  vbready     db                ; 1: the main program is waiting for the NMI's vblank work
  textdirty   db
  paneldirty  db
  screen      db
  screen2     dw                ; screen x 2
  runall      db                ; 1: running every self-check unattended
  padnow      dw
  padprev     dw
  padnew      dw
  pad         dw                ; pressed since the last frame (TakePad)
  padheld     dw
  tmp         dsb 8
  ptr         dsb 4             ; 24-bit pointers
  ptr2        dsb 4
  strptr      dsb 4
  valaddr     dw
  linestart   dw
  textstyle   db
  stylo       db
  styhi       db
  numval      dw
  places      db
  started     db
  digit       dw
  box_r0      dw
  box_c0      dw
  box_r1      dw
  box_c1      dw
  prow        dw
  pcount      dw
  sel         dw                ; the menu's selection
  blink       dw
  scroll      dsb 16            ; 8 layers' horizontal scroll (sub-pixel counters)
  page        dw                ; a test's page
  sub         dw                ; a test's choice within its page
  errs        dw
  errs2       dw
  loop0       dw
  loop1       dw
  loop2       dw
  bankn       dw
  hits        dw
  checks      db                ; checks made / failed by the test that runs
  fails       db
  testn       db                ; the test running (1-8)
  vdst        dsb 4             ; a video RAM byte address (19 bits)
  dlen        dw                ; a DMA's length (0 = 65,536)
  uikind      db                ; the backdrop SetupUI drew (0 the menu, 1 the title with the logo)
  nmitmp      dsb 8             ; the NMI's own scratch (it interrupts the main program's use of tmp)
  runstep     dw                ; the run-all's position in RunAllOrder
  waitn       dw
  patoff      dw                ; the memory test: where in the pattern a bank starts
  passes      dw
  item        dw
  wbank       dw                ; a work-RAM bank (0-15) / a video RAM unit / a sound RAM bank
  snddirty    db                ; 1: the memory test wrote over the sound test's samples
  row         dw                ; (words: their high bytes stay 0, so 16-bit index loads read them whole)
  col         dw
  st          dw
  pgidx       dw                ; tests 2-4: the page (its place in the test's list), how many, its number, x 2, its variant, still
  pgcount     dw
  pgpage      dw
  pgcur       dw
  pgvariant   dw
  pgstill     dw
  scrptr      dsb 4             ; the script being played
  t1dirty     db                ; test 1: the legend changed
  nocount     db                ; 1: Judge judges again (a live test's later rounds) without counting; a failure fails the test
  saved       dw
  scratch     dsb 32
.ENDE

; the results table (README.md: "The results table"), $7E:1000 (also $00:1000)
.DEFINE RES          $1000
.DEFINE RES_MAGIC    $1000      ; "WMTC"
.DEFINE RES_VERSION  $1004      ; 1
.DEFINE RES_CARTVER  $1005      ; 1, 0
.DEFINE RES_STATE    $1007
.DEFINE RES_FRAME    $1008      ; 16 bits, + 1 every NMI
.DEFINE RES_SCREEN   $100A
.DEFINE RES_RUNS     $100B      ; run-alls finished
.DEFINE RES_CONSOLE  $100C      ; 1: a WideMess answered
.DEFINE RES_BOOTPAD  $100D      ; 16 bits: the pad at power-on
.DEFINE RES_WMID     $1010
.DEFINE RES_WMVER    $1011
.DEFINE RES_WMREV    $1012
.DEFINE RES_WMFEAT   $1013
.DEFINE RES_FEAT     $1014      ; WMFEAT AND the cartridge's mask: what the tests use
.DEFINE RES_MASK     $1015
.DEFINE RES_PREID    $1016
.DEFINE RES_PRESPEED $1017
.DEFINE RES_BROKENID $1018
.DEFINE RES_RESERVED $1019
.DEFINE RES_STATUS   $1020      ; + test - 1: each test's status ('P' 'F' 'N' 'V' 'R', 0 = not run)
.DEFINE RES_CHECKS   $1028      ; + test - 1: checks made
.DEFINE RES_FAILS    $1030      ; + test - 1: checks failed
.DEFINE RES_DETAIL   $1100      ; each test's own block: $1100 + (test - 1) x $40
.DEFINE RES_T1       $1100
.DEFINE RES_T2       $1140
.DEFINE RES_T3       $1180
.DEFINE RES_T4       $11C0
.DEFINE RES_T5       $1200
.DEFINE RES_T6       $1240
.DEFINE RES_T7       $1280
.DEFINE RES_T8       $12C0

.DEFINE TEXTBUF  $7E2000        ; BG1's map (the text), 4 KB
.DEFINE PANELBUF $7E3000        ; BG2's map (the panels), 4 KB
.DEFINE SCRATCH  $7E4000        ; 16 KB for read-backs
.DEFINE DMASINK  $7E8000        ; the speed test's DMA lands here (16 KB)
.DEFINE OAMBUF   $7EC000        ; a WideMess sprite table (2 KB)
.DEFINE HDMABUF  $7EC800        ; HDMA tables
.DEFINE SNESBUF  $7ED000        ; a SNES BG map made from the text buffer (32 x 28 entries)
