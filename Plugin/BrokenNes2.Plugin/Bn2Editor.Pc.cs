// The x86 PC console in the editor (2026-11-10): the Instrument row on every channel (OPL patch / DAC waveform / Tandy mode), and the OPL patch editor, a modal window
// (operator envelopes, multiple, KSL/TL, waveform, the four flags, feedback and connection) whose patch is the instance's own (Bn2Plugin.PcCustomPatch, saved as OPC1).
// Control ids 2300-2399; editor test hooks 2300-2309 (through Test: 2300 editor open, 2301 open it, 2302 click field f's + (index f), 2303 its -, 2304 Use Instrument's patch).
using BrokenNes.Fruity;
using BrokenNes2.Pc;

namespace BrokenNes2;

public sealed unsafe partial class Bn2Editor
{
    public const int IdPcEdit = 2300, IdPcClose = 2301, IdPcRevert = 2302, IdPcPlus = 2310, IdPcMinus = 2350;
    private bool pcEdOpen;
    private static bool IsPcEdId(int id) => id is >= IdPcEdit and < 2400;

    /// <summary>The Instrument row of a PC chip (Direct mode, every channel). Returns false when the chip is not a PC chip.</summary>
    private bool DrawPcInstrument(nint dc, int x, int y, int w)
    {
        var chipId = Cx.Chips[Math.Clamp(plugin.Get(P.Core), 0, Cx.Chips.Count - 1)].Id;
        int bank = Cx.PcPatchBank(chipId);
        if (bank < 0) return false;
        if (bank == 0) { Txt(dc, "the PC speaker: a square from the 8254, on or off (no volume, no timbre)", R(x, y, w, RowH), Win32.DT_LEFT | Mid | Win32.DT_END_ELLIPSIS, Faint, fontSmall); return true; }
        var patch = Cx.PatchItems(bank)[Cx.PatchClamp(bank, plugin.Get(P.Instrument))];
        bool custom = bank == 6 && plugin.PcCustomPatch != null;
        Dropdown(dc, IdInstrument, "Instrument", x, y, w, 86, custom ? patch.Name + " (edited)" : patch.Name);
        int tw = w - 86;
        if (bank == 6)
        {
            Button(dc, IdPcEdit, custom ? "Edit patch*" : "Edit patch", R(x + w - 110, y + RowH + 1, 110, 20), primary: custom);
            tw -= 116;
        }
        Txt(dc, patch.Description, R(x + 86, y + RowH + 1, tw, 16), Win32.DT_LEFT | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS, Faint, fontSmall);
        return true;
    }

    private void DrawPcEditor(nint dc)
    {
        DimBackdrop(dc);
        hits.Clear();
        const int W = 700, H = 560;
        var box = R((Width - W) / 2, (Height - H) / 2, W, H);
        Box(dc, R(box.Left + 4, box.Top + 6, W, H), Black, -1, 10);
        Box(dc, box, Panel, Accent, 10);
        int lx = box.Left + 24;
        var regs = plugin.PcCustomPatch ?? OplBank.Patches[OplBank.Clamp(plugin.Get(P.Instrument))].Regs;
        Txt(dc, "OPL2 patch editor", R(lx, box.Top + 12, 400, 30), Win32.DT_LEFT | Mid, Text, fontTitle);
        Txt(dc, plugin.PcCustomPatch != null ? "this instance's own patch: saved in the FL project" : $"starts from the Instrument's patch: {OplBank.Patches[OplBank.Clamp(plugin.Get(P.Instrument))].Name}",
            R(lx, box.Top + 44, W - 48, 18), Win32.DT_LEFT | Win32.DT_SINGLELINE | Win32.DT_END_ELLIPSIS, Dim, fontSmall);
        Txt(dc, "Modulator", R(lx, box.Top + 70, 300, 20), Win32.DT_LEFT | Mid, Text, fontSection);
        Txt(dc, "Carrier", R(lx + 330, box.Top + 70, 300, 20), Win32.DT_LEFT | Mid, Text, fontSection);
        for (int f = 0; f < OplBank.Fields.Length; f++)
        {
            int col = f < 12 ? 0 : f < 24 ? 1 : 2, row = f < 24 ? f % 12 : 12 + (f - 24) / 2;
            int fx = lx + (col == 1 ? 330 : col == 2 ? (f - 24) % 2 == 0 ? 0 : 330 : 0), fy = box.Top + 96 + row * 32;
            if (col == 2) fx = lx + ((f - 24) % 2) * 330;
            var d = OplBank.Fields[f];
            string name = col == 2 ? d.Name : d.Name[4..];
            Txt(dc, name, R(fx, fy, 130, 26), Win32.DT_LEFT | Mid, Text, fontLabel);
            int v = OplBank.GetField(regs, f), max = (1 << d.Bits) - 1;
            Txt(dc, d.Bits == 1 ? (v != 0 ? "on" : "off") : $"{v} / {max}", R(fx + 130, fy, 80, 26), Win32.DT_CENTER | Mid, Text, fontLabel);
            Button(dc, IdPcMinus + f, "-", R(fx + 214, fy, 40, 26));
            Button(dc, IdPcPlus + f, "+", R(fx + 258, fy, 40, 26));
        }
        Button(dc, IdPcRevert, "Use Instrument's patch", R(lx, box.Bottom - 46, 220, 32), primary: false, enabled: plugin.PcCustomPatch != null);
        Button(dc, IdPcClose, "Close", R(box.Right - 24 - 120, box.Bottom - 46, 120, 32), primary: true);
    }

    private void PcEdClick(int id)
    {
        if (id == IdPcEdit) { pcEdOpen = true; return; }
        if (id == IdPcClose) { pcEdOpen = false; return; }
        if (id == IdPcRevert) { plugin.PcCustomPatch = null; return; }
        var regs = plugin.PcCustomPatch ?? OplBank.Patches[OplBank.Clamp(plugin.Get(P.Instrument))].Regs;
        if (id >= IdPcPlus && id < IdPcPlus + OplBank.Fields.Length) { int f = id - IdPcPlus; plugin.PcEditField(f, OplBank.GetField(regs, f) + 1); }
        else if (id >= IdPcMinus && id < IdPcMinus + OplBank.Fields.Length) { int f = id - IdPcMinus; plugin.PcEditField(f, OplBank.GetField(regs, f) - 1); }
        plugin.MarkConfigured("the PC patch editor changed the patch");
    }

    private nint PcEditorTest(int n, nint index)
    {
        switch (n)
        {
            case 2300: return pcEdOpen ? 1 : 0;
            case 2301: PcEdClick(IdPcEdit); return 1;
            case 2302: PcEdClick(IdPcPlus + (int)index); return 1;
            case 2303: PcEdClick(IdPcMinus + (int)index); return 1;
            case 2304: PcEdClick(IdPcRevert); return 1;
        }
        return 0;
    }
}
