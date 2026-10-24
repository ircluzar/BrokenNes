/*---------------------------------------------------------------------------------
    wmssound.c: the WideMess sound driver's main-CPU side (see wmssound.h). Built into a game whose wms.json has a "sound" section.
---------------------------------------------------------------------------------*/
#include "widemess.h"
#include "wmssound.h"

u8 wms_sound_ok;
static u8 snd_id;

/* waits until port 0 reads value; 0 when it did not within the limit (a sound unit that does not answer) */
static u8 snd_wait0(u8 value)
{
    u16 n;
    for (n = 0; n < 60000; n++)
        if (REG_APU0 == value) return 1;
    return 0;
}

u8 wmsSoundStart(u8 *uploads)
{
    u16 n;
    wms_sound_ok = 0;
    /* the boot loader after reset: $AA, $BB */
    for (n = 0; n < 60000; n++)
        if (REG_APU0 == 0xAA && REG_APU1 == 0xBB) break;
    if (n == 60000) return 0;
    wmsUpload(uploads);                 /* the driver at $0200, the bank, the directories, the samples */
    REG_APU2 = 0x00;                    /* jump to $0200 */
    REG_APU3 = 0x02;
    REG_APU1 = 0x00;
    REG_APU0 = 0xCC;
    if (!snd_wait0(0xCC)) return 0;
    for (n = 0; n < 60000; n++)
        if (REG_APU1 == 0x5A) break;    /* the driver runs */
    if (n == 60000) return 0;
    snd_id = 0;
    if (REG_APU2 != 0x57) return 0;     /* WID: no WideMess sound unit */
    wms_sound_ok = 1;
    return 1;
}

static void snd_command(u8 cmd, u8 a, u8 b)
{
    if (!wms_sound_ok) return;          /* no driver: wmsSoundStart() said so */
    while (REG_APU0 != snd_id) {}       /* the previous command is done (the driver takes a few hundred SPC700 cycles) */
    REG_APU1 = cmd;
    REG_APU2 = a;
    REG_APU3 = b;
    snd_id++;
    if (snd_id == 0) snd_id = 1;
    REG_APU0 = snd_id;
}

void sndPlaySong(u8 song)
{
    snd_command(WMS_SND_PLAY, song, 0);
}

void sndStopSong(void)
{
    snd_command(WMS_SND_STOP, 0, 0);
}

void sndPlayEffectOn(u8 sfx, u8 voice)
{
    snd_command(WMS_SND_EFFECT, sfx, voice);
}

void sndVolume(u8 volume)
{
    snd_command(WMS_SND_VOLUME, volume, 0);
}

u8 sndChannels(void)
{
    return REG_APU3 >> 3;
}
