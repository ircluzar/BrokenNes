using System.IO;

namespace NesEmulator.Snes;

/// <summary>
/// The Super FX's save state: the sixteen registers and the status register with all its flags, the ROM / RAM bank and screen registers, the one-byte fetch pipeline and the prefix state (source / destination register
/// numbers, ALT flags), the ROM read buffer and the RAM write buffer with the clocks they still have to run (a read or write that is in flight when the state is taken lands at the same moment after the load), the 512-byte instruction
/// cache with its line-valid bits, the two-entry pixel cache that batches PLOT, the chip's clock and counters, and the SNES-side lock-out latch. The work RAM is the cartridge's RAM (saved with the cartridge) except on a board without
/// any, where it is the chip's own and is written here.
/// <para>The tracing and watch hooks (<c>Trace</c>, <c>WatchPcs</c>...) are tools, not state.</para>
/// </summary>
public sealed partial class GSU_SFC
{
    private const byte StateVersion = 1;

    public void SaveState(BinaryWriter w)
    {
        SnesStateIo.Begin(w, StateVersion);
        w.Write(clock); w.Write(Instructions); w.Write(Stops);
        SnesStateIo.Put(w, r); w.Write(sfr);
        w.Write(pbr); w.Write(rombr); w.Write(rambr); w.Write(bramr); w.Write(cfgr); w.Write(scbr); w.Write(clsr); w.Write(scmr); w.Write(colr); w.Write(por);
        w.Write(cbr); w.Write(pipeline); w.Write(r15Modified); w.Write(sreg); w.Write(dreg); w.Write(ramAddr); w.Write(romBuffer);
        SnesStateIo.Put(w, cache); SnesStateIo.Put(w, cacheValid);
        WritePixels(w, pc0); WritePixels(w, pc1);
        w.Write(lockedOut);
        w.Write(romPending); w.Write(ramPending); w.Write(ramPendingAddr); w.Write(ramPendingData);
        w.Write(ownsRam);
        if (ownsRam) SnesStateIo.Put(w, ram);
    }

    public void LoadState(BinaryReader rd)
    {
        SnesStateIo.Expect(rd, StateVersion, "Super FX");
        clock = rd.ReadInt64(); Instructions = rd.ReadInt64(); Stops = rd.ReadInt64();
        SnesStateIo.Get(rd, r); sfr = rd.ReadUInt16();
        pbr = rd.ReadByte(); rombr = rd.ReadByte(); rambr = rd.ReadByte(); bramr = rd.ReadByte(); cfgr = rd.ReadByte(); scbr = rd.ReadByte(); clsr = rd.ReadByte(); scmr = rd.ReadByte(); colr = rd.ReadByte(); por = rd.ReadByte();
        cbr = rd.ReadUInt16(); pipeline = rd.ReadByte(); r15Modified = rd.ReadBoolean(); sreg = rd.ReadInt32(); dreg = rd.ReadInt32(); ramAddr = rd.ReadUInt16(); romBuffer = rd.ReadByte();
        SnesStateIo.Get(rd, cache); SnesStateIo.Get(rd, cacheValid);
        pc0 = ReadPixels(rd); pc1 = ReadPixels(rd);
        lockedOut = rd.ReadBoolean();
        romPending = rd.ReadInt32(); ramPending = rd.ReadInt32(); ramPendingAddr = rd.ReadUInt16(); ramPendingData = rd.ReadByte();
        bool own = rd.ReadBoolean();
        if (own != ownsRam) throw new InvalidDataException("The Super FX part of the state was saved on a cartridge with a different RAM layout.");
        if (ownsRam) SnesStateIo.Get(rd, ram);
        if (sreg is < 0 or > 15 || dreg is < 0 or > 15) throw new InvalidDataException("The Super FX part of the state holds a register number outside 0-15.");
    }

    private static void WritePixels(BinaryWriter w, PixelCache p)
    {
        w.Write(p.Offset); w.Write(p.BitPend);
        w.Write(p.D0); w.Write(p.D1); w.Write(p.D2); w.Write(p.D3); w.Write(p.D4); w.Write(p.D5); w.Write(p.D6); w.Write(p.D7);
    }

    private static PixelCache ReadPixels(BinaryReader rd) => new()
    {
        Offset = rd.ReadUInt16(), BitPend = rd.ReadByte(),
        D0 = rd.ReadByte(), D1 = rd.ReadByte(), D2 = rd.ReadByte(), D3 = rd.ReadByte(), D4 = rd.ReadByte(), D5 = rd.ReadByte(), D6 = rd.ReadByte(), D7 = rd.ReadByte(),
    };
}
