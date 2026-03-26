using System;
using System.Collections.Generic;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using SharpDX.XInput;

namespace BrokenNes.Windows
{
    /// <summary>
    /// Manages X and Y button input specifically for webmodule control
    /// These buttons are separate from NES emulation input
    /// </summary>
    public class WebModuleInputManager : IDisposable
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private Controller? controller;
        private State previousState;
        private UserIndex controllerIndex;
        
        private PlayerControllerConfig? playerConfig;
        private Func<bool>? keyboardFocusProvider;
        
        // Track previous state to detect button press/release events
        private bool previousXKeyboard = false;
        private bool previousYKeyboard = false;
        private bool previousXGamepad = false;
        private bool previousYGamepad = false;
        
        public event Action<string>? OnButtonPressed; // "X" or "Y"
        public event Action<string>? OnButtonReleased; // "X" or "Y"
        
        public WebModuleInputManager()
        {
            controllerIndex = UserIndex.One;
            controller = new Controller(controllerIndex);
        }
        
        /// <summary>
        /// Set player configuration for webmodule buttons
        /// </summary>
        public void SetPlayerConfig(PlayerControllerConfig config)
        {
            playerConfig = config;
            
            // Update controller if using gamepad
            if (config.DeviceType == InputDeviceType.Gamepad)
            {
                controllerIndex = (UserIndex)config.GamepadIndex;
                controller = new Controller(controllerIndex);
                
                if (controller.IsConnected)
                {
                    try
                    {
                        previousState = controller.GetState();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[WebModuleInput] Error getting initial controller state: {ex.Message}");
                    }
                }
            }
            
            Console.WriteLine($"[WebModuleInput] Configured for {config.DeviceType} device");
        }

        /// <summary>
        /// Set a callback that indicates whether keyboard input should be accepted.
        /// </summary>
        public void SetKeyboardFocusProvider(Func<bool> focusProvider)
        {
            keyboardFocusProvider = focusProvider;
        }
        
        /// <summary>
        /// Poll X/Y button state and fire events on state changes
        /// </summary>
        public void Poll()
        {
            if (playerConfig == null)
                return;
            
            bool currentX = false;
            bool currentY = false;
            
            // Poll keyboard
            if (playerConfig.DeviceType == InputDeviceType.Keyboard)
            {
                if (keyboardFocusProvider != null && !keyboardFocusProvider())
                {
                    currentX = false;
                    currentY = false;
                }
                else
                {
                    currentX = IsKeyPressed(playerConfig.X.Key);
                    currentY = IsKeyPressed(playerConfig.Y.Key);
                }
                
                // Check for state changes
                if (currentX && !previousXKeyboard)
                    OnButtonPressed?.Invoke("X");
                else if (!currentX && previousXKeyboard)
                    OnButtonReleased?.Invoke("X");
                    
                if (currentY && !previousYKeyboard)
                    OnButtonPressed?.Invoke("Y");
                else if (!currentY && previousYKeyboard)
                    OnButtonReleased?.Invoke("Y");
                    
                previousXKeyboard = currentX;
                previousYKeyboard = currentY;
            }
            // Poll gamepad
            else if (playerConfig.DeviceType == InputDeviceType.Gamepad && controller != null && controller.IsConnected)
            {
                try
                {
                    State state = controller.GetState();
                    var gamepad = state.Gamepad;
                    
                    // Use the configured gamepad buttons
                    currentX = playerConfig.X.GamepadButton.HasValue && 
                               (gamepad.Buttons & playerConfig.X.GamepadButton.Value) != 0;
                    currentY = playerConfig.Y.GamepadButton.HasValue && 
                               (gamepad.Buttons & playerConfig.Y.GamepadButton.Value) != 0;
                    
                    // Check for state changes
                    if (currentX && !previousXGamepad)
                        OnButtonPressed?.Invoke("X");
                    else if (!currentX && previousXGamepad)
                        OnButtonReleased?.Invoke("X");
                        
                    if (currentY && !previousYGamepad)
                        OnButtonPressed?.Invoke("Y");
                    else if (!currentY && previousYGamepad)
                        OnButtonReleased?.Invoke("Y");
                        
                    previousXGamepad = currentX;
                    previousYGamepad = currentY;
                    previousState = state;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WebModuleInput] Error polling gamepad: {ex.Message}");
                }
            }
        }
        
        private bool IsKeyPressed(string? keyName)
        {
            if (string.IsNullOrEmpty(keyName))
                return false;
                
            // Try to parse the key name
            if (Enum.TryParse<Keys>(keyName, out Keys key))
            {
                return (GetAsyncKeyState((int)key) & 0x8000) != 0;
            }
            
            return false;
        }
        
        public void Dispose()
        {
            // SharpDX.XInput Controller doesn't need explicit disposal
        }
    }
}
