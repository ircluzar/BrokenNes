# StarCraft speed-work scripts (moved from C:\BrokenNes-work\scspeed)

Scripts and helpers used for the StarCraft in-game speed work (x86 line, Nov 2026).
They still expect the working folder `C:\BrokenNes-work\scspeed` to exist: it holds the
StarCraft disk and state images (st_ingame.bin, sbyC.img), the battery scratch dir `bw`
and the `gbin` build output. Paths inside the scripts are unchanged.

- gate.sh: SST 80386 + test386 + battery pc gate on the X86-02 worktree
- ab.sh, abx.sh, abenv*.sh: interleaved A/B `--frametimes` runs under one heavy slot
- det.sh: determinism check (default JIT vs X86_NOJIT) with savestate md5s
- tog.sh: JITSYNC cycle comparison per env toggle
- bis2.sh: hd_r7 cold boot restart-prompt check (path on dead T:, repoint first)
- r.sh: single x86boot run under a heavy slot
- wprprof.ps1 / wprprof.cmd: Windows Performance Recorder capture (run elevated)
- diff.py / diff3.py: memory-dump diffing helpers
- py/: FAT32 image get/put, creg, and JIT/MMX patch helpers
- BPP8.REG: registry snippet used for the test VM
