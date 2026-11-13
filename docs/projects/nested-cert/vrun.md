# Nested certification: VRUN (CTW) on mapper 30

Measured 2026-11-12. Measure only: nothing in the Nested core was changed.

| | |
|---|---|
| SNES ROM | `T:\BrokenDev\Releases\VRUN_Nested_SNES\VRUN_Nested_SNES_2026-11-07_speed.smc` (sha256 b58a1fd5..., superseded 2026-11-13 by `..._2026-11-13_feedback-byfix.smc`, sha256 7665a9c8..., verify play lag 39 -> 0), rebuilt **byte-identical** today by `brokendev.ps1 nested test` (core from source, md5 57c4a845, injector built from source on .NET 10) |
| NES ROM | the release's `.source.nes` (sha256 8e3caa40... = `VRUN_Nes_Dev\sys0\simulations\VRUN\build\vrun_game_nested.nes`, symbols `vrun_game_nested.mlb`) |
| NES reference | BrokenNes CPU_FIX / PPU_FIX / APU_FIX, power-on RAM zeros (Mesen's NES setting in the private copy) |
| SNES | BrokenNes SNES core (BOARD_SFC, default APU) |
| Oracle | Mesen 2.1.1, private copy (scratchpad), settings untouched (NES + SNES RAM all zeros) |

## Verdict: **Certified** (game logic exact), with timing residuals

Nested runs VRUN's NES code exactly: in the long explorations, once the boot phase is aligned, all 2 KB of NES RAM are
byte-identical at every controller poll for 5350 of 5515 polls (ex0) and 5368 of 5537 (ex1). The only bytes that never
match are `snes_ext` / `raw_snes_ext`, which the game sets on purpose when it runs on the SNES. Every difference left is
a **timing** difference (boot phase, region detection, lag frames). Mesen shows the same ones on the same polls, so none
of them comes from BrokenNes's SNES core. Gameplay is not frame-identical to the NES. The NES and SNES runs pick
different random maps because VRUN seeds its map from how long the title has run, and the SNES title runs one step ahead.
The verdict is Certified, not Jank, because none of this is wrong game behaviour. It is timing jitter that a player
cannot tell apart from a normal run.

## Method

`Workshop --nested-cert` (new, this branch) runs both ROMs headless and **locks them on the game's controller polls**, not on frames:

- one NES poll = a CPU write to `$4016` with bit 0 clear;
- one SNES poll = a SNES `$4016` write with bit 0 set. Project Nested turns each NES `$4016=0` write into exactly that
  strobe (`IO__w4016_CopyStart`).

The NES run records the buttons held at each poll, and the SNES run gets the same buttons at the same poll. Both sides
therefore see identical input per game step, whatever their frame timing. The NES RAM is sampled at each poll. Nested
keeps the NES RAM at SNES WRAM `$7E:0000`, which the matching bytes confirm. The tool then compares the RAM (stack page
`$0100-$01FF` ignored), the lag on each side and the drift.

- **Lag**: a frame after the first poll that ends with no poll.
- **`--sync`**: copies the named bytes NES->SNES at every poll. It is used only to take a known timing difference out of
  the comparison, and every copy it makes is counted in the report.
- **Mesen**: `BrokenDev/tools/nested-cert-mesen.ps1` runs `mesen-poll-lockstep.lua` with the same per-poll buttons.
- **Analysis**: `nested-cert-analyze.py` names the differences from the `.mlb`, and `nested-cert-compare.py` compares any
  two streams.

Input mapping: none of the VRUN profiles set `Input.Enabled`, so the NES code reads the SNES serial bits directly:
**NES A = SNES B, NES B = SNES Y, SNES A/X/L/R are never seen**. The lockstep presses B/Y. Note: the verify scripts and
feedback plans of `vrun-nested` / `smb1-nested` press SNES "A" as if it were NES A, and the game ignores those presses
(harness finding, not a core one).

## Cross-check with Mesen (play scenario, 2991 polls)

| pair | differing polls | lag after first poll |
|---|---|---|
| BrokenNes NES FIX vs Mesen NES | **0** (byte-identical, every poll) | 0 / 0 |
| BrokenNes SNES vs Mesen SNES | **2** (polls 107-108, NESFab scratch `$1B-$20` only) | 9 / 9, on the same frames (207-208, 689-690, 1610-1615) |
| Mesen NES vs Mesen SNES | 2991, the same set of differences as BrokenNes NES vs SNES (`runtime_system` 00/03, `map_slot` 00/01, ...) | 0 / 9 |

Both emulators therefore agree on every NES-vs-SNES difference below. These differences are properties of the Nested
conversion, not emulator errors.

## Scenarios (NES frames; SNES lag = frames after the first poll with no poll)

| scenario | polls | SNES lag | boot offset / end drift (SNES frames) | RAM after boot alignment (`--sync 000A,000F,0396`) |
|---|---|---|---|---|
| play (vrun-nes verify input, 3000 f) | 2991 | 9 (natural) / 25 (synced map) | 87 / 93 | equal for polls 165-595, then split at the first room load (SNES lag 688x8) |
| ex0 (exploration plan, 5520 f) | 5515 | 2 | 87 / 94 | **byte-identical from poll 165 to the end (5350 polls)** |
| ex1 (exploration plan, 5542 f) | 5537 | 2 | 87 / 94 | **byte-identical from poll 169 to the end (5368 polls)** |
| save (pause + Select, 779 f) | 766 | 4 | 87 / 86 | `run_timer_subsec` and `hud_starve_count` split at the save/title return (time-budgeted code), the rest settles |

NES lag is 0 in every scenario.

## Every difference

1. **`runtime_system` (`$000F`) = 3 on the SNES, 0 on the NES**, from the first poll. This is NESFab's runtime region
   detection. It times the CPU between NMIs, and under Nested the count matches no real console, so it returns 3
   (unknown). Mesen agrees.
   *Likely cause in the core:* Nested delivers the NES NMI on its own schedule (at the game's idle loop) and runs
   recompiled code, so cycle-counting loops measure SNES time.
   *Effect on VRUN:* none found. VRUN does not read it.
2. **Boot phase: the SNES is one main-loop step ahead at the first poll.** `map_slot` reads 01 against 00 at poll 0, the
   title music is one tick ahead (`mus_*_wait`, polls 1-106), and `runtime_nmi_ready` reads 1 against 0. The cause is the
   same as item 1: the first NMI/idle hand-off runs one extra iteration before the first `$4016` write. Mesen agrees.
   *Effect:* VRUN rotates `map_slot` once per title frame to seed the run's map, so the SNES picks the **next map slot**
   for the same input timing. This one difference is why the natural NES and SNES runs play different maps (`current_room`
   and the doors differ from poll 109). After the title the music re-aligns at the first track change.
3. **Lag frames on the SNES**: 2 in ex0/ex1 (the title, just before Start), 4 in save, 9 in play (title, first room
   load, a room transition). With the map forced to the NES's slot, play has 25 (688x8 at the first room, ...): that map
   reaches code paths the feedback plans never ran.
   *Likely cause:* recompiles at run time on paths the 68 feedback entries do not cover, plus the slow paths around room
   loads.
   *Effect:* VRUN's frame-budget code (`runtime_nmi_ready`, `hud_starve_count`, the door/HUD queues) takes the other
   branch on a lag frame. That makes NESFab's `rand()` consumption, the hazards (`haz_x/y`, `rng_state` from poll 598 in
   play) and then `player_hp` differ. It is legitimate game behaviour under lag, not a CPU error.
4. **NMI landing state** (`runtime_nmi_saved_x/y/bank`, NESFab temporaries `$01-$40`): the NMI interrupts a different
   instruction. These differ while the timing differs and match again when it re-aligns. They are expected and have no
   effect.
5. **`snes_ext` / `raw_snes_ext` (`$0424-$0427`)**: by design, the VRUN `--nested` build detects the SNES extension.
6. **Save scenario**: `run_timer_subsec` stays different after the save/return to title. It is time-budgeted state, like
   item 3. The save contents themselves (NES flash sector vs the SNES `.srm`) were **not** compared in this round.

## What an MMC3 build would hit

The MMC3 build (`vrun_game_mmc3.nes`, 512 KB PRG + 256 KB CHR-ROM) is not buildable with Nested yet. The measurements
come from `Project-Nested-core/Documentation/NestedFork-MMC3-findings.md` plus today's numbers:

- **Items 1-5 above apply unchanged.** They are mapper-independent.
- **Title tunnel**: 6 IRQ splits per frame with an R0 CHR rewrite each, i.e. up to 7 background banks in one frame,
  against Nested's 3 resident CHR sets per frame. IRQs are delivered on a virtual scanline (no A12 counter) at idle or on
  a scroll write. Expect a wrong title picture first.
- **The I flag is ignored** by Nested's MMC3 IRQ delivery. VRUN arms the IRQ on the title only, so this is low risk for
  VRUN.
- **Step 17 (the atomic PRG-slot write) is missing for Mapper4.** An NMI landing between the R6/R7 store halves can run
  with a torn bank. The lag-shifted NMI landings (item 4) make this more likely on the SNES than on the NES.
- `$A001` is not implemented (harmless for VRUN), and CHR animation (R0 every 5 frames) fits the 3-set model.
- Item 3 (lag) gets worse: the MMC3 build's far-call trampolines are its known NES-side cost.

## Reproduce

```
Workshop --nested-cert --nes <release>.source.nes --snes <release>.smc --frames 3000 --power-on-ram zeros \
  --input "120:Start,132:,400:Right,600:Right+B,700:Right,900:Up,1000:Right+B,1200:Down,1300:Right+A,1500:Left+B,1800:Right" \
  --out-dir <dir> --raw [--sync 000A,000F,0396]
python BrokenDev/tools/nested-cert-analyze.py <dir> <stem> --mlb vrun_game_nested.mlb
BrokenDev/tools/nested-cert-mesen.ps1 -Rom <rom> -System nes|snes -Polls <dir>/<stem>.nested-cert.polls.csv -MesenDir <private Mesen copy> -Out <dir> -Tag t
python BrokenDev/tools/nested-cert-compare.py <a.ram> <b.ram> --mlb ...
```

The ex0/ex1/save inputs are the `vrun-nested` feedback plans moved to NES frames (-121), with SNES B->NES A and
SNES Y->NES B; SNES A presses are dropped.

## 2026-11-12 round 2: fixed-time NMI (`-Nmi accurate`) and the B/Y input fix

**What changed.** The core is untouched (step 18, md5 57c4a845; the golden tests still hold). Project Nested already ships
the fixed-time mode as an injector setting: `NmiMode.AtVblank` on + `NmiMode.AutoDetect` off delivers the NES NMI at every
SNES vblank instead of at the game's idle loop. `nested-build.ps1 -Nmi accurate|fast` (and `brokendev.ps1 nested build
-Nmi ...`) writes those two settings per game; `fast` (default) writes nothing, so earlier builds stay byte-identical. No
NESTED_STEP 19 was needed. The vrun-nested verify/oracle scripts and feedback plans now press SNES B/Y for NES A/B.

Builds (feedback re-run with the fixed plans, base profile 0192edb1, `-Post flash`), in
`T:\BrokenDev\Releases\VRUN_Nested_SNES\`: `2026-11-12_aa968b88_nmi-fast` and `2026-11-12_b39df809_nmi-accurate`.

| scenario (NES inputs with real A presses now) | SNES lag fast -> accurate | boot offset / end drift fast -> accurate |
|---|---|---|
| play (3000 f) | 2 -> 1 | 87/90 -> 89/95 |
| ex0 | 2 -> 1 | 87/94 -> 89/99 |
| ex1 | 2 -> 0 | 87/94 -> 89/98 |
| save | 4 -> 2 | 87/86 -> 89/92 |

- **Boot step: NOT fixed.** `map_slot` is still one ahead at poll 0 and `runtime_system` still reads 3 in both modes
  (`runtime_nmi_counter` is equal at every poll in both). The extra title step does not come from where the NMI lands; it
  comes from NESFab's boot (region detection counting CPU time between NMIs, which measures SNES time either way).
- **Cross-check:** in accurate mode BrokenNes SNES and Mesen SNES no longer agree byte-for-byte (ex0: 5403 of 5515 polls
  differ, from poll 108; in fast mode they agreed except scratch bytes). An NMI at a fixed SNES time interrupts the NES code
  at a cycle-exact point, so the two SNES emulators' small timing differences now reach game state. Neither is "the NES".
- With the corrected input (NES A presses now reach the game), the long explorations no longer stay byte-identical after
  boot alignment in either mode (game state $200-$7FF differs on 5191/5515 polls fast, 5515 accurate): the A presses take
  the run into lag-sensitive paths. This needs its own look before VRUN's verdict is re-stated; Certified stays for now.

**Recommendation for VRUN: fast** (no visible gain from accurate, and fast keeps the two emulators in agreement).
