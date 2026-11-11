// Instrument extraction in ROM mode (2026-11-10): the Presets tab's "Capture next note" watches a channel of the game that runs (NES on the FIX chip, Game Boy on the stock chip) with the
// shared capture core (Extract/), and when the channel's next note has ended the note becomes a preset in the library. The feed is attached on the UI thread under romLock and fed between
// frames (it allocates a little while a capture waits: nothing when none does). See docs/projects/flp-music-pipeline.md, "Instrument extraction".
using BrokenNes2.Extract;
using BrokenNes2.Instruments;

namespace BrokenNes2;

public sealed partial class Emulator
{
    private IExtractFeed? extractFeed;
    private IGame? extractGame;
    private int extractChannel, extractLeft;
    private volatile CapturedNote? extractDone;
    private volatile string? extractFailed;
    private const int ExtractWaitFrames = 1200;

    /// <summary>The console a capture can watch now (null: none), for the button's label.</summary>
    public ConsoleId? ExtractConsole
    {
        get
        {
            var g = game;
            if (Runaway || g == null) return null;
            if (g is NesGame { Nes: { } m } && NesExtractFeed.FixOf(m.Nes) != null) return ConsoleId.Nes;
            if (g is SessionGame sg && sg.GbApu != null) return ConsoleId.GameBoy;
            return null;
        }
    }

    public bool ExtractWaiting => extractFeed != null;

    /// <summary>UI thread: start watching <paramref name="channel"/>. Null when started, else why not.</summary>
    public string? StartExtract(int channel)
    {
        lock (romLock)
        {
            ApplyPending();
            var g = game;
            StopExtractLocked();
            extractDone = null; extractFailed = null;
            if (Runaway) return "the game is frozen (Instrument Runaway): let it run to capture a note";
            if (g is NesGame { Nes: { } m } && NesExtractFeed.FixOf(m.Nes) is { } fix) extractFeed = new NesExtractFeed(fix, m.CopyApuLatch);
            else if (g is SessionGame sg && sg.GbApu is { } apu) extractFeed = new GbExtractFeed(apu);
            else return "capture needs a NES game on the FIX sound chip or a Game Boy game running";
            extractGame = g;
            extractChannel = Math.Clamp(channel, 0, 3);
            extractLeft = ExtractWaitFrames;
            return null;
        }
    }

    public void CancelExtract() { lock (romLock) StopExtractLocked(); }

    private void StopExtractLocked() { extractFeed?.Detach(); extractFeed = null; extractGame = null; }

    /// <summary>Between frames (the frame loop, under romLock): feeds the capture; a finished note waits in <see cref="TakeExtracted"/>.</summary>
    private void ExtractFrame(IGame g)
    {
        var feed = extractFeed;
        if (feed == null) return;
        if (!ReferenceEquals(g, extractGame)) { StopExtractLocked(); extractFailed = "the game was replaced before a note was played"; return; }
        feed.EndFrame();
        var rec = feed.Recorder;
        var n = rec.Pick(extractChannel, 0, out _);
        if (n != null && n.End != CaptureEnd.RecordingEnd) { StopExtractLocked(); extractDone = n; return; }
        if (--extractLeft <= 0) { StopExtractLocked(); if (n != null) extractDone = n; else extractFailed = $"no note was played on {CapturedNote.ChannelName(rec.Console, extractChannel)} in 20 seconds"; }
    }

    /// <summary>UI thread: the finished capture (once), or the reason it failed (once), or nothing yet.</summary>
    public (CapturedNote? Note, string? Failed) TakeExtracted()
    {
        var n = extractDone; var f = extractFailed;
        extractDone = null; extractFailed = null;
        return (n, f);
    }

    public string ExtractGameName => RomPath != null ? Path.GetFileNameWithoutExtension(RomPath) : "";
}
