# Butchering mode — plan

A Tools & Activities mode (beside Corruption Slop) that recreates the *mechanisms* of the Melted
Electronics Glitch Boy (reverse-engineered KB in `~/Downloads/glitchboy_re/kb`, start at `10-recipe.md`;
research-use only, reimplement ideas, never copy firmware code), exploded into one control per effect,
and built to extend across every console.

## Decisions (interview 2026-11-03)

| Topic | Decision |
|---|---|
| Lifecycle | Entering takes a savestate; leaving reloads it (everything done inside is undone). A **Reload State** button returns to the entry state at any time. The UI says clearly that leaving discards progress. |
| Controls | No knobs, no macro: one toggle per effect, plus sliders where an effect has a continuous part (rate, magnitude, duration). |
| Presets | Named presets per console (save / load a set of toggles + sliders). |
| v1 console | **NES only**; other consoles added one by one after the loop is proven. |
| v1 NES cores | Full set on the FIX cores; the cartridge-pin group reaches most other PPUs for free (shared `Cartridge.PPURead`); anything a core can't do is shown as unavailable in the panel (no silent fallback). |
| Placement | Desktop app only in v1; Lite later. |
| Panel groups | Picture bus · Sprites · Sound (Bands later). Built from a per-console descriptor. |
| Mixed cores | The panel follows whichever chip actually sits on each bus. |

### Picture bus (v1)
- **Two pin groups:** *Console pins* (PPU address/data bus, before mapper banking; hits pattern, nametable, attribute fetches and `$2007`) and *Cartridge pins* (CHR chip, after banking).
- **Address lines A0–A13 and data lines D0–D7**, each with a fault type: **stuck low, stuck high, bridged to a neighbour line**, plus a **flicker / resistance** slider (per-fetch probability: intermittent like partial contention).
- **"Bend writes too"** toggle: off = read-only (Glitch Boy); on = PPU writes (CHR-RAM, VRAM) also land bent, so damage lasts until Reload State.

### Sprites (v1) — Glitch Boy's right knob, exploded
- Classic: X jitter, Y jitter (rate + range, hidden sprites stay hidden), tile ±1 (rate), attribute bit toggles V-flip / H-flip / behind-BG / palette 1 / palette 0 (rate).
- Organic: 8-sprite group lag, clone 2-back, field swap, force-bit (each toggle + rate/duration).

### Sound (v1)
- Classic: bit-flip toggles for bits 0–4 (rate), ±1 detune, per-channel drop-out toggles (pulse 1, pulse 2, triangle, noise, DMC) via `$4015`.
- Organic: stutter (replay older writes), drone, duty force, detune (toggle + amounts).
- DMC sample registers and `$4014` are not filtered.

### Later rounds
- **Bands:** the organic per-scanline engine (shift, vertical slip, pattern-table swap, nametable swap, BG off, sprites off, sprite height, banded bend), toggle + spawn rate / width / strength / lifetime each.
- **RAM nudges:** learned-variable profiler + probe.
- Other consoles (GB, SNES, SMS/GG, Genesis), Lite, frozen/gimmick NES cores.

## Architecture notes (from the seam survey)
- One shared per-session object (`BusBend`-style): masks/faults, `Bend(addr)` / `BendData(v)` costing one branch when off, `FilterWrite(port, v)` for the transit side.
- NES: cartridge pins at `Cartridge.PPURead` / `PPUWrite` ([Cartridge.cs](../../Windows/NesEmulator/board/Cartridge.cs)); console pins at the top of `PPU_FIX.Read` / `Write`; sprite transit at OAM DMA (`$4014`) / `$2004` in the bus; sound at the APU register writes in `Bus.cs`.
- Other consoles index VRAM directly in render code (GB `PPU_GB` ~11 sites, SNES `PPU_SFC` ~12, Genesis `MdVdpRender` ~11, SMS/GG `SmsVdp.Render` ~22). Check SNES tile caches (`InvalidateCaches`) before hooking there. Transit seams are at board level on every console.
- Per-console descriptor: address/data line count and meaning, sprite-memory fields, sound ports and channels; the panel is generated from it.
- Determinism: one seeded xorshift per effect group, so movies/states replay the same.
- Golden-hash check: all toggles off must change nothing.

## v1 as built (2026-11-03)
- **Engine** `Windows/NesEmulator/corruption/Butcher.cs` (settings model, pin-fault compiler, sprite + sound filters, per-frame events). `NES.ApplyButcher` / `GetButcherReach`; attached at each `RunFrame` start, detached once idle (off = `Bus.Butcher` / `Cartridge.Butcher` null).
- **Hooks**: console pins at the top of `PPU_FIX.Read` / `Write` (bent fetches landing in the palette read the nametable mirror instead: the palette is inside the PPU); cartridge pins in `Cartridge.PPURead` / `PPUWrite` via the new pure `IMapper.TryPpuToChrIndex` (mappers 0, 1, 2, 3, 4, 7, 9, 30, 33, 90, 228 and the SPD twins; **not MMC5** - the panel says so). The mapper's real `PPURead` always runs, so latches and A12 IRQs are untouched. Sprites in `Bus.FastOamDma` (every PPU core except EXE / IMG) and `$2004` (FIX); sound in the bus's APU write path (DMC `$4010/$4012/$4013` and `$4017` untouched).
- **Bridge** = wired AND with the next line up (the top line bridges down). Flicker = per-fetch chance the fault does not make contact.
- **Sprite-0 guarantee** (from Glitch Boy): while the mode is attached, PPU_FIX forces the sprite-0 hit at the end of sprite 0's last row if bent tiles or jitter lost it. Without it SMB hangs in its NMI `$2002` poll, and because its sprite DMA never runs again even "all off" cannot free it. Other PPU cores have no guarantee yet.
- **API** `/api/butcher/{reach,settings,off,presets}` (gated with Corruption Slop's unlock); presets in `%APPDATA%\BrokenNes\Butchering\presets-nes.json` (or `BROKENNES_SAVES_DIR`).
- **Webmodule** `Windows/Webmodules/ButcheringMode` (Tools & Activities, unlocked with Corruption Slop): entry state through `/api/gh/base-state`, Reload State, All off, Exit & undo, presets, a pin grid per group (click cycles connected / low / high / bridge, right-click reconnects).
- **Verified**: golden suite 125 pass / 0 fail (all off = nothing moved); headless check on Mega Man 2 (CHR-RAM): never-attached = all-off = disabled bit for bit, same settings twice = same hashes, sound effects change only audio, pins and sprites only the picture; SMB in the desktop app: every pin fault and sprite effect visible, game keeps running, exit restores a clean picture. **Not verified by ear**: the sound effects (hashes / RMS only). Panel tested in the browser pane against a test instance (clicks, toggles, presets, exit).
- **Next**: sprite-0 guarantee for the other PPU cores, MMC5 cartridge pins, then the Bands round.

## Round 2 as built (2026-11-03): bands, RAM nudges, every NES PPU core, the other consoles
- **Bands** (PPU FIX only): 6 slots, 8 types (shift = row rotated after it is drawn; slip = IncrementY at the band's first line; pattern / nametable swap = XOR on the fetch addresses; BG / sprites off = PPUMASK bits cleared for the line; sprite height = PPUCTRL bit 5 flipped for the frame; bend = A0-A8 lines grounded inside the band). Overrides are computed at the end of each line for the next one.
- **RAM nudges**: Glitch Boy's profiler + probe, generalised by a per-console RAM window. Two departures from the device, both found on SMB: zero page is included (SMB keeps its objects there; the stack page and the sprite DMA page are skipped), and samples that sat on a quiet byte for a 12-frame window move on (otherwise they settle on static bytes for good). Direction counts span a watch's life (halved past 200) so slow oscillations qualify.
- **Every NES PPU core**: console pins + `$2004` in BFR, CUBE, CUBEX, EIL, FMC, LOW, LQ, SPD, ULQ (EIL / SPD partial: their tile fetches bypass `Read`). EXE / IMG have no PPU bus. Bus-level **sprite-0 safety net** for every core but FIX: a `$2002` read reports the hit once the PPU's line counter is past sprite 0's last row (`IPpuFrameClock`). SMB keeps running on all 12 cores under heavy damage (its frame counter advances every frame).
- **Console profile** (`ButcherProfile`): sprite record layout, hidden-sprite rule, attribute bits, sound register map (volume / duty / period-low registers, the drop register and how a drop applies), PSG mode, RAM window + stack page, VRAM address / data lines. `IButcherHost` = NES, `GbSession`, `SmsSession` (+ GG), `MdSession`, `SnesSession`; the API unwraps `SegaPictureSession`. Presets are kept per console (`presets-<console>.json`).
- **Game Boy**: VRAM pins (14 lines incl. the Color bank line) on PPU_GB's 4 fetch sites + VRAM writes; OAM DMA filter (Y, X, tile, flags; Y 0 / >=160 stay hidden); sound map NR10-NR52 (drops via NR51, drone = full volume envelope held; power + wave RAM untouched); WRAM nudges.
- **Master System / Game Gear**: VRAM pins on every VDP render fetch; sprite-table filter on VRAM writes (Y, X, tile; Y = $D0 never touched); a **PSG filter** (latch / data bytes: bit flips, nudges, detune on tone data; drops hold volume 15, drone volume 0, stutter replays); 8 KB RAM nudges.
- **Genesis**: VRAM pins (16 lines) on every render fetch; PSG filter on both 68000 and Z80 writes (default sound unit only); 64 KB RAM nudges. **Not yet**: sprites (behind the VDP sprite cache), FM (YM2612).
- **SNES / WideMess**: word VRAM pins (15 address, 16 data lines: two chips) on the render fetches + VRAM writes; OAM filter on every `$2104` write (X, Y, tile, attributes); 128 KB RAM nudges. **Not yet**: sound (SPC700 + DSP).
- **Fix**: a drop-out no longer assumes every channel was off when the game had not written its enable register since the mode attached.
- **Verified**: full golden suite (see commit); per console in a test instance: reach, every pin fault changes the picture, off + Reload State gives the exact entry frame (hash-identical) on NES, GB, SMS, GG, Genesis and SNES. Sound still only checked by hashes / RMS, not by ear. RAM profiler confirmed variables on SMB in play; title screens give it little to learn.
- **Still open**: Genesis sprites + FM, SNES sound, bands beyond PPU FIX, MMC5 cartridge pins, Lite.

## Direction (the user, 2026-11-03)
The point is not to copy the Glitch Boy: it is to take the concepts its research uncovered (bus pin faults, transit filters, learned-variable nudges, timed bands, safety nets) and apply them to parts the device never covered - every console's picture chips and sound chips first, and later the **unsafe areas** (CPU, RAM, VRAM writes) where bending can leave permanent bugs until a reboot (the entry savestate / Reload State is the way back).

### Agreed next scope: audio on every console
- **Genesis**: move the PSG hook to the board so every sound unit is covered, and add a YM2612 filter on both the 68000 and Z80 address/data writes (detune, operator bit flips, key-off drop-outs, total-level drone).
- **Master System (Japan)**: a small YM2413 port filter.
- **SNES**: a hook at each sound core's DSP register write ($F2/$F3) - pitch, volume, envelope, key-on/off - shared if the cores share a DSP class (not checked). Not the $2140-$2143 ports (they break the CPU/SPC handshake: hangs, not glitches).

## Round 3 as built (2026-11-03): audio on every console
- **Genesis**: every sound unit (MD, GENS, GPGX, BLST, PICO) derives from `MdSoundUnit`, so the PSG hook already covered them all (an earlier note said "default unit only": wrong). New **YM2612 filter** at the unit's single `YmWrite` (68000 and Z80 both land there): address latch per part; drop-outs key a channel off (`$28`, ops cleared) and inject a key-off when a drop starts; drone holds total level `$40-$4F` at 0; detune / nudges on the frequency low bytes `$A0-$A2`; bit flips on the operator and channel parameters `$30-$B6`; LFO, timers, DAC left alone. Drop list = Tone 1-3, Noise, FM 1-6. Not reached: the "Genesis sound on another console's chip" bridges (MdSoundOnNes / MdSoundOnChip) - the panel says so.
- **Master System FM unit (YM2413, ports $F0/$F1)**: same model - key-off on `$20-$28` bit 4 (never set), drone = volume 0 on `$30-$38`, detune / nudges on `$10-$18`, bit flips on the instrument / volume registers. The profile (`SmsFm`, 13 channels) is picked when the session's sound unit has FM.
- **SNES**: every sound core but HLE derives from `APU_SFC`: one hook at its S-DSP data port (`$F3`), through the generic sound path with an S-DSP profile - 8 voices; drop-outs hold the voice's KOF (`$5C`) bit set and release to the game's own KOF; drone = voice volume `$x0/$x1` at `$7F`; detune / nudges on pitch low `$x2`; bit flips on the per-voice registers; every global (KON, FLG, echo buffer address, sample directory, FIR...) left alone so the SPC's RAM is never overwritten. HLE: "not reachable" in the panel.
- **Engine**: `DropRegDefault` per profile (what the drop register holds before the game writes it); the drop list grew to 16 channels.
- **Verified** (headless, per console: never attached vs all-off vs sound effects, twice): SNES / Genesis MD / Genesis GENS / Master System / Mark III / Game Gear / Master System FM (Phantasy Star (Japan), 13 channels): all-off bit-identical, sound changed, deterministic. Golden suite 125 / 0, Sega 46 / 0, SNES 21 / 0, SMS 26 / 0, Genesis 10 / 0. Still not checked by ear.

## Wave 4 decisions (interview 2026-11-03): finish the gaps
| Topic | Decision |
|---|---|
| Scope | Genesis sprites, bands everywhere, HLE + bridged sound. (MMC5 cartridge pins and the unsafe round wait.) |
| Bands on other consoles | **Native per chip**: each console's own registers (scroll, layer enables, tile bases); the panel lists what that console supports. |
| Extra band types | **Yes, per console** on top of the shared 8 (e.g. SNES Mode 7 matrix wobble, extra BG layers off, Genesis plane swap, GB window shift). |
| NES bands | **All NES PPU cores** where their structure allows; partial ones marked in the panel. |
| Bridged sound + SNES HLE | Corrupt the **guest's original register writes**, before any bridge translates them: same effects whatever chip plays them. |
| Genesis sprite links | **Never** corrupted (the list never loops or cuts); Y, tile, attributes, X are. |
| How | Here, one piece at a time, each verified (golden suites, per-console checks) and committed before the next. |

## Wave 4 as built (2026-11-03)
- **Genesis sprites**: the sprite filters on the table's Y low, attributes (V/H flip, priority, palette), tile low and X low bytes as they are written to VRAM (before the sprite cache records them). Size, link and the high bits are never touched.
- **Bridged sound**: MdSoundOnNes / MdSoundOnChip and the SNES NES-chip bridge / adapter hold a real MdSoundUnit / APU_SFC: the engine attaches to that inner unit, so the guest's own writes are bent whatever chip plays them. SNES HLE makes no sound (it only fakes the IPL upload): the panel says so. (Master System bridges were already covered: the filter sits at the board's port.)
- **Bands everywhere, native per chip**: `Butcher.BandLineEx(line)` returns a generic `BandOverride` (layers off, pattern swap, map swap, shift, slip offset, extras 1-3, bend lines); each renderer applies it to its own registers for that line and puts the game's values back (unless the CPU rewrote one meanwhile):
  - SNES (`PPU_SFC.RenderLine`): TM/TS, BGnHOFS/VOFS, BGnSC, BG12/34NBA; extras Mode 7 wobble (M7A/M7D), BG mode flip, colour-math flip.
  - Master System / Game Gear (`SmsVdp.BeginLine/EndLine`): h-scroll reg 8, latched v-scroll, name table reg 2, sprite patterns reg 6, sprite size reg 1, display; extras sprite zoom, left-column blank, sprite shift.
  - Game Boy (`PPU_GB.NextLine`): SCX, SCY, LCDC (tile data, BG map, BG / OBJ enable, OBJ size), WX; extras window off, window shift, window map swap. A CPU read of LCDC / SCX inside a banded line sees the band's value.
  - Genesis (`MdVdp.StartNextLine` + h-scroll read): h-scroll offset, plane A / B map swap, display; extras shadow / highlight, plane width, window side. No slip / pattern swap / sprites off / sprite height on this chip yet.
  - NES frozen cores (BFR, CUBE, CUBEX, EIL, FMC, LOW, LQ, SPD, ULQ at their `scanline++`): PPUCTRL / PPUMASK overlays - pattern swap, sprite height, BG off, sprites off. **No bend** there (measured: these cores do not fetch line by line where a bend could land). PPU FIX keeps its full 8-type engine.
  - The panel lists the band types each console / core supports (`reach.bandKinds`) and its extras (`reach.extraBands`).
- **Verified**: headless per console (never attached vs all-off vs each band type at full rate): all-off identical everywhere; every listed band type changes the picture and the game keeps moving (SNES, Game Boy, Master System, Genesis, NES FMC / LQ). Extras that use a layer the test game does not show (Super Mario Land's window, Super Mario World's Mode 7) changed nothing there - game-dependent. Golden suites: 125 / 0, Sega 46 / 0, SNES 21 / 0, Game Boy 4 / 0.

## Next-wave groups A-C as built (2026-11-04)
- **A1** `UAT/cases/butcher.json`: per console an all-off case that reproduces the plain golden of the same game, and a heavy-preset golden (`UAT/butcher/*.json`, Workshop `--butcher <file> [--butcher-at N]` on `--romtest` / `--session-test`).
- **A2** watchdog: frames with the work RAM unchanged (`stillFrames`) or a crashed NES (`crashed`) - the panel offers Reload State after ~3 s.
- **A3** palette (display only, every console): hue +60/+120/+180/+240, hue + brightness flip, invert, channel swap, posterize, auto-cycle; cached colour table on the finished picture; the game's palette and RAM untouched.
- **A4** MMC5 cartridge pins (`Mapper5.TryPpuToChrIndex`).
- **A5** group switches on every section (kept in presets; a switched-off group is sent as all-off).
- **B** per-field organic sprite events (SMS/GG, Genesis, SNES, NES $2004); SNES sprite high table; Genesis sprite cache read around while bent; stutter on every channel incl. FM; Genesis slip + sprites-off bands; RAM learning / ready readout.
- **C (unsafe)** NES cartridge PRG pins on the CPU bus (A0-A14, D0-D7, flicker, bent mapper writes); RAM rot and VRAM rot on every console (VRAM = CHR-RAM or PPU FIX nametables on the NES, VRAM elsewhere, word VRAM on the SNES); "bend writes too" moved here as its own unsafe group. Headless: CPU pins crash SMB (the watchdog reports it), rot changes every console's picture, all-off identical.
- **Speed** (SMB, FIX, 3000 frames, interleaved): pre-Butchering build 304-314 fps; today with the mode off 306-320 fps (no cost); pin faults on ~270 fps; many effects on ~240 fps.
- **Still open (group D)**: panel layout / search, randomize / mutate, solo-effect, MIDI / VST CC, capture, Lite; CPU pins on the other consoles.

## Group D interview (2026-11-04)
| Topic | Decision |
|---|---|
| Scope | Panel layout + search, Randomize / Mutate, Solo. **Capture: later** (undecided). MIDI / VST CC and Lite: later. |
| Layout | **Tabs per group** (Picture / Sprites / Sound / Bands / RAM / Palette / Unsafe), one visible at a time, plus a search box across all. |
| Randomize unsafe | Never, unless an "include unsafe" checkbox is ticked. |
| Strength | One strength slider: few gentle effects at low, many strong at high; Mutate nudges the current values by that amount. |
| Solo | An S button on each effect: only that one runs while soloed; everything else is kept and comes back on un-solo. |
| Rolls | A history of the last ~20 rolls to step back through; any roll can be saved as a preset (seed included). |
