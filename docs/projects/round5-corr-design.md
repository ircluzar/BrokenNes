# Round 5, agent `corr`: corruption in the plugin. Design (milestone C1)

Date: 2026-10-17. Author: agent `corr`. Status: design for the lead to read; C2 starts from it. Everything under "As it is today" was read in the code in this
worktree (commit 15795e6), not recalled. Everything under "Proposal" is not built yet.

The goal, in the user's words: farm corruptions in BrokenNes until a corrupted instrument sounds good, keep the corruption in a **stockpile**, then in the
BrokenNes2 FL plugin open the stockpile, pick the corruption, **Start** it, and perform **Instrument Runaway** to take over the corrupted instruments (the
SPC700 on the SNES first; Sega and 8-bit consoles later). The corrupted state must persist through a saved FL project.

---

## 1. As it is today

### 1.1 What a corruption and a stockpile are (the NES-only Glitch Harvester)

Code: `Windows/NesEmulator/corruptor/` (`Corruptor.cs`, `GlitchHarvesterEngine.cs`, `GlitchHarvester.cs` (the Blazor/Lite bridge), `CorruptorModels.cs`), the
hook `Windows/MainForm/MainForm.Corruption.cs`, the HTTP API `Windows/webapi/WebApiServer.Endpoints.GlitchHarvester.cs` (documented in
`Windows/webapi/README.md` and `docs/webmodule-api-requirements.md`), the UI `Windows/Webmodules/GlitchHarvester/` and `CorruptionSlop/` (and
`WebLite/Pages/Nes.razor`).

| Thing | What it is |
|---|---|
| **Base state** (`HarvesterBaseState`) | `Id`, `Name`, `Created`, `State` = a NES savestate as a JSON string (`NES.SaveState()`: CPU/PPU/APU/mapper JSON, RAM, PRG RAM, CHR RAM as base64; **the ROM is excluded** (`romData` empty), the ROM's SHA-256 (`romHash`), file name and **full local path** (`romPath`) are inside). |
| **Blast layer** (`BlastInstruction`) | `Domain` (string), `Address` (int), `Value` (byte): one write. A corruption is a list of them. |
| **Entry** (`HarvestEntry`) | `Id` (GUID), `Name`, `BaseStateId`, `State` (the machine just before the writes, a second NES savestate), `Writes[]`, `Created`. Replay = `nes.LoadState(entry.State)` then `ApplyBlastLayer(entry.Writes)`. |
| **Stash** | the list of entries just made ("Stash 1", ...), in memory. **Stockpile** = the entries the user kept ("Entry 1", ...), in memory. Both vanish with the process. |
| **Domains** | `PRG`, `PRGRAM`, `CHR`, `RAM` (2 KB system RAM). `Poke*` go straight to arrays (`Cartridge.PokePrg`, `Bus.PokeRam`...). |
| **Blast types** | RANDOM, TILT (+-1), NOP ($EA), BITFLIP, RANDOMTILT, and the ML "Imagine" modes (PRG bytes from a model). |

**Where a stockpile lives on disk: nowhere.** `GlitchHarvesterEngine.Stockpile` is an in-memory list. The only file is the **Export** (a browser download in
the web module, a JSON string from `GET /api/gh/stockpile/export`): `[{Id, Name, BaseStateId, Created, Writes, State}]`, indented, PascalCase, no version,
no game identity, no description. The Import (`POST /api/gh/stockpile/import`, Lite's file input) reads `List<HarvestEntry>` and the engine gives every
imported entry a new GUID.

### 1.2 Defects found while reading (evidence, not guesses)

1. **Two export shapes.** The engine writes `State`; Lite's `GhExportStockpile` (`GlitchHarvester.cs:177`) writes `BaseState` (and compact). Both import into a
   `HarvestEntry`, which has only `State`, so **a Lite export imports with an empty state**. `GhReplayEntry` then looks the base state up by `BaseStateId`,
   finds none and `return`s: a **silent no-op** (`GlitchHarvester.cs:136`).
2. **Domain sizes are guessed.** `MainForm.BuildMemoryDomains` sizes `PRG`, `PRGRAM`, `CHR` with `GetApproxSize` (probes the first KB and the power-of-two ends for
   non-zero bytes). The UAT run (`UAT/findings/glitch-harvester-r2.md`) shows "PRG ROM (256B), PRG RAM (256B), CHR (1KB)": a PRG blast never reaches past byte 256 of
   the ROM. The true sizes exist (`NesMemoryExtensions.GetActualPrgRomSize` etc.).
3. **TILT and BITFLIP in a stash are plain random bytes.** `Corruptor.GenerateBlastLayer` writes a random byte for both and says "adjusted in ApplyBlastLayer",
   which does not adjust anything. (`Corruptor.Blast`, the live blast, does compute them from the old value.)
4. **A ROM write outlives the state.** A savestate excludes the ROM, so replaying an entry with PRG writes after another entry that had some leaves both sets in
   the ROM (the writes compound). Nothing records the original byte, so nothing can undo them.
5. **`GenerateBlastLayer` uses an unseeded `Random`** (fine for the writes it returns, useless if you want to re-make a blast from a seed).
6. The web module reads `data.imported` on import; the API returns only `message`. Cosmetic.
7. A stockpile entry says nothing about **which game** it was made on beyond what is inside the NES state JSON (and that carries the user's file path).

### 1.3 Other consoles

BrokenNes 2 plays SNES, Game Boy / Color, Master System, Game Gear and Genesis through `IConsoleSession` (`Windows/NesEmulator/systems/`). Nothing in the
corruptor touches them: `_getNes()` / `_getCorruptor()` are the only handles the API has, and `MainForm` sets `nes = null` while a session runs.

**Save-state support per console today** (`IConsoleSession.SupportsState`): NES (its own JSON, not the session contract), Game Boy (`gb-1`), Master System, Game
Gear, Genesis: yes. **SNES: no** (`SnesSession.SupportsState` is false; `Workshop/Sega/SessionStateSuite.cs` asserts it). The lead's update: a new agent
(`snes-state`) is building SNES SaveState/LoadState this round. The design below is written against `SupportsState`, never against a console name.

### 1.4 What the plugin has that matters here (read, not changed)

- ROM mode: `Emulator.Rom.cs` runs one `IGame` (`NesGame` or `SessionGame` over an `IConsoleSession`) frame by frame **only when someone pulls audio or an
  editor pumps the picture** (`Produce`, under `romLock`), with `g.SetPad(PadBits)` once per frame. The game's frame count is therefore *not* tied to the clock,
  only to how many `RunFrame`s happened: the machine at frame F is the same machine whenever it is reached (deterministic cores, same input per frame).
- `StartLoad` builds a game on a worker thread and hands it to `pendingGame`; the swap happens under `romLock` between frames (`ApplyPending`).
- Instrument Runaway (`Emulator.SetRunaway`): under `romLock` it calls `game.CaptureSpc()` = `new SpcSnapshot(spc.Aram, spc.Dsp.Regs)` (a copy of the 64 KB
  ARAM and the 128 DSP registers), then freezes (`Produce` stops running the game). A project saved in Runaway keeps that copy (`SPC1`, ~64 KB) and
  reopens frozen with the instruments: **so an ARAM or DSP corruption that is in the machine when Runaway is pressed already persists through a project**.
- Project state (`Bn2Plugin.SaveRestoreState`): parameters, ROM path, `EMU2` place, then one optional section (`SPC1`, `GBS1`, `PSG1`/`OPL1`/`OPN1`). Agent
  `c4b` is extending it to the whole machine state; **I do not touch those paths until it is merged**.
- The plugin links `Core/BrokenNes.Core.csproj` (which links `Windows/NesEmulator/{board,systems,snes,gb,mix,sega,sms,md,...}`) and is Native AOT: **no
  reflection-based serialization** (`System.Text.Json` with reflection is out; `Utf8JsonWriter` / `JsonDocument` are fine, `NES.LoadState` already uses them).
- `Core` does **not** link `corruptor/` (the Workshop does not either); the desktop compiles all of `Windows/NesEmulator` except a remove-list; Lite lists its
  files by hand.

---

## 2. The stockpile file (format v2)

One JSON file, UTF-8. No reflection serializer: written with `Utf8JsonWriter`, read with `JsonDocument` (AOT-safe, and the same code runs in the desktop, Lite,
the plugin and the Workshop). Property names are lowerCamel. A reader ignores properties it does not know (that is how the format stays additive); it refuses a
file whose `version` is higher than it reads, with a message.

```jsonc
{
  "format": "brokennes-stockpile",
  "version": 2,                       // bumped only for a change an old reader would misread
  "created": "2026-10-17T10:00:00Z",
  "writer": "BrokenNes 2 (desktop)",  // informational
  "name": "SMW drum kit gone wrong",  // optional file-level name
  "entries": [
    {
      "id": "f0c3...",               // GUID; stable; a project refers to it
      "name": "Yoshi drums, sample table flipped",
      "description": "Flip bits in the sample directory; the kick becomes a growl.",   // new: user text
      "created": "2026-10-17T10:00:00Z",
      "tags": ["snes", "drums"],       // optional
      "game": {                         // what the corruption was made on
        "console": "snes",              // Consoles.Key: nes snes gb gbc sms gg md
        "sha1": "6b47bb75d16514b6a476aa0c73a683a2a4c18765",   // sha1 of the ROM file bytes as the session sees them (ConsoleSessions.Sha1)
        "title": "SUPER MARIOWORLD",    // the header title, for the user
        "file": "smw.smc"               // file NAME only, never a path
      },
      "machine": {                      // the parts it was made with; replay refuses or warns when they differ
        "cpu": "SFC", "ppu": "SFC", "apu": "SFC",   // CoreCatalog ids (NES: "FIX" ids)
        "note": "no coprocessor"        // e.g. the coprocessor / firmware used
      },
      "anchor": { ... },                // where the machine is when the writes land (section 3)
      "writes": [                       // applied in order, at the anchor (or "at" frames after it)
        { "domain": "ARAM", "address": 4660, "value": 255, "was": 12 },
        { "domain": "DSP",  "address": 76,   "value": 1,   "was": 0, "at": 30 }
      ],
      "summary": "2 writes: ARAM x1, DSP x1",      // informational, recomputed on write
      "source": { "blast": "BITFLIP", "intensity": 2, "seed": 1234, "domains": ["ARAM","DSP"] }   // optional: how it was farmed
    }
  ],
  "bases": [ ... ]                     // optional shared anchors, section 3.4
}
```

- **`writes[].domain`** is a domain id (section 4). The legacy NES ids `PRG`, `PRGRAM`, `CHR`, `RAM` stay valid and mean what they meant.
- **`was`** (optional) is the byte that was there when the corruption was made; with it a ROM-type domain can be put back (section 4.3) and a reader can tell
  that a write is a no-op on the current game.
- **`at`** (optional, default 0) is a frame offset *after the anchor*: the write lands that many frames later. The farming side only makes `at = 0` at first; the
  field exists so the project's own history (section 6.3) and a later "auto-corrupt for N seconds" capture use the same structure.
- Mixed stockpiles (several games, several consoles) are allowed: each entry has its own `game`. The plugin lists them all and says which fit the loaded game.
- **Legacy files** (a top-level JSON array, from the engine's or Lite's export, or an array of `HarvestEntry`) are read as-is: `Id Name BaseStateId Created Writes
  State|BaseState` map to `id name created writes` and an anchor `{ "kind": "state", "stateFormat": "nes-json", ... }` with the console `nes`. The game is filled from the
  state's own `romName` / `romHash` (the `romPath` inside is dropped on rewrite: a path is private). A legacy entry with **no** state (the Lite defect above) is read
  as anchor `none` **and flagged `"legacy":"no-state"`** so the UI can say so instead of replaying nothing.
- **No ROM data**, ever. A `state` anchor holds machine state (RAM, registers), which can contain fragments of the game's data: stockpiles with states are the
  user's files, not repository content. Repository fixtures are generated (a tiny ROM made by the test, writes only).
- Limits (a stockpile is a file a user hands the plugin: it is parsed as hostile): file <= 64 MB, <= 4096 entries, <= 1,000,000 writes per entry, one decoded state
  <= 64 MB. Over a limit: the entry (or file) is refused with a message, the rest is read.
- Written atomically (`.tmp` then replace). Entry order is kept. Rewriting a legacy file produces v2 (the old file is not touched; the app offers Save As).

### 2.1 Where stockpiles live

`%APPDATA%\BrokenNes\Stockpiles\*.stockpile.json` is the default folder for the desktop's Save / Open (both apps' file dialogs start there). A user may keep a
stockpile anywhere: the plugin's Open dialog starts in the folder of the last one opened (remembered like the other plugin preferences, registry
`HKCU\Software\Bogue\BrokenNes2`, not in the project).

---

## 3. Anchors: how a corruption finds its machine

A corruption is "writes at a moment of a game". The moment is the **anchor**. Three kinds, all in the file from the start so nothing changes when the SNES
gets a state:

| `anchor.kind` | Contents | Start does | Cost | Exact? |
|---|---|---|---|---|
| `state` | `console`, `stateFormat` (the session's `StateFormat` unit, e.g. `gb-1`, `nes-json`), `encoding` (`deflate-base64`; `json` for NES), `data`, optional `frame` | `session.LoadState(bytes)` (NES: `nes.LoadState`), then the writes | one load, instant; file grows by the state (GB ~tens of KB, NES ~100 KB, SNES est. 200 KB) | yes (the state), refused when the state is for another game or sound unit (`ConsoleStateFile.Unwrap` messages) |
| `boot` | `frames` (frames run from power-on), `inputs` (the pad per frame, run-length text), `pads` (players), `battery` (null, or the save RAM the game started with, base64), `cores` | builds a **fresh** session, replays `frames` frames of `inputs`, applies the writes | `frames` frames of emulation: SNES ~600 fps headless measured here (10x real time), the plugin slower; **cap 36,000 frames (10 min of game), refuse beyond with a message; progress shown while it runs** | yes, if the cores are deterministic (section 3.2) |
| `none` | nothing | applies the writes to whatever is running now | instant | no: it lands wherever the game happens to be |

`anchor.kind` also accepts `ref`: `{ "kind": "ref", "base": "<id>" }` pointing at `bases[]` (one state shared by many entries, which is how a farm session of 200 blasts
from one base stays small; the legacy format duplicated the state per entry).

### 3.1 Which to prefer, per console

| Console | `state` | `boot` | Prefer |
|---|---|---|---|
| NES | yes (existing) | possible (the NES is not a session; C5) | `state` |
| SNES | **not until `snes-state` merges**; then yes, through `SupportsState` | yes, today | `boot` now; once `SupportsState` is true, **`state` when the position is far from power-on (more than ~1 min of play, or needed input is not recorded), `boot` when the file must stay small or must not depend on a core's state layout** (a state is refused after a core changes its layout: `format` unit; a boot replay survives any change that keeps the cores deterministic). The farm writes `state` when it can and keeps the boot recipe next to it when it has one (`anchor.alt`), the plugin takes `state` first and falls back to `boot` when the state is refused (with a message saying so). |
| Game Boy / Color | yes (`gb-1`) | yes (but an MBC3 RTC game reads the wall clock: not reproducible, flagged) | `state` |
| SMS / GG / Genesis | yes (`sms-1`, `md-*`) | yes | `state` |

`alt` is **not** a silent fallback: the user sees "state refused (...), replaying 4210 frames from power-on instead" on the status line.

### 3.2 What a `boot` replay needs, and what it cannot reproduce (said on screen, not hidden)

Determinism comes from the cores: same ROM, same cores, same power-on, same pad at each frame, writes only between frames. To make that checkable, the file records
the facts and the loader compares them:

- **ROM:** `game.sha1` must equal the loaded game's; otherwise Start refuses ("this corruption was made on X, the loaded game is Y").
- **Cores:** `machine.cpu/ppu/apu`. The picture chip does not change the machine; the **sound unit must be the native one** on the SNES (the `ARAM`/`DSP` domains do not
  exist under HLE). A different core id is a warning ("made with APU HLE"), a missing domain is a refusal.
- **Battery RAM:** the desktop loads `SnesSaves\sfc<sha1>.srm` at session start; the plugin does not. If the farm session started with a non-empty save, the
  anchor carries it in `battery` (<= 64 KB, deflate-base64) and replay applies it before frame 0. Without it the replay would diverge, and so the farm **refuses to
  write a `boot` anchor if it cannot say what the game started with** (it writes a `state` anchor, or says why not).
- **Input:** the pad the game was handed on every frame since power-on. The desktop records this (section 5); the plugin records it (section 6.2).
- **What cannot be reproduced** (each *poisons* the recording with a reason that is shown): a **Reset** in the middle (can be recorded as an event, planned: v1 poisons), a
  **core hot-swap** (APU/PPU/CPU), loading a **state**, importing a **battery save after start**, a wall-clock cartridge (Game Boy MBC3 RTC), **input given before the
  recording started** (for a stockpile entry that only has `none`, or a project whose history starts after the game did), and **frames beyond the cap**. A poisoned
  position can still be stored as a `state` anchor where the console has states; otherwise the farm says "this position cannot be stored for replay: <reason>; restart the
  game and play to it again" and writes nothing.
- **Coprocessor firmware:** the desktop passes real firmware when it finds it; the plugin passes none (the homemade DSP-1). `machine.note` records which; a mismatch is a warning.

### 3.3 Where writes land in time

Writes are applied **between frames**, never inside one: the desktop's emulation thread between `RunFrame`s (`QueueEmuAction`), the plugin under `romLock` before
`RunFrame`, the Workshop in the loop. `at = n` means "after n more frames". This is what makes `anchor + writes + the following inputs` a complete description.

### 3.4 Shared bases

`bases[]` entries are anchors with an `id` and a `name`. The desktop's "Add Base" creates one; "Blast" from a base writes entries with `{ "kind": "ref", "base": id }`;
saving a stockpile writes only the bases something refers to. A reader resolves `ref` before use and refuses an entry whose base is missing, with a message.

---

## 4. Memory domains per console

A **domain** is a named, byte-addressed, bounded window onto one memory of the running machine.

```csharp
namespace NesEmulator.Corruption;
public enum DomainKind { Work, Video, Palette, Audio, Registers, CartRam, Rom }
public interface IMemoryDomain {
    string Id { get; }            // "ARAM"
    string Label { get; }         // "SPC700 audio RAM (64 KB)"
    int Size { get; }             // bytes, the TRUE size
    DomainKind Kind { get; }
    bool Writable { get; }        // false: the machine has it but poking it is not defined (listed, refused with a reason)
    string? Unavailable { get; }  // e.g. "the SNES sound unit is the HLE stand-in: no ARAM"
    byte Peek(int address);
    void Poke(int address, byte value);   // includes the post-write hook the machine needs (below)
}
public static class MachineDomains {
    public static IReadOnlyList<IMemoryDomain> For(IConsoleSession session);   // unwraps SegaPictureSession, finds the SPC through a NES bridge
    public static IReadOnlyList<IMemoryDomain> For(NES nes);                    // PRG PRGRAM CHR RAM + NAMETABLE later
}
```

**Rules that make access live and deterministic**

1. **Frame boundary only** (section 3.3). Peek/Poke are plain array access: no bus cycle, so no read side effects (a bus read of `$2002` would clear a flag), no cycle
   cost, nothing that depends on timing. `Peek` of a 16-bit-wide memory is byte-addressed little-endian (`VRAM` byte 0 = low byte of word 0).
2. **Post-write hooks are part of the domain**, because some chips keep derived state. Found by reading: the SNES S-PPU caches decoded sprite attributes and bumps `oamVersion`
   on OAM/OBSEL writes (`PPU_SFC.cs:983`, "external writers to Oam must call InvalidateCaches()"), so the SNES `OAM` domain calls `Ppu.InvalidateCaches()`; the S-DSP
   registers go through `Dsp.Write` (KON latch, ENDX clear) not the `Regs` array; the other domains need none today. Each console's domain list is verified by a test that
   pokes a value, reads it back, and (where there is a picture or sound consequence) shows the consequence.
3. **True sizes** (`Size`), not probed ones. Out-of-range addresses are refused (`Poke` returns, the applier counts them and the entry says "n writes outside the domain").
4. **Not every memory is poked the same way as the CPU would**: ROM domains write the cartridge's own array. A session created from a shared `byte[]` would then change
   the caller's copy (`SnesCartridge.Load` returns the same array when there is no copier header). **Rule for creators: a session that will be corrupted gets its own copy
   of the ROM** (`rom.AsSpan().ToArray()`), and the ROM domain keeps an **undo journal** (section 4.3).

### 4.1 The domains

Sizes are the arrays' real lengths in this tree. "Access" is how Peek/Poke reach it today (the object is public unless noted).

| Console | Domain id | Size | Backing | Notes |
|---|---|---|---|---|
| **NES** | `PRG` | cart PRG ROM | `Cartridge.PeekPrg/PokePrg` | ROM kind: journal |
| | `PRGRAM` | cart PRG RAM | `PeekPrgRam/PokePrgRam` | battery RAM when present |
| | `CHR` | cart CHR (ROM or RAM) | `PeekChr/PokeChr` | |
| | `RAM` | 2048 | `PeekSystemRam/PokeSystemRam` | |
| **SNES** | `WRAM` | 131072 | `BOARD_SFC.Wram` | |
| | `VRAM` | 65536 | `PPU_SFC.Vram` (`ushort[0x8000]`) | LE bytes of words |
| | `CGRAM` | 512 | `PPU_SFC.Cgram` (`ushort[256]`) | |
| | `OAM` | 544 | `PPU_SFC.Oam` | **hook: `Ppu.InvalidateCaches()`** |
| | `SRAM` | cart RAM size (0 = none) | `SnesCartridge.Sram` | battery RAM; Super FX / SA-1 RAM when the chip keeps it elsewhere is listed unavailable |
| | `ROM` | cart ROM size | `SnesCartridge.Rom` | ROM kind: journal |
| | **`ARAM`** | 65536 | `APU_SFC.Aram` (found through `SnesSession.Apu`, a NES bridge's `Front`, or the parked SPC) | the SPC700's RAM: sound driver code, sequence data, **BRR samples, sample directory, echo buffer**. This is the instrument domain. Unavailable under `HLE` and when no `APU_SFC` exists |
| | **`DSP`** | 128 | `APU_SFC.Dsp.Read/Write` | S-DSP registers (voice pitch/volume/sample/envelope, KON, DIR, echo, FLG) |
| **Game Boy / Color** | `WRAM` | 8192 (DMG) / 32768 (CGB banks) | `BOARD_GB.wram` (private: **additive public accessor**) | |
| | `HRAM` | 127 | `BOARD_GB.hram` (private: accessor) | |
| | `VRAM` | 16384 (the array; a DMG game uses the first 8192) | `PPU_GB.Vram` | |
| | `OAM` | 160 | `PPU_GB.Oam` | |
| | `CRAM` | 128 (CGB BG 64 + OBJ 64) | `PPU_GB.BgPalRam/ObjPalRam` | CGB only |
| | `SRAM` | cart RAM | `GbCartridge.Ram` | |
| | `ROM` | cart ROM | `GbCartridge.Rom` | journal |
| | `WAVE` | 16 | `APU_GB.WaveRam` | the Game Boy's wave-channel instrument |
| **Master System / Game Gear** | `RAM` | 8192 | `BOARD_SMS.Ram` | |
| | `VRAM` | 16384 | `SmsVdp.Vram` | |
| | `CRAM` | 64 (SMS 32, GG 64) | `SmsVdp.cram` (private: accessor) | |
| | `SRAM` | cart RAM | `SmsCartridge.Ram` | |
| | `ROM` | cart ROM | `SmsCartridge.Rom` | journal |
| **Genesis** | `RAM` | 65536 | `BOARD_MD.Ram` | 68000 work RAM |
| | `VRAM` | 65536 | `MdVdp.Vram` | |
| | `CRAM` | 128 | `MdVdp.Cram` (`ushort[64]`) | |
| | `VSRAM` | 128 | `MdVdp.Vsram` (`ushort[64]`) | |
| | `ZRAM` | 8192 | the Z80 sound RAM inside `MdSoundUnit` (private: accessor) | the Genesis sound driver's RAM (C5) |
| | `SRAM` | cart SRAM | `MdCartridge.Sram` | |
| | `ROM` | cart ROM | `MdCartridge.Rom` | journal |

Plugin Instrument Runaway captures sound from: the SNES `ARAM` + `DSP` (`SpcSnapshot`), the Game Boy sound registers + wave RAM, and for the Sega consoles the PSG /
YM2413 / YM2612 registers and the DAC write stream. So the corruption domains that change **instruments** are SNES `ARAM`/`DSP`, Game Boy `WAVE` (and registers via
the game's driver code in `WRAM`/`ROM`), Genesis `ZRAM`/`ROM` (the Z80 driver and its patch tables). Registers of the Sega sound chips are not poked directly in v1
(the game driver writes them; corrupting the driver's data or code is the lever), which keeps domains to memories.

### 4.2 Generating a corruption (farming)

`BlastGenerator` (library): given domains, the selected ids, intensity, blast type and a **seed**, it returns `writes[]` **with final values and `was`** computed from the
live domain (so TILT / BITFLIP / NOP are real values, defect 3), and with an own splitmix64 generator so a seed always gives the same writes on any .NET version. The
engine keeps today's `Corruptor.GenerateBlastLayer` for the NES path unchanged (no behaviour change there); the new generator is what console farming and the Workshop
use. The distribution is uniform over the selected domains weighted by size (`Corruptor` picks a domain uniformly then an address: the small RAM domain gets as many
writes as ROM; I keep that rule for NES parity and offer `weight: "size"` as a farm option).

### 4.3 Applying and removing, deterministically

`CorruptionApplier.Apply(domains, writes, journal)` writes in order and records for each write `(domain, address, before)` in the journal. `Revert(journal)` puts the bytes
back in reverse order. What "removing" means per kind:

- **ROM domains**: exact (the journal holds the original bytes, or `Reload` the cartridge from a clean copy).
- **RAM / video / audio domains**: the machine has moved on after the write, so a revert does **not** rewind the machine (a game may have copied the byte elsewhere). Removing a
  corruption from a running RAM state means restoring the anchor (`state` load or `boot` replay without the writes). The applier says so; the plugin's "Remove" is
  "restart from the anchor without the writes" and tells the user that.
- Applying the same entry twice is idempotent for the bytes (same values), not for what the machine did in between.

---

## 5. Desktop side (C3): farming on console sessions

Scope, as I propose it to the lead (a choice at C1's end: C3 can stop after step 3 and still serve the user's flow):

1. **Persistence for what exists:** the engine's `Export/ImportStockpile` become the v2 writer/reader (`NesEmulator.Corruption.StockpileJson`); new engine methods
   `SaveStockpileFile(path)` / `OpenStockpileFile(path)`; endpoints `POST /api/gh/stockpile/save`, `POST /api/gh/stockpile/open` (`{ path }`; the web module has no file
   system access so the desktop shows the native dialog, the same way Load ROM does), and `GET /api/gh/stockpile/files` (the folder's listing). The web module gains
   "Save stockpile..." and "Open stockpile..." buttons and a description field. NES behaviour is untouched; its entries are `state` anchors in the new file.
2. **Console domains in the RTC tab:** `GET /api/gh/domains` (the machine's domains with true sizes, per console, and which are unavailable and why). `MainForm`
   builds the Corruptor's `MemoryDomains` from `MachineDomains.For(session)` when a session runs (NES keeps `BuildMemoryDomains` as is, defect 2 left for the lead's call:
   the true sizes are a one-line change but change the blast distribution users know).
3. **Blast a running session:** `RequestBlast`, `Let it rip`, and Corruption Slop apply through `CorruptionApplier` on the emulation thread between frames for SNES, GB, SMS, GG
   and Genesis. Stash / Keep / Replay for sessions use the anchor rules: **base state** = `state` anchor if `SupportsState`, else a `boot` anchor from the **input recorder**
   (a per-frame record of the pad handed to `RunFrame` in `RunSessionFrame`, poisoned by Reset / core swap / state load / battery import after start, capped), else the
   button says why it is unavailable ("this position cannot be stored: <reason>"). Replay = `Start` as in section 3.
4. **Game identity:** every entry written gets `game` and `machine` from the session (`GameId` is `sfc<sha1>` on the SNES and a bare sha1 elsewhere; the file stores the sha1).
5. After step 3, the Imagine and Slop modules are not extended (NES-only ML); they keep working on the NES.

Lite shares `GlitchHarvesterEngine`/`Corruptor`, so the v2 reader/writer reaches it through the engine; its file list gets `corruption/*.cs` (explicit `Compile Include`s).
Lite's console support for farming is out of scope (it keeps the NES flow).

---

## 6. Plugin side (C4, after `c4b` merges)

### 6.1 The flow the user sees

In the editor, next to **Inputs** and **Instrument Runaway** (bottom left), a **Corruption** button opens a modal window (the same pattern as the Inputs window):

1. **Open stockpile...** (file dialog, `*.json`, starts in the last folder). Errors are listed in the window (never silent): "not a stockpile", "version 3 is newer than this
   plugin reads", "entry 4 refused: 1,200,000 writes". The stockpile's name and path show.
2. **The list** of its entries: name, description (first line), a summary ("16 writes in ARAM, DSP"), the game (title and a mark: matches the loaded game / other game /
   other console), the anchor kind and its cost ("replays 4210 frames (~8 s)", "state"). Entries that cannot start now are dimmed with the reason on selection.
3. **Start** (selected entry): per anchor kind, on a worker thread, with the status line showing progress (`replaying 2100 / 4210 frames`) and a **Cancel**:
   - the loaded game must match `game.sha1` (otherwise the window offers *Start anyway* on a different game only with a warning line; the default is refuse);
   - `state`: load, then the writes; `boot`: new session, replay, writes; `none`: writes now;
   - the game then **runs** (the corruption plays out and you hear what it did) unless **"Freeze at once (Runaway)"** is ticked, which captures right after the writes:
     the deterministic way to take exactly the corrupted instruments;
   - failures show their reason in the window and on the picture area's status text, and leave the previous game running.
4. **Instrument Runaway** (the existing button) captures the machine's instruments as today. On the SNES that is `SpcSnapshot` of the **corrupted** ARAM and DSP, and the
   Instrument picker lists the corrupted samples (heuristic list as before; a corruption that breaks a sample header makes it drop out of the list, said in the
   window: "n of m instruments no longer parse").
5. **Remove corruption**: restart from the anchor without the writes (section 4.3), with the sentence that says so.

### 6.2 Recording what the plugin feeds the game (the lead's requirement 2)

`Emulator` keeps an **input log** from the moment a game starts: in `Produce`, next to `g.SetPad(PadBits)`, it appends `(frame, padBits)` **only when the bits change**, in a
pre-sized array (4096 transitions, no allocation on the audio thread); when it fills, or when a poisoning event happens (Reset starts a new log; sound-chip switch
that swaps the SNES sound unit, a state load, a different game), the log is marked unreproducible with a reason string. The log restarts with every new game. It gives
the plugin the means to write a `boot` anchor for the current position (and is what the project stores, below).

What the plugin **cannot** reproduce, and says: the input the user gave in a farm session before a stockpile entry was recorded (an entry with a `none` anchor); any
poisoned log; a position more than 36,000 frames from power-on.

### 6.3 Persistence through a saved FL project (agreed with c4b's section after its merge)

A new trailer section **`CRP1`** (tag + length like the others, skipped by readers that do not know it) written only when a corruption was started:

| Field | Why |
|---|---|
| stockpile path (reference, UTF-8), entry id, entry name, entry content hash | the user's link back; detects a changed stockpile |
| the game's sha1 and console | refuse to restore onto another game |
| **the applied entry**: its `machine`, its anchor **if `boot` or `none`**, its writes (<= 64 KB total) | the project does not depend on the stockpile file continuing to exist |
| **the plugin's input log since game start** and the frame count at save time, and the frame each write landed at | with the above, a complete **history** (`boot` anchor + events) of the machine the project was saved from |
| flags: running or frozen (Runaway), freeze-at-once | |

On reopening: if the machine has a **state** from c4b's section (a console with states), that state *is* the corrupted machine and nothing is re-applied; the `CRP1` data
only repopulates the window ("this project was started from entry X"). If it has none (the SNES until `snes-state` merges), the plugin **replays the history**: new game,
the log up to the frame, the writes at their frames; progress shown, cap 36,000 frames, and over the cap or with an unreproducible log it says
"the corrupted game cannot be rebuilt (<reason>); the instruments saved in Runaway (SPC1) are used instead" and falls back to what `SPC1` already keeps. In every case a
project saved **in Runaway** keeps the corrupted ARAM/DSP in `SPC1` (the existing mechanism, ~64 KB), so the corrupted *instruments* survive without any replay.

`state` anchors are **not** copied into the project (they can be hundreds of KB; the project already has the machine state).

### 6.4 Threading and the audio thread

Start/Stop run on a worker thread (like `StartLoad`) and hand the finished game to `pendingGame`; applying writes to a live game happens under `romLock` between frames
inside `Produce`'s caller, never on the editor thread. The stockpile reader runs on the editor thread (a file of a few KB to a few MB); a big one is read on a worker
with the window showing "reading...". Nothing here allocates on the audio thread: the input log is pre-sized, the applier takes a prebuilt array.

---

## 7. Library, CLI and files (C2)

New folder `Windows/NesEmulator/corruption/` (namespace `NesEmulator.Corruption`), **no UI, no `NES`-class dependency except the NES domain adapter**:

| File | Role |
|---|---|
| `StockpileModel.cs` | `Stockpile`, `StockpileEntry`, `Anchor` (+ kinds), `CorruptionWrite`, `GameRef`, `MachineSpec`, `ReadResult` (entries + warnings + refusals) |
| `StockpileJson.cs` | `Utf8JsonWriter` writer, `JsonDocument` reader with legacy v1 handling, limits, versioning |
| `MemoryDomains.cs` | `IMemoryDomain`, `MachineDomains.For(...)`, the per-console domain classes |
| `CorruptionApplier.cs` | apply / revert with journal; `BlastGenerator` with the seeded generator |
| `InputScript.cs` | the pad-per-frame run-length text (`"60:Start,90:Right+A,120:"`, the `--console-run --input` syntax) parse / format, and the recorder |
| `BootReplay.cs` | session factory + replay of a `boot` anchor with progress, cap and cancel; `AnchorStart` that dispatches state / boot / none |

Linked by: `Core/BrokenNes.Core.csproj` (the plugin), `Workshop/BrokenNes.Workshop.csproj`, `WebLite/BrokenNes.Lite.csproj`, the desktop by its default glob. The legacy
`corruptor/` stays; its Export/Import call the new reader/writer in C3.

Workshop: **`--corrupt-apply --rom X --stockpile S [--entry id|index|name] [--frames N] [--out dir]`**: loads the game, starts the entry per its anchor, runs N more
frames, and prints what changed: per domain the bytes that differ from an uncorrupted run of the same length (count, first addresses), the audio hash of both runs, the
picture hash of both, and for the SNES the ARAM and DSP hashes (what `SpcSnapshot` would hold). **`--corrupt-test`**: the unit checks below. Both are listed in
`Workshop/README.md`.

---

## 8. Test plan (every check shown able to fail)

| # | Check | How it is shown able to fail |
|---|---|---|
| T1 | Format round trip: write then read gives identical entries; golden hash of the canonical bytes for a fixed stockpile | change one field in the writer -> the hash differs |
| T2 | Legacy import: the engine export and the Lite export (fixtures built by the test in the old shapes) read to the same entries; the Lite one is flagged `no-state` | feed the Lite shape to a reader that only knows `State` -> the flag check fails |
| T3 | Hostile files: truncated, a version 99, a 2-million-write entry, bad base64, a missing base, an entry with an unknown domain: refused with a message each, the rest read | remove a limit -> the oversized entry is accepted and the test fails |
| T4 | Domains: for each console a tiny generated ROM, `Peek`/`Poke` round trip on each domain, sizes equal the arrays', `OAM` poke invalidates the sprite cache (SNES: a sprite visible only after the hook) | drop the hook -> the picture check fails |
| T5 | Apply / Revert: ROM undo is exact, RAM revert is documented as not rewinding; applying twice leaves the same bytes | skip the journal -> ROM bytes differ |
| T6 | `BlastGenerator`: a fixed seed gives a fixed golden writes list; TILT/BITFLIP/NOP values are the real ones (defect 3) | the old random-byte behaviour fails the TILT check |
| T7 | **Determinism of `boot`:** the same anchor replayed twice -> the same SPC700 ARAM hash, DSP hash and audio hash; **one frame of input changed -> a different ARAM/audio hash** | an input-ignoring replay passes the first and fails the second |
| T8 | **SNES ARAM corruption changes audio deterministically**, and the `SpcSnapshot` built from the corrupted machine differs from the clean one (ARAM hash, instrument list); run twice -> identical | a no-op write list gives identical hashes and the check fails |
| T9 | `state` anchors: Game Boy (and the Sega sessions) entry -> save state, corrupt, `Start` restores then corrupts: the machine after equals the machine after a direct apply to the same state. **For the SNES the same test runs guarded by `SupportsState`**: before `snes-state` merges it reports "not run: the SNES has no states yet" (visible, not a pass), after it runs for real | break `LoadState` -> hashes differ |
| T10 | Plugin (C4, FruityHost selftest): open a stockpile file, list, Start (real mouse messages through the editor), Runaway, the instrument picker shows the corrupted sample; a saved project reopens with the corruption and the playing instrument; messages appear for a wrong game, an unreadable file, a refused entry; 0 bytes allocated on the audio thread | disable the apply -> the sample hash check fails |
| T11 | Existing goldens identical: `--romtest` NES golden suite, UAT `cores`, plugin `selftest` 47 checks | the suites themselves |

---

## 9. Risks and decisions I need or have made

1. **Replay cost on the SNES.** Measured here: SMW headless 588 fps on a busy machine (3,600 frames in about 6 s), so 10 minutes of game is about 1 minute of replay in the Workshop;
   the plugin is slower (a worker thread, FL running). Cap 36,000 frames; progress and Cancel. *Decision made.*
2. **A `boot` anchor is only as good as the recorder.** Anything the recorder cannot see (a pad read by a path that does not go through `RunSessionFrame` / `Produce`) breaks
   it; T7 proves the plugin's and the Workshop's paths, and the desktop's recorder is tested against a headless replay of its own log (C3).
3. **Determinism of every core** is assumed and tested (T7) on the SNES. The Game Boy RTC and any time-based core are flagged. A core with hidden randomness shows up as T7 failing.
4. **NES domain sizes (defect 2)**: fixing it changes the blast distribution users know. Recommendation: fix it in C3 and say so; the library already uses true sizes. *Lead's call.*
5. **c4b overlap:** `Emulator.Rom.cs StartLoad`, `Bn2Plugin.SaveRestoreState`, `Emulator.Adopt` are the shared spots. I add `CRP1` and the input log only after c4b is merged, in additive hooks, and
   read its design first. Until then C4 is design only.
6. **Plugin AOT:** the library must stay reflection-free; `Utf8JsonWriter`/`JsonDocument` only. A Native AOT build and the selftest are part of C4's done.
7. **Privacy:** file names only (no paths) are written; the legacy NES state's `romPath` is dropped on rewrite.
8. **Not doing in v1:** corrupting chip registers directly on Sega chips; `at` > 0 timelines from the desktop; Imagine on non-NES; Lite console farming.

---

## 10. Separate proposal: a SNES save state (not built by me)

The lead has put `snes-state` on it. For the record of why it matters here and what it costs (my estimate from reading the cores):

- **Why:** instant exact Start with no replay cost; a project can keep the corrupted SNES machine; no dependence on recorded input. It is the one console without a state.
- **Scope:** `CPU_SFC` (registers, flags, mode, stack, wait state: ~20 fields), `BOARD_SFC` (128 KB WRAM, timing counters, NMI/IRQ latches, DMA/HDMA registers and
  running state, multiply/divide unit, joypad auto-read, open bus), `PPU_SFC` (VRAM 64 KB, CGRAM, OAM, ~80 registers and internal latches, Mode 7, windows, the per-line
  state, `InvalidateCaches()` on load), `SMP_SFC` + `APU_SFC` (SPC700 registers, ARAM 64 KB, ports, timers, IPL flag, DSP address), `DSP_SFC` (128 registers, 8 voices with
  their BRR buffers / envelope / counters, echo history, noise, global counter), the coprocessors (SA-1 with its RAM, Super FX with its cache, DSP-1 / NEC DSP), plus the
  `ConsoleStateFile` units (`apu`, `cpu`, `chip`, `format`). Frame-boundary only, as the other consoles.
- **Cost:** roughly 1,200 to 1,800 lines and a round-trip determinism suite (run N frames, save, run M, hash; load, run M, hash; equal), the same pattern as `MdStateTest`; a day or two of
  careful work. Size of a state: about 200 KB deflated.
- **What my code needs from it:** only that `SupportsState` becomes true and `SaveState`/`LoadState` honour the contract (frame boundary, refuses another game / unit). T9 then turns from "not run" to a real test with no change in the library.
