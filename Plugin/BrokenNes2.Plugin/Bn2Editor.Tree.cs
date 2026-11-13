// Layout B (2026-11-13): Instrument Runaway's list as a tree in the Game view's column (the model: RunawayTree.cs).
//   a group header (Samples / Drums / Game voices / Sound effects, with its count and the duplicates folded into it) folds and unfolds the group;
//   an instrument row: a play button (auditions it once over this instance's output), its waveform, its name (the user's, else the default), "x N" when duplicates were folded;
//   clicking a row picks it for this channel (as the Instrument dropdown does); "Rename" types a new name for the picked row (Enter keeps it, Esc gives up), saved in the project.
// Control ids 2250 (Rename), 2260 + visible row (the row), 2280 + visible row (its play button); editor test hooks 895 - 899.
using BrokenNes.Fruity;
using BrokenNes2.Ui;

namespace BrokenNes2;

public sealed unsafe partial class Bn2Editor
{
    public const int IdTreeRename = 2250, IdTreeRow = 2260, IdTreePlay = 2280;
    private const int TreeRows = 20, TreeRowH = 22;

    private List<TreeItem> treeItems = [];
    private (object? Snap, int Recipes, int Mask, bool On) treeBuiltFor;
    private readonly bool[] treeFolded = new bool[4];
    private int treeScroll, treeSelected = -1;           // treeSelected: index into treeItems of the row picked last (the one Rename names)
    private readonly List<(bool Header, int Index)> treeVisible = new();   // what each drawn row is: a group header (Index = group) or an item (Index into treeItems)
    private string treeMessage = "";
    private bool treeMessageOk = true;
    private bool treeTyping;
    private string treeText = "";

    private bool TreeTyping => treeTyping;
    private static bool IsTreeId(int id) => id == IdTreeRename || id is >= IdTreeRow and < IdTreePlay + TreeRows;

    private string TreeNameOr(Emulator? emu, string key, string fallback) => emu?.RunawayName(key) ?? fallback;

    private void TreeRebuild(Emulator emu)
    {
        object? snap = (object?)emu.Snapshot ?? (object?)emu.SegaState ?? emu.WmsState;
        int recipes = emu.RecipeCount;
        var key = (snap, recipes, UiMutate.Mask, emu.Runaway);
        if (key == treeBuiltFor) return;
        treeBuiltFor = key;
        treeItems = RunawayTree.Build(emu);
        treeSelected = -1;
        treeScroll = 0;
        if (treeTyping) { treeTyping = false; UiReleaseFocus(); }
    }

    /// <summary>The value the Instrument / Recipe parameters hold now, in the tree's terms (as the Instrument dropdown's current value).</summary>
    private int TreeCurrent(Emulator emu)
    {
        int rp = plugin.Get(P.Recipe), ins = plugin.Get(P.Instrument);
        if (emu.SamplerMode && emu.Snapshot is { } s) return rp > 0 && rp <= s.Recipes.Length ? RecipeValue + rp - 1 : s.Resolve(ins);
        if (emu.SegaSamplerMode && emu.SegaState is { } sn) return rp > 0 && rp <= sn.Recipes.Length ? RecipeValue + rp - 1 : ins == 0 ? sn.Resolve(0) + 1 : ins;
        if (emu.WmsSamplerMode && emu.WmsState is { } ws) return ws.Resolve(ins);
        return -1;
    }

    /// <summary>Draws the tree in the column from (<paramref name="x"/>, <paramref name="y"/>); false when the frozen game has nothing the tree shows (Game Boy channels, a NES game), so the column shows the game list.</summary>
    private bool DrawRunawayTree(nint dc, Emulator emu, int x, int y, int w, int h)
    {
        TreeRebuild(emu);
        if (treeItems.Count == 0) { treeVisible.Clear(); return false; }
        int folded = treeItems.Sum(i => i.Count - 1);
        Section(dc, "INSTRUMENTS", folded > 0 ? $"{treeItems.Count} ({folded} duplicates folded)" : $"{treeItems.Count}", x, y, w);
        y += 24;
        // the rows: each group's header, then its instruments unless folded
        var all = new List<(bool Header, int Index)>();
        for (int g = 0; g < 4; g++)
        {
            var members = Enumerable.Range(0, treeItems.Count).Where(i => (int)treeItems[i].Group == g).ToList();
            if (members.Count == 0) continue;
            all.Add((true, g));
            if (!treeFolded[g]) foreach (int i in members) all.Add((false, i));
        }
        int bottomBar = 30;
        int visible = Math.Min(TreeRows, Math.Max(1, (h - bottomBar - 4) / TreeRowH));
        treeScroll = Math.Clamp(treeScroll, 0, Math.Max(0, all.Count - visible));
        treeVisible.Clear();
        int current = TreeCurrent(emu);
        for (int r = 0; r < visible && treeScroll + r < all.Count; r++)
        {
            var (isHeader, index) = all[treeScroll + r];
            treeVisible.Add((isHeader, index));
            var row = R(x, y + r * TreeRowH, w - (all.Count > visible ? 8 : 0), TreeRowH - 2);
            if (isHeader)
            {
                int n = treeItems.Count(i => (int)i.Group == index), dups = treeItems.Where(i => (int)i.Group == index).Sum(i => i.Count - 1);
                bool isHot = hot == IdTreeRow + r;
                if (isHot) Box(dc, row, FieldHot, -1, 4);
                Txt(dc, (treeFolded[index] ? "+  " : "-  ") + RunawayTree.GroupNames[index], R(row.Left + 4, row.Top, w - 70, TreeRowH - 2), Win32.DT_LEFT | Mid, Text, fontSection);
                Txt(dc, dups > 0 ? $"{n}  (+{dups})" : $"{n}", R(row.Right - 70, row.Top, 66, TreeRowH - 2), Win32.DT_RIGHT | Mid, Faint, fontSmall);
                AddHit(IdTreeRow + r, row, Kind.Button);
                continue;
            }
            var it = treeItems[index];
            bool on = it.Value == current, sel = index == treeSelected, hotRow = hot == IdTreeRow + r;
            Box(dc, row, hotRow ? FieldHot : Field, on ? Accent : sel ? Dim : Line, 4);
            // the play button
            var play = R(row.Left + 3, row.Top + 2, 16, TreeRowH - 6);
            bool hotPlay = hot == IdTreePlay + r;
            Triangle(dc, play, hotPlay ? White : Good);
            AddHit(IdTreePlay + r, play, Kind.Button);
            // the waveform (or a glyph of what it is)
            var wave = R(row.Left + 22, row.Top + 2, RunawayTree.WaveCols, TreeRowH - 6);
            DrawThumb(dc, it, wave);
            // the name, the fold count
            int nameX = wave.Right + 6, countW = it.Count > 1 ? 26 : 0;
            string name = sel && treeTyping ? treeText + "_" : emu.RunawayName(it.Key) ?? it.Name;
            Txt(dc, name, R(nameX, row.Top, row.Right - nameX - countW - 4, TreeRowH - 2), Win32.DT_LEFT | Mid | Win32.DT_END_ELLIPSIS, sel && treeTyping ? Warn : on ? White : Text, fontSmall);
            if (countW > 0) Txt(dc, $"x{it.Count}", R(row.Right - countW - 4, row.Top, countW, TreeRowH - 2), Win32.DT_RIGHT | Mid, Accent, fontSmall);
            AddHit(IdTreeRow + r, R(nameX - 2, row.Top, row.Right - nameX + 2, row.Bottom - row.Top), Kind.Button);
        }
        if (all.Count > visible)
        {
            int th = visible * TreeRowH, thumb = Math.Max(16, th * visible / all.Count);
            int ty = y + (th - thumb) * treeScroll / Math.Max(1, all.Count - visible);
            Box(dc, R(x + w - 5, y, 4, th), Field, -1, 2);
            Box(dc, R(x + w - 5, ty, 4, thumb), Dim, -1, 2);
        }
        // Rename, and what the last action said
        int by = y + visible * TreeRowH + 4;
        Button(dc, IdTreeRename, treeTyping ? "Keep name" : "Rename", R(x, by, 84, 24), primary: treeTyping, enabled: treeSelected >= 0);
        string say = treeTyping ? "type, Enter keeps, Esc gives up" : treeMessage.Length > 0 ? treeMessage : treeSelected < 0 ? "pick a row to rename it" : "";
        Txt(dc, say, R(x + 90, by, w - 90, 24), Win32.DT_LEFT | Mid | Win32.DT_END_ELLIPSIS, treeTyping ? Warn : treeMessageOk ? Faint : Warn, fontSmall);
        return true;
    }

    private void Triangle(nint dc, RECT r, int color)
    {
        int cx = r.Left + 4, cy = (r.Top + r.Bottom) / 2, s = Math.Min(6, (r.Bottom - r.Top) / 2);
        for (int k = 0; k <= s * 2; k++) Line_(dc, cx + k / 2, cy - s + k / 2, cx + k / 2, cy + s - k / 2 + 1, color);
    }

    private void DrawThumb(nint dc, TreeItem it, RECT r)
    {
        Fill(dc, r, Bg);
        int mid = (r.Top + r.Bottom) / 2, half = (r.Bottom - r.Top) / 2;
        thumbsDrawn++;
        if (it.WaveMin != null && it.WaveMax != null)
        {
            for (int c = 0; c < it.WaveMin.Length && r.Left + c < r.Right; c++)
            {
                int lo = mid - it.WaveMax[c] * half / 128, hi = mid - it.WaveMin[c] * half / 128;
                Line_(dc, r.Left + c, lo, r.Left + c, Math.Max(lo + 1, hi + 1), Win32.Rgb(110, 180, 200));
            }
            return;
        }
        // a glyph: square (PSG tones), sine (FM), noise, or a plain sample mark
        int prev = mid;
        var rng = new Random(it.Key.GetHashCode());
        for (int c = 0; c < r.Right - r.Left; c++)
        {
            double ph = c / 10.0 * Math.PI;
            int yv = it.Glyph switch
            {
                'q' => mid + (Math.Sin(ph) >= 0 ? -half + 2 : half - 2),
                'f' => mid - (int)(Math.Sin(ph) * (half - 2)),
                'n' => mid + rng.Next(-half + 2, half - 1),
                _ => mid - (int)(Math.Sin(ph * 1.7) * Math.Exp(-c / 20.0) * (half - 2)),
            };
            if (c > 0) Line_(dc, r.Left + c - 1, prev, r.Left + c, yv, Win32.Rgb(110, 180, 200));
            prev = yv;
        }
    }

    private int thumbsDrawn;

    private void TreeClick(int id)
    {
        var emu = plugin.Emulator;
        if (emu == null) return;
        if (id == IdTreeRename)
        {
            if (treeTyping) { TreeRenameEnd(commit: true); return; }
            if (treeSelected < 0 || treeSelected >= treeItems.Count) { treeMessage = "Pick a row first: Rename names the row picked last"; treeMessageOk = false; return; }
            treeTyping = true;
            treeText = emu.RunawayName(treeItems[treeSelected].Key) ?? treeItems[treeSelected].Name;
            UiNative.SetFocus(Hwnd);                     // (typed characters reach this window: WM_CHAR -> UiChar -> TreeChar)
            return;
        }
        int r = id >= IdTreePlay ? id - IdTreePlay : id - IdTreeRow;
        if (r < 0 || r >= treeVisible.Count) return;
        var (isHeader, index) = treeVisible[r];
        if (isHeader) { if (id < IdTreePlay) treeFolded[index] = !treeFolded[index]; return; }
        var it = treeItems[index];
        if (id >= IdTreePlay)
        {
            var res = plugin.AuditionRunaway(it);
            treeMessage = res.Message; treeMessageOk = res.Ok;
            auditionsAsked++;
            return;
        }
        // pick it for this channel, as the Instrument dropdown does
        if (treeTyping && index != treeSelected) TreeRenameEnd(commit: true);
        treeSelected = index;
        plugin.MarkConfigured("an instrument was picked in the Runaway tree");
        if (it.Value >= RecipeValue) plugin.SetFromUi(P.Recipe, it.Value - RecipeValue + 1);
        else { plugin.SetFromUi(P.Recipe, 0); plugin.SetFromUi(P.Instrument, it.Value); }
        treeMessage = ""; treeMessageOk = true;
    }

    private int auditionsAsked;

    private void TreeChar(char ch)
    {
        if (ch == '\b') { if (treeText.Length > 0) treeText = treeText[..^1]; return; }
        if (ch == '\r') { TreeRenameEnd(commit: true); return; }
        if (ch == 27) { TreeRenameEnd(commit: false); return; }
        if (ch >= ' ' && treeText.Length < RunawayNames.MaxName) treeText += ch;
    }

    /// <summary>Keys the rename takes as keys (Escape and Enter also come as WM_CHAR; this catches them when a host sends only the key).</summary>
    private bool TreeKey(int vk)
    {
        if (vk == 0x1B) { TreeRenameEnd(commit: false); return true; }
        if (vk == 0x0D) { TreeRenameEnd(commit: true); return true; }
        return vk == 0x08;      // (Backspace comes as WM_CHAR too)
    }

    private void TreeRenameEnd(bool commit)
    {
        if (!treeTyping) return;
        treeTyping = false;
        var emu = plugin.Emulator;
        if (commit && emu != null && treeSelected >= 0 && treeSelected < treeItems.Count)
        {
            var it = treeItems[treeSelected];
            emu.SetRunawayName(it.Key, treeText.Trim() == it.Name ? null : treeText);
            treeMessage = treeText.Trim().Length == 0 || treeText.Trim() == it.Name ? "back to its own name" : "renamed: the FL project keeps the name";
            treeMessageOk = true;
        }
        UiReleaseFocus();
    }

    private bool TreeWheel(int delta, int x, int y)
    {
        if (treeVisible.Count == 0 || x < GameColX || plugin.Emulator?.Runaway != true) return false;
        treeScroll = Math.Max(0, treeScroll - delta);
        return true;
    }

    private long TreeSignature() => treeItems.Count * 7L + treeScroll * 131L + treeSelected * 17L + (treeTyping ? treeText.GetHashCode() : 3) + treeMessage.GetHashCode() + treeFolded.Count(f => f) * 1009L + (treeFolded[0] ? 1 : 0) + (treeFolded[1] ? 2 : 0) + (treeFolded[2] ? 4 : 0) + (treeFolded[3] ? 8 : 0);

    /// <summary>895 index 0..3: items of group n; 4 all items; 5 duplicates folded; 6 rows drawn; 7 renaming (1/0); 8 + g: group g folded; 9 +... see below. 896 index i: fold count of item i.
    /// 897 copy the shown name of item <c>index</c> into the UTF-16 buffer <c>value</c> (64 chars), returns its length. 898 the drawn row of item <c>index</c> (-1 when not drawn); index -1 - g: the row of group g's header.
    /// 899 index 0: copy the tree's last message into <c>value</c>; 1: the play buttons pressed; 2: thumbnails drawn so far; 3: whether this instance is auditioning (1/0); 4: the waveform columns of item <c>value</c> with ink (0 for a glyph).</summary>
    private nint TreeTest(int n, nint index, nint value)
    {
        var emu = plugin.Emulator;
        switch (n)
        {
            case 895:
                if (index is >= 0 and < 4) return treeItems.Count(i => (int)i.Group == index);
                if (index is >= 8 and < 12) return treeFolded[index - 8] ? 1 : 0;
                return index switch { 4 => treeItems.Count, 5 => treeItems.Sum(i => i.Count - 1), 6 => treeVisible.Count, 7 => treeTyping ? 1 : 0, _ => -1 };
            case 896: return index >= 0 && index < treeItems.Count ? treeItems[(int)index].Count : -1;
            case 897:
            {
                if (index < 0 || index >= treeItems.Count) return -1;
                string s = emu?.RunawayName(treeItems[(int)index].Key) ?? treeItems[(int)index].Name;
                return CopyChars(s, 64, value);
            }
            case 898:
                for (int r = 0; r < treeVisible.Count; r++)
                    if (index >= 0 ? !treeVisible[r].Header && treeVisible[r].Index == index : treeVisible[r].Header && treeVisible[r].Index == -1 - index) return r;
                return -1;
            case 899:
                return index switch
                {
                    0 => CopyChars(treeMessage, 200, value),
                    1 => auditionsAsked,
                    2 => thumbsDrawn,
                    3 => plugin.AuditionPlaying ? 1 : 0,
                    4 => value >= 0 && value < treeItems.Count && treeItems[(int)value].WaveMax is { } wm ? wm.Count(v => v != 0) : 0,
                    _ => -1,
                };
        }
        return 0;
    }
}
