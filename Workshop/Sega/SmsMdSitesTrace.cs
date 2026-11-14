using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NesEmulator.Sega;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// <c>--sms-run &lt;rom&gt; --md-sites &lt;file.json&gt;</c>: the Nested Everything SMS -> Genesis translator's dynamic pass (BrokenDev/tools/nested-everything/sms2md.py).
/// Records the ROM bytes that ran as code with the slot they ran from, the targets of non-sequential jumps (with the kind of instruction that jumped), and per
/// instruction the memory regions its data accesses touched (bit 0-2 slots 0-2, 3 work RAM $C000-$DFFF, 4 its mirror $E000-$FFFB, 5 the mapper registers),
/// reads and writes apart. The translator uses the regions to give an access site the direct work-RAM path instead of the checked one.
/// </summary>
internal sealed class SmsMdSitesTrace : ISegaBusDebugTap
{
    private readonly BOARD_SMS board;
    private readonly byte[] code;
    private readonly HashSet<long> targets = new();          // (rom index or -1-pc) << 8 | opcode of the instruction that jumped there
    private readonly Dictionary<long, int[]> mem = new();
    private readonly HashSet<string> slotPages = new();
    private int lastPc = -1, lastIdx = -1, lastOp = -1;
    private long instructions;

    public SmsMdSitesTrace(BOARD_SMS board) { this.board = board; code = new byte[board.Cart.Rom.Length]; }

    public void OnRead(uint address, byte value) => Mem(address, false);
    public void OnWrite(uint address, byte value) => Mem(address, true);
    public void OnWriteDone(uint address, byte value) { }
    public void OnPort(ushort port, byte value, bool write) { }
    public void OnDmaRead(uint address, byte value) { }

    private void Mem(uint address, bool write)
    {
        if (lastPc < 0) return;
        int a = (ushort)address;
        if (!write && a >= lastPc && a < lastPc + 4) return;
        int bit = a < 0xC000 ? a >> 14 : a < 0xE000 ? 3 : a < 0xFFFC ? 4 : 5;
        long key = lastIdx >= 0 ? lastIdx : -1 - lastPc;
        if (!mem.TryGetValue(key, out var m)) mem[key] = m = new int[2];
        m[write ? 1 : 0] |= 1 << bit;
    }

    public void OnInstruction(ushort pc)
    {
        instructions++;
        int idx = board.Cart.DebugRomIndex(pc);
        if (idx >= 0) code[idx] = (byte)(1 + Math.Min(2, pc >> 14));
        int d = pc - lastPc;
        if (lastPc >= 0 && (d < 1 || d > 4)) targets.Add(((idx >= 0 ? (long)idx : -1 - pc) << 8) | (uint)(lastOp & 0xFF));
        lastPc = pc; lastIdx = idx; lastOp = board.Peek(pc);
    }

    public void OnFrame() => slotPages.Add($"{board.Cart.DebugSlotPage(0)},{board.Cart.DebugSlotPage(1)},{board.Cart.DebugSlotPage(2)}");

    public void Write(string path)
    {
        var sb = new StringBuilder();
        sb.Append("{\n  \"romSize\": ").Append(code.Length).Append(",\n  \"instructions\": ").Append(instructions);
        sb.Append(",\n  \"slotPages\": [").Append(string.Join(",", slotPages.Select(x => "\"" + x + "\""))).Append("],\n  \"code\": [");
        var runs = new List<string>();
        for (int i = 0; i < code.Length; i++) if (code[i] != 0) runs.Add($"[{i},{code[i] - 1}]");
        sb.Append(string.Join(",", runs)).Append("],\n  \"targets\": [");
        sb.Append(string.Join(",", targets.OrderBy(x => x).Select(x => $"[{x >> 8},{x & 0xFF}]"))).Append("],\n  \"mem\": [");
        sb.Append(string.Join(",", mem.OrderBy(k => k.Key).Select(k => $"[{k.Key},{k.Value[0]},{k.Value[1]}]"))).Append("]\n}\n");
        File.WriteAllText(path, sb.ToString());
    }
}
