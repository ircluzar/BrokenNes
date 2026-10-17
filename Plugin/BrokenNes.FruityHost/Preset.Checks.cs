// Round 6 (presets): the checks of the preset file, the folder library, the engines' snapshots and the repository hygiene rule. Two ways to run them: `FruityHost preset-test [--only id,id] [--no-mutants]` (no plugin DLL needed) and the `presets` check of `selftest`
// (these, then the plugin DLL: chips, direct-mode playback equal to Runaway, projects, the Presets tab). Real games are used when the user's library is on this machine and reported as "not tested" when it is not; nothing the checks make is committed.
// Every check is shown able to fail: a deliberate bug (PresetMutants, or a change in what the check is fed) per check, which that check must catch.
using System.Diagnostics;
using System.Text;
using BrokenNes2;
using BrokenNes2.Instruments;
using BrokenNes2.Presets;

namespace BrokenNes.FruityHost;

internal static partial class PresetChecks
{
    public sealed record Check(string Id, string What, Action<ImodelReport> Run);

    public static readonly Check[] All =
    [
        new("file-roundtrip", "every instrument of every snapshot (made-up and real), clean and corrupted, goes into a preset file and comes back equal; the bytes are the same every time; the history replays to the instrument", FileRoundTrip),
        new("file-refusals", "a preset file that is cut short at any length, has any bit flipped, is not a preset, is newer, is too big or whose parts disagree (with a valid checksum) is refused with a reason", FileRefusals),
        new("file-forward", "the reserved note-behaviour block and unknown parts survive a read and a write unchanged; limits on every text are enforced", FileForward),
        new("store-ops", "the folder library: add (no overwrite), list, search by name / tag / console / game, rename, delete, export, import (duplicates, damaged, newer, oversize and foreign files refused), ids that leave the folder refused", StoreOps),
        new("play-builds", "every instrument of every snapshot (clean and corrupted) builds a playable preset, or says why not; the NES DMC says it cannot play yet and every other NES kind builds", PlayBuilds),
        new("play-equals-runaway", "a preset played on a private engine renders the same PCM as Runaway playing that instrument from the game it was ripped from (same note, same velocity): bit-exact on the Genesis, Master System, Game Gear and Game Boy, SNES with the game's old echo cleared", PlayEqualsRunaway),
        new("play-alloc", "the engines built from presets allocate nothing while notes play", PlayAlloc),
        new("hygiene", "no *.bn2preset file is tracked by git and the ignore rule is in place (ripped presets hold copyrighted samples and must never be committed)", Hygiene),
    ];

    private static void RealNote(ImodelReport r)
    {
        var real = ImodelFixtures.Real();
        if (ImodelFixtures.RealMissing.Count > 0) r.Warn("not tested, game not available: " + string.Join("; ", ImodelFixtures.RealMissing));
        if (real.Count == 0) r.Warn("no real game available: only the made-up snapshots were tested");
    }

    private static IEnumerable<PresetSource> Sources(ImodelReport r)
    {
        RealNote(r);
        return PresetFixtures.All();
    }

    // =====================================================================================================================================================================
    // the file
    // =====================================================================================================================================================================

    public static void FileRoundTrip(ImodelReport r)
    {
        int made = 0; long bytes = 0;
        foreach (var src in Sources(r))
            foreach (var inst in PresetFixtures.Instruments(src))
                foreach (int steps in new[] { 0, 3 })
                {
                    if (steps > 0 && !inst.Editable) continue;                                  // a ROM instrument of the YM2413 cannot be corrupted
                    string what = $"{src.Name} {inst.Kind} #{inst.Index} after {steps} steps";
                    var d = PresetFixtures.Make(src, inst, steps, (ulong)inst.Index + 5);
                    var file = PresetFile.TryWrite(d, out string? why);
                    r.Expect(file != null, $"{what}: could not write the preset: {why}");
                    if (file == null) continue;
                    made++; bytes += file.Length;
                    var back = PresetFile.Read(file);
                    r.Expect(back.Ok, $"{what}: could not read it back: {back.Problem}");
                    if (back.Data == null) continue;
                    var b = back.Data;
                    r.Expect(b.Name == d.Name && b.Notes == d.Notes && b.SourceGame == d.SourceGame && b.SourceSha1 == d.SourceSha1 && b.CreatedUnix == d.CreatedUnix && b.Tags.SequenceEqual(d.Tags) && b.Console == d.Console && b.Kind == d.Kind && b.Index == d.Index && b.InstrumentName == d.InstrumentName, $"{what}: the header changed on the way");
                    r.Expect(b.Instrument.AsSpan().SequenceEqual(d.Instrument) && b.Clean.AsSpan().SequenceEqual(d.Clean) && b.History.AsSpan().SequenceEqual(d.History) && b.Context.AsSpan().SequenceEqual(d.Context), $"{what}: a part changed on the way");
                    r.Expect(b.Steps == steps && b.FieldCount == inst.Fields.Length && b.BlobCount == inst.Blobs.Length, $"{what}: the counts are {b.Steps}/{b.FieldCount}/{b.BlobCount}");
                    // the same bytes every time, from the data and from what was read
                    var again = PresetFile.TryWrite(d, out _);
                    var rewritten = PresetFile.TryWrite(b, out _);
                    r.Expect(again != null && again.AsSpan().SequenceEqual(file) && rewritten != null && rewritten.AsSpan().SequenceEqual(file), $"{what}: writing it again gave other bytes");
                    // the instrument is the one the history leads to, and the corruption can be replayed from the clean one
                    var u = InstrumentCodec.Unpack(b.Instrument, out _)!;
                    var h = InstrumentHistory.Deserialize(b.History, out string? hp);
                    r.Expect(h != null, $"{what}: the history does not read: {hp}");
                    if (h == null) continue;
                    var replay = h.Replay(h.Cursor);
                    r.Expect(replay.SameValues(u) && h.Current.SameValues(u) && h.ReplayMatchesSaved, $"{what}: replaying the saved history from the clean instrument does not give the saved instrument");
                    r.Expect(u.SameValues(PresetFixtures.Corrupt(inst, steps, (ulong)inst.Index + 5).Current), $"{what}: the saved instrument is not the corrupted one");
                    var clean = InstrumentCodec.Unpack(b.Clean, out _)!;
                    r.Expect(clean.SameValues(inst), $"{what}: the clean part is not the instrument the game had");
                }
        r.Note($"{made} presets (clean and corrupted, {bytes / 1024.0:0} KiB in all) written, read back equal and rewritten to the same bytes");
        r.Expect(made > 100 || ImodelFixtures.Real().Count == 0 && made > 20, $"only {made} presets were made: the check proves little");
    }

    private static byte[] OneFile(bool real, int steps = 2)
    {
        var src = (real ? PresetFixtures.Real().FirstOrDefault(s => s.Name == "smw") : null) ?? PresetFixtures.Synthetic().First(s => s.Name == "snes-samples");
        var inst = PresetFixtures.Instruments(src)[1];
        return PresetFile.TryWrite(PresetFixtures.Make(src, inst, steps, 3), out _)!;
    }

    public static void FileRefusals(ImodelReport r)
    {
        var good = OneFile(false);
        r.Expect(PresetFile.Read(good).Ok, "the good file is not read: the check would prove nothing");
        // cut short at every length
        int cut = 0;
        for (int n = 0; n < good.Length; n++) { var rd = PresetFile.Read(good.AsSpan(0, n).ToArray()); r.Expect(!rd.Ok && !string.IsNullOrEmpty(rd.Problem), $"a file cut to {n} of {good.Length} bytes was accepted"); cut++; }
        // every bit flipped (every byte, one bit each of 8 in turn)
        int flips = 0;
        for (int i = 0; i < good.Length; i++)
            for (int bit = 0; bit < 8; bit += (i % 7 == 0 ? 1 : 8))
            {
                var hit = (byte[])good.Clone(); hit[i] ^= (byte)(1 << bit);
                var rd = PresetFile.Read(hit);
                r.Expect(!rd.Ok && !string.IsNullOrEmpty(rd.Problem), $"a flipped bit {bit} of byte {i} was accepted");
                flips++;
            }
        r.Note($"{cut} truncations and {flips} flipped bits of a {good.Length}-byte preset all refused");
        // empty, garbage, another file type
        foreach (var (name, bytes) in new (string, byte[])[] { ("empty", []), ("zeros", new byte[300]), ("text", Encoding.UTF8.GetBytes("this is not a preset file at all, only text")), ("png header", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 73, 72, 68, 82, 0, 0, 0, 1]) })
        {
            var rd = PresetFile.Read(bytes);
            r.Expect(!rd.Ok && !string.IsNullOrEmpty(rd.Problem), $"{name} was accepted");
        }
        var rng = new Random(11);
        for (int i = 0; i < 300; i++) { var junk = new byte[rng.Next(1, 600)]; rng.NextBytes(junk); r.Expect(!PresetFile.Read(junk).Ok, "random bytes were accepted"); }
        // too big: refused by size, without being read as a preset
        var big = new byte[PresetFile.MaxFileBytes + 1];
        var rb = PresetFile.Read(big);
        r.Expect(!rb.Ok && rb.Problem!.Contains("too big"), $"a file over the size limit was not refused for its size: '{rb.Problem}'");
        // newer format, an unknown feature flag, with a valid checksum
        var (hdr, blobs) = PresetRaw.Split(good);
        var newer = PresetRaw.Build(2, 0, hdr, blobs);
        var rn = PresetFile.Read(newer);
        r.Expect(!rn.Ok && rn.Problem!.Contains("newer"), $"a preset of a newer format must be refused as newer: '{rn.Problem}'");
        var flagged = PresetRaw.Build(1, 1, hdr, blobs);
        var rf = PresetFile.Read(flagged);
        r.Expect(!rf.Ok && rf.Problem!.Contains("feature"), $"a preset with an unknown feature flag must be refused: '{rf.Problem}'");
        r.Expect(!PresetFile.Read(PresetRaw.Build(0, 0, hdr, blobs)).Ok, "format 0 was accepted");
        // damaged in ways a checksum cannot see (the checksum is fixed after the damage)
        void Refuse(string what, byte[] file, string? mention = null)
        {
            var rd = PresetFile.Read(file);
            r.Expect(!rd.Ok && !string.IsNullOrEmpty(rd.Problem) && (mention == null || rd.Problem!.Contains(mention, StringComparison.OrdinalIgnoreCase)), $"{what}: " + (rd.Ok ? "accepted" : $"refused as '{rd.Problem}', expected it to mention '{mention}'"));
        }
        string json = Encoding.UTF8.GetString(hdr);
        Refuse("a header that is not JSON", PresetRaw.Build(1, 0, Encoding.UTF8.GetBytes("{not json"), blobs), "header");
        Refuse("a header that is an array", PresetRaw.Build(1, 0, Encoding.UTF8.GetBytes("[1,2]"), blobs), "object");
        Refuse("an empty name", PresetRaw.WithHeader(good, h => h.Replace("\"name\":\"" + Name(h) + "\"", "\"name\":\"\"")), "name");
        Refuse("a name of 5000 characters", PresetRaw.WithHeader(good, h => h.Replace("\"name\":\"" + Name(h) + "\"", "\"name\":\"" + new string('x', 5000) + "\"")), "name");
        Refuse("an unknown console", PresetRaw.WithHeader(good, h => h.Replace("\"console\":\"SNES\"", "\"console\":\"PS2\"")), "console");
        Refuse("a console that is not the instrument's", PresetRaw.WithHeader(good, h => h.Replace("\"console\":\"SNES\"", "\"console\":\"MD\"")));
        Refuse("a kind that is not the instrument's", PresetRaw.WithHeader(good, h => h.Replace("\"kindId\":1,", "\"kindId\":10,")));
        Refuse("an instrument number that is not the instrument's", PresetRaw.WithHeader(good, h => h.Replace("\"index\":1,", "\"index\":2,")));
        Refuse("a SHA-1 that is not hexadecimal", PresetRaw.WithHeader(good, h => h.Replace("\"sha1\":\"", "\"sha1\":\"zz")), "SHA-1");
        Refuse("a header of another format", PresetRaw.WithHeader(good, h => h.Replace("\"format\":1", "\"format\":2")), "format");
        Refuse("a header that lies about its length", PresetRaw.Build(1, 0, hdr, blobs, headerLengthLie: hdr.Length + 5));
        Refuse("a header over the limit", PresetRaw.Build(1, 0, Encoding.UTF8.GetBytes("{\"x\":\"" + new string('y', PresetFile.MaxHeaderBytes) + "\"}"), blobs), "header");
        Refuse("a missing instrument", PresetRaw.WithoutBlob(good, PresetFile.TagInstrument), "missing");
        Refuse("a missing clean instrument", PresetRaw.WithoutBlob(good, PresetFile.TagClean), "missing");
        Refuse("a missing history", PresetRaw.WithoutBlob(good, PresetFile.TagHistory), "missing");
        Refuse("a missing context", PresetRaw.WithoutBlob(good, PresetFile.TagContext), "missing");
        Refuse("an instrument part of garbage", PresetRaw.WithBlob(good, PresetFile.TagInstrument, b => new byte[b.Length]), "instrument");
        Refuse("an instrument part cut short", PresetRaw.WithBlob(good, PresetFile.TagInstrument, b => b.AsSpan(0, b.Length / 2).ToArray()), "instrument");
        Refuse("a clean part of garbage", PresetRaw.WithBlob(good, PresetFile.TagClean, b => new byte[b.Length]), "clean");
        Refuse("a history of garbage", PresetRaw.WithBlob(good, PresetFile.TagHistory, b => new byte[b.Length]), "history");
        Refuse("a context of the wrong size", PresetRaw.WithBlob(good, PresetFile.TagContext, b => b.AsSpan(0, 64).ToArray()), "context");
        Refuse("a part twice", PresetRaw.Build(1, 0, hdr, [.. blobs, blobs[0]]), "twice");
        Refuse("bytes after the last part", PresetRaw.Build(1, 0, hdr, blobs, trailing: [1, 2, 3]), "after");
        Refuse("a part that is longer than the file", PresetRaw.Build(1, 0, hdr, [.. blobs, (0x31585858u, new byte[0])]).Take(100).Concat(new byte[10]).ToArray());
        Refuse("a note part that holds data at version 0", PresetRaw.WithBlob(good, PresetFile.TagNote, _ => [0, 0, 9, 9]), "note");
        // a history that belongs to another instrument (valid on its own, with a valid checksum)
        var otherSrc = PresetFixtures.Synthetic().First(s => s.Name == "snes-samples");
        var otherInst = PresetFixtures.Instruments(otherSrc)[2];
        var other = PresetFile.TryWrite(PresetFixtures.Make(otherSrc, otherInst, 2, 9), out _)!;
        var otherHistory = PresetRaw.Split(other).Blobs.First(b => b.Tag == PresetFile.TagHistory).Data;
        Refuse("a history of another instrument", PresetRaw.WithBlob(good, PresetFile.TagHistory, _ => otherHistory), "history");
        // a history that starts from the right clean instrument but leads somewhere else than the instrument (a different seed)
        var goodSrc = PresetFixtures.Synthetic().First(s => s.Name == "snes-samples");
        var cleanInst = PresetFixtures.Instruments(goodSrc)[1];
        var altHistory = PresetFixtures.Corrupt(cleanInst, 2, 77).Serialize();
        Refuse("a history that leads to another instrument than the one saved", PresetRaw.WithBlob(good, PresetFile.TagHistory, _ => altHistory), "history");
        // the writer refuses what the reader would refuse
        var bad = PresetFixtures.Make(goodSrc, cleanInst, 1, 4); bad.Name = "";
        r.Expect(PresetFile.TryWrite(bad, out string? w1) == null && w1 != null, "an empty name was written");
        bad = PresetFixtures.Make(goodSrc, cleanInst, 1, 4); bad.Console = ConsoleId.Genesis;
        r.Expect(PresetFile.TryWrite(bad, out string? w2) == null && w2 != null, "a console that is not the instrument's was written");
        bad = PresetFixtures.Make(goodSrc, cleanInst, 1, 4); bad.Context = [1, 2, 3];
        r.Expect(PresetFile.TryWrite(bad, out string? w3) == null && w3 != null, "a context of the wrong size was written");
    }

    private static string Name(string headerJson)
    {
        int a = headerJson.IndexOf("\"name\":\"", StringComparison.Ordinal) + 8;
        return headerJson[a..headerJson.IndexOf('"', a)];
    }

    public static void FileForward(ImodelReport r)
    {
        var src = PresetFixtures.Synthetic().First(s => s.Name == "snes-samples");
        var inst = PresetFixtures.Instruments(src)[1];
        var d = PresetFixtures.Make(src, inst, 2, 3);
        var plain = PresetFile.TryWrite(d, out _)!;
        // the reserved block: this version writes version 0, empty
        var note = PresetRaw.Split(plain).Blobs.First(b => b.Tag == PresetFile.TagNote).Data;
        r.Expect(note.Length == 2 && note[0] == 0 && note[1] == 0, "the reserved note-behaviour block is not version 0, empty");
        var rd = PresetFile.Read(plain);
        r.Expect(rd.Ok && rd.Data!.NoteVersion == 0 && !rd.Data.HasNoteBehaviour, "a version-0 preset reports note behaviour");
        // a later version's note behaviour: read, kept, written back unchanged, and said to be there
        var withNote = PresetRaw.WithHeader(PresetRaw.WithBlob(plain, PresetFile.TagNote, _ => [5, 0, 1, 2, 3, 4, 5, 6, 7, 8]), h => h.Replace("\"noteBehaviour\":0", "\"noteBehaviour\":5"));
        var rn = PresetFile.Read(withNote);
        r.Expect(rn.Ok && rn.Data!.NoteVersion == 5 && rn.Data.NoteBehaviour.SequenceEqual(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }) && rn.Data.HasNoteBehaviour, "a note-behaviour block of version 5 was not kept");
        var again = rn.Ok ? PresetFile.TryWrite(rn.Data!, out _) : null;
        r.Expect(again != null && again.AsSpan().SequenceEqual(withNote), "a note-behaviour block of version 5 did not survive a rewrite byte for byte");
        // an unknown part: kept
        var withUnknown = PresetRaw.Build(1, 0, PresetRaw.Split(plain).Header, [.. PresetRaw.Split(plain).Blobs, (0x31585A5Au, new byte[] { 9, 8, 7 })]);
        var ru = PresetFile.Read(withUnknown);
        r.Expect(ru.Ok && ru.Data!.Unknown.Count == 1 && ru.Data.Unknown[0].Data.SequenceEqual(new byte[] { 9, 8, 7 }), "an unknown part was dropped when reading");
        var uw = ru.Ok ? PresetFile.TryWrite(ru.Data!, out _) : null;
        r.Expect(uw != null && uw.AsSpan().SequenceEqual(withUnknown), "an unknown part did not survive a rewrite byte for byte");
        // limits
        void Limit(string what, Action<PresetData> edit)
        {
            var x = PresetFixtures.Make(src, inst, 1, 3); edit(x);
            var bytes = PresetFile.TryWrite(x, out string? why);
            r.Expect(bytes == null && !string.IsNullOrEmpty(why), $"{what} was written");
        }
        Limit("a name of 81 characters", x => x.Name = new string('n', PresetFile.MaxName + 1));
        Limit("notes over the limit", x => x.Notes = new string('n', PresetFile.MaxNotes + 1));
        Limit("25 tags", x => x.Tags = Enumerable.Range(0, 25).Select(i => "t" + i).ToArray());
        Limit("an empty tag", x => x.Tags = [""]);
        Limit("a control character in the name", x => x.Name = "bad\u0001name");
        Limit("a SHA-1 that is not hexadecimal", x => x.SourceSha1 = "not hex");
        Limit("a part over the limit", x => x.Unknown.Add((0x31585A5Au, new byte[PresetFile.MaxBlobBytes + 1])));
        Limit("a file over the limit", x => { x.Unknown.Add((0x31585A5Au, new byte[PresetFile.MaxBlobBytes])); x.Unknown.Add((0x32585A5Au, new byte[PresetFile.MaxBlobBytes])); x.Unknown.Add((0x33585A5Au, new byte[PresetFile.MaxBlobBytes])); });
        var longName = PresetFixtures.Make(src, inst, 1, 3); longName.Name = new string('é', PresetFile.MaxName);
        var rl = PresetFile.TryWrite(longName, out _);
        r.Expect(rl != null && PresetFile.Read(rl).Data?.Name == longName.Name, "a name of exactly the limit (non-ASCII) did not round trip");
        // texts that need escaping in JSON
        var tricky = PresetFixtures.Make(src, inst, 1, 3, "Quote \" slash \\ tab-less <b>&amp; émoji 🎹");
        tricky.Notes = "line one\nline two\t\"quoted\"";
        var rt = PresetFile.TryWrite(tricky, out _);
        var back = rt == null ? default : PresetFile.Read(rt);
        r.Expect(back.Data != null && back.Data.Name == tricky.Name && back.Data.Notes == tricky.Notes, "a name and notes with quotes, backslashes, newlines and an emoji did not round trip");
    }

    // =====================================================================================================================================================================
    // the library
    // =====================================================================================================================================================================

    private sealed class TempDir : IDisposable
    {
        public readonly string Path;
        public TempDir() { Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bn2_presets_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path); }
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    public static void StoreOps(ImodelReport r)
    {
        using var tmp = new TempDir();
        string folder = Path.Combine(tmp.Path, "lib");
        var store = new PresetStore(folder);
        store.Refresh(force: true);
        r.Expect(store.Entries.Count == 0 && !Directory.Exists(folder), "a library whose folder does not exist must be empty and must not create the folder by looking");

        var snes = PresetFixtures.Synthetic().First(s => s.Name == "snes-samples");
        var md = PresetFixtures.Synthetic().First(s => s.Name == "opn");
        var gb = PresetFixtures.Synthetic().First(s => s.Name == "gb-dmg");
        PresetData P(PresetSource s, int index, int steps, string name) => PresetFixtures.Make(s, PresetFixtures.Instruments(s)[index], steps, 5, name);
        long v0 = store.Version;
        var a = store.Add(P(snes, 0, 0, "Square lead"));
        r.Expect(a.Ok && a.Id != null && File.Exists(Path.Combine(folder, a.Id.Replace('/', Path.DirectorySeparatorChar))), $"add: {a.Message}");
        r.Expect(store.Version > v0 && store.Entries.Count == 1, "the version did not move when a preset was added");
        r.Expect(a.Id!.StartsWith("Made-up SNES game/"), $"a preset goes in the folder of its source game: {a.Id}");
        // the same name twice: both kept
        var a2 = store.Add(P(snes, 1, 2, "Square lead"));
        r.Expect(a2.Ok && a2.Id != a.Id && store.Entries.Count == 2, $"a second preset with the same name must be kept next to the first: {a2.Message}");
        var bytesA = File.ReadAllBytes(Path.Combine(folder, a.Id.Replace('/', Path.DirectorySeparatorChar)));
        var b = store.Add(P(md, 0, 3, "Electric piano"));
        var tagged = P(gb, 2, 0, "Wave bass"); tagged.Tags = [.. tagged.Tags, "chiptune-lead"];
        var c = store.Add(tagged);
        r.Expect(b.Ok && c.Ok && store.Entries.Count == 4, "four presets are in the library");
        r.Expect(PresetFile.Checksum(File.ReadAllBytes(Path.Combine(folder, a.Id.Replace('/', Path.DirectorySeparatorChar)))) == PresetFile.Checksum(bytesA), "adding another preset changed the first one's file");

        // the list and its facts
        var e = store.Entries.First(x => x.Id == b.Id);
        r.Expect(e.Name == "Electric piano" && e.Console == ConsoleId.Genesis && e.ConsoleName == "MD" && e.Corrupted && e.Problem == null && e.Category == "Made-up Genesis game", $"the entry shows '{e.Name}' {e.ConsoleName} corrupted {e.Corrupted} in '{e.Category}'");
        r.Expect(store.Entries.Select(x => x.Category).SequenceEqual(store.Entries.Select(x => x.Category).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)), "the list is not sorted by category");

        // search
        int Count(string text, string? console) => store.Search(text, console).Count;
        r.Expect(Count("", null) == 4, $"search for nothing found {Count("", null)}");
        r.Expect(Count("square", null) == 2 && Count("SQUARE lead", null) == 2, "search by name (any case, several words)");
        r.Expect(Count("piano", null) == 1 && Count("piano lead", null) == 0, "all words must match");
        r.Expect(Count("corrupted", null) == 2 && Count("clean", null) == 2, $"search by tag: corrupted {Count("corrupted", null)}, clean {Count("clean", null)}");
        r.Expect(Count("", "SNES") == 2 && Count("", "MD") == 1 && Count("", "GB") == 1 && Count("", "SMS") == 0 && Count("", "NES") == 0, "the console filter");
        r.Expect(Count("lead", "MD") == 0 && Count("piano", "MD") == 1, "text and console together");
        r.Expect(Count("genesis", null) == 1 && Count("wave", null) == 1, "search by source game and by instrument");
        r.Expect(Count("chiptune", null) == 1 && Count("chiptune-lead", "GB") == 1 && Count("chiptune", "MD") == 0, "search by a tag that is in no other field");

        // ids that leave the folder, or are not presets
        foreach (var bad in new[] { "", "../x.bn2preset", "a/../../x.bn2preset", "..\\x.bn2preset", "C:/x.bn2preset", "/x.bn2preset", "\\x.bn2preset", "x.txt", "a//b.bn2preset", "./x.bn2preset", "a\0b.bn2preset" })
            r.Expect(store.PathOf(bad) == null, $"the id '{bad.Replace("\0", "\\0")}' was accepted");
        r.Expect(store.PathOf(a.Id) != null, "a good id was refused");
        r.Expect(!store.Delete("../outside.bn2preset").Ok && !store.Rename("../outside.bn2preset", "x").Ok && !store.Export("../outside.bn2preset", Path.Combine(tmp.Path, "x.bn2preset")).Ok, "an id outside the folder was acted on");

        // load
        var l = store.Load(a2.Id!, out var loaded);
        r.Expect(l.Ok && l.Data!.Name == "Square lead" && loaded != null && loaded.Length > 0, $"load: {l.Problem}");

        // rename
        var rn = store.Rename(a2.Id!, "Square lead, crushed");
        r.Expect(rn.Ok && rn.Id != null && rn.Id != a2.Id, $"rename: {rn.Message}");
        r.Expect(!File.Exists(Path.Combine(folder, a2.Id!.Replace('/', Path.DirectorySeparatorChar))) && store.Entries.Count == 4 && store.Entries.Any(x => x.Name == "Square lead, crushed"), "rename: the old file is still there or the entry is not renamed");
        var rl = store.Load(rn.Id!, out _);
        r.Expect(rl.Ok && rl.Data!.Name == "Square lead, crushed" && rl.Data.Instrument.AsSpan().SequenceEqual(l.Data!.Instrument) && rl.Data.History.AsSpan().SequenceEqual(l.Data.History), "rename changed more than the name");
        r.Expect(!store.Rename(rn.Id!, "  ").Ok && !store.Rename(rn.Id!, new string('x', 200)).Ok && !store.Rename(rn.Id!, "bad\u0002").Ok, "a bad new name was accepted");
        var same = store.Rename(rn.Id!, "Square lead, crushed");
        r.Expect(same.Ok && same.Id == rn.Id && store.Entries.Count == 4, $"renaming to the same name must keep the file: {same.Message} ({same.Id})");
        var caseOnly = store.Rename(rn.Id!, "SQUARE LEAD, CRUSHED");
        r.Expect(caseOnly.Ok && store.Entries.Count == 4 && store.Entries.Any(x => x.Name == "SQUARE LEAD, CRUSHED"), $"a rename of capitals only: {caseOnly.Message}");

        // export, then import into another library
        string outFile = Path.Combine(tmp.Path, "out", "piano");
        Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
        var ex = store.Export(b.Id!, outFile);
        r.Expect(ex.Ok && File.Exists(outFile + ".bn2preset"), $"export (the extension is added): {ex.Message}");
        var exported = File.ReadAllBytes(outFile + ".bn2preset");
        r.Expect(exported.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(folder, b.Id!.Replace('/', Path.DirectorySeparatorChar)))), "the exported file is not byte for byte the library's");
        var other = new PresetStore(Path.Combine(tmp.Path, "lib2"));
        var im = other.Import(outFile + ".bn2preset");
        r.Expect(im.Ok && other.Entries.Count == 1 && other.Entries[0].Name == "Electric piano", $"import: {im.Message}");
        r.Expect(File.ReadAllBytes(Path.Combine(other.Root, im.Id!.Replace('/', Path.DirectorySeparatorChar))).AsSpan().SequenceEqual(exported), "the imported file is not byte for byte the exported one");
        var dup = other.Import(outFile + ".bn2preset");
        r.Expect(!dup.Ok && other.Entries.Count == 1 && dup.Message.Contains("already"), $"importing the same preset twice must be refused: {dup.Message}");

        // imports that must be refused, with nothing copied
        int before = other.Entries.Count;
        void RefuseImport(string what, byte[] bytes, string? mention = null)
        {
            string f = Path.Combine(tmp.Path, "imp_" + Guid.NewGuid().ToString("N") + ".bn2preset");
            File.WriteAllBytes(f, bytes);
            var res = other.Import(f);
            r.Expect(!res.Ok && (mention == null || res.Message.Contains(mention, StringComparison.OrdinalIgnoreCase)), $"import of {what}: " + (res.Ok ? "accepted" : $"refused as '{res.Message}', expected '{mention}'"));
            r.Expect(other.Entries.Count == before && Directory.EnumerateFiles(other.Root, "*", SearchOption.AllDirectories).Count(x => x.EndsWith(".bn2preset")) == before, $"import of {what}: something was copied into the library");
        }
        RefuseImport("a truncated preset", exported.AsSpan(0, exported.Length - 10).ToArray());
        var flipped = (byte[])exported.Clone(); flipped[40] ^= 0x10;
        RefuseImport("a damaged preset", flipped, "damaged");
        RefuseImport("a newer preset", PresetRaw.Build(3, 0, PresetRaw.Split(exported).Header, PresetRaw.Split(exported).Blobs), "newer");
        RefuseImport("a text file", Encoding.UTF8.GetBytes("hello"), "not a BrokenNes2 preset");
        RefuseImport("an oversize file", new byte[PresetFile.MaxFileBytes + 1000], "too big");
        r.Expect(!other.Import(Path.Combine(tmp.Path, "does-not-exist.bn2preset")).Ok && !other.Import("").Ok, "a file that is not there was imported");
        // a preset whose parts disagree (valid checksum) is refused by the deep read, not only by the checksum
        RefuseImport("a preset whose history belongs to another instrument", PresetRaw.WithBlob(exported, PresetFile.TagHistory, h => PresetRaw.Split(PresetFile.TryWrite(P(snes, 0, 2, "x"), out _)!).Blobs.First(x => x.Tag == PresetFile.TagHistory).Data), "history");

        // a damaged file inside the library is listed, with its reason, and the rest is fine
        string dmg = Path.Combine(folder, "Broken");
        Directory.CreateDirectory(dmg);
        File.WriteAllBytes(Path.Combine(dmg, "Damaged one.bn2preset"), flipped);
        File.WriteAllBytes(Path.Combine(dmg, "Not a preset.bn2preset"), Encoding.UTF8.GetBytes("text"));
        store.Refresh(force: true);
        var damaged = store.Entries.Where(x => x.Category == "Broken").ToList();
        r.Expect(damaged.Count == 2 && damaged.All(x => x.Problem != null && x.Console == null && x.Detail.StartsWith("cannot be opened")), "a damaged file must be listed with its reason");
        r.Expect(store.Entries.Count == 6 && store.Entries.Count(x => x.Problem == null) == 4, "a damaged file hid or broke the others");
        var ld = store.Load(damaged[0].Id, out _);
        r.Expect(!ld.Ok && !string.IsNullOrEmpty(ld.Problem), "loading a damaged file did not say why not");
        r.Expect(!store.Export(damaged[0].Id, Path.Combine(tmp.Path, "d.bn2preset")).Ok, "a damaged file was exported");
        r.Expect(Count("", "SNES") == 4 && Count("", "GB") == 3, "a damaged file must show under every console filter so that it is not hidden");   // 2 real SNES + 2 damaged; 1 GB + 2 damaged
        // a file added behind our back shows up
        long v1 = store.Version;
        File.Copy(outFile + ".bn2preset", Path.Combine(folder, "Dropped in.bn2preset"));
        store.Refresh(force: true);
        r.Expect(store.Version > v1 && store.Entries.Any(x => x.Id == "Dropped in.bn2preset"), "a file copied into the folder by hand did not appear");
        // delete
        var del = store.Delete("Dropped in.bn2preset");
        r.Expect(del.Ok && !File.Exists(Path.Combine(folder, "Dropped in.bn2preset")) && !store.Entries.Any(x => x.Id == "Dropped in.bn2preset"), $"delete: {del.Message}");
        r.Expect(!store.Delete("Dropped in.bn2preset").Ok, "deleting a preset that is gone must say so");
        r.Expect(Directory.EnumerateFiles(tmp.Path, ".tmp-*", SearchOption.AllDirectories).Count() == 0 && Directory.EnumerateFiles(tmp.Path, "*.tmp-*", SearchOption.AllDirectories).Count() == 0, "a temporary file was left behind");
        r.Note($"4 presets added, searched 9 ways, renamed 3 ways, exported, imported and refused 7 ways, a damaged file listed with its reason, a hand-copied file noticed, deleted");
    }

    // =====================================================================================================================================================================
    // the hygiene rule
    // =====================================================================================================================================================================

    /// <summary>The repository root (the folder with .git above the working directory, or BROKENNES_REPO), or null.</summary>
    internal static string? RepoRoot()
    {
        string? env = Environment.GetEnvironmentVariable("BROKENNES_REPO");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(env)) return env;
        for (var d = new DirectoryInfo(Directory.GetCurrentDirectory()); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, ".git")) || Directory.Exists(Path.Combine(d.FullName, ".git"))) return d.FullName;
        return null;
    }

    private static (int Exit, string Out) Git(string repo, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = repo };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, o.Trim());
    }

    /// <summary>The failures of the rule in <paramref name="repo"/>: a tracked preset file, an untracked one the ignore rule does not cover, or no rule at all.</summary>
    internal static List<string> HygieneFailures(string repo)
    {
        var bad = new List<string>();
        var (e1, tracked) = Git(repo, "ls-files", "--", "*.bn2preset");
        if (e1 != 0) { bad.Add("git ls-files failed"); return bad; }
        foreach (var f in tracked.Split('\n', StringSplitOptions.RemoveEmptyEntries)) bad.Add("tracked by git: " + f.Trim());
        var (e2, untracked) = Git(repo, "ls-files", "--others", "--exclude-standard", "--", "*.bn2preset");
        foreach (var f in untracked.Split('\n', StringSplitOptions.RemoveEmptyEntries)) bad.Add("in the tree and not ignored (the next commit would take it): " + f.Trim());
        var (e3, _) = Git(repo, "check-ignore", "-q", "--no-index", "Some Game/A Ripped Instrument.bn2preset");
        if (e3 != 0) bad.Add("the ignore rule for *.bn2preset is missing: git would not ignore a preset");
        return bad;
    }

    public static void Hygiene(ImodelReport r)
    {
        string? repo = RepoRoot();
        if (repo == null) { r.Warn("not tested: no git repository above the working directory (set BROKENNES_REPO)"); return; }
        var bad = HygieneFailures(repo);
        r.Expect(bad.Count == 0, "the repository breaks the rule: " + string.Join("; ", bad));
        // the check is shown able to fail: a scratch repository with a tracked preset and no rule
        string scratch = Path.Combine(Path.GetTempPath(), "bn2_hygiene_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(scratch);
            Git(scratch, "init", "-q");
            var ruleless = HygieneFailures(scratch);
            r.Expect(ruleless.Any(x => x.Contains("is missing")),"a repository with no ignore rule passed the hygiene check (it said: " + string.Join("; ", ruleless) + ")");
            File.WriteAllBytes(Path.Combine(scratch, "stray.bn2preset"), [1, 2, 3]);
            Git(scratch, "add", "-f", "stray.bn2preset");
            Git(scratch, "-c", "user.name=check", "-c", "user.email=check@example.invalid", "commit", "-q", "-m", "a preset");
            var tracked = HygieneFailures(scratch);
            r.Expect(tracked.Any(x => x.StartsWith("tracked by git")), "a repository with a tracked preset file passed the hygiene check");
            File.Move(Path.Combine(scratch, "stray.bn2preset"), Path.Combine(scratch, "loose.bn2preset"));
            Git(scratch, "rm", "-q", "--cached", "-f", "stray.bn2preset");
            var untracked = HygieneFailures(scratch);
            r.Expect(untracked.Any(x => x.StartsWith("in the tree")), "a preset file left in the tree without an ignore rule passed the hygiene check");
            File.WriteAllText(Path.Combine(scratch, ".gitignore"), "*.bn2preset\n");
            var fixedUp = HygieneFailures(scratch);
            r.Expect(fixedUp.Count == 0, "a repository with the ignore rule and no preset tracked failed the hygiene check: " + string.Join("; ", fixedUp));
            r.Note("the hygiene check fails on a scratch repository with no rule, with a tracked preset, with a loose preset, and passes once the rule is there");
        }
        finally { try { foreach (var f in Directory.EnumerateFiles(scratch, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal); Directory.Delete(scratch, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    // =====================================================================================================================================================================
    // mutants and the runner
    // =====================================================================================================================================================================

    public sealed record Mutant(string Name, int Id, string CatchingCheck, Action<ImodelReport> Run);

    public static readonly Mutant[] Mutants =
    [
        new("the file's checksum is not checked", PresetMutants.CrcNotChecked, "file-refusals", FileRefusals),
        new("a history that does not lead to the instrument is not noticed", PresetMutants.HistoryNotChecked, "file-refusals", FileRefusals),
        new("the library overwrites a preset with the same name", PresetMutants.LibraryOverwrites, "store-ops", StoreOps),
        new("import skips the deep read", PresetMutants.ImportSkipsValidation, "store-ops", StoreOps),
        new("search ignores tags", PresetMutants.SearchIgnoresTags, "store-ops", StoreOps),
        new("rename keeps the old name", PresetMutants.RenameKeepsOldName, "store-ops", StoreOps),
        new("delete leaves the file", PresetMutants.DeleteKeepsFile, "store-ops", StoreOps),
        new("export writes half the file", PresetMutants.ExportWritesWrong, "store-ops", StoreOps),
        new("the SNES envelope is not set on the engine", PresetMutants.SnesEnvelopeDropped, "play-equals-runaway", PlayEqualsRunaway),
        new("the SNES tuning is not set on the engine", PresetMutants.SnesTuningDropped, "play-equals-runaway", PlayEqualsRunaway),
        new("the Genesis engine plays the instrument after the one asked for", PresetMutants.GenesisPatchIndexOff, "play-equals-runaway", PlayEqualsRunaway),
        new("the Game Boy duty is not set on the engine", PresetMutants.GbDutyDropped, "play-equals-runaway", PlayEqualsRunaway),
    ];

    public sealed record CheckOutcome(string Id, string What, bool Passed, double Seconds, ImodelReport Report);

    public static List<CheckOutcome> RunChecks(string? only, Action<string>? log = null)
    {
        var list = new List<CheckOutcome>();
        foreach (var c in All)
        {
            if (only != null && !only.Split(',').Contains(c.Id)) continue;
            var r = new ImodelReport();
            var sw = Stopwatch.StartNew();
            try { c.Run(r); }
            catch (Exception e) { r.Failures.Add($"exception {e.GetType().Name}: {e.Message} @ {e.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}"); }
            var o = new CheckOutcome(c.Id, c.What, r.Failures.Count == 0, sw.Elapsed.TotalSeconds, r);
            list.Add(o);
            log?.Invoke($"  {(o.Passed ? (r.Warnings.Count > 0 ? "WARN" : "PASS") : "FAIL")}  {c.Id,-22} {r.Assertions,7} assertions  [{o.Seconds:0.0}s]" + (r.Notes.Count > 0 ? "\n        " + string.Join("\n        ", r.Notes) : "") + (r.Warnings.Count > 0 ? "\n        warning: " + string.Join("\n        warning: ", r.Warnings) : "") + (r.Failures.Count > 0 ? "\n        FAILED: " + string.Join("\n        FAILED: ", r.Failures.Take(8)) + (r.Failures.Count > 8 ? $"\n        ... and {r.Failures.Count - 8} more" : "") : ""));
        }
        return list;
    }

    public static (List<string> Lines, int Missed) RunMutants(string? only)
    {
        var lines = new List<string>();
        int missed = 0;
        foreach (var m in Mutants)
        {
            if (only != null && !only.Split(',').Contains(m.CatchingCheck)) continue;
            var r = new ImodelReport();
            PresetMutants.Active = m.Id;
            try { m.Run(r); }
            catch (Exception e) { r.Failures.Add($"exception {e.GetType().Name}: {e.Message}"); }
            finally { PresetMutants.Active = 0; }
            bool caught = r.Failures.Count > 0;
            if (!caught) missed++;
            lines.Add($"  {(caught ? "caught" : "MISSED")}  [{m.CatchingCheck}] {m.Name}: {r.Failures.Count} failing assertions" + (caught ? $", e.g. {Trim(r.Failures[0])}" : ""));
        }
        foreach (var m in TestMutants)
        {
            if (only != null && !only.Split(',').Contains(m.CatchingCheck)) continue;
            var r = new ImodelReport();
            try { m.Run(r); }
            catch (Exception e) { r.Failures.Add($"exception {e.GetType().Name}: {e.Message}"); }
            bool caught = r.Failures.Count > 0;
            if (!caught) missed++;
            lines.Add($"  {(caught ? "caught" : "MISSED")}  [{m.CatchingCheck}] {m.Name}: {r.Failures.Count} failing assertions" + (caught ? $", e.g. {Trim(r.Failures[0])}" : ""));
        }
        return (lines, missed);
    }

    private static string Trim(string s) => s.Length <= 170 ? s : s[..167] + "...";

    /// <summary>`FruityHost preset-test [--only id,id] [--no-mutants]`</summary>
    public static int Main(string[] args)
    {
        string? only = null;
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--only") only = args[i + 1];
        bool mutants = !args.Contains("--no-mutants");
        Console.WriteLine("BrokenNes.FruityHost preset-test (the preset file, the folder library, the engines' snapshots, the repository rule; no plugin DLL involved)");
        var results = RunChecks(only, Console.WriteLine);
        int failed = results.Count(o => !o.Passed), missed = 0;
        if (mutants)
        {
            Console.WriteLine("mutants (each must be caught by the check named):");
            var (lines, m) = RunMutants(only);
            foreach (var l in lines) Console.WriteLine(l);
            missed = m;
        }
        Console.WriteLine($"{results.Count - failed} checks passed, {failed} failed, {results.Sum(o => o.Report.Assertions)} assertions; {missed} mutants missed");
        return failed == 0 && missed == 0 ? 0 : 1;
    }
}
