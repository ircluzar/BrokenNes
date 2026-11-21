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

Round trip = capture(base, applied) gives the same files (sha256) and the same registry ops as the package.

Hive validity: `regcheck.py BEFORE.img AFTER.img` checks the free-list invariants Windows' Registry Checker enforces
(RGDB free records chained through their `used` field from the block's free head at +0x10, block unused count at +8 =
their sum, RGKN ending in a free node with inuse 0x80000000 and +4 = free bytes). A re-parse by creg.py alone is not proof.
The launch exe is checked at capture: it must be a captured file or exist on the base (case fixed to the disk's).

## Commands

    n98.py info PKG.n98                       # summary
    n98.py cmd PKG.n98 OUT.txt [--exit poweroff|shell|restart]   # seat command for x86boot --n98cmd (needs tools/nano98/seat.py)
