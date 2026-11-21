# x86 bug bundles that replay (design, not built)

Status 2026-11-20: x86 captures (F9 / POST /api/bugs/capture) write a bundle, but `Workshop --bug play` refuses
`console x86`, and a replay could not be checked anyway. This is what has to change. Nothing here is built yet.

## What is wrong today (measured on StarCraft, bundles 2026-11-20_1646xx)
1. **No keyboard/mouse in the movie.** `BugCaptureRing.Frame(PadButtons, PadButtons)` keeps pad bits only. The PC's
   input arrives through `PcSession.HostKey(scan, extended, down)` and `HostMouse(dx, dy, buttons)`, outside the pads,
   so any replay of a played game goes off course on the first click.
2. **No RAM keyframes.** `MachineDomains.For(PcSession)` gives no domain: every keyframe `ram` is CBF29CE484222325
   (FNV-1a of nothing), so a divergence can only be seen in the picture hash.
3. **A 1 GB copy per capture.** The bundle's `rom/` gets the whole C: image (`hd_scpairC_256.img`, 1,056,964,608 bytes):
   10-17 s per F9 and 1 GB of disk each. D: (`BROKENNES_X86_HDD2`) and the CD (`BROKENNES_X86_CD`) are not named at all.
4. The start state already carries the disk diff (copy-on-write overlay, x86 disk diffs round), so the base image is the
   only thing a replay needs besides the state.

## Design
### A. Input events in the ring (BugCaptureRing + movie format)
- New per-frame event list beside `pads`: `(frame, kind, a, b, c)` with kind = key (scan, extended, down) or
  mouse (dx, dy, buttons). Ring of the last ~30 s (bounded: drop the oldest, like `notes`).
- `PcSession.HostKey/HostMouse` report to the ring through a callback set by `BugCaptureSource.For` (only while
  capturing). Events are applied at the start of the frame they were queued for, as the session already does
  (verify: the session must apply host input at frame boundaries only, never mid-frame from the UI thread; if not, queue them).
- Movie (`.bnm`) lines: `k FRAME SCAN EXT DOWN` and `m FRAME DX DY BUTTONS`. Older readers ignore unknown line kinds?
  (check `Movie.Parse`; else bump the format version).
- Replay: MovieMachine x86 path feeds them to `HostKey/HostMouse` before `FrameOnce`.

### B. A RAM domain for keyframes
- `MachineDomains.For(PcSession)`: one domain "ram" = conventional + extended RAM as `PcMachine` holds it (hash only
  the first 16 MB, or a sampled stride, to keep a keyframe cheap: one hash a second on 64+ MB must stay < 5 ms).
- The picture hash stays the main check; RAM catches divergence before it shows.

### C. Disks by reference, not by copy
- bug.json gets `disks: [{slot: "C", path, size, sha256 of the first+last 1 MB + size}, {slot: "D", ...}, {cd: ...}]`
  instead of copying C: into `rom/`. The fingerprint is cheap; a full sha256 of 1 GB is ~2 s and could run on the
  pack worker once per session and be cached by (path, size, mtime).
- Optional `--with-disks` (or a checkbox) for a self-contained bundle to send elsewhere; default = references.
- `--bug play` on x86 opens the referenced images (or `--disk C=path` overrides), refuses on fingerprint mismatch.
- The start state's copy-on-write overlay means the base images are only read: replay never writes them.

### D. `--bug play` for x86
- `MovieMachine.ParseConsole` accepts "x86"; `DebugHost.Open` gets a PcSession path (machine WIN98-P166 from the
  state's units, APU from `cores`, disks from C., NIC off unless the bundle says LAN; LAN bundles are not replayable
  by design: the other PC's packets are not recorded, say so in the report).
- Same exit codes as the other consoles (0 in sync, 1 off course, 3 no movie).

### Order and checks
1. C (disk references): immediate win, F9 drops from 10-17 s / 1 GB to well under a second / ~45 MB.
2. B (RAM domain), A (input), D (replay), each checked by: capture a played StarCraft minute on the desktop
   (mouse + keys), `--bug play` it in Workshop, in sync to the last keyframe; then one AoE capture.
3. Battery: add an x86 bug round trip (scripted input via `--stepsfile`, capture, replay).

### Also seen
- Workshop `--bugcapture 60` soaks: the pack allocates a fresh ~44 MB array a second (104 GB over 33 min); working set
  12-16 GB with capture vs 3.7 GB without, flat. Reusing the packed output (pooled, exact-size copy only when a capture is
  written) is the fix; it is in lane C's files (ConsoleStateFile.Wrap / PcSession), coordinated there.
