# Sega track B3b: Genesis games, round 3 (record)

Branch `worktree-agent-a2324622697435662`, 2026-10-09, on top of `docs/projects/sega-track-b2a.md` and the B2b sound work. B3a (the VDP slot model and renderer) ran beside it and owns `md/MdVdp*.cs`; nothing of those files was committed here.
**This record is written while the work goes on and says at each line what was run.** Numbers marked "partial" are from a run that had not finished when this was written.

## What was built

| Piece | Where | What it does |
|---|---|---|
| Whole-library sweep | `Workshop/Sega/MdSweepCli.cs`, `--md-sweep <dir\|list\|rom> [--frames N] [--jobs 4] [--out results.tsv] [--script default\|mash\|start\|none] [--thumbs dir] [--no-retry]`, `--md-sweep --summary results.tsv`, `--md-sweep --thumb-sheet dir` | runs each ROM headless with a scripted pad, classifies it (crashed, hung, rejected, unsupported, halted, exception-loop, no-picture, frozen, silent, ok), one TSV row each (raw results stay in scratch, never in git), per-class and per-cause summary; a watchdog for emulator loops; zipped ROMs; a game that looks frozen or blank under one pad script gets the other scripts before it is called frozen |
| Diagnostics the sweep reads | `M68k.ExceptionCounts`, `BOARD_MD` (unclaimed writes, TMSS words, open-bus reads, the last register reads), `MdCartridge` (writes nothing took, unknown mapper writes) | what a stuck game is polling, what hardware it touches that is not there |
| `--md-run` additions | `MdRunCli.cs` | `--first-exception`, `--events`, `--until-pc`, `--cart-trace`, `--save-in/--save-out`, `--sheet/--sheet-at` (contact sheet of the run), `--pad-script`, an exception/interrupt count line |
| I2C EEPROM | `md/MdEeprom.cs`, `md/MdEepromTable.cs`, `md/MdCartridge.cs` | X24C01 and 24C02-24C64 from the datasheets (page write, wrap, random/sequential read, ack), three publisher wirings (Sega, Acclaim, Codemasters), a table by serial; the chip's bytes are the battery image |
| SRAM rules | `md/MdCartridge.cs` | window not covered by ROM is mapped at power-on; mirrors past the declared range; odd/even by address; junk windows ignored; headerless RAM (below) |
| Frame-exact oracle comparison | `Workshop/Sega/MdOracleCli.cs` (`--md-oracle`), `oracle-tools/md-picture/oraclerun.ps1`, `bl.ps1`, `BOARD_MD.RunUntilLine`, `MdSession.RunToLine` | see "How BlastEm is stepped" |
| Cartridge verifier | `Workshop/Sega/MdCartTest.cs` (`--md-cart-test`, 47 checks), proof of failure `Workshop/Sega/poison-cart.ps1` | EEPROM protocol and wirings, SRAM rules, region, STOP |

## The sweep: first whole-library run (948 files, 2,400 frames each, default script, 4 jobs, 49 minutes)

On the tree as merged (before any fix below): **818 ok, 71 frozen, 26 no picture, 18 unsupported (all 32X: correct), 12 exception loops, 2 halted, 1 rejected** (`delete.me`, 1 byte: a clean loader error). No crash, no hang, no exception out of the emulator on any of the 948 files. Median 188 frames/s with the sweep's own picture hashing and four jobs plus other agents' load (the slowest, Super Skidmarks, 118).
The causes, by number of games, and what was done:

| Games | Cause | Result |
|---|---|---|
| 17 of 18 in the group (Beast Wrestler, Valis, Valis III, Syd of Valis, Valis SD, Mr. Nutz, HardBall, HardBall 94/95/3, LHX Attack Helicopter, F-117 x2, Super Fantasy Zone, Summer Challenge, Gaiares, Fatman, Slaughter Sport) | **A stopped 68000 never woke.** Its interrupt level was sampled at bus cycles only, a STOP makes none. | fixed in `BOARD_MD.InterruptSample`; re-sweep of the group: 18 of 20 ok (the others: Slap Fight [hack], Lightening Force [bad dump]) |
| about 30 (EA and Accolade sports games, HardBall 3, Might and Magic II/III, College Football USA 96, NHL 96, Madden 98, John Madden 93, NHLPA 93, Rings of Power, Starflight, Test Drive II, Winter/Summer Challenge, PGA Tour Golf, Chess, ...) | **battery RAM with no SRAM header**: the games write to $200001 and test it | headerless RAM (32 KB on the odd bytes, mirrored, brought into the map by the first write, reported as a battery only once written or loaded) |
| HardBall 95, Barkley Shut Up and Jam 1 and 2, Brett Hull 95, MLBPA Baseball x2, ... | SRAM window not covered by the ROM was not mapped (3 MB ROM, SRAM at $300001), writes past the declared range, `$E840` on an 8 KB window taken for an EEPROM, parity of the window | fixed (see the rules table in the cartridge section) |
| 14 EEPROM games (NBA Jam TE, NFL QB Club 94 and 96, College Slam, Frank Thomas, NBA Jam, Brian Lara Cricket 95 and 96, Micro Machines Military/96/II, Evander Holyfield, Greatest Heavyweights, Dodge Ball, Wonder Boy in Monster World and V) | no EEPROM at all: games waited for it or showed a blank/striped screen | implemented; the whole list ran ok afterwards (24 of 25 with NBA Jam (EU), which has no EEPROM traffic at all); Mega Man: The Wily Wars and Rockman Megaworld use a different chip (below) |
| 2 (Golden Axe III, J. League Pro Striker) | region lock: Japan + Europe without the Americas got the overseas console | fixed in `MdCartridge.Overseas`; BlastEm runs both, ours showed the lock screen |
| 11 line-A/line-F "storms" (Heavy Nova, Dyna Brothers 2, Power Instinct, Twinkle Tale) | **not a bug**: these games use the line-F/line-A trap as their call mechanism (Heavy Nova: `JSR` then a data word `$FF10` that the handler reads) | the sweep no longer calls traps an error: the class needs address errors, illegal instructions, privilege violations or divides sustained in the last quarter of the run |
| 71 "frozen" | about half were the sweep's own Start presses pausing the game (Dark Castle, Exodus, Joshua, Pirates Gold, Star Trek DS9 ...) | the sweep retries a frozen/blank game under the `mash` and `start` scripts before it says frozen |
| 2 | Fatal Rewind / Killing Game Show: double fault after any Start press | open, see below |
| remaining | bad dumps ([b1]: 512 KB halves, Road Rash, F-22, Centurion ...), pirate carts with their own mappers (Pocket Monsters 1 and 2, Aq Renkan Awa, Mulan, Super Mario World (Unl), Rockman X3, Magic Girl, Command & Conquer beta: writes to `$A130F3` and beyond that nothing implements), Pioneer LaserActive BIOS, Virtua Racing (SVP: the game runs, the chip is not there), Super Street Fighter II (a 4 MB cut of a 5 MB game that writes the SSF2 bank registers: needs a good dump) | listed in the summary below; not fixed |

A second whole-library run on the fixed tree (with the retry logic) was **stopped at 370 of 948 files** (the work was wrapped up): of those 370, **347 ok, 9 frozen, 3 no picture, 2 exception loops, 1 halted (Fatal Rewind), 1 rejected (delete.me), 7 32X refused**, against about 13 percent non-ok before. The frozen ones left in that part: Bugs Bunny in Double Trouble (writes `$BF` to every `$A130F1-FF` register at frame 2202: a mapper nobody implements), College Football's National Championship (polls `$A1001E`, an I/O address nothing answers), Double Dragon 2 (J) (polls the VDP status forever at `$001078`: a VDP flag the game waits for, possibly B3a's area), Fatal Fury 2 [b1], Barver Battle Saga (pirate). **The remaining 578 files have not been re-run on the fixed tree**.

## Cartridge hardware

**EEPROM** (verified by `--md-cart-test`, 47 checks, 8 poison cases each failing at least one check, and by the games above running): the chip is the datasheet's, the pins are the game's. Wirings found by tracing what each game does to the bus (`--cart-trace`):
Sega (Evander, Greatest Heavyweights, Dodge Ball, Wonder Boy in Monster World / V): SDA bit 0 and SCL bit 1 of the byte at $200001, read back in bit 0. Acclaim (NBA Jam TE, NFL Quarterback Club, College Slam, Frank Thomas, NBA Jam): SCL is bit 0 of $200000, SDA bit 0 of $200001, read back there. Codemasters (Brian Lara Cricket x2, Micro Machines Military): SDA bit 0 and SCL bit 1 of $300000, read back in bit 7 of $380001. Chip sizes from the device bytes the games send: NBA Jam TE uses A0/A2/A4/A6 (four 256-byte pages: a 24C08), Micro Machines Military the same, the others only A0. **Not done: Mega Man: The Wily Wars / Rockman Megaworld**: their bit stream (CS on bit 2, clock on bit 1, data on bit 0, a leading run of zeros, a 7-bit address, 16 clocks of data) is a 3-wire (Microwire-style) serial EEPROM, not I2C; the header declares an EEPROM, the table has no entry, the cartridge says so (`EepromUnsupported`) and the game runs without saving. **Not compared with BlastEm yet**: BlastEm's save files for the same play (see the next steps); what is verified is that each game's own EEPROM traffic now gets acknowledged and read back, and that NBA Jam TE, College Slam and Frank Thomas fill the chip as they play.
**SRAM**: rules and tests are in `--md-cart-test`. The `$A130F1` toggle for a ROM that covers the window (Triple Play Gold, the Sonic and Knuckles lock-on image) is as B2a had it. **Not verified with a real game against BlastEm's save file: Phantasy Star IV, Sonic 3, Shining Force, Phantasy Star II/III.**
**SSF2 mapper**: the library has no ROM above 4 MB; the only Super Street Fighter II is a 4 MB cut ([b1]) of a 5 MB game, so the mapper is verified on the synthetic 5 MB ROM only. The game writes the bank registers (`$A130FF` = 2 ...) and cannot work without the missing megabyte.
**Region**: `MdCartridge.Overseas` is now: Americas listed, or Europe listed without Japan.
**TMSS**: games write "SEGA" to `$A14000` (the sweep counts them: `tmss_w`); it is accepted and ignored (no boot ROM, nothing locked). No game in the sweep misbehaved because of it.
**6-button pad (Mortal Kombat 3, Decap Attack)**: both run in the sweep (alive, with sound); not compared with BlastEm.

## How BlastEm is stepped (the frame-exact input protocol)

BlastEm's debugger cannot be driven over a pipe on Windows (its console detaches), its GDB stub (`-D`) can: breakpoints, registers and memory are reliable and fast (3,000 stops a second), single steps through a bus wait stall, and key events cannot be delivered at a chosen frame while it is halted. Pause (F7) and frame advance (F8), driven by `bl.ps1`, can:
1. Measured with a pad-polling probe ROM (`padprobe2.py`: the pad changes and the VINT count are drawn as bars): a key pressed while BlastEm is paused is first seen by the game at **V counter 232** with the VINT count of the pause point plus one, and running freely it takes events at line 232-233 every frame. A screenshot taken with `k:P` followed by one advance is the picture of the state it ends in. Our `--md-oracle` stops at line 232 (`BOARD_MD.RunUntilLine`) and changes the pad there: the probe gives `(217, 232, 31)` on both machines.
2. Protocol: BlastEm runs from reset without input, is paused (about frame 60 x seconds since launch), takes a screenshot (S0); `--scan` finds the boundary of ours that matches S0 pixel for pixel (and says AMBIGUOUS when a still picture matches many; then the later shots tell the candidates apart); from there both get the same buttons at the same boundaries and are compared at chosen steps. An unmatched shot is searched in our last 400 pictures: an exact match at another step means BlastEm lost frame advances (a tap lands while the previous frame still runs and is a pause), not that the picture differs.
3. Arrow keys need the extended-key flag (`bl.ps1` fixed; without it the Right arrow reached the game as numpad 6 and did nothing).
4. Known weakness: BlastEm names screenshots by the second and now and then writes one too many; `oraclerun.ps1` refuses to compare a run whose screenshot count is not exactly right.

## Roster

| # | Game | Result against BlastEm (scripted input, frames compared) |
|---|---|---|
| 3 | Sonic the Hedgehog | **15 of 15 pictures pixel-identical over 1,500 frames** with Start, C and A presses and walking right in Green Hill (pause at boundary 700, pictures every 100 frames) |
| 2, 4, 5 | Altered Beast, Golden Axe, Strider | **comparison invalid, not a difference**: BlastEm wrote 19, 24 screenshots for the 16 asked for in those runs (extra files by the second), so the shots were paired with the wrong pictures of ours (the lag search said "oracle stands 400 advances behind" on a picture that matched at step 100). `oraclerun.ps1` now refuses such a run ("CAPTURE UNRELIABLE"); these games need a rerun. Game 5 was cut off |
| other 16 | | not compared. With the default script all 20 roster games reach gameplay in ours (contact sheets of 2,400 frames each: Flicky, Altered Beast stage 1, Sonic 1 Green Hill, Golden Axe stage 1, Strider, Streets of Rage, Castle of Illusion, Ecco, Sonic 2, Kid Chameleon, Gunstar Heroes, SoR2, Shinobi III, Castlevania Bloodlines, Comix Zone, Contra Hard Corps, Phantasy Star IV intro, Sonic 3 and S&K intros, SoR3 stage 1); that is not a comparison with BlastEm |

## Found for another owner (VDP, `MdVdp.cs`: B3a)

The vertical interrupt (and the VINT status flag) comes about **780 master clocks late** in ours: `StartNextLine` sets `vintTime = lineStart + VintDelay` at line 224, but the plan's 788 clocks are counted from the VBlank flag, which the V counter raises at line 223's offset 2640. Probe ROMs on both machines (`vinttime.py`: the H/V counter at the interrupt handler's first instruction, and when the flag is first seen): BlastEm `$E023` and `$E016`, ours `$E050` and `$E042`; with `vintTime = lineStart + 40` ours gave `$E027`/`$E022` (+8: `$E01F`/`$E012`, +72: `$E027`/`$E022`). It did not change the Sonic 1 comparison above (all exact either way), so no game was shown to depend on it yet. Not changed here.

## Half-done and not done (read this first)

* **Roster**: only Sonic 1 is compared (15 of 15 exact). The other 19: runs of games 2, 4 and 5 were invalid (screenshot count), the rest not started. The pause frames per game that give a pinnable first screenshot are in `scratchpad` notes only; `--md-oracle --windows N` finds them again (a pause inside a window of frames whose pictures are unique). Games with no such window (Streets of Rage 1, 2, 3, Shinobi III, Phantasy Star IV) need the ambiguous-start resolution of `--md-oracle` (built, not yet used on BlastEm data). Status on the dashboard: Sonic 1 playable, the others unchanged (`working` where they were).
* **Whole-library second run**: 370 of 948.
* **Saves against BlastEm's files, Mega Man Wily Wars' 3-wire EEPROM, pirate mappers, SVP**: not done.
* **Kid Chameleon boot sound**: not investigated (the plan: BlastEm VGM log from a pause at frame ~100 with `m`, compare the first DAC (`$2A`) write time with ours; ours uploads the driver at frames 14, 30 and 204 and starts the DAC at 208).
* **Z80 bank-window stall and refresh constants, model defaults, YM busy flag**: not measured. Per-frame allocation: 0.0 bytes (`--alloc-probe`, Sonic, 1,200 frames); speed not re-measured on a quiet machine (about 125-190 frames/s with other jobs running).
* **Fatal Rewind**: see below.

## Open

* Fatal Rewind (and Killing Game Show): pressing Start at any time from frame 20 on halts the 68000 (a `JSR` through a RAM vector at `$FFE03E` that is still 0); without input it runs and its state at `$02249E` matches BlastEm's registers exactly. Not compared with Start pressed (needs the oracle with a script; the first screenshot is black, so use the ambiguous-start resolution).
## Verification (what was run)

| What | Result |
|---|---|
| `--md-cart-test` | 47 of 47; `poison-cart.ps1`: 8 injected bugs, each failed at least one check (1, 2, 1, 1, 1, 1, 3, 1) and the source was restored |
| `--md-test`, `--md-pic-test`, `--m68ktest --selftest`, `--sega-contracts-test`, `--sega-test`, `--md-golden` | 143, 93, 44, 80, 65 passed / 0 failed; golden 75 frames match (160 s); on the tree with the STOP, SRAM, EEPROM and region changes |
| Whole library, first run | above |

## Next steps

Finish the roster comparison (`rbatch`: each game 4-5 minutes of BlastEm window time, hold the lock), the second whole-library run, Kid Chameleon with the GDB stub (breakpoints on the Z80 area writes, VGM log from reset), Fatal Rewind with the oracle, BlastEm save files for Sonic 3 / Phantasy Star IV, the YM busy flag and bank-window stall probes, the speed and allocation measurement on a quiet machine.
