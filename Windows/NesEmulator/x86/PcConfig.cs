// The x86 PC's machine configuration (docs/projects/x86-modular-pc.md, M1): which parts the board is built with. Written for BrokenNes.
// The default config is the machine the x86 console always was (486-class CPU, 16 MB, BIOS-only disks): it builds nothing new, so a
// default machine runs and saves byte for byte as before. A state saved with another config carries a "PCFG" block and only loads into
// a machine built with the same config (a mismatch is a visible error, never a silent re-key).

using System;
using System.IO;

namespace NesEmulator.X86;

public enum PcStorage { BiosOnly = 0, Ide = 1 }

/// <summary>Isa: the classic AT board. Pci430TX: an i430TX host bridge + PIIX4 (ISA bridge, IDE) on PCI bus 0, config mechanism #1 (CF8h/CFCh), and the BIOS's
/// Windows 98 feature set (PCI BIOS INT 1Ah B1xxh + BIOS32 + $PIR, APM 1.2, PnP BIOS).</summary>
public enum PcBus { Isa = 0, Pci430TX = 1 }

public sealed record PcConfig
{
    /// <summary>CPU generation the core is built as: 3 = 386, 4 = 486 (5 = Pentium: flags mask only until the CPU-model lane lands).</summary>
    public int CpuGen { get; init; } = 4;
    public int MemoryMB { get; init; } = 16;
    /// <summary>BiosOnly: disks reach DOS through INT 13h only. Ide: a real ATA controller (1F0h/IRQ 14, 170h/IRQ 15) on the same disks, plus an ATAPI CD-ROM.</summary>
    public PcStorage Storage { get; init; } = PcStorage.BiosOnly;
    /// <summary>Boot order (IDE machines): 'C' CD-ROM, 'H' hard disk, 'F' floppy.</summary>
    public string BootOrder { get; init; } = "FH";
    public PcBus Bus { get; init; } = PcBus.Isa;
    /// <summary>A 3dfx Voodoo Graphics (Voodoo 1) PCI add-in card beside the 2D card (PCI board only; off by default). Config block version 3.</summary>
    public bool Voodoo { get; init; }
    /// <summary>No floppy controller and no drive (CMOS 10h = 0): the nano98 game seat (floppy games belong to the DOS path). Config block version 4.</summary>
    public bool NoFloppy { get; init; }
    /// <summary>No network card on the PCI bus: the nano98 seat base is installed without one, so a card there would make Windows run the
    /// Add New Hardware wizard on every package boot. Config block version 5.</summary>
    public bool NoNic { get; init; }
    /// <summary>VBOXVGA video memory in MB (0 = the 4 MB default; 8 or 16 for true colour at 1600x900 / 1920x1080). Ignored by other cards.</summary>
    public int VramMB { get; init; }

    public static readonly PcConfig Default = new();
    public static readonly PcConfig Dos386 = new() { CpuGen = 3 };
    public static readonly PcConfig Dos486 = new() { CpuGen = 4, MemoryMB = 16, Storage = PcStorage.Ide };
    public static readonly PcConfig Win98 = new() { CpuGen = 5, MemoryMB = 64, Storage = PcStorage.Ide, BootOrder = "CHF", Bus = PcBus.Pci430TX };
    public static readonly PcConfig Win98Voodoo = Win98 with { Voodoo = true };
    public static readonly PcConfig Win98Nano = Win98 with { NoFloppy = true, NoNic = true, BootOrder = "CH" };
    public static readonly PcConfig Win98VoodooNano = Win98Voodoo with { NoFloppy = true, NoNic = true, BootOrder = "CH" };
    /// <summary>nano98 seat with 256 MB (user 2026-11-22: big late-90s games; the 430TX takes 256 MB, Win98 SE runs well up to 512 MB).</summary>
    public static readonly PcConfig Win98Nano256 = Win98Nano with { MemoryMB = 256 };

    public bool IsDefault => this == Default;

    public static PcConfig Preset(string name) => name.ToUpperInvariant() switch
    {
        "DEFAULT" => Default, "DOS-386" => Dos386, "DOS-486" => Dos486, "WIN98-P166" or "WIN98" => Win98, "WIN98-P166-VOODOO" => Win98Voodoo, "WIN98-P166-NANO" => Win98Nano, "WIN98-P166-VOODOO-NANO" => Win98VoodooNano,
        // the Pentium II 400 machines: the same board (the CPU model comes from PcMachinePreset), so a P166 disk boots on either
        "WIN98-P2" => Win98, "WIN98-P2-VOODOO" => Win98Voodoo, "WIN98-P2-NANO" => Win98Nano, "WIN98-P166-NANO-256" or "WIN98-P2-NANO-256" => Win98Nano256,
        "WIN98-P166-WIDE" => Win98 with { VramMB = 16 }, "WIN98-P166-WIDE-NANO" => Win98Nano with { VramMB = 16 },
        _ => throw new ArgumentException($"no x86 machine preset '{name}' (DEFAULT, DOS-386, DOS-486, WIN98-P166)"),
    };

    public void Validate()
    {
        if (CpuGen is < 3 or > 5) throw new ArgumentException("CpuGen is 3, 4 or 5");
        if (Voodoo && Bus != PcBus.Pci430TX) throw new ArgumentException("the Voodoo needs the PCI board");
        if (MemoryMB is < 1 or > 256) throw new ArgumentException("MemoryMB is 1..256");
        foreach (char c in BootOrder) if (c is not ('C' or 'H' or 'F')) throw new ArgumentException("BootOrder uses C, H, F");
    }

    // version 1 (no bus field) is still written for an ISA board, so those states stay byte for byte what they were
    public void Write(BinaryWriter w)
    {
        w.Write(NoNic ? 5 : NoFloppy ? 4 : Voodoo ? 3 : Bus == PcBus.Isa ? 1 : 2); w.Write(CpuGen); w.Write(MemoryMB); w.Write((int)Storage); w.Write(BootOrder);
        if (Bus != PcBus.Isa) w.Write((int)Bus);
        if (Voodoo || NoFloppy || NoNic) w.Write(Voodoo ? 1 : 0);   // version 3: the 3D card
        if (NoFloppy || NoNic) w.Write(NoFloppy ? 1 : 0);   // version 4: no floppy controller
        if (NoNic) w.Write(1);   // version 5: no network card
    }
    public static PcConfig Read(BinaryReader r)
    {
        int v = r.ReadInt32();
        if (v is not (1 or 2 or 3 or 4 or 5)) throw new InvalidDataException($"x86 machine config block version {v} is not known");
        var c = new PcConfig { CpuGen = r.ReadInt32(), MemoryMB = r.ReadInt32(), Storage = (PcStorage)r.ReadInt32(), BootOrder = r.ReadString() };
        if (v >= 2) c = c with { Bus = (PcBus)r.ReadInt32() };
        if (v >= 3) c = c with { Voodoo = r.ReadInt32() != 0 };
        if (v >= 4) c = c with { NoFloppy = r.ReadInt32() != 0 };
        if (v >= 5) c = c with { NoNic = r.ReadInt32() != 0 };
        return c;
    }

    public override string ToString() => $"{(CpuGen == 3 ? "386" : CpuGen == 4 ? "486" : "586")}, {MemoryMB} MB, {(Storage == PcStorage.Ide ? "IDE" : "BIOS disks")}, boot {BootOrder}{(Bus == PcBus.Pci430TX ? ", PCI 430TX" : "")}{(Voodoo ? ", Voodoo" : "")}{(NoFloppy ? ", no floppy" : "")}{(NoNic ? ", no NIC" : "")}";
}

/// <summary>A whole machine the app offers (the desktop's "x86 machine" menu): the board config, the CPU model and the video card.</summary>
public sealed record PcMachinePreset(string Key, string Label, PcConfig Config, CpuModel? Cpu, PcVideo Video)
{
    public const string ClassicKey = "classic";
    public bool IsClassic => Key == ClassicKey;

    /// <summary>Classic = the x86 console as it always was (its CPU core menu and IPS); the others are fixed CPU models.</summary>
    public static readonly PcMachinePreset[] All =
    {
        new(ClassicKey, "Classic (486-class, VGA)", PcConfig.Default, null, PcVideo.Vga),
        new("dos-386", "DOS 386DX-33, VGA", PcConfig.Dos386, CpuModel.I386DX, PcVideo.Vga),
        new("dos-486", "DOS 486DX2-66, VGA, IDE", PcConfig.Dos486, CpuModel.I486DX2, PcVideo.Vga),
        new("win98-p166", "Win98 Pentium MMX 166, S3 Trio64, PCI", PcConfig.Win98, CpuModel.PentiumMmx, PcVideo.S3Trio64),
        new("win98-p166-geforce", "Win98 Pentium MMX 166, GeForce 256 DDR 32 MB, PCI", PcConfig.Win98, CpuModel.PentiumMmx, PcVideo.GeForce256),
        new("win98-p166-wide", "Win98 Pentium MMX 166, VirtualBox VGA 16 MB (widescreen, boxv9x), PCI", PcConfig.Win98 with { VramMB = 16 }, CpuModel.PentiumMmx, PcVideo.VBoxVga),
        new("win98-p166-wide-nano", "Win98 Pentium MMX 166, VirtualBox VGA 16 MB (widescreen, boxv9x), PCI, no floppy", PcConfig.Win98Nano with { VramMB = 16 }, CpuModel.PentiumMmx, PcVideo.VBoxVga),
        new("win98-p166-voodoo", "Win98 Pentium MMX 166, S3 Trio64 + 3dfx Voodoo, PCI", PcConfig.Win98Voodoo, CpuModel.PentiumMmx, PcVideo.S3Trio64),
        new("win98-p166-nano", "nano98 seat: Win98 Pentium MMX 166, S3 Trio64, PCI, no floppy", PcConfig.Win98Nano, CpuModel.PentiumMmx, PcVideo.S3Trio64),
        new("win98-p166-voodoo-nano", "nano98 seat: Win98 Pentium MMX 166, S3 Trio64 + 3dfx Voodoo, PCI, no floppy", PcConfig.Win98VoodooNano, CpuModel.PentiumMmx, PcVideo.S3Trio64),
        new("win98-p2", "Win98 Pentium II 400, S3 Trio64, PCI", PcConfig.Win98, CpuModel.PentiumII, PcVideo.S3Trio64),
        new("win98-p2-voodoo", "Win98 Pentium II 400, S3 Trio64 + 3dfx Voodoo, PCI", PcConfig.Win98Voodoo, CpuModel.PentiumII, PcVideo.S3Trio64),
        new("win98-p2-nano", "nano98 seat: Win98 Pentium II 400, S3 Trio64, PCI, no floppy", PcConfig.Win98Nano, CpuModel.PentiumII, PcVideo.S3Trio64),
        new("win98-p166-nano-256", "nano98 seat: Win98 Pentium MMX 166, 256 MB, S3 Trio64, PCI, no floppy", PcConfig.Win98Nano256, CpuModel.PentiumMmx, PcVideo.S3Trio64),
        new("win98-p2-nano-256", "nano98 seat: Win98 Pentium II 400, 256 MB, S3 Trio64, PCI, no floppy", PcConfig.Win98Nano256, CpuModel.PentiumII, PcVideo.S3Trio64),
    };

    /// <summary>The preset for a key (case-insensitive); an unknown or empty key is the classic machine.</summary>
    public static PcMachinePreset Find(string? key) => System.Array.Find(All, p => p.Key.Equals(key ?? "", System.StringComparison.OrdinalIgnoreCase)) ?? All[0];
    public static bool Known(string key) => System.Array.Exists(All, p => p.Key.Equals(key, System.StringComparison.OrdinalIgnoreCase));
}
