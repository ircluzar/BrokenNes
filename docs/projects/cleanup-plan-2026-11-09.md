# Cleanup plan, 2026-11-09 (DRY RUN, nothing deleted)

Written by agent D-house. Nothing on this list has been removed. A later pass runs it only after the user approves.

**Removal rules for the later pass**
- No worktree on this list has junctions or symlinks today (`cmd /c dir /AL /S` found none). Re-check right before removing. If any appear, `rmdir` each link first (no /s). Never use `git worktree remove --force` on a worktree that has links.
- Remove worktrees with `git worktree remove <path>` (no --force). A worktree that is dirty only from line endings needs `git checkout -- .` first. Then run `git worktree prune`.
- Delete branches with `git branch -d` (not -D) where possible.
- Check for other running agents first. Kill no processes.

## a. BrokenNes worktrees

Excluded from this plan: c1_fapu, c2_wt, d_tools, dmc*, and the main checkout. All "merged" checks are against origin/main at f98bf281.

### a1. REMOVE: merged, clean (or line-ending noise only), no links

| worktree | branch | merged | dirty | links | MB |
|---|---|---|---|---|---|
| C:\BrokenNes-work\b5_lite | b5-lite-menus | yes | clean | none | 2496 |
| C:\BrokenNes-work\b6_butcher | b6-butcher | yes | clean | none | 2355 |
| C:\BrokenNes-work\b8_plugin | b8-plugin | yes | clean | none | 2347 |
| C:\BrokenNes-work\cpu_wt | cpu-harvest | yes | clean | none | 2648 |
| C:\BrokenNes-work\flp2nano | flp2nano | yes | clean | none | 2337 |
| C:\BrokenNes-work\nano_gb | nano-gb | yes | clean | none | 2337 |
| C:\BrokenNes-work\nano_bench | (detached) | yes | clean | none | 2351 |
| C:\BrokenNes-work\ppu_wt | ppu-harvest | yes | clean | none | 3418 |
| C:\BrokenNes-work\ppuR_gb2 | ppuR-gb2 | yes | clean | none | 2647 |
| C:\BrokenNes-work\ppuR_nes2 | ppuR-nes2 | yes | clean | none | 2647 |
| C:\BrokenNes-work\ppuR_sega2 | ppuR-sega | yes | clean | none | 2647 |
| C:\BrokenNes-work\ppuR_snes2 | ppuR-snes2 | yes | clean | none | 2670 |
| C:\BrokenNes-work\ppuR_gb | ppuR-gb | yes | 1377 files "M", but `diff --ignore-cr-at-eol` shows 0 lines changed: line endings only | none | 2337 |
| C:\BrokenNes-work\ppuR_nes | ppuR-nes | yes | 1366 files, line endings only | none | 2337 |
| C:\BrokenNes-work\ppuR_snes | ppuR-snes | yes | 1360 files, line endings only | none | 2337 |
| .claude\worktrees\agent-a3b3615ae65eb18c2 | worktree-agent-a3b3615ae65eb18c2 | yes | clean | none | 2336 |
| .claude\worktrees\agent-a94ead2ff2bb645ab | worktree-agent-a94ead2ff2bb645ab | yes | clean | none | 2336 |
| .claude\worktrees\agent-ac83ca1c6af13c878 | apu-pspc3 | patch-equal in main (`git cherry` 0 unapplied) | clean | none | 2336 |
| .claude\worktrees\agent-a1933f2d47e1d3bcd | worktree-agent-a1933f... (PCEG) | patch-equal in main | clean | none | 2336 |
| .claude\worktrees\agent-a1aa311a0b08436e9 | worktree-agent-a1aa311a... (x86 BLASTER) | patch-equal in main | clean | none | 2336 |
| .claude\worktrees\agent-a6c826f54607eec93 | worktree-agent-a6c826f5... (D074F) | patch-equal in main | clean | none | 2336 |
| .claude\worktrees\agent-aadfda37f538b80da | worktree-agent-aadfda37... (NUK174) | patch-equal in main | clean | none | 2336 |
| .claude\worktrees\agent-ac09eea2b980702da | worktree-agent-ac09eea2... (D074C) | patch-equal in main | clean | none | 2336 |
| .claude\worktrees\agent-ae5ef2bba797f6dee | worktree-agent-ae5ef2bb... (RSID16) | patch-equal in main | clean | none | 2336 |
| .claude\worktrees\agent-afba4f5eda3d12247 | worktree-agent-afba4f5e... (D072) | patch-equal in main | clean | none | 2336 |
| C:\BrokenNes-work\harvest_build (holds detached worktrees wt_base, wt_push: merged, clean; wt2: patch-equal in main, clean) | (detached x3) | yes | clean | none | 9749 |
| C:\BrokenNes-work\bn_reset | butcher-reset | patch-equal in main (seeded reset is merged) | only untracked tools/butcher-replay/obj/ (build output) | none | 2341 |

**Total a1: 27 worktree folders (29 worktrees), 73361 MB, about 71.6 GB.**

### a2. UNCERTAIN (keep until the user decides)

| worktree | branch | why |
|---|---|---|
| C:\BrokenNes-work\bn_vstauto | vst-automation | NOT merged. Its plugin automation prototype ("AUT1") is not in main. 2337 MB. |
| .claude\worktrees\agent-a46ee14fa66e0cade | apu-smsa (SMSA) | Same: SMSA is in main (8 .cs files), commit not patch-equal. |
| .claude\worktrees\agent-a27971f4e3ad4dd50 | POKY | Commit not patch-equal, but POKY is in main (5 .cs files). Probably a merge with conflict edits. Check the diff first. |
| .claude\worktrees\agent-a3d9999132ef2e689 | AY124 | Same: AY124 is in main, commit not patch-equal. |
| .claude\worktrees\agent-a6d24ce472653efe6 | NOFR | Same: NOFR is in main. |
| .claude\worktrees\agent-aa3158e6829f70b29 | GWEN | Same: GWEN is in main. |
| .claude\worktrees\agent-ae00ae40c9c54252e | FSID | Same: FSID is in main. |
| .claude\worktrees\snes-cores | worktree-snes-cores | Merged, but has 1 modified file (.claude/launch.json). It is named in the memory notes as the SNES worktree. 2533 MB. |
| .claude\worktrees\agent-acbc82985d017c567, wf_48cf87dc-a5c-12 | (folders only) | Empty folders that are not registered worktrees. Safe-looking, but they may belong to a running session. |

These 6 APU worktrees are 2336 MB each. With bn_vstauto and snes-cores, a2 comes to 18886 MB, about 18.4 GB.

## b. Local BrokenNes branches merged into origin/main

`git branch --merged origin/main` lists 23. Delete these 17 with `git branch -d`, after their worktrees in a1 are gone:
b5-lite-menus, b6-butcher, b8-plugin, cpu-harvest, flp2nano, nano-gb, ppu-harvest, ppuR-gb, ppuR-gb2, ppuR-nes, ppuR-nes2, ppuR-sega, ppuR-snes, ppuR-snes2, worktree-agent-a3b3615ae65eb18c2, worktree-agent-a94ead2ff2bb645ab, worktree-agent-a46ee14fa66e0cade.

Also delete these 9: they show as "not merged", but their commits are patch-equal in main (`git cherry` shows 0 unapplied). They need `-D`, so check each one before deleting:
butcher-reset, apu-pspc3, worktree-agent-a1933f2d47e1d3bcd, -a1aa311a0b08436e9, -a6c826f54607eec93, -aadfda37f538b80da, -ac09eea2b980702da, -ae5ef2bba797f6dee, -afba4f5eda3d12247.

**Keep:** main, c1-fapu-state, c2-harvest and d-tools (these are active), worktree-snes-cores (see a2), plus the unmerged b4a-b4b-merge-proposal, ppu-battery and vst-automation, and the 6 APU branches from a2 (incl. apu-smsa).
**Note:** `git branch --merged` prints main as `heads/main` because origin also has a tag called `main`. Never delete it.

**Total b: 26 branches (17 merged + 9 patch-equal). Branches take no disk space beyond their worktrees.**

## c. VRUN_VOC_dev worktrees and voc-* branches

Repo: C:\Users\philt\OneDrive\Documents\PROJECTS\VRUN_VOC_dev (master f1f74bd). All 8 worktrees are merged into master, clean, and have no junctions or symlinks. The size counts tracked, untracked and ignored files; ignored build output was not reviewed.

| worktree | branch | MB |
|---|---|---|
| VRUN_VOC_dev_game | voc-game | 348.9 |
| VRUN_VOC_dev_maps | voc-maps | 41.1 |
| VRUN_VOC_dev_music | voc-music2 | 33.8 |
| VRUN_VOC_dev_physics | voc-physics | 229.1 |
| VRUN_VOC_dev_polish | voc-polish2 | 91.8 |
| VRUN_VOC_dev_sfx | voc-sfx | 531.9 |
| VRUN_VOC_dev_speed | voc-speed | 63.7 |
| VRUN_VOC_dev_tutorial | voc-tutorial | 70.2 |
| C:\BrokenNes-work\voc_handoff_src | (detached, merged) | small |

Branches: all 10 are merged into master. Delete them with `git branch -d`: voc-game, voc-maps, voc-music, voc-music2, voc-physics, voc-polish, voc-polish2, voc-sfx, voc-speed, voc-tutorial.

**Total c: 9 worktrees, about 1.41 GB, plus 10 branches.**
**Caveat:** these folders sit in OneDrive. Before deleting, check that no ignored toolchain folder is the only copy (see the snes-stub incident).

## d. C:\BrokenNes-work loose entries

Selection: last written before 2026-11-08, not a worktree, not a junction target, and the name does not appear in the memory notes or in BrokenNes\docs (whole-word match). None of the selected entries contains a junction or symlink.
Links found under C:\BrokenNes-work: s3_scratch\snes45\snes-stub\toolchain, plus vrun-bends\mz and vrun-bends\VRUN\tools\nesfab. Both folders are on the keep or uncertain lists.

- **REMOVE: 538 entries, about 61.3 GB (61255 MB).** The full list is below. The biggest are vocpolish2 (7.3 GB), bn_tf5 (5.6 GB), voc_maps_compare (3.9 GB), spk_ws_src (2.3 GB), dbg_d1_mainsrc.tar (2.3 GB) and voc_final_cmpA/B (1.6 GB each).
- **UNCERTAIN: 47 entries, about 9.3 GB.** Their names appear in the memory notes or docs, so the later pass should read those references first. They include apuall_build 3.1 GB, b4a_head.zip 2.1 GB, s2_timeflow, bn_app_main, x86_build, s1_scratch, vrun-bends (has junctions; `rmdir` the links first), mesen, voc_sgb, voc_mesen_sgb, svdos_scratch, floppy_backup_2026-11-06/-07, bootfloppy, svfloppy, md_handoff, gg_handoff, sms_handoff and voc_handoff.
- **KEEP:** 62 recent entries (written 2026-11-08 or later, about 49.6 GB) and 6 registered worktrees (about 20.5 GB). This category does not cover them.

## e. P:\BrokenDev\env

**REMOVE after approval: P:\BrokenDev\env, 43292 files, 9.31 GB.**
- The zip is verified: T:\BrokenDev\Backups\P_BrokenDev_env_2026-11-09.zip (4.19 GB on disk). It holds 43292 file entries, which matches the source.
- 40 files picked at random were test-extracted and match their originals by hash.
- The SHA-256 is e0b30cb24b676902daa3a7b946d961c96ae8179bcf1865986b5c0630e5d0b2ed. It is saved next to the zip in a .sha256 file.
- Before deleting:
  - The memory notes still name P: copies, for example the SuperVRUN snes-stub and Mesen2 toolchain at P:\BrokenDev\env\supervrun_dev, and the shared bnbuild. Re-point those references first.
  - No agent may be using P:\BrokenDev\env\bnbuild.
  - No junctions were found under P:\BrokenDev\env.

## Keep (summary)
- The main checkout. c1_fapu, c2_wt, d_tools, dmc* and their branches.
- Recent C:\BrokenNes-work entries, and s3_scratch (it holds the snes-stub toolchain link).
- vst-automation, b4a-b4b-merge-proposal, ppu-battery (unmerged).
- VRUN_VOC_dev master.

## Uncertain (summary)
- a2: bn_vstauto, 5 APU agent worktrees (not patch-equal; content appears in main), snes-cores (1 modified file), and 2 stray empty folders.
- d: 47 entries named in memory or docs.

## Not done: SMB1 engine sub-repo
C:\Users\philt\OneDrive\Documents\PROJECTS\SMB1_Remake_dev\source\engine. No ROM is tracked at the tip of any branch. But the history added rom_g1/, rom_g5/ and rom_s4/smb1_game.nes (commits 5a67d2e, b187ffd and 2e01f02, 2026-09-27), and pushing all branches would copy them. So the bare repo T:\BrokenDev\Projects\SMB1_Engine.git was NOT created. The repo also has no configured remotes, only stale remote-tracking refs (castle/fix/lag/route). The user decides whether to push anyway, or to filter the history first.

## d. Full REMOVE list (name | last written | MB)

| name | date | MB |
|---|---|---|
| after_BrokenNes.Core.log | 2026-10-01 | 0 |
| after_BrokenNes.Lite.log | 2026-10-01 | 0 |
| after_BrokenNes.Web.log | 2026-10-01 | 0 |
| after_BrokenNes.Windows.log | 2026-10-01 | 0 |
| after_BrokenNes.Workshop.log | 2026-10-01 | 0 |
| baseline_BrokenNes.Lite.log | 2026-10-01 | 0 |
| baseline_BrokenNes.Web.log | 2026-10-01 | 0 |
| baseline_BrokenNes.Windows.log | 2026-10-01 | 0 |
| baseline_BrokenNes.Workshop.log | 2026-10-01 | 0 |
| battery-desktop-build | 2026-11-04 | 789.4 |
| bfix_build | 2026-11-07 | 23 |
| bfix_scratch | 2026-11-07 | 1.2 |
| bfuzz_check | 2026-11-04 | 3.5 |
| bn_ab_before | 2026-10-30 | 17.7 |
| bn_app_main2 | 2026-10-30 | 731.9 |
| bn_apufix | 2026-10-29 | 16.9 |
| bn_apufix_before | 2026-10-29 | 16.9 |
| bn_apufix_before.log | 2026-10-29 | 0 |
| bn_apufix_merge.log | 2026-10-29 | 0 |
| bn_apufix_trionly | 2026-10-29 | 16.9 |
| bn_apufix_trionly.log | 2026-10-29 | 0 |
| bn_apufix.log | 2026-10-29 | 0 |
| bn_audiopace | 2026-10-29 | 734.7 |
| bn_audiopace_before | 2026-10-29 | 717.7 |
| bn_audiopace_diag | 2026-10-29 | 717.8 |
| bn_audiopace_probe | 2026-10-29 | 7.6 |
| bn_audiopace_ref | 2026-10-29 | 16.6 |
| bn_auto | 2026-11-07 | 230.4 |
| bn_bdfix | 2026-10-29 | 16.9 |
| bn_bdfix_app | 2026-10-29 | 717.7 |
| bn_bdfix_litebugs | 2026-10-29 | 415.7 |
| bn_bdfix_proof | 2026-10-29 | 3.7 |
| bn_butcher | 2026-11-03 | 753.8 |
| bn_butcher2 | 2026-11-03 | 733.6 |
| bn_butcher3 | 2026-11-03 | 753.9 |
| bn_butcher4 | 2026-11-03 | 754.5 |
| bn_d8_debugtest.txt | 2026-10-26 | 0 |
| bn_d8_debugtest.txt.err | 2026-10-26 | 0 |
| bn_d8_debugtest2.txt | 2026-10-26 | 0 |
| bn_d8_debugtest3.txt | 2026-10-26 | 0 |
| bn_gbstack | 2026-10-29 | 16.9 |
| bn_menufix | 2026-10-29 | 16.9 |
| bn_menufix_base | 2026-10-29 | 717.6 |
| bn_menufix_win | 2026-10-29 | 717.7 |
| bn_r6_ui_base_publish.log | 2026-10-17 | 0 |
| bn_r6_ui_base_publish.log.err | 2026-10-17 | 0 |
| bn_r8_relink.host.log | 2026-10-18 | 0.1 |
| bn_r8_relink.publish.log | 2026-10-18 | 0 |
| bn_seedcat_out | 2026-11-07 | 0 |
| bn_seedcat_out2 | 2026-11-07 | 0 |
| bn_seedcat_out2.log | 2026-11-07 | 0 |
| bn_sega_c3b_list.txt | 2026-10-09 | 0 |
| bn_sega_gate | 2026-10-15 | 18.6 |
| bn_tf0 | 2026-10-30 | 18 |
| bn_tf0_app | 2026-10-30 | 718.6 |
| bn_tf0_base | 2026-10-30 | 17.3 |
| bn_tf0_core | 2026-10-30 | 5.9 |
| bn_tf0_exp | 2026-10-30 | 17.6 |
| bn_tf0_lite | 2026-10-30 | 116.2 |
| bn_tf0_mcp | 2026-10-30 | 30.3 |
| bn_tf3 | 2026-10-30 | 748.2 |
| bn_tf3_measure_bin | 2026-10-30 | 8.3 |
| bn_tf4 | 2026-10-30 | 749.6 |
| bn_tf4_measure | 2026-10-30 | 8.4 |
| bn_tf5 | 2026-10-30 | 5625.4 |
| bn_timeflow | 2026-10-30 | 747.2 |
| bn_voc | 2026-10-29 | 16.9 |
| bn_vocgame | 2026-10-29 | 16.9 |
| bn_vocmaps | 2026-10-29 | 16.9 |
| bn_vocpolish | 2026-10-30 | 16.9 |
| bn_vocsfx | 2026-10-29 | 16.9 |
| bn_vocspeed | 2026-10-30 | 16.9 |
| bn_voctut | 2026-10-29 | 747.7 |
| bn_vstauto_build | 2026-11-07 | 215.1 |
| breplay_bin | 2026-11-07 | 3.8 |
| breplay_bin2 | 2026-11-07 | 3.9 |
| breplay_bin3 | 2026-11-07 | 4.3 |
| breplay_out2 | 2026-11-07 | 0 |
| breplay_out3 | 2026-11-07 | 0 |
| breplay_out4 | 2026-11-07 | 0 |
| bscen_out_postreset | 2026-11-07 | 0 |
| bscen_out_prereset | 2026-11-07 | 0 |
| bscen_postreset | 2026-11-07 | 4.3 |
| bscen_prereset | 2026-11-07 | 4.3 |
| certify_final.txt | 2026-10-01 | 0 |
| certify_final2.txt | 2026-10-01 | 0 |
| certify_run1.txt | 2026-10-01 | 0 |
| certify_run2.txt | 2026-10-01 | 0 |
| core_build.log | 2026-10-01 | 0 |
| corecheck | 2026-11-04 | 3.3 |
| cpu_battery.txt | 2026-11-07 | 0 |
| cpu_build.log | 2026-11-07 | 0 |
| cpu_msg0.txt | 2026-11-07 | 0 |
| cpu_msg1.txt | 2026-11-07 | 0 |
| cpu_msg2.txt | 2026-11-07 | 0 |
| cpu_suite1.txt | 2026-11-07 | 0 |
| cpu_suite2.txt | 2026-11-07 | 0 |
| cpu_suite3.txt | 2026-11-07 | 0 |
| cpu_suitebin | 2026-11-07 | 10.3 |
| dbg_d1_mainsrc.tar | 2026-10-24 | 2321.6 |
| envmove_build | 2026-11-06 | 16.3 |
| envuat_battery.txt | 2026-11-06 | 0 |
| envuat_bd | 2026-11-06 | 0.1 |
| envuat_build_vrun-nes-mmc3.txt | 2026-11-06 | 0 |
| envuat_build_vrun-nes.txt | 2026-11-06 | 0 |
| envuat_desktop.txt | 2026-11-06 | 0 |
| envuat_scratch | 2026-11-06 | 0 |
| envuat_verify_vrun-nes-mmc3.txt | 2026-11-06 | 0 |
| envuat_verify_vrun-nes.txt | 2026-11-06 | 0 |
| envuat_verify2_vrun-nes-mmc3.txt | 2026-11-06 | 0 |
| envuat_verify2_vrun-nes.txt | 2026-11-06 | 0 |
| esfm_registers.c | 2026-11-06 | 0 |
| esfm.c | 2026-11-06 | 0.1 |
| fmtest_bin | 2026-11-06 | 0.3 |
| gfx.diff | 2026-10-31 | 0 |
| hk_battery.txt | 2026-11-06 | 0 |
| hk_battery2.txt | 2026-11-06 | 0 |
| hk_bfuzz | 2026-11-06 | 3.7 |
| hk_build | 2026-11-06 | 21.6 |
| hk_desk | 2026-11-06 | 783.6 |
| hk_fz | 2026-11-06 | 0 |
| hk_msg1.txt | 2026-11-06 | 0 |
| hk_msg2.txt | 2026-11-06 | 0 |
| hk_msg3.txt | 2026-11-06 | 0 |
| hk_msg3a.txt | 2026-11-06 | 0 |
| hk_msg4.txt | 2026-11-06 | 0 |
| hk_msg5.txt | 2026-11-06 | 0 |
| hk_old | 2026-11-06 | 0 |
| hk_tf | 2026-11-06 | 86.8 |
| hk_v.py | 2026-11-06 | 0 |
| item2_start_at_slot_sweep.lua | 2026-10-23 | 0 |
| item2_startat.patch | 2026-10-23 | 0 |
| item3_opt.patch | 2026-10-23 | 0 |
| m6d_base_build.txt | 2026-10-25 | 0 |
| md_door | 2026-11-07 | 4.1 |
| mine.patch | 2026-10-15 | 0.4 |
| p3_scratch | 2026-11-06 | 0 |
| p3a_scratch | 2026-11-05 | 24.1 |
| patch_out.txt | 2026-10-15 | 0 |
| patch_out2.txt | 2026-10-15 | 0 |
| paths.local.json.oldmachine | 2026-09-17 | 0 |
| ppu_battery | 2026-11-07 | 37.5 |
| ppu_bfuzz | 2026-11-07 | 13.3 |
| ppu_bfuzz.log | 2026-11-07 | 0 |
| ppu_build.log | 2026-11-07 | 0 |
| ppu_shots | 2026-11-07 | 2.8 |
| ppu_suite | 2026-11-07 | 0.4 |
| ppu_suite_final | 2026-11-07 | 0.4 |
| ppu_suite_s9x | 2026-11-07 | 0.4 |
| ppu_suite2 | 2026-11-07 | 0.4 |
| ppu_suite3 | 2026-11-07 | 0.4 |
| ppu_win.log | 2026-11-07 | 0 |
| probe.ps1 | 2026-10-01 | 0 |
| probe2.ps1 | 2026-10-01 | 0 |
| probe3.ps1 | 2026-10-01 | 0 |
| r6_runaway_ws_build.log | 2026-10-17 | 0 |
| s0_lite | 2026-11-02 | 0.8 |
| s0_scratch | 2026-11-02 | 83.9 |
| s2_base | 2026-11-06 | 78.5 |
| s2_base_rel.log | 2026-11-06 | 0 |
| s2_base.log | 2026-11-06 | 0 |
| s2_cfe | 2026-11-06 | 41.1 |
| s2_cfe.log | 2026-11-06 | 0 |
| s2_hud | 2026-11-07 | 1083.2 |
| s2_hud_a.log | 2026-11-06 | 0 |
| s2_hud_auto.log | 2026-11-06 | 0 |
| s2_hud_bd_build.log | 2026-11-06 | 0 |
| s2_hud_compat.log | 2026-11-06 | 0 |
| s2_hud_d.log | 2026-11-06 | 0 |
| s2_hud_Koff.log | 2026-11-06 | 0 |
| s2_hud_Kon.log | 2026-11-06 | 0 |
| s2_hud_L0.log | 2026-11-07 | 0 |
| s2_hud_L1.log | 2026-11-06 | 0 |
| s2_hud_L2.log | 2026-11-06 | 0 |
| s2_hud_m2.log | 2026-11-06 | 0 |
| s2_hud_P1.log | 2026-11-07 | 0 |
| s2_hud_p2.log | 2026-11-06 | 0 |
| s2_hud_R0.log | 2026-11-06 | 0 |
| s2_hud_RB.log | 2026-11-06 | 0 |
| s2_hud_rel.log | 2026-11-07 | 0 |
| s2_hud_relnt.log | 2026-11-06 | 0 |
| s2_hud_RG.log | 2026-11-06 | 0 |
| s2_hud_sBS.log | 2026-11-06 | 0 |
| s2_hud_sI300.log | 2026-11-06 | 0 |
| s2_hud_sI301.log | 2026-11-06 | 0 |
| s2_hud_sI302.log | 2026-11-06 | 0 |
| s2_hud_sNL.log | 2026-11-06 | 0 |
| s2_hud_sS.log | 2026-11-06 | 0 |
| s2_hud_sw.log | 2026-11-06 | 0 |
| s2_hud_sw146.log | 2026-11-06 | 0 |
| s2_hud_sw67.log | 2026-11-06 | 0 |
| s2_hud_swA.log | 2026-11-06 | 0 |
| s2_hud_swA2.log | 2026-11-06 | 0 |
| s2_hud_swB.log | 2026-11-06 | 0 |
| s2_hud_swd.log | 2026-11-06 | 0 |
| s2_hud_swE.log | 2026-11-06 | 0 |
| s2_hud_swF.log | 2026-11-06 | 0 |
| s2_hud_swG.log | 2026-11-06 | 0 |
| s2_hud_swh.log | 2026-11-06 | 0 |
| s2_hud_swH2.log | 2026-11-06 | 0 |
| s2_hud_swN.log | 2026-11-06 | 0 |
| s2_hud_t39.log | 2026-11-06 | 0 |
| s2_hud_t41.log | 2026-11-06 | 0 |
| s2_scratch | 2026-11-05 | 56.3 |
| s2_ws_obj | 2026-11-05 | 11.2 |
| sln_build.log | 2026-10-01 | 0.1 |
| snes_enhance_head.py | 2026-10-23 | 0 |
| snes_enhance_item4.py | 2026-10-23 | 0 |
| snes_enhance_items45.py | 2026-10-23 | 0 |
| spk_scratch | 2026-11-06 | 18.2 |
| spk_ws_bin | 2026-11-06 | 9.9 |
| spk_ws_src | 2026-11-06 | 2347.2 |
| uat_audio_device_base | 2026-10-29 | 1 |
| uat_audio_device_desktop | 2026-10-29 | 1 |
| uat_audio_pacing_desktop | 2026-10-29 | 124.6 |
| uat_audio_pacing_try | 2026-10-29 | 32.8 |
| uat_debug_windows | 2026-10-30 | 0.7 |
| uat_debugger_desktop | 2026-10-29 | 0.1 |
| uat_desktop | 2026-10-01 | 727.8 |
| uat_fps_base | 2026-10-29 | 0 |
| uat_mcp_live | 2026-10-30 | 0 |
| uat_menu_race_base | 2026-10-29 | 1.2 |
| uat_menu_race_desktop | 2026-10-29 | 1.2 |
| uat_menufix_desktop_scratch | 2026-10-29 | 0.1 |
| uat_t0_debugger | 2026-10-30 | 0.3 |
| uat_t0_windows | 2026-10-30 | 0.9 |
| uat_timeflow_desktop | 2026-10-30 | 1.3 |
| uat_timeflow_hotpatch_desktop | 2026-10-30 | 0.2 |
| uat_timeflow_profile_desktop | 2026-10-30 | 1.2 |
| uat_timeflow_search_desktop | 2026-10-30 | 0.1 |
| uat_vrun_voc_desktop | 2026-10-29 | 1 |
| uat_widemess_desktop | 2026-10-29 | 0.1 |
| v6_b | 2026-10-31 | 39.3 |
| v6_bdworkshop | 2026-11-01 | 18.6 |
| v6_bisect | 2026-10-31 | 158.2 |
| v6_clv | 2026-10-31 | 0.1 |
| v6_darkcheck | 2026-10-31 | 79.5 |
| v6_dk_try | 2026-10-31 | 39.6 |
| v6_flash | 2026-10-31 | 47.5 |
| v6_flash.build.txt | 2026-10-31 | 0 |
| v6_handoff_readme.txt | 2026-11-01 | 0 |
| v6_hud_after | 2026-10-31 | 78.8 |
| v6_hud_before | 2026-10-31 | 78.8 |
| v6_hud_plant | 2026-10-31 | 78.8 |
| v6_mesen_sgb | 2026-10-31 | 91.3 |
| v6_msg | 2026-10-31 | 39.3 |
| v6_panel | 2026-10-31 | 39.4 |
| v6_panel.build.txt | 2026-10-31 | 0 |
| v6_polarity | 2026-10-31 | 308.1 |
| v6_release_build.txt | 2026-10-31 | 0 |
| v6_release_sgb | 2026-11-01 | 0.4 |
| v6_sgb | 2026-10-31 | 39.5 |
| v6_sgbauto | 2026-10-31 | 158.5 |
| v6_sgblib | 2026-10-31 | 0 |
| v6_sweep_final | 2026-10-31 | 81.3 |
| v6_sweep_final2 | 2026-10-31 | 81.3 |
| v6_sweep_final3 | 2026-11-01 | 81.3 |
| v6_tl2 | 2026-10-31 | 80.5 |
| v6_trace | 2026-10-31 | 41.8 |
| v6_trace.build.txt | 2026-10-31 | 0 |
| v7_compare.txt | 2026-11-01 | 0 |
| v7_dashless.txt | 2026-11-01 | 0 |
| v7_flashrom | 2026-11-01 | 39.5 |
| v7_polarity.txt | 2026-11-01 | 0 |
| v7_sgb_explore | 2026-11-01 | 40.7 |
| v7_trace | 2026-11-01 | 41.8 |
| v8_batch1.sh | 2026-11-01 | 0 |
| v8_bd_checkup.txt | 2026-11-01 | 0 |
| v8_bd_verify.txt | 2026-11-01 | 0 |
| v8_bdworkshop | 2026-11-01 | 20.1 |
| v8_bdworkshop.txt | 2026-11-01 | 0 |
| v8_build_dev.txt | 2026-11-01 | 0 |
| v8_clover | 2026-11-01 | 79.2 |
| v8_clover.txt | 2026-11-01 | 0 |
| v8_compare.txt | 2026-11-01 | 0 |
| v8_darkcheck | 2026-11-01 | 79.5 |
| v8_darkcheck.txt | 2026-11-01 | 0 |
| v8_dashless.txt | 2026-11-01 | 0 |
| v8_dbg | 2026-11-01 | 0 |
| v8_dbg2 | 2026-11-01 | 0 |
| v8_dbg3 | 2026-11-01 | 0.1 |
| v8_flash | 2026-11-01 | 7.9 |
| v8_flash.txt | 2026-11-01 | 0 |
| v8_flashrom | 2026-11-01 | 39.5 |
| v8_flashrom.txt | 2026-11-01 | 0 |
| v8_hudcheck | 2026-11-01 | 158.8 |
| v8_hudcheck.txt | 2026-11-01 | 0 |
| v8_legacy_gb | 2026-11-01 | 100.3 |
| v8_legacy_gb.txt | 2026-11-01 | 0 |
| v8_legacy_gbc | 2026-11-01 | 100.3 |
| v8_legacy_gbc.txt | 2026-11-01 | 0 |
| v8_maps.txt | 2026-11-01 | 0 |
| v8_options.txt | 2026-11-01 | 0 |
| v8_polarity | 2026-11-01 | 308.1 |
| v8_polarity.txt | 2026-11-01 | 0 |
| v8_release | 2026-11-01 | 39.4 |
| v8_release_build.txt | 2026-11-01 | 0 |
| v8_sgb | 2026-11-01 | 121.5 |
| v8_sgb.txt | 2026-11-01 | 0 |
| v8_shots | 2026-11-01 | 0 |
| v8_suspend_gb | 2026-11-01 | 39.7 |
| v8_suspend_gb.txt | 2026-11-01 | 0 |
| v8_suspend_gbc | 2026-11-01 | 39.7 |
| v8_suspend_gbc.txt | 2026-11-01 | 0 |
| v8_sweep | 2026-11-01 | 81.4 |
| v8_sweep_gb.txt | 2026-11-01 | 0 |
| v8_sweep_gbc.txt | 2026-11-01 | 0 |
| v8_sweep.txt | 2026-11-01 | 0 |
| v8_trace | 2026-11-01 | 41.8 |
| vga_base | 2026-11-07 | 33.8 |
| vga_map | 2026-11-07 | 33 |
| vga_new | 2026-11-07 | 35.9 |
| vga_new13 | 2026-11-07 | 32.9 |
| vga_patch1.py | 2026-11-07 | 0 |
| vga_patch2.py | 2026-11-07 | 0 |
| vga_patch3.py | 2026-11-07 | 0 |
| vga_rel | 2026-11-07 | 0.9 |
| vms2_dbg | 2026-10-29 | 32.6 |
| vms2_dbg.txt | 2026-10-29 | 0 |
| vms2_new | 2026-10-29 | 32.6 |
| vms2_new.txt | 2026-10-29 | 0 |
| vms2_new1 | 2026-10-29 | 32.6 |
| vms2_new1.txt | 2026-10-29 | 0 |
| vms2_new4 | 2026-10-29 | 32.6 |
| vms2_new4.txt | 2026-10-29 | 0 |
| vms2_new5 | 2026-10-29 | 32.6 |
| vms2_new5.txt | 2026-10-29 | 0 |
| vms2_new6 | 2026-10-29 | 32.6 |
| vms2_new6.txt | 2026-10-29 | 0 |
| vms2_new7 | 2026-10-29 | 32.6 |
| vms2_new7.txt | 2026-10-29 | 0 |
| vms2_new8 | 2026-10-29 | 32.6 |
| vms2_new8.txt | 2026-10-29 | 0 |
| vms2_new9 | 2026-10-29 | 32.6 |
| vms2_new9.txt | 2026-10-29 | 0 |
| vms2_none | 2026-10-29 | 32.6 |
| vms2_none.txt | 2026-10-29 | 0 |
| vms2_old | 2026-10-29 | 32.5 |
| vms2_old.txt | 2026-10-29 | 0 |
| vms2_out_dbg | 2026-10-29 | 0 |
| vms2_out_new | 2026-10-29 | 0.1 |
| vms2_out_new1 | 2026-10-29 | 0.1 |
| vms2_out_new4 | 2026-10-29 | 0.1 |
| vms2_out_new5 | 2026-10-29 | 0.1 |
| vms2_out_new6 | 2026-10-29 | 0.1 |
| vms2_out_new7 | 2026-10-29 | 0.1 |
| vms2_out_new8 | 2026-10-29 | 0.1 |
| vms2_out_new9 | 2026-10-29 | 0.1 |
| vms2_out_none | 2026-10-29 | 0.1 |
| vms2_out_old | 2026-10-29 | 0.1 |
| voc_audiosame | 2026-11-01 | 211.3 |
| voc_clover | 2026-10-31 | 89.5 |
| voc_darkcheck | 2026-11-01 | 89.8 |
| voc_dashless | 2026-11-01 | 204.5 |
| voc_dbg_dark | 2026-10-30 | 40.1 |
| voc_dbg_dark2 | 2026-10-30 | 39.4 |
| voc_final_cmp0 | 2026-10-30 | 141.8 |
| voc_final_cmp0.txt | 2026-10-30 | 0 |
| voc_final_cmpA | 2026-10-30 | 1646.6 |
| voc_final_cmpA.txt | 2026-10-30 | 0 |
| voc_final_cmpB | 2026-10-30 | 1608.6 |
| voc_final_cmpB.txt | 2026-10-30 | 0 |
| voc_final_cmpP | 2026-10-30 | 99.3 |
| voc_final_gt | 2026-10-30 | 370.2 |
| voc_final_gt.txt | 2026-10-30 | 0 |
| voc_final_pol2 | 2026-10-30 | 307.5 |
| voc_final_pol2.err | 2026-10-30 | 0 |
| voc_final_pol2.txt | 2026-10-30 | 0 |
| voc_final_scr | 2026-10-30 | 404.3 |
| voc_final_scr.err | 2026-10-30 | 0 |
| voc_final_scr.txt | 2026-10-30 | 0 |
| voc_final_sweep | 2026-10-30 | 81.5 |
| voc_final_sweep_gb.txt | 2026-10-30 | 0 |
| voc_final_sweep_gbc.txt | 2026-10-30 | 0 |
| voc_final2_sweep | 2026-10-30 | 81.5 |
| voc_final2_sweep_gb.txt | 2026-10-30 | 0 |
| voc_final2_sweep_gbc.txt | 2026-10-30 | 0 |
| voc_final22 | 2026-10-30 | 137.3 |
| voc_final22.err | 2026-10-30 | 0 |
| voc_final22.txt | 2026-10-30 | 0 |
| voc_flash | 2026-11-01 | 7.9 |
| voc_game_maps | 2026-10-29 | 189.8 |
| voc_hudcheck | 2026-11-01 | 179.4 |
| voc_legacy_short | 2026-11-01 | 105.5 |
| voc_maps_compare | 2026-10-29 | 3855.9 |
| voc_music_speed_after | 2026-10-29 | 32.8 |
| voc_music_speed_after.txt | 2026-10-29 | 0 |
| voc_music_speed_before | 2026-10-29 | 32.7 |
| voc_music_speed_before.txt | 2026-10-29 | 0 |
| voc_music_speed_nomusic | 2026-10-29 | 32.5 |
| voc_music_speed_nomusic.txt | 2026-10-29 | 0 |
| voc_music_speed_out_after | 2026-10-29 | 0.1 |
| voc_music_speed_out_before | 2026-10-29 | 0.1 |
| voc_music_speed_out_nomusic | 2026-10-29 | 0.1 |
| voc_music_speed_seqs | 2026-10-29 | 0.1 |
| voc_options | 2026-10-31 | 0.3 |
| voc_p1 | 2026-11-01 | 200.8 |
| voc_polarity | 2026-11-01 | 329.1 |
| voc_release_build.txt | 2026-10-30 | 0 |
| voc_sgbsnd | 2026-11-01 | 5.2 |
| voc_sgbsnd_test | 2026-11-01 | 90 |
| voc_sgbsnd_test_sgb1 | 2026-11-01 | 45.1 |
| voc_sgbsnd_wav | 2026-11-01 | 49.6 |
| voc_snap | 2026-11-06 | 544.3 |
| voc_speed_b1 | 2026-10-30 | 100.3 |
| voc_speed_b1.txt | 2026-10-30 | 0 |
| voc_speed_b10 | 2026-10-30 | 44.3 |
| voc_speed_b10.txt | 2026-10-30 | 0 |
| voc_speed_b11 | 2026-10-30 | 49.2 |
| voc_speed_b11.txt | 2026-10-30 | 0 |
| voc_speed_b12 | 2026-10-30 | 55.2 |
| voc_speed_b12.txt | 2026-10-30 | 0 |
| voc_speed_b13 | 2026-10-30 | 49.8 |
| voc_speed_b13.txt | 2026-10-30 | 0 |
| voc_speed_b14 | 2026-10-30 | 60.1 |
| voc_speed_b14.txt | 2026-10-30 | 0 |
| voc_speed_b15 | 2026-10-30 | 43.3 |
| voc_speed_b15.txt | 2026-10-30 | 0 |
| voc_speed_b16 | 2026-10-30 | 45.8 |
| voc_speed_b16.txt | 2026-10-30 | 0 |
| voc_speed_b17 | 2026-10-30 | 46 |
| voc_speed_b17.txt | 2026-10-30 | 0 |
| voc_speed_b18 | 2026-10-30 | 46 |
| voc_speed_b18.txt | 2026-10-30 | 0 |
| voc_speed_b19 | 2026-10-30 | 52.6 |
| voc_speed_b19.txt | 2026-10-30 | 0 |
| voc_speed_b2 | 2026-10-30 | 102.8 |
| voc_speed_b2.txt | 2026-10-30 | 0 |
| voc_speed_b20 | 2026-10-30 | 46 |
| voc_speed_b20.txt | 2026-10-30 | 0 |
| voc_speed_b3 | 2026-10-30 | 38.7 |
| voc_speed_b3.txt | 2026-10-30 | 0 |
| voc_speed_b4 | 2026-10-30 | 102.3 |
| voc_speed_b4.txt | 2026-10-30 | 0 |
| voc_speed_b5 | 2026-10-30 | 36.5 |
| voc_speed_b5.txt | 2026-10-30 | 0 |
| voc_speed_b6 | 2026-10-30 | 121.7 |
| voc_speed_b6.txt | 2026-10-30 | 0 |
| voc_speed_b7 | 2026-10-30 | 47.6 |
| voc_speed_b7.txt | 2026-10-30 | 0 |
| voc_speed_b8 | 2026-10-30 | 47.5 |
| voc_speed_b8.txt | 2026-10-30 | 0 |
| voc_speed_b9 | 2026-10-30 | 43.5 |
| voc_speed_b9.txt | 2026-10-30 | 0 |
| voc_speed_base | 2026-10-30 | 68.5 |
| voc_speed_base_gb.txt | 2026-10-30 | 0 |
| voc_speed_base_gbc.txt | 2026-10-30 | 0 |
| voc_speed_base.txt | 2026-10-30 | 0 |
| voc_speed_cmp | 2026-10-30 | 384.8 |
| voc_speed_cmp10 | 2026-10-30 | 339.6 |
| voc_speed_cmp10.txt | 2026-10-30 | 0 |
| voc_speed_cmp18 | 2026-10-30 | 201.1 |
| voc_speed_cmp8 | 2026-10-30 | 370 |
| voc_speed_cmp8.txt | 2026-10-30 | 0 |
| voc_speed_cmpA | 2026-10-30 | 709.1 |
| voc_speed_cmpA.txt | 2026-10-30 | 0 |
| voc_speed_cmpB | 2026-10-30 | 699.6 |
| voc_speed_cmpB.txt | 2026-10-30 | 0 |
| voc_speed_dc18 | 2026-10-30 | 79.7 |
| voc_speed_gt16 | 2026-10-30 | 370.2 |
| voc_speed_gt16.txt | 2026-10-30 | 0 |
| voc_speed_pol | 2026-10-30 | 281.3 |
| voc_speed_prof | 2026-10-30 | 67.2 |
| voc_speed_q18.txt | 2026-10-30 | 0 |
| voc_speed_s19 | 2026-10-30 | 710.9 |
| voc_speed_s19_gb.txt | 2026-10-30 | 0 |
| voc_speed_s19_gbc.txt | 2026-10-30 | 0 |
| voc_speed_s2 | 2026-10-30 | 908.8 |
| voc_speed_s2_gb.txt | 2026-10-30 | 0 |
| voc_speed_s2_gbc.txt | 2026-10-30 | 0 |
| voc_speed_s3 | 2026-10-30 | 970 |
| voc_speed_s3_gb.txt | 2026-10-30 | 0 |
| voc_speed_s3_gbc.txt | 2026-10-30 | 0 |
| voc_speed_s8 | 2026-10-30 | 532.5 |
| voc_speed_s8_gb.txt | 2026-10-30 | 0 |
| voc_speed_st | 2026-10-30 | 28.1 |
| voc_suspend_gb | 2026-11-01 | 45 |
| voc_suspend_gbc | 2026-11-01 | 45 |
| voc_sweep | 2026-11-01 | 91.7 |
| voc_sweep_a1 | 2026-10-30 | 67.4 |
| voc_sweep_a1.txt | 2026-10-30 | 0 |
| voc_sweep_a2 | 2026-10-30 | 67.3 |
| voc_sweep_a2.txt | 2026-10-30 | 0 |
| voc_sweep_before | 2026-10-30 | 67.2 |
| voc_sweep_before.txt | 2026-10-30 | 0 |
| voc_sweep_gbc | 2026-10-30 | 65.7 |
| voc_sweep_gbc.txt | 2026-10-30 | 0 |
| vocbaker_bin | 2026-10-29 | 0.3 |
| vocbaker_tut | 2026-10-29 | 0.3 |
| vocdos_plat.c | 2026-11-06 | 0 |
| vocdos_release | 2026-11-06 | 0.9 |
| vocgame | 2026-10-29 | 1279.8 |
| vocmaps_shots | 2026-10-29 | 2.1 |
| vocpolish | 2026-10-30 | 422.9 |
| vocpolish2 | 2026-10-30 | 7250.3 |
| vocsfx_render | 2026-10-29 | 0.2 |
| vocsfx_speed | 2026-10-29 | 0.1 |
| vocsfx_speed2 | 2026-10-29 | 0.1 |
| vocsnd_fm | 2026-11-06 | 1.3 |
| vocsnd_fm.log | 2026-11-06 | 0 |
| vocsnd_spk | 2026-11-06 | 1.3 |
| vocsnd_spk.log | 2026-11-06 | 0 |
| voctut | 2026-10-29 | 1231.8 |
| vocwin_after1 | 2026-11-06 | 33.8 |
| vocwin_after1.log | 2026-11-06 | 0 |
| vocwin_before1 | 2026-11-06 | 33.8 |
| vocwin_before1.log | 2026-11-06 | 0 |
| vocwin_rel | 2026-11-06 | 0.9 |
| vocwin_rel2 | 2026-11-06 | 0.9 |
| vocwin_rel3 | 2026-11-06 | 0.9 |
| vocwin_seedA | 2026-11-06 | 33.2 |
| vocwin_seedB | 2026-11-06 | 33.2 |
| vocwin_snd_patch.py | 2026-11-06 | 0 |
| vrun_regression.log | 2026-10-01 | 0 |
| vrundos | 2026-11-07 | 83.6 |
| vrunwin_bin | 2026-11-06 | 0 |
| vst_auto.patch | 2026-11-07 | 0 |
| wms_m2_build_base.log | 2026-10-24 | 0 |
| wms_m2_build.log | 2026-10-24 | 0 |
| wms_m2_core.log | 2026-10-24 | 0 |
| wms_m2_desktop.log | 2026-10-24 | 0.1 |
| wms_m2_lite.log | 2026-10-24 | 0 |
| wms_m3_uat_merged.json | 2026-10-24 | 0.1 |
| wms_m3_uat_merged2.json | 2026-10-24 | 0.1 |
| wms_m3_uat.json | 2026-10-24 | 0.1 |
| wms_m4_ab.txt | 2026-10-24 | 0 |
| wms_m4_ab2.txt | 2026-10-24 | 0 |
| wms_m4_base_build.log | 2026-10-24 | 0 |
| wms_m4_build.log | 2026-10-24 | 0 |
| wms_m4_core.log | 2026-10-24 | 0 |
| wms_m4_desktop.log | 2026-10-24 | 0.1 |
| wms_m4_lite.log | 2026-10-24 | 0 |
| wms_m4_proofs.txt | 2026-10-24 | 0 |
| wms_m4_proofs2.txt | 2026-10-24 | 0 |
| wms_m4_uat.json | 2026-10-24 | 0.1 |
| wms_m4_uat2.json | 2026-10-24 | 0.1 |
| wms_m4_uat2.txt | 2026-10-24 | 0 |
| xcompare_test | 2026-11-02 | 167.5 |

## Done 2026-11-09

Run by the housekeeping agent after the user approved items 1-9 in chat. Every target was re-checked right before removal (merged or `git cherry` 0, clean, no junctions or symlinks). No `--force` worktree removes, no processes killed.

**Removed**
- BrokenNes worktrees (`git worktree remove`, then prune): b5_lite, b6_butcher, b8_plugin, cpu_wt, flp2nano, nano_gb, nano_bench, ppu_wt, ppuR_gb, ppuR_gb2, ppuR_nes, ppuR_nes2, ppuR_snes, ppuR_snes2, ppuR_sega2. ppuR_gb/nes/snes were line-ending noise only (0 insertions, 0 deletions): `git checkout -- .` first.
- The 10 `.claude\worktrees` agent worktrees of a1 (a3b3615, a94ead2, ac83ca1, a1933f2, a1aa311, a6c826f, aadfda3, ac09eea, ae5ef2b, afba4f5).
- harvest_build: its 3 detached worktrees (wt2, wt_base, wt_push), then the folder itself (2.4 GB of 2026-11-07 build output, no links).
- bn_reset (its untracked tools/butcher-replay/obj build output deleted first).
- Today's merged worktrees c1_fapu, c2_wt, d_tools, and C:\BrokenNes-work\mr (merge scratch, 1.3 GB, last written 15:20).
- Branches: the 17 merged ones of section b plus c1-fapu-state, c2-harvest, d-tools (`-d`); the 9 patch-equal ones with `-D` after `git cherry origin/main` showed 0 unapplied for each.
- VRUN_VOC_dev: 8 worktrees + voc_handoff_src removed, 10 voc-* branches deleted with `-d`. OneDrive caveat checked: the only ignored content not in master was reference/original/BUILD (117 files in _maps, 1 in _physics); all 117 match by hash in PROJECTS\vrun_StepBack\BUILD, so nothing unique was lost. The rest was build output.
- Section d: 537 of the 538 listed C:\BrokenNes-work entries (59.3 GB), 0 failures.

**Skipped**
- battery-desktop-build (section d): written 2026-11-09 11:58, so it falls under the keep rule.
- P:\BrokenDev\env (item 8): NOT deleted. The zip's SHA-256 re-verified OK (e0b30cb2...), but during the check a live `sdcc.exe` was running from P:\BrokenDev\env\toolchains\sdcc\4.6.0 (PID 5976, gone a minute later), so something still builds from it. Memory notes were left unchanged. Needs: find which agent/script uses the P: toolchain path, repoint it to T:\BrokenDev\env (a copy of sdcc exists there), then retry.
- Branch worktree-agent-ac83ca1c6af13c878 still exists (not on the list; its worktree was on apu-pspc3).

**Freed:** about 137 GB on C: (drive free space before vs after; other agents were writing at the same time).
