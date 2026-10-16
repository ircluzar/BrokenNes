// The game's state in a project: a ROM-mode game is saved with the whole machine (CPU, picture chip, sound chips, memory, cartridge, and any change made to them) and comes back as it was,
// frozen in Instrument Runaway or running. The section's format is in GameState.cs; this is its two ends on the emulator: capturing it for a save and putting it back while the game is built.
//
// Threads: a save runs on FL's thread under the emulator's lock (between frames, like the audio thread's frames); a restore runs on the game's loading thread, on the game it just built, before the audio
// side can see that game, so nothing is allocated on the audio thread and the game never runs before its state is in place.
using BrokenNes.Fruity;
using NesEmulator.Systems;

namespace BrokenNes2;

/// <summary>What a project carried for the game when it was opened: its game-state section (null: an older project), or why the section could not be read.</summary>
internal sealed record RestoreRequest(GameStateSection? Section, string? Problem);

public sealed partial class Emulator
{
    private volatile byte[]? carriedSection;        // the project's section while its game is not yet the running one: a save made now writes it back as it came
    private bool stateNoteIsSave;

    /// <summary>Sets the note shown to the user: <paramref name="head"/> says what happened to the game's state, the end says what the game does. A note a save writes tells what a reopened project will do; one a load writes tells what the game did.</summary>
    private void SetStateNote(string head, bool save = false)
    {
        stateNoteIsSave = save;
        if (save) stateNoteRunaway = stateNoteIdle = $"{head}: the game starts from power-on when the project is reopened";
        else
        {
            stateNoteRunaway = $"{head}: the game restarts when Instrument Runaway is switched off";
            stateNoteIdle = $"{head}: the game started from power-on";
        }
    }

    private void ClearStateNote()
    {
        stateNoteIsSave = false;
        stateNoteIdle = stateNoteRunaway = null;
    }

    // ---- saving ----

    /// <summary>The game-state section for a project save (the payload; null: nothing to write). A game that is still being built is not the machine the project describes: what the project came with is written back as it was.
    /// A game that cannot keep its state, or whose state is too big, is written as a section that says so (and the user is told now, and again when the project is opened).</summary>
    internal byte[]? CaptureGameSection()
    {
        if (Mode != 1) return null;
        bool frozen; long frames; string id; int console;
        byte[]? state = null;
        string? unsupported = null, failure = null;
        PictureCopy? picture = null;
        // under the emulator's lock, between frames: only what has to be read from the running machine (the audio thread waits for this); the compression is done after
        lock (romLock)
        {
            if (loading || Volatile.Read(ref pendingGame) != null) return carriedSection;
            var g = game;
            if (g == null || g.Console != Console) return carriedSection;
            frozen = Runaway; frames = g.FramesRun; id = g.GameId; console = g.Console;
            if (!g.StateSupported) unsupported = g.StateUnsupportedReason;
            else
            {
                try { state = g.SaveGameState(); }
                catch (Exception e) { Diag.Log("saving the game's state failed: " + e); failure = $"The game's state could not be written ({e.Message.TrimEnd('.')})"; }
                // a frozen game's picture comes back with it (the game is not running to draw it again); it is the first thing to go when the section is too big
                if (state != null && frozen) picture = CopyPictureForState();
            }
        }
        if (unsupported != null)
        {
            SetStateNote(unsupported, save: true);
            return GameStateSection.BuildWithout(frozen, console, frames, id, unsupported, unsupported: true);
        }
        if (state == null)
        {
            SetStateNote(failure!, save: true);
            return GameStateSection.BuildWithout(frozen, console, frames, id, failure!, unsupported: false);
        }
        var built = GameStateSection.Build(frozen, console, frames, id, state, picture) ?? (picture != null ? GameStateSection.Build(frozen, console, frames, id, state, null) : null);
        if (built == null)
        {
            string reason = $"The game's state is too big for the project ({state.Length:N0} bytes; the limit is {GameStateSection.MaxStored:N0} bytes once compressed)";
            SetStateNote(reason, save: true);
            return GameStateSection.BuildWithout(frozen, console, frames, id, reason, unsupported: false);
        }
        if (stateNoteIsSave) ClearStateNote();      // a note an earlier save wrote is no longer true
        return built;
    }
    private PictureCopy? CopyPictureForState()
    {
        lock (frameLock)
        {
            if (!frameValid) return null;
            int n = picW * picH * 4;
            var copy = new byte[n];
            Buffer.BlockCopy(frame, 0, copy, 0, n);
            return new PictureCopy(picW, picH, dispW, dispH, copy);
        }
    }

    // ---- restoring (on the game's loading thread, on the game it has just built) ----

    /// <summary>Puts a project's state into a game that has just been built, or says why it did not. Never throws; the game is either in the project's state or at power-on.</summary>
    private void ApplyRestore(IGame g, RestoreRequest req)
    {
        string? head = null;                     // why the state was not used (null: it was)
        var sec = req.Section;
        try
        {
            if (req.Problem != null) head = $"The project's saved game state is {req.Problem}, so it was not used";
            else if (sec == null) { if (!g.StateSupported) head = g.StateUnsupportedReason; }
            else if (sec.Console != g.Console || !string.Equals(sec.GameId, g.GameId, StringComparison.OrdinalIgnoreCase))
                head = "The project's saved game state belongs to a different game than the file now at the saved path, so it was not used";
            else if (!sec.HasState)
                head = sec.Unsupported ? (g.StateSupported ? "This project was saved before games of this console could keep their state" : g.StateUnsupportedReason) : sec.Note.Length > 0 ? sec.Note : "The project holds no state for this game";
            else if (!g.StateSupported) head = g.StateUnsupportedReason;
            else
            {
                g.LoadGameState(sec.State!, sec.Frames);
                if (sec.Picture != null) RestoreFrame(sec.Picture);
                Volatile.Write(ref stateRestore, 1);
                return;
            }
        }
        catch (InvalidDataException e) { head = $"The project's saved game state could not be put back ({e.Message.TrimEnd('.')}), so it was not used"; }
        catch (Exception e)
        {
            Diag.Log("restoring the game's state failed: " + e);
            head = $"The project's saved game state could not be put back ({e.Message.TrimEnd('.')}), so it was not used";
        }
        if (head == null) return;                // an older project and a game that can keep its state: nothing to say
        Volatile.Write(ref stateRestore, 2);
        SetStateNote(head);
        carriedSection = null;
    }

    /// <summary>The frozen picture a project carried becomes the picture shown.</summary>
    private void RestoreFrame(PictureCopy p)
    {
        lock (frameLock)
        {
            Buffer.BlockCopy(p.Rgba, 0, frame, 0, Math.Min(p.Rgba.Length, frame.Length));
            picW = p.W; picH = p.H; dispW = p.DispW; dispH = p.DispH;
            frameValid = true;
            frameVersion++;
        }
    }
}
