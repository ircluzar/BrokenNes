// Round 6 (presets): the Emulator tab when the emulator is on a Presets chip. The channel strip gives way to a banner (every instance plays the preset it holds, not a channel), and the Instrument row becomes the PRESET PICKER: a dropdown of the library's presets
// (filtered to the console of the chip: the chip PRE-SNES lists SNES presets, PRESETS lists all of them). Picking one is the same as "Load" in the Presets tab. Nothing of the Emulator tab changes in any other state.
using BrokenNes.Fruity;
using BrokenNes2.Presets;
using BrokenNes2.Ui;

namespace BrokenNes2;

public sealed unsafe partial class Bn2Editor
{
    private string[] presetPickIds = [];
    private string presetPickMessage = "";
    private bool presetPickOk = true;

    /// <summary>Folded into the editor's repaint signature: the preset this instance holds, the note about the project's, and what the picker last said.</summary>
    private long PresetSignature()
    {
        var lp = plugin.Preset;
        return (lp == null ? 0 : PresetFile.Checksum(lp.File) + 1L) + (plugin.PresetNote?.GetHashCode() ?? 0) * 31L + presetPickMessage.GetHashCode();
    }

    private void DrawPresetBanner(nint dc, Emulator emu, int x, int y, int w)
    {
        var r = R(x, y, w, 56);
        Box(dc, r, AccentDim, Accent, 6);
        string filter = emu.PresetFilter ?? "";
        Txt(dc, filter.Length == 0 ? "PRESETS  -  ANY CONSOLE, NO ROM" : $"PRESETS  -  {filter}, NO ROM", R(r.Left + 12, r.Top + 8, w - 24, 18), Win32.DT_LEFT | Win32.DT_SINGLELINE, White, fontSection);
        Txt(dc, "Each instance plays the preset it holds. Pick one below, or Load one in the Presets tab.", R(r.Left + 12, r.Top + 28, w - 24, 24), Win32.DT_LEFT | Win32.DT_WORDBREAK, Win32.Rgb(240, 200, 195), fontSmall);
    }

    /// <summary>The Instrument row in preset mode: the preset this instance holds, a dropdown of the library, and one line about what is loaded (or why not).</summary>
    private void DrawPresetPicker(nint dc, Emulator emu, int x, int y, int w)
    {
        var lp = plugin.Preset;
        Dropdown(dc, IdInstrument, "Preset", x, y, w, 86, lp != null ? lp.Data.Name : "none loaded", dim: lp == null);
        string note = plugin.PresetNote ?? "";
        string info = presetPickMessage.Length > 0 ? presetPickMessage : note.Length > 0 ? note : lp != null ? lp.Summary : "pick a preset from the library, or send one from the Instruments tab";
        bool warn = presetPickMessage.Length > 0 ? !presetPickOk : note.Length > 0;
        Txt(dc, info, R(x + 86, y + RowH + 1, w - 86, 30), Win32.DT_LEFT | Win32.DT_WORDBREAK | Win32.DT_END_ELLIPSIS, warn ? Warn : Faint, fontSmall);
    }

    private ((string, string?)[] Items, int[] Values, int Current) PresetItems(Emulator emu)
    {
        PresetHost.Current = plugin;
        var rows = PresetLibrary.Instance.Search("", emu.PresetFilter);
        presetPickIds = rows.Select(r => r.Id).ToArray();
        var items = rows.Select(r => (r.Name, (string?)$"{r.Console}  -  {r.Category}")).ToArray();
        if (items.Length == 0) items = [("(the library has no preset for this chip)", "send an instrument from the Instruments tab, or Import one in the Presets tab")];
        var held = plugin.Preset;
        int current = held == null ? -1 : Array.FindIndex(presetPickIds, id => id == held.LibraryId || id == PresetLibrary.ProjectPrefix + PresetFile.Checksum(held.File).ToString("X8"));
        return (items, Enumerable.Range(0, items.Length).ToArray(), current);
    }

    private void PresetPick(int index)
    {
        PresetHost.Current = plugin;
        if ((uint)index >= (uint)presetPickIds.Length) { presetPickMessage = "The library has no preset for this chip yet."; presetPickOk = false; return; }
        var r = PresetLibrary.Instance.Load(presetPickIds[index]);
        presetPickMessage = r.Message;
        presetPickOk = r.Ok;
    }
}
