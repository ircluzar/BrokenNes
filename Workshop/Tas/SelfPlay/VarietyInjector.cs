using System;

namespace BrokenNes.Workshop.Tas.SelfPlay;

/// <summary>
/// Randomized exploration: a periodic random left/right flip, plus a stuck-X detector that rolls
/// the same flip when Mario's world position hasn't moved in a while. Ported from
/// UpdateSelfPlayVarietyInjector/UpdateSelfPlayStuckXInjector/StartHorizontalFlipWindow/
/// ApplyHorizontalVarietyFlip (input.cpp ~1439-1523).
/// </summary>
public sealed class VarietyInjector
{
    private readonly SelfPlayConfig _cfg;
    private readonly Random _rng;
    private int _framesUntilRoll;
    private int _lastMarioX = int.MinValue;
    private int _sameXFrames;

    public int HorizontalFlipFramesRemaining { get; private set; }
    public int HorizontalFlipCount { get; private set; }

    private const int Left = 6, Right = 7; // BrokenNes button order

    public VarietyInjector(SelfPlayConfig cfg, Random rng)
    {
        _cfg = cfg;
        _rng = rng;
        _framesUntilRoll = cfg.VarietyRollInterval;
    }

    public void ResetOnReload()
    {
        _lastMarioX = int.MinValue;
        _sameXFrames = 0;
    }

    /// <summary>Call once per frame.</summary>
    public void Update(int marioXPosition)
    {
        if (HorizontalFlipFramesRemaining > 0) HorizontalFlipFramesRemaining--;

        _framesUntilRoll--;
        if (_framesUntilRoll <= 0)
        {
            _framesUntilRoll = _cfg.VarietyRollInterval;
            if (HorizontalFlipFramesRemaining <= 0 && RollPercent(_cfg.VarietyChancePercent))
                StartFlipWindow();
        }

        if (_lastMarioX == marioXPosition)
        {
            _sameXFrames++;
        }
        else
        {
            _lastMarioX = marioXPosition;
            _sameXFrames = 0;
            return;
        }
        if (HorizontalFlipFramesRemaining > 0) return;
        if (_sameXFrames <= _cfg.StuckXFrames) return;
        if ((_sameXFrames - _cfg.StuckXFrames - 1) % _cfg.StuckXRerollInterval != 0) return;
        if (RollPercent(_cfg.StuckXChancePercent)) StartFlipWindow();
    }

    private void StartFlipWindow()
    {
        if (HorizontalFlipFramesRemaining > 0) return;
        int span = _cfg.VarietyFlipMaxFrames - _cfg.VarietyFlipMinFrames + 1;
        HorizontalFlipFramesRemaining = _cfg.VarietyFlipMinFrames + _rng.Next(span);
        HorizontalFlipCount++;
    }

    private bool RollPercent(int chancePercent)
    {
        if (chancePercent <= 0) return false;
        if (chancePercent >= 100) return true;
        return _rng.Next(100) < chancePercent;
    }

    /// <summary>Swaps Left/Right in the final merged button array while a flip window is active -
    /// a pure bit-swap on the output, doesn't touch hold-remaining state.</summary>
    public bool[] ApplyFlip(bool[] buttons)
    {
        if (HorizontalFlipFramesRemaining <= 0) return buttons;
        var result = (bool[])buttons.Clone();
        (result[Left], result[Right]) = (result[Right], result[Left]);
        return result;
    }
}
