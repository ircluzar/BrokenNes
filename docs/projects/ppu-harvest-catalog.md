# PPU harvest catalog (research draft 2, 2026-11-07)

This is the picture-side twin of `apu-harvest-catalog.md`: prospects for janky "character" PPU, VDP and video cores. A core gets its character in one of three ways:
- **(a) Pre-milestone builds:** old desktop renderers taken just before a picture-accuracy breakthrough.
- **(b) Console ports:** emulators that run on another console, use the host's tile and sprite hardware, and drop raster effects.
- **(c) Embedded ports:** ESP32, RP2040 and STM32 builds with line buffers or LUTs.

All research was web-only. Nothing was downloaded or cloned, and files were viewed at their tag or commit in a browser.

**How sure each entry is** (same markers as the APU catalog):
- **[verified]**: the target and the file at that target were seen, and a link is given.
- **[partial]**: some of it is sourced, and the gap is named.
- **[unverified]**: a lead only.

**Licences.** These cores are art instruments, referenced like the APU climb, so nothing is excluded for licence reasons. Each entry gives the licence seen at the target, and **??** marks an unknown one. A picture-side twin of the FA-1 exception (`docs/KNOWN_GAPS.md`) would cover GPL and non-commercial entries the same way FA-1 covers sound.

**Already in the repo, so not re-listed.** Every current PPU core is in-house:
- NES: FIX/FIXS, FMC, BFR, EIL, LOW, SPD and IMG all descend from the original "FMC" renderer (file headers: "Renamed original concrete PPU implementation to PPU_FMC"). The gimmick cores are LQ, ULQ, CUBE, CUBEX and EXE.
- Other consoles: SNES/SNESS, SFC/SFCS, WMS/WMSS, DMG/DMGS/DMGX, GB/GBE/GBX/GBXS, SMS/SMSS and MD/MDS.

No foreign picture core has been harvested. Mixing rule (`feedback_no_silent_fallbacks.md`): adapt each one to IPPU, and never fall back to FIX on a game it cannot handle.

## Ranked top 10 (interest x ease)

| # | Prospect | Chip | Exact target | Licence at target | Status |
|---|---|---|---|---|---|
| 1 | **FCEUX, the last ppu.cpp before newppu**: old FCEU whole-line renderer only | NES PPU | [`2b2d70b`](https://github.com/TASEmulators/fceux/blob/2b2d70b/src/ppu.cpp) (2008-08-08), `src/ppu.cpp`, **1473 lines**. newppu work starts in the next commits, `2cba0fd`/[`7d7f5ad`](https://github.com/TASEmulators/fceux/commits/master/src/ppu.cpp?until=2008-08-18) (2008-08-18) | GPL-2+ (header: BERO 1998, Xodnizel 2003) | [verified] |
| 2 | **SameBoy v0.1 `display.c`**: the very first SameBoy; per-pixel with self-confessed gaps | GB/GBC | tag [v0.1](https://github.com/LIJI32/SameBoy/blob/v0.1/Core/display.c) (2016-04-01), `Core/display.c`, **391 lines**, C | MIT (Lior Halphon 2015-2016, [LICENSE](https://github.com/LIJI32/SameBoy/blob/v0.1/LICENSE)) | [verified] |
| 3 | **InfoNES `InfoNES_DrawLine`**: line renderer; sprite 0 is hit by splitting CPU time at the sprite's X | NES PPU | [jay-kumogata/InfoNES `src/InfoNES.cpp`](https://github.com/jay-kumogata/InfoNES/blob/master/src/InfoNES.cpp), **1191 lines**, at master `fe3295c` (2026-08-01; the code itself is old, 1999-2002 dev notes) | **Apache-2.0** ([LICENSE](https://github.com/jay-kumogata/InfoNES/blob/master/LICENSE)) | [verified] (no earlier pin) |
| 4 | **nofrendo (retro-go)**: whole-line render, sprite-0 cycle computed afterwards | NES PPU | retro-go [`05e97e8`](https://github.com/ducalex/retro-go/blob/05e97e8/retro-core/components/nofrendo/nes/ppu.c) (2022-11-18), `retro-core/components/nofrendo/nes/ppu.c`, **866 lines** | LGPL-2 (Library GPL v2) | [verified] |
| 5 | **PicoDrive "fast renderer"** `draw2.c`: whole frame in 8x8 tiles, no per-line hscroll, 224 lines only | MD VDP | tag [v1.85](https://github.com/notaz/picodrive/blob/v1.85/pico/draw2.c) (2013-08-31), `pico/draw2.c`, **624 lines** C (an ARM asm twin exists) | MAME-style non-commercial ([COPYING](https://github.com/notaz/picodrive/blob/master/COPYING)) | [verified] |
| 6 | **Snes9x tile.cpp, before anomie's hires fix**: active macro shifts hires output one pixel left, with a blank right column | SNES PPU | libretro/snes9x [`88f3702`](https://github.com/libretro/snes9x/commit/88f3702a6a40e34ba7f8166d5d61fb76a0e3db4f) (parent of [`795542d`](https://github.com/libretro/snes9x/commit/795542d), 2013-01-12), `tile.cpp` | Snes9x licence (non-commercial) | [partial]: line count not taken |
| 7 | **Snes9xTYL PSP GU renderer**: SNES layers drawn as PSP GU sprites, priority through depth, OBJ through stencil; offset mode and Mode 7 are unsupported ([README](https://github.com/173210/snes9xTYL)) | SNES PPU | [173210/snes9xTYL `mecm` `gfx_psp.cpp`](https://github.com/173210/snes9xTYL/blob/mecm/gfx_psp.cpp), **2610 lines**. Reimplement the shortcuts; don't port the GU calls | Snes9x licence (non-commercial, file header) | [verified] (branch head, no tag) |
| 8 | **Peanut-GB, before the 2025 LCD fixes** | GB | [`8a692e4`](https://github.com/deltabeard/Peanut-GB/blob/8a692e468df49d2858d5812108f4e211cfe03fa2/peanut_gb.h) (parent of [`1a74227`](https://github.com/deltabeard/Peanut-GB/commit/1a74227) "go to OAM Search after VBLANK"), `peanut_gb.h`, **3916 lines** single header | MIT (Koshkouei 2018-2023; parts from SameBoy, MIT) | [verified] (date not shown) |
| 9 | **Genesis Plus GX, before the sprite pixel budget**: count-based sprite limit; Overdrive and Mickey Mania effects are wrong | MD VDP + SMS Mode 4 | [`cbf8f4e`](https://github.com/ekeeke/Genesis-Plus-GX/blob/cbf8f4ed2a8884f9cc9d3febce55b7b7c605c108/core/vdp_render.c) (parent of [`42ea79c`](https://github.com/ekeeke/Genesis-Plus-GX/commit/42ea79c), 2013-10-20), `core/vdp_render.c`, **4150 lines** | GPGX non-commercial (header) | [verified] |
| 10 | **gnuboy 1.0.3 `lcd.c`**: line-based; window bugs fixed only in 2021 | GB/GBC | rofl0r/gnuboy [`29076d4`](https://github.com/rofl0r/gnuboy/commits/master/lcd.c) (2012-06-23, "initial 1.0.3 import"); fix: `758addc` (2021-09-10) | GPL-2 | [partial]: line count not taken |

**Why this order.** #1-#4 are plain C or C++ NES and GB renderers with documented line-level shortcuts, so they are cheap and rich in jank. #5 and #7 are the most visually distinctive (tile-at-once and host-GPU), but each is a reimplementation. #3 is the most permissive NES entry (Apache-2.0).

**Runners-up (pinned):**
- **Mesen 0.1.0 `Core/PPU.cpp`.** [tag](https://github.com/SourMesen/Mesen/blob/0.1.0/Core/PPU.cpp), 959 lines; cycle-based but early, with "approximation" comments on sprite tile loads and DMA. Licence: GPL-3 at repo level; no header in the file. [verified]
- **SMS Plus GX (retro-go 1.20) `render.c`.** [link](https://github.com/ducalex/retro-go/blob/1.20/smsplusgx-go/components/smsplus/render.c), 844 lines; GPL-2+; ESP32 IRAM/LUT build. [verified]
- **PicoDrive, before `ac89144`.** Sprites are parsed on the same line instead of the previous one; parent [`e5a1d4c`](https://github.com/irixxxx/picodrive/commit/ac89144) (2023-03-26). MAME licence. [verified]
- **VBA-M `gbGfx.cpp`.** [link](https://github.com/visualboyadvance-m/visualboyadvance-m/blob/master/src/core/gb/gbGfx.cpp), 650 lines, per-line, full of per-game fixes. GPL-2. It needs an old VBA 1.7.2 pin. [partial]

### Changes from draft 1
- FCEUX is now pinned to a real pre-newppu commit. The 2.1.1 release (2009-07-29, [SourceForge](https://sourceforge.net/projects/fceultra/files/Source%20Code/)) already has newppu in-tree ([`7125945`](https://github.com/TASEmulators/fceux/blob/7125945/src/ppu.cpp), 2408 lines), so `2b2d70b` is the clean target.
- **New:** SameBoy v0.1, InfoNES (Apache-2.0), PicoDrive draw2 and Snes9xTYL.
- **Pinned:** Peanut-GB and GPGX now have exact parent commits.
- **Removed from the top 10:**
  - nesDS: asm, unread.
  - PocketNES: asm, no licence shown.
  - bsnes v064: tarball only.
  - ZSNES: see section 2.
- **Snes9x `8fbb7ca`** was opened (dated 2026-08-03 in the listing). It replaces the C++ template tile renderer with snes9x2010's `tile.c` (+37,325/-1,509 lines over 11 files; the author reports byte-identical output). It is not a visual milestone, but it marks the pre-2026 `tile.cpp` as a clean C++ unit. [verified]

## 1. NES PPU
- **FCEUX old PPU.** Target `2b2d70b` above. Known old-PPU jank: sprite-0 hit on pixel 255, which broke the Battletoads snake pit ([TASVideos](https://tasvideos.org/Forum/Posts/220073)), plus whole-line `RefreshLine`. The newppu timeline runs from [2008-08-18 to 2009-10](https://github.com/TASEmulators/fceux/commits/master/src/ppu.cpp?until=2009-11-02); it was made official in [2.1.2](https://sourceforge.net/p/fceultra/news) (2009-11-03). [verified]
- **InfoNES.** Top 10 #3. The sprite overflow flag is reset and sprites are counted, but whether the flag is ever set past eight was not confirmed. Pico ports: [pico-infones](https://github.com/shuichitakano/pico-infones) (GPL-3, HDMI through PicoDVI) and [pico-infonesPlus](https://github.com/opensensor/pico-infonesPlus). These are the picoNES lineage, so they add embedded variants of the same renderer. [verified]/[partial]
- **nofrendo.** Top 10 #4. Its own TODO says OAM should be fetched one line early. [verified]
- **Mesen 0.1.0.** See runners-up. Tags 0.1.0-0.1.4 are all dated 2016-06-22 (the import day; [tags](https://github.com/SourMesen/Mesen/tags?after=0.2.0)). [verified]
- **Nintendulator.** [quietust/nintendulator `src/PPU.cpp`](https://github.com/quietust/nintendulator/blob/master/src/PPU.cpp), 1373 lines; header "Copyright (C) QMT Productions", **licence ??**. It was dot-based throughout, so its milestones are small: 0.980 vertical-scroll reset, r883 sprite-0 delay ([t2e](https://www.t2e.pl/News/wiadomosci-oficjalne-releasy.322/nes-nintendulator-v0.980-stable-01012019.19495)). Low jank value. [partial]
- **VirtuaNES 0.97.** Source mirrors exist ([ivysnow/VirtuaNES](https://github.com/ivysnow/VirtuaNES)), described as "freeware", **licence ??**. The PPU file is inside `NES/` and was not opened. [partial]
- **nesDS `RP2C02`.** [v2.0.1](https://github.com/DS-Homebrew/NesDS/tree/v2.0.1/arm9/source/RP2C02): `RP2C02.s` and `render.s` are ARM asm. Public domain. Reimplement it. [partial]
- **PocketNES 9.98.** GBA tiles and sprites, with per-line HDMA scroll and no mid-line effects ([nesdev](https://nesdev.nes.science/f3/t804.xhtml)). Mirror [catskull/pocketnes](https://github.com/catskull/pocketnes), **licence ??**, asm. [partial]
- **No public source found:**
  - loopyNES: only the "loopy" scroll document is public.
  - RockNES and nesterJ: searches returned nothing.
  - iNES: closed source.
  - NesterDC: ported from TAKEDA's unofficial nester ([release notes](https://nesterdc.pqrs.org/docs/releasenotes/)); licence ??.
  [unverified]

## 2. SNES PPU
- **Snes9x tile.cpp.** Top 10 #6. [partial]
- **Snes9xTYL.** Top 10 #7. It has 5 render modes, including the "original Snes9x" and GU paths, plus `gfx_psp.cpp.old.cpp`. [verified]
- **SNEmulDS.** [Revival mirror](https://github.com/cryptolopes/SnemulDS), GPL-2. It maps SNES BGs to DS layers and uses an extra layer for per-tile priority ([GameBrew](https://www.gamebrew.org/wiki/SNEmulDS)). The renderer file is under `arm9/source` and was not opened. [partial]
- **ZSNES 1.51.** The original is x86 asm (`zsnes151src.tar.bz2`, tarball only). [xyproto/zsnes](https://github.com/xyproto/zsnes) is a **C conversion** of the 1.51-era code (GPL-2): `video/` holds `c_newgfx16.c`, `c_ngline.c`, `c_ngmosaic.c`, `c_mode716*.c` and `tilecache.c`, and `newgfx.c` is commented as a "C port of video/newgfx.asm". That makes a port realistic, but it is a fork, not the 1.51 tag. [partial]
- **bsnes v063/v064.** Scanline PPU vs the v064 cycle PPU ([announcement](https://game.faqs.tw/bsnes-v064-released-1271274871)). The source exists only as tarballs ([Alcaro/bsnes-gc](https://github.com/Alcaro/bsnes-gc): `bsnes_v063.tar.bz2` and `bsnes_v064.tar.bz2`). The [bsnes-history](https://github.com/Xeon3D/bsnes-history) tags could not be browsed (404). [partial]
- **Snes9x GX.** Plain Snes9x 1.62.3 core with GX only blitting and filtering ([dborth/snes9xgx](https://github.com/dborth/snes9xgx)). No distinct jank, so it was dropped. [verified]
- **SNES Station, DreamSNES.** No source located. [unverified]

## 3. Game Boy / GBC
- **SameBoy v0.1.** Top 10 #2. Its own TODOs: "Figure out how the Gameboy handles in-line changes to SCX", "verify this window behavior", and wrong LY=151/153 handling. The [v0.10 notes](https://github.com/LIJI32/SameBoy/releases/tag/v0.10) list the PPU timing, SCX, window and SCY-latch fixes that came later. [verified]
- **Gambatte.** 0.4.0 (2008-10-28) brought cycle-accurate sprite mapping and WX sprite timing ([summary](https://www.ngemu.com/threads/gambatte-0-4-0-released.113393/); the page itself was paywalled), so **0.3.1 (2007-10-26)** is the target ([SourceForge](https://sourceforge.net/projects/gambatte/files/gambatte/)). It is a tarball only, and the git history starts in 2009. GPL-2. [partial]
- **gnuboy 1.0.3.** Top 10 #10. The retro-go copy [`05e97e8`](https://github.com/ducalex/retro-go/commits/master/retro-core/components/gnuboy/lcd.c) predates its 2023 "scanlines 0 to 153" fix `1473ae3`. [verified]
- **Peanut-GB.** Top 10 #8. Variant: [pico-peanutGB](https://github.com/PicoPlus-devel/pico-peanutGB/releases) (no framebuffer on RP2040; hands each line straight to the display). [partial]
- **VBA-M `gbGfx.cpp`.** See runners-up. [partial]

## 4. SMS/GG and Genesis VDP
- **GPGX.** Top 10 #9. Later milestones: `8c91bdc` (2014), `d8ebe96` (2016) and `3e48f01` (2025, SAT change during HBLANK) ([history](https://github.com/ekeeke/Genesis-Plus-GX/commits/master/core/vdp_render.c)). [verified]
- **PicoDrive.** draw2 fast renderer (#5). The accurate `draw.c` milestones are `ac89144` (2023-03 sprite parse on the previous line), `97232a4` (partial-line blanking) and `557aa28` (2024 window priority) ([history](https://github.com/irixxxx/picodrive/commits/master/pico/draw.c)). The PSP and GP2X builds also expose 8-bit and "alternative" renderers ([readme](https://git.godzil.net/funkey/picodrive-irixxxx/src/branch/master/platform/base_readme.txt)). [verified]
- **SMS Plus.** 0.9.4a ([psms mirror](https://github.com/Cpasjuste/psms), GPL-2, `render.c` + `vdp.c`). The GX/retro-go version is in the runners-up. [partial]
- **DGen.** `vdp.cpp` ("Megadrive VDP C++ module from DGen v1.13+"; [mirror](https://github.com/smarkusg/dgen/blob/main/vdp.cpp)); the kolibrios copy returned 403. **Licence ??** [partial]
- **Gens.** [gens-rerecording](https://github.com/TASEmulators/gens-rerecording): the VDP renderer is x86 asm and was not located in `Gens-rr/src`. The repo shows no licence; upstream Gens is GPL. [unverified]
- **SegaVDP (SMSAdvance lineage, FluBBa).** [repo](https://github.com/FluBBaOfWard/SegaVDP): ARM asm (`SegaVDP.s`, `RendererGBA.s`). Sprite overflow came only in V0.4.0 (2014-04-12, [History.txt](https://github.com/FluBBaOfWard/SegaVDP/blob/master/History.txt)), so pre-0.4 is the jank target. Pre-0.4 commits were not found on GitHub. **Licence ??** [partial]
- **jEnesisDS.** C and ARM rewrite of jEnesis; no source found ([Racketboy](https://racketboy.com/retro/best-genesis-emulation-on-the-nintendo-ds-jenesisds)). [unverified]

## 5. DOS VGA/EGA/CGA
- **DOSBox 0.74 `src/hardware/vga_draw.cpp`.** Per-mode line functions (`VGA_Draw_1BPP_Line` and others). The xref page was blocked by bot protection, so there is no line count. DOSBox-X's later `VGA_ProcessScanline` marks the milestone ([doxygen](https://dosbox-x.com/doxygen/html/vga__draw_8cpp_source.html)). GPL-2+. [partial]
- **PCem.** Old CGA vs "New CGA" (composite port of reenigne's code, plus the fix for the first line being drawn twice; [forum patch](https://pcem-emulator.co.uk/phpBB3/viewtopic.php?p=4888)). No revision pinned. GPL-2. [partial]

## 6. On other consoles (summary)
- **Covered above:** PocketNES (GBA), nesDS (DS), Snes9xTYL (PSP), SNEmulDS (DS), PicoDrive (GP2X/PSP), SegaVDP/SMSAdvance (GBA) and NesterDC (Dreamcast).
- **Still unsourced:** SNES Station (PS2), DreamSNES (Dreamcast), jEnesisDS.

## 7. Embedded (summary)
- **retro-go:** nofrendo (#4), gnuboy (lcd.c), smsplus (1.20) and pce-go (`retro-core/components`).
- **Game & Watch fork:** [kbeckmann/game-and-watch-retro-go](https://github.com/kbeckmann/game-and-watch-retro-go), GPLv2, archived 2024-03-24; it reuses the same cores.
- **Pico:** pico-infones, pico-infonesPlus (picoNES) and pico-peanutGB.

## Open gaps
- **Tarball-only targets:** bsnes v063, ZSNES 1.51 (original asm), Gambatte 0.3.1 and VBA 1.7.2. Browsing them needs an archive viewer; reading them would mean a download, which needs your approval.
- **Line counts missing:** Snes9x `tile.cpp` at `88f3702` and gnuboy `lcd.c`.
- **Licence unknown:** PocketNES, VirtuaNES, Nintendulator, DGen and SegaVDP.
- **Not located:** the Gens asm renderer path, the SNEmulDS renderer file, and the pre-0.4 SegaVDP history.
- **No source found:** loopyNES, RockNES, nesterJ, iNES, SNES Station, DreamSNES and jEnesisDS.

## Climb plan (2026-11-07)

The user approved porting every realistic entry above as janky "character" picture cores (art instruments, like the APU climb). Rules:
- Source fetched at the pinned build into `C:\BrokenNes-tools\ppu-harvest\<name>\` (untrusted data, sha256 manifest `MANIFEST.sha256`; nothing there is ever run).
- Faithful ports: the flaws are kept and listed in each file's header. Licences are referenced in `THIRD_PARTY_NOTICES.md`, never a reason to exclude. Entries without source are skipped.
- NES picture cores become NES PPU cores (`PPU_<ID>`, found by `CoreRegistry`). GB, SNES, Genesis and SMS cores become picture cores on their own console, and also NES bridges where the existing bridge pattern allows. No silent substitution: a core that cannot draw something shows it wrong.
- DOS video becomes an x86 video-card/engine choice, reusing the APU climb's x86 menu pattern.
- Each core: port, register, notices, screenshots of real games, save/load state check, golden suite, commit, rebase, push. Full battery at the midpoint and the end.

Order: nofrendo, InfoNES, FCEUX old PPU, SameBoy v0.1, Peanut-GB, gnuboy 1.0.3, PicoDrive draw2, GPGX, Snes9x tile.cpp, then the runners-up and tarball targets.

## Progress

| # | Core | Id | State | Commit |
|---|---|---|---|---|
| 1 | nofrendo ppu.c @05e97e8 | NES `NOFR` | ported (SMB1, Castlevania, Battletoads, Contra, Mega Man 2 draw; state reload SAME) | 07ce5486 |
| 2 | InfoNES @fe3295c | NES `INES` | ported (SMB1, Castlevania, Battletoads, Mega Man 2, Zelda draw; state reload SAME) | abfaa298 |
| 3 | FCEUX old PPU @2b2d70b | NES `FCEU` | ported (same five games; state reload SAME) | abfaa298 |
| 4 | SameBoy v0.1 | GB/GBC `SB01` | ported as a renderer beside the stock chip (timing stays PPU_GB's); Mealybug m3 tests show its 8-pixel chunks; LA, Kirby, SML, Oracle of Seasons draw; state reload SAME | 575d27f1 |
| 5 | Peanut-GB @8a692e4 | GB/GBC `PGB` | ported as a renderer beside the stock chip; LA, Kirby, SML draw; GBC games drawn wrongly as DMG (its jank); state reload SAME | 575d27f1 |
| 6 | gnuboy 1.0.3 @29076d4 | GB/GBC `GNUB` | ported as a renderer beside the stock chip; LA, Kirby, SML, Oracle of Seasons draw; state reload SAME | 4da5ff09 |
| 7 | PicoDrive draw2 v1.85 | Genesis `PD2` | ported (whole frame at frame end from the stock VDP's memories); Sonic, Streets of Rage 2, Gunstar Heroes draw; state reload SAME | 6b067835 |
| 8 | Genesis Plus GX @cbf8f4e | Genesis `GPGX` | ported (Mode 5 path: lines drawn beside the stock VDP via MdVdp.LineObserver); Sonic, Sonic 2, Mickey Mania, Vectorman draw; state reload SAME. Mode 4 / SMS path not ported | GPGX commit |
| 9 | Snes9x tile.cpp @88f3702 | SNES | not started this run: needs Snes9x's whole GFX pipeline around it | |
| 10 | runners-up, tarball targets | | not started | |

Midpoint battery (2026-11-07, commit fc2371d5): suite 123/0, scenarios 1031/0, fuzz 1426 trials with 1 finding: nes undo-mismatch STATE, To The Earth, cpu SPD, ppu INES, minimised to Unsafe.VideoBus alone (repro `bfuzz nes 1 out 311282`). Plain save/reload of that game on INES is SAME; the divergence (CPU timing, openBus) is not yet traced to INES or to the board.

Fuzz finding traced (7b2115f5): the three NES cores read their state with JsonSerializer, which cannot read the number arrays NES.SaveState writes for small byte arrays, so SetState silently restored nothing. Not a board issue; fixed with HarvestPpuState.From.

Final battery (2026-11-07, 2e7a2748, run from the climb worktree): ALL GREEN - scenarios 1052/0, fuzz 916 trials 0 findings (it first caught a GNUB WX<0 overrun, fixed b5eb1a4d); golden suite from the main cases 123 pass / 0 fail / 30 skip.
