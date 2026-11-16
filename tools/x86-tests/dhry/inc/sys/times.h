/* Adapter for Dhrystone 2.1 (netlib dhry-c) on DOS / Open Watcom: stands in for <sys/times.h> (found first via -i=inc).
   times() reads the BIOS tick counter at 0040:006C (18.2065 Hz, the PIT's channel 0), so "-DTIMES -DHZ=18.2065" measures machine time. */
struct tms { long tms_utime; long tms_stime; long tms_cutime; long tms_cstime; };
static int times(struct tms *t) { t->tms_utime = (long)*(volatile unsigned long __far *)0x0040006CL; return 0; }
