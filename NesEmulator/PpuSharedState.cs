namespace NesEmulator;

// Shared PPU state container used when hot-swapping PPU cores so internal
// registers and VRAM/OAM contents transfer correctly between implementations.
// (Previously each core had its own private nested PpuState class, so
// Bus.SetPpuCore state handoff silently failed because the runtime types
// differed. This unified type fixes that.)
public class PpuSharedState
{
    public byte[] vram = new byte[2048];
    public byte[] palette = new byte[32];
    public byte[] oam = new byte[256];
    public byte[] frame = new byte[256 * 240 * 4];
    public byte PPUCTRL, PPUMASK, PPUSTATUS, OAMADDR, PPUSCROLLX, PPUSCROLLY, PPUDATA;
    public ushort PPUADDR;
    public byte fineX;
    public bool scrollLatch, addrLatch;
    public ushort v, t;
    public int scanlineCycle, scanline;
    public byte ppuDataBuffer;
    public int staticFrameCounter; // include to avoid visual jumps when switching cores mid-static effect
}
