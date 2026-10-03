using System;
using NesEmulator.Systems;

namespace NesEmulator.Sega;

/// <summary>A clock derived from a machine's master clock by an integer divider: it has ticked <c>master / Divider</c> times when the master clock reads <c>master</c>.</summary>
public readonly record struct SegaClock(string Name, int Divider)
{
    /// <summary>How many times this clock has ticked when the master clock reads <paramref name="master"/>.</summary>
    public long CyclesAt(long master) => master / Divider;

    /// <summary>How many ticks fall in the half-open stretch (<paramref name="fromMaster"/>, <paramref name="toMaster"/>]. Summed over any chain of stretches this is exact, so a device
    /// that runs to a new master clock each time and asks this never gains or loses a cycle.</summary>
    public int Due(long fromMaster, long toMaster) => (int)(toMaster / Divider - fromMaster / Divider);

    /// <summary>The master clock at which this clock's <paramref name="cycle"/>-th tick lands.</summary>
    public long MasterAt(long cycle) => cycle * Divider;
}

/// <summary>
/// One machine's clock arithmetic, region-parameterised: the master clock, every device clock derived from it by an integer divider, and the line and frame lengths
/// in master clocks. A board holds one of these and a running master-clock counter; every device is told to <c>RunTo(master)</c> and works out its own cycles with
/// <see cref="SegaClock.Due"/>. Nothing in a board hard-codes an NTSC number: NTSC and PAL tables are both present and PAL is reachable headless from the start.
/// </summary>
/// <remarks>
/// Genesis: the master clock is 53.693175 MHz NTSC and 53.203424 MHz PAL; the 68000 takes master/7, the Z80 master/15 (and the PSG that sits behind it), the YM2612
/// master/7 (one FM sample every 144 of its clocks, 1008 master clocks), the VDP pixel clock master/10 in H32 and master/8 in H40 (the same 5.37 MHz as the Master System's); a line is 3420 master clocks in
/// both widths. Master System / Game Gear: three master clocks per Z80 cycle, two per VDP pixel, 228 Z80 cycles (684 master clocks, 342 pixels) per line.
/// Sources are the ones listed on <see cref="SegaTiming"/>.
/// </remarks>
public sealed class SegaTimeline
{
    public ConsoleKind Console { get; }
    public SegaRegion Region { get; }
    public double MasterHz { get; }
    public int MasterClocksPerLine { get; }
    public int LinesPerFrame { get; }

    /// <summary>The main processor: the 68000 on the Genesis, the Z80 on the Master System and Game Gear.</summary>
    public SegaClock Cpu { get; }
    /// <summary>The Z80: the sound processor on the Genesis, the main one elsewhere.</summary>
    public SegaClock Z80 { get; }
    /// <summary>The clock fed to the SN76489 (which divides it by 16 itself, see <see cref="Sn76489.Divider"/>).</summary>
    public SegaClock Psg { get; }
    /// <summary>The VDP pixel clock; on the Genesis this is the H32 value, see <see cref="VdpPixelClock"/>.</summary>
    public SegaClock Vdp { get; }
    /// <summary>The FM chip's input clock: the YM2612 on the Genesis (master/7), the optional YM2413 on the Master System (the Z80 clock, 3.579545 MHz NTSC).</summary>
    public SegaClock Fm { get; }

    private SegaTimeline(ConsoleKind console, SegaRegion region, double masterHz, int perLine, int lines,
                         SegaClock cpu, SegaClock z80, SegaClock psg, SegaClock vdp, SegaClock fm)
    {
        Console = console; Region = region; MasterHz = masterHz; MasterClocksPerLine = perLine; LinesPerFrame = lines;
        Cpu = cpu; Z80 = z80; Psg = psg; Vdp = vdp; Fm = fm;
    }

    public long MasterClocksPerFrame => (long)MasterClocksPerLine * LinesPerFrame;
    public double FramesPerSecond => MasterHz / MasterClocksPerFrame;
    public double SecondsPerFrame => MasterClocksPerFrame / MasterHz;

    /// <summary>Master clocks that elapse while <paramref name="clock"/> ticks <paramref name="cycles"/> times.</summary>
    public long MasterClocks(SegaClock clock, long cycles) => clock.MasterAt(cycles);

    /// <summary>Genesis only: the VDP pixel clock in the current width (master/8 in H40, master/10 in H32).</summary>
    public SegaClock VdpPixelClock(bool h40) =>
        Console == ConsoleKind.Genesis ? new SegaClock("VDP", h40 ? 8 : 10) : Vdp;

    /// <summary>Genesis only: the master clock reading at which the YM2612 has produced <paramref name="sample"/> samples.</summary>
    public long Ym2612SampleAt(long sample) => sample * SegaTiming.GenesisYm2612MasterClocksPerSample;

    /// <summary>The Genesis audio rate the YM2612 produces (master / 1008): 53,267 Hz NTSC, 52,780 Hz PAL.</summary>
    public double Ym2612SampleRate => MasterHz / SegaTiming.GenesisYm2612MasterClocksPerSample;

    public static SegaTimeline Genesis(SegaRegion region) => new(
        ConsoleKind.Genesis, region, SegaTiming.GenesisMasterHz(region),
        SegaTiming.GenesisMasterClocksPerLine, SegaTiming.GenesisLinesPerFrame(region),
        cpu: new SegaClock("68000", SegaTiming.GenesisM68kDivider),
        z80: new SegaClock("Z80", SegaTiming.GenesisZ80Divider),
        psg: new SegaClock("PSG", SegaTiming.GenesisPsgDivider),
        vdp: new SegaClock("VDP", 10),
        fm: new SegaClock("YM2612", SegaTiming.GenesisYm2612Divider));

    /// <summary>The Master System (and Game Gear and SG-1000 on the same board): the master clock is three Z80 cycles, two VDP pixels.</summary>
    public static SegaTimeline MasterSystem(SegaRegion region, ConsoleKind console = ConsoleKind.MasterSystem)
    {
        if (console is not (ConsoleKind.MasterSystem or ConsoleKind.GameGear)) throw new ArgumentOutOfRangeException(nameof(console));
        double z80 = SegaTiming.MasterSystemCpuHz(region);
        int line = SegaTiming.MasterSystemCpuCyclesPerLine * 3;
        return new(console, region, z80 * 3, line, SegaTiming.MasterSystemLinesPerFrame(region),
            cpu: new SegaClock("Z80", 3), z80: new SegaClock("Z80", 3), psg: new SegaClock("PSG", 3),
            vdp: new SegaClock("VDP", 2), fm: new SegaClock("YM2413", 3));
    }

    public static SegaTimeline For(ConsoleKind console, SegaRegion region) => console switch
    {
        ConsoleKind.Genesis => Genesis(region),
        ConsoleKind.MasterSystem or ConsoleKind.GameGear => MasterSystem(region, console),
        _ => throw new ArgumentOutOfRangeException(nameof(console), console, "not a Sega console"),
    };
}
