namespace BrokenNes.Workshop.Tas.SelfPlay;

/// <summary>
/// Live-tunable self-play parameters, field-for-field mirror of the source project's
/// NESReflexSelfPlayConfig (TAS/fceux_custom/src/nesreflex_runtime.h) with the same default
/// values (TAS/fceux_custom/src/input.cpp's DefaultSelfPlayConfig(), an anonymous-enum-backed
/// literal table). Clamp ranges mirror ValidateSelfPlayConfig() there too.
/// </summary>
public sealed class SelfPlayConfig
{
    public int FlagCheckpointUses = 25;
    public int PowerupCheckpointUses = 15;
    public int ScoreCheckpointUses = 5;
    public int ScrollMilestoneCheckpointUses = 4;
    public int SucceededCheckpointUses = 32;
    public int ScoreCheckpointSpacing = 90;
    public int ScrollMilestoneSpacing = 128;
    public int MilestoneDeathInvalidationFrames = 120;
    public int GenericCheckpointSpacing = 45;
    public int FlagCooldownFrames = 900;
    public int ReloadCooldownFrames = 90;
    public int TimeUpPruneCount = 15;
    public int TimeUpMinCheckpointsToPrune = 2;
    public int VarietyRollInterval = 240;
    public int VarietyChancePercent = 10;
    public int VarietyFlipMinFrames = 30;
    public int VarietyFlipMaxFrames = 240;
    public int StuckXFrames = 60;
    public int StuckXRerollInterval = 60;
    public int StuckXChancePercent = 40;
    public int ReloadTopKMin = 3;
    public int ReloadTopKMax = 8;
    public int ReloadTemperatureMinX100 = 50;  // 0.50
    public int ReloadTemperatureMaxX100 = 400; // 4.00

    // Hardcoded (not user-configurable) constants used alongside the config above - kept here
    // since they're part of the same behavior surface, matching the source's separate
    // anonymous-enum constants (input.cpp:163-196) that aren't part of NESReflexSelfPlayConfig.
    public const int LowTimerDeathThreshold = 10;
    public const int LowTimerDeathPruneCount = 10;
    public const int GameOverPruneCount = 10;
    public const int MilestoneRecreateBlockFrames = 600;
    public const int ChillStyleChancePercent = 55;
    public const int ChillCooldownMinFrames = 10;
    public const int ChillCooldownMaxFrames = 60;
    public const int ChillDpadMinHoldFrames = 18;
    public const int ChillDpadStitchThresholdX100 = 35;
    public const int ChillHorizontalCommitMinFrames = 10;
    public const int ChillHorizontalCommitMaxFrames = 30;
    public const int ChillAMinHoldFrames = 40;

    /// <summary>Clamps every field into its valid range, mirroring ValidateSelfPlayConfig().</summary>
    public void Validate()
    {
        FlagCheckpointUses = Clamp(FlagCheckpointUses, 1, 999);
        PowerupCheckpointUses = Clamp(PowerupCheckpointUses, 1, 999);
        ScoreCheckpointUses = Clamp(ScoreCheckpointUses, 1, 999);
        ScrollMilestoneCheckpointUses = Clamp(ScrollMilestoneCheckpointUses, 1, 999);
        SucceededCheckpointUses = Clamp(SucceededCheckpointUses, 1, 999);
        ScoreCheckpointSpacing = Clamp(ScoreCheckpointSpacing, 0, 100000);
        ScrollMilestoneSpacing = Clamp(ScrollMilestoneSpacing, 0, 100000);
        MilestoneDeathInvalidationFrames = Clamp(MilestoneDeathInvalidationFrames, 0, 100000);
        GenericCheckpointSpacing = Clamp(GenericCheckpointSpacing, 0, 100000);
        FlagCooldownFrames = Clamp(FlagCooldownFrames, 0, 100000);
        ReloadCooldownFrames = Clamp(ReloadCooldownFrames, 0, 100000);
        TimeUpPruneCount = Clamp(TimeUpPruneCount, 0, 100000);
        TimeUpMinCheckpointsToPrune = Clamp(TimeUpMinCheckpointsToPrune, 0, 100000);
        VarietyRollInterval = Clamp(VarietyRollInterval, 1, 100000);
        VarietyChancePercent = Clamp(VarietyChancePercent, 0, 100);
        VarietyFlipMinFrames = Clamp(VarietyFlipMinFrames, 1, 100000);
        VarietyFlipMaxFrames = Clamp(VarietyFlipMaxFrames, VarietyFlipMinFrames, 100000);
        StuckXFrames = Clamp(StuckXFrames, 1, 100000);
        StuckXRerollInterval = Clamp(StuckXRerollInterval, 1, 100000);
        StuckXChancePercent = Clamp(StuckXChancePercent, 0, 100);
        ReloadTopKMin = Clamp(ReloadTopKMin, 1, 8);
        ReloadTopKMax = Clamp(ReloadTopKMax, ReloadTopKMin, 8);
        ReloadTemperatureMinX100 = Clamp(ReloadTemperatureMinX100, 1, 400);
        ReloadTemperatureMaxX100 = Clamp(ReloadTemperatureMaxX100, ReloadTemperatureMinX100, 400);
    }

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
}
