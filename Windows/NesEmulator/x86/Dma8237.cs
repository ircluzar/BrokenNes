// The PC/AT's two Intel 8237A DMA controllers (channels 0-3 byte-wide on ports 00h-0Fh, 4-7 word-wide on C0h-DEh, page registers 81h-8Fh).
// Written for BrokenNes from the public 8237A / PC/AT documentation; no source copied. The sound cards' view of a channel (Read / Write a block,
// a callback on mask changes and on terminal count) follows the interface DOSBox's cards were written against (dma.h of DOSBox 0.72/0.74: Register_Callback
// fires the current mask state at once; a single-cycle transfer reaching terminal count masks its channel), so the ported cards behave as they did there.
//
// Kept simple on purpose: transfers happen when a card asks (no DREQ arbitration, no cycle stealing, no memory-to-memory, no verify), the address only
// counts up (the decrement mode bit is stored but ignored, as DOSBox did), the 16-bit channels address words (page bits 0 cleared, address << 1).

using System;
using System.IO;

namespace NesEmulator.X86;

public enum DmaEvent { ReachedTc, Masked, Unmasked, TransferEnd }

public sealed class Dma8237
{
    public sealed class Channel
    {
        public readonly int Number;
        public readonly bool Dma16;
        public ushort BaseAddr, BaseCnt, CurrAddr, CurrCnt;
        public byte Page, Mode;
        public bool Masked = true, Tc, Request;
        public Action<Channel, DmaEvent>? Callback;
        internal Dma8237 Owner = null!;
        public Channel(int n) { Number = n; Dma16 = n >= 4; }
        public bool AutoInit => (Mode & 0x10) != 0;
        public bool ReadMode => (Mode & 0x0c) == 0x08;   // memory -> device

        /// <summary>A card's handler; like DOSBox's Register_Callback the current mask state is reported to it at once.</summary>
        public void RegisterCallback(Action<Channel, DmaEvent>? cb) { Callback = cb; SetMask(Masked); }
        /// <summary>Puts a handler back after a state load without telling it anything.</summary>
        public void SetCallbackSilently(Action<Channel, DmaEvent>? cb) => Callback = cb;
        public void SetMask(bool m) { Masked = m; Callback?.Invoke(this, m ? DmaEvent.Masked : DmaEvent.Unmasked); }

        private uint Phys(int i) => Dma16
            ? ((uint)(Page & 0xfe) << 16) | ((uint)((CurrAddr + i) & 0xffff) << 1)
            : ((uint)Page << 16) | (uint)((CurrAddr + i) & 0xffff);

        /// <summary>Moves up to <paramref name="want"/> transfers (bytes, or words on 4-7) from memory into <paramref name="buf"/> at <paramref name="off"/>; returns how many.
        /// Masked: nothing. At terminal count an auto-init channel reloads and goes on, a single-cycle one masks itself.</summary>
        public int Read(int want, byte[] buf, int off) => Move(want, buf, off, false);
        /// <summary>Moves up to <paramref name="want"/> transfers from <paramref name="buf"/> into memory.</summary>
        public int Write(int want, byte[] buf, int off) => Move(want, buf, off, true);

        private int Move(int want, byte[] buf, int off, bool toMem)
        {
            if (Masked) return 0;
            var m = Owner._mem;
            int done = 0, w = Dma16 ? 2 : 1;
            while (want > 0)
            {
                int left = CurrCnt + 1;
                int n = Math.Min(want, left);
                for (int i = 0; i < n; i++)
                {
                    uint a = Phys(i);
                    for (int b = 0; b < w; b++)
                    {
                        int bi = off + (done + i) * w + b;
                        if (bi >= buf.Length) continue;
                        if (toMem) m.PhysW8(a + (uint)b, buf[bi]); else buf[bi] = m.Phys8(a + (uint)b);
                    }
                }
                done += n; want -= n;
                if (n < left) { CurrAddr = (ushort)(CurrAddr + n); CurrCnt = (ushort)(CurrCnt - n); break; }
                Tc = true; Owner.SetTc(Number);
                Callback?.Invoke(this, DmaEvent.ReachedTc);
                if (AutoInit) { CurrAddr = BaseAddr; CurrCnt = BaseCnt; continue; }
                CurrAddr = (ushort)(CurrAddr + n); CurrCnt = 0xffff;
                Masked = true;
                Callback?.Invoke(this, DmaEvent.TransferEnd);
                break;
            }
            return done;
        }

        internal void Save(BinaryWriter w) { w.Write(BaseAddr); w.Write(BaseCnt); w.Write(CurrAddr); w.Write(CurrCnt); w.Write(Page); w.Write(Mode); w.Write(Masked); w.Write(Tc); w.Write(Request); }
        internal void Load(BinaryReader r) { BaseAddr = r.ReadUInt16(); BaseCnt = r.ReadUInt16(); CurrAddr = r.ReadUInt16(); CurrCnt = r.ReadUInt16(); Page = r.ReadByte(); Mode = r.ReadByte(); Masked = r.ReadBoolean(); Tc = r.ReadBoolean(); Request = r.ReadBoolean(); }
    }

    private readonly PcMachine _mem;
    public readonly Channel[] Channels = new Channel[8];
    private readonly bool[] _flipflop = new bool[2];
    private readonly byte[] _status = new byte[2];
    private readonly byte[] _pages = new byte[16];   // 80h-8Fh as written (the unused ones are plain storage, as on an AT)
    /// <summary>False while a card owns ports C0h-DFh (DOSBox's Tandy closes the second controller: its SN76496 sits at C0h).</summary>
    public bool SecondOpen = true;

    public Dma8237(PcMachine m)
    {
        _mem = m;
        for (int i = 0; i < 8; i++) Channels[i] = new Channel(i) { Owner = this };
    }

    public Channel this[int n] => Channels[n & 7];

    internal void SetTc(int ch) => _status[ch >> 2] |= (byte)(1 << (ch & 3));

    public void Reset()
    {
        foreach (var c in Channels) { c.BaseAddr = c.BaseCnt = c.CurrAddr = c.CurrCnt = 0; c.Page = c.Mode = 0; c.Masked = true; c.Tc = c.Request = false; c.Callback = null; }
        _flipflop[0] = _flipflop[1] = false; _status[0] = _status[1] = 0; Array.Clear(_pages);
    }

    public void ClearCallbacks() { foreach (var c in Channels) c.Callback = null; }

    private static readonly int[] PageChannel = { -1, 2, 3, 1, -1, -1, -1, 0, -1, 6, 7, 5, -1, -1, -1, 4 };   // 80h + i -> channel

    /// <summary>A port of the DMA controllers or the page registers (80h itself is the POST port and stays the board's). False when not one of them.</summary>
    public bool Out(int port, byte v)
    {
        if (port is >= 0x81 and <= 0x8f)
        {
            _pages[port & 15] = v;
            int ch = PageChannel[port & 15];
            if (ch >= 0) Channels[ch].Page = v;
            return true;
        }
        if (port is >= 0 and <= 0x0f) { WriteCtrl(0, port, v); return true; }
        if (SecondOpen && port is >= 0xc0 and <= 0xdf) { if ((port & 1) == 0) WriteCtrl(1, (port - 0xc0) >> 1, v); return true; }
        return false;
    }

    public bool In(int port, out byte v)
    {
        v = 0xff;
        if (port is >= 0x81 and <= 0x8f) { v = _pages[port & 15]; return true; }
        if (port is >= 0 and <= 0x0f) { v = ReadCtrl(0, port); return true; }
        if (SecondOpen && port is >= 0xc0 and <= 0xdf) { if ((port & 1) == 0) v = ReadCtrl(1, (port - 0xc0) >> 1); return true; }
        return false;
    }

    private void WriteCtrl(int c, int reg, byte v)
    {
        int b = c * 4;
        if (reg < 8)
        {
            var ch = Channels[b + (reg >> 1)];
            bool hi = _flipflop[c]; _flipflop[c] = !hi;
            if ((reg & 1) == 0)
            {
                ch.BaseAddr = hi ? (ushort)((ch.BaseAddr & 0xff) | (v << 8)) : (ushort)((ch.BaseAddr & 0xff00) | v);
                ch.CurrAddr = ch.BaseAddr;
            }
            else
            {
                ch.BaseCnt = hi ? (ushort)((ch.BaseCnt & 0xff) | (v << 8)) : (ushort)((ch.BaseCnt & 0xff00) | v);
                ch.CurrCnt = ch.BaseCnt;
            }
            return;
        }
        switch (reg)
        {
            case 0x08: break;                                              // command: ignored
            case 0x09: Channels[b + (v & 3)].Request = (v & 4) != 0; break;
            case 0x0a: Channels[b + (v & 3)].SetMask((v & 4) != 0); break;
            case 0x0b: Channels[b + (v & 3)].Mode = (byte)(v & 0xfc); break;
            case 0x0c: _flipflop[c] = false; break;
            case 0x0d: _flipflop[c] = false; _status[c] = 0; for (int i = 0; i < 4; i++) Channels[b + i].SetMask(true); break;
            case 0x0e: for (int i = 0; i < 4; i++) Channels[b + i].SetMask(false); break;
            case 0x0f: for (int i = 0; i < 4; i++) Channels[b + i].SetMask((v & (1 << i)) != 0); break;
        }
    }

    private byte ReadCtrl(int c, int reg)
    {
        int b = c * 4;
        if (reg < 8)
        {
            var ch = Channels[b + (reg >> 1)];
            bool hi = _flipflop[c]; _flipflop[c] = !hi;
            ushort val = (reg & 1) == 0 ? ch.CurrAddr : ch.CurrCnt;
            return (byte)(hi ? val >> 8 : val);
        }
        switch (reg)
        {
            case 0x08:
            {
                byte s = _status[c]; _status[c] = 0;
                for (int i = 0; i < 4; i++) if (Channels[b + i].Request) s |= (byte)(0x10 << i);
                return s;
            }
            case 0x0f:
            {
                byte s = 0xf0;
                for (int i = 0; i < 4; i++) if (Channels[b + i].Masked) s |= (byte)(1 << i);
                return s;
            }
        }
        return 0xff;
    }

    public void Save(BinaryWriter w)
    {
        foreach (var c in Channels) c.Save(w);
        w.Write(_flipflop[0]); w.Write(_flipflop[1]); w.Write(_status); w.Write(_pages); w.Write(SecondOpen);
    }

    public void Load(BinaryReader r)
    {
        foreach (var c in Channels) c.Load(r);
        _flipflop[0] = r.ReadBoolean(); _flipflop[1] = r.ReadBoolean(); r.Read(_status, 0, 2); r.Read(_pages, 0, 16); SecondOpen = r.ReadBoolean();
    }
}
