// [cer64plus] The N64 as three swappable cores, in the shape BrokenNes drives its consoles (CPU / PPU / APU slots with
// an id -> factory registry, like its SnesCores). No BrokenNes types are referenced; a BrokenNes-side session maps these.
//
//   CPU slot  "main emulation": VR4300, RSP, bus/memory map, PIF/SI/PI, scheduler (N64System + TickBatch).
//             The RSP belongs here: in low-level emulation it runs both the graphics and the audio microcode.
//   PPU slot  "video plugin":   the RDP (display-list processing into RDRAM) and VI scanout to ARGB.
//   APU slot  "audio plugin":   the AI DAC: consumes what the AI DMA plays and hands out stereo 16-bit samples.
//
// Task hooks: TryHandleTask lets a future high-level (HLE) video or audio core take a whole RSP task; every current core
// is low-level and returns false, so the RSP runs the microcode. (Any HLE core must be our own code: mupen's is GPL.)
using System;
using System.Collections.Generic;
using cor64.RCP;

namespace cor64.Cores
{
    public enum N64Slot { Cpu, Ppu, Apu }

    /// <summary>The OSTask header the RSP boot code sees in DMEM 0xFC0 when a task starts.</summary>
    public readonly struct N64Task
    {
        public readonly uint Type, Flags, Ucode, UcodeSize, UcodeData, UcodeDataSize, DataPtr, DataSize, OutputBuff, OutputSize;
        public N64Task(uint type, uint flags, uint ucode, uint ucodeSize, uint ucodeData, uint ucodeDataSize, uint dataPtr, uint dataSize, uint outputBuff, uint outputSize)
        { Type = type; Flags = flags; Ucode = ucode; UcodeSize = ucodeSize; UcodeData = ucodeData; UcodeDataSize = ucodeDataSize; DataPtr = dataPtr; DataSize = dataSize; OutputBuff = outputBuff; OutputSize = outputSize; }
        public const uint Graphics = 1, Audio = 2;
    }

    public interface IN64Core
    {
        string Id { get; }
        string Description { get; }
    }

    /// <summary>Main emulation. The session steps it; it owns the bus the other slots attach to.</summary>
    public interface IN64CpuCore : IN64Core
    {
        N64System Machine { get; }
        /// <summary>Runs n CPU instructions' worth of the whole machine (CPU, RSP, VI, AI).</summary>
        void RunBatch(int n);
        /// <summary>Vertical interrupts so far (per machine; a frame is one VI).</summary>
        long ViCount { get; }
    }

    /// <summary>Video plugin: the RDP and VI scanout.</summary>
    public interface IN64PpuCore : IN64Core, IDisposable
    {
        /// <summary>Finish all rendering already submitted (state boundaries, swaps).</summary>
        void Drain();
        /// <summary>Called at every vertical interrupt before the frame is scanned (asynchronous renderers finish here).</summary>
        void BeforeScanout();
        /// <summary>The visible picture as 0xAARRGGBB. False when the VI shows nothing.</summary>
        bool ScanOut(Video vi, ref uint[] frame, out int width, out int height);
        void BeforeStateSave();
        void AfterStateLoad();
        /// <summary>HLE hook: take a whole graphics task. Low-level cores return false.</summary>
        bool TryHandleTask(in N64Task task);
    }

    /// <summary>Audio plugin: the AI DAC.</summary>
    public interface IN64ApuCore : IN64Core
    {
        /// <summary>One stereo sample played by the AI at the given rate (called on the emulation thread).</summary>
        void Push(short left, short right, int rate);
        int SampleRate { get; }
        /// <summary>Stereo interleaved samples produced since the last call; returns the number of shorts written.</summary>
        int ReadSamples(Span<short> dest);
        /// <summary>Drop everything not yet read (after a state load or reset).</summary>
        void DiscardPending();
        /// <summary>Deliver a partially filled internal block (before a state save).</summary>
        void FlushPending();
        /// <summary>HLE hook: take a whole audio task. Low-level cores return false.</summary>
        bool TryHandleTask(in N64Task task);
    }

    /// <summary>Core ids to factories, per slot (the N64 counterpart of BrokenNes's SnesCores).</summary>
    public static class N64Cores
    {
        private static readonly Dictionary<string, Func<IN64PpuCore>> s_Ppu = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Func<IN64ApuCore>> s_Apu = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Func<bool>> s_PpuAvailable = new(StringComparer.OrdinalIgnoreCase);

        public const string CpuCor64 = "COR64";
        public const string PpuSoftware = "MADCAT", PpuParallel = "PRDP";
        public const string ApuAi = "AI";

        static N64Cores()
        {
            RegisterPpu(PpuSoftware, () => new cor64.Rdp.LLE.MadCatRdp(), () => true);
            RegisterPpu(PpuParallel, () => new cor64.Rdp.ParallelRdp(), () => cor64.Rdp.ParallelRdp.IsAvailable(false));
            RegisterApu(ApuAi, () => new AiDac());
        }

        public static void RegisterPpu(string id, Func<IN64PpuCore> factory, Func<bool> available) { s_Ppu[id] = factory; s_PpuAvailable[id] = available; }
        public static void RegisterApu(string id, Func<IN64ApuCore> factory) => s_Apu[id] = factory;

        public static IEnumerable<string> CpuIds => new[] { CpuCor64 };
        public static IEnumerable<string> PpuIds => s_Ppu.Keys;
        public static IEnumerable<string> ApuIds => s_Apu.Keys;

        public static bool IsAvailable(N64Slot slot, string id) => slot switch {
            N64Slot.Cpu => string.Equals(id, CpuCor64, StringComparison.OrdinalIgnoreCase),
            N64Slot.Ppu => s_PpuAvailable.TryGetValue(id, out var a) && a(),
            N64Slot.Apu => s_Apu.ContainsKey(id),
            _ => false
        };

        /// <summary>parallel-rdp when its DLL and a capable Vulkan device are present, else the software RDP.</summary>
        public static string DefaultPpu => IsAvailable(N64Slot.Ppu, PpuParallel) ? PpuParallel : PpuSoftware;

        public static IN64PpuCore CreatePpu(string id) => s_Ppu.TryGetValue(id, out var f) ? f() : throw new ArgumentException("Unknown N64 PPU core: " + id);
        public static IN64ApuCore CreateApu(string id) => s_Apu.TryGetValue(id, out var f) ? f() : throw new ArgumentException("Unknown N64 APU core: " + id);
    }
}
