// The x86 console's sound card choice (the APU harvest): the card and its engine, as one x86 APU id. Written for BrokenNes.

using System;
using System.Collections.Generic;
using System.Linq;

namespace NesEmulator.X86;

/// <summary>
/// x86 APU ids: "ADLIB" = the AdLib (YM3812) with ymfm, "ADLIB_&lt;engine&gt;" = the AdLib with another emulator's OPL2 core (<see cref="OplEngines"/>).
/// The PC speaker is always on (it is part of the PC).
/// </summary>
public static class PcSound
{
    private static readonly (string id, string name)[] Known =
    {
        ("M036", "MAME 0.36 fmopl (1999: log tables, cached LFO, instant release at rate 0)"),
        ("D072", "DOSBox 0.72 (MAME fmopl 0.70: 3.6 MHz, 22 kHz unresampled, 1 ms write blocks)"),
        ("D074C", "DOSBox 0.74 compat (Ken Silverman's ADLIBEMU: float envelopes, stepped)"),
        ("D074F", "DOSBox 0.74 fast (DBOPL: linear sine tables, block LFOs, 2x level)"),
        ("NUK174", "Nuked-OPL3 1.7.4 (AdPlug 2.3.1: before the 1.8 die-shot fixes)"),
    };

    /// <summary>The AdLib engines on the menu (id without the ADLIB_ prefix, label), the ones that are built in.</summary>
    public static IEnumerable<(string id, string label)> AdlibEngineLabels =>
        Known.Where(k => OplEngines.Factories.ContainsKey(k.id)).Select(k => (k.id, $"ADLIB_{k.id} (AdLib on {k.name} + PC speaker)"));

    public static string Describe(string apu)
    {
        if (apu.Equals("ADLIB", StringComparison.OrdinalIgnoreCase)) return "AdLib YM3812 OPL2, ymfm + PC speaker";
        var k = Known.FirstOrDefault(x => ("ADLIB_" + x.id).Equals(apu, StringComparison.OrdinalIgnoreCase));
        return k.id != null ? $"AdLib on {k.name} + PC speaker" : apu;
    }

    /// <summary>Puts the card and engine of <paramref name="apu"/> in the machine (live: held notes are re-keyed). False for an id it does not know.</summary>
    public static bool Apply(PcMachine m, string apu)
    {
        if (string.IsNullOrEmpty(apu) || apu.Equals("ADLIB", StringComparison.OrdinalIgnoreCase)) { m.Opl.Engine = null; return true; }
        if (apu.StartsWith("ADLIB_", StringComparison.OrdinalIgnoreCase) && OplEngines.Factories.TryGetValue(apu[6..], out var f)) { m.Opl.Engine = f(); return true; }
        return false;
    }
}
