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
