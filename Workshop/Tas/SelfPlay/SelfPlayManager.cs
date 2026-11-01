using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BrokenNes.Workshop.Tas;
using NesEmulator;

namespace BrokenNes.Workshop.Tas.SelfPlay;

/// <summary>
/// Orchestrates one self-play frame: pushes the observation into the inference pipe, resolves a
/// button decision, and runs the SMB1 event/checkpoint state machine. Ported from
/// NESReflexRuntime_OnFrameComplete + NESReflexRuntime_ApplyInputOverride (input.cpp), currently
/// SMB1-only (game_id 133) - a later pass would extract the profile-specific pieces (RAM
/// addresses, event predicates, checkpoint uses) behind an interface the way the source's own
/// design doc describes, if a second game's profile is ever added.
///
/// Known deliberate simplification: the source intercepts two RAM writes mid-frame ($00FC death-
/// music-queue, $0759 timer-expired-flag) to catch values that might get overwritten again before
/// frame-end - BrokenNes has no generic write-interception hook, so this only reads state after
/// each frame completes. This is low-risk: the death-jingle bit persists for many frames once set
/// (edge-detected fine post-frame), and the time-up path already falls through to a prune-only,
/// no-reload behavior on the source's own non-intercepted path, which is exactly what a
/// post-frame-only read naturally produces here.
/// </summary>
public sealed class SelfPlayManager
{
    private readonly SelfPlayConfig _cfg;
    private readonly CheckpointManager _checkpoints;
    private readonly InferencePipeClient _pipe;
    private readonly InputMerger _merger;
    private readonly VarietyInjector _variety;
    private readonly Random _rng;

    private byte _modelButtonMask;
    private float _temperature = 1.0f;
    private int _topK = 8;
    private readonly byte[] _holdElapsed = new byte[8];

    private Smb1State? _previous;
    private int _deathStateFrames;
    private int _lastAliveLives = -1;

    // Auto-boot: the source project never solved this - a human always had to press Start
    // manually, both for the very first run and again every time the game returns to the title
    // screen after a Game Over. Detected via OperMode==0 (title/demo screen, the well-known SMB1
    // RAM convention - not independently re-verified against source, unlike the addresses in
    // Smb1Addr which were read directly from input.cpp). Continuous, not one-shot: fires again
    // any time the title screen reappears, self-healing through repeated Game Overs.
    private const int AutoStartPressFrames = 4;
    private const int AutoStartCooldownFrames = 120; // ~2s - avoid re-tapping Start during the title->game transition
    private int _autoStartPressFramesRemaining;
    private int _autoStartCooldownFramesRemaining;
    public int AutoStartPressCount { get; private set; }

    // Continuous "best of all segments" input log: appended once per frame in ComputeFrameInput
    // (before RunFrame), so its length always equals CheckpointManager's lifetime frame counter -
    // a checkpoint's FrameIndex is therefore exactly this log's length at the moment the checkpoint
    // was captured. On reload we truncate back to that length, discarding the dead-end frames that
    // led to the death/timeout, the same effect FCEUX gets incidentally from movie/savestate
    // coupling (see CheckpointManager's class doc and project_tas_ml_integration memory). What
    // survives at any point in time is therefore always a single continuous, replayable path from
    // frame 0 to now - export it with ExportInputLog or by reading InputLog directly.
    private readonly List<bool[]> _inputLog = new();
    public IReadOnlyList<bool[]> InputLog => _inputLog;

    // Parallel to _inputLog, one FrameWitnessBreakdown per frame, appended in OnFrameComplete (after
    // RunFrame) so it captures the resulting state, truncated in lockstep with _inputLog on reload.
    // Exists purely so an exported movie can be verified (see MovieVerifyCli) to replay frame-for-
    // frame identically to what was actually simulated live - not just "doesn't crash".
    private readonly List<FrameWitnessBreakdown> _witnessLog = new();
    public IReadOnlyList<FrameWitnessBreakdown> WitnessLog => _witnessLog;

    // Auto-export: writes a fresh .fm2 (+ witness sidecar) whenever a Game Over happens (the
    // natural "episode boundary", after the resulting checkpoint reload/truncation) and, as a
    // safety net for long gaps between deaths, every AutoExportPeriodicFrames frames. Off by
    // default - call EnableAutoExport to turn it on.
    private string? _autoExportDir;
    private byte[]? _autoExportRomMd5;
    private string? _autoExportRomFilename;
    private int _autoExportPeriodicFrames;
    private int _framesSinceAutoExport;
    public int AutoExportCount { get; private set; }
    public string? LastAutoExportPath { get; private set; }

    public int GameId { get; }
    public CheckpointManager Checkpoints => _checkpoints;
    public bool IsPipeConnected => _pipe.IsConnected;
    public PlayStyle CurrentStyle => _merger.Style;
    public float CurrentTemperature => _temperature;
    public int CurrentTopK => _topK;
    public int BlockedStartCount { get; private set; }
    public int ReloadCount { get; private set; }
    public int CheckpointsCreated { get; private set; }

    private const int A = 0, B = 1, Select = 2, Start = 3, Up = 4, Down = 5, Left = 6, Right = 7;

    public SelfPlayManager(SelfPlayConfig cfg, string checkpointDir, int gameId = Smb1Addr.GameId, int? seed = null)
    {
        cfg.Validate();
        _cfg = cfg;
        _checkpoints = new CheckpointManager(cfg, checkpointDir);
        _pipe = new InferencePipeClient();
        _rng = seed.HasValue ? new Random(seed.Value) : new Random();
        _merger = new InputMerger(_rng);
        _variety = new VarietyInjector(cfg, _rng);
        GameId = gameId;
        // SMB1 default profile: the model may never press Start; a human/manual override still
        // can (merged in unconditionally after masking - see ComputeFrameInput).
        _modelButtonMask = (byte)(0xFF & ~(1 << Start));
        RandomizeModelSettings();
    }

    public bool ConnectPipe(string pipeName = "nesreflex_inference", int timeoutMs = 2000) => _pipe.Connect(pipeName, timeoutMs);
    public void DisconnectPipe() => _pipe.Disconnect();

    /// <summary>Call once per frame, BEFORE nes.RunFrame(): computes the button state to apply
    /// this frame (model decision merged with any manual/user override), given the state left
    /// over from the frame that just finished.</summary>
    public bool[] ComputeFrameInput(NES nes, bool[]? userOverrideP1 = null)
    {
        _pipe.PushFrame(nes, Unpack(_merger.LastAppliedButtons), _holdElapsed);

        float[] probs, holdFrames;
        var response = _pipe.Query(GameId);
        if (response != null)
        {
            var (logits, rawHold) = response.Value;
            probs = new float[8];
            holdFrames = new float[8];
            for (int i = 0; i < 8; i++)
            {
                probs[i] = TemperatureAdjust(Sigmoid(logits[i]), _temperature);
                holdFrames[i] = Math.Clamp(rawHold[i] * 32f, 1f, 32f);
            }
            ApplyTopK(probs, _topK);
        }
        else
        {
            // Fallback when disconnected: a minimal, deliberately simple "do nothing" policy
            // rather than the source's elaborate chaos-based placeholder (that placeholder exists
            // to make an unconnected session still look alive; parity with a real trained model
            // only matters on the connected path, which is what this port is verified against).
            probs = new float[8];
            holdFrames = new float[8];
        }

        var modelButtons = _merger.Resolve(probs, holdFrames, _temperature, _cfg);
        modelButtons = _variety.ApplyFlip(modelButtons);

        bool autoStartActive = ComputeAutoStart();

        var final = new bool[8];
        byte blockedByMask = 0;
        for (int i = 0; i < 8; i++)
        {
            bool modelWantsIt = modelButtons[i];
            bool allowedByMask = ((_modelButtonMask >> i) & 1) != 0;
            if (modelWantsIt && !allowedByMask) blockedByMask |= (byte)(1 << i);
            final[i] = (modelWantsIt && allowedByMask) || (userOverrideP1 != null && userOverrideP1[i]) || (i == Start && autoStartActive);
        }
        if ((blockedByMask & (1 << Start)) != 0) BlockedStartCount++;

        for (int i = 0; i < 8; i++)
            _holdElapsed[i] = final[i] ? (byte)Math.Min(64, _holdElapsed[i] + 1) : (byte)0;

        _inputLog.Add(final);
        return final;
    }

    /// <summary>Call once per frame, AFTER nes.RunFrame(): reads the resulting SMB1 state and
    /// runs event detection / checkpoint management.</summary>
    public void OnFrameComplete(NES nes)
    {
        _checkpoints.Tick();
        _witnessLog.Add(FrameWitness.ComputeBreakdown(nes));

        if (_autoExportDir != null)
        {
            _framesSinceAutoExport++;
            if (_autoExportPeriodicFrames > 0 && _framesSinceAutoExport >= _autoExportPeriodicFrames)
                TryAutoExport(nes);
        }

        var now = Smb1State.Read(nes);

        if (_previous == null)
        {
            _previous = now;
            _lastAliveLives = now.Lives;
            return;
        }
        var prev = _previous;

        _variety.Update(now.MarioXPosition);

        bool strongDeathSignal = Smb1EventDetection.DidLifeDecrease(prev.Lives, now.Lives)
            || Smb1EventDetection.DidDeathJingleStart(prev, now)
            || Smb1EventDetection.DidLifeDecrease(_lastAliveLives, now.Lives);
        bool weakDeathSignal = Smb1EventDetection.IsLikelyDeath(prev, now) || _deathStateFrames >= 1;
        if (strongDeathSignal || (_checkpoints.ReloadCooldownFrames == 0 && weakDeathSignal))
        {
            bool isGameOver = Smb1EventDetection.IsGameOver(now);
            _checkpoints.InvalidateRecentScrollMilestone(now);
            if (isGameOver)
                _checkpoints.Prune(SelfPlayConfig.GameOverPruneCount, 2, 1);
            else if (Smb1EventDetection.IsLowTimerDeath(now))
                _checkpoints.Prune(SelfPlayConfig.LowTimerDeathPruneCount, 2, 1);
            if (_checkpoints.Reload(nes))
            {
                ReloadCount++;
                TruncateLogs(_checkpoints.LastReloadFrameIndex);
            }
            ResetTransientStateAfterReload();
            _previous = Smb1State.Read(nes);
            _deathStateFrames = 0;
            // Game Over is the natural "episode boundary" - export whatever continuous path
            // survives at this point, same as a manual export but automatic, so a long unattended
            // session leaves a trail of movies without needing the button pressed.
            if (isGameOver && _autoExportDir != null) TryAutoExport(nes);
            return;
        }

        bool flagSequenceNow = _checkpoints.FlagSequenceActive || Smb1EventDetection.IsFlagSequenceState(now) || Smb1EventDetection.IsFlagLikeProgress(prev, now);
        if (Smb1EventDetection.IsFlagSequenceState(now)) _checkpoints.FlagSequenceActive = true;
        if (!flagSequenceNow && Smb1EventDetection.IsTimeUp(prev, now))
        {
            // No reload here - matches the source's own fallthrough for time-up signals that
            // weren't caught by the (unimplemented here) mid-frame write interception.
            _checkpoints.Prune(_checkpoints.NextTimeUpPruneCountForLevel(now), _cfg.TimeUpMinCheckpointsToPrune, 1);
        }

        if (_checkpoints.ReloadCooldownFrames == 0)
        {
            if (Smb1EventDetection.IsLevelTransition(prev, now))
            {
                _checkpoints.FlagSequenceActive = false;
                _checkpoints.FlagCooldownFrames = 0;
                _checkpoints.ResetTimeUpPrunePenaltyForLevel(now);
            }
            else if (Smb1EventDetection.IsFlagLikeProgress(prev, now))
            {
                if (!_checkpoints.FlagSequenceActive || Smb1EventDetection.IsFlagSequenceState(now))
                {
                    if (_checkpoints.TryCreate(SelfPlayEventType.Flag, _cfg.FlagCheckpointUses, now, nes)) CheckpointsCreated++;
                    else _checkpoints.FlagSequenceActive = true;
                }
            }
            else if (Smb1EventDetection.IsPowerupGain(prev, now))
            {
                if (_checkpoints.TryCreate(SelfPlayEventType.Powerup, _cfg.PowerupCheckpointUses, now, nes)) CheckpointsCreated++;
            }
            else if (_checkpoints.FlagCooldownFrames == 0 && Smb1EventDetection.IsScoreGain(prev, now))
            {
                if (_checkpoints.TryCreate(SelfPlayEventType.Score, _cfg.ScoreCheckpointUses, now, nes)) CheckpointsCreated++;
            }
            else if (_checkpoints.FlagCooldownFrames == 0 && Smb1EventDetection.IsScrollMilestoneProgress(prev, now, _cfg))
            {
                if (_checkpoints.TryCreate(SelfPlayEventType.ScrollMilestone, _cfg.ScrollMilestoneCheckpointUses, now, nes)) CheckpointsCreated++;
            }
        }

        // Safety net: always keep at least one durable anchor available during ordinary play.
        // Excludes the title screen (OperMode==0) - a baseline checkpoint saved there would just
        // send a later reload straight back to the title screen instead of into real gameplay,
        // fighting the auto-start logic above instead of complementing it.
        if (_checkpoints.Count == 0 && now.OperMode != 0 && !Smb1EventDetection.IsDeathAnimationState(now) && !Smb1EventDetection.IsGameOver(now))
            if (_checkpoints.TryCreate(SelfPlayEventType.Baseline, _cfg.SucceededCheckpointUses, now, nes)) CheckpointsCreated++;

        if (Smb1EventDetection.IsDeathAnimationState(now)) _deathStateFrames++;
        else { _deathStateFrames = 0; _lastAliveLives = now.Lives; }

        _previous = now;
    }

    /// <summary>Returns true if Start should be force-pressed this frame to auto-boot out of the
    /// title screen. Decrements the internal press/cooldown counters as a side effect - call
    /// exactly once per frame.</summary>
    private bool ComputeAutoStart()
    {
        bool active = false;
        if (_autoStartPressFramesRemaining > 0)
        {
            active = true;
            _autoStartPressFramesRemaining--;
        }
        else if (_autoStartCooldownFramesRemaining <= 0 && _previous != null && _previous.OperMode == 0)
        {
            _autoStartPressFramesRemaining = AutoStartPressFrames - 1;
            _autoStartCooldownFramesRemaining = AutoStartCooldownFrames;
            AutoStartPressCount++;
            active = true;
        }
        if (_autoStartCooldownFramesRemaining > 0) _autoStartCooldownFramesRemaining--;
        return active;
    }

    private void TruncateLogs(int frameIndex)
    {
        if (frameIndex >= 0 && frameIndex < _inputLog.Count)
            _inputLog.RemoveRange(frameIndex, _inputLog.Count - frameIndex);
        if (frameIndex >= 0 && frameIndex < _witnessLog.Count)
            _witnessLog.RemoveRange(frameIndex, _witnessLog.Count - frameIndex);
    }

    /// <summary>Writes the current continuous input log (the surviving "best of all segments" path,
    /// live up to whatever frame this is called at - not just at session end) as a single .fm2
    /// movie, plus a "path.witness.json" sidecar (WitnessSidecar) recording one FrameWitness hash
    /// per frame and the exact cores used - MovieVerifyCli replays the .fm2 independently and
    /// compares against this to prove no frame was lost, duplicated, or desynced. p1-only, matching
    /// the SMB1 self-play profile (the model never touches P2).</summary>
    public void ExportFm2(string path, byte[]? romChecksumMd5, string romFilename, string cpuCoreId, string ppuCoreId, string apuCoreId, bool palFlag = false)
    {
        var writer = new Fm2Writer
        {
            PalFlag = palFlag,
            RomFilename = romFilename,
            RomChecksumMd5 = romChecksumMd5,
            Port1 = (int)Fm2InputDevice.None, // self-play is P1-only - the model never touches P2
        };
        writer.Comments.Add($"BrokenNes self-play export: {_inputLog.Count} frames, {ReloadCount} reload(s) already pruned out.");
        foreach (var frame in _inputLog) writer.RecordFrame(false, frame);
        writer.Save(path);

        var sidecar = new WitnessSidecar(
            RomMd5Hex: romChecksumMd5 != null ? Convert.ToHexString(romChecksumMd5).ToLowerInvariant() : null,
            Cpu: cpuCoreId, Ppu: ppuCoreId, Apu: apuCoreId,
            FrameCount: _witnessLog.Count, Frames: new List<FrameWitnessBreakdown>(_witnessLog));
        File.WriteAllText(path + ".witness.json", JsonSerializer.Serialize(sidecar));
    }

    /// <summary>Turns on automatic export: a fresh .fm2 (+ witness sidecar) is written to `dir`
    /// whenever a Game Over happens, and as a safety net every `periodicFrames` frames regardless
    /// (0 disables the periodic trigger). Off by default.</summary>
    public void EnableAutoExport(string dir, byte[]? romMd5, string romFilename, int periodicFrames = 3600)
    {
        Directory.CreateDirectory(dir);
        _autoExportDir = dir;
        _autoExportRomMd5 = romMd5;
        _autoExportRomFilename = romFilename;
        _autoExportPeriodicFrames = periodicFrames;
        _framesSinceAutoExport = 0;
    }

    private void TryAutoExport(NES nes)
    {
        if (_autoExportDir == null || _inputLog.Count == 0) return;
        _framesSinceAutoExport = 0;
        AutoExportCount++;
        string path = Path.Combine(_autoExportDir, $"selfplay_auto_{AutoExportCount:D4}_{_inputLog.Count}f.fm2");
        try
        {
            ExportFm2(path, _autoExportRomMd5, _autoExportRomFilename ?? "rom", nes.GetCpuCoreId(), nes.GetPpuCoreId(), nes.GetApuCoreId());
            LastAutoExportPath = path;
        }
        catch
        {
            // Best-effort: a failed auto-export (e.g. disk full) shouldn't take down self-play.
            AutoExportCount--;
        }
    }

    private void ResetTransientStateAfterReload()
    {
        _merger.ResetOnReload();
        _variety.ResetOnReload();
        Array.Clear(_holdElapsed);
        RandomizeModelSettings();
    }

    private void RandomizeModelSettings()
    {
        _merger.Style = _rng.Next(100) < SelfPlayConfig.ChillStyleChancePercent ? PlayStyle.Chill : PlayStyle.Spazz;
        _topK = _cfg.ReloadTopKMin + _rng.Next(_cfg.ReloadTopKMax - _cfg.ReloadTopKMin + 1);
        int tempX100 = _cfg.ReloadTemperatureMinX100 + _rng.Next(_cfg.ReloadTemperatureMaxX100 - _cfg.ReloadTemperatureMinX100 + 1);
        _temperature = tempX100 / 100f;
    }

    private static bool[] Unpack(byte mask)
    {
        var r = new bool[8];
        for (int i = 0; i < 8; i++) r[i] = ((mask >> i) & 1) != 0;
        return r;
    }

    private static float Sigmoid(float x) => 1f / (1f + MathF.Exp(-x));

    /// <summary>Best-effort reconstruction, not verified against source (the research pass found
    /// its call site but not its body): standard logit-space temperature scaling.</summary>
    private static float TemperatureAdjust(float p, float temperature)
    {
        if (temperature <= 0.01f) return p >= 0.5f ? 1f : 0f;
        p = Math.Clamp(p, 1e-6f, 1f - 1e-6f);
        float logit = MathF.Log(p / (1f - p));
        return Sigmoid(logit / temperature);
    }

    /// <summary>Best-effort reconstruction, not verified against source: zero every probability
    /// outside the top K.</summary>
    private static void ApplyTopK(float[] probs, int topK)
    {
        if (topK >= probs.Length) return;
        var order = Enumerable.Range(0, probs.Length).OrderByDescending(i => probs[i]).ToArray();
        for (int rank = topK; rank < order.Length; rank++) probs[order[rank]] = 0f;
    }
}
