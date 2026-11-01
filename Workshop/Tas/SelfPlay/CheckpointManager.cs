using System;
using System.Collections.Generic;
using System.IO;
using NesEmulator;

namespace BrokenNes.Workshop.Tas.SelfPlay;

public enum SelfPlayEventType { None, Flag, Powerup, Score, ScrollMilestone, Baseline }

public sealed class Checkpoint
{
    public int Id;
    public int FrameIndex;
    public SelfPlayEventType EventType;
    public int UsesRemaining;
    public int MaxUses;
    public bool Succeeded;
    public int ScoreSnapshot;
    public int World;
    public int Level;
    public int XPosition; // Smb1State.MarioXPosition at creation time
    public string StatePath = string.Empty;
}

/// <summary>
/// The self-play checkpoint stack: creation, "succeeded" promotion, death/timeout-triggered
/// reload, and pruning. Ported algorithm-for-algorithm from TAS/fceux_custom/src/input.cpp
/// (CreateSelfPlayCheckpoint ~880, ReloadLatestSelfPlayCheckpoint ~1176, PruneLatestSelfPlayCheckpoints
/// ~947, InvalidateRecentScrollMilestoneCheckpoint ~817). One deliberate substitution: the source
/// uses FCEUX's own native savestate format via FCEUSS_Save/Load; this uses NES.SaveState()/
/// LoadState() (BrokenNes's own JSON format) instead - checkpoints are ephemeral, per-session
/// artifacts, so the on-disk format never needs to be FCEUX-compatible, only internally
/// consistent, which JSON round-tripping through the same NES instance guarantees.
/// </summary>
public sealed class CheckpointManager
{
    private readonly SelfPlayConfig _cfg;
    private readonly string _checkpointDir;
    private readonly List<Checkpoint> _checkpoints = new();
    private int _nextCheckpointId;
    private int _lifetimeFrameIndex;

    public bool FlagSequenceActive { get; set; }
    public int FlagCooldownFrames { get; set; }
    public int ReloadCooldownFrames { get; set; }
    public int LastReloadCheckpointId { get; private set; } = -1;
    public SelfPlayEventType LastReloadCheckpointEventType { get; private set; } = SelfPlayEventType.None;
    // The reloaded checkpoint's FrameIndex - the caller (SelfPlayManager) uses this to truncate its
    // running per-frame input log back to this point, so the log always reflects only the surviving
    // path (dead-end frames past the last reload are discarded), the same effect FCEUX gets
    // incidentally from savestate-coupled movie truncation. See CheckpointManager's class doc.
    public int LastReloadFrameIndex { get; private set; } = -1;
    public int FramesSinceReload { get; private set; } = int.MaxValue;

    private int _framesSinceCheckpoint;
    private int _framesSinceScoreCheckpoint;
    private int _timeUpPenaltyWorld = -1, _timeUpPenaltyLevel = -1, _timeUpPrunePenaltyMultiplier;

    // Scroll-milestone recreation lockouts: (world, level, centerX, expiresAtFrame).
    private readonly List<(int World, int Level, int CenterX, int ExpiresAtFrame)> _milestoneBlocks = new();

    public IReadOnlyList<Checkpoint> Checkpoints => _checkpoints;
    public int Count => _checkpoints.Count;

    public CheckpointManager(SelfPlayConfig cfg, string checkpointDir)
    {
        _cfg = cfg;
        _checkpointDir = checkpointDir;
        Directory.CreateDirectory(checkpointDir);
    }

    /// <summary>Call exactly once per frame, before any event handling - advances the pure
    /// frame-based counters (cooldowns, spacing).</summary>
    public void Tick()
    {
        _lifetimeFrameIndex++;
        _framesSinceCheckpoint++;
        _framesSinceScoreCheckpoint++;
        if (FlagCooldownFrames > 0) FlagCooldownFrames--;
        if (ReloadCooldownFrames > 0) ReloadCooldownFrames--;
        if (FramesSinceReload != int.MaxValue) FramesSinceReload++;
    }

    public bool TryCreate(SelfPlayEventType eventType, int uses, Smb1State now, NES nes)
    {
        if (eventType == SelfPlayEventType.Flag && FlagSequenceActive) return false;
        if (eventType != SelfPlayEventType.ScrollMilestone && _framesSinceCheckpoint < _cfg.GenericCheckpointSpacing) return false;
        if (eventType == SelfPlayEventType.Score && _framesSinceScoreCheckpoint < _cfg.ScoreCheckpointSpacing) return false;
        if (eventType == SelfPlayEventType.ScrollMilestone && IsScrollMilestoneRecreationBlocked(now)) return false;

        var cp = new Checkpoint
        {
            Id = _nextCheckpointId++,
            FrameIndex = _lifetimeFrameIndex,
            EventType = eventType,
            UsesRemaining = uses,
            MaxUses = uses,
            ScoreSnapshot = now.Score,
            World = now.World,
            Level = now.Level,
            XPosition = now.MarioXPosition,
        };
        cp.StatePath = Path.Combine(_checkpointDir, $"nesreflex_cp_{900 + cp.Id}.state.json");

        try
        {
            string json = nes.SaveState();
            if (string.IsNullOrEmpty(json)) return false;
            File.WriteAllText(cp.StatePath, json);
            if (!File.Exists(cp.StatePath)) return false;
        }
        catch { return false; }

        // Promotion: the nearest prior *non-scroll-milestone* checkpoint gets its lifeline reset
        // to SucceededCheckpointUses if this new checkpoint represents real forward progress past it.
        if (eventType != SelfPlayEventType.ScrollMilestone)
        {
            int succeededIndex = FindLatestRealCheckpointIndex(_checkpoints.Count);
            if (succeededIndex >= 0 && IsSucceededBy(_checkpoints[succeededIndex], cp))
            {
                _checkpoints[succeededIndex].UsesRemaining = _cfg.SucceededCheckpointUses;
                _checkpoints[succeededIndex].MaxUses = _cfg.SucceededCheckpointUses;
                _checkpoints[succeededIndex].Succeeded = true;
            }
        }

        _checkpoints.Add(cp);
        _framesSinceCheckpoint = 0;
        if (eventType == SelfPlayEventType.Score) _framesSinceScoreCheckpoint = 0;
        if (eventType == SelfPlayEventType.Flag)
        {
            FlagSequenceActive = true;
            FlagCooldownFrames = _cfg.FlagCooldownFrames;
        }
        else
        {
            FlagSequenceActive = false;
        }
        return true;
    }

    private int FindLatestRealCheckpointIndex(int beforeSlot)
    {
        for (int i = beforeSlot - 1; i >= 0; i--)
            if (_checkpoints[i].EventType != SelfPlayEventType.ScrollMilestone) return i;
        return -1;
    }

    private static bool IsSucceededBy(Checkpoint previous, Checkpoint next)
    {
        if (previous.EventType == SelfPlayEventType.None || string.IsNullOrEmpty(previous.StatePath)) return false;
        if (next.World != previous.World || next.Level != previous.Level) return true;
        return next.XPosition > previous.XPosition;
    }

    private static bool IsDurableFallback(Checkpoint cp) =>
        cp.Succeeded || cp.EventType == SelfPlayEventType.Baseline || cp.EventType == SelfPlayEventType.Flag;

    /// <summary>Reloads the newest checkpoint with UsesRemaining > 0, decrementing its lifeline
    /// (or force-keeping it at 1 use if it's the last durable fallback on the whole stack).
    /// Returns false only if no usable checkpoint exists anywhere.</summary>
    public bool Reload(NES nes)
    {
        for (int i = _checkpoints.Count - 1; i >= 0; i--)
        {
            var cp = _checkpoints[i];
            if (cp.UsesRemaining <= 0) continue;
            if (string.IsNullOrEmpty(cp.StatePath) || !File.Exists(cp.StatePath)) { cp.UsesRemaining = 0; continue; }

            string json;
            try { json = File.ReadAllText(cp.StatePath); }
            catch { cp.UsesRemaining = 0; continue; }

            try { nes.LoadState(json); }
            catch { cp.UsesRemaining = 0; continue; }

            bool hasOtherUsableReal = false;
            for (int j = 0; j < _checkpoints.Count; j++)
            {
                if (j == i) continue;
                var other = _checkpoints[j];
                if (other.UsesRemaining > 0 && !string.IsNullOrEmpty(other.StatePath) && other.EventType != SelfPlayEventType.ScrollMilestone)
                { hasOtherUsableReal = true; break; }
            }

            if (cp.UsesRemaining > 0) cp.UsesRemaining--;
            bool keepAsLastFallback = cp.UsesRemaining <= 0
                && cp.EventType != SelfPlayEventType.ScrollMilestone
                && !hasOtherUsableReal
                && IsDurableFallback(cp);
            if (keepAsLastFallback) cp.UsesRemaining = 1;

            LastReloadCheckpointId = cp.Id;
            LastReloadCheckpointEventType = cp.EventType;
            LastReloadFrameIndex = cp.FrameIndex;
            FramesSinceReload = 0;
            FlagSequenceActive = cp.EventType == SelfPlayEventType.Flag;
            ReloadCooldownFrames = _cfg.ReloadCooldownFrames;

            if (!keepAsLastFallback && cp.UsesRemaining <= 0)
                RemoveAt(i);

            return true;
        }
        return false;
    }

    /// <summary>Pops up to `count` checkpoints from the top (newest first), stopping at
    /// `minCheckpointCountToKeep`; a no-op unless the stack has at least `minCheckpointCountToPrune`
    /// entries to begin with.</summary>
    public int Prune(int count, int minCheckpointCountToPrune, int minCheckpointCountToKeep)
    {
        if (_checkpoints.Count < minCheckpointCountToPrune) return 0;
        int pruned = 0;
        while (_checkpoints.Count > minCheckpointCountToKeep && pruned < count)
        {
            RemoveAt(_checkpoints.Count - 1);
            pruned++;
        }
        return pruned;
    }

    private void RemoveAt(int index)
    {
        var cp = _checkpoints[index];
        try { if (!string.IsNullOrEmpty(cp.StatePath) && File.Exists(cp.StatePath)) File.Delete(cp.StatePath); }
        catch { /* best-effort cleanup */ }
        _checkpoints.RemoveAt(index);
    }

    /// <summary>Escalating time-up prune count: resets to 0 and starts counting again whenever
    /// the current (world,level) differs from the last time-up's, otherwise multiplies
    /// TimeUpPruneCount by an ever-increasing counter each additional time-up in the same level.</summary>
    public int NextTimeUpPruneCountForLevel(Smb1State now)
    {
        if (now.World != _timeUpPenaltyWorld || now.Level != _timeUpPenaltyLevel)
            ResetTimeUpPrunePenaltyForLevel(now);
        if (_timeUpPrunePenaltyMultiplier < int.MaxValue) _timeUpPrunePenaltyMultiplier++;
        long scaled = (long)_cfg.TimeUpPruneCount * _timeUpPrunePenaltyMultiplier;
        return scaled > int.MaxValue ? int.MaxValue : (int)scaled;
    }

    public void ResetTimeUpPrunePenaltyForLevel(Smb1State now)
    {
        _timeUpPenaltyWorld = now.World;
        _timeUpPenaltyLevel = now.Level;
        _timeUpPrunePenaltyMultiplier = 0;
    }

    /// <summary>Removes the single most-recent scroll-milestone checkpoint that looks like it
    /// trapped the model near a death (created recently, was just reloaded recently, or sits
    /// spatially close to the current death position), and blocks recreating one at that spot
    /// for MilestoneRecreateBlockFrames.</summary>
    public bool InvalidateRecentScrollMilestone(Smb1State now)
    {
        int windowFrames = Math.Max(_cfg.MilestoneDeathInvalidationFrames, _cfg.ReloadCooldownFrames + 30);
        int xWindow = Math.Clamp(_cfg.ScrollMilestoneSpacing / 2, 32, 96);

        for (int i = _checkpoints.Count - 1; i >= 0; i--)
        {
            var cp = _checkpoints[i];
            if (cp.EventType != SelfPlayEventType.ScrollMilestone) continue;

            int ageFrames = _lifetimeFrameIndex - cp.FrameIndex;
            bool recentCreation = ageFrames >= 0 && ageFrames <= windowFrames;
            bool loadedTrap = cp.Id == LastReloadCheckpointId
                && LastReloadCheckpointEventType == SelfPlayEventType.ScrollMilestone
                && FramesSinceReload <= windowFrames;
            bool nearDeathPosition = cp.World == now.World && cp.Level == now.Level
                && Math.Abs(cp.XPosition - now.MarioXPosition) <= xWindow;

            if (!recentCreation && !loadedTrap && !nearDeathPosition) continue;

            _milestoneBlocks.Add((cp.World, cp.Level, cp.XPosition, _lifetimeFrameIndex + SelfPlayConfig.MilestoneRecreateBlockFrames));
            RemoveAt(i);
            return true;
        }
        return false;
    }

    private bool IsScrollMilestoneRecreationBlocked(Smb1State now)
    {
        int xWindow = Math.Clamp(_cfg.ScrollMilestoneSpacing / 2, 32, 96);
        _milestoneBlocks.RemoveAll(b => b.ExpiresAtFrame < _lifetimeFrameIndex);
        foreach (var b in _milestoneBlocks)
        {
            if (b.World == now.World && b.Level == now.Level && Math.Abs(b.CenterX - now.MarioXPosition) <= xWindow)
                return true;
        }
        return false;
    }
}
