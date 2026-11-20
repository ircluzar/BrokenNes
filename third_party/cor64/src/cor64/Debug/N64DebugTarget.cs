// [dbg_disasm] IDebugTarget over an N64Session. Memory goes through the system bus as aligned 32-bit reads of
// RAM-like regions only (RDRAM, SP DMEM/IMEM, cart ROM, PIF RAM), so no MMIO register is touched and reads have no
// side effects. Call only between RunFrame calls (the CPU state is committed then).
// [dbg_mem] Reads now peek the device tables directly (no RDP wait, PI latch, log or watch); adds the VIRTUAL domain
// (TLB walk without side effects), read-only MMIO register domains, SetRegister, Search and mirror-aware watches
// (MemoryWatch.cs). A write into RDRAM drops the CPU's decode cache and compiled (IL and x64) blocks; a write into
// IMEM bumps the RSP IMEM generation, so patched code runs as written on the next frame.
using System;
using System.Collections.Generic;
using cor64.IO;
using cor64.Session;

namespace cor64.Debug
{
    public sealed class N64DebugTarget : IDebugMemoryEx, IDebugRsp
    {
        readonly N64Session m_Session;
        readonly MemoryDomain[] m_Domains;
        readonly Dictionary<string, (MemoryDomain d, uint phys)> m_Map = new Dictionary<string, (MemoryDomain, uint)>(StringComparer.OrdinalIgnoreCase);
        static readonly string[] s_Cpus = { "cpu", "rsp" };
        readonly MemoryWatchSet m_WatchSet;

        public const string VirtualDomain = "VIRTUAL";
        // [dbg_mem] RCP register blocks (read-only domains; reading the device tables has no side effects)
        static readonly (string name, uint phys, ulong size)[] s_Mmio = {
            ("SP", 0x04040000, 0x20), ("SPPC", 0x04080000, 0x8), ("DP", 0x04100000, 0x20), ("MI", 0x04300000, 0x10),
            ("VI", 0x04400000, 0x38), ("AI", 0x04500000, 0x18), ("PI", 0x04600000, 0x34), ("RI", 0x04700000, 0x20),
            ("SI", 0x04800000, 0x1C),
        };

        public SymbolTable Symbols { get; } = new SymbolTable();
        public string SystemName => "N64";
        public IReadOnlyList<string> Cpus => s_Cpus;
        public IReadOnlyList<MemoryDomain> Domains => m_Domains;
        public MemoryWatchSet WatchSet => m_WatchSet;

        /// <param name="romSize">Cart ROM size for the "ROM" domain (the session does not keep the image).</param>
        public N64DebugTarget(N64Session session, ulong romSize = 0x4000000)
        {
            m_Session = session ?? throw new ArgumentNullException(nameof(session));
            ulong rdram = session.System.ExpansionPak ? 0x800000UL : 0x400000UL;
            var d = new List<MemoryDomain> {
                new MemoryDomain("RDRAM", rdram, true, "cpu", 0x80000000),
                new MemoryDomain("DMEM", 0x1000, true, null, 0x04000000),
                new MemoryDomain("IMEM", 0x1000, true, "rsp", 0x04001000),
                new MemoryDomain("ROM", Math.Min(romSize, 0xFC00000UL), false, "cpu", 0xB0000000),
                new MemoryDomain("PIFRAM", 0x40, false, null, 0x1FC007C0),
                new MemoryDomain(VirtualDomain, 0x100000000UL, true, "cpu", 0),
            };
            var phys = new List<uint> { 0, 0x04000000, 0x04001000, 0x10000000, 0x1FC007C0, 0 };
            foreach (var m in s_Mmio) { d.Add(new MemoryDomain(m.name, m.size, false, null, m.phys)); phys.Add(m.phys); }
            m_Domains = d.ToArray();
            for (int i = 0; i < m_Domains.Length; i++) m_Map[m_Domains[i].Name] = (m_Domains[i], phys[i]);
            m_WatchSet = new MemoryWatchSet(Bus) { PcSource = () => (ulong)m_Session.System.DeviceCPU.ReadPC() };
        }

        N64MemoryController Bus => m_Session.System.SystemMemory;
        cor64.Mips.R4300I.Interpreter Cpu => m_Session.System.DeviceCPU as cor64.Mips.R4300I.Interpreter;

        uint Read32(uint phys) => (uint)Bus.DebugPeek(phys, 4);

        static bool IsRam(uint p) => p < 0x00800000 || (p >= 0x04000000 && p < 0x04002000);

        // physical address of a domain offset; false = unmapped (VIRTUAL only)
        bool Resolve((MemoryDomain d, uint phys) e, ulong o, out uint p)
        {
            if (e.d.Name == VirtualDomain) return TryTranslate(o, out p) && p < 0x20000000;
            p = e.phys + (uint)o; return true;
        }

        public int ReadMemory(string domain, ulong offset, Span<byte> dest)
        {
            if (!m_Map.TryGetValue(domain, out var e) || offset >= e.d.Size) return 0;
            int n = (int)Math.Min((ulong)dest.Length, e.d.Size - offset);
            for (int i = 0; i < n;)
            {
                ulong o = offset + (ulong)i;
                if (!Resolve(e, o & ~3UL, out uint a)) return i;
                uint w = Read32(a);
                for (int b = (int)(o & 3); b < 4 && i < n; b++, i++) dest[i] = (byte)(w >> (24 - 8 * b));
            }
            return n;
        }

        /// <summary>Writes RAM (RDRAM, DMEM, IMEM, or VIRTUAL addresses that land there); stops at anything else.</summary>
        public int WriteMemory(string domain, ulong offset, ReadOnlySpan<byte> src)
        {
            if (!m_Map.TryGetValue(domain, out var e) || !e.d.Writable || offset >= e.d.Size) return 0;
            int n = (int)Math.Min((ulong)src.Length, e.d.Size - offset);
            bool code = false, imem = false;
            int i = 0;
            while (i < n)
            {
                ulong o = offset + (ulong)i;
                if (!Resolve(e, o & ~3UL, out uint a) || !IsRam(a)) break;
                var dev = Bus.PeekDevice(a, out long off);
                if (dev == null) break;
                uint w = Read32(a);
                for (int b = (int)(o & 3); b < 4 && i < n; b++, i++)
                {
                    int sh = 24 - 8 * b; w = (w & ~(0xFFu << sh)) | ((uint)src[i] << sh);
                }
                dev.Write32((int)off, N64Endianess.U32(w));
                if (a < 0x00800000) code = true; else if (a >= 0x04001000) imem = true;
            }
            if (code) InvalidateCpuCode();
            if (imem) Bus.SPRegs.ImemGeneration++;
            return i;
        }

        /// <summary>Drops the CPU's decoded and compiled (IL and x64) blocks, so code patched in RDRAM runs as written.</summary>
        public void InvalidateCpuCode() => m_Session.System.DeviceCPU.InvalidateDecodeCache();

        /// <summary>CPU virtual address to physical for unmapped segments (KSEG0/KSEG1); false for TLB-mapped VAs.</summary>
        public static bool TryVirtToPhys(ulong va, out uint phys)
        {
            uint v = (uint)va; phys = v & 0x1FFFFFFF;
            return v >= 0x80000000u && v < 0xC0000000u;
        }

        public bool TryTranslate(ulong va, out uint phys)
        {
            phys = 0;
            var cpu = Cpu;
            if (cpu == null) return TryVirtToPhys(va, out phys);
            if (!cpu.DebugTranslate((long)(uint)va, out long p)) return false;
            phys = (uint)p; return true;
        }

        public IReadOnlyList<ulong> Search(string domain, ReadOnlySpan<byte> pattern, ReadOnlySpan<byte> mask, ulong start = 0, ulong length = ulong.MaxValue, int maxResults = 1024)
        {
            var res = new List<ulong>();
            if (pattern.Length == 0 || !m_Map.TryGetValue(domain, out var e) || start >= e.d.Size) return res;
            if (mask.Length != 0 && mask.Length != pattern.Length) throw new ArgumentException("mask length must match pattern");
            ulong end = length >= e.d.Size - start ? e.d.Size : start + length;
            int pl = pattern.Length;
            const int Chunk = 0x10000;
            var buf = new byte[Chunk + pl];
            ulong pos = start;
            while (pos < end && res.Count < maxResults)
            {
                int want = (int)Math.Min((ulong)(Chunk + pl - 1), end - pos);
                int got = ReadMemory(domain, pos, buf.AsSpan(0, want));
                if (got == 0) { pos = (pos | 0xFFF) + 1; continue; } // unmapped virtual page
                int starts = Math.Min(got, Chunk);
                for (int k = 0; k < starts && k + pl <= got && res.Count < maxResults; k++)
                {
                    bool ok = true;
                    for (int j = 0; j < pl; j++)
                    {
                        byte m = mask.Length == 0 ? (byte)0xFF : mask[j];
                        if (((buf[k + j] ^ pattern[j]) & m) != 0) { ok = false; break; }
                    }
                    if (ok) res.Add(pos + (ulong)k);
                }
                pos += (ulong)starts;
            }
            return res;
        }

        public int AddWatch(ulong address, int length, WatchKind kind, WatchCondition condition = WatchCondition.Any, ulong value = 0, ulong mask = ulong.MaxValue)
        {
            uint a = (uint)address, phys;
            if (a < 0x20000000u) phys = a;
            else if (!TryTranslate(a, out phys)) return -1;
            return m_WatchSet.Add(address, phys, length, kind, condition, value, mask).Id;
        }
        public bool RemoveWatch(int id) => m_WatchSet.Remove(id);
        public void ClearWatches() => m_WatchSet.Clear();
        public IReadOnlyList<MemoryWatch> Watches => m_WatchSet.Watches;
        public IReadOnlyList<WatchHit> DrainWatchHits() => m_WatchSet.Drain();

        bool TryReadCodeWord(string cpu, ulong address, out uint word)
        {
            word = 0;
            if (cpu == "rsp") { Span<byte> b = stackalloc byte[4]; if (ReadMemory("IMEM", address & 0xFFC, b) != 4) return false; word = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]); return true; }
            if (!TryVirtToPhys(address, out var p)) return false;
            if (p < 0x03F00000 || (p >= 0x04000000 && p < 0x04002000) || (p >= 0x10000000 && p < 0x1FC00000)) { word = Read32(p & ~3u); return true; }
            return false;
        }

        public IReadOnlyList<DisasmLine> Disassemble(string cpu, ulong address, int count)
        {
            var list = new List<DisasmLine>(count);
            bool rsp = cpu == "rsp";
            AddressLabeler lab = Symbols.Count > 0 ? Symbols.Labeler : null;
            for (int i = 0; i < count; i++)
            {
                ulong a = rsp ? (address + (ulong)i * 4) & 0xFFC : address + (ulong)i * 4;
                string text; uint w = 0;
                if (!TryReadCodeWord(cpu, a, out w)) text = "??";
                else text = rsp ? RspDisassembler.Disassemble(w, (uint)a, lab) : R4300Disassembler.Disassemble(w, a, lab);
                string label = rsp ? null : Symbols.TryLookup(a, out var s, out var off) && off == 0 ? s.Name : null;
                list.Add(new DisasmLine(a, w, text, label));
            }
            return list;
        }

        public ulong GetPC(string cpu)
        {
            if (cpu == "rsp") return (ulong)m_Session.System.DeviceRcp.DeviceRsp.ReadPC() & 0xFFC;
            return (ulong)(long)(int)(uint)m_Session.System.DeviceCPU.ReadPC(); // [brokennes_int3] sign-extended, as the GPRs and N64Debugger hold it
        }

        public IReadOnlyList<RegisterInfo> GetRegisters(string cpu)
        {
            var r = new List<RegisterInfo>(80);
            if (cpu == "rsp")
            {
                var rsp = m_Session.System.DeviceRcp.DeviceRsp;
                for (int i = 0; i < 32; i++) r.Add(new RegisterInfo(MipsNames.Gpr[i], 32, rsp.ReadGPR(i)));
                r.Add(new RegisterInfo("pc", 32, GetPC("rsp")));
                var vs = GetRspState(); // [dbg_rsp] vector unit scalars; the vector lanes are "vN.e" (TryGetRegister / SetRegister)
                r.Add(new RegisterInfo("vco", 16, vs.Vco)); r.Add(new RegisterInfo("vcc", 16, vs.Vcc)); r.Add(new RegisterInfo("vce", 8, vs.Vce));
                r.Add(new RegisterInfo("divin", 32, (uint)vs.DivIn)); r.Add(new RegisterInfo("divout", 32, (uint)vs.DivOut)); r.Add(new RegisterInfo("dph", 1, vs.DivDoublePrecision ? 1UL : 0));
                for (int i = 0; i < 8; i++) r.Add(new RegisterInfo("acc" + i, 48, vs.Acc[i]));
                r.Add(new RegisterInfo("sp_status", 32, vs.SpStatus)); r.Add(new RegisterInfo("semaphore", 32, vs.Semaphore));
                return r;
            }
            if (cpu != "cpu") return r;
            var c = m_Session.System.DeviceCPU; var st = c.StateR4000I;
            for (int i = 0; i < 32; i++) r.Add(new RegisterInfo(MipsNames.Gpr[i], 64, st.GetGpr64(i)));
            r.Add(new RegisterInfo("pc", 64, GetPC("cpu")));
            r.Add(new RegisterInfo("hi", 64, st.GetHi()));
            r.Add(new RegisterInfo("lo", 64, st.GetLo()));
            for (int i = 0; i < 32; i++) r.Add(new RegisterInfo(MipsNames.Cop0[i], 64, c.Cop0State.Read(i)));
            for (int i = 0; i < 32; i++) r.Add(new RegisterInfo(MipsNames.Fpr(i), 64, st.FPR.ReadScalarDword(i)));
            r.Add(new RegisterInfo("fcr31", 32, st.FCR.Value));
            return r;
        }

        public bool TryGetRegister(string cpu, string name, out ulong value)
        {
            if (cpu == "rsp" && TryLane(name, out int vr, out int ve) && Rsp is cor64.Mips.Rsp.RspInterpreter ri) { value = ri.m_VecRegs[vr].PackedU16(ve); return true; }
            foreach (var reg in GetRegisters(cpu)) if (string.Equals(reg.Name, name, StringComparison.OrdinalIgnoreCase)) { value = reg.Value; return true; }
            value = 0; return false;
        }

        public bool SetRegister(string cpu, string name, ulong value)
        {
            if (string.IsNullOrEmpty(name)) return false;
            int gi = Array.FindIndex(MipsNames.Gpr, g => string.Equals(g, name, StringComparison.OrdinalIgnoreCase));
            bool isPc = string.Equals(name, "pc", StringComparison.OrdinalIgnoreCase);
            if (cpu == "rsp")
            {
                var rsp = m_Session.System.DeviceRcp.DeviceRsp;
                if (TryLane(name, out int vr, out int ve))
                {
                    if (!(rsp is cor64.Mips.Rsp.RspInterpreter ri)) return false;
                    ri.m_VecRegs[vr].PackedU16(ve, (ushort)value); return true;
                }
                if (gi == 0) return true; // $zero: write ignored, as on hardware
                if (gi > 0) { rsp.WriteGPR(gi, (uint)value); return true; }
                if (isPc) { uint pc = (uint)value & 0xFFC; rsp.SafeSetPC(pc); rsp.Interface.SetPC(pc); return true; }
                return false;
            }
            if (cpu != "cpu") return false;
            var c = m_Session.System.DeviceCPU; var st = c.StateR4000I;
            if (gi == 0) return true;
            if (gi > 0) { st.SetGpr64(gi, value); return true; }
            if (isPc) { var ip = Cpu; if (ip != null) ip.DebugSetPC((long)value); else c.SafeSetPC((long)value); return true; }
            if (string.Equals(name, "hi", StringComparison.OrdinalIgnoreCase)) { st.SetHi(value); return true; }
            if (string.Equals(name, "lo", StringComparison.OrdinalIgnoreCase)) { st.SetLo(value); return true; }
            if (string.Equals(name, "fcr31", StringComparison.OrdinalIgnoreCase)) { st.FCR.Value = (uint)value; return true; }
            for (int i = 0; i < 32; i++)
            {
                if (string.Equals(MipsNames.Cop0[i], name, StringComparison.OrdinalIgnoreCase)) { c.Cop0State.Write(i, value); return true; }
                if (string.Equals(MipsNames.Fpr(i), name, StringComparison.OrdinalIgnoreCase)) { st.FPR.WriteScalarDword(i, value); return true; }
            }
            return false;
        }
    
        // ---------------- [dbg_rsp] RSP view ----------------
        cor64.Mips.Rsp.InterpreterBaseRsp Rsp => m_Session.System.DeviceRcp.DeviceRsp;

        /// <summary>"v3.5" / "v3[5]" / "v3e5": vector register 3, lane 5.</summary>
        public static bool TryLane(string name, out int reg, out int lane)
        {
            reg = lane = -1;
            if (string.IsNullOrEmpty(name) || (name[0] != 'v' && name[0] != 'V')) return false;
            var s = name.Substring(1).Replace("[", ".").Replace("]", "").Replace("e", ".").Replace("E", ".");
            int dot = s.IndexOf('.');
            if (dot <= 0 || !int.TryParse(s.Substring(0, dot), out reg) || !int.TryParse(s.Substring(dot + 1), out lane)) { reg = lane = -1; return false; }
            if (reg < 0 || reg > 31 || lane < 0 || lane > 7) { reg = lane = -1; return false; }
            return true;
        }

        uint Dmem(uint o) => Read32(0x04000000 + o);

        public OSTaskInfo GetOSTask() => OSTaskInfo.Decode(i => Dmem(0xFC0 + (uint)i * 4));

        public RspState GetRspState()
        {
            var st = new RspState { Pc = (uint)GetPC("rsp"), SpStatus = Read32(0x04040010), Semaphore = Read32(0x0404001C), Task = GetOSTask() };
            var rsp = Rsp;
            st.Vco = rsp.ReadVCO(); st.Vcc = rsp.ReadVCC(); st.Vce = rsp.ReadVCE();
            for (int i = 0; i < 8; i++) st.Acc[i] = rsp.Acc[i] & 0xFFFFFFFFFFFFUL;
            if (rsp is cor64.Mips.Rsp.RspInterpreter ri)
            {
                for (int r = 0; r < 32; r++) { var v = new ushort[8]; for (int e = 0; e < 8; e++) v[e] = ri.m_VecRegs[r].PackedU16(e); st.V[r] = v; }
                st.DivIn = ri.DebugDivIn; st.DivOut = ri.DebugDivOut; st.DivDoublePrecision = ri.DebugDivDoublePrecision;
            }
            else for (int r = 0; r < 32; r++) st.V[r] = new ushort[8];
            return st;
        }

        public UcodeInfo CurrentUcode()
        {
            var t = GetOSTask();
            if (t.Type == 0 && t.Ucode == 0) return null;
            return UcodeTable.Identify(t, UcodeTable.TextCrc(t, Read32), Read32);
        }

        public bool SetRspVector(int r, ReadOnlySpan<ushort> lanes)
        {
            if (r < 0 || r > 31 || lanes.Length != 8 || !(Rsp is cor64.Mips.Rsp.RspInterpreter ri)) return false;
            for (int e = 0; e < 8; e++) ri.m_VecRegs[r].PackedU16(e, lanes[e]);
            return true;
        }
    }
}
