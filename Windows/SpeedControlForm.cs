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
        private CheckBox triggerCheckBox;
        private System.Windows.Forms.Timer pollTimer;
        private InputManager? inputManager;
        
        /// <summary>
        /// Event fired when speed multiplier changes
        /// </summary>
        public event EventHandler<float>? SpeedChanged;
        
        /// <summary>
        /// Event fired when user finishes changing speed (releases trackbar)
        /// </summary>
        public event EventHandler? SpeedChangeComplete;
        
        /// <summary>
        /// Current speed multiplier (0.25x to 4x)
        /// </summary>
        public float SpeedMultiplier { get; private set; } = 1.0f;

        // Smoothing state
        private float currentSmoothedSpeed = 1.0f;
        private float targetSpeedMultiplier = 1.0f;
        
        public SpeedControlForm()
        {
            InitializeComponents();
        }
        
        private void InitializeComponents()
        {
            this.Text = "Speed Control";
            this.ClientSize = new Size(400, 180);
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
            speedTrackBar.Scroll += SpeedTrackBar_Scroll;
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

            // Trigger control checkbox
            triggerCheckBox = new CheckBox
            {
                Text = "Use triggers for time",
                Location = new Point(130, 135),
                Size = new Size(160, 24),
                AutoSize = true,
                Visible = false // Hidden until controller detected
            };
            triggerCheckBox.CheckedChanged += (s, e) => {
                if (!triggerCheckBox.Checked) ResetSpeed();
            };
            this.Controls.Add(triggerCheckBox);

            // Timer for checking controller status and smoothing speed
            pollTimer = new System.Windows.Forms.Timer();
            pollTimer.Interval = 16; // ~60fps for smooth updates
            pollTimer.Tick += PollTimer_Tick;
            pollTimer.Start();
            
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
                targetSpeedMultiplier = 0.25f + (value / 75f) * 0.75f;
            }
            else
            {
                // Map 75-150 to 1x-4x
                targetSpeedMultiplier = 1.0f + ((value - 75) / 75f) * 3.0f;
            }
            
            // Note: We don't update SpeedMultiplier or fire events here anymore.
            // The PollTimer handles smoothing and event firing to create the inertia effect.
        }
        
        private void SpeedTrackBar_Scroll(object? sender, EventArgs e)
        {
            // Fired when user releases the trackbar - perfect moment to resync
            SpeedChangeComplete?.Invoke(this, EventArgs.Empty);
        }
        
        private void SpeedControlForm_FormClosing(object? sender, FormClosingEventArgs e)
        {
            // Reset to normal speed when closing
            currentSmoothedSpeed = 1.0f;
            targetSpeedMultiplier = 1.0f;
            SpeedMultiplier = 1.0f;
            SpeedChanged?.Invoke(this, 1.0f);
        }

        public void SetInputManager(InputManager manager)
        {
            this.inputManager = manager;
        }

        private void PollTimer_Tick(object? sender, EventArgs e)
        {
            if (inputManager != null)
            {
                bool isConnected = inputManager.IsControllerConnected;
                if (triggerCheckBox.Visible != isConnected)
                {
                    triggerCheckBox.Visible = isConnected;
                    // If controller disconnected, uncheck
                    if (!isConnected)
                    {
                        triggerCheckBox.Checked = false;
                    }
                }

                if (isConnected && triggerCheckBox.Checked)
                {
                    // Map LeftTrigger (0-255) to slowdown (75 -> 0)
                    // Map RightTrigger (0-255) to fastforward (75 -> 150)
                    
                    float leftOffset = (inputManager.LeftTrigger / 255.0f) * 75.0f;
                    float rightOffset = (inputManager.RightTrigger / 255.0f) * 75.0f;
                    
                    // TrackBar.Value center is 75
                    // Net value = 75 - leftOffset + rightOffset
                    int newValue = 75 - (int)leftOffset + (int)rightOffset;
                    
                    // Clamp just in case
                    newValue = Math.Max(speedTrackBar.Minimum, Math.Min(speedTrackBar.Maximum, newValue));
                    
                    if (speedTrackBar.Value != newValue)
                    {
                        speedTrackBar.Value = newValue;
                        // SpeedTrackBar_ValueChanged will be called automatically, updating targetSpeedMultiplier
                    }
                }
            }

            // Apply smoothing logic
            float diff = targetSpeedMultiplier - currentSmoothedSpeed;
            
            // Only update if there's a significant difference
            if (Math.Abs(diff) > 0.001f)
            {
                // Smooth factor: 0.1 at ~60Hz provides a responsive but weighty feel ("felt acceleration")
                // Lower values = more lag/weight, higher values = snappier
                currentSmoothedSpeed += diff * 0.1f;
                
                // Snap if very close to avoid endless micro-updates
                if (Math.Abs(targetSpeedMultiplier - currentSmoothedSpeed) < 0.005f)
                {
                    currentSmoothedSpeed = targetSpeedMultiplier;
                }
                
                // Update public property
                SpeedMultiplier = currentSmoothedSpeed;
                
                // Update display
                currentSpeedLabel.Text = $"Current Speed: {SpeedMultiplier:F2}x";
                
                // Notify listeners
                SpeedChanged?.Invoke(this, SpeedMultiplier);
            }
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
