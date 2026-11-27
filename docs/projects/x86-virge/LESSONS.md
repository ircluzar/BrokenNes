# ViRGE rung - lessons for the GeForce rung (kept up to date)

## Picking the card
- Check the OS's own CABs first: Win98 FE ships S3V (ViRGE 325/VX/DX) with a Direct3D HAL (s3_32v.dll) - zero driver hunting.
  For the GeForce, the same check: what does Win98/ME/2000 ship in-box, and which NVIDIA reference driver is smallest.
- A data book + one open emulator source read for behaviour (not copied) was enough to write the engine in an afternoon; guessing
  register bits from memory would not have been (cmd_set fields, Y-up triangle walk, 1/32 sub-pixel first step, texel addressing).

## Architecture that carries over
- Card = SVGA core (VGA + vendor CRTC extensions) + MMIO engine class. Keep the engine in its own file with its own Save/Load,
  appended to the core's state blob, and its own chip id in the state ("S3VIRGE") so a state never loads on the wrong card.
- PCI memory BAR: the ViRGE's BAR0 is a 64 MB window: LFB at +0, MMIO at +16 MB. A GeForce has BAR0 = 16 MB registers, BAR1 = VRAM
  (and on NV2x a third). The Voodoo agent is making PcMachine.Pci.cs memory BARs generic: move the ViRGE onto that.
- Narrow MMIO writes: the CPU bus splits 16/32-bit accesses; the card must see full dwords (PcMachine now passes 16-bit LFB/MMIO
  writes whole; 8/16-bit writes to a dword register are merged and take effect on the top byte).
- Instant execution (no FIFO thread): deterministic, states/JIT parity hold, and status registers simply say "idle, FIFO empty".
- Raster3D.cs: texel decoders, bilinear, depth funcs, blends, ROP3, framebuffer packing - chip-neutral, exact integers. The chip owns
  its edge walk and fixed-point formats. IRaster3DBackend is the seam for a Vulkan path later.

## Display switch
- The desktop app's Machine menu lists every PcMachinePreset: adding the "win98-p166-virge" preset was the whole UI change.
- Cold boot needed: a state saved on another card is refused (by design).

## Traps
- UNALIGNED MMIO dword reads: the 16-bit DirectDraw driver polls 3DAh with `mov eax, es:[83DAh]` (VGA regs mirrored at MMIO 83B0-83DF).
  Returning the aligned dword (83D8) put the status in the wrong byte -> dxdiag hung forever. MMIO reads must give the bytes AT the address.
  Expect the same on NV (PRMVIO/PRMCIO VGA mirrors in BAR0).
- The status register must say "idle, FIFO empty" and raise the vsync-seen bit every frame (frame-count based = deterministic + JIT safe;
  the Voodoo agent found that machine-clock based status breaks JIT parity because cycles update only at block exits).
- Windows installed the in-box driver on its own at the first cold boot (PnP found PCI 5333:5631, files from C:\WIN98) and asked to restart:
  no INF work at all. For the GeForce, check the PnP ID path the same way before writing any registers.
- Win98 drew the whole desktop through the 2D engine only (BitBLT + mono pattern + rect fill ~150k register writes per boot); 3D needed
  just the triangle setup (dxdiag D3D7 cube: perspective textured triangles, cmd 5) - Gouraud/Z/fog/alpha paths were written but the
  dxdiag test does not exercise them all.
- Tooling: Workshop --shotat runs BEFORE --step (shots taken before any keys); use empty steps ("@N=>") for timed shots after keys.
  A state is tied to the disk image it was saved with (pass the same --hdd). An expanded "VAR=1 cmd" in a script is not an env assignment (use env).
- dxdiag "Next Page" (alt+n) does nothing while dxdiag is still probing; ctrl+tab switches tabs. Test Direct3D = alt+e, DirectDraw = alt+t.
