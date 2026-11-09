# VGA harvest catalog (research draft 1, 2026-11-09)

Picture-side prospects for the x86 console, in the spirit of `ppu-harvest-catalog.md`: janky, pre-accuracy VGA/EGA/CGA implementations from old emulators, kept as art instruments with their jank intact and their licences referenced. Nothing was downloaded or built.

**How sure each entry is:** [verified] = file seen at the target; [partial] = facts from memory of the project and its docs, target not pinned this session; [unverified] = lead only. Every entry in this draft is [partial] or [unverified]: the one fetch tried (`dosbox-staging` at `v0.74-3`) returned 404, so the pins are still open (see Open gaps).

## Where it plugs in

`Windows/NesEmulator/x86/Vga.cs` (516 lines, ported from tiny386, which ports QEMU's `vga.c`) is a sealed class. `PcMachine` calls it in five places: `In`/`Out` (ports 3B0-3DF), `MemRead`/`MemWrite` (A0000-BFFFF), `Status1`/`FrameIndex` (retrace), `Render()` (once per frame, whole frame, from `Ram` + registers) and `Save`/`Load`.

There are two ways to plug a core in:
- **Render character (cheap).** Keep `Vga`'s registers and planar RAM and swap only `Render()`. Extract an `IVgaRenderer` (`Render(Vga v, int[] frame)`) and let the core menu pick it. This covers renderers whose jank is in how they *draw*: tile caching, line caching, frame skipping and palette timing.
- **Card swap (bigger).** Extract an `IPcVideo` from Vga's public surface (In/Out/MemRead/MemWrite/Status1/FrameIndex/Render/Save/Load + `Frame`/`Width`/`Height`) and give `PcMachine` a second implementation. This is needed for CGA/Tandy cards (other port semantics, B8000 only, no planes) and for anything whose jank is in register or latch behaviour. The BIOS (`.Video`) assumes VGA modes 3/12h/13h, so a CGA card also needs the INT 10h stubs to set CGA modes. A game that needs VGA then fails visibly on a CGA card, which is honest and follows the no-silent-fallbacks rule.

Mid-frame effects (palette changes, split screens, CGA snow) need the renderer to see *when* a write happened. Today `Render()` runs once per frame, so a per-line core also needs a line hook: PcMachine would call `RenderLine(n)` from the PIT-timed frame loop (`FrameIndex` already maps PIT time to a line).

## Prospects

| # | Prospect | Visible jank | Source / licence | Size | Port to C# | Plug |
|---|---|---|---|---|---|---|
| 1 | **Bochs 1.x-2.0 `iodev/vga.cc`**: dirty-tile renderer | The screen updates in 16x16 tiles that are marked dirty by memory writes. A write the tracker misses (latch copies, a register-only change such as a panning or start-address change) leaves stale tiles, so you see a patchwork of old and new frames, smooth scrolling that tears in blocks, and a text cursor that blinks a tile at a time. There is no mid-frame raster at all. | bochs.sourceforge.io SVN/CVS, `iodev/vga.cc` (+ `vga.h`). **LGPL-2.1** (MandrakeSoft header). [partial] | ~3,000 lines (the C++ renderer is ~800 of them) | Easy for the render part: plain C++, the tile loop maps onto `Vga.Ram` (Bochs keeps planes separate, ours are interleaved: one index helper) | Render character + a dirty-tile set fed from `MemWrite` |
| 2 | **DOSBox 0.65-0.72 `src/hardware/vga_draw.cpp`**: early line drawer | It draws whole scanlines from a cached line address, picked up at line start. Palette and DAC writes land on the next line or the next frame (colour cycling tears). The EGA/VGA pel panning and line compare are taken at frame or line granularity (split screens jump by a line). There is no CGA snow, a "partial frame" redraw shows a torn frame, and frameskip ghosts. | DOSBox SVN (SourceForge), tags 0.65 (2006) / 0.72 (2007), `src/hardware/vga_draw.cpp`. **GPL-2+**. [partial] | ~1,000-1,300 lines at those tags (+ `vga_memory.cpp` if the chained/unchained handlers are taken too) | Medium: C++ with function pointers per mode (`VGA_DrawLine` handlers). The handlers port cleanly; the PIC-event timing becomes a line hook | Render character with a line hook |
| 3 | **PCem v8-v10 `src/vid_cga.c`** (+ `vid_tandy.c`): CGA with snow | 80-column text mode shows **CGA snow**: random character/attribute garbage on the lines where the CPU wrote video RAM during the display. Composite artefact colours are approximate (PCem's early NTSC table). The Tandy variant has 16-colour 160x200/320x200 and its own palette registers. | PCem (Sarah Walker), pcem-emulator.co.uk source zips v8 (2013) - v10. **GPL-2**. 86Box later inherited and fixed these files, so the PCem tags are the "ancestor" pins. [partial] | `vid_cga.c` ~500-600 lines; `vid_tandy.c` ~600 | Medium: C, cycle-timed per character clock. The snow needs CPU write timing (PcMachine knows the PIT time of each write) | Card swap (`IPcVideo`) + CGA INT 10h stubs |
| 4 | **QEMU 0.9-0.10 `hw/vga.c`**: dirty-page renderer | It updates by 4 KB dirty pages. Register-only changes miss the redraw, so the screen is stale until the next write to the page; `full_update` is triggered only on mode or palette-size changes. There are no mid-frame effects. | QEMU git, tag v0.9.1 (2008), `hw/vga.c` + `vga_template.h`. **MIT** (Fabrice Bellard header). [partial] | ~2,200 + ~500 lines | Easy: our Vga already *is* this code (tiny386 ports it), so this is mostly a dirty-page tracker added to `MemWrite` | Render character. Low novelty: the jank is close to Bochs #1 but coarser (whole page rows) |
| 5 | **PCjs `modules/pcx86/lib/video.js`**: cell-diff renderer | Text and graphics are redrawn by comparing cell/byte caches. Known quirks: the effects of EGA/VGA latch and bit-plane writes are visible only after a full refresh; no snow; no per-line timing. | github.com/jeffpar/pcjs. **GPL-3** (MIT for some later files: check). [unverified] | Very large, ~8,000 lines | Hard: big JS, a lot of it is the debugger and UI | Card swap; poor value for the size |
| 6 | **Tandy/CGA snow from reenigne's notes** (rebuilt rule, no code) | The same snow as #3, rebuilt from the documented rule (a CPU access in the display window replaces the fetched char/attr byte with the bus byte) | reenigne.org blog posts on CGA snow and the composite model. Description only, so no code licence; credit as an inspiration reference. [unverified] | n/a | Easy if #3's card exists | Option on the #3 card |

## Recommendation (2-4 picks)

1. **Bochs dirty-tile renderer (#1).** It has the most visible jank for the least work. It is a pure render character on the existing Vga, so no card or BIOS work is needed, and stale tile patchworks on scrolling DOS games are exactly the "faithful jank" look.
2. **DOSBox 0.65/0.72 line drawer (#2).** It is the DOS emulator most people actually saw. It adds the line hook, which every later mid-frame core will need, and its jank (palette tearing, split screens a line off) is distinct from #1.
3. **PCem CGA with snow (#3, + #6 as its rule notes).** This is the iconic CGA artefact, and it is the only pick that adds a new *card* (the `IPcVideo` extraction). Take it after 1-2, because it needs the CGA INT 10h stubs and games that support CGA. Tandy is a follow-up on the same card.

Skip #4 (too close to what we already run) and #5 (size).

## Open gaps
- No file was pinned this session: the DOSBox 0.65/0.72 tags (SourceForge SVN), the Bochs version in which `vga.cc` got the tile updater (the target is the last release before the 2.x "vgacore" split), the PCem v8 zip, and QEMU v0.9.1 `hw/vga.c`. The line counts above are estimates.
- The PCjs licence per file.
- Which Vaults DOS / Super VRUN DOS scenes use register-only scrolling or mid-frame palette changes (these are the scenes where #1 and #2 would show their jank).

## Downloads that would be needed (not done)
- Bochs source tarball (2.0.x) from sourceforge.net/projects/bochs.
- DOSBox 0.72 source tarball from sourceforge.net/projects/dosbox.
- PCem v8 (or v10) source zip from pcem-emulator.co.uk.
- (optional) QEMU 0.9.1 tarball from download.qemu.org.
