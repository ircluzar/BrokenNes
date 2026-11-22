# nanodos (.ndos) plan / resume note (paused 2026-11-21, user: "we will actually do it next week")

Goal (user 2026-11-21): .ndos = self-contained DOS + game pseudo-ROM (zip like .n98), boots instantly in BrokenNes2
(optional warm state), and bakes to a bootable floppy that shows a "nanodos" splash (VRUN-DOS loader style).

## What exists (found)
- VRUN-DOS loader: PROJECTS\VRUN_DOS_Boot_dev (39f6622; remote T:\BrokenDev\Projects\VRUN_DOS_Boot.git). build.ps1 ->
  tools/mkfloppy.py lays out the whole 1.44 MB FAT12 image itself (boot sector + stage 2 + 80x25 splash + KERNEL.SYS in
  reserved sectors, contiguous files, SHELL=game in FDCONFIG.SYS). Splash comes from game.json ("logo" word in a block
  font A-Z/0-3/-, kicker, name band, colours) -> "logo": "NANODOS" works with no code change.
  Prebuilt bin/KERNEL.SYS (UPX, 46 KB) + bin/COMMAND.COM; vboot.asm needs NASM (C:\BrokenNes-tools\dos). GPL: ship source.
  Non-reproducible bits in mkfloppy: volume serial + file dates from time.time() (fix for stable warm-state keys).
  Writing real floppies: tools/writefloppy.ps1 (user runs it).
- App: PcSession already boots raw floppies (<=2.88 MB, no partition table) and .exe/.com (PcDisks.ForProgram on FreeDOS).
  .n98 hooks to mirror: ConsoleSystems.cs (extension list line ~153, zip passthrough ~178, detection ~231),
  MainForm.Consoles.cs ~540, MainForm.RomLoading.cs (menu/open), N98Seat.cs (Prepare/IsPackage), PcSession warm/resume code.

## Design (proposed)
- .ndos zip: manifest.json {format:"ndos", schema 1, id, title, launch {exe,args}, machine preset, apu/ppu/cpu, ram,
  saves globs, media "fd144"}, files/<8.3 names>, system/ (KERNEL.SYS, COMMAND.COM, assembled VBOOT.BIN, COPYING.TXT),
  disk/boot.img (the deterministic image the app boots; zips to little).
- tools/nanodos/ndos.py: create (folder + launch cmd), info, bake (.ndos -> .img, 1.44; clear "does not fit by N KB"
  with options 2.88 MB / multi-disk / HDD image for CF cards). Vendor mkfloppy.build with prebuilt VBOOT.BIN (no NASM).
- App: NanoDos.cs like N98Seat (Prepare -> image + preset), GameId "ndos"+id, floppy is the battery as today.
- Warm state: CAUTION - the disk is the save; a state after DOS read the root dir/FAT/save holds stale buffers.
  Measure cold boot first (memory says ~0.07 s to game mode on the x86 console: warm state may be pointless). If needed,
  snapshot at POST end / boot-sector load (before any FAT read), keyed by image hash + emulator build.
- Test: Vaults DOS + Super VRUN DOS (EXEs in their repos / Releases\VRUN_DOS_Boot), Workshop --x86boot --floppy.

## State
Branch nanodos (worktree C:\BrokenNes-work\nanodos): only this note. No heavy slots or fleet jobs were taken.
