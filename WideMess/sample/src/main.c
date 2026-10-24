/*---------------------------------------------------------------------------------
    WIDE BLASTER: the WideMess devkit's sample game (made with 'brokendev new widemess widemess-sample', then grown).

    A small horizontal shooter that uses every WideMess feature (docs/projects/widemess-devkit.md):
      - 8 layers of 256 colours at 400 x 224: the HUD, the road, the city, two mountain ranges, a nebula, the stars, the sky,
        each scrolling at its own speed (wms.json "graphics" -> gfx.h);
      - the 256-sprite table: the ship, 24 bullets, 20 enemies, 32 explosions, a 64 x 64 256-colour boss and 120 sparkles
        (up to 199 sprites on the picture at once);
      - the WideMess sound chip: a song on up to 11 voices of voices 8-31 with 16-bit PCM bass, pad and lead and BRR drums,
        effects on voice 0 (BRR, an S-DSP voice) and voices 30-31 (16-bit PCM);
      - fast mode (816-tcc's C at 10.74 MHz keeps the frame) and fast DMA;
      - the 1 MB work RAM: every actor pool, the enemies' flight path and a 64 KB flight recorder of the pad (src/far.asm).

    Controls: the d-pad flies, B or Y fires, Start restarts after the game is over.
    Build / check / play: brokendev build widemess-sample / verify widemess-sample / run widemess-sample
---------------------------------------------------------------------------------*/
#include <snes.h>
#include "widemess.h"
#include "wmssound.h"
#include "gfx.h"
#include "snd.h"

#define MAX_BUL   24
#define MAX_EN    20
#define MAX_FX    32
#define MAX_SP    120
#define SLOT_SHIP 0
#define SLOT_BUL  1
#define SLOT_FX   25
#define SLOT_EN   57
#define SLOT_BOSS 77
#define SLOT_SP   80
#define PRIO      WMS_SPR_PRIO(2)       /* behind the HUD (BG1's high-priority tiles), in front of the other 7 layers */

/* far.asm: the pools, in the work RAM above the SNES's 128 KB */
extern s16 bul_x[], bul_y[];
extern u8 bul_on[];
extern s16 en_x[], en_y0[];
extern u16 en_t[];
extern u8 en_on[], en_kind[];
extern s16 fx_x[], fx_y[];
extern u8 fx_t[];
extern s16 sp_x[], sp_y[];
extern u8 sp_v[];
extern s16 path_y[];
extern u16 rec_pad[];
/* game.asm */
extern u8 bullet_hit(s16 left, s16 top, u16 w, u16 h);

/* globals the checks read by their symbols */
u16 game_frame;
u16 score;
u8 lives;
u8 game_over;
u16 rec_pos;

static s16 ship_x, ship_y;
static u8 fire_wait, invulnerable, spawn_wait;
static s16 boss_x, boss_y0;
static u8 boss_on, boss_hp;
static u16 boss_t;

/* the HUD: map entries copied from its own hidden parts (the digits at columns 50-59 of row 0, the message in row 29) */
static u16 digit_entry[10];
static u16 msg_entry[23];
static u16 blank_entry[23];
static u16 score_entries[6];
static u16 lives_entry;
static u8 hud_dirty, msg_dirty;

#define HUD_ADDR(col, row) (GFX_HUD_MAP + (((row) * 64 + (col)) << 1))

static void read_entries(u16 addr, u16 *dst, u8 n)
{
    u8 i;
    u16 lo, hi;
    REG_WMVADDL = (u8)addr;
    REG_WMVADDM = (u8)(addr >> 8);
    REG_WMVADDH = 0;
    for (i = 0; i < n; i++)
    {
        lo = REG_WMVDATA;
        hi = REG_WMVDATA;
        dst[i] = lo | (hi << 8);
    }
}

static void hud_update(void)
{
    u16 v = score;
    u8 d;
    d = 0; while (v >= 10000) { v -= 10000; d++; } score_entries[0] = digit_entry[d];
    d = 0; while (v >= 1000) { v -= 1000; d++; } score_entries[1] = digit_entry[d];
    d = 0; while (v >= 100) { v -= 100; d++; } score_entries[2] = digit_entry[d];
    d = 0; while (v >= 10) { v -= 10; d++; } score_entries[3] = digit_entry[d];
    score_entries[4] = digit_entry[v];
    score_entries[5] = digit_entry[0];
    lives_entry = digit_entry[lives];
    hud_dirty = 1;
}

static void vblank(void)
{
    wmsVblank();
    if (hud_dirty)
    {
        wmsVramCopy(HUD_ADDR(7, 0), (u8 *)score_entries, 12);
        wmsVramCopy(HUD_ADDR(46, 0), (u8 *)&lives_entry, 2);
        hud_dirty = 0;
    }
    if (msg_dirty)
    {
        wmsVramCopy(HUD_ADDR(13, 13), game_over ? (u8 *)msg_entry : (u8 *)blank_entry, 46);
        msg_dirty = 0;
    }
}

static void not_a_widemess(void)
{
    setPaletteColor(0, RGB5(31, 0, 0));
    setScreenOn();
    while (1) WaitForVBlank();
}

/* ---- actors -------------------------------------------------------------------------------------------------- */

static void boom_at(s16 x, s16 y)
{
    u8 i;
    for (i = 0; i < MAX_FX; i++)
        if (fx_t[i] == 0)
        {
            fx_x[i] = x;
            fx_y[i] = y;
            fx_t[i] = 1;
            return;
        }
}

static void fire(void)
{
    u8 i;
    for (i = 0; i < MAX_BUL; i++)
        if (!bul_on[i])
        {
            bul_on[i] = 1;
            bul_x[i] = ship_x + 12;
            bul_y[i] = ship_y + 4;
            sndPlayEffect(SND_SFX_LASER);
            return;
        }
}

static void spawn_enemy(void)
{
    u8 i;
    for (i = 0; i < MAX_EN; i++)
        if (!en_on[i])
        {
            en_on[i] = 1;
            en_kind[i] = (rand() & 3) == 0 ? 1 : 0;
            en_x[i] = WMS_WIDTH + 8;
            en_y0[i] = 40 + (rand() & 127);
            en_t[i] = rand() & 1023;
            return;
        }
}

static void reset_game(void)
{
    u8 i;
    for (i = 0; i < MAX_BUL; i++) bul_on[i] = 0;
    for (i = 0; i < MAX_EN; i++) en_on[i] = 0;
    for (i = 0; i < MAX_FX; i++) fx_t[i] = 0;
    ship_x = 40;
    ship_y = 110;
    score = 0;
    lives = 3;
    invulnerable = 90;
    boss_on = 0;
    game_over = 0;
    msg_dirty = 1;
    hud_update();
}

static void hit_ship(void)
{
    boom_at(ship_x, ship_y);
    sndPlayEffect(SND_SFX_BOOM);
    invulnerable = 120;
    if (lives > 0) lives--;
    if (lives == 0)
    {
        game_over = 1;
        msg_dirty = 1;
        wmsSpriteHide(SLOT_SHIP);
    }
    hud_update();
}

static void update_bullets(void)
{
    u8 i;
    for (i = 0; i < MAX_BUL; i++)
    {
        if (!bul_on[i]) { wmsSpriteHide(SLOT_BUL + i); continue; }
        bul_x[i] += 6;
        if (bul_x[i] > WMS_WIDTH) { bul_on[i] = 0; wmsSpriteHide(SLOT_BUL + i); continue; }
        wmsSpriteSet(SLOT_BUL + i, bul_x[i], bul_y[i], GFX_BULLET_TILE(0), GFX_BULLET_ATTR6, GFX_BULLET_ATTR7 | PRIO);
    }
}

static void update_enemies(void)
{
    u8 i, j;
    s16 x, y, dx, dy;
    for (i = 0; i < MAX_EN; i++)
    {
        if (!en_on[i]) { wmsSpriteHide(SLOT_EN + i); continue; }
        en_t[i] = (en_t[i] + 3) & 1023;
        en_x[i] -= en_kind[i] ? 3 : 1;
        x = en_x[i];
        y = en_y0[i] + path_y[en_t[i]];
        if (x < -16) { en_on[i] = 0; wmsSpriteHide(SLOT_EN + i); continue; }
        /* the bullets (game.asm): a bullet at dx -7..13, dy -5..13 from the enemy */
        j = bullet_hit(x - 7, y - 5, 21, 19);
        if (j != 255)
        {
            bul_on[j] = 0;
            en_on[i] = 0;
            boom_at(x, y);
            sndPlayEffect(SND_SFX_HIT);
            score++;
            hud_update();
            wmsSpriteHide(SLOT_EN + i);
            continue;
        }
        /* the ship */
        if (!game_over && invulnerable == 0)
        {
            dx = ship_x - x;
            dy = ship_y - y;
            if (dx > -12 && dx < 12 && dy > -10 && dy < 10) { en_on[i] = 0; hit_ship(); wmsSpriteHide(SLOT_EN + i); continue; }
        }
        if (en_kind[i])
            wmsSpriteSet(SLOT_EN + i, x, y, GFX_DART_TILE((game_frame >> 2) & 1), GFX_DART_ATTR6, GFX_DART_ATTR7 | PRIO);
        else
            wmsSpriteSet(SLOT_EN + i, x, y, GFX_SAUCER_TILE((game_frame >> 3) & 1), GFX_SAUCER_ATTR6, GFX_SAUCER_ATTR7 | PRIO);
    }
}

static void update_boss(void)
{
    u8 j;
    s16 y;
    if (!boss_on)
    {
        if ((game_frame & 1023) == 600) { boss_on = 1; boss_hp = 20; boss_x = WMS_WIDTH; boss_y0 = 60; boss_t = 0; }
        else { wmsSpriteHide(SLOT_BOSS); return; }
    }
    boss_t = (boss_t + 2) & 1023;
    if (game_frame & 1) boss_x--;
    y = boss_y0 + path_y[boss_t] + path_y[boss_t];
    if (boss_x < -64) { boss_on = 0; wmsSpriteHide(SLOT_BOSS); return; }
    /* one bullet a frame can hit it (game.asm): inside its 60 x 60 middle */
    j = bullet_hit(boss_x + 1, y + 1, 59, 59);
    if (j != 255)
    {
        bul_on[j] = 0;
        boom_at(bul_x[j], bul_y[j]);
        if (--boss_hp == 0)
        {
            boss_on = 0;
            boom_at(boss_x + 16, y + 16);
            boom_at(boss_x + 32, y + 32);
            sndPlayEffect(SND_SFX_BOOM);
            score += 50;
            hud_update();
            wmsSpriteHide(SLOT_BOSS);
            return;
        }
        sndPlayEffect(SND_SFX_HIT);
    }
    wmsSpriteSet(SLOT_BOSS, boss_x, y, GFX_BOSS_TILE(0), GFX_BOSS_ATTR6, GFX_BOSS_ATTR7 | PRIO);
}

static void update_fx(void)
{
    u8 i, t;
    for (i = 0; i < MAX_FX; i++)
    {
        t = fx_t[i];
        if (t == 0) { wmsSpriteHide(SLOT_FX + i); continue; }
        if (t >= 16) { fx_t[i] = 0; wmsSpriteHide(SLOT_FX + i); continue; }
        fx_t[i] = t + 1;
        fx_x[i] -= 1;
        wmsSpriteSet(SLOT_FX + i, fx_x[i], fx_y[i], GFX_BOOM_TILE(t >> 2), GFX_BOOM_ATTR6, GFX_BOOM_ATTR7 | PRIO);
    }
}

static void update_sparkles(void)
{
    u8 i;
    s16 x;
    /* half of them a frame: 120 sprites stay alive for half the CPU */
    for (i = (u8)(game_frame & 1); i < MAX_SP; i += 2)
    {
        x = sp_x[i] - sp_v[i];
        if (x < -8) { x += WMS_WIDTH + 8; sp_y[i] = 12 + (rand() & 191); }
        sp_x[i] = x;
        wmsSpriteSet(SLOT_SP + i, x, sp_y[i], GFX_SPARK_TILE((i + (game_frame >> 4)) & 1), GFX_SPARK_ATTR6, GFX_SPARK_ATTR7 | WMS_SPR_PRIO(0));
    }
}

static void init_tables(void)
{
    u16 i, u, v;
    /* the flight path: a smooth wave of 1,024 steps (a parabola each half), amplitude 31 */
    for (i = 0; i < 512; i++)
    {
        u = i >> 1;
        v = (u * (255 - u)) >> 9;
        path_y[i] = (s16)v;
        path_y[i + 512] = -(s16)v;
    }
    for (i = 0; i < MAX_SP; i++)
    {
        sp_x[i] = rand() % WMS_WIDTH;
        sp_y[i] = 12 + (rand() & 191);
        sp_v[i] = 1 + (i & 1) + ((i & 7) == 0 ? 2 : 0);
    }
    for (i = 0; i < 23; i++) blank_entry[i] = 0;
}

int main(void)
{
    u16 pad;

    consoleInit();
    setScreenOff();
    if (!wmsInit() || !wmsHas(WMFEAT_PICTURE | WMFEAT_SPRITES | WMFEAT_SOUND | WMFEAT_FASTCPU | WMFEAT_BIGWRAM | WMFEAT_FASTDMA)) not_a_widemess();

#ifndef SAMPLE_SNES_SPEED                /* -D SAMPLE_SNES_SPEED: the devkit's check that fast mode is what keeps 60 frames a second */
    wmsFastCpu(1);
#endif
    wmsFastDma(1);
    gfxLoadAll();
    wmsSpritesInit(GFX_SPRITE_UNIT);
    wmsSpritesShow(1);
    wmsScreen(WMCTRL_WIDE | WMCTRL_LAYERS8);
    read_entries(HUD_ADDR(50, 0), digit_entry, 10);
    read_entries(HUD_ADDR(13, 29), msg_entry, 23);
    srand(0x5A17);
    init_tables();
    reset_game();

    sndInit();
    sndPlaySong(SND_SONG_THEME);

    nmiSet(vblank);
    setScreenOn();

    while (1)
    {
        pad = pad_keys[0];
        rec_pad[rec_pos] = pad;                  /* the flight recorder: 64 KB of far RAM */
        rec_pos = (rec_pos + 1) & 0x7FFF;

        if (!game_over)
        {
            if ((pad & KEY_LEFT) && ship_x > 0) ship_x -= 2;
            if ((pad & KEY_RIGHT) && ship_x < WMS_WIDTH - 16) ship_x += 2;
            if ((pad & KEY_UP) && ship_y > 12) ship_y -= 2;
            if ((pad & KEY_DOWN) && ship_y < WMS_HEIGHT - 16) ship_y += 2;
            if (fire_wait) fire_wait--;
            if ((pad & (KEY_B | KEY_Y)) && fire_wait == 0) { fire(); fire_wait = 6; }
            if (invulnerable) invulnerable--;
            if (invulnerable & 4) wmsSpriteHide(SLOT_SHIP);
            else wmsSpriteSet(SLOT_SHIP, ship_x, ship_y, GFX_SHIP_TILE((game_frame >> 2) & 1), GFX_SHIP_ATTR6, GFX_SHIP_ATTR7 | PRIO);
        }
        else if (pad_keysdown[0] & KEY_START) reset_game();

        if (spawn_wait) spawn_wait--;
        else { spawn_enemy(); spawn_wait = 14 + (rand() & 15); }

        update_bullets();
        update_enemies();
        update_boss();
        update_fx();
        update_sparkles();

        /* parallax: each layer at its own speed (in eighths of a pixel a frame) */
        wmsScroll(GFX_GROUND_LAYER, game_frame << 2, 0);
        wmsScroll(GFX_CITY_LAYER, game_frame << 1, 0);
        wmsScroll(GFX_MID_LAYER, game_frame, 0);
        wmsScroll(GFX_FAR_LAYER, game_frame >> 1, 0);
        wmsScroll(GFX_NEBULA_LAYER, game_frame >> 2, 0);
        wmsScroll(GFX_STARS_LAYER, game_frame >> 3, 0);

        game_frame++;
        WaitForVBlank();
    }
    return 0;
}
