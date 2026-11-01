namespace NesEmulator.Sega;

/// <summary>
/// What the 68000 sees of the machine. Implement it as a <b>struct</b> that holds a reference to the board and pass it to <see cref="M68k{TBus}"/>: the JIT then compiles each
/// bus call as a direct call (the same arrangement as <see cref="IZ80Bus"/>).
/// </summary>
/// <remarks>
/// <para><b>Time.</b> The CPU never owns a clock the board must trust. Every call carries <c>cycles</c>: the 68000 clock cycles that have passed since the previous call into the
/// bus (internal work, then the four clocks of this bus cycle). The access happens at the <i>end</i> of that stretch. The board turns the stretch into master clocks
/// (seven per 68000 clock), runs the VDP, the Z80 and the rest up to that time, performs the access, and adds whatever the machine makes the CPU wait for (a full VDP FIFO, a DMA that
/// holds the bus, the Z80 using the bus, DRAM refresh) to its own clock. The CPU does not see those stalls, so its own cycle counter (<see cref="M68k{TBus}.Cycles"/>) is the
/// instruction-timing figure the test vectors check, not wall time.</para>
/// <para>Time that passes with no bus cycle (the tail of an instruction, <c>STOP</c>) is reported through <see cref="Idle"/> before the next instruction starts.</para>
/// <para>Addresses are the 24-bit bus address (A1 to A23, so always even). A byte access has exactly one of <c>upper</c> (/UDS, the even byte, D8-D15) and <c>lower</c> (/LDS, the odd
/// byte, D0-D7) set; a word or long access (two word cycles) sets both. A read returns the whole 16-bit data bus value; the CPU picks the byte it asked for. A byte write puts the
/// byte on both halves, as the hardware does.</para>
/// </remarks>
public interface IM68kBus
{
    /// <summary>A read cycle. See the type's remarks for <paramref name="cycles"/>.</summary>
    ushort Read(uint address, bool upper, bool lower, int cycles);

    /// <summary>A write cycle.</summary>
    void Write(uint address, bool upper, bool lower, ushort data, int cycles);

    /// <summary>Clock cycles with no bus cycle in them.</summary>
    void Idle(int cycles);

    /// <summary>The interrupt priority level on the IPL pins (0 = none, 7 = non-maskable). Read between instructions, once per instruction.</summary>
    int InterruptLevel { get; }

    /// <summary>The CPU acknowledges an interrupt of <paramref name="level"/> (the IACK cycle) and takes the vector number back; <c>24 + level</c> is the autovector the Genesis uses.
    /// <paramref name="cycles"/> is as for <see cref="Read"/> (the acknowledge falls at the end of that stretch).</summary>
    int Acknowledge(int level, int cycles);

    /// <summary>The RESET instruction asserted the reset pin (it resets external devices on boards that wire it, the Genesis does not).</summary>
    void ResetLine(int cycles);
}
