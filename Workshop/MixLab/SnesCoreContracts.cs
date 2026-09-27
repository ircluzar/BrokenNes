using System;
using System.Collections.Generic;
using NesEmulator.Snes;

namespace BrokenNes.Workshop.MixLab;

// =====================================================================================================
// MIX LAB (exploration only - not for main). The bridges between the NES and SNES core families talk to
// these contracts, never to a concrete core, so every SNES core registered here pairs with every NES core
// CoreRegistry knows, in both directions. Adding a second SNES CPU or PPU = one adapter + one Register
// line below; no bridge changes.
//
// The SFC cores do not implement these yet (they are concrete classes on the SNES branch); the adapters in
// SfcAdapters.cs wrap them. Where a contract needs state the SFC core keeps private (the PPU's register
// file), the adapter reads it by reflection - itself a finding: a real ISnesPpu needs a state snapshot.
// =====================================================================================================

/// <summary>A 65C816-compatible CPU core driven one instruction at a time over an <see cref="ISnesBus"/>.</summary>
public interface ISnesCpuCore
{
    string Id { get; }
    void Reset();
    /// <summary>Execute one instruction (or take one pending interrupt).</summary>
    void Step();
    void RaiseNmi();
    void SetIrq(bool asserted);
    bool Stopped { get; }
    // Register file, for NES<->SNES CPU hot-swaps (emulation mode = the 6502 view).
    ushort A { get; set; }
    ushort X { get; set; }
    ushort Y { get; set; }
    ushort S { get; set; }
    ushort PC { get; set; }
    byte P { get; set; }
    bool E { get; set; }
}

/// <summary>The register-level SNES PPU state a downgrade needs (what the chip was programmed with).</summary>
public sealed record SnesPpuSnapshot(
    byte Inidisp, byte BgMode, byte[] Bgsc, byte Bg12Nba, byte Bg34Nba,
    ushort[] Hofs, ushort[] Vofs, byte Obsel, byte Tm);

/// <summary>A SNES PPU: register port, raw memories, a line renderer and an ARGB framebuffer.</summary>
public interface ISnesPpuCore
{
    string Id { get; }
    ushort[] Vram { get; }
    ushort[] Cgram { get; }
    byte[] Oam { get; }
    /// <summary>0xAARRGGBB, 256 wide, <see cref="VisibleHeight"/> rows.</summary>
    uint[] FrameBuffer { get; }
    int VisibleHeight { get; }
    void WriteRegister(byte reg, byte value);
    void BeginFrame();
    void RenderLine(int line);
    void OnVBlankStart();
    /// <summary>Call after writing Vram/Cgram/Oam directly.</summary>
    void InvalidateCaches();
    SnesPpuSnapshot Snapshot();
}

/// <summary>The SNES core family as the bridges see it: ids -> factories.</summary>
public static class SnesCores
{
    private static readonly Dictionary<string, Func<ISnesBus, ISnesCpuCore>> cpus = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Func<ISnesPpuCore>> ppus = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Func<ISnesApu>> apus = new(StringComparer.OrdinalIgnoreCase);

    static SnesCores()
    {
        RegisterCpu("SFC", bus => new SfcCpuAdapter(new CPU_SFC(bus)));
        RegisterPpu("SFC", () => new SfcPpuAdapter(new PPU_SFC()));
        RegisterApu("SFC", () => new SfcApuAdapter(new APU_SFC()));
        RegisterApu("HLE", () => new APU_HLE());
        RegisterApu("NES", () => new NesApuOnSnes());
    }

    public static void RegisterCpu(string id, Func<ISnesBus, ISnesCpuCore> factory) => cpus[id] = factory;
    public static void RegisterPpu(string id, Func<ISnesPpuCore> factory) => ppus[id] = factory;
    public static void RegisterApu(string id, Func<ISnesApu> factory) => apus[id] = factory;
    public static IEnumerable<string> ApuIds => apus.Keys;
    public static ISnesApu CreateApu(string id) =>
        apus.TryGetValue(id, out var f) ? f() : throw new ArgumentException($"No SNES APU core '{id}' (have: {string.Join(", ", apus.Keys)})");
    public static IEnumerable<string> CpuIds => cpus.Keys;
    public static IEnumerable<string> PpuIds => ppus.Keys;

    public static ISnesCpuCore CreateCpu(string id, ISnesBus bus) =>
        cpus.TryGetValue(id, out var f) ? f(bus) : throw new ArgumentException($"No SNES CPU core '{id}' (have: {string.Join(", ", cpus.Keys)})");
    public static ISnesPpuCore CreatePpu(string id) =>
        ppus.TryGetValue(id, out var f) ? f() : throw new ArgumentException($"No SNES PPU core '{id}' (have: {string.Join(", ", ppus.Keys)})");

    /// <summary>Wrap a PPU a SNES board already owns (the board is still concrete on the SNES branch).</summary>
    public static ISnesPpuCore Wrap(PPU_SFC ppu) => new SfcPpuAdapter(ppu);
}

/// <summary>Which cores the NES-side bridge classes pair up. Set before the NES selects the bridge core.</summary>
public static class MixConfig
{
    /// <summary>SNES CPU behind the NES core CPU_SNES.</summary>
    public static string SnesCpu = "SFC";
    /// <summary>SNES PPU that draws for the NES core PPU_SNES.</summary>
    public static string SnesPpu = "SFC";
    /// <summary>NES PPU that keeps PPU_SNES's NES register semantics and timing (any core with IPpuProbe).</summary>
    public static string NesFrontPpu = "FIX";
    /// <summary>RESCUE: let a NES PPU without IPpuProbe front PPU_SNES through its exported PpuSharedState.</summary>
    /// <summary>SNES audio unit that APU_SNES (a NES APU core) plays through.</summary>
    public static string SnesApu = "SFC";
    /// <summary>NES APU that keeps APU_SNES's NES register semantics ( reads, frame IRQ, DMC DMA).</summary>
    public static string NesFrontApu = "FIX";
    /// <summary>SNES audio unit that runs the game's sound program in front of the NES APU (must expose ISnesApuProbe).</summary>
    public static string SnesFrontApu = "SFC";
    /// <summary>NES APU that makes the sound for the SNES APU id "NES".</summary>
    public static string NesBackApu = "FIX";
    /// <summary>Game Boy sound chip that NES APU id "DMG" plays through (GbCores id).</summary>
    public static string GbApu = "GB";
    /// <summary>NES APU that makes the sound for the Game Boy APU id "NES" (may be "SNES": Game Boy -> NES regs -> S-DSP).</summary>
    public static string GbBackNesApu = "FIX";
    /// <summary>Game Boy model the NES PPU id "DMG" draws with: "dmg" (4 greens) or "cgb" (colour).</summary>
    public static string GbPpuModel = "dmg";
    /// <summary>Which 160x144 part of the 256x240 NES screen the Game Boy PPU shows (PPU_DMG).</summary>
    public static int GbCropX = 48, GbCropY = 48;
    /// <summary>Screen size of the off-spec Game Boy picture chip behind NES PPU id "DMGX" (PPU_GBX; --gb-res WxH).</summary>
    public static int GbHiResWidth = 256, GbHiResHeight = 240;
    public static bool RescueFront = System.Environment.GetEnvironmentVariable("MIX_RESCUE_FRONT") != "0";
}
