using NesEmulator.Systems;

namespace BrokenNes.Mcp;

/// <summary>
/// Button names for the <c>input</c> tool, per console. "L" and "R" are ALWAYS the shoulder buttons (they used to read as Left / Right, so a SNES
/// shoulder press moved the player instead); the directions are Up Down Left Right, or the unambiguous short forms U D and DLeft DRight DUp DDown
/// (also "Dpad-Left" / "Dpad Left"). A button the console's pad does not have is refused, never silently dropped.
/// </summary>
internal static class Pads
{
    private const PadButtons Dirs = PadButtons.Up | PadButtons.Down | PadButtons.Left | PadButtons.Right;

    /// <summary>The buttons each console's session reads (aliases the session maps itself, like the Genesis L -> Z, are listed under Aliases).</summary>
    public static PadButtons Allowed(string consoleKey) => consoleKey switch
    {
        "nes" => Dirs | PadButtons.A | PadButtons.B | PadButtons.Start | PadButtons.Select,
        "gb" or "gbc" => Dirs | PadButtons.A | PadButtons.B | PadButtons.Start | PadButtons.Select,
        "snes" or "wms" => Dirs | PadButtons.A | PadButtons.B | PadButtons.X | PadButtons.Y | PadButtons.L | PadButtons.R | PadButtons.Start | PadButtons.Select,
        // Master System: 1 = A, 2 = B, Pause = Select, Reset = Mode; Game Gear: Start
        "sms" => Dirs | PadButtons.A | PadButtons.B | PadButtons.Select | PadButtons.Mode,
        "gg" => Dirs | PadButtons.A | PadButtons.B | PadButtons.Start,
        // Genesis six-button pad (MdSession also maps R -> C, L -> Z, Select -> Mode for the generic layout)
        "md" => Dirs | PadButtons.A | PadButtons.B | PadButtons.C | PadButtons.X | PadButtons.Y | PadButtons.Z | PadButtons.Mode | PadButtons.Start
                | PadButtons.L | PadButtons.R | PadButtons.Select,
        _ => (PadButtons)0x7FFF,
    };

    public static string Help(string consoleKey) => consoleKey switch
    {
        "nes" or "gb" or "gbc" => "A B Start Select Up Down Left Right",
        "snes" or "wms" => "A B X Y L R (shoulders) Start Select Up Down Left Right",
        "sms" => "A (=1) B (=2) Pause Reset Up Down Left Right",
        "gg" => "A (=1) B (=2) Start Up Down Left Right",
        "md" => "A B C X Y Z Mode Start Up Down Left Right (L = Z, R = C, Select = Mode)",
        _ => "A B X Y L R C Z Mode Start Select Up Down Left Right",
    };

    /// <summary>One name to its button, for this console ("" key = no console check).</summary>
    public static PadButtons One(string raw, string consoleKey)
    {
        string n = raw.Trim().ToLowerInvariant().Replace("-", "").Replace("_", "").Replace(" ", "");
        PadButtons? b = n switch
        {
            "up" or "u" or "dup" or "dpadup" => PadButtons.Up,
            "down" or "d" or "ddown" or "dpaddown" => PadButtons.Down,
            "left" or "dleft" or "dpadleft" => PadButtons.Left,
            "right" or "dright" or "dpadright" => PadButtons.Right,
            "l" or "lshoulder" or "shoulderl" or "tl" => PadButtons.L,
            "r" or "rshoulder" or "shoulderr" or "tr" => PadButtons.R,
            "a" => PadButtons.A, "b" => PadButtons.B, "x" => PadButtons.X, "y" => PadButtons.Y,
            "c" => PadButtons.C, "z" => PadButtons.Z, "mode" => PadButtons.Mode,
            "start" => PadButtons.Start, "select" or "sel" => PadButtons.Select,
            // Master System names only: elsewhere "pause" / "reset" would press an unrelated button
            "1" when consoleKey is "sms" or "gg" => PadButtons.A,
            "2" when consoleKey is "sms" or "gg" => PadButtons.B,
            "pause" when consoleKey is "sms" or "" => PadButtons.Select,
            "reset" when consoleKey is "sms" or "" => PadButtons.Mode,
            _ => null,
        };
        if (b == null) throw new ToolError($"unknown button '{raw}' on {(consoleKey.Length > 0 ? consoleKey : "this console")} ({Help(consoleKey)})");
        if (consoleKey.Length > 0 && (Allowed(consoleKey) & b.Value) == 0)
            throw new ToolError($"the {consoleKey} pad has no '{raw}' button ({Help(consoleKey)})");
        return b.Value;
    }

    public static PadButtons Parse(IEnumerable<string> names, string consoleKey)
    {
        PadButtons p = PadButtons.None;
        foreach (var raw in names) if (raw.Trim().Length > 0) p |= One(raw, consoleKey);
        return p;
    }

    /// <summary>The canonical names (what the desktop app's /api/input/set-buttons reads exactly).</summary>
    public static string[] Names(PadButtons b) =>
        Enum.GetValues<PadButtons>().Where(x => x != PadButtons.None && (b & x) != 0).Select(x => x.ToString()).ToArray();
}
