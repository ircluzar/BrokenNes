/*
 * psg_oracle: a black-box driver for the Nuked-PSG emulator (the Genesis VDP's SN76489, die-derived, GPL-2.0+).
 * It is OUR code: it only calls the oracle's public functions (YMPSG_Init / _Write / _Clock) and reads two fields of its state.
 * Nuked-PSG itself stays outside the repository (C:\BrokenNes-tools\sega\oracles\nuked\psg) and is linked at build time by
 * build-oracles.ps1; nothing of it is copied here.
 *
 * usage:  psg_oracle <script.txt> <out.bin>
 *          psg_oracle --levels            prints the oracle's 16 attenuation levels (volume 0 first), one per line, as measured amplitude ratios
 *   script: first line "<samples>"; then lines "<sample> <byte>": write <byte> to the chip just before PSG sample <sample> starts.
 *   out:    one byte per PSG sample (a sample is 16 input clocks): bit 3 tone 1, bit 2 tone 2, bit 1 tone 3, bit 0 noise,
 *           each the channel's raw output level (before attenuation).
 */
#include <stdio.h>
#include <stdlib.h>
#include "ympsg.h"

extern const float ympsg_vol[17];

int main(int argc, char **argv)
{
    if (argc == 2 && argv[1][0] == '-' && argv[1][1] == '-' && argv[1][2] == 'l')
    {
        for (int v = 0; v < 16; v++) printf("%.4f\n", ympsg_vol[v]);
        return 0;
    }
    if (argc < 3) { fprintf(stderr, "usage: psg_oracle <script.txt> <out.bin>\n"); return 2; }
    FILE *in = fopen(argv[1], "r");
    if (!in) { fprintf(stderr, "cannot open %s\n", argv[1]); return 2; }
    long samples = 0;
    if (fscanf(in, "%ld", &samples) != 1 || samples <= 0) { fprintf(stderr, "bad script header\n"); return 2; }
    unsigned char *out = (unsigned char *)malloc((size_t)samples);
    long wsample[65536]; int wbyte[65536]; int writes = 0;
    long s; int b;
    while (writes < 65536 && fscanf(in, "%ld %d", &s, &b) == 2) { wsample[writes] = s; wbyte[writes] = b; writes++; }
    fclose(in);

    ympsg_t chip;
    YMPSG_Init(&chip);
    int next = 0;
    for (long i = 0; i < samples; i++)
    {
        while (next < writes && wsample[next] <= i) { YMPSG_Write(&chip, (uint8_t)wbyte[next]); next++; }
        for (int c = 0; c < 16; c++) YMPSG_Clock(&chip);
        /* the raw channel levels, as the oracle's own mixer reads them */
        out[i] = (unsigned char)(((chip.sign & 14) | (chip.noise_sign_l & 1)) & 15);
    }
    FILE *f = fopen(argv[2], "wb");
    if (!f) { fprintf(stderr, "cannot write %s\n", argv[2]); return 2; }
    fwrite(out, 1, (size_t)samples, f);
    fclose(f);
    free(out);
    return 0;
}
