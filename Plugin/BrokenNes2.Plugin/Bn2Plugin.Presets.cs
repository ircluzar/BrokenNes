// Round 6 (presets): an instance plays a preset. With the emulator on a Presets chip (Direct mode, no ROM) every instance on it plays the preset IT holds, on an engine of its own, with FL's notes: the same sampler classes Instrument Runaway uses for the SNES, the Game Boy
// and the Sega chips (built from the preset alone, see Presets/PresetPlay.cs), and a register player for the NES (NesPresetPlayer). The engines are built on the UI thread (a load from the Presets tab, a project opening) and handed to the audio thread the way an instrument
// edit is (PrepareSamplerFor / TakePrepared): nothing is allocated while notes play. The preset rides in the project as section PRS1 (Presets/PresetSection.cs).
using BrokenNes.Fruity;
using BrokenNes2.Instruments;
using BrokenNes2.Presets;
using BrokenNes2.Ui;

namespace BrokenNes2;

/// <summary>Which instance's editor is handling the event or paint in progress: the preset library (one for the whole process) acts on that instance's preset. Set by the editor around every call into the tabs (GUI thread only).</summary>
internal static class PresetHost
{
    public static volatile Bn2Plugin? Current;
}

/// <summary>A preset loaded into an instance: what the library or the project held (the file's bytes, so the project can keep a copy), what it plays with, and the NES player when it is a NES preset.</summary>
internal sealed class LoadedPreset
{
    public required PresetData Data;
    public required byte[] File;
    public required PresetPlayable Play;
    public NesPresetPlayer? Nes;
    /// <summary>The library file it came from (null: from a project, an import not kept, a test).</summary>
    public string? LibraryId;
    public string Summary => PresetPlayable.Describe(Data);
}

public sealed unsafe partial class Bn2Plugin
{
    private volatile LoadedPreset? preset;
    private volatile string? presetNote;
    private volatile PresetAudition? audition;
    private static string lastPresetResult = "";

    internal LoadedPreset? Preset => preset;

    /// <summary>Why the project's preset was not used (null: nothing to say). The editor shows it over the picture.</summary>
    public string? PresetNote => presetNote;

    // ---- loading (UI thread) ----

    /// <summary>Makes <paramref name="file"/> (a complete preset file: from the library, an import, or the project) the preset this instance plays. The file is read completely first; anything wrong is refused with the reason and the instance keeps what it had.</summary>
    internal string? AdoptPreset(byte[] file, string? libraryId, out LoadedPreset? loaded)
    {
        loaded = null;
        var read = PresetFile.Read(PresetMutants.On(PresetMutants.LoadsWrongBytes) && file.Length > 40 ? file.AsSpan(0, file.Length - 1).ToArray() : file);
        if (read.Data == null) return read.Problem;
        var play = PresetPlayable.Build(read.Data, out string? why);
        if (play == null) return why;
        if (play.CannotPlay != null) return play.CannotPlay;
        int rate = EmulatorHub.HostRate;
        var lp = new LoadedPreset { Data = read.Data, File = file, Play = play, LibraryId = libraryId };
        if (play.Console == ConsoleId.Nes) lp.Nes = new NesPresetPlayer(play.Instrument, rate, BrokenNes2.Presets.PresetNote.IsNesTrack(read.Data));     // (a track recorded from a NES game plays its notes' volumes: round 8, record-nes)
        PrepareForPreset(lp);
        presetNote = null;
        preset = lp;
        loaded = lp;
        return null;
    }

    /// <summary>The "Load" of the Presets tab and the picker on the Emulator tab: the preset becomes this instance's, and the emulator goes to Direct mode on a Presets chip (the chip of the picker's console when it is on one already) so that it plays.</summary>
    internal ActionResult LoadPresetFile(byte[] file, string? libraryId)
    {
        var emu = Emulator;
        if (emu == null) return ActionResult.Refused("This instance has no emulator to play the preset on.");
        if (emu.PresetBlocker is { } blocked) return ActionResult.Refused(char.ToUpperInvariant(blocked[0]) + blocked[1..] + ".");        // (nothing is loaded and nothing changes: a game that runs here stays as it is)
        ForgetTrack();
        MarkConfigured("a preset was loaded");
        string? why = AdoptPreset(file, libraryId, out var lp);
        if (why != null) return ActionResult.Refused(why);
        if (Get(P.Recipe) != 0) SetFromUi(P.Recipe, 0);                  // (a new preset starts at its plain sample; the game voice picked for the last one means nothing here)
        string? refusal = emu.EnterPresetMode();
        string name = lp!.Data.Name;
        if (refusal != null) return ActionResult.Refused($"\"{name}\" is loaded into this instance, but {refusal}.");
        int others = emu.MemberCount - 1;
        return ActionResult.Done($"Loaded \"{name}\" ({lp.Summary}). Emulator #{emu.Id} is in Direct mode on the {emu.ChipId} chip and plays it from FL's notes" + (others > 0 ? $"; the {others} other instance{(others == 1 ? "" : "s")} on it play{(others == 1 ? "s" : "")} the preset {(others == 1 ? "it" : "they")} hold{(others == 1 ? "s" : "")}" : "") + ".");
    }

    internal void ClearPreset() { ForgetTrack(); preset = null; presetNote = null; DropPresetEngines(); }

    private void DropPresetEngines()
    {
        // (the audio thread notices that the preset is gone and drops its sampler with the next block)
    }

    private void PrepareForPreset(LoadedPreset lp)
    {
        int rate = EmulatorHub.HostRate;
        object? snap = (object?)lp.Play.Snes ?? (object?)lp.Play.Gb ?? lp.Play.Sega;
        if (snap != null) PrepareSamplerFor(snap, rate);
        if (lp.Nes != null) lp.Nes.HostRate = rate;
    }

    /// <summary>UI thread, before the audio thread drops its engines (Reset Console): the preset's sampler is built again and waits to be taken.</summary>
    internal void PrepareAgainForPreset()
    {
        var lp = preset;
        if (lp != null && Emulator?.PresetMode == true) PrepareForPreset(lp);
    }

    // ---- playing (audio thread) ----

    private void RenderPreset(float* dest, int length, float gl, float gr)
    {
        var lp = preset;
        if (lp == null || lp.Play.CannotPlay != null)
        {
            DropChip();
            if (sampler != null || gbSampler != null || segaSampler != null) DropSampler();
            FinishReleased();
            new Span<float>(dest, length * 2).Clear();
            return;
        }
        var p = lp.Play;
        switch (p.Console)
        {
            case ConsoleId.Snes: RenderSnes(p.Snes, p.InstrumentValue, dest, length, gl, gr); break;
            case ConsoleId.GameBoy: RenderGb(p.Gb, p, dest, length, gl, gr); break;
            case ConsoleId.Nes: RenderNesPreset(lp.Nes!, dest, length, gl, gr); break;
            default: RenderSega(p.Sega, p.InstrumentValue, dest, length, gl, gr); break;
        }
    }

    /// <summary>The NES preset: one monophonic player (the newest held note wins, like a Direct-mode channel); pitch follows FL's live value, loudness is FL's velocity and the Volume parameter.</summary>
    private void RenderNesPreset(NesPresetPlayer player, float* dest, int length, float gl, float gr)
    {
        DropChip();
        if (sampler != null || gbSampler != null || segaSampler != null) DropSampler();
        player.HostRate = EmulatorHub.HostRate;
        float master = Get(P.Volume) / 1000f;
        if (player.Track) { RenderNesTrack(player, dest, length, gl, gr); return; }
        nesProbe?.Bind(player.Chip);                               // (the self-test's NES replay probe, record-nes: null otherwise)
        for (int off = 0; off < length; off += Chunk)
        {
            int n = Math.Min(Chunk, length - off);
            var owner = ResolveOwner();
            float gain = 0;
            if (owner != null)
            {
                if (owner.Fresh) nesProbe?.Mark(owner.Handle);
                ref var fl = ref owner.Params->FinalLevels;
                player.Update(true, owner.Fresh, fl.Pitch + Get(P.Coarse) * 100 + Get(P.Fine));
                owner.Fresh = false;
                gain = (float)Math.Clamp(fl.Vol, 0.0, 1.0) * master;
            }
            else player.Update(false, false, 0);
            player.Render(mono.AsSpan(0, n));
            for (int i = 0; i < n; i++)
            {
                float s = mono[i] * gain;
                dest[(off + i) * 2] = s * gl;
                dest[(off + i) * 2 + 1] = s * gr;
            }
        }
    }

    // ---- audition (the Presets tab) ----

    /// <summary>Plays <paramref name="p"/> once over this instance's output: a middle C held a second and a half, then its release. Nothing of the instance changes.</summary>
    internal ActionResult StartAudition(PresetPlayable p)
    {
        var a = PresetAudition.Create(p, EmulatorHub.HostRate, out string? why);
        if (a == null) return ActionResult.Refused(why ?? "this preset cannot be played");
        audition = a;
        return ActionResult.Done("Playing the preset once (middle C, 1.5 seconds) through this instance's output.");
    }

    private void MixAudition(float* dest, int length)
    {
        var a = audition;
        if (a == null) return;
        if (!a.AddTo(dest, length, Get(P.Volume) / 1000f)) audition = null;
    }

    // ---- the project's copy ----

    private byte[]? PresetSectionBytes()
    {
        var lp = preset;
        if (lp == null || PresetMutants.On(PresetMutants.ProjectKeepsNothing)) return null;
        return PresetSection.Build(lp.File);
    }

    /// <summary>Opening a project: the instance's preset comes back from the project's own copy; a damaged, cut-short or newer section is refused and the note says why (the instance then has no preset).</summary>
    private void RestorePreset(byte[]? section, string? problem)
    {
        if (section == null && problem == null) return;
        if (problem == null)
        {
            var (data, file, why) = PresetSection.Read(section!);
            if (data == null) problem = why;
            else problem = AdoptPreset(file!, null, out _);
        }
        presetNote = problem == null ? null : "The project's preset was not used: " + problem;
        if (problem != null) Diag.Log("project preset: " + problem);
    }

    // ---- test hooks 340-359 ----

    /// <summary>Hooks of the presets, for the self-test (Fpd.TestBase + n, n from 340): 340 load the preset file in the <c>index</c> bytes at <c>value</c> into this instance and put its emulator on the Presets chip (0 done, 1 refused: the words are in hook 341), 341 copy the last result's words
    /// into the buffer at <c>value</c> (at most <c>index</c> chars; returns the length), 342 what this instance holds (0 nothing, 1 plays, 2 cannot play), 343 copy the held preset's name, 344 the held preset file's CRC-32 (0 none), 345 copy the project note
    /// (-1 none), 346 drop the preset, 347 whether an audition is sounding, 348 audition the held preset, 349 bytes this thread allocated, 350 switch a library bug on (value; 0 none), 351 the length of the PRS1 section this instance would save (-1 none),
    /// 352 whether the emulator is in preset mode, 353 copy the held preset's one-line summary, 354 copy the library folder, 355 force a rescan of the library folder (returns the number of presets listed), 356 the held preset's library id (copied; -1 none),
    /// 357 / 358 put the Sega / Game Boy snapshot in the <c>index</c> bytes at <c>value</c> in place of the frozen game's, 359 put the game voices (SpcRecipe.Write bytes, <c>index</c> of them at <c>value</c>) on the frozen SNES snapshot as a capture of a game would have.</summary>
    private nint PresetTestHook(int n, nint index, nint value)
    {
        var emu = assignment.Emulator;
        switch (n)
        {
            case 340:
            {
                if (index <= 0) return 1;
                var file = new ReadOnlySpan<byte>((void*)value, (int)index).ToArray();
                var r = LoadPresetFile(file, null);
                lastPresetResult = r.Message;
                return r.Ok ? 0 : 1;
            }
            case 341: return CopyText(lastPresetResult, index, value);
            case 342: return preset == null ? 0 : preset.Play.CannotPlay != null ? 2 : 1;
            case 343: return preset == null ? -1 : CopyText(preset.Data.Name, index, value);
            case 344: return preset == null ? 0 : (nint)PresetFile.Checksum(preset.File);
            case 345: return presetNote == null ? -1 : CopyText(presetNote, index, value);
            case 346: ClearPreset(); return 0;
            case 347: return audition != null ? 1 : 0;
            case 348:
            {
                if (preset == null) return 1;
                var r = StartAudition(preset.Play);
                lastPresetResult = r.Message;
                return r.Ok ? 0 : 1;
            }
            case 349: return (nint)GC.GetAllocatedBytesForCurrentThread();
            case 350: PresetMutants.Active = (int)value; return 0;
            case 351: return PresetSectionBytes()?.Length ?? -1;
            case 352: return emu?.PresetMode == true ? 1 : 0;
            case 353: return preset == null ? -1 : CopyText(preset.Summary, index, value);
            case 354: return CopyText(PresetLibrary.FolderPath(), index, value);
            case 355: return PresetLibrary.Instance.Rescan();
            case 356: return preset?.LibraryId == null ? -1 : CopyText(preset.LibraryId, index, value);
            case 357:           // put the Sega snapshot in the <index> bytes at <value> in place of the frozen game's (tests: a made-up sound memory), Runaway on (0 done, 1 not a snapshot)
            {
                var s = SegaSnapshot.FromBytes(new ReadOnlySpan<byte>((void*)value, (int)index).ToArray());
                if (s == null || emu == null) return 1;
                emu.ReplaceSegaForTests(s);
                return 0;
            }
            case 358:           // the same for a Game Boy snapshot
            {
                var g = GbSnapshot.FromBytes(new ReadOnlySpan<byte>((void*)value, (int)index).ToArray());
                if (g == null || emu == null) return 1;
                emu.ReplaceGbForTests(g);
                return 0;
            }
            case 359:           // the game voices a capture would have found, put on the frozen SNES snapshot (0 done, 1 not a recipe list or no snapshot)
            {
                var list = SpcRecipe.Read(new ReadOnlySpan<byte>((void*)value, (int)index).ToArray());
                var snap = emu?.Snapshot;
                if (list == null || snap == null) return 1;
                snap.Recipes = list;
                return 0;
            }
            default: return 0;
        }
    }
}
