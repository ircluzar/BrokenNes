using System.Runtime.InteropServices;
using System.Threading;
using cor64.IO;
using cor64.Mips;
using NLog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace cor64.RCP
{

    /*
     *         0x0440 0000 to 0x0440 0003  VI_STATUS_REG or VI_CONTROL_REG
                 VI status/control
           (RW): [1:0] type[1:0] (pixel size) 
                     0: blank (no data, no sync)
                     1: reserved
                     2: 5/5/5/3 ("16" bit)
                     3: 8/8/8/8 (32 bit)
                 [2] gamma_dither_enable (normally on, unless "special effect")
                 [3] gamma_enable (normally on, unless MPEG/JPEG)
                 [4] divot_enable (normally on if antialiased,
                     unless decal lines)
                 [5] reserved - always off
                 [6] serrate (always on if interlaced, off if not)                                          
                 [7] reserved - diagnostics only
                 [9:8] anti-alias (aa) mode[1:0] 
                     0: aa & resamp (always fetch extra lines)                                           
                     1: aa & resamp (fetch extra lines if needed)                                                
                     2: resamp only (treat as all fully covered)                                             
                     3: neither (replicate pixels, no interpolate)                                          
                 [11] reserved - diagnostics only
                 [15:12] reserved
        0x0440 0004 to 0x0440 0007  VI_ORIGIN_REG or VI_DRAM_ADDR_REG
                 VI origin
           (RW): [23:0] frame buffer origin in bytes
        0x0440 0008 to 0x0440 000B  VI_WIDTH_REG or VI_H_WIDTH_REG
                 VI width
           (RW): [11:0] frame buffer line width in pixels                                         
        0x0440 000C to 0x0440 000F  VI_INTR_REG or VI_V_INTR_REG
                 VI vertical intr
           (RW): [9:0] interrupt when current half-line = V_INTR
        0x0440 0010 to 0x0440 0013  VI_CURRENT_REG or VI_V_CURRENT_LINE_REG                                           
                 VI current vertical line
           (RW): [9:0] current half line, sampled once per line (the lsb of
                       V_CURRENT is constant within a field, and in
                       interlaced modes gives the field number - which is
                       constant for non-interlaced modes)
                       - Writes clears interrupt line
        0x0440 0014 to 0x0440 0017  VI_BURST_REG or VI_TIMING_REG
                 VI video timing
           (RW): [7:0] horizontal sync width in pixels
                 [15:8] color burst width in pixels
                 [19:16] vertical sync width in half lines
                 [29:20] start of color burst in pixels from h-sync
        0x0440 0018 to 0x0440 001B  VI_V_SYNC_REG
                 VI vertical sync
           (RW): [9:0] number of half-lines per field
        0x0440 001C to 0x0440 001F  VI_H_SYNC_REG
                 VI horizontal sync
           (RW): [11:0] total duration of a line in 1/4 pixel
                 [20:16] a 5-bit leap pattern used for PAL only (h_sync_period)
        0x0440 0020 to 0x0440 0023  VI_LEAP_REG or VI_H_SYNC_LEAP_REG
                 VI horizontal sync leap
           (RW): [11:0] identical to h_sync_period
                 [27:16] identical to h_sync_period
        0x0440 0024 to 0x0440 0027  VI_H_START_REG or VI_H_VIDEO_REG
                 VI horizontal video
           (RW): [9:0] end of active video in screen pixels
                 [25:16] start of active video in screen pixels
        0x0440 0028 to 0x0440 002B  VI_V_START_REG or VI_V_VIDEO_REG
                 VI vertical video
           (RW): [9:0] end of active video in screen half-lines
                 [25:16] start of active video in screen half-lines
        0x0440 002C to 0x0440 002F  VI_V_BURST_REG
                 VI vertical burst
           (RW): [9:0] end of color burst enable in half-lines
                 [25:16] start of color burst enable in half-lines
        0x0440 0030 to 0x0440 0033  VI_X_SCALE_REG
                 VI x-scale
           (RW): [11:0] 1/horizontal scale up factor (2.10 format)
                 [27:16] horizontal subpixel offset (2.10 format)
        0x0440 0034 to 0x0440 0037  VI_Y_SCALE_REG
                 VI y-scale
           (RW): [11:0] 1/vertical scale up factor (2.10 format)
                 [27:16] vertical subpixel offset (2.10 format)
        0x0440 0038 to 0x044F FFFF  Unused*/

    public class Video : N64MemoryDevice
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private readonly MemMappedBuffer m_ControlReg = new MemMappedBuffer(4);
        private readonly MemMappedBuffer m_Origin = new MemMappedBuffer(4); // Where framebuffer is
        private readonly MemMappedBuffer m_Width = new MemMappedBuffer(4);
        private readonly MemMappedBuffer m_Interrupt = new MemMappedBuffer(4);
        private readonly MemMappedBuffer m_CurrentLine = new MemMappedBuffer(4);
        private readonly MemMappedBuffer m_Timing = new MemMappedBuffer(4);
        private readonly MemMappedBuffer m_VSync = new MemMappedBuffer(4);
        private readonly MemMappedBuffer m_HSync = new MemMappedBuffer(4);
        private readonly MemMappedBuffer m_Leap = new MemMappedBuffer(4);
        private readonly MemMappedBuffer m_HStart = new MemMappedBuffer(4);
        private readonly MemMappedBuffer m_VStart = new MemMappedBuffer(4);
        private readonly MemMappedBuffer m_VBurst = new MemMappedBuffer(4);
        private readonly MemMappedBuffer m_XScale = new MemMappedBuffer(4);
        private readonly MemMappedBuffer m_YScale = new MemMappedBuffer(4);
        private readonly N64MemoryController m_Memory;
        private readonly VideoControlReg m_ControlRegStruct;
        private readonly MipsInterface m_Interface;

        private readonly ManualResetEvent m_RdpWait = new ManualResetEvent(true);
        private bool m_UseRdp = false;
        private uint m_RdpAddress;

        private int? m_RdpFramebufferWidth;

        private bool m_LastActiveState = false;

        private int m_Ticks;

        public Video(N64MemoryController controller, MipsInterface mipsInterface) : base(controller, 0x100000)
        {
            StaticMap(
                m_ControlReg,
                m_Origin,
                m_Width,
                m_Interrupt,
                m_CurrentLine,
                m_Timing,
                m_VSync,
                m_HSync,
                m_Leap,
                m_HStart,
                m_VStart,
                m_VBurst,
                m_XScale,
                m_YScale);

            m_Interface = mipsInterface;

            m_ControlReg.Write += () => {
                // Log.Debug("Video Control Reg: {0:X8}", m_ControlReg.RegisterValue);

                if (!m_LastActiveState && IsVideoActive) {
                    Log.Debug("Video is now active");
                }

                if (m_LastActiveState && !m_LastActiveState) {
                    Log.Debug("Video is not active");
                }

                m_LastActiveState = IsVideoActive;
            };

            m_Origin.Write += FramebufferAddressHandler;
            m_CurrentLine.Write += CurrentScanlineHandler;

            // m_Width.Write += () => Log.Debug("Framebuffer width set to " + m_Width.RegisterValue.ToString("X8"));

            // m_XScale.Write += () => Log.Debug("Framebuffer xscale set to " + m_XScale.RegisterValue.ToString("X8"));

            // m_HStart.Write += () => Log.Debug("Framebuffer hstart set to " + m_HStart.RegisterValue.ToString("X8"));

            // m_VStart.Write += () => Log.Debug("Framebuffer vstart set to " + m_VStart.RegisterValue.ToString("X8"));

            // m_YScale.Write += () => Log.Debug("Framebuffer yscale set to " + m_YScale.RegisterValue.ToString("X8"));

            // m_Interrupt.Write += () => Log.Debug("Framebuffer interrupt set to " + m_Interrupt.RegisterValue.ToString("X8"));

            m_CurrentLine.Read += () => {
                if (!IsVideoActive || Interrupt == 0) return;

                Line++;

                if (Line > Interrupt) Line = 0;
            };

            m_Memory = controller;

            m_XScale.RegisterValue = 0x100U * (640U / 160U);
            m_YScale.RegisterValue = 0x100U * (480U / 60U);
            m_Width.RegisterValue = 640;


            m_ControlRegStruct = new VideoControlReg(m_ControlReg);
        }

        public override string Name => "Video Interface";

        // [catalog_fix1] COR64_PAL_VI (default on; =0 off): CPU ticks per video field. It was always 1/60 s. When the
        // console runs as PAL (Cartridge.Region, see COR64_REGION_FIX) the field is 1/50 s, as on a PAL console.
        // Certified games keep NTSC through the region rule, so their timing is unchanged.
        public static long FieldTicks = 93750000 / 60;
        private static readonly bool s_PalVi = Environment.GetEnvironmentVariable("COR64_PAL_VI") != "0";
        public static void ConfigureForRegion(RegionType region)
        {
            FieldTicks = s_PalVi && region == RegionType.PAL ? 93750000 / 50 : 93750000 / 60;
        }

        // [catalog_fix7] COR64_CATFIX7_VIWRAP (default on; =0 off): VI_CURRENT is a per-tick counter that wraps at V_INTR+100.
        // Roadsters Trophy (NRR*) busy-waits at 80001DE8 for VI_CURRENT >= 500 with V_INTR=2, so it never leaves boot (black,
        // no RSP tasks). For the listed games the counter wraps at V_SYNC+1 (a full field of half-lines, 525 on NTSC) as on
        // hardware (=2: every game, experiment). Other games keep the old wrap, so certified timing is unchanged.
        private static readonly bool s_CatFix7ViWrap = Environment.GetEnvironmentVariable("COR64_CATFIX7_VIWRAP") != "0";
        private static bool s_FullWrap;
        public static void ConfigureLineWrap(string gameCode)
        {
            s_FullWrap = Environment.GetEnvironmentVariable("COR64_CATFIX7_VIWRAP") == "2" || s_CatFix7ViWrap && gameCode != null && gameCode.Length >= 3 && gameCode.Substring(1, 2) == "RR";
        }
        private uint LineWrap => s_FullWrap ? Math.Max(m_Interrupt.RegisterValue + 100, (m_VSync.RegisterValue & 0x3FF) + 1) : m_Interrupt.RegisterValue + 100;

        public void Tick() {
            m_Ticks++;

            ScanlineTick();
            // m_CurrentLine.RegisterValue = m_Interrupt.RegisterValue;

            #if FASTER_VI
            m_Ticks += Telemetry.ViSpeedup - 1; // [cer64plus] was a fixed +10; COR64_VI_SPEEDUP sets the factor (default 11)
            #endif

            // CPU frequency divided by 60 Hz
            if (m_Ticks >= FieldTicks) {
                if (IsVideoActive) SetVideoInterrupt();
                m_Ticks = 0;
            }
        }

        // [cer64plus] Advance n ticks at once (batched stepping).
        // [climb4 idle_span] batches of n ticks until the first one that raises the VI event or wraps the line (>= 1);
        // Tick(n * k) for k up to this value equals k calls of Tick(n).
        public long BatchesUntilEvent(int n) {
            #if FASTER_VI
            long d = (long)n * Telemetry.ViSpeedup;
            #else
            long d = n;
            #endif
            long b = FastDiv.Div(FieldTicks - (long)m_Ticks + d - 1, d);
            if (b < 1) b = 1;
            if (IsVideoActive) {
                long lim = (long)LineWrap, line = m_CurrentLine.RegisterValue;
                long w = line + n >= lim ? 1 : FastDiv.Div(lim - line + n - 1, (long)n);
                if (w < b) b = w;
            }
            return b;
        }

        // [n64-revive] COR64_REVIVE1_VIMOD=1 (default off, experiment): with slices of n >= the line wrap (1024-step slices,
        // V_INTR+100 < 1024) the old wrap set VI_CURRENT to 0 on EVERY batch, so a game polling VI_CURRENT != 0 spins forever
        // (Pokemon Puzzle League 8003F8DC, likely Roadsters' title_fix3 too). =1: the line wraps modulo instead.
        private static readonly bool s_ViMod = Environment.GetEnvironmentVariable("COR64_REVIVE1_VIMOD") == "1";
        public void Tick(int n) {
            m_Interface.TickDma(n); // [timing] PI/SI DMA completions (COR64_TIMING_DMADUR)
            if (IsVideoActive) {
                uint line = m_CurrentLine.RegisterValue + (uint)n;
                if (line >= LineWrap) line = s_ViMod && (uint)n >= LineWrap ? line % LineWrap : 0;
                if (m_Field != 0 || (s_FieldOn && (m_ControlReg.RegisterValue & 0x40) != 0)) line = FieldLine(line);
                m_CurrentLine.RegisterValue = line;
            }
            #if FASTER_VI
            m_Ticks += n * Telemetry.ViSpeedup;
            #else
            m_Ticks += n;
            #endif
            if (m_Ticks >= FieldTicks) {
                if (IsVideoActive) SetVideoInterrupt();
                m_Ticks = 0;
            }
        }

        private void CurrentScanlineHandler()
        {
            m_Interface.ClearInterrupt(MipsInterface.INT_VI);
            m_Ticks = 0;
        }

        private void FramebufferAddressHandler()
        {
            // Log.Debug("Framebuffer pointer set to {0:X8}", FramebufferOffset);
        }

        public int ReadFramebufferAddressSafe() {
            // m_RdpWait.WaitOne();

            // if (!m_UseRdp)
                return (int)(m_Origin.RegisterValue & 0x00FFFFFF);
            // else
            //     return (int)(m_RdpAddress & 0x00FFFFFF);
        }

        public uint Origin => m_Origin.RegisterValue;

        public uint XScale => m_XScale.RegisterValue;

        public uint YScale => m_YScale.RegisterValue;

        public uint HStart => m_HStart.RegisterValue;

        public uint VStart => m_VStart.RegisterValue;

        public uint WidthReg => m_Width.RegisterValue;

        private int ComputeHWidth()
        {
            if (m_RdpFramebufferWidth != null) {
                return m_RdpFramebufferWidth.Value;
            }

            if (XScale == 0)
                return 320;

            int start = (int)((HStart >> 16) & 0x3FF);
            int end = (int)(HStart & 0x3FF);
            int res = end - start;
            int scale = (int)(XScale & 0xFFF);
            int offset = (int)((XScale >> 16) & 0x3FF);

            //start -= 108;

            return res * scale / 1024;
        }

        private int ComputeVHeight()
        {
            if (YScale == 0)
                return 240;

            var start = ((VStart & 0x03FF0000) >> 16) & 0x3FF;
            var end = (VStart & 0x3FF);
            var delta = end - start;
            var scale = (YScale & 0xFFF);

            return (int)((delta * scale) / 0x800);
        }

        public int Width => ComputeHWidth();

        public int Height => ComputeVHeight();

        public uint Interrupt => m_Interrupt.RegisterValue;

        public uint Line {
            get => m_CurrentLine.RegisterValue;
            set => m_CurrentLine.RegisterValue = value;
        }

        public VideoControlReg ControlReg => m_ControlRegStruct;

        public bool IsVideoActive => (m_ControlReg.RegisterValue & 3) != 0 && m_Interrupt.RegisterValue > 0;

        // [cer64plus] PPU slot of this machine: asynchronous renderers (parallel-rdp) finish the last SYNC_FULL in
        // BeforeScanout, so what the VI scans never depends on host timing. Not part of savestates.
        [NonSerialized] public cor64.Cores.IN64PpuCore Ppu;
        /// <summary>[cer64plus] Vertical interrupts on this machine (Telemetry.ViInterrupts is process-wide, diagnostics only).</summary>
        public long ViCount;

        public IntPtr FramebufferPtr => m_Memory.RDRAM.GetRamPointer(ReadFramebufferAddressSafe());

        // public unsafe void CopyFramebufferRGB5551_16(UnmanagedBuffer buffer)
        // {
        //     if (!IsVideoActive) return;

        //     // Converts RGB5551 to RGB565

        //     ushort* srcPixel = (ushort*)m_Memory.RDRAM.GetRamPointer(ReadFramebufferAddressSafe());
        //     ushort* dstPixel = (ushort*)buffer.GetPointer();

        //     for (int i = 0; i < buffer.Size / 2; i++)
        //     {
        //         ushort read = *srcPixel++;
        //         int pixel = 0;

        //        pixel = read.ByteSwapped();

        //         int r = (pixel >> 11) & 0x1F;
        //         int g = (pixel >> 6)  & 0x1F;
        //         int b = (pixel >> 1)  & 0x1F;

        //         // Sign extend the green field to the 6th bit
        //         g |= (g & 0x10) << 1;

        //         *dstPixel++ = (ushort)((r << 11) | (g << 6) | b);
        //     }
        // }

        // public unsafe void CopyFramebufferRGB5551_32(UnmanagedBuffer buffer)
        // {
        //     if (!IsVideoActive) return;

        //     ushort* srcPixel = (ushort*)m_Memory.RDRAM.GetRamPointer(ReadFramebufferAddressSafe());
        //     uint* dstPixel = (uint*)buffer.GetPointer();

        //     for (int i = 0; i < buffer.Size / 2; i++)
        //     {
        //         ushort read = *srcPixel++;
        //         int pixel = read;

        //         //pixel = read.ByteSwapped();

        //         // Transparancy Toggle
        //         // XXX: This prevents SkiaSharp from crashing
        //         // XXX: This breaks some rendering
        //         // XXX: Forcing alpha to 1 won't help either
        //         // if ((pixel & 1) == 0) {
        //         //     *dstPixel++ = 0;
        //         //     return;
        //         // }

        //         int r = (pixel >> 8) & 0xF8;
        //         int g = (pixel & 0x7C0) >> 3;
        //         int b = (pixel & 0x3E) << 2;

        //         int color = (b << 16) | (g << 8) | r;

        //         *dstPixel++ = (uint)color;
        //     }
        // }

        // public unsafe void CopyFramebufferRGBA8888(UnmanagedBuffer buffer)
        // {
        //     if (!IsVideoActive) return;

        //     uint* srcPixel = (uint*)m_Memory.RDRAM.GetRamPointer(ReadFramebufferAddressSafe());
        //     uint* dstPixel = (uint*)buffer.GetPointer();

        //     for (int i = 0; i < buffer.Size / 4; i++)
        //     {
        //         uint pixel = *srcPixel++;

        //        // pixel = pixel.ByteSwapped();

        //         *dstPixel++ = pixel;
        //     }
        // }

        // public void ScanlineStart() {
        //     m_CurrentLine.RegisterValue = 0;
        // }

        // public void ScanlineEnd() {
        //     m_CurrentLine.RegisterValue = m_Interrupt.RegisterValue;
        // }

        public void ScanlineTick() {
            if (IsVideoActive) {
                m_CurrentLine.RegisterValue++;

                // Don't reset right away, let it run it over so the CPU has a chance to see the wait value
                if (m_CurrentLine.RegisterValue >= LineWrap) {
                    m_CurrentLine.RegisterValue = 0;
                }
                if (m_Field != 0 || (s_FieldOn && (m_ControlReg.RegisterValue & 0x40) != 0)) m_CurrentLine.RegisterValue = FieldLine(m_CurrentLine.RegisterValue);
            }
        }

        public void SimulateFullScan() {
            if (!IsVideoActive) return;

            for (m_CurrentLine.RegisterValue = 0; m_CurrentLine.RegisterValue < m_Interrupt.RegisterValue; m_CurrentLine.RegisterValue++) {
                //Thread.Sleep(1);
            }
        }

        // [cer64plus] Deterministic frame oracle: COR64_FRAMEHASH_EVERY=<n> logs an FNV-1a hash of the visible framebuffer
        // every n VI interrupts to telemetry/framehash.txt. Runs on the emulation thread, so it is exact across runs.
        private static readonly int s_HashEvery = int.TryParse(Environment.GetEnvironmentVariable("COR64_FRAMEHASH_EVERY"), out var he) ? he : 0;
        private static System.IO.StreamWriter s_HashLog;

        private unsafe void FrameHash() {
            int w = Width, h = Height;
            if (w < 1 || h < 1 || w > 800 || h > 600) { w = 320; h = 240; }
            int bpp = ControlReg.GetPixelMode() == VideoControlReg.PIXELMODE_32BPP ? 4 : 2;
            int stride = (int)WidthReg; if (stride < w || stride > 1024) stride = w; // hash domain unchanged (diagnostics)
            byte* p = (byte*)FramebufferPtr;
            ulong hash = 14695981039346656037UL;
            int bytes = stride * h * bpp;
            for (int i = 0; i < bytes; i++) { hash ^= p[i]; hash *= 1099511628211UL; }
            s_HashLog ??= new System.IO.StreamWriter("telemetry/framehash.txt", false) { AutoFlush = true };
            s_HashLog.WriteLine(Telemetry.ViInterrupts + " " + hash.ToString("X16"));
        }

        // [cer64plus] Diagnostics only: COR64_VI_LOG=1 prints the VI registers whenever they change (at VI interrupts).
        private static readonly bool s_ViLog = Environment.GetEnvironmentVariable("COR64_VI_LOG") == "1";
        private string m_LastViLog;
        private void ViLog() {
            var t = $"ctrl={m_ControlReg.RegisterValue:X8} width={WidthReg} hstart={HStart:X8} vstart={VStart:X8} xscale={XScale:X8} yscale={YScale:X8} -> {Width}x{Height}";
            if (t == m_LastViLog) return;
            m_LastViLog = t;
            Console.WriteLine($"VILOG vi={ViCount} origin={Origin:X8} {t}");
        }

        // [f5_boot] Interlaced field bit: on hardware V_CURRENT bit 0 is the field number, constant within a field, and it
        // toggles every field when serrate is on and V_SYNC (half-lines per field) is even (ares vi.cpp, io.cpp). libultra's
        // osViSwapBuffer reads it to pick the field's origin; the Factor 5 games (rogue, naboo, indy) render their hi-res
        // screens one field at a time and showed only the never-drawn field (black) while it read as 0 forever.
        // Progressive modes (SM64) never set the bit. COR64_F5BOOT_VIFIELD=0 restores the old behaviour.
        private static readonly bool s_FieldOn = Environment.GetEnvironmentVariable("COR64_F5BOOT_VIFIELD") != "0";
        [NonSerialized] private uint m_Field; // not in the state format: restarts at field 0 after a load
        private uint FieldLine(uint line) {
            if ((m_ControlReg.RegisterValue & 0x40) == 0 || !s_FieldOn) { m_Field = 0; return line; }
            return (line & ~1u) | m_Field;
        }

        public void SetVideoInterrupt() {
            if (s_FieldOn && (m_ControlReg.RegisterValue & 0x40) != 0 && (m_VSync.RegisterValue & 1) == 0) {
                m_Field ^= 1;
                m_CurrentLine.RegisterValue = (m_CurrentLine.RegisterValue & ~1u) | m_Field;
            }
            Ppu?.BeforeScanout(); // [cer64plus] GPU renderers finish the last SYNC_FULL before the frame is scanned
            ViCount++;
            if (cor64.Mips.R4300I.SystemController.Boot1Active) cor64.Mips.R4300I.SystemController.Boot1Field(); // [boot_fix1]
            Telemetry.ViInterrupts++;
            if (s_ViLog) ViLog();
            if (s_HashEvery > 0 && Telemetry.ViInterrupts % s_HashEvery == 0 && System.IO.Directory.Exists("telemetry")) FrameHash();
            m_Interface.SetInterrupt(MipsInterface.INT_VI, true);
        }

        public void ClearVideoInterrupt() {
            m_Interface.ClearInterrupt(MipsInterface.INT_VI);
        }

        internal void SetFBWidthFromRDP(int framebufferWidth)
        {
            m_RdpFramebufferWidth = framebufferWidth;
            // Console.WriteLine("RDP Set Framebuffer Width: {0}", framebufferWidth);
        }

        public void SetFBFromRDP(uint framebufferAddress)
        {
            // Enable this to directly display where RDP is rasterizing to
            // m_RdpWait.Reset();
            // m_RdpAddress = framebufferAddress;
            // m_UseRdp = true;
            // m_RdpWait.Set();
        }

        public void FakeCurrentLine() {
            m_CurrentLine.RegisterValue = m_Interrupt.ReadonlyRegisterValue;
        }

        public MemExports ExportPointers() => new MemExports(this);

        public class MemExports {
            public IntPtr Status { get; }
            public IntPtr Origin { get; }
            public IntPtr Width { get; }
            public IntPtr Interrupt { get; }
            public IntPtr CurrentLine { get; }
            public IntPtr Timing { get; }
            public IntPtr VSync { get; }
            public IntPtr HSync { get; }
            public IntPtr Leap { get; }
            public IntPtr HStart { get; }
            public IntPtr VStart { get; }
            public IntPtr VBurst { get; }
            public IntPtr XScale { get; }
            public IntPtr YScale { get; }

            public MemExports(Video iface) {
                Status = iface.m_ControlReg.ReadPtr;
                Origin = iface.m_Origin.ReadPtr;
                Width = iface.m_Width.ReadPtr;
                Interrupt = iface.m_Interrupt.ReadPtr;
                CurrentLine = iface.m_CurrentLine.ReadPtr;
                Timing = iface.m_Timing.ReadPtr;
                VSync = iface.m_VSync.ReadPtr;
                HSync = iface.m_HSync.ReadPtr;
                Leap = iface.m_Leap.ReadPtr;
                HStart = iface.m_HStart.ReadPtr;
                VStart = iface.m_VStart.ReadPtr;
                VBurst = iface.m_VBurst.ReadPtr;
                XScale = iface.m_XScale.ReadPtr;
                YScale = iface.m_YScale.ReadPtr;
            }
        }
    }
}
