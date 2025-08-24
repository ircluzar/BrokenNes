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
        var perf = m.Performance;
        var perfText = perf > 0 ? $"+{perf}%" : perf < 0 ? $"{perf}%" : "0%";
        var perfColor = perf > 0 ? "#16a34a" : perf < 0 ? "#dc2626" : "#6b7280"; // green/red/slate

        // Retro layout metrics
        const int pad = 10;
        int headerH = 28;
        int perfBadgeW = 56;
        int perfBadgeH = 18;
        int imageH = 150;
        int contentW = width - pad * 2;
        var descLines = Wrap(desc, maxCharsPerLine: 32, maxLines: 4);

        var sb = new StringBuilder();
        sb.Append($"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 {width} {height}' width='{width}' height='{height}' role='img' aria-label='Card for {displayName}'>");

        // Outer retro frame
        sb.Append($"<rect x='0.5' y='0.5' width='{width - 1}' height='{height - 1}' fill='#000' stroke='#ff5a26' stroke-width='4' />");
        sb.Append($"<rect x='6.5' y='6.5' width='{width - 13}' height='{height - 13}' fill='none' stroke='#ffffff' stroke-width='3' />");

        // Header: short name and display name (avoid faux-bold for bitmap font)
    sb.Append($"<text x='{pad}' y='{pad + 12}' font-family=\"'Press Start 2P', monospace\" font-size='10' fill='#ffffff'>{shortName}</text>");
    sb.Append($"<text x='{pad + 90}' y='{pad + 12}' font-family=\"'Press Start 2P', monospace\" font-size='10' fill='#ffffff'>{displayName}</text>");

        // Performance badge
        sb.Append($"<g transform='translate({width - pad - perfBadgeW},{pad - 4})'>");
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
        int descY = imgY + imageH + 12;
        int descHeight = Math.Max(24, 12 * descLines.Count + 8);
        sb.Append($"<g transform='translate({pad},{descY})'>");
        sb.Append($"<rect x='0' y='-10' width='{contentW}' height='{descHeight}' fill='none' stroke='#ffffff' stroke-width='2' />");
        int lineY = 0;
        foreach (var line in descLines)
        {
            sb.Append($"<text x='6' y='{lineY}' font-family=\"'Press Start 2P', monospace\" font-size='8' fill='#c0c0c0'><tspan>{EscapeXml(line)}</tspan></text>");
            lineY += 12;
        }
        sb.Append("</g>");

        // Rating stars (pixel stars to avoid relying on special glyphs)
        int starsY = descY + descHeight + 8;
        int starX = pad;
        for (int i = 0; i < 5; i++)
        {
            bool filled = i < stars;
            AppendPixelStar(sb, starX, starsY, filled);
            starX += 20;
        }

        // Footer id/note
    sb.Append($"<text x='{pad}' y='{height - 8}' font-family=\"'Press Start 2P', monospace\" font-size='7' fill='#7f7f7f'>{EscapeXml(m.FooterNote ?? id)}</text>");

        sb.Append("</svg>");
        return sb.ToString();
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
        if (string.IsNullOrWhiteSpace(text)) return result;
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var line = new StringBuilder();
        foreach (var word in words)
        {
            if (line.Length == 0)
            {
                line.Append(word);
            }
            else if (line.Length + 1 + word.Length <= maxCharsPerLine)
            {
                line.Append(' ').Append(word);
            }
            else
            {
                result.Add(line.ToString());
                if (result.Count >= maxLines) return result;
                line.Clear();
                line.Append(word);
            }
        }
        if (line.Length > 0 && result.Count < maxLines) result.Add(line.ToString());
        return result;
    }
    // Draw a 5x5 pixel star using small rects; each cell is 2x2 px for crisp retro look
    private static void AppendPixelStar(StringBuilder sb, int x, int y, bool filled)
    {
        string color = filled ? "#fbbf24" : "#374151"; // amber / slate for empty
        int s = 2; // pixel size
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
