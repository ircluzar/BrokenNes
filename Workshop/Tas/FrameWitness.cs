using System;
using System.Collections.Generic;
using System.IO;
using NesEmulator;

namespace BrokenNes.Workshop.Tas;

/// <summary>
/// Rigorous per-frame emulator-state fingerprint (RAM + CPU regs + PPU OAM/palette/nametables +
/// framebuffer, CRC32'd together) - used to verify a self-play session's exported .fm2 replays
/// frame-for-frame identically to what was actually simulated live, not just "doesn't crash".
/// Reuses the same field set and Crc32 algorithm as NesReflexDumpWriter's screen_hash so a mismatch
/// here is directly comparable in spirit to what that tool already tracks, just covering full state
/// instead of only the framebuffer.
/// </summary>
internal static class FrameWitness
{
    public static uint Compute(NES nes)
    {
        var ram = nes.PeekMemoryRange("System RAM", 0, 0x800);
        var fb = nes.GetFrameBuffer();
        using var ms = new MemoryStream(ram.Length + 7 + 4 + 256 + 32 + 2048 + fb.Length);

        ms.Write(ram, 0, ram.Length);

        var regs = nes.GetCpuRegs();
        ms.WriteByte(regs.A);
        ms.WriteByte(regs.X);
        ms.WriteByte(regs.Y);
        ms.WriteByte((byte)(regs.SP & 0xFF));
        ms.WriteByte(regs.P);
        ms.WriteByte((byte)(regs.PC & 0xFF));
        ms.WriteByte((byte)((regs.PC >> 8) & 0xFF));

        if (nes.GetPpuState() is PpuSharedState ppu)
        {
            ms.WriteByte(ppu.PPUCTRL);
            ms.WriteByte(ppu.PPUMASK);
            ms.WriteByte(ppu.PPUSTATUS);
            ms.WriteByte(ppu.OAMADDR);
            var oam = ppu.oam ?? new byte[256];
            var palette = ppu.palette ?? new byte[32];
            var nametables = ppu.vram ?? new byte[2048];
            ms.Write(oam, 0, oam.Length);
            ms.Write(palette, 0, palette.Length);
            ms.Write(nametables, 0, nametables.Length);
        }

        ms.Write(fb, 0, fb.Length);

        return Crc32.Compute(ms.ToArray());
    }

    /// <summary>Same fields as Compute(), but hashed separately per component - used only by
    /// SaveStateRoundtripDiagCli to pinpoint which piece of state a SaveState/LoadState round trip
    /// fails to preserve, instead of just knowing "something" differs.</summary>
    public static FrameWitnessBreakdown ComputeBreakdown(NES nes)
    {
        var ram = nes.PeekMemoryRange("System RAM", 0, 0x800);
        var regs = nes.GetCpuRegs();
        var regBytes = new byte[] { regs.A, regs.X, regs.Y, (byte)(regs.SP & 0xFF), regs.P, (byte)(regs.PC & 0xFF), (byte)((regs.PC >> 8) & 0xFF) };

        byte[] ppuRegs = Array.Empty<byte>(), oam = Array.Empty<byte>(), palette = Array.Empty<byte>(), nametables = Array.Empty<byte>();
        if (nes.GetPpuState() is PpuSharedState ppu)
        {
            ppuRegs = new byte[] { ppu.PPUCTRL, ppu.PPUMASK, ppu.PPUSTATUS, ppu.OAMADDR };
            oam = ppu.oam ?? new byte[256];
            palette = ppu.palette ?? new byte[32];
            nametables = ppu.vram ?? new byte[2048];
        }
        var fb = nes.GetFrameBuffer();

        return new FrameWitnessBreakdown(
            Crc32.Compute(ram), Crc32.Compute(regBytes), Crc32.Compute(ppuRegs),
            Crc32.Compute(oam), Crc32.Compute(palette), Crc32.Compute(nametables), Crc32.Compute(fb));
    }
}

public sealed record FrameWitnessBreakdown(uint Ram, uint CpuRegs, uint PpuRegs, uint Oam, uint Palette, uint Nametables, uint Framebuffer);

/// <summary>Sidecar written next to a self-play-exported .fm2 (path + ".witness.json"): the rom
/// checksum + cores it was captured with, and one FrameWitnessBreakdown per surviving frame in the
/// movie, in order. MovieVerifyCli replays the .fm2 independently and compares against this, frame
/// by frame and component by component, to catch dropped frames, duplicated frames, or any other
/// desync - and to pinpoint exactly which piece of state (RAM vs CPU regs vs PPU vs framebuffer)
/// diverged first, not just that "some" mismatch happened.</summary>
public sealed record WitnessSidecar(string? RomMd5Hex, string? Cpu, string? Ppu, string? Apu, int FrameCount, List<FrameWitnessBreakdown> Frames);
