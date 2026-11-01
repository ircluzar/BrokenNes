namespace NesEmulator.Plugin;

/// <summary>
/// One NES sound channel of a running game, on its own: a bare chip that receives only that channel's register writes
/// (plus the shared frame counter), replayed at the clock the game made them, so its sound is the channel as the game
/// plays it, without the others. The plugin gives each channel to a separate FL instance this way.
/// <para>Not thread-safe (the emulator's lock).</para>
/// </summary>
public sealed class ApuStem
{
    private readonly NesApuInstrument chip;
    private readonly int channel;
    private long clock;

    /// <param name="channel">0 pulse 1, 1 pulse 2, 2 triangle, 3 noise.</param>
    /// <param name="startClock">The game's APU clock now; <paramref name="latch"/>/<paramref name="written"/> its register state, applied so a stem made mid-game joins in tune.</param>
    public ApuStem(string coreId, int channel, int hostRate, long startClock, byte[] latch, uint written)
    {
        this.channel = channel;
        chip = new NesApuInstrument(coreId, hostRate, (byte)(1 << channel));
        clock = startClock;
        for (int r = 0; r < 0x18; r++)
            if ((written & (1u << r)) != 0) Apply(r, latch[r]);
    }

    public string CoreId => chip.CoreId;
    public int Available => chip.Available;
    public int ReadAvailable(Span<float> dest) => chip.ReadAvailable(dest);
    public void SetHostRate(int rate) => chip.HostSampleRate = rate;

    public void SetCore(string id) => chip.SetCore(id);

    private bool Owns(int reg) => reg is >= 0 and < 0x10 && (reg >> 2) == channel;

    private void Apply(int reg, byte value)
    {
        if (Owns(reg)) chip.Write(reg, value);
        else if (reg == 0x15) chip.Write(0x15, (byte)(value & (1 << channel)));   // the game enables its channels; this chip has only its own
        else if (reg == 0x17) chip.Write(0x17, value);                              // the frame counter clocks every channel's envelope and length
    }

    /// <summary>Replays one frame of the game's writes and runs the chip to the end of the frame.</summary>
    public void Replay(ReadOnlySpan<NesAudioMachine.ApuWrite> log, long frameEndClock)
    {
        foreach (var w in log)
        {
            if (w.Clock > clock) { chip.Advance(w.Clock - clock); clock = w.Clock; }
            Apply(w.Reg, w.Value);
        }
        if (frameEndClock > clock) { chip.Advance(frameEndClock - clock); clock = frameEndClock; }
    }
}
