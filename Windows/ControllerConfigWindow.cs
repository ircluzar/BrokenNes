using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using SharpDX.XInput;

namespace BrokenNes.Windows
{
    /// <summary>
    /// Window for configuring player controller bindings (keyboard and XInput gamepad)
    /// </summary>
    public partial class ControllerConfigWindow : Form
    {
        private readonly PlayerControllerConfig config;
        private List<Controller> controllers = new();
        private System.Windows.Forms.Timer? pollTimer;
        private bool isBinding = false;
        private Button? currentBindingButton;
        private Action<ButtonBinding>? currentBindingCallback;
        private string? currentButtonName;

        // UI Controls
        private ComboBox inputModeCombo;
        private Button refreshButton;
        private Dictionary<string, Button> bindingButtons = new();
        private bool isKeyboardMode = true;
        private int selectedControllerIndex = 0;

        public ControllerConfigWindow(PlayerControllerConfig config)
        {
            this.config = config;
            
            InitializeComponent();
            RefreshControllers();
            LoadBindings();

            // Start polling timer for gamepad input
            pollTimer = new System.Windows.Forms.Timer();
            pollTimer.Interval = 50; // Poll every 50ms
            pollTimer.Tick += PollTimer_Tick;
            pollTimer.Start();
        }

        private void InitializeComponent()
        {
            Text = $"Player {config.PlayerNumber} Controller Configuration";
            Size = new Size(500, 550);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            KeyPreview = true;

            var mainPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 5,
                Padding = new Padding(15),
                AutoSize = true
            };
            mainPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Title
            mainPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Input mode selector
            mainPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Bindings
            mainPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Info
            mainPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Buttons

            // Title label
            var titleLabel = new Label
            {
                Text = $"Player {config.PlayerNumber} Controller Configuration",
                Font = new Font(Font.FontFamily, 11, FontStyle.Bold),
                AutoSize = true,
                Dock = DockStyle.Top,
                Margin = new Padding(0, 0, 0, 10),
                TextAlign = ContentAlignment.MiddleCenter
            };

            // Input mode selector panel
            var selectorPanel = new FlowLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Top,
                FlowDirection = FlowDirection.LeftToRight,
                Margin = new Padding(0, 0, 0, 10),
                WrapContents = false
            };

            var inputLabel = new Label
            {
                Text = "Input Mode:",
                AutoSize = true,
                Margin = new Padding(0, 5, 10, 0),
                Font = new Font(Font.FontFamily, 9, FontStyle.Bold)
            };

            inputModeCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 200,
                Margin = new Padding(0, 0, 10, 0)
            };
            inputModeCombo.SelectedIndexChanged += InputModeCombo_SelectedIndexChanged;

            refreshButton = new Button
            {
                Text = "Refresh Controllers",
                AutoSize = true,
                Height = inputModeCombo.Height
            };
            refreshButton.Click += RefreshButton_Click;

            selectorPanel.Controls.Add(inputLabel);
            selectorPanel.Controls.Add(inputModeCombo);
            selectorPanel.Controls.Add(refreshButton);

            // Bindings panel
            var bindingsPanel = CreateBindingsPanel();

            // Info label
            var infoLabel = new Label
            {
                Text = "Click a button and press a key or gamepad button to bind it.",
                AutoSize = true,
                Dock = DockStyle.Top,
                Margin = new Padding(0, 10, 0, 10),
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.Gray,
                Font = new Font(Font.FontFamily, 8)
            };

            // Bottom button panel
            var buttonPanel = new FlowLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Top,
                FlowDirection = FlowDirection.RightToLeft,
                Margin = new Padding(0, 10, 0, 0)
            };

            var okButton = new Button
            {
                Text = "OK",
                Width = 80,
                Height = 30,
                Margin = new Padding(5, 0, 5, 0)
            };
            okButton.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };

            var cancelButton = new Button
            {
                Text = "Cancel",
                Width = 80,
                Height = 30,
                Margin = new Padding(5, 0, 5, 0)
            };
            cancelButton.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };

            var resetButton = new Button
            {
                Text = "Reset to Defaults",
                Width = 120,
                Height = 30,
                Margin = new Padding(20, 0, 5, 0)
            };
            resetButton.Click += ResetButton_Click;

            buttonPanel.Controls.Add(okButton);
            buttonPanel.Controls.Add(cancelButton);
            buttonPanel.Controls.Add(resetButton);

            mainPanel.Controls.Add(titleLabel, 0, 0);
            mainPanel.Controls.Add(selectorPanel, 0, 1);
            mainPanel.Controls.Add(bindingsPanel, 0, 2);
            mainPanel.Controls.Add(infoLabel, 0, 3);
            mainPanel.Controls.Add(buttonPanel, 0, 4);

            Controls.Add(mainPanel);
        }

        private Panel CreateBindingsPanel()
        {
            var panel = new TableLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Top,
                ColumnCount = 2,
                Padding = new Padding(10),
                Margin = new Padding(0)
            };

            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));

            var buttonNames = new[] { "A", "B", "Select", "Start", "Up", "Down", "Left", "Right" };

            foreach (var name in buttonNames)
            {
                var label = new Label
                {
                    Text = name + ":",
                    AutoSize = true,
                    Anchor = AnchorStyles.Left | AnchorStyles.Top,
                    Font = new Font(Font.FontFamily, 9, FontStyle.Bold),
                    Margin = new Padding(5, 7, 5, 5)
                };

                var button = new Button
                {
                    Width = 210,
                    Height = 28,
                    Anchor = AnchorStyles.Left | AnchorStyles.Top,
                    Margin = new Padding(5),
                    Text = "Not Bound"
                };

                bindingButtons[name] = button;
                button.Click += (s, e) => StartBinding(button, name);

                panel.Controls.Add(label);
                panel.Controls.Add(button);
            }

            return panel;
        }

        private void LoadBindings()
        {
            // Update all button texts based on current mode
            if (isKeyboardMode)
            {
                UpdateButtonText("A", config.A.Key);
                UpdateButtonText("B", config.B.Key);
                UpdateButtonText("Select", config.Select.Key);
                UpdateButtonText("Start", config.Start.Key);
                UpdateButtonText("Up", config.Up.Key);
                UpdateButtonText("Down", config.Down.Key);
                UpdateButtonText("Left", config.Left.Key);
                UpdateButtonText("Right", config.Right.Key);
            }
            else
            {
                UpdateButtonText("A", config.A.GamepadButton?.ToString());
                UpdateButtonText("B", config.B.GamepadButton?.ToString());
                UpdateButtonText("Select", config.Select.GamepadButton?.ToString());
                UpdateButtonText("Start", config.Start.GamepadButton?.ToString());
                UpdateButtonText("Up", config.Up.GamepadButton?.ToString());
                UpdateButtonText("Down", config.Down.GamepadButton?.ToString());
                UpdateButtonText("Left", config.Left.GamepadButton?.ToString());
                UpdateButtonText("Right", config.Right.GamepadButton?.ToString());
            }
        }

        private void UpdateButtonText(string buttonName, string? text)
        {
            if (bindingButtons.TryGetValue(buttonName, out var button))
            {
                button.Text = text ?? "Not Bound";
            }
        }

        private void RefreshControllers()
        {
            controllers.Clear();
            
            // Try to detect all 4 possible XInput controllers
            for (int i = 0; i < 4; i++)
            {
                try
                {
                    var userIndex = (UserIndex)i;
                    var controller = new Controller(userIndex);
                    if (controller.IsConnected)
                    {
                        controllers.Add(controller);
                    }
                }
                catch { }
            }

            // Update combo box
            inputModeCombo.Items.Clear();
            inputModeCombo.Items.Add("Keyboard");
            
            for (int i = 0; i < controllers.Count; i++)
            {
                inputModeCombo.Items.Add($"XInput Controller {(int)controllers[i].UserIndex + 1}");
            }

            // Restore selection from config
            if (config.DeviceType == InputDeviceType.Keyboard)
            {
                inputModeCombo.SelectedIndex = 0;
            }
            else
            {
                // Try to find the configured controller in the list
                int targetIndex = -1;
                for (int i = 0; i < controllers.Count; i++)
                {
                    if ((int)controllers[i].UserIndex == config.GamepadIndex)
                    {
                        targetIndex = i;
                        break;
                    }
                }
                
                if (targetIndex != -1)
                {
                    inputModeCombo.SelectedIndex = targetIndex + 1; // +1 because index 0 is Keyboard
                }
                else
                {
                    // Controller not found/connected, default to Keyboard
                    inputModeCombo.SelectedIndex = 0;
                }
            }
        }

        private void RefreshButton_Click(object? sender, EventArgs e)
        {
            RefreshControllers();
            MessageBox.Show(
                $"Found {controllers.Count} controller(s).",
                "Controllers Refreshed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );
        }

        private void InputModeCombo_SelectedIndexChanged(object? sender, EventArgs e)
        {
            if (inputModeCombo.SelectedIndex == 0)
            {
                isKeyboardMode = true;
                config.DeviceType = InputDeviceType.Keyboard;
            }
            else
            {
                isKeyboardMode = false;
                selectedControllerIndex = inputModeCombo.SelectedIndex - 1;
                
                config.DeviceType = InputDeviceType.Gamepad;
                if (selectedControllerIndex >= 0 && selectedControllerIndex < controllers.Count)
                {
                    config.GamepadIndex = (int)controllers[selectedControllerIndex].UserIndex;
                }
            }
            
            LoadBindings();
        }

        private void StartBinding(Button button, string buttonName)
        {
            if (isBinding) return;

            isBinding = true;
            currentBindingButton = button;
            currentButtonName = buttonName;

            button.Text = isKeyboardMode ? "Press any key..." : "Press gamepad button...";
            button.BackColor = Color.LightYellow;

            currentBindingCallback = (newBinding) =>
            {
                var binding = GetBindingForButton(buttonName);
                
                if (isKeyboardMode && !string.IsNullOrEmpty(newBinding.Key))
                {
                    binding.Key = newBinding.Key;
                }
                else if (!isKeyboardMode && newBinding.GamepadButton.HasValue)
                {
                    binding.GamepadButton = newBinding.GamepadButton;
                }
                
                UpdateBindingForButton(buttonName, binding);
            };
        }

        private ButtonBinding GetBindingForButton(string buttonName)
        {
            return buttonName switch
            {
                "A" => config.A,
                "B" => config.B,
                "Select" => config.Select,
                "Start" => config.Start,
                "Up" => config.Up,
                "Down" => config.Down,
                "Left" => config.Left,
                "Right" => config.Right,
                _ => throw new ArgumentException($"Unknown button: {buttonName}")
            };
        }

        private void UpdateBindingForButton(string buttonName, ButtonBinding binding)
        {
            // The config object already has references to the bindings, so they're updated automatically
            LoadBindings();
        }

        private void CompleteBinding()
        {
            if (currentBindingButton != null)
            {
                currentBindingButton.BackColor = SystemColors.Control;
                LoadBindings();
            }

            isBinding = false;
            currentBindingButton = null;
            currentBindingCallback = null;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            if (isBinding && currentBindingCallback != null && currentBindingButton != null)
            {
                // Capture the key
                var binding = new ButtonBinding { Key = e.KeyCode.ToString() };
                currentBindingCallback(binding);
                CompleteBinding();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        private void PollTimer_Tick(object? sender, EventArgs e)
        {
            if (!isBinding || isKeyboardMode || currentBindingCallback == null)
                return;

            if (selectedControllerIndex < 0 || selectedControllerIndex >= controllers.Count)
                return;

            var controller = controllers[selectedControllerIndex];
            if (!controller.IsConnected)
                return;

            try
            {
                var state = controller.GetState();
                var gamepad = state.Gamepad;

                // Check all buttons
                var pressedButtons = new List<GamepadButtonFlags>();

                foreach (GamepadButtonFlags flag in Enum.GetValues(typeof(GamepadButtonFlags)))
                {
                    if (flag == GamepadButtonFlags.None) continue;
                    if ((gamepad.Buttons & flag) != 0)
                    {
                        pressedButtons.Add(flag);
                    }
                }

                // If exactly one button is pressed, bind it
                if (pressedButtons.Count == 1)
                {
                    var binding = new ButtonBinding { GamepadButton = pressedButtons[0] };
                    currentBindingCallback(binding);
                    CompleteBinding();
                }
            }
            catch
            {
                // Ignore errors during polling
            }
        }

        private void ResetButton_Click(object? sender, EventArgs e)
        {
            var result = MessageBox.Show(
                "Reset all bindings to default values?",
                "Reset Confirmation",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question
            );

            if (result == DialogResult.Yes)
            {
                // Reset to defaults based on current device type
                PlayerControllerConfig defaults;
                
                if (config.DeviceType == InputDeviceType.Gamepad)
                {
                    defaults = PlayerControllerConfig.CreateDefaultGamepad(config.PlayerNumber);
                }
                else
                {
                    defaults = PlayerControllerConfig.CreateDefaultPlayer1();
                    // If not player 1, maybe we want different keys? 
                    // But current CreateDefaultPlayer1 is hardcoded to WASD/Arrow keys.
                    // For now, adhere to existing logic but match device type.
                }

                config.A = defaults.A;
                config.B = defaults.B;
                config.Select = defaults.Select;
                config.Start = defaults.Start;
                config.Up = defaults.Up;
                config.Down = defaults.Down;
                config.Left = defaults.Left;
                config.Right = defaults.Right;

                LoadBindings();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                pollTimer?.Stop();
                pollTimer?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
