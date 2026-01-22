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
