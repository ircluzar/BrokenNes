using System;

namespace NesEmulator.Snes;

/// <summary>
/// SA-1 - Nintendo's cartridge coprocessor (Kirby Super Star, Super Mario RPG, ...). A second
/// 65C816 at 10.74 MHz (half the master clock) with its own view of the cartridge, plus:
/// the "Super MMC" ROM bank mapper, 2KB I-RAM shared by both CPUs, BW-RAM (the battery RAM,
/// also viewable as a 2/4bpp bitmap), inter-CPU interrupts and message nibbles, a DMA unit with
/// character conversion (bitmap -> SNES tiles), a multiply/divide/multiply-accumulate unit, a
/// variable-length bit reader and an H/V timer.
///
/// The SA-1 CPU is a second <see cref="CPU_SFC"/> on this object's bus. It runs lazily: every SNES
/// access to shared state (I-RAM, BW-RAM, SA-1 ports) first runs the SA-1 up to the SNES clock,
/// and the board calls <see cref="RunTo"/> every scanline so interrupts to the SNES are never more
/// than a line late. ROM is read-only, so the board serves SNES ROM reads straight from its page
/// table without syncing.
/// </summary>
public sealed class SA1_SFC : ISnesCoprocessor, ISnesBus
{
    public string Name => "SA-1";

    private readonly byte[] rom;
    private readonly byte[] bwram;
    private readonly int bwMask;
    private readonly byte[] iram = new byte[0x800];
    public CPU_SFC Cpu { get; }

    private Action<bool>? setSnesIrq;
    private Action? remapPages;

    private long clock;          // master clocks the SA-1 has run to
    private byte mdr;
    private bool nmiRaised;      // an NMI was raised since the last Step (CPU_SFC keeps its latch private)

    // ---- SNES -> SA-1 control ($2200-$2208) ----
    private bool sa1Resb = true, sa1Rdyb;
    private byte smeg;
    private bool sieIrq, sieChdma;
    private ushort crv, cnv, civ;

    // ---- SA-1 -> SNES control ($2209-$220F) ----
    private bool ivsw, nvsw;
    private byte cmeg;
    private bool cieIrq, cieTimer, cieDma, cieNmi;
    private ushort snv, siv;

    // Interrupt flags: SFR ($2300, SNES side) and CFR ($2301, SA-1 side).
    private bool snesIrqFlag, chdmaIrqFlag;
    private bool sa1IrqFlag, timerIrqFlag, dmaIrqFlag, sa1NmiFlag;

    // ---- Timer ($2210-$2215) ----
    private byte tmc;
    private ushort hcnt, vcnt;
    private long timerBase;
    private ushort hcr, vcr;

    // ---- Memory mapping ($2220-$222A) ----
    private readonly byte[] mmc = new byte[4];     // CXB DXB EXB FXB
    private byte bmaps, bmap;
    private bool sbwe, cbwe;
    private byte bwpa = 0x0F;
    private byte siwp, ciwp;

    // ---- DMA ($2230-$2239, $223F-$224F) ----
    private byte dcnt, cdma;
    private uint sda, dda;
    private ushort dtc;
    private bool bbf2bpp;
    private readonly byte[] brf = new byte[16];
    private int ccLine;
    private bool cc1Active;

    // ---- Arithmetic ($2250-$2254) ----
    private byte mcnt;
    private ushort ma, mb;
    private ulong mr;
    private bool overflow;

    // ---- Variable-length bit reader ($2258-$225B) ----
    private bool vbAuto;
    private int vbLength = 16, vbBit;
    private uint va;

    public SA1_SFC(SnesCartridge cart)
    {
        rom = cart.Rom;
        bwram = cart.Sram.Length > 0 ? cart.Sram : new byte[0x800];
        bwMask = (bwram.Length & (bwram.Length - 1)) == 0 ? bwram.Length - 1 : -1;
        Cpu = new CPU_SFC(this);
        Reset();
    }

    public void Attach(Action<bool> setIrq, Action remap) { setSnesIrq = setIrq; remapPages = remap; }

    public void Reset()
    {
        sa1Resb = true; sa1Rdyb = false; smeg = 0;
        sieIrq = sieChdma = false;
        crv = cnv = civ = snv = siv = 0;
        ivsw = nvsw = false; cmeg = 0;
        cieIrq = cieTimer = cieDma = cieNmi = false;
        snesIrqFlag = chdmaIrqFlag = sa1IrqFlag = timerIrqFlag = dmaIrqFlag = sa1NmiFlag = false;
        tmc = 0; hcnt = vcnt = 0; timerBase = clock;
        mmc[0] = 0; mmc[1] = 1; mmc[2] = 2; mmc[3] = 3;
        bmaps = bmap = 0; sbwe = cbwe = false; bwpa = 0x0F; siwp = ciwp = 0;
        dcnt = cdma = 0; sda = dda = 0; dtc = 0; bbf2bpp = false; ccLine = 0; cc1Active = false;
        mcnt = 0; ma = mb = 0; mr = 0; overflow = false;
        vbAuto = false; vbLength = 16; vbBit = 0; va = 0;
        Array.Clear(iram);
        UpdateSnesIrq();
        remapPages?.Invoke();
    }

    public long Instructions => Cpu.InstructionCount;

    // =====================================================================================
    //  Scheduling
    // =====================================================================================

    public void RunTo(long masterClock)
    {
        while (clock < masterClock)
        {
            if (sa1Resb || sa1Rdyb) { clock = masterClock; break; }
            if (Cpu.Waiting && !nmiRaised && !Sa1IrqLine())
            {
                // Nothing but the timer (or the SNES, which can't act until we return) wakes it.
                long wake = NextTimerEvent(clock);
                if (wake >= masterClock) { clock = masterClock; break; }
                if (wake > clock) clock = wake;
            }
            long before = clock;
            nmiRaised = false;
            Cpu.Step();
            if ((tmc & 3) != 0) CheckTimer(before, clock);
        }
    }

    // =====================================================================================
    //  SNES side (the board)
    // =====================================================================================

    public bool Owns(uint bank, uint offset)
    {
        if ((bank & 0x40) == 0)
            return offset >= 0x8000 || (offset >= 0x2200 && offset < 0x2400) || (offset >= 0x3000 && offset < 0x3800) || offset >= 0x6000;
        return bank >= 0xC0 || (bank & 0xF0) == 0x40;
    }

    public bool TryMapPage(uint bank, uint offset, out byte[]? data, out int index)
    {
        data = null; index = 0;
        bool romPage = (bank & 0x40) == 0 ? offset >= 0x8000 : bank >= 0xC0;
        // Bank 0's last page holds the vectors the SA-1 can redirect (SNV/SIV), so it stays slow.
        if (!romPage || (bank == 0x00 && offset >= 0xF000)) return false;
        data = rom;
        index = RomIndex(bank, offset);
        return true;
    }

    public byte Read(uint bank, uint offset, long masterClock)
    {
        if ((bank & 0x40) == 0 && offset >= 0x8000 || bank >= 0xC0)
        {
            if (bank == 0 && offset >= 0xFFE0)
            {
                if (nvsw && offset is 0xFFEA or 0xFFEB) return (byte)(offset == 0xFFEA ? snv : snv >> 8);
                if (ivsw && offset is 0xFFEE or 0xFFEF) return (byte)(offset == 0xFFEE ? siv : siv >> 8);
            }
            return rom[RomIndex(bank, offset)];
        }
        RunTo(masterClock);
        if ((bank & 0x40) == 0)
        {
            if (offset < 0x2400) return ReadIo(offset);
            if (offset < 0x3800) return iram[offset & 0x7FF];
            uint a = (uint)(bmaps & 0x1F) * 0x2000 + (offset & 0x1FFF);
            return cc1Active ? Cc1Read(a) : bwram[Bw(a)];
        }
        uint linear = (bank & 0x0F) << 16 | offset;   // $40-$4F
        return cc1Active ? Cc1Read(linear) : bwram[Bw(linear)];
    }

    public void Write(uint bank, uint offset, byte value, long masterClock)
    {
        if ((bank & 0x40) == 0 && offset >= 0x8000 || bank >= 0xC0) return;   // ROM
        RunTo(masterClock);
        if ((bank & 0x40) == 0)
        {
            if (offset < 0x2400) { WriteIo(offset, value); return; }
            if (offset < 0x3800) { if ((siwp >> (int)((offset >> 8) & 7) & 1) != 0) iram[offset & 0x7FF] = value; return; }
            uint a = (uint)(bmaps & 0x1F) * 0x2000 + (offset & 0x1FFF);
            if (BwWritable(a, sbwe)) bwram[Bw(a)] = value;
            return;
        }
        uint linear = (bank & 0x0F) << 16 | offset;
        if (BwWritable(linear, sbwe)) bwram[Bw(linear)] = value;
    }

    // =====================================================================================
    //  SA-1 side (ISnesBus for the SA-1's own 65C816)
    // =====================================================================================

    // One SA-1 cycle is 2 master clocks. ROM and I-RAM take one cycle, BW-RAM two
    // (bus conflicts with the SNES CPU, which cost extra cycles on hardware, are not modeled).

    public void Idle() => clock += 2;

    // Bus conflicts: when the SNES CPU is on the same bus (ROM, BW-RAM or I-RAM) the SA-1 waits.
    // The SNES address is sampled when the SA-1 catches up, so this is statistical, not cycle-exact.
    public static bool BusConflicts = true;
    private Func<uint>? snesBus;
    public void AttachBusProbe(Func<uint> snesBusAddress) => snesBus = snesBusAddress;

    private static bool SnesOnRom(uint a) => (a & 0x408000) == 0x008000 || (a & 0xC00000) == 0xC00000;
    private static bool SnesOnBwram(uint a) => (a & 0x40E000) == 0x006000 || (a & 0xF00000) == 0x400000;
    private static bool SnesOnIram(uint a) => (a & 0x40F800) == 0x003000;

    private void RomAccess() { clock += 2; if (BusConflicts && snesBus != null && SnesOnRom(snesBus())) clock += 2; }
    private void BwAccess() { clock += 4; if (BusConflicts && snesBus != null && SnesOnBwram(snesBus())) clock += 4; }
    private void IramAccess() { clock += 2; if (BusConflicts && snesBus != null && SnesOnIram(snesBus())) clock += 4; }

    byte ISnesBus.Read(uint address)
    {
        uint bank = address >> 16, offset = address & 0xFFFF;
        if ((bank & 0x40) == 0)
        {
            if (offset >= 0x8000)
            {
                RomAccess();
                if (bank == 0 && offset >= 0xFFE0)
                {
                    switch (offset)
                    {
                        case 0xFFEA: case 0xFFFA: return mdr = (byte)cnv;
                        case 0xFFEB: case 0xFFFB: return mdr = (byte)(cnv >> 8);
                        case 0xFFEE: case 0xFFFE: return mdr = (byte)civ;
                        case 0xFFEF: case 0xFFFF: return mdr = (byte)(civ >> 8);
                        case 0xFFFC: return mdr = (byte)crv;
                        case 0xFFFD: return mdr = (byte)(crv >> 8);
                    }
                }
                return mdr = rom[RomIndex(bank, offset)];
            }
            if (offset < 0x0800 || (offset >= 0x3000 && offset < 0x3800)) { IramAccess(); return mdr = iram[offset & 0x7FF]; }
            if (offset >= 0x2200 && offset < 0x2400) { clock += 2; return mdr = ReadIo(offset); }
            if (offset >= 0x6000)
            {
                BwAccess();
                uint w = (uint)(bmap & 0x7F) * 0x2000 + (offset & 0x1FFF);
                return mdr = (bmap & 0x80) != 0 ? BitmapRead(w) : bwram[Bw(w)];
            }
            clock += 2;
            return mdr;
        }
        if (bank >= 0xC0) { RomAccess(); return mdr = rom[RomIndex(bank, offset)]; }
        if ((bank & 0xF0) == 0x40) { BwAccess(); return mdr = bwram[Bw((bank & 0x0F) << 16 | offset)]; }
        if ((bank & 0xF0) == 0x60) { BwAccess(); return mdr = BitmapRead((bank & 0x0F) << 16 | offset); }
        clock += 2;
        return mdr;
    }

    void ISnesBus.Write(uint address, byte value)
    {
        mdr = value;
        uint bank = address >> 16, offset = address & 0xFFFF;
        if ((bank & 0x40) == 0)
        {
            if (offset >= 0x8000) { RomAccess(); return; }
            if (offset < 0x0800 || (offset >= 0x3000 && offset < 0x3800))
            {
                IramAccess();
                if ((ciwp >> (int)((offset >> 8) & 7) & 1) != 0) iram[offset & 0x7FF] = value;
                return;
            }
            if (offset >= 0x2200 && offset < 0x2400) { clock += 2; WriteIo(offset, value); return; }
            if (offset >= 0x6000)
            {
                BwAccess();
                uint w = (uint)(bmap & 0x7F) * 0x2000 + (offset & 0x1FFF);
                if ((bmap & 0x80) != 0) BitmapWrite(w, value);
                else if (BwWritable(w, cbwe)) bwram[Bw(w)] = value;
                return;
            }
            clock += 2;
            return;
        }
        if ((bank & 0xF0) == 0x40) { BwAccess(); uint a = (bank & 0x0F) << 16 | offset; if (BwWritable(a, cbwe)) bwram[Bw(a)] = value; return; }
        if ((bank & 0xF0) == 0x60) { BwAccess(); BitmapWrite((bank & 0x0F) << 16 | offset, value); return; }
        clock += 2;
    }

    // =====================================================================================
    //  Memory helpers
    // =====================================================================================

    /// <summary>Super MMC: four 1MB windows chosen by CXB/DXB/EXB/FXB.</summary>
    private int RomIndex(uint bank, uint offset)
    {
        int index;
        if ((bank & 0x40) == 0)
        {
            // $00-1F CXB, $20-3F DXB, $80-9F EXB, $A0-BF FXB. With bit 7 clear the window is
            // fixed to its power-on block (0-3), LoROM style.
            int sel = (int)((bank >> 6) & 2 | (bank >> 5) & 1);
            int block = (mmc[sel] & 0x80) != 0 ? mmc[sel] & 7 : sel;
            index = block << 20 | (int)(bank & 0x1F) << 15 | (int)(offset & 0x7FFF);
        }
        else
        {
            int block = mmc[(bank >> 4) & 3] & 7;                    // $C0-CF .. $F0-FF, HiROM style
            index = block << 20 | (int)(bank & 0x0F) << 16 | (int)offset;
        }
        return index < rom.Length ? index : index % rom.Length;
    }

    private int Bw(uint a) => bwMask >= 0 ? (int)(a & (uint)bwMask) : (int)(a % (uint)bwram.Length);

    /// <summary>Writes into the first 256 &lt;&lt; BWPA bytes need the side's write-enable bit.</summary>
    private bool BwWritable(uint a, bool enabled) => enabled || (a & 0x3FFFF) >= (0x100u << (bwpa & 0x0F));

    private byte BitmapRead(uint a)
    {
        if (bbf2bpp) return (byte)((bwram[Bw(a >> 2)] >> (int)((a & 3) * 2)) & 3);
        return (byte)((bwram[Bw(a >> 1)] >> (int)((a & 1) * 4)) & 15);
    }

    private void BitmapWrite(uint a, byte value)
    {
        if (bbf2bpp)
        {
            int i = Bw(a >> 2), shift = (int)((a & 3) * 2);
            bwram[i] = (byte)((bwram[i] & ~(3 << shift)) | (value & 3) << shift);
        }
        else
        {
            int i = Bw(a >> 1), shift = (int)((a & 1) * 4);
            bwram[i] = (byte)((bwram[i] & ~(15 << shift)) | (value & 15) << shift);
        }
    }

    // =====================================================================================
    //  Registers
    // =====================================================================================

    private byte ReadIo(uint offset)
    {
        switch (offset)
        {
            case 0x2300:   // SFR
                return (byte)((snesIrqFlag ? 0x80 : 0) | (ivsw ? 0x40 : 0) | (chdmaIrqFlag ? 0x20 : 0) | (nvsw ? 0x10 : 0) | cmeg);
            case 0x2301:   // CFR
                return (byte)((sa1IrqFlag ? 0x80 : 0) | (timerIrqFlag ? 0x40 : 0) | (dmaIrqFlag ? 0x20 : 0) | (sa1NmiFlag ? 0x10 : 0) | smeg);
            case 0x2302: LatchCounters(); return (byte)hcr;
            case 0x2303: return (byte)(hcr >> 8);
            case 0x2304: return (byte)vcr;
            case 0x2305: return (byte)(vcr >> 8);
            case 0x2306: return (byte)mr;
            case 0x2307: return (byte)(mr >> 8);
            case 0x2308: return (byte)(mr >> 16);
            case 0x2309: return (byte)(mr >> 24);
            case 0x230A: return (byte)(mr >> 32);
            case 0x230B: return (byte)(overflow ? 0x80 : 0);
            case 0x230C: return (byte)VariableData();
            case 0x230D:
            {
                byte v = (byte)(VariableData() >> 8);
                if (vbAuto) AdvanceVariable();
                return v;
            }
            case 0x230E: return 0x23;   // version
        }
        return mdr;
    }

    private void WriteIo(uint offset, byte v)
    {
        switch (offset)
        {
            // ---- written by the SNES ----
            case 0x2200:   // CCNT
            {
                bool wasReset = sa1Resb;
                sa1Resb = (v & 0x20) != 0;
                sa1Rdyb = (v & 0x40) != 0;
                smeg = (byte)(v & 0x0F);
                if (wasReset && !sa1Resb) Cpu.Reset();   // releasing reset boots the SA-1 from CRV
                if ((v & 0x80) != 0) { sa1IrqFlag = true; UpdateSa1Irq(); }
                if ((v & 0x10) != 0) { sa1NmiFlag = true; if (cieNmi) { Cpu.RaiseNmi(); nmiRaised = true; } }
                break;
            }
            case 0x2201: sieIrq = (v & 0x80) != 0; sieChdma = (v & 0x20) != 0; UpdateSnesIrq(); break;
            case 0x2202:
                if ((v & 0x80) != 0) snesIrqFlag = false;
                if ((v & 0x20) != 0) chdmaIrqFlag = false;
                UpdateSnesIrq();
                break;
            case 0x2203: crv = (ushort)((crv & 0xFF00) | v); break;
            case 0x2204: crv = (ushort)((crv & 0x00FF) | v << 8); break;
            case 0x2205: cnv = (ushort)((cnv & 0xFF00) | v); break;
            case 0x2206: cnv = (ushort)((cnv & 0x00FF) | v << 8); break;
            case 0x2207: civ = (ushort)((civ & 0xFF00) | v); break;
            case 0x2208: civ = (ushort)((civ & 0x00FF) | v << 8); break;

            // ---- written by the SA-1 ----
            case 0x2209:   // SCNT
            {
                bool vectors = ((v & 0x50) != 0) != (ivsw || nvsw);
                ivsw = (v & 0x40) != 0;
                nvsw = (v & 0x10) != 0;
                cmeg = (byte)(v & 0x0F);
                if ((v & 0x80) != 0) { snesIrqFlag = true; UpdateSnesIrq(); }
                _ = vectors;   // bank 0's vector page is always on the slow path, nothing to remap
                break;
            }
            case 0x220A:
                cieIrq = (v & 0x80) != 0; cieTimer = (v & 0x40) != 0; cieDma = (v & 0x20) != 0; cieNmi = (v & 0x10) != 0;
                UpdateSa1Irq();
                break;
            case 0x220B:
                if ((v & 0x80) != 0) sa1IrqFlag = false;
                if ((v & 0x40) != 0) timerIrqFlag = false;
                if ((v & 0x20) != 0) dmaIrqFlag = false;
                if ((v & 0x10) != 0) sa1NmiFlag = false;
                UpdateSa1Irq();
                break;
            case 0x220C: snv = (ushort)((snv & 0xFF00) | v); break;
            case 0x220D: snv = (ushort)((snv & 0x00FF) | v << 8); break;
            case 0x220E: siv = (ushort)((siv & 0xFF00) | v); break;
            case 0x220F: siv = (ushort)((siv & 0x00FF) | v << 8); break;
            case 0x2210: tmc = v; break;
            case 0x2211: timerBase = clock; break;   // CTR: restart the timer
            case 0x2212: hcnt = (ushort)((hcnt & 0x100) | v); break;
            case 0x2213: hcnt = (ushort)((hcnt & 0x0FF) | (v & 1) << 8); break;
            case 0x2214: vcnt = (ushort)((vcnt & 0x100) | v); break;
            case 0x2215: vcnt = (ushort)((vcnt & 0x0FF) | (v & 1) << 8); break;

            // ---- memory mapping ----
            case 0x2220: case 0x2221: case 0x2222: case 0x2223:
                if (mmc[offset & 3] != v) { mmc[offset & 3] = v; remapPages?.Invoke(); }
                break;
            case 0x2224: bmaps = v; break;
            case 0x2225: bmap = v; break;
            case 0x2226: sbwe = (v & 0x80) != 0; break;
            case 0x2227: cbwe = (v & 0x80) != 0; break;
            case 0x2228: bwpa = v; break;
            case 0x2229: siwp = v; break;
            case 0x222A: ciwp = v; break;

            // ---- DMA ----
            case 0x2230: dcnt = v; if ((v & 0x80) == 0) cc1Active = false; break;
            case 0x2231:
                cdma = v;
                ccLine = 0;
                if ((v & 0x80) != 0) cc1Active = false;   // CHDEND: the SNES has everything
                break;
            case 0x2232: sda = (sda & 0xFFFF00) | v; break;
            case 0x2233: sda = (sda & 0xFF00FF) | (uint)v << 8; break;
            case 0x2234: sda = (sda & 0x00FFFF) | (uint)v << 16; break;
            case 0x2235: dda = (dda & 0xFFFF00) | v; break;
            case 0x2236:
                dda = (dda & 0xFF00FF) | (uint)v << 8;
                if ((dcnt & 0x80) != 0)
                {
                    if ((dcnt & 0x20) == 0 && (dcnt & 0x04) == 0) DmaNormal();   // to I-RAM
                    else if ((dcnt & 0x30) == 0x30) StartCc1();
                }
                break;
            case 0x2237:
                dda = (dda & 0x00FFFF) | (uint)v << 16;
                if ((dcnt & 0xA4) == 0x84) DmaNormal();                             // to BW-RAM
                break;
            case 0x2238: dtc = (ushort)((dtc & 0xFF00) | v); break;
            case 0x2239: dtc = (ushort)((dtc & 0x00FF) | v << 8); break;
            case 0x223F: bbf2bpp = (v & 0x80) != 0; break;
            case >= 0x2240 and <= 0x224F:
                brf[offset & 15] = v;
                if ((offset & 7) == 7 && (dcnt & 0xB0) == 0xA0) Cc2Line();
                break;

            // ---- arithmetic ----
            case 0x2250: mcnt = v; if ((v & 2) != 0) mr = 0; break;
            case 0x2251: ma = (ushort)((ma & 0xFF00) | v); break;
            case 0x2252: ma = (ushort)((ma & 0x00FF) | v << 8); break;
            case 0x2253: mb = (ushort)((mb & 0xFF00) | v); break;
            case 0x2254: mb = (ushort)((mb & 0x00FF) | v << 8); Arithmetic(); break;

            // ---- variable-length bit reader ----
            case 0x2258:
                vbAuto = (v & 0x80) != 0;
                vbLength = (v & 0x0F) == 0 ? 16 : v & 0x0F;
                if (!vbAuto) AdvanceVariable();   // fixed mode: each write steps the reader
                break;
            case 0x2259: va = (va & 0xFFFF00) | v; break;
            case 0x225A: va = (va & 0xFF00FF) | (uint)v << 8; break;
            case 0x225B: va = (va & 0x00FFFF) | (uint)v << 16; vbBit = 0; break;
        }
    }

    // ---- Interrupt lines ----

    private bool Sa1IrqLine() => (sa1IrqFlag && cieIrq) || (timerIrqFlag && cieTimer) || (dmaIrqFlag && cieDma);
    private void UpdateSa1Irq() => Cpu.SetIrq(Sa1IrqLine());
    private void UpdateSnesIrq() => setSnesIrq?.Invoke((snesIrqFlag && sieIrq) || (chdmaIrqFlag && sieChdma));

    // ---- Timer ----

    // HV mode counts like the PPU (1364 master clocks per line, 262 lines); linear mode is one
    // 18-bit counter (512 "lines" of 1364) - approximated with the same geometry.
    private int LinesPerFrame => (tmc & 0x80) != 0 ? 512 : 262;

    private void LatchCounters()
    {
        long t = clock - timerBase;
        long lineLen = 1364, frame = lineLen * LinesPerFrame;
        long pos = ((t % frame) + frame) % frame;
        hcr = (ushort)(pos % lineLen / 4);
        vcr = (ushort)(pos / lineLen);
    }

    /// <summary>Period and phase (master clocks since timerBase) of the timer IRQ, or false when off.</summary>
    private bool TimerEvent(out long period, out long phase)
    {
        long frame = 1364L * LinesPerFrame;
        switch (tmc & 3)
        {
            case 1: period = 1364; phase = hcnt * 4L; return true;
            case 2: period = frame; phase = vcnt * 1364L; return true;
            case 3: period = frame; phase = vcnt * 1364L + hcnt * 4L; return true;
            default: period = phase = 0; return false;
        }
    }

    private void CheckTimer(long from, long to)
    {
        if (!TimerEvent(out long period, out long phase)) return;
        long a = Math.DivRem(from - timerBase - phase + period * 1024, period, out _);
        long b = Math.DivRem(to - timerBase - phase + period * 1024, period, out _);
        if (b != a) { timerIrqFlag = true; UpdateSa1Irq(); }
    }

    private long NextTimerEvent(long now)
    {
        if (!cieTimer || !TimerEvent(out long period, out long phase)) return long.MaxValue;
        long rel = now - timerBase - phase;
        long n = rel >= 0 ? rel / period + 1 : 0;
        return timerBase + phase + n * period;
    }

    // ---- Arithmetic unit ----

    private void Arithmetic()
    {
        if ((mcnt & 2) != 0)
        {
            // Multiply-accumulate into the 40-bit result.
            long sum = (long)(mr << 24) >> 24;
            sum += (short)ma * (short)mb;
            overflow = sum >= 1L << 39 || sum < -(1L << 39);
            mr = (ulong)sum & 0xFF_FFFF_FFFF;
            mb = 0;
        }
        else if ((mcnt & 1) == 0)
        {
            mr = (uint)((short)ma * (short)mb);
            mb = 0;
        }
        else
        {
            // Signed dividend, unsigned divisor; remainder is never negative.
            if (mb == 0) mr = 0;
            else
            {
                int dividend = (short)ma, divisor = mb;
                int rem = ((dividend % divisor) + divisor) % divisor;
                int quo = (dividend - rem) / divisor;
                mr = (uint)((ushort)rem << 16 | (ushort)quo);
            }
            ma = mb = 0;
        }
    }

    // ---- Variable-length bit reader ----

    private int VariableData()
    {
        int d = PeekRom(va) | PeekRom(va + 1) << 8 | PeekRom(va + 2) << 16;
        return d >> vbBit;
    }

    private void AdvanceVariable()
    {
        vbBit += vbLength;
        va = (va + (uint)(vbBit >> 3)) & 0xFFFFFF;
        vbBit &= 7;
    }

    private byte PeekRom(uint a)
    {
        uint bank = (a >> 16) & 0xFF, offset = a & 0xFFFF;
        if ((bank & 0x40) == 0 ? offset >= 0x8000 : bank >= 0xC0) return rom[RomIndex(bank, offset)];
        return 0;
    }

    // ---- DMA ----

    private void DmaNormal()
    {
        int source = dcnt & 3;          // 0 ROM, 1 BW-RAM, 2 I-RAM
        bool toBwram = (dcnt & 4) != 0;
        int count = dtc;
        for (int i = 0; i < count; i++)
        {
            byte d = source switch
            {
                0 => PeekRom(sda),
                1 => bwram[Bw(sda & 0xFFFFF)],
                _ => iram[sda & 0x7FF],
            };
            if (toBwram) bwram[Bw(dda & 0x3FFFF)] = d;
            else iram[dda & 0x7FF] = d;
            sda = (sda + 1) & 0xFFFFFF;
            dda = (dda + 1) & 0xFFFFFF;
            clock += source == 1 || toBwram ? 4 : 2;
        }
        dtc = 0;
        dmaIrqFlag = true;
        UpdateSa1Irq();
    }

    /// <summary>Type 1: the SNES DMAs straight out of BW-RAM and gets tiles instead of a bitmap.</summary>
    private void StartCc1()
    {
        cc1Active = true;
        chdmaIrqFlag = true;   // tell the SNES the conversion is ready to be read
        UpdateSnesIrq();
    }

    private byte Cc1Read(uint addr)
    {
        int depth = cdma & 3;                        // 0 = 8bpp, 1 = 4bpp, 2 = 2bpp
        if (depth == 3) depth = 2;
        int charMask = (1 << (6 - depth)) - 1;       // 64/32/16 bytes per character
        if ((addr & (uint)charMask) == 0)
        {
            // Convert the character this read starts into I-RAM at DDA.
            int widthShift = Math.Min((cdma >> 2) & 7, 5);
            int bpp = 2 << (2 - depth);                               // bytes per 8-pixel row in the bitmap
            int bpl = (8 << widthShift) >> depth;                     // bitmap bytes per pixel line
            uint rel = (uint)((addr - sda) & 0xFFFFF);
            int tile = (int)(rel >> (6 - depth));
            int ty = tile >> widthShift, tx = tile & ((1 << widthShift) - 1);
            uint bw = (uint)(sda + ty * 8 * bpl + tx * bpp);
            Span<byte> row = stackalloc byte[8];
            for (int y = 0; y < 8; y++)
            {
                ulong data = 0;
                for (int b = 0; b < bpp; b++) data |= (ulong)bwram[Bw(bw + (uint)b)] << (b * 8);
                bw += (uint)bpl;
                row.Clear();
                int planes = bpp;   // 2, 4 or 8 bitplanes
                for (int x = 0; x < 8; x++)
                    for (int p = 0; p < planes; p++)
                    {
                        row[p] |= (byte)((data & 1) << (7 - x));
                        data >>= 1;
                    }
                for (int b = 0; b < bpp; b++)
                    iram[(dda + (uint)(y << 1) + (uint)((b & 6) << 3) + (uint)(b & 1)) & 0x7FF] = row[b];
            }
        }
        return iram[(dda + (addr & (uint)charMask)) & 0x7FF];
    }

    /// <summary>Type 2: the SA-1 writes 8 pixels into BRF; each full half becomes one tile row in I-RAM.</summary>
    private void Cc2Line()
    {
        int depth = cdma & 3;
        if (depth == 3) depth = 2;
        int bpp = 2 << (2 - depth);
        int half = (ccLine & 1) << 3;
        uint addr = dda & 0x7FF;
        addr &= ~(uint)((1 << (7 - depth)) - 1);
        addr += (uint)((ccLine & 8) * bpp);
        addr += (uint)((ccLine & 7) * 2);
        for (int b = 0; b < bpp; b++)
        {
            int output = 0;
            for (int bit = 0; bit < 8; bit++) output |= ((brf[half + bit] >> b) & 1) << (7 - bit);
            iram[(addr + (uint)((b & 6) << 3) + (uint)(b & 1)) & 0x7FF] = (byte)output;
        }
        ccLine = (ccLine + 1) & 15;
    }

    public string Describe() =>
        $"SA-1 {(sa1Resb ? "RESET" : sa1Rdyb ? "WAIT" : Cpu.Waiting ? "WAI" : "run")} pc=${Cpu.PBR:X2}:{Cpu.PC:X4} instr={Cpu.InstructionCount:N0} " +
        $"mmc={mmc[0]:X2},{mmc[1]:X2},{mmc[2]:X2},{mmc[3]:X2} bmaps={bmaps:X2} bmap={bmap:X2} sfr/cfr=${ReadIo(0x2300):X2}/${ReadIo(0x2301):X2} " +
        $"sie={(sieIrq ? "I" : "-")}{(sieChdma ? "C" : "-")} cie=${(cieIrq ? 0x80 : 0) | (cieTimer ? 0x40 : 0) | (cieDma ? 0x20 : 0) | (cieNmi ? 0x10 : 0):X2} " +
        $"dcnt=${dcnt:X2} cdma=${cdma:X2} tmc=${tmc:X2} siwp/ciwp=${siwp:X2}/${ciwp:X2} sbwe={sbwe} cbwe={cbwe} bwpa=${bwpa:X2}";
}
