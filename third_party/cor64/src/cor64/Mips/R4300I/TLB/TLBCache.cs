using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using NLog;

namespace cor64.Mips.R4300I.TLB
{
    /// <summary>[block_link] Bumped whenever a quiet fetch translation may change (TLB written or reset, EntryHi/ASID
    /// written, savestate loaded). Native block links into TLB-mapped code hold the value they were made under.
    /// Shared by all emulator instances (only ever increases, so another instance's bump just drops links).</summary>
    public static unsafe class MapEpoch
    {
        public static readonly long* Ptr = Alloc(1), Never = Alloc(-1);
        private static long* Alloc(long v) { var p = (long*)System.Runtime.InteropServices.NativeMemory.Alloc(8); *p = v; return p; }
        public static void Bump() => (*Ptr)++;
    }

    public class TLBCache
    {
        private readonly static Logger Log = LogManager.GetCurrentClassLogger();
        private readonly SystemController m_Cop0;
        private readonly TLBRegisters m_Registers;
        private readonly TLBEntry[] m_Entries = new TLBEntry[32];
        private Boolean m_DisableTLB = false; // Used to simulate a real TLB shutdown
        private readonly Dictionary<uint, int> m_EntryHashLookup = new Dictionary<uint, int>();

        // [ge_speed] COR64_GE_SPEED_TLBCACHE=0: no translation cache (every mapped access scans the 32 entries).
        // A direct-mapped cache of successful translations per 4 KB virtual page. The key holds the page, the ASID and a
        // generation that every TLB write, init and savestate load bumps, so a hit is exactly what the scan would return
        // (the scan's result is the same for every address in one 4 KB page: page masks are >= 4 KB). Only valid pages
        // are cached; a store needs the cached dirty bit, else it takes the scan (which raises TLB Mod as before).
        public static readonly bool s_Fc = Environment.GetEnvironmentVariable("COR64_GE_SPEED_TLBCACHE") != "0";
        private const int FcBits = 10;
        // [session_leak] the tables live in native memory so native CPU blocks can probe them inline (COR64_TLB_INLINE):
        // FcKeyHi holds (ASID << 20) | (generation << 32), refreshed by the CPU before each native block call.
        [NonSerialized] private unsafe ulong* m_FcTag;
        [NonSerialized] private unsafe uint* m_FcVal; // physical page base | 1 = dirty
        [NonSerialized] private unsafe ulong* m_FcKeyHi;
        [NonSerialized] private uint m_FcGen;
        public unsafe ulong* FcTagPtr { get { FcEnsure(); return m_FcTag; } }
        public unsafe uint* FcValPtr { get { FcEnsure(); return m_FcVal; } }
        public unsafe ulong* FcKeyHiPtr { get { FcEnsure(); return m_FcKeyHi; } }
        public const int FcMask = (1 << FcBits) - 1;
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public unsafe void FcUpdateKeyHi() => *m_FcKeyHi = ((ulong)m_Registers.RegEntryHi.AsidFast << 20) | ((ulong)m_FcGen << 32);
        private unsafe void FcEnsure()
        {
            if (m_FcTag != null) return;
            // never freed: native blocks of this machine may hold these addresses (a few KB per session)
            m_FcTag = (ulong*)System.Runtime.InteropServices.NativeMemory.AllocZeroed((nuint)(8 << FcBits));
            m_FcVal = (uint*)System.Runtime.InteropServices.NativeMemory.AllocZeroed((nuint)(4 << FcBits));
            m_FcKeyHi = (ulong*)System.Runtime.InteropServices.NativeMemory.AllocZeroed(8);
            if (m_FcGen == 0) m_FcGen = 1;
        }

        /// <summary>[ge_speed] Drops every cached translation (TLB written, reset, savestate loaded).</summary>
        public unsafe void InvalidateFastCache() { MapEpoch.Bump(); m_FcGen++; if (m_FcGen == 0) { m_FcGen = 1; if (m_FcTag != null) System.Runtime.InteropServices.NativeMemory.Clear(m_FcTag, (nuint)(8 << FcBits)); } }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private ulong FcKey(uint va) => (va >> 12) | ((ulong)m_Registers.RegEntryHi.AsidFast << 20) | ((ulong)m_FcGen << 32);

        private unsafe void FcPut(uint va, long phys, bool dirty)
        {
            if ((ulong)phys >= 0x100000000UL) return; // the cache holds 32-bit physical pages only
            FcEnsure();
            int i = (int)((va >> 12) & ((1 << FcBits) - 1));
            m_FcTag[i] = FcKey(va); m_FcVal[i] = ((uint)phys & 0xFFFFF000u) | (dirty ? 1u : 0u);
        }

        public TLBCache(SystemController systemController)
        {
            m_Cop0 = systemController;
            m_Registers = systemController.REGS.TLBRegs;
            if (s_Fc) FcEnsure(); // [session_leak] allocated up front: background compilers read the table addresses

            for (int i = 0; i < m_Entries.Length; i++)
            {
                m_Entries[i] = new TLBEntry();
            }
        }

        public void Initialize() {
            InvalidateFastCache();
            m_Registers.Reset();
        }

        public void Tick() {
            m_Registers.UpdateRandom();
        }

        public void Tick(long steps) => m_Registers.UpdateRandom(steps);

        private uint Key(uint pageMask, uint vpn2, uint asid, bool global) {
            if (global) {
                return (vpn2 ^ 0x100);
            }
            else {
                return (vpn2 ^ asid);
            }
        }

        /// <summary>
        /// If successful, the probe returns the entry index matching the VPN2 / ASID values in EntryHi.
        /// </summary>
        /// <remarks>
        /// Ref: MIPS R4300I Manual Page 637
        /// Ref: MIPS R4300I Manual Page 115
        /// </remarks>
        public virtual void Probe()
        {
            if (m_DisableTLB) return;

            m_Registers.ProbeFail = false;

            for (int i = 0; i < m_Entries.Length; i++) {
                var entry = m_Entries[i];

                uint vpn2 = m_Registers.RegEntryHi.VPN2 & VpnKeep(entry.Mask);

                bool regionMatch = true;

                // Console.WriteLine("Entry {0}: {1}", i, entry.ToString());
                // Console.WriteLine("EntryHi {0}", m_Registers.RegEntryHi.ToString());

                if (m_Cop0.IsOperation64) {
                    regionMatch = m_Registers.RegEntryHi.Region == entry.EntryHi.Region;
                }

                if (m_Registers.RegEntryHi.ASID == entry.ASID) {
                    if (vpn2 == entry.EntryHi.VPN2 && regionMatch) {
                        // Console.WriteLine("Match ASID: {0} {1}", i, entry.ToString());
                        m_Registers.Index = i;
                        return;
                    }
                }
                else {
                    if (entry.IsGlobal && regionMatch) {
                        if (vpn2 == entry.EntryHi.VPN2) {
                            // Console.WriteLine("Match G: {0} {1}", i, entry.ToString());
                            m_Registers.Index = i;
                            return;
                        }
                    }
                }
            }

            m_Registers.Index = 0;
            m_Registers.ProbeFail = true;
        }

        /// <summary>
        /// Writes an entry to the TLB.
        /// Registers used: EntryHi, PageMask, EntryLo0, EntryLo1, Index, Random, Wired, BadVAddr
        /// </summary>
        // [catalog_fix2] COR64_CATFIX2_VPN2 (default on): the VPN2 bits kept under a page mask. The old form ~mask >> 13 is a
        // 32-bit shift, so it kept only VPN2 bits 18:0 (VA bits 31:13) and dropped bits 26:19 (VA 39:32), while Translate
        // compares all 27 bits of the sign-extended VA. Every entry for kseg2/kseg3 (VA C0000000+, sign-extended so VA 39:32
        // are ones) therefore never matched: TLBWI stored a truncated VPN2 and TLBP missed. =0 keeps the old truncation.
        // [catalog_fix2] COR64_CATFIX2_PHYSHI (default on): a TLB entry can map to a physical address at or above 0x80000000,
        // which nothing decodes (ares bus.hpp: reads 0, writes dropped). cor64's memory map ends below that and the access
        // threw (A Bug's Life / Toy Story 2 map kseg2 C0000000 to PFN 0x80000 and read it). Such an address is redirected
        // to the unmapped 0x04900000 range, which reads 0 and drops writes the same way.
        public static readonly bool s_PhysHi = Environment.GetEnvironmentVariable("COR64_CATFIX2_PHYSHI") != "0";
        public static readonly bool s_Vpn27 = Environment.GetEnvironmentVariable("COR64_CATFIX2_VPN2") != "0";
        private static uint VpnKeep(uint mask) => s_Vpn27 ? ~(mask >> 13) & 0x7FFFFFFu : ~mask >> 13;

        public virtual void Write(bool isRandomWrite)
        {
            if (m_DisableTLB) return;

            var index = isRandomWrite ? m_Registers.Random : m_Registers.Index;

            // Upper 32  or lower 32 entries

            int wired = m_Registers.Wired;

            if (wired > 32) {
                wired -= 32;
            }

            wired--;

            // [tlb_exc] Random always lies in [Wired, 31], so this test dropped every TLBWR; COR64_TLBEXC_TLBWR=0 keeps it.
            if (isRandomWrite && (index >= wired) && !s_TlbWr) return;

            var entry = new TLBEntry(
                m_Registers.PageMaskFixed,
                new EntryHiStore(
                    m_Registers.RegEntryHi.ASID,
                    m_Registers.RegEntryLo0.IsGlobal && m_Registers.RegEntryLo1.IsGlobal,
                    m_Registers.RegEntryHi.VPN2 & VpnKeep(m_Registers.PageMask),
                    m_Registers.RegEntryHi.Region
                ),
                new EntryLoStore(
                    m_Registers.RegEntryLo0.PageFrameNumber,
                    m_Registers.RegEntryLo0.IsDirty,
                    m_Registers.RegEntryLo0.IsValid,
                    m_Registers.RegEntryLo0.CoherencyMode
                ),
                new EntryLoStore(
                    m_Registers.RegEntryLo1.PageFrameNumber,
                    m_Registers.RegEntryLo1.IsDirty,
                    m_Registers.RegEntryLo1.IsValid,
                    m_Registers.RegEntryLo1.CoherencyMode
                )
            );

            #if DEBUG_TLB
            if (entry.IsValid)
                Console.WriteLine("Entry {0} set to {1}", index, entry.ToString());
            #endif

            #if FAST_TLB
            uint key = Key(entry.Mask, entry.EntryHi.VPN2, entry.ASID, entry.IsGlobal);

            foreach (KeyValuePair<uint, int> e in m_EntryHashLookup) {
                if (e.Value == index) {
                    m_EntryHashLookup.Remove(e.Key);
                }
            }

            if (entry.IsValid) m_EntryHashLookup.Add(key, index);
            #endif

            m_Entries[index] = entry;
            InvalidateFastCache();
            if (s_TlbLog) {
                System.IO.Directory.CreateDirectory("telemetry");
                System.IO.File.AppendAllText("telemetry/tlbw.txt", string.Format("{0} idx={1} hi={2:X16} pm={3:X8} lo0={4:X16} lo1={5:X16} -> {6}\n",
                    isRandomWrite ? "tlbwr" : "tlbwi", index, m_Registers.EntryHi, m_Registers.PageMask, m_Registers.RegEntryLo0.Read(), m_Registers.RegEntryLo1.Read(), entry));
            }
        }
        private static readonly bool s_TlbLog = Environment.GetEnvironmentVariable("COR64_CPUDIFF_TLBLOG") == "1"; // [cpu_diff] debug

        // [cpu_diff] debug: every entry as text (used by the COR64_CPUDIFF_RING dump)
        public string DumpEntries()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < m_Entries.Length; i++) sb.AppendLine(i + " " + m_Entries[i].ToString() + " R=" + m_Entries[i].EntryHi.Region);
            return sb.ToString();
        }

        public virtual void Read()
        {
            if (m_DisableTLB) return;

            var entry = m_Entries[m_Registers.Index];

            m_Registers.PageMask = entry.Mask;

            m_Registers.RegEntryHi.ASID = entry.EntryHi.ASID;
            m_Registers.RegEntryHi.VPN2 = entry.EntryHi.VPN2;

            m_Registers.RegEntryLo0.CoherencyMode = entry.Even.CoherencyMode;
            m_Registers.RegEntryLo0.PageFrameNumber = entry.Even.PageFrameNumber;
            m_Registers.RegEntryLo0.IsDirty = entry.Even.IsDirty;
            m_Registers.RegEntryLo0.IsValid = entry.Even.IsValid;

            m_Registers.RegEntryLo1.CoherencyMode = entry.Odd.CoherencyMode;
            m_Registers.RegEntryLo1.PageFrameNumber = entry.Odd.PageFrameNumber;
            m_Registers.RegEntryLo1.IsDirty = entry.Odd.IsDirty;
            m_Registers.RegEntryLo1.IsValid = entry.Odd.IsValid;
        }

        public void FlushAll()
        {
            if (m_DisableTLB) return;

            Initialize();
        }

        private void RaiseRefillException(ulong vaddr, uint vpn2, byte asid, bool isStore) {
            // m_Cop0.REGS.Write(CTS.CP0_REG_BADVADDR, vaddr);
            // m_Cop0.SetTLBRefillException(isStore);
            // m_Cop0.REGS.Write(CTS.CP0_REG_CONTEXT, vpn2);
            // m_EntryHi.ASID = asid;
            // m_EntryHi.SetVPN2(vpn2);
        }

        private void RaiseInvalidException(ulong vaddr, uint vpn2, byte asid, bool isStore) {
            // m_Cop0.REGS.Write(CTS.CP0_REG_BADVADDR, vaddr);
            // m_Cop0.SetInvalidTLBException(isStore);
            // m_Cop0.REGS.Write(CTS.CP0_REG_CONTEXT, vpn2);
            // m_EntryHi.ASID = asid;
            // m_EntryHi.SetVPN2(vpn2);
        }

        private static long SinglePageSize(uint pageMask) {
            return SinglePageMask(pageMask) + 1;
        }

        private static uint SinglePageMask(uint pageMask) {
            return (pageMask | 0x1FFF) >> 1;
        }

        private bool FastTranslate(long virtualAddress, out TLBEntry entry) {
            uint size = 1;

            entry = null;

            for (int i = 0; i < 7; i++) {
                size *= 4;
                uint pageMask = ((size << 12 ) - 1) >> 1;

                uint vpn2 = (uint)virtualAddress >> 13;
                vpn2 &= ~pageMask >> 13;
                vpn2 &= 0x7FFFFFF;

                // Try asid entry
                uint key_asid = Key(pageMask, vpn2, m_Registers.RegEntryHi.ASID, false);
                uint key_global = Key(pageMask, vpn2, 0, true);

                // Console.WriteLine("FastTLB try lookup: {0:X8} {1:X8} {2:X8}", pageMask, key_asid, key_global);

                if (m_EntryHashLookup.TryGetValue(key_asid, out int foundA)) {
                    // Console.WriteLine("index found A = {0}", foundA);
                    entry = m_Entries[foundA];
                }

                if (m_EntryHashLookup.TryGetValue(key_global, out int foundB)) {
                    // Console.WriteLine("index found B = {0}", foundB);
                    entry = m_Entries[foundB];
                }
            }

            return entry != null;
        }

        private bool TryPhysicalTranslate(TLBEntry entry, long virtualAddress, out long physicalAddress) {
            uint pfn;

            if ((virtualAddress & SinglePageSize(entry.Mask)) != 0) {
                if (!entry.Odd.IsValid)
                    throw new InvalidOperationException("odd page is invalid!");

                pfn = entry.Odd.PageFrameNumber;
            }
            else {
                if (!entry.Even.IsValid)
                    throw new InvalidOperationException("even page is invalid!");

                pfn = entry.Even.PageFrameNumber;
            }

            // offset from vaddr is always 12 bytes
            pfn &= 0xFFFFFF;
            physicalAddress = pfn << 12;
            physicalAddress |= (uint)virtualAddress & SinglePageMask(entry.Mask);

            #if DEBUG_TLB_TRANSLATE
            Log.Debug("TLB Translated: {0:X8} -> {1:X8}", virtualAddress, physicalAddress);
            #endif

            return true;
        }

        // [tlb_exc] COR64_TLBEXC=0: the old behaviour (a miss or an invalid page throws a host exception, no TLB Mod).
        public static readonly bool s_TlbWr = Environment.GetEnvironmentVariable("COR64_TLBEXC_TLBWR") != "0";
        public static readonly bool s_TlbExc = Environment.GetEnvironmentVariable("COR64_TLBEXC") != "0";

        public long Translate(long virtualAddress, bool isStore)
        {
            if (TryTranslate(virtualAddress, isStore, out long pa)) return pa;
            throw new InvalidOperationException("todo accurately throw TLB exceptions");
        }

        /// <summary>
        /// [tlb_exc] TLB lookup of a mapped address (the low 32 bits of the address, as MemoryManager passes it; it is
        /// sign-extended here as the 32-bit kernel does). False = a TLB exception was raised in COP0 (refill when no entry
        /// matches, invalid when the matching page has V=0, modified when a store hits a page with D=0).
        /// </summary>
        // [scripts_speed] COR64_SCRIPTS_SPEED_TLBREC: the same lookup as TryTranslate for an instruction fetch, but it never
        // raises an exception (false = the fetch would fault: the interpreter takes that step). Used only by the CPU
        // recompiler to find the physical block of TLB-mapped code; it changes no state.
        public unsafe bool TryTranslateFetchQuiet(long virtualAddress, out long physicalAddress)
        {
            physicalAddress = 0;
            if (!s_TlbExc) return false;
            if (s_Fc && m_FcTag != null) {
                uint v = (uint)virtualAddress; int ci = (int)((v >> 12) & ((1 << FcBits) - 1));
                if (m_FcTag[ci] == FcKey(v)) { physicalAddress = (m_FcVal[ci] & 0xFFFFF000u) | (v & 0xFFFu); return true; }
            }
            ulong va = (ulong)(long)(int)(uint)virtualAddress;
            uint asid = m_Registers.RegEntryHi.ASID;
            uint vpnFull = (uint)(va >> 13) & 0x7FFFFFF;
            byte region = (byte)(va >> 62);
            for (int i = 0; i < m_Entries.Length; i++) {
                var entry = m_Entries[i];
                if (entry.Mask == 0 && entry.EntryHi.VPN2 == 0 && entry.ASID == 0 && !entry.IsGlobal && !entry.Even.IsValid && !entry.Odd.IsValid
                    && entry.Even.PageFrameNumber == 0 && entry.Odd.PageFrameNumber == 0 && entry.EntryHi.Region == 0) continue;
                if (!entry.IsGlobal && entry.ASID != asid) continue;
                if ((vpnFull & ~(entry.Mask >> 13)) != entry.EntryHi.VPN2) continue;
                if (entry.EntryHi.Region != region) continue;
                var lo = (virtualAddress & SinglePageSize(entry.Mask)) != 0 ? entry.Odd : entry.Even;
                if (!lo.IsValid) return false;
                uint pfn = lo.PageFrameNumber & 0xFFFFFF;
                physicalAddress = ((long)pfn << 12) | ((uint)virtualAddress & SinglePageMask(entry.Mask));
                if (s_PhysHi && physicalAddress >= 0x80000000L) physicalAddress = 0x04900000L | (physicalAddress & 0xFFFC);
                if (s_Fc) FcPut((uint)virtualAddress, physicalAddress, lo.IsDirty);
                return true;
            }
            return false;
        }

        // [dbg_mem] debugger TLB walk: the lookup of TryTranslateFetchQuiet without the fast-cache fill, so it changes
        // nothing (no COP0 write, no exception, no cache entry). False = no valid matching entry.
        public bool TryTranslateDebug(long virtualAddress, out long physicalAddress)
        {
            physicalAddress = 0;
            ulong va = (ulong)(long)(int)(uint)virtualAddress;
            uint asid = m_Registers.RegEntryHi.ASID;
            uint vpnFull = (uint)(va >> 13) & 0x7FFFFFF;
            byte region = (byte)(va >> 62);
            for (int i = 0; i < m_Entries.Length; i++) {
                var entry = m_Entries[i];
                if (entry.Mask == 0 && entry.EntryHi.VPN2 == 0 && entry.ASID == 0 && !entry.IsGlobal && !entry.Even.IsValid && !entry.Odd.IsValid
                    && entry.Even.PageFrameNumber == 0 && entry.Odd.PageFrameNumber == 0 && entry.EntryHi.Region == 0) continue;
                if (!entry.IsGlobal && entry.ASID != asid) continue;
                if ((vpnFull & ~(entry.Mask >> 13)) != entry.EntryHi.VPN2) continue;
                if (entry.EntryHi.Region != region) continue;
                var lo = (virtualAddress & SinglePageSize(entry.Mask)) != 0 ? entry.Odd : entry.Even;
                if (!lo.IsValid) return false;
                uint pfn = lo.PageFrameNumber & 0xFFFFFF;
                physicalAddress = ((long)pfn << 12) | ((uint)virtualAddress & SinglePageMask(entry.Mask));
                if (s_PhysHi && physicalAddress >= 0x80000000L) physicalAddress = 0x04900000L | (physicalAddress & 0xFFFC);
                return true;
            }
            return false;
        }

        public unsafe bool TryTranslate(long virtualAddress, bool isStore, out long physicalAddress)
        {
            if (!s_TlbExc) {
                physicalAddress = OldTranslate(virtualAddress, isStore);
                return true;
            }

            if (s_Fc && m_FcTag != null) {
                uint v = (uint)virtualAddress; int ci = (int)((v >> 12) & ((1 << FcBits) - 1));
                uint cv = m_FcVal[ci];
                if (m_FcTag[ci] == FcKey(v) && (!isStore || (cv & 1) != 0)) { physicalAddress = (cv & 0xFFFFF000u) | (v & 0xFFFu); return true; }
            }
            ulong va = (ulong)(long)(int)(uint)virtualAddress;
            uint asid = m_Registers.RegEntryHi.ASID;
            uint vpnFull = (uint)(va >> 13) & 0x7FFFFFF;
            byte region = (byte)(va >> 62);

            for (int i = 0; i < m_Entries.Length; i++) {
                var entry = m_Entries[i];
                // [tlb_exc] an entry still in its all-zero power-on state never matches, so a null pointer is a refill, not a
                // TLB invalid (power-on contents are undefined on hardware). Kept as a test, not a different power-on
                // pattern: TLBP/TLBR must see the same entries as before (SM64 boot hash).
                if (entry.Mask == 0 && entry.EntryHi.VPN2 == 0 && entry.ASID == 0 && !entry.IsGlobal && !entry.Even.IsValid && !entry.Odd.IsValid
                    && entry.Even.PageFrameNumber == 0 && entry.Odd.PageFrameNumber == 0 && entry.EntryHi.Region == 0) continue;
                if (!entry.IsGlobal && entry.ASID != asid) continue;
                if ((vpnFull & ~(entry.Mask >> 13)) != entry.EntryHi.VPN2) continue;
                if (entry.EntryHi.Region != region) continue;

                bool odd = (virtualAddress & SinglePageSize(entry.Mask)) != 0;
                var lo = odd ? entry.Odd : entry.Even;

                if (!lo.IsValid) {
                    m_Cop0.RaiseTlbException(isStore ? ExceptionType.TLBStore : ExceptionType.TLBLoad, false, va);
                    physicalAddress = 0;
                    return false;
                }

                if (isStore && !lo.IsDirty) {
                    m_Cop0.RaiseTlbException(ExceptionType.TLBMod, false, va);
                    physicalAddress = 0;
                    return false;
                }

                uint pfn = lo.PageFrameNumber & 0xFFFFFF;
                physicalAddress = ((long)pfn << 12) | ((uint)virtualAddress & SinglePageMask(entry.Mask));
                if (s_PhysHi && physicalAddress >= 0x80000000L) physicalAddress = 0x04900000L | (physicalAddress & 0xFFFC);
                if (s_Fc) FcPut((uint)virtualAddress, physicalAddress, lo.IsDirty);
                return true;
            }

            m_Cop0.RaiseTlbException(isStore ? ExceptionType.TLBStore : ExceptionType.TLBLoad, true, va);
            physicalAddress = 0;
            return false;
        }

        private long OldTranslate(long virtualAddress, bool isStore)
        {
            // TODO: Throw TLB mod exception when isStore == isDirty

            #if FAST_TLB
            if (FastTranslate(virtualAddress, out TLBEntry entry)) {
                if (TryPhysicalTranslate(entry, virtualAddress, out long address)) {
                    return address;
                }
            }
            #else

            for (int i = 0; i < m_Entries.Length; i++) {
                var entry = m_Entries[i];

                if (!entry.Even.IsValid && !entry.Odd.IsValid)
                    continue;

                // Console.WriteLine("Test TLB entry: " + entry.ToString());

                if (entry.IsGlobal || (!entry.IsGlobal && entry.ASID == m_Registers.RegEntryHi.ASID) ) {
                    uint vpn2 = (uint)virtualAddress >> 13;
                    vpn2 &= ~entry.Mask >> 13;
                    vpn2 &= 0x7FFFFFF;

                    // Console.WriteLine("VPN2 from vaddr = {0:X8}, mask = {1:X8}", vpn2, mask);

                    if (entry.EntryHi.VPN2 == vpn2) {
                        // Console.WriteLine("Entry Found");
                        if (TryPhysicalTranslate(entry, virtualAddress, out long address)) {
                            return address;
                        }
                    }
                }
            }
            #endif

            // if (RaiseRefillException(virtualAddress, ))
            throw new InvalidOperationException("todo accurately throw TLB exceptions");
        }
    }
}