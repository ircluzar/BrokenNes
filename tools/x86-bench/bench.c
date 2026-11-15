/* BENCH: self-checking integer workloads. Lines "K name checksum" are diffed between emulators; "T name ticks" is speed. */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

static unsigned long seed = 12345;
static unsigned long rnd(void) { seed = seed * 1103515245UL + 12345UL; return (seed >> 8) & 0xffffffUL; }

static unsigned char sv[8191];
static unsigned long k_sieve(int reps) {
    unsigned long total = 0; int r, i, k;
    for (r = 0; r < reps; r++) {
        int count = 0;
        memset(sv, 1, sizeof sv);
        for (i = 0; i < 8191; i++) if (sv[i]) { int p = i + i + 3; for (k = i + p; k < 8191; k += p) sv[k] = 0; count++; }
        total += count;
    }
    return total;
}

static unsigned long crctab[256];
static unsigned long k_crc(int reps) {
    unsigned long c, crc = 0; int n, k, r; static unsigned char buf[2048];
    for (n = 0; n < 256; n++) { c = n; for (k = 0; k < 8; k++) c = c & 1 ? 0xEDB88320UL ^ (c >> 1) : c >> 1; crctab[n] = c; }
    for (n = 0; n < 2048; n++) buf[n] = (unsigned char)(n * 7 + 3);
    for (r = 0; r < reps; r++) {
        c = 0xffffffffUL;
        for (n = 0; n < 2048; n++) c = crctab[(c ^ buf[n]) & 0xff] ^ (c >> 8);
        crc ^= c ^ 0xffffffffUL; crc = (crc << 1) | (crc >> 31);
    }
    { const char *s = "123456789"; c = 0xffffffffUL; while (*s) c = crctab[(c ^ (unsigned char)*s++) & 0xff] ^ (c >> 8); if ((c ^ 0xffffffffUL) != 0xCBF43926UL) printf("FAIL crc32 check value\n"); }
    return crc;
}

static int cmpl(const void *a, const void *b) { long x = *(const long *)a, y = *(const long *)b; return x < y ? -1 : x > y; }
static long qa[1500];
static unsigned long k_qsort(int reps) {
    unsigned long h = 0; int r, i;
    for (r = 0; r < reps; r++) {
        for (i = 0; i < 1500; i++) qa[i] = (long)rnd() - 0x800000L;
        qsort(qa, 1500, sizeof(long), cmpl);
        for (i = 1; i < 1500; i++) if (qa[i - 1] > qa[i]) { printf("FAIL qsort order\n"); break; }
        for (i = 0; i < 1500; i += 37) h = h * 31 + (unsigned long)qa[i];
    }
    return h;
}

static unsigned long k_div(int reps) {
    unsigned long h = 0; long a, b; int r, i;
    for (r = 0; r < reps; r++)
        for (i = 1; i < 2000; i++) {
            a = (long)rnd() * (i & 1 ? 1 : -1) * 37; b = (long)(rnd() % 1000) - 500; if (b == 0) b = 7;
            h = h * 33 + (unsigned long)(a / b) + (unsigned long)(a % b) * 7 + ((unsigned long)a >> (i & 31)) + (unsigned long)((a ^ b) >> (i & 15));
            h += (unsigned long)((unsigned)i * 40503u) + (unsigned long)(short)(i * 977);
        }
    return h;
}

static unsigned long k_ll(int reps) {
    unsigned long long x = 0x0123456789ABCDEFULL, y = 0xFEDCBA9876543210ULL; long long s = -1234567890123LL; int r, i;
    for (r = 0; r < reps; r++)
        for (i = 1; i < 500; i++) {
            x = x * 6364136223846793005ULL + 1442695040888963407ULL;
            y ^= x >> (i & 63); y += x / ((unsigned long long)i * 1000003ULL + 1); s = s / 3 + (long long)(x % 1000) - (s >> 5);
        }
    return (unsigned long)(x ^ (x >> 32) ^ y ^ (y >> 32) ^ (unsigned long long)s);
}

static int fib(int n) { return n < 2 ? n : fib(n - 1) + fib(n - 2); }
static unsigned long k_fib(int reps) { unsigned long h = 0; int r; for (r = 0; r < reps; r++) h += fib(20); return h; }

static int ma[24][24], mb[24][24], mc[24][24];
static unsigned long k_mat(int reps) {
    unsigned long h = 0; int r, i, j, k;
    for (i = 0; i < 24; i++) for (j = 0; j < 24; j++) { ma[i][j] = (i * 3 + j) % 17 - 8; mb[i][j] = (i ^ j) % 13 - 6; }
    for (r = 0; r < reps; r++) {
        for (i = 0; i < 24; i++) for (j = 0; j < 24; j++) { int s = 0; for (k = 0; k < 24; k++) s += ma[i][k] * mb[k][j]; mc[i][j] = s; }
        for (i = 0; i < 24; i++) for (j = 0; j < 24; j++) { h = h * 3 + (unsigned)mc[i][j]; ma[i][j] = (mc[i][j] & 15) - 7; }
    }
    return h;
}

static char sa[4000], sb2[4000];
static unsigned long k_str(int reps) {
    unsigned long h = 0; int r, i;
    for (r = 0; r < reps; r++) {
        for (i = 0; i < 3999; i++) sa[i] = (char)('A' + (i * 7 + r) % 26); sa[3999] = 0;
        memcpy(sb2, sa, 4000); memmove(sb2 + 3, sb2, 3000); memmove(sb2, sb2 + 5, 3000);
        h = h * 7 + strlen(sb2) + (unsigned long)strcmp(sa, sb2) + (unsigned long)(strchr(sb2, 'Q') - sb2);
        for (i = 0; i < 4000; i += 97) h += (unsigned char)sb2[i];
        sprintf(sa, "%ld %lu %x %5d|%-4s|", (long)h, h * 3, (unsigned)r * 31, -r, "ab"); h += strlen(sa) + sa[3];
    }
    return h;
}

struct K { const char *name; unsigned long (*f)(int); int reps; };
int main(int argc, char **argv) {
    static struct K ks[] = { {"sieve", k_sieve, 10}, {"crc32", k_crc, 40}, {"qsort", k_qsort, 4}, {"div", k_div, 4}, {"ll", k_ll, 6},
                             {"fib", k_fib, 6}, {"mat", k_mat, 10}, {"str", k_str, 40} };
    int i, scale = argc > 1 ? atoi(argv[1]) : 1; unsigned long all = 0; clock_t t0, t1;
    printf("BENCH %d-bit scale %d\n", (int)(sizeof(int) * 8), scale);
    for (i = 0; i < (int)(sizeof ks / sizeof ks[0]); i++) {
        unsigned long v; t0 = clock(); v = ks[i].f(ks[i].reps * scale); t1 = clock();
        printf("K %s %08lX\n", ks[i].name, v); printf("T %s %ld\n", ks[i].name, (long)(t1 - t0)); all ^= v;
    }
    printf("K all %08lX\nDONE\n", all);
    return 0;
}
