using System.IO;
using System.Runtime.InteropServices;

namespace NesEmulator.Snes;

/// <summary>
/// The S-PPU's save state: the three memories (VRAM, CGRAM, OAM), every register the games write with its internal latches (the two-write scroll latch, the Mode 7 latch, the CGRAM and OAM write latches, the VRAM prefetch,
/// the H/V counter latches and the two open-bus values of the PPU1 / PPU2 chips), the frame in progress and the published picture (the 256-wide frame, and the rows that were drawn in a hi-res mode at their true 512 width).
/// <para>What is <b>not</b> written, and why (the state test names each of these and shows that none of them can be seen from outside):
/// the per-line scratch buffers (<c>bgColor, bgPrio, objColor, objPrio, objMath, rankMain/Sub, colorMain/Sub, layerMain/Sub</c>) are rewritten whole for every line before they are read; the window masks and their signatures, the
/// decoded sprite attributes and their versions, the rank table and the brightness tables are caches of the registers and are rebuilt on use (<see cref="InvalidateCaches"/> is called by the loader); and the tools' hooks (<c>BridgeLines</c>,
/// <c>DebugLineRegisters</c>, <c>DebugLayerMask</c>, <c>CounterSource</c>) belong to whoever installed them.</para>
/// Any new register or latch added to this chip that a running game can change must be written here: <c>--snes-state-test</c> sets each field in turn and fails the one that comes back different.
/// </summary>
public sealed partial class PPU_SFC
{
    private const byte StateVersion = 1;

    public void SaveState(BinaryWriter w)
    {
        SnesStateIo.Begin(w, StateVersion);
        w.Write(inDisplay); w.Write(rangeOver); w.Write(timeOver);
        w.Write(inidisp); w.Write(obsel); w.Write(bgmode); w.Write(mosaic); w.Write(bg12nba); w.Write(bg34nba);
        SnesStateIo.Put(w, bgsc); SnesStateIo.Put(w, hofs); SnesStateIo.Put(w, vofs);
        w.Write(scrollLatch); w.Write(hofsLatch);
        w.Write(vmain); w.Write(vmadd); w.Write(vramPrefetch);
        w.Write(cgadd); w.Write(cgLatch); w.Write(cgHigh);
        w.Write(oamadd); w.Write(oamInternal); w.Write(oamLatch);
        w.Write(m7sel); w.Write(m7Latch);
        w.Write(m7a); w.Write(m7b); w.Write(m7c); w.Write(m7d); w.Write(m7x); w.Write(m7y); w.Write(m7hofs); w.Write(m7vofs);
        w.Write(tm); w.Write(ts); w.Write(w12sel); w.Write(w34sel); w.Write(wobjsel); w.Write(wh0); w.Write(wh1); w.Write(wh2); w.Write(wh3);
        w.Write(wbglog); w.Write(wobjlog); w.Write(tmw); w.Write(tsw); w.Write(cgwsel); w.Write(cgadsub); w.Write(setini); w.Write(coldata);
        w.Write(latchedH); w.Write(latchedV); w.Write(counterLatched); w.Write(ophctHigh); w.Write(opvctHigh); w.Write(ppu1Mdr); w.Write(ppu2Mdr);
        SnesStateIo.Put(w, Vram); SnesStateIo.Put(w, Cgram); SnesStateIo.Put(w, Oam);
        // the picture: the 256-wide frame (rows beyond VisibleHeight are stale but part of the machine), and the rows drawn at 512 width
        w.Write(VisibleHeight); w.Write(FrameHasHiRes);
        SnesStateIo.Put(w, FrameBuffer);
        SnesStateIo.Put(w, rowHiRes);
        for (int row = 0; row < rowHiRes.Length; row++)
            if (rowHiRes[row]) w.Write(MemoryMarshal.AsBytes(HiResBuffer.AsSpan(row * HiResWidth, HiResWidth)));
    }

    public void LoadState(BinaryReader r)
    {
        SnesStateIo.Expect(r, StateVersion, "S-PPU");
        inDisplay = r.ReadBoolean(); rangeOver = r.ReadBoolean(); timeOver = r.ReadBoolean();
        inidisp = r.ReadByte(); obsel = r.ReadByte(); bgmode = r.ReadByte(); mosaic = r.ReadByte(); bg12nba = r.ReadByte(); bg34nba = r.ReadByte();
        SnesStateIo.Get(r, bgsc); SnesStateIo.Get(r, hofs); SnesStateIo.Get(r, vofs);
        scrollLatch = r.ReadByte(); hofsLatch = r.ReadByte();
        vmain = r.ReadByte(); vmadd = r.ReadUInt16(); vramPrefetch = r.ReadUInt16();
        cgadd = r.ReadByte(); cgLatch = r.ReadByte(); cgHigh = r.ReadBoolean();
        oamadd = r.ReadUInt16(); oamInternal = r.ReadUInt16(); oamLatch = r.ReadByte();
        m7sel = r.ReadByte(); m7Latch = r.ReadByte();
        m7a = r.ReadInt16(); m7b = r.ReadInt16(); m7c = r.ReadInt16(); m7d = r.ReadInt16(); m7x = r.ReadInt16(); m7y = r.ReadInt16(); m7hofs = r.ReadInt16(); m7vofs = r.ReadInt16();
        tm = r.ReadByte(); ts = r.ReadByte(); w12sel = r.ReadByte(); w34sel = r.ReadByte(); wobjsel = r.ReadByte(); wh0 = r.ReadByte(); wh1 = r.ReadByte(); wh2 = r.ReadByte(); wh3 = r.ReadByte();
        wbglog = r.ReadByte(); wobjlog = r.ReadByte(); tmw = r.ReadByte(); tsw = r.ReadByte(); cgwsel = r.ReadByte(); cgadsub = r.ReadByte(); setini = r.ReadByte(); coldata = r.ReadUInt16();
        latchedH = r.ReadUInt16(); latchedV = r.ReadUInt16(); counterLatched = r.ReadBoolean(); ophctHigh = r.ReadBoolean(); opvctHigh = r.ReadBoolean(); ppu1Mdr = r.ReadByte(); ppu2Mdr = r.ReadByte();
        SnesStateIo.Get(r, Vram); SnesStateIo.Get(r, Cgram); SnesStateIo.Get(r, Oam);
        VisibleHeight = r.ReadInt32(); FrameHasHiRes = r.ReadBoolean();
        SnesStateIo.Get(r, FrameBuffer);
        SnesStateIo.Get(r, rowHiRes);
        for (int row = 0; row < rowHiRes.Length; row++)
            if (rowHiRes[row]) SnesStateIo.Get(r, HiResBuffer.AsSpan(row * HiResWidth, HiResWidth));
        // the caches describe the machine that was here before
        InvalidateCaches();
    }
}
