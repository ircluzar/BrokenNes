# Repo audit and health check, 2026-11-05 (read-only)

Nothing was changed. All commands were `git -c safe.directory=*` reads. In-flight agent edits were listed and not touched.

## 0. Health check

### 0.1 Where each game lives

| Game | Location | Repo / backup |
|---|---|---|
| VRUN NES / CTW (mapper 30 + MMC3) | `PROJECTS\VRUN_Nes_Dev\sys0\simulations\VRUN` (branch `vrun-history-export`) | git; its "origin" is the local bundle `VRUN_Nes_Dev\git\vrun-history.bundle` (Oct 23), now **19 commits behind**. Worktree `audio-bends` is in the scratch work dir |
| Super VRUN (SNES, WideMess fork) | `PROJECTS\SuperVRUN_dev` (`widemess`) | git; remote `archive` = `P:\BrokenDev\env\supervrun_dev\SuperVRUN`, which is stale (Oct 14, on `two-player`) |
| Vaults (GB/GBC) | `PROJECTS\VRUN_VOC_dev` (`master`) + 8 sibling worktrees `VRUN_VOC_dev_{game,maps,music,physics,polish,sfx,speed,tutorial}` | git, no remote; `P:\BrokenDev\env\backups\VRUN_VOC_dev-2026-10-24.bundle` (old) |
| Game Gear port | `PROJECTS\VRUN_VOC_GG_dev` | git, no remote, no backup |
| Master System port (CTW) | `PROJECTS\VRUN_CTW_SMS_dev` | git, no remote, no backup |
| Genesis port | `PROJECTS\SuperVRUN_MD_dev` | git, no remote, no backup |
| DOS port | `PROJECTS\VRUN_VOC_DOS_dev` (it exists) | git init'd, **no commits yet**, 3 untracked items |
| CTW Remastered | `PROJECTS\VRUN_CTW_Remastered` | GitHub `ircluzar/VRUN_CTW_Remastered`, pushed |
| SMB1 Remake | `PROJECTS\SMB1_Remake_dev` | **not a git repo**; `P:\BrokenDev\env\smb1_remake` and `P:\BrokenDev\SMB1 Topaz` exist (not compared) |
| WideMess Virtual cartridge | not found as its own folder (planned P4; likely not created yet) | - |
| Clickteam source | `P:\VRUN067 PC` (NAS, read-only) | NAS only |
| vrun_StepBack | `PROJECTS\vrun_StepBack` | GitHub `ircluzar/ED_StepBack`, pushed |
| vrun_EternalDegrade | `PROJECTS\vrun_EternalDegrade` | GitHub `ircluzar/ED_AudioDesign`, pushed |
| sys0 | `PROJECTS\sys0` | GitHub `ircluzar/sys0`, **1 commit unpushed** (8536ccd VRUN trace) + untracked tool scripts |

Handoff ROMs also sit loose on `P:\` (SuperVRUN_*.sfc, VRUN_CTW_*.nes/.sms, Vaults .gg/.gbc).

### 0.2 Where BrokenDev lives

- Code: `BrokenNes\BrokenDev` inside the BrokenNes repo (GitHub, pushed). `PROJECTS\BrokenDev` is an **empty, non-git** folder.
- Environment: `P:\BrokenDev\env` (not git): backups, bnbuild, emulators, logs, out, smb1_remake, supervrun_dev, timeflow, toolchains, vrun_nes_dev, work. `workshop-location.txt` points at `P:\BrokenDev\env\bnbuild\...\BrokenNes.Workshop.exe`.
- `P:\BrokenDev\SuperVRUN_dev.zip` (2.6 GB, Oct 14), `P:\BrokenDev\VRUN_Nes_Dev_2026-10-12.zip` (424 MB).
- `P:` is the NAS `\\STORAGE\Public`, so it IS an off-machine copy (has `#recycle`).
- `C:\BrokenNes-tools`: `dos` (dosbox-x, freedos, nasm, open-watcom, MANIFEST.md), `sega` (MANIFEST.json, archives, oracles, port-sources, test-sets), `sega-work`, `x86`. Not git; only copy (downloads are re-fetchable per manifests, but `oracles` / `port-sources` / test sets may not be).
- `%LOCALAPPDATA%\VRUN_Nes_Dev_work`: ~600 scratch dirs. **Important:** git records these as `C:\Users\philt\AppData\Local\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Local\VRUN_Nes_Dev_work`, i.e. the Claude app's virtualised package cache. Uninstalling or resetting the Claude app could wipe it, and with it the worktrees and handoff files that live there.

### 0.3 Project folders inventory

| Folder | git | Remote | Pushed | Last commit |
|---|---|---|---|---|
| BrokenNes | yes | GitHub ircluzar/BrokenNes | yes (0 ahead) | 32112045 2026-11-05 |
| SuperVRUN_dev | yes | local `archive` (P:, stale) | n/a | 2b46e30 2026-11-05 |
| SuperVRUN_MD_dev | yes | none | - | 156e111 2026-11-05 |
| VRUN_CTW_SMS_dev | yes | none | - | 5831397 2026-11-05 |
| VRUN_VOC_GG_dev | yes | none | - | 924a9b8 2026-11-05 |
| VRUN_VOC_dev (+8 worktrees) | yes | none | - | 099891d 2026-11-01 |
| VRUN_VOC_DOS_dev | yes, empty | none | - | no commits |
| VRUN_Nes_Dev\sys0\simulations\VRUN | yes | local bundle | 19 commits not in bundle | 84f0918 2026-11-05 |
| VRUN_Nes_Dev (root) | no | - | - | holds docs, Desktop_ROMs, Nested_VRUN, git\ bundles, tools; mirrored on P: |
| VRUN_Nes_Dev\sys0\simulations\mz, VRUN.p-backup-uncommitted | no | - | - | - |
| VRUN_CTW_Remastered | yes | GitHub | yes | fae13ea 2026-10-29 |
| sys0 | yes | GitHub | 1 unpushed | 8536ccd 2026-09-05 |
| vrun_StepBack, vrun_EternalDegrade | yes | GitHub | yes | 2026-02-19 / 2025-04-21 |
| SMB1_Remake_dev | **no** | - | - | - |
| SuperVRUN_dev.bundles | no | - | - | one bundle, 2026-10-23 (stale) |
| SuperVRUN_dev.p-backup-uncommitted | no | - | - | FROM.md + .handoff (Oct 19 snapshot) |
| BrokenDev (in PROJECTS) | no | - | - | empty |
| corruption-wiki-media | yes | GitHub | 1 unpushed | unrelated |

Sizes: SuperVRUN_dev 335 MB, VRUN_CTW_SMS_dev 51 MB, SuperVRUN_MD_dev 8.5 MB; the rest were not measured (du timed out on OneDrive). OneDrive: the client process was not running and the files show no cloud attributes, so **don't count on OneDrive as a backup**.

Other projects, not audited: RTCV, the RTCV_Plugin_* folders, the *-Vanguard / BizHawk folders, FileStub*, LunarBind, LuaCustomEngine, etc. (all on GitHub). `RTCV_Plugin_Autoscreenshot` and `net2proxy` are not git.

### 0.4 What's shared and how

Mirrors:
- `P:\BrokenDev\env\supervrun_dev\SuperVRUN` vs `SuperVRUN_dev`: **out of sync**, mirror HEAD d428c83 (2026-10-14, branch two-player); main repo is at 2b46e30 (2026-11-05).
- `P:\BrokenDev\env\vrun_nes_dev` vs `VRUN_Nes_Dev`: a copy of the root (Desktop_ROMs, Nested_VRUN, git bundles, retrospective). Its `sys0\simulations\VRUN` is not a git checkout; the bundles there are older. **Out of sync.**

Reparse points (junctions/symlinks) found:
- `BrokenNes\.claude\worktrees\agent-a1899be62afdd6d2d\SubProjects\65-o-borked\bin` -> `BrokenNes\SubProjects\65-o-borked\bin`
- `VRUN_Nes_Dev_work\s3_scratch\snes45\snes-stub\toolchain` -> `SuperVRUN_dev\snes-stub\toolchain`
- `VRUN_Nes_Dev_work\vrun-bends\mz` -> `VRUN_Nes_Dev\sys0\simulations\mz`
- `VRUN_Nes_Dev_work\vrun-bends\VRUN\tools\nesfab` -> `VRUN\tools\nesfab`
- None under `P:\BrokenDev\env` or the other `PROJECTS\*_dev` folders (depth 3).

Untracked but irreplaceable-ish content inside repo folders:
- `SuperVRUN_dev\snes-stub` (gitignored, includes the toolchain the junction above uses) and `SuperVRUN_dev\tools\Mesen2` (untracked). Last copy elsewhere: the Oct 14 P: zip.
- `SuperVRUN_dev\hardware-tests\*` ROMs/syms (ignored; some copied to P:).
- `VRUN_Nes_Dev\sys0\simulations\VRUN\tools\nesfab`, `mz` (not in git).

Exists only once, no backup at all: SuperVRUN_MD_dev, VRUN_CTW_SMS_dev, VRUN_VOC_GG_dev, VRUN_VOC_DOS_dev, SMB1_Remake_dev (no git either), `C:\BrokenNes-tools`, the scratch work dir; and everything newer than the stale bundles in VRUN, SuperVRUN_dev, VRUN_VOC_dev.

### 0.5 Risks (ranked)

1. **Four game repos have no copy anywhere off this disk** (MD, SMS, GG, DOS). One disk failure loses them.
2. **SMB1_Remake_dev is not under git at all.**
3. The scratch work dir (and 5 worktrees, incl. `audio-bends`, `vrun-bends`, `vrun-resume-2`) lives inside the Claude app's package cache: an app reset wipes it.
4. The VRUN, SuperVRUN and Vaults backups are 2-3 weeks stale (19 VRUN commits, ~50 SuperVRUN commits, Vaults since Oct 24).
5. Gitignored toolchains (`snes-stub`, `Mesen2`, `nesfab`) live only inside repo folders. Junctions point into them, so removing a worktree carelessly could delete the originals.
6. OneDrive looks inactive, so PROJECTS isn't really cloud-backed.
7. BrokenNes has 46 worktrees + ~600 scratch dirs. That's clutter, not data loss (all clean, all branches merged), but it hides real work.

### 0.6 Recommended structure (proposal only)

- `PROJECTS\<game>_dev`: one git repo per game, each with a remote `nas` = a bare repo at `P:\git\<game>.git` (local-only rule kept: the NAS is the user's own machine). `git push nas --all` after each commit session.
- Move gitignored toolchains into `P:\BrokenDev\env\toolchains\<name>` (or `C:\BrokenNes-tools`) and reach them via an env var or a documented junction, so no repo folder holds the only copy.
- Move the work dir out of the Claude package cache to a real path (e.g. `C:\BrokenNes-work`) and keep only live worktrees there.
- Retire the folder mirrors under `P:\BrokenDev\env\supervrun_dev` / `vrun_nes_dev` in favour of the bare NAS repos (or refresh them by script, never by hand).
- `git init` SMB1_Remake_dev; make a first commit in VRUN_VOC_DOS_dev.

## 1. Branches

### BrokenNes (main line `refs/heads/main` = 32112045; tag `main` = 86787591 is a different commit)
71 local branches. **70 are merged into main** (all fix/*, worktree-agent-*, worktree-gb-cores/mix-lab/snes-cores, vrun-*, r8-reload, relink, capture-mono, smb1-poweron-ram, plugin-mode-core, cert/game-campaign, feat/fix-default-cores): class (a), merged outright.

| Branch | Class | Notes |
|---|---|---|
| b4a-b4b-merge-proposal | (a) | 1 commit ahead (2026-10-15 scratch merge proposal), `git cherry` shows no `+`: patch already in main |
| the other 70 | (a) | ancestors of main |

### SuperVRUN_dev (main line `widemess`)
| Branch | Class | Notes |
|---|---|---|
| master | (a) | ancestor of widemess, 36 behind: **fast-forwardable** |
| invisible-enemies | (a) | ancestor, 0 ahead |
| two-player | (a) | ancestor, 0 ahead |
| weapon-mods | (a) | ancestor, 0 ahead |

### SuperVRUN_MD_dev (`main`)
s3-m3-picture-wip: (a), merged into main.

### VRUN_CTW_SMS_dev, VRUN_VOC_GG_dev, VRUN_CTW_Remastered
Only `main`. Nothing to classify.

### VRUN_VOC_dev (`master`)
All ten voc-* branches (game, maps, music, music2, physics, polish, polish2, sfx, speed, tutorial) are merged into master: (a). They're still checked out in the sibling worktrees.

### VRUN (main line `vrun-history-export`)
| Branch | Class | Notes |
|---|---|---|
| audio-bends | **(b)** | 2 unique commits (2026-10-18, audio bends work + README on per-song NMI cost); the patch doesn't apply as already present, so it's **genuinely unmerged**. It's still checked out in `VRUN_Nes_Dev_work\vrun-bends\VRUN` |

## 2. Worktrees

All worktrees are clean except the ones listed below. There are no stale/prunable entries; every folder exists.

| Repo | Worktree | Branch | State |
|---|---|---|---|
| BrokenNes | main checkout | main | in-flight edits (see 3) |
| BrokenNes | `.claude\worktrees\snes-cores` | worktree-snes-cores | dirty: ` M .claude/launch.json` |
| BrokenNes | 45 other `.claude\worktrees\*` | merged branches | clean |
| BrokenNes | `.claude\worktrees\agent-a1899be62afdd6d2d` | merged | clean, **HAS JUNCTION (SubProjects\65-o-borked\bin): DANGEROUS TO REMOVE** without `rmdir` of the link first |
| BrokenNes | work\bis_b4a\w (c5b7af55), work\bis_b4a\wmerge (f23f6ec3) | detached | clean; commits are on b4a-b4b-merge-proposal / capture-mono |
| BrokenNes | work\bn_r9_bends\wt | vrun-bends | clean |
| BrokenNes | work\bn_vrundev\wt | vrun-resume-2 | clean |
| VRUN_VOC_dev | main + 8 sibling `VRUN_VOC_dev_*` | voc-* | clean |
| VRUN_VOC_dev | work\voc_handoff_src | detached 2c462d39 (on master) | clean |
| VRUN | work\vrun-bends\VRUN | audio-bends | clean; **HAS JUNCTIONS (mz, tools\nesfab): DANGEROUS TO REMOVE** |
| (none) | work\s3_scratch\snes45 | not a worktree | **HAS JUNCTION to SuperVRUN_dev\snes-stub\toolchain: DANGEROUS** |

## 3. Uncommitted changes in main checkouts

- BrokenNes (in flight, SMS FM agent): PortedEngineBridges.cs, SmsFmBridge.cs, ConsoleSessions.cs, ConsoleSystems.cs, ApuSweepCli.cs (+ the strays below).
- SuperVRUN_MD_dev: `platform/md/video_md.c` (in flight).
- VRUN_CTW_SMS_dev: `src/game_fast_b23.c`, `src/nes_shim.c` (in flight).
- VRUN: `src/game/main.fab`, `mapper_m30.fab`, `mapper_mmc3.fab` (in flight).
- sys0: untracked `simulations/VRUN/tools/*.ps1/*.lua` + 1 unpushed commit.
- VRUN_VOC_DOS_dev: no commits, 3 untracked entries.
- SuperVRUN_dev, VRUN_VOC_GG_dev, VRUN_VOC_dev: clean.

### Strays at the BrokenNes root
All four are **0-byte empty files** (created 2026-11-05, probably a tool writing to a relative path). None holds anything; the real files live in their repos.

| Stray | Real file | Verdict |
|---|---|---|
| plat_md.c (0 B) | SuperVRUN_MD_dev/platform/md/plat_md.c (7.6 KB) | empty, safe to delete |
| video_md.c (0 B) | SuperVRUN_MD_dev/platform/md/video_md.c (86 KB) | empty, safe to delete |
| tools/fab2c.json (0 B) | VRUN_CTW_SMS_dev/tools/fab2c.json (9 KB) | empty, safe to delete |
| SmsFmBridge.cs (0 B) | Windows/NesEmulator/mix/SmsFmBridge.cs (31 KB) | empty, safe to delete |

## 4. SuperVRUN_dev
`master` is an ancestor of `widemess` (fast-forward, 36 commits). invisible-enemies, two-player and weapon-mods have nothing that isn't in widemess.

## 5. Off-machine copies
- GitHub: BrokenNes, VRUN_CTW_Remastered, sys0 (1 unpushed), vrun_StepBack, vrun_EternalDegrade.
- NAS (P:), stale: SuperVRUN_dev (Oct 14 mirror + zip), VRUN_Nes_Dev (Oct 12 zip, mirror), VRUN_VOC_dev (Oct 24 bundle).
- **None:** SuperVRUN_MD_dev, VRUN_CTW_SMS_dev, VRUN_VOC_GG_dev, VRUN_VOC_DOS_dev, SMB1_Remake_dev, C:\BrokenNes-tools.

Suggested backup (not run), once agents are idle:
```
$d="P:\BrokenDev\env\backups\2026-11-05"; mkdir $d
foreach($r in "SuperVRUN_dev","SuperVRUN_MD_dev","VRUN_CTW_SMS_dev","VRUN_VOC_GG_dev","VRUN_VOC_dev","VRUN_Nes_Dev\sys0\simulations\VRUN"){
  git -c safe.directory=* -C "C:\Users\philt\OneDrive\Documents\PROJECTS\$r" bundle create "$d\$(Split-Path $r -Leaf).bundle" --all }
```
Then verify each one with `git bundle verify`. Separately, zip SMB1_Remake_dev, `SuperVRUN_dev\snes-stub`, `SuperVRUN_dev\tools\Mesen2` and `C:\BrokenNes-tools\sega\{oracles,port-sources,test-sets}` to the NAS.

## 6. Cleanup plan (safe steps, each after user approval)
1. Backups first (section 5).
2. Merges: SuperVRUN_dev `master` fast-forward to widemess (`git branch -f master widemess` or `merge --ff-only`). VRUN `audio-bends` into vrun-history-export: needs the user's decision (2 real commits). Nothing else to merge.
3. Delete strays: the 4 empty files at the BrokenNes root (after the SMS FM agent finishes).
4. Delete class (a) branches: the 71 BrokenNes branches (keep main), the SuperVRUN invisible-enemies/two-player/weapon-mods, MD s3-m3-picture-wip, and the VOC voc-* branches once their worktrees are removed.
5. Remove worktrees: clean ones via `git worktree remove`. For the 3 with junctions, `rmdir` each link first. Leave `snes-cores` until its launch.json edit is kept or dropped.
6. Prune the scratch work dir, after moving it out of the Claude package cache.

## 7. Funnelling rule going forward
- Every agent branch merges into the repo's main line (`main`, `widemess`, `master`, `vrun-history-export`) the same day it's verified, then its branch and worktree are deleted by the agent that made them.
- No branch lives longer than its task; WIP is committed on the branch, never left only in a worktree.
- After each merge session, `git push` to GitHub (BrokenNes) or to the NAS bare repo (game repos).
- Worktrees go only in one place, and any junction a worktree creates is recorded in the worktree's README so it can be `rmdir`ed before removal.
