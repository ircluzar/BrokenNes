using NesEmulator;
using NesEmulator.Plugin;

namespace BrokenNes.Nano;

/// <summary>What the parity tool needs of a chip: registers in, mono samples out.</summary>
public interface IChipDriver
{
    void Write(int reg, byte value);
    byte Shadow(int reg);
    void Render(Span<float> dest);
}

/// <summary>Core's NesApuInstrument itself (the chip behind the FL plugin).</summary>
public sealed class InstrumentChip : IChipDriver
{
    readonly NesApuInstrument chip;
    public InstrumentChip(string core, int rate, byte mask) { chip = new NesApuInstrument(core, rate, mask); }
    public void Write(int reg, byte value) => chip.Write(reg, value);
    public byte Shadow(int reg) => chip.Shadow(reg);
    public void Render(Span<float> dest) => chip.Render(dest);
}

/// <summary>
/// The same chip with sample memory of the caller's choosing, for the DMC (NesApuInstrument has a NES with an idle ROM behind it, so a DPCM sample
/// would read the idle program's bytes). A NES with a 16 KB cartridge made of <c>rom</c> (seen at $C000 and, mirrored, at $8000) runs the FIX core, and
/// this class drives that core exactly as NesApuInstrument.Render does: 32 CPU cycles per step, the samples through Core's StreamResampler.
/// </summary>
public sealed class DmcChip : IChipDriver
{
    const int StepCycles = 32;
    readonly NES nes;
    readonly IAPU apu;
    readonly StreamResampler resampler = new();
    readonly float[] pull = new float[512];
    readonly byte[] shadow = new byte[0x18];

    public DmcChip(int hostRate, byte[] rom16k, byte mask)
    {
        if (rom16k.Length != 0x4000) throw new ArgumentException("the sample memory is 16 KB");
        var image = new byte[16 + 0x4000];
        image[0] = (byte)'N'; image[1] = (byte)'E'; image[2] = (byte)'S'; image[3] = 0x1A; image[4] = 1; image[5] = 0; image[6] = 0x01;
        Array.Copy(rom16k, 0, image, 16, 0x4000);
        nes = new NES { RomName = "nano-dmc.nes" };
        nes.LoadROM(image);
        if (!nes.SetApuCore("FIX") || !nes.GetApuCoreId().EndsWith("_FIX", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("FIX core not applied: " + nes.GetApuCoreId());
        apu = nes.ActiveApu ?? throw new InvalidOperationException("no APU");
        resampler.Clear();
        resampler.SetRates(apu.GetSampleRate(), hostRate);
        apu.WriteAPURegister(0x4017, 0x40);
        apu.WriteAPURegister(0x4015, mask);
    }

    public void Write(int reg, byte value) { shadow[reg] = value; apu.WriteAPURegister((ushort)(0x4000 + reg), value); }
    public byte Shadow(int reg) => shadow[reg];

    public void Render(Span<float> dest)
    {
        int done = 0;
        while (done < dest.Length)
        {
            done += resampler.Read(dest.Slice(done));
            if (done >= dest.Length) break;
            apu.Step(StepCycles);
            int n;
            while ((n = apu.ReadSamples(pull.AsSpan(0, Math.Min(pull.Length, resampler.FreeSpace)))) > 0)
                resampler.Write(pull.AsSpan(0, n));
        }
    }
}
