using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NesEmulator.Sega;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// <c>--sms-run &lt;rom&gt; --gg-sites &lt;file.json&gt;</c>: the Nested Everything GG -> SMS converter's dynamic pass (BrokenDev/tools/nested-everything/gg2sms.py).
/// While the game runs it records every Game Gear-specific or VDP port access with the ROM offset (or RAM address) of the instruction that made it, whether each
/// data-port write went to colour RAM, the ROM bytes that ran as code (instruction starts), the targets of non-sequential jumps, and the pages slots 0 and 1 held.
/// The converter uses it to confirm static sites, to find the sites a static scan cannot see (OUT (C), OTIR) and to check that its runtime's ROM space stays mapped.
/// </summary>
internal sealed class GgSitesTrace : ISegaBusDebugTap
{
    private readonly bool[] dataRead;   // ROM bytes read as data (operand fetches included: the converter subtracts the instructions)
    private readonly long[] vecRuns = new long[0x40];   // instructions run at $0000-$003F (how often each RST vector is entered)
    private readonly BOARD_SMS board;
    private readonly byte[] code;   // 0 = never ran, else 1 + the slot (0-2) it ran from
    private readonly bool[] ramChanged = new bool[0x2000];
    private byte[]? ramPrev;
    private readonly HashSet<int> targets = new();
    private readonly Dictionary<string, Site> sites = new();
    private readonly HashSet<string> slotPages = new();
    private int lastPc = -1, lastIdx = -1;

    private sealed class Site { public int Off; public int Pc; public bool Rw; public int Port; public long Count; public long Cram; public long Vram; public string Bytes = ""; public string Regs = ""; }

    public GgSitesTrace(BOARD_SMS board)
    {
        this.board = board;
        code = new byte[board.Cart.Rom.Length];
        dataRead = new bool[board.Cart.Rom.Length];
    }

    public void OnRead(uint address, byte value) { int i = board.Cart.DebugRomIndex((ushort)address); if (i >= 0) dataRead[i] = true; }
    public void OnWrite(uint address, byte value) { }
    public void OnWriteDone(uint address, byte value) { }
    public void OnPort(ushort port, byte value, bool write) { }
    public void OnDmaRead(uint address, byte value) { }

    public void OnInstruction(ushort pc)
    {
        int idx = board.Cart.DebugRomIndex(pc);
        if (idx >= 0)
        {
            code[idx] = (byte)(1 + Math.Min(2, pc >> 14));
            if (idx < 0x40 && pc < 0x40) vecRuns[idx]++;
            int d = pc - lastPc;
            if (lastPc >= 0 && (d < 1 || d > 4)) targets.Add(idx);
        }
        lastPc = pc; lastIdx = idx;
    }

    public void OnFrame()
    {
        slotPages.Add($"{board.Cart.DebugSlotPage(0)},{board.Cart.DebugSlotPage(1)}");
        var now = new byte[0x2000];
        for (int i = 0; i < now.Length; i++) now[i] = board.Peek((ushort)(0xC000 + i));
        if (ramPrev != null) for (int i = 0; i < now.Length; i++) if (now[i] != ramPrev[i]) ramChanged[i] = true;
        ramPrev = now;
    }

    public void OnPort(bool write, ushort port, byte value)
    {
        int p = port & 0xFF;
        bool interesting = p < 0x07 || (p >= 0x80 && p < 0xC0);
        if (!interesting || lastPc < 0) return;
        string key = $"{(lastIdx >= 0 ? "r" + lastIdx : "m" + lastPc)}:{(write ? "W" : "R")}:{p & (p >= 0x80 ? 0x81 : 0xFF):X2}";
        if (!sites.TryGetValue(key, out var s))
        {
            var pcu = (ushort)lastPc;
            s = new Site { Off = lastIdx, Pc = lastPc, Rw = write, Port = p >= 0x80 ? 0x80 | (p & 1) | 0x3E : p, Bytes = $"{board.Peek(pcu):X2}{board.Peek((ushort)(pcu + 1)):X2}{board.Peek((ushort)(pcu + 2)):X2}", Regs = $"C={board.Cpu.C:X2}" };
            sites[key] = s;
        }
        s.Count++;
        if (write && p >= 0x80 && (p & 1) == 0) { if (board.Vdp.CodeRegister == 3) s.Cram++; else s.Vram++; }
    }

    public void Write(string path)
    {
        var sb = new StringBuilder();
        sb.Append("{\n  \"romSize\": ").Append(code.Length).Append(",\n  \"slotPages\": [");
        sb.Append(string.Join(",", slotPages.Select(x => "\"" + x + "\""))).Append("],\n  \"code\": [");
        // instruction-start ROM offsets as [start, end) runs of executed bytes
        var runs = new List<string>();
        for (int i = 0; i < code.Length; i++) if (code[i] != 0) runs.Add($"[{i},{code[i] - 1}]");
        sb.Append(string.Join(",", runs)).Append("],\n  \"ramChanged\": [").Append(string.Join(",", Enumerable.Range(0, 0x2000).Where(i => ramChanged[i]).Select(i => 0xC000 + i)));
        sb.Append("],\n  \"dataRead\": [");
        var dr = new List<string>();
        for (int i = 0; i < dataRead.Length; i++) if (dataRead[i]) { int j = i; while (j < dataRead.Length && dataRead[j]) j++; dr.Add($"[{i},{j}]"); i = j; }
        sb.Append(string.Join(",", dr)).Append("],\n  \"vectorRuns\": [").Append(string.Join(",", vecRuns));
        sb.Append("],\n  \"targets\": [").Append(string.Join(",", targets.OrderBy(x => x))).Append("],\n  \"sites\": [\n");
        sb.Append(string.Join(",\n", sites.Values.OrderBy(s => s.Off).ThenBy(s => s.Pc).Select(s =>
            $"    {{\"off\": {s.Off}, \"pc\": {s.Pc}, \"write\": {(s.Rw ? "true" : "false")}, \"port\": {s.Port}, \"count\": {s.Count}, \"cram\": {s.Cram}, \"vram\": {s.Vram}, \"bytes\": \"{s.Bytes}\", \"regs\": \"{s.Regs}\"}}")));
        sb.Append("\n  ]\n}\n");
        File.WriteAllText(path, sb.ToString());
    }
}
