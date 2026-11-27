# S3 ViRGE rung - scope (2026-11-20)

## Card choice: ViRGE 86C325 (PCI 5333:5631), 4 MB
- Win98 FE ships the driver in its own CABs (checked locally, no download): PRECOPY1.CAB dxs3.inf lists
  `PCI\VEN_5333&DEV_5631` (ViRGE 325), `DEV_883D` (ViRGE/VX), `DEV_8A01` (ViRGE/DX) -> section S3V.
  Files (BASE4.CAB): s3v.drv (16-bit display driver), s3v.vxd? no: s3vdd2.vxd (VDD), s3_32v.dll (32-bit DirectDraw/Direct3D HAL), s3v.dll.
  Extracted copies: C:\BrokenNes-work\virge\drv.
- Plain 325 = the oldest/simplest S3d engine, the one every later ViRGE extends; DX only adds registers we can add later.
- CR2D/2E = 56h/31h, CR2F = revision, CR30 = E1h, CR36 memory size (4 MB).

## Shared with Trio64 (reuse S3Trio64.cs)
VGA core (Vga.cs), S3 locks CR38/39/SR08, CR31/35/51/6A banking, CR58-5A linear window, extended CRTC (CR5D/5E/51/69), CR67 pixel format,
hardware cursor CR45-4F, the RAMDAC/PLL SR regs (not timed). The VBE BIOS (PcMachine.Vbe.cs) stays ours.

## New on ViRGE
- **No 8514 xxE8 engine.** 2D and 3D both go through the S3d engine, programmed by MMIO only.
- PCI BAR0 = 64 MB memory window: +0 linear framebuffer, +0x1000000 "new MMIO" (CR53 bit 3):
  0x0000-7FFF image/CPU data transfer, 0x8180 streams, 0x8200 memory port ctl, 0x83B0-83DF VGA regs, 0x8504 subsystem status/ctl,
  0x8580 DMA, 0xA000-A3FF colour pattern (8x8), 0xA4D4.. BitBLT/rect fill, 0xA8D4.. 2D line, 0xACD4.. 2D polygon,
  0xB0D4.. 3D line, 0xB4D4.. 3D triangle (z/dest/tex base, strides, colour, start + deltas for U V W D(LOD) R G B A Z X).
  Old MMIO A0000-AFFFF maps the same 64 KB.
- 3D triangle: Gouraud, lit/unlit texture, perspective (W), texture formats ARGB8888/4444/1555, 8-bit palettized, blend modes
  (complex reflection / modulate / decal), filtering (nearest, bilinear, mip modes), Z-buffer (16-bit, 8 compare funcs, update bit),
  alpha blend (src alpha / texture alpha), fog. Dest formats 8/15(16)/24 bpp.
- Status in 0x8504 (FIFO free / engine idle): we execute instantly -> always idle, deterministic.

## Plan / carry-over to the GeForce
- PCI: own config block in the card (like S3Trio64), memory BAR sizing (64 MB). Voodoo agent is adding generic memory BARs to
  PcMachine.Pci.cs: I keep the ViRGE on the VgaDevFn hook to avoid conflicts and switch to their generic BAR once merged.
- x86/Raster3D.cs: chip-neutral software rasteriser (edge walk with chip-supplied fixed-point deltas, per-pixel Z/colour/UVW,
  texel fetch + filter, blend, Z test) behind an interface so a host-GPU (Vulkan) back end can replace it later.
- Selection: PcVideo.S3Virge, machine option / CLI `--video virge`; Trio64 stays default.

## References and licences
- S3 ViRGE 86C325 datasheet (public, bitsavers / vgamuseum): NOT downloaded (ask first). Register knowledge so far from memory + tracing what the real driver writes.
- 86Box src/video/vid_s3_virge.c: GPL-2.0+ -> ideas only (our tree is not GPL).
- MAME src/devices/bus/isa/... / video/s3virge.cpp: BSD-3-Clause, 2D only -> could port with notice; ideas only for now.
- QEMU has no ViRGE.

## Test
Own disk C:\BrokenNes-work\virge\disks\C.img (= osx dd1.img: Win98 FE + DX9.0c, 16 bpp). Cold boot with the ViRGE (states saved with
the Trio64 cannot load), let Win98 find new hardware, install S3V from C:\WINDOWS\OPTIONS\CABS or the CD, dxdiag DirectDraw + Direct3D,
3D screensaver. Shots -> T:\BrokenDev\Releases\x86\virge\.
