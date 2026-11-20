using System;
using System.Runtime.CompilerServices;

// [mem_fix] Paged per-word tables for N64MemoryDevice (COR64_MEM_PAGEDTABLES, default on).
// The device used to keep six flat arrays with one entry per 4-byte guest word (34 bytes per word, 8.5x the device's
// address-space size): about 2.1 GB for the 252 MB cartridge window, and another 2.1 GB for the dummy cartridge that
// it replaces, so about 6 GB per session. Almost every 4 KB page is either untouched (the default value) or a
// linear run (base + 4*i). Those pages are kept as (base, stride, fill). Only mixed pages (MMIO register blocks) get
// a 1024-entry array. Lookups return exactly what the flat table returned.
// COR64_MEM_PAGEDTABLES=0 gives every page its array at construction, as much memory as the flat tables (A/B control).
namespace cor64.IO
{
    internal static class WordTablesCfg
    {
        public const int Shift = 10, PageWords = 1 << Shift, Mask = PageWords - 1;
        public static readonly bool Paged = Environment.GetEnvironmentVariable("COR64_MEM_PAGEDTABLES") != "0";
    }

    /// <summary>Per-word host pointer table. A page without an array reads as off &lt; fill ? base + off*stride : default.</summary>
    internal sealed class PtrWordTable
    {
        private readonly nint m_Default;
        private readonly nint[] m_Base;
        private readonly int[] m_Fill;     // words [0, fill) follow base + off*stride; the rest are the default
        private readonly int[] m_Stride;   // 0 or 4; -1 = only one word placed, stride not known yet
        private readonly nint[][] m_Sub;
        public readonly int Length;

        public PtrWordTable(int words, nint dflt)
        {
            Length = words; m_Default = dflt;
            int pages = (words + WordTablesCfg.Mask) >> WordTablesCfg.Shift;
            m_Base = new nint[pages]; m_Fill = new int[pages]; m_Stride = new int[pages]; m_Sub = new nint[pages][];
            if (!WordTablesCfg.Paged) for (int p = 0; p < pages; p++) Materialize(p);
        }

        public nint this[int i]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get {
                int p = i >> WordTablesCfg.Shift, off = i & WordTablesCfg.Mask;
                var s = m_Sub[p];
                if (s != null) return s[off];
                return off < m_Fill[p] ? m_Base[p] + (nint)off * m_Stride[p] : m_Default;
            }
            set {
                int p = i >> WordTablesCfg.Shift, off = i & WordTablesCfg.Mask;
                var s = m_Sub[p];
                if (s != null) { s[off] = value; return; }
                int f = m_Fill[p];
                if (off < f) { if (m_Base[p] + (nint)off * m_Stride[p] == value) return; }
                else if (off == f) {
                    if (f == 0) { if (value == m_Default) return; m_Base[p] = value; m_Stride[p] = -1; m_Fill[p] = 1; return; }
                    if (m_Stride[p] == -1) {
                        if (f == 1 && value == m_Base[p] + 4) { m_Stride[p] = 4; m_Fill[p] = 2; return; }
                        if (f == 1 && value == m_Base[p]) { m_Stride[p] = 0; m_Fill[p] = 2; return; }
                    }
                    else if (value == m_Base[p] + (nint)off * m_Stride[p]) { m_Fill[p] = f + 1; return; }
                }
                else if (value == m_Default) return;
                Materialize(p)[off] = value;
            }
        }

        private nint[] Materialize(int p)
        {
            var s = new nint[WordTablesCfg.PageWords];
            int f = m_Fill[p], st = m_Stride[p] < 0 ? 0 : m_Stride[p];
            for (int k = 0; k < s.Length; k++) s[k] = k < f ? m_Base[p] + (nint)k * st : m_Default;
            m_Fill[p] = 0;
            return m_Sub[p] = s;
        }

        /// <summary>Base pointer when entries [i0, i0+n) are one contiguous run (t[i+1] == t[i] + 4), else 0.</summary>
        public nint LinearRun(int i0, int n)
        {
            nint b = this[i0], want = b;
            int i = i0, end = i0 + n;
            while (i < end) {
                int p = i >> WordTablesCfg.Shift, off = i & WordTablesCfg.Mask;
                int stop = Math.Min(end, (p + 1) << WordTablesCfg.Shift);
                var s = m_Sub[p];
                if (s == null && m_Stride[p] == 4 && off < m_Fill[p] && stop - (p << WordTablesCfg.Shift) <= m_Fill[p]) {
                    if (m_Base[p] + (nint)off * 4 != want) return 0;
                    want += (nint)(stop - i) * 4; i = stop; continue;
                }
                for (; i < stop; i++, want += 4) if (this[i] != want) return 0;
            }
            return b;
        }
    }

    /// <summary>Per-word table of a reference or value type whose pages are mostly one repeated value.</summary>
    internal sealed class UniformWordTable<T>
    {
        private readonly T[] m_Val;
        private readonly T[][] m_Sub;
        public UniformWordTable(int words)
        {
            int pages = (words + WordTablesCfg.Mask) >> WordTablesCfg.Shift;
            m_Val = new T[pages]; m_Sub = new T[pages][];
            if (!WordTablesCfg.Paged) for (int p = 0; p < pages; p++) m_Sub[p] = new T[WordTablesCfg.PageWords];
        }
        public T this[int i]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { var s = m_Sub[i >> WordTablesCfg.Shift]; return s != null ? s[i & WordTablesCfg.Mask] : m_Val[i >> WordTablesCfg.Shift]; }
            set {
                int p = i >> WordTablesCfg.Shift; var s = m_Sub[p];
                if (s == null) {
                    if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(m_Val[p], value)) return;
                    s = m_Sub[p] = new T[WordTablesCfg.PageWords]; Array.Fill(s, m_Val[p]);
                }
                s[i & WordTablesCfg.Mask] = value;
            }
        }
    }
}
