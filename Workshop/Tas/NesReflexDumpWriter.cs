using System;
using System.IO;
using NesEmulator;

namespace BrokenNes.Workshop.Tas;

/// <summary>
/// Writes the same "nesreflex-raw-v2" per-frame binary trace format the ML_NesPlayer project's
/// custom FCEUX fork produces (TAS/fceux_custom/src/nesreflex_dump.cpp), field-for-field, byte
/// offset for byte offset, so the existing Python extraction pipeline (build_shards.py etc.) can
/// consume BrokenNes-produced dumps without any changes on that side.
///
/// Two fields can't be byte-identical in value (not just format) to what the FCEUX fork emits,
/// documented at each write site below:
///   - IRQlow: FCEUX's X6502-internal pending-interrupt bitfield has no equivalent uniformly
///     exposed across BrokenNes's 7 independent CPU core implementations. Written as 0.
///   - screen_hash: FCEUX hashes its internal 8bpp indexed XBuf (256*240 bytes); BrokenNes only
///     exposes an RGBA framebuffer (256*240*4 bytes). Both use the same CRC32 algorithm, but the
///     input differs, so values are not cross-emulator-comparable - only useful as an
///     internal-consistency/dedup signal the way FCEUX's own consumers already treat it.
/// Everything else (RAM, CPU regs, PPU regs/scroll/VRAM-address/scanline/dot, OAM, palette,
/// nametables, lag detection) is a genuine field-for-field mirror.
/// </summary>
public sealed class NesReflexDumpWriter : IDisposable
{
    private readonly FileStream _stream;
    private uint _lagCounter;

    public int FramesWritten { get; private set; }

    public NesReflexDumpWriter(string path)
    {
        _stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        _stream.Write(new byte[] { (byte)'N', (byte)'R', (byte)'F', (byte)'X', (byte)'R', (byte)'A', (byte)'W', (byte)'2' });
        WriteU32(2); // format version
        WriteU32(2); // record layout version
    }

    /// <summary>Writes one frame's record. Call once per emulated frame, immediately after
    /// RunFrame() and before the next frame's SetInputs() call (so the read-count-based lag
    /// detection reflects exactly this frame's polling).</summary>
    public void WriteFrame(NES nes, int movieFrame, byte cmd)
    {
        WriteU32((uint)FramesWritten);
        WriteU32((uint)movieFrame);
        WriteU8(nes.GetP1RawInputState());
        WriteU8(nes.GetP2RawInputState());
        WriteU8(cmd);

        var regs = nes.GetCpuRegs();
        WriteU8(regs.A);
        WriteU8(regs.X);
        WriteU8(regs.Y);
        WriteU8((byte)(regs.SP & 0xFF));
        WriteU8(regs.P);
        WriteU16(regs.PC);
        WriteU8(nes.GetOpenBusValue()); // DB
        WriteU32(0); // IRQlow - see class doc

        int p1Reads = nes.ConsumeP1ReadCount();
        int p2Reads = nes.ConsumeP2ReadCount();
        bool lag = p1Reads == 0 && p2Reads == 0;
        if (lag) _lagCounter++;
        WriteU8((byte)(lag ? 1 : 0));
        WriteU32(_lagCounter);

        byte[] ram = nes.PeekMemoryRange("System RAM", 0, 0x800);
        _stream.Write(ram, 0, ram.Length);

        var ppu = nes.GetPpuState() as PpuSharedState;
        byte scrollXLo = (byte)((ppu?.PPUSCROLLX ?? 0) & 0xFF);
        byte scrollYLo = (byte)((ppu?.PPUSCROLLY ?? 0) & 0xFF);
        ushort vramAddr = ppu?.PPUADDR ?? 0;

        WriteU8(ppu?.PPUCTRL ?? 0);
        WriteU8(ppu?.PPUMASK ?? 0);
        WriteU8(ppu?.PPUSTATUS ?? 0);
        WriteU8(ppu?.OAMADDR ?? 0);
        WriteU8(scrollXLo);
        WriteU8(scrollYLo);
        WriteU8((byte)(vramAddr & 0xFF));
        WriteU8((byte)((vramAddr >> 8) & 0xFF));
        WriteU8(scrollXLo);
        WriteU8(scrollYLo);
        WriteU16(vramAddr);
        WriteI16(ppu?.scanline ?? 0);
        WriteI16(ppu?.scanlineCycle ?? 0); // "dot": PPU cycle within the current scanline

        byte[] oam = ppu?.oam ?? new byte[256];
        byte[] palette = ppu?.palette ?? new byte[32];
        byte[] nametables = ppu?.vram ?? new byte[2048];
        _stream.Write(oam, 0, 256);
        _stream.Write(palette, 0, 32);
        _stream.Write(nametables, 0, 2048);

        WriteU32(Crc32.Compute(nes.GetFrameBuffer()));

        FramesWritten++;
    }

    public void Dispose()
    {
        _stream.Dispose();
    }

    private void WriteU8(byte v) => _stream.WriteByte(v);
    private void WriteU16(ushort v) { _stream.WriteByte((byte)(v & 0xFF)); _stream.WriteByte((byte)((v >> 8) & 0xFF)); }
    private void WriteI16(int v) => WriteU16((ushort)(v & 0xFFFF));
    private void WriteU32(uint v)
    {
        _stream.WriteByte((byte)(v & 0xFF));
        _stream.WriteByte((byte)((v >> 8) & 0xFF));
        _stream.WriteByte((byte)((v >> 16) & 0xFF));
        _stream.WriteByte((byte)((v >> 24) & 0xFF));
    }
}

/// <summary>Standard IEEE 802.3 CRC-32 (polynomial 0xEDB88320) - the same algorithm FCEUX's
/// CalcCRC32 uses, though see NesReflexDumpWriter's class doc for why the hashed input still
/// differs (RGBA vs 8bpp indexed framebuffer).</summary>
internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    public static uint Compute(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }
}
