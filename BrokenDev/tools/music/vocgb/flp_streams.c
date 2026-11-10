/* The four reserved song streams (16,376 bytes each: a bank less the 8 bytes mus_fetch may read past a stream), one per bank 57-60.
   Placeholder content: a long wait and the loop (silence). FLP_BANK / FLP_NAME are set per file by build-vocgb.ps1. */
#pragma bank FLP_BANK
#include <stdint.h>
const uint8_t FLP_NAME[16376] = { 0xD0, 0xFF, 0xFF };
