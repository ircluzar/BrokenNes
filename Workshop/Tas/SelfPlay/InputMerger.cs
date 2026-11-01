using System;

namespace BrokenNes.Workshop.Tas.SelfPlay;

public enum PlayStyle { Spazz, Chill }

/// <summary>
/// Converts per-button probabilities into a held/released button state: a hold-replay loop (once
/// a button is pressed, its hold-remaining counter runs down without re-querying that button) plus
/// two conflict-resolution styles. Ported from NESReflexRuntime_ApplyInputOverride's Step 3
/// (input.cpp ~2090-2205) and the chill-specific helpers (~1026-1143).
///
/// "Spazz" (the default/only style in earlier builds) resolves Left/Right and Up/Down conflicts by
/// picking whichever has the higher model probability. "Chill" (55% chance per checkpoint reload)
/// commits to longer, steadier holds - directional presses become 10-30 frame chunks, A is held
/// ~2x longer, other buttons ~1.3x, with per-button cooldowns after release to avoid flickery
/// re-presses.
///
/// Two helper functions this depends on (TemperatureAdjust, top-k filtering) weren't captured
/// verbatim by source research - SelfPlayManager's versions are flagged as best-effort
/// reconstructions using standard, well-known formulas for both.
/// </summary>
public sealed class InputMerger
{
    private readonly Random _rng;
    private readonly int[] _holdRemaining = new int[8];
    private readonly int[] _chillCooldownRemaining = new int[8];

    public PlayStyle Style = PlayStyle.Spazz;
    public byte LastAppliedButtons { get; private set; }

    private const int A = 0, B = 1, Select = 2, Start = 3, Up = 4, Down = 5, Left = 6, Right = 7;

    public InputMerger(Random rng) => _rng = rng;

    public void ResetOnReload()
    {
        Array.Clear(_holdRemaining);
        Array.Clear(_chillCooldownRemaining);
        LastAppliedButtons = 0;
    }

    private static bool IsHorizontal(int b) => b == Left || b == Right;
    private static bool IsDirection(int b) => b == Up || b == Down || b == Left || b == Right;

    /// <summary>probabilities/modelHoldFrames are indexed in BrokenNes button order. Returns the
    /// resolved 8-button state, pre-mask/pre-variety-flip/pre-human-merge (SelfPlayManager applies
    /// those afterward, matching the source's own step ordering).</summary>
    public bool[] Resolve(float[] probabilities, float[] modelHoldFrames, float temperature, SelfPlayConfig cfg)
    {
        bool chill = Style == PlayStyle.Chill;
        byte newPressMask = 0;

        if (chill)
            for (int i = 0; i < 8; i++) if (_chillCooldownRemaining[i] > 0) _chillCooldownRemaining[i]--;

        var pressed = new bool[8];
        for (int i = 0; i < 8; i++)
        {
            if (_holdRemaining[i] > 0)
            {
                pressed[i] = true;
                _holdRemaining[i]--;
                if (chill && IsHorizontal(i))
                    StitchChillDirectionHold(i, probabilities[i], modelHoldFrames[i], cfg);
            }
            else
            {
                if (chill && _chillCooldownRemaining[i] > 0) continue;
                if (ShouldPress(probabilities[i], temperature))
                {
                    pressed[i] = true;
                    newPressMask |= (byte)(1 << i);
                    int frames = ModelHoldFramesForStyle(i, modelHoldFrames[i], chill, cfg) - 1;
                    _holdRemaining[i] = Math.Max(0, frames);
                }
            }
        }

        if (!chill)
        {
            if (pressed[Left] && pressed[Right])
            {
                if (probabilities[Right] >= probabilities[Left]) { pressed[Left] = false; _holdRemaining[Left] = 0; }
                else { pressed[Right] = false; _holdRemaining[Right] = 0; }
            }
            if (pressed[Up] && pressed[Down])
            {
                if (probabilities[Up] >= probabilities[Down]) { pressed[Down] = false; _holdRemaining[Down] = 0; }
                else { pressed[Up] = false; _holdRemaining[Up] = 0; }
            }
        }
        else
        {
            ApplyChillDirectionFilter(pressed);

            byte finalMask = Pack(pressed);
            byte finalNewPresses = (byte)(newPressMask & finalMask);
            bool nonDirectionNewlyPressed = (finalNewPresses & ((1 << A) | (1 << B) | (1 << Select) | (1 << Start))) != 0;
            bool directionNewlyPressed = (finalNewPresses & ((1 << Up) | (1 << Down) | (1 << Left) | (1 << Right))) != 0;
            if (nonDirectionNewlyPressed) for (int b = Up; b <= Right; b++) _chillCooldownRemaining[b] = 0;
            if (directionNewlyPressed) ReduceNonDirectionCooldowns(20);
            for (int i = 0; i < 8; i++)
                if (((finalNewPresses >> i) & 1) != 0)
                    _chillCooldownRemaining[i] = SampleCooldownFrames();
        }

        LastAppliedButtons = Pack(pressed);
        return pressed;
    }

    private bool ShouldPress(float probability, float temperature) =>
        temperature <= 0.01f ? probability >= 0.5f : _rng.NextDouble() < probability;

    private int ModelHoldFramesForStyle(int button, float modelHoldFrames, bool chill, SelfPlayConfig cfg)
    {
        if (chill && IsHorizontal(button))
            return SampleChillHorizontalCommitmentFrames(modelHoldFrames);

        float frames = modelHoldFrames;
        if (chill) frames *= button == A ? 2.00f : 1.30f;

        int rounded = (int)(frames + 0.5f);
        if (rounded < 1) rounded = 1;
        if (chill)
        {
            if (button == A && rounded < SelfPlayConfig.ChillAMinHoldFrames) rounded = SelfPlayConfig.ChillAMinHoldFrames;
            else if (IsDirection(button) && rounded < SelfPlayConfig.ChillDpadMinHoldFrames) rounded = SelfPlayConfig.ChillDpadMinHoldFrames;
        }
        return rounded;
    }

    private int SampleChillHorizontalCommitmentFrames(float modelHoldFrames)
    {
        int span = SelfPlayConfig.ChillHorizontalCommitMaxFrames - SelfPlayConfig.ChillHorizontalCommitMinFrames + 1;
        int randomFrames = SelfPlayConfig.ChillHorizontalCommitMinFrames + _rng.Next(span);
        int modelFrames = (int)(modelHoldFrames * 1.30f + 0.5f);
        int frames = Math.Max(modelFrames, randomFrames);
        return Math.Clamp(frames, SelfPlayConfig.ChillHorizontalCommitMinFrames, SelfPlayConfig.ChillHorizontalCommitMaxFrames);
    }

    private void StitchChillDirectionHold(int button, float probability, float modelHoldFrames, SelfPlayConfig cfg)
    {
        if (!IsHorizontal(button)) return;
        float threshold = SelfPlayConfig.ChillDpadStitchThresholdX100 / 100f;
        if (probability < threshold) return;
        int commitmentFrames = SampleChillHorizontalCommitmentFrames(modelHoldFrames);
        if (_holdRemaining[button] < commitmentFrames) _holdRemaining[button] = commitmentFrames;
    }

    private void ApplyChillDirectionFilter(bool[] pressed)
    {
        if (pressed[Left] && pressed[Right])
        {
            bool keepRight;
            if (_holdRemaining[Right] != _holdRemaining[Left])
                keepRight = _holdRemaining[Right] > _holdRemaining[Left];
            else
            {
                bool rightWasApplied = (LastAppliedButtons & (1 << Right)) != 0;
                bool leftWasApplied = (LastAppliedButtons & (1 << Left)) != 0;
                keepRight = rightWasApplied != leftWasApplied ? rightWasApplied : true; // final tie-break: Right
            }
            if (keepRight) { pressed[Left] = false; _holdRemaining[Left] = 0; }
            else { pressed[Right] = false; _holdRemaining[Right] = 0; }
        }
        if (pressed[Up] && pressed[Down])
        {
            pressed[Up] = false; pressed[Down] = false;
            _holdRemaining[Up] = 0; _holdRemaining[Down] = 0;
        }
    }

    private static readonly int[] NonDirectionButtons = { A, B, Select, Start };

    private void ReduceNonDirectionCooldowns(int percent)
    {
        foreach (int b in NonDirectionButtons)
            if (_chillCooldownRemaining[b] > 0)
                _chillCooldownRemaining[b] = (_chillCooldownRemaining[b] * percent + 99) / 100;
    }

    private int SampleCooldownFrames() =>
        SelfPlayConfig.ChillCooldownMinFrames + _rng.Next(SelfPlayConfig.ChillCooldownMaxFrames - SelfPlayConfig.ChillCooldownMinFrames + 1);

    private static byte Pack(bool[] buttons)
    {
        byte mask = 0;
        for (int i = 0; i < 8; i++) if (buttons[i]) mask |= (byte)(1 << i);
        return mask;
    }
}
