> 2026-11-27: Garage2 no longer available for BrokenNes; run gates/A-B on the fleet (submit.py ... --direct).

# TAS workline: script TASes of any game on any console, fast

Decided with the user 2026-11-21 (interview). Main goal: script TASes very quickly (proof + regression: every game played, replaying exactly). Secondary goal: chapters.

## Decisions
- **Unit of work = the chapter:** a savestate start, a goal, a script; chapters chain into full runs.
- **Chapter goals:** memory goals where a RAM map is known, picture goals (reference shot / HUD match) as fallback, so any game can start on day one.
- **Proving ground:** NES and N64 together, then every console (SNES, WideMess, GB/GBC, SMS/GG/MD, x86).
- **Where the tools live, in order:** 1) Workshop + MCP (agent tools, files); 2) BrokenNes2 playback of chapters and runs; 3) BrokenDev visual editor (timeline, piano roll, watches, branch compare).
- **Exploring alternatives:** agents branch and steer chapters; frame-exact spots go to an automatic fleet search (input variations from a savestate, scored by the goal).
- **Community TASes:** not imported; we make our own.
- **First swarm:** ~80% breadth (chapter 1 = real gameplay of as many NES + N64 games as possible), ~20% showcase full runs (SMB1, SMB3, SM64, MK64).

## Phases
- **T1 Format + runner:** one script language (from the N64 compat scripts: run/press/hold/stick/pulse/screenshot/checkpoint/savestate/poke/peek), console button maps, exact expansion to per-frame input logs; one Workshop runner for NES + N64; chapter manifest (start state, goal, script, expected checkpoints); determinism check (run twice); NES .fm2 export/import kept; MCP commands to run/branch a chapter.
- **T2 Goals + precision:** memory goals (RAM map files per game), picture goals; frame stepping with input edit, rewind/branch from any frame, lag-frame detection; per-run diff ("why did this frame differ").
- **T3 Fleet search:** input-variation search jobs scored by goals, parallel on the fleet, best branch written back as a chapter.
- **T4 Swarm:** breadth swarm on NES + N64 libraries (chapter 1 each) + showcase full runs; catalog grade "played: yes/no" per game.
- **T5 Playback in BrokenNes2;** **T6 BrokenDev editor;** **T7 other consoles** (memory-read command for Sega + x86 scripts, button maps, movie exporters).

## Existing pieces to reuse
NES: .fm2 replay, AutoTAS + Enhanced AutoTAS toolkit (grid/peek/explore/nodes/goal/roam), SMB1/SMB3 World 1 chapters. N64: UAT/n64/compat scripts + SessionProbe, COMPAT_TWICE. BrokenDev debugger + Timeflow. Fleet (fleetq/keeper, agent tier). Win98 steps files.

## Added 2026-11-21: TAS to MP4 rendering
User: "the ability to export a TAS to mp4 ... if i rewatch the starcraft tas, i would like to rewatch it as a lag-free video ... a simple tas to mp4 rendering pipeline that we could trigger on demand."
Offline deterministic replay (any host speed) -> every frame + emulated audio -> MP4 (H.264/AAC) at the console's true rate (NES/N64 60 Hz, Win98 70 Hz). Encoder: Windows Media Foundation (built in, no download); ffmpeg only if the user approves the download. Triggers: Workshop `--tas render`, MCP, fleet job, later BrokenNes2 "Save as video". First target: the StarCraft 1v1 bot match.

## T3 status (2026-11-21): BUILT + merged, paused (user: "Merge T3 and pause")
- Built: `--tas search | search-merge | search-pack` (Workshop/Tas/TasSearch.cs), fleet driver `tools/tas/fleetsearch.py` (fleet shards via
  `submit.py n64` generic items, pinned to PCs holding the build and not in fleet\dedicated.json; N64 skips AMBER/QUARTZ; retries; `--g2 N`
  extra shards on Garage2 via `g2run.sh run`), MCP `tas_search` (local, small budgets), `--tas vars --frame F,F..`, `--mp4check` audio rate.
  Fleet plumbing: fleetq.py n64 items take `"files"` (copied next to job.sh, `$JOBDIR` on the PC; backup fleetq.py.bak_tas3).
  Builds on the PCs: `C:\brokennes\x86\bin\tas3c` (via `fleet\x86sync.py --bin`; slow: it pauses on agent-busy PCs) and garage2 `bin\tas3c`.
- SMB1 proof: `UAT/tas/nes/smb1/search_1-2_best.tasrun.json` (blessed, deterministic twice, `--tas check` green): 1-2 flag grab 2091 -> 2087,
  lag 4 -> 3 (edits `delete 1764:3; delete 801:1`), 1733 candidates / 2 rounds / 7 PCs. 1-3 loads on the same frame: the 21-frame rule was not
  crossed. Next lever: chain several gains in one chapter (more rounds/bigger beam) until a frame rule falls, or search 1-1/1-2 entry timings.
  Round time: ~25 min on 2 PCs, ~85 min wall on 7 with 2 shards stuck on a busy PC (merge then ran on 10/12 shards by hand).
- SM64 search: NOT run (paused). Ready: RAM map `UAT/tas/ramdb/n64/sm64.json` VERIFIED on the fleet (level 16 = grounds; action 0x0C400201 idle,
  0x04000440 walk, 0x20001306 = waiting in the Lakitu dialog from ~8500 to past 9100 in the compat script), spec
  `UAT/tas/n64/sm64/search_lakitu.search.json` (goal `reach: action != 536875782`, hold/press A generators, tail 60). Run source staged in
  T:\BrokenDev\scratch\tas3\sm64src (sm64.tasrun.json with goals + sm64.txt + ramdb). Resume:
  `python -I tools/tas/fleetsearch.py --run T:/BrokenDev/scratch/tas3/sm64src/sm64.tasrun.json --chapter sm64 --frame 8600 --spec UAT/tas/n64/sm64/search_lakitu.search.json --rom C:/BrokenNes-work/n64/fleet_kit/data/sm64.z64 --name tas3sm64c --shards 12 --bin tas3c --cap 1 --n64 --pack-on-fleet`
- Traps: `submit.py --pcs` is NOT honoured for n64 specs (use item "pc"); a job must `mkdir -p "$N64_WORK"` before writing there; jobs pinned to
  a dedicated PC (PEARL) or a no-Vulkan PC for N64 never start (stale entries tas3smb12c_r0, tas3sm64a_pack, tas3smb12a_r1 may still sit in the keeper backlog);
  SMB1 scores are frame-rule quantised: score on a pre-rule event (flag grab) and keep the level-load goal as a requirement.
- T4 starts here: breadth swarm (chapter 1 of many NES + N64 games) using `tas_session`/`tas_search` for the precision spots.
