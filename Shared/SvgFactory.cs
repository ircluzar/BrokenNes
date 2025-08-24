using System;
using System.Reflection;

namespace BrokenNes.Shared;

/// <summary>
/// Inline SVG assets for core cards. Properties are named PREFIX_ID (e.g., CPU_FMC).
/// Each SVG uses viewBox="0 0 212 130" to fit the card image slot exactly.
/// </summary>
public static class SvgFactory
{
    /// <summary>
    /// Lookup an inline SVG by domain prefix and id (case-insensitive). Returns null if not found.
    /// </summary>
    public static string? Get(string? prefix, string? id)
    {
        if (string.IsNullOrWhiteSpace(prefix) || string.IsNullOrWhiteSpace(id)) return null;
        var key = (prefix + "_" + id).ToUpperInvariant();
        var prop = typeof(SvgFactory).GetProperty(key, BindingFlags.Public | BindingFlags.Static);
        if (prop != null)
            return prop.GetValue(null) as string;
        // Fallbacks by domain
        var up = prefix.ToUpperInvariant();
        if (up == "SHADER")
        {
            var def = typeof(SvgFactory).GetProperty("SHADER_DEFAULT", BindingFlags.Public | BindingFlags.Static);
            return def?.GetValue(null) as string;
        }
        return null;
    }

    // --- Sample minimal assets ----------------------------------------------------------
    // Baseline colors
    private const string ChipFillA = "#0f131b";    // panel fill
    private const string ChipFillB = "#111827";    // die fill
    private const string Stroke = "#9ca3af";       // neutral stroke
    // Accent is supplied by the renderer based on rating; assets use the {ACCENT} token.
    private const string AccentToken = "{ACCENT}";

    // CPU_FMC — neutral baseline chip
    public static string CPU_FMC =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='CPU_FMC'>" +
        "  <g>" +
        "    <rect x='10' y='18' width='192' height='94' rx='6' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <g fill='" + Stroke + "'>" +
        // top pins
        "      <rect x='14' y='14' width='10' height='4' rx='1'/>" +
        "      <rect x='34' y='14' width='10' height='4' rx='1'/>" +
        "      <rect x='54' y='14' width='10' height='4' rx='1'/>" +
        "      <rect x='74' y='14' width='10' height='4' rx='1'/>" +
        "      <rect x='94' y='14' width='10' height='4' rx='1'/>" +
        "      <rect x='114' y='14' width='10' height='4' rx='1'/>" +
        "      <rect x='134' y='14' width='10' height='4' rx='1'/>" +
        "      <rect x='154' y='14' width='10' height='4' rx='1'/>" +
        // bottom pins
        "      <rect x='14' y='112' width='10' height='4' rx='1'/>" +
        "      <rect x='34' y='112' width='10' height='4' rx='1'/>" +
        "      <rect x='54' y='112' width='10' height='4' rx='1'/>" +
        "      <rect x='74' y='112' width='10' height='4' rx='1'/>" +
        "      <rect x='94' y='112' width='10' height='4' rx='1'/>" +
        "      <rect x='114' y='112' width='10' height='4' rx='1'/>" +
        "      <rect x='134' y='112' width='10' height='4' rx='1'/>" +
        "      <rect x='154' y='112' width='10' height='4' rx='1'/>" +
        "    </g>" +
        "    <rect x='64' y='38' width='84' height='54' rx='3' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "  </g>" +
        "</svg>";

    // CPU_LOW — reduced pins, outlined die and a subtle notch accent
    public static string CPU_LOW =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='CPU_LOW'>" +
        "  <g>" +
        "    <rect x='10' y='18' width='192' height='94' rx='6' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <g fill='" + Stroke + "'>" +
        "      <rect x='24' y='14' width='10' height='4' rx='1'/>" +
        "      <rect x='64' y='14' width='10' height='4' rx='1'/>" +
        "      <rect x='104' y='14' width='10' height='4' rx='1'/>" +
        "      <rect x='144' y='14' width='10' height='4' rx='1'/>" +
        "      <rect x='24' y='112' width='10' height='4' rx='1'/>" +
        "      <rect x='64' y='112' width='10' height='4' rx='1'/>" +
        "      <rect x='104' y='112' width='10' height='4' rx='1'/>" +
        "      <rect x='144' y='112' width='10' height='4' rx='1'/>" +
        "    </g>" +
        "    <rect x='64' y='38' width='84' height='54' rx='3' fill='none' stroke='" + Stroke + "' stroke-width='2'/>" +
    "    <circle cx='74' cy='48' r='3' fill='" + AccentToken + "'/>" +
        "  </g>" +
        "</svg>";

    // CPU_SPD — chevrons and subtle diagonal stripes
    public static string CPU_SPD =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='CPU_SPD'>" +
        "  <defs>" +
        "    <pattern id='diag' patternUnits='userSpaceOnUse' width='6' height='6' patternTransform='rotate(30)'>" +
        "      <rect width='6' height='6' fill='none'/>" +
        "      <rect x='0' y='0' width='3' height='6' fill='rgba(255,255,255,0.04)'/>" +
        "    </pattern>" +
        "  </defs>" +
        "  <g>" +
        "    <rect x='10' y='18' width='192' height='94' rx='6' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='64' y='38' width='84' height='54' rx='3' fill='url(#diag)' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <g fill='none' stroke='" + AccentToken + "' stroke-width='3' opacity='0.9'>" +
        "      <path d='M72 66 l10 -8 l-10 -8'/>" +
        "      <path d='M92 66 l10 -8 l-10 -8'/>" +
        "      <path d='M112 66 l10 -8 l-10 -8'/>" +
        "    </g>" +
        "  </g>" +
        "</svg>";

    // CPU_EIL — microcode grid overlay (fine squares)
    public static string CPU_EIL =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='CPU_EIL'>" +
        "  <g>" +
        "    <rect x='10' y='18' width='192' height='94' rx='6' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='64' y='38' width='84' height='54' rx='3' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <g stroke='" + AccentToken + "' stroke-width='1' opacity='0.7'>" +
        "      <path d='M70 38 V92 M76 38 V92 M82 38 V92 M88 38 V92 M94 38 V92 M100 38 V92 M106 38 V92 M112 38 V92 M118 38 V92 M124 38 V92 M130 38 V92 M136 38 V92 M142 38 V92'/>" +
        "      <path d='M64 44 H148 M64 50 H148 M64 56 H148 M64 62 H148 M64 68 H148 M64 74 H148 M64 80 H148 M64 86 H148'/>" +
        "    </g>" +
        "  </g>" +
        "</svg>";

    // CPU_LW2 — corner notch and off-center die
    public static string CPU_LW2 =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='CPU_LW2'>" +
        "  <g>" +
        "    <path d='M16 18 H198 a6 6 0 0 1 6 6 V104 H16 Z' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <path d='M10 28 L10 18 H20' fill='none' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "    <rect x='70' y='42' width='74' height='48' rx='3' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "  </g>" +
        "</svg>";

    // PPU_FMC — baseline with tiny tile grid on die
    public static string PPU_FMC =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='PPU_FMC'>" +
        "  <g>" +
        "    <rect x='10' y='18' width='192' height='94' rx='6' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='64' y='38' width='84' height='54' rx='3' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <g stroke='" + Stroke + "' stroke-width='1' opacity='0.6'>" +
        "      <path d='M92 38 V92 M120 38 V92 M64 56 H148 M64 74 H148'/>" +
        "    </g>" +
        "  </g>" +
        "</svg>";

    // PPU_LOW — fewer tiles, subdued
    public static string PPU_LOW =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='PPU_LOW'>" +
        "  <g opacity='0.95'>" +
        "    <rect x='10' y='18' width='192' height='94' rx='6' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='68' y='42' width='76' height='46' rx='3' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <g stroke='" + Stroke + "' stroke-width='1' opacity='0.5'>" +
        "      <path d='M106 42 V88 M68 65 H144'/>" +
        "    </g>" +
        "  </g>" +
        "</svg>";

    // PPU_LQ — scanline tear
    public static string PPU_LQ =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='PPU_LQ'>" +
        "  <g>" +
        "    <rect x='10' y='18' width='192' height='94' rx='6' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='64' y='38' width='84' height='54' rx='3' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <path d='M64 60 H148' stroke='" + Stroke + "' stroke-width='2' stroke-dasharray='3 2' opacity='0.6'/>" +
        "  </g>" +
        "</svg>";

    // PPU_SPD — motion bars left→right
    public static string PPU_SPD =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='PPU_SPD'>" +
        "  <g>" +
        "    <rect x='10' y='18' width='192' height='94' rx='6' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='64' y='38' width='84' height='54' rx='3' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <g stroke='" + AccentToken + "' stroke-width='3'>" +
        "      <line x1='70' y1='50' x2='110' y2='50'/>" +
        "      <line x1='78' y1='62' x2='134' y2='62'/>" +
        "      <line x1='70' y1='74' x2='120' y2='74'/>" +
        "    </g>" +
        "  </g>" +
        "</svg>";

    // PPU_EIL — fine grid and corner highlights
    public static string PPU_EIL =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='PPU_EIL'>" +
        "  <g>" +
        "    <rect x='10' y='18' width='192' height='94' rx='6' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='64' y='38' width='84' height='54' rx='2' fill='" + ChipFillB + "' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "    <g stroke='" + Stroke + "' stroke-width='1' opacity='0.5'>" +
        "      <path d='M70 38 V92 M76 38 V92 M82 38 V92 M88 38 V92 M94 38 V92 M100 38 V92 M106 38 V92 M112 38 V92 M118 38 V92 M124 38 V92 M130 38 V92 M136 38 V92 M142 38 V92'/>" +
        "      <path d='M64 44 H148 M64 50 H148 M64 56 H148 M64 62 H148 M64 68 H148 M64 74 H148 M64 80 H148 M64 86 H148'/>" +
        "    </g>" +
        "  </g>" +
        "</svg>";

    // PPU_BFR — bleed bars from die edges
    public static string PPU_BFR =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='PPU_BFR'>" +
        "  <g>" +
        "    <rect x='10' y='18' width='192' height='94' rx='6' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='64' y='38' width='84' height='54' rx='3' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <g stroke='" + AccentToken + "' stroke-width='2' opacity='0.9'>" +
        "      <line x1='64' y1='48' x2='54' y2='48'/>" +
        "      <line x1='64' y1='66' x2='50' y2='66'/>" +
        "      <line x1='148' y1='58' x2='160' y2='58'/>" +
        "    </g>" +
        "  </g>" +
        "</svg>";

    // PPU_CUBE — checker squares
    public static string PPU_CUBE =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='PPU_CUBE'>" +
        "  <g>" +
        "    <rect x='10' y='18' width='192' height='94' rx='6' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='64' y='38' width='84' height='54' rx='2' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <g fill='" + AccentToken + "' opacity='0.7'>" +
        "      <rect x='70' y='44' width='10' height='10'/>" +
        "      <rect x='90' y='44' width='10' height='10'/>" +
        "      <rect x='110' y='44' width='10' height='10'/>" +
        "      <rect x='80' y='64' width='10' height='10'/>" +
        "      <rect x='100' y='64' width='10' height='10'/>" +
        "      <rect x='120' y='64' width='10' height='10'/>" +
        "    </g>" +
        "  </g>" +
        "</svg>";

    // APU_FMC — baseline with simple sine + square motif
    public static string APU_FMC =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='APU_FMC'>" +
        "  <g>" +
        "    <rect x='10' y='18' width='192' height='94' rx='6' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='64' y='38' width='84' height='54' rx='3' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='2'/>" +
    "    <path d='M70 70 C78 50, 86 90, 94 70 S110 50, 118 70' fill='none' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "    <path d='M128 78 H138 V62 H148' fill='none' stroke='" + Stroke + "' stroke-width='2'/>" +
        "  </g>" +
        "</svg>";

    // APU_EIL — enhanced: fine overlay + accent waveform
    public static string APU_EIL =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='APU_EIL'>" +
        "  <g>" +
        "    <rect x='10' y='18' width='192' height='94' rx='6' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='64' y='38' width='84' height='54' rx='2' fill='" + ChipFillB + "' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "    <g stroke='" + Stroke + "' stroke-width='1' opacity='0.35'>" +
        "      <path d='M70 38 V92 M82 38 V92 M94 38 V92 M106 38 V92 M118 38 V92 M130 38 V92 M142 38 V92'/>" +
        "      <path d='M64 50 H148 M64 62 H148 M64 74 H148 M64 86 H148'/>" +
        "    </g>" +
        "    <path d='M70 70 C78 50, 86 90, 94 70 S110 50, 118 70' fill='none' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  </g>" +
        "</svg>";

    // APU_LOW — thinner waveform, subdued
    public static string APU_LOW =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='APU_LOW'>" +
        "  <g opacity='0.95'>" +
        "    <rect x='10' y='18' width='192' height='94' rx='6' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='64' y='38' width='84' height='54' rx='3' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <path d='M70 72 C78 64, 86 80, 94 72 S110 64, 118 72' fill='none' stroke='" + AccentToken + "' stroke-width='1.5'/>" +
        "  </g>" +
        "</svg>";

    // APU_LQ — jagged/noisy waveform
    public static string APU_LQ =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='APU_LQ'>" +
        "  <g>" +
        "    <rect x='10' y='18' width='192' height='94' rx='6' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='64' y='38' width='84' height='54' rx='3' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <polyline points='70,74 78,62 86,78 94,64 102,80 110,60 118,76' fill='none' stroke='" + Stroke + "' stroke-width='2'/>" +
        "  </g>" +
        "</svg>";

    // APU_LQ2 — doubled noise motif
    public static string APU_LQ2 =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='APU_LQ2'>" +
        "  <g>" +
        "    <rect x='10' y='18' width='192' height='94' rx='6' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='64' y='38' width='84' height='54' rx='3' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <polyline points='70,76 78,64 86,80 94,66 102,82 110,62 118,78' fill='none' stroke='" + Stroke + "' stroke-width='2' opacity='0.8'/>" +
        "    <polyline points='70,68 78,56 86,72 94,58 102,74 110,54 118,70' fill='none' stroke='" + Stroke + "' stroke-width='2' opacity='0.5'/>" +
        "  </g>" +
        "</svg>";

    // APU_QLOW — reduced amplitude
    public static string APU_QLOW =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='APU_QLOW'>" +
        "  <g opacity='0.95'>" +
        "    <rect x='10' y='18' width='192' height='94' rx='6' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='64' y='38' width='84' height='54' rx='3' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <path d='M70 74 C78 68, 86 80, 94 74 S110 68, 118 74' fill='none' stroke='" + AccentToken + "' stroke-width='1.5'/>" +
        "  </g>" +
        "</svg>";

    // APU_QLQ — wobbly waveform
    public static string APU_QLQ =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='APU_QLQ'>" +
        "  <g>" +
        "    <rect x='10' y='18' width='192' height='94' rx='6' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='64' y='38' width='84' height='54' rx='3' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <path d='M70 74 Q78 62, 86 72 T102 76 T118 72' fill='none' stroke='" + Stroke + "' stroke-width='2'/>" +
        "  </g>" +
        "</svg>";

    // APU_QLQ2 — stronger wobble + two-phase offset
    public static string APU_QLQ2 =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='APU_QLQ2'>" +
        "  <g>" +
        "    <rect x='10' y='18' width='192' height='94' rx='6' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='64' y='38' width='84' height='54' rx='3' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <path d='M70 76 Q78 60, 86 76 T102 80 T118 76' fill='none' stroke='" + Stroke + "' stroke-width='2' opacity='0.9'/>" +
        "    <path d='M70 68 Q78 52, 86 68 T102 72 T118 68' fill='none' stroke='" + Stroke + "' stroke-width='2' opacity='0.5'/>" +
        "  </g>" +
        "</svg>";

    // APU_SPD — fast waveform with motion bars
    public static string APU_SPD =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='APU_SPD'>" +
        "  <g>" +
        "    <rect x='10' y='18' width='192' height='94' rx='6' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='64' y='38' width='84' height='54' rx='3' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <path d='M70 70 C78 50, 86 90, 94 70 S110 50, 118 70' fill='none' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "    <g stroke='" + AccentToken + "' stroke-width='2' opacity='0.8'>" +
        "      <line x1='120' y1='60' x2='140' y2='60'/>" +
        "      <line x1='120' y1='72' x2='135' y2='72'/>" +
        "    </g>" +
        "  </g>" +
        "</svg>";

    // APU_SPD2 — stepped segments
    public static string APU_SPD2 =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='APU_SPD2'>" +
        "  <g>" +
        "    <rect x='10' y='18' width='192' height='94' rx='6' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='64' y='38' width='84' height='54' rx='3' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <polyline points='70,70 80,70 80,60 90,60 90,80 100,80 100,64 110,64 110,76 118,76' fill='none' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  </g>" +
        "</svg>";

    // APU_QN — cleaner waveform with wobble ticks
    public static string APU_QN =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='APU_QN'>" +
        "  <g>" +
        "    <rect x='10' y='18' width='192' height='94' rx='6' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='64' y='38' width='84' height='54' rx='3' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <path d='M70 72 C78 62, 86 82, 94 72 S110 62, 118 72' fill='none' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "    <g stroke='" + Stroke + "' stroke-width='1' opacity='0.6'>" +
        "      <line x1='82' y1='58' x2='82' y2='64'/>" +
        "      <line x1='102' y1='58' x2='102' y2='64'/>" +
        "    </g>" +
        "  </g>" +
        "</svg>";

    // APU_MNES — 5-dot arc motif
    public static string APU_MNES =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='APU_MNES'>" +
        "  <g>" +
        "    <rect x='10' y='18' width='192' height='94' rx='6' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='64' y='38' width='84' height='54' rx='3' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <g fill='" + AccentToken + "'>" +
        "      <circle cx='84' cy='72' r='2'/>" +
        "      <circle cx='94' cy='66' r='2'/>" +
        "      <circle cx='106' cy='64' r='2'/>" +
        "      <circle cx='118' cy='66' r='2'/>" +
        "      <circle cx='128' cy='72' r='2'/>" +
        "    </g>" +
        "  </g>" +
        "</svg>";

    // APU_WF — musical note + soundbar
    public static string APU_WF =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='APU_WF'>" +
        "  <g>" +
        "    <rect x='10' y='18' width='192' height='94' rx='6' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='64' y='38' width='84' height='54' rx='3' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <path d='M88 54 v18 a6 6 0 1 1 -3 -5 v-20 h10' fill='none' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "    <g stroke='" + Stroke + "' stroke-width='2'>" +
        "      <line x1='110' y1='72' x2='110' y2='62'/>" +
        "      <line x1='116' y1='72' x2='116' y2='58'/>" +
        "      <line x1='122' y1='72' x2='122' y2='66'/>" +
        "    </g>" +
        "  </g>" +
        "</svg>";

    // CLOCK_CLR — inner gear ring and braces
    public static string CLOCK_CLR =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='CLOCK_CLR'>" +
        "  <g>" +
        "    <rect x='10' y='18' width='192' height='94' rx='6' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <circle cx='106' cy='65' r='22' fill='" + ChipFillB + "' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "    <g stroke='" + Stroke + "' stroke-width='2'>" +
        "      <path d='M106 49 l4 6 M106 49 l-4 6 M106 81 l4 -6 M106 81 l-4 -6 M90 65 l6 -4 M90 65 l6 4 M122 65 l-6 -4 M122 65 l-6 4'/>" +
        "    </g>" +
        "    <g stroke='" + AccentToken + "' stroke-width='2'>" +
        "      <path d='M84 46 h10 M128 84 h10 M84 84 h10 M128 46 h10'/>" +
        "    </g>" +
        "  </g>" +
        "</svg>";

    // CLOCK_TRB — turbo bolt/chevron overlay
    public static string CLOCK_TRB =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='CLOCK_TRB'>" +
        "  <g>" +
        "    <rect x='10' y='18' width='192' height='94' rx='6' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <circle cx='106' cy='65' r='24' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <path d='M98 50 l16 0 l-8 14 l14 0 l-24 28 l8 -20 l-12 0 z' fill='" + AccentToken + "' opacity='0.9'/>" +
        "  </g>" +
        "</svg>";

    // SHADER_DEFAULT — generic shader chip motif (pixel grid)
    public static string SHADER_DEFAULT =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='SHADER_DEFAULT'>" +
        "  <g>" +
        "    <rect x='8' y='16' width='196' height='98' rx='10' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='18' y='26' width='176' height='78' rx='6' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='1'/>" +
        "    <g stroke='" + AccentToken + "' stroke-width='1' opacity='0.8'>" +
        "      <path d='M30 50 H198 M30 66 H198 M30 82 H198'/>" +
        "      <path d='M62 26 V104 M86 26 V104 M110 26 V104 M134 26 V104 M158 26 V104'/>" +
        "    </g>" +
        "  </g>" +
        "</svg>";

    // SHADER_TV — CRT tube: scanlines, triads, subtle barrel
    public static string SHADER_TV =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='SHADER_TV'>" +
        "  <g>" +
        "    <rect x='8' y='16' width='196' height='98' rx='10' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='18' y='26' width='176' height='78' rx='6' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='1'/>" +
        "    <g stroke='" + Stroke + "' stroke-width='1' opacity='0.4'>" +
        "      <path d='M18 26 Q106 14 194 26'/>" +
        "      <path d='M18 104 Q106 116 194 104'/>" +
        "    </g>" +
        "    <g stroke='" + Stroke + "' stroke-width='1' opacity='0.25'>" +
        "      <path d='M26 38 H186 M26 46 H186 M26 54 H186 M26 62 H186 M26 70 H186 M26 78 H186 M26 86 H186'/>" +
        "    </g>" +
        "    <g opacity='0.7'>" +
        "      <rect x='58' y='26' width='4' height='78' fill='" + AccentToken + "' opacity='0.6'/>" +
        "      <rect x='62' y='26' width='4' height='78' fill='" + Stroke + "' opacity='0.25'/>" +
        "      <rect x='66' y='26' width='4' height='78' fill='" + Stroke + "' opacity='0.25'/>" +
        "    </g>" +
        "  </g>" +
        "</svg>";

    // SHADER_VHS — flagging top, head-switch bar bottom, dropout dashes
    public static string SHADER_VHS =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='SHADER_VHS'>" +
        "  <g>" +
        "    <rect x='8' y='16' width='196' height='98' rx='10' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='18' y='26' width='176' height='78' rx='6' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='1'/>" +
        "    <path d='M18 30 L62 26 L106 30 L150 26 L194 30' fill='none' stroke='" + AccentToken + "' stroke-width='2' opacity='0.8'/>" +
        "    <rect x='18' y='96' width='176' height='4' fill='" + Stroke + "' opacity='0.25'/>" +
        "    <g stroke='" + Stroke + "' stroke-width='2' opacity='0.5'>" +
        "      <path d='M26 48 Q50 52 74 48 T122 48 T170 48'/>" +
        "    </g>" +
        "    <g stroke='" + Stroke + "' stroke-width='2' stroke-dasharray='4 4' opacity='0.45'>" +
        "      <line x1='40' y1='70' x2='58' y2='70'/>" +
        "      <line x1='122' y1='64' x2='146' y2='64'/>" +
        "    </g>" +
        "  </g>" +
        "</svg>";

    // SHADER_LCD — smear strokes, ghost, vertical banding
    public static string SHADER_LCD =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='SHADER_LCD'>" +
        "  <g>" +
        "    <rect x='8' y='16' width='196' height='98' rx='10' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='18' y='26' width='176' height='78' rx='6' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='1'/>" +
        "    <g stroke='" + Stroke + "' stroke-width='2' opacity='0.45'>" +
        "      <line x1='34' y1='44' x2='102' y2='44'/>" +
        "      <line x1='34' y1='58' x2='120' y2='58'/>" +
        "      <line x1='34' y1='72' x2='110' y2='72'/>" +
        "    </g>" +
        "    <rect x='104' y='44' width='38' height='26' rx='3' fill='none' stroke='" + Stroke + "' stroke-width='1' opacity='0.35'/>" +
        "    <g stroke='" + Stroke + "' stroke-width='1' opacity='0.18'>" +
        "      <path d='M46 26 V104 M78 26 V104 M110 26 V104 M142 26 V104 M174 26 V104'/>" +
        "    </g>" +
        "  </g>" +
        "</svg>";

    // SHADER_RGBX — three vector splits (abstract arrows)
    public static string SHADER_RGBX =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='SHADER_RGBX'>" +
        "  <g>" +
        "    <rect x='8' y='16' width='196' height='98' rx='10' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='18' y='26' width='176' height='78' rx='6' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='1'/>" +
        "    <g fill='none' stroke='" + Stroke + "' stroke-width='2' opacity='0.7'>" +
        "      <path d='M106 65 l18 -8 l-4 6'/>" +
        "      <path d='M106 65 l-10 18 l-4 -6'/>" +
        "      <path d='M106 65 l-18 -10 l6 -4'/>" +
        "    </g>" +
        "    <circle cx='106' cy='65' r='4' fill='" + AccentToken + "' opacity='0.9'/>" +
        "  </g>" +
        "</svg>";

    // SHADER_PX — plain screen with pixel grid outline
    public static string SHADER_PX =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='SHADER_PX'>" +
        "  <g>" +
        "    <rect x='8' y='16' width='196' height='98' rx='10' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='18' y='26' width='176' height='78' rx='6' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='1'/>" +
        "    <rect x='62' y='42' width='88' height='46' rx='2' fill='none' stroke='" + Stroke + "' stroke-width='1' opacity='0.6'/>" +
        "    <g stroke='" + Stroke + "' stroke-width='1' opacity='0.35'>" +
        "      <path d='M84 42 V88 M106 42 V88 M128 42 V88'/>" +
        "      <path d='M62 56 H150 M62 74 H150'/>" +
        "    </g>" +
        "  </g>" +
        "</svg>";

    // SHADER_EXE — vertical beam, swirl arrows, glitch slices
    public static string SHADER_EXE =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='SHADER_EXE'>" +
        "  <g>" +
        "    <rect x='8' y='16' width='196' height='98' rx='10' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='18' y='26' width='176' height='78' rx='6' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='1'/>" +
        "    <rect x='102' y='26' width='4' height='78' fill='" + AccentToken + "' opacity='0.8'/>" +
        "    <g fill='none' stroke='" + Stroke + "' stroke-width='2' opacity='0.6'>" +
        "      <path d='M86 60 q6 -6 12 0'/>" +
        "      <path d='M118 70 q-6 6 -12 0'/>" +
        "    </g>" +
        "    <g stroke='" + Stroke + "' stroke-width='2' stroke-dasharray='6 6' opacity='0.45'>" +
        "      <line x1='40' y1='48' x2='78' y2='48'/>" +
        "      <line x1='134' y1='80' x2='172' y2='80'/>" +
        "    </g>" +
        "  </g>" +
        "</svg>";

    // SHADER_16B — gentle bands & scanlines (16-bit feel)
    public static string SHADER_16B =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='SHADER_16B'>" +
        "  <g>" +
        "    <rect x='8' y='16' width='196' height='98' rx='10' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='18' y='26' width='176' height='78' rx='6' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='1'/>" +
        "    <g stroke='" + AccentToken + "' stroke-width='6' opacity='0.18'>" +
        "      <line x1='30' y1='44' x2='182' y2='44'/>" +
        "      <line x1='30' y1='68' x2='182' y2='68'/>" +
        "      <line x1='30' y1='90' x2='182' y2='90'/>" +
        "    </g>" +
        "    <g stroke='" + Stroke + "' stroke-width='1' opacity='0.22'>" +
        "      <path d='M26 38 H186 M26 46 H186 M26 54 H186 M26 62 H186 M26 70 H186 M26 78 H186 M26 86 H186'/>" +
        "    </g>" +
        "  </g>" +
        "</svg>";

    // SHADER_BLD — color bleed bars from center block
    public static string SHADER_BLD =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='SHADER_BLD'>" +
        "  <g>" +
        "    <rect x='8' y='16' width='196' height='98' rx='10' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='18' y='26' width='176' height='78' rx='6' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='1'/>" +
        "    <rect x='96' y='56' width='20' height='20' rx='3' fill='none' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <g stroke='" + AccentToken + "' stroke-width='3' opacity='0.85'>" +
        "      <line x1='106' y1='56' x2='106' y2='38'/>" +
        "      <line x1='106' y1='76' x2='106' y2='94'/>" +
        "      <line x1='96' y1='66' x2='70' y2='66'/>" +
        "      <line x1='116' y1='66' x2='142' y2='66'/>" +
        "    </g>" +
        "  </g>" +
        "</svg>";

    // SHADER_BUMP — sun-dot and relief ramp
    public static string SHADER_BUMP =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='SHADER_BUMP'>" +
        "  <g>" +
        "    <rect x='8' y='16' width='196' height='98' rx='10' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='18' y='26' width='176' height='78' rx='6' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='1'/>" +
        "    <circle cx='56' cy='44' r='5' fill='" + AccentToken + "'/>" +
        "    <path d='M70 86 L146 50 L146 86 Z' fill='none' stroke='" + Stroke + "' stroke-width='2' opacity='0.8'/>" +
        "    <path d='M70 86 Q108 64 146 50' fill='none' stroke='" + AccentToken + "' stroke-width='2' opacity='0.7'/>" +
        "  </g>" +
        "</svg>";

    // SHADER_CCC — hue ring arcs
    public static string SHADER_CCC =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='SHADER_CCC'>" +
        "  <g>" +
        "    <rect x='8' y='16' width='196' height='98' rx='10' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='18' y='26' width='176' height='78' rx='6' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='1'/>" +
        "    <circle cx='106' cy='65' r='24' fill='none' stroke='" + Stroke + "' stroke-width='2' opacity='0.5'/>" +
        "    <path d='M82 65 a24 24 0 0 1 48 0' fill='none' stroke='" + AccentToken + "' stroke-width='3'/>" +
        "    <path d='M88 65 a18 18 0 0 0 36 0' fill='none' stroke='" + Stroke + "' stroke-width='2' opacity='0.5'/>" +
        "  </g>" +
        "</svg>";

    // SHADER_CNMA — teal/orange split, halo ring
    public static string SHADER_CNMA =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='SHADER_CNMA'>" +
        "  <g>" +
        "    <rect x='8' y='16' width='196' height='98' rx='10' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='18' y='26' width='176' height='78' rx='6' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='1'/>" +
        "    <path d='M30 34 L182 96' stroke='" + AccentToken + "' stroke-width='3' opacity='0.7'/>" +
        "    <circle cx='106' cy='65' r='18' fill='none' stroke='" + Stroke + "' stroke-width='2' opacity='0.5'/>" +
        "    <rect x='92' y='56' width='28' height='18' rx='2' fill='none' stroke='" + Stroke + "' stroke-width='2' opacity='0.6'/>" +
        "  </g>" +
        "</svg>";

    // SHADER_CRY — irregular shards with accent edge
    public static string SHADER_CRY =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='SHADER_CRY'>" +
        "  <g>" +
        "    <rect x='8' y='16' width='196' height='98' rx='10' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='18' y='26' width='176' height='78' rx='6' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='1'/>" +
        "    <path d='M44 54 L74 40 L90 56 L62 74 Z' fill='none' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <path d='M94 44 L124 52 L114 78 L86 70 Z' fill='none' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <path d='M124 64 L154 48 L170 76 L142 86 Z' fill='none' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <line x1='114' y1='52' x2='124' y2='64' stroke='" + AccentToken + "' stroke-width='3'/>" +
        "  </g>" +
        "</svg>";

    // SHADER_CRZ — shards + glint
    public static string SHADER_CRZ =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='SHADER_CRZ'>" +
        "  <g>" +
        "    <rect x='8' y='16' width='196' height='98' rx='10' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='18' y='26' width='176' height='78' rx='6' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='1'/>" +
        "    <path d='M48 58 L78 42 L92 70 L66 82 Z' fill='none' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <path d='M100 50 L130 62 L118 86 L94 76 Z' fill='none' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <path d='M136 60 L164 44 L176 74 L150 88 Z' fill='none' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <g stroke='" + AccentToken + "' stroke-width='2'>" +
        "      <line x1='124' y1='58' x2='120' y2='64'/>" +
        "      <line x1='120' y1='58' x2='124' y2='64'/>" +
        "    </g>" +
        "  </g>" +
        "</svg>";

    // SHADER_DOT — overlapping circles with boundary arcs
    public static string SHADER_DOT =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='SHADER_DOT'>" +
        "  <g>" +
        "    <rect x='8' y='16' width='196' height='98' rx='10' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='18' y='26' width='176' height='78' rx='6' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='1'/>" +
        "    <g fill='none' stroke='" + Stroke + "' stroke-width='2' opacity='0.8'>" +
        "      <circle cx='80' cy='60' r='22'/>" +
        "      <circle cx='110' cy='68' r='24'/>" +
        "      <circle cx='140' cy='58' r='20'/>" +
        "    </g>" +
        "    <path d='M96 48 q8 8 0 16' fill='none' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  </g>" +
        "</svg>";

    // SHADER_HUE — hue wheel arc + double arrow
    public static string SHADER_HUE =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='SHADER_HUE'>" +
        "  <g>" +
        "    <rect x='8' y='16' width='196' height='98' rx='10' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='18' y='26' width='176' height='78' rx='6' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='1'/>" +
        "    <circle cx='106' cy='65' r='20' fill='none' stroke='" + Stroke + "' stroke-width='2' opacity='0.5'/>" +
        "    <path d='M86 65 a20 20 0 0 1 40 0' fill='none' stroke='" + AccentToken + "' stroke-width='3'/>" +
        "    <path d='M96 60 l-8 5 l8 5' fill='none' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <path d='M116 60 l8 5 l-8 5' fill='none' stroke='" + Stroke + "' stroke-width='2'/>" +
        "  </g>" +
        "</svg>";

    // SHADER_LAT — diamond lattice + ghost offsets
    public static string SHADER_LAT =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='SHADER_LAT'>" +
        "  <g>" +
        "    <rect x='8' y='16' width='196' height='98' rx='10' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='18' y='26' width='176' height='78' rx='6' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='1'/>" +
        "    <g stroke='" + Stroke + "' stroke-width='1' opacity='0.55'>" +
        "      <path d='M46 44 L66 64 L46 84 M66 44 L86 64 L66 84 M86 44 L106 64 L86 84'/>" +
        "      <path d='M106 44 L126 64 L106 84 M126 44 L146 64 L126 84 M146 44 L166 64 L146 84'/>" +
        "    </g>" +
        "    <rect x='114' y='48' width='26' height='18' rx='2' fill='none' stroke='" + AccentToken + "' stroke-width='2' opacity='0.7'/>" +
        "    <rect x='118' y='52' width='26' height='18' rx='2' fill='none' stroke='" + Stroke + "' stroke-width='1' opacity='0.35'/>" +
        "  </g>" +
        "</svg>";

    // SHADER_LSD — spiral + offset strokes
    public static string SHADER_LSD =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='SHADER_LSD'>" +
        "  <g>" +
        "    <rect x='8' y='16' width='196' height='98' rx='10' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='18' y='26' width='176' height='78' rx='6' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='1'/>" +
        "    <path d='M106 65 m-16 0 a16 16 0 1 1 32 0 a12 12 0 1 1 -24 0 a8 8 0 1 1 16 0' fill='none' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "    <path d='M70 52 q12 6 24 0' fill='none' stroke='" + Stroke + "' stroke-width='2' opacity='0.5'/>" +
        "    <path d='M138 76 q-12 -6 -24 0' fill='none' stroke='" + Stroke + "' stroke-width='2' opacity='0.5'/>" +
        "  </g>" +
        "</svg>";

    // SHADER_MSH — block grid w/ misaligned blocks
    public static string SHADER_MSH =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='SHADER_MSH'>" +
        "  <g>" +
        "    <rect x='8' y='16' width='196' height='98' rx='10' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='18' y='26' width='176' height='78' rx='6' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='1'/>" +
        "    <g stroke='" + Stroke + "' stroke-width='1' opacity='0.6'>" +
        "      <path d='M58 44 H154 M58 64 H154 M58 84 H154'/>" +
        "      <path d='M78 36 V96 M98 36 V96 M118 36 V96 M138 36 V96'/>" +
        "    </g>" +
        "    <rect x='98' y='64' width='20' height='20' fill='none' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "    <rect x='78' y='44' width='20' height='20' fill='none' stroke='" + Stroke + "' stroke-width='2' transform='translate(4,4)' opacity='0.7'/>" +
        "  </g>" +
        "</svg>";

    // SHADER_RF — ripple sine and shimmer lines
    public static string SHADER_RF =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='SHADER_RF'>" +
        "  <g>" +
        "    <rect x='8' y='16' width='196' height='98' rx='10' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='18' y='26' width='176' height='78' rx='6' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='1'/>" +
        "    <path d='M30 66 q18 -6 36 0 t36 0 t36 0 t36 0' fill='none' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "    <g stroke='" + Stroke + "' stroke-width='2' opacity='0.35'>" +
        "      <line x1='58' y1='26' x2='58' y2='104'/>" +
        "      <line x1='146' y1='26' x2='146' y2='104'/>" +
        "    </g>" +
        "    <circle cx='126' cy='54' r='2' fill='" + Stroke + "' opacity='0.4'/>" +
        "    <circle cx='74' cy='78' r='2' fill='" + Stroke + "' opacity='0.4'/>" +
        "  </g>" +
        "</svg>";

    // SHADER_SPK — prism rays + star sparkles
    public static string SHADER_SPK =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='SHADER_SPK'>" +
        "  <g>" +
        "    <rect x='8' y='16' width='196' height='98' rx='10' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='18' y='26' width='176' height='78' rx='6' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='1'/>" +
        "    <g stroke='" + AccentToken + "' stroke-width='2' opacity='0.85'>" +
        "      <line x1='106' y1='65' x2='142' y2='54'/>" +
        "      <line x1='106' y1='65' x2='150' y2='74'/>" +
        "      <line x1='106' y1='65' x2='126' y2='92'/>" +
        "    </g>" +
        "    <g stroke='" + Stroke + "' stroke-width='2'>" +
        "      <line x1='76' y1='54' x2='80' y2='58'/>" +
        "      <line x1='80' y1='54' x2='76' y2='58'/>" +
        "      <line x1='88' y1='80' x2='92' y2='84'/>" +
        "      <line x1='92' y1='80' x2='88' y2='84'/>" +
        "    </g>" +
        "  </g>" +
        "</svg>";

    // SHADER_TRI — raised rectangle with rim/shadow
    public static string SHADER_TRI =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='SHADER_TRI'>" +
        "  <g>" +
        "    <rect x='8' y='16' width='196' height='98' rx='10' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='18' y='26' width='176' height='78' rx='6' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='1'/>" +
        "    <rect x='82' y='48' width='64' height='34' rx='2' fill='none' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "    <rect x='86' y='52' width='64' height='34' rx='2' fill='none' stroke='" + Stroke + "' stroke-width='2' opacity='0.45'/>" +
        "    <line x1='82' y1='82' x2='120' y2='90' stroke='" + Stroke + "' stroke-width='3' opacity='0.35'/>" +
        "  </g>" +
        "</svg>";

    // SHADER_TTF — subpixel columns and sharp bar
    public static string SHADER_TTF =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='SHADER_TTF'>" +
        "  <g>" +
        "    <rect x='8' y='16' width='196' height='98' rx='10' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='18' y='26' width='176' height='78' rx='6' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='1'/>" +
        "    <g opacity='0.9'>" +
        "      <rect x='86' y='38' width='3' height='54' fill='" + AccentToken + "'/>" +
        "      <rect x='90' y='38' width='3' height='54' fill='" + Stroke + "' opacity='0.5'/>" +
        "      <rect x='94' y='38' width='3' height='54' fill='" + Stroke + "' opacity='0.5'/>" +
        "    </g>" +
        "    <rect x='104' y='38' width='2' height='54' fill='" + Stroke + "'/>" +
        "  </g>" +
        "</svg>";

    // SHADER_WARM — warm wedge + green cross-talk bar
    public static string SHADER_WARM =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='SHADER_WARM'>" +
        "  <g>" +
        "    <rect x='8' y='16' width='196' height='98' rx='10' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='18' y='26' width='176' height='78' rx='6' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='1'/>" +
        "    <path d='M18 104 L66 26 L18 26 Z' fill='" + AccentToken + "' opacity='0.18'/>" +
        "    <rect x='140' y='38' width='6' height='54' fill='" + Stroke + "' opacity='0.4'/>" +
        "  </g>" +
        "</svg>";

    // SHADER_WTR — crossing waves + lens rings + small arrows
    public static string SHADER_WTR =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='SHADER_WTR'>" +
        "  <g>" +
        "    <rect x='8' y='16' width='196' height='98' rx='10' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='18' y='26' width='176' height='78' rx='6' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='1'/>" +
        "    <path d='M30 58 q16 10 32 0 t32 0 t32 0 t32 0' fill='none' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "    <path d='M30 74 q16 -10 32 0 t32 0 t32 0 t32 0' fill='none' stroke='" + Stroke + "' stroke-width='2' opacity='0.6'/>" +
        "    <circle cx='106' cy='65' r='10' fill='none' stroke='" + Stroke + "' stroke-width='2' opacity='0.5'/>" +
        "    <circle cx='106' cy='65' r='18' fill='none' stroke='" + Stroke + "' stroke-width='1' opacity='0.35'/>" +
        "    <path d='M86 64 l-8 0 l4 -4' fill='none' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <path d='M126 66 l8 0 l-4 4' fill='none' stroke='" + Stroke + "' stroke-width='2'/>" +
        "  </g>" +
        "</svg>";

    // CLOCK_FMC — tick ring
    public static string CLOCK_FMC =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='CLOCK_FMC'>" +
        "  <g>" +
        "    <rect x='10' y='18' width='192' height='94' rx='6' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <circle cx='106' cy='65' r='24' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <g stroke='" + Stroke + "' stroke-width='2'>" +
        "      <line x1='106' y1='41' x2='106' y2='47'/>" +
        "      <line x1='106' y1='83' x2='106' y2='89'/>" +
        "      <line x1='82' y1='65' x2='88' y2='65'/>" +
        "      <line x1='124' y1='65' x2='130' y2='65'/>" +
        "    </g>" +
        "    <line x1='106' y1='65' x2='122' y2='55' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  </g>" +
        "</svg>";

    /// <summary>
    /// Replace the {ACCENT} placeholder with a concrete color (e.g., based on rating).
    /// </summary>
    public static string ApplyAccent(string svg, string color)
        => string.IsNullOrEmpty(svg) ? svg : svg.Replace(AccentToken, color, StringComparison.OrdinalIgnoreCase);
}
