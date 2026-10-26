using System;
using System.Collections.Generic;
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
    /// The sprites (BrokenDev D8, "D8 settled"): the OAM list of the sprites on screen and their boxes on the game's last picture (the selected
    /// one in yellow). The decoder is the MCP server's (<see cref="DebugPictures.Sprites"/>). Reads memory and the frame only: nothing
    /// attached, nothing armed; the timer lives only while the window is open.
    /// </summary>
    public sealed class SpritesWindow : Form
    {
        private readonly Func<DebuggerHub?> hub;
        private readonly Func<object?> game;
        private readonly System.Windows.Forms.Timer timer = new() { Interval = 200 };
        private readonly ListBox list = new() { Name = "sprites", AccessibleName = "sprites", Dock = DockStyle.Fill, IntegralHeight = false, Font = new Font("Consolas", 9f) };
        private readonly PictureBox picture = new() { Name = "picture", Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
        private readonly TextBox info = new() { Name = "info", AccessibleName = "info", Dock = DockStyle.Bottom, ReadOnly = true };
        private string lastList = "";
        private List<DebugPictures.SpriteInfo> sprites = new();

        public SpritesWindow(Func<DebuggerHub?> hub, Func<object?> game)
        {
            this.hub = hub; this.game = game;
            Text = "BrokenNes Sprites";
            Name = "SpritesWindow";
            ClientSize = new Size(900, 520);
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            Font = new Font("Segoe UI", 9f);
            picture.Paint += (_, e) => e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 380 };
            var g = new GroupBox { Text = "On screen (OAM)", Dock = DockStyle.Fill }; g.Controls.Add(list);
            split.Panel1.Controls.Add(g);
            split.Panel2.Controls.Add(picture);
            Controls.Add(split);
            Controls.Add(info);
            list.SelectedIndexChanged += (_, _) => DrawPicture();
            timer.Tick += (_, _) => RefreshView();
            Shown += (_, _) => { RefreshView(); timer.Start(); };
            FormClosed += (_, _) => { timer.Stop(); timer.Dispose(); picture.Image?.Dispose(); };
        }

        private void RefreshView()
        {
            if (IsDisposed) return;
            var h = hub(); string? why = null;
            var m = h?.Machine(game(), out why);
            if (m == null) { info.Text = h == null ? "the app's debugger hub is not up yet" : "no game: " + why; return; }
            string note;
            try { sprites = DebugPictures.Sprites(m, out note); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException) { info.Text = e.Message; return; }
            var lines = sprites.Select(s => s.ToString()).ToArray();
            string key = string.Join("\n", lines);
            if (key != lastList)
            {
                int sel = list.SelectedIndex;
                lastList = key;
                list.BeginUpdate(); list.Items.Clear(); list.Items.AddRange(lines.Cast<object>().ToArray()); list.EndUpdate();
                if (sel < list.Items.Count) list.SelectedIndex = sel;
            }
            string it = $"{m.Console}: {sprites.Count} sprites on screen, frame {m.Frame}{(note.Length > 0 ? "; " + note : "")}";
            if (info.Text != it) info.Text = it;
            DrawPicture();
        }

        private void DrawPicture()
        {
            var (px, w, h) = DebugPictures.Frame(game());
            if (w == 0) return;
            DebugPictures.DrawBoxes(px, w, h, sprites);
            int sel = list.SelectedIndex;
            if (sel >= 0 && sel < sprites.Count) DebugPictures.DrawBoxes(px, w, h, new[] { sprites[sel] }, 0xFFFFFF00);
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            var ints = new int[px.Length];
            Buffer.BlockCopy(px, 0, ints, 0, ints.Length * 4);
            for (int y = 0; y < h; y++) Marshal.Copy(ints, y * w, bd.Scan0 + y * bd.Stride, w);
            bmp.UnlockBits(bd);
            var old = picture.Image; picture.Image = bmp; old?.Dispose();
        }
    }
}
