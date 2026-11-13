// Layout B (2026-11-13): the Runaway tree's play button. A row is auditioned on its own engine over the frozen snapshot (the Presets tab's audition: a middle C held 1.5 s, then its
// release, mixed over this instance's output; nothing of the instance changes). The engine is built here, on the editor's thread; the audio thread only renders it.
using BrokenNes2.Instruments;
using BrokenNes2.Presets;
using BrokenNes2.Ui;

namespace BrokenNes2;

public sealed unsafe partial class Bn2Plugin
{
    /// <summary>Plays a tree row once. The result says what plays, or why nothing does.</summary>
    internal ActionResult AuditionRunaway(TreeItem item)
    {
        var emu = Emulator;
        if (emu == null || !emu.Runaway) return ActionResult.Refused("Instrument Runaway is off: nothing to audition");
        if (UiMutate.Has(UiMutate.AuditionSilent)) return ActionResult.Done("Playing it once");        // (the `runaway-tree` check's deliberate bug: says it plays, plays nothing)
        PresetPlayable p;
        if (emu.SamplerMode && emu.Snapshot is { } snap) p = new PresetPlayable { Console = ConsoleId.Snes, Snes = snap, InstrumentValue = item.Recipe is SpcRecipe sr ? sr.Srcn : item.Value };
        else if (emu.SegaSamplerMode && emu.SegaState is { } sn) p = new PresetPlayable { Console = sn.Kind == SegaSnapshot.KindOpn ? ConsoleId.Genesis : ConsoleId.MasterSystem, Sega = sn, InstrumentValue = item.Recipe != null ? 0 : item.Value };
        else return ActionResult.Refused("the play button works on SNES and Sega games' instruments (a WideMess instrument plays from FL's notes)");
        var a = item.Recipe != null ? PresetAudition.CreateRecipe(p, EmulatorHub.HostRate, item.Recipe, out string? why) : PresetAudition.Create(p, EmulatorHub.HostRate, out why);
        if (a == null) return ActionResult.Refused(why ?? "it cannot be played");
        audition = a;
        return ActionResult.Done($"Playing {item.Name} once (middle C, 1.5 s)");
    }

    /// <summary>Tests: whether an audition is sounding on this instance (the Presets tab's or the tree's).</summary>
    internal bool AuditionPlaying => audition != null;
}
