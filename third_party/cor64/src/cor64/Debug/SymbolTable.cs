// [dbg_disasm] Symbol table for the debugger: GNU ld map files, ELF .symtab (32/64-bit, either endian) and plain
// "addr name" lists. Lookup by address gives "name" or "name+0x1C".
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace cor64.Debug
{
    public readonly struct Symbol
    {
        public readonly ulong Address; public readonly uint Size; public readonly string Name;
        public Symbol(ulong address, string name, uint size = 0) { Address = address; Name = name; Size = size; }
        public override string ToString() => $"{Address:X8} {Name}";
    }

    public sealed class SymbolTable
    {
        readonly List<Symbol> m_Syms = new List<Symbol>();
        readonly Dictionary<string, ulong> m_ByName = new Dictionary<string, ulong>(StringComparer.Ordinal);
        bool m_Sorted = true;

        /// <summary>Unsized symbols match at most this far past their start.</summary>
        public uint MaxUnsizedOffset { get; set; } = 0x10000;
        public int Count => m_Syms.Count;
        public IReadOnlyList<Symbol> All { get { Sort(); return m_Syms; } }

        /// <summary>Addresses are normalised to the low 32 bits sign-extended (KSEG0 0x80000000 == 0xFFFFFFFF80000000).</summary>
        public static ulong Norm(ulong a) => (ulong)(long)(int)(uint)a;

        public void Add(ulong address, string name, uint size = 0)
        {
            if (string.IsNullOrEmpty(name)) return;
            address = Norm(address);
            m_Syms.Add(new Symbol(address, name, size)); m_Sorted = false;
            m_ByName[name] = address;
        }

        public bool TryGetAddress(string name, out ulong address) => m_ByName.TryGetValue(name, out address);

        void Sort()
        {
            if (m_Sorted) return;
            m_Syms.Sort((a, b) => a.Address != b.Address ? a.Address.CompareTo(b.Address) : b.Size.CompareTo(a.Size));
            m_Sorted = true;
        }

        /// <summary>The nearest symbol at or below <paramref name="address"/>, within its size (or MaxUnsizedOffset).</summary>
        public bool TryLookup(ulong address, out Symbol sym, out ulong offset)
        {
            Sort(); address = Norm(address); sym = default; offset = 0;
            int lo = 0, hi = m_Syms.Count - 1, best = -1;
            while (lo <= hi) { int mid = (lo + hi) >> 1; if (m_Syms[mid].Address <= address) { best = mid; lo = mid + 1; } else hi = mid - 1; }
            if (best < 0) return false;
            // prefer the first entry at that address (sorted with the largest size first)
            ulong a = m_Syms[best].Address;
            while (best > 0 && m_Syms[best - 1].Address == a) best--;
            var s = m_Syms[best]; offset = address - s.Address;
            if (s.Size != 0 ? offset >= s.Size : offset > MaxUnsizedOffset) return false;
            sym = s; return true;
        }

        /// <summary>"name" or "name+0x1C"; null when no symbol covers the address.</summary>
        public string Label(ulong address)
        {
            if (!TryLookup(address, out var s, out var off)) return null;
            return off == 0 ? s.Name : s.Name + "+0x" + off.ToString("X");
        }

        public AddressLabeler Labeler => Label;

        // ---------------------------------------------------------------- loaders

        static readonly Regex s_Ident = new Regex(@"^[A-Za-z_.$][\w.$@]*$", RegexOptions.Compiled);

        static bool ParseHex(string t, out ulong v)
        {
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t.Substring(2);
            v = 0;
            return t.Length > 0 && t.Length <= 16 && ulong.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v);
        }

        /// <summary>Plain list ("80246000 main", "0x80246000 main", "main = 0x80246000;") or a GNU ld map file
        /// (lines "                0x0000000080246000                main"). Section/file lines are skipped.</summary>
        public int LoadText(string text)
        {
            int n = 0;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line.StartsWith("//")) continue;
                var tok = line.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
                ulong addr; string name = null;
                if (tok.Length >= 3 && tok[1] == "=" && ParseHex(tok[2].TrimEnd(';'), out addr)) name = tok[0];
                else if (tok.Length == 2 && ParseHex(tok[0], out addr)) name = tok[1];
                else if (tok.Length == 3 && ParseHex(tok[0], out addr) && tok[1].Length == 1) name = tok[2]; // nm: "80246000 T main"
                else continue;
                if (!s_Ident.IsMatch(name) || name == ".") continue;
                if (name.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) continue; // map size column
                Add(addr, name); n++;
            }
            return n;
        }

        /// <summary>ELF .symtab (FUNC / OBJECT / NOTYPE symbols with a non-zero value).</summary>
        public int LoadElf(byte[] f)
        {
            if (f.Length < 0x34 || f[0] != 0x7F || f[1] != 'E' || f[2] != 'L' || f[3] != 'F') throw new InvalidDataException("not an ELF file");
            bool is64 = f[4] == 2, be = f[5] == 2;
            ulong U(long o, int sz) { ulong v = 0; for (int i = 0; i < sz; i++) v = be ? (v << 8) | f[o + i] : v | ((ulong)f[o + i] << (8 * i)); return v; }
            long shoff = (long)U(is64 ? 0x28 : 0x20, is64 ? 8 : 4);
            int shentsize = (int)U(is64 ? 0x3A : 0x2E, 2), shnum = (int)U(is64 ? 0x3C : 0x30, 2);
            int n = 0;
            for (int i = 0; i < shnum; i++)
            {
                long sh = shoff + (long)i * shentsize;
                if ((uint)U(sh + 4, 4) != 2) continue; // SHT_SYMTAB
                long off = (long)U(sh + (is64 ? 0x18 : 0x10), is64 ? 8 : 4), size = (long)U(sh + (is64 ? 0x20 : 0x14), is64 ? 8 : 4);
                int link = (int)U(sh + (is64 ? 0x28 : 0x18), 4);
                long ent = (long)U(sh + (is64 ? 0x38 : 0x24), is64 ? 8 : 4); if (ent == 0) ent = is64 ? 24 : 16;
                long strsh = shoff + (long)link * shentsize, stroff = (long)U(strsh + (is64 ? 0x18 : 0x10), is64 ? 8 : 4);
                for (long s = off; s + ent <= off + size; s += ent)
                {
                    uint nameOff = (uint)U(s, 4); ulong val; uint sz; int info;
                    if (is64) { info = f[s + 4]; val = U(s + 8, 8); sz = (uint)U(s + 16, 8); }
                    else { val = U(s + 4, 4); sz = (uint)U(s + 8, 4); info = f[s + 12]; }
                    int type = info & 15;
                    if (type > 2 || val == 0 || nameOff == 0) continue; // NOTYPE, OBJECT, FUNC only
                    long p = stroff + nameOff; int e = (int)p; while (e < f.Length && f[e] != 0) e++;
                    var name = Encoding.ASCII.GetString(f, (int)p, e - (int)p);
                    if (name.Length == 0 || name.StartsWith("$")) continue;
                    Add(val, name, sz); n++;
                }
            }
            return n;
        }

        /// <summary>Load by sniffing: ELF magic, else text (map or plain list).</summary>
        public int LoadFile(string path)
        {
            var b = File.ReadAllBytes(path);
            if (b.Length >= 4 && b[0] == 0x7F && b[1] == 'E' && b[2] == 'L' && b[3] == 'F') return LoadElf(b);
            return LoadText(Encoding.UTF8.GetString(b));
        }
    }
}
