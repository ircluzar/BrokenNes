// The contract between the editor's Instruments and Presets tabs and the models behind them (round 6).
//
// The tabs (Ui/InstrumentsPanel.cs, Ui/PresetsPanel.cs) only know these interfaces. The `imodel` agent implements
// IInstrumentModelProvider over the instrument model (fields, blobs, the corruptor, history); the `presets` agent implements
// IPresetLibrary over the preset files. Both register themselves in UiServices (Ui/UiBoot.cs shows where, in the owner's own file); until they do, the tabs say so on screen
// ("not connected in this build"): a missing model is a message, never a silent empty list.
//
// Rules of the contract (stable on purpose: change a member only together with the tab that shows it):
//  * Every call is made on FL's GUI thread, from the editor's mouse/key handlers or paint. Nothing here is ever called from
//    the audio thread, and an implementation must not take a lock the audio thread takes while it works for a long time.
//    (Edits reach the audio side through whatever the implementation already does for Instrument Runaway; that is not this contract.)
//  * Lists and field arrays returned may be cached by the tab until Version changes. Version must change whenever anything the tab
//    can show changes, also when the change did not come from the tab (another instance, a project restore, Runaway released).
//  * The factory and every call are made while the editor paints or handles a mouse message: keep them quick (build lazily, cache), and say a failure with an
//    ActionResult or Unavailable. A call that throws is caught and shown on the tab ("A model behind this tab failed"), never silently ignored.
//  * Strings are shown as they are: say "BrokenNes2" where the product is named, never a longer branded name.
//  * Ids (field ids, blob ids, preset ids) are opaque, stable strings; the tab never parses them.
//  * An action that cannot be done returns ActionResult.Refused(reason). The tab shows the reason; it never retries or falls back.
namespace BrokenNes2.Ui;

/// <summary>What an action did: Ok with a short sentence for the status line, or a refusal with the reason (shown in amber).</summary>
public readonly record struct ActionResult(bool Ok, string Message)
{
    public static ActionResult Done(string message) => new(true, message);
    public static ActionResult Refused(string reason) => new(false, reason);
}

/// <summary>How a field is edited. Enum: Min..Max, EnumNames[value - Min] when given. Range: Min..Max. Signed: Min (negative) .. Max. BitField: Min is 0, Max is (1 &lt;&lt; bits) - 1, one toggle per bit.</summary>
public enum FieldKind { Enum = 0, Range = 1, BitField = 2, Signed = 3 }

/// <summary>What a blob holds (only for labels and the unit of its offsets: Bytes are always the raw stored bytes).</summary>
public enum BlobKind { Brr = 0, Pcm8 = 1, Pcm16 = 2, WaveNibbles = 3, Dmc = 4, Other = 5 }

/// <summary>One row of the instrument list. Detail is one short line (address, length, loops...); Sounding marks the one the game was playing.</summary>
public sealed record InstrumentSummary(string Name, string Detail, bool Sounding = false);

/// <summary>
/// One typed field of an instrument. <paramref name="Id"/> is stable across edits (the tab addresses a field by it).
/// <paramref name="Group"/> is an optional heading the tab may show (e.g. "Envelope", "Operator 2").
/// </summary>
public sealed record InstrumentField(string Id, string Name, FieldKind Kind, int Min, int Max, int Value, string Explanation,
    bool Locked = false, string[]? EnumNames = null, string? Group = null)
{
    /// <summary>Bits shown for a BitField (the width of Max), at least 1, at most 16.</summary>
    public int Bits => Math.Clamp(Max <= 0 ? 1 : 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)Max), 1, 16);

    /// <summary>The text for a value: the enum name when there is one, else the number (signed with a plus), bit fields in hex.</summary>
    public string Format(int v)
    {
        if (Kind == FieldKind.Enum && EnumNames != null && v - Min >= 0 && v - Min < EnumNames.Length) return EnumNames[v - Min];
        if (Kind == FieldKind.BitField) return "$" + v.ToString(Bits > 8 ? "X4" : "X2");
        if (Kind == FieldKind.Signed && v > 0) return "+" + v;
        return v.ToString();
    }
}

/// <summary>
/// A block of data an instrument owns (a BRR sample, a DAC sample, wave RAM, a DMC sample). <see cref="Bytes"/> are the stored bytes (what the
/// hex field shows and edits); <see cref="Samples"/> is the same data decoded to 16-bit for the waveform (empty when it cannot be shown);
/// <see cref="LoopStart"/> / <see cref="LoopEnd"/> are sample indices into <see cref="Samples"/> (-1 for none).
/// </summary>
public sealed record InstrumentBlob(string Id, string Name, BlobKind Kind, byte[] Bytes, short[] Samples, int SampleRate,
    int LoopStart, int LoopEnd, bool Editable = true, bool Locked = false, string? Note = null);

/// <summary>
/// The instruments of the frozen game and the tools that edit and corrupt them. One provider per (emulator, frozen game): the editor asks
/// <see cref="InstrumentProviderFactory"/> when Instrument Runaway is on and drops the provider when it goes off.
/// An instrument is addressed by its index in <see cref="Instruments"/>.
/// </summary>
public interface IInstrumentModelProvider
{
    /// <summary>Not null when the model cannot show anything for this game (the reason is shown instead of the instruments).</summary>
    string? Unavailable { get; }

    /// <summary>Changes whenever anything shown can have changed (see the rules above).</summary>
    long Version { get; }

    IReadOnlyList<InstrumentSummary> Instruments { get; }

    /// <summary>The fields of an instrument (empty for a bad index).</summary>
    IReadOnlyList<InstrumentField> Fields(int instrument);

    /// <summary>The blobs of an instrument (empty for a bad index or none).</summary>
    IReadOnlyList<InstrumentBlob> Blobs(int instrument);

    // ---- manual edits: the instrument the FL notes play changes at once ----
    ActionResult SetField(int instrument, string fieldId, int value);
    ActionResult SetFieldLocked(int instrument, string fieldId, bool locked);
    ActionResult SetBlobByte(int instrument, string blobId, int offset, byte value);
    ActionResult SetBlobLocked(int instrument, string blobId, bool locked);

    // ---- corruption tools (deterministic: a seed is chosen by the model and shown by SeedText) ----
    /// <summary>Randomises every unlocked field and blob; <paramref name="strength"/> is 0..1 (0 changes nothing, 1 scrambles).</summary>
    ActionResult Randomise(int instrument, double strength);

    /// <summary>A little nudge of one field (<paramref name="fieldId"/> null: the model picks one): <paramref name="direction"/> is -1 or +1.</summary>
    ActionResult Nudge(int instrument, string? fieldId, int direction);

    bool CanUndo(int instrument);
    bool CanRedo(int instrument);
    ActionResult Undo(int instrument);
    ActionResult Redo(int instrument);

    /// <summary>Where the instrument is in its history, e.g. "step 3 of 7" (empty when nothing was done).</summary>
    string HistoryText(int instrument);

    /// <summary>The seed of the last corruption, as shown on screen (empty when none).</summary>
    string SeedText(int instrument);

    /// <summary>Packages the instrument (clean or corrupted, as it is now) for the Presets tab; the tab hands the draft to <see cref="IPresetLibrary.Add"/>.</summary>
    (PresetDraft? Draft, string? Refusal) TakeForPreset(int instrument);

    /// <summary>Writes the edited instrument into the running game's sound memory so the song resumes with it. The result says what persisted and what did not, or why it cannot be done on this console.</summary>
    ActionResult PushIntoGame(int instrument);

    // ---- pushing into the game, part two (round 6, `inject`). Default members, so a model that knows nothing of pushes (the demonstration models) still fits the contract. ----

    /// <summary>Whether this instrument is pushed into the running game right now (the tab enables "Remove from game" for it).</summary>
    bool IsPushed(int instrument) => false;

    /// <summary>Takes the instrument out of the running game: the bytes the push wrote go back where the game still has them, the overrides are removed. The edit in the frozen copy (what FL's notes play) stays.</summary>
    ActionResult RemoveFromGame(int instrument) => ActionResult.Refused("this build cannot take a pushed instrument out of the game");

    /// <summary>A standing note the tab shows while it has nothing else to say (a project whose pushed instruments could not be put back, a sound unit that was replaced): null when there is none.</summary>
    string? Notice => null;
}

/// <summary>Creates the provider for the game frozen on <paramref name="emulator"/> (called when Instrument Runaway is on); null means this build cannot offer instruments for it.</summary>
public delegate IInstrumentModelProvider? InstrumentProviderFactory(Emulator emulator);

/// <summary>What the Instruments tab hands to the preset library: display facts plus the model's own packaging (<see cref="Payload"/>, opaque to the tabs).</summary>
public sealed record PresetDraft(string Name, string Console, string SourceGame, string Notes, object? Payload);

/// <summary>One row of the library list.</summary>
public sealed record PresetSummary(string Id, string Name, string Console, string Category, string Detail);

/// <summary>What the details area shows for the selected preset (ROM identity is for reference only, never the ROM).</summary>
public sealed record PresetDetails(string Id, string Name, string Console, string Category, string SourceGame, string SourceSha1,
    string[] Tags, string Notes, int FieldCount, int BlobCount, bool HasNoteBehaviour, string Origin);

/// <summary>The preset library: the disk library shared by every project, plus what the project embeds. No presets ship with the product.</summary>
public interface IPresetLibrary
{
    /// <summary>Not null when the library cannot be used (the reason is shown instead of the list).</summary>
    string? Unavailable { get; }

    long Version { get; }

    /// <summary>Presets whose name, tags or source contain <paramref name="text"/> (empty: all), of one console (null: every console: the short names NES, GB, SNES, SMS, MD).</summary>
    IReadOnlyList<PresetSummary> Search(string text, string? console);

    PresetDetails? Details(string id);

    /// <summary>Adds a draft the Instruments tab took (a rip, clean or corrupted).</summary>
    ActionResult Add(PresetDraft draft);

    /// <summary>Makes the preset the one this instance plays.</summary>
    ActionResult Load(string id);
    ActionResult Rename(string id, string newName);
    ActionResult Delete(string id);
    ActionResult Export(string id, string path);
    ActionResult Import(string path);

    /// <summary>Plays the preset once so the user hears it (a short note on the private engine; nothing changes in the project).</summary>
    ActionResult Audition(string id);
}

/// <summary>Where the real models register. Set once at start-up (or by a test hook); read on the GUI thread.</summary>
public static class UiServices
{
    private static volatile InstrumentProviderFactory? instruments;
    private static volatile IPresetLibrary? presets;

    /// <summary>The Instruments tab's source. Null = no model in this build (the tab says so).</summary>
    public static InstrumentProviderFactory? Instruments { get => instruments; set => instruments = value; }

    /// <summary>The Presets tab's library. Null = none in this build (the tab says so).</summary>
    public static IPresetLibrary? Presets { get => presets; set => presets = value; }

    public const string NoInstrumentModel = "The instrument model is not connected in this build of BrokenNes2.";
    public const string NoPresetLibrary = "The preset library is not connected in this build of BrokenNes2.";
    public const string NeedRunaway = "Switch Instrument Runaway on to see the game's instruments";
    public const string NeedGame = "Load a game in ROM mode, then switch Instrument Runaway on to see its instruments";
    public const string NoEmulator = "No emulator: BrokenNes2 has no emulator to look at";

    /// <summary>The tabs, by number (what the editor's strip shows): 0 Emulator, 1 Instruments, 2 Presets.</summary>
    public static readonly string[] TabNames = ["Emulator", "Instruments", "Presets"];
}
