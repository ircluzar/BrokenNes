# nano98 game package (.n98), schema 1

A `.n98` is a game installed on Windows 98 SE, captured as a layer over a base disk, so BrokenNes2 can
build a "game seat" disk = base + package without reinstalling. Tools: `tools/nano98/pkg/` (pure Python 3,
no dependencies, run with `python -I`).

Commercial-game packages hold the user's own installed files: they stay local, are never published or pushed,
and never contain a no-CD patch (a user-supplied crack may only ever be a separate user layer).

## Container

A zip (deflate) with:

| entry | what |
|---|---|
| `manifest.json` | the metadata below (always present, read first) |
| `files/<drive>/<path>` | content of every added or changed file, path as on the FAT32 disk (long names, original case) |
| `registry/SYSTEM.DAT.reg`, `registry/USER.DAT.reg` | the registry delta as REGEDIT4 text, for reading only (the manifest's ops are authoritative) |
| `snapshot/<file>` | optional resume savestate (see `snapshot`) |

## manifest.json

```json
{
 "format": "n98", "schema": 1, "id": "age-of-empires", "title": "Age of Empires",
 "created": "2026-11-21T17:40:38+00:00", "tool": "tools/nano98/pkg/n98.py",
 "base": {"os": "win98se", "drives": {"C": {"image": "hd_scpairC_256.img", "sha256": "sha256:4C88...", "length": 1056964608}}},
 "hardware": {"machine": "WIN98-P166", "video": "S3", "ram_mb": 64, "min_cpu": "P90", "sound": "SB16_D072"},
 "launch": {"exe": "C:/Program Files/Microsoft Games/Age of Empires/Empires.exe", "args": "", "cwd": "...", "display": "800x600x8"},
 "cd": [{"label": "AOE", "ref": "age_of_empires.iso", "required": true, "note": "user's own disc rip; never shipped"}],
 "presets": {"default": {}},
 "saves": ["C:/Program Files/Microsoft Games/Age of Empires/savegame/*"],
 "snapshot": null,
 "files": [{"path": "C:/Program Files/.../Empires.exe", "size": 1517568, "sha256": "...", "attr": 32,
            "time": [ctime, cdate, adate, wtime, wdate], "zip": "files/C/Program Files/.../Empires.exe", "change": "added"},
           {"path": "C:/Program Files/Microsoft Games", "dir": true, "attr": 16, "time": [...]}],
 "deleted": ["C:/..."],
 "ini": [{"path": "C:/WINDOWS/SYSTEM.INI", "ops": [{"section": "drivers32", "key": "VIDC.IV41", "value": "ir41_32.dll"}]}],
 "dlls": ["C:/WINDOWS/SYSTEM/ir41_32.dll"],
 "registry": {"SYSTEM.DAT": {"drive": "C", "root": "HKEY_LOCAL_MACHINE", "noise_ops_dropped": 183,
              "ops": [{"op": "key", "key": "Software\\Microsoft\\Games\\Age of Empires"},
                      {"op": "set", "key": "...", "name": "InstallationDirectory", "type": 1, "data": "<hex>"}]},
              "USER.DAT": {"drive": "C", "root": "HKEY_USERS", "ops": [...]}},
 "stats": {"C": {"noise_files_skipped": 12}, "payload_bytes": 67342639}
}
```

Fields:
- `schema`: integer; readers refuse a higher schema than they know. Additive fields do not bump it.
- `base.drives`: the image each drive was captured against (sha256 = the same `sha256:HEX` the x86 state DSKD block uses).
  Applying onto another base is allowed (that is the point: the nano98 base) and only noted.
- `hardware`: `machine` (x86boot machine preset), `video` (S3 / VGA), `ram_mb` (64 default, 128 max), `min_cpu`
  (warn below it, never refuse), optional `sound`, `3d` (e.g. Voodoo 1), `net`.
  Optional `p2cpi` (user 2026-11-22 "Per game"): the Pentium II cost on a gen-6 machine (1.0 = faithful default, e.g. StarCraft 5.3;
  a machine preset may carry its own via its CPU model). Applied when the seat starts; part of the warm-state folder name (`-cpiN`), so
  states never mix. X86_P2CPI (dev override) wins. Set it in the packaging window's Manifest tab or with the agent API `capture {p2cpi}`.
- `launch`: what the seat launcher runs. Paths use `/`; `n98.py cmd` turns them into the seat COMMAND PORT text
  (format owned by `tools/nano98/seat.py` / `n98launch.c`: `N98CMD1`, `run=`, `args=`, `dir=`, `exit=`).
- `cd`: disc images the game needs, by reference only (the user's own rips, never inside the package).
- `presets`: named setting sets (renderer, card, resolution...) for the menu.
- `saves`: globs of the game's save/config files; at run time they live in a separate saves layer, the package is never modified.
- `files` / `deleted`: the file delta. Times are raw FAT words (kept so installers' version checks see the original dates).
- `ini`: key-level edits to INI files that already exist on the base (SYSTEM.INI, WIN.INI...): merged, never replaced whole,
  so a package does not drag its capture machine's drivers along. `value: null` deletes the key.
- `registry`: ops in order: `key` (create), `set` (name, type = REG_* number: 1 string, 3 binary, 4 dword; data = hex bytes
  exactly as stored), `delval`, `delkey` (whole subtree).
- `dlls`: informational list of files the package adds under `WINDOWS/SYSTEM` (shared runtimes the base could provide as a layer).
- `snapshot`: `{zip, sha256, emulator}` optional resume state; rebuilt whenever the emulator version changes.

## Capture (host side, no in-guest tool)

    python -I tools/nano98/pkg/n98.py capture OUT.n98 --title T --profile profiles/GAME.json \
        --before C=base.img [D=data.img] --after C=installed.img [D=...] [--exclude GLOB...] [--deep] [--whole-ini] [--keep-reg-noise]

- Walks both FAT32 trees (`fat32.py`, LFN aware). A file counts as changed when size/date differ and the bytes differ
  (`--deep` also byte-compares files whose size and date match).
- Boot/session churn is filtered (`FILE_NOISE`: logs, swap, TEMP, Recent, CATROOT, WININIT.INI, ...).
- Hives are parsed straight from the disk (`creg.py`, the CREG/RGKN/RGDB format) and diffed. Only `Software\` and
  `.DEFAULT\Software\` are kept by default, minus shell/MRU churn (`REG_NOISE`); Enum/Config/hardware state is the base's business.
- An install made from a running x86 state: `state2img.py STATE OUTDIR BASE.img...` materializes its disks first
  (DSKD overlay applied onto the base found by hash).
- `profiles/*.json` hold the hand-written part (hardware, launch, cd, presets, saves).

## Apply (copy-on-write)

    python -I tools/nano98/pkg/n98.py apply PKG.n98 --base C=nano98_base.img [D=data.img] --out DIR [--full] [--saves SAVES.n98]

- Base images and the package are only read. Writes land in an in-memory sector overlay (`fat32.CowFile`).
- Output `DIR/<drive>.n98l` = the layer: `'N98L'`, int32 version 1, int64 base length, byte n + base hash (n bytes),
  int32 run count, then runs of (int64 first sector, int32 count, count x 512 bytes) - the same run layout as the x86
  state's DSKD diff, so the runtime can hand it to `DiskImage` as an overlay. `--full` also writes a whole `DIR/<drive>.img`.
- Order: deletes, files (content sha256 checked), INI merges, registry patch. The registry patch keeps every existing hive
  byte in place: changed keys get new records in appended RGDB blocks (old record freed), new keys get appended RGKN nodes
  (RGKN grows in 4 KB steps, RGDB offset fixed up). SYSTEM.DA0 / USER.DA0 are written in step so Windows' registry
  restore cannot undo the package. FSInfo free count is set to unknown (Windows recounts).
- `--saves` applies a second package (the saves layer) on top.

## Proofs (2026-11-21)

| package | base | result |
|---|---|---|
| StarCraft (C: + D:, 249 files, 1.30 GB, reg 10+21 ops) | hd_r7c + hd_sc_d (no StarCraft) | round trip exact; FAT clean; hive invariants ok |
| Age of Empires (177 files, 67 MB, reg 72+25, 2 INI) | hd_scpairC_256 | round trip exact; FAT clean; hive invariants ok; boots (base asks to restart for new hardware first) |
| Half-Life Uplink (74 files, 93 MB, reg 3 ops) | nano98 hd_d1 / seat_base | round trip exact; FAT clean; seat_base + package cold-boots clean (no Registry Checker) |

Seat launch proof (2026-11-21, fixed writer): StarCraft (C: + blank D: hd_sc_d) and Age of Empires applied onto the nano98
`seat_base.img`, booted `--machine win98-p166-nano --n98cmd` (command from `n98.py cmd`): status N98 BOOT -> READY -> RUN,
both games reach their main menu (Releases\x86\nano98_seat_starcraft_package_menu.png, nano98_seat_aoe_package_menu.png).

Round trip = capture(base, applied) gives the same files (sha256) and the same registry ops as the package.

Hive validity: `regcheck.py BEFORE.img AFTER.img` checks the free-list invariants Windows' Registry Checker enforces
(RGDB free records chained through their `used` field from the block's free head at +0x10, block unused count at +8 =
their sum, RGKN ending in a free node with inuse 0x80000000 and +4 = free bytes). A re-parse by creg.py alone is not proof.
The launch exe is checked at capture: it must be a captured file or exist on the base (case fixed to the disk's).

## Commands

    n98.py info PKG.n98                       # summary
    n98.py cmd PKG.n98 OUT.txt [--exit poweroff|shell|restart]   # seat command for x86boot --n98cmd (needs tools/nano98/seat.py)

## Quake II + desktop resume timing (2026-11-21)
- Quake II package (`profiles/quake2.json`, local only): seat_base + GeForce 256 NVIDIA 5.32 (Add New Hardware, CD search on nv532.iso, one reboot)
  + Quake II copied by a seat-command `XCOPY D:\INSTALL\DATA C:\QUAKE2 /S /I` (the seat has no Explorer shell, so Win+R does nothing).
  Captured with `--keep-reg-noise`: without the Enum/Config keys the applied disk stops at "new hardware, restart?".
  228 files, 358 MB, SYSTEM.DAT 221 ops. Presets via `n98.py cmd --preset NAME` (adds the preset's guest args; `video_arg` is for the emulator).
  Proof (fleet, `--n98cmd`): software (`--video s3`, `+set vid_ref soft +set sw_mode 3`) runs the attract demo at 640x480
  (Releases
ano98_seat_quake2_package_software.png); geforce (`+set vid_ref gl`) reaches N98 RUN and Quake II initializes, but the
  GL picture is still garbage (the GeForce rung's open GL bug, not the package).
- Desktop path (`Workshop --n98seat`, same PcSession as the app), time to N98 RUN, cold / resumed: StarCraft 60.3 s (44 s first apply) / 7.1 s,
  AoE 24.7 s / 3.5 s, HL Uplink (voodoo-nano) 63.4 s / 2.7 s. Packages that write D: (StarCraft) now get their D base
  (`BROKENNES_N98_BASE_D`, or the manifest's D image beside the seat base / in BROKENNES_N98_CDS) as the primary slave.

## Seat polish (2026-11-21)
- Boot screen: `tools/nano98/seat_look.py` (called by `seat.py install`, or run on a base image) writes C:\LOGO.SYS = black with
  white "nano98 is loading..." (5x7 font in the script, byte-reproducible), black LOGOW.SYS / LOGOS.SYS, and
  `.DEFAULT\Control Panel\Colors Background = 0 0 0` + no wallpaper/pattern (black desktop instead of teal).
- Add New Hardware wizard on every package: the seat machine had the PCI RTL8029 network card, the seat base was installed
  without one. `PcConfig.NoNic` (config block version 5) is set on `win98-p166-nano` and `win98-p166-voodoo-nano`.
- `Workshop --n98seat PKG --mp4 OUT.mp4 --shotevery N --shotdir DIR`: the desktop seat path recorded frame by frame.
  StarCraft proof: splash (white) -> black -> StarCraft splash -> menu, no wizard, no teal, N98 RUN.
- Pointer: the seat base's cursor scheme (Arrow / Wait / AppStarting) is C:\N98\BLANK.CUR, so no pointer shows during boot. After
  CreateProcess, N98LAUNCH.EXE waits for WaitForInputIdle (the game's window is up), then cursors_on() brings back the standard cursors
  in memory with SPI_SETCURSORS. The registry keeps the blank scheme for the next boot.

## Schema 2: the standard drive layout (2026-11-21)

nano98 is a standard minimalist Win98 platform: every game boots on the same machine, and Windows boots the same way for all of them.

| drive | what | where it comes from |
|---|---|---|
| C: | the seat base: Windows 98 SE culled (`slim.py`), the launcher as the shell | shared `seat_base.n98d` (or `base/C.n98d` inside the package), read-only; Windows' own writes land in the run's overlay |
| D: | the game drive: the game installed in `D:\GAME` | the package's `drives/D.n98d`, always the standard 2000 MB size |
| E: | the CD drive, only when the game needs a disc | the user's own disc image, by reference (`cd`) |

Schema-2 manifest fields (the rest as in schema 1):
- `schema: 2`. Readers that know only 1 refuse it; the app reads both (schema 1 still goes through `n98.py apply`).
- `drives.D = {zip: "drives/D.n98d", p256, length}`: the game drive, an N98D container (below), stored uncompressed in the zip.
- `system = {base: "nano98-se", layers: [ids], embedded_base?}`: the system variant the game needs = the base + its dependency layers in
  order. `base/C.n98d` (optional member) makes the package self-contained.
- `registry`, `ini`: applied AT RUN TIME by the launcher (command port, after the saved files: `N98R` records, see n98launch.c),
  so Windows boots on the plain base and one warm-boot state fits every package. Only `Software\` keys: Enum/Config/hardware changes
  (drivers) belong in a system layer, not in a game.
- `c_files`: the game's few files under C:\WINDOWS (fonts, codecs), put back by the launcher before the game starts, like saves.
- `launch`, `saves`: paths on D:\GAME.

### The standard game drive (`fat32.mkfs`, `N98Seat.MakeBlankDrive`: byte-identical)
2000 MB, MBR + one FAT32 LBA partition at sector 63, 4 KB clusters, fixed volume serial 4E393844, label NANO98GAME, FSInfo free count and
next-free "unknown", no boot code. Fixed content: FAT sector 0 = root (cluster 2), the hidden reserved file `N98RSVD.SYS` (clusters 3-127),
`GAME` (cluster 128); root directory = label, N98RSVD.SYS, GAME, all dated 1980-01-01. The game lives under `D:\GAME`.
Why: Windows mounts D: at boot, before the warm point. It reads the MBR, the boot sector + FSInfo, FAT sector 0 and the root directory
(measured: sectors 0, 63-70, 95, 503, 8095-8102 and one data probe; `[nano98] warm point:` log line). Those sectors are the same on every
game drive, so the drive can be swapped under a running Windows without stale caches. The two sectors Windows writes there (FAT "in use"
flag, both FAT copies) carry over onto the game drive at the swap.

### Warm boot (shared resume state)
One savestate per seat base + emulator build, taken at the launcher's `N98 BOOT` (before it reads the command) with D: = the blank
standard drive and no disc: `<cache>\warm\<base hash 16>-<emulator build>\warm.bin` (+ `warm.key` = the drives' content key). Any schema-2
package then: load the warm state, put its game drive in the D: slot, its disc in the CD drive (a new medium for Windows), send the
command (saved files, C: files, N98R ops). No warm state yet (first package on a new base or a new emulator build): cold boot, the state is
taken at N98 BOOT and the swap happens in the same run, so cold and warm runs go through the same point. `BROKENNES_N98_WARMDIR` overrides
the folder (a warm state shipped with the base).

### Conversion: `n98.py v2 OLD.n98 NEW.n98 [--drop GLOB...] [--embed-base BASE.n98d]`
Files the schema-1 package put on C: outside WINDOWS, and on D:, move to the same path under `D:\GAME`; every registry string, INI value,
.INI/.CFG text inside the game folders, launch path and save pattern naming those folders (long or 8.3 form) is rewritten to
`D:\GAME\...` (a mechanical relocation of the captured install, not a reinstall: a game that keeps absolute paths in binary files would
need a real reinstall to D:\GAME). Files under C:\WINDOWS become `c_files`; non-Software registry ops are dropped (count reported).
Packages holding the user's games or the base built from the user's own Win98 disc are personal copies: never published or pushed.

## N98D: sparse disk container (`pkg/sparse.py`, `DiskImage.ReadSparse`)
`'N98D'`, int32 version 1, int64 disk length, byte n + the disk's identity hash (`p256:HEX`, the same hash `DiskImage.BaseHash` gives the raw
image), int32 chunk count, then per chunk: int64 first sector, int32 sector count, int32 compressed length, zlib (level 9) of the sectors.
Chunks are runs of non-zero sectors (at most 1 MB) in LBA order; everything else reads as zero, so the guest sees the full disk with its free
space. Deterministic: the same image gives the same bytes. `sparse.py pack|unpack|info`. The app opens `.n98d` anywhere it opens a disk
image (seat base, packages, x86 sessions); the python tools read it read-only (`fat32.open_ro`, `CowFile`).

## Slimming (`slim.py IN.img OUT.img --n98d OUT.n98d`)
Reproducible step after `seat.py install`: deletes what the seat does not need (the `CULL` list: setup CABs, swap file, help, Java, IE/OE/
Media Player, Jet/ODBC/ADO, scripting, DHTML, VB5 runtime, task scheduler, wallpapers, readmes, extra fonts...), drops the Run/RunServices
start-up values and the VxD keys of removed files (JAVASUP: "cannot find a device file"), installs the current launcher, zero-fills free
clusters (so the container stores only used data) and writes the N98D. 2026-11-21 on seat_base_v3: used 333 MB -> 88 MB, N98D 33 MB;
AoE, HL Uplink, Quake II (software) and StarCraft boot to their menus / in game.

## System dependency layers and variants
Some games need system pieces the base does not carry (a DirectX version, VB/MFC runtimes, codecs, a 3D card's driver). Those are
LAYERS between the base and the game; base + layers = a SYSTEM VARIANT, with its own identity hash and therefore its own warm-boot state
(keyed by base + layers + emulator build, like the base's).
- A layer is a schema-1 package of C: changes only (`"kind": "layer"`): `n98.py layer PKG.n98 OUT.n98 --id ID [--keep GLOB...] [--reg GLOB...]`
  cuts one from a captured install (default: its files under C:/WINDOWS, all its registry ops including Enum/Config for drivers, its C: INI edits).
- `n98.py variant OUT.n98d --base BASE.n98d --layers L1.n98 L2.n98...` applies them at build time (files, INI, registry: Windows boots with
  them) and packs the result like a base (free space zeroed, N98D).
- The app: a package naming `system.layers` runs on `BASE+id1+id2.n98d` (beside the base, else the cache's `variants` folder); missing, it is
  built once from the layer packages (`BROKENNES_N98_LAYERS`, the base's `layers` folder, %LOCALAPPDATA%\BrokenNes
ano98\layers, the cache's
  `layers`). Proof 2026-11-21: layer `indeo41` (AoE's Indeo 4.1 codec: ir41_32.dll + SYSTEM.INI drivers32) -> variant p256 76B984B8..., AoE
  packaged without the codec and naming the layer: variant built in 17 s on first use, its own warm state, AoE menu.

## Base build in one command
`build_seat.py --workshop EXE --work DIR --out DIR/seat_base.n98d (--iso ISO --accept-eula | --installed IMG)`: setup disk, unattended Setup,
seat, slim, a settle boot with the standard blank D: attached (Windows meets the D: slot once, at build time) and a clean power-off, pack,
then the warm-boot state (an empty schema-2 package, `--n98seat --until-warm`) in `seat_base.n98d.warm\<base hash>-<build>`, used by the app
from beside the base. 2026-11-21 on Garage2 from the installed seat_base_v3: stages 3-7 in 43 s, base 33.7 MB (90.2 MB used), warm state 8.5 MB;
StarCraft (schema 2, first open, empty cache) resumed from it: N98 RUN at 5.9 s, main menu. Stages 1-2 (Setup from the disc) are the
existing build_base.py + stage2.steps path, not re-run in this round.

## Loading screen (desktop)
Opening a .n98 runs the package preparation (and a variant build), the saves layer and the warm-state load off the UI thread; the game area shows
the nano98 look (black, "nano98 is loading...", the game's title, the stage, a progress bar for the game drive's unpack, a sliding bar for
stages without a size); the window stays responsive. `N98Seat.Progress` / `PcSession.Prestart`.

## Install flow: the user's own disc -> an instant .n98 (2026-11-21)
The end-user path (desktop: Emulator > Install a Windows game...): put the game's disc in (or pick its image), follow the wizard, end with a
schema-2 package that opens from the warm state. One engine, `NesEmulator.X86.N98Install` (Windows/NesEmulator/x86/N98Install.cs), behind
every driver: the desktop wizard (Windows/DebugWindows/N98InstallWizard.cs), the desktop HTTP API, the MCP server and the Workshop CLI. Same
actions, same JSON.

1. **Disc**: an image (.iso / .cue / .bin) or the PC's CD/DVD drive (`rip`: the data track read to an ISO in `%LOCALAPPDATA%\BrokenNes\nano98\cds`;
   the first read error stops it with a message: copy protection is never worked around). Multi-disc games: `disc {path}` any time.
2. **Machine**: a nano98 seat preset (`machines`: the `*-nano` presets) + sound card (SB16_D072 default). The warm state is per base + build +
   machine (+ sound when not SB16_D072): `<cache>\warm\<hash16>-<build>[-<machine>][-<sound>]`.
3. **Install**: `start` boots the seat base (resuming its warm state) with the blank standard D:, the disc in E:, and the launcher command
   `run=C:\N98\N98INST.EXE`: the INSTALL SHELL (tools/nano98/launcher/n98inst.c), put on C: through the command port's file records. It
   takes requests from the host the same way (the host rewrites the command text: `iseq=` a new number, `iact=run|poweroff|restart|explorer`,
   `irun=`, `iargs=`, `idir=`) and reports `N98 I IDLE/RUN/EXIT/FAIL/POWEROFF/RESTART` on port E9h. `setups` lists the disc's setup programs
   (AUTORUN.INF open=, then SETUP/INSTALL programs). The user installs to **D:\GAME** and clears any Add New Hardware wizard themselves.
   - Windows asking for **its own CD** (a setup installing a Windows part: Age of Empires' "video support" wants mciavi.drv): the seat base has no
     setup CABs (slim.py), so `winfiles` puts in the **Windows 98 SE files disc**: an ISO (label WIN98SE, publisher BROKENNES) holding WIN98\*.CAB
     made once from the user's own Windows 98 SE ISO (`make-winfiles {iso}`, tools/nano98/pkg/winfiles.py: a real SE disc with \WIN98, or a
     compilation with WIN98SE.ZIP; anything else is refused). The install seat sets Setup `SourcePath` = E:\WIN98 (an N98R op), so Windows finds the
     files at once; when it then asks for the game's disc back ("Please insert CD-ROM AOE", a blue text prompt) put it back (`disc`) and press Enter.
     The files disc is never named by a package (it is recognised by its label) and stays local.
4. **Finish**: `poweroff` (Windows shuts down: its caches reach the drives), `programs` (the .EXEs on D:\GAME, likely game programs first),
   `capture {title, exe, out, layer_id?}` -> `n98.py install` (tools/nano98/pkg/n98install.py): the game drive's tree is copied onto a fresh
   standard drive (the root stays standard for the warm swap; anything setup put in D:'s root goes under GAME), C: is diffed against the seat
   base -> `c_files` + run-time Software registry / INI ops; driver files (WINDOWS/SYSTEM/*.VXD/.SYS/.DRV..., WINDOWS/INF/*.INF) and
   Enum/Config ops are hardware: with `layer_id` they become a system layer (`<id>.n98` beside the package and in nano98\layers) the package names.
5. **Open**: the app opens the package (first run: warm state or cold boot; then instant).
Settle mode (`start {settle: true}`, no disc): the user clears the hardware wizard of a new machine preset once; `capture-layer {id}` keeps the C:
changes as a system layer.

### Agent recipe
Agents drive the flow the way a user does, step by step, looking at the screen after each step.
- **MCP** (BrokenNes server): `n98_install {action, args}` + `n98_install_screenshot`. One headless seat per server. Extra actions there:
  `frames {count}`, `wait {until: ready|off|exit, frames}`. Example:
  `n98_install start {disc: "C:/games/aoe.iso"}` -> `runsetup` -> `frames {count: 1200}` -> screenshot -> `keys {text: "{at:214,156}{click}"}`
  -> ... -> `keys {text: "D:\GAME"}` -> ... -> `winfiles` when Windows asks for its CD -> `disc {path: game iso}` + `keys {text:"{enter}"}` when
  it asks for the game disc -> `poweroff` -> `programs` -> `capture {title: "Age of Empires", exe: "auto"}`.
- **Desktop HTTP API** (the user's running app; port from `%LOCALAPPDATA%\BrokenNes\instances\PID.json`): `POST /api/n98/install/ACTION`
  with the args as the JSON body; `GET /api/n98/install` = status; the screen: `GET /api/console/framebuffer`. `start` runs the seat in the
  app's window; `capture` opens the package when done (`open: false` to skip); `open {path}`, `close`.
  Base build: `build-base {iso, key, out?}` returns at once (`{ok, started}`); poll `base-build-status` (`running, seconds, progress, log,
  result` when done; the key is masked in every line). One build at a time; `base-status` = whether a base exists. Same in MCP.
- **CLI** (Workshop): `--n98install --disc ISO [--machine M] [--sound S] [--script FILE | --do "step; step"] [--out PKG --title T --exe auto
  [--layer-id ID]] [--settle-layer ID OUT] [--make-winfiles WIN98SE.ISO] [--shotdir DIR]`. Steps: `wait ready|off|exit|frames N|status TEXT`,
  `setups`, `runsetup`, `run PATH [ARGS]`, `keys TEXT`, `shot NAME.png`, `text`, `disc PATH|none`, `winfiles`, `poweroff`, `restart`,
  `explorer`, `save/load STATE`. Every result line is `n98install: {json}`. Timing is close but not frame-exact between runs (the warm state, disc timing):
  give `wait frames N` some slack, and look at the screen (shot) before each click.
- Input tokens (all drivers): text is typed; `{enter} {tab} {esc} {space} {bs} {up}... {f1}..{f12}`, combos `{alt+n}`, `{wait:N}`,
  `{at:x,y}` absolute pointer (corner + small steps; 1 mouse count = 2 pixels on the seat), `{m:dx,dy[,buttons]}`, `{click}` (held 10 frames:
  setup buttons miss shorter ones), `{click:dx,dy}`, `{rclick}`.
- Proof script: tools/nano98/install/aoe.steps (Age of Empires from the user's ISO, end to end).
Licences: a game's EULA screen needs the user's OK per program before an agent clicks I Agree.
