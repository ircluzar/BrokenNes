using BrokenNes.Fruity;

namespace BrokenNes2;

/// <summary>Parameter indices of Bogue :: BrokenNes 2 (the order is part of saved projects and automation: append only from here on).
/// <c>Core</c> (the sound chip), <c>Mode</c> and <c>Console</c> belong to the emulator the instance is attached to and are shared by every
/// instance on it; the others belong to the instance.</summary>
public static class P
{
    public const int Core = 0, Volume = 1, Pan = 2, Coarse = 3, Fine = 4, Duty = 5, Channel = 6, NoiseMode = 7;
    public const int Mode = 8, Emulator = 9, Console = 10, Instrument = 11;
    public const int Count = 12;

    public static bool IsEmulatorLevel(int index) => index is Core or Mode or Console;
}

/// <summary>The channels of an emulator, as the instance's Channel parameter lists them (value 0 is Auto, value n is channel n-1).</summary>
public static class Ch
{
    public const int Pulse1 = 0, Pulse2 = 1, Triangle = 2, Noise = 3, Mix = 4;
    public const int Count = 5, ToneCount = 4;
    /// <summary>Instrument Runaway on a SNES game: every instance is a sampler of the game's instruments (not one of the five channels, and not exclusive).</summary>
    public const int Sampler = 5;
    public static readonly string[] Names = ["Pulse 1", "Pulse 2", "Triangle", "Noise", "Mix (ROM only)"];
    public static readonly string[] ShortNames = ["Pulse 1", "Pulse 2", "Triangle", "Noise", "Mix"];
    public static string Label(int channel) => channel == Sampler ? "Sampler" : ShortNames[Math.Clamp(channel, 0, Count - 1)];
}

public static class Bn2Params
{
    public static readonly string[] DutyNames = ["12.5%", "25%", "50%", "75%"];
    public static readonly string[] ChannelNames = ["Auto", .. Ch.Names];
    public static readonly string[] ModeNames = ["Direct", "ROM"];

    public static string EmulatorText(int v) => v == 0 ? "Auto" : $"#{v}";

    public static FruityParam[] Build() =>
    [
        new("Sound chip", 0, Cx.Chips.Count - 1, 0, Pi.CantInterpolate, v => (uint)v < Cx.Chips.Count ? Cx.Chips[v].Id : "?"),
        new("Volume", 0, 1000, 1000, 0, v => $"{v / 10.0:0.0}%"),
        new("Pan", -100, 100, 0, Pi.Centered, v => v == 0 ? "Center" : v < 0 ? $"{-v}% L" : $"{v}% R"),
        new("Coarse", -24, 24, 0, Pi.Centered, v => $"{v:+0;-0;0} st"),
        new("Fine", -100, 100, 0, Pi.Centered, v => $"{v:+0;-0;0} ct"),
        new("Pulse duty", 0, 3, 1, Pi.CantInterpolate, v => DutyNames[Math.Clamp(v, 0, 3)]),
        new("Channel", 0, Ch.Count, 0, Pi.CantInterpolate, v => ChannelNames[Math.Clamp(v, 0, Ch.Count)]),
        new("Noise mode", 0, 1, 0, Pi.CantInterpolate, v => v == 0 ? "Long" : "Short (metallic)"),
        new("Mode", 0, 1, 0, Pi.CantInterpolate, v => ModeNames[Math.Clamp(v, 0, 1)]),
        new("Emulator", 0, EmulatorHub.MaxEmulators, 0, Pi.CantInterpolate, EmulatorText),
        new("Console", 0, Cx.Count - 1, 0, Pi.CantInterpolate, v => Cx.Names[Math.Clamp(v, 0, Cx.Count - 1)]),
        new("Instrument", 0, 255, 0, Pi.CantInterpolate, v => $"#{v}"),
    ];
}
