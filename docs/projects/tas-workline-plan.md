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
