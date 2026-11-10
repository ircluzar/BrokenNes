# Cross-version ledger: CTW and Super VRUN

Starting inventory, 2026-11-10. From now on, polish lands on a **base** version (CTW NES MMC3, Super VRUN SNES) and every other version must follow. This file shows what each version has today. The machine-readable source is `inventory.json`: 66 rows, each cell with status, detail and evidence.

**Legend:**
- **Y**: has
- **~**: partial
- **-**: missing
- **n/a**: not applicable
- **?**: unknown (the detail says what would settle it)

**Evidence tags in the JSON:**
- (code): read in the source.
- (run): measured by an earlier agent's run, recorded in memory or docs.
- (note): from a memory or doc note.

No ROM was re-run for this first pass, so every "run" cell cites an earlier measurement.

**How the versions relate:**
- **CTW:**
  - One NESFab source. MMC3 and mapper 30 differ only in the mapper layer.
  - SMS is a fab2c machine translation of the **mapper-30** source.
  - Nested VRUN is the **mapper-30** ROM inside Project Nested.
  - So MMC3-only polish (tunnel, CHR-ROM anims) never reaches SMS or Nested by itself. Gameplay changes do, once SMS is re-translated or Nested is rebuilt.
- **Super VRUN:**
  - MD, DOS and 95 compile a **copy** of the SNES `src/` (`logic/`), so they only pick up gameplay changes after a re-sync. MD, DOS and 95 synced at SNES 1c35e08 (2026-11-10 backport round).
  - WideMess is a SNES branch, built with `--widemess`.
- **No CTW Game Gear port exists.** The GG port is Vaults, which is out of scope.

## Chart

### Provenance

| Feature | CTW original (Fusion, ref) | CTW NES MMC3 (BASE) | CTW NES m30 | CTW SMS | Nested VRUN SNES | Super VRUN SNES (BASE) | SV WideMess | SV Genesis | SV DOS | SV Win95 |
|---|---|---|---|---|---|---|---|---|---|---|
| 1. Source lineage / sync point | Y Clickteam Fusion HTML5 game, 113-114 room XMLs, runtime JS map gene... | Y main.fab + mapper_mmc3.fab, ./build.sh game --mmc3 (MAIN NES build ... | Y same main.fab + mapper_m30.fab; lean reference build, never to be d... | Y tools/fab2c.py translates the MMC3 main build (main.fab + mapper_mmc3.... | Y m30 ROM (game.nested.cfg, snes_pad.fab) inside Project Nested; plai... | Y PVSnesLib C + 65816 asm, master 1c35e08 (2P, weapon mods, cgram fix... | Y SNES widemess branch from weapon-mods 8953dbf, build.sh --widemess ... | Y logic/ copy of SNES 1c35e08 (resync ce31650, 2026-11-10) + platform... | Y logic/ copy of SNES 1c35e08 (6d32c40) + software SNES PPU platform/... | Y logic/ from DOS 6d32c40 (SNES 1c35e08), Win32 GDI shell |
| 2. Other CTW ports (Game Gear etc.) | n/a | n/a no CTW Game Gear port exists; the GG port is Vaults (out of scope).... | n/a | n/a | n/a | n/a other game | n/a other game | n/a other game | n/a other game | n/a other game |

### Game modes

| Feature | CTW original (Fusion, ref) | CTW NES MMC3 (BASE) | CTW NES m30 | CTW SMS | Nested VRUN SNES | Super VRUN SNES (BASE) | SV WideMess | SV Genesis | SV DOS | SV Win95 |
|---|---|---|---|---|---|---|---|---|---|---|
| 3. Title menu: NEW GAME / CONTINUE / OPTIONS + hi score | ~ Start frame with random verb label, no CONTINUE/OPTIONS in canvas | Y NEW GAME / CONTINUE (greyed w/o save) / OPTIONS + HI SCORE | Y NEW GAME / CONTINUE (greyed w/o save) / OPTIONS + HI SCORE | Y NEW GAME / CONTINUE (greyed w/o save) / OPTIONS + HI SCORE (fab2c t... | Y NEW GAME / CONTINUE (greyed w/o save) / OPTIONS + HI SCORE (m30 ROM... | Y NEW GAME 1P / NEW GAME 2P / CONTINUE 1P/2P / OPTIONS + seed line + ... | Y as SNES + pointer hover/click | Y NEW GAME 1P / NEW GAME 2P / CONTINUE 1P/2P / OPTIONS + seed line + ... | Y NEW GAME 1P / NEW GAME 2P / CONTINUE 1P/2P / OPTIONS + seed line + ... | Y NEW GAME 1P / NEW GAME 2P / CONTINUE 1P/2P / OPTIONS + seed line + ... |
| 4. START AT (area 1-6 / BOSS) | ~ debug level-select backdoor, dead in normal play | Y Options START AT 1-6/BOSS; spawn safety fix 31767d0 | Y Options START AT 1-6/BOSS; spawn safety fix 31767d0 | Y Options START AT 1-6/BOSS; spawn safety fix 31767d0 (fab2c translat... | Y Options START AT 1-6/BOSS; spawn safety fix 31767d0 (m30 ROM in Nes... | Y Options START AT chips 1-6 + BOSS | Y Options START AT chips 1-6 + BOSS | Y Options START AT chips 1-6 + BOSS (logic/ copy) | Y Options START AT chips 1-6 + BOSS (logic/ copy) | Y Options START AT chips 1-6 + BOSS (from DOS 0c727bb) |
| 5. BOOST start buff (NONE/MID/HIGH) | - | Y Options BOOST, test aid | Y Options BOOST, test aid | Y Options BOOST, test aid (fab2c translation of m30) | Y Options BOOST, test aid (m30 ROM in Nested) | Y Options BOOST | Y Options BOOST | Y Options BOOST (logic/ copy) | Y Options BOOST (logic/ copy) | Y Options BOOST (from DOS 0c727bb) |
| 6. Story screen | Y 15 TTS voiceover lines | - no story app state (states TITLE GAME DEATH SHOP ENDING OPTIONS SOU... | - same | - same (translated) | - same | Y state_story.c (story text; skip behaviour unknown) | Y state_story.c (story text; skip behaviour unknown) | Y state_story.c (story text; skip behaviour unknown) (logic/ copy) | Y state_story.c (story text; skip behaviour unknown) (logic/ copy) | Y state_story.c (story text; skip behaviour unknown) (from DOS 0c727bb) |
| 7. Ending screen | Y time + scrolling credits | Y ending text + run time + score | Y ending text + run time + score | Y ending text + run time + score (fab2c translation of m30) | Y ending text + run time + score (m30 ROM in Nested) | Y state_ending.c | Y state_ending.c | Y state_ending.c (logic/ copy) | Y state_ending.c (logic/ copy) | Y state_ending.c (from DOS 0c727bb) |
| 8. Death sequence | Y TTS 'Deleted' then page reload | Y explosion, DELETED, glitch, restore | Y explosion, DELETED, glitch, restore | Y explosion, DELETED, glitch, restore (fab2c translation of m30) | Y explosion, DELETED, glitch, restore (m30 ROM in Nested) | Y state_death.c + deathfx HDMA ripple + ctwdeath.c | Y state_death.c + deathfx HDMA ripple + ctwdeath.c | ~ logic has it; HDMA ripple mapped to MD line scroll, extent unverified | Y state_death.c + deathfx HDMA ripple + ctwdeath.c (logic/ copy) | Y state_death.c + deathfx HDMA ripple + ctwdeath.c (from DOS 0c727bb) |

### World

| Feature | CTW original (Fusion, ref) | CTW NES MMC3 (BASE) | CTW NES m30 | CTW SMS | Nested VRUN SNES | Super VRUN SNES (BASE) | SV WideMess | SV Genesis | SV DOS | SV Win95 |
|---|---|---|---|---|---|---|---|---|---|---|
| 9. Areas / tiers | Y levels generated at runtime | Y 6 tiers + boss arena; room ranges 0-12,13-31,32-56,57-87,88-124,125... | Y 6 tiers + boss arena; room ranges 0-12,13-31,32-56,57-87,88-124,125... | Y 6 tiers + boss arena; room ranges 0-12,13-31,32-56,57-87,88-124,125... | Y 6 tiers + boss arena; room ranges 0-12,13-31,32-56,57-87,88-124,125... | Y areas 1-6 + boss (START AT chips) | Y areas 1-6 + boss (START AT chips) | Y areas 1-6 + boss (START AT chips) (logic/ copy) | Y areas 1-6 + boss (START AT chips) (logic/ copy) | Y areas 1-6 + boss (START AT chips) (from DOS 0c727bb) |
| 10. Room count / map generation | Y runtime JS generator (MapGeneratorReplay.mjs), 114 room XMLs | Y 168 room templates, 32 baked maps (638 combos), seed = title map-sl... | Y 168 room templates, 32 baked maps (638 combos), seed = title map-sl... | Y 168 room templates, 32 baked maps (638 combos), seed = title map-sl... | Y 168 room templates, 32 baked maps (638 combos), seed = title map-sl... | Y map_data.c, >=168 rooms (room 167 named); seeded via title map slot... | Y map_data.c, >=168 rooms (room 167 named); seeded via title map slot... | Y map_data.c, >=168 rooms (room 167 named); seeded via title map slot... | Y map_data.c, >=168 rooms (room 167 named); seeded via title map slot... | Y map_data.c, >=168 rooms (room 167 named); seeded via title map slot... |
| 11. Shop rooms | Y random 3 of 9 items | Y 8 fixed rows, door seals | Y 8 fixed rows, door seals | Y 8 fixed rows, door seals (fab2c translation of m30) | Y 8 fixed rows, door seals (m30 ROM in Nested) | Y 9 items modal (uimodal.c); 2P shared price + revive; shop lag fix f... | Y 9 items modal (uimodal.c); 2P shared price + revive; shop lag fix f... | Y 9 items modal (uimodal.c); 2P shared price + revive; shop lag fix f... | Y 9 items modal (uimodal.c); 2P shared price + revive; shop lag fix f... | Y 9 items modal (uimodal.c); 2P shared price + revive; shop lag fix f... |
| 12. Portals (blue/pink) | Y | Y blue + pink pairs, flat pairing scan 2de35e3 | Y blue + pink pairs, flat pairing scan 2de35e3 | Y blue + pink pairs, flat pairing scan 2de35e3 (fab2c translation of ... | Y blue + pink pairs, flat pairing scan 2de35e3 (m30 ROM in Nested) | Y portal.c | Y portal.c | Y portal.c (logic/ copy) | Y portal.c (logic/ copy) | Y portal.c (from DOS 0c727bb) |
| 13. Lens / mirror / key-block laser puzzle | Y | Y rebuilt after phase-5 cut; bullet tier 4 lens beam | Y rebuilt after phase-5 cut; bullet tier 4 lens beam | Y rebuilt after phase-5 cut; bullet tier 4 lens beam (fab2c translati... | Y rebuilt after phase-5 cut; bullet tier 4 lens beam (m30 ROM in Nest... | Y puzzlefx.c, ctwpuzzle.c | Y puzzlefx.c, ctwpuzzle.c | Y puzzlefx.c, ctwpuzzle.c (logic/ copy) | Y puzzlefx.c, ctwpuzzle.c (logic/ copy) | Y puzzlefx.c, ctwpuzzle.c (from DOS 0c727bb) |
| 14. Pause area map | - (Remastered adds a level map) | - pause shows seed + save prompt only | - | - | - | Y pausemap.c | Y pause map + LIVE area map in the side columns (hover tint, click op... | ~ pause map + stats drawn (resync ce31650); the BG1 darken + mosaic a... | Y LOCAL copy with stat_of fix (BACKPORT #1) | Y from DOS |

### Enemies

| Feature | CTW original (Fusion, ref) | CTW NES MMC3 (BASE) | CTW NES m30 | CTW SMS | Nested VRUN SNES | Super VRUN SNES (BASE) | SV WideMess | SV Genesis | SV DOS | SV Win95 |
|---|---|---|---|---|---|---|---|---|---|---|
| 15. Enemy species | Y Ghost family, Bouncing L1-3, H-Movers, Red family, InfectedBlock, R... | Y 9: H-Mover L1-3, Bouncing L1-3 (L2/L3 shoot), Ghost L1-2, Red Grid ... | Y 9: H-Mover L1-3, Bouncing L1-3 (L2/L3 shoot), Ghost L1-2, Red Grid ... | Y 9: H-Mover L1-3, Bouncing L1-3 (L2/L3 shoot), Ghost L1-2, Red Grid ... | Y 9: H-Mover L1-3, Bouncing L1-3 (L2/L3 shoot), Ghost L1-2, Red Grid ... | Y 9: HMOVER L1-3, BBLOCK L1-3, GHOST L1-2, RGRID_MOVER with original ... | Y 9: HMOVER L1-3, BBLOCK L1-3, GHOST L1-2, RGRID_MOVER with original ... | Y 9: HMOVER L1-3, BBLOCK L1-3, GHOST L1-2, RGRID_MOVER with original ... | Y 9: HMOVER L1-3, BBLOCK L1-3, GHOST L1-2, RGRID_MOVER with original ... | Y 9: HMOVER L1-3, BBLOCK L1-3, GHOST L1-2, RGRID_MOVER with original ... |
| 16. Max simultaneous enemies | ? not extracted | Y MAX_HAZARDS 4 | Y MAX_HAZARDS 4 | Y MAX_HAZARDS 4 (fab2c translation of m30) | Y MAX_HAZARDS 4 (m30 ROM in Nested) | Y unknown (settle: hazard.h pool constant) | Y unknown (settle: hazard.h pool constant) | Y unknown (settle: hazard.h pool constant) (logic/ copy) | Y unknown (settle: hazard.h pool constant) (logic/ copy) | Y unknown (settle: hazard.h pool constant) (from DOS 0c727bb) |
| 17. Per-tier minibosses | ~ level-6 miniboss is dead data | Y injected species at spawn in last room of each tier | Y injected species at spawn in last room of each tier | Y injected species at spawn in last room of each tier (fab2c translat... | Y injected species at spawn in last room of each tier (m30 ROM in Nes... | Y unknown (settle: grep miniboss in SNES src) | Y unknown (settle: grep miniboss in SNES src) | Y unknown (settle: grep miniboss in SNES src) (logic/ copy) | Y unknown (settle: grep miniboss in SNES src) (logic/ copy) | Y unknown (settle: grep miniboss in SNES src) (from DOS 0c727bb) |
| 18. LV1 Boss | Y phase machine; Lives-sync bug | Y 5 attack phases + intro, 160 HP/phase, death: explode, vomit, SYSTE... | Y 5 attack phases + intro, 160 HP/phase, death: explode, vomit, SYSTE... | Y 5 attack phases + intro, 160 HP/phase, death: explode, vomit, SYSTE... | Y 5 attack phases + intro, 160 HP/phase, death: explode, vomit, SYSTE... | Y boss.c/bossvis.c/bossdeath.c/ctwboss.c | Y boss.c/bossvis.c/bossdeath.c/ctwboss.c | ~ logic has it; boss BG3 effects not drawn on MD | Y boss.c/bossvis.c/bossdeath.c/ctwboss.c (logic/ copy) | Y boss.c/bossvis.c/bossdeath.c/ctwboss.c (from DOS 0c727bb) |
| 19. Hazard terrain (spikes, Red Grid, Bad Sector, tears, bricks, doors) | Y | Y Red Spikes, Red Grid, Bad Sector, Blue Tears (max 4, infect 8 neigh... | Y Red Spikes, Red Grid, Bad Sector, Blue Tears (max 4, infect 8 neigh... | Y Red Spikes, Red Grid, Bad Sector, Blue Tears (max 4, infect 8 neigh... | Y Red Spikes, Red Grid, Bad Sector, Blue Tears (max 4, infect 8 neigh... | Y same families via logic | Y same families via logic | Y same families via logic (logic/ copy) | Y same families via logic (logic/ copy) | Y same families via logic (from DOS 0c727bb) |
| 20. Invisible-enemy fix (OAM X bit 8, 9e13885) | n/a other game | n/a other game | n/a other game | n/a other game | n/a other game | Y _rtn_os_hide in roomtrans.asm; sweep 108 -> 0 | ? depends on branch merge order; settle: grep _rtn_os_hide in SuperVR... | ? asm replaced by asmref_room.c; settle: check bit-8 clear in rtn_oam... | ? same as MD (DOS fixed rtn_oam_dma separately, BACKPORT #3) | ? inherits DOS |

### Combat

| Feature | CTW original (Fusion, ref) | CTW NES MMC3 (BASE) | CTW NES m30 | CTW SMS | Nested VRUN SNES | Super VRUN SNES (BASE) | SV WideMess | SV Genesis | SV DOS | SV Win95 |
|---|---|---|---|---|---|---|---|---|---|---|
| 21. Bullet tiers / speeds | Y Speed stat picks L1 15/110ms, L2 18/70ms, L3 25/40ms; ammo cap Burs... | Y 8-slot shared pool; player t0-t2 coloured yellow/red/blue; cooldown... | Y 8-slot shared pool; player t0-t2 coloured yellow/red/blue; cooldown... | Y 8-slot shared pool; player t0-t2 coloured yellow/red/blue; cooldown... | Y 8-slot shared pool; player t0-t2 coloured yellow/red/blue; cooldown... | Y fire periods L1 165 / L2 105 / L3 60, MAX_BULLETS 24 | Y fire periods L1 165 / L2 105 / L3 60, MAX_BULLETS 24 | Y fire periods L1 165 / L2 105 / L3 60, MAX_BULLETS 24 (logic/ copy) | Y fire periods L1 165 / L2 105 / L3 60, MAX_BULLETS 24 (logic/ copy) | Y fire periods L1 165 / L2 105 / L3 60, MAX_BULLETS 24 (from DOS 0c72... |
| 22. Weapon modifiers (BlastType Wall/Chunk/Cross/Wave) | Y world pickups ids 25-33 | - cut for bullet count/ROM | - | - | - | Y NONE/WALL/CHUNK/CROSS/WAVE, original CTW balance (fbc86f2) | Y NONE/WALL/CHUNK/CROSS/WAVE, original CTW balance (fbc86f2) | Y NONE/WALL/CHUNK/CROSS/WAVE, original CTW balance (fbc86f2) (logic/ ... | Y NONE/WALL/CHUNK/CROSS/WAVE, original CTW balance (fbc86f2) (logic/ ... | Y NONE/WALL/CHUNK/CROSS/WAVE, original CTW balance (fbc86f2) (from DO... |
| 23. Held items (Corner/Field/Heal/Upgrade/Laser) | Y ItemType 1-5 on Space | - removed for ROM budget 2026-09-12 | - | - | - | Y 5 items on L/R; Laser 4 cardinal dirs, horizontal fallback | Y 5 items on L/R; Laser 4 cardinal dirs, horizontal fallback | Y 5 items on L/R; Laser 4 cardinal dirs, horizontal fallback (logic/ ... | Y 5 items on L/R; Laser 4 cardinal dirs, horizontal fallback (logic/ ... | Y 5 items on L/R; Laser 4 cardinal dirs, horizontal fallback (from DO... |
| 24. World pickups | Y random substitution 50% on Ressource L1/Health Square | ~ currency drops (homing, max 10), blue heal, Health Square +1 HP; no... | ~ currency drops (homing, max 10), blue heal, Health Square +1 HP; no... | ~ currency drops (homing, max 10), blue heal, Health Square +1 HP; no... | ~ currency drops (homing, max 10), blue heal, Health Square +1 HP; no... | Y 9 kinds, max 8/room, once per run (pk_taken) | Y 9 kinds, max 8/room, once per run (pk_taken) | Y 9 kinds, max 8/room, once per run (pk_taken) (logic/ copy) | Y 9 kinds, max 8/room, once per run (pk_taken) (logic/ copy) | Y 9 kinds, max 8/room, once per run (pk_taken) (from DOS 0c727bb) |

### Progression

| Feature | CTW original (Fusion, ref) | CTW NES MMC3 (BASE) | CTW NES m30 | CTW SMS | Nested VRUN SNES | Super VRUN SNES (BASE) | SV WideMess | SV Genesis | SV DOS | SV Win95 |
|---|---|---|---|---|---|---|---|---|---|---|
| 25. Shop catalogue | Y 3 random of 9 incl. Field 200/400, no Health | Y 8 rows: Burst Range Health(+3, NES ladder) Intensity Speed Hover Tr... | Y 8 rows: Burst Range Health(+3, NES ladder) Intensity Speed Hover Tr... | Y 8 rows: Burst Range Health(+3, NES ladder) Intensity Speed Hover Tr... | Y 8 rows: Burst Range Health(+3, NES ladder) Intensity Speed Hover Tr... | Y 9: BURST RANGE HEALTH INTENSITY SPEED(pierce) HOVER TRAIL LUCK FIELD | Y 9: BURST RANGE HEALTH INTENSITY SPEED(pierce) HOVER TRAIL LUCK FIELD | Y 9: BURST RANGE HEALTH INTENSITY SPEED(pierce) HOVER TRAIL LUCK FIEL... | Y 9: BURST RANGE HEALTH INTENSITY SPEED(pierce) HOVER TRAIL LUCK FIEL... | Y 9: BURST RANGE HEALTH INTENSITY SPEED(pierce) HOVER TRAIL LUCK FIEL... |
| 26. Luck price typo 12800 + unreachable tiers >= 64000 (16-bit score) | Y typo is in the original | Y kept typo; UU score caps at 65535 | Y kept typo; UU score caps at 65535 | Y kept typo; UU score caps at 65535 (fab2c translation of m30) | Y kept typo; UU score caps at 65535 (m30 ROM in Nested) | n/a other game | n/a other game | n/a other game | n/a other game | n/a other game |
| 27. Speed stat effect | Y selects bullet type | ~ cosmetic player recolour only | ~ cosmetic player recolour only | ~ cosmetic player recolour only | ~ cosmetic player recolour only | Y SPEED = pierce | Y SPEED = pierce | Y SPEED = pierce (logic/ copy) | Y SPEED = pierce (logic/ copy) | Y SPEED = pierce (from DOS 0c727bb) |
| 28. Trail stat | Y cosmetic afterimage | Y damaging markers (divergence) | Y damaging markers (divergence) | Y damaging markers (divergence) (fab2c translation of m30) | Y damaging markers (divergence) (m30 ROM in Nested) | Y TRAIL in shop; behaviour vs CTW unknown | Y TRAIL in shop; behaviour vs CTW unknown | Y TRAIL in shop; behaviour vs CTW unknown (logic/ copy) | Y TRAIL in shop; behaviour vs CTW unknown (logic/ copy) | Y TRAIL in shop; behaviour vs CTW unknown (from DOS 0c727bb) |
| 29. HP model | Y 1 HP per hit with cooldown, Heal +10 | Y start 5, Health +3, bar cap 32, 60-frame invuln | Y start 5, Health +3, bar cap 32, 60-frame invuln | Y start 5, Health +3, bar cap 32, 60-frame invuln (fab2c translation ... | Y start 5, Health +3, bar cap 32, 60-frame invuln (m30 ROM in Nested) | Y start 5, +3 per HEALTH, max (user kept SV model); heal pickups/items | Y start 5, +3 per HEALTH, max (user kept SV model); heal pickups/items | Y start 5, +3 per HEALTH, max (user kept SV model); heal pickups/item... | Y start 5, +3 per HEALTH, max (user kept SV model); heal pickups/item... | Y start 5, +3 per HEALTH, max (user kept SV model); heal pickups/item... |

### Player

| Feature | CTW original (Fusion, ref) | CTW NES MMC3 (BASE) | CTW NES m30 | CTW SMS | Nested VRUN SNES | Super VRUN SNES (BASE) | SV WideMess | SV Genesis | SV DOS | SV Win95 |
|---|---|---|---|---|---|---|---|---|---|---|
| 30. Jump / hover | Y Fusion platform movement | Y hold jump; hover fuel 25/50 by Hover tier | Y hold jump; hover fuel 25/50 by Hover tier | Y hold jump; hover fuel 25/50 by Hover tier (fab2c translation of m30) | Y hold jump; hover fuel 25/50 by Hover tier (m30 ROM in Nested) | Y jump + hover (fuel none/25/50) | Y jump + hover (fuel none/25/50) | Y jump + hover (fuel none/25/50) (logic/ copy) | Y jump + hover (fuel none/25/50) (logic/ copy) | Y jump + hover (fuel none/25/50) (from DOS 0c727bb) |
| 31. Physics constants | ? values not extracted (settle: port-spec 02 �1-2) | Y GRAVITY .1875, MAX_FALL 3.0, JUMP -2.0, HOLD .1875 x40, HOVER_VY -.... | Y GRAVITY .1875, MAX_FALL 3.0, JUMP -2.0, HOLD .1875 x40, HOVER_VY -.... | Y GRAVITY .1875, MAX_FALL 3.0, JUMP -2.0, HOLD .1875 x40, HOVER_VY -.... | Y GRAVITY .1875, MAX_FALL 3.0, JUMP -2.0, HOLD .1875 x40, HOVER_VY -.... | Y unknown values (settle: player.c/plctx.c); ports keep formats exact | Y unknown values (settle: player.c/plctx.c); ports keep formats exact | Y unknown values (settle: player.c/plctx.c); ports keep formats exact... | Y unknown values (settle: player.c/plctx.c); ports keep formats exact... | Y unknown values (settle: player.c/plctx.c); ports keep formats exact... |
| 32. Aim / control schemes | Y Classic/Intro/Mixed/Twin with analog sticks + keyboard | Y 8-dir aim; CLASSIC/AIMLOCK/TWINPAD/TWINSHOOT (pad 2 for twin); Star... | Y same | ~ schemes kept; in-game Start+Select cycle dropped (no Select button) | Y + SNES pad: X/B/Y/A diamond aim, R jump, L fire for schemes C/D | Y CONTROLS + CONTROLS2 (P2) schemes, L/R item key | Y CONTROLS + CONTROLS2 (P2) schemes, L/R item key | Y CONTROLS + CONTROLS2 (P2) schemes, L/R item key (logic/ copy) | Y CONTROLS + CONTROLS2 (P2) schemes, L/R item key (logic/ copy) | Y CONTROLS + CONTROLS2 (P2) schemes, L/R item key (from DOS 0c727bb) |

### Screen

| Feature | CTW original (Fusion, ref) | CTW NES MMC3 (BASE) | CTW NES m30 | CTW SMS | Nested VRUN SNES | Super VRUN SNES (BASE) | SV WideMess | SV Genesis | SV DOS | SV Win95 |
|---|---|---|---|---|---|---|---|---|---|---|
| 33. Resolution / layout | Y 398x224 canvas, 256x224 room centred with 71 px margins | Y 256x224 single-screen rooms, HUD row 28 | Y same | ~ 256x192: 184-line view follows player 0-40 px, 32 px of room off-sc... | Y NES picture on SNES | Y 256x224 Mode 1, BG1 room, BG2 backdrop/HUD, BG3 effects | Y 400x224, game at columns 72-327, side HUD/live map; edge repeat dro... | ~ H40 320x224, game x32-287, black side bars from 14 sprites; BG3 not... | Y VGA mode X 320x240, SNES 256x224 at x32 y8, software PPU | Y same PPU in resizable window 1x-6x, Alt+Enter/F11 fullscreen, keep ... |
| 34. HUD | Y stat widgets, ItemBox, Score=Currency, Lives=Exp | Y row 28: HP+bar, $ score; BOSS/PHASE row 1 in arena | Y same | Y own BG band via line IRQ @183 + NT $3000 (costs ~40% of lag) | Y NES HUD; enhanced = custom 16-colour font | Y HUD with item slots, second half for P2 | Y HUD with item slots, second half for P2 | Y HUD on WINDOW plane (519c13a) so it no longer scrolls with the room | Y HUD with item slots, second half for P2 (logic/ copy) | Y HUD with item slots, second half for P2 (from DOS 0c727bb) |
| 35. Title tunnel | - (Remastered has one) | Y 16 CHR-ROM frames x 6 IRQ bands, 1 ring/s, darker greens (439616b) | - logo + menu only (MMC3-only feature) | Y tunnel present (cropped to 192 lines: the copyright line is off screen... | - m30 build | Y titletun.c/.asm, options over tunnel (45efdc6) | Y titletun.c/.asm, options over tunnel (45efdc6) | Y pre-converted records, ROM DMA; phase fixed in 156e111 | Y titletun.c/.asm, options over tunnel (45efdc6) (logic/ copy) | Y titletun.c/.asm, options over tunnel (45efdc6) (from DOS 0c727bb) |
| 36. Animated tiles (Bad Sector, tears, portals) | Y | ~ 24 CHR-ROM banks on ONE shared 83 ms tick (independent speeds impos... | Y CHR-RAM NMI uploads, each anim its own speed | Y tile slot re-uploads, each anim its own speed (fab2c.json ct_overrides... | Y m30 model | n/a other game | n/a other game | n/a other game | n/a other game | n/a other game |
| 37. HDMA effects (gradient, line scroll, mosaic, colour math) | n/a other game | n/a other game | n/a other game | n/a other game | n/a other game | Y bgfx gradient + line scroll HDMA, hit mosaic, shadows/tints | Y bgfx gradient + line scroll HDMA, hit mosaic, shadows/tints | ~ gradient by chained H-int, line-scroll mode; dark colours round to ... | Y software PPU emulates mode 1, windows, mosaic, colour math, HDMA pe... | Y bgfx gradient + line scroll HDMA, hit mosaic, shadows/tints (from D... |
| 38. SNES enhanced graphics (Nested only) | n/a | n/a | n/a | n/a | Y enhanced build: 16-colour font + hue-shifted bevel shading via spar... | n/a other game | n/a other game | n/a other game | n/a other game | n/a other game |

### Corruption

| Feature | CTW original (Fusion, ref) | CTW NES MMC3 (BASE) | CTW NES m30 | CTW SMS | Nested VRUN SNES | Super VRUN SNES (BASE) | SV WideMess | SV Genesis | SV DOS | SV Win95 |
|---|---|---|---|---|---|---|---|---|---|---|
| 39. Corruption visuals | Y | Y CHR bank flip on hits (chr_bank), ambient churn, hurt flash, death ... | Y same, flip via CHR-RAM bank 1 | Y emulated at tile-conversion upload | Y Nested CHR mirror bank switch; enhanced shading follows bank | Y corrupt.c, corruptfx.c Field flip; Field fix 871167d everywhere (MD... | Y corrupt.c, corruptfx.c Field flip; Field fix 871167d everywhere (MD... | Y corrupt.c, corruptfx.c Field flip; Field fix 871167d everywhere (MD... | Y corrupt.c, corruptfx.c Field flip; Field fix 871167d everywhere (MD... | Y corrupt.c, corruptfx.c Field flip; Field fix 871167d everywhere (MD... |

### Save

| Feature | CTW original (Fusion, ref) | CTW NES MMC3 (BASE) | CTW NES m30 | CTW SMS | Nested VRUN SNES | Super VRUN SNES (BASE) | SV WideMess | SV Genesis | SV DOS | SV Win95 |
|---|---|---|---|---|---|---|---|---|---|---|
| 40. Save system | Y browser JS gamestate | Y 8 KB battery RAM $6000, magic VRU+$01, hi $6004, save $6010-$6022 | Y self-flash 2 sectors (118/122): hi score 2 B + 19 B save | Y cart SRAM via $FFFC bit 3; needs 'VRUN' at 0x1F0 on cold SRAM | Y flash emulated (SST39SF040) by flash_patch_vrun.py | Y battery SRAM, loadout record @256 + checksum | Y battery in WMS header | ? plan SRAM $200001 odd bytes; settle: grep SRAM in platform/md/plat_... | Y SVRUN.SAV; save -> CONTINUE after reboot verified | Y SVRUN.SAV beside exe (same format as DOS), %APPDATA%\SuperVRUN95 fa... |
| 41. Save & quit from pause | ? | Y Select = save & quit (SMS: button 1) | Y Select = save & quit (SMS: button 1) | Y Select = save & quit (SMS: button 1) (fab2c translation of m30) | Y Select = save & quit (SMS: button 1) (m30 ROM in Nested) | Y SAVE & QUIT | Y SAVE & QUIT | Y SAVE & QUIT (logic/ copy) | Y SAVE & QUIT (logic/ copy) | Y SAVE & QUIT (from DOS 0c727bb) |

### Multiplayer

| Feature | CTW original (Fusion, ref) | CTW NES MMC3 (BASE) | CTW NES m30 | CTW SMS | Nested VRUN SNES | Super VRUN SNES (BASE) | SV WideMess | SV Genesis | SV DOS | SV Win95 |
|---|---|---|---|---|---|---|---|---|---|---|
| 42. Two-player co-op | ~ Multiverse networked ghosts, inert stub | - pad 2 only for twin-stick aim | - pad 2 only for twin-stick aim | - pad 2 only for twin-stick aim | - pad 2 only for twin-stick aim | Y p2.c: shared shop price, revive, nearest-player targeting; 2P lag 0... | Y both pads + separate pointer port | Y pad 2 on port 2, checked both pads 62c4f90 | Y pad 1 = stick B for P2 | Y second controller = P2 |

### Menus

| Feature | CTW original (Fusion, ref) | CTW NES MMC3 (BASE) | CTW NES m30 | CTW SMS | Nested VRUN SNES | Super VRUN SNES (BASE) | SV WideMess | SV Genesis | SV DOS | SV Win95 |
|---|---|---|---|---|---|---|---|---|---|---|
| 43. Pause screen + stats | ? | Y blur fill, PAUSED, seed, save prompt (no stats) | Y blur fill, PAUSED, seed, save prompt (no stats) | Y blur fill, PAUSED, seed, save prompt (no stats) (fab2c translation ... | Y blur fill, PAUSED, seed, save prompt (no stats) (m30 ROM in Nested) | Y pause stats (BURST/LUCK/HOVER...) + map | Y pause stats (BURST/LUCK/HOVER...) + map | ~ PAUSED, seed, SELECT: SAVE & QUIT + P1 stats (BURST 1, LUCK 0, HOVE... | Y pause stats (BURST/LUCK/HOVER...) + map (logic/ copy) | Y pause stats (BURST/LUCK/HOVER...) + map (from DOS 0c727bb) |
| 44. Options menu contents | - HTML page outside canvas | Y CONTROLS A-D, SOUND TEST, SFX TEST, START AT, BOOST, BACK | Y CONTROLS A-D, SOUND TEST, SFX TEST, START AT, BOOST, BACK | Y CONTROLS A-D, SOUND TEST, SFX TEST, START AT, BOOST, BACK (fab2c tr... | Y CONTROLS A-D, SOUND TEST, SFX TEST, START AT, BOOST, BACK (m30 ROM ... | Y tabs GRAPHICS/AUDIO/CONTROLS: CONTROLS, CONTROLS2, SOUND TEST, SFX ... | Y tabs GRAPHICS/AUDIO/CONTROLS: CONTROLS, CONTROLS2, SOUND TEST, SFX ... | ~ SNES 1c35e08 options code (tabbed panel); the panel picture is garb... | Y + AUDIO > SOUND OUT AUTO/FM/SPEAKER/OFF (BACKPORT #2) | Y + SOUND OUT + Windows menus (File/Options/View/Help, Controllers) |
| 45. Sound test + SFX test | ? not inventoried | Y music test 12 tracks, SFX test 22 | Y music test 12 tracks, SFX test 22 | Y music test 12 tracks, SFX test 22 (fab2c translation of m30) | Y music test 12 tracks, SFX test 22 (m30 ROM in Nested) | Y SOUND TEST + SFX TEST states | Y SOUND TEST + SFX TEST states | Y SOUND TEST + SFX TEST states (logic/ copy) | Y SOUND TEST + SFX TEST states (logic/ copy) | Y SOUND TEST + SFX TEST states (from DOS 0c727bb) |

### Audio

| Feature | CTW original (Fusion, ref) | CTW NES MMC3 (BASE) | CTW NES m30 | CTW SMS | Nested VRUN SNES | Super VRUN SNES (BASE) | SV WideMess | SV Genesis | SV DOS | SV Win95 |
|---|---|---|---|---|---|---|---|---|---|---|
| 46. Music engine | Y 5 original soundtracks (MP3/OGG) | Y NESFab MIDI-derived decoders (midi2nes.py), 4 decoders/track | Y same | ~ virtual APU -> PSG: triangle an octave down, fixed level, no noise/... | Y Nested APU emulation | Y snesmod, IT modules + sfx.it | Y snesmod (bigger SPC700 unused?) - unknown if WMS sound extensions u... | Y own Z80 driver mdsnd.s80: FM1-5 NES-wave patches, PSG, DPCM drums o... | Y OPL2 streams (oplconv.py) + PC speaker; patches not heard by user | Y software OPL2 of DOS streams; output API unknown (settle: platform/... |
| 47. Track list | Y 5 tracks | Y 12: OUTSIDE(think) BOSS SHOP BAROPEN BAROPENCRPT CHICAGOVOICES PUZZ... | Y 12: OUTSIDE(think) BOSS SHOP BAROPEN BAROPENCRPT CHICAGOVOICES PUZZ... | Y 12: OUTSIDE(think) BOSS SHOP BAROPEN BAROPENCRPT CHICAGOVOICES PUZZ... | Y 12: OUTSIDE(think) BOSS SHOP BAROPEN BAROPENCRPT CHICAGOVOICES PUZZ... | ~ 13 IT modules per S3-scope; >=11 seen (baropen baropencrpt boss chi... | ? same as SNES presumably | Y 11 songs converted (src/gen music_*.bin) | ? oplconv of the IT set; count unknown | ? from DOS |
| 48. SFX count | ? | Y 22 clips (FIRE..ZAP) | Y 22 clips (FIRE..ZAP) | Y 22 clips (FIRE..ZAP) (fab2c translation of m30) | Y 22 clips (FIRE..ZAP) (m30 ROM in Nested) | ? sfx.it; settle: sfx_table.h | ? | Y 52 PCM clips on DAC | Y sfxconv.py per-frame pitch/level on OPL voice 6 or speaker; count u... | Y from DOS |
| 49. Pitch bends | n/a | ~ decoder support merged (e5ba53f); NO roster song uses bends; audio-... | ~ same | - fab2c template from e9e3a61^1 can't do bends/duty | ~ same as m30 ROM | ? IT pitch slides presumably; not checked | ? | ? | ? | ? |
| 50. Duty command (.flp pipeline D3 dd) | n/a | Y added VRUN a36dd5e | Y same source | - fab2c template can't | ? only if Nested ROM rebuilt after a36dd5e | n/a sample engine | n/a | n/a | n/a | n/a |
| 51. .flp music pipeline back end | n/a | Y | Y | ~ embedded template, no bends/duty | ? | Y one .it path -> SNES/WMS/MD/DOS/95 (build-supervrun.ps1); Runaway p... | Y presets -> BRR | Y presets -> DAC | ~ OPL path unheard | ~ Win95 unrun |

### Input

| Feature | CTW original (Fusion, ref) | CTW NES MMC3 (BASE) | CTW NES m30 | CTW SMS | Nested VRUN SNES | Super VRUN SNES (BASE) | SV WideMess | SV Genesis | SV DOS | SV Win95 |
|---|---|---|---|---|---|---|---|---|---|---|
| 52. Input devices | Y keyboard + analog gamepads | Y 2 NES pads | Y 2 NES pads | Y SMS pad (2 jump, 1 fire, Pause=Start), Genesis pad Start | Y SNES pad | Y 2 SNES pads (NO mouse by design) | Y 2 pads + WideMess pointer port (absolute X/Y, hover/press) | Y 3/6-button: Y->A B->B A->C, Start+A or Mode = Select | Y keyboard (arrows, Z/Shift shoot, X/Space jump, Q/W L/R...) + joystick | Y keyboard + DirectInput + XInput (runtime-loaded) |

### Performance

| Feature | CTW original (Fusion, ref) | CTW NES MMC3 (BASE) | CTW NES m30 | CTW SMS | Nested VRUN SNES | Super VRUN SNES (BASE) | SV WideMess | SV Genesis | SV DOS | SV Win95 |
|---|---|---|---|---|---|---|---|---|---|---|
| 53. Lag status | n/a | Y 0 gameplay lag; sweep +2,005 / boss heal +4,367 cycles spare; ~5 la... | Y 0 lag; +2,133 / +4,414 spare | ~ lag 7.1/1000 frames (S2_SWEEP, 167 of 168 rooms, MMC3 source e693044; ... | ~ gameplay 0 lag after feedback build; 1-7 frames in title->room fade... | ~ 1P/2P self-play ~0; max loadout + Chunk lags; 2P busy rooms 0.64% p... | ? | ~ idle room 0 and a 600-frame walk+shoot in room 0: 1.00 / 0.995 tick... | ~ x86@130M area-4 run: 23 skipped pictures (19 title/area load, 3 roo... | ? fixed 60.0988 fps; not measured |

### Debug

| Feature | CTW original (Fusion, ref) | CTW NES MMC3 (BASE) | CTW NES m30 | CTW SMS | Nested VRUN SNES | Super VRUN SNES (BASE) | SV WideMess | SV Genesis | SV DOS | SV Win95 |
|---|---|---|---|---|---|---|---|---|---|---|
| 54. Test / debug builds | ~ Debug==666 level select (dead) | Y selftest_mmc3.lua, verify_rom_mmc3.py, frame_cost.lua | Y selftest.lua, 68/68 suite, Lua checks | Y S2_SWEEP (168-room lag sweep), HAZHP/NOFIRE, FAB2C_PROFILE, SHIM_AU... | Y feedback-plans verify scenarios | Y --test, SV_XCHECK, WM_PLANT, SV_INVIS_TEST, guard.c, FX/BG/UI LAB, ... | ~ --widemess build; wms tests in BrokenNes | Y MD_NEGCTL, MD_DOORTEST, MD_PROF, MD_TPROF, compare_snes timeflow | Y SV_DOS_TEST + TEST.TXT (pad/seed/startat/startroom/money/pos/probe/... | Y SV_DOS_TEST + tests/compare.ps1 vs DOS (state-exact 5 scripts) |

### Quirks

| Feature | CTW original (Fusion, ref) | CTW NES MMC3 (BASE) | CTW NES m30 | CTW SMS | Nested VRUN SNES | Super VRUN SNES (BASE) | SV WideMess | SV Genesis | SV DOS | SV Win95 |
|---|---|---|---|---|---|---|---|---|---|---|
| 55. MMC3 boots ~10 frames faster -> same inputs pick a different map | n/a | Y compare needs inputs shifted 10 + map_slot poke | n/a reference | Y start-timing calibrated to the MMC3 build (NES_TITLE_FRAME 30, was 41 ... | ? | n/a other game | n/a other game | n/a other game | n/a other game | n/a other game |
| 56. NESFab non-deterministic builds (2 px drift at tick 445 between builds) | n/a | Y freeze ROM + mlb; zero-tolerance compares useless | Y freeze ROM + mlb; zero-tolerance compares useless | Y freeze ROM + mlb; zero-tolerance compares useless (fab2c translatio... | Y freeze ROM + mlb; zero-tolerance compares useless (m30 ROM in Nested) | n/a other game | n/a other game | n/a other game | n/a other game | n/a other game |
| 57. MD coins/items/puzzle budget drift (perf_lines_now) | n/a other game | n/a other game | n/a other game | n/a other game | n/a other game | n/a reference | ? | Y accepted by user 2026-11-06; cash tolerance 30 | - state-exact vs SNES (fixed beam V 226) | - state-exact vs DOS |
| 58. MD colours: ladder rails pink, side doors, 9-bit CRAM dark colours | n/a other game | n/a other game | n/a other game | n/a other game | n/a other game | n/a reference | n/a | ~ remap 62c4f90 made doors green + ladders = SNES; rails parked; dark... | - exact palette (15-bit cache) | - |
| 59. DOS/95 parallax/glyph animation phase differs from SNES | n/a other game | n/a other game | n/a other game | n/a other game | n/a other game | n/a SNES lags ~227 frames in loads; phase counter is vblank-driven | ? | ? | Y known difference | Y inherits DOS |
| 60. Area-door re-entry freeze (gs_set fix) | n/a other game | n/a other game | n/a other game | n/a other game | n/a other game | n/a SNES gsta re-runs init natively | n/a | Y fixed 71cf59b (gs_set) | Y fixed 0c727bb | Y built from DOS 0c727bb |
| 61. Port endian/backport fixes back in SNES (MD BACKPORT 1-8, DOS #1 stat_of) | n/a other game | n/a other game | n/a other game | n/a other game | n/a other game | Y in the base since 1c35e08 (include/plat_endian.h; SNES ROM byte-ide... | Y merged (c60964f); SNES + .wms builds byte-identical | Y resynced: BACKPORT 1-8 come from the base, LOCAL.txt empty (ce31650... | Y stat_of fix from the base (6d32c40); TRACE identical | Y from DOS 6d32c40; compare.ps1 46 probe lines identical |
| 62. Shop-exit freeze on real SNES (suspected DMA/HDMA lock-up) | n/a other game | n/a other game | n/a other game | n/a other game | n/a other game | ? awaiting console test; settle: user flash-cart run | n/a | ? | n/a | n/a |
| 63. SMS view hides 32 px of each room | n/a | n/a | n/a | Y dead-zone follow 0-40 | n/a | n/a other game | n/a other game | n/a other game | n/a other game | n/a other game |
| 64. Nested enhanced title shows NES font ~5 frames | n/a | n/a | n/a | n/a | Y accepted side effect of f13a2f9 lag fix | n/a other game | n/a other game | n/a other game | n/a other game | n/a other game |
| 65. SMS README stale (says S0 skeleton) | n/a | n/a | n/a | Y doc drift only | n/a | n/a other game | n/a other game | n/a other game | n/a other game | n/a other game |
| 66. Nested still on mapper 30 (not the MMC3 main build) | n/a | n/a | n/a | Y moved to the MMC3 source 2026-11-10 (e693044): fab2c translates mapper... | Y move to MMC3 planned (cards nestedasm, nestedmmc3) | n/a other game | n/a other game | n/a other game | n/a other game | n/a other game |

## Super VRUN only

CTW has none of these:
- **Two-player co-op:** shared shop price, revive, per-player slots, CONTROLS2.
- **Weapon modifiers** (Wall/Chunk/Cross/Wave), with the original CTW balance. The NES demake cut them.
- **Five held items** (Corner, Field, Heal, Upgrade, Laser) on L/R, plus 9 world pickups. The NES removed them for ROM space.
- **Shop:** a 9th item, FIELD. SPEED is a real stat (pierce); on the NES it only recolours the player.
- **Screens:** a story screen, pause stats and a pause area map. WideMess adds a live side map and a pointer.
- **Options tabs:** TILESET, BG LAB, FX LAB, UI LAB.
- **HDMA effects:** gradient, line scroll, mosaic, colour math, death ripple.
- **Bullet pool:** 24 bullets vs 8.
- **Own sound engines on the ports:** MD Z80 FM+DAC, DOS OPL2/speaker.

The other way round, CTW has things Super VRUN lacks or may lack:
- Per-tier minibosses (unknown on SV).
- 4 control schemes with in-game Start+Select cycling.
- Duty-command music (n/a on SV's sample engines).

## Settled by running (2026-11-10 pass)

Evidence tags in the JSON for this pass end in "(run)" or "(code, settle pass)".

| Unknown | Result | How |
|---|---|---|
| Invisible-enemy fix on WMS/MD/DOS/95 | present on all four | code: WMS roomtrans.asm:1012, MD asmref_room.c:1304, DOS/95 asmref_room.c:1315 |
| MD save -> CONTINUE | works | **run**: headless BrokenNes MCP, Releases/SuperVRUN_MD/2026-11-09_62c4f90; Start f530, Select -> title CONTINUE 1P f850, SRAM $40.. = 3C 0B 00 05 05 ..., Start -> in game f1050. Same power session only. |
| MD pause map | missing, and no stats either | **run**: screenshot f700 of the same session |
| stat_of on SNES/WMS/MD | latent on SNES/WMS (build-guarded), absent on MD | code |
| SV track list | 12 tracks, same on SNES/WMS/MD/DOS/95 | audio.h TRACK_* |
| SV SFX list | 52 sounds, same table everywhere | sfx_table.h SFX_NSOUNDS |
| SV room count, physics | N_ROOMS 168, GRAVITY 48, MAX_FALL_SPEED 768 everywhere | code |
| Win95 audio API | WinMM waveOut | plat_win.c:461 |
| Nested duty | none: only ROM predates a36dd5e | release file date |
| CTW SMS bends/duty | now has both | VRUN_CTW_SMS_dev 783c093 |

Not settled in this pass:
- Title tunnel and animation clocks on the CTW versions (needs side-by-side NES/SMS/Nested runs).
- Invisible-enemy **run** check (needs the TEST BUILD per port).
- MD save across a power cycle.
- Win95: cannot be run here.

### Pass 3 runs (2026-11-10)

Screenshots: `C:\Users\philt\AppData\Local\Temp\claude\724ae6c77c6890ea\a47307b0-02aa-4683-aff7-7419181d3498\scratchpad\cvl_runs\` (scratch; copy them out if they should be kept).

| Check | Result | Evidence |
|---|---|---|
| CTW title tunnel | **MMC3 yes, SMS yes, mapper 30 no, Nested no** (bug list #8 CONFIRMED) | headless MCP, all four at f299-301: ctw_mmc3_title_f299.png, ctw_sms_title_f300.png, ctw_m30_title_f299.png, ctw_nested_title_f301.png. SMS is the released final3 (11-07), not 783c093. |
| MD save across a power cycle | **works** | Save in one session (SRAM `..`), new power-on session with those bytes: CONTINUE 1P lit at f260, resumes in the same room at f510. Control with blank SRAM: CONTINUE greyed. md_powercycle_title.png, md_powercycle_continue.png, md_fresh_title_no_save.png |

Blocked or not done in pass 3:
- **Invisible-enemy run check:** building the SV_INVIS_TEST ROMs (`SV_CFLAGS=-DSV_INVIS_TEST bash build.sh` in a scratch clone) was refused by the agent's worktree-isolation guard, so no test ROM could be built. No released ROM has the hook, and a plain input script can't set up four live ghosts at a right-hand exit. This stays (code).
- **CTW SMS 783c093:** not built, for the same reason.
- **CTW animation clocks:** frame-by-frame tile timing in game was not measured.
- **Sound tests (12 tracks / 52 SFX), room count, physics measurements:** not run. These stay (code).
- **Lag and shop-exit rows 53/57/59/62:** not run.

## Differences that look like bugs (or unowned drift)

1. **SV Genesis has no pause map. CONFIRMED (run).** The MD pause screen is only PAUSED / SELECT: SAVE & QUIT, with no map and no stats. Since MD has no `pausemap.c`, the `stat_of` bug is **REFUTED** there: the code is absent.
2. **The `stat_of` fix never went back to SNES/WideMess. CONFIRMED LATENT, not live (code).** `tools/srccheck.py` plctxcheck fails the build if plctx.c's layout drifts from the PLC_* offsets, so it cannot break silently on 816-tcc.
3. **The invisible-enemy fix (9e13885): REFUTED as a gap (code).** WideMess master contains 9e13885 (`_rtn_os_hide`); MD, DOS and 95 clear X bit 8 in `asmref_room.c` `rtn_oam_step` (gotcha #115 comment). Not yet re-run: that needs the run_invis_compat TEST BUILD.
4. **The MD save system: REFUTED as a bug (run).** Pause+Select wrote SRAM and CONTINUE 1P resumed the run (see below). Original note: The docs plan SRAM at $200001, but no save -> CONTINUE run is recorded on MD. DOS and 95 have one.
5. **The MD sync point (45efdc6) is behind DOS/95 (22bd670).** Any SNES change between those two commits is missing on MD, except hand forward-ports such as the Field fix. That may include cgram-uninit. Diff the two SNES commits to settle it.
6. **CTW SMS bends/duty: FIXED since (code).** VRUN_CTW_SMS_dev 783c093 builds from live VRUN a36dd5e with bend decoders and the duty opcode. Nested is now the CTW version without duty (its only ROM is from 2026-11-07). Original note: The fab2c template predates them, so new `.flp` music written on the NES will sound different on SMS. The fence reports it, but the ledger should track it.
7. **MMC3 animated tiles share one 83 ms clock.** On m30, SMS and Nested each anim keeps its own speed. This is forced by the hardware, but the base now looks different from the other versions.
8. **The CTW title tunnel is on MMC3 and SMS but not on mapper 30 or Nested. CONFIRMED (run, pass 3).** Nested is still built from m30; the move to MMC3 is planned.
9. **Lag:**
   - CTW SMS lags 9.1 frames per 1000; NES has 0. The HUD band causes about 40% of it.
   - Super VRUN MD runs about 0.91 game ticks per frame vs 1.00 on SNES.
   - MD big-room builds take about 50 frames vs 8 on SNES.
10. **MD colour and budget drifts:**
    - Coin and item budgets follow the 68000's line count. The user accepted this drift.
    - Ladder rails are still pink.
    - Dark gradient colours round to black.

Also noted:
- The CTW SMS README is stale: it still says "S0 skeleton".
- All CTW ports keep the original's Luck price typo (12800), and Luck tiers of 64000 and up can't be reached with a 16-bit score.

## Proposal: ledger columns going forward

One row per polish item (not per feature), added whenever the base changes:
- **Identity:** `id`, `date`, `game`, `base_commit` (VRUN or SuperVRUN_dev commit), `category`, `summary`.
- **`kind`:** one of:
  - gameplay: travels through a re-sync or re-translation
  - platform: must be redone per port
  - audio
  - art
- **Per version:**
  - `status`: todo / done / n.a. / wontfix-by-user
  - `port_commit`
  - `evidence`: code / run / note, plus a path
  - `verified_by`: compare, screenshot, or user hardware test
- **Per port, `sync_point`:** the SNES commit in `logic/SOURCE.txt`, the fab2c input commit for SMS, or the NES ROM sha for Nested. A re-sync can then mark many rows done at once.
- **`user_decision`:** accepted drifts, such as MD coins or the SMS view crop, so they are never reported again as bugs.

Keep `inventory.json` as the feature baseline. Change a cell only when there is new evidence, never on assumption.

## Unknowns worth settling first

- Invisible-enemy bit-8 clear on WideMess, MD and DOS.
- MD SRAM save.
- The full SNES track and SFX lists (`sfx_table.h`, `audio/`).
- The SV room count and physics constants (`map_data.c`, `player.c`).
- The Win95 audio API.
- Duty support in Nested: is there a build after VRUN a36dd5e?

## 2026-11-10 backport round (Super VRUN)

- **Backports to the SNES base (SuperVRUN_dev 1c35e08):** Genesis BACKPORT 1-8 and DOS #1 (`stat_of`) through `include/plat_endian.h` (le16, put_le16, HI8, PLC_LIVE, PLC_LIVE_U8, PLAT_BANK_OF). On the SNES they expand to the old native code: the SNES ROM is byte-identical (md5 885413ee...), and so are WideMess's SNES and .wms builds (merged as c60964f). No hardware-only fix needed to stay local: DOS #2 (SOUND OUT) and #3 (rtn_oam_dma) are platform-layer items and stay in the DOS repo.
- **Genesis resync (SuperVRUN_MD_dev ce31650):** logic/ = SNES 1c35e08, nothing local. Pause map + stats run; save across a power cycle, 2P, compare_snes all pass. RAM: +2.1 KB .bss, paid by moving three platform buffers into SGDK's heap after the boot (_bend $E4B8). Open: the pause darken/mosaic, the options panel picture, and `timeflow play-compare` drifts in room 1 (y 13 px at tick 3250, after 15 matching events; the build before the resync drifts too, at tick 3440 after 5 events).
- **DOS (6d32c40) and Win95 (21aa766):** logic/ at SNES 1c35e08; the x86 trace is identical to before and compare.ps1 (DOS vs 95) matches 46/46 probe lines.
