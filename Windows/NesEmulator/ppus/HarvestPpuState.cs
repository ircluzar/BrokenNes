using System;
using System.Text.Json;

namespace NesEmulator;

/// <summary>
/// The PPU harvest cores' state reader (PPU_NOFR, PPU_INES, PPU_FCEU). NES.SaveState writes a core's <see cref="PpuSharedState"/> with its own
/// PlainSerialize: byte arrays of 64 bytes or fewer (the palette, NOFR's pipeline) become JSON number arrays, longer ones base64 strings. A stock
/// JsonSerializer reads only the base64 form, threw on the palette and left the core un-restored (fuzz seed 311282's undo mismatch), so this reads both.
/// </summary>
internal static class HarvestPpuState
{
    public static PpuSharedState? From(object state)
    {
        if (state is PpuSharedState s) return s;
        if (state is not JsonElement je || je.ValueKind != JsonValueKind.Object) return null;
        var r = new PpuSharedState();
        byte[]? Bytes(string n)
        {
            if (!je.TryGetProperty(n, out var p)) return null;
            if (p.ValueKind == JsonValueKind.String) { try { return p.GetBytesFromBase64(); } catch { return null; } }
            if (p.ValueKind != JsonValueKind.Array) return null;
            var b = new byte[p.GetArrayLength()]; int i = 0;
            foreach (var el in p.EnumerateArray()) b[i++] = (byte)el.GetInt32();
            return b;
        }
        int I(string n) => je.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;
        bool B(string n) => je.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.True;
        r.vram = Bytes("vram") ?? r.vram; r.palette = Bytes("palette") ?? r.palette; r.oam = Bytes("oam") ?? r.oam; r.fixPipeline = Bytes("fixPipeline") ?? Array.Empty<byte>();
        r.PPUCTRL = (byte)I("PPUCTRL"); r.PPUMASK = (byte)I("PPUMASK"); r.PPUSTATUS = (byte)I("PPUSTATUS"); r.OAMADDR = (byte)I("OAMADDR");
        r.PPUADDR = (ushort)I("PPUADDR"); r.v = (ushort)I("v"); r.t = (ushort)I("t"); r.fineX = (byte)I("fineX");
        r.addrLatch = B("addrLatch"); r.scrollLatch = B("scrollLatch"); r.scanline = I("scanline"); r.scanlineCycle = I("scanlineCycle"); r.ppuDataBuffer = (byte)I("ppuDataBuffer");
        return r;
    }
}
