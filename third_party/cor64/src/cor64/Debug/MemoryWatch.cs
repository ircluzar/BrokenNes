// [dbg_mem] Mirror-aware memory watches. A watch is kept as a physical byte range, so a watch set on 0x80001000
// (KSEG0) also fires for 0xA0001000 (KSEG1), for TLB-mapped VAs that land on the same page, and for physical 0x1000.
// Hooked in N64MemoryController's U8..U64 (every CPU interpreter load/store goes through them). While any watch is
// armed the CPU runs interpreted (compiled blocks access RDRAM directly) and idle/poll skipping is off; with none
// armed the bus pays one not-taken branch and nothing else changes (COR64_DBG_WATCH=0 removes even that).
// Not seen: DMA (PI/SI/SP/AI) and the RSP's own DMEM/IMEM accesses, which do not go through the CPU bus.
using System;
using System.Collections.Generic;

namespace cor64.Debug
{
    [Flags] public enum WatchKind { Read = 1, Write = 2, ReadWrite = 3 }

    /// <summary>Value test on the accessed value: Any, Equal / NotEqual ((v &amp; Mask) vs (Value &amp; Mask)),
    /// Changed (writes whose new value differs from the old one under Mask).</summary>
    public enum WatchCondition { Any, Equal, NotEqual, Changed }

    public sealed class MemoryWatch
    {
        public int Id { get; internal set; }
        /// <summary>Address as given by the user (virtual or physical).</summary>
        public ulong Address { get; internal set; }
        public uint PhysStart { get; internal set; }
        public int Length { get; internal set; }
        public WatchKind Kind { get; internal set; }
        public WatchCondition Condition { get; internal set; }
        public ulong Value { get; internal set; }
        public ulong Mask { get; internal set; } = ulong.MaxValue;
        public long HitCount { get; internal set; }
        public override string ToString() => $"#{Id} {Address:X8} (phys {PhysStart:X8}+{Length}) {Kind} {Condition}";
    }

    public readonly struct WatchHit
    {
        public readonly int WatchId; public readonly uint Phys; public readonly int Size; public readonly bool IsWrite;
        public readonly ulong Value; public readonly ulong OldValue; public readonly ulong Pc;
        public WatchHit(int id, uint phys, int size, bool write, ulong value, ulong old, ulong pc)
        { WatchId = id; Phys = phys; Size = size; IsWrite = write; Value = value; OldValue = old; Pc = pc; }
        public override string ToString() => $"watch #{WatchId} {(IsWrite ? "W" : "R")}{Size} {Phys:X8} = {Value:X} (was {OldValue:X}) pc={(uint)Pc:X8}";
    }

    public sealed class MemoryWatchSet
    {
        public const int MaxHits = 4096;
        readonly N64MemoryController m_Bus;
        MemoryWatch[] m_Watches = Array.Empty<MemoryWatch>();
        readonly List<WatchHit> m_Hits = new List<WatchHit>();
        int m_NextId = 1;
        public Func<ulong> PcSource;
        /// <summary>Called on every hit, on the emulation thread.</summary>
        public Action<WatchHit> OnHit;
        /// <summary>Hits dropped because the hit buffer was full.</summary>
        public long Dropped { get; private set; }
        /// <summary>Set on a hit; the host clears it (e.g. to stop at the end of the frame).</summary>
        public bool HitPending;

        public MemoryWatchSet(N64MemoryController bus) { m_Bus = bus ?? throw new ArgumentNullException(nameof(bus)); }

        public IReadOnlyList<MemoryWatch> Watches => m_Watches;

        public MemoryWatch Add(ulong address, uint phys, int length, WatchKind kind, WatchCondition cond, ulong value, ulong mask)
        {
            if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));
            if ((kind & WatchKind.ReadWrite) == 0) throw new ArgumentException("kind");
            var w = new MemoryWatch { Id = m_NextId++, Address = address, PhysStart = phys, Length = length, Kind = kind, Condition = cond, Value = value, Mask = mask };
            var a = new MemoryWatch[m_Watches.Length + 1]; m_Watches.CopyTo(a, 0); a[^1] = w; m_Watches = a;
            m_Bus.Watches = this;
            return w;
        }

        public bool Remove(int id)
        {
            int i = Array.FindIndex(m_Watches, w => w.Id == id);
            if (i < 0) return false;
            var a = new List<MemoryWatch>(m_Watches); a.RemoveAt(i); m_Watches = a.ToArray();
            if (m_Watches.Length == 0) m_Bus.Watches = null;
            return true;
        }

        public void Clear() { m_Watches = Array.Empty<MemoryWatch>(); m_Bus.Watches = null; }

        public IReadOnlyList<WatchHit> Drain() { var r = m_Hits.ToArray(); m_Hits.Clear(); HitPending = false; return r; }

        /// <summary>Called by the bus for each CPU access (address = physical).</summary>
        public void OnAccess(long address, int size, bool write, ulong value)
        {
            uint p = (uint)address;
            var ws = m_Watches;
            for (int i = 0; i < ws.Length; i++)
            {
                var w = ws[i];
                if (p + (uint)size <= w.PhysStart || p >= w.PhysStart + (uint)w.Length) continue;
                if ((w.Kind & (write ? WatchKind.Write : WatchKind.Read)) == 0) continue;
                ulong old = m_Bus.DebugPeek(address, size);
                ulong v = write ? value : old;
                if (size < 8) v &= (1UL << (8 * size)) - 1;
                bool ok = w.Condition switch
                {
                    WatchCondition.Equal => (v & w.Mask) == (w.Value & w.Mask),
                    WatchCondition.NotEqual => (v & w.Mask) != (w.Value & w.Mask),
                    WatchCondition.Changed => write && (v & w.Mask) != (old & w.Mask),
                    _ => true,
                };
                if (!ok) continue;
                w.HitCount++;
                var hit = new WatchHit(w.Id, p, size, write, v, old, PcSource?.Invoke() ?? 0);
                if (m_Hits.Count < MaxHits) m_Hits.Add(hit); else Dropped++;
                HitPending = true;
                OnHit?.Invoke(hit);
            }
        }
    }
}
