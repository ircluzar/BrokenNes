namespace NesEmulator;

public interface IPPU
{
	void Step(int cycles);
	byte[] GetFrameBuffer();
	void UpdateFrameBuffer();
	object GetState();
	void SetState(object state);
	byte ReadPPURegister(ushort address);
	void WritePPURegister(ushort address, byte value);
	void WriteOAMDMA(byte page);
	void GenerateStaticFrame();
}
