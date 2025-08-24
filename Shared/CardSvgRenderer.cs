using System.Globalization;
using System.Collections.Generic;
using System.Text;

namespace BrokenNes.Shared;

public static class CardSvgRenderer
{
    public static string Render(CoreCardModel m, int width = 240, int height = 340)
    {
        // Defensive defaults
        m ??= new CoreCardModel();
        var id = m.Id ?? m.ShortName ?? Guid.NewGuid().ToString("N");
        var shortName = EscapeXml(m.ShortName ?? "");
        var displayName = EscapeXml(m.DisplayName ?? "");
        var desc = m.Description ?? string.Empty;
        var stars = Math.Clamp(m.Rating, 0, 5);
    var borderColor = RatingToBorderColor(stars);
        var perf = m.Performance;
        var perfText = perf > 0 ? $"+{perf}%" : perf < 0 ? $"{perf}%" : "0%";
        var perfColor = perf > 0 ? "#16a34a" : perf < 0 ? "#dc2626" : "#6b7280"; // green/red/slate

    // Fixed base canvas for consistent layout; scale via SVG width/height attributes
    const int baseW = 240;
    const int baseH = 340;
    int svgW = width <= 0 ? baseW : width;
    int svgH = height <= 0 ? baseH : height;

    // Retro layout metrics (in base coordinates)
    const int pad = 14; // a bit more outer padding for airy layout
    int headerH = 40;   // taller header to accommodate two lines comfortably
    int perfBadgeW = 56;
    int perfBadgeH = 18;
    int imageH = 130;   // slightly smaller image area
    int contentW = baseW - pad * 2;
    // Fixed description box metrics (keep box height stable; fit more, smaller lines inside)
    int descHeight = 76; // keep box stable
    int descTopPad = 10; // visual box padding above text area
    int descTextTopInset = 6; // start text a bit lower for more top padding
    int descLineH = 9; // tighter line height
    int descInnerPadTotal = 20; // total left+right inner padding for text within box
    int descLeftPad = descInnerPadTotal / 2;
    int descMaxLines = 15; // Alternatively: Math.Max(4, (descHeight - descTopPad - descTextTopInset) / descLineH)
    // Conservative per-character width estimate for 'Press Start 2P' at font-size 6
    int approxCharWidth = 7; // smaller font, slightly narrower estimate
    int maxCharsPerLine = Math.Max(12, (contentW - descInnerPadTotal) / approxCharWidth);
    var descLines = Wrap(desc, maxCharsPerLine: maxCharsPerLine, maxLines: descMaxLines);

        var sb = new StringBuilder();
        sb.Append($"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 {baseW} {baseH}' width='{svgW}' height='{svgH}' role='img' aria-label='Card for {displayName}'>");

    // Outer retro frame (border color based on rating)
    sb.Append($"<rect x='0.5' y='0.5' width='{baseW - 1}' height='{baseH - 1}' fill='#000' stroke='{borderColor}' stroke-width='4' />");
        sb.Append($"<rect x='6.5' y='6.5' width='{baseW - 13}' height='{baseH - 13}' fill='none' stroke='#ffffff' stroke-width='3' />");

    // Header: short name on first line, display name on second line, lighter and slightly smaller
    sb.Append($"<text x='{pad}' y='{pad + 12}' font-family=\"'Press Start 2P', monospace\" font-size='10' fill='#ffffff'>{shortName}</text>");
    sb.Append($"<text x='{pad}' y='{pad + 26}' font-family=\"'Press Start 2P', monospace\" font-size='9' fill='#e5e7eb'>{displayName}</text>");

    // Performance badge (nudged down a bit)
    sb.Append($"<g transform='translate({baseW - pad - perfBadgeW},{pad + 6})'>");
        sb.Append($"<rect width='{perfBadgeW}' height='{perfBadgeH}' fill='{perfColor}' />");
    sb.Append($"<text x='{perfBadgeW / 2}' y='{perfBadgeH - 5}' text-anchor='middle' font-family=\"'Press Start 2P', monospace\" font-size='9' fill='#0b0f14'>{EscapeXml(perfText)}</text>");
        sb.Append("</g>");

    // Image placeholder
    int imgY = pad + headerH;
        sb.Append($"<g transform='translate({pad},{imgY})'>");
        sb.Append($"<rect x='0' y='0' width='{contentW}' height='{imageH}' fill='#111' stroke='#ffffff' stroke-width='2' stroke-dasharray='4 4' />");
    sb.Append($"<text x='{contentW / 2}' y='{imageH / 2}' text-anchor='middle' font-family=\"'Press Start 2P', monospace\" font-size='8' fill='#c0c0c0'>IMAGE</text>");
        sb.Append("</g>");

        // Description block under image
    int descY = imgY + imageH + 25; // a little more space below image
        sb.Append($"<g transform='translate({pad},{descY})'>");
    sb.Append($"<rect x='0' y='-{descTopPad}' width='{contentW}' height='{descHeight}' fill='none' stroke='#ffffff' stroke-width='2' />");
        int lineY = descTextTopInset;
        foreach (var line in descLines)
        {
            sb.Append($"<text x='{descLeftPad}' y='{lineY}' font-family=\"'Press Start 2P', monospace\" font-size='6' fill='#c0c0c0'><tspan>{EscapeXml(line)}</tspan></text>");
            lineY += descLineH;
        }
        sb.Append("</g>");

        // Rating stars (pixel stars to avoid relying on special glyphs)
        int starsY = descY + descHeight + 3; // closer to the description box
        int starX = pad;
        for (int i = 0; i < 5; i++)
        {
            bool filled = i < stars;
            AppendPixelStar(sb, starX, starsY, filled);
            starX += 25; // keep a modest gap between stars
        }

        // Footer id/note
    sb.Append($"<text x='{pad}' y='{baseH - 18}' font-family=\"'Press Start 2P', monospace\" font-size='7' fill='#7f7f7f'>{EscapeXml(m.FooterNote ?? id)}</text>");

        sb.Append("</svg>");
        return sb.ToString();
    }

    // Map rating to a distinctive border color (0-5). Palette chosen for good contrast on dark.
    private static string RatingToBorderColor(int rating)
    {
        return rating switch
        {
            <= 0 => "#6b7280", // slate/neutral
            1 => "#ef4444",    // red 500
            2 => "#f59e0b",    // amber 500
            3 => "#10b981",    // emerald 500
            4 => "#3b82f6",    // blue 500
            _ => "#a855f7"      // purple 500 for 5
        };
    }

    private static string SanitizeId(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-') sb.Append(ch);
        }
        if (sb.Length == 0) sb.Append("id");
        return sb.ToString();
    }

    private static string EscapeXml(string s)
    {
        return s
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }

    private static List<string> Wrap(string text, int maxCharsPerLine, int maxLines)
    {
        var result = new List<string>(maxLines);
        if (string.IsNullOrWhiteSpace(text) || maxCharsPerLine <= 1 || maxLines <= 0)
            return result;

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var line = new StringBuilder();
        int i = 0;
        while (i < words.Length)
        {
            var word = words[i];

            // Do not hyphenate: if the word cannot fit on this line, move to next line
            if (word.Length > maxCharsPerLine)
            {
                // If a single unbreakable word exceeds the line length, place it on a new line
                if (line.Length > 0)
                {
                    result.Add(line.ToString());
                    if (result.Count >= maxLines)
                    {
                        result[^1] = Ellipsize(result[^1], maxCharsPerLine);
                        return result;
                    }
                    line.Clear();
                }
                // Place as much as fits without breaking, then ellipsize final line if needed
                line.Append(word);
                // If this single word still exceeds the limit, ellipsize it
                if (line.Length > maxCharsPerLine)
                    line.Length = Math.Max(0, Math.Min(line.Length, maxCharsPerLine - 1));
                if (line.Length >= maxCharsPerLine - 1)
                {
                    result.Add(Ellipsize(line.ToString(), maxCharsPerLine));
                    return result;
                }
                i++;
                continue;
            }

            int needed = line.Length == 0 ? word.Length : 1 + word.Length;
            if (line.Length + needed <= maxCharsPerLine)
            {
                if (line.Length > 0) line.Append(' ');
                line.Append(word);
                i++;
            }
            else
            {
                // finalize current line
                result.Add(line.ToString());
                if (result.Count >= maxLines)
                {
                    result[^1] = Ellipsize(result[^1], maxCharsPerLine);
                    return result;
                }
                line.Clear();
            }
        }

        if (line.Length > 0)
        {
            if (result.Count < maxLines) result.Add(line.ToString());
            else if (result.Count == maxLines)
                result[^1] = Ellipsize(result[^1], maxCharsPerLine);
        }

        return result;
    }

    private static string Ellipsize(string s, int maxCharsPerLine)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= maxCharsPerLine) return s;
        if (maxCharsPerLine <= 1) return "…";
        return s.Substring(0, maxCharsPerLine - 1) + "…";
    }
    // Draw a 5x5 pixel star using small rects; each cell is 2x2 px for crisp retro look
    private static void AppendPixelStar(StringBuilder sb, int x, int y, bool filled)
    {
        string color = filled ? "#fbbf24" : "#374151"; // amber / slate for empty
        int s = 3; // pixel size (bigger stars)
        // Coordinates for a diamond-like star shape
        var on = new (int cx, int cy)[] {
            (2,0),
            (1,1),(2,1),(3,1),
            (0,2),(1,2),(2,2),(3,2),(4,2),
            (1,3),(2,3),(3,3),
            (2,4)
        };
        sb.Append("<g>");
        foreach (var (cx, cy) in on)
        {
            int rx = x + cx * s;
            int ry = y + cy * s;
            sb.Append($"<rect x='{rx}' y='{ry}' width='{s}' height='{s}' fill='{color}' />");
        }
        sb.Append("</g>");
    }
}

public sealed class CoreCardModel
{
    public string? Id { get; set; }
    public string? ShortName { get; set; }
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public int Rating { get; set; }
    public int Performance { get; set; }
    public string? FooterNote { get; set; }
}
