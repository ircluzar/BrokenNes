using System;

namespace NesEmulator.Sega;

/// <summary>
/// The optional parts a Sega board can be built with: a CPU, a picture chip and a sound unit supplied by the caller (the cross-console bridges and the mix lab), each one
/// null for "build the stock part". Every Sega board constructor takes one of these from the first commit, so the bridges never need a board retrofitted
/// (<c>BOARD_SFC</c> had to be, at 25b8c3b; this is the shape it ended up with).
/// </summary>
/// <remarks>
/// The rules, which every <c>BOARD_SMS</c> / <c>BOARD_MD</c> follows:
/// <list type="bullet">
/// <item>A null factory means the stock part. A factory is called once, while the board is being built.</item>
/// <item>The CPU factory receives the board as the CPU's bus. A factory that hands the CPU a wrapper instead of the board gets the interface bus path, not any fast path
/// that assumes the bus is the board.</item>
/// <item>Whatever picture chip is supplied, the board installs its own hooks on it (counter latch, interrupt line, timing); a supplied chip does not have to know the board.</item>
/// <item>The sound unit is told the machine's region and model through <see cref="SegaMachine"/>, not through its constructor, so a factory is just a lambda.</item>
/// </list>
/// </remarks>
/// <typeparam name="TBus">What the CPU sees as its bus (<c>IZ80Bus</c> on the Master System, the 68000 bus on the Genesis).</typeparam>
/// <typeparam name="TCpu">The CPU type the board drives.</typeparam>
/// <typeparam name="TPpu">The picture chip type.</typeparam>
/// <typeparam name="TApu">The sound unit type (a <c>Sn76489</c> wrapper on the Master System, <see cref="IGenesisSound"/> on the Genesis).</typeparam>
public sealed class SegaBoardParts<TBus, TCpu, TPpu, TApu>
{
    public Func<TBus, TCpu>? Cpu { get; init; }
    public Func<TPpu>? Ppu { get; init; }
    public Func<SegaMachine, TApu>? Apu { get; init; }

    /// <summary>All stock parts.</summary>
    public static SegaBoardParts<TBus, TCpu, TPpu, TApu> Stock { get; } = new();

    /// <summary>True when nothing is replaced.</summary>
    public bool IsStock => Cpu == null && Ppu == null && Apu == null;

    public TCpu BuildCpu(TBus bus, Func<TBus, TCpu> stock) => Cpu != null ? Cpu(bus) : stock(bus);
    public TPpu BuildPpu(Func<TPpu> stock) => Ppu != null ? Ppu() : stock();
    public TApu BuildApu(SegaMachine machine, Func<SegaMachine, TApu> stock) => Apu != null ? Apu(machine) : stock(machine);
}

/// <summary>What a sound unit (or any part built by a factory) needs to know about the machine it is in.</summary>
/// <param name="Timeline">The master clock and the device clocks derived from it, for this region.</param>
/// <param name="GenesisModel">Which Genesis board (YM2612 or YM3438 behaviour and the rest); ignored on the Master System.</param>
/// <param name="SampleRate">The audio rate the host wants <c>ReadSamples</c> in.</param>
public sealed record SegaMachine(SegaTimeline Timeline, GenesisModel GenesisModel, int SampleRate)
{
    public SegaRegion Region => Timeline.Region;
}
