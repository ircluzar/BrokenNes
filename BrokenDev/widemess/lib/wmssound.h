/*---------------------------------------------------------------------------------
    wmssound.h: the WideMess sound driver's main-CPU side (BrokenDev's WideMess devkit; docs/projects/widemess-devkit.md).

    The driver (driver/wmsdrv.asm) runs on the WideMess sound unit: songs on up to 24 channels (voices 8-31, 16-bit PCM or BRR samples
    anywhere in the 1 MB of sound RAM), effects on any of the 32 voices. wms.json's "sound" section lists the samples, songs (MIDI) and
    effects; wmssnd.py converts them into snd.h (SND_SONG_*, SND_SFX_*) and an upload table, snd_uploads.

    sndInit() loads the driver, the songs and the samples through the sound RAM port while the boot loader runs (spec 4: the main CPU
    fills the 1 MB, the SPC700 copies nothing), then starts the driver. It needs the unlock (wmsInit) first.
---------------------------------------------------------------------------------*/
#ifndef WMSSOUND_H
#define WMSSOUND_H

#include <snes.h>

#define REG_APU0 (*(vuint8 *)0x2140)
#define REG_APU1 (*(vuint8 *)0x2141)
#define REG_APU2 (*(vuint8 *)0x2142)
#define REG_APU3 (*(vuint8 *)0x2143)

/* The driver's commands (port 1). */
#define WMS_SND_PLAY     1
#define WMS_SND_STOP     2
#define WMS_SND_EFFECT   3
#define WMS_SND_VOLUME   4

/* Loads and starts the driver with an upload table (snd.h's sndInit() passes snd_uploads). Returns 1 when the driver runs on a
   WideMess sound unit, 0 when it did not answer or found no WideMess (WID). */
u8 wmsSoundStart(u8 *uploads);
/* 1 once wmsSoundStart() succeeded. */
extern u8 wms_sound_ok;

void sndPlaySong(u8 song);
void sndStopSong(void);
/* Effect sfx on its own voice (voice = 0xFF) or on voice 0-31. */
void sndPlayEffectOn(u8 sfx, u8 voice);
#define sndPlayEffect(sfx) sndPlayEffectOn((sfx), 0xFF)
/* Master volume 0-127. */
void sndVolume(u8 volume);
/* Channels of the song still sounding (the driver's status, port 3). */
u8 sndChannels(void);

#endif
