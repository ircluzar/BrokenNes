// The x86 PC's machine configuration (docs/projects/x86-modular-pc.md, M1): which parts the board is built with. Written for BrokenNes.
// The default config is the machine the x86 console always was (486-class CPU, 16 MB, BIOS-only disks): it builds nothing new, so a
// default machine runs and saves byte for byte as before. A state saved with another config carries a "PCFG" block and only loads into
// a machine built with the same config (a mismatch is a visible error, never a silent re-key).

using System;
using System.IO;

namespace NesEmulator.X86;

public enum PcStorage { BiosOnly = 0, Ide = 1 }

public sealed record PcConfig
{
    /// <summary>CPU generation the core is built as: 3 = 386, 4 = 486 (5 = Pentium: flags mask only until the CPU-model lane lands).</summary>
    public int CpuGen { get; init; } = 4;
    public int MemoryMB { get; init; } = 16;
    /// <summary>BiosOnly: disks reach DOS through INT 13h only. Ide: a real ATA controller (1F0h/IRQ 14, 170h/IRQ 15) on the same disks, plus an ATAPI CD-ROM.</summary>
    public PcStorage Storage { get; init; } = PcStorage.BiosOnly;
    /// <summary>Boot order (IDE machines): 'C' CD-ROM, 'H' hard disk, 'F' floppy.</summary>
    public string BootOrder { get; init; } = "FH";

    public static readonly PcConfig Default = new();
    public static readonly PcConfig Dos386 = new() { CpuGen = 3 };
    public static readonly PcConfig Dos486 = new() { CpuGen = 4, MemoryMB = 16, Storage = PcStorage.Ide };
    public static readonly PcConfig Win98 = new() { CpuGen = 5, MemoryMB = 64, Storage = PcStorage.Ide, BootOrder = "CHF" };

    public bool IsDefault => this == Default;

    public static PcConfig Preset(string name) => name.ToUpperInvariant() switch
    {
        "DEFAULT" => Default, "DOS-386" => Dos386, "DOS-486" => Dos486, "WIN98-P166" or "WIN98" => Win98,
        _ => throw new ArgumentException($"no x86 machine preset '{name}' (DEFAULT, DOS-386, DOS-486, WIN98-P166)"),
    };

    public void Validate()
    {
        if (CpuGen is < 3 or > 5) throw new ArgumentException("CpuGen is 3, 4 or 5");
        if (MemoryMB is < 1 or > 128) throw new ArgumentException("MemoryMB is 1..128");
        foreach (char c in BootOrder) if (c is not ('C' or 'H' or 'F')) throw new ArgumentException("BootOrder uses C, H, F");
    }

    public void Write(BinaryWriter w) { w.Write(1); w.Write(CpuGen); w.Write(MemoryMB); w.Write((int)Storage); w.Write(BootOrder); }
    public static PcConfig Read(BinaryReader r)
    {
        int v = r.ReadInt32();
        if (v != 1) throw new InvalidDataException($"x86 machine config block version {v} is not known");
        return new PcConfig { CpuGen = r.ReadInt32(), MemoryMB = r.ReadInt32(), Storage = (PcStorage)r.ReadInt32(), BootOrder = r.ReadString() };
    }

    public override string ToString() => $"{(CpuGen == 3 ? "386" : CpuGen == 4 ? "486" : "586")}, {MemoryMB} MB, {(Storage == PcStorage.Ide ? "IDE" : "BIOS disks")}, boot {BootOrder}";
}
