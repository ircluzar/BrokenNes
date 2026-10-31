# VRUN on Sega: three ports (next week, from 2026-11-02)

Status: plan (interview of 2026-10-30). Nothing built yet.

| Port | Original | Sega console | Screen |
|---|---|---|---|
| **Vaults of Obsolete Code** | `VRUN_VOC_dev` (Game Boy, tick-identical to Eternal Degrade) | **Game Gear** | 160x144 = the Game Boy's: the same view |
| **Corrupt The World demake** | VRUN NES (`VRUN_Nes_Dev`, NESFab) | **Master System** | 256x192 vs the NES's 256x240: the original's view kept, the HUD / borders rearranged |
| **Super VRUN** | `SuperVRUN_dev` (SNES, with 2P) | **Genesis / Mega Drive** | 320x224 vs the SNES's 256x224: the original's view kept, the extra width as borders / HUD |

## Decisions (interview, 2026-10-30)

1. **Fidelity: faithful feel, adapted.** The same game, free to adapt physics and timing where the Sega hardware makes that natural
   (not tick-identical like Vaults on the Game Boy).
2. **Screens: keep each original's view;** the Genesis's extra width and the Master System's missing height are handled with borders
   or by moving the HUD.
3. **Toolchains (downloads approved):** devkitSMS + SDCC (Master System, Game Gear), SGDK (Genesis), and assembly where needed
   (WLA-DX, already pinned for the SNES). Pinned with sha256 in BrokenDev's toolchains manifest like GBDK and cc65.
4. **Hardware:** the user's Genesis flash cart, which runs all three consoles' games (the Genesis natively, Master System and Game
   Gear through its backwards compatibility).
5. **The cross-console comparison checks game logic with tolerances:** the same inputs on the original and on the port; rooms visited,
   items, deaths, clears and key positions must match within tolerances set per game; where they drift it is flagged, and the user
   decides whether it is an intended adaptation.
6. **Order:** Vaults on the Game Gear first (the same screen as the Game Boy, the Z80 family of the Master System), then the
   Corrupt The World demake on the Master System (sharing devkitSMS), then Super VRUN on the Genesis (the biggest).
7. **Sound: converted from each original.** Master System / Game Gear PSG (three squares + noise) from the NES / Game Boy versions;
   Genesis FM + PSG from the SNES version's music; effects re-fitted like Vaults' Fourier pass.
8. **Super VRUN keeps 2P** on the Genesis (two pad ports).

## The first tool: the cross-console comparison (replaces Timeflow T6)

Timeflow T6 (a self-filling gameplay corpus) is replaced by what these ports need; T7 (proof on a real bug) is these ports themselves.
- **Record on the original** (Timeflow T1: Game Boy, NES, SNES), **replay the same inputs on the port** (the Sega consoles have
  Timeflow and the debugger since T0).
- **Map the two games' variables** by name (each project's symbols) and the two tick rates (e.g. Vaults' 24 Hz ticks on both; the
  NES's 60 Hz vs the Master System's 60 Hz; SNES vs Genesis frames), then compare per game-logic event: room changes, items, deaths,
  clears, key positions, within the per-game tolerances (a manifest section).
- **Report the first drift** with both sides' state, and open both sides at that point with T3 profiling.
- **Re-check after rebuilds** (the one part of T6 kept): when a port is rebuilt, its recorded sessions replay and the first frame the
  change altered is reported.

## Milestones

| Milestone | Delivers |
|---|---|
| **S0** | Toolchains pinned (devkitSMS + SDCC, SGDK), BrokenDev projects for the three ports (build, verify, compat on BrokenNes + other emulators, Lite), the cross-console comparison tool with the re-check. |
| **S1** | **Vaults on the Game Gear**: the game, its palettes, the PSG sound converted from the Game Boy version, comparison within tolerances, the flash-cart test. |
| **S2** | **Corrupt The World demake on the Master System**: the view kept in 256x192, the game, PSG sound from the NES version, comparison, flash cart. |
| **S3** | **Super VRUN on the Genesis**: 1P and 2P, FM + PSG music from the SNES version, comparison, flash cart. |

## Open questions (to settle per port, in their own interviews)

1. Per game: which adaptations are wanted (e.g. a physics rate on the Master System), and the comparison's tolerances.
2. The Master System's 192 lines vs the NES's 240: which rows of the NES view the HUD gives up, or a scrolling view.
3. The Genesis's sound driver (SGDK's XGM, or a port of the music engine).
4. Whether the Game Gear version uses the Game Gear's colours (4096) or stays close to the Game Boy Color's look.
