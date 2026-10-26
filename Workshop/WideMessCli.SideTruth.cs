using System;
using System.Collections.Generic;
using System.Linq;
using NesEmulator.Snes;
using NesEmulator.Systems;

namespace BrokenNes.Workshop;

internal static partial class WideMessCli
{
    /// <summary>
    /// <c>--wms-side-truth (--game KEY | --rom game.sfc [--input S]) --layer bg1 [--frames N] [--scratch-profile p.json] [--all-extend] [--show-bands N] [--detail N]</c>: how true one
    /// layer's side columns are (spec 6.3). The layer is captured (the picture chip's tool capture: the game is unaffected) and its scroll followed across frames (HOFS and VOFS
    /// unwrapped; a step of more than 64 pixels, or a new map or BGMODE, starts again). Every side column a frame shows is a claim: "at this place of the layer (unwrapped x, from
    /// the unwrapped VOFS down) the game's map looks like this". When the place later scrolls into the SNES picture, the SNES picture is the truth, on the world rows both show:
    /// the claim is right (the same pixels, but for at most 2% of those rows), empty (the layer shows nothing there, a filled side, where the truth shows something) or wrong.
    /// The pixels are the layer's own colours (before windows, colour math and brightness). The truth is taken 8 columns or more inside the SNES picture's edges (<c>--truth-margin</c>),
    /// where games have finished drawing a column that scrolls in.
    /// <para>Per line (milestone 6 climb): a frame's claims and truths use only its band, the lines the picture shows (not forced blank, brightness above 0) whose scroll, map and
    /// BGMODE are the first shown line's. Lines of another scroll (a status bar, a parallax split, blank lines at the top for DMA) show other places and are left out; a frame
    /// with fewer than 32 such lines is skipped, as is a pair of claim and truth that share fewer than 32 world rows (the claim waits).</para>
    /// </summary>
    /// <summary>The last <see cref="SideTruth"/> run's counts (for the checks, E8).</summary>
    [ThreadStatic] internal static (long right, long empty, long wrong) LastSideTruth;
    internal static (long right, long empty, long wrong) SideTruthCounts(string[] args) { SideTruth(args); return LastSideTruth; }

    public static int SideTruth(string[] args)
    {
        LastSideTruth = (0, 0, 0);
        RomTestCli.EnsureConsole();
        var g = GameArgs(args);
        string layerName = Opt(args, "layer", "bg1");
        int layer = Array.IndexOf(new[] { "bg1", "bg2", "bg3", "bg4" }, layerName);
        if (g == null || layer < 0) { Console.Error.WriteLine("usage: --wms-side-truth (--game KEY | --rom game.sfc [--input S]) --layer bg1..bg4 [--frames N] [--scratch-profile p.json] [--all-extend]"); return 2; }
        var (rom, script, moments, name) = g.Value;
        int frames = int.Parse(Opt(args, "frames", moments.Max().ToString()));
        using var s = EnhancedSession(rom, args, out string note);
        if (s == null) return 1;
        var ppu = s.WideMessPpu!;
        ppu.CompareEnhanced = true;   // the SNES chip draws each line too: it fills DebugLineRegisters
        ppu.DebugCaptureLayer = layer;   // the layer's own colours, before windows and colour math (the sky's fixed-colour gradients and fades would change every pixel)
        var pad = ParsePad(script);
        const int W = PPU_WMS.WideWidth, O = PPU_WMS.SnesOrigin;
        const ushort NotShown = 0xFFFF;   // a row outside the frame's band (a BGR555 colour never sets bit 15; Clear is 0x8000)
        // claims by place: the unwrapped x of the layer; each a column of world rows from its top (the unwrapped VOFS of the frame it was made in)
        var pending = new Dictionary<long, List<(int frame, long top, ushort[] col)>>();
        long u = 0, vu = 0; int prevH = -1, prevV = -1, prevSig = -1;
        long right = 0, empty = 0, wrong = 0, skipped = 0, claims = 0, bandRows = 0, shownRows = 0, sidePixels = 0, sideFilled = 0;
        var firstWrong = new List<int>();
        int detail = int.Parse(Opt(args, "detail", "0")), showBands = int.Parse(Opt(args, "show-bands", "0"));
        // the truth is taken 8 columns or more inside the SNES picture's edges: games finish drawing a column of their map as it scrolls in there (and hide the seam there),
        // so the outermost columns can show a place before it is drawn (--truth-margin 0: the first version's every column)
        int margin = int.Parse(Opt(args, "truth-margin", "8"));
        int at = 0; PadButtons held = 0;
        var inBand = new bool[PPU_SFC.MaxHeight];
        for (int f = 1; f <= frames; f++)
        {
            PlayTo(s, pad, ref at, f, ref held);
            var lines = ppu.DebugLineRegisters!;
            int h = ppu.VisibleHeight;
            if (showBands > 0 && f % showBands == 0)
            {
                var sb = new System.Text.StringBuilder($"  bands {f} (width {s.FrameWidth}):");
                int st = 1;
                for (int line = 2; line <= h + 1; line++)
                    if (line == h + 1 || Hofs(lines[line], layer) != Hofs(lines[st], layer) || Vofs(lines[line], layer) != Vofs(lines[st], layer) || Shown(lines[line]) != Shown(lines[st]))
                    { sb.Append($" {st - 1}-{line - 2}:{(Shown(lines[st]) ? "" : "blank ")}{Hofs(lines[st], layer)},{Vofs(lines[st], layer)}"); st = line; }
                Console.WriteLine(sb.ToString());
            }
            int refLine = 1;
            while (refLine <= h && !Shown(lines[refLine])) refLine++;
            if (s.FrameWidth != W || refLine > h) { prevH = -1; skipped++; continue; }
            var rr = lines[refLine];
            int hofs = Hofs(rr, layer), vofs = Vofs(rr, layer), sig = Sc(rr, layer) | rr.Bgmode << 8;
            int band = 0;
            for (int line = 1; line <= h; line++)
            {
                var r = lines[line];
                bool shown = Shown(r);
                if (shown) shownRows++;
                inBand[line - 1] = shown && Hofs(r, layer) == hofs && Vofs(r, layer) == vofs && Sc(r, layer) == Sc(rr, layer) && r.Bgmode == rr.Bgmode;
                if (inBand[line - 1]) band++;
            }
            bandRows += band;
            bool cut = prevH < 0 || sig != prevSig;
            int d = 0, dv = 0;
            if (!cut)
            {
                d = ((hofs - prevH + 512) & 1023) - 512; dv = ((vofs - prevV + 512) & 1023) - 512;
                if (Math.Abs(d) > 64 || Math.Abs(dv) > 64) cut = true;
            }
            if (cut) { pending.Clear(); u = hofs; vu = vofs; } else { u += d; vu += dv; }
            prevH = hofs; prevV = vofs; prevSig = sig;
            if (band < 32) { skipped++; continue; }   // too little of the layer's main scroll shown to judge by
            var fr = ppu.DebugCapture;
            if (fr == null) { skipped++; continue; }
            ushort[] Column(int c) { var col = new ushort[h]; for (int y = 0; y < h; y++) col[y] = inBand[y] ? fr[y * W + c] : NotShown; return col; }
            // the truth: every SNES column of this frame settles the claims made about its place, on the world rows both show
            for (int c = O + margin; c < O + 256 - margin; c++)
            {
                long key = u + (c - O);
                if (!pending.TryGetValue(key, out var list)) continue;
                var truth = Column(c);
                bool anyTruth = false;
                for (int y = 0; y < h && !anyTruth; y++) anyTruth = truth[y] != NotShown && truth[y] != PPU_WMS.CaptureClear;
                if (!anyTruth) continue;   // the layer shows nothing there (or is off on this frame): no truth to judge by
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var (frame, top, col) = list[i];
                    int common = 0, differ = 0, claimed = 0, fy = -1;
                    for (int y = 0; y < h; y++)
                    {
                        long cy = vu + y - top;
                        if (cy < 0 || cy >= h || truth[y] == NotShown || col[cy] == NotShown) continue;
                        common++;
                        if (col[cy] != PPU_WMS.CaptureClear) claimed++;
                        if (col[cy] != truth[y]) { differ++; if (fy < 0) fy = y; }
                    }
                    if (common < 32) continue;   // the two share too few world rows: the claim waits for a better view
                    if (differ <= common / 50) right++;   // right: all but at most 2% of the rows both show (a row at the map's edge the game streams vertically)
                    else if (claimed == 0) empty++;
                    else
                    {
                        wrong++;
                        if (firstWrong.Count < 8 && (firstWrong.Count == 0 || firstWrong[^1] != frame)) firstWrong.Add(frame);
                        if (detail-- > 0)
                            Console.WriteLine($"    wrong: claimed at frame {frame}, shown at frame {f} column {c}: {differ} of {common} rows differ, first row {fy}: {col[vu + fy - top]:X4} vs {truth[fy]:X4}");
                    }
                    list.RemoveAt(i);
                }
                if (list.Count == 0) pending.Remove(key);
            }
            // the claims: the side columns
            for (int c = 0; c < W; c++)
            {
                if (c == O) c = O + 256;
                for (int y = 0; y < h; y++)
                    if (inBand[y]) { sidePixels++; if (fr[y * W + c] != PPU_WMS.CaptureClear) sideFilled++; }
                long key = u + (c - O);
                if (!pending.TryGetValue(key, out var list)) pending[key] = list = new List<(int, long, ushort[])>();
                if (list.Count < 4) { list.Add((f, vu, Column(c))); claims++; }
            }
            if (f % 600 == 0) foreach (var k in pending.Where(kv => kv.Value[^1].frame < f - 600).Select(kv => kv.Key).ToList()) pending.Remove(k);
        }
        long judged = right + empty + wrong;
        LastSideTruth = (right, empty, wrong);
        double P(long n) => judged == 0 ? 0 : 100.0 * n / judged;
        Console.WriteLine($"{name}, {layerName}, {note}: {frames:N0} frames ({skipped:N0} skipped: blank, another width or under 32 lines of the layer's main scroll; {(shownRows == 0 ? 0 : 100.0 * bandRows / shownRows):F0}% of the shown lines in the band), {claims:N0} side columns claimed, {judged:N0} later shown by the SNES picture:");
        Console.WriteLine($"  right {right:N0} ({P(right):F1}%), empty {empty:N0} ({P(empty):F1}%), wrong {wrong:N0} ({P(wrong):F1}%){(firstWrong.Count > 0 ? $"; wrong claims made at frames {string.Join(", ", firstWrong)}..." : "")}");
        Console.WriteLine($"  the sides as shown: the layer draws {(sidePixels == 0 ? 0 : 100.0 * sideFilled / sidePixels):F1}% of the side pixels of its main scroll (every frame judged, whether or not the place is shown later)");
        var mem = ppu.MemoryStats(layer);
        if (mem != null) Console.WriteLine($"  map memory: {mem}");
        return 0;

        static int Hofs(PPU_SFC.RegisterSnapshot r, int bg) => bg switch { 0 => r.Bg1hofs, 1 => r.Bg2hofs, 2 => r.Bg3hofs, _ => r.Bg4hofs } & 0x3FF;
        static int Vofs(PPU_SFC.RegisterSnapshot r, int bg) => bg switch { 0 => r.Bg1vofs, 1 => r.Bg2vofs, 2 => r.Bg3vofs, _ => r.Bg4vofs } & 0x3FF;
        static int Sc(PPU_SFC.RegisterSnapshot r, int bg) => bg switch { 0 => r.Bg1sc, 1 => r.Bg2sc, 2 => r.Bg3sc, _ => r.Bg4sc };
        static bool Shown(PPU_SFC.RegisterSnapshot r) => (r.Inidisp & 0x80) == 0 && (r.Inidisp & 0x0F) != 0;
    }
}
