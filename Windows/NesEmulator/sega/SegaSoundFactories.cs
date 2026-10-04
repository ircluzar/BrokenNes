using System;
using System.Collections.Generic;
using NesEmulator.Systems;

namespace NesEmulator.Sega;

/// <summary>What a Master System / Game Gear / SG-1000 sound unit is built for.</summary>
/// <param name="Machine">The machine's timeline (region), model and the host's audio rate.</param>
/// <param name="Console">The console: Master System (which also runs SG-1000 and SC-3000 cartridges) or Game Gear.</param>
/// <param name="Variant">Which SN76489: <see cref="PsgVariant.Sega"/> for the Master System and Game Gear, <see cref="PsgVariant.Ti"/> for the SG-1000 and SC-3000.</param>
/// <param name="Detail">What follows the colon in a menu id such as <c>NES:FIXS</c> (the NES sound chip that plays the PSG); null for an id without one.</param>
public sealed record SmsSoundRequest(SegaMachine Machine, ConsoleKind Console, PsgVariant Variant, string? Detail = null)
{
    /// <summary>The Z80 clock the sound unit is run by (a third of the master clock).</summary>
    public double CpuClockHz => Machine.Timeline.MasterHz / Machine.Timeline.Z80.Divider;
}

/// <summary>
/// The sound units a Master System / Game Gear session can be given, by the id the console's APU menu uses (<c>CoreCatalog</c>). Each id maps to a factory; adding a unit is one line in
/// <see cref="Factories"/>. The board never names a unit: its session asks here for the one the user picked, so bridges (a Sega game's sound on NES chips) plug in without touching the board.
/// Ids: <c>SMS</c> is the stock unit (PSG, Game Gear stereo, and the YM2413 once the Master System track adds it).
/// </summary>
public static class SmsSoundFactories
{
    public static readonly Dictionary<string, Func<SmsSoundRequest, ISmsSound>> Factories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SMS"] = r => new SmsSound(r.CpuClockHz, r.Machine.SampleRate, r.Variant),
        ["NES"] = r => new SmsSoundOnNes(r),   // Track C: the PSG replayed on a NES sound chip ("NES" = FIX, "NES:FIXS" ... name the chip)
        ["GG"] = r => new SmsSound(r.CpuClockHz, r.Machine.SampleRate, r.Variant),   // the Game Gear menu's id for the same unit (its stereo port is part of it)
    };

    public static IEnumerable<string> Ids => Factories.Keys;

    /// <summary>
    /// The unit for <paramref name="id"/>; an unknown id is an error, never a quiet substitution. A menu id with a colon (<c>NES:FIXS</c>) is looked up whole first, then by the part
    /// before the colon, with the rest handed to the factory in <see cref="SmsSoundRequest.Detail"/>.
    /// </summary>
    public static ISmsSound Create(string id, SmsSoundRequest request)
    {
        if (Factories.TryGetValue(id, out var f)) return f(request);
        int colon = id.IndexOf(':');
        if (colon > 0 && Factories.TryGetValue(id[..colon], out var g)) return g(request with { Detail = id[(colon + 1)..] });
        throw new ArgumentException($"No Master System sound unit '{id}' (have: {string.Join(", ", Factories.Keys)})");
    }
}

/// <summary>The Genesis sound units (Z80 + YM2612 + PSG), by APU-menu id. <c>MD</c> is the stock unit: the real Z80, the PSG and the YM2612's registers and timers (<see cref="MdSoundUnit"/>; its FM makes no
/// sound yet, and it says so in <c>CoreName</c>). <c>MD-STUB</c> is the labelled stand-in with no Z80 at all.</summary>
public static class GenesisSoundFactories
{
    public static readonly Dictionary<string, Func<SegaMachine, IGenesisSound>> Factories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MD"] = m => new MdSoundUnit(m),
        ["MD-STUB"] = m => new GenesisSoundStub(m),
    };

    public static IEnumerable<string> Ids => Factories.Keys;

    public static IGenesisSound Create(string id, SegaMachine machine) =>
        Factories.TryGetValue(id, out var f) ? f(machine) : throw new ArgumentException($"No Genesis sound unit '{id}' (have: {string.Join(", ", Factories.Keys)})");
}
