# UAT/n64: the N64 (cor64) gates

Drop-in for BrokenNes `UAT/n64/` (staged in cer64 `integration/brokennes/uat_n64`). Bash + python, as in cer64; run from Git Bash
(or `pwsh -File UAT\n64\gate.ps1`). Run one emulator at a time and only with more than 8 GB free (`wait_free.sh`).

## Setup on a PC
1. The cor64 tree at `third_party/cor64` (src/cor64, src/cor64.BareMetal, src/RunN64, src/SessionProbe, native/, LICENSE,
   THIRD-PARTY-NOTICES.md, .gitignore), or `Cor64Root` pointing at one. `native/prdp/build/Release/cor64_prdp.dll` must be there.
2. `cp UAT/n64/n64.local.json.example UAT/n64/n64.local.json` and edit (gitignored), or set the env vars instead:
   `N64_SM64_ROM`, `N64_ROM_DIR` (or `BROKENNES_N64_ROMS`), `N64_LIBRARY_DIR`, `N64_STATE_DIR`, `N64_KROM_DIR`, `N64_WORK`.
   Order: env var, `UAT/n64/n64.local.json`, `%BROKENNES_WORK%\n64\n64.local.json`, default (see `lib/n64env.sh`).
3. `UAT/n64/build.sh` builds `third_party/cor64/bin` (RunN64 + SessionProbe, default defines) and `bin_rt` (SessionProbe,
   Cor64FasterVi=false) and copies castle/title.state into both `st/`. About 20 s warm, 1-2 min cold.

## Gates
| script | what | pass line |
|---|---|---|
| `gate.sh ["ENV=1"]` (`KROM=1` adds krom, `BUILD=1` builds first) | SM64 boot hash, castle + title goldens, low-end castle | `GATE PASS` |
| `compat/tools/run_game.sh <id>` with `COMPAT_TWICE=1`, then `python -I compat/check_report.py <run dir> --twice` | one compat game, twice, determinism | `PASS` |
| `ab30.sh` | cold-boot hashes of the 40 games.tsv games vs `goldens/ab30_expected.tsv` | `AB30 PASS 40/40` |
| `catalog/run_one.sh "<archive>"` | one library smoke (map, not a gate) | `rc=0`, class by eye |

The full pass check for a core change: `gate.sh` with KROM=1, `gate.sh COR64_X64=0`, COMPAT_TWICE on sm64, oot, ge007, pd,
`ab30.sh`, and `UAT/battery.ps1 -Consoles n64`.

## Goldens (goldens/)
`boot.txt` (cold boot 2400 frames), `lowend_castle.txt`, `ref4_castle/` and `ref4_title/` (audio.wav + framehash.txt from RunN64),
`krom/` (krom's reference PNGs, public domain), `krom_expected_pass.txt` (54), `ab30_expected.tsv`.
Save states (`castle.state`, `title.state`, 12 MB each) hold SM64 memory, so they stay out of git like the ROMs: `N64_STATE_DIR`.
