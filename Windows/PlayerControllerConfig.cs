using System;
using System.Text.Json.Serialization;
using SharpDX.XInput;

namespace BrokenNes.Windows
{
    public enum InputDeviceType
    {
        Keyboard,
        Gamepad
    }

    /// <summary>
    /// Represents input binding for a single NES button
    /// Supports both keyboard keys and XInput gamepad buttons
    /// </summary>
    public class ButtonBinding
    {
        /// <summary>
        /// Keyboard key name (e.g., "Z", "Up", "Space")
        /// </summary>
        [JsonPropertyName("key")]
        public string? Key { get; set; }

        /// <summary>
        /// XInput gamepad button flag
        /// </summary>
        [JsonPropertyName("gamepadButton")]
        public GamepadButtonFlags? GamepadButton { get; set; }

        /// <summary>
        /// Create a keyboard binding
        /// </summary>
        public static ButtonBinding FromKey(string key) => new ButtonBinding { Key = key };

        /// <summary>
        /// Create a gamepad button binding
        /// </summary>
        public static ButtonBinding FromGamepadButton(GamepadButtonFlags button) => 
            new ButtonBinding { GamepadButton = button };

        /// <summary>
        /// Check if this binding has any input configured
        /// </summary>
        public bool IsConfigured => !string.IsNullOrEmpty(Key) || GamepadButton.HasValue;

        /// <summary>
        /// Get a display-friendly name for this binding
        /// </summary>
        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrEmpty(Key))
                    return Key;
                if (GamepadButton.HasValue)
                    return $"GP: {GamepadButton.Value}";
                return "Not Bound";
            }
        }
    }

    /// <summary>
    /// Controller configuration for a single player
    /// </summary>
    public class PlayerControllerConfig
    {
        [JsonPropertyName("playerNumber")]
        public int PlayerNumber { get; set; } = 1;

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        [JsonPropertyName("deviceType")]
        public InputDeviceType DeviceType { get; set; } = InputDeviceType.Keyboard;

        [JsonPropertyName("gamepadIndex")]
        public int GamepadIndex { get; set; } = 0;

        // NES button bindings
        [JsonPropertyName("a")]
        public ButtonBinding A { get; set; } = ButtonBinding.FromKey("X");

        [JsonPropertyName("b")]
        public ButtonBinding B { get; set; } = ButtonBinding.FromKey("Z");

        [JsonPropertyName("select")]
        public ButtonBinding Select { get; set; } = ButtonBinding.FromKey("Space");

        [JsonPropertyName("start")]
        public ButtonBinding Start { get; set; } = ButtonBinding.FromKey("Return");

        [JsonPropertyName("up")]
        public ButtonBinding Up { get; set; } = ButtonBinding.FromKey("Up");

        [JsonPropertyName("down")]
        public ButtonBinding Down { get; set; } = ButtonBinding.FromKey("Down");

        [JsonPropertyName("left")]
        public ButtonBinding Left { get; set; } = ButtonBinding.FromKey("Left");

        [JsonPropertyName("right")]
        public ButtonBinding Right { get; set; } = ButtonBinding.FromKey("Right");

        // Webmodule control buttons (not routed to NES)
        [JsonPropertyName("x")]
        public ButtonBinding X { get; set; } = ButtonBinding.FromKey("A");

        [JsonPropertyName("y")]
        public ButtonBinding Y { get; set; } = ButtonBinding.FromKey("S");

        /// <summary>
        /// Get all button bindings in NES order (A, B, Select, Start, Up, Down, Left, Right)
        /// </summary>
        public ButtonBinding[] GetAllBindings()
        {
            return new[] { A, B, Select, Start, Up, Down, Left, Right };
        }
        
        /// <summary>
        /// Get webmodule button bindings (X, Y)
        /// </summary>
        public ButtonBinding[] GetWebmoduleBindings()
        {
            return new[] { X, Y };
        }

        /// <summary>
        /// Create default keyboard configuration for Player 1
        /// </summary>
        public static PlayerControllerConfig CreateDefaultPlayer1()
        {
            return new PlayerControllerConfig
            {
                PlayerNumber = 1,
                Enabled = true,
                DeviceType = InputDeviceType.Keyboard,
                A = ButtonBinding.FromKey("X"),
                B = ButtonBinding.FromKey("Z"),
                Select = ButtonBinding.FromKey("Space"),
                Start = ButtonBinding.FromKey("Return"),
                Up = ButtonBinding.FromKey("Up"),
                Down = ButtonBinding.FromKey("Down"),
                Left = ButtonBinding.FromKey("Left"),
                Right = ButtonBinding.FromKey("Right"),
                X = ButtonBinding.FromKey("A"),
                Y = ButtonBinding.FromKey("S")
            };
        }

        /// <summary>
        /// Create default gamepad configuration
        /// </summary>
        public static PlayerControllerConfig CreateDefaultGamepad(int playerNumber)
        {
            return new PlayerControllerConfig
            {
                PlayerNumber = playerNumber,
                Enabled = true,
                DeviceType = InputDeviceType.Gamepad,
                GamepadIndex = Math.Max(0, playerNumber - 1),
                A = ButtonBinding.FromGamepadButton(GamepadButtonFlags.A),
                B = ButtonBinding.FromGamepadButton(GamepadButtonFlags.B),
                Select = ButtonBinding.FromGamepadButton(GamepadButtonFlags.Back),
                Start = ButtonBinding.FromGamepadButton(GamepadButtonFlags.Start),
                Up = ButtonBinding.FromGamepadButton(GamepadButtonFlags.DPadUp),
                Down = ButtonBinding.FromGamepadButton(GamepadButtonFlags.DPadDown),
                Left = ButtonBinding.FromGamepadButton(GamepadButtonFlags.DPadLeft),
                Right = ButtonBinding.FromGamepadButton(GamepadButtonFlags.DPadRight),
                X = ButtonBinding.FromGamepadButton(GamepadButtonFlags.X),
                Y = ButtonBinding.FromGamepadButton(GamepadButtonFlags.Y)
            };
        }
    }
}
