/*
 * opn2_oracle: a black-box driver for the Nuked-OPN2 emulator (Yamaha YM3438 / YM2612, die-derived, LGPL-2.1).
 * It is OUR code: it only calls the oracle's public functions (OPN2_Reset / OPN2_SetChipType / OPN2_Write / OPN2_Read / OPN2_Clock) and reads fields of the public state struct
 * declared in ym3438.h. Nuked-OPN2 itself stays outside the repository (C:\BrokenNes-tools\sega\oracles\nuked\opn2) and is linked at build time by build-oracles.ps1; nothing of it is copied here.
 *
 * usage:  opn2_oracle <script.txt> <out.bin>
 *   script: first line "<chip type> <clocks> <flags>"
 *             chip type: bit 0 = YM2612 mode (the discrete chip), bit 1 = status readable on any port   (the library's two mode bits)
 *             clocks:    how many OPN2_Clock calls to make (24 per FM sample; one call is one prescaled chip clock = 6 input clocks)
 *             flags:     bit 0 = per-clock records (otherwise one record per sample, taken after the 24th clock of each),
 *                        bit 1 = also dump the per-slot internal state (envelope level, phase counter) after every sample
 *           then lines:  "W <clock> <port> <value>"  write <value> to port <port> (0 to 3) just before clock call number <clock> (0-based)
 *                        "R <clock> <port>"          read <port> just before clock call <clock>; the value goes to <out.bin>.reads as "<clock> <port> <value>"
 *   out:    records, little endian.
 *           per record: int16 left, int16 right (the library's mol and mor after the clock), uint8 busy, uint8 timer A flag, uint8 timer B flag, uint8 cycle counter,
 *                       then, with flag bit 1 and at the end of each sample: 24 x uint16 eg_level, 24 x uint32 pg_phase, 6 x int16 ch_out, 24 x uint16 eg_out, 24 x int16 fm_out, 24 x uint16 fm_mod, 24 x uint32 pg_inc, 4 x uint8 (lfo_am, lfo_pm, lfo_cnt, lfo_en).
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "ym3438.h"

typedef struct { long clock; int kind; int port; int value; } event_t;

int main(int argc, char **argv)
{
    if (argc < 3) { fprintf(stderr, "usage: opn2_oracle <script.txt> <out.bin>\n"); return 2; }
    FILE *in = fopen(argv[1], "r");
    if (!in) { fprintf(stderr, "cannot open %s\n", argv[1]); return 2; }
    int type = 0; long clocks = 0; int flags = 0;
    if (fscanf(in, "%d %ld %d", &type, &clocks, &flags) != 3 || clocks <= 0) { fprintf(stderr, "bad script header\n"); return 2; }
    size_t cap = 1024, n = 0;
    event_t *ev = (event_t *)malloc(cap * sizeof(event_t));
    char tag[4]; long c; int p, v;
    while (fscanf(in, "%3s", tag) == 1)
    {
        if (tag[0] == 'W') { if (fscanf(in, "%ld %d %d", &c, &p, &v) != 3) break; }
        else if (tag[0] == 'R') { if (fscanf(in, "%ld %d", &c, &p) != 2) break; v = 0; }
        else break;
        if (n == cap) { cap *= 2; ev = (event_t *)realloc(ev, cap * sizeof(event_t)); }
        ev[n].clock = c; ev[n].kind = tag[0]; ev[n].port = p; ev[n].value = v; n++;
    }
    fclose(in);

    char readsName[1024];
    snprintf(readsName, sizeof readsName, "%s.reads", argv[2]);
    FILE *out = fopen(argv[2], "wb");
    FILE *reads = fopen(readsName, "w");
    if (!out || !reads) { fprintf(stderr, "cannot write output\n"); return 2; }

    ym3438_t chip;
    OPN2_SetChipType((Bit32u)type);
    OPN2_Reset(&chip);

    size_t next = 0;
    for (long i = 0; i < clocks; i++)
    {
        while (next < n && ev[next].clock <= i)
        {
            if (ev[next].kind == 'W') OPN2_Write(&chip, (Bit32u)ev[next].port, (Bit8u)ev[next].value);
            else fprintf(reads, "%ld %d %d\n", ev[next].clock, ev[next].port, (int)OPN2_Read(&chip, (Bit32u)ev[next].port));
            next++;
        }
        Bit16s buf[2];
        OPN2_Clock(&chip, buf);
        int endOfSample = ((i + 1) % 24) == 0;
        if ((flags & 1) || endOfSample)
        {
            Bit16s lr[2] = { buf[0], buf[1] };
            Bit8u b[4] = { chip.busy, chip.timer_a_overflow_flag, chip.timer_b_overflow_flag, (Bit8u)chip.cycles };
            fwrite(lr, 2, 2, out);
            fwrite(b, 1, 4, out);
            if ((flags & 2) && endOfSample)
            {
                Bit16u lv[24]; Bit32u ph[24]; Bit16s co[6]; Bit16u eo[24]; Bit16s fo[24]; Bit16u fmd[24]; Bit32u inc[24]; Bit8u lfo[4];
                for (int s = 0; s < 24; s++) { lv[s] = chip.eg_level[s]; ph[s] = chip.pg_phase[s]; eo[s] = chip.eg_out[s]; fo[s] = chip.fm_out[s]; fmd[s] = chip.fm_mod[s]; inc[s] = chip.pg_inc[s]; }
                lfo[0] = chip.lfo_am; lfo[1] = chip.lfo_pm; lfo[2] = chip.lfo_cnt; lfo[3] = chip.lfo_en;
                for (int s = 0; s < 6; s++) co[s] = chip.ch_out[s];
                fwrite(lv, 2, 24, out); fwrite(ph, 4, 24, out); fwrite(co, 2, 6, out); fwrite(eo, 2, 24, out); fwrite(fo, 2, 24, out); fwrite(fmd, 2, 24, out); fwrite(inc, 4, 24, out); fwrite(lfo, 1, 4, out);
            }
        }
    }
    fclose(out);
    fclose(reads);
    free(ev);
    return 0;
}
