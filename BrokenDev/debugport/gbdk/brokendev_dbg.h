/*
 * The BrokenDev debug port for GBDK-2020 (Game Boy / Color), milestone D7 of docs/projects/brokendev-debugging-plan.md ("D7 settled").
 *
 * It speaks the debug-message convention bgb and Emulicious already show and GBDK ships (gbdk/emu_debug.h): "ld d,d; jr; dw $6464; dw
 * $0000; text" (an inline message) and GBDK's EMU_printf (Emulicious's printf message), plus the "ld b,b" source breakpoint. These are
 * instructions that do nothing on the hardware (jr skips the text), so no I/O register is touched. BrokenNes reads them while its debugger
 * or dev mode is on; bgb and Emulicious print the same lines (the timers' lines carry bgb's %ZEROCLKS% / %LASTCLKS%, so their profiler times
 * them too).
 *
 * Every macro compiles to NOTHING unless BROKENDEV_DEBUG is defined (lcc -DBROKENDEV_DEBUG): a release build is the same source without
 * the define, and BrokenDev's proof shows its ROM is byte-identical to the source with every DBG_ line taken out.
 *
 *   DBG_PRINT("room loaded")                  a text (bgb fields work: "A=%A% HL=%HL%")
 *   DBG_PRINTF("room %u of %u", room, n)      EMU_printf: %u %d %x (16 bits), %hu %hd %hx (8 bits: cast to the 8-bit type), %c (not cast), %s
 *   DBG_PRINT_U16("boot", boot_count)         "boot 3"
 *   DBG_ASSERT(hp <= max_hp)                  on failure: "@assert line 120: hp <= max_hp" and ld b,b (BrokenNes's debugger stops at it, with
 *                                             the function's symbol; verify fails). The condition's text goes into the assembler: no " / % in it.
 *   DBG_MARKER("boss phase 2")                a marker: movies, bug captures, the divergence report
 *   DBG_TIMER_START("draw_hud") ... DBG_TIMER_STOP("draw_hud")    cycles per call, worst and per frame in BrokenNes's profile
 *   DBG_BREAK()                               ld b,b: the debugger stops here
 *
 * One message per source line (the assembler macro is named after __LINE__, as GBDK's own EMU_MESSAGE).
 */
#ifndef BROKENDEV_DBG_H
#define BROKENDEV_DBG_H

#ifdef BROKENDEV_DEBUG

#include <gbdk/emu_debug.h>

#define BDBG_STR2(x) #x
#define BDBG_STR(x) BDBG_STR2(x)
#define BDBG_NAME(A) BDBG_NAME1(A)
#define BDBG_NAME1(A) BDBGMSG##A

/* an inline message of up to four string literals (each its own .ascii line: sdas takes one string per directive); the parameters have
   names no register has (a parameter called d would replace the d of "ld d, d") */
#define BDBG_MSG4(name, bdbg_p1, bdbg_p2, bdbg_p3, bdbg_p4) \
__asm \
.MACRO name t1, t2, t3, t4, ?llbl \
  ld d, d \
  jr llbl \
  .dw 0x6464 \
  .dw 0x0000 \
  .ascii t1 \
  .ascii t2 \
  .ascii t3 \
  .ascii t4 \
llbl: \
.ENDM \
name ^/bdbg_p1/, ^/bdbg_p2/, ^/bdbg_p3/, ^/bdbg_p4/ \
__endasm
#define BDBG_MSG(bdbg_p1, bdbg_p2, bdbg_p3, bdbg_p4) BDBG_MSG4(BDBG_NAME(__LINE__), bdbg_p1, bdbg_p2, bdbg_p3, bdbg_p4)

#define DBG_PRINT(msg)            BDBG_MSG(msg, "", "", "")
#define DBG_PRINTF(...)           EMU_printf(__VA_ARGS__)
#define DBG_PRINT_U16(msg, value) EMU_printf(msg " %u", (uint16_t)(value))
#define DBG_ASSERT(cond)          do { if (!(cond)) { BDBG_MSG("@assert line ", BDBG_STR(__LINE__), ": ", #cond); __asm__("ld b, b"); } } while (0)
#define DBG_MARKER(name)          BDBG_MSG("@mark ", name, "", "")
#define DBG_TIMER_START(name)     BDBG_MSG("@timer+ ", name, " %ZEROCLKS%", "")
#define DBG_TIMER_STOP(name)      BDBG_MSG("@timer- ", name, " %-8+LASTCLKS%", "")
#define DBG_BREAK()               __asm__("ld b, b")

#else

#define DBG_PRINT(msg)            ((void)0)
#define DBG_PRINTF(...)           ((void)0)
#define DBG_PRINT_U16(msg, value) ((void)0)
#define DBG_ASSERT(cond)          ((void)0)
#define DBG_MARKER(name)          ((void)0)
#define DBG_TIMER_START(name)     ((void)0)
#define DBG_TIMER_STOP(name)      ((void)0)
#define DBG_BREAK()               ((void)0)

#endif
#endif
