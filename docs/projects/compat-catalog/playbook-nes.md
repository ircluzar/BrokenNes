# Playbook: NES / Famicom

Accuracy core: CPU_FIX / PPU_FIX / APU_FIX (open_rom's default). Reference: Mesen 2.1.1, offset usually 0 (Mega Man 2: -10).
Tool reference: [../compat-agent-playbook.md](../compat-agent-playbook.md).

## Controls

Pad: `Up Down Left Right A B Select Start`. Convention in most games: A = jump / confirm, B = attack / run / cancel, Start = begin and
pause, Select = menu cursor or weapon switch. Two players: `buttons2`. No Zapper, Power Pad, Vaus or Famicom keyboard in the agent loop.

## Starting a game

- Title screens appear 60-400 frames after power-on. Wait with `play_step frames=120` (no buttons), look, then press Start for
  **8-12 frames** (`buttons=[Start] frames=10`), then `frames=30` with none (pilot 2026-11-15: 5-6 frame presses were dropped on
  Arkanoid, Mega Man 3, Punch-Out, Batman and Ninja Gaiden titles). Nothing happened: wait 60 frames, press once more. Dry run 2026-11-15: Mega Man 2's stage select ignored a
  1-2 frame Start that arrived as the menu appeared; a 6-frame press after 10 idle frames worked. Many titles also ignore a press during
  their fade-in: press again after 30-60 frames.
- A menu with "1 PLAYER / 2 PLAYERS": the cursor starts on 1 player; just Start. A menu that needs Select to move: Select moves, Start picks.
- Attract mode: if you wait too long on the title (often ~600-1500 frames) a demo plays. The demo is gameplay too (good for picture
  comparison) but do not mistake it for your game: your inputs do nothing in it.
- Name entry (RPGs: Dragon Warrior, Final Fantasy, Zelda): pick 1-4 letters with A, then move to END / OK (often bottom right) and A,
  or Start where the game allows it. Zelda 1: register your name, Start; Zelda II: as in the certification route (Start, Select to
  REGISTER, Start, A to type a letter, Select x3 to END, Start, Start).
- Battery games start with no save on a fresh session (the MCP session has its own empty save folder). Pick the first empty file.
- Passwords: only use one from the game's own manual/common knowledge for a T3 resume if the game has no battery; never for T2.
  Common: Contra's code (Up Up Down Down Left Right Left Right B A Start, on the title) gives 30 lives: allowed for T3 only, note it.

## Per-game menu recipes (harvested from the NES pilot, 2026-11-15)

Every press below is 10 frames, with 20-30 idle frames after it; look at the picture after each. If a recipe does not match what you
see, follow the picture, and write what worked in your notes (the next agent gets it).

- **The Legend of Zelda**: title, Start -> file select (a heart cursor at file 1). Select moves the heart down: file 1, 2, 3,
  REGISTER YOUR NAME, ELIMINATION MODE. Select x3 to REGISTER YOUR NAME, Start. On the name screen A types the letter under the
  grid cursor (the D-pad moves the grid cursor; it starts on A, so one A press gives a one-letter name), then Select moves the heart
  to the next row: Select until the heart is at END (below file 3), Start. Back on file select the heart is on file 1: Start, Start.
- **Zelda II**: Start, Select to REGISTER, Start, A to type a letter, Select x3 to END, Start, Start (pilot route, worked).
- **Dragon Warrior**: Begin a new quest (A); name entry: A types, move to END (bottom right), A, then Start/A to confirm.
- **Final Fantasy**: class screen: A on a slot opens the class (Left/Right changes, A takes it) and goes straight to name entry:
  type **4 letters with A** (the screen goes back by itself after the 4th; Start does not end it). Four slots, then Start on the
  class screen. Budget: about 16 steps for the whole party; Right/Down move the letter cursor if A keeps typing the same letter.
- **StarTropics**: file select: pick a file, name entry as Zelda-style (A types, move the cursor to END, A or Start). If END cannot be
  found in 8 steps, stop: "Not tested (menu)".
- **Marble Madness**: Start, 1P, name entry (A letter, cursor to END, A), control type (A), practice race.
- **Contra / Micro Machines**: Select moves the 1P/2P cursor, Start picks; on Micro Machines press Select once before Start.
- **Super Mario Bros. 3**: after 1 PLAYER GAME, the World 1 map: wait 120 frames (the "WORLD 1" box must be gone), Right 10 frames
  (Mario walks to the 1 panel), wait 40, A 10 frames to enter. Pictures: Mario is the small figure left of the "1" panel.
- **Solstice**: the title and copyright screens are timed; Start is ignored until about frame 1250. `frames=600` twice, then Start.
- **Mega Man 6 / Caltron 6-in-1**: long timed intros; Start skips MM6's after it has run ~300 frames; Caltron's (Cosmos Cop, ~2300
  frames) cannot be skipped: wait with `frames=600` steps.
- **Y/N or YES/NO prompts**: the cursor moves with Up/Down or Left/Right, A picks (B often means No). **Uncharted Waters**' "Is this
  OK? (Y/N)" accepted no pad input on either emulator in the pilot: grade Not tested ("game waits") at that point, do not spend steps.
- **Name grids (any game)**: one direction press per step (10 frames, no idle step in between: the next step's picture shows the
  result); the cursor moves one cell per press whatever the hold length. Count the cells to END before pressing.
- **Metroid**: Start on the title, Start again on START/CONTINUE; gameplay ~frame 740; Right+A moves Samus off the elevator.
- **Mega Man 2**: Start may be ignored several times on the title: wait 300 idle frames, then Start; stage select Right, Start.
- **Castlevania**: the gate opens with the whip (B 20 frames) then A, not Up.
- **Pinbot**: Start, Start, wait 120: the table drawing is the verdict; the plunger input is unknown (same on Mesen).

## Spotting progress (RAM watch patterns)

Put these in `ram` when the engine is known; only rows marked verified are trusted, an unverified row is a guess to check against
the HUD once (drop it if it does not match). NES RAM is `$0000-$07FF`; `$6000-$7FFF` is cartridge (save) RAM on boards that have it.
Addresses below are the well-known community ones; check one value against the picture before trusting it (lives shown on screen = the
value or value+1).

| Game / engine | Watches |
|---|---|
| Super Mario Bros. (and SMB + Duck Hunt's SMB) | `lives=[$075A]` (shown +1: 2 = "x 3"), `world=[$075F]` (0-based), `level=[$0760]`, `x=[$0086]`, `page=[$006D]`, `state=[$000E]` (8 = normal play, $0B = dying; verified 2026-11-15) |
| Super Mario Bros. 3 | `world=[$0727]`, `lives=[$0736]` (unverified: guesses) |
| Mega Man 2 | `hp=[$06C0]` (28 = full, verified), `stage=[$002A]` (Wood Man = 2, verified), `screen=[$0020]`. Other Mega Mans: different addresses, search |
| The Legend of Zelda | `room=[$00EB]`, `hearts=[$066F]` (unverified) |
| Metroid | `energy=[$0107]`, `area=[$0074]` (unverified; the pilot's energy value did not match the HUD) |
| Castlevania | `stage=[$0028]`, `hp=[$0045]`, `lives=[$002A]` (unverified) |
| Contra | `lives=[$0032]`, `level=[$0030]` (unverified) |
| Unknown engine | `search_memory` for the on-screen lives value right after a death (value, then value-1): at most 5 calls, then picture only |

Cheap generic progress signals that work with no addresses: the picture `hash` changes; the scroll moves (the background slides); a
"STAGE 2" / "WORLD 1-2" card appears; the music changes (not visible to you: ignore).

## Using play_step well (NES)

- **Never script blind for long.** Dry run 2026-11-15: a 600-frame pre-planned Right+B+jump script ran Super Mario into the first Goomba
  (state $0B) without the agent seeing it. Keep steps 15-30 frames while enemies are on screen; long holds only on empty ground.
- Ignore the `checkFindings` count for grading: the hardware checks report the game's own habits (Mega Man 2 reached 289 in 2,000
  frames and plays correctly). Findings are only for the fix waves.

- Platformers: hold `Right` 20-40 frames; jump = `Right+A` 25-35 frames (longer hold = higher jump in most games); `B` held = run (SMB).
- RPGs / menus: tap and wait. Text boxes: A, 1 frame, then 10-20 frames of nothing. Walking in an RPG: 16 frames = one tile in
  Dragon Warrior/Final Fantasy.
- Waiting for a level card or cutscene: `until "[$000E] == 8"` style conditions when the engine is known, else `frames=120`.
- Lag: `lagging` is only meaningful with checks:true and a recognised wait loop. A lag count near the frame count = "unknown", not slow
  (Castlevania does this).

## What counts as a finding (NES specifics)

- **Split rows**: a status bar line shaking or one scanline early/late (MMC3 IRQ games: SMB3, Mega Man 3, Kirby, Skull & Crossbones) -
  note it with the score even if small; the fix waves know this class.
- Flicker on games with >8 sprites a line is normal; the reference flickers the same way.
- A black column at the left edge: games turn it on themselves ($2001 bit 1/2) - only a finding if the reference shows the column filled.
- Wrong tiles in big blocks after a bank switch = mapper bug, tag `mapper`.
- Missing extra-chip audio (VRC6/VRC7/5B/N163/MMC5): you cannot hear; skip.
- Missing mappers today: 13, 47, 118. open_rom fails with **"the NES CPU core 'FIX' did not apply"** (seen on Alien Syndrome, mapper
  118, 2026-11-15): the message names the CPU, but the cause is the board. Grade Nothing, tag `mapper`, quote the error.

## Known traps

- Do not run `load_state`/`reset` during a recording.
- Battery files: a reference Mesen keeps its own saves (the session copy starts clean); if the reference boots to "continue" and BrokenNes
  doesn't, that is a reference-setup problem, not a finding.
- `[!]` in file names: pass the path exactly as given (quotes are handled by the tool).
