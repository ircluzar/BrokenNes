using System;
using System.Collections.Generic;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using SharpDX.XInput;

namespace BrokenNes.Windows
{
    /// <summary>
    /// Unified input manager for keyboard and XInput gamepad support
    /// </summary>
    public class InputManager : IDisposable
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private Controller controller;
        private State previousState;
        private Dictionary<Keys, int> keyMap = new();
        
        // Separate states for keyboard and controller to allow simultaneous usage
        private bool[] keyboardStates = new bool[8];
        private bool[] controllerStates = new bool[8];
        
        private bool useController = false;
        
        public InputManager()
        {
            // Try to initialize XInput controller
            controller = new Controller(UserIndex.One);
            useController = controller.IsConnected;
            
            if (useController)
            {
                Console.WriteLine("XInput controller detected and initialized");
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
                Console.WriteLine("No XInput controller detected, using keyboard only");
            }
        }
        
        /// <summary>
        /// Set up keyboard mappings
        /// </summary>
        public void SetKeyMap(Dictionary<Keys, int> map)
        {
            keyMap = new Dictionary<Keys, int>(map);
            Console.WriteLine($"Input manager configured with {keyMap.Count} key bindings");
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
            
            // Even if packet number hasn't changed, we should process the state
            // because we might have cleared it or logic might have changed
            
            var gamepad = state.Gamepad;
            
            // Map XInput buttons to NES buttons
            // NES: A=0, B=1, Select=2, Start=3, Up=4, Down=5, Left=6, Right=7
            controllerStates[0] = (gamepad.Buttons & GamepadButtonFlags.A) != 0; // A
            controllerStates[1] = (gamepad.Buttons & GamepadButtonFlags.B) != 0; // B
            controllerStates[2] = (gamepad.Buttons & GamepadButtonFlags.Back) != 0; // Select
            controllerStates[3] = (gamepad.Buttons & GamepadButtonFlags.Start) != 0; // Start
            
            // D-Pad values need to be OR'd with existing state if we want to support multiple mappings
            // But here we are just setting from controller
            controllerStates[4] = (gamepad.Buttons & GamepadButtonFlags.DPadUp) != 0;
            controllerStates[5] = (gamepad.Buttons & GamepadButtonFlags.DPadDown) != 0;
            controllerStates[6] = (gamepad.Buttons & GamepadButtonFlags.DPadLeft) != 0;
            controllerStates[7] = (gamepad.Buttons & GamepadButtonFlags.DPadRight) != 0;
            
            // Also support analog stick for D-Pad
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
            
            // Merge keyboard and controller input
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
