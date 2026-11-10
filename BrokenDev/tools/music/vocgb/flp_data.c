/* The .flp test ROM's song data (BrokenDev/tools/music/vocgb/build-vocgb.ps1): ONE song whose tables and four streams are
   reserved space, filled in by the BrokenNes2 plugin (Music/Flp/VocGbBackend.cs) at the offsets the manifest names. The driver is
   the game's own (VRUN_VOC_dev src/music: voc_music.c + voc_music_asm.s, compiled unchanged); this file stands in for the
   generated src/music/gen/voc_music_data.c and has the same symbols. */
#pragma bank 56
#include <stdint.h>
#include "voc_music.h"

extern const uint8_t flp_s0[], flp_s1[], flp_s2[], flp_s3[];

/* 16 instruments of 8 bytes, 16 wave tables of 16 bytes, the noise table: written by the plugin */
const uint8_t mus_instab[128] = { 0x80, 0, 0, 1, 0, 1, 0, 0, 0x80, 0, 0, 1, 0, 1, 0, 0, 0, 0, 0, 1, 0, 1, 0, 0, 0, 0, 0, 1, 0, 1, 0, 0 };
const uint8_t mus_wavetab[256] = { 0xFE, 0xDC, 0xBA, 0x98, 0x76, 0x54, 0x32, 0x10, 0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF };
const uint8_t mus_noisetab[128] = { 0x35 };
const mus_song_t mus_songtab[1] = {
    { 0x0100, { { flp_s0, 57 }, { flp_s1, 58 }, { flp_s2, 59 }, { flp_s3, 60 } } },
};
const uint8_t mus_song_count = 1;
const uint8_t mus_track_song[22] = { 0 };
const uint8_t mus_track_count = 22;
const char * const mus_song_names[] = { "FLP TEST" };
const char * const mus_track_names[] = { "title" };
