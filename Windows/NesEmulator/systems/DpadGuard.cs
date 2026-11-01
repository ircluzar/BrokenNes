using System;

namespace NesEmulator.Systems;

/// <summary>
/// D-pad protection. A real d-pad cannot press Left and Right, or Up and Down, at the same moment (the cross is one piece of plastic), but a keyboard
/// can, and so can a stick that is nudged while the d-pad is held. Games written for real pads misbehave when they see it (a walk cycle that runs
/// backwards, a menu that moves two ways, a famous glitch that only a keyboard can start). So the apps hold back both directions of a pair that
/// arrives together: the pair is neither pressed (neutral), which is what a player who lets go of both sees. It is on by default; the setting
/// "Allow opposite d-pad directions" turns it off for the people who want the glitch.
///
/// This is the ONE place the rule lives. It is a pure function over the pad bits and it is called from the HUMAN input sources only (the desktop's
/// <c>InputManager</c>, the plugin's editor poll; Lite's JavaScript twin is <c>WebLite/wwwroot/lib/dpadGuard.js</c>). It must never be called on recorded or
/// programmatic input: the Web API's set-buttons, movie and TAS playback, Workshop's headless runs and replay tools may carry a combination a real pad cannot
/// make, and a replay has to reach the console exactly as it was recorded. <c>Workshop --input-test</c> scans the tree for the callers and fails when a new
/// one appears outside the allow-list.
/// </summary>
public static class DpadGuard
{
    private const PadButtons Horizontal = PadButtons.Left | PadButtons.Right;
    private const PadButtons Vertical = PadButtons.Up | PadButtons.Down;

    /// <summary>
    /// The pad as the console should see it from a human. With <paramref name="allowOpposite"/> false, Left and Right together become neither, and Up and Down
    /// together become neither; every other bit passes through untouched. With it true the pad is returned as it is.
    /// </summary>
    public static PadButtons Resolve(PadButtons pad, bool allowOpposite)
    {
        if (allowOpposite) return pad;
        if ((pad & Horizontal) == Horizontal) pad &= ~Horizontal;
        if ((pad & Vertical) == Vertical) pad &= ~Vertical;
        return pad;
    }

    /// <summary>
    /// The same rule over a button list in NES order (A, B, Select, Start, Up, Down, Left, Right, then anything else), changed in place:
    /// indices 4 and 5 are Up and Down, 6 and 7 are Left and Right. Lists shorter than eight entries are left alone.
    /// </summary>
    public static void ResolveNesOrder(Span<bool> buttons, bool allowOpposite)
    {
        if (allowOpposite || buttons.Length < 8) return;
        if (buttons[4] && buttons[5]) { buttons[4] = false; buttons[5] = false; }
        if (buttons[6] && buttons[7]) { buttons[6] = false; buttons[7] = false; }
    }
}
