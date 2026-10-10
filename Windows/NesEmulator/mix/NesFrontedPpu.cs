using System;
using System.Collections.Generic;
using NesEmulator.Mix;

namespace NesEmulator;

/// <summary>
/// The common half of the NES picture cores that hand their picture to another console's video chip (PPU_SMS, PPU_MD): a real NES PPU core (a FIX-timed one,
/// <see cref="MixConfig.NesFrontPpu"/>) stays in front and owns everything a NES program can see of the PPU - register behaviour, timing, NMI, the sprite-0 hit,
/// the status flags - while this class records, at dot 320 of every line, what the picture of the NEXT line is made from (scroll, mask, control), keeps the
/// pattern tables as they were when each 8-line group began, and takes the nametables, palette and OAM when the frame starts. At the end of the frame the
/// derived class replays that on a Sega VDP and the VDP draws the picture the user sees (<see cref="Present"/>).
/// </summary>
/// <remarks>
/// The core claims <see cref="IPpuFixTiming"/> and forwards it to the front chip, so the board runs a NES game on it with exactly the timing PPU_FIX gives it (per-instruction
/// catch-up, the NMI placed inside the instruction): no timing switch is needed to use it, and a front chip without that timing is refused rather than quietly run batched.
/// Only the captured frame ever reaches the screen; the front chip's own picture stays available (<see cref="IPictureBridgeInfo"/>) as the reference a fidelity measurement compares with.
/// </remarks>
public abstract class NesFrontedPpu : IPPU, IPpuFixTiming, IPictureBridgeInfo
{
    protected readonly Bus bus;
    protected readonly IPPU front;
    private readonly IPpuFixTiming timing;
    protected readonly byte[] frame = new byte[256 * 240 * 4];
    private readonly Func<ushort, byte> busRead;

    // ---- the NES registers the bridge shadows (the front chip also gets every access)
    private byte ctrl, oamAddr;
    private readonly byte[] oam = new byte[256];

    // ---- what each line of the frame is drawn from, taken at dot 320 of the line before
    protected readonly int[] LineX = new int[240], LineY = new int[240];
    protected readonly byte[] LineMask = new byte[240], LineCtrl = new byte[240];
    /// <summary>Nametables (2 KB mirrored out to four 1 KB pages), palette and OAM as the frame began.</summary>
    protected readonly byte[] Nt = new byte[0x1000], Pal = new byte[32], FrameOam = new byte[256];
    /// <summary>The palette near the end of the frame (a palette written mid-frame shows as a difference from <see cref="Pal"/>).</summary>
    protected readonly byte[] PalEnd = new byte[32];
    /// <summary>The picture frame the translation is for (counts frames presented).</summary>
    protected long FrameNo;
    private int lastLine = -1;

    public readonly BridgeCounters Counters = new();

    /// <summary>One pattern-table content (CHR as the PPU saw it for some stretch of the frame). Equal contents are the same object, so a game that flips between two banks keeps two versions, not one per frame.</summary>
    protected sealed class ChrVersion
    {
        public int Id;
        public readonly byte[] Data = new byte[0x2000];
        public ulong Hash;
        public long LastUsed;
    }

    private readonly Dictionary<ulong, ChrVersion> chrCache = new();
    private int nextChrId = 1;
    private readonly ChrVersion?[] groupChr = new ChrVersion?[30];
    private ulong lastFingerprint;
    private byte[] scratchChr = new byte[0x2000];

    protected NesFrontedPpu(Bus bus, string? frontId = null)
    {
        this.bus = bus;
        frontId ??= MixConfig.NesFrontPpu;
        var t = CoreRegistry.PpuTypes.TryGetValue(frontId, out var ft) ? ft : throw new ArgumentException($"No NES PPU '{frontId}'");
        front = CoreRegistry.CreateInstance<IPPU>(t, bus) ?? throw new InvalidOperationException($"The front NES PPU {frontId} could not be created");
        timing = front as IPpuFixTiming
            ?? throw new NotSupportedException($"NES PPU {frontId} has no FIX timing - it cannot front a {GetType().Name.Replace("PPU_", "")} picture chip (use FIX or FIXS)");
        busRead = timing.ProbePpuBusRead;
        for (int i = 0; i < frame.Length; i += 4) frame[i + 3] = 255;
    }

    // ---------------------------------------------------------------- the IPPU surface (the front chip's behaviour, untouched)
    public abstract string CoreName { get; }
    public abstract string Description { get; }
    public int Performance => 0;
    public int Rating => 1;
    public string Category => "Experimental";

    public void Step(int cycles)
    {
        while (cycles > 0)
        {
            int dot = timing.ProbeDot;
            int chunk = dot < 320 ? Math.Min(cycles, 320 - dot) : Math.Min(cycles, 341 - dot);
            if (chunk <= 0) chunk = 1;
            front.Step(chunk);
            cycles -= chunk;
            if (timing.ProbeDot == 320) OnDot320(timing.ProbeScanline);
            int line = timing.ProbeScanline;
            if (line == 240 && lastLine != 240) PresentFrame();
            lastLine = line;
        }
    }

    public byte ReadPPURegister(ushort address) => front.ReadPPURegister(address);

    public void WritePPURegister(ushort address, byte value)
    {
        switch (address & 7)
        {
            case 0: ctrl = value; break;
            case 3: oamAddr = value; break;
            case 4: oam[oamAddr++] = value; break;
        }
        front.WritePPURegister(address, value);
    }

    public void WriteOAMDMA(byte page)
    {
        for (int i = 0; i < 256; i++) oam[(oamAddr + i) & 0xFF] = bus.Read((ushort)((page << 8) | i));
        front.WriteOAMDMA(page);
    }

    /// <summary>MIX_FRONTFB=1 shows the front chip's own picture instead (diagnosis).</summary>
    public static bool ShowFront = Environment.GetEnvironmentVariable("MIX_FRONTFB") == "1";
    public byte[] GetFrameBuffer() => ShowFront ? front.GetFrameBuffer() : frame;
    public void UpdateFrameBuffer() { }
    public object GetState() => front.GetState();
    public void SetState(object state) => front.SetState(state);
    public void GenerateStaticFrame() => front.GenerateStaticFrame();
    public void ClearBuffers() { front.ClearBuffers(); Array.Clear(frame); for (int i = 3; i < frame.Length; i += 4) frame[i] = 255; }

    // ---- probe and FIX timing: the front chip's, so the board treats this core as it treats PPU_FIX
    public int ProbeScanline => timing.ProbeScanline;
    public int ProbeDot => timing.ProbeDot;
    public byte ProbeMask => timing.ProbeMask;
    public byte ProbePpuBusRead(ushort address) => busRead(address);
    public void ProbePpuBusWrite(ushort address, byte value) => timing.ProbePpuBusWrite(address, value);
    public long DotCounter => timing.DotCounter;
    public long LastInterruptDot => timing.LastInterruptDot;
    public ushort ProbeV => timing.ProbeV;
    public byte ProbeFineX => timing.ProbeFineX;

    // ---------------------------------------------------------------- capture
    private void OnDot320(int line)
    {
        int next = line == 261 ? 0 : line + 1;
        if (line != 261 && line >= 239) return;
        if (next == 0) BeginFrameCapture();
        else if ((next & 7) == 0) CaptureGroup(next >> 3, full: false);
        if (next == 232) for (int i = 0; i < 32; i++) PalEnd[i] = busRead((ushort)(0x3F00 + i));
        ushort v = timing.ProbeV; int fx = timing.ProbeFineX;
        LineX[next] = ((v >> 10) & 1) * 256 + (v & 31) * 8 + fx;
        LineY[next] = ((v >> 11) & 1) * 256 + ((v >> 5) & 31) * 8 + ((v >> 12) & 7);
        LineMask[next] = timing.ProbeMask;
        LineCtrl[next] = ctrl;
    }

    private void BeginFrameCapture()
    {
        for (int i = 0; i < 0x1000; i++) Nt[i] = busRead((ushort)(0x2000 + i));
        for (int i = 0; i < 32; i++) Pal[i] = busRead((ushort)(0x3F00 + i));
        Array.Copy(Pal, PalEnd, 32);
        Array.Copy(oam, FrameOam, 256);
        CaptureGroup(0, full: true);
    }

    /// <summary>The pattern tables for the 8-line group that starts now: a cheap sample of 64 bytes decides whether anything changed since the group before (a bank switch mid-frame).</summary>
    private void CaptureGroup(int group, bool full)
    {
        ulong fp = 14695981039346656037UL;
        for (int k = 0; k < 64; k++) fp = (fp ^ busRead((ushort)(k * 128 + ((k * 53) & 127)))) * 1099511628211UL;
        if (!full && fp == lastFingerprint && groupChr[group - 1] != null) { groupChr[group] = groupChr[group - 1]; return; }
        lastFingerprint = fp;
        for (int i = 0; i < 0x2000; i++) scratchChr[i] = busRead((ushort)i);
        ulong h = 14695981039346656037UL;
        for (int i = 0; i < 0x2000; i++) h = (h ^ scratchChr[i]) * 1099511628211UL;
        if (!chrCache.TryGetValue(h, out var ver) || !scratchChr.AsSpan().SequenceEqual(ver.Data))
        {
            if (chrCache.Count > 48)
            {
                // drop the versions not used for a while
                var old = new List<ulong>();
                foreach (var kv in chrCache) if (kv.Value.LastUsed < FrameNo - 120) old.Add(kv.Key);
                foreach (var k in old) chrCache.Remove(k);
                if (chrCache.Count > 48) chrCache.Clear();
            }
            ver = new ChrVersion { Id = nextChrId++, Hash = h };
            Array.Copy(scratchChr, ver.Data, 0x2000);
            chrCache[h] = ver;
        }
        ver.LastUsed = FrameNo;
        groupChr[group] = ver;
    }

    /// <summary>The pattern tables the 8-line group of NES line <paramref name="line"/> was drawn with.</summary>
    protected ChrVersion ChrAtLine(int line) => groupChr[Math.Clamp(line >> 3, 0, 29)] ?? groupChr[0]!;

    protected int DistinctChrVersions()
    {
        var seen = new HashSet<int>();
        foreach (var g in groupChr) if (g != null) seen.Add(g.Id);
        return seen.Count;
    }

    /// <summary>The nametable cell at map pixel (X, Y) in the 512 x 512 tile space (X bit 8 and Y bit 8 pick the page): tile index and attribute palette.</summary>
    protected int NesCell(int X, int Y, out int palette)
    {
        int ntx = (X >> 8) & 1, nty = (Y >> 8) & 1, col = (X & 255) >> 3, row = Math.Min((Y & 255) >> 3, 29);
        int baseAddr = (ntx + nty * 2) * 0x400;
        int attr = Nt[baseAddr + 0x3C0 + (row >> 2) * 8 + (col >> 2)];
        palette = (attr >> (((row & 2) << 1) | (col & 2))) & 3;
        return Nt[baseAddr + row * 32 + col];
    }

    private void PresentFrame()
    {
        FrameNo++;
        Counters.BeginFrame();
        // the pattern tables were sampled only up to the last group; groups never reached keep the last version
        for (int g = 1; g < 30; g++) groupChr[g] ??= groupChr[g - 1];
        Present();
    }

    /// <summary>Replay the captured frame on the Sega chip and write the finished picture into <see cref="frame"/> (RGBA, 256 x 240).</summary>
    protected abstract void Present();

    protected void StoreRgb(int x, int y, uint argb)
    {
        int o = (y * 256 + x) * 4;
        frame[o] = (byte)(argb >> 16); frame[o + 1] = (byte)(argb >> 8); frame[o + 2] = (byte)argb; frame[o + 3] = 255;
    }

    // ---------------------------------------------------------------- the reference and the counters (IPictureBridgeInfo)
    public abstract string BridgeName { get; }
    BridgeCounters IPictureBridgeInfo.Counters => Counters;
    public int CanvasWidth => 256;
    public int CanvasHeight => 240;
    public (int X, int Y, int W, int H) Region => (0, 0, 256, 240);
    public bool ReferenceIsSameFrame => true;
    private uint[]? refCanvas, bridgeCanvas;
    public uint[] ReferenceCanvas => ToArgb(front.GetFrameBuffer(), ref refCanvas);
    public uint[] BridgeCanvas => ToArgb(frame, ref bridgeCanvas);
    /// <summary>The front chip's own picture (RGBA), what the NES would have shown.</summary>
    public byte[] FrontFrame => front.GetFrameBuffer();

    private static uint[] ToArgb(byte[] rgba, ref uint[]? cache)
    {
        cache ??= new uint[256 * 240];
        for (int i = 0; i < cache.Length; i++) { int o = i * 4; cache[i] = 0xFF000000u | (uint)rgba[o] << 16 | (uint)rgba[o + 1] << 8 | rgba[o + 2]; }
        return cache;
    }

    // ---------------------------------------------------------------- helpers the Sega-side translations share
    /// <summary>A NES colour (palette byte) as 0xRRGGBB exactly as the front chip (the PPU_FIX family) draws it: greyscale and the three emphasis bits of <paramref name="mask"/> applied (an emphasised
    /// channel keeps its level, the other two are darkened to 74.6 %, as PPU_FIX does).</summary>
    protected static int NesRgb(byte colour, byte mask)
    {
        int c = colour & 0x3F;
        if ((mask & 1) != 0) c &= 0x30;
        int rgb = NesFixPalette.Rgb(c);
        if ((mask & 0xE0) != 0)
        {
            int r = rgb >> 16 & 0xFF, g = rgb >> 8 & 0xFF, b = rgb & 0xFF;
            bool emR = (mask & 0x20) != 0, emG = (mask & 0x40) != 0, emB = (mask & 0x80) != 0;
            if (emG || emB) r = (r * 746 + 500) / 1000;
            if (emR || emB) g = (g * 746 + 500) / 1000;
            if (emR || emG) b = (b * 746 + 500) / 1000;
            rgb = r << 16 | g << 8 | b;
        }
        return rgb;
    }

    /// <summary>The mask most lines used (greyscale and emphasis are chip-wide on the Sega side).</summary>
    protected byte DominantMask()
    {
        var count = new int[256];
        for (int l = 0; l < 240; l++) count[LineMask[l]]++;
        int best = 0; for (int i = 1; i < 256; i++) if (count[i] > count[best]) best = i;
        return (byte)best;
    }
}
