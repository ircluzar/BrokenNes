using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BrokenNes.Windows
{
    /// <summary>
    /// BrokenNes 2: the volume button at the right end of the menu bar - a speaker (shell32's) drawn as a button in the
    /// bar, opening a floating panel to set the emulation volume on the fly. Scrolling the mouse wheel over the button
    /// nudges the volume. The level lives in <see cref="AudioManager.MasterVolume"/> / <see cref="AudioManager.Muted"/>
    /// and is saved in config.json (emulationVolume, emulationMuted). An ear-protecting limiter will join the panel later.
    /// </summary>
    public partial class MainForm
    {
        private VolumeMenuButton? volumeButton;
        private TrackBar? volumeSlider;
        private Label? volumeValueLabel;
        private Button? volumeMuteButton;
        private bool volumeDirty;

        private const int VolumeWheelStep = 5;

        private ToolStripMenuItem BuildVolumeMenu(MenuStrip menuStrip)
        {
            ApplyEmulationVolume();

            volumeButton = new VolumeMenuButton
            {
                Alignment = ToolStripItemAlignment.Right,
                DropDownDirection = ToolStripDropDownDirection.BelowLeft,
                ToolTipText = "Emulation volume (scroll to adjust)",
                AccessibleName = "Volume",
            };

            var panel = BuildVolumePanel();
            var host = new ToolStripControlHost(panel) { AutoSize = false, Size = panel.Size, Margin = Padding.Empty, Padding = Padding.Empty };
            var dropDown = new ToolStripDropDown { Padding = new Padding(1), AutoClose = true, DropShadowEnabled = true };
            dropDown.Items.Add(host);
            dropDown.Opened += (_, _) => { RefreshVolumeUi(); volumeSlider?.Focus(); };
            dropDown.Closed += (_, _) => SaveVolumeIfDirty();
            volumeButton.DropDown = dropDown;

            menuStrip.MouseWheel += (_, e) =>
            {
                if (volumeButton == null || !volumeButton.Bounds.Contains(e.Location)) return;
                SetEmulationVolume(config.EmulationVolume + Math.Sign(e.Delta) * VolumeWheelStep, e.Delta > 0 ? false : null);
                SaveVolumeIfDirty();
            };

            RefreshVolumeUi();
            return volumeButton;
        }

        private Panel BuildVolumePanel()
        {
            var font = SystemFonts.MenuFont ?? Control.DefaultFont;
            var panel = new Panel { Size = new Size(280, 124), BackColor = SystemColors.Window, Padding = new Padding(12, 10, 12, 10) };

            var title = new Label { Text = "Emulation volume", AutoSize = true, Font = new Font(font, FontStyle.Bold), Location = new Point(12, 12) };
            volumeValueLabel = new Label
            {
                AutoSize = false, Size = new Size(70, title.PreferredHeight), TextAlign = ContentAlignment.MiddleRight,
                Location = new Point(panel.Width - 12 - 70, 12), Font = font,
            };

            volumeMuteButton = new Button
            {
                Size = new Size(34, 30), Location = new Point(10, 40), FlatStyle = FlatStyle.Flat, TabStop = false,
                ImageAlign = ContentAlignment.MiddleCenter,
            };
            volumeMuteButton.FlatAppearance.BorderColor = SystemColors.ControlDark;
            volumeMuteButton.Click += (_, _) => { SetEmulationVolume(null, !config.EmulationMuted); SaveVolumeIfDirty(); };
            var muteTip = new ToolTip();
            muteTip.SetToolTip(volumeMuteButton, "Mute / unmute");

            volumeSlider = new TrackBar
            {
                Minimum = 0, Maximum = 100, TickFrequency = 10, SmallChange = 1, LargeChange = 10,
                Location = new Point(48, 40), Size = new Size(panel.Width - 48 - 8, 30), AutoSize = false,
                BackColor = SystemColors.Window,
            };
            volumeSlider.ValueChanged += (_, _) =>
            {
                if (volumeSlider.Value != config.EmulationVolume) SetEmulationVolume(volumeSlider.Value, false);
            };

            panel.Controls.Add(title);
            panel.Controls.Add(volumeValueLabel);
            panel.Controls.Add(volumeMuteButton);
            panel.Controls.Add(volumeSlider);

            // Quick levels
            int[] presets = { 10, 25, 50, 75, 100 };
            int gap = 6, bw = (panel.Width - 24 - gap * (presets.Length - 1)) / presets.Length;
            for (int i = 0; i < presets.Length; i++)
            {
                int level = presets[i];
                var b = new Button
                {
                    Text = level + "%", Font = font, Size = new Size(bw, 28), Location = new Point(12 + i * (bw + gap), 84),
                    FlatStyle = FlatStyle.Flat, TabStop = false, BackColor = SystemColors.Control,
                };
                b.FlatAppearance.BorderColor = SystemColors.ControlDark;
                b.Click += (_, _) => { SetEmulationVolume(level, false); SaveVolumeIfDirty(); };
                panel.Controls.Add(b);
            }
            return panel;
        }

        /// <summary>Change the volume (percent) and/or mute. Null leaves that part as it is. Saved on the next <see cref="SaveVolumeIfDirty"/>.</summary>
        private void SetEmulationVolume(int? volume, bool? muted)
        {
            if (volume is int v) { v = Math.Clamp(v, 0, 100); if (v != config.EmulationVolume) { config.EmulationVolume = v; volumeDirty = true; } }
            if (muted is bool m && m != config.EmulationMuted) { config.EmulationMuted = m; volumeDirty = true; }
            ApplyEmulationVolume();
            RefreshVolumeUi();
        }

        private void ApplyEmulationVolume()
        {
            AudioManager.MasterVolume = Math.Clamp(config.EmulationVolume, 0, 100) / 100f;
            AudioManager.Muted = config.EmulationMuted;
        }

        private void SaveVolumeIfDirty()
        {
            if (!volumeDirty) return;
            volumeDirty = false;
            Helpers.ConfigHelper.Save(config);
        }

        private void RefreshVolumeUi()
        {
            int v = config.EmulationVolume; bool muted = config.EmulationMuted;
            bool silent = muted || v == 0;
            if (volumeButton != null)
            {
                volumeButton.Level = v; volumeButton.Muted = muted;
                volumeButton.ToolTipText = muted ? $"Emulation muted (volume {v}%) - scroll to adjust" : $"Emulation volume {v}% - scroll to adjust";
                volumeButton.Invalidate();
            }
            if (volumeSlider != null && volumeSlider.Value != v) volumeSlider.Value = v;
            if (volumeValueLabel != null) volumeValueLabel.Text = muted ? "Muted" : v + "%";
            if (volumeMuteButton != null)
            {
                var old = volumeMuteButton.Image;
                volumeMuteButton.Image = VolumeMenuButton.SpeakerBitmap(20, silent);
                old?.Dispose();
            }
        }

        private void WireVolumeApi(WebApi.WebApiServer server)
        {
            server.GetEmulationVolume = () => (config.EmulationVolume, config.EmulationMuted);
            server.SetEmulationVolume = (volume, muted) => Invoke(() => { SetEmulationVolume(volume, muted); SaveVolumeIfDirty(); });
        }

        /// <summary>
        /// The menu bar's volume button: a ToolStripMenuItem (so it sits in the bar at the menus' height and opens like
        /// them) painted as a rounded button with shell32's speaker and the level.
        /// </summary>
        private sealed class VolumeMenuButton : ToolStripMenuItem
        {
            [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
            public int Level { get; set; } = 100;
            [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
            public bool Muted { get; set; }

            public override Size GetPreferredSize(Size constrainingSize)
            {
                int icon = IconSize();
                int text = TextRenderer.MeasureText("Muted", Font).Width;
                var baseSize = base.GetPreferredSize(constrainingSize);
                return new Size(8 + icon + 5 + text + 8, Math.Max(baseSize.Height, icon + 6));
            }

            private int IconSize() => Math.Max(16, (Owner?.ImageScalingSize.Height ?? 16));

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var r = new Rectangle(2, 2, Width - 5, Height - 5);
                bool open = DropDown.Visible || Pressed;
                Color fill = open ? ProfessionalColors.MenuItemPressedGradientBegin
                           : Selected ? ProfessionalColors.MenuItemSelected
                           : SystemColors.ControlLightLight;
                Color border = Selected || open ? ProfessionalColors.MenuItemBorder : SystemColors.ControlDark;
                using (var path = Rounded(r, 4))
                using (var b = new SolidBrush(fill))
                using (var p = new Pen(border))
                {
                    g.FillPath(b, path);
                    g.DrawPath(p, path);
                }

                int icon = IconSize();
                int iy = (Height - icon) / 2;
                using (var bmp = SpeakerBitmap(icon, Muted || Level == 0))
                    g.DrawImage(bmp, 8, iy, icon, icon);

                var textRect = new Rectangle(8 + icon + 5, 0, Width - (8 + icon + 5) - 6, Height);
                TextRenderer.DrawText(g, Muted ? "Muted" : Level + "%", Font, textRect,
                    Muted ? Color.Firebrick : SystemColors.MenuText, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            }

            private static GraphicsPath Rounded(Rectangle r, int radius)
            {
                int d = radius * 2;
                var path = new GraphicsPath();
                path.AddArc(r.X, r.Y, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                return path;
            }

            // shell32.dll icon 168: the speaker. Extracted once at 32 px and scaled; silent adds a red cross.
            private static Icon? speakerIcon;
            private const int ShellSpeakerIndex = 168;

            [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
            private static extern int ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, int count);
            [DllImport("user32.dll")]
            private static extern bool DestroyIcon(IntPtr handle);

            private static Icon? SpeakerIcon()
            {
                if (speakerIcon != null) return speakerIcon;
                try
                {
                    var large = new IntPtr[1];
                    string shell32 = Environment.ExpandEnvironmentVariables(@"%WINDIR%\System32\shell32.dll");
                    if (ExtractIconEx(shell32, ShellSpeakerIndex, large, null, 1) > 0 && large[0] != IntPtr.Zero)
                    {
                        speakerIcon = (Icon)Icon.FromHandle(large[0]).Clone();
                        DestroyIcon(large[0]);
                    }
                }
                catch { }
                return speakerIcon;
            }

            public static Bitmap SpeakerBitmap(int size, bool silent)
            {
                var bmp = new Bitmap(size, size);
                using var g = Graphics.FromImage(bmp);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var ico = SpeakerIcon();
                if (ico != null) g.DrawIcon(ico, new Rectangle(0, 0, size, size));
                else using (var b = new SolidBrush(SystemColors.ControlText)) g.FillRectangle(b, size / 4, size / 4, size / 2, size / 2);
                if (silent)
                {
                    float s = size * 0.48f, x = size - s, y = size - s;
                    using var bg = new SolidBrush(Color.White);
                    g.FillEllipse(bg, x - 1, y - 1, s + 1, s + 1);
                    using var pen = new Pen(Color.Firebrick, Math.Max(2f, size / 9f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    float m = s * 0.28f;
                    g.DrawLine(pen, x + m, y + m, x + s - m, y + s - m);
                    g.DrawLine(pen, x + s - m, y + m, x + m, y + s - m);
                }
                return bmp;
            }
        }
    }
}
