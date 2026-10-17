// The Instruments tab: the instruments of the frozen game, the fields of the selected one, its blobs (waveform and hex), and the corruption tools.
// Everything it shows comes from an IInstrumentModelProvider (Contracts.cs); when Instrument Runaway is off, no game is loaded or no model is
// connected the tab says so instead. Immediate mode like the rest of the editor: every paint lays the panel out again and registers its controls; the
// editor routes mouse and key events here by control id.
using BrokenNes.Fruity;

namespace BrokenNes2.Ui;

internal sealed class InstrumentsPanel
{
    // layout (absolute window coordinates; see UiLayout)
    private const int ListX = UiLayout.PanelX, ListY = 92, ListW = 200, ListH = 530;
    private const int DetailX = ListX + ListW + 14, DetailW = UiLayout.PanelRight - DetailX;
    private const int TitleY = 92, FieldsY = 118, FieldRowH = 30, FieldRows = 7, ExplainY = 330, BlobY = 354, WaveY = 376, WaveH = 60, HexY = 440, HexH = 64, ToolY = 510, StatusY = 608;
    private const int NameW = 132, ControlX = 138, ControlW = 246, LockX = DetailW - 14 - 24;

    private enum Drag { None, ListBar, FieldsBar, HexBar, Slider, Strength }

    private readonly UiCanvas c;
    private readonly ScrollList list = new(UiIds.InstList, UiIds.InstListBar) { RowH = 28 };
    private readonly VScrollBar fieldBar = new();
    private readonly Slider[] sliders = new Slider[UiIds.MaxFieldRows];
    private readonly Stepper[] steppers = new Stepper[UiIds.MaxFieldRows];
    private readonly BitToggle[] bits = new BitToggle[UiIds.MaxFieldRows];
    private readonly Slider strengthSlider = new(UiIds.Strength);
    private readonly WaveView wave = new(UiIds.Wave);
    private readonly HexField hex = new(UiIds.Hex, UiIds.HexBar);

    private IInstrumentModelProvider? provider;
    private InstrumentProviderFactory? factoryUsed;
    private bool factoryTried;
    private long cacheVersion = -1;
    private IReadOnlyList<InstrumentSummary> summaries = [];
    private IReadOnlyList<InstrumentField> fields = [];
    private IReadOnlyList<InstrumentBlob> blobs = [];
    private int cacheInstrument = -1;
    private string? selField;
    private int blobIndex, strength = 35, dragSlot = -1;
    private Drag drag;
    private string status = "", message = "";
    private bool statusOk = true;
    private InstrumentBlob? hexBlob;

    public InstrumentsPanel(UiCanvas canvas)
    {
        c = canvas;
        for (int r = 0; r < UiIds.MaxFieldRows; r++)
        {
            sliders[r] = new Slider(UiIds.FieldBase + r * UiIds.FieldStride + UiIds.PartControl);
            steppers[r] = new Stepper(UiIds.FieldBase + r * UiIds.FieldStride + UiIds.PartMinus);
            bits[r] = new BitToggle(UiIds.FieldBits + r * 16);
        }
        hex.Edited = (offset, value) =>
        {
            if (provider != null && hexBlob != null && list.Selected >= 0) Act(provider.SetBlobByte(list.Selected, hexBlob.Id, offset, value));
        };
    }

    /// <summary>The message shown instead of the instruments right now ("" when the instruments are shown).</summary>
    public string Message => message;
    public string Status => status;
    public bool StatusOk => statusOk;
    public bool WantsKeys => hex.Focused;
    public int SelectedInstrument => list.Selected;
    public int Strength => strength;

    public long Signature() => (provider?.Version ?? -1) * 7 + list.Scroll * 131 + list.Selected * 17 + fieldBar.Pos * 1009 + blobIndex * 31 + strength * 3 + (selField?.GetHashCode() ?? 0)
        + hex.Signature() + status.GetHashCode() + (int)drag + message.GetHashCode();

    private void Act(ActionResult r) { status = r.Message; statusOk = r.Ok; }

    public void Blur() { hex.Blur(); }

    /// <summary>The tab went away or the game did: let go of everything (a drag, the keyboard).</summary>
    public void Cancel()
    {
        drag = Drag.None; list.BarUp(); fieldBar.Up(); hex.BarUp(); hex.Blur();
    }

    // ---------------------------------------------------------------- drawing

    public void Draw(nint dc, Emulator? emu)
    {
        string? gate = emu == null ? UiServices.NoEmulator : !emu.CanRunaway ? UiServices.NeedGame : !emu.Runaway ? UiServices.NeedRunaway : null;
        string note = gate != null ? "works with Instrument Runaway" : provider is { Unavailable: null } ? $"{summaries.Count} instrument{(summaries.Count == 1 ? "" : "s")} of the frozen game" : "";
        if (gate != null) { provider = null; factoryTried = false; cacheVersion = -1; }
        var factory = UiServices.Instruments;
        if (gate == null && (provider == null && (!factoryTried || !ReferenceEquals(factory, factoryUsed))))
        {
            factoryTried = true; factoryUsed = factory;
            provider = factory?.Invoke(emu!);
            cacheVersion = -1; cacheInstrument = -1; selField = null; blobIndex = 0; list.Selected = provider == null ? -1 : 0; list.Scroll = 0; fieldBar.Pos = 0;
        }
        c.Section(dc, "INSTRUMENTS", note, UiLayout.PanelX, UiLayout.PanelY, UiLayout.PanelW);

        message = gate ?? (provider == null ? UiServices.NoInstrumentModel : provider.Unavailable ?? "");
        if (message.Length > 0)
        {
            DrawMessage(dc, message, gate == UiServices.NeedRunaway
                ? "Instrument Runaway freezes the game and keeps its sounds. The button is at the bottom left of the window; the game's instruments appear here as soon as it is on."
                : gate != null ? "" : provider == null ? "A model that reads the game's instruments has to be connected to this tab; this build has none yet." : "");
            return;
        }
        Sync();
        if (summaries.Count == 0)
        {
            DrawMessage(dc, "This game has no instruments BrokenNes2 can read", "The game's sound memory holds nothing the instrument model recognises.");
            return;
        }
        DrawList(dc);
        if (list.Selected < 0) return;
        var inst = summaries[Math.Clamp(list.Selected, 0, summaries.Count - 1)];
        c.Txt(dc, inst.Name, UiCanvas.R(DetailX, TitleY, DetailW / 2, 24), Win32.DT_LEFT | UiCanvas.Mid, UiCanvas.White, c.FontLead);
        c.Txt(dc, inst.Detail, UiCanvas.R(DetailX + DetailW / 2, TitleY, DetailW / 2, 24), Win32.DT_RIGHT | UiCanvas.Mid | Win32.DT_END_ELLIPSIS, UiCanvas.Dim, c.FontSmall);
        DrawFields(dc);
        DrawBlobs(dc);
        DrawTools(dc);
        DrawStatus(dc);
    }

    private void DrawMessage(nint dc, string text, string more)
    {
        var box = UiCanvas.R(UiLayout.PanelX, 110, UiLayout.PanelW, 360);
        c.Box(dc, box, UiCanvas.Panel, UiCanvas.Line, 8);
        c.Txt(dc, text, UiCanvas.R(box.Left + 40, box.Top + 110, UiCanvas.W(box) - 80, 70), Win32.DT_CENTER | Win32.DT_WORDBREAK | Win32.DT_VCENTER, UiCanvas.Text, c.FontLead);
        if (more.Length > 0) c.Txt(dc, more, UiCanvas.R(box.Left + 80, box.Top + 190, UiCanvas.W(box) - 160, 70), Win32.DT_CENTER | Win32.DT_WORDBREAK, UiCanvas.Dim, c.FontLabel);
    }

    /// <summary>Re-reads what the model holds when its Version or the selection changed.</summary>
    private void Sync()
    {
        var p = provider!;
        long v = p.Version;
        if (v != cacheVersion || list.Selected != cacheInstrument)
        {
            if (v != cacheVersion) summaries = p.Instruments;
            if (list.Selected >= summaries.Count) list.Selected = summaries.Count - 1;
            if (list.Selected < 0 && summaries.Count > 0) list.Selected = 0;
            cacheVersion = v; cacheInstrument = list.Selected;
            fields = list.Selected >= 0 ? p.Fields(list.Selected) : [];
            blobs = list.Selected >= 0 ? p.Blobs(list.Selected) : [];
            if (selField != null && !fields.Any(f => f.Id == selField)) selField = null;
            blobIndex = Math.Clamp(blobIndex, 0, Math.Max(0, blobs.Count - 1));
            hexBlob = blobs.Count > 0 ? blobs[blobIndex] : null;
            hex.SetData(hexBlob?.Bytes ?? []);
            hex.Editable = hexBlob?.Editable == true;
        }
    }

    private void DrawList(nint dc)
    {
        list.Draw(c, dc, UiCanvas.R(ListX, ListY, ListW, ListH), summaries.Count, (row, rr, selected, hot) =>
        {
            var s = summaries[row];
            if (selected) c.Box(dc, rr, UiCanvas.FieldHot, -1, 4);
            else if (hot) c.Box(dc, rr, UiCanvas.Field, -1, 4);
            if (selected) c.Fill(dc, UiCanvas.R(rr.Left + 1, rr.Top + 5, 3, rr.Bottom - rr.Top - 10), UiCanvas.Accent);
            if (s.Sounding) c.Dot(dc, rr.Right - 12, rr.Top + 14, 3, UiCanvas.Good);
            c.Txt(dc, s.Name, UiCanvas.R(rr.Left + 12, rr.Top, UiCanvas.W(rr) - 30, rr.Bottom - rr.Top), Win32.DT_LEFT | UiCanvas.Mid | Win32.DT_END_ELLIPSIS, selected || hot ? UiCanvas.Text : Win32.Rgb(200, 204, 216), selected ? c.FontSection : c.FontLabel);
        });
        c.Txt(dc, $"{summaries.Count} instruments", UiCanvas.R(ListX, ListY + ListH + 6, ListW, 16), Win32.DT_LEFT | Win32.DT_SINGLELINE, UiCanvas.Faint, c.FontSmall);
    }

    private int HoveredFieldRow()
    {
        int hot = c.Hot;
        if (hot >= UiIds.FieldBase && hot < UiIds.FieldBase + UiIds.MaxFieldRows * UiIds.FieldStride) return (hot - UiIds.FieldBase) / UiIds.FieldStride;
        if (hot >= UiIds.FieldBits && hot < UiIds.FieldBits + UiIds.MaxFieldRows * 16) return (hot - UiIds.FieldBits) / 16;
        return -1;
    }

    private void DrawFields(nint dc)
    {
        var area = UiCanvas.R(DetailX, FieldsY, DetailW, FieldRows * FieldRowH);
        fieldBar.Layout(UiCanvas.R(area.Right - VScrollBar.Width, area.Top, VScrollBar.Width, UiCanvas.H(area)), fields.Count, FieldRows);
        c.Box(dc, area, UiCanvas.Panel, UiCanvas.Line, 6);
        int hoverRow = HoveredFieldRow();
        string? explain = null;
        for (int r = 0; r < FieldRows; r++)
        {
            int idx = fieldBar.Pos + r;
            if (idx >= fields.Count) break;
            var f = fields[idx];
            int y = FieldsY + r * FieldRowH, x0 = DetailX + 4;
            bool selected = f.Id == selField;
            if (selected) c.Box(dc, UiCanvas.R(DetailX + 3, y + 1, DetailW - 6 - (fieldBar.Needed ? VScrollBar.Width + 2 : 0), FieldRowH - 2), UiCanvas.FieldHot, -1, 4);
            var nameRect = UiCanvas.R(x0, y, NameW, FieldRowH);
            c.Txt(dc, f.Name, UiCanvas.R(x0 + 6, y, NameW - 8, FieldRowH), Win32.DT_LEFT | UiCanvas.Mid | Win32.DT_END_ELLIPSIS, f.Locked ? UiCanvas.Faint : selected || hoverRow == r ? UiCanvas.White : UiCanvas.Text, c.FontLabel);
            c.AddHit(UiIds.FieldBase + r * UiIds.FieldStride + UiIds.PartSelect, nameRect);
            if (hoverRow == r || (hoverRow < 0 && selected)) explain = (f.Group != null ? f.Group + ": " : "") + f.Explanation;
            DrawFieldControl(dc, r, f, x0 + ControlX - 4, y + 3, FieldRowH - 6);
            DrawLock(dc, UiIds.FieldBase + r * UiIds.FieldStride + UiIds.PartLock, UiCanvas.R(DetailX + LockX, y + 5, 24, FieldRowH - 10), f.Locked);
        }
        if (fields.Count == 0) c.Txt(dc, "This instrument has no fields", area, Win32.DT_CENTER | UiCanvas.Mid, UiCanvas.Faint, c.FontLabel);
        if (fieldBar.Needed)
        {
            fieldBar.Draw(c, dc, c.IsHot(UiIds.FieldsBar));
            c.AddHit(UiIds.FieldsBar, UiCanvas.R(fieldBar.Track.Left - 2, fieldBar.Track.Top, VScrollBar.Width + 4, UiCanvas.H(fieldBar.Track)));
        }
        c.Txt(dc, explain ?? "Hover a field to see what it does; click its name to choose it for Nudge.", UiCanvas.R(DetailX, ExplainY, DetailW, 18), Win32.DT_LEFT | UiCanvas.Mid | Win32.DT_END_ELLIPSIS, explain != null ? UiCanvas.Dim : UiCanvas.Faint, c.FontSmall);
    }

    private void DrawFieldControl(nint dc, int r, InstrumentField f, int x, int y, int h)
    {
        // A locked field can still be edited by hand: the lock only protects it from Randomise and Nudge.
        var area = UiCanvas.R(x, y, ControlW, h);
        switch (f.Kind)
        {
            case FieldKind.Range:
            case FieldKind.Signed:
                steppers[r].Draw(c, dc, area, null, f.Value > f.Min, f.Value < f.Max);          // [-]   [+] at the ends,
                sliders[r].Draw(c, dc, UiCanvas.R(x + Stepper.ButtonW + 4, y, ControlW - 2 * (Stepper.ButtonW + 4), h), f.Value, f.Min, f.Max, true, f.Kind == FieldKind.Signed);   // the slider between
                break;
            case FieldKind.Enum:
                steppers[r].Draw(c, dc, area, f.Format(f.Value), f.Value > f.Min, f.Value < f.Max);
                c.AddHit(sliders[r].Id, UiCanvas.R(x + Stepper.ButtonW + 4, y, ControlW - 2 * (Stepper.ButtonW + 4), h));    // a click on the name steps forward, wrapping
                break;
            case FieldKind.BitField:
                bits[r].Draw(c, dc, x, y + 2, h - 4, f.Value, f.Bits);
                break;
        }
        string text = f.Kind == FieldKind.Enum ? $"= {f.Value}" : f.Format(f.Value);
        int tx = f.Kind == FieldKind.BitField ? x + BitToggle.WidthOf(f.Bits) + 6 : x + ControlW + 8;
        c.Txt(dc, text, UiCanvas.R(tx, y, Math.Max(40, DetailX + LockX - tx - 4), h), Win32.DT_LEFT | UiCanvas.Mid, f.Locked ? UiCanvas.Faint : UiCanvas.Text, c.FontLabel);
    }

    /// <summary>A small padlock: shut and in the accent colour when the field is locked against the corruption tools, open and faint when not.</summary>
    private void DrawLock(nint dc, int id, RECT r, bool locked)
    {
        bool hot = c.Hot == id;
        int cx = (r.Left + r.Right) / 2, col = locked ? UiCanvas.Accent : hot ? UiCanvas.Dim : UiCanvas.Faint;
        var body = UiCanvas.R(cx - 6, r.Top + 8, 13, 9);
        c.Box(dc, body, locked ? UiCanvas.AccentDim : UiCanvas.Field, col, 2);
        // the shackle: closed over the body when locked, lifted and offset when open
        int sx = locked ? cx - 3 : cx + 1, top = locked ? r.Top + 2 : r.Top;
        c.Ln(dc, sx, r.Top + 8, sx, top + 3, col); c.Ln(dc, sx, top + 3, sx + 4, top + 3, col); c.Ln(dc, sx + 4, top + 3, sx + 4, locked ? r.Top + 8 : r.Top + 5, col);
        c.AddHit(id, UiCanvas.R(r.Left - 2, r.Top - 2, UiCanvas.W(r) + 4, UiCanvas.H(r) + 4));
    }

    private void DrawBlobs(nint dc)
    {
        // header: chips for each blob (when there are several), or its caption; the lock check on the right
        int x = DetailX;
        if (blobs.Count == 0)
        {
            c.Txt(dc, "This instrument has no sample data", UiCanvas.R(x, BlobY, DetailW, 20), Win32.DT_LEFT | UiCanvas.Mid, UiCanvas.Faint, c.FontSmall);
            c.Box(dc, UiCanvas.R(DetailX, WaveY, DetailW, WaveH), UiCanvas.Black, UiCanvas.Line, 5);
            c.AddHit(UiIds.Wave, UiCanvas.R(DetailX, WaveY, DetailW, WaveH));
            c.Box(dc, UiCanvas.R(DetailX, HexY, DetailW, HexH), UiCanvas.Bg, UiCanvas.Line, 5);
            return;
        }
        if (blobs.Count > 1)
            for (int i = 0; i < blobs.Count && i < 8; i++)
            {
                var chip = UiCanvas.R(x, BlobY, 96, 20);
                bool on = i == blobIndex, hot = c.Hot == UiIds.BlobChip + i;
                c.Box(dc, chip, on ? UiCanvas.Accent : hot ? UiCanvas.FieldHot : UiCanvas.Field, on ? UiCanvas.Accent : UiCanvas.Line, 5);
                c.Txt(dc, blobs[i].Name, chip, Win32.DT_CENTER | UiCanvas.Mid | Win32.DT_END_ELLIPSIS, on ? UiCanvas.White : UiCanvas.Dim, c.FontSmall);
                c.AddHit(UiIds.BlobChip + i, chip);
                x += 100;
            }
        var b = blobs[Math.Clamp(blobIndex, 0, blobs.Count - 1)];
        string caption = $"{b.Name}: {b.Kind}, {b.Bytes.Length} byte{(b.Bytes.Length == 1 ? "" : "s")}" + (b.Samples.Length > 0 ? $", {b.Samples.Length} samples at {b.SampleRate} Hz" : "") + (b.LoopStart >= 0 ? $", loop {b.LoopStart}-{b.LoopEnd}" : ", no loop");
        c.Txt(dc, caption, UiCanvas.R(x + 6, BlobY, DetailX + DetailW - x - 156, 20), Win32.DT_LEFT | UiCanvas.Mid | Win32.DT_END_ELLIPSIS, UiCanvas.Dim, c.FontSmall);
        c.Check(dc, UiIds.LockBlob, "Lock sample data", DetailX + DetailW - 150, BlobY + 1, 112, b.Locked);
        wave.Draw(c, dc, UiCanvas.R(DetailX, WaveY, DetailW, WaveH), b.Samples, b.LoopStart, b.LoopEnd, b.Samples.Length == 0 ? "BrokenNes2 cannot draw this data as a waveform" : null);
        hex.Draw(c, dc, UiCanvas.R(DetailX, HexY, DetailW, HexH));
    }

    private void DrawTools(nint dc)
    {
        var p = provider!;
        int inst = list.Selected, x = DetailX;
        // row 1: strength, randomise, nudge
        int y = ToolY;
        c.Txt(dc, "Strength", UiCanvas.R(x, y, 60, 28), Win32.DT_LEFT | UiCanvas.Mid, UiCanvas.Dim, c.FontLabel);
        strengthSlider.Draw(c, dc, UiCanvas.R(x + 60, y, 130, 28), strength, 0, 100);
        c.Txt(dc, strength + "%", UiCanvas.R(x + 192, y, 44, 28), Win32.DT_LEFT | UiCanvas.Mid, UiCanvas.Text, c.FontLabel);
        c.Button(dc, UiIds.Randomise, "Randomise", UiCanvas.R(x + 242, y, 100, 28), primary: true);
        string nudgeTarget = selField != null ? fields.FirstOrDefault(f => f.Id == selField)?.Name ?? "" : "";
        c.Button(dc, UiIds.NudgeDown, "Nudge -", UiCanvas.R(x + 348, y, 90, 28));
        c.Button(dc, UiIds.NudgeUp, "Nudge +", UiCanvas.R(x + 444, y, 90, 28));
        // row 2: locks, history, seed
        y += 32;
        c.Button(dc, UiIds.LockAll, "Lock all", UiCanvas.R(x, y, 84, 28));
        c.Button(dc, UiIds.UnlockAll, "Unlock all", UiCanvas.R(x + 90, y, 92, 28));
        c.Button(dc, UiIds.Undo, "Undo", UiCanvas.R(x + 196, y, 66, 28), enabled: inst >= 0 && p.CanUndo(inst));
        c.Button(dc, UiIds.Redo, "Redo", UiCanvas.R(x + 268, y, 66, 28), enabled: inst >= 0 && p.CanRedo(inst));
        string hist = inst >= 0 ? p.HistoryText(inst) : "", seed = inst >= 0 ? p.SeedText(inst) : "";
        c.Txt(dc, hist.Length > 0 ? hist : "no history yet", UiCanvas.R(x + 344, y, 80, 28), Win32.DT_LEFT | UiCanvas.Mid, UiCanvas.Faint, c.FontSmall);
        c.Txt(dc, seed.Length > 0 ? "seed " + seed : "", UiCanvas.R(x + 424, y, DetailW - 424, 28), Win32.DT_RIGHT | UiCanvas.Mid | Win32.DT_END_ELLIPSIS, UiCanvas.Dim, c.FontMono);
        // row 3: to the presets, into the game
        y += 32;
        c.Button(dc, UiIds.SendToPresets, "Send to Presets", UiCanvas.R(x, y, 170, 28));
        c.Button(dc, UiIds.Push, "Push into the game", UiCanvas.R(x + 176, y, 190, 28), primary: true);
        c.Txt(dc, nudgeTarget.Length > 0 ? "Nudge acts on: " + nudgeTarget : "Nudge picks a field (click a field name to choose one)", UiCanvas.R(x + 376, y, DetailW - 376, 28), Win32.DT_LEFT | UiCanvas.Mid | Win32.DT_END_ELLIPSIS, UiCanvas.Faint, c.FontSmall);
    }

    private void DrawStatus(nint dc)
    {
        if (status.Length == 0) return;
        c.Fill(dc, UiCanvas.R(DetailX, StatusY, 3, 36), statusOk ? UiCanvas.Good : UiCanvas.Warn);
        c.Txt(dc, status, UiCanvas.R(DetailX + 12, StatusY, DetailW - 12, 40), Win32.DT_LEFT | Win32.DT_WORDBREAK | Win32.DT_VCENTER | Win32.DT_END_ELLIPSIS, UiCanvas.Text, c.FontSmall);
    }

    // ---------------------------------------------------------------- events

    /// <summary>A press on control <paramref name="id"/> at (x, y). Returns true when a drag started (the editor then captures the mouse).</summary>
    public bool Down(int id, int x, int y, bool shift, bool doubleClick)
    {
        var p = provider;
        if (p == null || message.Length > 0) return false;
        int inst = list.Selected;
        hex.Blur();

        if (list.OwnsBar(id)) { if (list.BarDown(y)) { drag = Drag.ListBar; return true; } return false; }
        int row = list.RowOf(id);
        if (row >= 0)
        {
            if (UiMutate.Has(UiMutate.RowOffByOne)) row = Math.Min(row + 1, summaries.Count - 1);
            list.Selected = row; blobIndex = 0; selField = null; fieldBar.Pos = 0;
            return false;
        }
        if (id == UiIds.FieldsBar) { if (fieldBar.Down(y)) { drag = Drag.FieldsBar; return true; } return false; }
        if (id == UiIds.HexBar) { if (hex.BarDown(y)) { drag = Drag.HexBar; return true; } return false; }
        if (id == UiIds.Hex) { hex.Click(x, y); return false; }
        if (id >= UiIds.BlobChip && id < UiIds.BlobChip + 8) { blobIndex = Math.Min(id - UiIds.BlobChip, Math.Max(0, blobs.Count - 1)); cacheInstrument = -1; return false; }
        if (id == UiIds.LockBlob && hexBlob != null) { Act(p.SetBlobLocked(inst, hexBlob.Id, !hexBlob.Locked)); return false; }

        if (id >= UiIds.FieldBase && id < UiIds.FieldBase + UiIds.MaxFieldRows * UiIds.FieldStride)
        {
            int r = (id - UiIds.FieldBase) / UiIds.FieldStride, part = (id - UiIds.FieldBase) % UiIds.FieldStride, idx = fieldBar.Pos + r;
            if (idx >= fields.Count) return false;
            var f = fields[idx];
            switch (part)
            {
                case UiIds.PartSelect: selField = selField == f.Id ? null : f.Id; return false;
                case UiIds.PartLock: Act(p.SetFieldLocked(inst, f.Id, !f.Locked)); return false;
                case UiIds.PartControl:
                    if (f.Kind == FieldKind.Enum) { SetField(f, f.Value >= f.Max ? f.Min : f.Value + 1); return false; }
                    dragSlot = r; drag = Drag.Slider; SliderTo(r, f, x);
                    return true;
                case UiIds.PartMinus:
                case UiIds.PartPlus:
                {
                    int step = steppers[r].StepOf(id, shift && f.Max - f.Min > 31);
                    if (step != 0) SetField(f, Math.Clamp(f.Value + step, f.Min, f.Max));
                    return false;
                }
            }
            return false;
        }
        if (id >= UiIds.FieldBits && id < UiIds.FieldBits + UiIds.MaxFieldRows * 16)
        {
            int r = (id - UiIds.FieldBits) / 16, bit = (id - UiIds.FieldBits) % 16, idx = fieldBar.Pos + r;
            if (idx < fields.Count) SetField(fields[idx], BitToggle.Flip(fields[idx].Value, bit));
            return false;
        }

        switch (id)
        {
            case UiIds.Strength: drag = Drag.Strength; strength = strengthSlider.ValueAt(x); return true;
            case UiIds.Randomise: Act(p.Randomise(inst, strength / 100.0)); break;
            case UiIds.NudgeDown: Act(p.Nudge(inst, selField, -1)); break;
            case UiIds.NudgeUp: Act(p.Nudge(inst, selField, +1)); break;
            case UiIds.LockAll: LockAll(true); break;
            case UiIds.UnlockAll: LockAll(false); break;
            case UiIds.Undo: Act(UiMutate.Has(UiMutate.UndoRedoSwapped) ? (p.CanRedo(inst) ? p.Redo(inst) : ActionResult.Refused("nothing to redo")) : p.CanUndo(inst) ? p.Undo(inst) : ActionResult.Refused("nothing to undo")); break;
            case UiIds.Redo: Act(p.CanRedo(inst) ? p.Redo(inst) : ActionResult.Refused("nothing to redo")); break;
            case UiIds.SendToPresets: SendToPresets(p, inst); break;
            case UiIds.Push: Act(p.PushIntoGame(inst)); break;
        }
        return false;
    }

    private void SetField(InstrumentField f, int value)
    {
        if (value == f.Value) return;
        Act(provider!.SetField(list.Selected, f.Id, value));
    }

    private void SliderTo(int r, InstrumentField f, int x)
    {
        int idx = fieldBar.Pos + r;
        if (idx >= fields.Count) return;
        int v = Math.Clamp(sliders[r].ValueAt(x), f.Min, f.Max);
        SetField(fields[idx], v);
    }

    private void LockAll(bool locked)
    {
        var p = provider!;
        int n = 0;
        foreach (var f in fields) if (f.Locked != locked) { p.SetFieldLocked(list.Selected, f.Id, locked); n++; }
        foreach (var b in blobs) if (b.Locked != locked) { p.SetBlobLocked(list.Selected, b.Id, locked); n++; }
        Act(ActionResult.Done(locked ? $"locked {n} item{(n == 1 ? "" : "s")}: the corruption tools leave them alone" : $"unlocked {n} item{(n == 1 ? "" : "s")}"));
    }

    private void SendToPresets(IInstrumentModelProvider p, int inst)
    {
        var (draft, refusal) = p.TakeForPreset(inst);
        if (draft == null) { Act(ActionResult.Refused(refusal ?? "the model could not take this instrument")); return; }
        var lib = UiServices.Presets;
        if (lib == null) { Act(ActionResult.Refused(UiServices.NoPresetLibrary)); return; }
        Act(lib.Add(draft));
    }

    public void Move(int x, int y)
    {
        switch (drag)
        {
            case Drag.ListBar: list.BarDrag(y); break;
            case Drag.FieldsBar: if (!UiMutate.Has(UiMutate.FieldsNoScroll)) fieldBar.Drag(y); break;
            case Drag.HexBar: hex.BarDrag(y); break;
            case Drag.Strength: strength = strengthSlider.ValueAt(x); break;
            case Drag.Slider:
                if (dragSlot >= 0 && provider != null) { int idx = fieldBar.Pos + dragSlot; if (idx < fields.Count) SliderTo(dragSlot, fields[idx], x); }
                break;
        }
    }

    public void Up()
    {
        drag = Drag.None; dragSlot = -1;
        list.BarUp(); fieldBar.Up(); hex.BarUp();
    }

    public bool Wheel(int delta, int x, int y)
    {
        if (provider == null || message.Length > 0) return false;
        if (UiCanvas.In(list.Rect, x, y)) return list.Wheel(delta);
        if (y >= FieldsY && y < FieldsY + FieldRows * FieldRowH && x >= DetailX && fieldBar.Needed)
        {
            if (UiMutate.Has(UiMutate.FieldsNoScroll) || UiMutate.Has(UiMutate.ListWheelDead)) return false;
            int before = fieldBar.Pos;
            fieldBar.Pos = Math.Clamp(fieldBar.Pos - delta, 0, fieldBar.MaxPos);
            return fieldBar.Pos != before;
        }
        if (UiCanvas.In(hex.Rect, x, y)) return hex.Wheel(delta);
        return false;
    }

    public bool Char(char ch) => hex.Char(ch);
    public bool Key(int vk) => hex.Key(vk);

    // ---- what the self-test reads ----
    public int FieldFirstRow => fieldBar.Pos;
    public int FieldCount => fields.Count;
    public int ListFirstRow => list.Scroll;
    public int ListCount => summaries.Count;
    public int HexSelected => hex.Selected;
    public int HexFirstRow => hex.FirstRow;
    public int BlobIndex => blobIndex;
    public int WaveColumns => wave.DrawnColumns;
}
