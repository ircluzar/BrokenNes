# Sega track A: Master System, Game Gear, SG-1000, milestone A1

Written 2026-10-03/04 at the end of milestone A1. Branch `worktree-agent-a949c301bc3fc0be8`. What was built, what was measured, what was decided, what remains. The plan is `sega-cores-plan.md`; the shared rules are `sega-tracks-brief.md`; provenance is in `Windows/NesEmulator/sega/THIRD_PARTY_NOTICES.md` (sections 1c, 1d).

## What exists

| Part | Where |
|---|---|
| Board: Z80 + VDP + PSG + cartridge on one clock, I/O decode (A7/A6/A0), the pads, the memory-control and I/O-control ports, the Game Gear ports $00-$06 and stereo, the post-BIOS state, the frame loop | `Windows/NesEmulator/sms/BOARD_SMS.cs` |
| Cartridge: Sega mapper (banks, protected first KB, two 16 KB RAM banks), Codemasters, Korean $A000, MSX-style Korean and Nemesis, plain (SG-1000); battery detection; copier-header strip | `sms/SmsCartridge.cs`, `sega/SegaRomFormat.cs` |
| VDP: Mode 4 (scroll with the two locks, sprites 8x8 / 8x16 / 64 / 8 per line, left-column mask, line counter, frame and line interrupts, V and H counters, TH latch, collision and overflow flags with their pixel positions), the TMS9918 modes 0-3, SMS 1 / SMS 2 / Game Gear revisions (SMS 1 address-line quirks, GG 12-bit colour and the 160x144 window), the write pipeline (latency, access slots, colour-RAM dot) | `sms/SmsVdp.cs`, `sms/SmsVdp.Render.cs` |
| Sessions: `SmsSession`, `GgSession` (`IConsoleSession`): pad and Pause/Reset mapping, region and model from the file name and SegaSettings, battery save, sound unit by APU id (`ISmsSound`, the YM2413 ports $F0-$F2 are routed to it for A2) | `sms/SmsSession.cs` |
| Workshop: `--sms-run` (frames, input scripts, PNG, hashes, audio statistics, Z80 / port / VDP / PC tracing with cycle counts, oracle comparison, `--tune`), `--smstest` (nine suites, `--prove`, `--golden-write`), the Mesen drivers | `Workshop/Sega/SmsRunCli.cs`, `SmsTestCli.cs`, `SmsSuitesHw.cs`, `SmsAsm.cs`, `mesen/` |
| Desktop smoke for the real consoles, with a hand-assembled program, a battery program and a two-run save check | `UAT/entrypoints/sega-preview-smoke.ps1` |

`Consoles.MasterSystemReady` and `GameGearReady` are **true** (the Genesis flag is untouched). Without the preview switch the Console menu lists NES, SNES, Game Boy, Game Boy Color, Master System, Game Gear.

## Verification, with the numbers

`BrokenNes.Workshop.exe --smstest [--roms <dir>]` (assets from `C:\BrokenNes-tools\sega`, ROMs from a folder of loose files: `sms\`, `gg\`):

| Suite | Checks | How the result is read |
|---|---|---|
| vdptest | 55 / 55 | FluBBa's SMS VDP Test 1.31 runs on the board; the verdict is the test program's own result bytes in work RAM ($C900+). 55 includes #46 (sprite collision with a sprite off the picture), which Mesen 2.1.1 fails and the hardware passes |
| irqphase | 8 / 8 | the Z80 cycle at which the frame-interrupt handler is entered after a NOP sled (4 phases x 3 frames) and at which a status-polling loop sees the flag, equal to the cycle counts the oracle reported for the same program at the same start line |
| psg | 23 / 23 | the delivered audio: tone frequency on all three channels (437.0 Hz measured for a period of $100, wanted 437.0), the 2 dB volume steps (0.631 / 0.398 / 0.158 of full at steps 2 / 4 / 8), noise, silence at power-on, the Game Gear stereo port (left only, right only, both, neither) |
| save | 6 / 6 | export, import into a program that reads cartridge RAM, no save offered without RAM mapping |
| mapper | 12 / 12 | banks, protected first KB, RAM banking, wrap, Codemasters, plain, detection, copier header |
| session | 18 / 18 | pad bits on $DC / $DD, Pause as one NMI per press, region and model from the name, frame rates (59.92 and 49.70), picture sizes |
| smoke | 12 / 12 | the desktop smoke programs (picture, scroll with Right, battery) and that the script carries the same bytes |
| golden | 54 / 54 | frame hashes (frames 300, 600, 899) of the 18 roster games' scripted play-through: a regression guard, not an accuracy claim |
| speed | 1 / 1 | 23x real time on one core (Astro Warrior; 28.7x on the VDP test), target 8x |

**Every suite shown able to fail:** `--smstest --roms <dir> --prove` runs each suite clean and then with a poison and demands a failure: vdptest (frame and line interrupt 55 master clocks late: 5 fail), irqphase (phase moved 4 cycles: 4 fail), psg (a sound unit 6% fast with no stereo port: 9 fail), save (a corrupted save: 1), mapper (the plain cartridge where the Sega mapper belongs: 3), session (the wrong button: 1), golden (interrupt late: 7 of 54 hashes change), speed (an impossible target), smoke (Left where Right is expected). The desktop smoke was shown to fail by loading a program with the display-on write changed (3 checks fail, exit 1).

Other verifiers, all run on the branch: `--sega-test` 65 / 65 (adapted: see below), `--sega-contracts-test` 77 / 77, `--z80test --selftest` 57 / 57; `UAT\entrypoints\sega-preview-smoke.ps1` 38 / 38 (menu without the switch lists six and no Genesis, a Master System program runs without the switch, real Master System and Game Gear programs load with the right sizes and cores, are still without input and scroll with Right held, the Genesis placeholder still loads; two app instances in a row show red then green and leave `SmsSaves\<sha1>.sav` / `GgSaves\<sha1>.sav` holding the marker and counts 1 and 2); `UAT\entrypoints\desktop-smoke.ps1` 8 / 8.

## The climb: 10 Master System and 8 Game Gear games against Mesen 2.1.1

Method: a scripted play-through (tap button 1 or Start, then walk), frames 2-899, Mesen's screenshot of each frame against this emulator's with the oracle's random power-on line replicated (`--start-line` from the phase file the Lua script writes), the oracle's one-frame screenshot lag corrected. "Identical" = every pixel of the frame. Two runs per game: the default timing, and with the oracle's Z80 instruction lengths (below).

| # | Game (file) | Identical frames of 898, default timing | with the oracle's Z80 lengths | Status |
|---|---|---|---|---|
| SMS 1 | Astro Warrior (USA) | 894 | 895 | playable (title, Galaxy Zone) |
| SMS 2 | Fantasy Zone (World) (v1.2) | 894 | 896 | playable (title, round 1) |
| SMS 3 | Alex Kidd in Miracle World (v1.1) | 894 | 895 | playable (title, map, platforming) |
| SMS 4 | Black Belt | 631 | 769 | playable; diverges from the oracle later (item 5 below) |
| SMS 5 | Columns | 365 | **898** | playable; the default-timing difference is the oracle's Z80 (item 1) |
| SMS 6 | Bubble Bobble (Europe), PAL | 862 | 883 | playable (50 Hz) |
| SMS 7 | Penguin Land | 891 | 892 | playable; the only roster game offered a battery save (the scan finds the RAM mapping) |
| SMS 8 | Wonder Boy | 893 | 894 | playable |
| SMS 9 | Psycho Fox | 895 | 896 | playable |
| SMS 10 | Shinobi | **898** | **898** | playable (title, first level) |
| GG 1 | GG Demo by Charles MacDonald (PD) | 897 | 897 | working (graphics demo, no sound by design) |
| GG 2 | Columns (U) | 602 | **898** | playable |
| GG 3 | Fantasy Zone Gear (JU) | 891 | 896 | playable (reaches the shop and round 1 with a longer script) |
| GG 4 | Bubble Bobble (U) | 714 | 718 | playable; the remaining frames differ by colour-RAM dots (item 2) |
| GG 5 | Pac-Man (J) | 896 | **898** | playable |
| GG 6 | Wonder Boy (E) | 895 | **898** | playable (60 Hz although the name says Europe: a Game Gear is always 60 Hz here) |
| GG 7 | Castle of Illusion (U) [S][!] | **898** | **898** | boots, then the same blue striped hang as the oracle (item 6) |
| GG 8 | Land of Illusion (U) | **898** | **898** | working (title, story, world map with a longer script) |

Every game boots, shows its title and reaches gameplay with the script (screenshots reviewed), and plays sound (peak and RMS measured per game with `--sms-run --audio-stats`; two are silent in all 899 frames: the Game Gear demo, which has no PSG writes, and Castle of Illusion, below). Penguin Land is the only roster game the heuristic offers a save for.

### What the remaining differences are

1. **The oracle's Z80 is not the Zilog table.** Measured one instruction at a time (a straight-line program of 1,457 instruction forms, 139 branch / call / return / block forms, the oracle's exec log with cycle counts, and a V-counter probe of the same opcodes): Mesen 2.1.1 runs `LD (HL),n` in 12 cycles (Zilog 10), `INC`/`DEC (HL)` 10 (11), `RLD` 14 (18), `LD (IX/IY+d),n` 24 (19), `INC`/`DEC (IX/IY+d)` 22 (23) and the undocumented `BIT b,(IX/IY+d)` forms with a register code 19 (20); everything else measured matches. The shared Z80 core follows the manual and passes z80test's hardware CRCs, so **this emulator keeps the Zilog timing**, and `BOARD_SMS.OracleZ80Timing` (`--tune OracleZ80Timing=1`, a tool setting, never on in the app) reproduces the oracle's lengths so timing-chaotic games can still be compared. With it, Columns matches on 898 of 898 frames (533 differ without it) and Game Gear Columns 898 of 898 (296 differ without). It is not exact for every game: the negative corrections are applied as time given back to the next instruction, not at the cycle the oracle takes them.
2. **Colour-RAM dots.** A write to colour RAM with the display off shows through as a one-pixel dot at the moment it lands. The write times were fitted from sweeps against the oracle; a few frames of Bubble Bobble (Game Gear: 180 frames of single dots) differ at the pixel or two level. The oracle also drops colour writes that follow another within a few cycles, which this emulator does not model.
3. **Block I/O.** The moment within `OTIR`/`OUTI` at which the port write reaches the VDP differs from the oracle's by about two cycles; the cause is the oracle's, not established.
4. **Sprite collision with off-screen sprites.** This emulator (and the hardware, per FluBBa's test #46) sets the collision flag for overlapping sprites parked in the border lines; Mesen does not. Shinobi's status read differs in bit 5 on every frame because of it (no visible effect in the 898 frames).
5. **Isolated single frames, and drift.** Most games show 2-7 differing frames in 898, each a band of 3-8 lines of a few dozen pixels (the band's line differs from run to run, the frame number does not: a mid-frame write that lands a few lines apart in one frame; the working hypothesis is the VRAM access-slot timing, which was fitted from colour-RAM sweeps only). **Black Belt** and **Bubble Bobble** (both consoles): the first diverging frames are a handful of pixels, and the games' states drift apart afterwards. Black Belt's IRQ entry cycles match the oracle's exactly on all 80 interrupts of the first 200 frames with the oracle's Z80 lengths on, so the cause is not interrupt timing; not found (129 frames differ with the oracle's lengths on, 267 without).
6. **Castle of Illusion (Game Gear) [S][!]** shows its SEGA logo, then a blue striped screen it never leaves, with or without button mashing; **Mesen shows the same on all 898 frames**, so it is not an emulation difference this harness can see. The game is in a wait loop for a RAM flag its vertical-blank routine does not clear. Not investigated further; the dump may expect something neither emulator provides.

### What was found about the oracle (for whoever uses it next)

- Mesen starts the VDP at a random scanline every run (and the first frame shows it); the phase is written by `capture.lua` and replayed with `--start-line`. The Z80 / VDP phase within the line is constant: **21 Z80 cycles** (`BOARD_SMS.StartCycle`), found by an interrupt-entry probe and a polling-loop probe (`UAT`-style micro ROMs built in `SmsSuitesHw.cs`); frames end 21 cycles into line 192 (`FrameEndCycle`).
- Its screenshot call returns the frame before the one just completed; the script takes N+1 and names it N.
- Work RAM after the BIOS is zero except byte 0, which holds the memory-control value ($AB; $A8 on the Game Gear). The colour RAM holds a fixed pattern on the Master System, zero on the Game Gear.
- VRAM write callbacks do not fire for the VDP's own writes; read callbacks are not available; the exec callback's `cpu.cycleCount` is the Z80's own count.

## Decisions and why

- **Time base.** One Z80 T-state is the unit; the VDP is advanced to each event time ("lazy", with a pending-write pipeline) so a write in the middle of a line shows from that pixel; the picture is drawn pixel by pixel behind the CPU. Chosen over line-batched rendering because the oracle sweeps showed games depend on mid-line effects (colour dots, display toggles).
- **A frame ends where the picture is complete** (line 192 plus 21 cycles), so RAM, VRAM and the picture are what the oracle reports at the end of its frame.
- **VDP write latency is -1 master clock.** The latency used to be 62 with a phase of 0; moving the phase to the oracle's measured value moved the whole latency into it. Sweeps of colour RAM, register 1 and register 7 writes are unchanged by the move (edge positions equal).
- **Oracle Z80 timing as an opt-in tool setting,** not the default: the hardware is the Zilog table, and the app must not inherit another emulator's deviations.
- **Ready flags on for SMS and GG.** Gate A1 met: see the verification above. YM2413 and savestates are not part of A1.
- **Shared files touched, each small:** `ConsoleSessions.Create` (an optional `fileName`, used by Master System sessions for the region tag, and the two Ready branches), `ConsoleSystems.cs` (the two flags and a comment), `SegaSoundFactories.cs` (the `GG` id), `SegaRomFormat.cs` (a `.gg` file whose header carries a Master System code is a Game Gear cartridge: a few are), `MainForm.Consoles.cs` and `Emulator.Consoles.cs` (pass the ROM's name to `Create`), `Workshop/Program.cs` (routes for `--sms-run`, `--smstest`), `WebLite/Pages/Nes.razor` (the import hint names the available Sega extensions), `Workshop/Sega/SegaFoundationTestCli.cs` (`--sega-test`: "hidden" now means "not ready", the Ready consoles are checked to have real sessions; the contract file `sega/` themselves are unchanged apart from the sound factory id), `sega/THIRD_PARTY_NOTICES.md` (sections 1c, 1d), `UAT/entrypoints/sega-preview-smoke.ps1`.

## Not tested / open

- **YM2413** (ports routed, no chip), **savestates** (`IConsoleSession` has none), the **SG-1000 / SC-3000** (the TMS9918 modes are implemented and the model detection tested, no SG-1000 game was run), the **Mark III / SMS 1 quirks** (A10/A7 masks implemented from documentation; no SMS 1 game compared), the **3D glasses, light phaser, paddle and keyboard** (not implemented), **PAL beyond Bubble Bobble** (one PAL game compared; the frame rates and V-counter tables are tested).
- **Lite.** `dotnet run --project WebLite/BrokenNes.Lite.csproj --urls http://localhost:5015` (Release): the page lists the Sega extensions in its import hint and its file filter, an uploaded `.sms` / `.gg` switches the console selector to Master System / Game Gear and starts the real session (cores Z80 / SMS / SMS), and the picture is right: the desktop smoke's Master System program draws its 256x192 three-colour pattern (49,152 non-black pixels, the first pixel blue as natively), its Game Gear twin draws 160x144, a battery program shows red, and programs using OTIR to the control, colour and VRAM ports draw what they draw natively. It is **slow**: the interpreted (non-AOT) WASM runs the board at about one frame a second in the headless browser pane (the NES test ROM at 0.2), and the pane only runs frames while it is shown and after a user gesture; the AOT build (`brokennes-lite-maxspeed`) was not measured. Expect the native 23x real time divided by whatever the WASM build costs; the Lite speed floor belongs to the certification phase.
- **Heard through speakers:** not possible on this machine (no playback device, as for track C); the audio is measured on the delivered samples.
- **Mesen's own random power-on** means a game that depends on the start line shows differently from run to run in the oracle; the harness replays the phase, one Mesen run per comparison.
- **Open difference list:** the five items above.

## Next steps for A2 (the YM2413)

1. The chip: port `emu2413` (MIT) or `ymfm` (BSD-3) with ledger rows; `ISmsSound` already has `HasFm`, `WriteFmAddress/Data`, `WriteAudioControl` and `ReadAudioControl` ($F2 detection), the board routes ports $F0-$F2 only when the unit says it has the chip, and the APU menu id for the FM-capable unit goes in `SmsSoundFactories`.
2. Verify against the oracle's FM output (Mesen with `EnableFmAudio`), `YM2413_TestRom.sms` from the tools folder, and a Japanese FM game.
3. The detection routine games run (write $F2, read it back) must answer as hardware does with and without the chip; Mesen's setting `EnableFmAudio` is already a switch of `mesen-capture.ps1 -Fm`.
4. Then: savestates for the family, an SG-1000 game run, the remaining roster (SMS 11-20, GG 9-20), PAL certification, and the Lite AOT speed measurement.
