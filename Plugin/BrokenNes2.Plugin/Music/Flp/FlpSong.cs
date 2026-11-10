// The music pipeline's shared .flp reader, C# twin of BrokenDev/tools/music/flp/flpsong.mjs (docs/projects/flp-music-pipeline.md). An FL Studio project -> the neutral song model that every
// game back end reads. The two must write the SAME canonical JSON bytes for the same .flp (FruityHost `flp-test` checks it on the user's songs): any change here goes into the JS one too.
// Pure library: no UI, no plugin, no file system (the caller gives the bytes). Used by the plugin (Test MIDI export), FruityHost and Workshop.
using System.Security.Cryptography;
using System.Text;

namespace BrokenNes2.Music.Flp;

/// <summary>One note on the song's timeline (FL ticks of the project's PPQ).</summary>
public readonly record struct FlpNote(int Start, int End, int Key, int Fine, int Velocity, int Pan, int Colour, bool Slide, int Pattern);

/// <summary>A preset carried by an instance in its PRS1 section.</summary>
public sealed class FlpPreset
{
    public bool Ok;
    public string Problem = "", Sha256 = "", Header = "";
    public byte[] File = [];
    public List<(string Tag, int Length, string Sha256)> Blobs = new();
}

/// <summary>A BrokenNes2 instance's saved state, decoded.</summary>
public sealed class FlpBn2
{
    public int Version;
    public int[] Params = [];
    public int Console, Core, Channel, Emulator;
    public string ConsoleName = "", Mode = "direct", RomPath = "";
    public List<(string Tag, byte[] Data)> Sections = new();
    public FlpPreset? Preset;
    public int Param(int i, int fallback = 0) => i < Params.Length ? Params[i] : fallback;
}

/// <summary>One channel of FL's rack.</summary>
public sealed class FlpInstrument
{
    public int Rack, Type;
    public string Name = "", Plugin = "";
    public int[]? Levels;
    public FlpBn2? Bn2;
    public List<FlpNote> Notes = new();
}

public sealed class FlpSongModel
{
    public string FlVersion = "", Title = "", Sha256 = "", TimelineMode = "playlist", Arrangement = "";
    public int Ppq, TempoMilli, Bytes, End;
    public List<FlpInstrument> Instruments = new();
    public List<string> Caveats = new();
    public double Tempo => TempoMilli / 1000.0;
}

public static class FlpSong
{
    public const string Format = "brokennes-song";
    public const int Version = 1, SlideFlag = 0x08;
    public static readonly string[] Consoles = ["NES", "GB", "SNES", "SMS", "MD"];
    public static readonly string[] NoteFields = ["start", "end", "key", "fine", "velocity", "pan", "colour", "slide", "pattern"];

    private sealed class RawNote { public int Pos, Flags, Channel, Len, Key, Fine, Colour, Pan, Velocity; public int Start, End, Pattern, Seq; }
    private sealed class RawPattern { public int Index; public List<RawNote> Notes = new(); public int Controllers; }
    private sealed class RawItem { public int Pos, Len, Flags, Pattern, Channel, Start; }
    private sealed class RawArr { public string Name = ""; public List<RawItem> Items = new(); }
    private sealed class RawChannel { public int Index, Type = -1; public byte[]? NameRaw, PluginRaw, State; public int[]? Levels; }

    private static uint U32(byte[] b, int o) => BitConverter.ToUInt32(b, o);
    private static int I32(byte[] b, int o) => BitConverter.ToInt32(b, o);
    private static int U16(byte[] b, int o) => b[o] | b[o + 1] << 8;
    private static string Latin1(byte[] b, int o, int n) { var c = new char[n]; for (int i = 0; i < n; i++) c[i] = (char)b[o + i]; return new string(c); }
    private static string Utf16(byte[]? d)
    {
        if (d == null) return "";
        var c = new char[d.Length / 2];
        for (int k = 0; k < c.Length; k++) c[k] = (char)(d[2 * k] | d[2 * k + 1] << 8);
        return new string(c).TrimEnd('\0');
    }
    private static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    /// <summary>Reads a project. Throws <see cref="FormatException"/> with the reason when it is not an FL project.</summary>
    public static FlpSongModel Read(byte[] b)
    {
        if (b.Length < 14 || Latin1(b, 0, 4) != "FLhd") throw new FormatException("not an FL Studio project (no FLhd header)");
        int hlen = (int)U32(b, 4), ppq = U16(b, 12);
        int p = 8 + hlen;
        if (p + 8 > b.Length || Latin1(b, p, 4) != "FLdt") throw new FormatException("FL project has no FLdt chunk");
        long dlen = U32(b, p + 4);
        p += 8;
        int end = (int)Math.Min(b.Length, p + dlen);
        var song = new FlpSongModel { Ppq = ppq, TempoMilli = 120000, Bytes = b.Length, Sha256 = Hex(b) };
        var channels = new List<RawChannel>();
        var patterns = new Dictionary<int, RawPattern>();
        var arrs = new List<RawArr>();
        RawChannel? ch = null; RawPattern? pat = null; RawArr? arr = null;
        var created = new List<RawPattern>();
        RawPattern PatOf(int n) { if (!patterns.TryGetValue(n, out var x)) { patterns[n] = x = new RawPattern { Index = n }; created.Add(x); } return x; }

        while (p < end)
        {
            int id = b[p++];
            int value = 0; byte[]? data = null;
            if (id < 64) { value = b[p]; p += 1; }
            else if (id < 128) { value = U16(b, p); p += 2; }
            else if (id < 192)
            {
                int L = 4;
                if (id == 172 && b[p + 4] < 0xC0) L = b[p + 3] >= 0xC0 ? 3 : b[p + 5] >= 0xC0 ? 5 : 4;
                value = L == 4 ? I32(b, p) : 0; p += L;
            }
            else
            {
                long size = 0; int sh = 0;
                for (; ; ) { int c = b[p++]; size += (long)(c & 127) << sh; sh += 7; if ((c & 128) == 0) break; }
                int n = (int)Math.Max(0, Math.Min(size, b.Length - p));
                data = b.AsSpan(p, n).ToArray(); p += (int)size;
            }
            switch (id)
            {
                case 64: ch = new RawChannel { Index = value }; channels.Add(ch); pat = null; break;
                case 21: if (ch != null) ch.Type = value; break;
                case 65: pat = PatOf(value); ch = null; break;
                case 99: arr = new RawArr(); arrs.Add(arr); ch = null; pat = null; break;
                case 156: song.TempoMilli = value; break;
                case 199: song.FlVersion = Latin1(data!, 0, data!.Length).TrimEnd('\0'); break;
                case 194: song.Title = Utf16(data); break;
                case 201: if (ch != null) ch.PluginRaw = data; break;
                case 203: if (ch != null) ch.NameRaw = data; break;
                case 213: if (ch != null) ch.State = data; break;
                case 219: if (ch != null && data!.Length >= 12) ch.Levels = [I32(data, 0), I32(data, 4), I32(data, 8)]; break;
                case 241: if (arr != null) arr.Name = Utf16(data); break;
                case 223: if (pat != null) pat.Controllers++; break;
                case 224:
                    if (pat == null) break;
                    for (int k = 0; k + 24 <= data!.Length - (FlpMutants.On(FlpMutants.ReaderDropsLastNote) ? 24 : 0); k += 24)
                        pat.Notes.Add(new RawNote
                        {
                            Pos = (int)U32(data, k), Flags = U16(data, k + 4), Channel = U16(data, k + 6), Len = (int)U32(data, k + 8), Key = U16(data, k + 12),
                            Fine = data[k + 16], Colour = data[k + 19], Pan = data[k + 20], Velocity = data[k + 21],
                        });
                    break;
                case 233:
                    {
                        if (arr == null) { arr = new RawArr(); arrs.Add(arr); }
                        int size = data!.Length % 88 == 0 ? 88 : data.Length % 60 == 0 ? 60 : 32;
                        for (int k = 0; k + size <= data.Length; k += size)
                        {
                            int bse = U16(data, k + 4), idx = U16(data, k + 6);
                            arr.Items.Add(new RawItem { Pos = (int)U32(data, k), Len = (int)U32(data, k + 8), Flags = U16(data, k + 18), Pattern = idx > bse ? idx - bse : 0, Channel = idx > bse ? -1 : idx, Start = I32(data, k + 24) });
                        }
                        break;
                    }
            }
        }
        if (p != end) song.Caveats.Add($"the event stream ended {p - end} bytes off its declared length: a part of the file was not understood");

        var notes = Timeline(song, patterns, created, arrs);
        foreach (var c in channels)
        {
            var ins = new FlpInstrument { Rack = c.Index, Name = Utf16(c.NameRaw), Plugin = Utf16(c.PluginRaw), Type = c.Type, Levels = c.Levels };
            if (ins.Plugin.Equals("BrokenNes2", StringComparison.OrdinalIgnoreCase))
            {
                var st = ReadBn2(c.State, out int rest);
                if (st == null) song.Caveats.Add($"channel {c.Index} \"{ins.Name}\": BrokenNes2 with no readable state (another version?)");
                else
                {
                    foreach (var s in st.Sections)
                        if (s.Tag == "PRS1" && st.Preset == null)
                        {
                            st.Preset = PresetOf(s.Data);
                            if (!st.Preset.Ok) song.Caveats.Add($"channel {c.Index} \"{ins.Name}\": its preset (PRS1) cannot be used: {st.Preset.Problem}");
                        }
                    if (rest != 0) song.Caveats.Add($"channel {c.Index} \"{ins.Name}\": {rest} bytes at the end of its state were not understood");
                    ins.Bn2 = st;
                }
            }
            else if (c.Type == 5) song.Caveats.Add($"channel {c.Index} \"{ins.Name}\" is an automation clip: automation is not read");
            song.Instruments.Add(ins);
        }
        var byRack = new Dictionary<int, FlpInstrument>();
        foreach (var i in song.Instruments) byRack[i.Rack] = i;
        foreach (var n in notes)
        {
            if (!byRack.TryGetValue(n.Channel, out var ins)) { song.Caveats.Add($"a note at tick {n.Start} is on rack channel {n.Channel}, which the project does not have: not read"); continue; }
            ins.Notes.Add(new FlpNote(n.Start, n.End, n.Key, n.Fine, n.Velocity, n.Pan, n.Colour, (n.Flags & SlideFlag) != 0, n.Pattern));
        }
        return song;
    }

    private static List<RawNote> Timeline(FlpSongModel song, Dictionary<int, RawPattern> patterns, List<RawPattern> created, List<RawArr> arrs)
    {
        var notes = new List<RawNote>();
        var arr = arrs.FirstOrDefault(a => a.Items.Count > 0);
        int songEnd = 0;
        if (arr != null)
        {
            song.Arrangement = arr.Name;
            if (arrs.Count(a => a.Items.Count > 0) > 1) song.Caveats.Add($"{arrs.Count} arrangements: only \"{arr.Name}\" (the first with clips) is read");
            foreach (var it in arr.Items)
            {
                if (it.Pattern <= 0) { song.Caveats.Add($"playlist clip at tick {it.Pos} is an audio/automation clip of channel {it.Channel}: not read"); continue; }
                if ((it.Flags & 0x1000) != 0) { song.Caveats.Add($"playlist clip at tick {it.Pos} (pattern {it.Pattern}) is muted: skipped"); continue; }
                if (!patterns.TryGetValue(it.Pattern, out var pat)) continue;
                long from = it.Start >= 0 ? it.Start : 0, to = from + (long)(uint)it.Len;
                foreach (var n in pat.Notes)
                {
                    if (n.Pos < from || n.Pos >= to) continue;
                    long start = it.Pos + n.Pos - from, e = Math.Min((long)it.Pos + it.Len, start + n.Len);
                    notes.Add(Place(n, (int)start, (int)e, it.Pattern));
                }
                songEnd = Math.Max(songEnd, it.Pos + it.Len);
            }
        }
        else
        {
            song.TimelineMode = "patterns-in-order";
            int bar = song.Ppq * 4, at = 0;
            var used = patterns.Values.OrderBy(x => x.Index).ToList();
            foreach (var pat in used)
            {
                if (pat.Notes.Count == 0) continue;
                long mx = pat.Notes.Max(n => (long)n.Pos + n.Len);
                int len = (int)Math.Max(bar, (long)Math.Ceiling(mx / (double)bar) * bar);
                foreach (var n in pat.Notes) notes.Add(Place(n, at + n.Pos, at + n.Pos + n.Len, pat.Index));
                at += len;
            }
            songEnd = at;
            song.Caveats.Add($"THE PLAYLIST IS EMPTY: the patterns are played one after another in pattern order ({string.Join(", ", created.Where(x => x.Notes.Count > 0).Select(x => x.Index))}), each rounded up to whole 4/4 bars. Put the patterns on the playlist in FL for the real arrangement.");
        }
        foreach (var pat in patterns.Values.OrderBy(x => x.Index)) if (pat.Controllers > 0) song.Caveats.Add($"pattern {pat.Index} has recorded controller events: not read");
        for (int i = 0; i < notes.Count; i++) notes[i].Seq = i;
        song.End = songEnd;
        return notes.OrderBy(n => n.Start).ThenBy(n => n.Channel).ThenBy(n => n.Seq).ToList();
    }

    private static RawNote Place(RawNote n, int start, int end, int pattern) => new()
    { Pos = n.Pos, Flags = n.Flags, Channel = n.Channel, Len = n.Len, Key = n.Key, Fine = n.Fine, Colour = n.Colour, Pan = n.Pan, Velocity = n.Velocity, Start = start, End = end, Pattern = pattern };

    /// <summary>The plugin's saved state (FruityPluginBase + Bn2Plugin.SaveRestoreState): "BFNP", version, count, parameters, ROM path, "EMU2" emulator + channel, tagged sections.</summary>
    public static FlpBn2? ReadBn2(byte[]? s, out int rest)
    {
        rest = 0;
        if (s == null || s.Length < 12 || Latin1(s, 0, 4) != "BFNP") return null;
        int version = I32(s, 4), count = I32(s, 8);
        if (count < 0 || count > 64 || 12 + 4 * count > s.Length) return null;
        var st = new FlpBn2 { Version = version, Params = new int[count] };
        for (int i = 0; i < count; i++) st.Params[i] = I32(s, 12 + 4 * i);
        int o = 12 + 4 * count, placeChannel = -1;
        if (o + 4 <= s.Length)
        {
            int n = I32(s, o); o += 4;
            if (n > 0 && o + n <= s.Length) st.RomPath = Encoding.UTF8.GetString(s, o, n);
            o += Math.Max(0, n);
            if (o + 12 <= s.Length && Latin1(s, o, 4) == "EMU2") { st.Emulator = I32(s, o + 4); placeChannel = I32(s, o + 8); o += 12; }
        }
        while (o + 8 <= s.Length)
        {
            string tag = Latin1(s, o, 4); int len = I32(s, o + 4);
            if (len < 0 || o + 8 + len > s.Length) break;
            st.Sections.Add((tag, s.AsSpan(o + 8, len).ToArray())); o += 8 + len;
        }
        rest = s.Length - o;
        st.Console = st.Param(10); st.Core = st.Param(0);
        st.ConsoleName = (uint)st.Console < Consoles.Length ? Consoles[st.Console] : $"console {st.Console}";
        st.Mode = st.Param(8) == 1 ? "rom" : "direct";
        int chParam = st.Param(6);
        st.Channel = chParam > 0 ? chParam - 1 : placeChannel;
        return st;
    }

    private static uint Crc32(ReadOnlySpan<byte> b)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte x in b) { c ^= x; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; }
        return c ^ 0xFFFFFFFF;
    }

    private static FlpPreset PresetOf(byte[] d)
    {
        var o = new FlpPreset();
        if (d.Length < 8 + 18 + 4) { o.Problem = "cut short"; return o; }
        if (d[0] != 1) { o.Problem = $"section version {d[0]}"; return o; }
        if (d[1] != 0) { o.Problem = "an unknown feature flag"; return o; }
        uint len = U32(d, 4);
        if (len != (uint)(d.Length - 12)) { o.Problem = "damaged (its length)"; return o; }
        if (U32(d, d.Length - 4) != Crc32(d.AsSpan(0, d.Length - 4))) { o.Problem = "damaged (the checksum does not match)"; return o; }
        var f = d.AsSpan(8, (int)len).ToArray();
        o.File = f; o.Sha256 = Hex(f);
        if (Latin1(f, 0, 4) != "BN2P") { o.Problem = "not a preset file"; return o; }
        int hn = (int)U32(f, 8);
        if (hn < 0 || 12L + hn + 2 > f.Length) { o.Problem = "its header is cut short"; return o; }
        o.Header = Encoding.UTF8.GetString(f, 12, hn);
        int p = 12 + hn, count = U16(f, p); p += 2;
        for (int i = 0; i < count && p + 8 <= f.Length - 4; i++)
        {
            string tag = Latin1(f, p, 4); int bl = (int)U32(f, p + 4);
            if (bl < 0 || p + 8L + bl > f.Length - 4) { o.Problem = "a blob is cut short"; return o; }
            o.Blobs.Add((tag, bl, Hex(f.AsSpan(p + 8, bl)))); p += 8 + bl;
        }
        o.Ok = true;
        return o;
    }

    // ---- canonical JSON: the exact bytes flpsong.mjs songJson writes ----

    public static string Json(FlpSongModel s)
    {
        var w = new StringBuilder();
        void Str(string x)
        {
            w.Append('"');
            foreach (char c in x)
            {
                if (c == '"') w.Append("\\\""); else if (c == '\\') w.Append("\\\\");
                else if (c < 0x20 || c > 0x7E) w.Append("\\u").Append(((int)c).ToString("x4"));
                else w.Append(c);
            }
            w.Append('"');
        }
        void Key(string k) { Str(k); w.Append(':'); }
        void Ints(IEnumerable<int> v) { w.Append('['); bool f = true; foreach (int x in v) { if (!f) w.Append(','); f = false; w.Append(x); } w.Append(']'); }

        w.Append('{'); Key("format"); Str(Format); w.Append(','); Key("version"); w.Append(Version); w.Append(',');
        Key("source"); w.Append('{'); Key("fl"); Str(s.FlVersion); w.Append(','); Key("title"); Str(s.Title); w.Append(','); Key("ppq"); w.Append(s.Ppq); w.Append(',');
        Key("tempoMilli"); w.Append(s.TempoMilli); w.Append(','); Key("bytes"); w.Append(s.Bytes); w.Append(','); Key("sha256"); Str(s.Sha256); w.Append("},");
        Key("timeline"); w.Append('{'); Key("mode"); Str(s.TimelineMode); w.Append(','); Key("arrangement"); Str(s.Arrangement); w.Append(','); Key("end"); w.Append(s.End); w.Append("},");
        Key("noteFields"); w.Append('['); w.Append(string.Join(",", NoteFields.Select(f => "\"" + f + "\""))); w.Append("],");
        Key("instruments"); w.Append('[');
        for (int i = 0; i < s.Instruments.Count; i++)
        {
            var ins = s.Instruments[i];
            if (i > 0) w.Append(',');
            w.Append('{'); Key("rack"); w.Append(ins.Rack); w.Append(','); Key("name"); Str(ins.Name); w.Append(','); Key("plugin"); Str(ins.Plugin); w.Append(',');
            Key("type"); w.Append(ins.Type); w.Append(','); Key("levels"); if (ins.Levels == null) w.Append("null"); else Ints(ins.Levels); w.Append(',');
            Key("bn2");
            if (ins.Bn2 is not { } b) w.Append("null");
            else
            {
                w.Append('{'); Key("version"); w.Append(b.Version); w.Append(','); Key("params"); Ints(b.Params); w.Append(','); Key("console"); w.Append(b.Console); w.Append(',');
                Key("consoleName"); Str(b.ConsoleName); w.Append(','); Key("core"); w.Append(b.Core); w.Append(','); Key("mode"); Str(b.Mode); w.Append(',');
                Key("channel"); w.Append(b.Channel); w.Append(','); Key("emulator"); w.Append(b.Emulator); w.Append(','); Key("romPath"); Str(b.RomPath); w.Append(',');
                Key("sections"); w.Append('[');
                for (int k = 0; k < b.Sections.Count; k++)
                {
                    if (k > 0) w.Append(',');
                    w.Append('{'); Key("tag"); Str(b.Sections[k].Tag); w.Append(','); Key("length"); w.Append(b.Sections[k].Data.Length); w.Append(','); Key("sha256"); Str(Hex(b.Sections[k].Data)); w.Append('}');
                }
                w.Append("],"); Key("preset");
                if (b.Preset is not { } pr) w.Append("null");
                else
                {
                    w.Append('{'); Key("ok"); w.Append(pr.Ok ? "true" : "false"); w.Append(','); Key("problem"); Str(pr.Problem); w.Append(','); Key("sha256"); Str(pr.Sha256); w.Append(',');
                    Key("header"); Str(pr.Header); w.Append(','); Key("blobs"); w.Append('[');
                    for (int k = 0; k < pr.Blobs.Count; k++)
                    {
                        if (k > 0) w.Append(',');
                        w.Append('{'); Key("tag"); Str(pr.Blobs[k].Tag); w.Append(','); Key("length"); w.Append(pr.Blobs[k].Length); w.Append(','); Key("sha256"); Str(pr.Blobs[k].Sha256); w.Append('}');
                    }
                    w.Append("],"); Key("file"); Str(pr.File.Length > 0 ? Convert.ToBase64String(pr.File) : ""); w.Append('}');
                }
                w.Append('}');
            }
            w.Append(','); Key("notes"); w.Append('[');
            for (int k = 0; k < ins.Notes.Count; k++)
            {
                var n = ins.Notes[k];
                if (k > 0) w.Append(',');
                Ints([n.Start, n.End, n.Key, n.Fine, n.Velocity, n.Pan, n.Colour, n.Slide ? 1 : 0, n.Pattern]);
            }
            w.Append("]}");
        }
        w.Append("],"); Key("caveats"); w.Append('[');
        for (int i = 0; i < s.Caveats.Count; i++) { if (i > 0) w.Append(','); Str(s.Caveats[i]); }
        w.Append("]}\n");
        return w.ToString();
    }
}
