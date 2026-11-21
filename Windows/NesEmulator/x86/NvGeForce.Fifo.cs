// GeForce 256: PFIFO (the command path). Channels submit through USER (BAR0 800000 + chid * 64 KB): PIO method writes, or in DMA mode
// the PUT pointer (+40h) of a push buffer described by a DMA object in PRAMIN. The pusher runs synchronously at the PUT write (the
// FIFO never fills, GET == PUT when the write returns: deterministic, the Voodoo/ViRGE rule). Methods go to PGRAPH through the object
// bound to each subchannel (method 0: a handle looked up in RAMHT). Layouts from envytools rnndb + the driver's own register log.
//
// Push buffer words (NV04-NV10): 001ccccc ... jump (bits 28:2 = new GET) when bits 31:29 = 001; otherwise a method header:
// count bits 28:18, subchannel bits 15:13, method bits 12:2, bit 30 = non-increasing method.

using System;
using System.IO;

namespace NesEmulator.X86;

public sealed partial class NvGeForce
{
    /// <summary>System memory (physical) for push buffers / DMA objects that target PCI memory.</summary>
    public Func<uint, uint>? SysRead32;
    public Action<uint, uint>? SysWrite32;

    // the push buffer decoder (the active channel's, as CACHE1 holds it)
    private int _dmaCount, _dmaMethod, _dmaSubc;
    private bool _dmaNonInc;
    private readonly uint[] _subcObj = new uint[256];   // PRAMIN instance (bytes) of the object bound to each channel's subchannels (chid * 8 + subc): part of the channel context
    private readonly int[] _subcEng = new int[256];      // its engine (RAMHT context bits 17:16): 0 = software (the driver's ISR runs it), 1 = PGRAPH
    public long Methods, PushWords;                   // diagnostics
    public uint TracePut = 0xffffffff; public bool PutHit;   // diagnostics: x86boot --nvtrace
    /// <summary>Diagnostics: every method executed (chid, subc, class, method, data).</summary>
    public Action<int, int, int, int, uint>? MethodLog;

    private void ResetFifo() { ResetDecode(); Array.Clear(_subcObj); Array.Clear(_subcEng); }
    private void ResetDecode() { _dmaCount = _dmaMethod = _dmaSubc = 0; _dmaNonInc = false; }
    private int Sc(int subc) => ActiveChid * 8 + subc;

    /// <summary>CACHE1 holds a method the driver must run (a software object's): the pusher waits until its ISR took it (GET == PUT).</summary>
    private bool Stalled => (Reg(0x002100) & 1) != 0;   // (CACHE1 GET/PUT are the driver's to move while it holds the channel)

    public uint Ramin32(uint off) => off < 0x100000 - 3 ? BitConverter.ToUInt32(_v.Ram, RaminBase + (int)off) : 0;
    public void RaminW32(uint off, uint v) { if (off < 0x100000 - 3) BitConverter.TryWriteBytes(_v.Ram.AsSpan(RaminBase + (int)off), v); }
    private uint Vram32(uint a) { a &= VramBytes - 1; return a <= VramBytes - 4 ? BitConverter.ToUInt32(_v.Ram, (int)a) : 0; }
    private void VramW32(uint a, uint v) { a &= VramBytes - 1; if (a <= VramBytes - 4) BitConverter.TryWriteBytes(_v.Ram.AsSpan((int)a), v); }

    // ================= DMA objects =================

    /// <summary>The address an offset into a DMA object resolves to: (target VRAM?, address).</summary>
    public (bool vram, uint addr) DmaAddr(uint inst, uint off)
    {
        uint w0 = Ramin32(inst);
        off += w0 >> 20;                                   // adjust: the low 12 bits of the start
        bool linear = (w0 & 0x2000) != 0;
        uint page = linear ? Ramin32(inst + 8) & ~0xfffu : Ramin32(inst + 8 + (off >> 12) * 4) & ~0xfffu;
        uint a = linear ? page + off : page + (off & 0xfff);
        int target = (int)(w0 >> 16) & 3;
        return (target == 0 || target == 1, a);
    }

    public uint DmaRead32(uint inst, uint off) { var (vr, a) = DmaAddr(inst, off); return vr ? Vram32(a) : SysRead32?.Invoke(a) ?? 0; }
    public void DmaWrite32(uint inst, uint off, uint v) { var (vr, a) = DmaAddr(inst, off); if (vr) VramW32(a, v); else SysWrite32?.Invoke(a, v); }

    // ================= USER =================

    private int ActiveChid => (int)(Reg(0x003204) & 0x1f);
    private bool DmaMode(int chid) => (Reg(0x002504) & (1u << chid)) != 0;
    private uint RamfcBase => (Reg(0x002214) & 0x1fe) << 8;   // RAMFC (one 32-byte context per channel)

    private uint UserRead(uint a)
    {
        int chid = (int)(a >> 16) & 0x1f; uint o = a & 0x1fff;
        bool act = chid == ActiveChid;
        return o switch
        {
            0x10 => 0x7c,                                                       // PIO: free bytes
            0x40 => act ? Reg(0x003240) : Ramin32(RamfcBase + (uint)chid * 32),   // DMA PUT
            0x44 => act ? Reg(0x003244) : Ramin32(RamfcBase + (uint)chid * 32 + 4), // DMA GET
            0x48 => act ? Reg(0x003248) : Ramin32(RamfcBase + (uint)chid * 32 + 8), // REF
            _ => 0,
        };
    }

    private void UserWrite(uint a, uint v)
    {
        int chid = (int)(a >> 16) & 0x1f; uint o = a & 0x1fff; int subc = (int)(a >> 13) & 7;
        if (DmaMode(chid))
        {
            if (o != 0x40) return;
            if (v == TracePut) PutHit = true;
            if (chid != ActiveChid) SwitchTo(chid);
            _reg[0x003240] = v & 0x1ffffffc;
            RunPusher();
            return;
        }
        if (chid != ActiveChid) SwitchTo(chid);
        Method(chid, subc, (int)o, v);                                          // PIO
    }

    /// <summary>A channel switch: CACHE1's DMA state goes back to the old channel's RAMFC, the new one's comes in.</summary>
    private void SwitchTo(int chid)
    {
        int old = ActiveChid; uint ob = RamfcBase + (uint)old * 32, nb = RamfcBase + (uint)chid * 32;
        RaminW32(ob, Reg(0x003240)); RaminW32(ob + 4, Reg(0x003244)); RaminW32(ob + 8, Reg(0x003248)); RaminW32(ob + 12, Reg(0x00322c));
        _reg[0x003240] = Ramin32(nb); _reg[0x003244] = Ramin32(nb + 4); _reg[0x003248] = Ramin32(nb + 8); _reg[0x00322c] = Ramin32(nb + 12);
        _reg[0x003204] = (Reg(0x003204) & ~0x1fu) | (uint)chid | (DmaMode(chid) ? 0x100u : 0);
        if (DmaMode(chid)) _reg[0x003220] = Reg(0x003220) | 1;   // a DMA channel switched in pushes (hardware restores DMA_PUSH with the context)
        ResetDecode();
    }

    // ================= the DMA pusher =================

    private bool _inPusher;

    private void RunPusher()
    {
        if (_inPusher) return;
        if ((Reg(0x003204) & 0x100) == 0 || (Reg(0x003220) & 1) == 0 || (Reg(0x003200) & 1) == 0) return;   // not DMA mode / pusher off / push access off
        _inPusher = true;
        try
        {
            uint inst = (Reg(0x00322c) & 0xffff) << 4;
            uint get = Reg(0x003244), put = Reg(0x003240);
            int guard = 0;
            while (get != put && guard++ < 4_000_000)
            {
                if (Stalled) break;
                uint w = DmaRead32(inst, get); get += 4; PushWords++;
                if (_dmaCount > 0)
                {
                    _reg[0x003244] = get;
                    Method(ActiveChid, _dmaSubc, _dmaMethod, w);
                    if (!_dmaNonInc) _dmaMethod += 4;
                    _dmaCount--;
                    continue;
                }
                if ((w & 0xe0000003) == 0x20000000) { get = w & 0x1ffffffc; continue; }   // jump
                if ((w & 0xa0030003) != 0) { _reg[0x003228] = w; continue; }              // not a header we know: skip it (logged in DMA_STATE)
                _dmaCount = (int)(w >> 18) & 0x7ff; _dmaSubc = (int)(w >> 13) & 7; _dmaMethod = (int)(w & 0x1ffc); _dmaNonInc = (w & 0x40000000) != 0;
            }
            _reg[0x003244] = get;
        }
        finally { _inPusher = false; }
    }

    // ================= method dispatch =================

    /// <summary>The PRAMIN instance of the object with this handle (RAMHT, searched whole: the hash only speeds a lookup up).</summary>
    private uint RamhtLookup(uint handle, int chid)
    {
        uint ctx = RamhtContext(handle, chid);
        return ctx == 0 ? 0 : (ctx & 0xffff) << 4;
    }

    private uint RamhtContext(uint handle, int chid)
    {
        uint cfg = Reg(0x002210), b = (cfg & 0x1f0) << 8, size = 0x1000u << (int)((cfg >> 16) & 3);
        for (uint o = 0; o < size; o += 8)
        {
            if (Ramin32(b + o) != handle) continue;
            uint ctx = Ramin32(b + o + 4);
            if ((ctx & 0x80000000) == 0 && ctx == 0) continue;
            if (((ctx >> 24) & 0x1f) != chid) continue;
            return ctx | 0x80000000;
        }
        return 0;
    }

    public int ClassOf(int subc) => (int)(Ramin32(_subcObj[Sc(subc)]) & 0xfff);

    private void Method(int chid, int subc, int mthd, uint data)
    {
        Methods++;
        if (mthd == 0)
        {
            uint ctx = RamhtContext(data, chid);
            _subcObj[Sc(subc)] = (ctx & 0xffff) << 4; _subcEng[Sc(subc)] = (int)(ctx >> 16) & 3;
            MethodLog?.Invoke(chid, subc, ClassOf(subc), 0, data);
            if (_subcEng[Sc(subc)] == 0) SoftwareMethod(subc, 0, data);   // the driver tracks its own objects' binds too
            return;
        }
        if (mthd == 0x50) { _reg[0x003248] = data; return; }                    // REF_CNT
        if (mthd < 0x100) { MethodLog?.Invoke(chid, subc, -1, mthd, data); return; }
        if (_subcEng[Sc(subc)] == 0) { MethodLog?.Invoke(chid, subc, -2, mthd, data); SoftwareMethod(subc, mthd, data); return; }
        int cls = ClassOf(subc);
        MethodLog?.Invoke(chid, subc, cls, mthd, data);
        Graph(subc, _subcObj[Sc(subc)], cls, mthd, data);
    }

    /// <summary>A method for a software object: into CACHE1 (METHOD/DATA at 3800h + 8 x entry), PFIFO CACHE_ERROR raised; the pusher
    /// stops until the driver's ISR has taken it (it advances CACHE1 GET, clears the interrupt and turns pulling back on).</summary>
    private void SoftwareMethod(int subc, int mthd, uint data)
    {
        uint put = Reg(0x003210) & 0x1fc;
        _reg[0x003800 + put * 2] = (uint)(subc << 13) | (uint)mthd;
        _reg[0x003804 + put * 2] = data;
        _reg[0x003210] = (put + 4) & 0x1fc;
        _reg[0x002100] = Reg(0x002100) | 1;      // CACHE_ERROR
        _reg[0x003250] = Reg(0x003250) | 0x100;  // PULL0: hash failed (no engine took it)
        UpdateIrq();
    }

    private void SaveFifo(BinaryWriter w)
    {
        w.Write(_dmaCount); w.Write(_dmaMethod); w.Write(_dmaSubc); w.Write(_dmaNonInc);
        foreach (var o in _subcObj) w.Write(o);
        foreach (var e in _subcEng) w.Write(e);
        SaveGraph(w);
    }

    private void LoadFifo(BinaryReader r, int ver)
    {
        _dmaCount = r.ReadInt32(); _dmaMethod = r.ReadInt32(); _dmaSubc = r.ReadInt32(); _dmaNonInc = r.ReadBoolean();
        Array.Clear(_subcObj); Array.Clear(_subcEng);
        int n = ver >= 5 ? 256 : 8, o = ver >= 5 ? 0 : ActiveChid * 8;   // (a version 4 state kept one channel's: the active one)
        for (int i = 0; i < n; i++) _subcObj[o + i] = r.ReadUInt32();
        for (int i = 0; i < n; i++) _subcEng[o + i] = r.ReadInt32();
        LoadGraph(r, ver);
    }
}
