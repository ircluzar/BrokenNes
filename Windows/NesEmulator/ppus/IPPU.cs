namespace NesEmulator;

public interface IPPU
{
	// Core metadata (new)
	string CoreName { get; }
	string Description { get; }
	int Performance { get; } // relative performance score (higher=faster)
	int Rating { get; } // subjective quality rating 1..N
	string Category { get; }
	void Step(int cycles);
	byte[] GetFrameBuffer();
	void UpdateFrameBuffer();
	object GetState();
	void SetState(object state);
	byte ReadPPURegister(ushort address);
	void WritePPURegister(ushort address, byte value);
	void WriteOAMDMA(byte page);
	void GenerateStaticFrame();
	// Drop or release any large transient buffers (like framebuffers) so a fresh
	// allocation occurs on next use. This helps reduce memory after resets/state loads.
	void ClearBuffers();
}

/// <summary>
/// The line counter a core renders from, for presentation only: NES.GetFrameBuffer hands out the
/// last COMPLETED frame (copied when this crosses into 240) instead of whatever the framebuffer
/// holds when RunFrame returns, which in default timing is two frames stitched at a drifting seam.
/// Frame-phase accuracy is all that is asked - unlike <see cref="IPpuProbe"/>, which promises a
/// dot-accurate counter - and every core, gimmick ones included, does run a real 0-261 line counter
/// to know which row it is drawing.
/// </summary>
public interface IPpuFrameClock
{
	/// <summary>Scanline the PPU counter currently sits on: 0-239 visible, 240 post-render,
	/// 241-260 vblank, 261 pre-render. Never -1 - this codebase numbers pre-render as 261.</summary>
	int ProbeScanline { get; }
}

/// <summary>
/// OPTIONAL capability a PPU core may advertise on top of <see cref="IPPU"/>: side-effect-free
/// access to the PPU's own timing counters and its own 16KB address space.
///
/// Deliberately a SEPARATE interface rather than new members on IPPU. Twelve cores implement
/// IPPU, most of them gimmick/novelty renderers (PPU_LQ, PPU_CUBE, PPU_EXE, ...) that do not model
/// a dot-accurate scanline counter at all; forcing them to implement one would either bloat every
/// core or - worse - have them return a plausible-looking number that is not the counter anything
/// actually renders from. A consumer tests for this interface and says so honestly when it is
/// absent, instead of silently reading a fiction.
///
/// Everything here must be a pure observation: no latch is toggled, no buffer refilled, no flag
/// cleared. That is what separates it from ReadPPURegister/WritePPURegister, which are the CPU's
/// side-effect-bearing view of the same hardware.
/// </summary>
public interface IPpuProbe : IPpuFrameClock
{

	/// <summary>Dot (a.k.a. cycle) within <see cref="ProbeScanline"/>, 0-340.</summary>
	int ProbeDot { get; }

	/// <summary>Live PPUMASK ($2001). Bit 3 = background enabled, bit 4 = sprites enabled;
	/// "the PPU is rendering" is those two OR'd together.</summary>
	byte ProbeMask { get; }

	/// <summary>Read one byte of the PPU's own address space ($0000-$3FFF, mirrored the same way
	/// the renderer sees it: pattern tables through the mapper, nametables through the current
	/// mirroring, palette at $3F00 with its four write-mirrors folded). Side-effect free: unlike a
	/// $2007 read it does not touch the read buffer or advance the VRAM address.</summary>
	byte ProbePpuBusRead(ushort address);

	/// <summary>Write one byte of that same address space, again without touching any latch or
	/// address register. For tooling (hex editors, corruption injection) only.</summary>
	void ProbePpuBusWrite(ushort address, byte value);
}
