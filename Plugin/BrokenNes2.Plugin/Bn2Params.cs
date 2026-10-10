using BrokenNes.Fruity;

namespace BrokenNes2;

/// <summary>Parameter indices of Bogue :: BrokenNes 2 (the order is part of saved projects and automation: append only from here on).
/// <c>Core</c> (the sound chip), <c>Mode</c> and <c>Console</c> belong to the emulator the instance is attached to and are shared by every
/// instance on it; the others belong to the instance. <c>Voice</c> (appended with the Sega stems) names one of the Sega chips' own voices (FM 1, the DAC ...)
/// when the instance should sit on one rather than on a channel of the NES model; 0 leaves the choice to <c>Channel</c>.</summary>
public static class P
{
    public const int Core = 0, Volume = 1, Pan = 2, Coarse = 3, Fine = 4, Duty = 5, Channel = 6, NoiseMode = 7;
    public const int Mode = 8, Emulator = 9, Console = 10, Instrument = 11, Voice = 12;
    public const int Count = 13;

    public static bool IsEmulatorLevel(int index) => index is Core or Mode or Console;
}

/// <summary>
/// The channels of an emulator, as the instance's Channel parameter lists them (value 0 is Auto, value n is channel n-1) and, for the Sega chips' own voices (appended later,
/// never renumbered), as its Voice parameter lists them (value 0 is none, value n is channel 5 + n).
/// <para>The first five are the NES model (the four registers' channels and the game's whole mix); in a Sega game's ROM mode the first four are the PSG's tone 1, 2, 3 and noise. Sampler (5) is not a channel but what every instance
/// becomes in Instrument Runaway on a game that has instruments to play. 6 to 20 are the Sega FM chips' voices: FM 1 to 9, the Genesis DAC, and the Master System FM chip's five rhythm voices.</para>
/// </summary>
public static class Ch
{
    public const int Pulse1 = 0, Pulse2 = 1, Triangle = 2, Noise = 3, Mix = 4;
    public const int Count = 5, ToneCount = 4;
    /// <summary>Instrument Runaway on a game with instruments: every instance is a sampler of the game's instruments (not one of the channels, and not exclusive).</summary>
    public const int Sampler = 5;
    /// <summary>FM 1 to FM 9 are 6 to 14.</summary>
    public const int Fm1 = 6, Fm9 = 14;
    public const int Dac = 15;
    /// <summary>The Master System FM chip's rhythm voices, in the chip's own order.</summary>
    public const int Bass = 16, HiHat = 17, Snare = 18, Tom = 19, Cymbal = 20;
    /// <summary>The number of channel ids (the arrays that index by channel have this many entries; 5 is the Sampler's hole).</summary>
    public const int Total = 21;

    public static readonly string[] Names = ["Pulse 1", "Pulse 2", "Triangle", "Noise", "Mix (ROM only)"];
    public static readonly string[] ShortNames = ["Pulse 1", "Pulse 2", "Triangle", "Noise", "Mix"];
    /// <summary>Every channel id's name (index = id), with the Sampler at 5.</summary>
    public static readonly string[] AllNames = ["Pulse 1", "Pulse 2", "Triangle", "Noise", "Mix", "Sampler", "FM 1", "FM 2", "FM 3", "FM 4", "FM 5", "FM 6", "FM 7", "FM 8", "FM 9", "DAC", "Bass drum", "Hi-hat", "Snare", "Tom", "Cymbal"];
    /// <summary>The names of channels 0 to 3 in a Sega game's ROM mode (what the stems are).</summary>
    public static readonly string[] PsgNames = ["PSG tone 1", "PSG tone 2", "PSG tone 3", "PSG noise"];

    public static string Label(int channel) => channel is >= 0 and < Total ? AllNames[channel] : "-";

    /// <summary>The name of a channel on a given console and mode: the first four are the PSG's voices in a Sega game's ROM mode.</summary>
    public static string Label(int channel, int console, int mode) =>
        mode == 1 && console is Cx.MasterSystem or Cx.Genesis && channel is >= 0 and < ToneCount ? PsgNames[channel] : Label(channel);

    /// <summary>The channel the Voice parameter's value names (-1: none).</summary>
    public static int OfVoice(int voice) => voice <= 0 ? -1 : 5 + voice;

    /// <summary>The Voice parameter's value for a channel (0 for the channels the Channel parameter names).</summary>
    public static int VoiceOf(int channel) => channel >= Fm1 && channel < Total ? channel - 5 : 0;

    /// <summary>Whether a channel is one of the Sega chips' own voices (the Voice parameter's).</summary>
    public static bool IsChipVoice(int channel) => channel >= Fm1 && channel < Total;
}

public static class Bn2Params
{
    public static readonly string[] DutyNames = ["12.5%", "25%", "50%", "75%"];
    public static readonly string[] ChannelNames = ["Auto", .. Ch.Names];
    public static readonly string[] ModeNames = ["Direct", "ROM"];

    public static string EmulatorText(int v) => v == 0 ? "Auto" : $"#{v}";

    /// <summary>The Voice parameter's text: "-" for none, "FM 1" ... "Cymbal".</summary>
    public static string VoiceText(int v) => v <= 0 ? "-" : (uint)(5 + v) < Ch.Total ? Ch.AllNames[5 + v] : "?";

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
        new("Voice", 0, Ch.Total - 6, 0, Pi.CantInterpolate, VoiceText),
    ];
}
