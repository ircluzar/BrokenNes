using System.Text.Json;
using NesEmulator.Gb;

// NanoGbParity: the C# side of Nano GB's proofs (Nano/gb/test/parity.mjs drives it).
//
//   NanoGbParity replay <dir>  every <name>.trace.json ({"rate":48000,"ops":[[0,reg,value],[1,samples],...]}): the register timeline through Core's
//                              GnubSound built at that rate -> <name>.core.f32 (left then right, float32 = l / 128). At 44100 Hz also through APU_GNUB
//                              itself, clocked in T-cycles as the emulator clocks it -> <name>.apu.f32 (its 16-bit samples / 32768, same layout)
//   NanoGbParity notes <dir>   every <name>.scn.json (the engine's events at their frames, presets normalized): Core's NanoGbEngine (the plugin's engine)
//                              -> <name>.ref.f32 and <name>.ref.trace.json
//   NanoGbParity midi <dir>    every <name>.mid (+ <name>.sidecar.json): what the plugin plays when FL plays that file with one BrokenNes2 Nano GB instance per
//                              MIDI channel: a NanoGbEngine per channel fed the file's notes, the preset from the program / GM drums / sidecar -> <name>.midi.f32

if (args.Length < 2 || args[0] is not ("replay" or "notes" or "midi")) { Console.Error.WriteLine("usage: NanoGbParity replay|notes|midi <dir>"); return 2; }

static void WriteF32(string path, float[] a, float[] b)
{
    var bytes = new byte[(a.Length + b.Length) * 4];
    Buffer.BlockCopy(a, 0, bytes, 0, a.Length * 4);
    Buffer.BlockCopy(b, 0, bytes, a.Length * 4, b.Length * 4);
    File.WriteAllBytes(path, bytes);
}
var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
int done = 0;

if (args[0] == "replay")
{
    foreach (var path in Directory.GetFiles(args[1], "*.trace.json").Where(p => !p.EndsWith(".ref.trace.json")).OrderBy(p => p, StringComparer.Ordinal))
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        int rate = doc.RootElement.GetProperty("rate").GetInt32();
        var ops = doc.RootElement.GetProperty("ops");
        long total = 0;
        foreach (var op in ops.EnumerateArray()) if (op[0].GetInt32() == 1) total += op[1].GetInt32();
        var L = new float[total]; var R = new float[total]; int at = 0;
        var snd = new GnubSound(false, rate);
        snd.sound_reset();
        snd.Output = (l, r) => { L[at] = l / 128f; R[at] = r / 128f; at++; };
        foreach (var op in ops.EnumerateArray())
        {
            if (op[0].GetInt32() == 0) snd.sound_write(op[1].GetInt32(), (byte)op[2].GetInt32());
            else snd.Advance(op[1].GetInt32() * snd.RATE);
        }
        WriteF32(path.Replace(".trace.json", ".core.f32"), L, R);
        if (rate == 44100)
        {
            // the emulator's own class: T-cycles in (2 per gnuboy unit), 16-bit stereo out
            var apu = new APU_GNUB(GbModel.Dmg);
            var AL = new float[total]; var AR = new float[total]; int got = 0;
            var buf = new short[8192];
            void Drain() { int n; while ((n = apu.ReadSamples(buf)) > 0) for (int i = 0; i < n; i += 2) { AL[got] = buf[i] / 32768f; AR[got] = buf[i + 1] / 32768f; got++; } }
            foreach (var op in ops.EnumerateArray())
            {
                if (op[0].GetInt32() == 0) apu.WriteRegister(op[1].GetInt32(), (byte)op[2].GetInt32());
                else { int n = op[1].GetInt32(); while (n > 0) { int k = Math.Min(n, 1000); apu.Tick(k * GnubSound.DefaultRate * 2); Drain(); n -= k; } }
            }
            WriteF32(path.Replace(".trace.json", ".apu.f32"), AL, AR);
        }
        done++;
    }
    Console.WriteLine($"replayed {done} traces");
}
else if (args[0] == "notes")
{
    foreach (var path in Directory.GetFiles(args[1], "*.scn.json").OrderBy(p => p, StringComparer.Ordinal))
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        int rate = root.GetProperty("sampleRate").GetInt32(), frames = root.GetProperty("frames").GetInt32();
        var presets = new Dictionary<string, NanoGbPreset>();
        foreach (var p in root.GetProperty("presets").EnumerateObject()) presets[p.Name] = p.Value.Deserialize<NanoGbPreset>(jsonOpts)!;
        var e = new NanoGbEngine(rate, trace: true) { Volume = root.TryGetProperty("volume", out var vo) ? vo.GetDouble() : 1 };
        foreach (var ev in root.GetProperty("events").EnumerateArray())
        {
            long f = ev.GetProperty("frame").GetInt64();
            string t = ev.GetProperty("t").GetString()!;
            double D(string k, double d = 0) => ev.TryGetProperty(k, out var x) && x.ValueKind == JsonValueKind.Number ? x.GetDouble() : d;
            int I(string k, int d) => ev.TryGetProperty(k, out var x) && x.ValueKind == JsonValueKind.Number ? x.GetInt32() : d;
            switch (t)
            {
                case "on": e.NoteOn(f, I("id", 0), D("note"), D("vel"), presets[ev.GetProperty("preset").GetString()!], I("channel", -1), I("duty", -1), I("pan", 2), D("bend"), D("duration")); break;
                case "off": if (ev.TryGetProperty("id", out _)) e.NoteOff(f, I("id", 0)); else e.NoteOffByNote(f, D("note")); break;
                case "bend": e.BendVoice(f, I("id", 0), D("cents")); break;
                case "alloff": e.AllOff(f); break;
                case "kill": e.Kill(f); break;
                case "volume": e.SetVolume(f, D("value")); break;
                case "pitchBend": e.SetPitchBend(f, D("value")); break;
                case "tune": e.SetTune(f, D("value")); break;
                case "pan": { var v = ev.GetProperty("value"); e.SetPan(f, v[0].GetInt32(), v[1].GetInt32(), v[2].GetInt32(), v[3].GetInt32()); break; }
                default: throw new InvalidDataException("event " + t);
            }
        }
        var L = new float[frames]; var R = new float[frames];
        for (int o = 0; o < frames; o += 128) e.Render(L, R, o, Math.Min(128, frames - o));
        WriteF32(path.Replace(".scn.json", ".ref.f32"), L, R);
        File.WriteAllText(path.Replace(".scn.json", ".ref.trace.json"), JsonSerializer.Serialize(e.Trace));
        done++;
    }
    Console.WriteLine($"ran {done} scripts through NanoGbEngine");
}
else
{
    foreach (var path in Directory.GetFiles(args[1], "*.mid").OrderBy(p => p, StringComparer.Ordinal))
    {
        int rate = 44100;
        var rp = Path.ChangeExtension(path, ".rate.txt");
        if (File.Exists(rp)) rate = int.Parse(File.ReadAllText(rp).Trim());
        var sc = Path.ChangeExtension(path, ".sidecar.json");
        var song = FlLike.Read(File.ReadAllBytes(path));
        var (L, R) = FlLike.Render(song, rate, File.Exists(sc) ? File.ReadAllText(sc) : null);
        WriteF32(Path.ChangeExtension(path, ".midi.f32"), L, R);
        done++;
    }
    Console.WriteLine($"rendered {done} MIDI files as the plugin plays them");
}
return 0;

/// <summary>A .mid read the way Nano/gb/src/song.js reads it, played the way the plugin plays FL's notes: one Nano GB instance (one NanoGbEngine) per MIDI channel.
/// FL hands each instance its notes, velocities and pitch bends; the instance's Instrument (the program number) or the sidecar picks the preset.</summary>
static class FlLike
{
    public record struct Ev(double Sec, int Type, int Ch, int A, int B);
    public sealed record Song(double Duration, List<Ev> Events, List<int> Channels);

    public static Song Read(byte[] b)
    {
        int p = 0;
        uint U32() { uint v = (uint)(b[p] << 24 | b[p + 1] << 16 | b[p + 2] << 8 | b[p + 3]); p += 4; return v; }
        int U16() { int v = b[p] << 8 | b[p + 1]; p += 2; return v; }
        string Str() => System.Text.Encoding.ASCII.GetString(b, p, 4);
        if (Str() != "MThd") throw new InvalidDataException("no MThd");
        p += 4; uint hlen = U32(); U16(); int ntracks = U16(), division = U16(); p = 8 + (int)hlen;
        var raw = new List<(long tick, int tr, int n, int type, int ch, int a, int bb, int us)>();
        for (int t = 0; t < ntracks; t++)
        {
            if (Str() != "MTrk") { p += 4; uint l0 = U32(); p += (int)l0; t--; if (p >= b.Length) break; continue; }
            p += 4; int len = (int)U32(), end = p + len;
            long tick = 0; int status = 0, order = 0;
            while (p < end)
            {
                int d = 0, c; do { c = b[p++]; d = (d << 7) | (c & 127); } while ((c & 128) != 0 && p < end);
                tick += d;
                int s = b[p];
                if ((s & 0x80) != 0) p++; else s = status;
                if (s == 0xFF)
                {
                    int type = b[p++]; int l = 0; do { c = b[p++]; l = (l << 7) | (c & 127); } while ((c & 128) != 0);
                    if (type == 0x51 && l == 3) raw.Add((tick, t, order++, -1, 0, 0, 0, b[p] << 16 | b[p + 1] << 8 | b[p + 2]));
                    p += l; continue;
                }
                if (s == 0xF0 || s == 0xF7) { int l = 0; do { c = b[p++]; l = (l << 7) | (c & 127); } while ((c & 128) != 0); p += l; continue; }
                status = s;
                int hi = s & 0xF0, ch = s & 15, a = b[p++]; bool two = hi != 0xC0 && hi != 0xD0; int v = two ? b[p++] : 0;
                raw.Add((tick, t, order++, hi, ch, a, v, 0));
            }
            p = end;
        }
        raw.Sort((x, y) => x.tick != y.tick ? x.tick.CompareTo(y.tick) : x.tr != y.tr ? x.tr.CompareTo(y.tr) : x.n.CompareTo(y.n));
        double us = 500000, sec = 0; long last = 0;
        var evs = new List<Ev>();
        foreach (var e in raw)
        {
            sec += (e.tick - last) * us / 1e6 / division; last = e.tick;
            if (e.type == -1) { us = e.us; continue; }
            if (e.type is 0x90 or 0x80 or 0xB0 or 0xC0 or 0xE0) evs.Add(new Ev(sec, e.type == 0x90 && e.bb == 0 ? 0x80 : e.type, e.ch, e.a, e.bb));
        }
        var chans = evs.Where(e => e.Type == 0x90).Select(e => e.Ch).Distinct().OrderBy(c => c).ToList();
        return new Song(sec, evs, chans);
    }

    sealed class Side { public string? Preset; public double? Volume; public int? Duty; public int? Pan; }

    public static (float[] L, float[] R) Render(Song song, int sr, string? sidecar)
    {
        var side = new Dictionary<int, Side>();
        if (sidecar != null)
        {
            using var doc = JsonDocument.Parse(sidecar);
            if (doc.RootElement.TryGetProperty("channels", out var chs))
                foreach (var c in chs.EnumerateObject())
                {
                    var s = new Side();
                    if (c.Value.TryGetProperty("preset", out var pv)) s.Preset = pv.GetString();
                    if (c.Value.TryGetProperty("volume", out var vv)) s.Volume = vv.GetDouble();
                    if (c.Value.TryGetProperty("duty", out var dv)) s.Duty = dv.GetInt32();
                    if (c.Value.TryGetProperty("pan", out var nv)) s.Pan = nv.ValueKind == JsonValueKind.String ? nv.GetString() switch { "L" => -1, "R" => 1, _ => 0 } : nv.GetInt32();
                    side[int.Parse(c.Name) - 1] = s;
                }
        }
        var engines = new Dictionary<int, NanoGbEngine>();
        foreach (int ch in song.Channels)
        {
            var e = new NanoGbEngine(sr);
            if (side.TryGetValue(ch, out var s) && s.Pan is int pan) for (int i = 0; i < 4; i++) e.ChanPan[i] = pan;
            engines[ch] = e;
        }
        var frames = song.Events.Select(e => (long)Math.Floor(e.Sec * sr + 0.5)).ToArray();
        int total = (int)Math.Floor((song.Duration + 1) * sr + 0.5);
        long endFrame = (long)Math.Floor(song.Duration * sr + 0.5);
        var L = new float[total]; var R = new float[total];
        var sl = new float[128]; var srr = new float[128];
        var program = new int[16]; var bendRange = Enumerable.Repeat(2.0, 16).ToArray(); var rpn = Enumerable.Repeat(0x3FFF, 16).ToArray();
        var held = new Dictionary<int, int>();
        int id = 1, next = 0; double pos = 0; bool playing = true;
        for (int off = 0; off < total; off += 128)
        {
            int n = Math.Min(128, total - off);
            if (playing)
            {
                double until = pos + n;
                while (next < frames.Length && frames[next] < until)
                {
                    int at = (int)Math.Max(0, Math.Floor(frames[next] - pos));
                    var e = song.Events[next++];
                    engines.TryGetValue(e.Ch, out var eng);
                    if (eng == null && e.Type == 0x90) continue;
                    long f = eng != null ? eng.Frame + at : 0;
                    int key = e.Ch * 128 + e.A;
                    switch (e.Type)
                    {
                        case 0x90:
                        {
                            if (held.TryGetValue(key, out int old)) { eng!.NoteOff(f, old); held.Remove(key); }
                            NanoGbPreset? preset; double note = e.A, gain = 1; int duty = -1;
                            if (side.TryGetValue(e.Ch, out var s) && s.Preset != null) { preset = NanoGbPresets.All[NanoGbPresets.IndexOf(s.Preset)]; gain = s.Volume ?? 1; duty = s.Duty ?? -1; }
                            else if (e.Ch == 9) { preset = NanoGbPresets.Drum(e.A); if (preset != null) note = preset.Note; }
                            else preset = NanoGbPresets.Program(program[e.Ch]);
                            if (preset == null) break;
                            int nid = id++;
                            eng!.NoteOn(f, nid, note, Math.Min(1, e.B / 127.0 * gain), preset, duty: duty);
                            held[key] = nid;
                            break;
                        }
                        case 0x80: if (held.TryGetValue(key, out int hid)) { eng!.NoteOff(f, hid); held.Remove(key); } break;
                        case 0xC0: program[e.Ch] = e.A; break;
                        case 0xE0: eng?.SetPitchBend(f, ((e.B << 7 | e.A) - 8192) / 8192.0 * bendRange[e.Ch] * 100); break;
                        case 0xB0:
                            if (e.A == 101) rpn[e.Ch] = (rpn[e.Ch] & 0x7F) | (e.B << 7);
                            else if (e.A == 100) rpn[e.Ch] = (rpn[e.Ch] & 0x3F80) | e.B;
                            else if (e.A == 6 && rpn[e.Ch] == 0) bendRange[e.Ch] = e.B;
                            else if ((e.A == 123 || e.A == 120) && eng != null) { if (e.A == 120) eng.Kill(f); else eng.AllOff(f); }
                            break;
                    }
                }
                pos = until;
                if (pos >= endFrame && next >= frames.Length) { playing = false; foreach (var eng in engines.Values) eng.AllOff(eng.Frame + n); }
            }
            foreach (int ch in song.Channels)
            {
                engines[ch].Render(sl, srr, 0, n);
                for (int i = 0; i < n; i++) L[off + i] = (float)(L[off + i] + sl[i] * 1.0);
                for (int i = 0; i < n; i++) R[off + i] = (float)(R[off + i] + srr[i] * 1.0);
            }
        }
        return (L, R);
    }
}
