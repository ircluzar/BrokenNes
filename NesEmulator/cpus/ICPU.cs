namespace NesEmulator;

public interface ICPU
{
	bool IgnoreInvalidOpcodes { get; set; }
	void Reset();
	int ExecuteInstruction();
	void RequestIRQ(bool line);
	void RequestNMI();
	object GetState();
	void SetState(object state);
	(ushort PC, byte A, byte X, byte Y, byte P, ushort SP) GetRegisters();
	void AddToPC(int delta);
}
