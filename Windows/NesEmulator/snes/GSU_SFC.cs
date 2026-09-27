using System;
using System.Linq;

namespace NesEmulator.Snes;

/// <summary>
/// Super FX (GSU-1 "Mario chip" / GSU-2) - the cartridge RISC CPU behind Star Fox and Yoshi's
/// Island. Sixteen 16-bit registers (R15 = PC, R14 = ROM pointer), one-byte opcodes modified by
/// prefix instructions (ALT1/2/3, TO/FROM/WITH), a one-byte fetch pipeline that gives every jump a
/// delay slot, a 512-byte instruction cache, a ROM read buffer, and a pixel unit (PLOT/RPIX) that
/// writes SNES bitplane tiles straight into cartridge RAM through a two-entry pixel cache.
///
/// While the GSU runs with ROM/RAM access (SCMR.RON/RAN) the SNES CPU is locked out of them: ROM
/// reads return a fixed vector table that points into WRAM, which is where games park their NMI/IRQ
/// handlers while the GSU renders. Scheduling is lazy like the SA-1: shared accesses catch the GSU
/// up to the SNES clock, and the board calls <see cref="RunTo"/> every scanline.
/// </summary>
public sealed class GSU_SFC : ISnesCoprocessor
{
    public string Name { get; }

    private readonly byte[] rom;
    private readonly byte[] ram;
    private readonly int ramMask;
    private readonly byte version;

    private Action<bool>? setSnesIrq;
    private Action? remapPages;

    private long clock;
    public long Instructions { get; private set; }
    public long Stops { get; private set; }

    // ---- Registers ----
    private readonly ushort[] r = new ushort[16];
    private ushort sfr;
    private byte pbr, rombr, rambr, bramr, cfgr, scbr, clsr, scmr, colr, por;
    private ushort cbr;
    private byte pipeline = 0x01;
    private bool r15Modified;
    private int sreg, dreg;
    private ushort ramAddr;       // last RAM address used by a load/store (SBK writes back to it)
    private byte romBuffer;

    private const ushort FZ = 0x0002, FCY = 0x0004, FS = 0x0008, FOV = 0x0010, FG = 0x0020, FR = 0x0040,
                         FALT1 = 0x0100, FALT2 = 0x0200, FB = 0x1000, FIRQ = 0x8000;

    // ---- Cache ----
    private readonly byte[] cache = new byte[512];
    private readonly bool[] cacheValid = new bool[32];

    // ---- Pixel cache: [0] = current 8-pixel group, [1] = previous (flushed on the next change) ----
    private struct PixelCache { public ushort Offset; public byte BitPend; public byte D0, D1, D2, D3, D4, D5, D6, D7; }
    private PixelCache pc0, pc1;

    private bool lockedOut;       // the SNES currently sees the fake ROM (for page-table remaps)

    public GSU_SFC(SnesCartridge cart)
    {
        rom = cart.Rom;
        ram = cart.Sram.Length > 0 ? cart.Sram : new byte[0x8000];
        ramMask = (ram.Length & (ram.Length - 1)) == 0 ? ram.Length - 1 : 0x7FFF;
        bool gsu2 = cart.ChipsetByte is 0x15 or 0x1A;
        version = (byte)(gsu2 ? 4 : 1);
        Name = gsu2 ? "Super FX 2" : "Super FX";
        Reset();
    }

    public void Attach(Action<bool> setIrq, Action remap) { setSnesIrq = setIrq; remapPages = remap; }

    public void Reset()
    {
        Array.Clear(r);
        sfr = 0; pbr = rombr = rambr = bramr = cfgr = scbr = clsr = scmr = colr = por = 0;
        cbr = 0; pipeline = 0x01; r15Modified = false; sreg = dreg = 0; ramAddr = 0; romBuffer = 0;
        romPending = ramPending = 0;
        romPending = ramPending = 0;
        FlushCache();
        pc0 = pc1 = default;
        setSnesIrq?.Invoke(false);
        UpdateLockout();
    }

    private bool Running => (sfr & FG) != 0;
    private int Cycle => clsr != 0 ? 1 : 2;           // master clocks per GSU cycle (21.4 / 10.7 MHz)
    private int MemCycle => clsr != 0 ? 5 : 6;        // one ROM/RAM access

    private void UpdateLockout()
    {
        bool now = Running && (scmr & 0x10) != 0;
        if (now != lockedOut) { lockedOut = now; remapPages?.Invoke(); }
    }

    // =====================================================================================
    //  Scheduling
    // =====================================================================================

    public void RunTo(long masterClock)
    {
        while (clock < masterClock)
        {
            if (!Running) { clock = masterClock; return; }
            Step();
        }
    }

    // =====================================================================================
    //  SNES side
    // =====================================================================================

    public bool Owns(uint bank, uint offset)
    {
        if ((bank & 0x40) == 0)
            return offset >= 0x8000 || (offset >= 0x3000 && offset < 0x3500) || (offset >= 0x6000 && offset < 0x8000);
        uint b = bank & 0x7F;
        return b < 0x60 || b is 0x70 or 0x71;
    }

    public bool TryMapPage(uint bank, uint offset, out byte[]? data, out int index)
    {
        data = null; index = 0;
        if (lockedOut) return false;
        bool romPage = (bank & 0x40) == 0 ? offset >= 0x8000 : (bank & 0x7F) < 0x60;
        if (!romPage) return false;
        data = rom;
        index = SnesRomIndex(bank, offset);
        return true;
    }

    // While the GSU owns the ROM bus, the SNES reads this instead: the vectors all point at $01xx
    // in WRAM, where games keep their interrupt handlers.
    private static readonly byte[] LockedRom = { 0x00, 0x01, 0x00, 0x01, 0x04, 0x01, 0x00, 0x01, 0x00, 0x01, 0x08, 0x01, 0x00, 0x01, 0x0C, 0x01 };

    public byte Read(uint bank, uint offset, long masterClock)
    {
        RunTo(masterClock);
        bool romArea = (bank & 0x40) == 0 ? offset >= 0x8000 : (bank & 0x7F) < 0x60;
        if (romArea)
        {
            if (Running && (scmr & 0x10) != 0) return LockedRom[offset & 15];
            return rom[SnesRomIndex(bank, offset)];
        }
        if ((bank & 0x40) == 0 && offset < 0x3500) return ReadIo(offset);
        if (Running && (scmr & 0x08) != 0) return 0;   // GSU owns the RAM bus
        return ram[SnesRamIndex(bank, offset)];
    }

    public void Write(uint bank, uint offset, byte value, long masterClock)
    {
        RunTo(masterClock);
        bool romArea = (bank & 0x40) == 0 ? offset >= 0x8000 : (bank & 0x7F) < 0x60;
        if (romArea) return;
        if ((bank & 0x40) == 0 && offset < 0x3500) { WriteIo(offset, value); return; }
        if (Running && (scmr & 0x08) != 0) return;
        ram[SnesRamIndex(bank, offset)] = value;
    }

    private int SnesRomIndex(uint bank, uint offset)
    {
        int i = (bank & 0x40) == 0
            ? (int)((bank & 0x3F) << 15 | (offset & 0x7FFF))     // LoROM view
            : (int)((bank & 0x1F) << 16 | offset);               // $40-5F: linear view of the same ROM
        return i < rom.Length ? i : i % rom.Length;
    }

    private int SnesRamIndex(uint bank, uint offset) =>
        (bank & 0x40) == 0 ? (int)(offset & 0x1FFF) & ramMask : (int)((bank & 1) << 16 | offset) & ramMask;

    private byte ReadIo(uint offset)
    {
        uint a = 0x3000 | (offset & 0x3FF);
        if (a >= 0x3100 && a < 0x3300) return cache[(a - 0x3100 + cbr) & 511];
        if (a < 0x3020) { ushort v = r[(a >> 1) & 15]; return (byte)((a & 1) != 0 ? v >> 8 : v); }
        switch (a)
        {
            case 0x3030: return (byte)sfr;
            case 0x3031:
            {
                byte v = (byte)(sfr >> 8);
                sfr &= unchecked((ushort)~FIRQ);
                setSnesIrq?.Invoke(false);
                return v;
            }
            case 0x3034: return pbr;
            case 0x3036: return rombr;
            case 0x303B: return version;
            case 0x303C: return rambr;
            case 0x303E: return (byte)cbr;
            case 0x303F: return (byte)(cbr >> 8);
        }
        return 0;
    }

    private void WriteIo(uint offset, byte v)
    {
        uint a = 0x3000 | (offset & 0x3FF);
        if (a >= 0x3100 && a < 0x3300)
        {
            uint i = (a - 0x3100 + cbr) & 511;
            cache[i] = v;
            if ((i & 15) == 15) cacheValid[i >> 4] = true;
            return;
        }
        if (a < 0x3020)
        {
            int n = (int)(a >> 1) & 15;
            r[n] = (a & 1) == 0 ? (ushort)((r[n] & 0xFF00) | v) : (ushort)((r[n] & 0x00FF) | v << 8);
            if (n == 14) LoadRomBuffer();
            if (a == 0x301F) { sfr |= FG; UpdateLockout(); }   // writing R15's high byte starts the GSU
            return;
        }
        switch (a)
        {
            case 0x3030:
            {
                bool wasRunning = Running;
                sfr = (ushort)((sfr & 0xFF00) | v);
                if (wasRunning && !Running) { cbr = 0; FlushCache(); }
                UpdateLockout();
                break;
            }
            case 0x3031: sfr = (ushort)((sfr & 0x00FF) | v << 8); break;
            case 0x3033: bramr = (byte)(v & 1); break;
            case 0x3034: pbr = (byte)(v & 0x7F); FlushCache(); break;
            case 0x3037: cfgr = v; break;
            case 0x3038: scbr = v; break;
            case 0x3039: clsr = (byte)(v & 1); break;
            case 0x303A: scmr = v; UpdateLockout(); break;
        }
    }

    // =====================================================================================
    //  GSU bus
    // =====================================================================================

    private byte GsuRead(uint address)
    {
        uint bank = (address >> 16) & 0x7F;
        if (bank < 0x40) { int i = (int)((bank & 0x3F) << 15 | (address & 0x7FFF)); return rom[i < rom.Length ? i : i % rom.Length]; }
        if (bank < 0x60) { int i = (int)((bank & 0x1F) << 16 | (address & 0xFFFF)); return rom[i < rom.Length ? i : i % rom.Length]; }
        return ram[(int)(address & 0x1FFFF) & ramMask];
    }

    // ---- Memory timing: the ROM and RAM buffers work in the background ----
    // Writing R14 starts a ROM fetch that completes MemCycle clocks later; GETB & co. wait only if
    // it is still in flight. A RAM write goes into a one-entry buffer and completes MemCycle clocks
    // later; the next RAM access waits for it. RAM reads themselves are immediate. (The first
    // version charged every access up front: Yoshi's Island's intro ran ~40% slow vs Mesen 2.)
    private int romPending, ramPending;       // clocks until the buffered ROM fetch / RAM write lands
    private ushort ramPendingAddr;
    private byte ramPendingData;

    private void Tick(int clocks)
    {
        clock += clocks;
        if (romPending > 0 && (romPending -= clocks) <= 0)
        {
            romPending = 0;
            romBuffer = GsuRead((uint)rombr << 16 | r[14]);   // the address at completion, as on hardware
            sfr &= unchecked((ushort)~FR);
        }
        if (ramPending > 0 && (ramPending -= clocks) <= 0)
        {
            ramPending = 0;
            ram[RamIndex(ramPendingAddr)] = ramPendingData;
        }
    }

    private void SyncRom() { if (romPending > 0) Tick(romPending); }
    private void SyncRam() { if (ramPending > 0) Tick(ramPending); }

    private int RamIndex(ushort addr) => (rambr << 16 | addr) & ramMask;
    private byte RamRead(ushort addr) { SyncRam(); return ram[RamIndex(addr)]; }
    private void RamWrite(ushort addr, byte v) { SyncRam(); ramPendingAddr = addr; ramPendingData = v; ramPending = MemCycle; }
    private ushort RamReadWord(ushort addr) => (ushort)(RamRead(addr) | RamRead((ushort)(addr ^ 1)) << 8);
    private void RamWriteWord(ushort addr, int v) { RamWrite(addr, (byte)v); RamWrite((ushort)(addr ^ 1), (byte)(v >> 8)); }

    /// <summary>R14 was written by the GSU: start a background ROM fetch.</summary>
    private void StartRomBuffer() { romPending = MemCycle; sfr |= FR; }

    /// <summary>R14 was written by the SNES (the GSU is stopped): fetch at once.</summary>
    private void LoadRomBuffer() { romPending = 0; romBuffer = GsuRead((uint)rombr << 16 | r[14]); }

    private byte RomBuffer() { SyncRom(); return romBuffer; }

    private void FlushCache() => Array.Clear(cacheValid);

    private byte FetchOpcode(ushort addr)
    {
        int offset = (ushort)(addr - cbr);
        if (offset < 512)
        {
            int line = offset >> 4;
            if (!cacheValid[line])
            {
                int dp = offset & 0x1F0;
                uint sp = (uint)pbr << 16 | (uint)((cbr + dp) & 0xFFF0);
                for (int n = 0; n < 16; n++) { Tick(MemCycle); cache[dp + n] = GsuRead(sp + (uint)n); }
                cacheValid[line] = true;
            }
            else Tick(Cycle);
            return cache[offset];
        }
        // Outside the cache the fetch shares the bus with the buffers.
        if (pbr < 0x60) SyncRom(); else SyncRam();
        Tick(MemCycle);
        return GsuRead((uint)pbr << 16 | addr);
    }

    // =====================================================================================
    //  Core
    // =====================================================================================

    private void W(int n, int value)
    {
        r[n] = (ushort)value;
        if (n == 14) StartRomBuffer();
        else if (n == 15) r15Modified = true;
    }

    private byte Pipe()
    {
        byte v = pipeline;
        r[15]++;
        pipeline = FetchOpcode(r[15]);
        r15Modified = false;
        return v;
    }

    private void ResetPrefix() { sfr &= unchecked((ushort)~(FB | FALT1 | FALT2)); sreg = dreg = 0; }

    private void SetFlag(ushort f, bool on) { if (on) sfr |= f; else sfr &= (ushort)~f; }
    private bool Flag(ushort f) => (sfr & f) != 0;
    private void SZ(int v) { SetFlag(FS, (v & 0x8000) != 0); SetFlag(FZ, (v & 0xFFFF) == 0); }

    /// <summary>Debug: ring of recent instructions, (pbr:pc of the opcode) &lt;&lt; 8 | opcode; null = off.</summary>
    public long[]? Trace;
    private int tracePos;
    public int TracePos => tracePos;

    /// <summary>Debug: log the registers every time the opcode at this pbr:pc (24-bit) is about to run.</summary>
    public System.Collections.Generic.HashSet<int>? WatchPcs;
    public long WatchFrom;
    public int WatchRamFrom, WatchRamLength;
    /// <summary>Debug: start logging only after the opcode at WatchArmPc has run WatchArmCount times.</summary>
    public int WatchArmPc = -1, WatchArmCount = 1, WatchMax = 600;
    private int watchArmHits;
    private bool watchArmed;
    public readonly System.Collections.Generic.List<string> WatchLog = new();

    private void Step()
    {
        byte op = pipeline;
        if (Trace != null) Trace[tracePos++ % Trace.Length] = ((long)pbr << 16 | (ushort)(r[15] - 1)) << 8 | op | (long)sfr << 32;
        if (WatchArmPc >= 0 && !watchArmed && (pbr << 16 | (ushort)(r[15] - 1)) == WatchArmPc && ++watchArmHits >= WatchArmCount) watchArmed = true;
        if (WatchPcs != null && Instructions >= WatchFrom && (WatchArmPc < 0 || watchArmed) && WatchLog.Count < WatchMax
            && (WatchPcs.Contains(-1) || WatchPcs.Contains(pbr << 16 | (ushort)(r[15] - 1))))
        {
            string ramText = "";
            if (WatchRamLength > 0)
                ramText = " ram " + string.Join(" ", System.Linq.Enumerable.Range(WatchRamFrom, WatchRamLength).Select(a => ram[a & ramMask].ToString("X2")));
            WatchLog.Add($"{pbr:X2}:{(ushort)(r[15] - 1):X4} #{Instructions} op={op:X2} clk={clock} cbr={cbr:X4} {DescribeRegs()} sfr=${sfr:X4}{ramText}");
        }
        pipeline = FetchOpcode(r[15]);
        r15Modified = false;
        Execute(op);
        if (!r15Modified) r[15]++;
        Instructions++;
    }

    private void Branch(bool take)
    {
        int e = (sbyte)Pipe();
        if (take) W(15, r[15] + e);
        // Branches leave the prefix state alone: Star Fox puts WITH R14 before a BRA so that the
        // ADD in the delay slot advances its display-list pointer.
    }

    private void Execute(byte op)
    {
        int alt = (sfr >> 8) & 3;     // 0 none, 1 ALT1, 2 ALT2, 3 ALT3
        int n = op & 15;
        int s = r[sreg];
        switch (op >> 4)
        {
            case 0x0:
                switch (n)
                {
                    case 0x0:   // STOP
                        SyncRam(); SyncRom();   // let buffered accesses land before the SNES looks
                        Stops++;
                        if ((cfgr & 0x80) == 0) { sfr |= FIRQ; setSnesIrq?.Invoke(true); }
                        sfr &= unchecked((ushort)~FG);
                        pipeline = 0x01;
                        ResetPrefix();
                        UpdateLockout();
                        return;
                    case 0x1: ResetPrefix(); return;                                        // NOP
                    case 0x2:                                                                // CACHE
                        if (cbr != (r[15] & 0xFFF0)) { cbr = (ushort)(r[15] & 0xFFF0); FlushCache(); }
                        ResetPrefix();
                        return;
                    case 0x3: SetFlag(FCY, (s & 1) != 0); W(dreg, s >> 1); SZ(s >> 1); ResetPrefix(); return;   // LSR
                    case 0x4:                                                                                  // ROL
                    {
                        int v = (s << 1) | (Flag(FCY) ? 1 : 0);
                        SetFlag(FCY, (s & 0x8000) != 0); W(dreg, v); SZ(v); ResetPrefix(); return;
                    }
                    case 0x5: Branch(true); return;
                    case 0x6: Branch(Flag(FS) == Flag(FOV)); return;     // BGE
                    case 0x7: Branch(Flag(FS) != Flag(FOV)); return;     // BLT
                    case 0x8: Branch(!Flag(FZ)); return;
                    case 0x9: Branch(Flag(FZ)); return;
                    case 0xA: Branch(!Flag(FS)); return;
                    case 0xB: Branch(Flag(FS)); return;
                    case 0xC: Branch(!Flag(FCY)); return;
                    case 0xD: Branch(Flag(FCY)); return;
                    case 0xE: Branch(!Flag(FOV)); return;
                    default: Branch(Flag(FOV)); return;
                }
            case 0x1:   // TO Rn / MOVE
                if (Flag(FB)) { W(n, s); ResetPrefix(); }
                else dreg = n;
                return;
            case 0x2:   // WITH Rn
                sreg = dreg = n;
                sfr |= FB;
                return;
            case 0x3:
                if (n < 12)
                {
                    ramAddr = r[n];
                    if (alt == 1) RamWrite(ramAddr, (byte)s);                // STB
                    else RamWriteWord(ramAddr, s);                           // STW
                    ResetPrefix();
                    return;
                }
                switch (n)
                {
                    case 12:   // LOOP
                    {
                        int v = (r[12] - 1) & 0xFFFF;
                        r[12] = (ushort)v; SZ(v);
                        if (v != 0) W(15, r[13]);
                        ResetPrefix();
                        return;
                    }
                    case 13: sfr = (ushort)((sfr & ~(FB | FALT2)) | FALT1); return;             // ALT1
                    case 14: sfr = (ushort)((sfr & ~(FB | FALT1)) | FALT2); return;             // ALT2
                    default: sfr = (ushort)((sfr & ~FB) | FALT1 | FALT2); return;              // ALT3
                }
            case 0x4:
                if (n < 12)
                {
                    ramAddr = r[n];
                    int v = alt == 1 ? RamRead(ramAddr) : RamReadWord(ramAddr);   // LDB / LDW
                    W(dreg, v);
                    ResetPrefix();
                    return;
                }
                switch (n)
                {
                    case 12:
                        if (alt == 1) { int v = Rpix((byte)r[1], (byte)r[2]); W(dreg, v); SZ(v); }   // RPIX
                        else { Plot((byte)r[1], (byte)r[2]); W(1, r[1] + 1); }                       // PLOT
                        ResetPrefix();
                        return;
                    case 13: { int v = (s >> 8) | (s << 8); W(dreg, v); SZ(v); ResetPrefix(); return; }  // SWAP
                    case 14:
                        if (alt == 1) por = (byte)s;          // CMODE
                        else colr = Color((byte)s);           // COLOR
                        ResetPrefix();
                        return;
                    default: { int v = ~s; W(dreg, v); SZ(v); ResetPrefix(); return; }                  // NOT
                }
            case 0x5:   // ADD / ADC / ADD # / ADC #
            {
                int b = alt >= 2 ? n : r[n];
                int v = s + b + ((alt & 1) != 0 && Flag(FCY) ? 1 : 0);
                SetFlag(FOV, (~(s ^ b) & (b ^ v) & 0x8000) != 0);
                SetFlag(FCY, v >= 0x10000);
                W(dreg, v); SZ(v);
                ResetPrefix();
                return;
            }
            case 0x6:   // SUB / SBC / SUB # / CMP
            {
                int b = alt == 2 ? n : r[n];
                int v = s - b - (alt == 1 && !Flag(FCY) ? 1 : 0);
                SetFlag(FOV, ((s ^ b) & (s ^ v) & 0x8000) != 0);
                SetFlag(FCY, v >= 0);
                if (alt != 3) W(dreg, v);
                SZ(v);
                ResetPrefix();
                return;
            }
            case 0x7:
                if (n == 0)   // MERGE
                {
                    int v = (r[7] & 0xFF00) | (r[8] >> 8);
                    W(dreg, v);
                    SetFlag(FOV, (v & 0xC0C0) != 0);
                    SetFlag(FS, (v & 0x8080) != 0);
                    SetFlag(FCY, (v & 0xE0E0) != 0);
                    SetFlag(FZ, (v & 0xF0F0) != 0);
                }
                else          // AND / BIC / AND # / BIC #
                {
                    int b = alt >= 2 ? n : r[n];
                    int v = (alt & 1) != 0 ? s & ~b : s & b;
                    W(dreg, v); SZ(v);
                }
                ResetPrefix();
                return;
            case 0x8:   // MULT / UMULT / MULT # / UMULT #
            {
                int b = alt >= 2 ? n : r[n];
                int v = (alt & 1) != 0 ? (byte)s * (byte)b : (sbyte)s * (sbyte)b;
                W(dreg, v); SZ(v);
                if ((cfgr & 0x20) == 0) Tick(Cycle);
                ResetPrefix();
                return;
            }
            case 0x9:
                switch (n)
                {
                    case 0x0: RamWriteWord(ramAddr, s); break;                                   // SBK
                    case 0x1: case 0x2: case 0x3: case 0x4: W(11, r[15] + n); break;              // LINK #n
                    case 0x5: { int v = (sbyte)s; W(dreg, v); SZ(v); break; }                   // SEX
                    case 0x6:                                                                    // ASR / DIV2
                    {
                        SetFlag(FCY, (s & 1) != 0);
                        int v = (short)s >> 1;
                        if (alt == 1 && s == 0xFFFF) v = 0;
                        W(dreg, v); SZ(v);
                        break;
                    }
                    case 0x7:                                                                    // ROR
                    {
                        int v = (Flag(FCY) ? 0x8000 : 0) | (s >> 1);
                        SetFlag(FCY, (s & 1) != 0); W(dreg, v); SZ(v);
                        break;
                    }
                    case 0xE: { int v = s & 0xFF; W(dreg, v); SetFlag(FS, (v & 0x80) != 0); SetFlag(FZ, v == 0); break; }   // LOB
                    case 0xF:                                                                    // FMULT / LMULT
                    {
                        int prod = (short)s * (short)r[6];
                        int v = (prod >> 16) & 0xFFFF;
                        W(dreg, v);
                        if (alt == 1) W(4, prod);   // LMULT: low word to R4 (after the high word, as on hardware)
                        SetFlag(FCY, (prod & 0x8000) != 0);
                        SZ(v);
                        Tick(((cfgr & 0x20) != 0 ? 3 : 7) * Cycle);
                        break;
                    }
                    default:                                                                     // JMP / LJMP R8-R13
                        if (alt == 1)
                        {
                            pbr = (byte)(r[n] & 0x7F);
                            W(15, s);
                            cbr = (ushort)(r[15] & 0xFFF0);
                            FlushCache();
                        }
                        else W(15, r[n]);
                        break;
                }
                ResetPrefix();
                return;
            case 0xA:   // IBT / LMS / SMS
                if (alt == 1) { ramAddr = (ushort)(Pipe() << 1); W(n, RamReadWord(ramAddr)); }
                else if (alt == 2) { ramAddr = (ushort)(Pipe() << 1); RamWriteWord(ramAddr, r[n]); }
                else W(n, (sbyte)Pipe());
                ResetPrefix();
                return;
            case 0xB:   // FROM Rn / MOVES
                if (Flag(FB))
                {
                    int v = r[n];
                    W(dreg, v);
                    SetFlag(FOV, (v & 0x80) != 0); SZ(v);
                    ResetPrefix();
                }
                else sreg = n;
                return;
            case 0xC:
                if (n == 0) { int v = s >> 8; W(dreg, v); SetFlag(FS, (v & 0x80) != 0); SetFlag(FZ, v == 0); }   // HIB
                else
                {
                    int b = alt >= 2 ? n : r[n];
                    int v = (alt & 1) != 0 ? s ^ b : s | b;       // OR / XOR / OR # / XOR #
                    W(dreg, v); SZ(v);
                }
                ResetPrefix();
                return;
            case 0xD:
                if (n < 15) { int v = r[n] + 1; W(n, v); SZ(v); }                 // INC
                else if (alt == 2) { SyncRam(); rambr = (byte)(s & 1); }                           // RAMB
                else if (alt == 3) { SyncRom(); rombr = (byte)(s & 0x7F); }                        // ROMB
                else colr = Color(RomBuffer());                                       // GETC
                ResetPrefix();
                return;
            case 0xE:
                if (n < 15) { int v = r[n] - 1; W(n, v); SZ(v); }                 // DEC
                else
                {
                    int v = alt switch
                    {
                        1 => RomBuffer() << 8 | (s & 0xFF),      // GETBH
                        2 => (s & 0xFF00) | RomBuffer(),         // GETBL
                        3 => (sbyte)RomBuffer(),                 // GETBS
                        _ => RomBuffer(),                      // GETB
                    };
                    W(dreg, v);
                }
                ResetPrefix();
                return;
            default:    // IWT / LM / SM
            {
                int lo = Pipe(), hi = Pipe();
                int word = lo | hi << 8;
                if (alt == 1) { ramAddr = (ushort)word; W(n, RamReadWord(ramAddr)); }
                else if (alt == 2) { ramAddr = (ushort)word; RamWriteWord(ramAddr, r[n]); }
                else W(n, word);
                ResetPrefix();
                return;
            }
        }
    }

    // =====================================================================================
    //  Pixel unit
    // =====================================================================================

    private byte Color(byte source)
    {
        if ((por & 0x04) != 0) return (byte)((colr & 0xF0) | (source >> 4));     // high nibble
        if ((por & 0x08) != 0) return (byte)((colr & 0xF0) | (source & 0x0F));   // freeze high
        return source;
    }

    private int Mode => scmr & 3;
    private int Bpp => Mode == 0 ? 2 : Mode == 3 ? 8 : 4;

    private void Plot(byte x, byte y)
    {
        int color = colr;
        if ((por & 0x02) != 0 && Mode != 3)          // dither: alternate nibbles in a checkerboard
        {
            if (((x ^ y) & 1) != 0) color >>= 4;
            color &= 0x0F;
        }
        if ((por & 0x01) == 0)                        // transparency on
        {
            if (Mode == 3 ? ((por & 0x08) != 0 ? (color & 0x0F) == 0 : color == 0) : (color & 0x0F) == 0) return;
        }

        ushort offset = (ushort)((y << 5) + (x >> 3));
        if (offset != pc0.Offset)
        {
            FlushPixels(ref pc1);
            pc1 = pc0;
            pc0.BitPend = 0;
            pc0.Offset = offset;
        }
        int slot = (x & 7) ^ 7;
        SetPixel(ref pc0, slot, (byte)color);
        pc0.BitPend |= (byte)(1 << slot);
        if (pc0.BitPend == 0xFF)
        {
            FlushPixels(ref pc1);
            pc1 = pc0;
            pc0.BitPend = 0;
        }
    }

    private static void SetPixel(ref PixelCache c, int slot, byte v)
    {
        switch (slot)
        {
            case 0: c.D0 = v; break; case 1: c.D1 = v; break; case 2: c.D2 = v; break; case 3: c.D3 = v; break;
            case 4: c.D4 = v; break; case 5: c.D5 = v; break; case 6: c.D6 = v; break; default: c.D7 = v; break;
        }
    }

    private static byte GetPixel(in PixelCache c, int slot) => slot switch
    {
        0 => c.D0, 1 => c.D1, 2 => c.D2, 3 => c.D3, 4 => c.D4, 5 => c.D5, 6 => c.D6, _ => c.D7,
    };

    /// <summary>RAM address of the tile row holding pixel (x, y) in the current screen layout.</summary>
    private int TileRowAddress(int x, int y)
    {
        int ht = (por & 0x10) != 0 ? 3 : ((scmr >> 2) & 1) | ((scmr >> 4) & 2);
        int cn = ht switch
        {
            0 => ((x & 0xF8) << 1) + ((y & 0xF8) >> 3),                                  // 128 lines
            1 => ((x & 0xF8) << 1) + ((x & 0xF8) >> 1) + ((y & 0xF8) >> 3),              // 160 lines
            2 => ((x & 0xF8) << 1) + (x & 0xF8) + ((y & 0xF8) >> 3),                     // 192 lines
            _ => ((y & 0x80) << 2) + ((x & 0x80) << 1) + ((y & 0x78) << 1) + ((x & 0x78) >> 3),   // OBJ layout
        };
        return cn * (Bpp << 3) + (scbr << 10) + (y & 7) * 2;
    }

    private void FlushPixels(ref PixelCache c)
    {
        if (c.BitPend == 0) return;
        int x = (c.Offset << 3) & 0xFF, y = (c.Offset >> 5) & 0xFF;
        int addr = TileRowAddress(x, y);
        int bpp = Bpp;
        for (int plane = 0; plane < bpp; plane++)
        {
            int byteOff = ((plane >> 1) << 4) + (plane & 1);
            int data = 0;
            for (int slot = 0; slot < 8; slot++) data |= ((GetPixel(c, slot) >> plane) & 1) << slot;
            int i = (addr + byteOff) & ramMask;
            if (c.BitPend != 0xFF)
            {
                Tick(MemCycle);
                data = (data & c.BitPend) | (ram[i] & ~c.BitPend);
            }
            Tick(MemCycle);
            ram[i] = (byte)data;
        }
        c.BitPend = 0;
    }

    private int Rpix(byte x, byte y)
    {
        FlushPixels(ref pc1);
        FlushPixels(ref pc0);
        int addr = TileRowAddress(x, y);
        int slot = (x & 7) ^ 7;
        int v = 0;
        for (int plane = 0; plane < Bpp; plane++)
        {
            int byteOff = ((plane >> 1) << 4) + (plane & 1);
            Tick(MemCycle);
            v |= ((ram[(addr + byteOff) & ramMask] >> slot) & 1) << plane;
        }
        return v;
    }

    public string DescribeRegs()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 16; i++) sb.Append($"r{i}=${r[i]:X4} ");
        sb.Append($"rombr=${rombr:X2} rambr=${rambr:X2} colr=${colr:X2} romBuf=${romBuffer:X2} ramAddr=${ramAddr:X4}");
        return sb.ToString();
    }

    public string Describe() =>
        $"{Name} {(Running ? "RUN" : "stop")} pbr=${pbr:X2} r15=${r[15]:X4} r14=${r[14]:X4} sfr=${sfr:X4} scmr=${scmr:X2} por=${por:X2} scbr=${scbr:X2} " +
        $"cfgr=${cfgr:X2} clsr={clsr} cbr=${cbr:X4} instr={Instructions:N0} stops={Stops} ram={ram.Length / 1024}KB";

    // =====================================================================================
    //  Disassembler (debug tooling)
    // =====================================================================================

    private static readonly string[] BranchNames = { "", "", "", "", "", "BRA", "BGE", "BLT", "BNE", "BEQ", "BPL", "BMI", "BCC", "BCS", "BVC", "BVS" };

    /// <summary>Disassembles straight-line code; prefixes are listed as their own lines.</summary>
    public string Disassemble(uint address, int count)
    {
        var sb = new System.Text.StringBuilder();
        int alt = 0;
        for (int k = 0; k < count; k++)
        {
            byte op = GsuRead(address);
            byte b1 = GsuRead(address + 1), b2 = GsuRead(address + 2);
            int n = op & 15, len = 1;
            string text;
            switch (op >> 4)
            {
                case 0x0:
                    if (n >= 5) { len = 2; text = $"{BranchNames[n]} {(address + 2 + (sbyte)b1) & 0xFFFF:X4}"; }
                    else text = new[] { "STOP", "NOP", "CACHE", "LSR", "ROL" }[n];
                    break;
                case 0x1: text = $"TO R{n}"; break;
                case 0x2: text = $"WITH R{n}"; break;
                case 0x3: text = n < 12 ? $"{(alt == 1 ? "STB" : "STW")} (R{n})" : new[] { "LOOP", "ALT1", "ALT2", "ALT3" }[n - 12]; break;
                case 0x4:
                    text = n < 12 ? $"{(alt == 1 ? "LDB" : "LDW")} (R{n})"
                        : n == 12 ? (alt == 1 ? "RPIX" : "PLOT") : n == 13 ? "SWAP" : n == 14 ? (alt == 1 ? "CMODE" : "COLOR") : "NOT";
                    break;
                case 0x5: text = new[] { "ADD R", "ADC R", "ADD #", "ADC #" }[alt] + n; break;
                case 0x6: text = new[] { "SUB R", "SBC R", "SUB #", "CMP R" }[alt] + n; break;
                case 0x7: text = n == 0 ? "MERGE" : new[] { "AND R", "BIC R", "AND #", "BIC #" }[alt] + n; break;
                case 0x8: text = new[] { "MULT R", "UMULT R", "MULT #", "UMULT #" }[alt] + n; break;
                case 0x9:
                    text = n switch
                    {
                        0 => "SBK",
                        >= 1 and <= 4 => $"LINK #{n}",
                        5 => "SEX",
                        6 => alt == 1 ? "DIV2" : "ASR",
                        7 => "ROR",
                        14 => "LOB",
                        15 => alt == 1 ? "LMULT" : "FMULT",
                        _ => $"{(alt == 1 ? "LJMP" : "JMP")} R{n}",
                    };
                    break;
                case 0xA:
                    len = 2;
                    text = alt switch { 1 => $"LMS R{n},({b1 << 1:X3})", 2 => $"SMS ({b1 << 1:X3}),R{n}", _ => $"IBT R{n},#{(sbyte)b1}" };
                    break;
                case 0xB: text = $"FROM R{n}"; break;
                case 0xC: text = n == 0 ? "HIB" : new[] { "OR R", "XOR R", "OR #", "XOR #" }[alt] + n; break;
                case 0xD: text = n < 15 ? $"INC R{n}" : alt switch { 2 => "RAMB", 3 => "ROMB", _ => "GETC" }; break;
                case 0xE: text = n < 15 ? $"DEC R{n}" : new[] { "GETB", "GETBH", "GETBL", "GETBS" }[alt]; break;
                default:
                    len = 3;
                    int w = b1 | b2 << 8;
                    text = alt switch { 1 => $"LM R{n},({w:X4})", 2 => $"SM ({w:X4}),R{n}", _ => $"IWT R{n},#{w:X4}" };
                    break;
            }
            sb.AppendLine($"  {address >> 16:X2}:{address & 0xFFFF:X4}  {text}");
            alt = op switch { 0x3D => 1, 0x3E => 2, 0x3F => 3, _ => (op >> 4) is 1 or 2 or 0xB ? alt : 0 };
            address = (address & 0xFF0000) | ((address + (uint)len) & 0xFFFF);
        }
        return sb.ToString();
    }
}
