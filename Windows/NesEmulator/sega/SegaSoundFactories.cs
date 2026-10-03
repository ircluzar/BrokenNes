using System;
using System.Collections.Generic;
using NesEmulator.Systems;

namespace NesEmulator.Sega;

/// <summary>What a Master System / Game Gear / SG-1000 sound unit is built for.</summary>
/// <param name="Machine">The machine's timeline (region), model and the host's audio rate.</param>
/// <param name="Console">The console: Master System (which also runs SG-1000 and SC-3000 cartridges) or Game Gear.</param>
/// <param name="Variant">Which SN76489: <see cref="PsgVariant.Sega"/> for the Master System and Game Gear, <see cref="PsgVariant.Ti"/> for the SG-1000 and SC-3000.</param>
public sealed record SmsSoundRequest(SegaMachine Machine, ConsoleKind Console, PsgVariant Variant)
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
    };

    public static IEnumerable<string> Ids => Factories.Keys;

    /// <summary>The unit for <paramref name="id"/>; an unknown id is an error, never a quiet substitution.</summary>
    public static ISmsSound Create(string id, SmsSoundRequest request) =>
        Factories.TryGetValue(id, out var f) ? f(request) : throw new ArgumentException($"No Master System sound unit '{id}' (have: {string.Join(", ", Factories.Keys)})");
}

/// <summary>The Genesis sound units (Z80 + YM2612 + PSG), by APU-menu id. <c>MD</c> is the stock unit: today the labelled stand-in, replaced by the real one when the Genesis track delivers it.</summary>
public static class GenesisSoundFactories
{
    public static readonly Dictionary<string, Func<SegaMachine, IGenesisSound>> Factories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MD"] = m => new GenesisSoundStub(m),
    };

    public static IEnumerable<string> Ids => Factories.Keys;

    public static IGenesisSound Create(string id, SegaMachine machine) =>
        Factories.TryGetValue(id, out var f) ? f(machine) : throw new ArgumentException($"No Genesis sound unit '{id}' (have: {string.Join(", ", Factories.Keys)})");
}
