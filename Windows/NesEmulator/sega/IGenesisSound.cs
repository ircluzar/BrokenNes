using System;
using System.IO;

namespace NesEmulator.Sega;

/// <summary>
/// The Genesis sound subsystem as the board sees it: a Z80 with 8 KB of RAM, a YM2612 and the PSG, which on hardware is a second computer sitting beside the 68000
/// and sharing parts of its address space. It lives in the board's <b>APU slot</b>, like <c>ISnesApu</c>, but a plain four-port interface is not enough: the 68000 and
/// the Z80 hand a bus back and forth, so the contract carries that handshake (BUSREQ, RESET), the Z80 RAM window the 68000 sees, the Z80's access to 68000 space
/// through its bank window, and the VDP's vertical-blank interrupt into the Z80.
/// </summary>
/// <remarks>
/// <para>Every call that can change what the unit does carries the master-clock reading it happens at, and the unit runs itself up to that reading first. The unit never
/// steps instruction by instruction on the board's behalf: the board says "run until master clock N" (<see cref="RunTo"/>) before each access and once per frame.</para>
/// <para>The 68000's view (<see cref="Read"/>, <see cref="Write"/>, offset 0 to $FFFF = addresses $A00000 to $A0FFFF): $0000-$1FFF Z80 RAM, $2000-$3FFF its mirror,
/// $4000-$5FFF the YM2612 (four ports repeating), $6000-$60FF the bank register, $7F00-$7FFF VDP window (the PSG at $7F11 and its mirrors), $8000-$FFFF the Z80's
/// view of the 68000's own space (not reachable from the 68000 side). The Z80 owns this space unless the 68000 holds the bus.</para>
/// <para>The Z80's view through the bank window: addresses $8000-$FFFF are the 32 KB of 68000 space selected by the 9-bit bank register (bank &lt;&lt; 15), reached
/// through <see cref="IGenesisSoundHost"/>, which also tells the unit how long the 68000's bus makes the Z80 wait.</para>
/// </remarks>
public interface IGenesisSound
{
    string CoreName { get; }

    /// <summary>Native output rate; <see cref="ReadSamples"/> delivers this rate (the unit resamples its chips to it).</summary>
    int SampleRate { get; }

    /// <summary>The board hands the unit its connection to 68000 space (the Z80's bank window). Called once, before the first <see cref="RunTo"/>.</summary>
    void Attach(IGenesisSoundHost host);

    /// <summary>Power-on state: the Z80 held in reset, the bus not requested, the bank register cleared, the YM2612 and PSG silent.</summary>
    void Reset();

    /// <summary>Advance the unit to the given master-clock reading.</summary>
    void RunTo(long masterClock);

    // ---- the bus handshake ($A11100 and $A11200) ----

    /// <summary>The 68000 writes $A11100: <paramref name="requested"/> asks the Z80 to give up the bus (the Z80 grants it at the end of its current machine cycle); false takes the request back and the Z80 runs on.</summary>
    void WriteBusRequest(bool requested, long masterClock);

    /// <summary>What reading $A11100 reports: true once the Z80 has granted the bus (the register's bit 0 reads as the inverse). Polled by the 68000 before it touches Z80 RAM.</summary>
    bool IsBusGranted(long masterClock);

    /// <summary>The 68000 writes $A11200: false asserts the Z80's RESET line (which also resets the YM2612), true releases it and the Z80 starts again at $0000.</summary>
    void WriteReset(bool released, long masterClock);

    /// <summary>True while RESET is asserted.</summary>
    bool IsInReset { get; }

    // ---- the 68000's window into the unit ----

    /// <summary>A byte read at <c>$A00000 + offset</c>. Only reaches the unit while the 68000 holds the bus; otherwise the unit answers with the open-bus value.</summary>
    byte Read(int offset, long masterClock);

    /// <summary>A byte written at <c>$A00000 + offset</c>. Lost unless the 68000 holds the bus.</summary>
    void Write(int offset, byte value, long masterClock);

    /// <summary>The VDP's vertical-blank interrupt line into the Z80 (<paramref name="asserted"/> true for the stretch it is held, false when it drops).</summary>
    void SetInterrupt(bool asserted, long masterClock);

    /// <summary>The 68000 writing the PSG through the VDP's PSG port ($C00011 and its mirrors): reaches the same chip the Z80 does at $7F11.</summary>
    void WritePsg(byte value, long masterClock);

    // ---- sound out ----

    /// <summary>Move queued stereo samples (interleaved L,R) into <paramref name="dest"/>. Returns the number of shorts written (always even).</summary>
    int ReadSamples(Span<short> dest);

    // ---- state and probes ----

    void SaveState(BinaryWriter w);
    void LoadState(BinaryReader r);

    /// <summary>One-line human-readable state for probes and logs.</summary>
    string Describe();
}

/// <summary>What the sound unit needs from the 68000's side: the Z80's reads and writes through its bank window.</summary>
public interface IGenesisSoundHost
{
    /// <summary>The Z80 reads the byte at 68000 address <paramref name="address"/> (24 bits). <paramref name="waitMasterClocks"/> is how long the Z80 must stall for it: the 68000's bus is
    /// busy with its own cycle, a DMA, or a refresh, and the Z80 gets its turn after that.</summary>
    byte ReadBus(int address, long masterClock, out int waitMasterClocks);

    /// <summary>The Z80 writes the byte at 68000 address <paramref name="address"/> (it reaches the VDP and I/O, not ROM).</summary>
    void WriteBus(int address, byte value, long masterClock, out int waitMasterClocks);
}
