# VRUN: Vaults of Obsolete Code for the Game Boy

Plan of 2026-10-23, written after studying the original and an interview with the user. This is the **third sprint of the week of
2026-10-26**, after WideMess milestone 1 and the BrokenDev debugging toolkit (`docs/projects/brokendev-debugging-plan.md`).
It may start early if there is spare capacity. **Milestone V0, the skeleton, is built** (2026-10-24, as the toolkit's D2): the workspace
`PROJECTS\VRUN_VOC_dev`, GBDK-2020 4.5.0, the hello world on the final cartridge and the BrokenDev project `vrun-voc`. The game itself is not.

## What it is

A **faithful 1:1 port** of *VRUN: Vaults of Obsolete Code*, the mission and standalone spinoff in Eternal Degrade (the C# game in
`PROJECTS\vrun_StepBack`, standalone `Legacy_VRUN.exe`, v129). It is itself a spinoff of Corrupt The World: it uses CTW's 113 room
templates (byte-identical files) and the same kind of map generator. Its physics come from Eternal Degrade's caverns, most CTW
objects become nothing or spikes, and there are no enemies.

The goal of a run is to collect clovers. Each dead-end room holds one ♣. Eating breakable code (░) slows you like mud. Spikes cost a
life, and you start with 0 lives. ♦ materials (16 make a life) and ♠ health keep you going. Portals, an automatic wall jump and a
mid-air dash that freezes time until you choose a direction complete the moves.

## How the original works (study of 2026-10-23)

| | Vaults of Obsolete Code (Eternal Degrade) | VRUN NES, for comparison |
|---|---|---|
| Rooms | CTW's 113 templates, 16 x 14 cells; layer = grid + 1 over BASE | The same templates; layer = tier + 1 |
| Map | Generated every run, unseeded: 7 grids of 13 / 19 / 25 / 31 / 37 / 43 / **241** rooms = **409 rooms**; random walks left, right or up (never down); the 3-clover mission uses the fixed map `VRUN3.txt` (54 clovers) | 32 fixed maps baked offline, 6 tiers, 168 rooms |
| Dead ends | **Clovers** (the goal) | Shops |
| Objects | walls # (shimmer) and blocks [ ] solid; spikes ▲▼ (CTW spikes, bad sectors, tears, red grids); ░ breakable, eaten, halves your speed; ♦ +5 materials; ♠ +2 lives; ☼ portal pair; everything else empty, doors are openings, **no enemies** | All of CTW: enemies, doors, lasers, tears, bosses |
| Room memory | What you eat stays gone for the whole run | Rooms reset on entry |
| Lives | Start at 0; ♣ +3, ♠ +2, 16 materials +1; below 0 the run ends | 5 HP |
| Physics | **24 ticks per second**, at most one character per axis per tick (a character is half a cell wide, a cell tall), fractional "push" accumulators; fixed jump (5 cells, 22 ticks); automatic wall jump; mid-air jump freezes time, then a direction dashes | 60 Hz pixel physics, 16 x 16 player, variable jump, hover, shooting |
| Controls | Left / Right set a running direction that stays, Down brakes, one key event per tick (no repeat, no release), jump = Enter / Space / pad B, Backspace = abort (first press never, then 30%) | Held d-pad |
| Screen | A 32 x 16-character box (HUD row, blank row, 32 x 14-character room), characters 9 x 17 px; flip-screen rooms, no scrolling | 256 x 224 room + HUD below |
| Colour | Walls take the colour of the grid's song (20 songs, 12 colours); grid 7 is dark: invisible walls, random dark-grey glitter every frame | 4 palettes per room |
| Sound | 22 MP3 tracks (title, 20 level songs, clear) and 16 MP3 sound effects | NES driver |
| Modes | 18 clovers (standalone), 3 (mission, VRUN3), Dashless (Down x5 on the title), 5-9 (7 ± 25%, from a cavern exit) | Map choice, START AT |

Sources: `StepBack\Assets\Microprograms\Caverns\MP_VirusRun.cs` (the game), `RunnerCommon.cs` (the constants: accel 0.10, gravity
0.120, jump -1.20), `StepBack\WorldEmulator\CavernWorld\CavernWorkFlow.cs` (`RunVRUN`), `VRUN_GenStandard\` (the generator),
`StepBack\Assets\Microprograms\Interaction\MP_VRUN.cs` (the title), `MP_BonusVRUN.cs` (the clear screen), and `BUILD\DATA\`: `DEPTHS\`
(the templates), `ANIM\VRUN3.txt`, `MUSIC\`, `SFX\`. Note that `BUILD\` is not in that repo's git.

## Decisions (interview, 2026-10-23)

| Topic | Decision |
|---|---|
| Faithfulness | **1:1, quirks included.** The quirks the study found stay: corner slip, portal snap-back, half-cell double hits, dash-push bounces, leftover push, the start-room exceptions, never-down towers, the clover sound mismatch, the abort lottery. |
| Screen | **The whole room at 8 px per cell: 128 x 112.** **HUD below the room**, side panels in the level colour, a message line. |
| Look | **The original's characters**, 4 x 8 px each (one cell = 2 characters, as in the original): @, # and the shades, [ ], ░, ▲▼, ♦ in half cells, ☼, ♠, ♣. |
| Movement | **Authentic 24 Hz steps**, as the original. |
| Maps | **32 fixed maps baked on the cartridge**: map 0 = **VRUN3** (the original's own map), maps 1-31 made offline by the original generator from seeds. |
| Map choice | **Random each run**; the map number shows on the clear screen. |
| Modes | **TUTORIAL** = 3 clovers on VRUN3 (the user: "should be considered a tutorial mode"), **RUN** = 18 clovers, **SHORT RUN** = 5-9 clovers, and **Dashless** (Down x5 on the title). |
| Mode select | **A small menu on the title** (TUTORIAL / RUN / SHORT RUN); Down x5 still toggles Dashless. |
| Controls | **The original's scheme, plus Start to pause.** Left / Right set the direction, Down brakes, A jumps (in mid-air: freeze, then the d-pad dashes), B jumps too, Select is the abort lottery. |
| Saving | **Suspend a run** on the cartridge's battery RAM (switching off keeps the run). |
| Clear screen | **As intended**: "VRUN Cleared", the stats (blocks, spikes and symbols eaten), the bonus 25 + spikes + 3 x symbols + blocks, the map number, "You found N Ethereal Clovers". |
| Messages | **Shown** in the message line (the standalone wrote them to a header row outside its window). |
| Dark grid | **Faithful glitter**: invisible walls, random characters glittering every frame. |
| Music | **All 22 tracks**, **arranged by the user in FL Studio** (previewing with the BrokenNes2 plugin's Game Boy instruments), converted by BrokenDev. |
| Sound effects | **Reproduced from a Fourier analysis of the original samples** (the user's direct instruction): the 16 MP3 effects analysed frame by frame and rebuilt on the Game Boy sound chip. |
| Proof | **A reference built from the original code**, compared tick by tick with the Game Boy build. |
| Original files | **Copy what's needed** into the new workspace, recording each source. `vrun_StepBack` is never written to. |
| Hardware | **The user has a Game Boy flash cartridge**: hardware test ROMs at milestones. |
| First slice | **The tutorial**: VRUN3's nearest 3 clovers are 5, 6 and 9 rooms away, all in the first 13-room grid. |
| Earlier decisions | GBDK-2020 (C plus assembly); one ROM for the Game Boy Color that also plays on the original; compat on Mesen and BizHawk (Gambatte, SameBoy); workspace `PROJECTS\VRUN_VOC_dev` with its own git (local commits only). |

## The design

### Cartridge

- **MBC5** with a **1 MB ROM** (64 banks of 16 KB) and **32 KB battery RAM**.
- **Dual mode** (the Color flag set, palettes on a Color, shades on an original).
- Common flash cartridges support this setup. The exact cartridge is checked against the user's.

### Screen

- **The room:** 16 x 14 cells = 32 x 14 characters of 4 x 8 px = 128 x 112, at x 16-143, y 0-111.
- **Side panels:** 16 px each, in the level colour.
- **Below the room:** a blank row, then the original's HUD row (32 characters = 128 px), the message line and a blank row.
  - HUD: ♣ clovers / goal, + lives, the 15-character materials bar.
  - Messages: the original's texts.
- **Background tiles:** each 8 x 8 tile is a **pair of characters**. A room uses fewer than 100 pairs, including:
  - wall shimmer combinations;
  - half-eaten cells;
  - half-cell ♦;
  - portal blink frames;
  - dark-grid glitter.

  The Game Boy has 384 tiles (768 on a Color).
- **The player** (@, and ‼ while aiming a dash) is a sprite drawn in one half of an 8 x 8 sprite, so it can sit in either half of a cell.
- **Colour:**
  - **Game Boy Color:** the original's 16 console colours mapped to background palettes, with walls in the song's colour as in the original.
  - **Original Game Boy:** four shades, black ground as in the original, walls one step up, objects and the player brightest.
- **Room changes are instant**, as in the original. The next room is written into the second background map over the 2-3 frames
  of a tick, then shown at the next tick. Nothing is visible in between.
- **Effects:**
  - **Wall shimmer** (every 7 ticks, per character: # to ▓ 30%, ▓ to ▒ 57%, ▒ to # 42%) and **dark-grid glitter** (every frame,
    every other wall character, 30% dark grey) are tile-map writes from a prepared queue, spread over frames if a vblank can't hold them.
  - The portal blinks.
  - The damage flash is a white flash.
  - The box opening and dissolving animations.
  - The abort tilt becomes a scroll-register shake.

### Timing and physics

- **The game ticks 24 times per second**, as the original's engine. The Game Boy runs at 59.73 frames per second, so a fixed-point
  accumulator ticks 24/59.73 per frame (alternately every 2 and 3 frames). The screen changes only on ticks, as in the original.
- **The movement code is a port of the original's**:
  - the latched direction and brake;
  - velocities and fractional push accumulators;
  - the start boost;
  - horizontal then vertical resolution;
  - the automatic wall jump;
  - the dash (freeze, then up / down / left / right / cancel);
  - mud (░ halves the speeds);
  - portals (the first other portal scanning row by row, speeds x 1.25);
  - hard landings;
  - the free jump when entering from below;
  - every quirk.
- **The hardest part is the numbers.** The original uses C# `double` arithmetic and compares sums against thresholds (for example
  "push reaches 1"). Ten steps of 0.1 do not add up to exactly 1.0 in `double`, so a naive fixed-point port would cross those
  thresholds on different ticks.
  - The port uses integer units chosen so that every threshold crossing happens on the same tick as in the original, for every state
    the game can reach.
  - The reference proves it by exploring the reachable states. Where no integer unit can match, the port reproduces the original's
    rounding with small tables.

### Maps

- **The baker** (C#, in the new workspace) runs a copy of the original generator (`VRUN_GenStandard`).
  - The original seeds each walk from the clock; the baker gives each walk a fixed seed instead.
  - Maps 1-31 come from seeds, and map 0 is read from `VRUN3.txt` (the original's own text format, 21 lines per room).
  - The original's generator retries forever when no template fits. The baker retries offline and records the seed that worked.
- **Storage:**
  - A **layout library**: each (template, layer) a room uses, converted to Vaults of Obsolete Code's cell kinds (4 bits per cell,
    stored raw or run-length coded).
  - A **record per map**: per room the layout, the song (wall colour and music), the four neighbours, and the flags (start room, clover room).
  - An estimate: about 60-90 KB of layouts plus about 4.5 KB per map, so about 230 KB for 32 maps.
- **Room memory:** only cells that can be eaten need a bit (breakables, spikes, ♦, ♠, ♣): about 6,000 cells on VRUN3, under 1 KB per
  run. It lives in work RAM and in the battery RAM for the suspend save.
  - Per-cell bits are enough, because the original writes 0 into the whole cell when you eat it.
  - The original's half-eaten look lasts only until the room reloads, so it is kept in the room's screen state.
- **Equivalence check:**
  - every baked room, rebuilt the way the Game Boy rebuilds it, must equal the original generator's output for that seed;
  - map 0 must equal `VRUN3.txt`, cell for cell.

### Game flow

- **Title:**
  - The original's title, rebuilt at 4 x 8 characters: the VRUN logo, "Vaults of Obsolete Code" on the dark red bar, the random verb
    (Retrieve / Recover / Trace / Explore / Probe), "{year} Redscientist Labs".
  - The flickering character that runs across when you start.
  - The new menu: TUTORIAL / RUN / SHORT RUN.
  - Down x5 for Dashless.
  - The title music.
- **A run:**
  - the map: map 0 for the tutorial, random for the others;
  - the goal: 3, 18, or 7 ± 25%;
  - the box opening;
  - the start room's player marker.
  - Then: clovers, lives, materials, the win at the goal, and the end below 0 lives.
- **Pause:** Start. The abort lottery stays on Select.
- **Clear screen:** as intended (above), with the clear music.
- **Suspend:** a run in progress is kept in battery RAM. It holds:
  - the map, the room and the player's exact state;
  - the tick phase;
  - lives, clovers and materials;
  - the eaten bits;
  - the mode and the dash flag;
  - the random-number states.

### Sound

- **Sound effects: the Fourier tool.** A C# tool in BrokenDev (no download; ffmpeg is already installed for decoding).
  1. It decodes each of the 16 original effects:
     - Viral_Open, Viral_Close;
     - TunnelRun_Jump, TunnelRun_WallJump, TunnelRun_Land, TunnelRun_EnterVRUN, TunnelRun_Die;
     - CrashPrepare;
     - CoinGet;
     - VRUN_BreakBlock, VRUN_Teleport, VRUN_HealthGet, VRUN_DamageRespawn;
     - Bonk, Menu_Back, Text_Item.
  2. It runs a short-time Fourier analysis, one window per Game Boy frame. It measures the pitch, the harmonics (which suggest a
     square wave's duty: 12.5, 25, 50%), the noisiness (spectral flatness) and the volume envelope.
  3. It maps each effect to Game Boy channel settings, frame by frame:
     - square: pitch, duty, envelope, sweep;
     - wave: a 32-sample waveform built from the harmonics;
     - noise: the rate and the 7- or 15-bit mode.
  4. It plays the result through BrokenNes's Game Boy sound chip and compares the two analyses. The settings are refined until the
     difference stops shrinking.
  - The result is a per-frame register script per effect, played by the sound-effect driver. Effects take a channel from the music
    and give it back.
  - Some originals are long (VRUN_HealthGet, Viral_Open, TunnelRun_EnterVRUN); the analysis shows how much of each is really heard.
- **Music:** all 22 tracks, arranged by the user for the 4 Game Boy channels in FL Studio, previewed with the BrokenNes2 plugin's
  Game Boy instruments. BrokenDev converts the MIDI into the game's music driver, like VRUN NES's path. Placeholders until the
  arrangements arrive.

### Proof that it is 1:1

- **The reference:** the original's VRUN code (movement, collision, pickups, lives, materials, room changes; copied from
  `MP_VirusRun.cs` and `RunnerCommon.cs`) in a headless C# program.
  - Given a map, a start and an input script, it writes every tick's state: position, velocities, pushes, lives, clovers, materials,
    eaten cells, room.
- **The comparison:**
  - The Game Boy build runs the same input script in BrokenNes (headless, through the debugging toolkit's MCP server and debugger,
    reading the state by symbol).
  - The first tick where the two differ is a bug, quirks included.
  - Input scripts come from recorded play, from random input (the chaos bots), and from every short input sequence from a set of starts.
- **Randomness:**
  - Things the original leaves to chance (shimmer, glitter, the abort lottery, the title verb) are checked by their rates, not tick by tick.
  - Movement has no randomness, so it is compared exactly.
- **The other checks:**
  - the map equivalence check;
  - the effects' spectral comparison;
  - the toolkit's Game Boy hardware checks in `brokendev verify` (no LCD switch-off outside vblank, no VRAM writes during drawing,
    OAM DMA from HRAM...);
  - compat on Mesen and BizHawk (Gambatte, SameBoy), BrokenNes desktop and Lite;
  - the flash cartridge at milestones.

## Milestones

| | Milestone | Done when |
|---|---|---|
| **V0** | **Skeleton** (= debugging toolkit D2) | `VRUN_VOC_dev` exists with GBDK-2020. The hello world has the final cartridge setup (MBC5, 1 MB, 32 KB battery RAM, dual mode). It is a BrokenDev project that builds, runs and plays on BrokenNes, Mesen and BizHawk. **Done 2026-10-24** (the debugging plan's "D2 as built"): GBDK-2020 4.5.0 pinned in BrokenDev; `VRUN_VOC_dev` (own git, local commits) builds `vrun_voc.gbc` (text, a sprite on the d-pad, a beep on A, a battery boot counter, work RAM cleared at start-up); project `vrun-voc` passes verify with no hardware finding and works on BrokenNes (desktop, Lite, Workshop), Mesen, BizHawk Gambatte and SameBoy. |
| **V1** | **Reference and maps** | The original's code and files are copied with their sources recorded. The C# reference writes per-tick states. The baker makes the 32 maps and passes the equivalence check. The 4 x 8 character set and the tile-pair builder exist. |
| **V2** | **The tutorial** | TUTORIAL is playable on VRUN3: the 24 Hz physics and every quirk, matching the reference tick for tick; rooms, spikes, eating, ♦ ♠ ♣, lives, materials, portals, dash, HUD and messages; room memory, shimmer, instant room changes and the clear screen. First hardware test ROM. |
| **V3** | **Sound effects** | The Fourier tool and all 16 effects on the Game Boy sound chip, each with its spectral comparison; the effect driver in the game. |
| **V4** | **The whole game** | RUN, SHORT RUN and Dashless; the title and its menu; random maps; the dark grid's glitter; pause; suspend; the clear screen's stats and bonus. The reference comparison passes on all 32 maps. |
| **V5** | **Music** | The music driver and the MIDI converter. The user's 22 arrangements in the game as they arrive. |
| **V6** | **Release** | BrokenDev checkup green (verify, hardware checks, compat on every emulator, Lite), a hardware test on the flash cartridge, a release build. |

## Open questions (to settle as the work reaches them)

1. Which maps RUN and SHORT RUN draw from: all 32, or only the 31 generated ones (keeping VRUN3 for the tutorial).
2. Seeds for maps 1-31: taken in order, as on the NES, or picked so that 18 clovers are reachable before the dark grid. About 20% of
   generated maps need it.
3. The tick rate: confirm the original's 24 per second (`GameEngine.cs:340`), and whether its frame pacing has any jitter that matters.
4. How the original's `double` rounding is reproduced, if integer units cannot match it everywhere.
5. The music driver (a standard Game Boy driver such as hUGEDriver, or our own) and the MIDI rules for the user's arrangements.
6. The title's layout: the standalone window is 33 x 17 characters, which at 4 x 8 px is 132 x 136 and fits the screen, so it can be
   rebuilt character for character.
7. The user's flash cartridge model (MBC5 and battery RAM support).

Mock-ups from the study: `docs/projects/vrun-voc-mockups/` (a real room, `abby_AccuracyArena` at tier 3, converted with the
original's rules; placeholder art).
