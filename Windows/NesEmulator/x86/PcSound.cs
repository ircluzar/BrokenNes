// The x86 console's sound card choice (the APU harvest): the card and its engine, as one x86 APU id. Written for BrokenNes.

using System;
using System.Collections.Generic;
using System.Linq;

namespace NesEmulator.X86;

/// <summary>
/// x86 APU ids, one family per sound card (the family label is the card's name): "ADLIB" = the AdLib (YM3812) with ymfm, "ADLIB_&lt;engine&gt;" = the AdLib with
/// another emulator's OPL2 core (<see cref="OplEngines"/>); "SB_D072" = DOSBox 0.72's Sound Blaster Pro 2 with the AdLib (ymfm) as its FM, "SB_D072_&lt;engine&gt;" the same
/// with another OPL2 engine; "GUS_D074" = DOSBox 0.74's Gravis Ultrasound; "TANDY_D072" = DOSBox 0.72's Tandy SN76496; "SPEAKER" = the PC speaker alone (no AdLib);
/// "COVOX" = a Covox Speech Thing on LPT1. The PC speaker is always on (it is part of the PC); only the ADLIB and SB families have the AdLib at 388h.
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

    public const string SbLabel = "Sound Blaster Pro 2, DOSBox 0.72 (DSP 3.02 at 220h, IRQ 7, DMA 1; 1 ms smeared direct DAC, mixer-pulled DMA)";
    public const string GusLabel = "Gravis Ultrasound, DOSBox 0.74 (240h, IRQ 5, DMA 3, 1 MB; mono, AutoAmp that never recovers)";
    public const string TandyLabel = "Tandy SN76496, DOSBox 0.72 (C0h; 15-bit periodic noise, unipolar output, mutes 5 s after the last write)";
    public const string CovoxLabel = "Covox Speech Thing on LPT1 (378h, 8-bit DAC, sample-and-hold)";

    /// <summary>The non-AdLib cards on the menu: (id, label, family).</summary>
    public static IEnumerable<(string id, string label, string family)> CardOptions()
    {
        yield return ("SB_D072", $"SB_D072 ({SbLabel} + AdLib ymfm + PC speaker)", "Sound Blaster");
        foreach (var k in Known.Where(k => OplEngines.Factories.ContainsKey(k.id))) yield return ("SB_D072_" + k.id, $"SB_D072_{k.id} (Sound Blaster Pro 2, DOSBox 0.72, its FM on {k.name} + PC speaker)", "Sound Blaster");
        yield return ("GUS_D074", $"GUS_D074 ({GusLabel} + PC speaker)", "Gravis Ultrasound");
        yield return ("TANDY_D072", $"TANDY_D072 ({TandyLabel} + PC speaker)", "Tandy");
        yield return ("SPEAKER", "SPEAKER (the PC speaker only: no AdLib)", "PC speaker");
        yield return ("COVOX", $"COVOX ({CovoxLabel} + PC speaker)", "Covox");
    }

    /// <summary>The FDAUTO.BAT lines that let DOS programs find the card of <paramref name="apu"/> (CRLF-ended; empty for cards with no variable), matching the cards' defaults:
    /// the SB Pro 2 at 220h / IRQ 7 / DMA 1 (T4), the GUS at 240h / DMA 3 / IRQ 5 (ULTRADIR names C:\ULTRASND, but no GUS patches are shipped). Written only when the boot disk
    /// is built for a program (<see cref="PcDisks.ForProgram"/>, at session creation); a disk image opened as it is keeps its own AUTOEXEC (no safe hook: it is the user's disk);
    /// after a live card swap the variables are stale until a new session builds a new disk (the running DOS keeps the environment it booted with).</summary>
    public static string DosEnv(string apu)
    {
        string u = (apu ?? "").ToUpperInvariant();
        if (u == "SB_D072" || u.StartsWith("SB_D072_")) return "SET BLASTER=A220 I7 D1 T4\r\n";
        if (u == "GUS_D074") return "SET ULTRASND=240,3,3,5,5\r\nSET ULTRADIR=C:\\ULTRASND\r\n";
        return "";
    }

    public static string Describe(string apu)
    {
        string u = apu.ToUpperInvariant();
        if (u == "ADLIB") return "AdLib YM3812 OPL2, ymfm + PC speaker";
        if (u == "SB_D072") return SbLabel + " + AdLib ymfm + PC speaker";
        if (u.StartsWith("SB_D072_")) { var e = Known.FirstOrDefault(x => x.id == u[8..]); return e.id != null ? $"{SbLabel}, FM on {e.name} + PC speaker" : apu; }
        if (u == "GUS_D074") return GusLabel + " + PC speaker";
        if (u == "TANDY_D072") return TandyLabel + " + PC speaker";
        if (u == "SPEAKER") return "PC speaker only";
        if (u == "COVOX") return CovoxLabel + " + PC speaker";
        var k = Known.FirstOrDefault(x => ("ADLIB_" + x.id).Equals(apu, StringComparison.OrdinalIgnoreCase));
        return k.id != null ? $"AdLib on {k.name} + PC speaker" : apu;
    }

    /// <summary>Puts the card and engine of <paramref name="apu"/> in the machine (live: held notes are re-keyed). False for an id it does not know.
    /// A different card starts cold (its state is not carried across a swap); the same card stays as it is.</summary>
    public static bool Apply(PcMachine m, string apu)
    {
        string u = string.IsNullOrEmpty(apu) ? "ADLIB" : apu.ToUpperInvariant();
        string card; string? engine;
        if (u == "ADLIB") { card = ""; engine = null; }
        else if (u.StartsWith("ADLIB_")) { card = ""; engine = u[6..]; }
        else if (u == "SB_D072") { card = "SB_D072"; engine = null; }
        else if (u.StartsWith("SB_D072_")) { card = "SB_D072"; engine = u[8..]; }
        else if (u is "GUS_D074" or "TANDY_D072" or "COVOX" or "SPEAKER") { card = u == "SPEAKER" ? "" : u; engine = null; }
        else return false;
        Func<IOplEngine>? f = null;
        if (engine != null && !OplEngines.Factories.TryGetValue(engine, out f)) return false;
        bool adlib = u.StartsWith("ADLIB") || card == "SB_D072";
        m.Opl.Engine = f?.Invoke();
        m.AdlibPresent = adlib;
        if ((m.Card?.Id ?? "") != card)
            m.SetCard(card switch
            {
                "SB_D072" => new Dosbox072SoundBlaster(m),
                "GUS_D074" => new Dosbox074Gus(m),
                "TANDY_D072" => new Dosbox072Tandy(m),
                "COVOX" => new CovoxCard(m),
                _ => null,
            });
        return true;
    }
}
