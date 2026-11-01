namespace BrokenNes.Workshop.Tas.SelfPlay;

/// <summary>
/// Pure edge-triggered event predicates, ported condition-for-condition from
/// TAS/fceux_custom/src/input.cpp's SMB1 detector functions (roughly lines 1278-1561). Every
/// function here takes only (previous frame's state, current frame's state[, config]) and returns
/// a bool or int - no mutable state, so these are trivially testable and match the source's own
/// separation of "pure detection" from "what to do about it" (that orchestration lives in
/// SelfPlayManager, mirroring NESReflexRuntime_OnFrameComplete).
///
/// Two predicates (IsGameOver, IsLevelTransition) were not directly captured verbatim by source
/// research and are reconstructed from surrounding usage context - flagged inline.
/// </summary>
public static class Smb1EventDetection
{
    // --- Death ---

    public static bool IsDeathAnimationState(Smb1State now) =>
        (now.EventMusicQueue & Smb1Addr.EventMusicDeath) != 0
        || now.PlayerState == 0x06
        || now.GameEngine == 0x0B;

    public static bool DidDeathJingleStart(Smb1State prev, Smb1State now) =>
        (prev.EventMusicQueue & Smb1Addr.EventMusicDeath) == 0
        && (now.EventMusicQueue & Smb1Addr.EventMusicDeath) != 0;

    /// <summary>previousLives &lt; 0 is this port's "no prior sample yet" sentinel, matching the
    /// source's use of a negative previous-lives value for the same purpose.</summary>
    public static bool DidLifeDecrease(int previousLives, int currentLives)
    {
        if (previousLives < 0) return false;
        if (currentLives < previousLives) return true;
        return previousLives == 0 && currentLives >= 0x80; // wraparound underflow
    }

    public static bool IsLikelyDeath(Smb1State prev, Smb1State now)
    {
        if (DidLifeDecrease(prev.Lives, now.Lives)) return true;
        if (DidDeathJingleStart(prev, now)) return true;
        return prev.Lives == now.Lives && now.GameEngine == 0x0B && prev.GameEngine != 0x0B;
    }

    public static bool IsLowTimerDeath(Smb1State now) =>
        now.Timer >= 0 && now.Timer < SelfPlayConfig.LowTimerDeathThreshold;

    /// <summary>Reconstructed: not directly captured verbatim from source (the research pass
    /// found only its call site, gated on "operMode==3 observed"), inferred from SMB1's
    /// well-documented OperMode value 3 = game-over screen.</summary>
    public static bool IsGameOver(Smb1State now) => now.OperMode == 3;

    // --- Time-up ---

    public static bool IsTimeUp(Smb1State prev, Smb1State now) => prev.Timer > 0 && now.Timer == 0;

    // --- Flag / level ---

    public static bool IsFlagSequenceState(Smb1State now) => now.GameEngine == 0x04 || now.GameEngine == 0x05;

    public static bool IsFlagLikeProgress(Smb1State prev, Smb1State now)
    {
        if (now.XPosition < 2300) return false;
        return prev.XPosition < 2300 && now.XPosition >= 2300;
    }

    /// <summary>Reconstructed: not directly captured verbatim from source, inferred from its
    /// effects at the call site (resets flag-sequence/cooldown/time-up-penalty state) - the
    /// natural, minimal definition of "changed level."</summary>
    public static bool IsLevelTransition(Smb1State prev, Smb1State now) =>
        now.World != prev.World || now.Level != prev.Level;

    // --- Power-up ---

    public static bool IsPowerupGain(Smb1State prev, Smb1State now) =>
        now.Status > prev.Status && now.Status is >= 1 and <= 2;

    // --- Score ---

    public static bool IsScoreGain(Smb1State prev, Smb1State now) =>
        now.Score > prev.Score || now.Coins != prev.Coins;

    // --- Scroll milestone ---

    public static bool IsScrollMilestoneProgress(Smb1State prev, Smb1State now, SelfPlayConfig cfg)
    {
        if (now.World != prev.World || now.Level != prev.Level) return false;
        int spacing = cfg.ScrollMilestoneSpacing;
        if (spacing <= 0) return false;
        if (now.MarioXPosition <= prev.MarioXPosition) return false;
        return now.MarioXPosition / spacing > prev.MarioXPosition / spacing;
    }
}
