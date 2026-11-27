# x86 S3 ViRGE rung (archived notes)

The S3 ViRGE 86C325 card option (`--machine WIN98-P166-VIRGE` / `--video virge`; Trio64 stays the default) was a practice rung
for the GeForce: "usable, then stop". Win98 FE installs its own S3V driver at the first cold boot, and dxdiag's D3D7 cube renders.

- LESSONS.md: what carried over to the GeForce line (card = SVGA core + MMIO engine, unaligned MMIO dword reads, status bits).
- SCOPE.md: card choice, register map, plan (written 2026-11-20).
- SOURCES.md: the two reference downloads (86Box source read for behaviour only, the S3 data book) with hashes and licences.

Code: Windows/NesEmulator/x86/S3Virge.cs (S3d engine), S3Trio64.cs (ViRGE variant), Raster3D.Ops.cs; test `--x86video` (virge_s3d).
The old work folder (C:\BrokenNes-work\virge) disks and states are scratch and reproducible from the Win98 base disk.
