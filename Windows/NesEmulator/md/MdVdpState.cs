using System;
using System.IO;

namespace NesEmulator.Sega;

/// <summary>
/// The VDP's save state, in one place: the memories and registers, the clock and the interrupt machinery, the command and FIFO and DMA state, and the renderer's half-drawn line with the two picture buffers (the
/// published picture is part of the state, so that the picture right after a load is the one that was saved, and the buffer under construction is, so that a restored VDP is the saved one field for field).
/// Any new field of <see cref="MdVdp"/> that a running game can change must be written here: <c>--md-state-test</c> compares a restored VDP with the one that saved by reflection and names the field that was left out.
/// </summary>
public sealed partial class MdVdp
{
    public void SaveState(BinaryWriter w)
    {
        w.Write(Vram); foreach (ushort c in Cram) w.Write(c); foreach (ushort v in Vsram) w.Write(v); w.Write(Reg);
        w.Write(Time); w.Write(lineStart); w.Write(Line); w.Write(Frames); w.Write(FrameCompleted); w.Write(oddFrame);
        w.Write(vintPending); w.Write(hintPending); w.Write(hintCounter); w.Write(vintTime); w.Write(hintTime); w.Write(z80IntClear); w.Write(z80IntAsserted);
        w.Write(vintEnableFrom); w.Write(hintEnableFrom); w.Write(slotCursor);
        w.Write(pendingCommand); w.Write(commandFirst); w.Write(code); w.Write(address); w.Write(readBuffer); w.Write(readReadyAt); w.Write(readCacheCode); w.Write(readCacheAddress); w.Write(readStale); w.Write(hvLatched);
        w.Write(fifoHead); w.Write(fifoCount); w.Write(lastFifoData);
        foreach (var e in fifo) { w.Write(e.Code); w.Write(e.Address); w.Write(e.Data); w.Write(e.SlotsLeft); w.Write(e.FromDma); }
        w.Write(dmaInFifo);
        w.Write((int)dma); w.Write(fillArmed); w.Write(dmaSource); w.Write(dmaLength); w.Write(dmaSlotsLeft); w.Write(fillData); w.Write(dmaTargetCode); w.Write(fillSourceStart); w.Write(fillLengthStart);
        w.Write(dmaStartTime); w.Write(dmaNextFetch); w.Write(SpriteCache);
        // the census counters (B4a's --md-census reads them): carried so that a loaded session's numbers go on from the saved one's
        w.Write(ActiveRegWrites); w.Write(ActiveCramWrites); w.Write(ActiveVsramWrites); w.Write(ActiveVramWrites); w.Write(HintTaken); w.Write(VintTaken);
        foreach (long v in ActiveRegWritesBy) w.Write(v);
        w.Write(DataWrites); w.Write(ControlWrites); w.Write(FifoStalls); w.Write(DmaWords); w.Write(FifoStallClocks);
        SaveRenderState(w);
    }

    public void LoadState(BinaryReader r)
    {
        ReadAll(r, Vram); for (int i = 0; i < Cram.Length; i++) Cram[i] = r.ReadUInt16(); for (int i = 0; i < Vsram.Length; i++) Vsram[i] = r.ReadUInt16(); ReadAll(r, Reg);
        Time = r.ReadInt64(); lineStart = r.ReadInt64(); Line = r.ReadInt32(); Frames = r.ReadInt64(); FrameCompleted = r.ReadBoolean(); oddFrame = r.ReadBoolean();
        vintPending = r.ReadBoolean(); hintPending = r.ReadBoolean(); hintCounter = r.ReadInt32(); vintTime = r.ReadInt64(); hintTime = r.ReadInt64(); z80IntClear = r.ReadInt64(); z80IntAsserted = r.ReadBoolean();
        vintEnableFrom = r.ReadInt64(); hintEnableFrom = r.ReadInt64(); slotCursor = r.ReadInt64();
        pendingCommand = r.ReadBoolean(); commandFirst = r.ReadUInt16(); code = r.ReadInt32(); address = r.ReadUInt32(); readBuffer = r.ReadUInt16(); readReadyAt = r.ReadInt64(); readCacheCode = r.ReadInt32(); readCacheAddress = r.ReadUInt32(); readStale = r.ReadBoolean(); hvLatched = r.ReadInt32();
        fifoHead = r.ReadInt32(); fifoCount = r.ReadInt32(); lastFifoData = r.ReadUInt16();
        for (int i = 0; i < fifo.Length; i++) fifo[i] = new FifoEntry { Code = r.ReadInt32(), Address = r.ReadUInt32(), Data = r.ReadUInt16(), SlotsLeft = r.ReadInt32(), FromDma = r.ReadBoolean() };
        dmaInFifo = r.ReadInt32();
        dma = (DmaKind)r.ReadInt32(); fillArmed = r.ReadBoolean(); dmaSource = r.ReadUInt32(); dmaLength = r.ReadInt32(); dmaSlotsLeft = r.ReadInt32(); fillData = r.ReadUInt16(); dmaTargetCode = r.ReadInt32(); fillSourceStart = r.ReadInt32(); fillLengthStart = r.ReadInt32();
        dmaStartTime = r.ReadInt64(); dmaNextFetch = r.ReadInt64(); ReadAll(r, SpriteCache);
        ActiveRegWrites = r.ReadInt64(); ActiveCramWrites = r.ReadInt64(); ActiveVsramWrites = r.ReadInt64(); ActiveVramWrites = r.ReadInt64(); HintTaken = r.ReadInt64(); VintTaken = r.ReadInt64();
        for (int i = 0; i < ActiveRegWritesBy.Length; i++) ActiveRegWritesBy[i] = r.ReadInt64();
        DataWrites = r.ReadInt64(); ControlWrites = r.ReadInt64(); FifoStalls = r.ReadInt64(); DmaWords = r.ReadInt64(); FifoStallClocks = r.ReadInt64();
        LoadRenderState(r);
    }

    // ---- the renderer: the line in progress, the sprites found for it, the window latch, and the two picture buffers

    private void SaveRenderState(BinaryWriter w)
    {
        w.Write(lineActive); w.Write(lineY); w.Write(lineWidth); w.Write(linePxDiv); w.Write(renderX); w.Write(colourX); w.Write(rowBase);
        w.Write(hscrollA); w.Write(hscrollB); w.Write(sprOverflow); w.Write(sprCollision); w.Write(sprDirty); w.Write(prevLineDotLimit);
        w.Write(winLatchDone); w.Write(winNext17); w.Write(winNext18); w.Write(winCur17); w.Write(winCur18);
        foreach (int s in lineSprites) w.Write(s);
        w.Write(lineSpriteCount); w.Write(lineSpriteOverflow);
        w.Write(lineIdx); w.Write(bufA); w.Write(bufB); w.Write(sprLine);
        w.Write(PictureWidth); w.Write(PictureHeight); w.Write(FramesRendered);
        WriteWords(w, picture); WriteWords(w, building);
        // interlace mode 2 (B4a): the field's row of the double-height picture, the cell height, and the woven picture itself when there is one (it is 1.3 MB: only in that mode)
        w.Write(dbl); w.Write(imgRow); w.Write(cellShift); w.Write(wovenValid); w.Write(WovenHeight); w.Write(SpriteOverflowLines);
        if (wovenValid) WriteWords(w, woven);
    }

    private void LoadRenderState(BinaryReader r)
    {
        lineActive = r.ReadBoolean(); lineY = r.ReadInt32(); lineWidth = r.ReadInt32(); linePxDiv = r.ReadInt32(); renderX = r.ReadInt32(); colourX = r.ReadInt32(); rowBase = r.ReadInt32();
        hscrollA = r.ReadInt32(); hscrollB = r.ReadInt32(); sprOverflow = r.ReadBoolean(); sprCollision = r.ReadBoolean(); sprDirty = r.ReadBoolean(); prevLineDotLimit = r.ReadBoolean();
        winLatchDone = r.ReadBoolean(); winNext17 = r.ReadByte(); winNext18 = r.ReadByte(); winCur17 = r.ReadByte(); winCur18 = r.ReadByte();
        for (int i = 0; i < lineSprites.Length; i++) lineSprites[i] = r.ReadInt32();
        lineSpriteCount = r.ReadInt32(); lineSpriteOverflow = r.ReadBoolean();
        ReadAll(r, lineIdx); ReadAll(r, bufA); ReadAll(r, bufB); ReadAll(r, sprLine);
        PictureWidth = r.ReadInt32(); PictureHeight = r.ReadInt32(); FramesRendered = r.ReadInt64();
        ReadWords(r, picture); ReadWords(r, building);
        dbl = r.ReadBoolean(); imgRow = r.ReadInt32(); cellShift = r.ReadInt32(); wovenValid = r.ReadBoolean(); WovenHeight = r.ReadInt32(); SpriteOverflowLines = r.ReadInt64();
        if (wovenValid) ReadWords(r, woven); else Array.Clear(woven);
        // the 192-entry colour table is derived from colour RAM
        for (int i = 0; i < 64; i++) UpdatePal(i);
    }

    private static void WriteWords(BinaryWriter w, uint[] a) => w.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.AsSpan()));

    private static void ReadWords(BinaryReader r, uint[] a) => ReadAll(r, System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.AsSpan()));

    /// <summary>Fills <paramref name="dst"/> from the reader or throws: a state that is cut short is an error, never a half-loaded machine.</summary>
    internal static void ReadAll(BinaryReader r, Span<byte> dst)
    {
        int got = 0;
        while (got < dst.Length)
        {
            int k = r.Read(dst[got..]);
            if (k <= 0) throw new EndOfStreamException("The state is cut short.");
            got += k;
        }
    }
}
