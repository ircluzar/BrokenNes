using System;
using System.Drawing;
using System.Windows.Forms;

namespace BrokenNes.Windows
{
    /// <summary>
    /// Form for controlling emulation speed with a trackbar
    /// </summary>
    public class SpeedControlForm : Form
    {
        private TrackBar speedTrackBar;
        private Label speedLabel;
        private Label minLabel;
        private Label maxLabel;
        private Label currentSpeedLabel;
        
        /// <summary>
        /// Event fired when speed multiplier changes
        /// </summary>
        public event EventHandler<float>? SpeedChanged;
        
        /// <summary>
        /// Current speed multiplier (0.25x to 4x)
        /// </summary>
        public float SpeedMultiplier { get; private set; } = 1.0f;
        
        public SpeedControlForm()
        {
            InitializeComponents();
        }
        
        private void InitializeComponents()
        {
            this.Text = "Speed Control";
            this.ClientSize = new Size(400, 150);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterParent;
            
            // Title label
            speedLabel = new Label
            {
                Text = "Emulation Speed",
                Font = new Font("Segoe UI", 12, FontStyle.Bold),
                Location = new Point(20, 20),
                Size = new Size(360, 25),
                TextAlign = ContentAlignment.MiddleCenter
            };
            this.Controls.Add(speedLabel);
            
            // Min speed label (0.25x)
            minLabel = new Label
            {
                Text = "0.25x",
                Location = new Point(20, 55),
                Size = new Size(50, 20),
                TextAlign = ContentAlignment.MiddleLeft
            };
            this.Controls.Add(minLabel);
            
            // Max speed label (4x)
            maxLabel = new Label
            {
                Text = "4x",
                Location = new Point(330, 55),
                Size = new Size(50, 20),
                TextAlign = ContentAlignment.MiddleRight
            };
            this.Controls.Add(maxLabel);
            
            // Track bar for speed control
            // Range: 0 to 150 (mapped to 0.25x to 4x)
            // Center position (75) = 1x speed
            speedTrackBar = new TrackBar
            {
                Location = new Point(70, 50),
                Size = new Size(260, 45),
                Minimum = 0,
                Maximum = 150,
                Value = 75, // 1x speed (middle)
                TickFrequency = 15,
                LargeChange = 15,
                SmallChange = 5
            };
            speedTrackBar.ValueChanged += SpeedTrackBar_ValueChanged;
            this.Controls.Add(speedTrackBar);
            
            // Current speed display
            currentSpeedLabel = new Label
            {
                Text = "Current Speed: 1.00x",
                Font = new Font("Segoe UI", 10, FontStyle.Regular),
                Location = new Point(20, 100),
                Size = new Size(360, 25),
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.DarkBlue
            };
            this.Controls.Add(currentSpeedLabel);
            
            // Handle form closing to reset speed
            this.FormClosing += SpeedControlForm_FormClosing;
        }
        
        private void SpeedTrackBar_ValueChanged(object? sender, EventArgs e)
        {
            // Map trackbar value (0-150) to speed multiplier (0.25x - 4x)
            // 0 = 0.25x, 75 = 1x, 150 = 4x
            int value = speedTrackBar.Value;
            
            if (value <= 75)
            {
                // Map 0-75 to 0.25x-1x
                SpeedMultiplier = 0.25f + (value / 75f) * 0.75f;
            }
            else
            {
                // Map 75-150 to 1x-4x
                SpeedMultiplier = 1.0f + ((value - 75) / 75f) * 3.0f;
            }
            
            // Update display
            currentSpeedLabel.Text = $"Current Speed: {SpeedMultiplier:F2}x";
            
            // Notify listeners
            SpeedChanged?.Invoke(this, SpeedMultiplier);
        }
        
        private void SpeedControlForm_FormClosing(object? sender, FormClosingEventArgs e)
        {
            // Reset to normal speed when closing
            SpeedMultiplier = 1.0f;
            SpeedChanged?.Invoke(this, 1.0f);
        }
        
        /// <summary>
        /// Reset the trackbar to 1x speed
        /// </summary>
        public void ResetSpeed()
        {
            speedTrackBar.Value = 75;
        }
    }
}
