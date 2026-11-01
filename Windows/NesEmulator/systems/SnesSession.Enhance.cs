using System;
using System.Collections.Generic;
using NesEmulator.Snes;

namespace NesEmulator.Systems;

/// <summary>What is switched on for a SNES game on the WideMess console (docs/projects/widemess-spec.md section 6.1).</summary>
public readonly record struct WmsEnhancements(bool Widescreen, bool NoFlicker, bool BetterSound, bool FastCpu)
{
    public bool Any => Widescreen || NoFlicker || BetterSound || FastCpu;
    public bool this[WmsEnhancement e] => e switch { WmsEnhancement.Widescreen => Widescreen, WmsEnhancement.NoFlicker => NoFlicker, WmsEnhancement.BetterSound => BetterSound, _ => FastCpu };
    public override string ToString()
    {
        var on = new List<string>();
        if (Widescreen) on.Add("widescreen"); if (NoFlicker) on.Add("no flicker"); if (BetterSound) on.Add("better sound"); if (FastCpu) on.Add("fast CPU");
        return on.Count == 0 ? "none" : string.Join(", ", on);
    }
}

/// <summary>
/// The WideMess console's enhancements of a SNES game (milestone 6, spec section 6): widescreen (the game's profile's rules), no sprite flicker, better sound (cubic interpolation) and fast CPU.
/// They are the console's: the game stays locked and never sees one (fast CPU changes its timing, by nature). A <c>.wms</c> game has none, and a SNES game loses them when it unlocks the
/// extensions. All off, the session is the SNES console's, frame for frame.
/// </summary>
public sealed partial class SnesSession
{
    private string? imageSha1;
    private WmsProfileLookup? profileLookup;
    private WmsProfile? scratchProfile;
    private WmsEnhancements enhancements;
    private string enhanceNote = "";
    private bool enhanceHooked;

    /// <summary>The SHA-1 a profile names this game by: of the ROM image, without a 512-byte copier header (spec 6.2).</summary>
    public string ImageSha1 => imageSha1 ??= WmsProfiles.ImageSha1(romBytes);

    /// <summary>True when this session can be enhanced: the WideMess console running a SNES game (not a .wms game).</summary>
    public bool CanEnhance => wms != null && !GameId.StartsWith("wms", StringComparison.Ordinal);

    /// <summary>Why this session has no enhancements, or null when it can have them.</summary>
    public string? EnhanceRefusal =>
        wms == null ? "enhancements are the WideMess console's: switch the console to WideMess"
        : !CanEnhance ? "a WideMess game has no enhancements: it uses the WideMess itself"
        : board.WideMess is { Unlocked: true } ? "the game unlocked WideMess: the picture, sound and speed are the game's to set"
        : null;

    /// <summary>The game's profile (a scratch one set through the API first, then WideMess/profiles), or none, or a broken file and why.</summary>
    public WmsProfileLookup EnhanceProfile => scratchProfile != null ? new WmsProfileLookup(scratchProfile, null) : profileLookup ??= WmsProfiles.Find(ImageSha1);

    /// <summary>What is switched on.</summary>
    public WmsEnhancements Enhancements => enhancements;

    /// <summary>The last thing that happened to the enhancements on its own (the game unlocked WideMess), or "".</summary>
    public string EnhanceNote => enhanceNote;

    /// <summary>A scratch profile for this game (the HTTP API, tools): checked like a file, held by this session only; null goes back to the folder's. The enhancements are kept as they are
    /// when the new profile still offers them, else switched off.</summary>
    public void SetScratchProfile(WmsProfile? profile)
    {
        scratchProfile = profile;
        var e = enhancements;
        if (e.Widescreen && (EnhanceProfile.Profile?.Widescreen == null)) e = e with { Widescreen = false };
        ApplyEnhancements(e);
    }

    /// <summary>Why an enhancement cannot be switched on for this game, or null when it can (spec 6.2: widescreen needs a profile that offers it; the others are generic).</summary>
    public string? CannotEnhance(WmsEnhancement e)
    {
        if (EnhanceRefusal is { } why) return why;
        var look = EnhanceProfile;
        var p = look.Profile;
        if (p == null) return e == WmsEnhancement.Widescreen ? (look.Error != null ? $"widescreen needs the game's profile, and it is broken: {look.Error}" : "widescreen needs a profile, and there is no profile for this game") : null;
        return p.Verdict(e) == WmsVerdict.Unavailable ? $"the profile says {WmsProfile.Key(e)} is unavailable for {p.Title}" : null;
    }

    /// <summary>Switches enhancements (null leaves one as it is). Returns null when done, or why not (then nothing changed).</summary>
    public string? SetEnhancements(bool? widescreen = null, bool? noFlicker = null, bool? betterSound = null, bool? fastCpu = null)
    {
        var want = new WmsEnhancements(widescreen ?? enhancements.Widescreen, noFlicker ?? enhancements.NoFlicker, betterSound ?? enhancements.BetterSound, fastCpu ?? enhancements.FastCpu);
        if (want.Any)
        {
            foreach (WmsEnhancement e in Enum.GetValues<WmsEnhancement>())
                if (want[e] && !enhancements[e] && CannotEnhance(e) is { } why) return why;
        }
        ApplyEnhancements(want);
        return null;
    }

    /// <summary>"Use the profile": switches on what the game's profile recommends (and nothing else); false switches every enhancement off. Returns null, or why not.</summary>
    public string? UseProfile(bool on)
    {
        if (!on) { ApplyEnhancements(default); return null; }
        if (EnhanceRefusal is { } why) return why;
        var p = EnhanceProfile.Profile;
        if (p == null) return EnhanceProfile.Describe();
        var r = p.Recommended;
        return SetEnhancements(Array.IndexOf(r, WmsEnhancement.Widescreen) >= 0, Array.IndexOf(r, WmsEnhancement.NoFlicker) >= 0, Array.IndexOf(r, WmsEnhancement.BetterSound) >= 0, Array.IndexOf(r, WmsEnhancement.FastCpu) >= 0);
    }

    private void ApplyEnhancements(WmsEnhancements e)
    {
        if (wms == null) return;
        if (!enhanceHooked && board.WideMess is { } ext)
        {
            // the unlock takes them away (spec 6): chained after the board's own handler, which applies the WideMess registers
            var boardHandler = ext.Changed;
            ext.Changed = () => { boardHandler?.Invoke(); OnWideMessChanged(); };
            enhanceHooked = true;
        }
        enhancements = e;
        wms.WorkRam = board.Wram;   // the level in work RAM (spec 6.1): the picture chip reads it as a debugger does
        wms.SetEnhancements(e.Widescreen ? EnhanceProfile.Profile?.Widescreen : null, e.NoFlicker);
        board.ConsoleFastCpu = e.FastCpu;
        ApplySoundEnhancement();
        if (e.Any) enhanceNote = "";
    }

    private void OnWideMessChanged()
    {
        if (!enhancements.Any || board.WideMess is not { Unlocked: true }) return;
        enhanceNote = $"the game unlocked WideMess, so its enhancements ({enhancements}) are off: the picture, sound and speed are the game's to set";
        ApplyEnhancements(default);
    }

    /// <summary>The better-sound setting follows whichever SNES sound unit plays (a swap or a state load puts another one in): checked at each frame.</summary>
    private void ApplySoundEnhancement()
    {
        var spc = Spc;
        if (spc != null && spc.Dsp.CubicInterpolation != enhancements.BetterSound) spc.Dsp.CubicInterpolation = enhancements.BetterSound;
        if (parkedSpc != null && parkedSpc.Dsp.CubicInterpolation != enhancements.BetterSound) parkedSpc.Dsp.CubicInterpolation = enhancements.BetterSound;
    }

    /// <summary>Called at the start of each frame on the WideMess console.</summary>
    private void BeforeFrameEnhance()
    {
        if (enhancements.BetterSound || (Spc?.Dsp.CubicInterpolation ?? false)) ApplySoundEnhancement();
    }
}
