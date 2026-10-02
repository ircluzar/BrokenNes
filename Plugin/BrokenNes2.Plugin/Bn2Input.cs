using System.Runtime.InteropServices;
using NesEmulator.Systems;

namespace BrokenNes2;

/// <summary>
/// Player 1's controls for the game an emulator runs: which keyboard key and which gamepad button press each pad button. One mapping for the
/// whole plugin, remembered for the Windows user (see <see cref="Prefs"/>). The keyboard is read with GetAsyncKeyState and the gamepad with
/// XInput, both polled by an open editor; the editor also has an on-screen pad. A console uses the buttons it has (the NES and Game Boy have
/// no X, Y, L, R).
/// </summary>
internal static unsafe partial class Inputs
{
    public static readonly PadButtons[] Buttons =
        [PadButtons.Up, PadButtons.Down, PadButtons.Left, PadButtons.Right, PadButtons.A, PadButtons.B, PadButtons.X, PadButtons.Y, PadButtons.L, PadButtons.R, PadButtons.Start, PadButtons.Select];
    public static readonly string[] Names = ["Up", "Down", "Left", "Right", "A", "B", "X", "Y", "L", "R", "Start", "Select"];

    /// <summary>Virtual-key code per pad button (0 = none).</summary>
    public static readonly int[] Keys = new int[12];
    /// <summary>XInput button mask per pad button (0 = none).</summary>
    public static readonly int[] Pads = new int[12];
    public static bool KeyboardEnabled = true, GamepadEnabled = true;

    private static readonly object gate = new();
    static Inputs() => Load();

    // ---- XInput button masks ----
    public const int XUp = 0x1, XDown = 0x2, XLeft = 0x4, XRight = 0x8, XStart = 0x10, XBack = 0x20, XLeftThumb = 0x40, XRightThumb = 0x80,
        XLb = 0x100, XRb = 0x200, XA = 0x1000, XB = 0x2000, XX = 0x4000, XY = 0x8000;

    public static void Defaults()
    {
        lock (gate)
        {
            int[] keys = [0x26, 0x28, 0x25, 0x27, 'X', 'Z', 'S', 'A', 'Q', 'W', 0x0D, 0xA1];     // arrows; X Z S A Q W; Enter; right Shift
            int[] pads = [XUp, XDown, XLeft, XRight, XB, XA, XY, XX, XLb, XRb, XStart, XBack];  // the gamepad's B is the SNES A (the right-hand button) and so on
            Array.Copy(keys, Keys, 12);
            Array.Copy(pads, Pads, 12);
            KeyboardEnabled = GamepadEnabled = true;
        }
    }

    public static void Load()
    {
        lock (gate)
        {
            Defaults();
            try
            {
                var k = Prefs.Get("InputKeys")?.Split(',');
                var p = Prefs.Get("InputPads")?.Split(',');
                if (k is { Length: 12 }) for (int i = 0; i < 12; i++) if (int.TryParse(k[i], out int v)) Keys[i] = v;
                if (p is { Length: 12 }) for (int i = 0; i < 12; i++) if (int.TryParse(p[i], out int v)) Pads[i] = v;
                KeyboardEnabled = Prefs.GetInt("InputKeyboard", 1) != 0;
                GamepadEnabled = Prefs.GetInt("InputGamepad", 1) != 0;
            }
            catch { }
        }
    }

    public static void Save()
    {
        lock (gate)
        {
            Prefs.Set("InputKeys", string.Join(",", Keys));
            Prefs.Set("InputPads", string.Join(",", Pads));
            Prefs.SetInt("InputKeyboard", KeyboardEnabled ? 1 : 0);
            Prefs.SetInt("InputGamepad", GamepadEnabled ? 1 : 0);
        }
    }

    // ---- reading the devices ----

    [LibraryImport("user32.dll")] private static partial short GetAsyncKeyState(int vk);
    [LibraryImport("user32.dll")] private static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll")] private static partial nint GetAncestor(nint hwnd, uint flags);
    [LibraryImport("user32.dll", EntryPoint = "MapVirtualKeyW")] private static partial uint MapVirtualKey(uint code, uint mapType);
    [LibraryImport("user32.dll", EntryPoint = "GetKeyNameTextW")] private static partial int GetKeyNameText(int lParam, char* buffer, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState { public uint Packet; public ushort Buttons; public byte LeftTrigger, RightTrigger; public short LX, LY, RX, RY; }

    [LibraryImport("xinput1_4.dll", EntryPoint = "XInputGetState")] private static partial uint XInputGetState(uint index, XInputState* state);

    private static bool xinputBroken;

    /// <summary>True when the window is in the foreground application's window: only then does the keyboard play the game, so typing elsewhere in FL is left alone.</summary>
    public static bool IsForeground(nint editorWindow)
    {
        try
        {
            var fg = GetForegroundWindow();
            if (fg == 0) return false;
            var root = GetAncestor(editorWindow, 2 /* GA_ROOT */);
            return fg == root || fg == editorWindow || GetAncestor(fg, 2) == root;
        }
        catch { return false; }
    }

    private static bool Down(int vk) => vk != 0 && (GetAsyncKeyState(vk) & 0x8000) != 0;

    public static PadButtons PollKeyboard()
    {
        PadButtons r = 0;
        for (int i = 0; i < 12; i++) if (Down(Keys[i])) r |= Buttons[i];
        return r;
    }

    /// <summary>The first connected XInput controller, if any.</summary>
    private static bool ReadPad(out XInputState state)
    {
        state = default;
        if (xinputBroken) return false;
        try
        {
            for (uint i = 0; i < 4; i++)
            {
                XInputState s;
                if (XInputGetState(i, &s) == 0) { state = s; return true; }
            }
        }
        catch { xinputBroken = true; }
        return false;
    }

    public static PadButtons PollGamepad(out bool connected)
    {
        connected = ReadPad(out var s);
        PadButtons r = 0;
        if (!connected) return r;
        for (int i = 0; i < 12; i++) if (Pads[i] != 0 && (s.Buttons & Pads[i]) != 0) r |= Buttons[i];
        const int dead = 9000;                      // the left stick is a second D-pad
        if (s.LX < -dead) r |= PadButtons.Left; else if (s.LX > dead) r |= PadButtons.Right;
        if (s.LY > dead) r |= PadButtons.Up; else if (s.LY < -dead) r |= PadButtons.Down;
        return r;
    }

    /// <summary>The XInput buttons held right now (0 when no controller), for rebinding.</summary>
    public static int PadMaskNow() => ReadPad(out var s) ? s.Buttons : 0;

    /// <summary>A key that is down right now, for rebinding (Esc is reported as -1: cancel), or 0.</summary>
    public static int KeyNow()
    {
        if (Down(0x1B)) return -1;
        for (int vk = 8; vk < 255; vk++)
        {
            if (vk is 0x10 or 0x11 or 0x12 or 0x1B) continue;         // the left/right variants of Shift, Ctrl and Alt are read instead
            if (vk is >= 0x01 and <= 0x06) continue;                  // mouse buttons
            if (Down(vk)) return vk;
        }
        return 0;
    }

    // ---- names ----

    public static string KeyName(int vk)
    {
        if (vk == 0) return "-";
        if (vk is >= 'A' and <= 'Z' or >= '0' and <= '9') return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x6F);
        string? n = vk switch
        {
            0x08 => "Backspace", 0x09 => "Tab", 0x0D => "Enter", 0x20 => "Space", 0x25 => "Left", 0x26 => "Up", 0x27 => "Right", 0x28 => "Down",
            0x10 => "Shift", 0x11 => "Ctrl", 0x12 => "Alt", 0xA0 => "L Shift", 0xA1 => "R Shift", 0xA2 => "L Ctrl", 0xA3 => "R Ctrl", 0xA4 => "L Alt", 0xA5 => "R Alt",
            0x2D => "Insert", 0x2E => "Delete", 0x24 => "Home", 0x23 => "End", 0x21 => "Page Up", 0x22 => "Page Down",
            >= 0x60 and <= 0x69 => "Num " + (vk - 0x60), 0x6A => "Num *", 0x6B => "Num +", 0x6D => "Num -", 0x6E => "Num .", 0x6F => "Num /",
            0xBA => ";", 0xBB => "=", 0xBC => ",", 0xBD => "-", 0xBE => ".", 0xBF => "/", 0xC0 => "`", 0xDB => "[", 0xDC => "\\", 0xDD => "]", 0xDE => "'",
            _ => null,
        };
        if (n != null) return n;
        try
        {
            char* buf = stackalloc char[64];
            int len = GetKeyNameText((int)(MapVirtualKey((uint)vk, 0) << 16), buf, 64);
            if (len > 0) return new string(buf, 0, len);
        }
        catch { }
        return $"key {vk}";
    }

    public static string PadName(int mask)
    {
        if (mask == 0) return "-";
        var parts = new List<string>();
        foreach (var (bit, name) in new[] { (XUp, "D-pad up"), (XDown, "D-pad down"), (XLeft, "D-pad left"), (XRight, "D-pad right"), (XStart, "Start"), (XBack, "Back"),
                                           (XLeftThumb, "Left stick click"), (XRightThumb, "Right stick click"), (XLb, "LB"), (XRb, "RB"), (XA, "A"), (XB, "B"), (XX, "X"), (XY, "Y") })
            if ((mask & bit) != 0) parts.Add(name);
        return parts.Count == 0 ? "-" : string.Join(" + ", parts);
    }

    /// <summary>The pad buttons as the NES reads them: A, B, Select, Start, Up, Down, Left, Right.</summary>
    public static void ToNes(PadButtons b, bool[] dst)
    {
        dst[0] = (b & PadButtons.A) != 0; dst[1] = (b & PadButtons.B) != 0; dst[2] = (b & PadButtons.Select) != 0; dst[3] = (b & PadButtons.Start) != 0;
        dst[4] = (b & PadButtons.Up) != 0; dst[5] = (b & PadButtons.Down) != 0; dst[6] = (b & PadButtons.Left) != 0; dst[7] = (b & PadButtons.Right) != 0;
    }
}
