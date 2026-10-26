/*---------------------------------------------------------------------------------
    The BrokenDev debug port for PVSnesLib (816-tcc): the SNES and the WideMess, milestone D7 of
    docs/projects/brokendev-debugging-plan.md ("D7 settled").

    BrokenNes listens to $4230-$423F (banks $00-$3F / $80-$BF) while its debugger or dev mode is on: the range the WideMess spec reserved,
    which no SNES chip or coprocessor decodes, so on real hardware and other emulators the writes go nowhere. A game only writes there.

    Every macro compiles to NOTHING unless BROKENDEV_DEBUG is defined (816-tcc -DBROKENDEV_DEBUG; the WideMess devkit: wmsbuild.ps1
    -Define BROKENDEV_DEBUG, or brokendev build's dev profile): a release build is the same source without the define, and BrokenDev's
    proof shows its ROM is byte-identical to the build before the calls were written.

      DBG_PRINT("room loaded")
      DBG_PRINT1("room %u loaded", room)          one value, 16 bits: %u %d %x %X (widths and 0 as in C; %hu etc. are 8-bit and
      DBG_PRINT2("x=%u y=%u", x, y)               would read half a value: pass 16-bit values with %u)
      DBG_ASSERT(hp <= max_hp)                    on failure: "line 120: hp <= max_hp" at the function's symbol; the debugger stops;
                                                  verify fails
      DBG_MARKER("boss phase 2")                  a marker: movies, bug captures, the divergence report
      DBG_TIMER_NAME(1, "draw_hud")               once; then DBG_TIMER_START(1) ... DBG_TIMER_STOP(1): one store each
      DBG_BREAK()                                 the debugger stops here
---------------------------------------------------------------------------------*/
#ifndef BROKENDEV_DBG_H
#define BROKENDEV_DBG_H

#ifdef BROKENDEV_DEBUG

#define BDBG_DATA  (*(volatile unsigned char *)0x4230)
#define BDBG_CMD   (*(volatile unsigned char *)0x4232)
#define BDBG_START (*(volatile unsigned char *)0x4234)
#define BDBG_STOP  (*(volatile unsigned char *)0x4236)
#define BDBG_STR2(x) #x
#define BDBG_STR(x) BDBG_STR2(x)

static void bdbg_text(const char *s) { while (*s) BDBG_DATA = (unsigned char)*s++; }
static void bdbg_value(unsigned short v) { BDBG_DATA = (unsigned char)v; BDBG_DATA = (unsigned char)(v >> 8); }
static void bdbg_say(unsigned char cmd, const char *s) { bdbg_text(s); BDBG_CMD = cmd; }
static void bdbg_say1(const char *s, unsigned short a) { bdbg_text(s); BDBG_DATA = 0; bdbg_value(a); BDBG_CMD = 0x01; }
static void bdbg_say2(const char *s, unsigned short a, unsigned short b) { bdbg_text(s); BDBG_DATA = 0; bdbg_value(a); bdbg_value(b); BDBG_CMD = 0x01; }
static void bdbg_name(unsigned char id, const char *s) { BDBG_DATA = id; bdbg_text(s); BDBG_CMD = 0x07; }

#define DBG_PRINT(msg)           bdbg_say(0x01, msg)
#define DBG_PRINT1(msg, a)       bdbg_say1(msg, (unsigned short)(a))
#define DBG_PRINT2(msg, a, b)    bdbg_say2(msg, (unsigned short)(a), (unsigned short)(b))
#define DBG_ASSERT(cond)         do { if (!(cond)) bdbg_say(0x02, "line " BDBG_STR(__LINE__) ": " #cond); } while (0)
#define DBG_MARKER(name)         bdbg_say(0x03, name)
#define DBG_TIMER_NAME(id, name) bdbg_name((unsigned char)(id), name)
#define DBG_TIMER_START(id)      (BDBG_START = (unsigned char)(id))
#define DBG_TIMER_STOP(id)       (BDBG_STOP = (unsigned char)(id))
#define DBG_BREAK()              (BDBG_CMD = 0x06)

#else

#define DBG_PRINT(msg)           ((void)0)
#define DBG_PRINT1(msg, a)       ((void)0)
#define DBG_PRINT2(msg, a, b)    ((void)0)
#define DBG_ASSERT(cond)         ((void)0)
#define DBG_MARKER(name)         ((void)0)
#define DBG_TIMER_NAME(id, name) ((void)0)
#define DBG_TIMER_START(id)      ((void)0)
#define DBG_TIMER_STOP(id)       ((void)0)
#define DBG_BREAK()              ((void)0)

#endif
#endif
