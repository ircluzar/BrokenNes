# VRUN corruption oracle against BrokenNes: what the CLEAN verdicts are worth

Date: 2026-09-04. ROM: `game.nes`, sha256 `1b18f3bd…df53` (mapper 30, the Desktop build).
Tool: `Workshop --corrupt`, the port of the VRUN project's own `tools/corruption_detector.lua`.
Cores: CPU_FIX / PPU_FIX / APU_FIX throughout. Release build.

## 1. The three checks have each been SEEN to fail

The Lua's own argument is that a checker nobody has watched fail proves nothing. All three now
fail on demand via `--inject`, and pass on the untouched ROM:

| drive | check exercised | result |
|---|---|---|
| `--inject none` | all three | CLEAN |
| `--inject scroll` | late PPU write | **FAIL** - 2 `$2005` writes outside vblank while rendering |
| `--inject nt` | nametable floor writes | **FAIL** - 1 non-floor tile on a non-solid cell |
| `--inject chr --allowed-tiles …` | CHR scatter | **FAIL** - `frame 124: CHR tile 16 changed (not in the allowlist)` |

`--inject chr` does **not** fail without `--allowed-tiles`: with no allowlist the CHR check is
INFO-only by design and cannot fail. It still saw the injection (13 changing tiles instead of 12).
The allowlist used is the 12 tiles observed changing on this ROM: `8,9,14,15,24,25,30,31,104,105,120,121`.
Those form exact 2x2 metatile quads, but they were derived by observation rather than from
`map_data.fab` (which is generated and absent from the source tree), so a room this route never
enters could still hold a 13th legitimately-animated tile. Re-derive with `--learn` over a wider
route before treating the allowlist as authoritative.

## 2. The ntsc-off roam failure is a harness artifact, not a shipping-config defect

An earlier pass found `--input roam --ntsc-frame-timing off` failing check 3 deterministically:

    frame 159: tile $6c written on non-solid cell (4,10) flags=0

and reported it as a failure appearing only in the timing configuration the app actually ships.
It does not survive controlled comparison.

**Non-perturbed drives are clean in both configurations.** `combat` (the Lua's own drive) and
`idle`, 1800 frames each, are CLEAN with `--ntsc-frame-timing` both on and off. Only `roam` fails,
and `roam` pokes `player_x`/`player_y` to force room transitions - which the tool's own help warns
makes it a perturbed run whose failures must be reproduced with a scripted drive before belief.

**The failure does not track the timing flag.** Shifting the boot phase by 10 frames removes it
while leaving `--ntsc-frame-timing off` in place:

| boot input | ntsc=on | ntsc=off |
|---|---|---|
| `60:Start,66:` | CLEAN | **CORRUPTION** (frame 159, tile `$6c`, cell 4,10) |
| `70:Start,76:` | CLEAN | CLEAN |
| `90:Start,96:` | CLEAN | CLEAN |

One of six configurations. The two roam runs also take visibly different routes between the two
timing settings (room transitions at 530/615/699… vs 706/790/875…), so they were never the same
run being compared. Conclusion: a single fragile coincidence in a position-poked run, not a defect
in the shipped timing. Not believed, per the detector's own doctrine.

## 3. Standing coverage gap

`combat` and `idle` never leave the spawn room on this ROM, so a green run in either mode has
observed **zero room transitions** - and step_room_load is where the Lua's author found the one
real bug this detector has ever caught. `roam` reaches transitions only by poking. A scripted
`--input script:…` route through several rooms is the missing piece; until it exists, the clean
verdicts cover the spawn room plus whatever `roam` perturbs its way into.
