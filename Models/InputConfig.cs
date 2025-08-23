using System.Text.Json.Serialization;

namespace BrokenNes.Models;

public enum InputDeviceType
{
    Touch,
    Keyboard,
    Gamepad
}

public class KeyboardMapping
{
    // Standard DOM KeyboardEvent.code values
    public string Up { get; set; } = "ArrowUp";
    public string Down { get; set; } = "ArrowDown";
    public string Left { get; set; } = "ArrowLeft";
    public string Right { get; set; } = "ArrowRight";
    public string A { get; set; } = "KeyX"; // emulator current default
    public string B { get; set; } = "KeyZ"; // emulator current default
    public string Select { get; set; } = "Space";
    public string Start { get; set; } = "Enter";
}

public class GamepadMapping
{
    // Support both d-pad buttons and left analog stick for directions
    // Standard mapping indices (per W3C Gamepad API typical layout)
    public int DpadUp { get; set; } = 12;
    public int DpadDown { get; set; } = 13;
    public int DpadLeft { get; set; } = 14;
    public int DpadRight { get; set; } = 15;
    public int AxisX { get; set; } = 0; // left stick X
    public int AxisY { get; set; } = 1; // left stick Y
    public float AxisThreshold { get; set; } = 0.5f;
    public int A { get; set; } = 0; // South (A on Xbox, Cross on PS)
    public int B { get; set; } = 1; // East (B on Xbox, Circle on PS)
    public int Select { get; set; } = 8; // Back/View
    public int Start { get; set; } = 9;  // Start/Menu
}

public class PlayerInputConfig
{
    public int Player { get; set; } = 1;
    public InputDeviceType Device { get; set; } = InputDeviceType.Keyboard;
    // Gamepad selection
    public int? GamepadIndex { get; set; }
    public string? GamepadId { get; set; }
    public KeyboardMapping Keyboard { get; set; } = new();
    public GamepadMapping Gamepad { get; set; } = new();
}

public class InputSettings
{
    public PlayerInputConfig Player1 { get; set; } = new() { Player = 1 };
    public PlayerInputConfig Player2 { get; set; } = new() { Player = 2, Device = InputDeviceType.Touch };
}
