// GeForce 256: PGRAPH. G2 starts here: the methods every NV object class shares (notifiers); the 2D and Celsius 3D classes follow
// from the driver's method log.

using System;
using System.IO;

namespace NesEmulator.X86;

public sealed partial class NvGeForce
{
    private readonly uint[] _notifyObj = new uint[8];   // DMA object (PRAMIN instance) each subchannel's object notifies into

    private void Graph(int subc, uint inst, int cls, int mthd, uint data)
    {
        switch (mthd)
        {
            case 0x180: _notifyObj[subc] = RamhtLookup(data, ActiveChid); return;   // SET_DMA_NOTIFY
            case 0x104:                                                                // NOTIFY: done at once (synchronous engine)
                if (_notifyObj[subc] != 0)
                {
                    long ns = Frame * 14_285_714;
                    DmaWrite32(_notifyObj[subc], 0, (uint)ns); DmaWrite32(_notifyObj[subc], 4, (uint)(ns >> 32));
                    DmaWrite32(_notifyObj[subc], 8, 0); DmaWrite32(_notifyObj[subc], 12, 0);   // return value 0, status 0 = done
                }
                return;
        }
    }

    private void SaveGraph(BinaryWriter w) { foreach (var o in _notifyObj) w.Write(o); }
    private void LoadGraph(BinaryReader r) { for (int i = 0; i < 8; i++) _notifyObj[i] = r.ReadUInt32(); }
}
