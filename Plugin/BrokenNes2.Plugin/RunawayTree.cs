// Layout B (2026-11-13): the Instrument Runaway list as a TREE by kind, the model behind the Game view's tree (Bn2Editor.Tree.cs).
//
//   Samples        an instrument that holds a note: a looping sample (SNES, WideMess), an FM patch, the PSG's square tones, a looping DAC sound, a YM2413 instrument
//   Drums          a one-shot of at most DrumMs (400 ms): a short unlooped BRR / PCM sample, a short unlooped Genesis DAC sound; and the PSG noises
//   Game voices    the recipes Instrument Runaway learned from the game's sound driver (SNES SpcRecipe, Genesis GenRecipe)
//   Sound effects  a one-shot longer than DrumMs (an explosion, a voice clip, a jingle that is a sample)
//
// It is a heuristic and says so: a game's sample table does not say what a sample is for. A looping sample is played as an instrument; a short one-shot is what drivers use for
// drums; a long one-shot is usually an effect. Duplicates (the same bytes at two slots, the same patch on two FM channels, the same game voice learned twice) are folded into the
// first one with their count. Names the user gives are kept per emulator, by a key made from the instrument's CONTENT (so a renamed sample keeps its name after another freeze of
// the same game), and saved in the project (section RNM1, after CRP1 and before INJ1, which stays last).
using System.Text;
using BrokenNes2.Ui;

namespace BrokenNes2;

internal enum TreeGroup { Samples = 0, Drums = 1, GameVoices = 2, Effects = 3 }

/// <summary>One instrument of the tree: what the Instrument / Recipe parameters are set to when it is picked (<see cref="Value"/>: as the Instrument dropdown's values, game voices
/// from <see cref="Bn2Editor"/>'s RecipeValue), its default name, its fold count, its rename key, a 40-column min/max waveform (null: drawn as a glyph of its <see cref="Glyph"/>).</summary>
internal sealed class TreeItem
{
    public TreeGroup Group;
    public string Name = "", Note = "", Key = "";
    public int Value, Count = 1;
    public sbyte[]? WaveMin, WaveMax;
    public char Glyph = 's';          // s sample, q square, f FM sine, n noise
    public object? Recipe;            // the SpcRecipe / GenRecipe for an audition of a game voice
}

internal static class RunawayKey
{
    private static string Hash(ReadOnlySpan<byte> b) => Music.Crc32.Of(b).ToString("X8") + b.Length.ToString("X");

    public static string Snes(SpcSnapshot s, SpcSnapshot.SampleInfo i)
    {
        int len = Math.Min(i.Blocks * 9, SpcSnapshot.AramSize - i.Start);
        return $"snes:{Hash(s.Aram.AsSpan(i.Start, Math.Max(0, len)))}:{i.Loop - i.Start}";
    }
}

internal static class RunawayTree
{
    public const int DrumMs = 400, WaveCols = 40;
    public const int RecipeValue = 1000;        // (= Bn2Editor.RecipeValue)
    public static readonly string[] GroupNames = ["Samples", "Drums", "Game voices", "Sound effects"];

    /// <summary>The kind of a sample by the heuristic above. With the deliberate fault TreeKindsWrong every sample is a Sample.</summary>
    public static TreeGroup KindOf(bool loops, double ms)
    {
        if (UiMutate.Has(UiMutate.TreeKindsWrong) || loops) return TreeGroup.Samples;
        return ms <= DrumMs ? TreeGroup.Drums : TreeGroup.Effects;
    }

    /// <summary>The tree's instruments for a frozen game (empty when the emulator holds none the tree can show: Game Boy channels, NES), in group order, duplicates folded unless the fault TreeNoFold is on.</summary>
    public static List<TreeItem> Build(Emulator emu)
    {
        var items = new List<TreeItem>();
        if (!emu.Runaway) return items;
        if (emu.SamplerMode && emu.Snapshot is { } snap)
        {
            foreach (var i in snap.Instruments())
            {
                var it = new TreeItem { Group = KindOf(i.Loops, i.Milliseconds), Name = $"#{i.Slot}  ${i.Start:X4}", Note = $"{i.Milliseconds:0} ms{(i.Loops ? ", loops" : ", one-shot")}{(i.Sounding ? ", playing" : "")}", Key = RunawayKey.Snes(snap, i), Value = i.Slot };
                int len = Math.Min(i.Blocks * 9, SpcSnapshot.AramSize - i.Start);
                if (len >= 9)
                {
                    var brr = snap.Aram.AsSpan(i.Start, len).ToArray();
                    Thumb(it, Record.RecRoot.Decode(brr, i.Loop - i.Start, Math.Min(4096, i.Blocks * 16), out _));
                }
                items.Add(it);
            }
            for (int k = 0; k < snap.Recipes.Length; k++)
            {
                var r = snap.Recipes[k];
                var (name, note) = r.Describe(k);
                string body = name.Contains(':') ? name[(name.IndexOf(':') + 1)..].Trim() : name;
                items.Add(new TreeItem { Group = TreeGroup.GameVoices, Name = name, Note = note, Key = $"spcr:{body}:{r.Non}{r.Eon}{r.Pmon}", Value = RecipeValue + k, Recipe = r, Glyph = r.Non ? 'n' : 's' });
            }
        }
        else if (emu.WmsSamplerMode && emu.WmsState is { } ws)
        {
            foreach (var i in ws.Instruments())
                items.Add(new TreeItem { Group = KindOf(i.Loops, i.Milliseconds), Name = i.Name, Note = $"{i.Milliseconds:0} ms{(i.Loops ? ", loops" : ", one-shot")}{(i.WideDir ? ", new voice" : ", S-DSP voice")}", Key = $"wms:{(i.WideDir ? 1 : 0)}:{i.Start:X}:{i.Length}:{i.Loop}", Value = i.Index });
        }
        else if (emu.SegaSamplerMode && emu.SegaState is { } sn)
        {
            for (int i = 0; i < sn.InstrumentCount; i++)
            {
                var (name, note) = sn.Instrument(i);
                var it = new TreeItem { Name = name, Note = note ?? "", Value = i + 1 };
                if (sn.IsPsgInstrument(i))
                {
                    bool noise = sn.IsNoiseInstrument(i);
                    it.Group = noise ? (UiMutate.Has(UiMutate.TreeKindsWrong) ? TreeGroup.Samples : TreeGroup.Drums) : TreeGroup.Samples;
                    it.Glyph = noise ? 'n' : 'q';
                    it.Key = $"psg:{sn.Kind}:{i - sn.PsgInstrument}";
                }
                else if (sn.IsDacInstrument(i))
                {
                    var d = sn.Dac[i - 6];
                    it.Group = KindOf(d.Loops, d.Seconds * 1000);
                    it.Key = "dac:" + Music.Crc32.Of(d.Data).ToString("X8") + d.Data.Length.ToString("X");
                    var pcm = new short[Math.Min(d.Data.Length, 8192)];
                    for (int k = 0; k < pcm.Length; k++) pcm[k] = (short)((d.Data[k] - 128) << 8);
                    Thumb(it, pcm);
                }
                else if (sn.Kind == SegaSnapshot.KindOpn)
                {
                    it.Group = TreeGroup.Samples;
                    it.Glyph = 'f';
                    it.Key = "opn:" + Convert.ToHexString(sn.OpnPatch[i].AsSpan(0, Math.Min(29, sn.OpnPatch[i].Length)));     // the same patch on two channels folds
                }
                else { it.Group = TreeGroup.Samples; it.Glyph = 'f'; it.Key = $"opll:{i}"; }
                items.Add(it);
            }
            for (int k = 0; k < sn.Recipes.Length; k++)
            {
                var r = sn.Recipes[k];
                var (name, note) = r.Describe(k);
                string body = name.Contains(':') ? name[(name.IndexOf(':') + 1)..].Trim() : name;
                items.Add(new TreeItem { Group = TreeGroup.GameVoices, Name = name, Note = note, Key = $"genr:{r.Kind}:{body}", Value = RecipeValue + k, Recipe = r, Glyph = r.Kind == GenRecipe.KindPsgNoise ? 'n' : r.IsFm ? 'f' : 'q' });
            }
        }
        // fold the duplicates into the first of each key, keep the groups in order
        var folded = new List<TreeItem>();
        var byKey = new Dictionary<string, TreeItem>();
        foreach (var it in items)
        {
            if (!UiMutate.Has(UiMutate.TreeNoFold) && byKey.TryGetValue(it.Key, out var first)) { first.Count++; continue; }
            byKey[it.Key] = it;
            folded.Add(it);
        }
        return folded.OrderBy(i => (int)i.Group).ToList();       // (a stable sort: each group keeps the game's order)
    }

    /// <summary>A 40-column min/max outline of the first few thousand samples.</summary>
    private static void Thumb(TreeItem it, short[] pcm)
    {
        if (pcm.Length == 0) return;
        it.WaveMin = new sbyte[WaveCols]; it.WaveMax = new sbyte[WaveCols];
        for (int c = 0; c < WaveCols; c++)
        {
            int a = c * pcm.Length / WaveCols, b = Math.Max(a + 1, (c + 1) * pcm.Length / WaveCols);
            int lo = 0, hi = 0;
            for (int k = a; k < b && k < pcm.Length; k++) { lo = Math.Min(lo, pcm[k]); hi = Math.Max(hi, pcm[k]); }
            it.WaveMin[c] = (sbyte)Math.Clamp(lo >> 8, -127, 127); it.WaveMax[c] = (sbyte)Math.Clamp(hi >> 8, -127, 127);
        }
    }
}

/// <summary>The names the user gave tree rows, in the project: section <c>RNM1</c> = version 1, count (u16), then per name key and name (u8 length + UTF-8 each), then a CRC-32 of all before it.
/// A damaged or newer section is dropped (the names it held are lost, the rest of the project is not).</summary>
internal static class RunawayNames
{
    public const uint Tag = 0x314D4E52;            // "RNM1"
    public const int MaxBytes = 64 * 1024, MaxNames = 1024, MaxName = 60;

    public static byte[]? Build(IReadOnlyDictionary<string, string> names)
    {
        if (names.Count == 0) return null;
        var ms = new MemoryStream();
        ms.WriteByte(1);
        var list = names.Take(MaxNames).ToList();
        ms.WriteByte((byte)(list.Count & 0xFF)); ms.WriteByte((byte)(list.Count >> 8));
        foreach (var (k, v) in list)
        {
            foreach (var s in new[] { k, v })
            {
                var b = Encoding.UTF8.GetBytes(s);
                if (b.Length > 255) b = b[..255];
                ms.WriteByte((byte)b.Length); ms.Write(b);
            }
        }
        uint crc = Music.Crc32.Of(ms.ToArray());
        ms.Write(BitConverter.GetBytes(crc));
        return ms.Length <= MaxBytes ? ms.ToArray() : null;
    }

    /// <summary>The names in a section, or null when it is damaged or of a later version.</summary>
    public static Dictionary<string, string>? Read(byte[] b)
    {
        if (b.Length < 7 || b[0] != 1) return null;
        if (Music.Crc32.Of(b.AsSpan(0, b.Length - 4)) != BitConverter.ToUInt32(b, b.Length - 4)) return null;
        int n = b[1] | (b[2] << 8), p = 3, end = b.Length - 4;
        var d = new Dictionary<string, string>();
        for (int i = 0; i < n; i++)
        {
            string? k = Str(b, ref p, end), v = Str(b, ref p, end);
            if (k == null || v == null) return null;
            d[k] = v;
        }
        return p == end ? d : null;
    }

    private static string? Str(byte[] b, ref int p, int end)
    {
        if (p >= end) return null;
        int l = b[p++];
        if (p + l > end) return null;
        string s = Encoding.UTF8.GetString(b, p, l);
        p += l;
        return s;
    }
}
