namespace NesEmulator.Sega;

/// <summary>
/// What the Z80 sees of the machine. Implement it as a <b>struct</b> that holds a reference to the board and pass it to <see cref="Z80{TBus}"/>: the
/// JIT then compiles each bus call as a direct (inlinable) call instead of an interface dispatch, which is what makes the generic core fast and keeps it
/// friendly to AOT and WebAssembly (no reflection, no delegates).
/// </summary>
/// <remarks>
/// <para>Every method is called at the T-state where the real chip completes the access, from inside <see cref="Z80{TBus}.Tick"/>: a memory read or
/// write is the third T-state of its machine cycle (the second one is where the WAIT pin is sampled), an opcode fetch is the third T-state of M1, an
/// I/O access the fourth. A board that keeps its own clock can therefore read <see cref="Z80{TBus}.TotalCycles"/> inside the call to learn the
/// exact time of the access.</para>
/// <para>Port addresses are the full 16-bit address bus value, as on hardware: <c>IN A,(n)</c> and <c>OUT (n),A</c> put A on the high byte, <c>IN r,(C)</c>
/// and the block I/O instructions put B there. The Master System decodes only A7, A6 and A0, so it ignores the upper byte.</para>
/// </remarks>
public interface IZ80Bus
{
    /// <summary>The M1 (instruction fetch) read at <paramref name="address"/>. Usually the same as <see cref="Read"/>; it is separate so a board can tell fetches from data reads.</summary>
    byte FetchOpcode(ushort address);

    /// <summary>A memory read.</summary>
    byte Read(ushort address);

    /// <summary>A memory write.</summary>
    void Write(ushort address, byte value);

    /// <summary>An I/O read (IORQ + RD). <paramref name="port"/> is the whole 16-bit address bus.</summary>
    byte In(ushort port);

    /// <summary>An I/O write (IORQ + WR). <paramref name="port"/> is the whole 16-bit address bus.</summary>
    void Out(ushort port, byte value);

    /// <summary>
    /// The byte on the data bus during an interrupt acknowledge (IORQ + M1), for as long as the interrupt is being taken. In IM 2 it is the low byte of the
    /// vector address; in IM 0 it is the instruction the device forces (a <c>RST n</c> opcode in practice; the Master System returns $FF = <c>RST 38h</c>).
    /// Not used in IM 1.
    /// </summary>
    byte InterruptVector();
}
