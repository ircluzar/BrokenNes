# Sega track C4b: cross-console integration and certification (round 4)

Branch `worktree-agent-a89085e9c63da6624`, 2026-10-15, on top of `sega-track-c3a.md` / `-c3b.md`. Plan and rules: `sega-cores-plan.md`, `sega-tracks-brief.md` (Round 4 bar items 4 and 5). The user's words for this track: "I want those sega cores integrated and polished like we did for gameboy and snes; we are just doing the same exercise as previously with different consoles."

**This record says at each line what was run.** Nobody has listened to any sound produced by this track; every audio statement is a measurement of rendered samples. Items under "Not tested" and "Open" are not claims.

## 1. What was built

| Piece | Where | What it is |
|---|---|---|
| CPU walls | `Windows/NesEmulator/mix/CpuWalls.cs` | `CPU_Z80` and `CPU_M68K` as NES CPUs: the real Z80 / 68000 cores on the NES bus. The NES has no instruction set for them to run, so a NES game on them is an **instruction-set wall** by design: the catalog labels say so, and the combination is classified dead. The wall itself is a real CPU: a Z80 / 68000 program written for the NES bus runs (interrupt vectors answered from the NES vectors `$FFFA/$FFFC/$FFFE`, the NES IRQ latch translated to a level at acknowledge, clock ratio 2 Z80 T-states per NES cycle, 30 68000 clocks = 7 NES cycles). |
| Shared voice-slot allocator | `mix/HubReplayer.cs` (`HubReplayer`, `HubSlotAllocator`); `MdSoundOnNes` refactored onto it | One documented policy for every Sega-sound-on-NES bridge (section 4.2). |
| YM2413 on NES chips | `mix/SmsFmSoundOnNes.cs`, id `NES-FM:<chip>` (Master System only) | The Japanese console's PSG + YM2413 (18 hub voices: 0-3 PSG, 4-12 FM, 13-17 rhythm) played by any NES chip. |
| Sound hub in every menu | `systems/ConsoleSystems.cs` (`HubSoundChips`, `HubSoundLabel`) | The GB, SNES, SMS/GG and Genesis APU menus list the other consoles' chips as `NES:<chip>` entries (DMG, DMGS, SNES, SMS, SMSFM, MD), with honest labels. |
| Hot-swap parity | `systems/ConsoleSessions.cs`, `mix/SegaPictureSession.cs`, `md/MdSession.cs` | `IConsoleSession.SwapRefusal(slot, id)` (a default member, forwarded by the wrapper); `TrySwapCore` refuses ids the menu does not list (SNES, Game Boy, Sega wrapper); the Genesis sound unit says why it restarts. |
| Sweep classifier | `Workshop/Sega/BridgeSweepCli.cs` | `--menus --swaps --cross --ids --entries --slots --include-native`, classes alive / lossy / diverged / restart / static / frozen / dead / refused / crashed / hung plus an audio class; swap cells are judged on the late half of the run. |
| Headless verifiers | `Workshop/Sega/MdBridgeScenesCli.cs` (`--md-bridge-scenes`: any scene ROM through the Genesis picture bridge, section 4.4), `Workshop/Sega/CpuWallTestCli.cs` (`--cpu-wall-test`), `SwapMatrixTestCli.cs` (`--swap-matrix-test`), `SessionTestCli.cs` (`--session-test`), `SmsFmOnNesTests.cs` + `PatchDistinctTests.cs` (groups `smsfmnes`, `patches` of `--sega-bridge-test`) | Section 7. |
| UAT | `UAT/cases/sega-sessions.json` (22 cases), `UAT/cases/walls.json` (3), `UAT/run-suite.ps1` (case kind `session`), `UAT/lib/Prepare-SegaRoms.ps1`, `UAT/sega-gate.ps1`, `UAT/certify.ps1` (`suite`, `sega-gate`, `sega-preview`, `notices`, `plugin-synth` entries), `UAT/entrypoints/notices-check.ps1` | Section 8. |
| Desktop | `Windows/MainForm/*`, `EmulatorConfig.cs`, `webapi/*`, `Rendering/NesDirectXRenderer.cs`, `ControllerConfigWindow.cs`, `sega/SegaOptions.cs` | Section 5. |
| Notices | `THIRD_PARTY_NOTICES.md` (root and `sega/`), csproj copy rules, `PluginInstaller.CopyNotices`, `WebLite` publish + header link | Section 9. |
| Bridge fixes | `mix/GbPictureBridges.cs`, `mix/MdToNes.cs`, `ppus/ExtPicture.cs` | `GbShades.Cgb` masks bit 15 (a Game Boy Color game on `SNES:SFC` crashed with IndexOutOfRange); the Genesis bridge follows B4a's three VDP rules (section 4.4). |

## 2. The combination matrix

**Method.** `BridgeSweepCli` (`--bridge-sweep`), 400 frames per cell, 11 games over the 7 consoles (NES `smb1`, `vrun`; SNES `smw`; Game Boy `sml`; Game Boy Color `zeldadx`; Master System `alexkidd`, `sonic-sms`; Game Gear `sonic-gg`, `columns-gg`; Genesis `sonic1`, `sor2`), each cell in its own child process with a timeout; every game also runs on its console's own cores (the control) so "dead" and "frozen" mean the bridge failed to show what the game itself shows. A swap cell changes the core in the middle of the run and is judged on the late half. Classes: **alive** (changes, similar to the control), **lossy** (alive but under 0.55 structural similarity to the source chip's picture), **diverged** (alive and different from the control: the chip draws the game differently, by design), **restart** (the swap cannot be done in place and the game restarts, with the reason), **static** (nothing to judge: the game idled in the control too), **frozen / dead / crashed / hung / refused**.

**The last full run** (exe `bn_sega_c4b`, 2026-10-15 13:05 build, after the MdSession reasons, the late-audio rule and the diverged exemption; sweep reports kept in the scratchpad as `final_A_menus`, `final_B_swaps`, `final_C_cross`, not in git):

*A. Menu sweep (`--menus`, 426 runs): every entry of every slot of every console that is not the console's own core, one game per cell.* An "entry" is a menu item; a "cell" is entry x game.

| Console | CPU slot | Picture slot | Sound slot |
|---|---|---|---|
| NES (cross-family entries) | 4 entries, 8 cells: **Z80 dead, 68000 dead** (both silent too), **SM83 diverged on SMB1, dead on VRUN**, **SNES 65816 identical to the control on SMB1, dead on VRUN** | 6 entries, 12 cells: 12 alive | 6 entries, 12 cells: 12 alive |
| SNES | only its own | 18 cells: 18 alive | 25 entries: 24 alive, **HLE silent** (the SNES's own high-level-emulation unit makes no sound by design) |
| Game Boy | 5 entries: 4 alive, **65816 frozen** (boot logo) | 16 cells: 16 alive | 24 cells: 24 alive |
| Game Boy Color | 5 entries: 4 alive, **65816 dead** | 16 cells: 16 alive | 24 cells: 24 alive |
| Master System | only its own | 18 entries, 36 cells: 30 alive, **6 lossy** | 26 entries, 52 cells: 52 alive |
| Game Gear | only its own | 18 entries, 36 cells: 31 alive, **5 lossy** | 23 entries, 46 cells: 46 alive |
| Genesis | only its own | 18 entries, 36 cells: 31 alive, **5 lossy** | 22 entries, 44 cells: 43 alive, **1 dead** (Streets of Rage 2 on `NES:ULQ`: silent, the ULQ chip quantises levels coarsely by design) |
| **Totals** | 18 cells: 9 alive, 1 diverged, 1 frozen, 7 dead | **170 cells: 154 alive, 16 lossy, 0 dead / frozen / crashed** | **227 cells: 225 alive, 2 dead** |

Lossy pictures are the 4-shade chips (`NES:DMG`, `NES:DMGX`, `NES:DMGS`, and `NES:ULQ` on one game): a Sega picture shown in four shades keeps its shapes but not its look (structural similarity of the lossy cells from -0.13 to 0.51; the same chips are alive on a game where the picture is simpler, 0.59 to 0.60). Every other chip is alive by the classifier's rule (average similarity to the source chip's picture of 0.55 or more), and the layered `NES:FIXS` reproduces the Genesis / Master System / Game Gear picture to the NES gamut (0 pixels differ from the VDP's own picture in the checked scenes).

*B. Swap sweep (`--swaps`, 281 runs): every entry swapped into a running game at frame 200, judged on frames 200-400.* 7 games (smb1, smw, sml, zeldadx, alexkidd, sonic-gg, sonic1), 274 cells:

| Console | Entries | Result |
|---|---|---|
| Master System, Game Gear | 44, 41 | all alive, swapped in place |
| SNES | 43 | all alive in place (`HLE`, silent by design, is judged on audio only) |
| Game Boy Color | 45 | 44 alive, **65816 dead** |
| NES | 16 | 13 alive, **Z80 and 68000 frozen** (the walls, silent), **SM83 diverged** |
| Genesis | 40 | 18 alive in place; **22 `restart`** (all of the Genesis sound slot except the stock unit): the game restarts on the new chip and the menu / API say "the Genesis sound unit (the Z80 sound driver, the YM2612 and the PSG with the bus handshake the 68000 sees) cannot yet hand its state to NES:<chip> while the game runs" |
| Game Boy | 45 | **40 static** in the run above (the script walked no picture in the late half: Super Mario Land sat on a title the control did not move either; a Tetris attempt did no better), 4 alive, the 65816 diverged. **Judged since** (section 14.2: a script that presses Start three times and walks right, 600 frames): **43 alive, 1 diverged (the 65816 half-rate case), 1 frozen (the 65816 wall), 0 static** |
| **Totals** | 274 | in the run above: **207 alive, 2 diverged, 22 restart, 40 static, 2 frozen, 1 dead, 0 refused / crashed / hung**; with the Game Boy rows judged the 40 static cells become 43 alive, 1 diverged and 1 frozen (section 14.2) |

The Sega consoles' own swaps (CPU, picture, sound, every menu entry) are not left to the sweep's one game each: `--swap-matrix-test` (section 7) swaps every entry of every slot of every console and checks the answer.

*C. Cross product (`--cross`, 6 games, a picture chip and a sound unit chosen together, per console 8 to 12 combinations):* **60 alive, 24 dead, 0 diverged / crashed / hung.** Game Boy 11, Master System 11, SNES 11, Game Gear 8, Genesis 8 combinations, all alive; the NES: 11 alive with its own 6502, and **24 dead** = `CPU_Z80` or `CPU_M68K` (2) x the same 12 picture / sound combinations, every one dead and silent.

**Where this leaves each direction.**
- *Alive, in place:* every Sega game on every NES-family picture chip (`NES:<chip>`, 18 entries per console); the NES, Game Boy and SNES families' sound chips for every Sega game (the sound hub, `NES:<chip>`, 22 to 26 entries per console, plus `NES-FM:<chip>` on the Master System); the PSG, YM2413 and YM2612 playing NES games; Game Boy, Game Boy Color and SNES games on the NES-family picture chips and on the sound hub (16 / 16 / 18 picture and 24 / 24 / 24 sound cells alive).
- *Lossy (reported by the metric):* the 4-shade chips (16 cells, above).
- *Unavailable, with the reason in the menu and the API:* NES games on `CPU_Z80` / `CPU_M68K` (instruction-set walls), NES games on the SM83 and 65816 where they cannot run them, Game Boy / Color games on the 65816, the live (no-restart) swap of the Genesis sound unit (a restart, with the reason; B4b's domain), and the two silent sound cells above.

## 3. Dead combinations: decisions

Rule: for each dead, frozen, black, silent or crashing combination, decide with evidence between an **honest rescue** (adapt the core) and **reporting it unavailable** with the reason in the menu and the API. Never substitute another core, roll back, or detect and switch.

| Combination | Evidence | Decision |
|---|---|---|
| Game Boy Color game, picture `SNES:SFC` | The first sweep: **crashed** (IndexOutOfRange in `GbShades.Cgb`: a colour with palette bit 15 set indexed a 32,768-entry table out of range) | **Rescued**: the mask is `& 0x7FFF`. Alive since (the sweep and the picture-bridge test). |
| NES game on `CPU_Z80` / `CPU_M68K` | 24 of 24 cross cells dead and silent; the hand-written checks (`--cpu-wall-test`, 27) show the chips themselves work on the NES bus (a Z80 program counts frames through the NES registers, a 68000 program drives the PPU, a 6502 wall check, a mid-game swap) | **Unavailable by design**: the games are 6502 machine code, and running them would need a 6502 inside the other CPU (a substitute core). The menu label (flag `CoreOption.Wall`, shown by the menus and the HTTP API) reads "an instruction-set wall: it runs the game's 6502 code as Z80 (68000) instructions, so nearly every game stops at once"; the sweep classifies dead. Both CPUs stay in the menu because the NES-bus programs run on them. |
| NES game on the Game Boy's SM83 | SMB1 diverged (`d0.25`), VRUN dead | **Unavailable**, same reason; labelled as a wall ("most games stop at once"). |
| NES game on the SNES 65816 | SMB1 identical to the control (its emulation mode runs the official 6502 set), VRUN dead (it uses unofficial opcodes the 65816 does not have) | **Not a wall, labelled with its limit**: "runs the game's 6502 code in its emulation mode; a game that uses the 6502's unofficial opcodes stops". Not rescued: giving the 65816 the unofficial set would make it a different CPU. |
| Game Boy / Color game on the 65816 | Boot logo frozen / dead | **Unavailable**; labelled "an instruction-set wall: it runs the game's SM83 code as 65816 instructions, so nearly every game stops at the boot logo". |
| Genesis game, sound on any NES chip, swapped live | 22 of 23 entries `restart`: `TrySwapCore` refuses (the sound unit's Z80 driver, YM2612 and PSG with the 68000-visible bus handshake cannot hand state to the NES chip mid-game) | **Restart with the reason** in `SwapRefusal` (title, menu note and API). A live swap is B4b's file (`MdSession`) and is listed under open problems. |
| Streets of Rage 2 sound on `NES:ULQ` | Silent: the ULQ chip quantises levels coarsely (a gimmick-personality core, not an accuracy target) | **Unavailable as a usable choice, left in the menu**: the personality is the point of the chip; no rescue. |
| SNES `HLE` sound | Silent by design | Unchanged, reported. |
| 4-shade Game Boy chips as Sega pictures | 16 lossy cells | **Reported lossy** (inherent to 4 shades). |

Nothing was substituted, rolled back or detected-and-switched. An id the menu does not list is refused (`TrySwapCore` false, `SwapRefusal` says why), never replaced; `--swap-matrix-test` checks that a garbage id is refused on every console.

What the wall test shows of the chips themselves (`--cpu-wall-test`): a tiny program written for that chip, in a NES cartridge, drives the NES screen through the PPU registers (its NMI handler is reached through the NES vector, the backdrop colour cycles with the frames, its IRQ handler through the NES IRQ vector); the stock-clock timing is the documented ratio (a NOP is 2 NES cycles on the Z80; 30 clocks of the 68000 are 7); the same job written in 6502 works on `CPU_FIX` and does not on either wall; swapping the CPU in the middle of a run is accepted and the old game stops without a crash.

## 4. Sound bridges

### 4.1 YM2413 through NES chips (`NES-FM:<chip>`)
`SmsFmSoundOnNes` wraps the real `SmsFmSound` (the PSG and the YM2413 on the board, as `ISmsSound`) and hands its 18 voices to the shared allocator, which places them on the NES chip's channels. Level: `FmFullRms = 2042/32768/sqrt(2)` per FM channel at full volume; the scale is `min(1, PulseRms(2,15) / (0.5 * psg.Gain * PsgGain))` so the loudest FM voice never exceeds a NES pulse at its loudest. Registered as `["NES-FM"]` in `sega/SegaSoundFactories.cs`; only on the Master System (and Game Gear) APU menus, labelled with what is played.
Measured (group `smsfmnes` of `--sega-bridge-test`, 28 checks): the ids (`NES-FM:FIX`, `NES-FM` alone means FIX, `NES-FM:FIXS` builds the 8-channel one; an unknown chip is an error, never a quiet substitute); the unit has the FM chip (a game's detection routine finds it) and the audio-control register reads back; FM pitch to the nearest NES period (the timer step is 27 cents at 1760 Hz, so the check is the nearest period, not a fixed tolerance); FM volumes 0-3 within the NES floor, a voice below the quietest step (volume 9, 27 dB down) is silent and counted; the policy (4.2); the audio-control register gating FM and PSG; rhythm keys released free their channels; a saved state loads into a fresh bridge, and a state from the stock SMS-FM unit loads too; the speed of the bridge in processor time (10.7x on CPU time, 14x on wall time). Shown able to fail: 5 injected bugs, each flagged.

### 4.2 The voice-slot policy (`HubSlotAllocator`)
1. A voice that was already placed keeps its channel (no hopping).
2. New voices are placed loudest first, on the free pulse that matches best.
3. A newcomer more than **6 dB** louder than the quietest placed pulse replaces it; otherwise the newcomer is **dropped and counted** ("voices dropped: no free channel").
4. The triangle takes only voices at least **35 percent** of its fixed level (it has no volume control), the rest go to the pulses.
5. Notes below the chip's floor follow `LowNotePolicy` (moved up an octave or dropped, counted).
6. The loop is bounded by the voice count (an injected bug that made it a `while (true)` hung the verifier; fixed so a wrong policy fails instead of hanging).

### 4.3 PitchGuard and the volume panel
The desktop volume panel has a second combo ("SMS FM instrument", 22 instruments, saved as `SmsFmPatch`, API field `SmsFmPatch` of `/api/audio/emulation-volume`). `APU_SMSFM.SetPatchEverywhere` plus a live registry reach every running core, so a change under a held note takes effect without a restart (checked in the smsfm group). The patch bank is analysed, not just measured at one pitch (`PatchDistinctTests`, group `patches`, 6 checks): every patch sounds a steady tone at the asked pitch (C4, within 15 cents); every pair differs by at least 2 dB RMS in harmonics and envelope (early, late and envelope fingerprints; the closest pairs are Genesis Pluck / Bass punch 2.9 dB and OPLL Trumpet / Fed-back lead 4.8 dB); the level spread on the bank's own calibration is 1.44 dB (Genesis, settled) and 3.03 dB (OPLL, first 350 ms; bound 3.5). **What needs ears:** whether 2.9 dB apart is audibly distinct, whether the six designed OPLL instruments are pleasant, and the volume panel's balance at the extremes.

### 4.4 Genesis picture bridge and the rules Track B4a measured (late request from the lead)
B4a's VDP branch (record: `docs/projects/sega-track-b4a.md` sections 1.2 and 1.3, measured on BlastEm; two relays from the lead) changes rules that `MdToNes` reproduced. Every rule below has checks in `--picture-bridge-test` group "Genesis VDP rules measured by Track B4a" that compute the expected picture **from the rule, not from this branch's VDP**, were written first and **failed on the bridge as it was** (the second set: 15 of the new checks failed, then passed with the change), and each scene also counts the pixels the old rule would have drawn differently (17,920 to 57,344), so a scene that cannot tell them apart fails.

| Rule (B4a) | In the bridge |
|---|---|
| **2-cell column scroll by cell pairs**: pairs start at `hs & 15`; the strip before the first pair is the last column's two entries ANDed in H40 and **0 in H32**; with a **window at the left** of 16 w pixels and `hs & 15` != 0, plane A's first b0 pixels after the window take column w (the pair after); the scroll mask is **0x7FF in interlace 2** | `MdToNes.VerticalScroll` (flat path, and the per-line consistency count) and `ExtPicture.Layer` (`ColumnScroll` by pair number, `ColumnShift` per line, `ColumnLeft`, `ColumnExtraFrom` / `ColumnExtra` for the window strip, `ColumnValue`) |
| **Invalid plane sizes**: 128 wide forces 32 high; 64 wide with height code 128 is 64 x 64 | `MdToNes.PlaneOf` |
| **Shadow/highlight, colour 14 / 15 of a low-priority sprite** (14 normal, 15 follows the state) | Nothing to mirror: the bridge draws no shading. The operators (palette 3, colours 14 and 15 in this mode) are see-through and counted ("sprite pieces holding shadow/highlight operators") as before; S/H scenes of B4a's sets stay mask-exact or at noise level |
| **Left column blank** (R0 bit 5): the first 8 pixels are the backdrop; with shadow/highlight on, the backdrop there is *shadowed* and the planes of cell 1 (x 8-15) are cell 0's planes (sprites stay) | `ExtPicture.Layer.BlankLeft` (nothing of the layer in those pixels), `ObjHidden` for the sprites, `CellOneStart` (cell 1 shows cell 0's pixels) on the layered path; the shadowed backdrop is not drawn (counted: "lines with the left column blanked in shadow/highlight mode"); the flat path counts the blank lines and draws the column |
| **Interlace mode 2**: cells 8 x 16 (64-byte patterns), the field's line y shows pattern row 2y + field, scroll (11 bits) and sprite Y (offset 256) count picture rows | Before: the bridge read 8x8 tiles out of 64-byte patterns (garbage, counted as "drawn as normal frames"). Now the field is drawn from the **even rows** of the cells (`TilePixel`, `ContentOf` over 64 bytes, scroll and sprite Y halved to field lines): a recognisable picture, **a row off at odd scrolls and vertical flips** because the field flag is private to `MdVdp` (B4a's file). Counted as "frames in interlace mode 2 (the field is drawn from the even rows of the 8x16 cells)" |

**The woven picture and `DisplayWidth` 640.** With B4a's VDP the native session in interlace 2 reports FrameHeight 448 / 480 and DisplayWidth 640 (a 320 x 448 picture shown 640 wide). Checked, by reading and by tests on a scratch merge: `SegaPictureSession` forwards the three sizes when native (and `Frame` holds the picture) and reports the NES canvas, 256 x 240 shown 256 wide, when bridged; the desktop (`MainForm.Consoles.cs`) rebuilds its buffers from `FrameWidth` / `FrameHeight` / `DisplayWidth` every frame and takes the aspect from `DisplayWidth : FrameHeight` (640:448 is 10:7 like 320:224; the renderer's 8:7 rule only fires for a 256-wide picture); Lite's `_setFrameSize` takes any width, height and display width. Three new checks (own chip in H40 and H32, a NES chip) pass on this branch (the field, 224 high) and on the merge (the woven picture, 448 high). **Not run:** a real interlace-2 game on screen in the desktop app or Lite.

**The bridge over B4a's pinned scenes (`--md-bridge-scenes`, new).** Their 24 scene sets (516 scenes: 21 seeded random-scene families and 3 probe sets, each pixel-identical with BlastEm on their VDP) run through the bridge on `NES:FIXS`, bridge picture against the VDP's own: **453 exact in the mask (a pixel drawn in one picture and not the other), 63 at noise level (at most 3.5 percent of the picture: a random tile has up to 15 colours and the bridge keeps 3, so a very dark colour becomes the NES's black), 0 differing with no loss counted; the 31 designed probe scenes (column scroll, left windows, left blank column) are exact to the pixel.** Honest limits: the mask cannot see a displaced noise texture (every pixel of a full plane is "drawn"), SSIM on random scenes averages 0.4 to 0.6 (0.17 to 0.2 for interlace 2, where the row parity is approximated), and the designed probe scenes are only partly blank-sensitive. Shown able to fail: with the old column rules injected, 5 of the 31 probe scenes differ (416 and 744 mask pixels) and the tool exits 1. The tool needs B4a's VDP to compare with the right picture; the runs above are on a scratch merge, built outside git (a patch of this branch applies cleanly on B4a's head).

## 5. Desktop integration audit (against Game Boy / SNES)

Done by reading the desktop code against the Game Boy and SNES paths (an Explore agent produced the findings list A1-A6, B1-B8, C1-C7, D, E; its output is data, I verified each fix myself) and fixed in small commits. Each row is something a Sega console lacked or did differently.

| Area | Change (files) |
|---|---|
| Pacing | The emulation loop paces at the session's own rate (`session.FramesPerSecond`: each console's own NTSC or PAL rate) instead of the NES rate (`MainForm.Emulation.cs`). |
| Window and picture | The size check includes the picture's display width (Genesis interlace-2 shows 640 wide); the renderer's 8:7 pixel-aspect rule applies only to pictures at least 224 lines high (`NesDirectXRenderer.cs`, `MainForm.Consoles.cs`). |
| Input | Pad mapping per console (`ForConsole`: Master System Start is the console's Pause button, Game Gear has no Select); the controller window tooltips give each console's button meanings (`ControllerConfigWindow.cs`); the API has 15 buttons, names `C`, `Z`, `Mode`, aliases `pause`, `reset` (`WebApiServer.Endpoints.Input.cs`). Separate key bindings for C / Z / Mode were decided against: hints and API names only. |
| Menus | A Sega machine menu (`BuildSegaMenu`; words in one place, `sega/SegaOptions.cs`): video standard (Automatic / NTSC 60 Hz / PAL 50 Hz), Genesis board (Automatic / Model 1 / 2 / 3), Master System board (Automatic / Mark III / SMS 1 / SMS 2); saved in `EmulatorConfig` (`SegaRegion`, `GenesisModel`, `MasterSystemModel`, `SmsFmPatch`); applied at session creation through `ConsoleSessions.Create(..., SegaOptions)` and, from the API, `/api/console/sega` GET / POST (the game restarts on the new machine, with the reason in the title). |
| Volume panel | A second combo for the SMS FM instrument (22), panel height 362, `SmsFmPatch` in `/api/audio/emulation-volume` (`MainForm.Volume.cs`, `WebApiServer.Endpoints.Audio.cs`). |
| Status line and API | `/api/console` carries Region, Model, HasBattery, SupportsState, StateUnsupportedReason and Note; the window title names the console and, after a swap that restarted the game, why; `current-rom` works for sessions (`MainForm.Initialization.cs`). |
| Save states and ROM loading | Quick save / load through the API answers with a reason when a console has none (`StateResult`, `QuickStateFromApi`); the app's Save State / Load State menu unchanged. ROM loading by file, zip and API already routed by `RomDetect`; verified for all three Sega consoles by the smokes below. |
| Docs | `Windows/webapi/README.md` (Sega settings, state results, buttons), `UAT/README.md`. |

Verified by: `desktop-smoke.ps1` 9 / 9, `sega-preview-smoke.ps1` 47 / 47, `install-smoke.ps1` 14 / 14 (all on my build, run alone), and the API integration batch in the UAT suite (section 8).

**Not done from the audit** (each is small, none blocks the gate): Genesis save states (B4b's `SupportsState`; the app says so with a reason); a live Genesis APU swap without a restart (B4b: the sound unit restarts the game and says why, section 3); the session-level Master System / Game Gear Pause (A4's `SmsSession.SetPad`; only the desktop-layer mapping is done); Lite's UI for region / model / SMS FM instrument (C4a); bridge loss counters in a desktop window (they are in the status API and the sweep). *Done later* (section 14.3): a clearer error for a file no console recognises, and tooltips on the non-NES core menus.

## 6. FL plugin

**Speed floors, re-measured with an interleaved A/B against `refs/heads/main`** (built into separate folders, run alternately, three rounds). The machine was never quiet: other agents kept it at 58 to 86 percent busy during the whole run, so **the quiet-machine floors were not confirmed**. Result under that load: this branch passed all four speed-floor checks (`md`, `fm`, `standalone-rack`, `realtime`) in 2 of 3 rounds; main passed 0 of 4, 3 of 4 and 3 of 4 in its rounds. No regression against main; no claim that the floors hold on a quiet machine.

**Self-test on the fresh Native AOT DLL** (`FruityHost selftest`): 45 passed, 1 warning (QLQ plays an octave sharp on purpose), 1 failed: `realtime` at exactly 5.0x against a floor of "5x or better" in the first run, load-sensitive.

**Real games in ROM mode** (`BROKENNES_TEST_GAMES`: `alexkidd.sms`, `sonic.gg`, `columns.gg`, `sonic1.md`, `sor2.md`, `sml.gb`, `smw.smc`): `runaway-sega` passed; `realtime` failed only on load-sensitive floors (4 instances 4.0x against 5x, ROM mode 2.4x against 2.5x). Measured in processor time under load: Master System 10.7x, Game Gear 12.8x and 13.2x, Genesis **2.9x and 2.7x** (2.0x with 11 stems claimed), Game Boy 8.9x, SNES 7.0x. The Genesis games are the slow ones: the real FM chip and the 68000 together. Every Sega console works in ROM mode (the test games pass for all three); one finding: a `.zip` in `BROKENNES_TEST_GAMES` is mapped to console 3 by its extension (the test harness's own mapping, not the plugin's), so list the unzipped file.

**The real-mouse `inputs` self-test "failed on this desktop"** (the C3 record): **not reproduced**. `inputs` and `editor` passed 4 of 4 times here (three consecutive runs and the full self-test). The tests send messages to the plugin's own windows (`SendMessage`), not global input, so a foreground window cannot take the clicks; the earlier failures were most likely the machine load or another program covering the plugin's window at that moment. Not proven.

**Standalone synth smoke** (`UAT/plugin/synth-smoke.ps1`): failed 4 of 9 checks in one run because another agent's BlastEm window held the foreground and `keybd_event` keystrokes went to it. Fixed in two steps: `Focus` now asks for the foreground the way a program is allowed to (attaching to the holder's input queue), checks that the synth window really has it, and a key check that finds the foreground changed while its keys were down is reported **NOT RUN** (never a pass: the "keyboard off does nothing" check passes for nothing if the key went elsewhere; never a FAIL of the synth); menus and windows are waited for instead of found once. After the fix: 13 / 13 on the desktop build (one run), 13 / 13, 13 / 13, and 12 passing plus one honest NOT RUN (`tab_routing`) in three further runs; `certify.ps1` reports a NOT RUN synth smoke as NOT RUN. Shown able to fail: with the foreground request disabled the script reports `keyboard_switch_off` NOT RUN with the reason.

## 7. Verifiers, with numbers

**The Sega gate on this branch** (`pwsh UAT\sega-gate.ps1 -Build`, a fresh build of the committed tree, one machine shared with other agents): **18 steps passed, 1 failed, 0 skipped (full runs: 543 s on the first commit set, 486 s on the tree of the first report, 488 s on the tree of commit c1d6b10 after the second work list).** The table is the first-report run, except the two rows that changed since (`--sega-test` 65 to 72, `--picture-bridge-test` 230 to 236 passed, both from section 14; the rest of the third run is the same numbers).

| Step | Result | Passed / failed | Seconds |
|---|---|---|---|
| `--sega-test` | PASS | 72 / 0 (65 + 7 catalog checks, 14.3) | 1 |
| `--sega-chips-test` | PASS | 34 / 0 | 1 |
| `--sega-contracts-test` | PASS | 80 / 0 | 1 |
| `--cpu-wall-test` (new) | PASS | 27 / 0 | 2 |
| `--swap-matrix-test` (new) | PASS | 21 / 0 (35 / 0 since 14.6: the sound checks and the converter) | 46 |
| `--sega-bridge-test` (incl. the new `smsfmnes` 28 and `patches` 6) | PASS | 356 / 0 | 202 |
| `--sega-stems-test` | PASS | 38 / 0 | 15 |
| `--picture-bridge-test` | **FAIL** | 236 / 1 (230 + the 6 synthetic allocation checks, 14.5) | 22 |
| `--md-test` | PASS | 144 / 0 | 1 |
| `--md-cart-test` | PASS | 47 / 0 | 0 |
| `--md-pic-test` | PASS | 93 / 0 | 1 |
| `--md-golden` | PASS | 75 / 0 | 127 |
| `--m68ktest` (vectors) | PASS | 1,284,785 / 0 | 18 |
| `--z80test` self-test, vectors | PASS | 57 / 0, 1,604,000 / 0 | 0, 18 |
| `--sega-oracle-test` | PASS | 17 / 0 | 4 |
| `--ym-oracle-test` | PASS | 43 / 0 | 13 |
| `--fm-oracle-test` | PASS | 173 / 0 | 13 |
| `--smstest` | PASS | 301 / 0 (2 skipped inside: missing input) | 35 |

The one failure is the reference check of `--picture-bridge-test` named in 4.4 and 12: it compares the bridge with *this* branch's VDP, which has the old column-scroll rule. On a **scratch merge** (B4a's branch head 5772409 exported with `git archive`, this branch's patch applied on it, built under `%LOCALAPPDATA%\VRUN_Nes_Dev_work`, nothing merged in git) the same test is **231 passed, 0 failed**, `--md-pic-test` 129 / 0 and `--md-test` 150 / 0. (Before the second set of B4a rules, with only B4a's two VDP files copied in: 208 / 0.) The gate refuses a stale exe (shown: it exits 2 naming the newer source file).

**Verifiers added or changed by this track, with their proof of failure** (each: a bug injected by text replacement, built, run, reverted with `git checkout`; the harness is not in the repo):

| Verifier | Checks | Shown able to fail |
|---|---|---|
| `--cpu-wall-test` | 27 | 4 injected bugs (in the Z80 and 68000 interrupt and timing paths), each flagged; one run with them in shows 6 Z80 and 3 68000 checks failing (NMI / IRQ handler not reached, the backdrop never cycling, a NOP not 2 NES cycles, 30 clocks not 7 cycles) |
| `--swap-matrix-test` | 21 (every entry of every slot of every console; a garbage id refused; the Genesis sound unit restarts with a reason containing "sound unit") | injections on the Game Boy, Master System and Genesis rows flagged (3 runs) |
| `--sega-bridge-test` group `smsfmnes` | 28 | 5 injected bugs, 5 failing checks (the unit not showing its FM chip to a game's detection, FM level +5.8 dB over the stock unit, voice drops not counted, a newcomer taking the wrong channels, a 3 dB newcomer allowed to take a channel) |
| `--sega-bridge-test` group `patches` | 6 | 2 injections this round: Marimba made identical to Pluck (flagged: "Pluck / Marimba 0.3 dB"), Sine's carrier 60 steps quieter (flagged: "loudest 29.30 dB over the quietest") |
| `--picture-bridge-test`, group "Genesis VDP rules" | first set 25 (column scroll 10, plane sizes 3, operators 12), second set 21 more (column scroll with left windows 6, left blank column in two modes 9, interlace 2: 3, own-chip and NES-chip picture size 3) | first set: 3 injections (old column rule, plane-size rule removed, operators drawn as pixels): 14 failures; second set: written first, 15 of the new checks failed on the unchanged bridge and pass now; the old rule would draw 17,920 to 57,344 pixels differently per scene |
| `--picture-bridge-test`, sweep classifier | 26 (alive, lossy, diverged incl. its exemption, restart, static, frozen, dead, refused; audio classes; the late-half rule for swaps) | 5 threshold injections (the lossy similarity, the silent level, the diverged threshold and its exemption) each flagged by the matching check; the diverged class became a function with its own two checks after the first injection showed no check covered it |
| `--md-bridge-scenes` (new, not in the gate: needs B4a's scene ROMs and python) | B4a's 24 scene sets, 516 scenes | with the old column rules injected 5 of the 31 probe scenes differ and the tool exits 1 (section 4.4) |
| UAT `sega-sessions.json` / `walls.json` | 25 golden cases (22 + 3) | a poisoned golden fails its case |
| `notices-check.ps1` | tree + three output kinds | fails on an app, Lite or plugin folder lacking notices (shown for all three) |
| `synth-smoke.ps1` | 13 | with the foreground request disabled the keyboard check is NOT RUN with its reason (section 6) |

Also run on this branch, outside the gate: `desktop-smoke.ps1` 9 / 9, `install-smoke.ps1` 14 / 14, `sega-preview-smoke.ps1` 47 / 47 (each alone, on my desktop build).

## 8. Certification

**The command:** `pwsh -NoProfile -Command "& .\UAT\certify.ps1 -Only cores,desktop,plugin-install,plugin-synth,suite,sega-gate,sega-preview,notices"` (the `-Only` list must be passed through `-Command`: `-File` hands a comma list over as one string and every entry reports SKIPPED "not selected"; I hit exactly that). Not in this list, not run: `web-lite` (needs the headless browser and a Lite publish), `plugin-host` (run separately, section 6) and `plugin-fl` (needs the plugin installed in FL). Result on the final tree, in runs of the same entries (the UI entries need the desktop to themselves, so they ran apart from the slow ones):
- `cores` **PASS** (VRUN reproduces the recorded pictures). `suite` **PASS**: 30 pass, 0 fail, 60 skip (22 Sega + 3 wall + 5 mapper30 cases pass; the skips are NES, story and corpus cases whose ROMs are not on this machine, never passes). `sega-gate` **FAIL, 18 of 19 steps pass** in 486 s: the one failing check is the `--picture-bridge-test` reference check that needs B4a's VDP (section 4.4, 12).
- `sega-preview` **PASS 47 / 47**, `desktop` **PASS 9 / 9**, `plugin-install` **PASS 14 / 14**, `notices` **PASS 10 / 10** (the ledgers against the tree and the freshly built desktop folder; the Lite folder and the installed plugin's folder were skipped in that run, and shown failing and passing separately in section 9), `plugin-synth` **NOT RUN**: two keyboard checks could not be made because another window held the foreground (12 of 14 checks made, none failed).
- What the earlier runs of the same command taught: `sega-preview` once failed `off_graceful_shutdown` while other agents drove windows (47 / 47 on the rerun alone); `notices` failed against desktop and Lite folders left by earlier runs (the entry now checks only folders its own run built); the gate failed `--smstest` once on two speed checks (19x and 7.4x against 25x and 8x) at about 80 percent machine load, which the gate now names as "every failing check is a speed check".
- **Not in this run:** `web-lite`, `plugin-host` (run apart, section 6), `plugin-fl`.

**UAT cases in the same suite as the NES / SNES / Game Boy ones** (`UAT/run-suite.ps1`, manifests in `UAT/cases/`):
- `sega-sessions.json`: 22 cases of kind `session` (new): a Master System, Game Gear or Genesis game run headless through the app's own `ConsoleSessions` by `Workshop --session-test`, which prints one deterministic JSON line (picture hash, audio hash, audio level, distinct colours, swaps done). Own cores for each console; machine variants (PAL, models); the picture bridges (`NES:FIXS` and a flat chip) for each console; the sound hub; core swaps mid-run, ending on the answer the menu promises.
- `walls.json`: 3 cases of kind `romtest` for the instruction-set walls (a NES game on `CPU_Z80`, on `CPU_M68K` and on the SM83), kept so "dead stays dead" is pinned and a change to it is seen.
- `run-suite.ps1` gained the `session` case kind (`Invoke-SessionCase`) and `%VAR%` expansion in ROM roots (`{sms}`, `{gg}`, `{md}`); `UAT/lib/Prepare-SegaRoms.ps1` unpacks the Master System games that sit in 7z archives to `%LOCALAPPDATA%\VRUN_Nes_Dev_work\sega_roms\sms`. A ROM not on the machine SKIPs, never passes.
- All 25 cases carry a recorded golden (`expect`); the suite run in the certify result above checks them; earlier in the round the goldens were recorded and re-run with identical results (deterministic), and a deliberately poisoned golden made its case FAIL.
- Goldens say "nothing moved", not "correct" (the accuracy claims are the oracle comparisons of the other tracks).

**`UAT/sega-gate.ps1`**: the verifiers of section 7 in one table; options `-Build` (own artifacts folder, never the repo), `-Quick` (about 2 minutes: leaves out the long vector sets and the real-game groups), `-Strict` (a skip is a failure), `-Prove` (runs the verifiers that have a built-in proof of failure), `-Only`; refuses a stale exe (exits 2 naming the newer source); parses every verifier's own result line (a verifier that prints nothing is a failure, not a pass); writes `summary.md` / `summary.json`. Time on this shared machine: 543 s full.

**`UAT/certify.ps1`** entries added: `suite`, `sega-gate`, `sega-preview`, `notices` (and `plugin-synth` made to report NOT RUN honestly). `UAT/README.md` documents the Sega cases, the gate and the NOT RUN rule.

## 9. Notices

What ships: the Z80 core (BizHawk `Z80A`, MIT), the 68000 core (ares, ISC), the YM2612 core (ymfm, BSD-3-Clause), the YM2413 core (emu2413, MIT), the SN76489 notes (written here; the Nuked-* cores are oracles only and nothing of them is in the tree). The root `THIRD_PARTY_NOTICES.md` and `Windows/NesEmulator/sega/THIRD_PARTY_NOTICES.md` ledgers were made accurate (a stale "planned" Z80 row replaced by three rows that say what is in the tree; stale "planned" rows removed from the Sega ledger; section 2d added for the instruction-set walls and the hub bridges).

- Desktop: both ledgers are copied beside the exe on build and publish (`Windows/BrokenNes.Windows.csproj`).
- Plugin: `PluginInstaller.CopyNotices` puts them beside the installed DLL; `install-smoke.ps1` gained the check `notices_beside_dll` (14 / 14).
- Lite: published into `wwwroot` (`WebLite/BrokenNes.Lite.csproj`) with a "Notices" link in the header (`MainLayout.razor`); `LinkerConfig.xml` roots `CPU_Z80` and `CPU_M68K` so the walls survive trimming.
- **Certification check:** `UAT/entrypoints/notices-check.ps1` (and the `notices` entry of `certify.ps1`): every source file carrying another project's copyright header (BizHawk, ares, ymfm, emu2413) is named by a ledger, each project's license text is in the root ledger, no row still says "planned" for code that exists, and the notices (root ledger, the three family ledgers, LICENSE.txt) are present in each output folder it is given (`-AppDir`, `-LiteDir`, `-PluginDir`; a folder not given is SKIPPED, not passed). It passed on the tree, the desktop build and the Lite publish output, and **fails on a folder that lacks the notices** (shown on an output folder with them removed). Not run: a Lite AOT publish to a server (the output folder was checked, not a deployed site).

## 10. Decisions

1. **Dead stays dead and visible.** The walls (`CPU_Z80`, `CPU_M68K`, the SM83 on NES, the 65816 on Game Boy) stay in the menus with a label that says what happens; nothing hides them, rescues them with a hidden translator, or switches the core behind the user's back.
2. **One voice-slot policy for every Sega-sound-on-NES bridge** (4.2): the PSG, YM2612 and YM2413 bridges share `HubSlotAllocator`; a change to the policy changes all three and the tests of all three.
3. **`NES-FM` is a separate id from `NES`** on the Master System: the plain NES unit is the export console and has no FM; the two are different consoles (checked by name in `smsfmnes`).
4. **The sound hub appears in every console's menu** (`NES:<chip>` for the other families, honest labels), not only on the Sega ones, so the matrix has no one-way doors.
5. **`TrySwapCore` refuses an id the menu does not list**, on SNES, Game Boy and the Sega wrapper; the app then restarts the game on it and says why. Not a silent replacement.
6. **Goldens are regression guards, not accuracy claims.** The UAT cases pin picture / audio hashes of this build ("nothing moved"); accuracy is the oracle comparisons of A4, B4a, B4b.
7. **Key checks that cannot be made are NOT RUN, never PASS and never FAIL** (synth-smoke); the same status flows through `certify.ps1`.
8. **Sweep classes:** a swap is judged on the late half of the run; "diverged" (alive but not the control's picture) is exempt for a NES game drawn by another VDP, which differs by design.
9. **Speed of the FM bridge is measured in processor time** (a wall-clock check failed at 4.1x under 80 percent machine load and passed at 10.7x CPU / 14x wall).
10. **C / Z / Mode have no key bindings of their own** in the desktop: hints in the controller window and names in the API (the pad window stays the Game Boy / NES layout).
11. **B4a's VDP rules in the bridge** are implemented from the rules, behind checks that do not depend on this branch's VDP (4.4).

## 11. Not tested

- **Nobody has listened** to NES-FM, the allocator's choices, the 22 instruments (the six designed OPLL ones especially), the patch banks, or any bridge; every audio statement above is rendered-sample analysis.
- **The standalone synth's real playback device** (WASAPI): the smoke renders into nothing (`BROKENNES_SYNTH_AUDIO=null`).
- **Quiet-machine speed floors:** the plugin's four floors, the gate's speed checks and the bridges' frame rates were measured under other agents' load only (plugin A/B against main, section 6). No claim they hold on a quiet machine.
- **Lite:** only the notices and the linker roots (a published output folder was checked). Lite's UI for the new Sega options and a bridged picture on screen in Lite were not seen (C4a's side).
- **Genesis save states and a live Genesis sound-unit swap** (B4b).
- **Game Boy swap cells:** judged since section 14.2 (43 alive, 1 diverged, 1 frozen, with the 600-frame script); `--swap-matrix-test` also answers for every entry.
- **The reference check of `--picture-bridge-test`** ("2-cell column vertical scroll (20 different values) is followed") fails on this branch until B4a's VDP is in the tree; it was shown green only by copying B4a's two VDP files in temporarily (reverted), not by a merge.
- **A merge with the other round-4 branches:** none done (the brief says only `refs/heads/main` is merged, at wrap-up); `main` had not moved from `e758833` at the time of writing.
- **A fresh hand-off build** and a run on another machine.

## 12. Shared files touched

Files outside this track's own new files that other tracks also edit (expect textual conflicts at merge; none touched structurally):
- `Workshop/Program.cs`: three route lines (`--cpu-wall-test`, `--session-test`, `--swap-matrix-test`), next to the other tracks' routes (**the likeliest conflict**).
- `Windows/NesEmulator/md/MdSession.cs` (B4b): one additive `SwapRefusal` override (the sound-unit reason).
- `Windows/NesEmulator/ppus/ExtPicture.cs` (C3a's): `Layer.ColumnScroll` became pair-numbered with `ColumnLeft`, `ColumnShift`, `ColumnValue` (4.4).
- `Windows/NesEmulator/mix/MdToNes.cs`, `GbPictureBridges.cs`, `SegaPictureSession.cs`, `SmsFmBridge.cs`, `MdSoundOnNes.cs` (the bridges, mine by domain).
- `Windows/NesEmulator/systems/ConsoleSystems.cs`, `ConsoleSessions.cs`: catalog labels, `Wall`, hub entries, `SwapRefusal`, `SegaOptions` parameter.
- `Windows/NesEmulator/sega/SegaSoundFactories.cs` (`NES-FM`), `sega/SegaOptions.cs` (new), `sega/THIRD_PARTY_NOTICES.md`, root `THIRD_PARTY_NOTICES.md`.
- Desktop: `Windows/MainForm/MainForm.{Consoles,Emulation,Initialization,SaveStates,Volume}.cs`, `EmulatorConfig.cs`, `ControllerConfigWindow.cs`, `Rendering/NesDirectXRenderer.cs`, `Synth/PluginInstaller.cs`, `webapi/WebApiServer.Endpoints.{Audio,Console,Emulator,Input}.cs`, `webapi/README.md`, `BrokenNes.Windows.csproj`.
- Lite: `WebLite/BrokenNes.Lite.csproj`, `Layout/MainLayout.razor`, `LinkerConfig.xml`.
- Tests and UAT: `Workshop/Sega/{PictureBridgeSegaTests,PictureBridgeTestCli,SegaBridgeMdTests,SegaBridgeTestCli,SegaFoundationTestCli,SmsFmBridgeTests,BridgeSweepCli}.cs` (catalog checks widened for the hub and `NES-FM:` entries), `UAT/run-suite.ps1`, `UAT/README.md`, `UAT/certify.ps1`, `UAT/plugin/{install,synth}-smoke.ps1`.
- **Second work list (section 14):** `Windows/NesEmulator/mix/{SmsToNes,MdToNes,SegaQuantizer,FlatTilePacker,NesPictureSink}.cs` rewritten to keep their tables (MdToNes is also B4a's and mine by domain: the rule changes of 4.4 and the pooling touch the same functions, so expect to merge by hand and rerun the identity comparison of 14.5), `ppus/ExtPicture.cs` (`Layer.TakeColumnScroll`, 3 lines beside the C3a / B4a column-scroll members), `systems/ConsoleSystems.cs` (`CoreCatalog.Tip`, `RomDetect.UnrecognisedMessage`), `MainForm.{Consoles,Initialization}.cs` (the frame-loop race, the unrecognised-file throw, tooltips, `ShowItemToolTips`), `UAT/certify.ps1` (the `lite-sega` entry and its three parameters: it only calls C4a's `UAT/entrypoints/lite-sega.ps1`, which is not in this branch), `UAT/entrypoints/sega-preview-smoke.ps1`, `UAT/README.md`, `Windows/webapi/README.md`, new `Workshop/Sega/bridge-sweep-games.json`.
- **After B4a merges:** the Genesis session goldens in `UAT/cases/sega-sessions.json` (`sega-md-*`) pin pictures drawn by the VDP; B4a's timing and rule changes will (correctly) change them. Re-record them with `UAT\run-suite.ps1 -Filter 'sega-md-*' -UpdateGolden` after looking at the new pictures; do not loosen the check.

## 13. Open problems and next steps

1. **Live Genesis sound-unit swap** (22 restarts): the unit must hand its Z80 driver, YM2612 and PSG state to the NES-hosted chip while the 68000 keeps running (B4b's `MdSession`).
2. **Re-verify after B4a lands on `main`:** `--picture-bridge-test` (the reference check), `--bridge-sweep --cross` for the Genesis rows, and the Genesis session goldens (section 12). Merge `refs/heads/main` (not a tag named `main`; `fetch --no-tags`).
3. **Master System / Game Gear Pause at session level** (A4's `SmsSession.SetPad`); only the desktop-layer mapping exists.
4. **Lite UI** for region, board model and the SMS FM instrument (C4a); the API and desktop have them.
5. **Plugin on a quiet machine:** the four speed floors; the Genesis ROM mode is the one near the floor (2.7 to 2.9x real time in processor time under load, 2.0x with 11 stems claimed, against a 2.5x ROM-mode floor): the real YM2612 and 68000 together.
6. **A load-sensitive speed check in `--smstest`** (the nine-FM-channels check at 25x failed once at 80 percent load): A3 / A4's check, not changed here; the gate reports it by name.
7. **Desktop leftovers:** bridge loss counters in a window (skipped: not under an hour), separate bindings for C / Z / Mode if wanted. (The clearer message for an unrecognised file and the core-menu tooltips are done: 14.3.)
8. ~~Game Boy sweep coverage~~ done: 14.2.
9. **Listening:** a person should play NES-FM on two or three chips, the 22 instruments and the sound hub entries on a Genesis game, and say what is wrong; the numbers say the pitch and level are right, not that it is pleasant.

## 14. Second work list (after the first report)

Every number here is from a run on this machine, shared with other agents; the UI-driven ones need the desktop to themselves. Commits: `9a0f93a` (swap smoke, frame-loop race), `1ac99d5` (sweep manifest and Game Boy script), `346441f` (unrecognised file, tooltips), `62778b0` (`lite-sega` entry), `c1d6b10` (allocation).

### 14.1 Core swaps in the desktop smoke, and the race it found

`UAT/entrypoints/sega-preview-smoke.ps1` now swaps cores in the running desktop app for all three consoles through the app's own endpoint (`POST /api/console/core/{slot}/{id}`), and once through the real `Console > PPU` menu with UI Automation:

| Check (per console) | What it asserts |
|---|---|
| `*_swap_ppu_to_nes_fixs` | the swap answers success, the status names `NES:FIXS` and the bridge, the picture is 256x240, mostly not black, and differs from the native one |
| `*_swap_ppu_back_restores_the_picture` | back on the console's own chip the picture is the one from before the swap (same size, same hash of a settled frame) |
| `*_swap_ppu_game_keeps_running` | the picture changes under a held pad on the NES chip, and again on the console's own chip afterwards (a game that died on the swap fails here) |
| `sms_/gg_swap_apu_in_place`, `md_swap_apu_is_live_or_restarts_and_says_why`, `*_swap_apu_back` | the sound unit to `NES:FIXS` and back; the game still answers the pad. Master System and Game Gear swap in place; the Genesis check **passes on either side of the B4b merge**: a live swap (empty note) or a restart whose note matches `restarted on APU NES:FIXS: <20+ characters of reason>` (it asserts sanity and the reason text, not which path) |
| `*_swap_refuses_an_id_the_menu_does_not_list` | `NES:NOPE` answers `success=false` with a reason and the chip stays |
| `ui_ppu_menu_swaps_the_picture` | the menu item (found by the catalog label, then by prefix) invoked through UI Automation swaps the Master System's picture to the bridge |
| `unrecognised_file_says_what_it_plays`, `unrecognised_file_leaves_the_game_running` | section 14.3 |

Checks that need the app to hold the foreground (`on_menu_lists_seven`, the UI check) are **NOT RUN**, never PASS or FAIL, when another window has it; the result line carries `"status":"NOT RUN"` only when nothing failed, and `certify.ps1` reports it that way. `-SwapsOnly` runs just this section. Runs: the **whole smoke 68 / 68 in 101 s** (0 NOT RUN, 0 failed) on a desktop built from the tree of commit c1d6b10, and `-SwapsOnly` 26 / 26 in 54 s on the tree of commit 346441f; earlier, 4 of 5 runs of the UI check passed and the 5th was NOT RUN (the app had lost the foreground).

**What it found: a race that killed the emulation after a swap.** `RunSessionFrame` read the picture's width and height, then took the emulation lock, then copied `session.Frame`; a swap between the two (the Sega wrapper changes size from 256x192 to 256x240) made the copy index past the end, the exception ended the loop (`isEmulationRunning = false`) and the game stopped for good: the API reported `emulationActive=False` while a button was latched. Fixed in `MainForm.Consoles.cs`: size, buffer check and copy are read under the lock, with the copy length bounded. The loop also publishes the pad the session was handed (`PublishObserved`), so `/api/input/state` reports a console's real pad (found because the machine has an Xbox 360 controller whose stuck axes held Down / Left / X / Y at times; the picture checks use buttons that still move the picture then).

**Shown able to fail** (each: a bug put in, the smoke run, the bug taken out): an API swap that does nothing makes the three `*_swap_ppu_to_nes_fixs` fail; a menu handler that does nothing makes `ui_ppu_menu_swaps_the_picture` fail; a restart note without its reason fails `md_swap_apu_is_live_or_restarts_and_says_why`; the **old, racy frame loop** (kept in a scratch build) fails `md_` and `gg_swap_ppu_game_keeps_running` in 3 of 3 runs; the old desktop build fails both unrecognised-file checks.

### 14.2 The Game Boy swap cells are judged

`Workshop/Sega/bridge-sweep-games.json` (new; the sweep's game list is in the repo now, with the ROM folder taken from `BN_SWEEP_ROMS`, no machine paths) carries an input script for Super Mario Land that keeps the picture moving in the second half: `100:Start,106:,160:Start,166:,220:Start,226:,260:Right`, run with `--frames 600`. The 45 Game Boy swap cells that were 40 static + 4 alive + 1 diverged are now **43 alive, 1 diverged (`GB-HALF`), 1 frozen (the 65816 wall)**. `BridgeSweepCli` expands environment variables in the manifest's paths.

### 14.3 Desktop leftovers

- **A file no console recognises** (`RomDetect.UnrecognisedMessage`, thrown from `RouteRom`): the message names the file and its size and lists what BrokenNes plays, by console and extension (only the consoles that are on), says a renamed ROM is still read by its contents, and mentions the one near miss (a `.32x` cartridge). Before, the file went to the NES loader, the dialog said "Invalid iNES Header! Got: 74 68 69 73..." and **the running game was dropped**; now the game keeps running (checked by the smoke).
- **Tooltips on the non-NES core menu entries** (`CoreCatalog.Tip`; the menu strip has `ShowItemToolTips`; the NES menu keeps its overlay cards): what the entry is in a line or two and what the choice costs, honest about limits: the console's own part ("runs as it was made"); a picture chip from another family ("through a picture bridge ... what the part cannot show is counted: the status line and GET /api/console list the losses"); a sound chip ("voices it cannot hold are dropped and counted; the timbres are approximated, not reproduced"); the FM chip of the Master System; a CPU of another instruction set ("works only where the two instruction sets agree"); a wall keeps its own label.
- 7 catalog checks in `--sega-test` (65 to **72**): tooltips carry the label, state a cost, differ by slot, a wall's tooltip is its label, a text file is unrecognised, the message names file / size / extensions / zip, the 32X sentence. Shown able to fail: with `Tip` returning only the label and the message listing no consoles, `--sega-test` gave 70 passed, 2 failed.
- **Not done:** the bridge counters window (not under an hour: the counters are in the status line and in `GET /api/console`).

### 14.4 `lite-sega` in `certify.ps1`

`UAT/certify.ps1 -Only lite-sega` runs Track C4a's `UAT/entrypoints/lite-sega.ps1` (read from its worktree, commit c421178; the script and its `UAT/lite/*.mjs` are **not in this branch**: they arrive with C4a's merge). There is no "quick set" in `certify.ps1` (everything runs without `-Only`), so the entry is **opt-in**: without `-Only lite-sega` it reports SKIPPED. It is **NOT RUN with the reason** when: the script is not in the tree, node 22 or newer is missing, there is no Edge or Chrome, the `wasm-tools` workload is missing (and no `-LitePublish`), `-LitePublish` holds no site (`wwwroot\index.html`), `-LiteRoms` lacks `sms\ gg\ md\`, or `X:\EMULATION` / 7-Zip is not there for the script to gather its games. Otherwise PASS or FAIL from the script's exit code, with the check summary (and the script's error) in the detail. Parameters: `-LitePublish`, `-LiteRoms`, `-LiteOnly`.

Run, with C4a's files copied in temporarily (not committed): the missing-script reason in this tree; each other NOT RUN reason on a scratch copy of `certify.ps1` with the condition made false (no browser, no workload, node "too old"), plus a missing `-LitePublish` folder and a `-LiteRoms` without folders; a **PASS** (`-LiteOnly console`: 7 / 7, 8 s, against an earlier non-AOT publish of this branch) and a **FAIL** (`-LiteOnly layout,pickers,swap`: 20 of 30 checks, 531 s, certify exit 1) against the same older publish, whose failures name the checks. **Not run: the whole 109-check suite against a fresh AOT publish of a merged tree** (needs C4a's UI in the build, ~6 min publish + ~15 min).

### 14.5 Allocation: the picture bridges allocate nothing once warm

The Master System, Game Gear and Genesis pictures drawn by a NES chip allocated 88 to 441 KB a frame (up to 26 MB a second at 60 frames a second, all of it garbage for the collector) while the boards themselves, the NES chips and every other path of the pipeline allocated nothing. Measured with the repo's own method (`GC.GetAllocatedBytesForCurrentThread` around warm frames, as the plugin and sound checks do), in the new group **`--picture-bridge-test --only alloc`**: bytes per frame beyond what the board allocates alone (0 in every case), least of three windows after a warm-up.

| Case (bridge on the layered `NES:FIXS` / the flat `NES:FIX`) | Before (bytes a frame) | After |
|---|---|---|
| Synthetic scenes that move every frame: Master System / Game Gear / Genesis on `FIXS` | 126,544 / 121,880 / 204,696 | 0 / 0 / 0 |
| ... on `FIX` | 92,896 / 88,232 / 143,288 | 0 / 0 / 0 |
| Master System Alex Kidd, `FIXS` / `FIX` | 197,380 / 172,692 | 0 / 0 |
| Master System Sonic, `FIXS` / `FIX` | 146,688 / 196,096 | 0 / 0 |
| Game Gear Sonic, `FIXS` / `FIX` | 261,680 / 226,172 | 0 / 0 |
| Game Gear Columns, `FIXS` / `FIX` | 193,872 / 194,488 | 0 / 0 |
| Genesis Sonic, `FIXS` / `FIX` | 222,536 / 157,840 | 0 / 0 |
| Genesis Streets of Rage, `FIXS` / `FIX` | 436,688 / 402,828 | 12 / 14 |
| Genesis Golden Axe, `FIXS` / `FIX` | 414,916 / 440,608 | 0 / 0 |

The 12 and 14 bytes are a table still growing to the most that game has needed (a dictionary doubling; the check's allowance for real games is 256 bytes and `--alloc-types` names the types: `Entry[Int32,Int32][]`, `Int32[]`, `Entry[UInt64,Int32][]`). Real games are measured after 600 frames of warm-up (`--alloc-warm`, `--alloc-window`), with the game directories and `--only alloc` (or `--alloc-games`); the synthetic scenes (limit 0) run in every `--picture-bridge-test` and so in the Sega gate.

**What was allocating, and what changed** (`SmsToNes.cs`, `MdToNes.cs`, `SegaQuantizer.cs`, `FlatTilePacker.cs`, `NesPictureSink.cs`, and `ExtPicture.Layer.TakeColumnScroll`): a new `NesPicture` (10.5 KB of tables) and two `Enumerable.Repeat(...).ToArray()` per frame; the tile-identity dictionary (512 entries) of the Master System bridge; the Genesis sprite records with a per-line array each (72 KB at 80 sprites); a `bool` array per layer and per frame; colour tables, histograms (a `Dictionary` per tile) and closures per tile in the quantizer; the flat path's sort (`OrderByDescending`), packer tables and cells; lists, dictionaries and tuples. Now each bridge keeps its tables and clears them for the next frame; the quantizer keeps its histograms; the packer sorts one `long` per variant (count high to low, key low to high: the same order); the environment variable the sink read for every frame is read once. The colour-RAM copies the Genesis line capture made for every palette change are reused.

**Golden-exact, by comparison with the old translators** (a Workshop built from the committed old translators with the new test code, run beside the pooled one): every group of `--picture-bridge-test` (catalog 17, nes2sega 48, sega2nes 127 passed + the one known branch failure, session 12, classify 26, games 13: 0 differing lines of output, SSIM values and counters included); **18 scene sets** (`--md-bridge-scenes`, B4a's pinned sets, about 380 scenes) on `NES:FIXS` and again on `NES:FIX`: 18 of 18 outputs identical each time; a hash of every frame's picture over nine game runs (`--verbose`): identical; the Sega cases of the golden suite on their recorded goldens: 24 pass, 0 fail (it includes the bridged picture of Sonic on `NES:FIXS`).

**Shown able to fail:** the build with the old translators fails 20 of the 20 allocation checks; a 424-byte array put into the layered Master System path (static field, reverted) fails exactly the two cases that run it, at 424 bytes.

**Not covered:** the SNES and Game Boy bridges to the NES (`SnesToNes`, `GbPictureBridges`) still make tables per frame, and the other direction (a NES game on `PPU_SMS` / `PPU_MD`) was not looked at; neither is in the brief for this item.

### 14.6 The sound hub on the merged tree: what the swap suite found, and the gate

After A4, C4a and C4b were merged, A4's `--smstest --only swap` (159 checks on A4's own tree) failed 8 on the merged one: for the Master System and the Game Gear, `swap Apu -> NES:SNES` (47,839 samples against the control's 66,235), `NES:DMG` and `NES:DMGS` (72,092) and `NES:MD` (66,225 samples, "878.4 Hz where 440.4 is wanted"). Reproduced first (111 pass, 8 fail, 3 skip), then taken apart:

| Failure | Root cause | Fix |
|---|---|---|
| `NES:SNES`, `NES:DMG`, `NES:DMGS`: the wrong number of samples | The chips run at their own rates (the SNES sound unit 32,000 Hz, the Game Boy's 48,000) and the bridges (`SmsSoundOnNes`, `SmsFmSoundOnNes`, `MdSoundOnNes`) passed those samples on as they were. The Master System and Game Gear sessions report the unit's rate, so a host that follows it plays them right but at a rate that is not the console's own (and the swap suite counts samples); the **Genesis session reports its fixed 44,100 whatever the unit delivers**, so the same entries really played at the wrong pitch there: **606 Hz for 440 on `NES:SNES`, 404 Hz on `NES:DMG` / `NES:DMGS`** (measured on the Genesis menu; the Genesis APU menu lists these three) | New `ChipRateConverter` (64-tap Blackman windowed sinc, 512 positions, unit gain at each, no latency in samples, no allocation) and `NesChipOutput`: a chip whose rate differs from the machine's is converted, and every unit says the machine's rate. A chip at 44,100 passes through unchanged (same code path, same samples: only the two goldens below changed) |
| `NES:SNES` also 306 samples short after an in-place swap (65,929) | `APU_SNES` boots an SPC700 driver in its constructor, which takes the unit's clock ahead; the NES's time then counted from zero against a clock already at the boot's end, so the unit made no sound for about 7 ms after a swap | The unit's time counts from the end of its boot (`bootClock`) |
| `NES:MD`: 66,225 samples | `APU_MD`'s band-limited mixers hold back half their kernel (8 samples) before the first one is final: a unit started in the middle of a game delivered 8 (plus 2 of rounding) samples fewer than the stock unit for the same frames | The ring starts (and restarts after a buffer clear) with that much silence, so the unit hands out as many samples as time has passed |
| `NES:MD`: "an octave up" | **Not an octave error.** The spectrum has its fundamental at 440.4 Hz with a second harmonic **1.41 times the fundamental's size** (the default FM voice is bright); A4's `Tone()` counted rising zero crossings with hysteresis, which cross twice a cycle for such a waveform: 878.4. The sound is the right pitch | `SwapSuite.Tone` reads the pitch from the spectrum (the lowest strong component, `SwapMatrixTestCli.Fundamental`); the tolerance stays 2 percent, the named `NES:QLQ` exception stays, and a unit that really plays an octave away still fails (shown below) |

Checks added: in `--swap-matrix-test` (21 to **35**), for the Master System, Game Gear and Genesis, a **sound** check of every APU entry (`SwapMatrixSound.cs`): in a game that plays one steady PSG tone, swap the entry in (or, where the swap restarts the game, start a fresh session on it, as the app does) and require the console's sample rate, the same number of samples as the console's own unit (within 8) and the same pitch (spectral, within 2 percent) and audible; and a **converter** group (`--only resampler`): the sample count and pitch of a 1 kHz tone through 32 -> 44.1, 48 -> 44.1 and 44.1 -> 48 kHz, a 14 kHz tone's level and its 18 kHz image, a 23 kHz tone not folding back, the same output however the input is cut into blocks, no latency in samples, no allocation. **Shown able to fail** (an injection, the built exe run, the injection reverted): conversion switched off (matrix: the three consoles fail with `NES:SNES: 48062 samples`; A4's swap suite: 6 fail), every NES chip an octave up (matrix: 3 consoles; A4's suite: 46 fail), the converter's ratio 1 percent off (matrix: 9 fail; A4's suite: 6 fail). Results on the fixed tree: **A4's `--smstest --only swap`: 119 pass, 0 fail, 3 skip** (the three skips need the roster; with it, 169 pass in the prove run); `--swap-matrix-test` 35 / 0; `--sega-bridge-test` 356 / 0; the Genesis menu entries `NES:SNES` / `NES:DMG` / `NES:DMGS` now 66,235 samples at 439.9 / 439.8 Hz.

Two UAT goldens changed, audio only (the frame hashes are the same): `sega-sms-sonic-on-snes-chip` (audio `A96AF40A37E646A7` -> `F4FB2FF73C0C9BF1`) and `sega-gg-sonic-on-gb-chip` (`977780EEEA8F621F` -> `36FA54F474960885`), re-recorded with `run-suite.ps1 -UpdateGolden` (each run twice); the other 44 Sega cases pass on their goldens.

**The gate's `smstest-prove` step.** `--smstest --prove` prints one line per suite and ends in "prove: ..." with no pass / fail counts and exits 1 when any suite is not proven, so the gate said "no result line". The gate now reads the lines (`CAN FAIL` / `NOT PROVEN` / `NOT PROVEN (the suite could not run here)`), runs from the repo folder (the golden hashes are found by walking up), passes the roster folder to `smstest` and the prove run (`-Roms`, `BROKENNES_SMS_ROMS`, or `%LOCALAPPDATA%\VRUN_Nes_Dev_work\sega_roster`, which the new `UAT/lib/Prepare-SegaRoster.ps1` fills from the libraries: 40 of 40 roster games), and treats a proof that could not run here as a skip, a failure only under `-Strict`. A suite that fails its clean proof run once on a busy machine (the `fm` suite did, 38 pass 1 fail, and passed alone 39 / 0; I did not look at which check, the suite has speed checks and the lead's gate saw its speed check miss under load) is run once more before it counts. Shown on three runs: with the roster, 20 of 20 suites proven (360 s); with an empty roster the step is PASS with "2 proof(s) could not run here (rosterstate, golden...)" and under `-Strict` FAIL with the same note.

**The whole gate on the merged tree** (main at 20c31fb, B4a included, plus this branch; `-Prove`; 997 s on a shared machine): **19 of 20 steps pass.** `smstest` 596 / 0 (2 skipped inside: missing input), `smstest-prove` 20 of 20 suites proven, `--picture-bridge-test` **237 / 237**, `--swap-matrix-test` 35 / 0, `--sega-bridge-test` 356 / 0, `--md-test` 153, `--md-pic-test` 137. The one failure is **`--md-golden` 76 of 77 frames**: Titan Overdrive 1 (PAL) frame 1007, B4a's documented open item (the 68000 takes interrupts later on the oracle after long instructions); it is a picture hash of the stock Genesis path, which none of this touches.

**B4a's scene sets through the bridge on the merged tree** (`--md-bridge-scenes`, `NES:FIXS`, `--noise 0.035` on the random sets, 28 sets built from `md_scene_sets.tsv` with `scenerun.py` / `vsprobe.py`): **600 scenes: 529 exact, 71 at noise level, 0 with a counted loss, 0 differing with no loss counted** - the same split B4a measured. The sets with the most noise-level scenes: `sc_il2` 7 of 24 (interlace 2), `sc_plain2` 7 of 30, `sc_vsc` 5 of 20, `f_window` 5 of 20, `f_all` 5 of 40, `sc_shnw` 4 of 16, `sc_h32` 4 of 16, `f_il2b` 4 of 30, `f_sprites` 4 of 30; the probe sets (31 scenes) and `f_v30`, `f_satcache`, `sc_v30` are exact throughout.

## 15. A project keeps the whole machine of a ROM-mode game (the lead's third work list, 2026-10-16)

**The bug (found by the user testing the hand-off build):** with a game loaded in ROM mode and Instrument Runaway on, saving the FL project and reopening it brought the instrument back (the `SPC1` / `GBS1` / `PSG1` / `OPL1` / `OPN1` sections) but not the game: only the ROM path was stored, so the game was at power-on and switching Runaway off restarted it from reset. The user's requirement: *"ensure that the state is correctly saved, as later we will modify the game state with corruptions and do instrument runaway on it, and it is important to retain those changes for the instrument to persist through a save."* So a project must keep the **whole machine, with every change made to it**, and Runaway off after a reopen must go on from there.

### 15.1 What was built

A new append-only project section `GAM1` (`Plugin/BrokenNes2.Plugin/GameState.cs`, written after the sound section): the game's id (the ROM's SHA-1), the console, the frame count, whether Runaway was on, the machine as the game's session writes it (deflated when that is smaller, CRC-32 checked) and, for a frozen game, its picture (deflated RGBA, CRC checked, so the picture is there on reopening although the frozen game never draws again). `IGame` got `GameId`, `StateSupported`, `StateUnsupportedReason`, `SaveGameState`, `LoadGameState`, `WorkRamSize`, `PeekWorkRam`, `PokeWorkRam`; `SessionGame` forwards to `IConsoleSession.SupportsState` / `SaveState` / `LoadState` and **never looks at a console**, so a session that starts supporting state (the SNES, from the `snes-state` branch) is saved from the day it merges with no change here. The NES has its own state: `NES.SaveState()` JSON (written by the NES core's reflection serializer, which works inside the Native AOT DLL: checked, below) plus the APU register latch, which is not in that state (`Bus.SetApuLatch`, additive), so a per-channel stem made after a reopen starts from the registers the game had set.

- **Saving** (`Emulator.State.cs` `CaptureGameSection`, called from `Bn2Plugin.SaveRestoreState`; every instance writes the section, as they do the Runaway snapshots, so a project with five instances carries it five times): the machine is taken under the emulator's lock between frames, the compression is done after the lock is released. A game that is still being built, or waits to be taken by the audio side, is not the machine the project describes: the section the project came with is written back as it came (`carriedSection`), so saving at the wrong moment loses nothing. A game with no state, a state that cannot be written, and a section over 8 MB are written as a short marker section that says why, and the user is told at once.
- **Restoring** (`Emulator.Adopt` -> `StartLoad(RestoreRequest)` -> `ApplyRestore`, only for the first instance of a project to open, as before): done **on the loading thread, on the game it just built, before the audio side can see it** (so the game is never running in a state that is not the project's, nothing is allocated on the audio thread, and the emulator's lock is not held for a state load). A project saved in Runaway starts frozen before the thread starts. The identity (console + game id), the section's lengths, sizes and checksums are all checked; any failure is a **visible note** (a banner over the bottom of the screen, `Emulator.StateNote`, editor test hook 90) and the game starts from power-on, as a fresh start: another game at the saved path ("belongs to a different game than the file now at the saved path"), a damaged / cut-short / newer / oversize section, a state the game refuses. An older project, or a section nobody here knows, loads as before. A game with no state says "SNES games cannot keep their state in a project yet: the game restarts when Instrument Runaway is switched off" (frozen at power-on after a reopen; "...the game started from power-on" once it runs), on saving and on opening, and for an older project as well.
- **The corruption hook for the next phase:** `Emulator.PokeWorkRam(offset, value)` / `PeekWorkRam` (`IGame.PokeWorkRam`) write the main work RAM between frames, running or frozen: NES the 2 KB of system RAM, Master System and Game Gear the 8 KB at `$C000`, Genesis the 64 KB at `$FF0000`, Game Boy and Color the 8 KB at `$C000-$DFFF` (`GbSession.PokeWorkRam`, additive). Anything changed in the machine before the save is in the saved bytes; Runaway on, save, reopen, Runaway off continues from the corrupted machine (`runaway-state` does exactly that with a counter the game keeps in RAM). Memory outside work RAM (VRAM, palette, sound chips) is reached through the session's board the same way, under `romLock`; the blob is `GameStateSection.State` (a `ConsoleStateFile` for the sessions, `NES1` + latch + JSON for the NES).

### 15.2 The check, and what it showed

`runaway-state` (selftest, Native AOT DLL): tiny ROMs with a counter in work RAM that also paints the picture stand in for games (NES, Game Boy, Game Boy Color, Master System PSG and FM chip, Game Gear, Genesis; the NES also with the built-in VRUN). For each: a control (N frames, a corrupted counter and a corrupted spare byte, M more frames) is run twice and must be the same; then the game is run N frames, corrupted, saved frozen (Runaway on) and running, and a new instance opens the project. It must be at frame N, its **machine byte for byte the saved one**, the corrupted bytes back, the frozen picture back, a frozen game must stay frozen over 30 forced frames and 0.2 s of audio pulls, saving the reopened project again must give the same bytes (not the NES: below), and after Runaway off and M frames the machine, the picture, the memory and the frame count must equal the control's. Then: an older project (the sections cut off), an unknown section before and after ours, a damaged section, one cut in half, one that claims 1 GB, one of absurd length, another game at the saved path, an oversize state (cap lowered to 2,000 bytes), two instances of one project (the second to open must not put its state back over the first's: it was saved 20 frames later), a project saved again while its game is still being built, and a SNES game (asserts the visible message, with the editor open; it runs the full scenarios instead once `SupportsState` is true). `BROKENNES_TEST_GAMES` runs the family scenarios on real games with 10 s before the project and 5 s after.

**Before the fix (commit `260cbcc`, same test, the plugin as it was):** FAIL on every restore check: "the project has no game-state section", "the game is at frame 0, it was saved at frame 90 (a restart from power-on is the bug)", "the corrupted byte came back as 0, saved 165", "the frozen picture is not the one that was saved", "the machine that came back is not the machine that was saved", for all eight families, frozen and running; the control was deterministic everywhere. **After the fix:** `runaway-state` passes on the Native AOT DLL.

Things the check found that the code did not say:
- The NES core drops its queued audio on a state load, so a restored NES machine matches the one that never left in everything except the APU's audio bookkeeping (ring pointers, the resampling phase, the output filters' memory): measured by diffing the two states field by field, with a scratch harness, after 90 and after 210 frames (VRUN and the counter ROM). `NesGame.SaveComparableState` zeroes exactly those fields for the comparison, and the NES cases do not require the same bytes on a second save. The picture, the RAM and the frame count are exactly equal.
- Reading a Genesis sound unit's voices for the Runaway snapshot (`Ym2612.DescribeVoices`) resets `dacSwing`, a bookkeeping value the state carries and nobody hears: on Titan Overdrive, switching Runaway on and off on a live game changes the state hash. Compared with a scratch harness against an untouched session over 300 more frames (an FNV hash of every picture and of every sample): picture and sound identical. The frozen cases are therefore compared with a control that did the same switch on and off, and the check says the picture, the memory and the frame count do not change with it.
- The existing `runaway-sega` hostile-snapshot checks judged "dropped" by Runaway being off. With the machine in the project, a project saved in Runaway comes back frozen whatever happens to the instrument snapshot, so they now judge the snapshot (kind 0), and one new line says that a damaged snapshot leaves the game frozen at its saved state, without the instrument.

### 15.3 Shown able to fail

Five deliberate bugs put into the fixed plugin at once (a separate build, run, then reverted with `git checkout`; the tree was clean afterwards): the session restore writes zeros over the corrupted bytes; the game-id check removed; a project saved in Runaway ignores the frozen flag; an unknown section ends the reader before the game-state section; the frozen picture not put back. `runaway-state` failed, and said what: "the corrupted byte came back as N, saved N" and "the second corrupted byte came back as N" (12 times each, one per family and scenario), "the machine that came back is not the machine that was saved" (12), "after the same frames the machine differs from the one that never left" (14), "the frozen picture is not the one that was saved" (8), "the state did not come back" (2: the two unknown-section cases, which compare the machine, so the first injection alone breaks them), "after reopening Runaway is 0, saved 1" (the NES, which has no sound snapshot to bring Runaway back by; the other consoles come back frozen through their instrument snapshot, so this injection only shows on the NES). The unknown-section injection was then run **alone**: "an unknown section before: the state did not come back (restore 0)", and nothing else failed.

The removed game-id check was then run **alone**, after a NES case was added to the foreign-game check: the Master System session's own id check still refused the foreign state (with the session's wording, not the expected one, so the check failed on the text), and the NES, where the plugin's check is the only one, **took the foreign game's state without a word**: "another NES game: restore 1, note '', frame 90". Reverted; with the check back both pass. Before the fix the same test failed everywhere (15.2). The SNES message path failed before the fix as well ("saving a game that has no state said ''", "the editor does not show the note").
### 15.4 Numbers

Whole plugin suite on the final tree, Native AOT DLL (`Plugin\build-dist.ps1`: 9.1 MB, sha256 `5E87A859...78A8A`): **48 checks: 47 pass, 1 warning (the QLQ core, on purpose), 0 failed** (`runaway-state` is the new one: 17 s alone with the tiny ROMs, 90 s with Titan Overdrive, the Master System test suite and VRUN as extra games); `certify.ps1 -Only plugin-host,plugin-install,plugin-synth`: plugin-host PASS (47 passed, 1 warning, 0 failed; 150 s), plugin-install PASS 14/14, plugin-synth PASS 14/14. The realtime check still measures 0 bytes allocated while playing (nothing here runs on the audio thread but `carriedSection = null` when a game is taken).

**What the section adds to a project, per instance** (every instance of a project writes its own copy; "frozen" includes the frozen picture):

| Game | Saved running | Saved frozen (Runaway on) |
|---|---|---|
| NES, VRUN (mapper 30, 760 KB of state before compression) | 15,852 B | 24,092 B |
| NES, counter ROM | 2,150 B | 9,086 B |
| Game Boy (DMG) | 713 B | 4,557 B |
| Game Boy Color | 510 B | 1,408 B |
| Master System, PSG | 3,667 B | 6,938 B |
| Master System, FM chip | 3,925 B | 7,196 B |
| Game Gear | 911 B | 1,857 B |
| Genesis, FM tones | 7,057 B | 9,532 B |
| Genesis, Titan Overdrive (600 frames in) | 68,745 B | 97,444 B |
| Master System test suite (600 frames in) | 14,595 B | 24,392 B |
| VRUN as a file (600 frames in) | 15,833 B | 24,014 B |

The project without the section was 80 to 436 bytes for these (the frozen ones carry the Runaway snapshots: 66,004 B for Titan Overdrive). A save takes 0.3 ms (NES counter ROM) to 11 ms (Master System) to 26 ms (the tiny Genesis ROM) and 27 to 53 ms (Titan Overdrive), measured around `SaveState()` from the host, of which the machine's own write is the only part under the emulator's lock.

### 15.5 Files touched, not tested, open

Shared tree, additive only: `Core/NesAudioMachine.cs` (`RestoreState`), `Windows/NesEmulator/board/Bus.cs` and `NES.cs` (`SetApuLatch`), `Windows/NesEmulator/systems/ConsoleSessions.cs` (`GbSession.WorkRamSize`, `PeekWorkRam`, `PokeWorkRam`). Plugin: `GameState.cs` and `Emulator.State.cs` (new), `Game.cs`, `Emulator.cs`, `Emulator.Rom.cs`, `Bn2Plugin.cs` (the section reader / writer, hooks 81-89), `Bn2Editor.cs` (the banner, hook 90). Tests: `SelfTest.RunawayState.cs` (new), `SelfTest.cs` / `SelfTest.Sega.cs` (counter ROMs, `MakeGbRom(counter, color)`, `MakeMdRom(counter)`), `SelfTest.RunawaySega.cs` (the hostile-snapshot checks, 15.2).

- **Not tested:** FL itself (the plugin is not re-certified in FL for this change: `plugin-fl` needs FL closed and was not run); a real Game Boy Color game, a real Game Gear game and a real SNES game (the Game Boy and Game Gear cases are tiny ROMs); the SNES with state (the scenario is written and guarded by `SupportsState`, it cannot run until the `snes-state` branch merges, and its byte-for-byte "same bytes on a second save" line may need the same allowance the NES got); a project of many instances of a big game (the size multiplies by the instance count); saving during playback with a heavy game (the lock is held for the machine's write, 1 to 27 ms; no audio underrun was measured because the host test renders no real audio device).
- **Open:** (1) a damaged instrument snapshot with an intact machine leaves Runaway on without the instrument (the project could rebuild it from the restored machine, minus a Genesis game's DAC sounds, which are only in the snapshot); (2) the NES audio bookkeeping is not restored, so the first few samples after a reopen are not bit-exact with a run that never left (a resampling phase and filter memory, inaudible); (3) every instance writes the whole section; a leaner project would write it once and have the others point to it, which needs a rule for which instance opens first.