using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.Json;
using NesEmulator.Sega;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// <c>--md-scenes &lt;rom&gt; --count N [--shots dir] [--meta scenes.json] [--out dir] [--first 8] [--period 12] [--region pal] [--only i,j]</c>: the static-scene differential (Track B4a, <c>scenefuzz.py</c> makes the ROM).
/// The ROM shows a scene (a complete VDP state) until Start is pressed, then the next. Our side presses Start every <c>--period</c> frames from frame <c>--first</c> and looks at the picture just before the next
/// press; <c>--shots</c> is a folder of the oracle's screenshots <c>s000.png ...</c> of the same scenes (one per scene, taken after the scene had been set up). Each shot is compared with our picture of the scene: the
/// frame as the session shows it (woven double-height picture in interlace mode 2), and the one field the VDP drew in the last two frames; the screenshot's offset (the oracle surrounds the picture with a border) is searched.
/// Without <c>--shots</c> the pictures of our side are written (<c>--out</c>) so the ROM and the scenes can be looked at. Exit code 0 when every compared scene is identical.
/// </summary>
internal static class MdSceneCli
{
    private static readonly SortedDictionary<string, int> SprStat = new();
    private static int ClassifyXMin = 0, ClassifyXMax = int.MaxValue, ClassifyYMin = 0, ClassifyYMax = int.MaxValue, ClassifyMax = 30;
    private static bool ClassifyAll;

    private sealed class Img { public int W, H; public uint[] Px = Array.Empty<uint>(); public string Name = ""; }

    private static Img Copy(uint[] src, int w, int h, int srcStride, string name)
    {
        var im = new Img { W = w, H = h, Px = new uint[w * h], Name = name };
        for (int y = 0; y < h; y++) Array.Copy(src, y * srcStride, im.Px, y * w, w);
        return im;
    }

    private static void Save(Img im, string path)
    {
        using var bmp = new Bitmap(im.W, im.H, PixelFormat.Format32bppArgb);
        for (int y = 0; y < im.H; y++) for (int x = 0; x < im.W; x++) bmp.SetPixel(x, y, Color.FromArgb((int)im.Px[y * im.W + x]));
        bmp.Save(path, ImageFormat.Png);
    }

    /// <summary>Differing pixels between a candidate and the shot placed at (dx, dy) of the shot (stops counting above <paramref name="stop"/>).</summary>
    private static long Count(Img c, Img t, int dx, int dy, long stop)
    {
        long bad = 0;
        for (int y = 0; y < c.H; y++)
        {
            int ty = y + dy; if (ty < 0 || ty >= t.H) continue;      // a row the oracle's window cuts off (its V30 picture starts a line above the window) is not compared
            for (int x = 0; x < c.W; x++)
            {
                int tx = x + dx;
                if (tx < 0 || tx >= t.W || (c.Px[y * c.W + x] & 0xFFFFFF) != (t.Px[ty * t.W + tx] & 0xFFFFFF)) bad++;
            }
            if (bad > stop) return bad;
        }
        return bad;
    }

    public static int Run(string[] args)
    {
        string? rom = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null;
        if (rom == null) { Console.WriteLine("usage: --md-scenes <rom> --count N [--shots dir] [--meta json] [--out dir] [--first 8] [--period 12] [--region pal] [--only 3,5]"); return 2; }
        int count = int.TryParse(M68kTestCli.Arg(args, "--count"), out int c0) ? c0 : 20;
        int first = int.TryParse(M68kTestCli.Arg(args, "--first"), out int f0) ? f0 : 8;
        int period = int.TryParse(M68kTestCli.Arg(args, "--period"), out int p0) ? p0 : 12;
        string? shotsDir = M68kTestCli.Arg(args, "--shots"), outDir = M68kTestCli.Arg(args, "--out"), metaPath = M68kTestCli.Arg(args, "--meta");
        var only = (M68kTestCli.Arg(args, "--only") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToHashSet();
        RegionChoice? region = M68kTestCli.Arg(args, "--region")?.ToLowerInvariant() switch { "ntsc" => RegionChoice.Ntsc, "pal" => RegionChoice.Pal, _ => null };
        if (M68kTestCli.Arg(args, "--slot-set") is { } slotSet) MdRunCli.ApplySlotSet(slotSet);
        if (int.TryParse(M68kTestCli.Arg(args, "--xmin"), out int xmn)) ClassifyXMin = xmn;
        if (int.TryParse(M68kTestCli.Arg(args, "--xmax"), out int xmx)) ClassifyXMax = xmx;
        if (int.TryParse(M68kTestCli.Arg(args, "--ymin"), out int ymn)) ClassifyYMin = ymn;
        if (int.TryParse(M68kTestCli.Arg(args, "--ymax"), out int ymx)) ClassifyYMax = ymx;
        if (int.TryParse(M68kTestCli.Arg(args, "--max"), out int cmx)) ClassifyMax = cmx;
        ClassifyAll = M68kTestCli.Flag(args, "--all");
        if (outDir != null) Directory.CreateDirectory(outDir);
        // pinned pictures: --hash-out <tsv> --set <name> appends one line per scene that was pixel-identical with the oracle (set, scene, candidate picture, hash); --hash-check <tsv> --set <name> (no shots needed) compares
        // the current build's pictures with those lines
        string? hashOut = M68kTestCli.Arg(args, "--hash-out"), hashCheck = M68kTestCli.Arg(args, "--hash-check");
        string setName = M68kTestCli.Arg(args, "--set") ?? Path.GetFileNameWithoutExtension(rom);
        var pinned = new SortedDictionary<int, (int ci, string hash)>();
        if (hashCheck != null)
            foreach (var line in File.ReadAllLines(hashCheck))
            {
                var f = line.Split('\t');
                if (f.Length >= 4 && f[0] == setName && int.TryParse(f[1], out int pi) && int.TryParse(f[2], out int pc)) pinned[pi] = (pc, f[3]);
            }
        JsonElement[]? meta = null;
        if (metaPath != null && File.Exists(metaPath)) meta = JsonDocument.Parse(File.ReadAllText(metaPath)).RootElement.EnumerateArray().ToArray();

        var session = new MdSession(File.ReadAllBytes(rom), "M68K", "MD", "MD", region);
        var vdp = session.Board.Vdp;
        // our pictures: for each scene, the session's frame and the field pictures of the last two frames before the next press
        var cand = new List<Img>[count];
        var cramAt = new ushort[count][];
        var layersAt = new byte[count][][];
        var layersPrevAt = new byte[count][][];
        byte[][]? prevLayers = null;
        var vramAt = new byte[count][]; var regAt = new byte[count][]; var cacheAt = new byte[count][];
        int sprLineY = int.TryParse(M68kTestCli.Arg(args, "--sprline"), out int sly) ? sly : -1;
        if (M68kTestCli.Flag(args, "--classify") || M68kTestCli.Flag(args, "--sprstat")) vdp.DebugKeepLayers = true;
        Img? prevField = null;
        int total = first + (count - 1) * period;
        for (int f = 0; f < total; f++)
        {
            // Start is held for three frames at the press that ends scene k (k = 0 .. count-2) and begins scene k+1
            int rel = f - first;
            bool pressing = rel >= 0 && rel % period < 3 && rel / period < count - 1;
            session.SetPad(0, pressing ? PadButtons.Start : PadButtons.None);
            session.RunFrame();
            var field = Copy(vdp.Picture, vdp.PictureWidth, vdp.PictureHeight, MdVdp.PictureStride, "field");
            // scene i is looked at in the frame before the press that ends it
            int si = (f + 1 - first) / period;
            if (f + 1 - first >= 0 && (f + 1 - first) % period == 0 && si < count)
            {
                var list = new List<Img>();
                list.Add(Copy(session.Frame, session.FrameWidth, session.FrameHeight, session.FrameWidth, session.FrameHeight > 240 ? "woven" : "frame"));
                list.Add(field);
                if (prevField != null) list.Add(prevField);
                cand[si] = list;
                cramAt[si] = (ushort[])vdp.Cram.Clone();
                if (sprLineY >= 0) { vramAt[si] = (byte[])vdp.Vram.Clone(); regAt[si] = (byte[])vdp.Reg.Clone(); cacheAt[si] = (byte[])vdp.SpriteCache.Clone(); }
                if (vdp.DebugKeepLayers && vdp.DebugA != null) { layersAt[si] = new[] { (byte[])vdp.DebugA.Clone(), (byte[])vdp.DebugB!.Clone(), (byte[])vdp.DebugS!.Clone() }; layersPrevAt[si] = prevLayers; }
            }
            prevField = field;
            if (vdp.DebugKeepLayers && vdp.DebugA != null) prevLayers = new[] { (byte[])vdp.DebugA.Clone(), (byte[])vdp.DebugB!.Clone(), (byte[])vdp.DebugS!.Clone() };
        }

        if (hashCheck != null)
        {
            int ok = 0, bad = 0;
            foreach (var kv in pinned)
            {
                if (only.Count > 0 && !only.Contains(kv.Key)) continue;
                if (kv.Key >= count || cand[kv.Key] == null || kv.Value.ci >= cand[kv.Key].Count) { bad++; Console.WriteLine($"scene {kv.Key}: no picture to check"); continue; }
                string h = PictureHash(cand[kv.Key][kv.Value.ci]);
                if (h == kv.Value.hash) ok++; else { bad++; Console.WriteLine($"scene {kv.Key}: CHANGED {h} (pinned {kv.Value.hash}, candidate {kv.Value.ci})"); }
            }
            if (pinned.Count == 0) { Console.WriteLine($"no pinned scenes for set '{setName}' in {hashCheck}"); return 1; }
            Console.WriteLine($"== pinned scene check '{setName}': {ok} of {ok + bad} match");
            return bad == 0 ? 0 : 1;
        }

        long totalBad = 0; int compared = 0, identical = 0;        for (int i = 0; i < count; i++)
        {
            if (only.Count > 0 && !only.Contains(i)) continue;
            if (cand[i] == null) { Console.WriteLine($"scene {i}: no picture captured"); continue; }
            string what = meta != null && i < meta.Length ? Describe(meta[i]) : "";
            if (sprLineY >= 0 && vramAt[i] != null) SpriteLine(vramAt[i], regAt[i], sprLineY, cacheAt[i]);
            if (shotsDir == null)
            {
                if (outDir != null) for (int k = 0; k < cand[i].Count; k++) Save(cand[i][k], Path.Combine(outDir, $"ours_{i:D3}_{cand[i][k].Name}{k}.png"));
                Console.WriteLine($"scene {i}: {cand[i][0].W}x{cand[i][0].H} {what}");
                continue;
            }
            string sp = Path.Combine(shotsDir, $"s{i:D3}.png");
            if (!File.Exists(sp)) { Console.WriteLine($"scene {i}: no shot"); continue; }
            var shot = MdFindCli.Load(sp);
            var t = new Img { W = shot.w, H = shot.h, Px = shot.px };
            long best = long.MaxValue; int bdx = 0, bdy = 0; Img? bc = null;
            foreach (var c in cand[i])
            {
                // the shot has a border of its own around the picture: search the offsets that fit
                int maxDx = Math.Max(0, t.W - c.W), maxDy = Math.Max(0, t.H - c.H);
                for (int dy = -2; dy <= maxDy + 2; dy++)
                    for (int dx = 0; dx <= maxDx; dx += 1)
                    {
                        long bad = Count(c, t, dx, dy, best);
                        if (bad < best) { best = bad; bdx = dx; bdy = dy; bc = c; }
                    }
            }
            if (M68kTestCli.Flag(args, "--sprstat") && bc != null && layersAt[i] != null && cramAt[i] != null && bc.W == cand[i][0].W && bc.H == cand[i][0].H && bc.Name != "woven") SpriteStats(bc, t, bdx, bdy, cramAt[i], layersAt[i]);
            compared++; totalBad += best;
            if (best == 0) identical++;
            if (best == 0 && hashOut != null && bc != null) File.AppendAllText(hashOut, $"{setName}\t{i}\t{cand[i].IndexOf(bc)}\t{PictureHash(bc)}\n");
            Console.WriteLine($"scene {i}: {(best == 0 ? "identical" : $"{best:N0} px differ")}  [{bc?.Name} {bc?.W}x{bc?.H} at ({bdx},{bdy}) in a {t.W}x{t.H} shot] {what}");
            if (best != 0 && bc != null && M68kTestCli.Flag(args, "--classify") && cramAt[i] != null) Classify(bc, t, bdx, bdy, cramAt[i], cand[i].Count > 2 && ReferenceEquals(bc, cand[i][2]) ? layersPrevAt[i] : layersAt[i], bc.Name != "woven" && (ReferenceEquals(bc, cand[i][0]) || cand[i].Count > 1 && ReferenceEquals(bc, cand[i][1]) || cand[i].Count > 2 && ReferenceEquals(bc, cand[i][2])));
            if (best != 0 && bc != null && outDir != null)
            {
                Save(bc, Path.Combine(outDir, $"ours_{i:D3}.png"));
                var th = new Img { W = bc.W, H = bc.H, Px = new uint[bc.W * bc.H] };
                var df = new Img { W = bc.W, H = bc.H, Px = new uint[bc.W * bc.H] };
                for (int y = 0; y < bc.H; y++) for (int x = 0; x < bc.W; x++)
                    {
                        int tx = x + bdx, ty = y + bdy;
                        uint tv = tx >= 0 && tx < t.W && ty >= 0 && ty < t.H ? t.Px[ty * t.W + tx] : (bc.Px[y * bc.W + x] & 0xFFFFFF) | 0xFF000000u;      // a row or column outside the shot counts as equal
                        th.Px[y * bc.W + x] = tv;
                        df.Px[y * bc.W + x] = (bc.Px[y * bc.W + x] & 0xFFFFFF) == (tv & 0xFFFFFF) ? 0xFF000000u : 0xFFFF00FFu;
                    }
                Save(th, Path.Combine(outDir, $"theirs_{i:D3}.png")); Save(df, Path.Combine(outDir, $"diff_{i:D3}.png"));
            }
        }
        if (SprStat.Count > 0) { Console.WriteLine("sprite pixel statistics (sprite priority, plane A, plane B -> the brightness the oracle shows the sprite pixel in):"); foreach (var kv in SprStat) Console.WriteLine($"  {kv.Key} {kv.Value}"); }
        if (shotsDir != null) Console.WriteLine($"== {identical} of {compared} scenes identical, {totalBad:N0} differing pixels in all");
        return shotsDir == null || identical == compared ? 0 : 1;
    }

    /// <summary>What kind of difference each differing pixel is, by looking at the colour RAM: the same entry in another brightness (shadow/highlight), another entry, or a colour that is no entry at all.</summary>
    private static void Classify(Img c, Img t, int dx, int dy, ushort[] cram, byte[][]? layers, bool plain)
    {
        static uint Lv(int v, int mode) { int[] L = { 0, 27, 49, 71, 87, 103, 119, 130, 146, 157, 174, 190, 206, 228, 255 }; return (uint)(mode == 0 ? L[v * 2] : mode == 1 ? L[v] : L[v + 7]); }
        var map = new Dictionary<uint, List<(int e, int m)>>();
        for (int e = 0; e < 64; e++) for (int m = 0; m < 3; m++)
            {
                int col = cram[e]; uint rgb = Lv(col >> 1 & 7, m) << 16 | Lv(col >> 5 & 7, m) << 8 | Lv(col >> 9 & 7, m);
                if (!map.TryGetValue(rgb, out var l)) map[rgb] = l = new List<(int, int)>(); l.Add((e, m));
            }
        var kinds = new SortedDictionary<string, int>(); int shown = 0;
        var firsts = new List<string>();
        for (int y = 0; y < c.H; y++) for (int x = 0; x < c.W; x++)
            {
                int tx = x + dx, ty = y + dy; if (tx < 0 || tx >= t.W || ty < 0 || ty >= t.H) continue;
                if (x < ClassifyXMin || x > ClassifyXMax || y < ClassifyYMin || y > ClassifyYMax) continue;
                uint a = c.Px[y * c.W + x] & 0xFFFFFF, b = t.Px[ty * t.W + tx] & 0xFFFFFF;
                if (a == b)
                {
                    if (ClassifyAll && layers != null && plain && shown < ClassifyMax) { shown++; int o = y * MdVdp.PictureStride + x; Console.WriteLine($"      ({x},{y}) A={layers[0][o]:X2} B={layers[1][o]:X2} S={layers[2][o]:X2} both {a:X6}"); }
                    continue;
                }
                map.TryGetValue(a, out var la); map.TryGetValue(b, out var lb);
                string k;
                if (la == null || lb == null) k = $"{(la == null ? "ours-not-a-palette-colour" : "ours-ok")}/{(lb == null ? "theirs-not-a-palette-colour" : "theirs-ok")}";
                else if (la.Any(p => lb.Any(q => p.e == q.e))) { var p = la.First(p => lb.Any(q => p.e == q.e)); var q = lb.First(q => q.e == p.e); k = $"same-entry mode {p.m}->{q.m}"; }
                else k = "other entry" + (la.Any(p => lb.Any(q => p.m != q.m)) ? " (modes differ)" : " (same modes)");
                kinds[k] = kinds.GetValueOrDefault(k) + 1;
                if (firsts.Count < 6) firsts.Add($"({x},{y}) {k}");
                if (layers != null && plain && shown < ClassifyMax)
                {
                    shown++; int o = y * MdVdp.PictureStride + x;
                    string Ents(List<(int e, int m)>? l) => l == null ? "-" : string.Join("|", l.Take(3).Select(p => $"{p.e:X2}{"nsh"[p.m]}"));
                    Console.WriteLine($"      ({x},{y}) A={layers[0][o]:X2} B={layers[1][o]:X2} S={layers[2][o]:X2} ours {a:X6} [{Ents(la)}] theirs {b:X6} [{Ents(lb)}] {k}");
                }
            }
        Console.WriteLine("    classes: " + string.Join("; ", kinds.Select(kv => $"{kv.Key} {kv.Value}")) + "   e.g. " + string.Join(" ", firsts.Take(4)));
    }

    /// <summary>For the pixels where a plain sprite (not an operator) is on top in our index stage: which brightness the oracle shows it in, keyed by what lies under it.</summary>
    private static void SpriteStats(Img c, Img t, int dx, int dy, ushort[] cram, byte[][] layers)
    {
        static uint Lv(int v, int mode) { int[] L = { 0, 27, 49, 71, 87, 103, 119, 130, 146, 157, 174, 190, 206, 228, 255 }; return (uint)(mode == 0 ? L[v * 2] : mode == 1 ? L[v] : L[v + 7]); }
        for (int y = 0; y < c.H; y++) for (int x = 0; x < c.W; x++)
            {
                int o = y * MdVdp.PictureStride + x;
                int sv = layers[2][o], av = layers[0][o], bv = layers[1][o];
                if ((sv & 15) == 0 || (sv & 0x3F) == 0x3E || (sv & 0x3F) == 0x3F) continue;
                int tx = x + dx, ty = y + dy; if (tx < 0 || tx >= t.W || ty < 0 || ty >= t.H) continue;
                uint b = t.Px[ty * t.W + tx] & 0xFFFFFF;
                int col = cram[sv & 0x3F];
                string shown = "other";
                for (int m = 0; m < 3; m++) { uint rgb = Lv(col >> 1 & 7, m) << 16 | Lv(col >> 5 & 7, m) << 8 | Lv(col >> 9 & 7, m); if (rgb == b) { shown = m == 0 ? "normal" : m == 1 ? "shadow" : "highlight"; break; } }
                string pa = (av & 15) == 0 ? ((av & 0x80) != 0 ? "A:clear+prio" : "A:clear") : ((av & 0x80) != 0 ? "A:hi" : "A:lo");
                string pb = (bv & 15) == 0 ? ((bv & 0x80) != 0 ? "B:clear+prio" : "B:clear") : ((bv & 0x80) != 0 ? "B:hi" : "B:lo");
                string k = $"{((sv & 0x80) != 0 ? "S:hi" : "S:lo")}{((sv & 15) >= 14 ? " c" + (sv & 15) : "")} {pa} {pb} -> {shown}";
                SprStat[k] = SprStat.GetValueOrDefault(k) + 1;
            }
    }

    /// <summary>Tool: the sprites on picture line <paramref name="y"/> (a field line in interlace mode 2: picture row 2y and 2y+1), in link order, with the cell count so far and the per-line limits.</summary>
    private static void SpriteLine(byte[] vram, byte[] reg, int y, byte[] cache)
    {
        bool h40 = (reg[12] & 0x81) == 0x81, dbl = (reg[12] & 6) == 6;
        int maxSprites = h40 ? 80 : 64, sat = (reg[5] & (h40 ? 0x7E : 0x7F)) << 9, cells = 0;
        Console.WriteLine($"    sprites on line {y} ({(h40 ? "H40" : "H32")}{(dbl ? ", double rows 2y and 2y+1" : "")}), SAT at {sat:X4}:");
        int idx = 0, onLine = 0;
        for (int n = 0; n < maxSprites; n++)
        {
            int a = sat + idx * 8;
            int ypos = (cache[idx * 4] << 8 | cache[idx * 4 + 1]) & (MdVdp.SpriteY9 && !dbl ? 0x1FF : 0x3FF), size = cache[idx * 4 + 2], link = cache[idx * 4 + 3] & 0x7F;
            int xraw = (vram[a + 6 & 0xFFFF] << 8 | vram[a + 7 & 0xFFFF]) & 0x1FF;
            int w = (size >> 2 & 3) + 1, h = (size & 3) + 1;
            int top = ypos - (dbl ? 256 : 128), rows = h << (dbl ? 4 : 3);
            int row = dbl ? 2 * y : y;
            bool hit = row + (dbl ? 1 : 0) >= top && row < top + rows;
            if (hit) { onLine++; cells += w; }
            if (hit) Console.WriteLine($"      #{onLine} sprite {idx}: x {xraw - 128} (raw {xraw}) w {w} cells h {h}, top row {top}, cells so far {cells}");
            idx = link; if (idx == 0 || idx >= (MdVdp.SpriteLinks80 ? 80 : maxSprites)) break;
        }
        Console.WriteLine($"      {onLine} sprites, {cells} cells on the line (limits {(h40 ? "20 sprites, 40 cells" : "16 sprites, 32 cells")})");
    }

    /// <summary>FNV-1a over the size and the 24-bit colours of a picture.</summary>
    private static string PictureHash(Img c)
    {
        ulong h = 14695981039346656037UL;
        h = (h ^ (ulong)c.W) * 1099511628211UL; h = (h ^ (ulong)c.H) * 1099511628211UL;
        for (int i = 0; i < c.W * c.H; i++) { h ^= c.Px[i] & 0xFFFFFF; h *= 1099511628211UL; }
        return h.ToString("x16");
    }

    private static string Describe(JsonElement m)
    {
        try
        {
            var parts = new List<string>();
            parts.Add(m.GetProperty("h40").GetBoolean() ? "H40" : "H32");
            if (m.GetProperty("v30").GetBoolean()) parts.Add("V30");
            int il = m.GetProperty("interlace").GetInt32(); if (il != 0) parts.Add("interlace" + (il == 3 ? "2" : il.ToString()));
            if (m.GetProperty("shadow").GetBoolean()) parts.Add("S/H");
            if (m.GetProperty("window_on").GetBoolean()) parts.Add("window");
            var r = m.GetProperty("regs");
            int r11 = r.GetProperty("11").GetInt32(), r16 = r.GetProperty("16").GetInt32();
            parts.Add($"hs{r11 & 3} vs{(r11 >> 2) & 1} size{r16 & 3}x{(r16 >> 4) & 3}");
            return string.Join(" ", parts);
        }
        catch { return ""; }
    }
}
