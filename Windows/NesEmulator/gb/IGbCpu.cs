using System.IO;

namespace NesEmulator.Gb;

/// <summary>
/// What BOARD_GB needs from the CPU it runs. CPU_GB (the SM83) is the real one; cross-console experiments plug other
/// CPUs in through a factory to see what a Game Boy game does on them.
/// </summary>
public interface IGbCpu
{
    string Id { get; }
    void ResetPostBoot(GbModel model, bool cgbGame);
    /// <summary>One instruction, one interrupt dispatch or one halted M-cycle.</summary>
    void Step();
    ushort PC { get; }
    bool Halted { get; }
    bool Stopped { get; }
    bool Locked { get; }
    long Instructions { get; }
    void Wake();
    void SaveState(BinaryWriter w);
    void LoadState(BinaryReader r);
}
