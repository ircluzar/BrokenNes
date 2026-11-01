// Round 9 (pitch bends): the APU registers as each NMI of the music test ROM left them, for the checks (FruityHost midi-test, the plugin's hook 611 with its per-NMI flag).
//
// Why: a frame's music is written by the ROM's NMI, which starts at the beginning of vblank, near the END of the emulator's frame (RunFrame returns at the end of the
// last vblank line). VRUN's bend decoders take more CPU time than the plain ones, so on a busy frame (all channels decode, and the AudioPlayer redraws its status line
// once a second) the NMI is still writing the last channels when RunFrame returns: a latch read there shows those registers one frame late (measured: 0 to 16 field-
// frames in 2,400 per song with the round-9 template; none with round 7's). The values are right, the sample point is not. Here every write the ROM makes ($4000-$4017,
// with the APU clock) is kept, and frame i's registers are those after every write made before the middle of frame i+1: the NMI of frame i is over long before that
// (it lasts at most a few thousand cycles of the frame's 29,780), and the NMI of frame i+1 has not begun (it starts in vblank, at the frame's end).
//
// Pure code, tests only (it allocates; the plugin calls it from a test hook, never on the audio thread).
namespace BrokenNes2.Music;

public sealed class NmiLatch
{
    public const int Bytes = 0x18;

    private readonly byte[] start;
    private readonly List<(long Clock, int Reg, byte Value)> writes = [];
    private readonly List<long> frameStarts = [];

    /// <summary>A recorder for one machine, from its registers as they are now (its latch before the first frame recorded).</summary>
    public NmiLatch(ReadOnlySpan<byte> latchNow)
    {
        start = latchNow.ToArray();
        if (start.Length != Bytes) Array.Resize(ref start, Bytes);
    }

    /// <summary>Every write the machine makes to $4000-$4017 (its APU write tap: address, value, APU clock).</summary>
    public void OnWrite(ushort address, byte value, long clock)
    {
        if (address is >= 0x4000 and < 0x4000 + Bytes) writes.Add((clock, address - 0x4000, value));
    }

    /// <summary>After each frame: the APU clock at which that frame started (NesAudioMachine.FrameStartClock).</summary>
    public void FrameRan(long frameStartClock) => frameStarts.Add(frameStartClock);

    public int Frames => frameStarts.Count;

    /// <summary>The registers after each recorded frame's NMI, <see cref="Bytes"/> per frame. The last frame's are all the writes made so far (its NMI may go on into a
    /// frame not run yet: run one frame more than you compare).</summary>
    public byte[] PerNmi()
    {
        var o = new byte[frameStarts.Count * Bytes];
        var reg = (byte[])start.Clone();
        int w = 0;
        for (int i = 0; i < frameStarts.Count; i++)
        {
            long limit = i + 1 < frameStarts.Count ? frameStarts[i + 1] + (frameStarts[i + 1] - frameStarts[i]) / 2 : long.MaxValue;
            while (w < writes.Count && writes[w].Clock < limit) { reg[writes[w].Reg] = writes[w].Value; w++; }
            reg.CopyTo(o, i * Bytes);
        }
        return o;
    }
}
