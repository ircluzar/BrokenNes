namespace NesEmulator
{
public class Input
{
	private byte controllerState = 0;   // Latched buttons
	private byte controllerShift = 0;   // Shift register for reads
	private bool strobe = false;        // Current strobe bit

	// For Blazor: set input state from UI (Up, Down, Left, Right, A, B, Select, Start)
	public void SetInput(bool[] buttons)
	{
		controllerState = 0;
		if (buttons.Length >= 8)
		{
			if (buttons[4]) controllerState |= 1 << 0; // A
			if (buttons[5]) controllerState |= 1 << 1; // B
			if (buttons[6]) controllerState |= 1 << 2; // Select
			if (buttons[7]) controllerState |= 1 << 3; // Start
			if (buttons[0]) controllerState |= 1 << 4; // Up
			if (buttons[1]) controllerState |= 1 << 5; // Down
			if (buttons[2]) controllerState |= 1 << 6; // Left
			if (buttons[3]) controllerState |= 1 << 7; // Right
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
		byte result = (byte)(controllerShift & 1);
		if (!strobe)
		{
			// Only shift when strobe low (during serial read phase)
			controllerShift >>= 1;
		}
		return result;
	}

	// Debug helpers for save state serialization (internal emulator use only)
	public byte DebugGetRawState() => controllerState;
	public byte DebugGetShift() => controllerShift;
	public bool DebugGetStrobe() => strobe;
	public void DebugSetState(byte raw, byte shift, bool str) { controllerState = raw; controllerShift = shift; strobe = str; }
}
}
