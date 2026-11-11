// The music pipeline's Vaults GB back end (docs/projects/flp-music-pipeline.md): the neutral song (FlpSong) -> VRUN: Vaults of Obsolete Code's OWN music engine (VRUN_VOC_dev src/music,
// docs/music.md there: per-channel byte streams in Game Boy frames, tempo $0100 = one tick a frame). No MIDI step: the events are made here from FL's notes as the plugin plays them
// (FlpMono), then encoded by a port of the game's converter back end (tools/music/vocmusic.py Music.encode, byte for byte: FruityHost flp-test `vocgb-python` runs the Python one on the
// same events and compares). The same events go to the game as a roster song (`"events": "<song>.vocsong.json"`, the front end added to vocmusic.py), so the game's build makes the bytes
// the test ROM plays.
//
// Mapping (BrokenNes2 instances on the Game Boy console, Direct mode): Pulse 1 / Pulse 2 -> pulse 1 / 2 (the duty from Pulse duty: the Game Boy has the NES's four), Triangle -> the wave
// channel with the NES triangle's 32-step table (what the plugin's Game Boy chip plays there; full level: the plugin's triangle has no volume), Noise -> the noise channel (the period the
// plugin plays: NES noise index from the pitch, nearest Game Boy clock; Noise mode Short -> the 7-bit width). Pan: hard left / right / both (the Game Boy's NR51).
// The fence (FenceLine): every limit of the engine, checked; what is changed or left out says so and by how much.
namespace BrokenNes2.Music.Flp;

public enum VocTarget { Gb, Gg }

/// <summary>An instrument of the engine (the part the plugin can ask for): pulse duty 0-3, the noise's 7-bit width; the wave channel's table is the triangle.</summary>
public sealed record VocInstrument(int Duty = 2, bool Width7 = false, byte[]? Wave = null)
{
    /// <summary>The driver's 8 bytes for a channel kind (vocmusic.py Instrument.row).</summary>
    public byte[] Row(int kind, int waveIndex) => kind == 2 ? [(byte)waveIndex, 0, 0, 1, 0, 1, (byte)waveIndex, 0]
        : kind == 3 ? [0, 0, 0, 1, 0, 1, (byte)(Width7 ? 8 : 0), 0] : [(byte)(Duty << 6), 0, 0, 1, 0, 1, 0, 0];
}

/// <summary>One event of a channel, in Game Boy frames: inst, vol (0-15), on (a MIDI pitch; the noise: an NR43 value), pitch (an exact register, a note-on), freq (register, no restart), off, pan (0-3).</summary>
public sealed record VocEvent(int Frame, string Op, int Value, VocInstrument? Inst = null);

public sealed class VocSong
{
    public string Name = "", Title = "";
    public int Length;
    public bool Loop = true;
    public VocTarget Target;
    public List<VocEvent>[] Ch = [new(), new(), new(), new()];
    public byte[][] Streams = [];
    public List<byte[]> Instruments = new(), Waves = new();
    public List<int> Noise = new();
    public List<FenceLine> Fence = new();
    public double Seconds;
    public string[] Parts = ["", "", "", ""];
    public int[] Notes = new int[4];
    public IEnumerable<string> Caveats => Fence.Where(f => f.Level != FenceLevel.Ok).Select(FlpFence.Line);
}

public static class VocGbBackend
{
    public const double Fps = 4194304.0 / 70224.0;              // 59.7275 Hz
    public static readonly string[] Names = ["pulse 1", "pulse 2", "wave", "noise"];
    private static readonly string[] JsonNames = ["pulse1", "pulse2", "wave", "noise"];
    public const int PulseMin = 36, WaveMin = 24, NoteMax = 115, StreamCap = 16384 - 8, SongBanks = 7;
    public static readonly int[] NesNoisePeriods = [4, 8, 16, 32, 64, 96, 128, 160, 202, 254, 380, 508, 762, 1016, 2034, 4068];
    public static readonly byte[] TriangleWave = WaveBytes([.. Enumerable.Range(0, 16).Select(i => 15 - i), .. Enumerable.Range(0, 16)]);

    private static int PyRound(double x) => (int)Math.Round(x, MidpointRounding.ToEven);
    public static double MidiHz(double n) => 440.0 * Math.Pow(2, (n - 69) / 12.0);
    public static int PulseX(double hz) => Math.Clamp(2048 - PyRound(131072.0 / hz), 0, 2047);
    public static double XHz(int x, bool wave) => (wave ? 65536.0 : 131072.0) / (2048 - x);
    public static readonly int[] FreqTab = [.. Enumerable.Range(0, 128).Select(n => PulseX(MidiHz(n)))];
    public static double Nr43Clock(int v) { int r = v & 7, s = v >> 4; return 262144.0 / ((r == 0 ? 0.5 : r) * (1 << s)); }
    public static int NoiseNr43(double clockHz, bool shortMode)
    {
        int best = 0; double err = 1e9;
        for (int s = 0; s < 14; s++)
            for (int r = 0; r < 8; r++)
            {
                double c = 262144.0 / ((r == 0 ? 0.5 : r) * (1 << s)), e = Math.Abs(Math.Log(c / clockHz));
                if (e < err) { best = (s << 4) | (shortMode ? 8 : 0) | r; err = e; }
            }
        return best;
    }
    private static byte[] WaveBytes(int[] s) { var b = new byte[16]; for (int i = 0; i < 16; i++) b[i] = (byte)(s[2 * i] << 4 | s[2 * i + 1]); return b; }

    /// <summary>The song, its events, streams, tables and fence. <paramref name="target"/> Gg = the same bytes (Vaults GG plays the Game Boy songs unchanged) with the Game Gear's fence added by VocGgBackend.</summary>
    public static VocSong Build(FlpSongModel song, string name, VocTarget target = VocTarget.Gb)
    {
        var o = new VocSong { Name = name, Title = song.Title.Length > 0 ? song.Title : name, Target = target };
        var F = o.Fence;
        string console = target == VocTarget.Gb ? "Game Boy" : "Game Gear";
        foreach (var c in song.Caveats) F.Add(new FenceLine("project", FenceLevel.Note, c));
        F.AddRange(FlpFence.ChannelVolume(song));
        var byCh = new List<FlpInstrument>[4];
        for (int i = 0; i < 4; i++) byCh[i] = new();
        var presetInst = new Dictionary<FlpInstrument, VocInstrument>();
        var presetCh = new Dictionary<FlpInstrument, int>();
        int wanted = target == VocTarget.Gb ? 1 : 3;     // the plugin console the song is written with (Gg: the Master System console, whose PSG chip the Game Gear has)
        foreach (var ins in song.Instruments)
        {
            string who = $"channel {ins.Rack} \"{ins.Name}\"";
            if (ins.Bn2 is not { } b)
            {
                if (ins.Notes.Count > 0) F.Add(new FenceLine("instruments", FenceLevel.Over, $"{who} is not BrokenNes2 ({(ins.Plugin.Length > 0 ? ins.Plugin : ins.Type == 0 ? "sampler" : "type " + ins.Type)}): its {ins.Notes.Count} notes are not in the game"));
                continue;
            }
            if (ins.Notes.Count == 0) continue;
            if (b.Mode != "direct") { F.Add(new FenceLine("instruments", FenceLevel.Over, $"{who} is in ROM mode (it plays a game's channel, not notes): its {ins.Notes.Count} notes are not in the game")); continue; }
            if (b.Preset != null && b.Core is >= FlpPresetSound.FirstPresetChip and <= FlpPresetSound.LastPresetChip)
            {
                // a Runaway preset: the Game Boy engine plays a Game Boy preset's channel with its duty / width / wave RAM; anything else is not in the game
                var pd = b.Preset.Ok ? BrokenNes2.Presets.PresetFile.Read(b.Preset.File).Data : null;
                var play = pd != null ? BrokenNes2.Presets.PresetPlayable.Build(pd, out _) : null;
                if (target != VocTarget.Gb || play == null || play.Console != BrokenNes2.Instruments.ConsoleId.GameBoy || FlpMutants.On(PsMutants.GbPresetIgnored))
                {
                    F.Add(new FenceLine("preset", FenceLevel.Over, $"{who} plays a {(play != null ? play.Console.ToString() : "damaged")} preset{(b.Preset.Ok ? "" : $" ({b.Preset.Problem})")}: the {console} engine has no way to play it, its {ins.Notes.Count} notes are not in the game"));
                    continue;
                }
                var pi = play.Instrument;
                int pc = play.GbChannel;
                presetCh[ins] = pc;
                presetInst[ins] = pc == 2 ? new VocInstrument(Wave: pi.Blobs[0].Data.Take(16).ToArray()) : pc == 3 ? new VocInstrument(Width7: play.GbNoiseShort) : new VocInstrument(Duty: pi.Get("duty") & 3);
                F.Add(new FenceLine("preset", FenceLevel.Ok, $"{who}: Game Boy preset \"{(pd!.Name.Length > 0 ? pd.Name : pd.InstrumentName)}\" on {Names[pc]}: {(pc == 2 ? "its 32-sample wave RAM is the song's wave table (exact)" : pc == 3 ? $"its {(play.GbNoiseShort ? 7 : 15)}-bit noise" : $"its duty {new[] { "12.5", "25", "50", "75" }[pi.Get("duty") & 3]}%")}"));
                if (pc != 2) F.Add(new FenceLine("preset", FenceLevel.Note, $"{who}: the preset's volume envelope is not played: the engine's notes hold the velocity's level (as Direct mode does)"));
                byCh[pc].Add(ins);
                continue;
            }
            if (b.Console != wanted && !(target == VocTarget.Gg && b.Console == 1))
            { F.Add(new FenceLine("instruments", FenceLevel.Over, $"{who} is a {b.ConsoleName} instrument: its {ins.Notes.Count} notes are not in this {console} song (that console's back end plays it)")); continue; }
            if (b.Channel is < 0 or > 3) { F.Add(new FenceLine("channels", FenceLevel.Over, $"{who}: channel {(b.Channel == 4 ? "Mix" : b.Channel.ToString())} is not pulse 1 / pulse 2 / triangle / noise: its {ins.Notes.Count} notes are not in the game")); continue; }
            byCh[b.Channel].Add(ins);
        }

        double perTick = 60.0 / Math.Max(1e-3, song.Tempo) / Math.Max(1, song.Ppq);       // seconds per FL tick
        int Frame(double tick) => (int)Math.Round(tick * perTick * Fps, MidpointRounding.AwayFromZero);
        double TickOf(int frame) => frame / Fps / perTick;
        o.Length = Math.Max(2, Frame(song.End));
        o.Seconds = o.Length / Fps;

        int lowCount = 0, lowWorst = 0, quantOver = 0, shortNotes = 0, lost = 0, silent = 0, bends = 0, noiseSlides = 0, partialPan = 0, beyond = 0;
        double quantWorst = 0;
        string lowWhere = "";
        var duties = new SortedSet<int>();
        bool usedShort = false;
        for (int c = 0; c < 4; c++)
        {
            var group = byCh[c];
            var ev = o.Ch[c];
            if (group.Count == 0) { ev.Add(new VocEvent(0, "inst", 0, new VocInstrument())); continue; }
            var mono = FlpMono.Resolve(group);
            o.Parts[c] = string.Join(" + ", group.Select(g => $"\"{g.Name}\""));
            if (group.Count > 1) F.Add(new FenceLine("channels", FenceLevel.Note, $"{Names[c]}: {group.Count} instances play it ({o.Parts[c]}): the {console} has one {Names[c]}, they are merged one note at a time (the newest held note sounds, as in the plugin)"));
            if (mono.Cut > 0) F.Add(new FenceLine("polyphony", FenceLevel.Note, $"{Names[c]}: one note at a time: {mono.Cut - mono.Hidden} notes are cut short by a newer one and {mono.Hidden} are never heard (a newer note covers them whole), exactly as the plugin plays them"));
            if (mono.Empty > 0) F.Add(new FenceLine("notes", FenceLevel.Ok, $"{Names[c]}: {mono.Empty} notes of length 0 are silent in FL and in the game"));
            if (mono.LoneSlides > 0) F.Add(new FenceLine("slides", FenceLevel.Ok, $"{Names[c]}: {mono.LoneSlides} slide notes have no note under them: they do nothing in FL either"));
            int pan = group[0].Bn2!.Param(2);
            foreach (var g in group) if (g.Bn2!.Param(2) != 0 && Math.Abs(g.Bn2.Param(2)) != 100) partialPan++;
            int panCode = Math.Abs(pan) >= 50 ? (pan < 0 ? 2 : 1) : 3;
            VocInstrument InstOf(MonoVoice v) => presetInst.TryGetValue(v.Instrument, out var pv) ? pv : c < 2 ? new VocInstrument(Duty: v.Instrument.Bn2!.Param(5, 1) & 3) : c == 3 ? new VocInstrument(Width7: v.Instrument.Bn2!.Param(7) != 0) : new VocInstrument();
            var first = mono.Segs.Count > 0 ? InstOf(mono.Segs[0].V) : new VocInstrument();
            ev.Add(new VocEvent(0, "inst", 0, first));
            if (panCode != 3) ev.Add(new VocEvent(0, "pan", panCode));
            VocInstrument cur = first;
            int lastVol = -1, cursor = 0, notes = 0;
            // the octave shift a note needs to be playable (raised under the channel's lowest), kept for its bends
            int Shift(double cents)
            {
                int lo = c < 2 ? PulseMin : WaveMin, sh = 0;
                while (cents / 100 + sh < lo - 0.5) sh += 12;
                while (cents / 100 + sh > NoteMax + 0.5) sh -= 12;
                return sh;
            }
            int Reg(double cents) { double hz = MidiHz(cents / 100); return PulseX(c == 2 ? hz * 2 : hz); }
            var segs = mono.Segs.Where(s => c == 2 || s.V.Level > 0).ToList();
            silent += mono.Segs.Count - segs.Count;
            for (int i = 0; i < segs.Count; i++)
            {
                var s = segs[i];
                var v = s.V;
                int f0 = Math.Max(Frame(s.From), cursor), f1 = Frame(s.To);
                if (f1 <= Frame(s.From)) { shortNotes++; f1 = Frame(s.From) + 1; }
                if (f0 >= f1 || f0 >= o.Length) { lost++; continue; }
                if (f1 > o.Length) { beyond++; f1 = o.Length; }
                notes++;
                var want = InstOf(v);
                if (c < 2) duties.Add(want.Duty);
                if (c == 3 && want.Width7) usedShort = true;
                if (want != cur) { ev.Add(new VocEvent(f0, "inst", 0, want)); cur = want; }
                int lvl = c == 2 ? 15 : v.Level;
                if (lvl != lastVol) { ev.Add(new VocEvent(f0, "vol", lvl)); lastVol = lvl; }
                if (c == 3)
                {
                    int idx = VrunNesBackend.NoiseIndex(v.PitchAt(s.From) - 6000);
                    ev.Add(new VocEvent(f0, "on", NoiseNr43(1789773.0 / NesNoisePeriods[idx], want.Width7)));
                    if (v.Glides.Count > 0) noiseSlides++;
                }
                else
                {
                    double cents = v.PitchAt(s.From);
                    int sh = Shift(cents);
                    if (sh > 0) { lowCount++; int k = (int)Math.Round(cents / 100); if (sh > lowWorst || lowWhere.Length == 0) { lowWorst = Math.Max(lowWorst, sh); lowWhere = $"{Names[c]} {FlpFence.NoteName(k)} at {s.From / (double)Math.Max(1, song.Ppq) / 4 + 1:0.##} bars"; } }
                    double c2 = cents + sh * 100;
                    int n = (int)Math.Round(c2 / 100, MidpointRounding.AwayFromZero), x = Reg(c2);
                    int tab = Math.Clamp(n + (c == 2 ? 12 : 0), 0, 127);
                    if (Math.Abs(c2 - n * 100) < 0.5 && n >= 0 && n <= NoteMax && FreqTab[tab] == x) ev.Add(new VocEvent(f0, "on", n));
                    else ev.Add(new VocEvent(f0, "pitch", x));
                    double err = Math.Abs(1200 * Math.Log2(XHz(x, c == 2) / MidiHz(c2 / 100)));
                    if (err > 5) { quantOver++; quantWorst = Math.Max(quantWorst, err); }
                    if (v.Glides.Count > 0)
                    {
                        int last = x;
                        for (int f = f0 + 1; f < f1; f++)
                        {
                            int y = Reg(v.PitchAt(TickOf(f)) + sh * 100);
                            if (y != last) { ev.Add(new VocEvent(f, "freq", y)); last = y; bends++; }
                        }
                    }
                }
                bool joined = i + 1 < segs.Count && Math.Max(Frame(segs[i + 1].From), f1) == f1 && Frame(segs[i + 1].From) <= f1;
                if (!joined && f1 < o.Length) ev.Add(new VocEvent(f1, "off", 0));
                cursor = f1;
            }
            o.Notes[c] = notes;
        }

        // ---- the fence ----
        var used = Enumerable.Range(0, 4).Where(c => byCh[c].Count > 0).ToList();
        F.Add(new FenceLine("channels", used.Count > 0 ? FenceLevel.Ok : FenceLevel.Over, used.Count > 0 ? $"{string.Join(", ", used.Select(c => $"{Names[c]} <- {o.Parts[c]} ({o.Notes[c]} notes)"))}; the engine has 4 channels, one note at a time each"
            : $"NOTHING for Vaults {(target == VocTarget.Gb ? "GB" : "GG")}: no BrokenNes2 instance on the {(target == VocTarget.Gb ? "Game Boy" : "Game Boy or Master System")} console in Direct mode with notes on pulse 1, pulse 2, triangle or noise"));
        if (lowCount > 0) F.Add(new FenceLine("note range", FenceLevel.Over, $"{lowCount} notes are under the lowest the {(target == VocTarget.Gb ? "Game Boy" : "engine")} plays (pulse C2 = 36, wave C1 = 24): played {(lowWorst > 12 ? "up to " + lowWorst / 12 + " octaves" : "an octave")} higher; the lowest is {lowWhere}"));
        else F.Add(new FenceLine("note range", FenceLevel.Ok, "every note is inside the channels' ranges (pulse C2-G9, wave C1-G9)"));
        if (quantOver > 0) F.Add(new FenceLine("tuning", FenceLevel.Note, $"{quantOver} notes are more than 5 cents off their pitch on the frequency register's steps (worst {quantWorst:0.#} cents: high notes)"));
        if (shortNotes > 0) F.Add(new FenceLine("timing", FenceLevel.Note, $"{shortNotes} notes are shorter than a Game Boy frame ({1000 / Fps:0.0} ms): they play for one frame"));
        if (lost > 0) F.Add(new FenceLine("timing", FenceLevel.Over, $"{lost} notes start and end inside a frame another note already has: not heard in the game"));
        if (beyond > 0) F.Add(new FenceLine("length", FenceLevel.Note, $"{beyond} notes go past the song's end: cut at the end"));
        F.Add(new FenceLine("timing", FenceLevel.Ok, $"events on the {(target == VocTarget.Gb ? "Game Boy's" : "engine's")} frames ({Fps:0.0000} Hz, {1000 / Fps:0.00} ms): notes within half a frame of FL's time; {o.Length} frames = {o.Seconds:0.00} s, the whole song loops"));
        if (silent > 0) F.Add(new FenceLine("volume", FenceLevel.Ok, $"{silent} notes have level 0 (velocity x Volume): silent in FL and in the game"));
        if (target == VocTarget.Gb)
        {
            F.Add(new FenceLine("volume", FenceLevel.Ok, "pulse and noise: 16 levels = the plugin's round(15 x velocity/128 x Volume); wave: full (the plugin's triangle has no volume either)"));
            F.Add(new FenceLine("duty", FenceLevel.Ok, duties.Count == 0 ? "no pulse notes" : $"duties {string.Join(", ", duties.Select(d => new[] { "12.5", "25", "50", "75" }[d] + "%"))}: the Game Boy has all four"));
            F.Add(new FenceLine("noise", FenceLevel.Ok, usedShort ? "Short noise = the 7-bit width" : "long noise (15-bit)"));
            F.Add(new FenceLine("pan", partialPan > 0 ? FenceLevel.Note : FenceLevel.Ok, partialPan > 0 ? $"{partialPan} instances are partly panned: the Game Boy pans hard (left, right or both): |Pan| 50 and more = that side, less = centre" : "hard left / right / centre"));
        }
        if (noiseSlides > 0) F.Add(new FenceLine("slides", FenceLevel.Over, $"noise: {noiseSlides} hits have slides: the noise channel's rate does not glide in the game (the hit keeps its first rate)"));
        if (bends > 0) F.Add(new FenceLine("slides", FenceLevel.Ok, $"{bends} frequency changes (3 bytes each) play FL's slides frame by frame, without a restart"));
        F.Add(new FenceLine("effects", FenceLevel.Ok, "the engine's envelopes, vibrato and sweep are not used (the plugin plays constant notes): nothing to map"));
        F.Add(new FenceLine("chip", FenceLevel.Ok, "the game runs on the real Game Boy sound unit; a sound chip other than GB on an instance is not in the song"));

        Encode(o);
        if (target == VocTarget.Gb)
        {
            int quads = QuadFrames(o).Count;
            if (quads > 0) F.Add(new FenceLine("timing", FenceLevel.Note, $"{quads} frames have something to do on all four channels: the engine decodes at most 3 a frame (noise, wave, pulse 1 first), so pulse 2 comes one frame ({1000 / Fps:0.0} ms) late there; the next wait is a frame shorter (no drift)"));
        }
        for (int c = 0; c < 4; c++)
            if (o.Streams[c].Length > StreamCap) F.Add(new FenceLine("bytes", FenceLevel.Over, $"{Names[c]}: {o.Streams[c].Length} bytes, the engine's stream limit is {StreamCap} (one bank): {o.Streams[c].Length - StreamCap} bytes over: shorten the song or thin its slides"));
        int total = o.Streams.Sum(s => s.Length);
        F.Add(new FenceLine("bytes", FenceLevel.Ok, $"{string.Join(" + ", o.Streams.Select(s => s.Length))} = {total} bytes of song; one stream may be {StreamCap} bytes (a bank), the game's {SongBanks} song banks ({SongBanks * StreamCap} bytes) hold all 22 tracks"));
        if (o.Instruments.Count > 16) F.Add(new FenceLine("instruments", FenceLevel.Over, $"{o.Instruments.Count} instrument rows, the test ROM has room for 16"));
        if (o.Noise.Count > 128) F.Add(new FenceLine("noise", FenceLevel.Over, $"{o.Noise.Count} noise settings, the engine's table holds 128"));
        return o;
    }

    /// <summary>The frames (of the first pass) at which a channel's stream is decoded: frame 0 and the end of every wait.</summary>
    public static SortedSet<int> DecodeFrames(byte[] st, int length)
    {
        var o = new SortedSet<int> { 0 };
        int t = 0;
        for (int i = 0; i < st.Length && t < length;)
        {
            int b = st[i];
            if (b >= 0x81 && b <= 0xBF) { t += b - 0x80; o.Add(t); i++; }
            else if (b == 0xD0) { t += st[i + 1]; o.Add(t); i += 2; }
            else if (b is 0xD1 or 0xD2 or 0xD6) i += 3;
            else if (b is 0xD3 or 0xD5) i += 2;
            else if (b is 0xFE or 0xFF) break;
            else i++;
        }
        o.RemoveWhere(x => x >= length);
        return o;
    }

    /// <summary>The frames where all four channels decode (the driver does at most 3 a frame: SEQ 3, 2, 0, 1 puts pulse 2 off by one frame).</summary>
    public static SortedSet<int> QuadFrames(VocSong s)
    {
        var q = DecodeFrames(s.Streams[0], s.Length);
        for (int c = 1; c < 4; c++) q.IntersectWith(DecodeFrames(s.Streams[c], s.Length));
        return q;
    }

    // ---- the game converter's back end (vocmusic.py Music.encode), byte for byte ----

    private static int InstIndex(VocSong s, VocInstrument inst, int kind)
    {
        int w = 0;
        if (kind == 2)
        {
            var wave = inst.Wave ?? TriangleWave;          // a Game Boy wave preset brings its own 32 samples (the .flp pipeline's presets)
            int k = s.Waves.FindIndex(x => x.AsSpan().SequenceEqual(wave));
            if (k < 0) { s.Waves.Add(wave); k = s.Waves.Count - 1; }
            w = k;
        }
        var row = inst.Row(kind, w);
        int i = s.Instruments.FindIndex(x => x.AsSpan().SequenceEqual(row));
        if (i < 0) { s.Instruments.Add(row); i = s.Instruments.Count - 1; }
        return i;
    }

    private static int NoiseIndexOf(VocSong s, int nr43)
    {
        int v = nr43 & 0xF7;
        int i = s.Noise.IndexOf(v);
        if (i < 0) { s.Noise.Add(v); i = s.Noise.Count - 1; }
        return i;
    }

    public static void Encode(VocSong s)
    {
        s.Instruments.Clear(); s.Waves.Clear(); s.Noise.Clear();
        s.Streams = new byte[4][];
        for (int c = 0; c < 4; c++)
        {
            var ev = s.Ch[c].Select((e, i) => (e, i)).OrderBy(x => x.e.Frame).ThenBy(x => x.i).Select(x => x.e).ToList();
            if (c == 3)
            {
                var outEv = new List<VocEvent>(); int last = 0;
                foreach (var e in ev)
                {
                    int tk = e.Frame;
                    if (e.Op == "on")
                    {
                        double lead = 15.0 / Nr43Clock(e.Value) * Fps;
                        int ld = lead >= 2 ? (int)lead : 0;
                        if (ld != 0) tk = Math.Max(last, tk - ld);
                    }
                    outEv.Add(e with { Frame = tk });
                    last = tk;
                }
                ev = outEv.Select((e, i) => (e, i)).OrderBy(x => x.e.Frame).ThenBy(x => x.i).Select(x => x.e).ToList();
            }
            var o = new List<byte>();
            int t = 0, curVol = -1;
            void Wait(int n)
            {
                while (n > 0)
                {
                    if (n <= 63) { o.Add((byte)(0x80 + n)); n = 0; }
                    else { int k = Math.Min(n, 255); o.Add(0xD0); o.Add((byte)k); n -= k; }
                }
            }
            bool laterSetup = ev.Any(e => e.Frame > 0 && e.Op is "inst" or "pan");
            if (laterSetup && s.Loop) o.Add(0xD4);
            foreach (var grp in ev.GroupBy(e => e.Frame).OrderBy(g => g.Key))
            {
                int tk = grp.Key;
                if (tk >= s.Length) continue;
                (string Op, int V)? note = null; int? freq = null, vol = null, pan = null; VocInstrument? inst = null;
                foreach (var e in grp)
                {
                    if (e.Op is "on" or "pitch" or "off") { note = (e.Op, e.Value); freq = null; }
                    else if (e.Op == "freq") freq = e.Value;
                    else if (e.Op == "vol") vol = e.Value;
                    else if (e.Op == "pan") pan = e.Value;
                    else if (e.Op == "inst") inst = e.Inst;
                }
                Wait(tk - t); t = tk;
                if (inst != null)
                {
                    int ii = InstIndex(s, inst, c);
                    if (tk != 0 || ii != 0 || laterSetup) { o.Add(0xD3); o.Add((byte)ii); }
                }
                if (pan != null) { o.Add(0xD5); o.Add((byte)pan.Value); }
                if (tk == 0 && s.Loop && !laterSetup) o.Add(0xD4);
                if (vol != null && vol != curVol) { o.Add((byte)(0xC0 | vol.Value)); curVol = vol.Value; }
                if (note is { } nt)
                {
                    if (nt.Op == "off") o.Add(0x80);
                    else if (nt.Op == "pitch") { o.Add(0xD1); o.Add((byte)(nt.V & 0xFF)); o.Add((byte)(nt.V >> 8)); }
                    else if (c == 3) o.Add((byte)NoiseIndexOf(s, nt.V));
                    else o.Add((byte)nt.V);
                }
                if (freq != null) { o.Add(0xD6); o.Add((byte)(freq.Value & 0xFF)); o.Add((byte)(freq.Value >> 8)); }
            }
            Wait(s.Length - t);
            o.Add(s.Loop ? (byte)0xFF : (byte)0xFE);
            s.Streams[c] = o.ToArray();
        }
    }

    // ---- the events file the game's roster reads (vocmusic.py events_song) ----

    public static string EventsJson(VocSong s)
    {
        var w = new System.Text.StringBuilder();
        w.Append("{\"format\":\"vocgb-events\",\"version\":1,\"name\":\"").Append(Esc(s.Name)).Append("\",\"title\":\"").Append(Esc(s.Title)).Append("\",\"length\":").Append(s.Length)
         .Append(",\"loop\":").Append(s.Loop ? "true" : "false").Append(",\"channels\":{");
        for (int c = 0; c < 4; c++)
        {
            if (c > 0) w.Append(',');
            w.Append('"').Append(JsonNames[c]).Append("\":[");
            for (int i = 0; i < s.Ch[c].Count; i++)
            {
                var e = s.Ch[c][i];
                if (i > 0) w.Append(',');
                w.Append('[').Append(e.Frame).Append(",\"").Append(e.Op).Append("\",");
                if (e.Inst is { } n) w.Append("{\"duty\":").Append(n.Duty).Append(",\"width\":").Append(n.Width7 ? 7 : 15).Append(",\"wave\":\"triangle\"}");
                else w.Append(e.Value);
                w.Append(']');
            }
            w.Append(']');
        }
        w.Append("}}\n");
        return w.ToString();
    }
    private static string Esc(string x) { var b = new System.Text.StringBuilder(); foreach (char ch in x) { if (ch == '"' || ch == '\\') b.Append('\\').Append(ch); else if (ch < 0x20 || ch > 0x7E) b.Append("\\u").Append(((int)ch).ToString("x4")); else b.Append(ch); } return b.ToString(); }

    // ---- the test ROM: the template (the game's own driver + one reserved song) with this song written in ----

    /// <summary>The template's manifest (key = value lines; offsets in hex). Throws FormatException on anything it does not know.</summary>
    public static Dictionary<string, string> Manifest(string text, string format, int version = 1)
    {
        var d = new Dictionary<string, string>();
        foreach (var raw in text.Split('\n'))
        {
            var l = raw.Trim();
            if (l.Length == 0 || l[0] == '#') continue;
            int eq = l.IndexOf('=');
            if (eq < 0) throw new FormatException("manifest line without '=': " + l);
            d[l[..eq].Trim()] = l[(eq + 1)..].Trim();
        }
        if (d.GetValueOrDefault("format") != format || d.GetValueOrDefault("version") != version.ToString()) throw new FormatException($"not a {format} version {version} manifest");
        return d;
    }

    public static (int Offset, int Size) Region(Dictionary<string, string> m, string key)
    {
        var p = m[key].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return (Convert.ToInt32(p[0], 16), p.Length > 1 ? int.Parse(p[1]) : 0);
    }

    /// <summary>The test ROM: null with the reason when the song does not fit the template.</summary>
    public static byte[]? Patch(byte[] template, string manifest, VocSong s, out string? why)
    {
        why = null;
        var m = Manifest(manifest, "bn2-flp-vocgb");
        if (template.Length.ToString() != m["rom-bytes"]) { why = "the template is not the one its manifest describes"; return null; }
        var rom = (byte[])template.Clone();
        var parts = new List<(string Key, byte[] Data, string What)> { ("instab", s.Instruments.SelectMany(x => x).ToArray(), "instruments"),
            ("wavetab", s.Waves.SelectMany(x => x).ToArray(), "wave tables"), ("noisetab", s.Noise.Select(x => (byte)x).ToArray(), "noise table") };
        for (int c = 0; c < 4; c++) parts.Add(("stream" + c, s.Streams[c], Names[c]));
        foreach (var (key, data, what) in parts)
        {
            var (off, size) = Region(m, key);
            if (data.Length > size) { why = $"{what}: {data.Length} bytes, the test ROM has room for {size}"; return null; }
            Array.Clear(rom, off, size);
            data.CopyTo(rom, off);
        }
        return rom;
    }
}
