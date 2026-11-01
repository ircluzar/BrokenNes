using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using BrokenNes.Windows.WebApi;
using NesEmulator.Debugging;

namespace BrokenNes.Windows.DebugWindows
{
    /// <summary>
    /// The tile viewer (BrokenDev D8, "D8 settled"): the NES's pattern tables (as the PPU sees them, or the cartridge's CHR), the SNES's VRAM
    /// as 2 / 4 / 8 bits a pixel, the Game Boy's tile data; greys or one of the game's palettes. The decoder is the MCP server's
    /// (<see cref="DebugPictures.Tiles"/>). A tile picked by number (or a click) shows its colour indices, row by row. Reads the memory domains
    /// only: nothing attached, nothing armed; the timer lives only while the window is open.
    /// </summary>
    public sealed class TilesWindow : Form
    {
        private readonly Func<DebuggerHub?> hub;
        private readonly Func<object?> game;
        private readonly System.Windows.Forms.Timer timer = new() { Interval = 200 };
        private readonly ComboBox sourceBox = new() { Name = "source", AccessibleName = "source", DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
        private readonly ComboBox bppBox = new() { Name = "bpp", AccessibleName = "bpp", DropDownStyle = ComboBoxStyle.DropDownList, Width = 60 };
        private readonly TextBox paletteBox = new() { Name = "palette", AccessibleName = "palette", Width = 40, Text = "-1" };
        private readonly TextBox startBox = new() { Name = "start", AccessibleName = "start", Width = 70, Text = "0" };
        private readonly TextBox tileBox = new() { Name = "tile", AccessibleName = "tile", Width = 50, Text = "0" };
        private readonly PictureBox picture = new() { Name = "sheet", Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(32, 32, 32) };
        private readonly TextBox tilePixels = new() { Name = "tilepixels", AccessibleName = "tilepixels", Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, Font = new Font("Consolas", 10f) };
        private readonly TextBox info = new() { Name = "info", AccessibleName = "info", Dock = DockStyle.Bottom, ReadOnly = true };
        private string? family;
        private DebugPictures.TileSheet? sheet;
        private ulong lastHash;

        public TilesWindow(Func<DebuggerHub?> hub, Func<object?> game)
        {
            this.hub = hub; this.game = game;
            Text = "BrokenNes Tiles";
            Name = "TilesWindow";
            ClientSize = new Size(760, 600);
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            Font = new Font("Segoe UI", 9f);
            picture.Paint += (_, e) => e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 32, Padding = new Padding(4), WrapContents = false };
            void L(string t) => bar.Controls.Add(new Label { Text = t, AutoSize = true, Padding = new Padding(6, 6, 0, 0) });
            L("Source"); bar.Controls.Add(sourceBox);
            L("Bits"); bar.Controls.Add(bppBox);
            L("Palette (-1 greys)"); bar.Controls.Add(paletteBox);
            L("Start"); bar.Controls.Add(startBox);
            L("Tile"); bar.Controls.Add(tileBox);
            var show = new Button { Text = "Show", Name = "show", AccessibleName = "show", AutoSize = true, Height = 24 };
            show.Click += (_, _) => { lastHash = 0; RefreshView(); };
            bar.Controls.Add(show);
            bppBox.Items.AddRange(new object[] { "2", "4", "8" }); bppBox.SelectedIndex = 1;
            sourceBox.SelectedIndexChanged += (_, _) => { lastHash = 0; RefreshView(); };
            bppBox.SelectedIndexChanged += (_, _) => { lastHash = 0; RefreshView(); };
            foreach (var tb in new[] { paletteBox, startBox, tileBox }) tb.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; lastHash = 0; RefreshView(); } };
            picture.MouseClick += (_, e) => PickAt(e.Location);

            var side = new GroupBox { Text = "Tile colour indices", Dock = DockStyle.Right, Width = 170 };
            side.Controls.Add(tilePixels);
            Controls.Add(picture);
            Controls.Add(side);
            Controls.Add(info);
            Controls.Add(bar);
            timer.Tick += (_, _) => RefreshView();
            Shown += (_, _) => { RefreshView(); timer.Start(); };
            FormClosed += (_, _) => { timer.Stop(); timer.Dispose(); picture.Image?.Dispose(); };
        }

        private static int Num(string s, int fallback)
        {
            s = s.Trim();
            if (s.StartsWith("$")) return int.TryParse(s[1..], System.Globalization.NumberStyles.HexNumber, null, out int h) ? h : fallback;
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return int.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber, null, out int h2) ? h2 : fallback;
            return int.TryParse(s, out int d) ? d : fallback;
        }

        private void PickAt(Point p)
        {
            if (sheet == null || picture.Image == null) return;
            // the Zoom layout: the image centred and scaled to fit
            float k = Math.Min((float)picture.ClientSize.Width / sheet.Width, (float)picture.ClientSize.Height / sheet.Height);
            float ox = (picture.ClientSize.Width - sheet.Width * k) / 2, oy = (picture.ClientSize.Height - sheet.Height * k) / 2;
            int x = (int)((p.X - ox) / k), y = (int)((p.Y - oy) / k);
            if (x < 0 || y < 0 || x >= sheet.Width || y >= sheet.Height) return;
            int t = y / 8 * sheet.Columns + x / 8;
            if (t < sheet.Tiles) { tileBox.Text = t.ToString(); lastHash = 0; RefreshView(); }
        }

        private void RefreshView()
        {
            if (IsDisposed) return;
            var h = hub(); string? why = null;
            var m = h?.Machine(game(), out why);
            if (m == null) { info.Text = h == null ? "the app's debugger hub is not up yet" : "no game: " + why; return; }
            string fam = DebugPictures.Family(m.Console);
            if (fam != family)
            {
                family = fam;
                sourceBox.Items.Clear();
                if (fam == "nes") sourceBox.Items.AddRange(new object[] { "ppu", "chr" }); else sourceBox.Items.Add("vram");
                sourceBox.SelectedIndex = 0;
                bppBox.Enabled = fam == "snes";
                lastHash = 0;
            }
            DebugPictures.TileSheet s;
            try { s = DebugPictures.Tiles(m.Domains(), fam, sourceBox.SelectedItem as string, Num((string)bppBox.SelectedItem!, 4), Num(paletteBox.Text, -1), Num(startBox.Text, 0), 0); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException) { info.Text = e.Message; return; }

            ulong hash = 14695981039346656037UL;
            foreach (uint v in s.Pixels) { hash ^= v; hash *= 1099511628211UL; }
            hash ^= (ulong)Num(tileBox.Text, 0) << 40;
            if (hash == lastHash) return;
            lastHash = hash; sheet = s;

            var bmp = new Bitmap(s.Width, s.Height, PixelFormat.Format32bppArgb);
            var bd = bmp.LockBits(new Rectangle(0, 0, s.Width, s.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            var ints = new int[s.Pixels.Length];
            Buffer.BlockCopy(s.Pixels, 0, ints, 0, ints.Length * 4);
            for (int y = 0; y < s.Height; y++) Marshal.Copy(ints, y * s.Width, bd.Scan0 + y * bd.Stride, s.Width);
            bmp.UnlockBits(bd);
            var old = picture.Image; picture.Image = bmp; old?.Dispose();

            int t = Math.Clamp(Num(tileBox.Text, 0), 0, Math.Max(0, s.Tiles - 1));
            var sb = new StringBuilder();
            int tx = t % s.Columns * 8, ty = t / s.Columns * 8;
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++) sb.Append(s.Indices[(ty + y) * s.Width + tx + x].ToString("X"));
                sb.Append("\r\n");
            }
            tilePixels.Text = sb.ToString();
            info.Text = $"{m.Console}: {s.What}; {s.Tiles} tiles of {s.TileBytes} bytes from ${s.Start:X}; tile {t} at ${s.Start + (long)t * s.TileBytes:X}; {s.Colours}";
        }
    }
}
