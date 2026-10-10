# Track A3: Master System, Game Gear and SG-1000, round 3 (2026-10-09)

Record of the third round on the Z80 consoles. Earlier records: `sega-track-a.md`, `sega-track-a2.md`. Provenance of code and outside facts: `Windows/NesEmulator/sega/THIRD_PARTY_NOTICES.md`, section 1h. Branch `worktree-agent-a25c4b0a9e39d0386` (not merged, not pushed). **The round was stopped early by the usage cap: items marked HALF-DONE are not finished.**

## What exists

| Area | What | Where |
|---|---|---|
| Whole-library sweep | `--sms-sweep <folder, list, archive or ROM>`: every game headless for 2,400 frames under a scripted pad through the app's own session, classified from evidence (crash, hung, no-picture, blank, dark, noisy, static, silent, audio-bug?, ok) with evidence columns; `.7z`/`.zip`, contact sheets, `--model`, `--selftest [--poison]` (six programs, one per class; `--poison` fails 5 of 6) | `Workshop/Sega/SmsSweepCli.cs` |
| Mesen at scale | `mesen-compare.ps1` (lists, two oracle runs, several comparison modes, per-row frames, `-RomDir`), `menu-scripts.tsv`, capture options (RAM power-on state, VDP revision, plain-named ROM copy) | `Workshop/Sega/mesen/` |
| Save states | `IConsoleSession.SupportsState / StateUnsupportedReason / SaveState / LoadState`, container `ConsoleStateFile`; Master System, Game Gear, Game Boy / Color implement it; desktop F7 / F5 + Save / Load State; Lite buttons | `systems/ConsoleStates.cs`, `sms/SmsSession.cs`, `MainForm.SessionStates.cs`, `board/Emulator.Consoles.cs` |
| 24C01 EEPROM | the four Game Gear baseball cartridges | `sms/Eeprom24c01.cs` |
| Cartridge tables | by SHA-1: EEPROM games, two Korean `$A000` games, games that need the original VDP (`KnownModels`: Ancient Ys Japan) | `sms/SmsCartridge.cs` |
| BIOS images | a file named as a BIOS runs as the BIOS from a cold power-on | `sms/SmsSession.cs`, `BOARD_SMS.cs` |
| Fixes | TMS-mode fixed palette on the SMS / GG chips; post-BIOS shadow registers | `SmsVdp.Render.cs`, `BOARD_SMS.cs` |
| Tool-only oracle settings | `OracleHaltNoRefresh` (R does not count while halted, as the oracle's does not) beside A2's `OracleZ80Timing`; never on in the app | `BOARD_SMS.cs`, `sega/Z80/Z80.cs` |
| Suites | new `sessionstate`, `eeprom`, `bios`, `known`, `refresh`; grown `sg1000`, `mapper` | `Workshop/Sega/*Suite.cs` |

## The whole-library sweep

Master System library (460 files from 338 archives plus 3 loose), third pass: **ok 438, noisy 8, silent 5, static 4, audio-bug? 2, hung 2, blank 1** (second pass: ok 430, hung 10, noisy 8, silent 6, static 3, no-picture 2, blank 1). What moved: 7 BIOS images became ok when files named as a BIOS started running as the BIOS; Dallyeora Pigu-Wang (Korea) went from no picture to ok (Korean `$A000` mapper; pixel-identical to the oracle at frame 300, Sangokushi 3 likewise). What is left is not an emulation defect as far as examined: noisy (8: dithered or flickering scenes; each one compared with the oracle at 5 checkpoints matched it, Tecmo World Cup '93 aside); silent / audio-bug? (the scripted pad never reaches the music: with the menu script Wonder Boy III makes sound in 53 of 91 fifth-second blocks); static (Micro Machines needs menu input, Monopoly x2 input the pattern lacks, one bad BIOS dump); Excellent Dizzy Collection (Proto) is "blank" because its language menu leads to a black screen under this input and the oracle shows the same black frames (identical at 600, 1,200, 2,400). Gangster Town and Marksman Shooting (silent) were not examined. **HALF-DONE: the Game Gear library (665 files)** was swept in the second pass only (554 ok, 32 no-picture, mostly bad dumps and the now-fixed EEPROM trio); the third pass was started and not finished.

## Mesen at scale

**Method.** For each game the oracle (Mesen 2.1.1, headless, GPL, run only) runs **twice** (its power-on line is random: frames where its two runs differ are "the oracle disagrees with itself", counted apart); this emulator replays the oracle's power-on line and is compared on frames 2 to 899 once per mode: **default** (the app: Zilog Z80 timing, R counting in HALT), **oracle lengths** (A2's `OracleZ80Timing`), **R only** (`OracleHaltNoRefresh`), **lengths + R**. 126 games were listed (the 58 of the roster, 68 random in strata); **118 were compared (74 Master System, 44 Game Gear)**; 8 could not be captured by the oracle from the driver (Alex Kidd in Miracle World v1.1, Castle of Illusion, Doki Doki Penguin Land, Land of Illusion, Ren and Stimpy, Sapo Xule vs. Os Invasores do Brejo, Tamalympics, Wonder Boy III: the same capture works from a direct call; not understood). Per-game table: appendix.

| Mode | stable frames identical (of 96,465) | games with every stable frame identical (of 118) | oracle-disagreement frames matched (of 9,499) |
|---|---|---|---|
| default (the app) | 93,762 (97.2%) | 68 | 2,821 |
| oracle Z80 lengths | 95,679 (99.2%) | 87 | 7,288 |
| R only | 93,753 (97.2%) | 68 | 2,816 |
| lengths + R | 95,693 (99.2%) | 88 | 7,501 |

**What the differences are.** (1) The oracle's instruction lengths (A2): 19 games match fully only with them; the default stays Zilog because the hardware follows it. The lengths mode is an approximation and is *worse* for Out Run (871 of 871 by default, 826 with lengths) and Ristar. (2) **The refresh register.** The oracle's R does not count while the Z80 is halted (a real Z80 refreshes); measured with a probe (`--smstest --only refresh`): around a HALT the oracle's R moves by 1 for the HALT and 1 for the acknowledge and nothing between. Games that take random numbers from `LD A,R` diverge after the first HALT for that alone. **Game Gear Sonic The Hedgehog 2** (an A2 open item: 558 of 898 frames) is **642 of 642 stable frames identical and 256 of 256 oracle-disagreement frames matched** with lengths + R in the table's run (a separate run: 636 of 636 and 262 of 267); lengths alone 628, R alone 539. (3) The oracle is chaotic in some games (Space Harrier (USA, Europe): 662 of 898 frames differ between its own runs and none of the stable ones differ from ours; F-16 x2, Black Belt, Penguin Land). (4) **Post-BIOS shadow registers.** Tracing Fantastic Dizzy (no input, aligned instruction by instruction) found the first difference after an `EXX` in the interrupt handler: the oracle's shadow registers at a cartridge's first instruction are BC' `$0200`, DE' `$C700`, HL' `$C75F` (AF', IX, IY zero); ours were zero. Set now and checked with the oracle's own probe program (RAM identical on the Master System and Game Gear); the 18 golden hashes did not change. **HALF-DONE: Fantastic Dizzy still differs** (about 115 stable frames, a title-logo animation); the next difference was not found. (5) Games with a few differing stable frames (Alf, Astro Warrior, Bubble Bobble, Klax, Paperboy, Psycho Fox, Sonic 1, Streets of Rage II): not explained.

**Screening** (5 checkpoints, 1 oracle run, 387 Master System games compared of 450): all five checkpoints identical with the default in 241 (62%), with default or oracle lengths in 342 (88%). Game Gear screening: cut short, not reported.

**Original VDP (SMS 1).** 80 Japanese and Korean titles compared as SMS 1 and SMS 2: 70 identical either way, 1 better as SMS 1 (**Ancient Ys (Japan)**: 17,491 pixels of difference over 5 checkpoints to 392; it draws its status bar by writing R2 `$F8`/`$FF` and R5 `$EF` at lines 159, 167, 175, 183), none worse, 9 not compared. The oracle with its VDP revision set to SMS 1 draws this emulator's SMS 1 picture pixel for pixel. Ys (Japan) now runs as an SMS 1 by table. **HALF-DONE: the whole-library SMS 1 vs SMS 2 sweep** (`--sms-sweep --model sms1`, results in scratch) was not analysed; more games may belong in the table.

## The VDP's VRAM access queue: decision (kept as is)

**Question (A2).** In the active display the oracle changes the effect of some VRAM data writes (two writes 12 to 63 cycles apart dropped, swapped or late; about 8 percent of isolated writes in a sweep); this emulator keeps every write in order. Hardware queue or oracle artifact? **Documents** (MacDonald's VDP document, Maxim's SMS Power notes, sverx's 2014 measurements): a data write made too soon after the previous access during the active display is lost or lands wrong, safe spacing 22.8 to 26 cycles. **Oracle, measured:** `vram-double-on-*.sms` (144 trials of write pairs at 12 to 65 cycles): 64 differ from this emulator with this round's build (A2: 34 with its build), as phase-dependent patterns (second lost, pair swapped to `a ^ 1`, first lost) at spacings up to 65 cycles, far beyond 26. Yet the same oracle shows no corruption in a real game that writes faster than the limit: **Shinobi (Japan)** has 190 write gaps of 15 to 16 cycles in the active display (`--sms-run --fast-writes 26`: 10 of 15, 180 of 16) and this emulator, modelling no corruption, equals it on 898 of 898 frames. So "under 26 cycles corrupts" is contradicted by the oracle on the one game that tests it, and its probes contradict the documents the other way. SMSTestSuite 0.38 does not test this. Bubble Bobble (the game that started the question) first differs at frame 113 as a sprite X one pixel off on six lines, later stack-area RAM bytes (`$1FF0` to `$1FF4`) one off: the signature of the frame ending at a slightly different instant against the main thread, which points away from lost VRAM writes (a pointer, not a proof). **Decision: no access-queue corruption is modelled**: hardware and oracle cannot be told apart without a real console and either guess would make a measured game worse. The probes (`--write-vdp-probes`, `--vram-double`, `--vram-sweep`) and the gap histogram stay for whoever has hardware.

## A2's open items

1. **Menu scripts** (`Workshop/Sega/mesen/menu-scripts.tsv`; `mesen-compare.ps1 -List ... -RomDir <games>`): Golden Axe Warrior (button 2 picks letters and END, two story texts, then the overworld), Wonder Boy III (New Game, story, castle with a monster), Defenders of Oasis (Start skips the demo, button 2 for New Game, Chapter 1, the king), Shining Force II (name AAAAAA, story, first road scene) now play past their menus in this emulator (looked at picture by picture). **HALF-DONE: their comparison with the oracle was not run.** Micro Machines (Europe) is still open (default selection is head to head, player two not scripted).
2. **GG Sonic 2:** explained (oracle lengths and R, above). **Out Run:** identical on all 871 stable frames by default; the divergence is the oracle-lengths approximation.
3. **Castle of Illusion (Game Gear) is not a hang.** A Start press during the Sega logo or the copyright text leaves the game on a text screen for good, in both emulators alike; with no input for 1,250 frames the same dumps reach the title (frame 1,200), the story (2,000) and the first scene (3,200). A2's script presses Start at 150 and 270. Whether real hardware does the same was not determined; the oracle comparison of the new script was not run.
4. **Access queue:** decided above.

## Save states

`IConsoleSession` has default members, so no session breaks; one without states says why. Container `ConsoleStateFile`: magic `BNCS`, console, game id (ROM SHA-1), units (sound unit id, model, region, format, CPU / PPU ids), deflated payload, CRC-32. A state for another game, console, sound unit, region or format, or a damaged one, is **refused with the reason and the machine left as it was**. Master System / Game Gear (`sms-2`): board, Z80, mapper, cartridge RAM, the EEPROM chip mid-transfer, sound unit (PSG output side, FM) and the VDP's pending write queue, line position, events, framebuffer and scratch lines (**6 of 41 save points failed a round trip until the VDP additions**). Game Boy / Color (`gb-1`, SM83 and GB sound). SNES and NES-chip sessions say not supported with a reason; Genesis is `md/` (off limits: default "not supported"); the NES keeps its JSON mechanism. Desktop: F7 / F5, Save State... / Load State..., quick slot `%APPDATA%\BrokenNes\States\<console>\<sha1>.quick.bnstate`, the API's `quick-save-state` / `quick-load-state` reach the same code. Lite: `SaveSessionStateAsync` / `LoadSessionStateAsync` (IndexedDB), hooked into the existing buttons. `--smstest --only sessionstate`: 68 checks (round trips from many save points with every field compared by reflection, the refusals, Game Boy on real ROMs, SNES saying no); 18 fail with the proof; a mutation dropping the VDP's pending queue is caught. A save file is now found even when the ROM scan did not flag a battery (Sega consoles). **HALF-DONE: the desktop wiring has new checks in `UAT\entrypoints\sega-preview-smoke.ps1` that were written and not run; Lite was compiled, not run in a browser.**

## Hardware and cartridges

**24C01 EEPROM** (World Series Baseball V1.0, V1.1, '95, The Majors Pro Baseball): found from the driver's bus pattern (START is a write of 3 then 2 to the `$8000` window, MSB first, acknowledge in bit 0, a read returns the chip's side of the line); all four reach their title screens (`eeprom` suite: 11 checks, 6 fail with the proof). Jang Pung II (Korea) was first taken for a 93C46 and is not (bank switching; frame 300 identical, 900 differs, unresolved). **BIOS images** (`bios` suite: 8, 3 fail with the proof; Alex Kidd BIOS, Hang-On, Safari Hunt, Missile Defense 3-D, Japanese v2.1, Sonic's BIOS and a Majesco GG BIOS run). **TMS modes on the SMS / GG chips** use the fixed six-bit table `000000 000000 00AD00 00FF00 000052 0000FF 520000 00FFFF AD0000 FF0000 525200 FFFF00 005200 FF00FF 525252 FFFFFF` (read off the oracle's palette probe; Loretta no Shouzou: 11,935 pixels of difference to 2; `sg1000` suite 26 checks, 9 fail with the proof). **SG-1000 / SC-3000 games:** searched `X:\EMULATION` to depth 6 for `.sg .sc .sg1000 .sc3000`: none; A2's eight programs remain the evidence; real SG-1000 software is **not tested**.

## Verifiers (last run on this branch: `--smstest --roms <roster>`)

385 pass, 2 fail. Every suite passes (vdptest 55, irqphase 8, psg 23, state 21, **sessionstate 68, eeprom 11, bios 8, known 5, refresh 11**, pal 7, **sg1000 26**, save 6, **mapper 14**, session 18, smoke 12, golden 54, speed 1) except **two throughput checks of the `fm` suite** (nine FM channels alone 19x, the whole machine 6.7x, against 8x), which failed because the machine was saturated by other agents and the sweeps (they passed in A2 on a quiet machine). Proofs of failure run this round: refresh (2 of 11 fail poisoned), known (2 of 5), mapper (4 of 14); eeprom, bios, sg1000 and sessionstate as stated above. **Not rerun after the final commit:** `--smstest --prove` as a whole, `--sega-contracts-test`, `--sega-bridge-test`, `--z80test --selftest`, `--sega-test`, `--md-test`, the plugin self-test, both desktop smoke scripts, `web-smoke.ps1` (the gate runs were started and their output lost to a quoting error; wrap-up came first).

## Decisions

No VRAM access-queue corruption (evidence above). The app keeps the chip's behaviour where documents agree and the oracle differs (Zilog lengths, R counting in HALT); the oracle's versions are tool-only. A game that needs the original VDP gets it by SHA-1 table, a user's board choice wins, the description names the board. Save states are refused, not forced. BIOS images run as BIOS images by name; none shipped. The sweep's classes are hints; none is a defect without a comparison.

## Next steps

1. Run the whole gate on a quiet machine, plus `UAT\entrypoints\sega-preview-smoke.ps1` (new save-state checks) and `web-smoke.ps1`.
2. Finish the Game Gear sweep third pass (`--sms-sweep "X:\EMULATION\Sega Game Gear" --frames 2400`) and analyse the SMS 1 sweep for more `KnownModels` entries.
3. Compare the menu scripts and the Castle script with the oracle (`mesen-compare.ps1 -List Workshop\Sega\mesen\menu-scripts.tsv -RomDir <folders> -OutTsv ...`).
4. Fantastic Dizzy: `mesen-capture.ps1 -Exec 1-N -ExecRegs` against `--trace-pc 1-N --trace-pc-regs`, collapse repeated lines (the HALT wake-up artifact is removed from our log), align by registers.
5. Examine games with tens to hundreds of differing stable frames (Rambo III, Ristar, Alf, Puyo Puyo 2, Shining Force Gaiden II, Ancient Ys (USA, Europe)); find why the oracle driver cannot capture the 8 games.
6. Dashboard (`BJeTEhpACkjebnpYtcm6ej`): not updated beyond what was there.

## Appendix: per-game table (Mesen 2.1.1, frames 2 to 899, two oracle runs)

Stable frames = frames on which the oracle's two runs agree; each mode column is the number of those this emulator reproduces pixel for pixel (differing ones in brackets); the last column is the number of frames on which the oracle disagrees with itself that this emulator equals one run on (default / lengths + R). Modes: default = the app; oracle Z80 lengths = `OracleZ80Timing=1`; lengths + R = that and `OracleHaltNoRefresh=1`. Pad: the Workshop's 900-frame golden script, region from the file name.

<!-- 126 games, 8 without an oracle capture -->
| # | Game | | stable frames | default | oracle Z80 lengths | lengths + R | oracle-disagreement frames matched (default / lengths + R) |
|---|---|---|---|---|---|---|---|
| 1 | Action Fighter (Japan) (v1.1) | SMS | 897 | 897 | 897 | 897 | 1 / 1 of 1 |
| 2 | Aerial Assault (Europe) | SMS | 586 | 586 | 586 | 586 | 0 / 312 of 312 |
| 3 | Alex Kidd BMX Trial (Japan) | SMS | 255 | 237 (18 differ) | 255 | 255 | 143 / 643 of 643 |
| 4 | Alex Kidd in Miracle World (Japan) | SMS | 894 | 894 | 894 | 894 | 0 / 2 of 4 |
| 5 | Alex Kidd in Miracle World (USA) | SMS | 894 | 894 | 894 | 894 | 0 / 1 of 4 |
| 6 | Alf (USA) | SMS | 890 | 878 (12 differ) | 880 (10 differ) | 880 (10 differ) | 4 / 5 of 8 |
| 7 | American Baseball (Europe) | SMS | 898 | 898 | 898 | 898 | - |
| 8 | Ancient Ys Vanished Omen (USA, Europe) | SMS | 245 | 209 (36 differ) | 208 (37 differ) | 208 (37 differ) | 14 / 12 of 653 |
| 9 | Arcade Smash Hits (Europe) | SMS | 898 | 898 | 898 | 898 | - |
| 10 | Arena - Maze of Death (U) [!] | GG | 850 | 846 (4 differ) | 850 | 850 | 13 / 48 of 48 |
| 11 | Asterix (Europe) (En,Fr) (v1.1) | SMS | 898 | 898 | 898 | 898 | - |
| 12 | Asterix and the Great Rescue (E) (M5) [!] | GG | 896 | 896 | 896 | 896 | 2 / 2 of 2 |
| 13 | Astro Warrior (USA) | SMS | 893 | 890 (3 differ) | 891 (2 differ) | 891 (2 differ) | 4 / 4 of 5 |
| 14 | Berlin no Kabe (J) | GG | 898 | 836 (62 differ) | 898 | 898 | - |
| 15 | Black Belt (USA, Europe) | SMS | 572 | 572 | 572 | 572 | 66 / 63 of 326 |
| 16 | Bubble Bobble (Europe) | SMS | 882 | 851 (31 differ) | 869 (13 differ) | 869 (13 differ) | 11 / 14 of 16 |
| 17 | Bubble Bobble (U) [!] | GG | 711 | 708 (3 differ) | 710 (1 differ) | 710 (1 differ) | 5 / 9 of 187 |
| 18 | California Games II (Europe) | SMS | 898 | 897 (1 differ) | 898 | 898 | - |
| 19 | Championship Hockey (Europe) | SMS | 890 | 890 | 890 | 890 | 8 / 8 of 8 |
| 20 | Clutch Hitter (U) [!] | GG | 898 | 898 | 898 | 898 | - |
| 21 | Columns (U) [!] | GG | 602 | 602 | 602 | 602 | 0 / 296 of 296 |
| 22 | Columns (USA, Europe) | SMS | 364 | 363 (1 differ) | 364 | 364 | 1 / 533 of 534 |
| 23 | Cosmic Spacehead (E) (M4) [!] | GG | 784 | 583 (201 differ) | 784 | 784 | 112 / 114 of 114 |
| 24 | Cosmic Spacehead (Europe) (En,Fr,De,Es) | SMS | 896 | 896 | 896 | 896 | 1 / 1 of 2 |
| 25 | Defenders of Oasis (U) [!] | GG | 898 | 896 (2 differ) | 898 | 898 | - |
| 26 | Double Hawk (Europe) | SMS | 782 | 782 | 781 (1 differ) | 781 (1 differ) | 69 / 115 of 116 |
| 27 | Dragon Crystal (J) | GG | 415 | 370 (45 differ) | 415 | 415 | 0 / 483 of 483 |
| 28 | Dragon Crystal (U) [!] | GG | 366 | 366 | 366 | 366 | 42 / 531 of 532 |
| 29 | Drop Zone (U) [!] | GG | 897 | 897 | 897 | 897 | 1 / 1 of 1 |
| 30 | Dynamite Dux (Europe) | SMS | 895 | 895 | 895 | 895 | 2 / 3 of 3 |
| 31 | Earthworm Jim (U) [!] | GG | 898 | 898 | 898 | 898 | - |
| 32 | Ernie Els Golf (E) | GG | 782 | 782 | 782 | 782 | 115 / 116 of 116 |
| 33 | F-16 Fighter (USA, Europe) | SMS | 232 | 227 (5 differ) | 226 (6 differ) | 226 (6 differ) | 0 / 615 of 666 |
| 34 | F-16 Fighting Falcon (USA) | SMS | 204 | 201 (3 differ) | 201 (3 differ) | 201 (3 differ) | 25 / 645 of 694 |
| 35 | Fantastic Dizzy (Europe) (En,Fr,De,Es,It) | SMS | 895 | 706 (189 differ) | 707 (188 differ) | 707 (188 differ) | 1 / 2 of 3 |
| 36 | Fantasy Zone (Japan) | SMS | 891 | 890 (1 differ) | 891 | 891 | 2 / 5 of 7 |
| 37 | Fantasy Zone (World) (v1.1) (Beta) | SMS | 368 | 364 (4 differ) | 364 (4 differ) | 364 (4 differ) | 526 / 528 of 530 |
| 38 | Fantasy Zone (World) (v1.2) | SMS | 895 | 892 (3 differ) | 893 (2 differ) | 893 (2 differ) | 3 / 3 of 3 |
| 39 | Fantasy Zone Gear (JU) [!] | GG | 896 | 889 (7 differ) | 894 (2 differ) | 894 (2 differ) | 1 / 2 of 2 |
| 40 | Final Bubble Bobble (Japan) | SMS | 883 | 857 (26 differ) | 873 (10 differ) | 873 (10 differ) | 9 / 13 of 15 |
| 41 | G-LOC Air Battle (Europe) | SMS | 898 | 897 (1 differ) | 897 (1 differ) | 897 (1 differ) | - |
| 42 | Gear Works (U) [!] | GG | 898 | 898 | 898 | 898 | - |
| 43 | GG Demo by Charles MacDonald (PD) | GG | 897 | 897 | 897 | 897 | 0 / 0 of 1 |
| 44 | GG Shinobi (E) [!] | GG | 898 | 898 | 898 | 898 | - |
| 45 | Gokuaku Doumei Dump Matsumoto (Japan) | SMS | 859 | 827 (32 differ) | 859 | 859 | 33 / 35 of 39 |
| 46 | Golden Axe (USA, Europe) | SMS | 271 | 176 (95 differ) | 271 | 271 | 386 / 627 of 627 |
| 47 | Golden Axe Warrior (USA, Europe) | SMS | 892 | 891 (1 differ) | 892 | 892 | 6 / 6 of 6 |
| 48 | Great Tennis (Japan) | SMS | 892 | 892 | 892 | 892 | 3 / 3 of 6 |
| 49 | Hokuto no Ken (Japan) | SMS | 686 | 685 (1 differ) | 686 | 686 | 1 / 153 of 212 |
| 50 | James Bond 007 - The Duel (Brazil) | SMS | 882 | 879 (3 differ) | 881 (1 differ) | 881 (1 differ) | 16 / 16 of 16 |
| 51 | Klax (Europe) | SMS | 892 | 890 (2 differ) | 891 (1 differ) | 891 (1 differ) | 3 / 6 of 6 |
| 52 | Krusty's Fun House (Europe) | SMS | 898 | 764 (134 differ) | 898 | 898 | - |
| 53 | Lion King, The (Europe) | SMS | 898 | 898 | 898 | 898 | - |
| 54 | Loretta no Shouzou (Japan) | SMS | 897 | 320 (577 differ) | 891 (6 differ) | 891 (6 differ) | 0 / 0 of 1 |
| 55 | Makai Retsuden (Japan) | SMS | 896 | 896 | 896 | 896 | 2 / 2 of 2 |
| 56 | Master of Darkness (Europe) | SMS | 897 | 897 | 897 | 897 | 1 / 1 of 1 |
| 57 | Micro Machines (E) | GG | 897 | 897 | 897 | 897 | 1 / 1 of 1 |
| 58 | Micro Machines (Europe) | SMS | 895 | 895 | 895 | 895 | 1 / 1 of 3 |
| 59 | Micro Machines 2 - Turbo Tournament (E) | GG | 896 | 896 | 896 | 896 | 2 / 2 of 2 |
| 60 | Ms. Pac-Man (Europe) | SMS | 876 | 874 (2 differ) | 874 (2 differ) | 874 (2 differ) | 22 / 22 of 22 |
| 61 | NBA Jam (U) (V1.1) [!] | GG | 774 | 774 | 774 | 774 | 124 / 124 of 124 |
| 62 | Ninku (J) [!] | GG | 718 | 661 (57 differ) | 718 | 718 | 117 / 180 of 180 |
| 63 | Ninku 2 - Tenkuryu-e no Michi (J) [!] | GG | 882 | 882 | 882 | 882 | 16 / 16 of 16 |
| 64 | Ninku Gaiden - Hiroyuki Daikatsugeki (J) | GG | 898 | 898 | 898 | 898 | - |
| 65 | Out Run (USA, Europe) | SMS | 871 | 871 | 826 (45 differ) | 826 (45 differ) | 0 / 1 of 27 |
| 66 | Out Run 3-D (Europe) | SMS | 889 | 887 (2 differ) | 889 | 889 | 2 / 8 of 9 |
| 67 | Pac-Man (J) | GG | 895 | 895 | 895 | 895 | 1 / 3 of 3 |
| 68 | Paperboy (Europe) | SMS | 885 | 884 (1 differ) | 884 (1 differ) | 884 (1 differ) | 10 / 11 of 13 |
| 69 | Penguin Land (USA, Europe) | SMS | 466 | 466 | 466 | 466 | 425 / 428 of 432 |
| 70 | Phantasy Star (Brazil) | SMS | 897 | 897 | 897 | 897 | 1 / 1 of 1 |
| 71 | Phantasy Star (Japan) | SMS | 897 | 897 | 897 | 897 | 1 / 1 of 1 |
| 72 | Phantasy Star (Korea) | SMS | 889 | 889 | 889 | 889 | 9 / 9 of 9 |
| 73 | Phantasy Star (USA, Europe) (v1.2) | SMS | 895 | 895 | 895 | 895 | 3 / 3 of 3 |
| 74 | Phantasy Star (USA, Europe) (v1.3) | SMS | 897 | 897 | 897 | 897 | 1 / 1 of 1 |
| 75 | Pop Breaker (J) | GG | 898 | 898 | 898 | 898 | - |
| 76 | Prince of Persia (E) [S][!] | GG | 898 | 898 | 898 | 898 | - |
| 77 | Psychic World (J) | GG | 898 | 898 | 898 | 898 | - |
| 78 | Psycho Fox (USA, Europe) | SMS | 890 | 889 (1 differ) | 889 (1 differ) | 889 (1 differ) | 6 / 7 of 8 |
| 79 | Puyo Puyo 2 (J) [!] | GG | 898 | 521 (377 differ) | 896 (2 differ) | 896 (2 differ) | - |
| 80 | R-Type (USA, Europe) | SMS | 897 | 897 | 897 | 897 | 1 / 1 of 1 |
| 81 | R.C. Grand Prix (U) [S][!] | GG | 839 | 839 | 839 | 839 | 55 / 58 of 59 |
| 82 | Rambo III (USA, Europe) | SMS | 896 | 784 (112 differ) | 783 (113 differ) | 783 (113 differ) | 1 / 1 of 2 |
| 83 | Ristar the Shooting Star (U) [!] | GG | 898 | 889 (9 differ) | 612 (286 differ) | 612 (286 differ) | - |
| 84 | RoboCop versus The Terminator (Europe) | SMS | 898 | 898 | 898 | 898 | - |
| 85 | Sapo Xule - S.O.S Lagoa Poluida (Brazil) | SMS | 895 | 892 (3 differ) | 893 (2 differ) | 893 (2 differ) | 2 / 2 of 3 |
| 86 | Scramble Spirits (Europe) | SMS | 898 | 898 | 898 | 898 | - |
| 87 | Seishun Scandal (Japan) | SMS | 892 | 891 (1 differ) | 891 (1 differ) | 891 (1 differ) | 3 / 3 of 6 |
| 88 | Shining Force Gaiden II (J) | GG | 898 | 878 (20 differ) | 878 (20 differ) | 878 (20 differ) | - |
| 89 | Shining Force II - The Sword of Hajya (U) [!] | GG | 897 | 897 | 897 | 897 | 1 / 1 of 1 |
| 90 | Shinobi (Japan) | SMS | 898 | 898 | 898 | 898 | - |
| 91 | Shinobi (USA, Europe) | SMS | 857 | 857 | 857 | 857 | 41 / 41 of 41 |
| 92 | Shinobi II - The Silent Fury (U) [!] | GG | 897 | 897 | 897 | 897 | 1 / 1 of 1 |
| 93 | Sonic Chaos (U) [!] | GG | 897 | 897 | 897 | 897 | 1 / 1 of 1 |
| 94 | Sonic The Hedgehog (U) (V1.1) [!] | GG | 898 | 898 | 898 | 898 | - |
| 95 | Sonic The Hedgehog (USA, Europe) | SMS | 874 | 870 (4 differ) | 871 (3 differ) | 871 (3 differ) | 22 / 22 of 24 |
| 96 | Sonic The Hedgehog 2 (Europe) | SMS | 897 | 845 (52 differ) | 897 | 897 | 1 / 1 of 1 |
| 97 | Sonic The Hedgehog 2 (Europe) (v1.1) | SMS | 896 | 844 (52 differ) | 896 | 896 | 2 / 2 of 2 |
| 98 | Sonic The Hedgehog 2 (U) [!] | GG | 642 | 548 (94 differ) | 628 (14 differ) | 642 | 9 / 256 of 256 |
| 99 | Space Harrier (Japan) | SMS | 892 | 892 | 892 | 892 | 5 / 5 of 6 |
| 100 | Space Harrier (USA, Europe) | SMS | 236 | 236 | 236 | 236 | 0 / 0 of 662 |
| 101 | Speedball (Europe) (Mirrorsoft) | SMS | 897 | 897 | 897 | 897 | 1 / 1 of 1 |
| 102 | Star Wars (Europe) | SMS | 847 | 847 | 847 | 847 | 51 / 51 of 51 |
| 103 | Streets of Rage II (Europe) | SMS | 891 | 889 (2 differ) | 888 (3 differ) | 888 (3 differ) | 1 / 1 of 7 |
| 104 | Strider (USA, Europe) | SMS | 894 | 833 (61 differ) | 894 | 894 | 3 / 4 of 4 |
| 105 | Super Monaco GP (Europe) | SMS | 775 | 775 | 775 | 775 | 118 / 118 of 123 |
| 106 | Super Wonder Boy (Japan) | SMS | 891 | 891 | 891 | 891 | 4 / 4 of 7 |
| 107 | Sylvan Tale (J) [!] | GG | 897 | 897 | 897 | 897 | 1 / 1 of 1 |
| 108 | T2 - The Arcade Game (U) [!] | GG | 898 | 898 | 898 | 898 | - |
| 109 | Tails' Adventures (U) [!] | GG | 897 | 865 (32 differ) | 897 | 897 | 1 / 1 of 1 |
| 110 | Taisen Mahjong HaoPai (J) | GG | 883 | 883 | 883 | 883 | 15 / 15 of 15 |
| 111 | Turma da Monica em O Resgate (Brazil) | SMS | 895 | 582 (313 differ) | 895 | 895 | 1 / 2 of 3 |
| 112 | Wolfchild (U) [!] | GG | 810 | 810 | 810 | 810 | 88 / 88 of 88 |
| 113 | Wonder Boy - The Dragon's Trap (E) [!] | GG | 895 | 895 | 895 | 895 | 3 / 3 of 3 |
| 114 | Wonder Boy (E) [!] | GG | 893 | 893 | 893 | 893 | 2 / 5 of 5 |
| 115 | Wonder Boy (USA, Europe) | SMS | 888 | 888 | 888 | 888 | 6 / 6 of 10 |
| 116 | World Class Leader Golf (U) [!] | GG | 898 | 893 (5 differ) | 893 (5 differ) | 893 (5 differ) | - |
| 117 | Zaxxon 3-D (World) | SMS | 890 | 890 | 890 | 890 | 7 / 8 of 8 |
| 118 | Zillion (USA) (v1.1) | SMS | 898 | 898 | 898 | 898 | - |

games compared: 118 (SMS 74, GG 44); stable frames: 96465; oracle-disagreement frames: 9499
  zilog   identical stable frames  93762 (97.2%), games with every stable frame identical  68 of 118, oracle-disagreement frames matched 2821 of 9499
  oracle  identical stable frames  95679 (99.2%), games with every stable frame identical  87 of 118, oracle-disagreement frames matched 7288 of 9499
  rhalt   identical stable frames  93753 (97.2%), games with every stable frame identical  68 of 118, oracle-disagreement frames matched 2816 of 9499
  both    identical stable frames  95693 (99.2%), games with every stable frame identical  88 of 118, oracle-disagreement frames matched 7501 of 9499
without a capture: Alex Kidd in Miracle World (USA, Europe) (v1.1); Castle of Illusion Starring Mickey Mouse (U) [S][!]; Doki Doki Penguin Land - Uchuu Daibouken (Japan); Land of Illusion Starring Mickey Mouse (U) [!]; Ren & Stimpy - Quest for the Shaven Yak, The (U) [!]; Sapo Xule vs. Os Invasores do Brejo (Brazil); Tama & Friends - 3choume Kouen - Tamalympics (J); Wonder Boy III - The Dragon's Trap (USA, Europe)
