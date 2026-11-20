// [climb4 x64_emit] Executable memory, W^X by double mapping: one pagefile-backed section is mapped twice, a read-write
// view the emitter writes through and an execute-read view the code runs from; no view is ever writable and executable.
// Blocks are packed densely (64-byte size classes) so hot code shares pages and iTLB entries (COR64_X64_PACK=0: one
// whole page per block, the first prototype's layout). Publishing writes the bytes, flushes the icache, and the caller
// then publishes the entry pointer with a volatile store.
// Invalidated code is retired (Retire) and goes back to the size-class free lists at the next Reclaim(), which the
// emulator thread calls only outside any native block (top of CPU RunSteps), so no retired code can still be running.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace cor64.Mips.X64
{
    public static unsafe class ExecMem
    {
        private const uint PAGE_EXECUTE_READWRITE = 0x40, SEC_COMMIT = 0x8000000;
        private const uint FILE_MAP_WRITE = 0x2, FILE_MAP_READ = 0x4, FILE_MAP_EXECUTE = 0x20;
        private const long Size = 128L << 20;

        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateFileMappingW(IntPtr file, IntPtr attr, uint protect, uint hi, uint lo, IntPtr name);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr MapViewOfFile(IntPtr map, uint access, uint offHi, uint offLo, UIntPtr size);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] private static extern bool FlushInstructionCache(IntPtr proc, IntPtr addr, UIntPtr size);

        public static readonly bool Pack = Environment.GetEnvironmentVariable("COR64_X64_PACK") != "0";
        private static readonly object s_Lock = new();
        private static byte* s_Rx, s_Rw;
        private static long s_Used;
        private static readonly Dictionary<long, Stack<long>> s_Free = new();   // size class -> offsets
        private static readonly Dictionary<long, long> s_Size = new();          // offset -> size class
        private static readonly List<IntPtr> s_Retired = new();
        public static volatile int RetiredCount;
        public static long Reused, Freed;
        public static long BytesUsed => s_Used;

        public static bool Supported => OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

        public static void Retire(IntPtr p) { if (p == IntPtr.Zero) return; lock (s_Lock) { s_Retired.Add(p); RetiredCount = s_Retired.Count; } }

        public static void Reclaim()
        {
            lock (s_Lock) {
                foreach (var p in s_Retired) {
                    long off = (byte*)p - s_Rx;
                    if (!s_Size.TryGetValue(off, out long sz)) continue;
                    if (!s_Free.TryGetValue(sz, out var st)) s_Free[sz] = st = new();
                    st.Push(off); Freed++;
                }
                s_Retired.Clear(); RetiredCount = 0;
            }
        }

        private static long Alloc(int size)
        {
            long cls = Pack ? (size + 63L) & ~63L : (size + 4095L) & ~4095L;
            lock (s_Lock) {
                if (s_Rx == null) {
                    var h = CreateFileMappingW(new IntPtr(-1), IntPtr.Zero, PAGE_EXECUTE_READWRITE | SEC_COMMIT, (uint)(Size >> 32), (uint)Size, IntPtr.Zero);
                    if (h == IntPtr.Zero) throw new OutOfMemoryException("x64 code section");
                    s_Rw = (byte*)MapViewOfFile(h, FILE_MAP_WRITE, 0, 0, (UIntPtr)(ulong)Size);
                    s_Rx = (byte*)MapViewOfFile(h, FILE_MAP_READ | FILE_MAP_EXECUTE, 0, 0, (UIntPtr)(ulong)Size);
                    if (s_Rw == null || s_Rx == null) throw new OutOfMemoryException("x64 code views");
                }
                if (s_Free.TryGetValue(cls, out var st) && st.Count > 0) { Reused++; return st.Pop(); }
                if (s_Used + cls > Size) return -1;
                long off = s_Used;
                s_Used += cls;
                s_Size[off] = cls;
                return off;
            }
        }

        /// <summary>Links the code for its execute address, writes it through the RW view, flushes the icache.</summary>
        public static byte* Publish(X64Asm a)
        {
            long off = Alloc(a.Length);
            if (off < 0) return null;
            byte* rx = s_Rx + off;
            var bytes = a.Link((long)rx);
            Marshal.Copy(bytes, 0, (IntPtr)(s_Rw + off), bytes.Length);
            FlushInstructionCache(GetCurrentProcess(), (IntPtr)rx, (UIntPtr)(ulong)bytes.Length);
            System.Threading.Thread.MemoryBarrier();
            return rx;
        }
    }
}
