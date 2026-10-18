// Relink (2026-10-18): while a recording's tracks are on offer, every BrokenNes2 editor shows what its channel is to them, over the top of the screen: not linked (with a prominent "Link to
// recording" button: the user's anchor when a channel did not join by itself), linked and waiting for its first note (with "Unlink"), or the track it plays and how it found it. The recording's own
// channels show nothing. Control ids 2060 (Link to recording) and 2061 (Unlink); what the last paint drew is editor hook 678 (Bn2Editor.Record.cs).
using BrokenNes.Fruity;

namespace BrokenNes2;

public sealed unsafe partial class Bn2Editor
{
    public const int IdLink = 2060, IdUnlink = 2061;
    private string? drawnBanner;
    private string linkMessage = "";
    private long linkMessageUntil;

    /// <summary>The banner over the screen (below a corruption or song note when one is shown).</summary>
    private void DrawLinkBanner(nint dc, RECT inner)
    {
        drawnBanner = null;
        var b = plugin.LinkBanner();
        if (b is not { } banner) return;
        int top = inner.Top + (corNoteShown || midNoteShown ? 60 : 14);
        var box = R(inner.Left + 14, top, 512 - 28, 118);
        int fill = banner.Button == 1 ? AccentDim : Win32.Rgb(14, 40, 28), edge = banner.Button == 1 ? Accent : Good;
        Box(dc, box, fill, edge, 8);
        Txt(dc, banner.Title, R(box.Left + 14, box.Top + 8, box.Right - box.Left - 28, 18), Win32.DT_LEFT | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS, White, fontSection);
        bool message = linkMessage.Length > 0 && Environment.TickCount64 < linkMessageUntil;
        string text = message ? linkMessage : banner.Text;
        int textRight = banner.Button == 2 ? box.Right - 112 : box.Right - 14;
        int textBottom = banner.Button == 1 ? box.Bottom - 44 : box.Bottom - 8;
        Txt(dc, text, R(box.Left + 14, box.Top + 30, textRight - box.Left - 14, textBottom - box.Top - 30), Win32.DT_LEFT | Win32.DT_WORDBREAK | Win32.DT_END_ELLIPSIS, Text, fontSmall);
        if (banner.Button == 1) Button(dc, IdLink, "Link to recording", R(box.Left + 14, box.Bottom - 40, 220, 32), primary: true);
        else if (banner.Button == 2) Button(dc, IdUnlink, "Unlink", R(box.Right - 104, box.Top + 34, 90, 28));
        drawnBanner = banner.Title + " | " + text;
    }

    /// <summary>The banner's buttons.</summary>
    private void LinkClick(int id)
    {
        if (id == IdLink)
        {
            var why = plugin.LinkByHand("the Link to recording button", fromButton: true);
            linkMessage = why == null ? "Linked: press Play in FL and I will find my track from my first note." : "Not linked: " + why + ".";
        }
        else if (id == IdUnlink)
        {
            plugin.UnlinkByHand();
            linkMessage = "Unlinked: this channel keeps its own sound.";
        }
        linkMessageUntil = Environment.TickCount64 + 4000;
    }

    private long LinkSignature() => TrackOffers.MembersVersion * 31 + plugin.LinkState + (Environment.TickCount64 < linkMessageUntil ? 7 : 3) + (TrackOffers.Current?.Version ?? 0) * 131;
}
