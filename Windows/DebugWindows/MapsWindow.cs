using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using BrokenNes.Windows.WebApi;
using NesEmulator.Debugging;

namespace BrokenNes.Windows.DebugWindows
{
    /// <summary>
    /// The maps (BrokenDev D8, "D8 settled"): the NES's four nametables, a SNES background layer (BG1-BG4 in the current mode), the Game Boy's
    /// background or window map, drawn whole with the visible area outlined (magenta). A cell picked by its column and row (or a click) shows
    /// its tile, palette and flags. Drawn by <see cref="DebugPictures.Map"/> from video memory and the registers; nothing attached, nothing
    /// armed; the timer lives only while the window is open.
    /// </summary>
    public sealed class MapsWindow : Form
    {
        private readonly Func<DebuggerHub?> hub;
        private readonly Func<object?> game;
        private readonly System.Windows.Forms.Timer timer = new() { Interval = 250 };
        private readonly ComboBox layerBox = new() { Name = "layer", AccessibleName = "layer", DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
        private readonly TextBox cellX = new() { Name = "cellx", AccessibleName = "cellx", Width = 40, Text = "0" };
        private readonly TextBox cellY = new() { Name = "celly", AccessibleName = "celly", Width = 40, Text = "0" };
        private readonly TextBox cellInfo = new() { Name = "cell", AccessibleName = "cell", Width = 330, ReadOnly = true };
        private readonly PictureBox picture = new() { Name = "map", Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(32, 32, 32) };
        private readonly TextBox info = new() { Name = "info", AccessibleName = "info", Dock = DockStyle.Bottom, ReadOnly = true };
        private string? family;
        private DebugPictures.MapView? view;

        public MapsWindow(Func<DebuggerHub?> hub, Func<object?> game)
        {
            this.hub = hub; this.game = game;
            Text = "BrokenNes Maps";
            Name = "MapsWindow";
            ClientSize = new Size(820, 720);
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            Font = new Font("Segoe UI", 9f);
            picture.Paint += (_, e) => e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 32, Padding = new Padding(4), WrapContents = false };
            void L(string t) => bar.Controls.Add(new Label { Text = t, AutoSize = true, Padding = new Padding(6, 6, 0, 0) });
            L("Layer"); bar.Controls.Add(layerBox);
            L("Cell column"); bar.Controls.Add(cellX);
            L("row"); bar.Controls.Add(cellY);
            var show = new Button { Text = "Show", Name = "show", AccessibleName = "show", AutoSize = true, Height = 24 };
            show.Click += (_, _) => RefreshView();
            bar.Controls.Add(show);
            bar.Controls.Add(cellInfo);
            layerBox.SelectedIndexChanged += (_, _) => RefreshView();
            picture.MouseClick += (_, e) => PickAt(e.Location);
            Controls.Add(picture);
            Controls.Add(info);
            Controls.Add(bar);
            timer.Tick += (_, _) => RefreshView();
            Shown += (_, _) => { RefreshView(); timer.Start(); };
            FormClosed += (_, _) => { timer.Stop(); timer.Dispose(); picture.Image?.Dispose(); };
        }

        private void PickAt(Point p)
        {
            var v = view;
            if (v == null) return;
            float k = Math.Min((float)picture.ClientSize.Width / v.Width, (float)picture.ClientSize.Height / v.Height);
            float ox = (picture.ClientSize.Width - v.Width * k) / 2, oy = (picture.ClientSize.Height - v.Height * k) / 2;
            int x = (int)((p.X - ox) / k), y = (int)((p.Y - oy) / k);
            if (x < 0 || y < 0 || x >= v.Width || y >= v.Height) return;
            cellX.Text = (x / v.CellSize).ToString(); cellY.Text = (y / v.CellSize).ToString();
            RefreshView();
        }

        private void RefreshView()
        {
            if (IsDisposed) return;
            var h = hub(); string? why = null;
            var g = game();
            var m = h?.Machine(g, out why);
            if (m == null) { info.Text = h == null ? "the app's debugger hub is not up yet" : "no game: " + why; return; }
            string fam = DebugPictures.Family(m.Console);
            if (fam != family)
            {
                family = fam;
                layerBox.Items.Clear(); layerBox.Items.AddRange(DebugPictures.MapLayers(fam).Cast<object>().ToArray());
                if (layerBox.Items.Count > 0) layerBox.SelectedIndex = 0;
            }
            DebugPictures.MapView v;
            try { v = DebugPictures.Map(m, g, layerBox.SelectedItem as string ?? ""); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException) { info.Text = e.Message; cellInfo.Text = ""; return; }
            view = v;
            int cx = int.TryParse(cellX.Text, out int a) ? Math.Clamp(a, 0, v.CellsX - 1) : 0, cy = int.TryParse(cellY.Text, out int b) ? Math.Clamp(b, 0, v.CellsY - 1) : 0;
            var c = v.Cell(cx, cy);
            string ct = $"cell {cx},{cy}: tile ${c.tile:X} palette {c.palette}{(c.flags.Length > 0 ? " " + c.flags : "")}";
            if (cellInfo.Text != ct) cellInfo.Text = ct;
            DebugPictures.DrawView(v);
            var bmp = new Bitmap(v.Width, v.Height, PixelFormat.Format32bppArgb);
            var bd = bmp.LockBits(new Rectangle(0, 0, v.Width, v.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            var ints = new int[v.Pixels.Length];
            Buffer.BlockCopy(v.Pixels, 0, ints, 0, ints.Length * 4);
            for (int y = 0; y < v.Height; y++) Marshal.Copy(ints, y * v.Width, bd.Scan0 + y * bd.Stride, v.Width);
            bmp.UnlockBits(bd);
            var old = picture.Image; picture.Image = bmp; old?.Dispose();
            string it = $"{m.Console}: {v.What}";
            if (info.Text != it) info.Text = it;
        }
    }
}
