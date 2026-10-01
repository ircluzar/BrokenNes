namespace NesEmulator.Plugin;

/// <summary>
/// Streaming mono resampler from a core's native rate (NES APUs 44100 Hz, the Game Boy bridge
/// 48000 Hz, ...) to the host's rate. 4-point, 3rd-order Hermite interpolation: cheap, smooth,
/// and the cores already low-pass their output. No allocation after construction; input and
/// output are pushed and pulled in blocks of any size.
/// </summary>
public sealed class StreamResampler
{
    private readonly float[] buffer;
    private int count;          // samples held in buffer
    private double position;    // read position in buffer, between sample 1 and count-3
    private double step;        // input samples per output sample

    public StreamResampler(int capacity = 1 << 15)
    {
        buffer = new float[capacity];
        Clear();
    }

    public int InputRate { get; private set; } = 44100;
    public int OutputRate { get; private set; } = 44100;

    public void SetRates(int inputRate, int outputRate)
    {
        if (inputRate <= 0 || outputRate <= 0) throw new ArgumentOutOfRangeException(nameof(inputRate));
        InputRate = inputRate; OutputRate = outputRate;
        step = (double)inputRate / outputRate;
    }

    /// <summary>Drops everything buffered (after a core swap or a reset).</summary>
    public void Clear()
    {
        Array.Clear(buffer);
        count = 3;              // three samples of silence of history for the first interpolation
        position = 1;
        step = (double)InputRate / OutputRate;
    }

    /// <summary>How many output samples the buffered input can produce right now.</summary>
    public int Available => Math.Max(0, (int)Math.Floor((count - 3 - position) / step) + 1);

    /// <summary>Input samples still needed to produce <paramref name="outputCount"/> outputs.</summary>
    public int InputNeeded(int outputCount)
    {
        double last = position + (outputCount - 1) * step;
        return Math.Max(0, (int)Math.Ceiling(last) + 3 - count);
    }

    /// <summary>Space left for input before the oldest samples must be consumed.</summary>
    public int FreeSpace => buffer.Length - count;

    public void Write(ReadOnlySpan<float> input)
    {
        if (input.Length > FreeSpace) throw new InvalidOperationException("resampler input overflow: read more output first");
        input.CopyTo(buffer.AsSpan(count));
        count += input.Length;
    }

    /// <summary>Writes up to output.Length samples; returns how many.</summary>
    public int Read(Span<float> output)
    {
        int n = Math.Min(output.Length, Available);
        for (int i = 0; i < n; i++)
        {
            int k = (int)position;
            float t = (float)(position - k);
            float y0 = buffer[k - 1], y1 = buffer[k], y2 = buffer[k + 1], y3 = buffer[k + 2];
            float c1 = 0.5f * (y2 - y0);
            float c2 = y0 - 2.5f * y1 + 2f * y2 - 0.5f * y3;
            float c3 = 0.5f * (y3 - y0) + 1.5f * (y1 - y2);
            output[i] = ((c3 * t + c2) * t + c1) * t + y1;
            position += step;
        }
        // Keep one sample of history before the read position; shift the rest down.
        int drop = (int)position - 1;
        if (drop > 0)
        {
            buffer.AsSpan(drop, count - drop).CopyTo(buffer);
            count -= drop;
            position -= drop;
        }
        return n;
    }
}
