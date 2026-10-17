using System.Diagnostics;
using System.Runtime.InteropServices.JavaScript;
using NesEmulator;
using NesEmulator.Plugin;

// A stand-in for the pieces of the NES board that APU_FIX touches (the real Bus drags the whole emulator in).
namespace NesEmulator
{
    public sealed class CpuStub { public void RequestIRQ(bool on) { } }
    public sealed class Bus
    {
        public CpuStub cpu = new();
        public byte GetOpenBus() => 0;
        public byte Read(ushort address) => 0;
        public void RequestDmcDma() { }
    }
}

public static partial class Floor
{
    static APU_FIX apu = null!;
    static StreamResampler rs = new();
    static readonly float[] pull = new float[512];
    static readonly float[] block = new float[128];

    [JSExport]
    public static void Init(int hostRate)
    {
        apu = new APU_FIX(new Bus());
        rs.SetRates(44100, hostRate);
        apu.WriteAPURegister(0x4017, 0x40);
        apu.WriteAPURegister(0x4015, 0x0F);
    }

    [JSExport]
    public static void Write(int address, int value) => apu.WriteAPURegister((ushort)address, (byte)value);

    // Same loop as NesApuInstrument.Render, for a block of 128 samples; returns the peak (so the work cannot be optimised away).
    [JSExport]
    public static double RenderBlock()
    {
        Span<float> dest = block;
        int done = 0;
        while (done < dest.Length)
        {
            done += rs.Read(dest.Slice(done));
            if (done >= dest.Length) break;
            apu.Step(32);
            int n;
            while ((n = apu.ReadSamples(pull.AsSpan(0, Math.Min(pull.Length, rs.FreeSpace)))) > 0) rs.Write(pull.AsSpan(0, n));
        }
        float peak = 0;
        foreach (var s in block) peak = Math.Max(peak, Math.Abs(s));
        return peak;
    }

    // Renders `seconds` of audio at `hostRate` entirely inside WebAssembly; returns the elapsed milliseconds.
    [JSExport]
    public static double Bench(int hostRate, int seconds)
    {
        int blocks = hostRate * seconds / 128;
        var sw = Stopwatch.StartNew();
        double peak = 0;
        for (int i = 0; i < blocks; i++) peak += RenderBlock();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }
}

public static class Program
{
    public static void Main() { }
}
