// The Presets tab: the library (a searchable, filterable list), the details of the selected preset, and Load, Rename, Delete, Export, Import and Audition.
// Everything comes from an IPresetLibrary (Contracts.cs); with none connected the tab says so. Export and Import ask for a file with the standard dialogs
// (a test names the file instead: see Bn2Editor.Tabs.cs).
using BrokenNes.Fruity;

namespace BrokenNes2.Ui;

internal sealed class PresetsPanel
{
    private const int ListX = UiLayout.PanelX, ListW = 290, SearchY = 92, FilterY = 126, ListY = 158, ListH = 462;
    private const int DetailX = ListX + ListW + 16, DetailW = UiLayout.PanelRight - DetailX, DetailY = 92, DetailH = 440, RenameY = 540, ButtonsY = 584, StatusY = 620;

    private static readonly string?[] FilterValues = [null, "NES", "GB", "SNES", "SMS", "MD"];
    private static readonly string[] FilterNames = ["All", "NES", "GB", "SNES", "SMS", "MD"];
    private static readonly string[] Buttons = ["Load", "Rename", "Delete", "Export", "Import", "Audition"];

    private enum Drag { None, ListBar }

    /// <summary>Asks for a file: (save?, title, filter) -> path or null. The editor supplies the real dialogs; a test supplies a path.</summary>
    public Func<bool, string, string, string?>? ChooseFile;

    private readonly UiCanvas c;
    private readonly ScrollList list = new(UiIds.PresetList, UiIds.PresetListBar) { RowH = 40 };
    private readonly TextInput search = new(UiIds.Search) { Placeholder = "Search name, tag or game", MaxLength = 40 };
    private readonly TextInput rename = new(UiIds.RenameInput) { MaxLength = 48 };

    private IReadOnlyList<PresetSummary> rows = [];
    private PresetDetails? details;
    private long cacheVersion = -1;
    private string cacheText = "\0";
    private string? cacheFilter = "\0", selectedId;
    private int filter;
    private bool renaming;
    private string? deleteArmed;
    private long deleteArmedTick;
    private Drag drag;
    private string status = "", message = "";
    private bool statusOk = true;

    public PresetsPanel(UiCanvas canvas)
    {
        c = canvas;
        search.Changed = _ => { list.Scroll = 0; };
        rename.Committed = _ => CommitRename();
        rename.Cancelled = _ => { renaming = false; rename.Blur(); };
    }

    public string Message => message;
    public string Status => status;
    public bool StatusOk => statusOk;
    public bool WantsKeys => search.Focused || rename.Focused;
    public bool Renaming => renaming;
    public string? SelectedId => selectedId;
    public int RowCount => rows.Count;
    public int FirstRow => list.Scroll;
    public string SearchText => search.Text;
    public int FilterIndex => filter;

    public long Signature() => (UiServices.Presets?.Version ?? -1) * 7 + list.Scroll * 131 + (selectedId?.GetHashCode() ?? 0) + filter * 3 + search.Signature() + rename.Signature() + (renaming ? 5 : 0)
        + status.GetHashCode() + (deleteArmed != null ? 11 : 0) + message.GetHashCode() + rows.Count;

    private void Act(ActionResult r) { status = r.Message; statusOk = r.Ok; }

    public void Blur() { search.Blur(); rename.Blur(); }
    public void Cancel() { drag = Drag.None; list.BarUp(); Blur(); renaming = false; deleteArmed = null; }

    // ---------------------------------------------------------------- drawing

    public void Draw(nint dc)
    {
        var lib = UiServices.Presets;
        c.Section(dc, "PRESETS", lib == null ? null : "shared by every project; no presets are provided", UiLayout.PanelX, UiLayout.PanelY, UiLayout.PanelW);
        message = lib == null ? UiServices.NoPresetLibrary : lib.Unavailable ?? "";
        if (message.Length > 0)
        {
            var box = UiCanvas.R(UiLayout.PanelX, 110, UiLayout.PanelW, 360);
            c.Box(dc, box, UiCanvas.Panel, UiCanvas.Line, 8);
            c.Txt(dc, message, UiCanvas.R(box.Left + 40, box.Top + 120, UiCanvas.W(box) - 80, 70), Win32.DT_CENTER | Win32.DT_WORDBREAK | Win32.DT_VCENTER, UiCanvas.Text, c.FontLead);
            return;
        }
        Sync(lib!);

        // left: search, console filter, list
        search.Draw(c, dc, UiCanvas.R(ListX, SearchY, ListW - 32, 28));
        var clear = UiCanvas.R(ListX + ListW - 28, SearchY, 28, 28);
        c.Button(dc, UiIds.SearchClear, "x", clear, enabled: search.Text.Length > 0);
        int fw = ListW / FilterNames.Length;
        for (int i = 0; i < FilterNames.Length; i++)
        {
            var seg = UiCanvas.R(ListX + i * fw, FilterY, fw, 26);
            bool on = filter == i, hot = c.Hot == UiIds.Filter + i;
            c.Box(dc, seg, on ? UiCanvas.Accent : hot ? UiCanvas.FieldHot : UiCanvas.Field, on ? UiCanvas.Accent : UiCanvas.Line, 5);
            c.Txt(dc, FilterNames[i], seg, Win32.DT_CENTER | UiCanvas.Mid, on ? UiCanvas.White : UiCanvas.Dim, on ? c.FontSection : c.FontSmall);
            c.AddHit(UiIds.Filter + i, seg);
        }
        DrawList(dc);
        c.Txt(dc, rows.Count == 0 ? "" : $"{rows.Count} preset{(rows.Count == 1 ? "" : "s")}", UiCanvas.R(ListX, ListY + ListH + 6, ListW, 16), Win32.DT_LEFT | Win32.DT_SINGLELINE, UiCanvas.Faint, c.FontSmall);

        // right: details, rename, buttons, status
        DrawDetails(dc);
        DrawButtons(dc);
        if (status.Length > 0)
        {
            c.Fill(dc, UiCanvas.R(DetailX, StatusY, 3, 28), statusOk ? UiCanvas.Good : UiCanvas.Warn);
            c.Txt(dc, status, UiCanvas.R(DetailX + 12, StatusY, DetailW - 12, 32), Win32.DT_LEFT | Win32.DT_WORDBREAK | Win32.DT_VCENTER | Win32.DT_END_ELLIPSIS, UiCanvas.Text, c.FontSmall);
        }
    }

    private void Sync(IPresetLibrary lib)
    {
        long v = lib.Version;
        string? f = FilterValues[filter];
        if (v != cacheVersion || search.Text != cacheText || f != cacheFilter)
        {
            cacheVersion = v; cacheText = search.Text; cacheFilter = f;
            rows = lib.Search(search.Text, f);
            if (selectedId != null && !rows.Any(r => r.Id == selectedId)) { selectedId = null; renaming = false; rename.Blur(); }
            list.Selected = -1;
            for (int i = 0; i < rows.Count; i++) if (rows[i].Id == selectedId) list.Selected = i;
            details = selectedId == null ? null : lib.Details(selectedId);
        }
        if (deleteArmed != null && Environment.TickCount64 - deleteArmedTick > 4000) deleteArmed = null;
    }

    private void DrawList(nint dc)
    {
        list.Draw(c, dc, UiCanvas.R(ListX, ListY, ListW, ListH), rows.Count, (row, rr, selected, hot) =>
        {
            var p = rows[row];
            if (selected) c.Box(dc, rr, UiCanvas.FieldHot, -1, 4);
            else if (hot) c.Box(dc, rr, UiCanvas.Field, -1, 4);
            if (selected) c.Fill(dc, UiCanvas.R(rr.Left + 1, rr.Top + 6, 3, rr.Bottom - rr.Top - 12), UiCanvas.Accent);
            c.Txt(dc, p.Name, UiCanvas.R(rr.Left + 12, rr.Top + 3, UiCanvas.W(rr) - 64, 20), Win32.DT_LEFT | UiCanvas.Mid | Win32.DT_END_ELLIPSIS, UiCanvas.Text, selected ? c.FontSection : c.FontLabel);
            c.Txt(dc, p.Console, UiCanvas.R(rr.Right - 52, rr.Top + 3, 44, 20), Win32.DT_RIGHT | UiCanvas.Mid, UiCanvas.Accent, c.FontSection);
            c.Txt(dc, p.Category + "  -  " + p.Detail, UiCanvas.R(rr.Left + 12, rr.Top + 22, UiCanvas.W(rr) - 20, 16), Win32.DT_LEFT | UiCanvas.Mid | Win32.DT_END_ELLIPSIS, UiCanvas.Faint, c.FontSmall);
        });
        if (rows.Count == 0)
            c.Txt(dc, search.Text.Length > 0 || filter != 0 ? "No preset matches. Clear the search or pick All." : "No presets yet. BrokenNes2 provides none: send an instrument from the Instruments tab, or Import a file.",
                UiCanvas.R(ListX + 20, ListY + 60, ListW - 40, 120), Win32.DT_CENTER | Win32.DT_WORDBREAK, UiCanvas.Faint, c.FontLabel);
    }

    private void DrawDetails(nint dc)
    {
        var box = UiCanvas.R(DetailX, DetailY, DetailW, DetailH);
        c.Box(dc, box, UiCanvas.Panel, UiCanvas.Line, 6);
        if (details == null)
        {
            c.Txt(dc, rows.Count > 0 ? "Pick a preset to see its details." : "Nothing to show yet.", box, Win32.DT_CENTER | UiCanvas.Mid, UiCanvas.Faint, c.FontLabel);
            return;
        }
        var d = details;
        int x = box.Left + 16, w = DetailW - 32, y = box.Top + 12;
        c.Txt(dc, d.Name, UiCanvas.R(x, y, w - 70, 28), Win32.DT_LEFT | UiCanvas.Mid | Win32.DT_END_ELLIPSIS, UiCanvas.White, c.FontLead);
        c.Txt(dc, d.Console, UiCanvas.R(x + w - 64, y, 64, 28), Win32.DT_RIGHT | UiCanvas.Mid, UiCanvas.Accent, c.FontLead);
        y += 40;
        void Row(string k, string v, int color)
        {
            c.Txt(dc, k, UiCanvas.R(x, y, 110, 22), Win32.DT_LEFT | UiCanvas.Mid, UiCanvas.Dim, c.FontLabel);
            c.Txt(dc, v, UiCanvas.R(x + 116, y, w - 116, 22), Win32.DT_LEFT | UiCanvas.Mid | Win32.DT_END_ELLIPSIS, color, c.FontLabel);
            y += 26;
        }
        Row("Category", d.Category, UiCanvas.Text);
        Row("From", d.SourceGame.Length > 0 ? d.SourceGame : "unknown", UiCanvas.Text);
        Row("ROM SHA-1", d.SourceSha1.Length > 0 ? d.SourceSha1 + "  (reference only)" : "-", UiCanvas.Faint);
        Row("Tags", d.Tags.Length > 0 ? string.Join(", ", d.Tags) : "-", UiCanvas.Text);
        Row("Holds", $"{d.FieldCount} field{(d.FieldCount == 1 ? "" : "s")}, {d.BlobCount} sample{(d.BlobCount == 1 ? "" : "s")}" + (d.HasNoteBehaviour ? ", the game's note behaviour" : ""), UiCanvas.Text);
        Row("Stored", d.Origin == "library" ? "in the library on this computer" : d.Origin == "this project" ? "in this FL project" : d.Origin, UiCanvas.Text);
        y += 6;
        c.Ln(dc, x, y, x + w, y, UiCanvas.Line);
        y += 8;
        c.Txt(dc, d.Notes.Length > 0 ? d.Notes : "(no notes)", UiCanvas.R(x, y, w, box.Bottom - y - 10), Win32.DT_LEFT | Win32.DT_WORDBREAK | Win32.DT_END_ELLIPSIS, d.Notes.Length > 0 ? UiCanvas.Dim : UiCanvas.Faint, c.FontLabel);
    }

    private void DrawButtons(nint dc)
    {
        bool have = selectedId != null && details != null;
        if (renaming && have)
        {
            rename.Draw(c, dc, UiCanvas.R(DetailX, RenameY, DetailW - 150, 30));
            c.Button(dc, UiIds.RenameOk, "OK", UiCanvas.R(DetailX + DetailW - 144, RenameY, 70, 30), primary: true);
            c.Button(dc, UiIds.RenameCancel, "Cancel", UiCanvas.R(DetailX + DetailW - 70, RenameY, 70, 30));
        }
        int bw = (DetailW - 5 * 6) / 6;
        for (int i = 0; i < Buttons.Length; i++)
        {
            int id = UiIds.Load + i;
            bool needsSelection = i != 4;                     // Import needs none
            string label = i == 2 && deleteArmed != null && deleteArmed == selectedId ? "Really?" : Buttons[i];
            c.Button(dc, id, label, UiCanvas.R(DetailX + i * (bw + 6), ButtonsY, bw, 30), primary: i == 0 || (i == 2 && label == "Really?"), enabled: !needsSelection || have);
        }
    }

    // ---------------------------------------------------------------- events

    /// <summary>A press on control <paramref name="id"/>. Returns true when a drag started.</summary>
    public bool Down(int id, int x, int y, bool doubleClick)
    {
        var lib = UiServices.Presets;
        if (lib == null || message.Length > 0) return false;
        if (id != UiIds.Search && id != UiIds.RenameInput) { search.Blur(); if (id != UiIds.RenameOk && id != UiIds.RenameCancel) rename.Blur(); }
        if (id != UiIds.Delete) deleteArmed = null;

        if (list.OwnsBar(id)) { if (list.BarDown(y)) { drag = Drag.ListBar; return true; } return false; }
        int row = list.RowOf(id);
        if (row >= 0)
        {
            selectedId = rows[row].Id; renaming = false; rename.Blur();
            list.Selected = row;
            details = lib.Details(selectedId);
            return false;
        }
        if (id == UiIds.Search) { rename.Blur(); search.Click(x, doubleClick); return false; }
        if (id == UiIds.SearchClear) { if (search.Text.Length > 0) { search.Text = ""; list.Scroll = 0; } return false; }
        if (id == UiIds.RenameInput) { search.Blur(); rename.Click(x, doubleClick); return false; }
        if (id >= UiIds.Filter && id < UiIds.Filter + FilterNames.Length) { filter = id - UiIds.Filter; list.Scroll = 0; return false; }
        if (id == UiIds.RenameOk) { CommitRename(); return false; }
        if (id == UiIds.RenameCancel) { renaming = false; rename.Blur(); return false; }

        bool have = selectedId != null && details != null;
        switch (id)
        {
            case UiIds.Load: if (have) Act(lib.Load(selectedId!)); else NeedPreset(); break;
            case UiIds.Rename:
                if (!have) { NeedPreset(); break; }
                renaming = true; rename.Text = details!.Name; rename.Focus(selectAll: true);
                break;
            case UiIds.Delete:
                if (!have) { NeedPreset(); break; }
                if (deleteArmed == selectedId) { deleteArmed = null; Act(lib.Delete(selectedId!)); }
                else { deleteArmed = selectedId; deleteArmedTick = Environment.TickCount64; Act(ActionResult.Done($"Click Delete again to remove \"{details!.Name}\" from the library.")); }
                break;
            case UiIds.Export:
            {
                if (!have) { NeedPreset(); break; }
                string? path = ChooseFile?.Invoke(true, "Export a preset", "BrokenNes2 presets (*.bn2preset)|*.bn2preset|All files (*.*)|*.*");
                if (path == null) { Act(ActionResult.Done("Export cancelled.")); break; }
                Act(UiMutate.Has(UiMutate.PresetExportImport) ? lib.Import(path) : lib.Export(selectedId!, path));
                break;
            }
            case UiIds.Import:
            {
                string? path = ChooseFile?.Invoke(false, "Import a preset", "BrokenNes2 presets (*.bn2preset)|*.bn2preset|All files (*.*)|*.*");
                if (path == null) { Act(ActionResult.Done("Import cancelled.")); break; }
                Act(lib.Import(path));
                break;
            }
            case UiIds.Audition: if (have) Act(lib.Audition(selectedId!)); else NeedPreset(); break;
        }
        return false;
    }

    private void NeedPreset() => Act(ActionResult.Refused("Pick a preset in the list first."));

    private void CommitRename()
    {
        var lib = UiServices.Presets;
        if (lib == null || selectedId == null) return;
        Act(lib.Rename(selectedId, rename.Text));
        renaming = false; rename.Blur();
        cacheVersion = -1;
    }

    public void Move(int x, int y) { if (drag == Drag.ListBar) list.BarDrag(y); }
    public void Up() { drag = Drag.None; list.BarUp(); }

    public bool Wheel(int delta, int x, int y) => UiCanvas.In(list.Rect, x, y) && list.Wheel(delta);

    public bool Char(char ch) => search.Char(ch) || rename.Char(ch);
    public bool Key(int vk) => search.Key(vk) || rename.Key(vk);
}
