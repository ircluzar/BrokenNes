namespace NesEmulator
{
public class Input
{
	private byte controllerState = 0;   // Latched buttons
	private byte controllerShift = 0;   // Shift register for reads
	private bool strobe = false;        // Current strobe bit
	// Counts $4016/$4017 reads since the last ConsumeReadCount() call - used for TAS-style lag
	// frame detection (a frame where the game never polled the controller port at all).
	private int readCount = 0;

	// For Blazor: set input state from UI.
	// Standard NES Index Order: 0:A, 1:B, 2:Select, 3:Start, 4:Up, 5:Down, 6:Left, 7:Right
	public void SetInput(bool[] buttons)
	{
		controllerState = 0;
		if (buttons.Length >= 8)
		{
			// Standard mapping: Index matches bit position
			if (buttons[0]) controllerState |= 1 << 0; // A
			if (buttons[1]) controllerState |= 1 << 1; // B
			if (buttons[2]) controllerState |= 1 << 2; // Select
			if (buttons[3]) controllerState |= 1 << 3; // Start
			if (buttons[4]) controllerState |= 1 << 4; // Up
			if (buttons[5]) controllerState |= 1 << 5; // Down
			if (buttons[6]) controllerState |= 1 << 6; // Left
			if (buttons[7]) controllerState |= 1 << 7; // Right
		}

		// If strobe is high, continually refresh shift register to allow rapid polling reflect current state
		if (strobe)
		{
			controllerShift = controllerState;
		}
	}

	// Writing to 0x4016 controls strobe: when bit0 goes from 1 to 0, latch buttons into shift register
	public void Write4016(byte value)
	{
		bool newStrobe = (value & 1) != 0;
		if (strobe && !newStrobe)
		{
			// Falling edge: latch current controller state
			controllerShift = controllerState;
		}
		strobe = newStrobe;
	}

	public byte Read4016()
	{
		readCount++;
		byte result = (byte)(controllerShift & 1);
		if (!strobe)
		{
			// Real hardware's shift register floats/reads as 1 once the 8 real bits are shifted
			// out (verified against AccuracyCoin's "Controller Clocking" test, which expects 1s
			// past the 8th read) - shifting in a 1 from the top reproduces that naturally instead
			// of needing a separate read-count.
			controllerShift = (byte)((controllerShift >> 1) | 0x80);
		}
		return result;
	}

	// Consume-and-reset accessor for the per-frame read counter (see readCount's declaration).
	public int ConsumeReadCount() { int c = readCount; readCount = 0; return c; }

	// Debug helpers for save state serialization (internal emulator use only)
	public byte DebugGetRawState() => controllerState;
	public byte DebugGetShift() => controllerShift;
	public bool DebugGetStrobe() => strobe;
	public void DebugSetState(byte raw, byte shift, bool str) { controllerState = raw; controllerShift = shift; strobe = str; }
}
}
