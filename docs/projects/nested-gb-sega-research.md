# Nested GB -> Sega: Game Boy -> Game Gear and Game Boy -> Master System (research)

2026-11-14. Research and write-up only. No code was changed and nothing was downloaded. This sits beside `nested-everything-research.md` (SMS/GG -> Genesis) and `nested-fantasy-research.md` (Nested GB = GB -> SNES), and reuses the figures from those reports.

Tags used below:
- **[measured]**: read from BrokenNes's own cores (file named) or our own runs.
- **[sourced]**: from a page that was opened during this research (link given).
- **[estimate]**: reasoning or arithmetic that has not been tested yet.

## Summary

| Target | Feasibility (1-5) | Best-case games | Hardest blocker | Smallest proof of concept |
|---|---|---|---|---|
| **GB -> Game Gear** | **3** | DMG games that stay on one screen or scroll sideways, use the window only as a fixed status bar, and do their VRAM work in VBlank: Tetris, Dr. Mario, puzzle and card games, Kirby-style platformers | **Video, not the CPU.** GB VRAM writes go through plain memory stores (`LD (HL),A`), but the Sega VDP sits behind I/O ports, so every store that might reach VRAM has to be found and redirected. There are also smaller mismatches: sprites have no flip bits, there are 8 sprites per line instead of 10, the vertical map wraps at 224 instead of 256, and OBP1 sprites need duplicate tiles | `cpu_instrs` `06-ld r,r.gb`, then `01-special.gb`, recompiled and run on BrokenNes's GG core with results read from RAM. Then Tetris |
| **GB -> Master System** | **2** | The same games, letterboxed | All of the GG problems, plus the 160x144 picture sits inside a 256x192 screen. The VDP can only mask the left 8 columns, so the scrolled map shows past the GB edges. The SMS also has 2 buttons and a Pause NMI instead of Start/Select, and mono sound | Use the same build as the GG proof of concept with a different header and joypad stub, then fix the border |

**Verdict.** The CPU half is easier than any other Nested pairing studied so far:
- SM83 is a cut-down Z80, so most opcodes are byte-for-byte the same instruction.
- The Z80 runs most instructions in fewer T-states than the SM83 takes for the same instruction.
- The memory maps line up remarkably well. GB bank 0 / bank N / cart RAM / 8 KB WRAM at $C000 map onto Sega slot 0 / slot 1 / slot 2 RAM / 8 KB RAM at $C000.

The video side is the actual project. Build GB -> GG first, then add SMS as a variant on top of it.

Approach: use a Nested-style recompiler, not a pure in-place re-encode. It can still be far simpler than Nested itself, because ~80% of opcodes pass through unchanged.

---

## 1. CPU: SM83 onto Z80

### 1.1 Instruction fit [sourced: https://gbdev.io/pandocs/CPU_Comparison_with_Z80.html]

**Removed from the Z80 (the GB never uses them, so they cost nothing):**
- IX and IY (the DD/FD prefixes)
- every ED-prefixed instruction
- the alternate register set (`EXX`, `EX AF,AF'`)
- `IN`/`OUT`, `EX (SP),HL`, `EX DE,HL`
- the S and P/V flags, along with every PO/PE/P/M conditional

A translator therefore never needs IX, IY or the shadow registers, and they are free for the runtime. Our runtime can use `EXX` for fast trap stubs and IY as a base pointer.

**Same encoding and same meaning:**
- all `LD r,r'`, `LD r,n`, `LD rr,nn`
- 8-bit ALU on r, n and (HL); `INC`/`DEC` r, rr and (HL); `ADD HL,rr`
- `JP`, `JR`, `CALL`, `RET` with NZ/Z/NC/C; `RST`; `PUSH`/`POP`
- `HALT`, `DI`, `EI`, `DAA`, `CPL`, `SCF`, `CCF`
- `LD A,(BC)`/`(DE)` and `LD (BC)`/`(DE),A`
- the CB-prefix rotates, shifts, `BIT`, `RES` and `SET`, except row $3x

**Same byte, different instruction (each one must be rewritten):**

| Opcode | SM83 | Z80 meaning of the same byte | Z80 replacement |
|---|---|---|---|
| 08 | `LD (nn),SP` | `EX AF,AF'` | `LD (nn),SP` = ED 73 (4 bytes) |
| 10 | `STOP` | `DJNZ` | trap (wait for a button press) |
| 22 / 2A | `LD (HL+),A` / `LD A,(HL+)` | `LD (nn),HL` / `LD HL,(nn)` | `LD (HL),A : INC HL` (2 bytes) |
| 32 / 3A | `LD (HL-),A` / `LD A,(HL-)` | `LD (nn),A` / `LD A,(nn)` | `LD (HL),A : DEC HL` |
| D9 | `RETI` | `EXX` | `EI : RET` (on the GB, RETI sets IME immediately) |
| E0 / F0 | `LDH (n),A` / `LDH A,(n)` | `RET PO` / `RET P` | I/O trap, or `LD (FFnn),A` for HRAM |
| E2 / F2 | `LD (C),A` / `LD A,(C)` | `JP PO` / `JP P` | runtime helper |
| E8 | `ADD SP,e` | `RET PE` | helper |
| F8 | `LD HL,SP+e` | `RET M` | helper |
| EA / FA | `LD (nn),A` / `LD A,(nn)` | `JP PE` / `JP M` | Z80 32 / 3A (same length, 3 bytes) |
| CB 30-37 | `SWAP r` | `SLL` (undocumented) | 4x `RLCA` on A; via A for other registers |

**Flags.** The SM83 F register is `Z N H C 0 0 0 0`. The Z80 F register is `S Z Y H X P/V N C` [sourced: Pan Docs above for the removal of S and P/V; the bit layouts are standard].
- Z, H, N and C sit at different bit positions but mean the same thing.
- In particular, C is "borrow" after a subtract on both CPUs. Unlike Nested GB on the 65816 (see the sibling report), **no carry inversion is needed**.
- So conditional branches and `DAA` work unchanged.

Differences that matter:
- **`PUSH AF`/`POP AF`.** The stacked F byte differs. Games that compare it, or build F by hand, need the bits remapped in a helper [estimate: rare outside test ROMs; `cpu_instrs 01` does check it].
- **`RLCA`/`RRCA`/`RLA`/`RRA`.** These clear Z on the SM83; the Z80 leaves Z unchanged [standard behaviour, also modelled in `gb/CPU_GB.cs`, measured]. This only matters if a branch on Z follows. The recompiler can see that and add `OR A`-style fix-ups when needed.
- **`ADD SP,e` / `LD HL,SP+e`.** Their flags come from the low byte, so they need a helper anyway.

**Interrupt timing.**
- `EI` takes effect one instruction late on both CPUs [measured: `gb/CPU_GB.cs` `imePending`; the Z80 behaves the same in `sega/Z80/Z80.Interrupts.cs`].
- `RETI` on the GB re-enables at once [measured: `CPU_GB.cs` line 323, `Ime = true`]. The `EI : RET` replacement gives the same result.
- **Vectors differ.** The GB has five vectors ($40 VBlank, $48 STAT, $50 timer, $58 serial, $60 joypad). The Sega Z80 runs in IM 1, which has one vector at $0038, plus NMI at $0066 for SMS Pause.
  - The runtime owns $0038. It reads the VDP status, works out which GB source fired, and then jumps to the guest's `$40` or `$48` handler.
  - GB code in bank 0 at $0038-$0067 has to be relocated. That code is `RST $38`, and the start of the `RST`/vector table.
- **The GB timer ($FF04-$FF07) has no Sega equivalent.** DIV reads can be faked from the V counter. A timer *interrupt* has to come from the line counter [estimate: few DMG games depend on the timer interrupt; some sound drivers do].

### 1.2 Clock budget

- **GB:** 4,194,304 Hz, 70,224 clocks per frame (17,556 M-cycles), 59.73 fps [measured: `gb/BOARD_GB.cs`, `DmgFps = 4194304.0 / 70224`].
- **SMS/GG:** Z80 at 3,579,545 Hz, 228 cycles per line x 262 lines = **59,736 T-states per frame** [measured: `sega/SegaTiming.cs`].
- **Raw ratio: 0.85 Z80 T-states per GB clock.** Unlike every other Nested pairing, the host is *slower* than the guest.

Per-instruction cost is in the Z80's favour. The figures below are GB clocks against Z80 T-states for the same instruction [GB values measured from `gb/CPU_GB.cs` timings; Z80 values from the standard Zilog timings, also in `sega/Z80/Z80.Decode.cs`]:

| Instruction | GB clocks | Z80 T-states |
|---|---|---|
| `LD r,r` | 4 | 4 |
| `LD r,n` | 8 | 7 |
| `LD A,(HL)` | 8 | 7 |
| `INC rr` | 8 | 6 |
| `JP nn` | 16 | 10 |
| `CALL` | 24 | 17 |
| `RET` | 16 | 10 |
| `PUSH` | 16 | 11 |
| `POP` | 12 | 10 |
| `JR` taken | 12 | 12 |

What this means:
- Code that passes through unchanged runs in about **70-90% of the GB's clock count** [estimate: depends on the instruction mix].
- After the 0.85 ratio, that is roughly **break-even (0.85-1.05 of a frame)** before any traps [estimate].
- Trap overhead then adds cost. VRAM-candidate stores, `LDH` to I/O and `(HL+)` expansions push CPU-heavy games to about **1.2-1.6x a frame** [estimate].
- **Expect:** HALT-idle games (Tetris, puzzle games) at full speed, and busy action games lagging at about Nested's NES level. That is still *better* than the GB -> SNES estimate in the sibling report (5-7 host cycles per guest instruction there; here ~1-1.5x per instruction).

### 1.3 Static re-encode with traps, or a recompiler?

An in-place re-encode almost works but fails on instruction length:
- `LDH (n),A` is 2 bytes, and its Z80 replacement is 3.
- `LD (HL+),A` is 1 byte, and its replacement is 2.
- `LD (nn),SP` is 3 bytes, and its replacement is 4.
- Patching with `RST` traps (1 byte) would work for the 1-byte cases, but the Z80 has only 8 `RST` vectors, and they collide with the GB's own `RST` table.

So the code must be **relocated**. That means:
- a recompiler that rewrites each basic block;
- a GB-address -> host-address table for indirect jumps (`JP (HL)`, jump tables, `RST` dispatch);
- a fallback for code run from RAM (rare on DMG; OAM-DMA routines copied to HRAM are the usual case and are a known pattern to replace with a runtime routine).

This is the Nested design, but much lighter. Most instructions copy byte for byte, there are no flag conversions, and no register mapping is needed because the registers are the same. **Recommendation: a Nested-style static recompiler with a small runtime** [estimate].

### 1.4 Memory and banking

| GB | Sega mapper | Fit |
|---|---|---|
| $0000-$3FFF bank 0 | slot 0 (first 1 KB fixed) | direct (the recompiled bank 0 lives here) |
| $4000-$7FFF banked ROM | slot 1, register $FFFE | direct: GB bank N -> recompiled bank N |
| $8000-$9FFF VRAM | slot 2 cartridge RAM (16 KB, $FFFC bit 3) | **shadow VRAM**: guest stores land in RAM |
| $A000-$BFFF cart RAM | upper half of the same slot 2 RAM | direct, and battery-backable |
| $C000-$DFFF WRAM (8 KB) | $C000-$DFFF RAM (8 KB) | **exact** |
| $FE00 OAM, $FF00 I/O, $FF80 HRAM, $FFFF IE | $E000-$FFFF is a *mirror* of the same RAM, and $FFFC-$FFFF are the mapper registers | collision: HRAM aliases WRAM $DF80-$DFFF, and IE at $FFFF is the slot 2 register |

[Sega mapper layout measured: `sms/SmsCartridge.cs`. GB MBC1/2/3/5 measured: `gb/GbCartridge.cs`.]

How the collisions resolve:
- Every `LDH` is rewritten anyway, so HRAM and IE moves to a runtime-owned RAM area. This costs 128 bytes of WRAM: the guest's top-of-WRAM use has to be relocated, or WRAM shrinks.
- MBC writes (`LD ($2000),A` and similar) hit ROM on the Sega and do nothing. The recompiler turns constant-address writes into writes to $FFFE.
  - MBC1's upper bits and RAM-enable, MBC2's 4-bit RAM and MBC3/5's larger bank numbers all become runtime helpers.
  - MBC3's RTC needs its own helper. It can be faked from the frame count.
- Recompiled code grows by an estimated ~1.3x, so a GB bank may overflow its 16 KB. Overflow code goes into spare banks and is reached through far-call stubs. Sega mapper carts go up to 4 MB [sourced earlier, not re-opened here: https://www.smspower.org/Development/Mappers], which is enough for any MBC5 game.

---

## 2. Video

| GB DMG | SMS / GG (Mode 4) | Translation |
|---|---|---|
| 2bpp tiles, 16 bytes, two planes per row | 4bpp, 32 bytes, four planes interleaved per row [measured: `sms/SmsVdp.cs` notes] | **Trivial**: write the GB's 2 row bytes, then 2 zero bytes. No bit shuffle, unlike the SNES 4bpp case |
| 4 shades through BGP / OBP0 / OBP1 | GG: 32 x 12-bit CRAM. SMS: 32 x 6-bit `--BBGGRR` [measured: `SmsVdp.cs` `cram`] | BGP -> CRAM 0-3 (4 writes per BGP change). OBP0 -> sprite palette 16-19. OBP1 -> 20-23, which needs plane-2-set duplicate sprite tiles. Both palettes have plenty of depth for 4 greens or greys |
| 160x144 | GG LCD: 160x144 window into the 256x192 picture, at x = 48 | **Exact on GG** [measured: `sms/SmsSession.cs` line 258, `+ 48`]. On SMS it is centred in 256x192 with nothing to mask it (see 2.1) |
| BG map 32x32, wraps at 256 px | name table 32x28, wraps vertically at 224 [measured: `SmsVdp.cs`] | Games that scroll vertically through all 32 rows need row remapping when they write the map, and an SCY remap [estimate: most DMG games scroll horizontally or not at all] |
| Window layer (WX/WY, own map) | none | Line interrupt at the WY line, then swap the name-table base (register 2) and reset the scroll. Our VDP model re-reads register 2 on every line (`SmsVdp.Render.cs` line 88, measured), so a mid-frame swap takes effect. Real hardware does the same [estimate, to be confirmed against Mesen]. A side window (WX > 7 on lines that already show BG) cannot be done. A second name table costs 1.75 KB of tile space |
| 40 sprites, 10 per line, X/Y flip, behind-BG bit | 64 sprites, **8 per line** [measured: `SmsVdp.Render.cs` line 146, `found == 8`], **no flip bits**, priority is set per BG tile | Lines with 9 or 10 sprites drop sprites (or the runtime rotates them to flicker). Flipped sprites need pre-flipped tile copies. GB's OBJ-behind-BG becomes the SMS per-tile BG priority bit, which only approximates it |
| 8x16 sprites (LCDC bit 2) | 8x16 is register 1 bit 1 [sourced: https://www.smspower.org/Development/VDPRegisters] | direct |
| STAT/LYC interrupts, LY reads | line counter (register 10), V counter port $7E [sourced: VDPRegisters page; measured: `SmsVdp.cs` line counter] | LYC becomes a counter reload. Mode-0 HBlank STAT becomes counter = 0 (every line). LY = V counter - 24 on GG. GB's 154-line frame is mapped onto 262 lines, so LY during VBlank is faked |
| VRAM writes as memory stores, timed by mode 3 | port writes. Free during VBlank (70 lines x 228 = 15,960 T, measured) and slowed during the active picture | Guest stores go to shadow VRAM in slot 2 RAM. The runtime copies the dirty tiles and map in the Sega VBlank |
| OAM DMA ($FF46) | the sprite table in VRAM | The runtime converts 40 entries (Y, X, tile, attributes) into the SAT and the X/tile table, picks the OBP1 and flip tile variants, and applies the 8-per-line cut [estimate: ~3-5k T per frame] |

### 2.1 The hard parts, in order

1. **Dirty tracking.** Shadow VRAM is cheap to write to, but scanning 8 KB for changes every frame costs more than the whole frame [estimate: ~8k bytes x ~20 T]. So the recompiler must mark the stores that can reach $8000-$9FFF:
   - When HL comes from an immediate value in the same block, it can prove the target.
   - Otherwise the store goes through a `LD (HL),A` helper that sets a dirty bit when the high byte is $80-$9F (about 25-40 T per store [estimate]).
   - This is the same kind of fix as Nested's `(zp),Y` I/O stores.
2. **Tile budget.** SMS VRAM is 16 KB = 512 tile slots. Take away a name table (1.75 KB) and the SAT, and about **448 tiles** are left [estimate]. A second name table for the window leaves about 392.
   - The GB has 384 tiles.
   - OBP1 duplicates and flipped duplicates of sprite tiles must fit in what is left.
   - So a game with many sprite tiles, all of them flipped and in both palettes, will not fit. Most DMG games use far fewer distinct flipped and OBP1 sprite tiles; the runtime allocates the duplicates on demand.
3. **The SMS border.** On SMS the 256x192 picture shows map columns beyond the GB's 160 px:
   - Lines above and below the 144 can be blanked with line interrupts (register 1 bit 6 off).
   - Horizontally, the VDP can only mask the left 8 columns (register 0 bit 5).
   - The options are: let the extra map show; build the playfield 256 wide (which breaks GB scroll semantics); or use sprites as a curtain, which eats the 8-per-line limit.
   - This is why SMS rates lower than GG.

---

## 3. Sound

**GB APU:** two pulse channels (12.5/25/50/75% duty, with a sweep on CH1), a wave channel (32 4-bit samples), and noise (15/7-bit LFSR). Envelopes are clocked at 64 Hz, sweep at 128 Hz and length at 256 Hz [sourced: https://gbdev.io/pandocs/Audio_details.html].

**SN76489 (SMS/GG PSG):** three square channels that are 50% duty only, 10-bit period, lowest note about 109 Hz. Noise has white or periodic modes, with 3 fixed rates or tone 2's rate. Volume has 16 steps of 2 dB [sourced: https://www.smspower.org/Development/SN76489].

| GB | SN76489 | What is lost |
|---|---|---|
| Pulse 1 / 2 | tone 0 / 1 | **Duty timbre** (everything becomes 50%). Notes below about 109 Hz must jump up an octave. Hardware sweep and envelope become software ticks in the 60 Hz VBlank (64/128 Hz are close enough) |
| Wave | tone 2 | **The whole wave timbre.** Bass lines and custom waves become squares, and wave-RAM "sample" tricks are gone. Tone 2 is also the only way to get a tunable noise pitch |
| Noise | noise | The GB's many LFSR clock rates map onto 4 SN rates. Tuned noise drums cost tone 2 (so the wave channel goes silent). GB 7-bit short mode is only approximately SN periodic noise |
| NR50 / NR51 stereo | **GG has a stereo register at port $06** | GG keeps GB panning. SMS is mono |
| Linear 4-bit volume | 2 dB-step attenuation | A 16-entry lookup table |

- All NRxx writes go through `LDH`, so each one is a trap that writes the PSG directly. A runtime ticker does the envelopes, sweep and length counters [estimate: ~1-2k T per frame].
- **SMS FM unit (YM2413)** [measured: we have `sega/SmsFmSound*.cs` cores]. It is Japan-only, and its 15 fixed patches plus 1 custom patch do not produce a GB pulse or wave sound better than the PSG does. It is not worth it, except as an "art" option. The GG has no FM.

**Overall:** music stays recognisable. Wave-channel basslines and duty-change effects suffer most [estimate]. If the user wants them, the BrokenNes "foreign APU" machinery can render the GB APU into the ROM's audio test hooks for an A/B listen.

---

## 4. Prior art

- **No Game Boy emulator or translator running on Sega 8-bit or MSX hardware was found.** A web search for GB on Game Gear / Master System / MSX Z80 homebrew returned only host-side emulators:
  - Emulicious, which emulates GB, SMS, GG and MSX on a PC
  - Gearsystem: https://github.com/drhelius/Gearsystem
  - jgenesis: https://github.com/jsgroth/jgenesis
  - crater: https://git.benkurtovic.com/ben/crater
  - fMSX / MasterGear (Marat Fayzullin's shared Z80 core): https://fms.komkon.org
  [sourced, but none of them is prior art for the translation]
- **Static SM83 recompilers for PC** (gb-recompiled, jitboy) are listed with links in `nested-fantasy-research.md` §1. They show that SM83 control-flow recovery and an interpreter fallback work at catalogue scale [sourced there].
- **SM83 <-> Z80 relationship:** Pan Docs' comparison page (§1.1) is the definitive opcode diff [sourced].
- **The reverse direction exists commercially:** Sega ported many GG games from or to the GB by hand. No automated tool is known [estimate, not checked].

So this would be first-of-its-kind as an automatic pipeline. Its closest relative is our own Nested.

---

## 5. Verdicts and how it would sit in BrokenDev

### GB -> Game Gear: 3 out of 5
- **Strengths:** CPU near-identity, a pixel-exact screen, stereo, a Start button, the memory map fits, and tile conversion is trivial.
- **Hardest blocker:** the shadow-VRAM dirty tracking and the sprite translation (flips, OBP1, 8 per line).
- **Best-case games:** Tetris, Dr. Mario, Alleyway, puzzle and card games, horizontally scrolling platformers with a bottom-window HUD.
- **Worst-case games:** vertical map wrappers, side windows, games that write VRAM in mid-frame raster tricks, and games built on the wave channel or the timer interrupt.

### GB -> Master System: 2 out of 5
- Everything above, plus the 256-wide border problem, mono sound and Pause-as-NMI. Select needs a 2nd-button combo.
- Best built as a "GG build + SMS shell" variant once GG works.

### Smallest proofs of concept (in order)
1. **`cpu_instrs` `06-ld r,r.gb`.** Recompile it to a GG ROM and run it headless on BrokenNes's GG core through the MCP session. Read the pass/fail from WRAM: blargg tests also print to the screen, so the tile translation is a bonus check.
   - This proves the pass-through encoding, the `LDH` trap and the bank layout.
   - It also gives a **measured** frame-time ratio (T-states used against GB clocks).
2. `01-special.gb` (`DAA`, `PUSH AF`/`POP AF` flag remap), then `02-interrupts.gb` (the vector dispatcher), then the other `individual/` sub-tests.
   On disk: `Windows\Resources\gb-test-roms\blargg\cpu_instrs\individual\`.
3. **Tetris**: no MBC, 32 KB, HALT-idle, a fixed BG with a few sprites. This exercises OAM DMA, BGP and the PSG trap.
4. **`dmg-acid2.gb`** (`Windows\Resources\gb-test-roms\dmg-acid2\`) for the window, flips and priority. Expect partial results; the flip and priority gaps will show up here.
5. **`VDPTEST.sms`** (`C:\BrokenNes-tools\sega\test-sets\sms-roms\smsvdptest-1.31\`) is useful only as a check that the runtime's mid-frame register 2 swap behaves on real-VDP models (and on Mesen).

### Place in BrokenDev "Nested Everything"
- A pipeline with **source = GB (DMG) ROM** and **target = GG or SMS**. It would be configured by:
  - target (GG/SMS)
  - sprite-overflow policy (drop or flicker)
  - window strategy (name-table swap, or off)
  - wave-channel policy (tone 2 or mute)
  - SMS border strategy
  - a per-game overrides file: the code/data map, RAM-executed routines, and vertical-wrap remap
- **Output:** a `.gg` / `.sms` ROM plus a report (traps hit, uncovered indirect jumps, tile-budget peak), and a lag measurement taken by running the ROM on BrokenNes headless (the same loop as `nested-build.ps1 -Feedback brokennes`).
- **Shared parts:**
  - GB control-flow recovery and banking analysis, shared with Nested GB (GB -> SNES).
  - The Sega runtime pieces (VBlank upload queue, line-interrupt dispatcher), shared with the GB -> GG -> SMS shells.
  - Building the GB front end once serves both the SNES and the Sega targets.
- **Ranking beside the sibling ideas:** GB -> GG probably reaches "Tetris plays" sooner than GB -> SNES, because there is no CPU speed wall and the tiles are trivial. GB -> SNES has the better picture (a window layer, flip bits, 2bpp-native tiles).

## Files the user would need to approve

None needed for the first proof of concept: the test ROMs are already on disk. Tetris would need the user's own DMG Tetris dump; no download is proposed.

## Sources (opened)
- Pan Docs, CPU comparison with Z80: https://gbdev.io/pandocs/CPU_Comparison_with_Z80.html
- Pan Docs, audio details: https://gbdev.io/pandocs/Audio_details.html
- SMS Power, SN76489: https://www.smspower.org/Development/SN76489
- SMS Power, VDP registers: https://www.smspower.org/Development/VDPRegisters
- Prior-art search results: https://github.com/drhelius/Gearsystem, https://github.com/jsgroth/jgenesis, https://git.benkurtovic.com/ben/crater, https://fms.komkon.org
- Cited from the sibling reports, not re-opened: SMS Power Mappers page, gb-recompiled, jitboy
