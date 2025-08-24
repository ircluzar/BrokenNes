namespace NesEmulator;

public interface ICPU
{
	// Core metadata (new)
	string CoreName { get; }
	string Description { get; }
	int Performance { get; } // relative performance score (higher=faster)
	int Rating { get; } // subjective quality rating 1..N
	string Category { get; }
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
