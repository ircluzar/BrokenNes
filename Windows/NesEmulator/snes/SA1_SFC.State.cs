using System.IO;

namespace NesEmulator.Snes;

/// <summary>
/// The SA-1's save state: its own 65816 (the same code as the SNES CPU's), the clock it has run to, the 2 KB I-RAM, every control and status register of both directions (reset / wait lines, message nibbles, the three vectors it
/// can override, the interrupt enables and flags of both CPUs), the timer, the Super MMC bank registers and the BW-RAM mapping and write-protect registers, the DMA and character-conversion units (including the bitmap register file
/// and the line counter), the multiply / divide / accumulate unit and the variable-length bit reader. BW-RAM is the cartridge's RAM (saved with the cartridge) except on a board without any, where it is the chip's own and is written here.
/// </summary>
public sealed partial class SA1_SFC
{
    private const byte StateVersion = 1;

    public void SaveState(BinaryWriter w)
    {
        SnesStateIo.Begin(w, StateVersion);
        Cpu.SaveState(w);
        w.Write(clock); w.Write(mdr); w.Write(nmiRaised);
        SnesStateIo.Put(w, iram);
        w.Write(sa1Resb); w.Write(sa1Rdyb); w.Write(smeg); w.Write(sieIrq); w.Write(sieChdma); w.Write(crv); w.Write(cnv); w.Write(civ);
        w.Write(ivsw); w.Write(nvsw); w.Write(cmeg); w.Write(cieIrq); w.Write(cieTimer); w.Write(cieDma); w.Write(cieNmi); w.Write(snv); w.Write(siv);
        w.Write(snesIrqFlag); w.Write(chdmaIrqFlag); w.Write(sa1IrqFlag); w.Write(timerIrqFlag); w.Write(dmaIrqFlag); w.Write(sa1NmiFlag);
        w.Write(tmc); w.Write(hcnt); w.Write(vcnt); w.Write(timerBase); w.Write(hcr); w.Write(vcr);
        SnesStateIo.Put(w, mmc); w.Write(bmaps); w.Write(bmap); w.Write(sbwe); w.Write(cbwe); w.Write(bwpa); w.Write(siwp); w.Write(ciwp);
        w.Write(dcnt); w.Write(cdma); w.Write(sda); w.Write(dda); w.Write(dtc); w.Write(bbf2bpp); SnesStateIo.Put(w, brf); w.Write(ccLine); w.Write(cc1Active);
        w.Write(mcnt); w.Write(ma); w.Write(mb); w.Write(mr); w.Write(overflow);
        w.Write(vbAuto); w.Write(vbLength); w.Write(vbBit); w.Write(va);
        w.Write(ownsBwram);
        if (ownsBwram) SnesStateIo.Put(w, bwram);
    }

    public void LoadState(BinaryReader r)
    {
        SnesStateIo.Expect(r, StateVersion, "SA-1");
        Cpu.LoadState(r);
        clock = r.ReadInt64(); mdr = r.ReadByte(); nmiRaised = r.ReadBoolean();
        SnesStateIo.Get(r, iram);
        sa1Resb = r.ReadBoolean(); sa1Rdyb = r.ReadBoolean(); smeg = r.ReadByte(); sieIrq = r.ReadBoolean(); sieChdma = r.ReadBoolean(); crv = r.ReadUInt16(); cnv = r.ReadUInt16(); civ = r.ReadUInt16();
        ivsw = r.ReadBoolean(); nvsw = r.ReadBoolean(); cmeg = r.ReadByte(); cieIrq = r.ReadBoolean(); cieTimer = r.ReadBoolean(); cieDma = r.ReadBoolean(); cieNmi = r.ReadBoolean(); snv = r.ReadUInt16(); siv = r.ReadUInt16();
        snesIrqFlag = r.ReadBoolean(); chdmaIrqFlag = r.ReadBoolean(); sa1IrqFlag = r.ReadBoolean(); timerIrqFlag = r.ReadBoolean(); dmaIrqFlag = r.ReadBoolean(); sa1NmiFlag = r.ReadBoolean();
        tmc = r.ReadByte(); hcnt = r.ReadUInt16(); vcnt = r.ReadUInt16(); timerBase = r.ReadInt64(); hcr = r.ReadUInt16(); vcr = r.ReadUInt16();
        SnesStateIo.Get(r, mmc); bmaps = r.ReadByte(); bmap = r.ReadByte(); sbwe = r.ReadBoolean(); cbwe = r.ReadBoolean(); bwpa = r.ReadByte(); siwp = r.ReadByte(); ciwp = r.ReadByte();
        dcnt = r.ReadByte(); cdma = r.ReadByte(); sda = r.ReadUInt32(); dda = r.ReadUInt32(); dtc = r.ReadUInt16(); bbf2bpp = r.ReadBoolean(); SnesStateIo.Get(r, brf); ccLine = r.ReadInt32(); cc1Active = r.ReadBoolean();
        mcnt = r.ReadByte(); ma = r.ReadUInt16(); mb = r.ReadUInt16(); mr = r.ReadUInt64(); overflow = r.ReadBoolean();
        vbAuto = r.ReadBoolean(); vbLength = r.ReadInt32(); vbBit = r.ReadInt32(); va = r.ReadUInt32();
        bool own = r.ReadBoolean();
        if (own != ownsBwram) throw new InvalidDataException("The SA-1 part of the state was saved on a cartridge with a different BW-RAM layout.");
        if (ownsBwram) SnesStateIo.Get(r, bwram);
        if (vbLength is < 1 or > 16 || vbBit is < 0 or > 7) throw new InvalidDataException("The SA-1 part of the state holds a bit-reader position outside its range.");
    }
}
