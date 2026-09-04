using System;

namespace NesEmulator
{
// Mapper 30 (UNROM-512): homebrew flash-cart board.
//  - PRG: 16KB swappable bank at $8000-$BFFF, fixed last bank at $C000-$FFFF (UxROM-style), up to
//    512KB (32 x 16KB banks).
//  - CHR: always CHR-RAM (no CHR-ROM), 8KB banks selected by the same register, up to 32KB (4 banks).
//  - Mirroring: dynamic one-screen A/B, selected by bit 7 of the same register.
//  - Battery variant (iNES battery flag set): PRG "ROM" is actually an SST39SF0x0 flash chip the
//    game can self-reprogram (used by homebrew with in-game level editors or save data baked into
//    PRG space) - $8000-$BFFF carries the flash command protocol instead of bus-conflicted bank
//    writes, while $C000-$FFFF always writes the bank-select register regardless of flash state.
//  Ported from BizHawk's Mapper030.cs (MIT licensed, BizHawk.Emulation.Cores.Nintendo.NES).
public class Mapper30 : IMapper
{
    private readonly Cartridge cart;
    private int prg;                  // currently selected swappable 16KB PRG bank
    private int chr;                  // currently selected 8KB CHR-RAM bank (0-3)
    private readonly int prgBankMask; // (16KB bank count - 1)
    private readonly bool useFlash;   // true for the battery-backed, self-flashing variant

    private enum FlashMode { Default, Erase, Write, Id }
    private int flashState;
    private FlashMode flashMode;
    private byte[] flashOverlay;      // shadow copy of prgROM once a sector has been programmed (useFlash only)
    private int[] sectorWriteCount;   // per-4KB-sector "programmed since last erase" counter (useFlash only)

    // SST39SF0x0 unlock sequence: AA@bank1:$1555, 55@bank0:$2AAA, then a command byte at bank1:$1555.
    private static readonly int[] UnlockAddr = { 0x1555, 0x2AAA, 0x1555, 0x1555, 0x2AAA };
    private static readonly int[] UnlockBank = { 1, 0, 1, 1, 0 };
    private static readonly byte[] UnlockData = { 0xAA, 0x55, 0x80, 0xAA, 0x55 };

    public Mapper30(Cartridge c)
    {
        cart = c;
        int banks16k = Math.Max(1, cart.prgROM.Length / 0x4000);
        prgBankMask = banks16k - 1;
        useFlash = cart.hasBattery;
        if (useFlash)
        {
            flashOverlay = new byte[cart.prgROM.Length];
            sectorWriteCount = new int[Math.Max(1, cart.prgROM.Length / 0x1000)];
        }
    }

    public void Reset()
    {
        prg = 0;
        chr = 0;
        flashState = 0;
        flashMode = FlashMode.Default;
        cart.SetMirroring(Mirroring.SingleScreenA);
    }

    public byte CPURead(ushort address)
    {
        if (address < 0x8000) return 0; // no WRAM on this board
        int rel = address - 0x8000;
        int bank = (rel >= 0x4000) ? prgBankMask : prg;

        if (useFlash)
        {
            if (flashMode == FlashMode.Id)
            {
                switch (rel & 0x1FF)
                {
                    case 0: return 0xBF; // SST manufacturer ID
                    case 1:
                        int kb = cart.prgROM.Length / 1024;
                        if (kb == 128) return 0xB5;
                        if (kb == 256) return 0xB6;
                        if (kb == 512) return 0xB7;
                        return 0xFF;
                    default: return 0xFF;
                }
            }
            if (GetWriteCount(rel) > 0)
            {
                int fIdx = bank * 0x4000 + (rel & 0x3FFF);
                return (fIdx >= 0 && fIdx < flashOverlay.Length) ? flashOverlay[fIdx] : (byte)0xFF;
            }
        }

        int idx = bank * 0x4000 + (rel & 0x3FFF);
        return (idx >= 0 && idx < cart.prgROM.Length) ? cart.prgROM[idx] : (byte)0xFF;
    }

    public void CPUWrite(ushort address, byte value)
    {
        if (address < 0x8000) return; // no WRAM on this board
        int rel = address - 0x8000;

        if (!useFlash || rel >= 0x4000)
        {
            // Bank-select register: always live in the upper half; for the plain (non-flash) ROM
            // variant it's live everywhere, and real UNROM-style boards bus-conflict these writes.
            byte value2 = useFlash ? value : HandleBusConflict(rel, value);
            chr = (value2 >> 5) & 3;
            prg = value2 & prgBankMask;
            cart.SetMirroring((value2 & 0x80) != 0 ? Mirroring.SingleScreenB : Mirroring.SingleScreenA);
            return;
        }

        // Flash command state machine (battery variant only, $8000-$BFFF).
        switch (flashMode)
        {
            case FlashMode.Default:
                if (flashState < 5 && UnlockAddr[flashState] == rel && UnlockBank[flashState] == prg && UnlockData[flashState] == value)
                {
                    flashState++;
                    if (flashState == 5) flashMode = FlashMode.Erase;
                }
                else if (flashState == 2 && rel == 0x1555 && prg == 1 && value == 0x90)
                {
                    flashMode = FlashMode.Id;
                }
                else if (flashState == 2 && rel == 0x1555 && prg == 1 && value == 0xA0)
                {
                    flashState++;
                    flashMode = FlashMode.Write;
                }
                else
                {
                    flashState = 0;
                    flashMode = FlashMode.Default;
                }
                break;

            case FlashMode.Erase:
                if (value == 0x10)
                {
                    // Whole-chip erase ("you probably don't want to do this").
                    for (int i = 0; i < sectorWriteCount.Length; i++) IncrementWriteCount(i);
                    Array.Fill(flashOverlay, (byte)0xFF);
                }
                else if (value == 0x30)
                {
                    IncrementWriteCount(SectorIndex(rel));
                    int sectorStart = prg * 0x4000 + (rel & 0x3000);
                    for (int i = 0; i < 0x1000; i++)
                    {
                        int di = sectorStart + i;
                        if (di >= 0 && di < flashOverlay.Length) flashOverlay[di] = 0xFF;
                    }
                }
                flashState = 0;
                flashMode = FlashMode.Default;
                break;

            case FlashMode.Write:
                {
                    if (GetWriteCount(rel) == 0)
                    {
                        IncrementWriteCount(SectorIndex(rel));
                        int sectorStart = prg * 0x4000 + (rel & 0x3000);
                        for (int i = 0; i < 0x1000; i++)
                        {
                            int di = sectorStart + i;
                            if (di >= 0 && di < flashOverlay.Length && di < cart.prgROM.Length) flashOverlay[di] = cart.prgROM[di];
                        }
                    }
                    int byteIdx = prg * 0x4000 + (rel & 0x3FFF);
                    // Flash programming can only clear bits (AND), never set them, until the next erase.
                    if (byteIdx >= 0 && byteIdx < flashOverlay.Length) flashOverlay[byteIdx] &= value;
                    flashState = 0;
                    flashMode = FlashMode.Default;
                    break;
                }

            case FlashMode.Id:
                break; // handled below (exit-ID-mode check only)
        }

        if (flashMode == FlashMode.Id && value == 0xF0)
        {
            flashState = 0;
            flashMode = FlashMode.Default;
        }
    }

    private byte HandleBusConflict(int rel, byte value)
    {
        int bank = (rel >= 0x4000) ? prgBankMask : prg;
        int idx = bank * 0x4000 + (rel & 0x3FFF);
        byte romByte = (idx >= 0 && idx < cart.prgROM.Length) ? cart.prgROM[idx] : (byte)0xFF;
        return (byte)(value & romByte);
    }

    private int SectorIndex(int rel)
    {
        int bank = (rel >= 0x4000) ? prgBankMask : prg;
        return (bank << 2) | ((rel >> 12) & 3);
    }

    private int GetWriteCount(int rel)
    {
        if (sectorWriteCount == null) return 0;
        int s = SectorIndex(rel);
        return (s >= 0 && s < sectorWriteCount.Length) ? sectorWriteCount[s] : 0;
    }

    private void IncrementWriteCount(int sectorIndex)
    {
        if (sectorWriteCount == null) return;
        if (sectorIndex >= 0 && sectorIndex < sectorWriteCount.Length && sectorWriteCount[sectorIndex] < int.MaxValue)
            sectorWriteCount[sectorIndex]++;
    }

    public byte PPURead(ushort address)
    {
        if (address < 0x2000)
        {
            if (cart.chrBanks > 0) return cart.chrROM[address % cart.chrROM.Length]; // defensive: real UNROM-512 ROMs carry no CHR-ROM
            int idx = (chr & 3) * 0x2000 + address;
            return (idx >= 0 && idx < cart.chrRAM.Length) ? cart.chrRAM[idx] : (byte)0;
        }
        return 0;
    }

    public void PPUWrite(ushort address, byte value)
    {
        if (address < 0x2000 && cart.chrBanks == 0)
        {
            int idx = (chr & 3) * 0x2000 + address;
            if (idx >= 0 && idx < cart.chrRAM.Length) cart.chrRAM[idx] = value;
        }
    }

    public bool TryCpuToPrgIndex(ushort address, out int prgIndex)
    {
        prgIndex = -1;
        if (address < 0x8000) return false;
        int rel = address - 0x8000;
        int bank = (rel >= 0x4000) ? prgBankMask : prg;
        int idx = bank * 0x4000 + (rel & 0x3FFF);
        if (idx >= 0 && idx < cart.prgROM.Length) { prgIndex = idx; return true; }
        return false;
    }

    public bool IsCpuReadOpenBus(ushort address) => address < 0x8000;

    public uint GetChrBankSignature() => (uint)chr;

    // === Non-volatile flash contents (see IMapper.ExportNonVolatileMemory) ===
    //
    // WHY A SPARSE, PER-SECTOR FORMAT rather than dumping all 512KB of flashOverlay:
    //
    // The overlay is a shadow of the whole PRG chip, but only sectors the game has actually erased
    // or programmed carry meaning. That is not a heuristic, it is an invariant of the code above:
    // every path that writes a byte into flashOverlay (chip erase, sector erase, and the
    // seed-from-ROM step of a program) bumps that sector's write count FIRST, and CPURead consults
    // the overlay only where the count is non-zero. So overlay[i] can only be non-zero inside a
    // live sector, and dropping dead sectors is lossless rather than merely "good enough" - a
    // freshly constructed mapper and a mapper restored from this blob are byte-identical in both
    // arrays. A game that has touched two sectors writes an 8KB file instead of a 512KB one; a game
    // that issued the whole-chip erase legitimately writes all 128.
    //
    // A byte-level delta against prgROM would be smaller still, but it would make the save file
    // meaningless without the exact ROM that produced it - a bad trade for a homebrew flash cart
    // whose ROM is expected to be rebuilt often. Sector records are self-describing.
    //
    // The write COUNT is stored, not just a live/dead bit. Only its zero/non-zero-ness is
    // observable through the bus, but preserving the exact value keeps a savestate taken after a
    // reload identical to one taken before it, which the differential-trace tooling compares.
    //
    // Layout (little-endian, header 24 bytes):
    //   0  : 8  magic "BNMAP30F"
    //   8  : 1  format version (1)
    //   9  : 3  reserved, zero
    //   12 : 4  total overlay length in bytes - a blob whose length disagrees with the loaded ROM
    //           is from a different build and is refused wholesale rather than half-applied
    //   16 : 4  sector size in bytes (0x1000)
    //   20 : 4  number of live sector records that follow
    //   then, per record: 4 sector index, 4 write count, <sector size> bytes of data
    private static readonly byte[] FlashBlobMagic = { (byte)'B', (byte)'N', (byte)'M', (byte)'A', (byte)'P', (byte)'3', (byte)'0', (byte)'F' };
    private const int FlashSectorSize = 0x1000;
    private const int FlashBlobHeaderSize = 24;
    private const byte FlashBlobVersion = 1;

    public bool HasNonVolatileMemory => useFlash;

    public byte[]? ExportNonVolatileMemory()
    {
        if (!useFlash || flashOverlay == null || sectorWriteCount == null) return null;

        int liveCount = 0;
        for (int i = 0; i < sectorWriteCount.Length; i++) if (sectorWriteCount[i] > 0) liveCount++;
        // Nothing programmed yet: report "no save data" so the caller does not leave an empty file
        // behind for a cartridge the player has never saved on.
        if (liveCount == 0) return null;

        var blob = new byte[FlashBlobHeaderSize + liveCount * (8 + FlashSectorSize)];
        Array.Copy(FlashBlobMagic, 0, blob, 0, FlashBlobMagic.Length);
        blob[8] = FlashBlobVersion;
        WriteU32(blob, 12, (uint)flashOverlay.Length);
        WriteU32(blob, 16, FlashSectorSize);
        WriteU32(blob, 20, (uint)liveCount);

        int at = FlashBlobHeaderSize;
        for (int s = 0; s < sectorWriteCount.Length; s++)
        {
            if (sectorWriteCount[s] <= 0) continue;
            WriteU32(blob, at, (uint)s); at += 4;
            WriteU32(blob, at, (uint)sectorWriteCount[s]); at += 4;
            int src = s * FlashSectorSize;
            // A PRG size that is not a whole number of sectors would leave the tail short; copy what
            // exists and leave the rest of the record zeroed rather than reading out of bounds.
            int n = Math.Max(0, Math.Min(FlashSectorSize, flashOverlay.Length - src));
            if (n > 0) Buffer.BlockCopy(flashOverlay, src, blob, at, n);
            at += FlashSectorSize;
        }
        return blob;
    }

    public void ImportNonVolatileMemory(byte[] data)
    {
        if (!useFlash || flashOverlay == null || sectorWriteCount == null) return;
        if (data == null || data.Length < FlashBlobHeaderSize) return;
        for (int i = 0; i < FlashBlobMagic.Length; i++) if (data[i] != FlashBlobMagic[i]) return;
        if (data[8] != FlashBlobVersion) return;
        // Refuse a blob recorded against a differently sized PRG chip outright. Partially applying
        // it would leave the flash a chimera of two builds, which is far harder to diagnose than a
        // save that simply did not load.
        if (ReadU32(data, 12) != (uint)flashOverlay.Length) return;
        if (ReadU32(data, 16) != FlashSectorSize) return;

        uint records = ReadU32(data, 20);
        long needed = (long)FlashBlobHeaderSize + (long)records * (8 + FlashSectorSize);
        if (needed > data.Length) return; // truncated file (interrupted write, bad copy) - ignore it

        // Only commit once the whole blob has been validated, so a rejected file cannot leave the
        // flash half-overwritten.
        Array.Clear(flashOverlay, 0, flashOverlay.Length);
        Array.Clear(sectorWriteCount, 0, sectorWriteCount.Length);

        int at = FlashBlobHeaderSize;
        for (uint r = 0; r < records; r++)
        {
            uint sector = ReadU32(data, at); at += 4;
            uint count = ReadU32(data, at); at += 4;
            if (sector < (uint)sectorWriteCount.Length)
            {
                sectorWriteCount[sector] = count > int.MaxValue ? int.MaxValue : (int)count;
                int dst = (int)sector * FlashSectorSize;
                int n = Math.Max(0, Math.Min(FlashSectorSize, flashOverlay.Length - dst));
                if (n > 0) Buffer.BlockCopy(data, at, flashOverlay, dst, n);
            }
            at += FlashSectorSize;
        }
    }

    private static void WriteU32(byte[] buf, int offset, uint value)
    {
        buf[offset] = (byte)value;
        buf[offset + 1] = (byte)(value >> 8);
        buf[offset + 2] = (byte)(value >> 16);
        buf[offset + 3] = (byte)(value >> 24);
    }

    private static uint ReadU32(byte[] buf, int offset)
        => (uint)(buf[offset] | (buf[offset + 1] << 8) | (buf[offset + 2] << 16) | (buf[offset + 3] << 24));

    private class Mapper30State
    {
        public int prg;
        public int chr;
        public int flashState;
        public int flashMode;
        public byte[] flashOverlay;
        public int[] sectorWriteCount;
    }

    public object GetMapperState() => new Mapper30State
    {
        prg = prg,
        chr = chr,
        flashState = flashState,
        flashMode = (int)flashMode,
        flashOverlay = useFlash ? flashOverlay : null,
        sectorWriteCount = useFlash ? sectorWriteCount : null
    };

    public void SetMapperState(object state)
    {
        if (state is Mapper30State s)
        {
            prg = s.prg; chr = s.chr; flashState = s.flashState; flashMode = (FlashMode)s.flashMode;
            if (useFlash)
            {
                if (s.flashOverlay != null && s.flashOverlay.Length == flashOverlay.Length) Array.Copy(s.flashOverlay, flashOverlay, flashOverlay.Length);
                if (s.sectorWriteCount != null && s.sectorWriteCount.Length == sectorWriteCount.Length) Array.Copy(s.sectorWriteCount, sectorWriteCount, sectorWriteCount.Length);
            }
            return;
        }
        if (state is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            try
            {
                if (je.TryGetProperty("prg", out var p)) prg = p.GetInt32();
                if (je.TryGetProperty("chr", out var c)) chr = c.GetInt32();
                if (je.TryGetProperty("flashState", out var fs)) flashState = fs.GetInt32();
                if (je.TryGetProperty("flashMode", out var fm)) flashMode = (FlashMode)fm.GetInt32();
                if (useFlash)
                {
                    if (je.TryGetProperty("flashOverlay", out var fo))
                    {
                        if (fo.ValueKind == System.Text.Json.JsonValueKind.Array) { int i = 0; foreach (var el in fo.EnumerateArray()) { if (i < flashOverlay.Length) flashOverlay[i++] = (byte)el.GetInt32(); else break; } }
                        else if (fo.ValueKind == System.Text.Json.JsonValueKind.String) { try { var b = fo.GetBytesFromBase64(); Array.Copy(b, flashOverlay, Math.Min(b.Length, flashOverlay.Length)); } catch { } }
                    }
                    if (je.TryGetProperty("sectorWriteCount", out var sw) && sw.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        int i = 0; foreach (var el in sw.EnumerateArray()) { if (i < sectorWriteCount.Length) sectorWriteCount[i++] = el.GetInt32(); else break; }
                    }
                }
            }
            catch { }
        }
    }
}
}
