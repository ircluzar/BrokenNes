/*
 * opll_oracle: a black-box driver for the Nuked-OPLL emulator (a die-derived YM2413, GPL-2.0).
 * It is OUR code: it only calls the oracle's public functions (OPLL_Reset / OPLL_Write / OPLL_Clock) and nothing of Nuked-OPLL is copied here.
 * Nuked-OPLL stays outside the repository (C:\BrokenNes-tools\sega\oracles\nuked\opll) and is linked at build time by build-oracle-opll.ps1.
 *
 * usage:  opll_oracle <script.txt> <out.bin> [--cycles] [--gap N] [--settle N]
 *   script: first line "<samples>"; then lines "<sample> <register> <value> [<clock>]": the register is written (address port, then data port) as soon after the start of YM2413 sample <sample>
 *           (a sample is 72 chip clocks = 18 calls of OPLL_Clock) as the chip accepts it: the address port is written, the data port <gap> OPLL clocks later (default 3 = 12 chip clocks, the datasheet's
 *           address-to-data time), and the next register write waits <settle> OPLL clocks after that (default 21 = 84 chip clocks, the datasheet's data-to-address time).
 *           Writes queue in script order. A register of -1 writes <value> to the address port only (to probe the write protocol); a register of -2 writes <value> to the data port only.
 *   out:    per sample, two little-endian int32: the sum over the 18 OPLL clocks of the melodic output (buffer[0]) and of the rhythm output (buffer[1]).
 *   --type N: the chip type to reset as (0 YM2413, the default; 1 VRC7; 2 YM2413B; 3 YMF281; 4 YMF281B; 5 YM2420; 6 YM2423).
 *   --cycles: out holds, per OPLL clock, the two outputs of that call (int32 each, 36 per sample), so the structure of the output can be inspected.
 *   --writelog: prints to stderr the OPLL clock at which each port write was issued.
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "opll.h"

#define MAXW 400000

int main(int argc, char **argv)
{
    if (argc < 3) { fprintf(stderr, "usage: opll_oracle <script.txt> <out.bin> [--cycles] [--gap N] [--settle N]\n"); return 2; }
    int cycles_mode = 0, gap = 3, settle = 21, writelog = 0, outclock = 7, chiptype = opll_type_ym2413;
    for (int a = 3; a < argc; a++)
    {
        if (strcmp(argv[a], "--cycles") == 0) cycles_mode = 1;
        else if (strcmp(argv[a], "--writelog") == 0) writelog = 1;
        else if (strcmp(argv[a], "--out-clock") == 0 && a + 1 < argc) outclock = atoi(argv[++a]);
        else if (strcmp(argv[a], "--type") == 0 && a + 1 < argc) chiptype = atoi(argv[++a]);   /* 0 YM2413, 1 VRC7, 2 YM2413B, 3 YMF281, 4 YMF281B, 5 YM2420, 6 YM2423 */
        else if (strcmp(argv[a], "--gap") == 0 && a + 1 < argc) gap = atoi(argv[++a]);
        else if (strcmp(argv[a], "--settle") == 0 && a + 1 < argc) settle = atoi(argv[++a]);
    }
    FILE *in = fopen(argv[1], "r");
    if (!in) { fprintf(stderr, "cannot open %s\n", argv[1]); return 2; }
    long samples = 0;
    if (fscanf(in, "%ld", &samples) != 1 || samples <= 0) { fprintf(stderr, "bad script header\n"); return 2; }
    long *wsample = (long *)malloc(sizeof(long) * MAXW), *wclk = (long *)calloc(MAXW, sizeof(long));
    int *wreg = (int *)malloc(sizeof(int) * MAXW), *wval = (int *)malloc(sizeof(int) * MAXW);
    int writes = 0;
    long s; int r, v;
    char line[256];
    while (writes < MAXW && fgets(line, sizeof line, in))
    {
        long clk = 0;
        int got = sscanf(line, "%ld %d %d %ld", &s, &r, &v, &clk);   /* an optional fourth number: the OPLL clock within the sample at which the write is first allowed */
        if (got < 3) continue;
        wsample[writes] = s; wreg[writes] = r; wval[writes] = v; wclk[writes] = got == 4 ? clk : 0; writes++;
    }
    fclose(in);

    opll_t chip;
    memset(&chip, 0, sizeof(chip));
    OPLL_Reset(&chip, (uint32_t)chiptype);

    size_t per = cycles_mode ? 36 : 2;
    int32_t *out = (int32_t *)malloc(sizeof(int32_t) * per * (size_t)samples);
    int next = 0;
    long busy_until = 0;          /* the OPLL clock before which the chip does not accept another register write */
    long data_at = -1;            /* the clock at which the pending data-port write goes in */
    int data_val = 0, data_port = 0;
    for (long i = 0; i < samples; i++)
    {
        int32_t sum_m = 0, sum_r = 0;
        for (int c = 0; c < 18; c++)
        {
            long g = i * 18 + c;
            if (data_at >= 0 && g >= data_at) { OPLL_Write(&chip, 1, (uint8_t)data_val); if (writelog) fprintf(stderr, "clock %ld data %02X\n", g, data_val); data_at = -1; }
            if (data_at < 0 && next < writes && g >= wsample[next] * 18 + wclk[next] && g >= busy_until)
            {
                if (wreg[next] == -1) { OPLL_Write(&chip, 0, (uint8_t)wval[next]); busy_until = g + settle; }
                else if (wreg[next] == -2) { OPLL_Write(&chip, 1, (uint8_t)wval[next]); busy_until = g + settle; }
                else
                {
                    OPLL_Write(&chip, 0, (uint8_t)wreg[next]);
                    if (writelog) fprintf(stderr, "clock %ld addr %02X\n", g, wreg[next]);
                    data_at = g + gap; data_val = wval[next]; busy_until = g + gap + settle;
                }
                next++;
            }
            int32_t buf[2] = { 0, 0 };
            OPLL_Clock(&chip, buf);
            if (c == outclock) { sum_m = buf[0]; sum_r = buf[1]; }
            if (cycles_mode) { out[i * 36 + c * 2] = buf[0]; out[i * 36 + c * 2 + 1] = buf[1]; }
        }
        if (!cycles_mode) { out[i * 2] = sum_m; out[i * 2 + 1] = sum_r; }
    }
    if (writelog) fprintf(stderr, "final state: kon0=%d fnum0=%d block0=%d inst0=%d vol0=%d rhythm=%d cycles=%u\n", chip.kon[0], chip.fnum[0], chip.block[0], chip.inst[0], chip.vol[0], chip.rhythm, (unsigned)chip.cycles);
    FILE *f = fopen(argv[2], "wb");
    if (!f) { fprintf(stderr, "cannot write %s\n", argv[2]); return 2; }
    fwrite(out, sizeof(int32_t), per * (size_t)samples, f);
    fclose(f);
    free(out); free(wsample); free(wclk); free(wreg); free(wval);
    return 0;
}
