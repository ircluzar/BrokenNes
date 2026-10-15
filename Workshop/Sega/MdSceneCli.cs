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
            int ty = y + dy; if (ty < 0 || ty >= t.H) { bad += c.W; continue; }
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
        if (outDir != null) Directory.CreateDirectory(outDir);
        JsonElement[]? meta = null;
        if (metaPath != null && File.Exists(metaPath)) meta = JsonDocument.Parse(File.ReadAllText(metaPath)).RootElement.EnumerateArray().ToArray();

        var session = new MdSession(File.ReadAllBytes(rom), "M68K", "MD", "MD", region);
        var vdp = session.Board.Vdp;
        // our pictures: for each scene, the session's frame and the field pictures of the last two frames before the next press
        var cand = new List<Img>[count];
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
            }
            prevField = field;
        }

        long totalBad = 0; int compared = 0, identical = 0;
        for (int i = 0; i < count; i++)
        {
            if (only.Count > 0 && !only.Contains(i)) continue;
            if (cand[i] == null) { Console.WriteLine($"scene {i}: no picture captured"); continue; }
            string what = meta != null && i < meta.Length ? Describe(meta[i]) : "";
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
                for (int dy = 0; dy <= maxDy; dy++)
                    for (int dx = 0; dx <= maxDx; dx += 1)
                    {
                        // a cheap test on a few rows first
                        long probe = 0;
                        for (int y = 0; y < c.H && probe < 24; y += Math.Max(1, c.H / 8)) probe += Count(new Img { W = c.W, H = 1, Px = c.Px.AsSpan(y * c.W, c.W).ToArray() }, t, dx, dy + y, 24);
                        if (probe >= 24 && best != long.MaxValue) continue;
                        long bad = Count(c, t, dx, dy, best);
                        if (bad < best) { best = bad; bdx = dx; bdy = dy; bc = c; }
                    }
            }
            compared++; totalBad += best;
            if (best == 0) identical++;
            Console.WriteLine($"scene {i}: {(best == 0 ? "identical" : $"{best:N0} px differ")}  [{bc?.Name} {bc?.W}x{bc?.H} at ({bdx},{bdy}) in a {t.W}x{t.H} shot] {what}");
            if (best != 0 && bc != null && outDir != null)
            {
                Save(bc, Path.Combine(outDir, $"ours_{i:D3}.png"));
                var th = new Img { W = bc.W, H = bc.H, Px = new uint[bc.W * bc.H] };
                var df = new Img { W = bc.W, H = bc.H, Px = new uint[bc.W * bc.H] };
                for (int y = 0; y < bc.H; y++) for (int x = 0; x < bc.W; x++)
                    {
                        int tx = x + bdx, ty = y + bdy;
                        uint tv = tx < t.W && ty < t.H ? t.Px[ty * t.W + tx] : 0xFF000000u;
                        th.Px[y * bc.W + x] = tv;
                        df.Px[y * bc.W + x] = (bc.Px[y * bc.W + x] & 0xFFFFFF) == (tv & 0xFFFFFF) ? 0xFF000000u : 0xFFFF00FFu;
                    }
                Save(th, Path.Combine(outDir, $"theirs_{i:D3}.png")); Save(df, Path.Combine(outDir, $"diff_{i:D3}.png"));
            }
        }
        if (shotsDir != null) Console.WriteLine($"== {identical} of {compared} scenes identical, {totalBad:N0} differing pixels in all");
        return shotsDir == null || identical == compared ? 0 : 1;
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
