using System;

namespace NesEmulator
{
    /// <summary>
    /// A CPU whose <see cref="ICPU.ExecuteInstruction"/> runs a whole batch (a scanline or part of one) and returns its summed cycles. NES.RunFrame
    /// turns its precise and per-instruction paths off for it and flushes the batch in one lump, so the PPU and APU only catch up between batches.
    /// Without this the precise window would step the PPU on every bus access inside the batch and erase the batching.
    /// </summary>
    public interface IBatchedCpu { }

    /// <summary>
    /// CPU harvest (docs/projects/cpu-harvest-catalog.md): the scanline-batch timing layer. Every preset runs CPU_FIX's own opcodes on another emulator's scheduler:
    /// the CPU runs a line's budget of cycles while the PPU stands still, then the PPU and APU catch up in one go, so <c>$2002</c>, sprite-0 hits and MMC3 IRQs are seen at line
    /// granularity. That sync model, not the opcodes, is where those emulators' visible jank came from. Nothing falls back to FIX when a game breaks.
    /// <para>The settings are the presets' fields (see the subclasses). Rebuilt from the donors' sources (read, not copied): InfoNES K6502_Step / InfoNES_Cycle,
    /// nofrendo nes.c nes_renderframe / nes_setcompathacks, PocketNES 6502.s check_irq and its branch/JMP speedhack hooks.</para>
    /// </summary>
    public abstract class BatchCpuBase : ICPU, IBatchedCpu
    {
        public enum IrqPoint
        {
            /// <summary>CPU_FIX's own poll, every instruction (inside the batch).</summary>
            EveryInstruction,
            /// <summary>Only at the start of a batch (InfoNES: K6502_Step checks the IRQ wire once on entry).</summary>
            BatchStart,
            /// <summary>At the start of a batch and right after CLI, PLP or RTI (PocketNES: check_irq is reached from those three).</summary>
            BatchStartAndUnmask,
        }

        protected readonly Bus bus;
        private readonly CPU_FIX inner;
        private readonly string id;

        // --- settings (fixed per preset) ---
        /// <summary>CPU cycles a line's batch runs (InfoNES 113, nofrendo 1789773/60/262, hardware 341/3).</summary>
        protected double CyclesPerLine = 341.0 / 3.0;
        /// <summary>Restart the line count at the vblank NMI, as the donors' frame loops do (their lines are counted from their own frame start).</summary>
        protected bool LockLinesAtNmi = true;
        /// <summary>Cycles run between the vblank flag and the NMI being taken (nofrendo: 7).</summary>
        protected int NmiDelay;
        /// <summary>InfoNES: on the line where sprite 0 starts, split the batch at X*budget/256 so the hit is seen there (needs a PPU that shows its OAM).</summary>
        protected bool Sprite0Split;
        /// <summary>nofrendo: +2.5 cycles a line for four games (Zen Intergalactic, SMB3, Kirby's Adventure, Power Blade 2), by PRG+CHR CRC32.</summary>
        protected bool PerGameLineHack;
        /// <summary>PocketNES-style idle skip: a short backward branch or JMP that comes round with the same registers burns the rest of the batch.</summary>
        protected bool IdleLoopSkip;
        protected IrqPoint IrqCheck = IrqPoint.EveryInstruction;
        /// <summary>FCEUX e0028f6 (2009-06-24, reverted the next day): no batching, but page-cross and interrupt cycles are counted twice against the PPU.</summary>
        protected bool DoubleCountPenalties;

        // --- runtime ---
        private double lineLeft;          // cycles still to run in the current line (negative = overshoot carried)
        private double lineBudget;        // CyclesPerLine plus the per-game hack, fixed once the cartridge is known
        private int splitLeft = -1;       // sprite-0 split: cycles to the split point in this line, -1 none
        private int nmiDelayLeft = -1;    // cycles before the held NMI is handed in, -1 none
        private bool irqLatch;            // the IRQ wire as the devices left it (for the batch-start check points)
        private int idlePc = -1; private int idleRegs = -1;
        private bool hackChecked;

        public long Batches { get; private set; }
        public long IdleSkips { get; private set; }
        /// <summary>The 6502 inside (tools read its registers).</summary>
        public CPU_FIX Inner => inner;

        protected BatchCpuBase(Bus bus, string id)
        {
            this.bus = bus; this.id = id;
            inner = new CPU_FIX(bus);
            lineBudget = CyclesPerLine;
        }

        public abstract string CoreName { get; }
        public abstract string Description { get; }
        public int Performance => 0;
        public int Rating => 1;
        public string Category => "Harvest";
        public bool IgnoreInvalidOpcodes { get => inner.IgnoreInvalidOpcodes; set => inner.IgnoreInvalidOpcodes = value; }

        public void Reset()
        {
            inner.Reset();
            lineLeft = 0; splitLeft = -1; nmiDelayLeft = -1; irqLatch = false; idlePc = -1; hackChecked = false;
        }

        private void CheckHack()
        {
            hackChecked = true;
            lineBudget = CyclesPerLine;
            if (!PerGameLineHack) return;
            var rom = bus.cartridge?.rom;
            if (rom == null || rom.Length <= 16) return;
            int start = 16 + ((rom[6] & 0x04) != 0 ? 512 : 0);   // rom.c: CRC32 from the PRG start to the end of the file
            if (start >= rom.Length) return;
            uint crc = Crc32(rom, start, rom.Length - start);
            if (crc is 0xD8578BFD or 0x2E6301ED or 0x5ED6F221 or 0xD273B409) lineBudget += 2.5;
        }
        public bool LineHackActive => hackChecked && lineBudget != CyclesPerLine;

        private static uint Crc32(byte[] d, int off, int len)
        {
            uint c = 0xFFFFFFFF;
            for (int i = off; i < off + len; i++) { c ^= d[i]; for (int k = 0; k < 8; k++) c = (c >> 1) ^ (0xEDB88320u & (uint)-(int)(c & 1)); }
            return ~c;
        }

        // A side-effect-free look at the next opcode: only RAM and cartridge space (never a register page).
        private int PeekOpcode(ushort pc) => pc < 0x2000 || pc >= 0x8000 ? bus.Read(pc) : -1;

        public int ExecuteInstruction()
        {
            if (!hackChecked) CheckHack();
            bus.InstructionStartDot = -1;   // the inner CPU_FIX must not judge interrupts by dots inside a batch
            Batches++;
            return DoubleCountPenalties ? RunOneCounted() : RunBatch();
        }

        private int RunOneCounted()
        {
            bool nmiBefore = inner.NmiPending, irqBefore = inner.IrqPending && (inner.GetRegisters().P & 0x04) == 0;
            int op = PeekOpcode(inner.GetRegisters().PC);
            int c = inner.ExecuteInstruction();
            bool took = (nmiBefore && !inner.NmiPending) || (irqBefore && !inner.IrqPending);
            if (took) return c * 2;
            if (op < 0) return c;
            int extra = c - BaseCycles[op];
            if ((op & 0x1F) == 0x10) extra = c == 4 ? 1 : 0;   // a branch: only the page cross counts twice, not the taken cycle
            return extra > 0 ? c + extra : c;
        }

        private int RunBatch()
        {
            if (lineLeft <= 0)
            {
                lineLeft += lineBudget;
                splitLeft = -1;
                if (Sprite0Split && bus.ppu is IPpuViewProbe vp && bus.ppu is IPpuFrameClock clk)
                {
                    int sl = clk.ProbeScanline, y = vp.ProbeOam(0) + 1;
                    if (sl == y && sl < 240) splitLeft = vp.ProbeOam(3) * (int)lineBudget / 256;   // InfoNES: SPRRAM[SPR_X] * STEP_PER_SCANLINE / NES_DISP_WIDTH
                }
            }
            double target = lineLeft;
            if (splitLeft > 0 && splitLeft < target) target = splitLeft;
            if (nmiDelayLeft > 0 && nmiDelayLeft < target) target = nmiDelayLeft;

            bool gate = IrqCheck != IrqPoint.EveryInstruction;
            int open = 0;   // instructions the IRQ wire stays visible to the inner CPU (CLI/PLP delay the poll by one, so a reopen lasts two)
            if (gate && irqLatch) { inner.RequestIRQ(true); open = 1; }
            int ran = 0;
            do
            {
                ushort pc = inner.GetRegisters().PC;
                int op = gate || IdleLoopSkip ? PeekOpcode(pc) : -1;
                ran += inner.ExecuteInstruction();
                if (gate)
                {
                    if (open > 0 && !inner.IrqPending) { irqLatch = false; open = 0; }   // taken: the request is used up
                    if (open > 0 && --open == 0) inner.RequestIRQ(false);
                    if (irqLatch && IrqCheck == IrqPoint.BatchStartAndUnmask && (op == 0x58 || op == 0x28 || op == 0x40)) { inner.RequestIRQ(true); open = 2; }
                }
                if (IdleLoopSkip && op >= 0 && ((op & 0x1F) == 0x10 || op == 0x4C))
                {
                    var r = inner.GetRegisters();
                    if (r.PC <= pc && pc - r.PC <= 16)
                    {
                        int regs = r.A | r.X << 8 | r.Y << 16 | r.P << 24;
                        if (idlePc == r.PC && idleRegs == regs) { IdleSkips++; if (ran < target) ran = (int)Math.Ceiling(target); break; }
                        idlePc = r.PC; idleRegs = regs;
                    }
                }
            } while (ran < target);
            lineLeft -= ran;
            if (splitLeft > 0) { splitLeft -= ran; if (splitLeft <= 0) splitLeft = -1; }
            if (nmiDelayLeft > 0) { nmiDelayLeft -= ran; if (nmiDelayLeft <= 0) { nmiDelayLeft = -1; inner.RequestNMI(); } }
            return ran;
        }

        public void RequestIRQ(bool line)
        {
            if (IrqCheck == IrqPoint.EveryInstruction || DoubleCountPenalties) { inner.RequestIRQ(line); return; }
            irqLatch = line;
            if (!line) inner.RequestIRQ(false);
        }

        public void RequestNMI()
        {
            if (LockLinesAtNmi && !DoubleCountPenalties) { lineLeft = lineBudget; splitLeft = -1; }
            if (NmiDelay > 0 && !DoubleCountPenalties) nmiDelayLeft = NmiDelay;
            else inner.RequestNMI();
        }

        public object GetState()
        {
            var s = (CpuSharedState)inner.GetState();
            var st = BlobCpuState.Make(id, w =>
            {
                w.Write(s.A); w.Write(s.X); w.Write(s.Y); w.Write(s.status); w.Write(s.PC); w.Write(s.SP);
                w.Write(s.irqRequested); w.Write(s.nmiRequested); w.Write(s.irqDeferOne); w.Write(s.nmiDeferOne);
                w.Write(lineLeft); w.Write(splitLeft); w.Write(nmiDelayLeft); w.Write(irqLatch); w.Write(idlePc); w.Write(idleRegs);
            });
            st.A = s.A; st.X = s.X; st.Y = s.Y; st.status = s.status; st.PC = s.PC; st.SP = s.SP; st.irqRequested = s.irqRequested; st.nmiRequested = s.nmiRequested;
            return st;
        }

        public void SetState(object state)
        {
            bool own = BlobCpuState.TryLoad(state, id, r =>
            {
                var s = new CpuSharedState { A = r.ReadByte(), X = r.ReadByte(), Y = r.ReadByte(), status = r.ReadByte(), PC = r.ReadUInt16(), SP = r.ReadUInt16(),
                    irqRequested = r.ReadBoolean(), nmiRequested = r.ReadBoolean(), irqDeferOne = r.ReadBoolean(), nmiDeferOne = r.ReadBoolean() };
                inner.SetState(s);
                lineLeft = r.ReadDouble(); splitLeft = r.ReadInt32(); nmiDelayLeft = r.ReadInt32(); irqLatch = r.ReadBoolean(); idlePc = r.ReadInt32(); idleRegs = r.ReadInt32();
            }, out _);
            if (own) return;
            // Another core's state: its 6502 registers (CPU_FIX reads a CpuSharedState or the JSON a load hands over), and a fresh line.
            inner.SetState(state);
            lineLeft = 0; splitLeft = -1; nmiDelayLeft = -1; irqLatch = false; idlePc = -1;
        }

        public (ushort PC, byte A, byte X, byte Y, byte P, ushort SP) GetRegisters() => inner.GetRegisters();
        public void AddToPC(int delta) => inner.AddToPC(delta);

        private static readonly byte[] BaseCycles =
        {
            7,6,2,8,3,3,5,5,3,2,2,2,4,4,6,6, 2,5,2,8,4,4,6,6,2,4,2,7,4,4,7,7,
            6,6,2,8,3,3,5,5,4,2,2,2,4,4,6,6, 2,5,2,8,4,4,6,6,2,4,2,7,4,4,7,7,
            6,6,2,8,3,3,5,5,3,2,2,2,3,4,6,6, 2,5,2,8,4,4,6,6,2,4,2,7,4,4,7,7,
            6,6,2,8,3,3,5,5,4,2,2,2,5,4,6,6, 2,5,2,8,4,4,6,6,2,4,2,7,4,4,7,7,
            2,6,2,6,3,3,3,3,2,2,2,2,4,4,4,4, 2,6,2,6,4,4,4,4,2,5,2,5,5,5,5,5,
            2,6,2,6,3,3,3,3,2,2,2,2,4,4,4,4, 2,5,2,5,4,4,4,4,2,4,2,4,4,4,4,4,
            2,6,2,8,3,3,5,5,2,2,2,2,4,4,6,6, 2,5,2,8,4,4,6,6,2,4,2,7,4,4,7,7,
            2,6,2,8,3,3,5,5,2,2,2,2,4,4,6,6, 2,5,2,8,4,4,6,6,2,4,2,7,4,4,7,7,
        };
    }

    /// <summary>InfoNES (Apache-2.0, jay-kumogata/InfoNES master): 113 cycles a line (hardware 113.67), interrupts checked only when a line's batch starts,
    /// and the line where sprite 0 starts split at X*113/256.</summary>
    public sealed class CPU_BAT_INES : BatchCpuBase
    {
        public CPU_BAT_INES(Bus bus) : base(bus, "BAT_INES") { CyclesPerLine = 113; Sprite0Split = true; IrqCheck = IrqPoint.BatchStart; }
        public override string CoreName => "Batch: InfoNES";
        public override string Description => "FIX opcodes on InfoNES's scheduler: 113-cycle line batches, interrupts at batch start, sprite-0 split at its X";
    }

    /// <summary>nofrendo (LGPL-2, retro-go 05e97e8): 1789773/60/262 cycles a line, the NMI 7 cycles after vblank, +2.5 a line for four MMC3 games.</summary>
    public sealed class CPU_BAT_NOFR : BatchCpuBase
    {
        public CPU_BAT_NOFR(Bus bus) : base(bus, "BAT_NOFR") { CyclesPerLine = 1789773.0 / 60 / 262; NmiDelay = 7; PerGameLineHack = true; }
        public override string CoreName => "Batch: nofrendo";
        public override string Description => "FIX opcodes on nofrendo's scheduler: whole-line batches, NMI 7 cycles late, the +2.5 MMC3 hack for SMB3/Kirby/Zen/Power Blade 2";
    }

    /// <summary>PocketNES (GBA, no licence file in the mirror): line batches, IRQs only at a line or after CLI/PLP/RTI, idle loops skipped to the line end.</summary>
    public sealed class CPU_BAT_PNES : BatchCpuBase
    {
        public CPU_BAT_PNES(Bus bus) : base(bus, "BAT_PNES") { IdleLoopSkip = true; IrqCheck = IrqPoint.BatchStartAndUnmask; }
        public override string CoreName => "Batch: PocketNES";
        public override string Description => "FIX opcodes on PocketNES's scheduler: line batches, IRQ only at a line or after CLI/PLP/RTI, idle-loop skip";
    }

    /// <summary>FCEUX e0028f6 (GPL-2, 2009-06-24, reverted in 854553e): per instruction, page-cross and interrupt cycles counted twice against the PPU.</summary>
    public sealed class CPU_BAT_FCEU : BatchCpuBase
    {
        public CPU_BAT_FCEU(Bus bus) : base(bus, "BAT_FCEU") { DoubleCountPenalties = true; }
        public override string CoreName => "Batch: FCEUX e0028f6";
        public override string Description => "FIX opcodes with FCEUX e0028f6's double-counted page-cross and interrupt cycles (the PPU gets those cycles twice)";
    }
}
