using BrokenNes.Fruity;
using NesEmulator.Plugin;

namespace BrokenNes2;

/// <summary>Parameter indices of BrokenNes2 (the order is part of saved projects and automation: append only).</summary>
public static class P
{
    public const int Core = 0, Volume = 1, Pan = 2, Coarse = 3, Fine = 4, Duty = 5, Channel = 6, NoiseMode = 7;
    public const int Count = 8;
}

public static class Bn2Params
{
    public static readonly string[] DutyNames = ["12.5%", "25%", "50%", "75%"];
    public static readonly string[] ChannelNames = ["Auto (note colour)", "Pulse 1", "Pulse 2", "Triangle", "Noise"];

    /// <summary>Every NES APU core BrokenNes knows (FIX, QN, DMG = Game Boy sound chip, ...).</summary>
    public static IReadOnlyList<string> Cores => NesApuInstrument.ApuCoreIds;

    public static FruityParam[] Build()
    {
        var cores = Cores;
        int fix = Math.Max(0, cores.ToList().IndexOf("FIX"));
        return
        [
            new("Sound chip", 0, cores.Count - 1, fix, Pi.CantInterpolate, v => (uint)v < cores.Count ? cores[v] : "?"),
            new("Volume", 0, 1000, 1000, 0, v => $"{v / 10.0:0.0}%"),
            new("Pan", -100, 100, 0, Pi.Centered, v => v == 0 ? "Center" : v < 0 ? $"{-v}% L" : $"{v}% R"),
            new("Coarse", -24, 24, 0, Pi.Centered, v => $"{v:+0;-0;0} st"),
            new("Fine", -100, 100, 0, Pi.Centered, v => $"{v:+0;-0;0} ct"),
            new("Pulse duty", 0, 3, 1, Pi.CantInterpolate, v => DutyNames[Math.Clamp(v, 0, 3)]),
            new("Channel", 0, 4, 0, Pi.CantInterpolate, v => ChannelNames[Math.Clamp(v, 0, 4)]),
            new("Noise mode", 0, 1, 0, Pi.CantInterpolate, v => v == 0 ? "Long" : "Short (metallic)"),
        ];
    }
}
