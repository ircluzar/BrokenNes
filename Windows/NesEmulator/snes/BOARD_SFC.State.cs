using System;
using System.IO;

namespace NesEmulator.Snes;

/// <summary>
/// The board's save state: the master clock and the scanline scheduler (line clock, the next-event bound, the HDMA / refresh stall, the vblank line), the CPU I/O block ($4200-$421F) with the multiply / divide unit and the
/// interrupt flags, the controller ports (held buttons, the auto-read result, the serial shift registers), the DMA registers and the HDMA channel state (done / transfer flags, the diagnostic counters), the 128 KB of work RAM
/// and the WRAM port address, the 65816, the S-PPU, the cartridge's RAM and the cartridge chip (DSP-1, SA-1, Super FX) when there is one.
/// <para>The <b>sound unit is not here</b>: it is the session's (it can be swapped while the game runs, and a state belongs to the one it was saved with - see <c>SnesSession</c>). The page table is derived
/// (<c>BuildPageTable</c> runs at the end of the load, after the chip, whose bank registers it depends on). The hooks (<c>InstructionHook</c>, <c>WriteWatch</c>) belong to whoever installed them.</para>
/// <para>Where the machine can be stopped: <see cref="RunFrame"/> ends at the start of vertical blanking, between two instructions; <see cref="RunToLine"/> stops at the first instruction boundary on or after a line. The 65816 never stops inside an
/// instruction and a DMA / HDMA transfer runs inside the write that starts it, so every point a caller can reach is a point where this state is complete (the sound unit, the SA-1 and the Super FX catch up lazily
/// from clocks they carry themselves).</para>
/// </summary>
public sealed partial class BOARD_SFC
{
    private const byte StateVersion = 1;

    /// <summary>
    /// Tool / test: runs whole instructions until the scanline counter reaches <paramref name="line"/> in the current frame (or the next frame starts), and stops there, between two instructions. Unlike <see cref="RunFrame"/> it does not stop
    /// at vblank, so lines 225 to 261 can be reached. A following <see cref="RunFrame"/> carries on to the next vblank.
    /// </summary>
    public void RunToLine(int line)
    {
        long startFrame = FrameCount;
        long guard = MasterClock + ClocksPerLine * (long)LinesPerFrame * 4;
        var hook = InstructionHook;
        while (FrameCount == startFrame && Scanline < line && MasterClock < guard)
        {
            hook?.Invoke(Cpu);
            Cpu.Step();
        }
    }

    public void SaveState(BinaryWriter w)
    {
        SnesStateIo.Begin(w, StateVersion);
        w.Write(MasterClock); w.Write(FrameCount); w.Write(Scanline); w.Write(lineClock); w.Write(inVBlank); w.Write(frameReady); w.Write(autoJoyBusyUntil);
        w.Write(nextEvent); w.Write(vblankLine); w.Write(stall);
        w.Write(nmitimen); w.Write(wrio); w.Write(wrmpya); w.Write(wrmpyb); w.Write(memsel); w.Write(hdmaen);
        w.Write(wrdiv); w.Write(rddiv); w.Write(rdmpy); w.Write(htime); w.Write(vtime);
        w.Write(nmiFlag); w.Write(irqFlag); w.Write(chipIrq); w.Write(mdr); w.Write(busAddress);
        w.Write(NmiCount); w.Write(VBlankCount);
        w.Write(wramPortAddress);
        SnesStateIo.Put(w, Pads); SnesStateIo.Put(w, autoJoy); SnesStateIo.Put(w, serialShift); w.Write(joyLatch);
        SnesStateIo.Put(w, dmaRegs);
        SnesStateIo.Put(w, hdmaDone); SnesStateIo.Put(w, hdmaDoTransfer); SnesStateIo.Put(w, hdmaBytes); SnesStateIo.Put(w, hdmaBytesLastFrame);
        foreach (var (frame, line, clock, value) in hdmaenLog) { w.Write(frame); w.Write(line); w.Write(clock); w.Write(value); }
        w.Write(hdmaenLogPos);
        SnesStateIo.Put(w, Wram);
        Cpu.SaveState(w);
        Ppu.SaveState(w);
        Cart.SaveState(w);
        w.Write(Coprocessor != null);
        if (Coprocessor != null) { w.Write(Coprocessor.StateId); Coprocessor.SaveState(w); }
    }

    public void LoadState(BinaryReader r)
    {
        SnesStateIo.Expect(r, StateVersion, "SNES board");
        MasterClock = r.ReadInt64(); FrameCount = r.ReadInt64(); Scanline = r.ReadInt32(); lineClock = r.ReadInt32(); inVBlank = r.ReadBoolean(); frameReady = r.ReadBoolean(); autoJoyBusyUntil = r.ReadInt64();
        nextEvent = r.ReadInt32(); vblankLine = r.ReadInt32(); stall = r.ReadInt32();
        nmitimen = r.ReadByte(); wrio = r.ReadByte(); wrmpya = r.ReadByte(); wrmpyb = r.ReadByte(); memsel = r.ReadByte(); hdmaen = r.ReadByte();
        wrdiv = r.ReadUInt16(); rddiv = r.ReadUInt16(); rdmpy = r.ReadUInt16(); htime = r.ReadUInt16(); vtime = r.ReadUInt16();
        nmiFlag = r.ReadBoolean(); irqFlag = r.ReadBoolean(); chipIrq = r.ReadBoolean(); mdr = r.ReadByte(); busAddress = r.ReadUInt32();
        NmiCount = r.ReadInt64(); VBlankCount = r.ReadInt64();
        wramPortAddress = r.ReadUInt32();
        SnesStateIo.Get(r, Pads); SnesStateIo.Get(r, autoJoy); SnesStateIo.Get(r, serialShift); joyLatch = r.ReadBoolean();
        SnesStateIo.Get(r, dmaRegs);
        SnesStateIo.Get(r, hdmaDone); SnesStateIo.Get(r, hdmaDoTransfer); SnesStateIo.Get(r, hdmaBytes); SnesStateIo.Get(r, hdmaBytesLastFrame);
        for (int i = 0; i < hdmaenLog.Length; i++) hdmaenLog[i] = (r.ReadInt64(), r.ReadInt32(), r.ReadInt32(), r.ReadByte());
        hdmaenLogPos = r.ReadInt32();
        SnesStateIo.Get(r, Wram);
        Cpu.LoadState(r);
        Ppu.LoadState(r);
        Cart.LoadState(r);
        bool hasChip = r.ReadBoolean();
        if (hasChip != (Coprocessor != null)) throw new InvalidDataException(hasChip ? "The state was saved with a cartridge chip; this game is running without one." : "The state was saved without a cartridge chip; this game is running with one.");
        if (Coprocessor != null)
        {
            string id = r.ReadString();
            if (id != Coprocessor.StateId) throw new InvalidDataException($"The state was saved with the {id} chip; this game is running the {Coprocessor.StateId} one.");
            Coprocessor.LoadState(r);
        }
        BuildPageTable();   // derived: the chip's bank registers and $420D decide it
    }
}
