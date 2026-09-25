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
    // Large framebuffer omitted from saves by default; keep field for backward compat but use empty array
    public byte[] frame = System.Array.Empty<byte>();
    public byte PPUCTRL, PPUMASK, PPUSTATUS, OAMADDR, PPUSCROLLX, PPUSCROLLY, PPUDATA;
    public ushort PPUADDR;
    public byte fineX;
    public bool scrollLatch, addrLatch;
    public ushort v, t;
    public int scanlineCycle, scanline;
    public byte ppuDataBuffer;
    public int staticFrameCounter; // include to avoid visual jumps when switching cores mid-static effect
    public bool oddFrame; // PPU_FIX: parity for the odd-frame pre-render dot skip (other cores ignore it)
    // PPU_FIX mid-line latches. A savestate is taken wherever RunFrame stopped, usually mid-scanline, so
    // these cannot be rebuilt from t on load (the game may have written $2005/$2006 since dot 257).
    // hasLineLatches = false marks a state written before they existed; the loader then reconstructs.
    public bool hasLineLatches; public ushort renderAddr, horiLatch; public int sprite0HitDot = -1;
    // Background fade (PPU_BFR specific, harmless for others if left default)
    public float backgroundFadeAlpha; // current alpha applied
    public bool enableAutoFade; // auto sine oscillation flag
    public long fadeFrameCounter; // frames elapsed for auto fade timing
}
