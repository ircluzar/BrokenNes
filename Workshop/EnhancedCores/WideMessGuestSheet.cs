using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using NesEmulator.Mix;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.EnhancedCores;

/// <summary>
/// <c>--wms-guest-sheet --out dir [--only "name,name"] [--scale 2]</c>: contact sheets of the user's games on the WideMess widescreen chip (WideMess milestone 7b): for each game, at a few moments of one run, the game on its
/// own picture chip beside the same frame on the widescreen chip (400 wide), with the guest's edges marked above it and the chip's rules and counts written under it. One PNG a game, named after it.
/// </summary>
internal static class WideMessGuestSheet
{
    public static int Run(string[] args)
    {
        RomTestCli.EnsureConsole();
        Consoles.WideMessEnabled = true;
        string Opt(string name, string def) { int i = Array.FindIndex(args, a => a.Equals("--" + name, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : def; }
        string outDir = Opt("out", "");
        if (outDir == "") { Console.Error.WriteLine("usage: --wms-guest-sheet --out dir [--only \"Super Mario Bros.,Sonic the Hedgehog (Genesis)\"] [--scale 2]"); return 2; }
        Directory.CreateDirectory(outDir);
        int scale = int.Parse(Opt("scale", "2"));
        var only = Opt("only", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int made = 0;
        foreach (var g in WideMessGuestTests.Games())
        {
            if (only.Length > 0 && !only.Any(o => g.Name.Contains(o, StringComparison.OrdinalIgnoreCase))) continue;
            if (!File.Exists(g.Path)) { Console.WriteLine($"{g.Name}: no ROM at {g.Path}"); continue; }
            string file = Path.Combine(outDir, Slug(g.Name) + ".png");
            Sheet(g, file, scale);
            made++;
        }
        Console.WriteLine($"{made} sheet(s) in {outDir}");
        return 0;
    }

    private static string Slug(string name) => new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Replace("--", "-").Trim('-');

    private static void Sheet(WideMessGuestTests.Game g, string file, int scale)
    {
        using var run = new WideMessGuestTests.Run(g, withOwn: true, wideApu: g.Console == ConsoleKind.Nes ? "WMSS" : "WMS:WMSS");
        const int pad = 12, label = 18, notes = 34;
        int wideW = 400 * scale, ownMax = 320 * scale;
        var rows = new System.Collections.Generic.List<(Bitmap own, Bitmap wide, string head, string rules, string counts, int side)>();
        int moment = 0;
        for (int f = 0; f < g.Frames && moment < g.Moments.Length; f++)
        {
            run.Step();
            if (f + 1 != g.Moments[moment]) continue;
            moment++;
            var (oa, ow, oh) = run.Own; var (wa, ww, wh) = run.Wide; var c = run.Chip;
            rows.Add((Image(oa, ow, oh, scale), Image(wa, ww, wh, scale), $"frame {f + 1}: {Consoles.DisplayName(g.Console)} picture chip ({ow} x {oh})  |  WideMess widescreen ({ww} x {wh}, {c.Side} columns a side)",
                c.DescribeRules(), $"side columns x lines: extended {c.Extended:N0}, repeated {c.Repeated:N0}, filled {c.Filled:N0}, masked {c.MaskedSideColumns:N0}; sprite pixels in the sides {c.SideSpritePixels:N0}", c.Side));
        }
        int rowH = rows.Count == 0 ? 0 : rows.Max(r => Math.Max(r.own.Height, r.wide.Height));
        int width = pad + ownMax + pad + wideW + pad, height = pad + 24 + rows.Count * (label + 6 + rowH + notes + pad);
        using var sheet = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var gr = Graphics.FromImage(sheet);
        using var title = new Font("Segoe UI", 12f, FontStyle.Bold);
        using var font = new Font("Segoe UI", 9f);
        using var mark = new Pen(Color.FromArgb(255, 200, 60), 2);
        gr.Clear(Color.FromArgb(24, 24, 32));
        gr.DrawString($"{g.Name} on the WideMess widescreen chip (WMS:WMSS{(g.Console == ConsoleKind.Nes ? ", the NES board's WMSS" : "")}): the game's own picture, then the same frame 400 wide (yellow marks: the game's own edges)", title, Brushes.White, pad, pad - 4);
        int y = pad + 24;
        foreach (var r in rows)
        {
            gr.DrawString(r.head, font, Brushes.Gainsboro, pad, y);
            y += label;
            int ox = pad + (ownMax - r.own.Width) / 2, wx = pad + ownMax + pad;
            gr.DrawImageUnscaled(r.own, ox, y + 6);
            gr.DrawImageUnscaled(r.wide, wx, y + 6);
            int e0 = wx + r.side * scale, e1 = wx + r.wide.Width - r.side * scale;
            gr.DrawLine(mark, e0, y, e0, y + 5); gr.DrawLine(mark, e1, y, e1, y + 5);
            y += 6 + rowH;
            gr.DrawString("rules: " + r.rules, font, Brushes.Silver, wx, y + 2);
            gr.DrawString(r.counts, font, Brushes.Silver, wx, y + 17);
            y += notes + pad;
            r.own.Dispose(); r.wide.Dispose();
        }
        sheet.Save(file, ImageFormat.Png);
        Console.WriteLine($"{file}: {sheet.Width} x {sheet.Height}, {rows.Count} moments");
    }

    private static Bitmap Image(uint[] argb, int w, int h, int scale)
    {
        var bmp = new Bitmap(w * scale, h * scale, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        var row = new int[bmp.Width];
        for (int yy = 0; yy < bmp.Height; yy++)
        {
            for (int xx = 0; xx < bmp.Width; xx++) row[xx] = unchecked((int)(argb[(yy / scale) * w + xx / scale] | 0xFF000000u));
            System.Runtime.InteropServices.Marshal.Copy(row, 0, data.Scan0 + yy * data.Stride, row.Length);
        }
        bmp.UnlockBits(data);
        return bmp;
    }
}
