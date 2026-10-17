# Round 5, track bd: BrokenDev (record)

Branch `worktree-agent-a3754a911d619b72a`. Brief: `docs/projects/round5-brief.md`. Results: `docs/projects/brokendev-report.md` (the six versions) and
`docs/projects/brokendev-music-pipeline.md` (the music design). This page is the record: what was built, every verifier with its proof of failure, what was
not tested, decisions, open problems, next steps.

## 1. What was asked

The user wants **BrokenDev**: a developer-only devkit module of BrokenNes, prefilled with his three homebrew projects (VRUN, the SMB1 remake, SuperVRUN),
which restores them, builds six versions with their own toolchains and proves on BrokenNes that each runs: SMB1 NES; SMB1 Nested (SNES); VRUN NES; VRUN
Nested; VRUN Nested enhanced; SuperVRUN. Long term: compose in FL Studio, have the plugin turn the song into a ROM, preview it in the emulator, export a file
the games import. This round: the module, the six versions verified, the knowledge note on SMB1, and the music design.

## 2. What was built

| Milestone | Where | What |
|---|---|---|
| M1 restore + doctor | `BrokenDev/brokendev.ps1 restore`, `doctor.ps1`, `lib/Archive.ps1`, `archives.json` | The three archives are extracted into `BROKENDEV_HOME` (default `P:\BrokenDev\env`, outside the repository; refused if inside it) through a staging folder, with a marker that records the archive size and the sentinel files checked. `doctor` checks Git Bash, Python 3.10+ with pillow and numpy (any interpreter on the machine that has both), Node 20+, .NET 10, Windows PowerShell 5.1, 7-Zip, .NET Framework 4.8, disk, the Workshop, and the shipped toolchains (NESFab, Mesen, Project Nested, PVSnesLib). Absolute paths: no junction or `subst` was needed; the build and verify steps read no absolute path of the author's machine (`docs/START_HERE.md` section 4). |
| M2 module | `BrokenDev/` (README, `brokendev.ps1` verbs doctor, list, restore, build, build-workshop, build-app, verify, diff, run, status; `lib/`; `projects/<id>/project.json` x6; `tests/`; `tools/`; `docs/`) | Manifests are data the scripts interpret; nothing about a game is hard-coded. Dev-only gating: `BROKENNES_DEV=1` or `brokenDevEnabled` in the config; a BrokenDev menu in the desktop app and `BrokenNes.Workshop --brokendev` exist only then. The repository holds no ROM, archive, toolchain, build output or file over 1 MB (a script test checks it). |
| M3 build + verify | `lib/Build.ps1`, `lib/Verify.ps1`, `Workshop/BrokenDevCli.cs`, `tools/nested-build.ps1`, `tools/diff-nes.ps1`, `tools/diff-snes.ps1`, `lib/Diff.ps1`, `lib/TraceGate.ps1` | All six built and verified on BrokenNes; differential replay against Mesen; `UAT/cases/brokendev.json`; the report. |
| M4 docs + music | `BrokenDev/docs/START_HERE.md`, `BrokenDev/docs/smb1-knowledge.md`, `docs/projects/brokendev-music-pipeline.md`, `BrokenDev/tools/music-replay-check.ps1` | The knowledge note is in my own words (no Nintendo code or data). The music document is a design, plus a measured Phase 0 and a template-ROM fast path. |

## 3. Verifiers and how each was shown able to fail

| Verifier | What it checks | Result | Proof of failure |
|---|---|---|---|
| `BrokenDev/tests/run-tests.ps1` | the scripts themselves: manifests, placeholders, restore (synthetic zip), build runner, doctor, Workshop gate, verify, the trace gate, the decoder model | 58 pass, 0 fail, 0 skip under Windows PowerShell 5.1 and under PowerShell 7 | Each check has a negative control in the same file: a broken manifest, a missing sentinel, a build that leaves the old output, a step that outlives its timeout (killed by PID), a ROM that draws nothing / ignores the pad / is silent, a two-frame APU divergence, a one-frame picture divergence, too many blips, a trace without a header, a wrong period, a decode one frame off |
| `brokendev.ps1 verify <id>` | boot, play, hashes, colours, audio, speed on BrokenNes | 6 of 6 pass | the script tests above |
| `UAT/cases/brokendev.json` (12 cases) | final picture hash per project and scenario, reproducible over two runs | included in the suite: 64 pass, 0 fail, 60 skip | `-UpdateGolden` refuses a case whose two runs disagree; a missing ROM skips with a reason (shown), and `-StrictMissing` turns the skip into a failure (shown) |
| `brokendev.ps1 diff <id>` (NES) | picture/PPU exact, APU exact except isolated blips, against Mesen | `vrun-nes` pass, `smb1-nes` pass (16 blip frames of 24) | BrokenNes given another pad script than Mesen: fail; before the APU fixes `smb1-nes` failed on p1vol/p2vol/tlin; the gate has its own script tests |
| `brokendev.ps1 diff <id>` (SNES) | pictures at checkpoints, best frame offset, residue ceiling | 4 of 4 pass | `supervrun` with no input for BrokenNes: fail |
| `BrokenDev/tests/apu-regress.ps1` | five APU_FIX behaviours against values measured on Mesen | 5 of 5 guards hold | each fails on the pre-fix build (X=3; `0ee`; `193 1c6 ...`; volumes 8,4 return; tlin jumps to `7c`) and on the older FMC core it prints that it disagrees |
| `BrokenDev/tests/app-smoke.ps1` | the real desktop app: menu absent when off, present via env or config, a click opens a ROM, the power-on RAM lever | 9 of 9 on an earlier build; **rerun after the final merge, see section 6** | negative controls inside (lever off vs on changes the machine) |
| `BrokenDev/tools/music-replay-check.ps1` | MIDI -> converter -> ROM -> BrokenNes -> decoder model, frame by frame; with `-Template`, the patched template as well | 12 of 12 songs agree; template 11 of 11 | three controls per song (notes a semitone off, a frame out of step, another song's streams), a fourth for the template; all fail as required |
| plugin selftest (`FruityHost selftest`) | the FL plugin after the APU changes | 49 pass + the QLQ warning, 0 failed | it caught a bug of mine (sweep period default) that the UAT suite did not see |

## 4. Decisions

* **Manifests are data.** Adding a seventh project means writing a `project.json`, not code. Build outputs must be rewritten by the build or it fails.
* **Where the user's own Mesen tracer is needed it is a named input** (`BROKENDEV_TRACE_LUA`), not copied into the repository; without it the NES `diff` is skipped with the reason, never passed.
* **SMB1: ingest the knowledge, not the game.** The ROM is never in git; `docs/smb1-knowledge.md` is words only.
* **Power-on RAM is a lever, not a hidden setting.** BrokenNes's FCEUX-pattern fill is deliberate (it found a project bug); the verifier runs SMB1 with zeros and records the FCEUX run as information.
* **The gate lets isolated one-frame APU blips through, lists them and budgets them**; pictures and PPU state get no allowance. Stated in `lib/TraceGate.ps1`, the report and the tests.
* **APU_FIX was fixed where Mesen and hardware agree and the guard can be measured**; no change to a core whose behaviour I could not measure against an oracle.
* **Music: SMF in, generated streams inside, APU-state log for verification, SMF out** (design section 3). Template-ROM patching before a live ROM.
* No downloads, no installs: the Python 3.14 already on the machine had pillow and numpy.

## 5. Not tested, and open problems

Not tested: the six versions with a real keyboard or controller in the desktop window; any listening; SMB1 parity with the original; Nested builds with the harvest; a `PokePrg` on a running machine for the template (the patch was applied to the ROM file); the music pipeline inside FL Studio (no plugin change was made);
anything on real hardware.
Open: the 16 one-frame APU blips on `smb1-nes` are explained (Mesen's lazily updated APU state, the sample point straddling the NMI) but not proven one by one; the SNES comparison is a few pictures; VRUN's NESFab build is not byte-reproducible; the SMB1 uninitialised variables are the user's to fix (two lines, in the report).

## 6. After the final merge of `main`

(Filled in by the closing section of the report to the lead; see the end of this file.)

## 7. Next steps

1. The user fixes the two uninitialised variables in the SMB1 remake (`$07FC`, `$07AE`) so it needs no power-on RAM lever.
2. Music P1: a "Preview a MIDI file" entry in the BrokenDev menu that converts, patches the template with `PokePrg`, resets and plays, with the same decoder-model check on the live machine.
3. Run `Nested` harvest builds for the three Nested versions once, on a quiet machine (10 to 20 minutes each, needs Mesen), and re-verify.
4. Extend the SNES comparison to whole runs (a DSP-write log, as the SuperVRUN lab already does for its music).
5. Decide the questions in `brokendev-music-pipeline.md` section 12.

## 8. Traps for the next agent

* PowerShell variables are case-insensitive (`$Post` and `$post` are one variable): a helper silently dropped its post-build steps this way; now guarded.
* Windows PowerShell 5.1: `ConvertTo-Json` of `@($genericList)` fails ("Argument types do not match"): use `.ToArray()`. `$null.Count` under strict mode fails: wrap in `@()`.
* `Out-String -Stream` before `Where-Object` when matching native output lines.
* Mesen's `--testRunner` stops a script after about 100 s; its APU state is updated lazily, so one-frame APU differences are expected.
* `UAT\diff-trace.ps1` does not parse in 5.1: the NES differential needs `pwsh`.
* A hand-rolled "equal" regex that expected `frame(s)` at the end of a line once let APU differences pass: read the traces column by column (`lib/TraceGate.ps1`).
* An edit to a shared APU core should be followed by the plugin selftest, not only the UAT suite: the sweep-period bug passed 64 UAT cases and failed `runaway-state`.
