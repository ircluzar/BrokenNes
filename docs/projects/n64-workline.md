# N64 workline in BrokenNes after the merge

This is the handoff from the cer64 climb to the N64 workline inside BrokenNes. In BrokenNes this file becomes `docs/projects/n64-workline.md`.

- **cer64 main at merge: 2fa17d7** (cer64plus/cor64 main; staging 21a83a3 + .gitattributes). From that sha the cer64 repo freezes read-only: all later N64 work happens in BrokenNes.
- Gates: `UAT/n64/` (staged as `integration/brokennes/uat_n64`). Fleet job specs: `fleet_jobs.json` / `FLEET_JOBS.md`, one entry per task below.
 Each task below stands alone and can run as one fleet job. Its pass check is the gates.

## Ground rules that travel with the core

- **SM64 stays bit-exact.** The goldens are the cold-boot hash `video=76B3F191B5360CB9 audio=E9A7D2766854D844`, the castle and title frame and audio goldens, and the low-end castle `video=880ADEAAAEEF93C3`.
- **The pass check for every change:**
  - both gates (`KROM=1 UAT/n64/gate.sh` and `UAT/n64/gate.sh COR64_X64=0`)
  - COMPAT_TWICE on sm64, oot, ge007 and pd with exceptions=0
  - the 40-game ab30 hash run (`UAT/n64/ab30.sh`)
  - in BrokenNes, also `UAT/battery.ps1 -Consoles n64`
- **Flags:** every behaviour change goes behind a `COR64_*` flag, and per-game settings go in rows of `Session/GameDb.cs`.
- **Speed claims:** a gain must clear about 1.5% in interleaved A/Bs on a quiet machine. Benchmarks on a loaded host are not valid.
- **Licensing:** no GPL ports. ares, cen64, parallel-rdp and libdragon may be adapted; mupen64plus, simple64 and Project64 are behaviour-only references.
- **Running cer64 as part of BrokenNes:** after the merge the cer64 repo is read-only (frozen at the merge sha); cor64 lives at `third_party/cor64` and changes land there, gated by `UAT/n64`.

## Tasks (independent; good fleet jobs)

### Speed: the five games under 150%, and SM64 to 300%

1. **CPU block linking or larger blocks in the x64 emitter.** This is the shared limit for Racer (~91%), ASB99 (~131%), Aidyn (~133–150%), South Park Rally (~137%), Gauntlet (~140%), and SM64 (~239% toward 300%). Block entry and call/return overhead is now the largest cost.
2. **Finish speed_cat19 (`COR64_SPDCAT19_RAS`, return-address cache, branch climb4/speed_cat19 881beda).** Verify the stale-link crash fix with COMPAT_TWICE on sm64, then run a clean A/B.
3. **A/B speed_cat18 (`COR64_SPDCAT18_TWAIT`, ASB99 frame-limiter skip, branch climb4/speed_cat18 67a1281).** It is opt-in now; enable it per game only if the A/B wins.
4. **Re-bench the library on a quiet fleet machine** and refresh `findings/catalog/catalog_page.csv` (fps column = speed %).

### Accuracy

5. **Dezaemon 3D, black playfield.** The display list sends almost no triangles. Identify microcode `DC3C65D7C985B290` and check culling. Tools: `COR64_ACC2_RDPLOG=1`.
6. **Robotech: Crystal Dreams, audio dies around frame 6300–7100.** The synth produces all-zero buffers. Tools: `COR64_ACC2_AILOG=1`.
7. **Battlezone:** add a nav past the "Insert Rumble Pak" prompt and check gameplay audio.
8. **Software RDP garbles menu textures** (seen in Dezaemon). Low priority; parallel-rdp is the default.

### Coverage

9. **Certify more games with savestate test lists.** There are 30 certified today. Agents play the games (cheats OK), save states and add checks.
10. **Fix well-known per-game quirks** (polish bar), for example the MK64 jumbotron.

## Fleet: parallel tests for N64 agents (agent tier)

The 10 fleet PCs run N64 jobs on a prebuilt kit (`C:\brokennes\n64` on each PC: Git bash, 7z, `UAT/n64` + the cor64 `bin`/`bin_rt`
that `C:\BrokenNes-work\n64\n64_provision.py` last pushed, the SM64 ROM + states, the 40 compat ROMs, krom). There is no SDK on the PCs:
for a code change, build here (`bash UAT/n64/build.sh`), point `SRC` in `n64_provision.py` at your checkout, re-run it, then submit.

Tests an agent is waiting on are **agent tier**: the keeper starts them within about a minute and takes PCs from sweeps (sweeps stop
taking new batches there; running sweep jobs finish, sweep batches are short). Submit with the script, never by editing
`keeper_backlog.json` (run from `C:\BrokenNes-work\fleet`):

```
# one job per item, each on its own PC, all at once; heavy = only on a PC with no other fleet job (use it for speed A/B)
python -I submit.py n64 ab_myflag --spec C:\path\items.json
#   items.json: [{"id": "on",  "cmds": ["COR64_MYFLAG=1 bash UAT/n64/ab30.sh"], "heavy": true, "wall": 60},
#                {"id": "off", "cmds": ["COR64_MYFLAG=0 bash UAT/n64/ab30.sh"], "heavy": true, "wall": 60}]
# COMPAT_TWICE batch across games (one game per PC):
#   [{"id": "sm64", "cmds": ["d=$(COMPAT_TWICE=1 bash UAT/n64/compat/tools/run_game.sh sm64 | tail -1); python -I UAT/n64/compat/check_report.py \"$d\" --twice"]}, ...]
# the same lines N times (repeats for noise):
python -I submit.py n64 ab_rep --cmd "COR64_MYFLAG=1 bash UAT/n64/ab30.sh" --repeat 5 --heavy
python -I submit.py status ab_myflag        # keeper status, items done, each result's last log lines
```

- Lines run in bash in `C:\brokennes\n64\repo` with `N64_WORK` = that job's own folder. An item's `"lib": ["Game.7z", ...]` copies those
  library archives with the job (`N64_LIBRARY_DIR` points at them), so `catalog/run_one.sh` works on any PC, not only STONE.
- Results come back to `C:\BrokenNes-work\n64\fleet\<name>\<item id>\<PC>\` (`job.log` + the work folder).
  `python -I C:\BrokenNes-work\n64\fleet\summarize.py` rewrites `SUMMARY.md` there (pass/fail and numbers per job).
- QUARTZ cannot start parallel-rdp (no Vulkan): give an item `"pc": "<other PC>"` or use `COR64_RDP=software` when it matters.
- `--tier sweep` is for regression / status runs (they only fill spare capacity). Rule: agent tests first, sweeps fill the rest.

## Reference

- Known issues: `KNOWN_ISSUES.md`
- Merge steps: `MERGE_PLAN.md`
- Findings history: cer64 `FINDINGS.md`
- Status board: https://claude.ai/artifact/KTF7pMPCWMCENbvZ3k3zAk
