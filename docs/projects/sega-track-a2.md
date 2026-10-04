# Sega track A: Master System, Game Gear, SG-1000, milestone A2

Written 2026-10-04 at the end of milestone A2. Branch `worktree-agent-a14218e0629f0b1fe`. It continues `sega-track-a.md` (milestone A1: board, VDP, sessions, the first 18 games). What was built, what was
measured, what was decided, what remains. Provenance: `Windows/NesEmulator/sega/THIRD_PARTY_NOTICES.md`, section 1e (the YM2413).

## What exists now (A2)

| Part | Where |
|---|---|
| YM2413 (OPLL): 9 melodic channels + 5 rhythm voices, 18 slots, tables, LFO, noise, instrument ROM; a port of emu2413 v1.6.2 (MIT) **with the corrections the black-box oracle forced** (below) | `Windows/NesEmulator/sega/Ym2413.cs`, `Ym2413Tables.cs` |
| Rate converter for it (emu2413's windowed-sinc converter, push model, no allocation) | `sega/Ym2413Resampler.cs` |
| The Japanese console's sound unit: SN76489 + YM2413, ports `$F0` (address) `$F1` (data) `$F2` (audio control), the real mix, hub voices (4 PSG + 14 FM), a complete state | `sega/SmsFmSound.cs`; menu id `SMS-FM` in `sega/SegaSoundFactories.cs`, listed in the Master System APU menu (`systems/ConsoleSystems.cs`), not in the Game Gear's |
| The verifier against Nuked-OPLL, sample for sample | `Workshop/Sega/FmOracleTestCli.cs` (`--fm-oracle-test`), our own driver `Workshop/Sega/oracle/opll_oracle.c`, `build-oracle-opll.ps1` |
| `--smstest` suites added: `fm`, `state`, `pal` | `Workshop/Sega/FmSuite.cs`, `SmsSuitesFm.cs` (probe programs), `StateSuite.cs`, `PalSuite.cs` |
| Probe generators for the oracle: `$F2` / FM port probes, interrupt sleds, VRAM read sweeps, SG-1000 TMS programs | `SmsSuitesFm.cs`, `IrqProbes.cs`, `VdpSlotProbes.cs`, `Sg1000Progs.cs` (`--smstest --write-fm-probes / --write-irq-probes / --write-vdp-probes / --write-sg-programs dir`) |
| `--sms-run` additions: `--apu`, `--fm-regs-at`; Mesen driver `-Overscan`; the full-frame capture cut to the picture height; a second oracle run (`--mesen-b`) | `Workshop/Sega/SmsRunCli.cs`, `mesen/mesen-capture.ps1` |

## The YM2413

**What it is.** emu2413's structure (18 slots, a 19-bit phase generator, a 1,024-entry log-sine table, a 7-bit envelope in 0.375 dB steps, rate scaling, the tremolo and vibrato tables, a 23-bit noise generator) ported to C#
with every value of the exponential table, the quarter sine, the tremolo pattern and the instrument ROM taken by script (no digit retyped). The output scale is the chip's own: a full-volume sine peaks at +2043 / -2042 (the
oracle's emu2413 state dump shows +2043 / -2037 over 141 sampled instants), nine channels and the five percussion voices are summed by `Calc()`.

**The oracle.** Nuked-OPLL (die-derived, GPL: never read, only run) through our own driver, `opll_oracle.exe`, which calls its public reset / write / clock functions and logs what each of the 18 OPLL clocks of a
sample carries. The alignment (one sample of output latency; channels 7-9 two; the clock at which each write lands) was measured, not assumed, and is documented in the header of `FmOracleTestCli.cs`.

**What differs from emu2413 and why** (each found by the oracle comparison; all are in the ledger, section 1e): integer key-scale levels (a table, not emu2413's), the silent half of the half-sine reads negative zero,
a key-on at maximum attenuation starts the attack at once, the LFO starts one sample late, the envelope clock has per-phase offsets (attack and decay/sustain/release, by rate group), five of the fifteen built-in
instruments carry other bytes than emu2413's (4 flute, 8 organ, 11 harpsichord, 13 synthesizer bass, 14 acoustic bass: found by fitting the user instrument to the oracle's output, 99.0 to 100.0% of 10,131 samples equal
on three pitches each), masked channels read zero, a rhythm-mode change clears the channel outputs, the AM phase wraps at 13,440.

**What "match" means, and the numbers** (`BrokenNes.Workshop.exe --fm-oracle-test`, 173 checks, about 12 s, exit 0): *exact* = every compared sample equal; *bounded* = SNR in dB, the largest single difference in the oracle's
9-bit units (full scale 256) and the share of exactly equal samples stay inside the numbers the group states.
- structure 11 (the channel clock map, rhythm clocks, per-channel key-on latency): exact.
- oscillator 77 (multipliers, feedback 0-6, the four waveforms, total level, volume, key-scale level for every block and level code, vibrato, tremolo on all nine channels over whole periods): exact; the chaotic corner (full-level modulator with feedback 7, or total level 0/1 with feedback 5) is reported as information only (exact on some channels).
- lfo 2, instruments 15 (all fifteen: level error per 128-sample window at most 2 dB, mean at most 0.5 dB, 85% or more samples exact; ten are 99.8 to 100% exact), envelope 54 (attack 15..3, decay, release, sustain flag, key-scale rate at two blocks: SNR at least 26 dB, largest error at most 30 units), rhythm 5 (bass drum 98.4% exact, tom about 99% until key-off, snare / cymbal / hi-hat level within 0.4 dB), pitch 9.
- **Not matched, said plainly:** the noise voices (snare, cymbal, hi-hat) have the right level but not the oracle's noise sequence (no relation found; level within 0.4 dB); the tom's release differs; the chaotic corner above; envelope step timing at the fastest rates differs by up to one step.

**Proof the verifier can fail:** `--fm-oracle-test --prove` injects a fault per group (pitch registers shifted, key-scale levels off, vibrato table, a wrong instrument byte, wrong rates, a percussion key on the wrong slot, the write latency moved one sample) and demands failures:
oscillator 77, lfo 2, instruments 15, envelope 54, rhythm 1, pitch 9, structure 10 failed under their faults.

**On the board** (`--smstest --only fm`, 39 checks; poisoned with the FM data port dropped and the `$F2` read-back wrong in bit 0, 19 of its 32 board checks fail): detection and ports, the register file, pitch, 3 dB volume steps, the full-scale peak, the audio-control gating, rhythm, the hub's voices, an APU swap in a running game, the unit's state round trip bit for bit, no allocation while running, speed, and the seven oracle groups above run again from `--smstest` (skipped, not counted as passed, when the oracle executable is missing).

**The `$F2` port and how a game finds the chip (measured on Mesen 2.1.1, with and without its FM chip, by probe programs; `SmsSuitesFm.cs`):**
- With the console's I/O chip **off** (bit 2 of `$3E`) and the unit present, a read of `$F2` returns what was written to it, masked to bits 0-1 (all 256 values written and read back); at power-on it reads 0. `$F0`, `$F1` read `$FF`.
- With the I/O chip **on**, or without the unit, `$F0` and `$F2` read as `$DC` and `$F1` as `$DD` (the pad mirrors), whatever was written.
- Phantasy Star's detection (I/O chip off, write 0 and 1 to `$F2` seven times, read each back, compare with the written value) depends on exactly this, and the A1 board answered the read-back regardless of `$3E` (it would have failed the oracle's probe, and a game that reads the pads through a mirror got the audio control instead). Fixed in `BOARD_SMS.ReadPortCore`.
- Writes: `$F2` stores bits 0-1 (the oracle: value & 3; with no FM chip, nothing).

**Against the oracle in real games** (register file of the YM2413 at fixed frames, Mesen with `EnableFmAudio` against `SMS-FM`, same input script, same start line; the file diff is every one of the 64 registers plus the audio control): Phantasy Star (Japan) 29 of 29 sampled frames to frame 1,500 identical (up to 27 registers non-zero); Shinobi (Japan) 12 of 12; Doki Doki Penguin Land 12 of 12; Fantasy Zone II (Japan) 12 of 12; `YM2413_TestRom.sms` driven by a pad script (instrument, tab, key notes): registers 35 of 35 and pictures 35 of 35 identical. Alex Kidd BMX Trial (Japan) agrees at 7 of 12 and then differs: Mesen's own runs of it differ from each other at those frames (three runs, run two differs from runs one and three at frames 600 to 1,000), so it says nothing about this emulator.
**Level statistics against the oracle** (the oracle's audio cannot be captured: its test runner has no audio output and its Lua API no audio call, so no recording exists; what it does expose is the chip's last output and each channel's output in its state dump): over 1,441 frame-end instants of each game, RMS of the chip's output sum, this emulator over the oracle: Phantasy Star (Japan) 1.010, Fantasy Zone II 0.999, Shinobi (Japan) 0.993, Doki Doki Penguin Land 0.974; per channel mostly 0.99 to 1.01 (a few quiet or decaying channels 0.85 to 1.09 from sampling the instant, not the level).

**Decisions.**
- *Which unit is the default.* `SMS` (the PSG only, the export console) stays the default; `SMS-FM` is selectable in the Master System's APU menu and is not offered for the Game Gear. Most of the library is export software and the Japanese FM games detect the unit themselves; no game is silently given another unit, and switching the APU in a running game keeps the PSG state and starts the FM chip silent (checked: SMS to SMS-FM and back, an unknown id refused). A per-game or per-region automatic choice would be a substitution the user did not ask for, so it is not done.
- *What the two audio-control bits do to the mix* (bit 0 FM on, bit 1 PSG off) is the documented convention. The oracle shows its audio control register but not its audio, so this is **not** compared with a recording.
- *The instrument ROM bytes of five instruments are the oracle's, not emu2413's*, because the oracle is the die-derived chip and the fitted bytes match it to 99-100%; emu2413's own values stay in the comments.
- *Gains.* `FmGain` and `PsgGain` default 1.0: the chips' own scales (a full-volume FM sine peaks at 2,043, a full-volume PSG tone at about 3,277 of 32,768). The relative level on the console was not calibrated against a recording (none can be made here).
- *Speed.* The FM unit with nine channels sounding runs 43x real time alone and allocates nothing; the whole machine (Z80, VDP, PSG, FM) runs the same program at 17x real time, target 8x.

**What Track C's YM2413 bridge and the plugin's chip need.** `ISmsSound.DescribeVoices` returns 18 voices for `SMS-FM`: the four PSG voices (tone 1-3, noise), then the 14 FM voices in this order: melodic channels 1-9 (`Pulse`), bass drum (`Triangle`), snare (`Noise`), tom (`Pulse`), cymbal (`Noise`), hi-hat (`Noise`); `Hz` is the carrier's frequency, `Level` the channel's present amplitude (0 when the FM output is off, `$F2` bit 0 clear). Register interface for a bridge that replays FM: `SmsFmSound.WriteFmAddress/WriteFmData` (the chip's `$F0` / `$F1`), `WriteAudioControl` (`$F2`), `Fm.WriteRegister(reg, value)`, `Fm.Register(i)` (the 64 registers), `Fm.Mask` and `MaskChannel / MaskHiHat / ...` to isolate stems, `Fm.ChannelOutput(v)` (per-voice output), `Fm.Operator(channel, carrier)` (phase, envelope, level, state), `Fm.ChannelHz`, `Fm.ChannelKeyOn`. The chip runs from the Z80 clock (one sample per 72 clocks: 49,716 Hz NTSC, 49,262 PAL).

## The remaining roster against the oracle: Master System 11-20, Game Gear 9-20

Method as in A1 (a scripted play-through, frames 2 to 899, the oracle's screenshot of each frame against this emulator's, the oracle's power-on line replayed), with three additions: **two oracle runs** (its power-on is random; a frame where its two runs differ is "the
oracle disagrees with itself" and is counted apart: this emulator is asked to equal one of them), the oracle's whole video frame cut to the picture's height (so 224-line games like Micro Machines compare), and each game also run with `OracleZ80Timing` (the oracle's own instruction
lengths; a tool setting, never on in the app). "n of 898" counts frames identical to the oracle's (stable frames) plus the oracle-disagreement frames where this emulator equals one of its two runs. Every game also ran on the long play-through described below; screenshots at frames 600 and 899 (and later) were looked at.

Frames identical to the oracle's, out of 898 (the short script: tap button 1 or Start, then walk), with the default (Zilog) timing / with the oracle's instruction lengths; "stable" = frames where the oracle's two runs agree, "O/O" = frames where they disagree with each other:

| # | Game | What the scripted play reached (screenshots looked at) | default | oracle's Z80 lengths | Long script, 2,399 frames (default / oracle's lengths) |
|---|---|---|---|---|---|
| SMS 11 | Out Run | driving | 871 (0 stable differ, 27 O/O) | 826 (45 stable differ: the parity emulation is not exact here) | |
| SMS 12 | Space Harrier | in the game | 236 (O/O 662: the oracle's two runs differ from frame 212, this emulator equals neither) | 236 | |
| SMS 13 | Sonic The Hedgehog | Green Hill | 892 (4 stable differ) | 893 | |
| SMS 14 | R-Type | stage 1 | 898 | 898 | |
| SMS 15 | Phantasy Star (USA, Europe) | intro story; with the long script the first-person dungeon and its menu | 898 | 898 | 2,399 / 2,399 |
| SMS 16 | Golden Axe Warrior | intro, then the name entry (the input did not get past it) | 894 (4 stable differ) | 898 | 2,395 / 2,398 |
| SMS 17 | Wonder Boy III | title, then the password screen (not further); the script causes only silent PSG writes | 898 | 898 | 2,399 / 2,399 |
| SMS 18 | Sonic The Hedgehog 2 | Underground Zone | 846 (52 stable differ) | 898 | |
| SMS 19 | Micro Machines (Europe), PAL, 224 lines | the game-select and character menus (not a race) | 896 (0 stable differ, O/O 3) | 896 | 2,397 / 2,397 |
| SMS 20 | Streets of Rage II (Europe), PAL | stage 1 | 889 (3 stable differ) | 890 | |
| GG 09 | Sonic The Hedgehog | title; Green Hill with the long script | 898 | 898 | 2,396 / 2,397 |
| GG 10 | Dragon Crystal | the first dungeon | 393 (15 stable differ, O/O 493) | **898** | |
| GG 11 | GG Shinobi (E) | the truck stage | **898** (was 773 before the VRAM-read fix) | 898 | |
| GG 12 | Shinobi II | title; the first stage with the long script | 898 | 898 | 2,396 / 2,396 |
| GG 13 | Sonic Chaos | Turquoise Hill | 898 | 898 | |
| GG 14 | Sonic The Hedgehog 2 | the first zone (its start-ring animation) | 558 (131 stable differ, O/O 228) | 662 (24 stable differ) | |
| GG 15 | Tails' Adventures | title; the equipment menu with the long script | 866 (32 stable differ) | **898** | 2,332 / 2,398 |
| GG 16 | Defenders of Oasis | story, then the title menu (battery detected) | 896 | **898** | 2,399 / 2,399 |
| GG 17 | Ristar | a stage (paused by the script's Start) | 816 (15 stable differ, O/O 317; was 612 before the fix) | 821 | 2,390 / 2,399 |
| GG 18 | Micro Machines | menus (the long script reaches its link-cable screen) | 895 | 896 | 2,399 / 2,399 |
| GG 19 | Wonder Boy III (E) | the password screen | 898 | 898 | 2,398 / 2,398 |
| GG 20 | Shining Force II | intro dialogue (battery detected) | 898 | 898 | 1,440 / 1,440: diverges from frame 398, see below |

Over all 40 games (the 18 of A1 included, re-run with the two-run method): 32,784 of 35,920 frames with the default timing, 34,364 with the oracle's lengths (95.7%); stable frames that differ: 306 and 112. The 18 A1 games alone: 14,754 and 15,672 of 16,164 (A1's single-run numbers were 14,808 and 15,817: the oracle's own run-to-run differences now count as such, which moves some games' numbers either way).

What the differences are, in the order of how many frames they cost: **(1) the oracle's Z80 is not the Zilog one** (`LD (HL),n` 12 cycles against 10 and the others of A1's list): games whose timing hangs on those instructions (Dragon Crystal, both Columns, Tails, Defenders, Sonic 2 on the Master System, Golden Axe Warrior) match only with the oracle's lengths, which stay a tool setting; **(2) the oracle disagrees with itself** where a game's state depends on its random power-on line
(Space Harrier, Black Belt, Dragon Crystal, Ristar, Out Run and others, 10 to 660 frames each); **(3) the oracle's own parity emulation here is not exact** (Out Run and the Shining Force II long run: with the oracle's lengths the instruction trace of Shining Force II is identical for 41,976 instructions, 14 frames, and then the frame interrupt is accepted one instruction later in this emulator, a difference of a cycle or two in where the approximation puts a corrected instruction's time);
**(4) the VDP access queue** (isolated frames, below).
Also: Shining Force II differs on 959 stable frames from 398 on in the long script (in a run examined, a game timer was one count apart from frame 16 and a text prompt's sprite sat at Y $2F in the oracle while hidden here, a 38-pixel band): the game's state diverged through (1) and (3); its 898-frame script matches throughout.

## What became of A1's open differences

1. **Game Gear Shinobi (E) diverged from frame 504 (125 of 898 frames), cause found and fixed.** RAM and VRAM dumps at every frame showed the first non-stack difference at frame 300 (a copy of the VDP status with the collision bit set here and not in the oracle: the oracle sets no
   sprite collision in the border lines above and below the picture, FluBBa's test #46 says the hardware does; kept as the tool setting `OracleNoBorderCollision`, no effect on pictures) and the first VRAM difference at frame 471: **a game's decompressor copies VRAM to VRAM with the source one byte behind the destination, and a read of VRAM did not see the write still waiting for its access slot**
   (this emulator's reads fetched the old byte). The CPU's accesses go through one queue, so a read now takes the byte of a pending write to the same address (`SmsVdp.FetchVram`). Shinobi (E) went from 773 to **898 of 898**; Ristar from 612 to 816; no other game of the 40 got worse; the A1 golden hashes did not change.
   The instruction trace of that stretch (aligned on the first common interrupt entry, 17,916 instructions, PCs and cycle counts) was identical in both emulators before the fix.
2. **Interrupt-boundary question closed.** Where the frame interrupt is taken when the instruction it falls in is longer than the NOP's 4 cycles: 13 sled programs (NOP, `JR`, `DJNZ`, `LD A,(nn)`, `LD (nn),A`, `JP`, `PUSH`/`POP`, `LD A,n`, `INC HL`, `OUT`, `IN`, `CALL`, `LD (HL),n`), two oracle runs each at random start lines, 13 frames each:
   the handler's entry cycle equal in every one, except `LD (HL),n` (the oracle's 12 cycles against Zilog's 10, known). So the A1 doubt about instruction-length-dependent interrupt acceptance is answered: it is not the cause of the isolated frames.
3. **Black Belt "drift": explained as the oracle's, not this emulator's.** Over the two oracle runs, the frames where the two runs agree (564 of 898) all equal this emulator; from frame 565 on the oracle's runs differ from each other for 334 frames, and this emulator equals neither on most of them. A game whose state depends on the power-on line shows a
   different run per start line; this emulator replays one line.
4. **Bubble Bobble (both consoles) and the 2-30 isolated differing frames in several games** (a band of 1-8 lines of a few dozen pixels in one frame). Investigated with probe programs against the oracle, **not fixed**: during active display the oracle changes the effect of a VRAM data write in some cases of two writes 12 to 63 cycles apart (`vram-double-on-0..2.sms`: 34 of 144 trials differ from this emulator's, which keeps every write in order at its own address; the oracle's results are dropped, swapped or one byte late, with the display off none)
   and loses about 8% of the isolated writes of a sweep (`vram-wsweep-*`: 29 of 368 rows; in the rows inspected the byte is found at a neighbouring address, with the next write's byte beside it). A single write followed within 0 to 124 cycles by a new address always landed correctly in both,
   so it is not a plain "address register changed under a pending write". Whether this is the hardware's VDP access queue or the oracle's artifact cannot be told here (no hardware), and the effect depends on the write's phase against the access slots, which was fitted from visible effects only. The probes (`--write-vdp-probes`, `--vram-double`, `--vram-sweep`) are ready for whoever models the queue.
   The VRAM read sweep (`vram-read-fine.sms`): reads 0 to 8 cycles after setting a read address see a stale buffer in the oracle in 20 of 768 trials, and in this emulator in none of them (the coarser first sweep had one the other way); with 9 or more cycles of delay (what games use) both always see the fresh byte.
5. **Castle of Illusion (Game Gear).** Three dumps ((U) [S] [!], (J) [S] [!], (J) [S] [h1]) behave identically on both emulators (898 of 898 frames equal, the same blue striped screen, silent): not an emulation difference. The same game's Master System dump plays and equals the oracle on 897 of 898 frames (898 with the oracle's Z80 lengths). The Game Gear game is not stuck in an interrupt wait: its vertical-blank handler runs every frame
   (the flag at `$C061` is set by the wait routine and cleared by the handler at `$058D`, traced), it runs a state machine through `RST 8` (state in `$C060`), reads only the pads and the status port, and writes the PSG only to silence it. What it waits for is not known; the dump may expect something neither emulator provides.
6. **Space Harrier** differs from the oracle after frame 212, but the oracle's two runs differ from each other on every frame from there (662 of 898): this emulator equals neither. Chaotic in the oracle; no cause for a difference of this emulator's found.

## Battery saves of the roster

The cartridge-RAM detection offers a save for Phantasy Star, Golden Axe Warrior, Defenders of Oasis and Shining Force II (A1 knew Penguin Land). After the same scripted 900 frames on the oracle and here, **Phantasy Star (USA, Europe)'s save is identical to the oracle's battery file byte for byte (8 KB, 280 non-zero bytes) and so is Golden Axe Warrior's (8 KB, 26 non-zero bytes)**;
this emulator's file is 32 KB (both 16 KB RAM banks), the oracle's the 8 KB the game uses, the rest zero. For the Game Gear's Defenders of Oasis (8,191 non-zero bytes after the script) and Shining Force II (9,386) this emulator offers a save and the oracle wrote no file in that run, so nothing was compared. No roster game was found to use a serial EEPROM (the 93C46); that case is **not tested**.
The save file round trip itself (export, import, a program that reads it) is the A1 `save` suite, and the desktop smoke's two-run battery check.

## Savestates

`IConsoleSession` has no save-state member, and neither the Game Boy nor the SNES session offers one (the NES has its own JSON mechanism in the desktop's main form), so there is **no shared mechanism to plug the Master System into**. What exists and is now verified is the board's own: `BOARD_SMS.SaveState/LoadState` (work RAM, ports, mapper and cartridge RAM, VDP including the pending-write pipeline, the sound unit, the Z80).
The first round-trip test found a real defect (the board's frame-loop position, the next line and the first-line cycle, was not saved: a restored session was 70 lines off), fixed; the PSG's output side (chip time, the band-limited mixer's buffer and DC state) and the FM unit's unread samples were not saved either, fixed.
`--smstest --only state`: 21 checks (three programs built here, four roster games with `--roms`): after a save at a frame boundary and a restore into a fresh session, the picture of each of the next 40 or 90 frames, every audio sample, the work RAM, the PC and the cycle count equal the session that carried on; 12 of 21 fail when a work RAM byte is left unrestored. Saves taken in the middle of a frame are not possible (a frame is one call). A golden hash of a state is not kept: nothing promises the format yet.
**An app-level feature needs:** a member on `IConsoleSession` (save to / load from a byte array), a place for the file (the Game Boy and SNES battery saves have one), the sound unit's id stored with the state (a state saved with the stock unit does not load into SMS-FM: they differ in layout; the APU swap path loads the PSG part only and starts the FM chip silent).

## PAL, headless

`--smstest --only pal` (7 checks): a PAL frame is 313 lines of 228 Z80 cycles (71,364), an NTSC one 262 (59,736); the PSG's tone of period `$100` is 432.97 Hz on the PAL clock (3,546,895 Hz) and 436.96 Hz on the NTSC clock, the YM2413's A4 tone 435.98 Hz and 439.99 Hz; **FluBBa's VDP test run as a PAL console gives the oracle's PAL result bytes on all 60 tests** (V counter table of test #21 included; #46, which the oracle fails and the hardware passes, passes here).
PAL games against the oracle frame by frame (two oracle runs): Micro Machines (Europe), in 224-line mode (an extended height the capture is cut to): **every stable frame identical** (all 895, and 1 of the 3 frames where the oracle's two runs differ; the long script: all 2,319 stable frames, and 78 of the 80 oracle-disagreement frames); Bubble Bobble (Europe) and Streets of Rage II (Europe) in the tables. The Game Gear is 60 Hz whatever the name says (A1).

## SG-1000

No SG-1000 or SC-3000 game is in the libraries (no `.sg` / `.sc` file, no such folder; the Master System archives hold only Master System games). So the verification is eight hand-assembled programs run as `.sg` cartridges (Graphics I, Graphics II, Text, Multicolor, each with 8x8 and with magnified 16x16 sprites, pseudo-random pattern, colour and name data, 20 sprites,
the status port polled every frame into `$C001`): **all eight pictures are pixel-identical to the oracle's (frame 30) and the status flags it saw are equal** (`--smstest --only sg1000`, 24 checks, poison 8 of 24 fail). Getting there took three fixes found by the comparison: the TMS9918's sixteen colours (the oracle's, read off a palette probe; they differ from the common list in the last digits),
the status low bits (0 without a fifth sprite, not 31), and sprites not being processed with the display blanked (a program that loads its sprite table with the display off no longer sets the collision and fifth-sprite flags). Not tested: real SG-1000 software, the SC-3000's keyboard, the SG-1000's pad and Pause behaviour beyond what `.sg` detection and the Texas Instruments PSG variant already had.

## Speed

`--smstest` throughput 23x real time on one core (Astro Warrior; unchanged), with `SMS-FM` and nine FM channels sounding the whole machine runs 17x (target 8x), the FM unit alone 43x with no allocation while it runs. Lite: the WASM build was not measured here (see the open items).

## Verifiers (all on this branch, last run)

`--smstest --roms <dir> --prove`: 13 suites, every one fails when poisoned (fm, state, pal and sg1000 are new); `--fm-oracle-test --prove` 173 passed and every group fails when a fault is injected; `--sega-contracts-test` 78 / 78; `--sega-bridge-test` 124 of 126 before the APU menu's new SMS-FM entry was added to its expectation (the two checks assumed the stock unit was followed only by NES chips), all pass after;
`--z80test --selftest` 57 / 57; `--sega-test` 65 / 65; `--sega-oracle-test` all pass; `--md-test` 113 / 113; `--m68ktest` exit 0.
