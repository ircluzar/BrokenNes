using System;
using System.Collections.Generic;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using SharpDX.XInput;

namespace BrokenNes.Windows
{
    /// <summary>
    /// Unified input manager for keyboard and XInput gamepad support with configurable bindings
    /// </summary>
    public class InputManager : IDisposable
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private Controller controller;
        private State previousState;
        
        // NEW: Support for configurable bindings
        private PlayerControllerConfig? playerConfig;
        
        // Separate states for keyboard and controller to allow simultaneous usage
        private bool[] keyboardStates = new bool[8];
        private bool[] controllerStates = new bool[8];

        public byte LeftTrigger { get; private set; }
        public byte RightTrigger { get; private set; }
        
        // Legacy: Keep old key map for backwards compatibility
        private Dictionary<Keys, int> keyMap = new();
        
        private bool useController = false;
        private UserIndex controllerIndex;
        
        public InputManager(UserIndex controllerIndex = UserIndex.One)
        {
            this.controllerIndex = controllerIndex;
            
            // Try to initialize XInput controller
            controller = new Controller(controllerIndex);
            useController = controller.IsConnected;
            
            if (useController)
            {
                Console.WriteLine($"XInput controller {controllerIndex} detected and initialized");
                try 
                {
                    previousState = controller.GetState();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error getting initial controller state: {ex.Message}");
                    useController = false;
                }
            }
            else
            {
                Console.WriteLine($"No XInput controller detected at {controllerIndex}, using keyboard only");
            }
        }
        
        /// <summary>
        /// Set up keyboard mappings (LEGACY)
        /// </summary>
        public void SetKeyMap(Dictionary<Keys, int> map)
        {
            keyMap = new Dictionary<Keys, int>(map);
            Console.WriteLine($"Input manager configured with {keyMap.Count} key bindings (legacy mode)");
        }

        /// <summary>
        /// Set up player controller configuration (NEW)
        /// </summary>
        public void SetPlayerConfig(PlayerControllerConfig config)
        {
            playerConfig = config;
            
            // Apply controller index from config if using gamepad
            if (config.DeviceType == InputDeviceType.Gamepad)
            {
                if ((int)controllerIndex != config.GamepadIndex)
                {
                    controllerIndex = (UserIndex)config.GamepadIndex;
                    controller = new Controller(controllerIndex);
                    useController = controller.IsConnected;
                    Console.WriteLine($"Switched to XInput controller {controllerIndex}");
                }
            }
            
            Console.WriteLine($"Input manager configured for Player {config.PlayerNumber} with new binding system");
        }
        
        /// <summary>
        /// Handle keyboard key down (Legacy event-based - now using polling)
        /// </summary>
        public void OnKeyDown(Keys key)
        {
            // Kept for compatibility, but state is now primarily handled in Poll() via GetAsyncKeyState
            // We can still use this for immediate feedback if needed, but Poll will overwrite
        }
        
        /// <summary>
        /// Handle keyboard key up (Legacy event-based - now using polling)
        /// </summary>
        public void OnKeyUp(Keys key)
        {
            // Kept for compatibility
        }
        
        /// <summary>
        /// Poll input devices and update button states
        /// </summary>
        public void Poll()
        {
            PollKeyboard();
            PollController();
        }

        private void PollKeyboard()
        {
            // Reset keyboard states before polling
            Array.Clear(keyboardStates, 0, keyboardStates.Length);

            // Optimization: Skip keyboard polling if only using gamepad
            if (playerConfig != null && playerConfig.DeviceType == InputDeviceType.Gamepad)
                return;

            // NEW: Use player config if available
            if (playerConfig != null)
            {
                var bindings = playerConfig.GetAllBindings();
                for (int i = 0; i < bindings.Length && i < 8; i++)
                {
                    if (!string.IsNullOrEmpty(bindings[i].Key))
                    {
                        // Try to parse the key name
                        if (Enum.TryParse<Keys>(bindings[i].Key, out Keys key))
                        {
                            if ((GetAsyncKeyState((int)key) & 0x8000) != 0)
                            {
                                keyboardStates[i] = true;
                            }
                        }
                    }
                }
            }
            else
            {
                // LEGACY: Use old key map
                foreach (var kvp in keyMap)
                {
                    // Check if key is currently pressed (high bit set)
                    // GetAsyncKeyState takes a virtual key code, which maps 1:1 with WinForms Keys
                    if ((GetAsyncKeyState((int)kvp.Key) & 0x8000) != 0)
                    {
                        int buttonIndex = kvp.Value;
                        if (buttonIndex >= 0 && buttonIndex < keyboardStates.Length)
                        {
                            keyboardStates[buttonIndex] = true;
                        }
                    }
                }
            }
        }

        private void PollController()
        {
            // Always check connection status to support hot-plugging
            if (!controller.IsConnected)
            {
                // If controller disconnects, clear its state so keys don't get stuck
                Array.Clear(controllerStates, 0, controllerStates.Length);
                return;
            }
            
            State state;
            try
            {
                state = controller.GetState();
            }
            catch
            {
                return;
            }
            
            var gamepad = state.Gamepad;

            LeftTrigger = gamepad.LeftTrigger;
            RightTrigger = gamepad.RightTrigger;
            
            // NEW: Use player config if available for custom button mappings
            if (playerConfig != null)
            {
                var bindings = playerConfig.GetAllBindings();
                for (int i = 0; i < bindings.Length && i < 8; i++)
                {
                    if (bindings[i].GamepadButton.HasValue)
                    {
                        var buttonFlag = bindings[i].GamepadButton.Value;
                        controllerStates[i] = (gamepad.Buttons & buttonFlag) != 0;
                    }
                }
            }
            else
            {
                // LEGACY: Default XInput button mapping
                // Map XInput buttons to NES buttons
                // NES: A=0, B=1, Select=2, Start=3, Up=4, Down=5, Left=6, Right=7
                controllerStates[0] = (gamepad.Buttons & GamepadButtonFlags.A) != 0; // A
                controllerStates[1] = (gamepad.Buttons & GamepadButtonFlags.B) != 0; // B
                controllerStates[2] = (gamepad.Buttons & GamepadButtonFlags.Back) != 0; // Select
                controllerStates[3] = (gamepad.Buttons & GamepadButtonFlags.Start) != 0; // Start
                
                // D-Pad
                controllerStates[4] = (gamepad.Buttons & GamepadButtonFlags.DPadUp) != 0;
                controllerStates[5] = (gamepad.Buttons & GamepadButtonFlags.DPadDown) != 0;
                controllerStates[6] = (gamepad.Buttons & GamepadButtonFlags.DPadLeft) != 0;
                controllerStates[7] = (gamepad.Buttons & GamepadButtonFlags.DPadRight) != 0;
            }
            
            // Also support analog stick for D-Pad (always enabled regardless of config)
            const short threshold = 16384; // Half of max range
            if (gamepad.LeftThumbY > threshold) controllerStates[4] = true; // Up
            if (gamepad.LeftThumbY < -threshold) controllerStates[5] = true; // Down
            if (gamepad.LeftThumbX < -threshold) controllerStates[6] = true; // Left
            if (gamepad.LeftThumbX > threshold) controllerStates[7] = true; // Right
            
            previousState = state;
        }
        
        /// <summary>
        /// Get the state of a button (merges keyboard and controller)
        /// </summary>
        public bool GetButton(int buttonIndex)
        {
            if (buttonIndex < 0 || buttonIndex >= 8)
                return false;
            
            // Respect configured device type for exclusivity
            if (playerConfig != null)
            {
                if (playerConfig.DeviceType == InputDeviceType.Keyboard)
                    return keyboardStates[buttonIndex];
                    
                if (playerConfig.DeviceType == InputDeviceType.Gamepad && useController)
                    return controllerStates[buttonIndex];
            }
            
            // Merge keyboard and controller input (Legacy fallback)
            return keyboardStates[buttonIndex] || controllerStates[buttonIndex];
        }
        
        /// <summary>
        /// Check if controller is connected
        /// </summary>
        public bool IsControllerConnected => controller.IsConnected;
        
        public void Dispose()
        {
            // SharpDX.XInput Controller doesn't need explicit disposal
        }
    }
}
