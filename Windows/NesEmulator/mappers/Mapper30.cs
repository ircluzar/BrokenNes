using System;

namespace NesEmulator
{
// Mapper 30 (UNROM-512): homebrew flash-cart board.
//  - PRG: 16KB swappable bank at $8000-$BFFF, fixed last bank at $C000-$FFFF (UxROM-style), up to
//    512KB (32 x 16KB banks).
//  - CHR: always CHR-RAM (no CHR-ROM), 8KB banks selected by the same register, up to 32KB (4 banks).
//  - Mirroring: NOT unconditionally software-controlled. UNROM-512 boards ship with the mirroring
//    jumper in one of two configurations, and the iNES four-screen flag (header byte 6 bit 3) is
//    what tells the emulator which one this cartridge is:
//      four-screen SET   -> one-screen mirroring, page selected by bit 7 (M) of the bank register
//      four-screen CLEAR -> mirroring is HARDWIRED to the header's H/V bit and bit 7 does nothing
//    Treating every mapper-30 cart as the one-screen variant is not a harmless default: on a
//    hardwired-horizontal cart it collapses $2400 onto $2000, so the moment a vertically scrolling
//    game crosses the coarse-Y wrap into the second nametable it re-reads the first one. See the
//    mirroring block in CPUWrite.
//  - Battery variant (iNES battery flag set): PRG "ROM" is actually an SST39SF0x0 flash chip the
//    game can self-reprogram (used by homebrew with in-game level editors or save data baked into
//    PRG space) - $8000-$BFFF carries the flash command protocol instead of bus-conflicted bank
//    writes, while $C000-$FFFF always writes the bank-select register regardless of flash state.
//  Ported from BizHawk's Mapper030.cs (MIT licensed, BizHawk.Emulation.Cores.Nintendo.NES).
public class Mapper30 : IMapper, Bus.IMapperRegisterProbe
{
    private readonly Cartridge cart;
    private int prg;                  // currently selected swappable 16KB PRG bank
    private int chr;                  // currently selected 8KB CHR-RAM bank (0-3)
    private readonly int prgBankMask; // (16KB bank count - 1)
    private readonly bool useFlash;   // true for the battery-backed, self-flashing variant

    // Which mirroring variant of the board this cartridge is - decided once from the header and
    // never again, because it is a solder jumper, not state. See the header comment above.
    private readonly bool softMirroring;      // four-screen flag set: bit 7 of the register picks A/B
    private readonly Mirroring fixedMirroring; // otherwise: hardwired to the header's H/V bit

    private enum FlashMode { Default, Erase, Write, Id }
    private int flashState;
    private FlashMode flashMode;
    private byte[] flashOverlay;      // shadow copy of prgROM once a sector has been programmed (useFlash only)
    private int[] sectorWriteCount;   // per-4KB-sector "programmed since last erase" counter (useFlash only)
    private readonly byte[] romFingerprint; // identifies the ROM this flash belongs to; see the blob layout below

    // Bumped by every path that changes flash contents. NOT emulated state and NOT serialised into
    // a savestate - it exists so the host can tell whether the game has written flash SINCE some
    // earlier moment, which a value that rolled back with the savestate could not answer.
    private ulong flashWriteGeneration;

    // === Bus.IMapperRegisterProbe backing (diagnostic only) =================================
    //
    // NOT emulated state and deliberately NOT serialised into a savestate, for the same reason as
    // flashWriteGeneration above: these exist so a differential tracer can describe what a write
    // DID, and a counter that rolled back with a savestate could not answer that. Nothing in the
    // emulation reads them, so a savestate round-trip that drops them is still byte-exact.
    //
    // The cost on the write path is two stores on the bank-register branch only - a branch taken a
    // few thousand times per second at most, and never at all on a $8000-$BFFF flash command.
    private byte lastRegisterValue;    // effective byte the bank register last latched (post bus-conflict)
    private long registerLatchCount;   // how many times it has latched since power-on

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

        // Read straight out of the retained raw image rather than from a Cartridge field: Cartridge
        // parses the four-screen bit and then discards it ("Four-screen mirroring not implemented"),
        // leaving mirroringMode at whatever it defaulted to. cart.rom is the whole file including
        // the 16-byte header (Cartridge.rom = romData) and RefreshRomDomainsFromRom already reads it
        // the same way, so this needs no change to the shared cartridge code.
        byte flags6 = (cart.rom != null && cart.rom.Length >= 7) ? cart.rom[6] : (byte)0;
        softMirroring = (flags6 & 0x08) != 0;
        fixedMirroring = (flags6 & 0x01) != 0 ? Mirroring.Vertical : Mirroring.Horizontal;
        if (useFlash)
        {
            flashOverlay = new byte[cart.prgROM.Length];
            sectorWriteCount = new int[Math.Max(1, cart.prgROM.Length / 0x1000)];
            // Computed once here rather than per-import: hashing 512KB is cheap at load time and
            // free thereafter, and it must be the PRISTINE ROM image - not the flash overlay, which
            // the game will start mutating the moment it saves.
            romFingerprint = ComputeRomFingerprint(cart.prgROM);
        }
        else
        {
            romFingerprint = Array.Empty<byte>();
        }
    }

    // First 8 bytes of SHA-1 over the PRG image. 64 bits is not cryptographic strength and does not
    // need to be: the threat is a stale or mismatched .sav file quietly loading over the wrong ROM,
    // not an adversary crafting a collision, and a wrong-build save is exactly what this catches.
    private static byte[] ComputeRomFingerprint(byte[] prg)
    {
        var full = System.Security.Cryptography.SHA1.HashData(prg ?? Array.Empty<byte>());
        var fp = new byte[RomFingerprintSize];
        Array.Copy(full, fp, RomFingerprintSize);
        return fp;
    }

    public void Reset()
    {
        prg = 0;
        chr = 0;
        flashState = 0;
        flashMode = FlashMode.Default;
        // Diagnostic probe counters follow the register they describe: the register powers on at 0,
        // so a "last latched value" of anything else would be a lie about the current hardware.
        lastRegisterValue = 0;
        registerLatchCount = 0;
        // Power-on state of the register is 0, so on a one-screen board that is page A. On a
        // hardwired board the jumper decides and the register cannot move it.
        cart.SetMirroring(softMirroring ? Mirroring.SingleScreenA : fixedMirroring);
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
            // Recorded before the mirroring decision so the probe reflects the byte the register
            // actually latched even on the hardwired-mirroring variant, where bit 7 is a no-op.
            lastRegisterValue = value2;
            registerLatchCount++;
            // Bit 7 (M) is only wired to the mirroring on the one-screen variant of the board. On a
            // hardwired cart the bit is a don't-care that games freely leave set or clear as a side
            // effect of the bank number they wanted, so acting on it there corrupts the nametable
            // layout on a ROM that never asked for one-screen mirroring.
            if (softMirroring)
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
                    flashWriteGeneration++;
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
                    flashWriteGeneration++;
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
                    // Counted even when the AND happens to change nothing: the GAME issued a program
                    // command, and that intent - "I am saving now" - is what the host cares about.
                    flashWriteGeneration++;
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

    // === Bus.IMapperRegisterProbe ==========================================================
    // See the field declarations above and Bus.MapperRegisterWrite for the contract. Read-only
    // views of diagnostic counters; nothing here participates in emulation.
    public byte ProbeLastRegisterValue => lastRegisterValue;
    public long ProbeRegisterLatchCount => registerLatchCount;

    /// <summary>Flash command-state-machine snapshot, packed as (mode &lt;&lt; 8) | unlockStep, or 0
    /// on the non-flash variant of the board where there is no state machine at all. Packed rather
    /// than split because the observer carries one mapper-private int and this is the only mapper
    /// state a write can change WITHOUT changing a bank - which is exactly the case a bank-number
    /// comparison would miss.</summary>
    public int ProbeMapperStatus => useFlash ? (((int)flashMode) << 8) | (flashState & 0xFF) : 0;

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
    // WHY VERSION 2 CARRIES A ROM FINGERPRINT: a chip length alone does not identify a cartridge.
    // Every 512KB mapper-30 build has the same length, so a v1 blob from a DIFFERENT game - or,
    // far more likely here, a different revision of the same homebrew - passed validation and got
    // mapped over the flash, producing a cart whose PRG is half one build and half another. The
    // fingerprint makes "is this save mine?" answerable, and the answer is reported to the caller
    // (NonVolatileImportResult.NotForThisRom) so the foreign file can be left on disk instead of
    // being overwritten by the next autosave.
    //
    // No v1 read path exists: the format shipped with no .flash.sav ever written to disk, so there
    // is nothing in the field to be compatible with, and a silent "v1 has no fingerprint, trust it"
    // fallback would reopen exactly the hole this closes.
    //
    // Layout (little-endian, header 32 bytes):
    //   0  : 8  magic "BNMAP30F"
    //   8  : 1  format version (2)
    //   9  : 3  reserved, zero
    //   12 : 4  total overlay length in bytes - a blob whose length disagrees with the loaded ROM
    //           is from a different build and is refused wholesale rather than half-applied
    //   16 : 4  sector size in bytes (0x1000)
    //   20 : 4  number of live sector records that follow (never 0; see ImportNonVolatileMemory)
    //   24 : 8  ROM fingerprint: first 8 bytes of SHA-1 over the pristine prgROM image
    //   then, per record: 4 sector index, 4 write count, <sector size> bytes of data
    private static readonly byte[] FlashBlobMagic = { (byte)'B', (byte)'N', (byte)'M', (byte)'A', (byte)'P', (byte)'3', (byte)'0', (byte)'F' };
    private const int FlashSectorSize = 0x1000;
    private const int RomFingerprintSize = 8;
    private const int FlashBlobFingerprintOffset = 24;
    private const int FlashBlobHeaderSize = 32;
    private const byte FlashBlobVersion = 2;
    private const int FlashBlobRecordSize = 8 + FlashSectorSize;

    public bool HasNonVolatileMemory => useFlash;

    public byte[]? ExportNonVolatileMemory()
    {
        if (!useFlash || flashOverlay == null || sectorWriteCount == null) return null;

        int liveCount = 0;
        for (int i = 0; i < sectorWriteCount.Length; i++) if (sectorWriteCount[i] > 0) liveCount++;
        // Nothing programmed yet: report "no save data" so the caller does not leave an empty file
        // behind for a cartridge the player has never saved on.
        if (liveCount == 0) return null;

        var blob = new byte[FlashBlobHeaderSize + liveCount * FlashBlobRecordSize];
        Array.Copy(FlashBlobMagic, 0, blob, 0, FlashBlobMagic.Length);
        blob[8] = FlashBlobVersion;
        WriteU32(blob, 12, (uint)flashOverlay.Length);
        WriteU32(blob, 16, FlashSectorSize);
        WriteU32(blob, 20, (uint)liveCount);
        Array.Copy(romFingerprint, 0, blob, FlashBlobFingerprintOffset, RomFingerprintSize);

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

    // Loads a blob produced by ExportNonVolatileMemory. STRICTLY all-or-nothing: on any non-Applied
    // result the flash is byte-for-byte what it was on entry, so a bad file can never damage a save
    // that is already loaded.
    public NonVolatileImportResult ImportNonVolatileMemory(byte[] data)
    {
        if (!useFlash || flashOverlay == null || sectorWriteCount == null) return NonVolatileImportResult.Rejected;
        if (data == null || data.Length < FlashBlobHeaderSize) return NonVolatileImportResult.Rejected;
        for (int i = 0; i < FlashBlobMagic.Length; i++) if (data[i] != FlashBlobMagic[i]) return NonVolatileImportResult.Rejected;
        if (data[8] != FlashBlobVersion) return NonVolatileImportResult.Rejected;
        if (ReadU32(data, 16) != FlashSectorSize) return NonVolatileImportResult.Rejected;

        // Identity before structure, so a save belonging to another build is reported as somebody's
        // real data (NotForThisRom - keep the file) rather than as corruption (which invites the
        // caller to overwrite it).
        if (ReadU32(data, 12) != (uint)flashOverlay.Length) return NonVolatileImportResult.NotForThisRom;
        for (int i = 0; i < RomFingerprintSize; i++)
            if (data[FlashBlobFingerprintOffset + i] != romFingerprint[i]) return NonVolatileImportResult.NotForThisRom;

        uint records = ReadU32(data, 20);
        // Export NEVER emits a record-less blob - it returns null when nothing is programmed - so a
        // header claiming zero records is a damaged file, not "an empty save". Treating it as empty
        // would blank a flash that may hold the player's only copy of their progress.
        if (records == 0) return NonVolatileImportResult.Rejected;
        long needed = (long)FlashBlobHeaderSize + (long)records * FlashBlobRecordSize;
        if (needed > data.Length) return NonVolatileImportResult.Rejected; // truncated (interrupted write, bad copy)

        // PASS 1 - validate every record before touching a single byte of live state.
        //
        // The clear below used to run BEFORE this loop, so a blob that went bad at record 5 of 20
        // left the flash erased and 4 sectors deep into a foreign save: the worst of both outcomes,
        // and the exact opposite of what the comment beside it promised. Validation must complete
        // first, which means walking the records twice - trivial next to the file I/O around it.
        var seen = new bool[sectorWriteCount.Length];
        int at = FlashBlobHeaderSize;
        for (uint r = 0; r < records; r++)
        {
            uint sector = ReadU32(data, at);
            uint count = ReadU32(data, at + 4);
            at += FlashBlobRecordSize;

            if (sector >= (uint)sectorWriteCount.Length) return NonVolatileImportResult.Rejected;
            // A record with count 0 would install overlay bytes into a sector the bus reports as
            // never-programmed, breaking the invariant the whole sparse format rests on (see above)
            // and making the very next Export lossy. Export cannot produce one.
            if (count == 0) return NonVolatileImportResult.Rejected;
            // Duplicate indices are likewise impossible from Export (it walks sectors in order) and
            // mean the file is scrambled; last-write-wins would silently pick one at random.
            if (seen[sector]) return NonVolatileImportResult.Rejected;
            seen[sector] = true;
        }

        // PASS 2 - commit. Everything below is guaranteed in-range by pass 1.
        Array.Clear(flashOverlay, 0, flashOverlay.Length);
        Array.Clear(sectorWriteCount, 0, sectorWriteCount.Length);

        at = FlashBlobHeaderSize;
        for (uint r = 0; r < records; r++)
        {
            uint sector = ReadU32(data, at); at += 4;
            uint count = ReadU32(data, at); at += 4;
            sectorWriteCount[sector] = count > int.MaxValue ? int.MaxValue : (int)count;
            int dst = (int)sector * FlashSectorSize;
            int n = Math.Max(0, Math.Min(FlashSectorSize, flashOverlay.Length - dst));
            if (n > 0) Buffer.BlockCopy(data, at, flashOverlay, dst, n);
            at += FlashSectorSize;
        }
        return NonVolatileImportResult.Applied;
    }

    // See IMapper.NonVolatileWriteGeneration. Bumped by chip erase, sector erase and byte program;
    // never restored by SetMapperState, so it only ever moves forward within a session.
    public ulong NonVolatileWriteGeneration => flashWriteGeneration;

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
        // Nullable: a non-battery mapper-30 cart has no flash arrays to capture at all.
        public byte[]? flashOverlay;
        public int[]? sectorWriteCount;
    }

    // === Savestates vs. the battery file ===
    //
    // The flash arrays ARE part of the savestate, on purpose: the differential-trace tooling
    // compares sectorWriteCount across a save/load pair, and a savestate that silently dropped the
    // flash would restore a machine whose PRG bus reads differently from the one that was saved.
    //
    // The consequence has to be handled rather than avoided: loading an older savestate rolls the
    // flash BACK, and the battery layer must not then commit that rollback to disk over a newer
    // real save. NES exposes NES.SuppressBatteryAutosave for exactly that, driven by
    // NonVolatileWriteGeneration above - which is why that counter is NOT restored here.
    public object GetMapperState() => new Mapper30State
    {
        prg = prg,
        chr = chr,
        flashState = flashState,
        flashMode = (int)flashMode,
        // CLONE, never hand out the live arrays. An in-memory savestate that aliased them would go
        // on seeing every subsequent flash write, so "load state" would restore the flash to its
        // present contents instead of its captured ones - a rewind that silently does nothing.
        flashOverlay = (useFlash && flashOverlay != null) ? (byte[])flashOverlay.Clone() : null,
        sectorWriteCount = (useFlash && sectorWriteCount != null) ? (int[])sectorWriteCount.Clone() : null
    };

    // Restore the two flash arrays together or not at all.
    //
    // They are one datum in two pieces: sectorWriteCount says which sectors of flashOverlay are
    // live. Applying a partial or mismatched pair (the JSON path used to copy however many elements
    // happened to be present) leaves sectors whose counts came from the savestate sitting on top of
    // overlay bytes from the previous run - a save file that looks valid and contains two different
    // games. Refusing outright keeps the mapper's own coherent state, which is the safer of the two
    // wrong answers when a savestate does not match the cartridge in the machine.
    private bool ApplyFlashArrays(byte[]? overlay, int[]? counts)
    {
        if (!useFlash || flashOverlay == null || sectorWriteCount == null) return false;
        if (overlay == null || counts == null) return false;
        if (overlay.Length != flashOverlay.Length || counts.Length != sectorWriteCount.Length) return false;
        Array.Clear(flashOverlay, 0, flashOverlay.Length);
        Array.Clear(sectorWriteCount, 0, sectorWriteCount.Length);
        Array.Copy(overlay, flashOverlay, flashOverlay.Length);
        Array.Copy(counts, sectorWriteCount, sectorWriteCount.Length);
        return true;
    }

    public void SetMapperState(object state)
    {
        if (state is Mapper30State s)
        {
            prg = s.prg; chr = s.chr; flashState = s.flashState; flashMode = (FlashMode)s.flashMode;
            ApplyFlashArrays(s.flashOverlay, s.sectorWriteCount);
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
                    // Decode into scratch arrays first; ApplyFlashArrays decides whether they are
                    // usable. NES.PlainSerialize emits the overlay as base64 (it is far larger than
                    // its 64-byte threshold) and the counts as a JSON array, but accept either shape
                    // for both so a hand-edited or older savestate still parses.
                    byte[]? overlay = null;
                    int[]? counts = null;
                    if (je.TryGetProperty("flashOverlay", out var fo))
                    {
                        if (fo.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            overlay = new byte[fo.GetArrayLength()];
                            int i = 0; foreach (var el in fo.EnumerateArray()) overlay[i++] = (byte)el.GetInt32();
                        }
                        else if (fo.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            try { overlay = fo.GetBytesFromBase64(); } catch { overlay = null; }
                        }
                    }
                    if (je.TryGetProperty("sectorWriteCount", out var sw) && sw.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        counts = new int[sw.GetArrayLength()];
                        int i = 0; foreach (var el in sw.EnumerateArray()) counts[i++] = el.GetInt32();
                    }
                    ApplyFlashArrays(overlay, counts);
                }
            }
            catch { }
        }
    }
}
}
