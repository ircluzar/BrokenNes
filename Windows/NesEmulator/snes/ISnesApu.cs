using System;

namespace NesEmulator.Snes;

/// <summary>
/// The SNES audio unit as the board sees it: four bidirectional I/O ports ($2140-$2143) plus a
/// clock to catch up to. On hardware the audio unit is its own computer (SPC700 + S-DSP + 64KB
/// ARAM) running from its own 24.576 MHz crystal, so the board never steps it instruction by
/// instruction - it tells it "run until master clock N" before every port access and once per frame.
///
/// The two port directions are separate latches: WritePort never changes what ReadPort returns.
/// </summary>
public interface ISnesApu
{
    string CoreName { get; }
    void Reset();

    /// <summary>CPU reads $2140+port (the value the audio unit last wrote to that port).</summary>
    byte ReadPort(int port);

    /// <summary>CPU writes $2140+port (the audio unit sees it on its $F4+port).</summary>
    void WritePort(int port, byte value);

    /// <summary>Advance the audio unit to the given SNES master clock (21.477 MHz ticks).</summary>
    void RunTo(long masterClock);

    /// <summary>Native output rate of this unit (32000 for a real S-DSP).</summary>
    int SampleRate { get; }

    /// <summary>Move queued stereo samples (interleaved L,R) into dest. Returns the number of shorts written.</summary>
    int ReadSamples(Span<short> dest);

    /// <summary>One-line human-readable state for probes and logs.</summary>
    string Describe();
}
