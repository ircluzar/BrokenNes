using System;
using System.Reflection;

namespace BrokenNes.Windows;

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
        var key = NormalizeKeySegment(prefix) + "_" + NormalizeKeySegment(id);
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

    private static string NormalizeKeySegment(string value)
    {
        var chars = value.Trim();
        if (chars.Length == 0)
        {
            return string.Empty;
        }

        var result = new System.Text.StringBuilder(chars.Length);
        var lastWasUnderscore = false;

        foreach (var ch in chars)
        {
            if (char.IsLetterOrDigit(ch))
            {
                result.Append(char.ToUpperInvariant(ch));
                lastWasUnderscore = false;
                continue;
            }

            if (!lastWasUnderscore)
            {
                result.Append('_');
                lastWasUnderscore = true;
            }
        }

        return result.ToString().Trim('_');
    }

    public static string BACKGROUND_GRADIENT_DEFAULT =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='BACKGROUND_GRADIENT_DEFAULT'>" +
        "  <defs>" +
        "    <pattern id='bg-grad-dither' width='8' height='8' patternUnits='userSpaceOnUse'>" +
        "      <rect width='8' height='8' fill='none'/>" +
        "      <rect x='1' y='1' width='1' height='1' fill='rgba(255,255,255,0.12)'/>" +
        "      <rect x='5' y='2' width='1' height='1' fill='rgba(255,255,255,0.08)'/>" +
        "      <rect x='2' y='5' width='1' height='1' fill='rgba(255,255,255,0.08)'/>" +
        "      <rect x='6' y='6' width='1' height='1' fill='rgba(255,255,255,0.12)'/>" +
        "    </pattern>" +
        "    <linearGradient id='bg-grad-main' x1='0%' y1='0%' x2='100%' y2='0%'>" +
        "      <stop offset='0%' stop-color='#343a40'/>" +
        "      <stop offset='50%' stop-color='#06080d'/>" +
        "      <stop offset='100%' stop-color='#343a40'/>" +
        "    </linearGradient>" +
        "  </defs>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='url(#bg-grad-main)' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <rect x='18' y='28' width='176' height='74' rx='6' fill='url(#bg-grad-dither)' opacity='0.9'/>" +
        "  <path d='M106 24 v82' stroke='rgba(255,255,255,0.08)' stroke-width='2' stroke-dasharray='2 6'/>" +
        "</svg>";

    public static string BACKGROUND_NONE_BLACK =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='BACKGROUND_NONE_BLACK'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#020202' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <rect x='22' y='30' width='168' height='70' rx='4' fill='none' stroke='rgba(255,255,255,0.08)' stroke-width='1.5'/>" +
        "  <path d='M34 82 H178' stroke='rgba(255,255,255,0.12)' stroke-width='2'/>" +
        "</svg>";

    public static string BACKGROUND_ANIMATEDWAVE =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='BACKGROUND_ANIMATEDWAVE'>" +
        "  <defs>" +
        "    <linearGradient id='bg-wave-fill' x1='0%' y1='0%' x2='100%' y2='100%'>" +
        "      <stop offset='0%' stop-color='#07121f'/>" +
        "      <stop offset='100%' stop-color='#0d2538'/>" +
        "    </linearGradient>" +
        "  </defs>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='url(#bg-wave-fill)' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <path d='M16 72 C36 52, 56 92, 76 72 S116 52, 136 72 S176 92, 196 68' fill='none' stroke='" + AccentToken + "' stroke-width='4' stroke-linecap='round'/>" +
        "  <path d='M16 88 C40 68, 60 108, 84 88 S128 68, 152 88 S176 104, 196 84' fill='none' stroke='rgba(255,255,255,0.35)' stroke-width='2.5' stroke-linecap='round'/>" +
        "  <circle cx='64' cy='54' r='7' fill='rgba(255,255,255,0.14)'/>" +
        "  <circle cx='128' cy='46' r='5' fill='rgba(255,255,255,0.10)'/>" +
        "</svg>";

    public static string BACKGROUND_ANIMATEDBUBBLE =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='BACKGROUND_ANIMATEDBUBBLE'>" +
        "  <defs>" +
        "    <linearGradient id='bg-bubble-fill' x1='0%' y1='0%' x2='0%' y2='100%'>" +
        "      <stop offset='0%' stop-color='#08141c'/>" +
        "      <stop offset='100%' stop-color='#123042'/>" +
        "    </linearGradient>" +
        "  </defs>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='url(#bg-bubble-fill)' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <g fill='rgba(255,255,255,0.08)' stroke-linecap='round'>" +
        "    <circle cx='58' cy='78' r='16' stroke='rgba(255,255,255,0.22)' stroke-width='2'/><circle cx='58' cy='78' r='6' fill='rgba(255,255,255,0.10)'/>" +
        "    <circle cx='98' cy='58' r='12' stroke='" + AccentToken + "' stroke-width='2.5' opacity='0.82'/><circle cx='98' cy='58' r='4' fill='rgba(255,255,255,0.12)'/>" +
        "    <circle cx='142' cy='72' r='20' stroke='rgba(255,255,255,0.18)' stroke-width='2'/><circle cx='142' cy='72' r='8' fill='rgba(255,255,255,0.08)'/>" +
        "  </g>" +
        "  <path d='M38 98 C52 84, 64 68, 70 50 M88 92 C96 78, 104 64, 112 44 M142 94 C150 82, 160 66, 168 48' fill='none' stroke='rgba(255,255,255,0.12)' stroke-width='2'/>" +
        "</svg>";

    public static string BACKGROUND_BELOUSOVZHABOTINSKY =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='BACKGROUND_BELOUSOVZHABOTINSKY'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#171023' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <g fill='none' stroke-linecap='round'>" +
        "    <path d='M30 84 C46 48, 86 34, 122 48 C152 60, 166 86, 186 84' stroke='" + AccentToken + "' stroke-width='4' opacity='0.84'/>" +
        "    <path d='M26 92 C46 60, 82 48, 116 58 C144 66, 164 90, 190 92' stroke='rgba(255,255,255,0.20)' stroke-width='3'/>" +
        "    <path d='M46 34 C72 26, 106 32, 134 48 C154 60, 166 74, 178 98' stroke='rgba(180,255,218,0.18)' stroke-width='2.5'/>" +
        "    <circle cx='84' cy='62' r='10' stroke='rgba(255,255,255,0.14)' stroke-width='2'/><circle cx='126' cy='72' r='8' stroke='rgba(255,255,255,0.12)' stroke-width='2'/>" +
        "  </g>" +
        "</svg>";

    public static string BACKGROUND_BREATHINGGRADIENTS =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='BACKGROUND_BREATHINGGRADIENTS'>" +
        "  <defs>" +
        "    <radialGradient id='bg-breathe-core' cx='50%' cy='50%' r='60%'>" +
        "      <stop offset='0%' stop-color='rgba(255,255,255,0.18)'/>" +
        "      <stop offset='100%' stop-color='rgba(255,255,255,0)'/>" +
        "    </radialGradient>" +
        "    <linearGradient id='bg-breathe-wash' x1='0%' y1='0%' x2='100%' y2='100%'>" +
        "      <stop offset='0%' stop-color='#211339'/>" +
        "      <stop offset='50%' stop-color='#0f2038'/>" +
        "      <stop offset='100%' stop-color='#173127'/>" +
        "    </linearGradient>" +
        "  </defs>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='url(#bg-breathe-wash)' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <circle cx='104' cy='66' r='34' fill='url(#bg-breathe-core)'/>" +
        "  <circle cx='122' cy='54' r='18' fill='none' stroke='rgba(255,255,255,0.18)' stroke-width='2'/>" +
        "  <path d='M28 82 C54 64, 82 96, 110 76 S158 58, 186 72' fill='none' stroke='rgba(255,255,255,0.16)' stroke-width='2.5' stroke-linecap='round'/>" +
        "</svg>";

    public static string BACKGROUND_CALMWATERREFLECTION =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='BACKGROUND_CALMWATERREFLECTION'>" +
        "  <defs>" +
        "    <linearGradient id='bg-calm-sky' x1='0%' y1='0%' x2='0%' y2='100%'>" +
        "      <stop offset='0%' stop-color='#294560'/>" +
        "      <stop offset='54%' stop-color='#7aa6c0'/>" +
        "      <stop offset='55%' stop-color='#153041'/>" +
        "      <stop offset='100%' stop-color='#0d1e29'/>" +
        "    </linearGradient>" +
        "  </defs>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='url(#bg-calm-sky)' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <path d='M24 66 H188' stroke='rgba(255,255,255,0.28)' stroke-width='2'/>" +
        "  <path d='M46 84 C72 80, 94 88, 118 84 S164 78, 184 82' fill='none' stroke='rgba(255,255,255,0.18)' stroke-width='2.5' stroke-linecap='round'/>" +
        "  <path d='M54 94 C74 92, 94 98, 116 94 S156 88, 176 92' fill='none' stroke='" + AccentToken + "' stroke-width='2.8' stroke-linecap='round' opacity='0.74'/>" +
        "  <circle cx='72' cy='46' r='6' fill='rgba(255,255,255,0.16)'/>" +
        "</svg>";

    public static string BACKGROUND_CLIFFORDATTRACTOR =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='BACKGROUND_CLIFFORDATTRACTOR'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#10161f' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <g fill='rgba(255,255,255,0.16)'>" +
        "    <circle cx='70' cy='52' r='1.6'/><circle cx='78' cy='60' r='1.7'/><circle cx='88' cy='68' r='1.5'/><circle cx='100' cy='74' r='1.6'/><circle cx='114' cy='74' r='1.5'/>" +
        "    <circle cx='128' cy='68' r='1.6'/><circle cx='138' cy='58' r='1.7'/><circle cx='146' cy='50' r='1.5'/><circle cx='122' cy='48' r='1.4'/><circle cx='92' cy='46' r='1.4'/>" +
        "  </g>" +
        "  <path d='M62 74 C70 42, 108 34, 130 50 C146 62, 142 82, 122 88 C96 96, 70 92, 62 74 Z' fill='none' stroke='" + AccentToken + "' stroke-width='3.4' opacity='0.84'/>" +
        "  <path d='M74 70 C82 48, 106 44, 122 56 C134 66, 130 80, 116 84 C96 90, 80 86, 74 70 Z' fill='none' stroke='rgba(255,255,255,0.18)' stroke-width='2.2'/>" +
        "</svg>";

    public static string BACKGROUND_COMPLEXDOMAINCOLORING =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='BACKGROUND_COMPLEXDOMAINCOLORING'>" +
        "  <defs>" +
        "    <linearGradient id='bg-complex-spectrum' x1='0%' y1='0%' x2='100%' y2='100%'>" +
        "      <stop offset='0%' stop-color='#1f4f87'/>" +
        "      <stop offset='30%' stop-color='#2a9d8f'/>" +
        "      <stop offset='60%' stop-color='#e9c46a'/>" +
        "      <stop offset='100%' stop-color='#c44536'/>" +
        "    </linearGradient>" +
        "  </defs>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#11151d' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <rect x='30' y='34' width='152' height='62' rx='6' fill='url(#bg-complex-spectrum)' opacity='0.85'/>" +
        "  <path d='M106 34 V96 M30 65 H182' stroke='rgba(255,255,255,0.22)' stroke-width='2'/>" +
        "  <path d='M52 46 C72 70, 92 60, 106 65 C122 70, 144 58, 160 82' fill='none' stroke='" + AccentToken + "' stroke-width='3.2' opacity='0.84'/>" +
        "  <circle cx='106' cy='65' r='18' fill='none' stroke='rgba(255,255,255,0.16)' stroke-width='2'/>" +
        "</svg>";

    public static string BACKGROUND_DEJONGATTRACTOR =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='BACKGROUND_DEJONGATTRACTOR'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#15131f' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <g fill='rgba(255,255,255,0.14)'>" +
        "    <circle cx='64' cy='70' r='1.7'/><circle cx='74' cy='56' r='1.5'/><circle cx='86' cy='46' r='1.6'/><circle cx='100' cy='42' r='1.5'/><circle cx='116' cy='44' r='1.6'/>" +
        "    <circle cx='130' cy='50' r='1.7'/><circle cx='142' cy='62' r='1.6'/><circle cx='148' cy='76' r='1.5'/><circle cx='120' cy='80' r='1.5'/><circle cx='90' cy='80' r='1.5'/>" +
        "  </g>" +
        "  <path d='M58 74 C74 44, 112 28, 142 46 C160 56, 158 80, 136 88 C108 98, 74 94, 58 74 Z' fill='none' stroke='" + AccentToken + "' stroke-width='3.4' opacity='0.84'/>" +
        "  <path d='M72 72 C84 50, 110 40, 130 52 C144 60, 142 78, 124 84 C102 90, 82 88, 72 72 Z' fill='none' stroke='rgba(255,255,255,0.18)' stroke-width='2.2'/>" +
        "</svg>";

    public static string BACKGROUND_DRIFTINGCLOUDS =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='BACKGROUND_DRIFTINGCLOUDS'>" +
        "  <defs>" +
        "    <linearGradient id='bg-cloud-sky' x1='0%' y1='0%' x2='0%' y2='100%'>" +
        "      <stop offset='0%' stop-color='#2a4360'/>" +
        "      <stop offset='100%' stop-color='#677f95'/>" +
        "    </linearGradient>" +
        "  </defs>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='url(#bg-cloud-sky)' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <g fill='rgba(255,255,255,0.22)'>" +
        "    <ellipse cx='66' cy='58' rx='26' ry='12'/><ellipse cx='90' cy='54' rx='18' ry='10'/><ellipse cx='50' cy='62' rx='16' ry='9'/>" +
        "    <ellipse cx='138' cy='74' rx='30' ry='13'/><ellipse cx='164' cy='70' rx='18' ry='9'/><ellipse cx='118' cy='78' rx='14' ry='8'/>" +
        "  </g>" +
        "  <path d='M22 88 H190' stroke='rgba(255,255,255,0.14)' stroke-width='2'/>" +
        "</svg>";

    public static string BACKGROUND_FLOWINGAURORA =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='BACKGROUND_FLOWINGAURORA'>" +
        "  <defs>" +
        "    <linearGradient id='bg-aurora-night' x1='0%' y1='100%' x2='100%' y2='0%'>" +
        "      <stop offset='0%' stop-color='#031015'/>" +
        "      <stop offset='100%' stop-color='#0b1b24'/>" +
        "    </linearGradient>" +
        "  </defs>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='url(#bg-aurora-night)' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <path d='M20 94 C38 54, 62 94, 82 52 S122 20, 142 52 S176 96, 196 44' fill='none' stroke='" + AccentToken + "' stroke-width='5' stroke-linecap='round' opacity='0.92'/>" +
        "  <path d='M28 100 C56 72, 82 102, 112 70 S152 42, 186 66' fill='none' stroke='rgba(193,255,238,0.28)' stroke-width='3' stroke-linecap='round'/>" +
        "  <circle cx='52' cy='40' r='2' fill='rgba(255,255,255,0.8)'/><circle cx='146' cy='34' r='1.5' fill='rgba(255,255,255,0.7)'/>" +
        "</svg>";

    public static string BACKGROUND_FRACTALFLAME =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='BACKGROUND_FRACTALFLAME'>" +
        "  <defs>" +
        "    <radialGradient id='bg-flame-core' cx='50%' cy='52%' r='48%'>" +
        "      <stop offset='0%' stop-color='rgba(255,255,255,0.22)'/>" +
        "      <stop offset='100%' stop-color='rgba(255,255,255,0)'/>" +
        "    </radialGradient>" +
        "  </defs>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#171126' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <path d='M106 30 C82 50, 78 84, 106 100 C134 84, 130 50, 106 30 Z' fill='url(#bg-flame-core)'/>" +
        "  <path d='M106 34 C90 52, 92 78, 106 92 C120 78, 122 52, 106 34 Z' fill='none' stroke='" + AccentToken + "' stroke-width='3'/>" +
        "  <path d='M76 70 C88 54, 98 86, 106 70 C114 54, 124 86, 136 70' fill='none' stroke='rgba(255,255,255,0.18)' stroke-width='2.5' stroke-linecap='round'/>" +
        "</svg>";

    public static string BACKGROUND_GENTLERIPPLES =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='BACKGROUND_GENTLERIPPLES'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#10243a' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <g fill='none' stroke-linecap='round'>" +
        "    <circle cx='70' cy='60' r='12' stroke='rgba(255,255,255,0.18)' stroke-width='2'/><circle cx='70' cy='60' r='24' stroke='rgba(255,255,255,0.12)' stroke-width='2'/>" +
        "    <circle cx='142' cy='78' r='10' stroke='" + AccentToken + "' stroke-width='2.5' opacity='0.8'/><circle cx='142' cy='78' r='22' stroke='rgba(255,255,255,0.12)' stroke-width='2'/>" +
        "    <circle cx='108' cy='48' r='8' stroke='rgba(170,225,255,0.25)' stroke-width='2'/><circle cx='108' cy='48' r='18' stroke='rgba(170,225,255,0.12)' stroke-width='1.5'/>" +
        "  </g>" +
        "</svg>";

    public static string BACKGROUND_HENONMAP =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='BACKGROUND_HENONMAP'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#141d0d' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <g fill='none' stroke-linecap='round'>" +
        "    <path d='M56 80 C72 42, 104 38, 118 66 C130 90, 150 88, 168 58' stroke='" + AccentToken + "' stroke-width='3.5' opacity='0.85'/>" +
        "    <path d='M60 84 C78 52, 102 52, 114 70 C126 88, 144 86, 160 64' stroke='rgba(255,255,255,0.16)' stroke-width='2'/>" +
        "    <circle cx='88' cy='56' r='2' fill='rgba(255,255,255,0.55)'/><circle cx='124' cy='76' r='2' fill='rgba(255,255,255,0.45)'/>" +
        "  </g>" +
        "</svg>";

    public static string BACKGROUND_HOPFBIFURCATION =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='BACKGROUND_HOPFBIFURCATION'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#10231f' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <g fill='none' stroke-linecap='round'>" +
        "    <circle cx='106' cy='65' r='14' stroke='rgba(255,255,255,0.14)' stroke-width='2'/>" +
        "    <circle cx='106' cy='65' r='28' stroke='" + AccentToken + "' stroke-width='3' opacity='0.82'/>" +
        "    <circle cx='106' cy='65' r='40' stroke='rgba(168,255,226,0.20)' stroke-width='2'/>" +
        "    <path d='M106 25 C128 38, 132 92, 106 105 C80 92, 84 38, 106 25 Z' stroke='rgba(255,255,255,0.12)' stroke-width='2'/>" +
        "  </g>" +
        "</svg>";

    public static string BACKGROUND_IKEDAMAP =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='BACKGROUND_IKEDAMAP'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#1a1324' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <g fill='none' stroke-linecap='round'>" +
        "    <path d='M54 82 C70 38, 120 32, 138 66 C150 88, 138 96, 110 88 C82 80, 72 58, 88 46' stroke='" + AccentToken + "' stroke-width='3.5' opacity='0.86'/>" +
        "    <path d='M64 86 C84 54, 118 48, 130 68 C138 82, 128 86, 108 82 C88 78, 82 64, 92 54' stroke='rgba(255,255,255,0.18)' stroke-width='2'/>" +
        "    <circle cx='142' cy='60' r='3' fill='rgba(255,255,255,0.35)'/><circle cx='92' cy='50' r='2' fill='rgba(255,255,255,0.28)'/>" +
        "  </g>" +
        "</svg>";

    public static string BACKGROUND_JULIASET =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='BACKGROUND_JULIASET'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#0f2217' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <g fill='none' stroke-linecap='round' stroke-linejoin='round'>" +
        "    <path d='M72 82 C58 62, 68 38, 92 34 C112 30, 128 40, 136 56 C144 72, 136 92, 116 96 C98 100, 82 94, 72 82 Z' stroke='" + AccentToken + "' stroke-width='3.5' opacity='0.9'/>" +
        "    <path d='M88 74 C82 62, 88 48, 100 44 C114 40, 126 48, 130 60 C134 72, 128 84, 116 88 C104 92, 94 86, 88 74 Z' stroke='rgba(255,255,255,0.18)' stroke-width='2'/>" +
        "    <circle cx='104' cy='64' r='3' fill='rgba(255,255,255,0.30)'/>" +
        "  </g>" +
        "</svg>";

    public static string BACKGROUND_LAVALAMP =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='BACKGROUND_LAVALAMP'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#28111f' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <g>" +
        "    <ellipse cx='70' cy='78' rx='18' ry='24' fill='rgba(255,150,88,0.26)'/><ellipse cx='96' cy='54' rx='16' ry='20' fill='rgba(255,86,184,0.24)'/>" +
        "    <ellipse cx='126' cy='82' rx='22' ry='18' fill='rgba(255,132,80,0.24)'/><ellipse cx='148' cy='52' rx='14' ry='18' fill='rgba(255,96,180,0.20)'/>" +
        "    <path d='M56 88 C70 54, 92 92, 108 60 S142 40, 162 78' fill='none' stroke='" + AccentToken + "' stroke-width='3.5' stroke-linecap='round' opacity='0.82'/>" +
        "  </g>" +
        "</svg>";

    public static string BACKGROUND_LOGISTICMAPBIFURCATION =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='BACKGROUND_LOGISTICMAPBIFURCATION'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#10182a' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <g fill='rgba(148,196,255,0.26)'>" +
        "    <circle cx='58' cy='70' r='2'/><circle cx='74' cy='64' r='2'/><circle cx='74' cy='78' r='2'/><circle cx='92' cy='54' r='2'/>" +
        "    <circle cx='92' cy='72' r='2'/><circle cx='92' cy='88' r='2'/><circle cx='114' cy='44' r='2'/><circle cx='114' cy='60' r='2'/>" +
        "    <circle cx='114' cy='78' r='2'/><circle cx='114' cy='94' r='2'/><circle cx='140' cy='38' r='2'/><circle cx='140' cy='52' r='2'/>" +
        "    <circle cx='140' cy='68' r='2'/><circle cx='140' cy='84' r='2'/><circle cx='140' cy='98' r='2'/>" +
        "  </g>" +
        "  <path d='M30 92 H184' stroke='rgba(255,255,255,0.10)' stroke-width='1.5'/>" +
        "  <path d='M42 34 V98' stroke='rgba(255,255,255,0.08)' stroke-width='1.5'/>" +
        "</svg>";

    public static string BACKGROUND_LORENZATTRACTOR =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='BACKGROUND_LORENZATTRACTOR'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#0d2122' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <g fill='none' stroke-linecap='round'>" +
        "    <path d='M72 70 C60 46, 78 36, 96 50 C112 62, 104 82, 90 84 C74 86, 66 78, 72 70 Z' stroke='rgba(255,255,255,0.18)' stroke-width='2'/>" +
        "    <path d='M122 60 C112 40, 132 32, 148 46 C162 58, 156 76, 142 78 C128 80, 118 70, 122 60 Z' stroke='rgba(255,255,255,0.18)' stroke-width='2'/>" +
        "    <path d='M78 70 C68 46, 90 28, 112 42 C126 50, 126 66, 118 74 C130 48, 152 34, 166 52 C176 66, 166 84, 148 84' stroke='" + AccentToken + "' stroke-width='3.5' opacity='0.86'/>" +
        "  </g>" +
        "</svg>";

    public static string BACKGROUND_MANDELBROTDRIFT =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='BACKGROUND_MANDELBROTDRIFT'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#221014' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <g fill='none' stroke-linecap='round' stroke-linejoin='round'>" +
        "    <path d='M70 76 C66 58, 78 42, 100 42 C120 42, 132 56, 128 76 C124 92, 110 98, 94 94 C82 90, 74 86, 70 76 Z' stroke='" + AccentToken + "' stroke-width='3.5' opacity='0.86'/>" +
        "    <path d='M88 72 C86 62, 92 54, 102 54 C112 54, 118 62, 116 72 C114 80, 106 84, 98 82 C92 80, 90 78, 88 72 Z' stroke='rgba(255,255,255,0.16)' stroke-width='2'/>" +
        "    <circle cx='146' cy='52' r='3' fill='rgba(255,200,164,0.28)'/><circle cx='156' cy='62' r='2' fill='rgba(255,200,164,0.18)'/>" +
        "  </g>" +
        "</svg>";

    public static string BACKGROUND_PERLINNOISE =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='BACKGROUND_PERLINNOISE'>" +
        "  <defs>" +
        "    <linearGradient id='bg-perlin-wash' x1='0%' y1='0%' x2='100%' y2='100%'>" +
        "      <stop offset='0%' stop-color='#241430'/><stop offset='50%' stop-color='#4a1f52'/><stop offset='100%' stop-color='#7b2e67'/>" +
        "    </linearGradient>" +
        "  </defs>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='url(#bg-perlin-wash)' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <path d='M20 52 C42 36, 66 70, 92 58 S142 36, 170 54 S192 72, 198 62' fill='none' stroke='rgba(255,255,255,0.16)' stroke-width='3' stroke-linecap='round'/>" +
        "  <path d='M18 82 C36 68, 58 94, 90 84 S146 58, 198 86' fill='none' stroke='" + AccentToken + "' stroke-width='4' stroke-linecap='round' opacity='0.78'/>" +
        "</svg>";

    public static string BACKGROUND_PLASMAFLOW =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='BACKGROUND_PLASMAFLOW'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#26140c' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <g>" +
        "    <path d='M18 48 C38 34, 56 68, 80 54 S122 26, 148 46 S180 78, 198 60' fill='none' stroke='rgba(255,177,111,0.28)' stroke-width='3' stroke-linecap='round'/>" +
        "    <path d='M18 72 C42 56, 64 88, 92 72 S138 46, 166 64 S188 88, 198 78' fill='none' stroke='" + AccentToken + "' stroke-width='4.5' stroke-linecap='round' opacity='0.84'/>" +
        "    <path d='M18 94 C34 84, 56 104, 86 92 S140 72, 198 90' fill='none' stroke='rgba(255,255,255,0.14)' stroke-width='2.5' stroke-linecap='round'/>" +
        "  </g>" +
        "</svg>";

    public static string BACKGROUND_REACTDIFFUSION =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='BACKGROUND_REACTDIFFUSION'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#121a2f' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <g>" +
        "    <path d='M36 44 C48 34, 64 36, 74 48 C82 58, 80 74, 68 82 C56 88, 42 82, 36 70 C30 60, 28 50, 36 44 Z' fill='rgba(170,214,255,0.16)'/>" +
        "    <path d='M104 54 C116 42, 134 42, 146 54 C156 64, 156 78, 144 88 C132 96, 116 94, 106 84 C96 74, 94 62, 104 54 Z' fill='rgba(170,214,255,0.12)'/>" +
        "    <path d='M54 90 C78 72, 96 96, 122 78 S164 62, 184 86' fill='none' stroke='" + AccentToken + "' stroke-width='3' stroke-linecap='round' opacity='0.8'/>" +
        "  </g>" +
        "</svg>";

    public static string BACKGROUND_ROSSLERATTRACTOR =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='BACKGROUND_ROSSLERATTRACTOR'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#10221b' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <g fill='none' stroke-linecap='round'>" +
        "    <path d='M70 74 C60 50, 80 38, 100 48 C116 56, 120 72, 108 84 C96 92, 80 90, 70 74 Z' stroke='rgba(255,255,255,0.16)' stroke-width='2'/>" +
        "    <path d='M84 68 C76 52, 94 42, 116 50 C134 56, 140 74, 128 84 C114 94, 94 90, 84 68 Z' stroke='" + AccentToken + "' stroke-width='3.5' opacity='0.86'/>" +
        "  </g>" +
        "</svg>";

    public static string BACKGROUND_SPIRALGALAXY =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='BACKGROUND_SPIRALGALAXY'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#140f24' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <g fill='none' stroke-linecap='round'>" +
        "    <path d='M106 64 C122 48, 144 48, 154 62 C162 72, 154 86, 136 90' stroke='" + AccentToken + "' stroke-width='3.5' opacity='0.82'/>" +
        "    <path d='M106 64 C92 78, 72 82, 60 72 C50 62, 58 46, 78 42' stroke='rgba(255,255,255,0.18)' stroke-width='3'/>" +
        "    <circle cx='106' cy='64' r='5' fill='rgba(255,255,255,0.28)'/><circle cx='150' cy='56' r='2' fill='rgba(255,255,255,0.35)'/><circle cx='66' cy='82' r='2' fill='rgba(255,255,255,0.28)'/>" +
        "  </g>" +
        "</svg>";

    public static string BACKGROUND_STARFIELDDRIFT =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='BACKGROUND_STARFIELDDRIFT'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#10152b' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <g fill='rgba(255,255,255,0.78)'>" +
        "    <circle cx='52' cy='42' r='1.8'/><circle cx='78' cy='58' r='1.4'/><circle cx='112' cy='46' r='2.2'/><circle cx='146' cy='68' r='1.6'/><circle cx='172' cy='50' r='2.1'/>" +
        "  </g>" +
        "  <path d='M24 88 C46 80, 74 94, 102 84 S160 72, 188 86' fill='none' stroke='rgba(255,255,255,0.10)' stroke-width='2.5' stroke-linecap='round'/>" +
        "</svg>";

    public static string BACKGROUND_VORONOIDRIFT =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='BACKGROUND_VORONOIDRIFT'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#2a2411' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <g fill='none'>" +
        "    <path d='M32 42 Q56 26 84 44 T138 40 T186 54' stroke='rgba(255,255,255,0.16)' stroke-width='2'/>" +
        "    <path d='M22 84 Q54 60 92 76 T154 70 T190 88' stroke='" + AccentToken + "' stroke-width='3' opacity='0.8'/>" +
        "    <path d='M58 34 Q72 60 54 84 Q76 96 102 82 Q112 54 96 34 Q78 22 58 34 Z' stroke='rgba(255,255,255,0.14)' stroke-width='2'/>" +
        "    <path d='M128 40 Q148 28 164 46 Q172 70 154 88 Q132 92 120 72 Q114 52 128 40 Z' stroke='rgba(255,255,255,0.14)' stroke-width='2'/>" +
        "  </g>" +
        "</svg>";

    public static string NULLPROVIDER_STATIC =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='NULLPROVIDER_STATIC'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#0a0a0a' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <g fill='rgba(255,255,255,0.8)'>" +
        "    <rect x='26' y='34' width='2' height='2'/><rect x='42' y='42' width='2' height='2'/><rect x='58' y='36' width='2' height='2'/>" +
        "    <rect x='72' y='48' width='2' height='2'/><rect x='92' y='40' width='2' height='2'/><rect x='108' y='52' width='2' height='2'/>" +
        "    <rect x='124' y='38' width='2' height='2'/><rect x='146' y='46' width='2' height='2'/><rect x='168' y='34' width='2' height='2'/>" +
        "    <rect x='34' y='62' width='2' height='2'/><rect x='52' y='74' width='2' height='2'/><rect x='84' y='68' width='2' height='2'/>" +
        "    <rect x='118' y='80' width='2' height='2'/><rect x='136' y='66' width='2' height='2'/><rect x='178' y='72' width='2' height='2'/>" +
        "  </g>" +
        "  <path d='M20 92 H192' stroke='rgba(255,255,255,0.15)' stroke-width='2' stroke-dasharray='3 4'/>" +
        "</svg>";

    public static string NULLPROVIDER_VOID =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='NULLPROVIDER_VOID'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#000000' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <rect x='38' y='42' width='136' height='46' rx='3' fill='none' stroke='rgba(255,255,255,0.08)' stroke-width='1.5'/>" +
        "  <circle cx='106' cy='65' r='12' fill='none' stroke='rgba(255,255,255,0.10)' stroke-width='1.5'/>" +
        "</svg>";

    public static string NULLPROVIDER_AURORA =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='NULLPROVIDER_AURORA'>" +
        "  <defs>" +
        "    <linearGradient id='np-aurora-base' x1='0%' y1='100%' x2='100%' y2='0%'>" +
        "      <stop offset='0%' stop-color='#041015'/>" +
        "      <stop offset='100%' stop-color='#071b22'/>" +
        "    </linearGradient>" +
        "  </defs>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='url(#np-aurora-base)' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <path d='M20 88 C42 54, 62 106, 86 62 S128 26, 150 60 S178 92, 196 46' fill='none' stroke='" + AccentToken + "' stroke-width='5' stroke-linecap='round' opacity='0.9'/>" +
        "  <path d='M24 96 C54 70, 76 104, 106 74 S150 48, 188 70' fill='none' stroke='rgba(132,255,222,0.35)' stroke-width='3' stroke-linecap='round'/>" +
        "</svg>";

    public static string NULLPROVIDER_BUTTERFLY =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='NULLPROVIDER_BUTTERFLY'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#131523' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <g fill='none' stroke-linecap='round'>" +
        "    <path d='M78 72 C66 48, 84 34, 100 48 C112 58, 108 76, 92 82 C84 84, 80 80, 78 72 Z' stroke='rgba(255,255,255,0.18)' stroke-width='2.5'/>" +
        "    <path d='M114 68 C106 50, 120 38, 136 48 C148 56, 148 72, 136 80 C126 86, 118 82, 114 68 Z' stroke='" + AccentToken + "' stroke-width='3.5' opacity='0.84'/>" +
        "  </g>" +
        "</svg>";

    public static string NULLPROVIDER_CELLS =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='NULLPROVIDER_CELLS'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#172021' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <g fill='none'>" +
        "    <path d='M34 46 Q52 32 76 46 T116 46 T156 52 T186 42' stroke='rgba(255,255,255,0.16)' stroke-width='2'/>" +
        "    <path d='M26 78 Q54 56 84 74 T142 68 T188 82' stroke='" + AccentToken + "' stroke-width='3' opacity='0.78'/>" +
        "    <path d='M52 30 Q60 54 44 70 Q62 92 88 86 Q104 62 92 40 Q72 24 52 30 Z' stroke='rgba(255,255,255,0.14)' stroke-width='2'/>" +
        "    <path d='M124 40 Q144 28 158 44 Q170 64 154 84 Q132 92 118 74 Q110 54 124 40 Z' stroke='rgba(255,255,255,0.14)' stroke-width='2'/>" +
        "  </g>" +
        "</svg>";

    public static string NULLPROVIDER_CHAOS =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='NULLPROVIDER_CHAOS'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#11191d' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <g fill='none' stroke-linecap='round'>" +
        "    <path d='M72 74 C64 50, 84 36, 104 48 C120 58, 118 76, 102 84 C90 90, 78 86, 72 74 Z' stroke='rgba(255,255,255,0.16)' stroke-width='2'/>" +
        "    <path d='M88 68 C82 54, 96 42, 120 50 C138 56, 144 74, 132 84 C118 94, 96 88, 88 68 Z' stroke='" + AccentToken + "' stroke-width='3.5' opacity='0.84'/>" +
        "  </g>" +
        "</svg>";

    public static string NULLPROVIDER_BREATH =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='NULLPROVIDER_BREATH'>" +
        "  <defs>" +
        "    <radialGradient id='np-breath-pulse' cx='50%' cy='50%' r='52%'>" +
        "      <stop offset='0%' stop-color='rgba(255,255,255,0.22)'/>" +
        "      <stop offset='100%' stop-color='rgba(255,255,255,0)'/>" +
        "    </radialGradient>" +
        "  </defs>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#15182d' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <circle cx='106' cy='65' r='36' fill='url(#np-breath-pulse)'/>" +
        "  <circle cx='106' cy='65' r='22' fill='none' stroke='" + AccentToken + "' stroke-width='2.5' opacity='0.75'/>" +
        "  <circle cx='106' cy='65' r='48' fill='none' stroke='rgba(255,255,255,0.12)' stroke-width='1.5'/>" +
        "</svg>";

    public static string NULLPROVIDER_PLASMA =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='NULLPROVIDER_PLASMA'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#120c16' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <g>" +
        "    <rect x='24' y='34' width='20' height='14' fill='#6d3eff'/><rect x='44' y='34' width='20' height='14' fill='#2cc7ff'/><rect x='64' y='34' width='20' height='14' fill='#41ff9f'/>" +
        "    <rect x='84' y='34' width='20' height='14' fill='#ffd24c'/><rect x='104' y='34' width='20' height='14' fill='#ff6f61'/><rect x='124' y='34' width='20' height='14' fill='#ff4fd0'/>" +
        "    <rect x='144' y='34' width='20' height='14' fill='#7b66ff'/><rect x='164' y='34' width='20' height='14' fill='#2cc7ff'/>" +
        "    <rect x='34' y='48' width='20' height='14' fill='#2cc7ff'/><rect x='54' y='48' width='20' height='14' fill='#41ff9f'/><rect x='74' y='48' width='20' height='14' fill='#ffd24c'/>" +
        "    <rect x='94' y='48' width='20' height='14' fill='#ff6f61'/><rect x='114' y='48' width='20' height='14' fill='#ff4fd0'/><rect x='134' y='48' width='20' height='14' fill='#7b66ff'/>" +
        "    <rect x='154' y='48' width='20' height='14' fill='#41ff9f'/>" +
        "  </g>" +
        "  <path d='M24 92 C48 82, 72 100, 96 88 S144 78, 188 92' fill='none' stroke='rgba(255,255,255,0.18)' stroke-width='2'/>" +
        "</svg>";

    public static string NULLPROVIDER_WAVES =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='NULLPROVIDER_WAVES'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#0f1d24' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <path d='M18 56 C34 40, 54 72, 70 54 S102 36, 118 54 S150 72, 166 52 S188 38, 198 48' fill='none' stroke='rgba(114,208,255,0.8)' stroke-width='3' stroke-linecap='round'/>" +
        "  <path d='M18 74 C40 58, 60 90, 84 74 S128 58, 152 74 S182 88, 198 70' fill='none' stroke='" + AccentToken + "' stroke-width='4' stroke-linecap='round' opacity='0.85'/>" +
        "  <path d='M18 92 C36 78, 60 104, 82 88 S126 72, 146 86 S178 100, 198 84' fill='none' stroke='rgba(255,255,255,0.18)' stroke-width='2.5' stroke-linecap='round'/>" +
        "</svg>";

    public static string NULLPROVIDER_CLOUDS =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='NULLPROVIDER_CLOUDS'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#8ba0b4' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <g fill='rgba(255,255,255,0.28)'>" +
        "    <ellipse cx='62' cy='58' rx='24' ry='11'/><ellipse cx='86' cy='54' rx='17' ry='10'/><ellipse cx='46' cy='62' rx='14' ry='8'/>" +
        "    <ellipse cx='136' cy='72' rx='28' ry='12'/><ellipse cx='160' cy='68' rx='18' ry='9'/><ellipse cx='118' cy='76' rx='14' ry='7'/>" +
        "  </g>" +
        "</svg>";

    public static string NULLPROVIDER_EMBER =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='NULLPROVIDER_EMBER'>" +
        "  <defs>" +
        "    <linearGradient id='np-ember-rise' x1='0%' y1='100%' x2='0%' y2='0%'>" +
        "      <stop offset='0%' stop-color='#4d1306'/><stop offset='55%' stop-color='#b94a13'/><stop offset='100%' stop-color='#2a0d08'/>" +
        "    </linearGradient>" +
        "  </defs>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#170c0a' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <path d='M28 96 C44 74, 50 80, 60 50 C72 84, 86 70, 96 38 C108 76, 122 64, 134 48 C144 80, 162 70, 182 92' fill='none' stroke='url(#np-ember-rise)' stroke-width='6' stroke-linecap='round' opacity='0.9'/>" +
        "  <circle cx='74' cy='64' r='3' fill='rgba(255,190,92,0.55)'/><circle cx='126' cy='58' r='2.5' fill='rgba(255,190,92,0.45)'/>" +
        "</svg>";

    public static string NULLPROVIDER_FIFTH_DIMENSION =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='NULLPROVIDER_FIFTH_DIMENSION'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#111426' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <g fill='none' stroke-linecap='round'>" +
        "    <rect x='62' y='40' width='44' height='34' stroke='rgba(255,255,255,0.18)' stroke-width='2'/><rect x='104' y='52' width='44' height='34' stroke='rgba(255,255,255,0.14)' stroke-width='2'/>" +
        "    <path d='M62 40 L104 52 M106 40 L148 52 M106 74 L148 86 M62 74 L104 86' stroke='" + AccentToken + "' stroke-width='3' opacity='0.82'/>" +
        "    <path d='M84 28 L126 40 M84 98 L126 86' stroke='rgba(255,255,255,0.16)' stroke-width='2'/>" +
        "  </g>" +
        "</svg>";

    public static string NULLPROVIDER_FLOW =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='NULLPROVIDER_FLOW'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#122128' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <path d='M18 50 C34 44, 50 72, 68 64 S102 34, 122 52 S154 88, 176 72 S192 46, 198 58' fill='none' stroke='rgba(120,220,255,0.35)' stroke-width='3' stroke-linecap='round'/>" +
        "  <path d='M18 74 C42 56, 58 90, 82 72 S124 48, 146 68 S180 92, 198 78' fill='none' stroke='" + AccentToken + "' stroke-width='4' stroke-linecap='round' opacity='0.85'/>" +
        "  <path d='M18 92 C38 82, 62 104, 92 90 S148 70, 198 88' fill='none' stroke='rgba(255,255,255,0.15)' stroke-width='2.2' stroke-linecap='round'/>" +
        "</svg>";

    public static string NULLPROVIDER_FLUID =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='NULLPROVIDER_FLUID'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#16202a' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <g>" +
        "    <circle cx='74' cy='58' r='18' fill='rgba(112,220,255,0.24)'/><circle cx='96' cy='70' r='16' fill='rgba(112,220,255,0.30)'/>" +
        "    <circle cx='122' cy='56' r='20' fill='rgba(112,220,255,0.22)'/><circle cx='144' cy='74' r='14' fill='rgba(112,220,255,0.28)'/>" +
        "    <path d='M56 76 C72 50, 104 98, 132 60 C146 42, 164 60, 170 82' fill='none' stroke='" + AccentToken + "' stroke-width='3.5' stroke-linecap='round' opacity='0.82'/>" +
        "  </g>" +
        "</svg>";

    public static string NULLPROVIDER_GROWTH =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='NULLPROVIDER_GROWTH'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#101710' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <g fill='none' stroke-linecap='round'>" +
        "    <path d='M106 96 L106 66 L92 52 M106 66 L120 50 M92 52 L82 40 M92 52 L92 36 M120 50 L132 38 M120 50 L124 32' stroke='" + AccentToken + "' stroke-width='3'/>" +
        "    <path d='M106 82 L96 72 M106 82 L116 72 M92 36 L84 28 M124 32 L132 24' stroke='rgba(195,255,195,0.30)' stroke-width='2'/>" +
        "  </g>" +
        "</svg>";

    public static string NULLPROVIDER_INFINITY =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='NULLPROVIDER_INFINITY'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#13131f' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <g fill='none' stroke-linecap='round'>" +
        "    <path d='M58 74 C58 46, 96 34, 118 52 C130 62, 130 78, 118 88 C94 106, 58 92, 58 74 Z' stroke='" + AccentToken + "' stroke-width='3.5' opacity='0.88'/>" +
        "    <path d='M88 70 C88 56, 106 48, 118 56 C126 62, 126 72, 118 80 C106 88, 88 82, 88 70 Z' stroke='rgba(255,255,255,0.16)' stroke-width='2'/>" +
        "    <circle cx='150' cy='48' r='3' fill='rgba(255,255,255,0.20)'/><circle cx='162' cy='40' r='2' fill='rgba(255,255,255,0.16)'/>" +
        "  </g>" +
        "</svg>";

    public static string NULLPROVIDER_JULIA =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='NULLPROVIDER_JULIA'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#181226' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <g fill='none' stroke-linecap='round' stroke-linejoin='round'>" +
        "    <path d='M74 80 C64 58, 80 38, 104 40 C124 42, 138 58, 134 78 C130 92, 114 98, 94 94 C82 92, 78 88, 74 80 Z' stroke='" + AccentToken + "' stroke-width='3.5' opacity='0.86'/>" +
        "    <path d='M90 74 C84 60, 94 50, 108 52 C120 54, 126 64, 122 76 C118 84, 108 88, 98 86 C92 84, 92 80, 90 74 Z' stroke='rgba(255,255,255,0.18)' stroke-width='2'/>" +
        "  </g>" +
        "</svg>";

    public static string NULLPROVIDER_LATTICE =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='NULLPROVIDER_LATTICE'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#171d1b' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <g fill='none' stroke='rgba(255,255,255,0.18)' stroke-width='1.8'>" +
        "    <polygon points='56,44 68,38 80,44 80,56 68,62 56,56'/><polygon points='92,44 104,38 116,44 116,56 104,62 92,56'/>" +
        "    <polygon points='74,66 86,60 98,66 98,78 86,84 74,78'/><polygon points='128,66 140,60 152,66 152,78 140,84 128,78'/>" +
        "  </g>" +
        "  <path d='M56 50 H116 M74 72 H152' stroke='" + AccentToken + "' stroke-width='2.4' opacity='0.72'/>" +
        "</svg>";

    public static string NULLPROVIDER_MIRRORS =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='NULLPROVIDER_MIRRORS'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#14161f' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <g fill='none' stroke-linecap='round'>" +
        "    <path d='M106 65 L150 42 L172 65 L150 88 Z' stroke='" + AccentToken + "' stroke-width='3' opacity='0.85'/>" +
        "    <path d='M106 65 L62 42 L40 65 L62 88 Z' stroke='rgba(255,255,255,0.18)' stroke-width='3'/>" +
        "    <path d='M106 65 L106 28 M106 65 L106 102 M106 65 L72 52 M106 65 L140 52 M106 65 L72 78 M106 65 L140 78' stroke='rgba(255,255,255,0.14)' stroke-width='2'/>" +
        "  </g>" +
        "</svg>";

    public static string NULLPROVIDER_MURMURATION =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='NULLPROVIDER_MURMURATION'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#10151b' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <g fill='rgba(255,255,255,0.18)'>" +
        "    <circle cx='62' cy='54' r='2'/><circle cx='74' cy='60' r='2.2'/><circle cx='88' cy='50' r='1.8'/><circle cx='102' cy='64' r='2.1'/>" +
        "    <circle cx='118' cy='58' r='2'/><circle cx='132' cy='68' r='2.2'/><circle cx='146' cy='62' r='1.9'/><circle cx='160' cy='72' r='2'/>" +
        "  </g>" +
        "  <path d='M50 74 C72 46, 94 82, 118 60 S158 44, 178 72' fill='none' stroke='" + AccentToken + "' stroke-width='3' stroke-linecap='round' opacity='0.76'/>" +
        "</svg>";

    public static string NULLPROVIDER_OSCILLATIONS =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='NULLPROVIDER_OSCILLATIONS'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#13192b' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <g fill='none' stroke-linecap='round'>" +
        "    <path d='M30 64 C54 18, 92 112, 122 52 S176 18, 190 74' stroke='rgba(255,255,255,0.16)' stroke-width='2.5'/>" +
        "    <path d='M30 82 C68 36, 86 92, 124 68 S170 40, 190 92' stroke='" + AccentToken + "' stroke-width='3.5' opacity='0.84'/>" +
        "  </g>" +
        "</svg>";

    public static string NULLPROVIDER_RIPPLES =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='NULLPROVIDER_RIPPLES'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#102334' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <g fill='none' stroke-linecap='round'>" +
        "    <circle cx='68' cy='58' r='10' stroke='rgba(255,255,255,0.18)' stroke-width='2'/><circle cx='68' cy='58' r='22' stroke='rgba(255,255,255,0.12)' stroke-width='2'/>" +
        "    <circle cx='136' cy='72' r='12' stroke='" + AccentToken + "' stroke-width='2.5' opacity='0.8'/><circle cx='136' cy='72' r='24' stroke='rgba(255,255,255,0.12)' stroke-width='2'/>" +
        "    <circle cx='110' cy='46' r='8' stroke='rgba(166,224,255,0.24)' stroke-width='2'/>" +
        "  </g>" +
        "</svg>";

    public static string NULLPROVIDER_STARS =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='NULLPROVIDER_STARS'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#1a1230' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <g fill='rgba(255,255,255,0.78)'>" +
        "    <circle cx='46' cy='42' r='2'/><circle cx='76' cy='60' r='1.8'/><circle cx='110' cy='48' r='2.3'/><circle cx='142' cy='70' r='1.9'/><circle cx='174' cy='52' r='2.4'/>" +
        "  </g>" +
        "  <path d='M44 42 L40 34 M110 48 L116 38 M174 52 L182 44' stroke='rgba(255,255,255,0.18)' stroke-width='1.8' stroke-linecap='round'/>" +
        "</svg>";

    public static string NULLPROVIDER_SWARM =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='NULLPROVIDER_SWARM'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#16181f' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <g fill='rgba(255,255,255,0.18)'>" +
        "    <circle cx='56' cy='74' r='2'/><circle cx='74' cy='58' r='2.2'/><circle cx='94' cy='82' r='2'/><circle cx='118' cy='62' r='2.1'/><circle cx='142' cy='78' r='2.2'/>" +
        "  </g>" +
        "  <path d='M56 74 C70 62, 82 68, 98 60 S132 66, 154 54' fill='none' stroke='" + AccentToken + "' stroke-width='3.2' stroke-linecap='round' opacity='0.8'/>" +
        "  <circle cx='162' cy='50' r='4' fill='rgba(255,255,255,0.72)'/>" +
        "</svg>";

    public static string WEBMODULE_ACHIEVEMENTSRUNTIME =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='WEBMODULE_ACHIEVEMENTSRUNTIME'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#1a1426' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <path d='M52 88 V48 H160 V88' fill='none' stroke='rgba(255,255,255,0.16)' stroke-width='2'/>" +
        "  <path d='M76 42 H136 L146 56 H66 Z' fill='none' stroke='" + AccentToken + "' stroke-width='3' stroke-linejoin='round' opacity='0.84'/>" +
        "  <circle cx='106' cy='68' r='16' fill='none' stroke='rgba(255,255,255,0.20)' stroke-width='2'/><path d='M98 68 l6 6 l12 -14' fill='none' stroke='" + AccentToken + "' stroke-width='3' stroke-linecap='round' stroke-linejoin='round'/>" +
        "</svg>";

    public static string WEBMODULE_ACHIEVEMENTSTEST =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='WEBMODULE_ACHIEVEMENTSTEST'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#161a2a' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <rect x='34' y='34' width='144' height='62' rx='6' fill='none' stroke='rgba(255,255,255,0.14)' stroke-width='2'/>" +
        "  <path d='M68 46 H144 M68 60 H128 M68 74 H136' stroke='rgba(255,255,255,0.18)' stroke-width='2' stroke-linecap='round'/>" +
        "  <circle cx='154' cy='76' r='12' fill='none' stroke='" + AccentToken + "' stroke-width='3'/><path d='M148 76 l4 4 l8 -10' fill='none' stroke='" + AccentToken + "' stroke-width='2.6' stroke-linecap='round' stroke-linejoin='round'/>" +
        "</svg>";

    public static string WEBMODULE_APITEST =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='WEBMODULE_APITEST'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#121b23' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <rect x='28' y='34' width='64' height='18' rx='4' fill='none' stroke='rgba(255,255,255,0.16)' stroke-width='2'/><rect x='120' y='34' width='64' height='18' rx='4' fill='none' stroke='rgba(255,255,255,0.16)' stroke-width='2'/>" +
        "  <rect x='74' y='78' width='64' height='18' rx='4' fill='none' stroke='rgba(255,255,255,0.16)' stroke-width='2'/>" +
        "  <path d='M92 44 H120 M152 52 V78 M92 52 V78' fill='none' stroke='" + AccentToken + "' stroke-width='3' stroke-linecap='round' opacity='0.82'/>" +
        "</svg>";

    public static string WEBMODULE_AUDIOTEST =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='WEBMODULE_AUDIOTEST'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#211712' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <rect x='36' y='38' width='140' height='54' rx='8' fill='none' stroke='rgba(255,255,255,0.14)' stroke-width='2'/>" +
        "  <path d='M56 74 V54 l18 -10 v40 Z' fill='none' stroke='rgba(255,255,255,0.18)' stroke-width='2.4' stroke-linejoin='round'/>" +
        "  <path d='M104 76 V54 M122 70 V48 M140 80 V58' stroke='" + AccentToken + "' stroke-width='4' stroke-linecap='round' opacity='0.84'/>" +
        "</svg>";

    public static string WEBMODULE_CONTINUE =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='WEBMODULE_CONTINUE'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#14201d' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <rect x='34' y='34' width='64' height='50' rx='6' fill='none' stroke='rgba(255,255,255,0.14)' stroke-width='2'/><rect x='114' y='34' width='64' height='50' rx='6' fill='none' stroke='rgba(255,255,255,0.14)' stroke-width='2'/>" +
        "  <path d='M98 58 H114' stroke='" + AccentToken + "' stroke-width='3' stroke-linecap='round'/><path d='M108 50 l10 8 l-10 8' fill='none' stroke='" + AccentToken + "' stroke-width='3' stroke-linecap='round' stroke-linejoin='round'/>" +
        "  <path d='M48 46 H84 M128 46 H164 M48 62 H76 M128 62 H156' stroke='rgba(255,255,255,0.18)' stroke-width='2' stroke-linecap='round'/>" +
        "</svg>";

    public static string WEBMODULE_CORES =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='WEBMODULE_CORES'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#162026' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <rect x='30' y='34' width='46' height='24' rx='5' fill='none' stroke='rgba(255,255,255,0.16)' stroke-width='2'/><rect x='84' y='34' width='46' height='24' rx='5' fill='none' stroke='rgba(255,255,255,0.16)' stroke-width='2'/><rect x='138' y='34' width='46' height='24' rx='5' fill='none' stroke='rgba(255,255,255,0.16)' stroke-width='2'/>" +
        "  <rect x='57' y='68' width='46' height='24' rx='5' fill='none' stroke='rgba(255,255,255,0.16)' stroke-width='2'/><rect x='111' y='68' width='46' height='24' rx='5' fill='none' stroke='rgba(255,255,255,0.16)' stroke-width='2'/>" +
        "  <path d='M76 58 L90 68 M130 58 L122 68' stroke='" + AccentToken + "' stroke-width='3' stroke-linecap='round' opacity='0.82'/>" +
        "</svg>";

    public static string WEBMODULE_CORRUPTIONSLOP =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='WEBMODULE_CORRUPTIONSLOP'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#211413' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <path d='M42 42 H170 V88 H42 Z' fill='none' stroke='rgba(255,255,255,0.14)' stroke-width='2'/>" +
        "  <path d='M58 76 C72 42, 90 92, 106 54 S136 30, 154 72' fill='none' stroke='" + AccentToken + "' stroke-width='4' stroke-linecap='round' opacity='0.85'/>" +
        "  <circle cx='162' cy='44' r='8' fill='none' stroke='rgba(255,255,255,0.18)' stroke-width='2'/><path d='M158 44 H166 M162 40 V48' stroke='rgba(255,255,255,0.18)' stroke-width='2' stroke-linecap='round'/>" +
        "</svg>";

    public static string WEBMODULE_DECKBUILDER =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='WEBMODULE_DECKBUILDER'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#1a1f18' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <rect x='42' y='34' width='50' height='60' rx='6' fill='none' stroke='rgba(255,255,255,0.16)' stroke-width='2'/><rect x='76' y='30' width='50' height='60' rx='6' fill='none' stroke='rgba(255,255,255,0.14)' stroke-width='2'/><rect x='110' y='26' width='50' height='60' rx='6' fill='none' stroke='rgba(255,255,255,0.12)' stroke-width='2'/>" +
        "  <path d='M62 48 H80 M96 44 H114 M130 40 H148' stroke='" + AccentToken + "' stroke-width='2.8' stroke-linecap='round' opacity='0.8'/>" +
        "</svg>";

    public static string WEBMODULE_DECKBUILDERCRUD =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='WEBMODULE_DECKBUILDERCRUD'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#171d20' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <rect x='30' y='34' width='152' height='56' rx='6' fill='none' stroke='rgba(255,255,255,0.14)' stroke-width='2'/>" +
        "  <path d='M48 46 H164 M48 60 H124 M48 74 H150' stroke='rgba(255,255,255,0.18)' stroke-width='2' stroke-linecap='round'/>" +
        "  <path d='M142 54 l8 8 l16 -16' fill='none' stroke='" + AccentToken + "' stroke-width='3' stroke-linecap='round' stroke-linejoin='round' opacity='0.82'/>" +
        "</svg>";

    public static string WEBMODULE_GLITCHHARVESTER =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='WEBMODULE_GLITCHHARVESTER'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#181523' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <rect x='30' y='34' width='52' height='52' rx='6' fill='none' stroke='rgba(255,255,255,0.16)' stroke-width='2'/><rect x='94' y='34' width='36' height='52' rx='6' fill='none' stroke='rgba(255,255,255,0.14)' stroke-width='2'/><rect x='142' y='34' width='40' height='52' rx='6' fill='none' stroke='rgba(255,255,255,0.12)' stroke-width='2'/>" +
        "  <path d='M82 60 H94 M130 60 H142' stroke='" + AccentToken + "' stroke-width='3' stroke-linecap='round' opacity='0.84'/>" +
        "  <path d='M48 52 l8 8 l16 -18' fill='none' stroke='rgba(255,255,255,0.18)' stroke-width='2.6' stroke-linecap='round' stroke-linejoin='round'/>" +
        "</svg>";

    public static string WEBMODULE_HEXEDITOR =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='WEBMODULE_HEXEDITOR'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#15181e' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <rect x='28' y='34' width='156' height='58' rx='6' fill='none' stroke='rgba(255,255,255,0.14)' stroke-width='2'/>" +
        "  <path d='M44 48 H168 M44 62 H168 M44 76 H168' stroke='rgba(255,255,255,0.12)' stroke-width='1.8' stroke-linecap='round'/>" +
        "  <path d='M62 40 V86 M98 40 V86 M134 40 V86' stroke='" + AccentToken + "' stroke-width='2.4' stroke-linecap='round' opacity='0.72'/>" +
        "</svg>";

    public static string WEBMODULE_HOME =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='WEBMODULE_HOME'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#16231e' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <path d='M54 74 V52 L106 34 L158 52 V74' fill='none' stroke='rgba(255,255,255,0.16)' stroke-width='2.4' stroke-linejoin='round'/>" +
        "  <path d='M78 74 V58 H134 V74' fill='none' stroke='" + AccentToken + "' stroke-width='3' stroke-linejoin='round' opacity='0.84'/>" +
        "  <path d='M90 48 H122' stroke='rgba(255,255,255,0.18)' stroke-width='2' stroke-linecap='round'/>" +
        "</svg>";

    public static string WEBMODULE_IMAGINEBUG =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='WEBMODULE_IMAGINEBUG'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#181327' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <path d='M106 32 V98 M38 65 H174' stroke='rgba(255,255,255,0.14)' stroke-width='2' stroke-linecap='round'/>" +
        "  <rect x='70' y='48' width='72' height='34' rx='4' fill='none' stroke='" + AccentToken + "' stroke-width='3' opacity='0.84'/>" +
        "  <circle cx='106' cy='65' r='14' fill='none' stroke='rgba(255,255,255,0.18)' stroke-width='2'/><circle cx='106' cy='65' r='3' fill='rgba(255,255,255,0.3)'/>" +
        "</svg>";

    public static string WEBMODULE_OPTIONS =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='WEBMODULE_OPTIONS'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#1c1d17' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <path d='M54 46 H158 M54 64 H158 M54 82 H158' stroke='rgba(255,255,255,0.14)' stroke-width='2' stroke-linecap='round'/>" +
        "  <circle cx='84' cy='46' r='8' fill='none' stroke='" + AccentToken + "' stroke-width='3'/><circle cx='126' cy='64' r='8' fill='none' stroke='" + AccentToken + "' stroke-width='3'/><circle cx='96' cy='82' r='8' fill='none' stroke='" + AccentToken + "' stroke-width='3'/>" +
        "</svg>";

    public static string WEBMODULE_OVERLAY =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='WEBMODULE_OVERLAY'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#12161d' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <rect x='54' y='30' width='104' height='70' rx='8' fill='none' stroke='rgba(255,255,255,0.16)' stroke-width='2'/>" +
        "  <rect x='70' y='42' width='72' height='46' rx='6' fill='none' stroke='" + AccentToken + "' stroke-width='3' opacity='0.82'/>" +
        "  <path d='M82 54 H130 M82 68 H120' stroke='rgba(255,255,255,0.18)' stroke-width='2' stroke-linecap='round'/>" +
        "</svg>";

    public static string WEBMODULE_ROMMANAGER =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='WEBMODULE_ROMMANAGER'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#1d1c15' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <rect x='34' y='34' width='144' height='56' rx='6' fill='none' stroke='rgba(255,255,255,0.14)' stroke-width='2'/>" +
        "  <path d='M52 48 H126 M52 62 H112 M52 76 H136' stroke='rgba(255,255,255,0.18)' stroke-width='2' stroke-linecap='round'/>" +
        "  <path d='M146 50 l10 10 l16 -18' fill='none' stroke='" + AccentToken + "' stroke-width='3' stroke-linecap='round' stroke-linejoin='round' opacity='0.82'/>" +
        "</svg>";

    public static string WEBMODULE_STORY =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='WEBMODULE_STORY'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#19131f' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <rect x='36' y='28' width='140' height='64' rx='6' fill='none' stroke='rgba(255,255,255,0.14)' stroke-width='2'/>" +
        "  <path d='M52 44 H160 M52 58 H144 M52 72 H154' stroke='rgba(255,255,255,0.16)' stroke-width='2' stroke-linecap='round'/>" +
        "  <path d='M72 88 H140' stroke='" + AccentToken + "' stroke-width='3' stroke-linecap='round' opacity='0.84'/>" +
        "</svg>";

    public static string WEBMODULE_TIMEJUMP =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='WEBMODULE_TIMEJUMP'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#141a26' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <path d='M40 82 H172' stroke='rgba(255,255,255,0.16)' stroke-width='2' stroke-linecap='round'/>" +
        "  <circle cx='62' cy='82' r='8' fill='none' stroke='rgba(255,255,255,0.18)' stroke-width='2'/><circle cx='98' cy='82' r='8' fill='none' stroke='rgba(255,255,255,0.18)' stroke-width='2'/><circle cx='134' cy='82' r='8' fill='none' stroke='rgba(255,255,255,0.18)' stroke-width='2'/>" +
        "  <path d='M106 46 a20 20 0 1 1 -0.1 0' fill='none' stroke='" + AccentToken + "' stroke-width='3.2' opacity='0.84'/><path d='M106 46 V62 L118 68' fill='none' stroke='" + AccentToken + "' stroke-width='3' stroke-linecap='round' stroke-linejoin='round'/>" +
        "</svg>";

    public static string WEBMODULE_VOICETEST =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='WEBMODULE_VOICETEST'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#201814' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <path d='M76 44 a14 14 0 0 1 28 0 v16 a14 14 0 0 1 -28 0 z' fill='none' stroke='rgba(255,255,255,0.18)' stroke-width='2.4'/>" +
        "  <path d='M90 76 V88 M76 88 H104' stroke='rgba(255,255,255,0.16)' stroke-width='2' stroke-linecap='round'/>" +
        "  <path d='M120 48 C136 48, 144 60, 120 60 M124 68 C146 68, 154 82, 126 82' fill='none' stroke='" + AccentToken + "' stroke-width='3' stroke-linecap='round' opacity='0.84'/>" +
        "</svg>";

    public static string WEBMODULE_XYTEST =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='WEBMODULE_XYTEST'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#141b21' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <circle cx='82' cy='64' r='18' fill='none' stroke='rgba(255,255,255,0.16)' stroke-width='2.4'/><circle cx='130' cy='64' r='18' fill='none' stroke='rgba(255,255,255,0.16)' stroke-width='2.4'/>" +
        "  <path d='M74 70 l16 -12 M74 58 l16 12' fill='none' stroke='" + AccentToken + "' stroke-width='3' stroke-linecap='round' opacity='0.84'/>" +
        "  <path d='M124 58 l12 12 M136 58 l-12 12' fill='none' stroke='" + AccentToken + "' stroke-width='3' stroke-linecap='round' opacity='0.84'/>" +
        "</svg>";

    public static string FEATURE_SAVESTATES =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='FEATURE_SAVESTATES'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#151a27' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <path d='M40 82 H172' stroke='rgba(255,255,255,0.14)' stroke-width='2' stroke-linecap='round'/>" +
        "  <rect x='48' y='48' width='28' height='22' rx='4' fill='none' stroke='rgba(255,255,255,0.18)' stroke-width='2'/>" +
        "  <rect x='92' y='40' width='28' height='30' rx='4' fill='none' stroke='" + AccentToken + "' stroke-width='3' opacity='0.84'/>" +
        "  <rect x='136' y='52' width='28' height='18' rx='4' fill='none' stroke='rgba(255,255,255,0.14)' stroke-width='2'/>" +
        "  <path d='M106 34 a18 18 0 1 1 -0.1 0 M106 34 V50 L116 56' fill='none' stroke='" + AccentToken + "' stroke-width='2.8' stroke-linecap='round' stroke-linejoin='round' opacity='0.82'/>" +
        "</svg>";

    public static string FEATURE_RTC =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='FEATURE_RTC'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#191422' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <rect x='38' y='40' width='48' height='50' rx='6' fill='none' stroke='rgba(255,255,255,0.14)' stroke-width='2'/>" +
        "  <rect x='126' y='40' width='48' height='50' rx='6' fill='none' stroke='rgba(255,255,255,0.14)' stroke-width='2'/>" +
        "  <path d='M86 64 H126' stroke='" + AccentToken + "' stroke-width='3.2' stroke-linecap='round' opacity='0.84'/>" +
        "  <path d='M98 50 l12 14 l-12 14 M114 50 l12 14 l-12 14' fill='none' stroke='" + AccentToken + "' stroke-width='2.8' stroke-linecap='round' stroke-linejoin='round'/>" +
        "</svg>";

    public static string FEATURE_GH =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='FEATURE_GH'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#18151f' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <rect x='34' y='34' width='64' height='62' rx='6' fill='none' stroke='rgba(255,255,255,0.14)' stroke-width='2'/>" +
        "  <rect x='114' y='34' width='64' height='62' rx='6' fill='none' stroke='rgba(255,255,255,0.12)' stroke-width='2'/>" +
        "  <path d='M66 48 H82 M66 62 H84 M66 76 H78' stroke='rgba(255,255,255,0.16)' stroke-width='2' stroke-linecap='round'/>" +
        "  <path d='M130 52 l10 10 l18 -20' fill='none' stroke='" + AccentToken + "' stroke-width='3.2' stroke-linecap='round' stroke-linejoin='round' opacity='0.84'/>" +
        "</svg>";

    public static string FEATURE_IMAGINE =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='FEATURE_IMAGINE'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#171228' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <path d='M106 30 V100 M36 65 H176' stroke='rgba(255,255,255,0.12)' stroke-width='2' stroke-linecap='round'/>" +
        "  <circle cx='106' cy='65' r='22' fill='none' stroke='" + AccentToken + "' stroke-width='3' opacity='0.84'/>" +
        "  <circle cx='106' cy='65' r='10' fill='none' stroke='rgba(255,255,255,0.20)' stroke-width='2'/>" +
        "  <path d='M106 50 V80 M91 65 H121' stroke='" + AccentToken + "' stroke-width='2.8' stroke-linecap='round'/>" +
        "</svg>";

    public static string FEATURE_DEBUG =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='FEATURE_DEBUG'>" +
        "  <rect x='10' y='18' width='192' height='94' rx='8' fill='#171b1f' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  <rect x='40' y='36' width='132' height='58' rx='6' fill='none' stroke='rgba(255,255,255,0.14)' stroke-width='2'/>" +
        "  <path d='M56 50 H116 M56 64 H104 M56 78 H124' stroke='rgba(255,255,255,0.18)' stroke-width='2' stroke-linecap='round'/>" +
        "  <path d='M136 48 v32 M120 64 h32' stroke='" + AccentToken + "' stroke-width='3' stroke-linecap='round' opacity='0.84'/>" +
        "  <circle cx='136' cy='64' r='16' fill='none' stroke='rgba(255,255,255,0.12)' stroke-width='2'/>" +
        "</svg>";

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

    // CPU_ULQ — scarred die and missing pins
    public static string CPU_ULQ =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='CPU_ULQ'>" +
        "  <g>" +
        "    <rect x='10' y='18' width='192' height='94' rx='6' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <g fill='" + Stroke + "' opacity='0.65'>" +
        "      <rect x='18' y='14' width='8' height='4' rx='1'/>" +
        "      <rect x='42' y='14' width='8' height='4' rx='1'/>" +
        "      <rect x='90' y='14' width='8' height='4' rx='1'/>" +
        "      <rect x='138' y='14' width='8' height='4' rx='1'/>" +
        "      <rect x='18' y='112' width='8' height='4' rx='1'/>" +
        "      <rect x='66' y='112' width='8' height='4' rx='1'/>" +
        "      <rect x='114' y='112' width='8' height='4' rx='1'/>" +
        "      <rect x='162' y='112' width='8' height='4' rx='1'/>" +
        "    </g>" +
        "    <rect x='70' y='42' width='74' height='48' rx='3' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <path d='M78 48 L98 68 L78 88' fill='none' stroke='" + Stroke + "' stroke-width='2' opacity='0.7'/>" +
        "    <path d='M116 46 L96 66 L116 86' fill='none' stroke='" + AccentToken + "' stroke-width='2' opacity='0.8'/>" +
        "    <circle cx='130' cy='52' r='3' fill='" + AccentToken + "' opacity='0.6'/>" +
        "  </g>" +
        "</svg>";

    // CPU_Z80 — experimental chip with zigzag and warning elements
    public static string CPU_Z80 =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='CPU_Z80'>" +
        "  <g>" +
        "    <rect x='10' y='18' width='192' height='94' rx='6' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <g fill='" + Stroke + "'>" +
        // irregular pins to suggest experimental nature
        "      <rect x='20' y='14' width='8' height='4' rx='1'/>" +
        "      <rect x='44' y='14' width='8' height='4' rx='1'/>" +
        "      <rect x='72' y='14' width='8' height='4' rx='1'/>" +
        "      <rect x='96' y='14' width='8' height='4' rx='1'/>" +
        "      <rect x='124' y='14' width='8' height='4' rx='1'/>" +
        "      <rect x='148' y='14' width='8' height='4' rx='1'/>" +
        "      <rect x='20' y='112' width='8' height='4' rx='1'/>" +
        "      <rect x='44' y='112' width='8' height='4' rx='1'/>" +
        "      <rect x='72' y='112' width='8' height='4' rx='1'/>" +
        "      <rect x='96' y='112' width='8' height='4' rx='1'/>" +
        "      <rect x='124' y='112' width='8' height='4' rx='1'/>" +
        "      <rect x='148' y='112' width='8' height='4' rx='1'/>" +
        "    </g>" +
        "    <rect x='64' y='38' width='84' height='54' rx='3' fill='" + ChipFillB + "' stroke='" + AccentToken + "' stroke-width='2' stroke-dasharray='4 2'/>" +
        // zigzag pattern for Z80
        "    <path d='M75 50 L85 60 L95 50 L105 60 L115 50 L125 60 L135 50' fill='none' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "    <path d='M75 75 L85 85 L95 75 L105 85 L115 75 L125 85 L135 75' fill='none' stroke='" + Stroke + "' stroke-width='2' opacity='0.6'/>" +
        // warning indicator
        "    <circle cx='130' cy='48' r='4' fill='" + AccentToken + "' opacity='0.8'/>" +
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
    
    // PPU_IMG — image-focused: viewfinder frame with pixel grid
    public static string PPU_IMG =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='PPU_IMG'>" +
        "  <g>" +
        "    <rect x='10' y='18' width='192' height='94' rx='6' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='58' y='34' width='96' height='62' rx='4' fill='" + ChipFillB + "' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "    <g stroke='" + Stroke + "' stroke-width='1' opacity='0.45'>" +
        "      <path d='M70 34 V96 M82 34 V96 M94 34 V96 M106 34 V96 M118 34 V96 M130 34 V96 M142 34 V96'/>" +
        "      <path d='M58 48 H154 M58 62 H154 M58 76 H154 M58 90 H154'/>" +
        "    </g>" +
        "    <g stroke='" + AccentToken + "' stroke-width='2'>" +
        "      <path d='M58 34 h10 M58 34 v10'/>" +
        "      <path d='M154 34 h-10 M154 34 v10'/>" +
        "      <path d='M58 96 h10 M58 96 v-10'/>" +
        "      <path d='M154 96 h-10 M154 96 v-10'/>" +
        "    </g>" +
        "  </g>" +
        "</svg>";

    // PPU_ULQ — chunky blocks
    public static string PPU_ULQ =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='PPU_ULQ'>" +
        "  <g>" +
        "    <rect x='10' y='18' width='192' height='94' rx='6' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='64' y='38' width='84' height='54' rx='3' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <g fill='" + Stroke + "' opacity='0.6'>" +
        "      <rect x='72' y='46' width='10' height='8'/>" +
        "      <rect x='90' y='46' width='10' height='8'/>" +
        "      <rect x='108' y='46' width='10' height='8'/>" +
        "      <rect x='126' y='46' width='10' height='8'/>" +
        "      <rect x='72' y='62' width='10' height='8'/>" +
        "      <rect x='90' y='62' width='10' height='8'/>" +
        "      <rect x='108' y='62' width='10' height='8'/>" +
        "      <rect x='126' y='62' width='10' height='8'/>" +
        "      <rect x='72' y='78' width='10' height='8'/>" +
        "      <rect x='90' y='78' width='10' height='8'/>" +
        "      <rect x='108' y='78' width='10' height='8'/>" +
        "      <rect x='126' y='78' width='10' height='8'/>" +
        "    </g>" +
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

    // PPU_EXE — secret: phase echo shimmer
    public static string PPU_EXE =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='PPU_EXE'>" +
        "  <g>" +
        "    <rect x='10' y='18' width='192' height='94' rx='6' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='62' y='36' width='88' height='58' rx='3' fill='" + ChipFillB + "' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "    <g stroke='" + Stroke + "' stroke-width='1' opacity='0.5'>" +
        "      <path d='M66 44 H146 M66 54 H146 M66 64 H146 M66 74 H146 M66 84 H146'/>" +
        "    </g>" +
        "    <g stroke='" + AccentToken + "' stroke-width='2' opacity='0.9'>" +
        "      <path d='M68 48 C78 40, 96 56, 106 48 S132 40, 142 50'/>" +
        "      <path d='M70 78 C82 70, 98 86, 110 78 S128 70, 142 82'/>" +
        "    </g>" +
        "    <g fill='" + AccentToken + "' opacity='0.8'>" +
        "      <rect x='56' y='50' width='6' height='6'/>" +
        "      <rect x='150' y='64' width='6' height='6'/>" +
        "      <rect x='58' y='78' width='6' height='6'/>" +
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

    // PPU_CUBEX — enhanced CUBE with outlined structures, bigger shadows, stronger gradients
    public static string PPU_CUBEX =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='PPU_CUBEX'>" +
        "  <defs>" +
        "    <linearGradient id='cubeGrad' x1='0%' y1='0%' x2='0%' y2='100%'>" +
        "      <stop offset='0%' style='stop-color:" + ChipFillB + ";stop-opacity:1'/>" +
        "      <stop offset='100%' style='stop-color:#000;stop-opacity:1'/>" +
        "    </linearGradient>" +
        "  </defs>" +
        "  <g>" +
        "    <rect x='10' y='18' width='192' height='94' rx='6' fill='" + ChipFillA + "' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "    <rect x='64' y='38' width='84' height='54' rx='2' fill='url(#cubeGrad)' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "    <g fill='" + AccentToken + "' opacity='0.9'>" +
        // enhanced cubes with outlines
        "      <rect x='70' y='44' width='10' height='10' stroke='" + Stroke + "' stroke-width='1'/>" +
        "      <rect x='90' y='44' width='10' height='10' stroke='" + Stroke + "' stroke-width='1'/>" +
        "      <rect x='110' y='44' width='10' height='10' stroke='" + Stroke + "' stroke-width='1'/>" +
        "      <rect x='80' y='64' width='10' height='10' stroke='" + Stroke + "' stroke-width='1'/>" +
        "      <rect x='100' y='64' width='10' height='10' stroke='" + Stroke + "' stroke-width='1'/>" +
        "      <rect x='120' y='64' width='10' height='10' stroke='" + Stroke + "' stroke-width='1'/>" +
        "    </g>" +
        // bigger shadow effects
        "    <g fill='#000' opacity='0.35'>" +
        "      <rect x='73' y='56' width='10' height='3'/>" +
        "      <rect x='93' y='56' width='10' height='3'/>" +
        "      <rect x='113' y='56' width='10' height='3'/>" +
        "      <rect x='83' y='76' width='10' height='3'/>" +
        "      <rect x='103' y='76' width='10' height='3'/>" +
        "      <rect x='123' y='76' width='10' height='3'/>" +
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
    
    // APU_HI — clean double waveform with accent shimmer
    public static string APU_HI =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='APU_HI'>" +
        "  <g>" +
        "    <rect x='10' y='18' width='192' height='94' rx='6' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='64' y='38' width='84' height='54' rx='3' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <path d='M70 70 C78 50, 86 90, 94 70 S110 50, 118 70' fill='none' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "    <path d='M70 76 C78 60, 86 88, 94 76 S110 60, 118 76' fill='none' stroke='" + Stroke + "' stroke-width='1.5' opacity='0.6'/>" +
        "  </g>" +
        "</svg>";
    
    // APU_HI2 — warm layer under detail waveform
    public static string APU_HI2 =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='APU_HI2'>" +
        "  <g>" +
        "    <rect x='10' y='18' width='192' height='94' rx='6' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='64' y='38' width='84' height='54' rx='3' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <path d='M70 78 C78 66, 86 84, 94 78 S110 66, 118 78' fill='none' stroke='" + Stroke + "' stroke-width='3' opacity='0.25'/>" +
        "    <path d='M70 70 C78 50, 86 90, 94 70 S110 50, 118 70' fill='none' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "  </g>" +
        "</svg>";
    
    // APU_HI2X — layered waveform with reverb echoes
    public static string APU_HI2X =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='APU_HI2X'>" +
        "  <g>" +
        "    <rect x='10' y='18' width='192' height='94' rx='6' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='64' y='38' width='84' height='54' rx='3' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <path d='M70 78 C78 66, 86 84, 94 78 S110 66, 118 78' fill='none' stroke='" + Stroke + "' stroke-width='3' opacity='0.25'/>" +
        "    <path d='M70 70 C78 50, 86 90, 94 70 S110 50, 118 70' fill='none' stroke='" + AccentToken + "' stroke-width='2'/>" +
        "    <path d='M124 64 C132 54, 140 74, 148 64' fill='none' stroke='" + AccentToken + "' stroke-width='2' opacity='0.35'/>" +
        "    <path d='M128 74 C134 66, 142 80, 148 74' fill='none' stroke='" + AccentToken + "' stroke-width='2' opacity='0.2'/>" +
        "  </g>" +
        "</svg>";

    // APU_ULQ — stepped 1-bit blocks
    public static string APU_ULQ =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='APU_ULQ'>" +
        "  <g>" +
        "    <rect x='10' y='18' width='192' height='94' rx='6' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='64' y='38' width='84' height='54' rx='3' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <polyline points='70,74 80,74 80,64 92,64 92,78 104,78 104,60 116,60 116,74' fill='none' stroke='" + Stroke + "' stroke-width='2'/>" +
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

    // SHADER_MUSK — rocket plume and starfield
    public static string SHADER_MUSK =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='SHADER_MUSK'>" +
        "  <g>" +
        "    <rect x='8' y='16' width='196' height='98' rx='10' fill='" + ChipFillA + "' stroke='" + Stroke + "' stroke-width='2'/>" +
        "    <rect x='18' y='26' width='176' height='78' rx='6' fill='" + ChipFillB + "' stroke='" + Stroke + "' stroke-width='1'/>" +
        "    <g fill='" + AccentToken + "' opacity='0.8'>" +
        "      <circle cx='50' cy='50' r='2'/>" +
        "      <circle cx='160' cy='40' r='1.5'/>" +
        "      <circle cx='140' cy='80' r='1'/>" +
        "      <circle cx='70' cy='90' r='1.5'/>" +
        "    </g>" +
        "    <path d='M106 65 L106 35 L98 45 L106 35 L114 45 Z' fill='" + Stroke + "'/>" +
        "    <path d='M106 75 Q90 85 106 95 Q122 85 106 75' fill='none' stroke='" + AccentToken + "' stroke-width='3'/>" +
        "    <path d='M106 95 Q95 105 106 115 Q117 105 106 95' fill='none' stroke='" + AccentToken + "' stroke-width='2' opacity='0.6'/>" +
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
