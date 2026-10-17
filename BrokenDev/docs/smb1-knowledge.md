# What reverse engineering Super Mario Bros. taught us

*For anyone starting a NES homebrew or a NESFab project. Written by BrokenDev from the SMB1 remake project's own documents
(README, retrospective, parity status, architecture notes, the agents' memory notes) and from what BrokenNes found when it ran the
remake. It is knowledge, in our words. It contains no code, no data tables and no assets of Nintendo's game; the original ROM and
anything built from its data never enter this repository (see `BrokenDev/README.md`).*

Super Mario Bros. is Nintendo's game. The remake (`smb1-nes` in BrokenDev) is the user's own NESFab program that aims at **parity**:
given the same buttons from power-on it should show the same frames, enemy positions, score, sound and timing as the original. It reached all 32
levels of the first quest, with a 159-case same-input replay suite against the original, a second quest partly done, and about 50,000 lines of
NESFab plus generators that rebuild the level data from the original cartridge. The documents say plainly that parity cannot be proven by
testing, only approached; every new kind of replay found differences the older ones could not see. This page keeps what is worth keeping.

## 1. Behaviours of the original that the documentation does not tell you

Each item was found by **measuring the original** (write watches on its RAM, same-input replays, synced probes), not by reading its
disassembly or the community's maps. Where a map and a measurement disagreed the project's rule was: the measurement wins, and the
map's figure is kept beside it as a warning. (The community speed caps, for one, were wrong.)

**The frame and the loader**

* One game tick is one video frame, with no accumulator. The original's hand-tuned code never lags; code written in a higher-level language
  starts at several times the 29,781-cycle frame budget, so most of a remake's effort goes into getting back under it.
* The enemy loader spends one entry per **empty slot per frame**. The order enemies appear in the slots, which is visible (flicker priority, who
  is processed first), follows from that turn order, not from the level data's order. In the hard-quest tables, an entry carrying the
  hard-mode bit still **spends a loader turn** even when it does not spawn; missing that in a table shifted later entries.
* Enemies spawn on the **camera**, not on the player. The first Goomba of 1-1 appears at the same camera position whether you walk or run (a
  player-keyed trigger agrees with the original at exactly one speed).
* There is **no processing window**: a walking enemy keeps moving at every screen position it can exist at, far outside the screen. Freezing
  off-camera enemies looks like a harmless optimisation and is wrong: it left 1-1's first Goomba three pixels behind for its whole life.
  Enemies are removed only by a camera-relative **erase** window (well off both edges), and the two flag objects are exempt.
* Frame counters, the random generator and the interval timer have phases that depend on the whole history of play. The remake's own tick
  counter is not the original's frame counter. A walk cannot prove anything driven by them; you seed both games identically mid-level and
  compare (a "synced mechanism probe").

**Touching the player**

* The enemy-contact test runs on **every other frame**; on the frames in between nothing collides however deeply the boxes overlap.
* It is an inclusive overlap of two snapshotted boxes, not a distance, and the enemy's box is far smaller than its sprite. The player's box is
  built **once per frame, before the background collision**, and the enemy's from before its move in that frame.
* Stomp versus injury is decided by the **sign of the player's vertical speed**, nothing else: strictly falling is a stomp; zero or rising
  hurts. No comparison of heights.
* A hit freezes the whole game (player, enemies, the clock, animation counters; not the frame counter, not the sound) for about a second; the
  enemies resume a frame before the player; the camera does not freeze. The invulnerability afterwards is counted in ticks of an interval
  counter, not in frames.

**Movement**

* Enemy speed is a signed fixed-point byte whose arithmetic is observable: a Goomba walking left and one walking right take their half-pixel
  steps on opposite frames. Two quirks make the phase matter: the mover does **not run on the frame a slot is filled**, and its fractional
  accumulator is **never initialised**, so a new enemy inherits the previous occupant's fraction. The same "accumulator never cleared" rule governs
  falling brick chunks and the star.
* The background-turn test runs **before** the mover, from the current position, over a span that is neither the collision box nor the
  question "am I touching Mario".
* Scroll locks are decoded at **multiples of 32** columns; calibrating them from walks at one speed (where two models agree) was wrong.

**Screens and sequences**

* The death card, level card, time-up and game-over cards: the timer is armed **after** the black frames' interval ticks, so they last one tick
  (21 frames) differently at some phases of the interval counter. A test that shifts the whole input timeline by 0 to 20 frames finds it; a
  fixed-timeline test cannot.
* **Start is ignored while the screen is black**, including on the title's hand-over from the attract demo.
* World 1 has five area slots for four levels because 1-2 owns two of them. The outdoor **pipe-intro area** before the underground (Mario is
  walked right into a side pipe by a script, with its own music) is real, and so is 1-2's flagpole ending, reached through a link in the underground
  level's enemy stream. Three separate investigation passes concluded these did not exist because every one of them reached the level by writing
  the world and area numbers at boot, which skips the transition. **Never conclude "the original does not do X" from a forced or teleported
  capture.** The decode rule itself is simple: an area is found from the world number's offset plus the area number.
* 1-1 has no Piranha Plants, deliberately: the pipe-spawn code special-cases World 1 Area 1. Putting one in "for authenticity" is inauthentic.
* The level format is object-based (blank template, then variable-size objects placed by column and row, with a separate vertical column
  template library), and a level's enemies are a separate stream whose entries can link to other areas. The format description found on the community
  pages was wrong in its bit layout; the real decode came from tracing the code that reads it.

**Sound**

* The original's sound is **not note data**. Every effect and every piece of music is a short routine that writes the APU registers a few times per
  frame; pitch slides use the square channels' own hardware sweep. The only faithful representation is the captured register traffic, replayed.
  A MIDI-driven opcode player is the right shape for composing new music and the wrong one for reproducing this.
* An effect ends by writing the channel-enable register **twice in one frame**, first with its bit clear (which zeroes the length counter and cuts
  the note) and then set again. Keep only the first write and the channel is dead for good, with byte-identical traffic in every other
  respect. Check whether a channel is *audible*, not whether the writes match.
* Capture effects by replaying the same frames twice from a savestate, once with the effect queued and once without, and keeping the difference: the
  music, the sprite DMA and the controller strobe all add APU-adjacent traffic of their own.
* A traffic encoder that drops a write whenever the register already holds that value is only valid while the stream plays forwards; a looped
  piece breaks it in two distinct ways.

**Hardware facts the project had to relearn**

* Horizontal scrolling wants **vertical** mirroring in the usual naming (the axis names refer to the mirror axis). The first version of the
  design page had it backwards.
* A status-register read in the screen-rebuild code can land on the exact cycle the vblank flag sets and swallow one frame's NMI. Nothing failed
  at real timing until an unrelated change moved the code; then three levels failed deterministically. Never read the status register while an NMI can
  arrive: set the address instead of resetting the latch, and test every rebuild with a synthetic delay sweep (0 to 255 cycles, odd values too).
* The NMI is a thread. Never call banked functions from it; nothing of variable cost before the sprite-0 split; locals of an assembly routine
  called from the NMI can share memory with a main-loop local; the poll that waits for the split must expire long before the next NMI or a frame
  without a hit nests the next one.
* A long text written inside the NMI ran 14 cycles past vblank and wrote two bytes into CHR-RAM (the corrupted blank tile after the World 8 princess
  message). Budget the NMI and defer what does not fit.

## 2. Lessons for the next NESFab (or any homebrew) project

**Tests: "it passes" was not "it matches".** Parity went from "31 of 32 levels green" to hundreds more fixes only because each new *kind* of test
found what the earlier kinds structurally could not: replays as big and as fire Mario (every earlier replay was small), slow walks, shifted
timelines, title presses at every frame of the demo hand-over, a lag guard, table audits generated from the ROM, forced-start probes. Before declaring
a milestone, list the dimensions of play your tests do not vary (player state, input speed, input phase, interval-counter phase, power-up state,
difficulty, idle time, lag) and write one test family per dimension. Treat "all tests green" as a statement about the dimensions you tested.

**Evidence standards.** Forced states are probes, never proof. Only same-input play of both programs from power-on counts. Label forced-start cases
and never count them in a parity claim. Allow them to *find* differences, prove each cause with a synced probe on the reference, fix it, and commit the case.

**Compare pictures, not just state.** The replay suite compared positions, slots, timers and screen edges and never one pixel, sprite or palette entry.
After weeks of "parity" a one-hour human playthrough found five visible bugs in minutes: lost colours after a pipe, an upside-down enemy shell, a
mis-coloured tile column, mirrored boss halves, a corrupted background tile. Capture PNGs of the reference and the program at the same frames of every
replay, diff them, and keep a CHR-RAM and palette integrity check (a write watch that fails on any pattern-table write after boot except the known
uploads). Schedule a human playthrough at every milestone.

**The compiler is a suspect.** NESFab 1.8 produced a series of real miscompiles, each of which looked like a game bug first:
* a store is silently dropped before a call that might write the same global (when "an assignment doesn't happen", suspect this);
* a store with no later read is deleted even when it matters (a permanent softlock came from this; add a dummy read-back);
* a function with an early return made a later `return (expr)` compute 0;
* calling a small function from an extremely hot per-probe site corrupted unrelated physics (hand-duplicate it inline);
* adding a fourth arm to a working three-arm dispatch function corrupted unrelated physics (give new cases their own function);
* writing a new global array from a rarely run path broke a scenario that never reaches that code;
* `param >> N` on an unsigned parameter crashed the compiler;
* three separate bank re-pack bugs, fixed in a patched copy of the compiler whose patches are kept in the repository;
* a bank's copy of a small constant array could land on another table (a checker script catches the class);
* "out of ROM space" with half the ROM free is the greedy packer (the patched compiler retries); "out of RAM" naming a draw function means the globals are exhausted;
* the same source can compile to different ROMs, so the project's verify script rebuilds three times and requires identical output.
Budget for the compiler as a suspect: when a result is impossible, build the previous source and compare ROM hashes before debugging the game; keep a fuzz
scenario that exercises code you did not touch and run it after every change to hot code; keep fixes to the compiler as patches in the repository, never as a
hand-edited binary; keep the hot per-frame code in hand-written assembly behind a thin interface from the start.

**Budgets on day one.** Fixed-bank bytes, RAM bytes and cycles per frame, enforced by one script that fails the build when exceeded, with every patch
reporting its deltas. This project's fixed bank fell from 1,726 to 978 free bytes across a few patches before anyone noticed, and RAM ended with 3 bytes free.
Make the lag guard (count passes over the cycle budget, in the same replays that check parity) a standing test from the first day, and put cold code behind banked
bodies with a small static gate.

**Process.** Four parallel agent streams produced a great deal of verified work and most of the self-inflicted problems: duplicate fixes in the same hot
functions (an explicit ownership boundary per area; a stream sends *findings* for other areas, not patches), merge conflicts (merge one source patch at a time,
rebuild, compare the ROM hash with the stream's validated hash, run the full suite on the exact merged ROM, rebase by branch and never by unpacking a source
archive over a working tree), a verify script that passed builds failing replay cases (only the full replay suite catches frame-timing bugs), test-setup
mistakes that cost as much as engine bugs (a case name ending in `h` silently ran in second-quest mode, a renamed case kept a log recorded in the other mode, a
failed build emptied the symbol file a suite was reading, a script edited while a run was going, concurrent runs overwriting each other's checkpoint files, a
"no score compare" flag exported for months), and saturation (nine to eleven emulator runs on four cores made Mesen's 100-second script limit a livelock: cap at
about two emulator runs per stream and one full suite at a time). What saved the work after compactions: a state file per stream, a written note per patch, a
log of what is in each build, and an honest dashboard.

**Methods that paid off.** Write watches on the original's RAM beat reading its disassembly for settling arguments (lock decode frame, the Start gate, the
loader turn of a hard-mode entry, the Bullet Bill order); synced mechanism probes proved assembly rewrites safe over thousands of random configurations; decode
the original's tables with a script and diff them against yours instead of copying a camera value (three enemy triggers were wrong that way); disassembly is a
reference, not a proof; know your harness's own lag (its per-frame rows lagged the camera by one frame and a model fitted to them was wrong); the original lags
too (a random walk in 3-1 repeats a frame in the original itself), and the suite header should say so.

**Smaller traps.** A heredoc turned a Lua escape into a real newline and a probe exited 127; `ipairs` stops at a leading nil; a Mesen memory callback on plain RAM
needs four arguments (three throw silently); Python's `write_text` empties the file before raising an encode error; pipes mask a Mesen exit code; symbol reads go
stale in states where the owning code stops running and unrelated code reuses the RAM (screenshot the "impossible" frame before chasing a miscompile); mixed line
endings are a build problem; the emulator's test runner has a wall-clock limit, so design replay harnesses as checkpointed from day one.

**What to do differently**, in the project's own order: decide the budgets; write the verification harness before the game (same-input replay of a reference with a
frame-by-frame comparator, a lag guard and a delay sweep); hand-write hot paths in assembly; generate data from the source of truth with scripts kept in the repository
and re-run as checks; vary every dimension in tests and track proof class per test; run the compiler's self-check first when something impossible happens; fewer
parallel agents with sharper ownership and one coordinator whose only jobs are merge, verify and record; keep an honest dashboard; and say up front what "done" means
(every test family green, every sweep empty for N hours of machine time, a final clean walk).

## 3. Open items of the project, as of 2026-10-11

Not merged: fix 084 (built, never run through the suite), castle 061 and 062 (the mushroom wall-check order explains a real-play 1-1 divergence), lag 013 and 014
(five Fire Mario levels still lag). Unexplored: Starman, Bowser as big or fire Mario, big or fire Mario through pipes, a random-input comparison from power-on.
Small known differences: Hammer Bro, Lakitu and Spiny-egg slot order, an enemy landing a frame later under a freeze in 7-1, a pit game-over card 21 frames late in
one forced case in 8-4, a few loader and table items. Second quest: failure states of two castles and the hard-mode sweeps. Resource limits: about 1,100 bytes
free in the fixed bank and 3 bytes of RAM.

## 4. What BrokenNes added (2026-10-16)

Running the remake on the BrokenNes cores and against Mesen (see `docs/projects/brokendev-report.md`) found four things worth passing on:

1. **The remake reads two variables it never initialises** (a debug-mode flag for the placeholder digit tiles, and a fire-animation timer). It was developed
   against an emulator that powers RAM on as zeros. Hardware powers RAM on undefined, and BrokenNes (like FCEUX) fills it with a fixed pattern, which puts a
   non-zero byte in both: digits drawn as blocks and a World card that never ends. **Initialise every global, or clear RAM in the reset code, and test with
   RAM filled with both zeros and ones.** BrokenDev's `powerOnRam` lever (verify scenarios, the desktop BrokenDev menu, `romtest --power-on-ram`) exists for this.
2. The remake writes the APU frame-counter register **every frame** with the 5-step bit set. That exercised three APU_FIX inaccuracies no earlier game had hit
   (the 5-step sequence put its half-frame clocks one step early, the sweep ignored its enable flag, the sweep updated one half-frame late after a reload),
   all fixed; see the report. Length counters ran 1.5 times too fast.
3. BrokenNes' picture, nametables, OAM, palette and CHR agree with Mesen on the remake for 2,398 of 2,400 frames (the two frames are the boot-time upload skew).
4. The original ROM is **not needed to build** the remake (the level, music and sound data derived from it are already in the source tree); it is needed to
   regenerate that data and for the same-input parity suite.
