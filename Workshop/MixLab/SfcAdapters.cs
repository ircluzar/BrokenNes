using System;
using NesEmulator.Snes;

namespace BrokenNes.Workshop.MixLab;

/// <summary>CPU_SFC behind <see cref="ISnesCpuCore"/>.</summary>
internal sealed class SfcCpuAdapter : ISnesCpuCore
{
    private readonly CPU_SFC c;
    public SfcCpuAdapter(CPU_SFC cpu) { c = cpu; }
    public string Id => "SFC";
    public void Reset() => c.Reset();
    public void Step() => c.Step();
    public void RaiseNmi() => c.RaiseNmi();
    public void SetIrq(bool asserted) => c.SetIrq(asserted);
    public bool Stopped => c.Stopped;
    public ushort A { get => c.A; set => c.A = value; }
    public ushort X { get => c.X; set => c.X = value; }
    public ushort Y { get => c.Y; set => c.Y = value; }
    public ushort S { get => c.S; set => c.S = value; }
    public ushort PC { get => c.PC; set => c.PC = value; }
    public byte P { get => c.P; set => c.P = value; }
    public bool E { get => c.E; set => c.E = value; }
}

/// <summary>PPU_SFC behind <see cref="ISnesPpuCore"/>; <see cref="Snapshot"/> uses PPU_SFC.GetRegisterSnapshot().</summary>
internal sealed class SfcPpuAdapter : ISnesPpuCore
{
    private readonly PPU_SFC p;
    public SfcPpuAdapter(PPU_SFC ppu) { p = ppu; }
    public PPU_SFC Inner => p;
    public string Id => "SFC";
    public ushort[] Vram => p.Vram;
    public ushort[] Cgram => p.Cgram;
    public byte[] Oam => p.Oam;
    public uint[] FrameBuffer => p.FrameBuffer;
    public int VisibleHeight => p.VisibleHeight;
    public void WriteRegister(byte reg, byte value) => p.WriteRegister(reg, value);
    public void BeginFrame() => p.BeginFrame();
    public void RenderLine(int line) => p.RenderLine(line);
    public void OnVBlankStart() => p.OnVBlankStart();
    public void InvalidateCaches() => p.InvalidateCaches();

    public SnesPpuSnapshot Snapshot()
    {
        var r = p.GetRegisterSnapshot();
        return new(r.Inidisp, r.Bgmode, new[] { r.Bg1sc, r.Bg2sc, r.Bg3sc, r.Bg4sc }, r.Bg12nba, r.Bg34nba,
            new[] { r.Bg1hofs, r.Bg2hofs, r.Bg3hofs, r.Bg4hofs }, new[] { r.Bg1vofs, r.Bg2vofs, r.Bg3vofs, r.Bg4vofs }, r.Obsel, r.Tm, r.Ts);
    }
}
