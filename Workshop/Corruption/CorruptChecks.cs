using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using BrokenNes2;
using NesEmulator.Corruption;
using NesEmulator.Snes;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Corruption;

/// <summary>
/// --corrupt-test: the checks of the corruption library. Each check that can be fooled has a control next to it: the same check run on a sabotaged input, which it must catch. A golden hash here
/// says "the output did not change since it was recorded", not "the output is right"; the claims about behaviour are the other checks.
/// </summary>
internal static class CorruptChecks
{
    // recorded from this build's own output (see the notes on golden hashes above)
    private const ulong GoldenFormat = 0xB9E93D96A2EF7597UL, GoldenBlast = 0x44B0D16D9CDC0C31UL;

    public static int Run(string[] args)
    {
        var c = new Checks();
        string only = args.SkipWhile(a => a != "--only").Skip(1).FirstOrDefault() ?? "";
        bool Want(string g) => only.Length == 0 || only.Split(',').Contains(g);

        string smw = args.SkipWhile(a => a != "--smw").Skip(1).FirstOrDefault()
            ?? Environment.GetEnvironmentVariable("BROKENNES_TEST_SMW")
            ?? @"C:\Users\philt\OneDrive\Documents\PROJECTS\!!! Experiments\AI SMW Hacker\smw.smc";

        var rom = SynthSnesRom.Build();
        Console.WriteLine($"the synthetic SNES game: {rom.Length:N0} bytes, sha1 {AnchorStart.Sha1Hex(rom)}");
        // --write-synth-rom <file>: write the generated game to a file (for tests that need a ROM file: the desktop UAT), then stop
        int wi = Array.IndexOf(args, "--write-synth-rom");
        if (wi >= 0 && wi + 1 < args.Length) { File.WriteAllBytes(args[wi + 1], rom); Console.WriteLine($"written: {args[wi + 1]}"); return 0; }

        if (Want("format")) { Section("stockpile file format"); Guard(c, "format", () => Format(c)); }
        if (Want("legacy")) { Section("older stockpile files (the Glitch Harvester's export, Lite's export)"); Guard(c, "legacy", () => Legacy(c)); }
        if (Want("hostile")) { Section("damaged and hostile files"); Guard(c, "hostile", () => Hostile(c)); }
        if (Want("input")) { Section("input scripts and the recorder"); Guard(c, "input", () => Input(c)); }
        if (Want("synth")) { Section("the synthetic game itself (the other checks stand on it)"); Guard(c, "synth", () => Synth(c, rom)); }
        if (Want("domains")) { Section("memory domains (SNES)"); Guard(c, "domains", () => Domains(c, rom)); }
        if (Want("apply")) { Section("applying and reverting writes"); Guard(c, "apply", () => Apply(c, rom)); }
        if (Want("blast")) { Section("generating corruptions (farming)"); Guard(c, "blast", () => Blast(c, rom)); }
        if (Want("anchors")) { Section("anchors: state, boot, none, the alternative, the refusals (a test machine)"); Guard(c, "anchors", () => Anchors(c)); }
        if (Want("boot")) { Section("boot anchor: determinism of the replay"); Guard(c, "boot", () => Boot(c, rom)); }
        if (Want("audio")) { Section("an SPC700 audio-RAM corruption changes the sound, deterministically, and what Instrument Runaway captures"); Guard(c, "audio", () => Audio(c, rom)); }
        if (Want("engine")) { Section("the Glitch Harvester engine on a console game: farm, stockpile file, start on another machine (the desktop's part, headless)"); Guard(c, "engine", () => CorruptEngineChecks.Run(c, rom)); }
        if (Want("nes")) { Section("the NES path of the engine is unchanged, and its entries travel in the new file"); Guard(c, "nes", () => CorruptEngineChecks.Nes(c)); }
        if (Want("cli")) { Section("--corrupt-apply"); Guard(c, "cli", () => Cli(c, rom)); }
        if (Want("state")) { Section("state anchors on the SNES (run once the SNES has save states)"); Guard(c, "state", () => SnesState(c, rom)); }
        if (Want("smw")) { Section("a real game (Super Mario World), when its ROM is present"); Guard(c, "smw", () => Smw(c, smw)); }
        return c.Finish();
    }

    private static void Section(string name) { Console.WriteLine(); Console.WriteLine("== " + name); }

    /// <summary>A group that throws is a failed group (and the other groups still run), not a crashed run.</summary>
    private static void Guard(Checks c, string group, Action run)
    {
        try { run(); }
        catch (Exception e) { c.Ok($"group '{group}' ran to the end", false, e.GetType().Name + ": " + e.Message); }
    }

    // ===================================================================================================================== format

    private static Stockpile Fixture()
    {
        var t = new DateTime(2026, 10, 17, 10, 0, 0, DateTimeKind.Utc);
        var s = new Stockpile { Name = "fixture", Created = t, Writer = "test" };
        var bat = new byte[100]; for (int i = 0; i < bat.Length; i++) bat[i] = (byte)(i * 7);
        var st = new byte[300]; for (int i = 0; i < st.Length; i++) st[i] = (byte)(i % 13);
        s.Entries.Add(new StockpileEntry
        {
            Id = "00000000-0000-0000-0000-000000000001", Name = "Boot entry", Description = "flip some drum bits", Created = t,
            Tags = { "snes", "drums" },
            Game = new GameRef { Console = "snes", Sha1 = "6b47bb75d16514b6a476aa0c73a683a2a4c18765", Title = "TEST", File = "test.sfc" },
            Machine = new MachineSpec { Cpu = "SFC", Ppu = "SFC", Apu = "SFC" },
            Anchor = new Anchor { Kind = AnchorKind.Boot, Frames = 240, Inputs = "100:A,160:", Battery = bat, Alt = new Anchor { Kind = AnchorKind.State, Console = "snes", StateFormat = "snes-1", StateData = st, Frame = 240 } },
            Writes = { new CorruptionWrite("ARAM", 1280, 0x11, 0x77), new CorruptionWrite("DSP", 0x4C, 1, 0, 30) },
            Source = new BlastSource { Blast = "BITFLIP", Intensity = 2, Seed = 1234, Domains = { "ARAM", "DSP" }, SizeModel = "true" },
        });
        s.Entries.Add(new StockpileEntry
        {
            Id = "00000000-0000-0000-0000-000000000002", Name = "NES state \"quoted\" \u00e9", Created = t,
            Game = new GameRef { Console = "nes", Sha256 = "abc123", Title = "g", File = "g.nes" },
            Anchor = new Anchor { Kind = AnchorKind.State, Console = "nes", StateFormat = "nes-json", StateData = Encoding.UTF8.GetBytes("{\"romHash\":\"abc123\"}") },
            Writes = { new CorruptionWrite("RAM", 16, 255), new CorruptionWrite("PRG", 70000, 1, 2) },
        });
        s.Entries.Add(new StockpileEntry { Id = "00000000-0000-0000-0000-000000000003", Name = "None", Created = t, Game = new GameRef { Console = "gb", Sha1 = "ff" }, Anchor = new Anchor { Kind = AnchorKind.None } });
        s.Bases.Add(new StockpileBase { Id = "b1", Name = "a base", Anchor = new Anchor { Kind = AnchorKind.Boot, Frames = 10, Inputs = "5:Start" } });
        s.Bases.Add(new StockpileBase { Id = "unused", Name = "not written", Anchor = new Anchor { Kind = AnchorKind.None } });
        s.Entries.Add(new StockpileEntry { Id = "00000000-0000-0000-0000-000000000004", Name = "Ref", Created = t, Game = new GameRef { Console = "snes", Sha1 = "aa" }, Anchor = new Anchor { Kind = AnchorKind.Ref, BaseId = "b1" }, Writes = { new CorruptionWrite("WRAM", 5, 6) } });
        return s;
    }

    private static string Why(Stockpile a, Stockpile b)
    {
        if (a.Entries.Count != b.Entries.Count) return $"{a.Entries.Count} vs {b.Entries.Count} entries";
        for (int i = 0; i < a.Entries.Count; i++)
        {
            var x = a.Entries[i]; var y = b.Entries[i];
            if (x.Id != y.Id || x.Name != y.Name || x.Description != y.Description || x.Created.ToUniversalTime() != y.Created.ToUniversalTime() || !x.Tags.SequenceEqual(y.Tags)) return $"entry {i}: identity";
            if (x.Game.Console != y.Game.Console || x.Game.Sha1 != y.Game.Sha1 || x.Game.Sha256 != y.Game.Sha256 || x.Game.Title != y.Game.Title || x.Game.File != y.Game.File) return $"entry {i}: game";
            if (x.Machine.Cpu != y.Machine.Cpu || x.Machine.Ppu != y.Machine.Ppu || x.Machine.Apu != y.Machine.Apu || x.Machine.Note != y.Machine.Note) return $"entry {i}: machine";
            if (!x.Writes.SequenceEqual(y.Writes)) return $"entry {i}: writes";
            var wa = WhyAnchor(x.Anchor, y.Anchor); if (wa != "") return $"entry {i}: anchor {wa}";
            if ((x.Source == null) != (y.Source == null)) return $"entry {i}: source";
            if (x.Source != null && (x.Source.Blast != y.Source!.Blast || x.Source.Intensity != y.Source.Intensity || x.Source.Seed != y.Source.Seed || x.Source.SizeModel != y.Source.SizeModel || !x.Source.Domains.SequenceEqual(y.Source.Domains))) return $"entry {i}: source";
        }
        var used = a.Bases.Where(bb => a.Entries.Any(e => e.Anchor.Kind == AnchorKind.Ref && e.Anchor.BaseId == bb.Id)).ToList();
        if (used.Count != b.Bases.Count) return $"{used.Count} used bases vs {b.Bases.Count}";
        for (int i = 0; i < used.Count; i++) { var w = WhyAnchor(used[i].Anchor, b.Bases[i].Anchor); if (w != "" || used[i].Id != b.Bases[i].Id) return "base " + i + " " + w; }
        return "";
    }

    private static string WhyAnchor(Anchor x, Anchor y)
    {
        if (x.Kind != y.Kind || x.Console != y.Console || x.StateFormat != y.StateFormat || x.Frame != y.Frame || x.Frames != y.Frames || x.Inputs != y.Inputs || x.Inputs2 != y.Inputs2 || x.BaseId != y.BaseId) return "fields";
        if (!x.StateData.AsSpan().SequenceEqual(y.StateData)) return "state bytes";
        if (!(x.Battery ?? Array.Empty<byte>()).AsSpan().SequenceEqual(y.Battery ?? Array.Empty<byte>())) return "battery bytes";
        if ((x.Alt == null) != (y.Alt == null)) return "alt";
        return x.Alt == null ? "" : WhyAnchor(x.Alt, y.Alt!);
    }

    private static void Format(Checks c)
    {
        var s = Fixture();
        string json1 = StockpileJson.Write(s);
        var r = StockpileJson.Read(json1);
        c.Ok("the file reads back", r.Ok && r.Refusals.Count == 0 && !r.Legacy, $"{json1.Length:N0} bytes");
        string json2 = r.Stockpile != null ? StockpileJson.Write(r.Stockpile) : "";
        c.Ok("write, read, write gives the same bytes", json1 == json2);
        string why = r.Stockpile != null ? Why(s, r.Stockpile) : "no stockpile";
        c.Ok("every field survives the round trip (entries, game, machine, anchors with state/battery/alt, writes with was/at, source, shared bases)", why == "", why);
        c.Ok("a base nothing points to is not written", r.Stockpile != null && r.Stockpile.Bases.Count == 1 && r.Stockpile.Bases[0].Id == "b1");
        int lines = json1.Split('\n').Count(l => l.TrimStart().StartsWith("{\"domain\""));
        c.Ok("one line per write", lines == s.Entries.Sum(e => e.Writes.Count), $"{lines} write lines of {s.Entries.Sum(e => e.Writes.Count)}: " + string.Join(" | ", json1.Split('\n').Where(l => l.Contains("\"domain\"")).Take(3).Select(l => l.Trim())));
        c.Ok("the summary line is written", json1.Contains("2 writes: ARAM x1, DSP x1"));
        c.Ok("no path is written (the file name only)", !json1.Contains(":\\") && !json1.Contains("\\\\"));

        ulong h = Hash.Fnv(json1);
        c.Ok("golden: the fixture's bytes are as recorded", h == GoldenFormat, $"hash {h:X16}" + (GoldenFormat == 0 ? " (not recorded yet)" : ""));

        // controls: the comparison must see a changed write, a changed anchor byte, a dropped alt; the golden must move with a changed field
        var mutated = StockpileJson.Read(json1).Stockpile!;
        mutated.Entries[0].Writes[0] = mutated.Entries[0].Writes[0] with { Value = 0x12 };
        c.Control("a changed write value is seen by the comparison", Why(s, mutated) != "");
        mutated = StockpileJson.Read(json1).Stockpile!;
        mutated.Entries[0].Anchor.Alt!.StateData[5] ^= 1;
        c.Control("one flipped bit in a saved state is seen", Why(s, mutated) != "");
        mutated = StockpileJson.Read(json1).Stockpile!;
        mutated.Entries[0].Anchor.Alt = null;
        c.Control("a dropped alternative anchor is seen", Why(s, mutated) != "");
        var s2 = Fixture(); s2.Entries[0].Description += "!";
        c.Control("the golden hash moves when a field changes", Hash.Fnv(StockpileJson.Write(s2)) != h);

        // content hash: ignores name and description, sees writes, anchor and game
        var e0 = Fixture().Entries[0]; string ch = e0.ContentHash();
        var e1 = Fixture().Entries[0]; e1.Name = "other"; e1.Description = "other";
        var e2 = Fixture().Entries[0]; e2.Writes[0] = e2.Writes[0] with { Value = 0 };
        var e3 = Fixture().Entries[0]; e3.Anchor.Inputs = "101:A,161:";
        c.Ok("content hash ignores the name and description", e1.ContentHash() == ch);
        c.Ok("content hash sees a write, and the anchor", e2.ContentHash() != ch && e3.ContentHash() != ch);

        // the file on disk
        string dir = Path.Combine(Path.GetTempPath(), "bn-corrupt-test-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "t" + Stockpile.FileSuffix);
        StockpileJson.SaveFile(path, s);
        var disk = StockpileJson.LoadFile(path);
        c.Ok("SaveFile / LoadFile through the file system (no .tmp left behind)", disk.Ok && Why(s, disk.Stockpile!) == "" && !File.Exists(path + ".tmp"));
        c.Ok("a file that is not there is refused with a message", StockpileJson.LoadFile(Path.Combine(dir, "nope.json")) is { Ok: false } nr && nr.Refusals.Count == 1 && nr.Refusals[0].Contains("does not exist"));
    }

    // ===================================================================================================================== legacy

    private const string NesStateJson = "{\"cycleRemainder\":0,\"ram\":\"AAAA\",\"romHash\":\"DEADBEEF00\",\"romName\":\"C:\\\\Users\\\\someone\\\\Games\\\\Kirby.nes\",\"romPath\":\"C:\\\\Users\\\\someone\\\\Games\\\\Kirby.nes\"}";

    private static void Legacy(Checks c)
    {
        string state = NesStateJson.Replace("\\", "\\\\").Replace("\"", "\\\"");
        // the engine's export: indented, PascalCase, "State"
        string engine = "[\n  {\n    \"Id\": \"11111111-1111-1111-1111-111111111111\",\n    \"Name\": \"Entry 1\",\n    \"BaseStateId\": \"b\",\n    \"Created\": \"2026-09-01T10:00:00Z\",\n" +
                        "    \"Writes\": [ { \"Domain\": \"RAM\", \"Address\": 16, \"Value\": 255 }, { \"Domain\": \"PRG\", \"Address\": 70000, \"Value\": 3 } ],\n    \"State\": \"" + state + "\"\n  }\n]";
        // Lite's export: compact, "BaseState"
        string lite = "[{\"Id\":\"22222222-2222-2222-2222-222222222222\",\"Name\":\"Entry 2\",\"BaseStateId\":\"b\",\"Created\":\"2026-09-02T10:00:00Z\",\"Writes\":[{\"Domain\":\"CHR\",\"Address\":7,\"Value\":9}],\"BaseState\":\"" + state + "\"}]";
        string stateless = "[{\"Id\":\"33333333-3333-3333-3333-333333333333\",\"Name\":\"Entry 3\",\"BaseStateId\":\"b\",\"Created\":\"2026-09-03T10:00:00Z\",\"Writes\":[{\"Domain\":\"RAM\",\"Address\":1,\"Value\":2}]}]";

        var re = StockpileJson.Read(engine);
        var e = re.Stockpile?.Entries.FirstOrDefault();
        c.Ok("the engine's export is read (older file, a warning says so)", re.Ok && re.Legacy && re.Warnings.Any(w => w.Contains("older")) && e != null && e.Writes.Count == 2 && e.Writes[1] == new CorruptionWrite("PRG", 70000, 3));
        c.Ok("its machine state becomes a NES state anchor holding the original text", e != null && e.Anchor.Kind == AnchorKind.State && e.Anchor.Console == "nes" && e.Anchor.StateFormat == "nes-json" && Encoding.UTF8.GetString(e.Anchor.StateData).Contains("DEADBEEF00"));
        c.Ok("the game identity comes from the state (hash lower-cased, file name without the path)", e != null && e.Game.Sha256 == "deadbeef00" && e.Game.File == "Kirby.nes" && e.Game.Title == "Kirby" && e.Game.Console == "nes");
        string rewritten = e != null ? StockpileJson.Write(re.Stockpile!) : "";
        using (var rd = System.Text.Json.JsonDocument.Parse(rewritten))
        {
            var game = rd.RootElement.GetProperty("entries")[0].GetProperty("game");
            c.Ok("rewriting it in the current format keeps the file name only in the entry's game block (no path)", game.GetProperty("file").GetString() == "Kirby.nes" && !game.ToString().Contains("someone"));
        }
        c.Ok("...and the rewritten file reads back to the same entry", e != null && Why(re.Stockpile!, StockpileJson.Read(rewritten).Stockpile!) == "");

        var rl = StockpileJson.Read(lite);
        var le = rl.Stockpile?.Entries.FirstOrDefault();
        c.Ok("Lite's export (BaseState) is read with its machine state", rl.Ok && le != null && le.Anchor.Kind == AnchorKind.State && le.Legacy == "" && le.Writes.Count == 1);
        // the control: what the older reader did with it (only "State" was read): no state, and Replay did nothing
        using (var doc = System.Text.Json.JsonDocument.Parse(lite))
        {
            bool oldReaderFindsState = doc.RootElement[0].TryGetProperty("State", out _);
            c.Control("the older reader (which looked for \"State\" only) would have found no state in Lite's file", !oldReaderFindsState);
        }
        var rs = StockpileJson.Read(stateless);
        var se = rs.Stockpile?.Entries.FirstOrDefault();
        c.Ok("an entry with no machine state is read as 'none' and flagged, with a warning (never a silent no-op)", se != null && se.Anchor.Kind == AnchorKind.None && se.Legacy == "no-state" && rs.Warnings.Any(w => w.Contains("carries no saved machine")));
        c.Ok("the flag survives a rewrite", StockpileJson.Read(StockpileJson.Write(rs.Stockpile!)).Stockpile!.Entries[0].Legacy == "no-state");
    }

    // ===================================================================================================================== hostile

    private static string Doc(string entries, string head = "\"format\":\"brokennes-stockpile\",\"version\":2") => "{" + head + ",\"entries\":[" + entries + "]}";
    private const string GoodEntry = "{\"id\":\"g\",\"name\":\"good\",\"game\":{\"console\":\"snes\",\"sha1\":\"aa\"},\"anchor\":{\"kind\":\"none\"},\"writes\":[{\"domain\":\"WRAM\",\"address\":1,\"value\":2}]}";

    private static void Hostile(Checks c)
    {
        var good = StockpileJson.Read(Doc(GoodEntry));
        c.Control("a valid stockpile has no refusals and one entry (so the refusals below are the files' fault)", good.Ok && good.Refusals.Count == 0 && good.Stockpile!.Entries.Count == 1);

        var cut = StockpileJson.Read(Doc(GoodEntry)[..^15]);
        c.Ok("a truncated file is refused with a message", !cut.Ok && cut.Refusals.Count == 1 && cut.Refusals[0].Contains("not valid JSON"), cut.Refusals.FirstOrDefault() ?? "");
        var other = StockpileJson.Read("{\"format\":\"something-else\",\"version\":2,\"entries\":[]}");
        c.Ok("another JSON file is refused", !other.Ok && other.Refusals[0].Contains("not a BrokenNes stockpile"));
        var newer = StockpileJson.Read(Doc(GoodEntry, "\"format\":\"brokennes-stockpile\",\"version\":99"));
        c.Ok("a newer version is refused and says to update", !newer.Ok && newer.Refusals[0].Contains("newer") && newer.Refusals[0].Contains("99"), newer.Refusals.FirstOrDefault() ?? "");
        c.Ok("not an object nor an array is refused", !StockpileJson.Read("42").Ok && !StockpileJson.Read("").Ok);

        string bad = "{\"id\":\"x\",\"name\":\"bad value\",\"writes\":[{\"domain\":\"WRAM\",\"address\":1,\"value\":300}]}";
        var rb = StockpileJson.Read(Doc(GoodEntry + "," + bad));
        c.Ok("an entry with a write value that is not a byte is refused; the good entry is read", rb.Ok && rb.Stockpile!.Entries.Count == 1 && rb.Refusals.Count == 1 && rb.Refusals[0].Contains("not a byte"), rb.Refusals.FirstOrDefault() ?? "");
        string badDomain = "{\"id\":\"y\",\"name\":\"bad domain\",\"writes\":[{\"domain\":\"W RAM; drop\",\"address\":1,\"value\":3}]}";
        var rd = StockpileJson.Read(Doc(badDomain + "," + GoodEntry));
        c.Ok("a domain that is not an id is refused", rd.Stockpile!.Entries.Count == 1 && rd.Refusals.Count == 1 && rd.Refusals[0].Contains("not a domain id"));
        string negative = "{\"id\":\"z\",\"name\":\"neg\",\"writes\":[{\"domain\":\"WRAM\",\"address\":-5,\"value\":3}]}";
        c.Ok("a negative address is refused", StockpileJson.Read(Doc(negative + "," + GoodEntry)).Refusals.Count == 1);
        string badAnchor = "{\"id\":\"a\",\"name\":\"weird\",\"anchor\":{\"kind\":\"teleport\"}}";
        c.Ok("an anchor kind this build does not know is refused", StockpileJson.Read(Doc(badAnchor + "," + GoodEntry)).Refusals.Any(r => r.Contains("kind this build does not know")));
        string badB64 = "{\"id\":\"b\",\"name\":\"b64\",\"anchor\":{\"kind\":\"state\",\"console\":\"snes\",\"encoding\":\"deflate-base64\",\"data\":\"!!!not base64!!!\"}}";
        var rbb = StockpileJson.Read(Doc(badB64 + "," + GoodEntry));
        c.Ok("a damaged saved state is refused, the rest is read", rbb.Stockpile!.Entries.Count == 1 && rbb.Refusals.Any(r => r.Contains("damaged")));
        string noBase = "{\"id\":\"c\",\"name\":\"orphan\",\"anchor\":{\"kind\":\"ref\",\"base\":\"missing\"}}";
        c.Ok("an entry pointing to a base the file does not contain is refused", StockpileJson.Read(Doc(noBase + "," + GoodEntry)).Refusals.Any(r => r.Contains("shared base")));

        // too many writes: one more than allowed
        var sb = new StringBuilder("{\"id\":\"big\",\"name\":\"huge\",\"writes\":[");
        for (int i = 0; i <= StockpileJson.MaxWritesPerEntry; i++) { if (i > 0) sb.Append(','); sb.Append("{\"domain\":\"A\",\"address\":0,\"value\":0}"); }
        sb.Append("]}");
        var rh = StockpileJson.Read(Doc(sb + "," + GoodEntry));
        c.Ok($"an entry with more than {StockpileJson.MaxWritesPerEntry:N0} writes is refused; the good entry is read", rh.Stockpile!.Entries.Count == 1 && rh.Refusals.Any(r => r.Contains("writes, more than")));

        // a deflate bomb: 70 MB of zeros in a few tens of KB
        string bomb;
        using (var z = new MemoryStream())
        {
            using (var d = new DeflateStream(z, CompressionLevel.Optimal, true)) { var zero = new byte[1 << 20]; for (int i = 0; i < 70; i++) d.Write(zero, 0, zero.Length); }
            bomb = Convert.ToBase64String(z.ToArray());
        }
        string bombEntry = "{\"id\":\"bomb\",\"name\":\"bomb\",\"anchor\":{\"kind\":\"state\",\"console\":\"snes\",\"encoding\":\"deflate-base64\",\"data\":\"" + bomb + "\"}}";
        var rbomb = StockpileJson.Read(Doc(bombEntry + "," + GoodEntry));
        c.Ok($"a saved state that inflates past {StockpileJson.MaxStateBytes / 1048576} MB is refused ({bomb.Length / 1024:N0} KB of text)", rbomb.Stockpile!.Entries.Count == 1 && rbomb.Refusals.Any(r => r.Contains("inflates past the limit")));
        c.Ok("(and the same file with a state under the limit is accepted: the limit is what refused it)", StockpileJson.Read(Doc(MakeStateEntry(5 * 1024 * 1024))).Stockpile!.Entries.Count == 1);
    }

    private static string MakeStateEntry(int zeros)
    {
        using var z = new MemoryStream();
        using (var d = new DeflateStream(z, CompressionLevel.Optimal, true)) d.Write(new byte[zeros], 0, zeros);
        return "{\"id\":\"ok\",\"name\":\"fits\",\"anchor\":{\"kind\":\"state\",\"console\":\"snes\",\"encoding\":\"deflate-base64\",\"data\":\"" + Convert.ToBase64String(z.ToArray()) + "\"}}";
    }

    // ===================================================================================================================== input

    private static void Input(Checks c)
    {
        string text = "60:Start,90:Right+A,120:";
        c.Ok("a script parses and formats back to itself", InputScript.TryParse(text, out var sc, out _) && InputScript.Format(sc) == text && sc.Count == 3 && sc[1].Bits == (ushort)(PadButtons.Right | PadButtons.A));
        c.Ok("a script with a bad button or frame is refused with a message", !InputScript.TryParse("60:Fly", out _, out string e1) && e1.Contains("not a button") && !InputScript.TryParse("x:A", out _, out string e2) && e2.Contains("frame:buttons"));
        InputScript.TryParse(text, out sc, out _);
        var pl = new InputScript.Player(sc);
        bool same = true;
        for (int f = 0; f < 200; f++) same &= pl.Bits(f) == InputScript.BitsAt(sc, f);
        c.Ok("the walking player and the binary search give the same pad for frames 0 to 199", same);
        c.Ok("the pad is 0 before the first entry, Start at 60 to 89, Right+A at 90 to 119, 0 from 120", InputScript.BitsAt(sc, 59) == 0 && InputScript.BitsAt(sc, 60) == (ushort)PadButtons.Start && InputScript.BitsAt(sc, 89) == (ushort)PadButtons.Start && InputScript.BitsAt(sc, 90) == (ushort)(PadButtons.Right | PadButtons.A) && InputScript.BitsAt(sc, 120) == 0);

        var rec = new InputRecorder(16);
        for (int f = 0; f < 200; f++) rec.Record(f is >= 60 and < 90 ? (ushort)PadButtons.Start : f is >= 90 and < 120 ? (ushort)(PadButtons.Right | PadButtons.A) : (ushort)0);
        c.Ok("the recorder writes exactly the script the pad followed", rec.ToScript() == text && rec.Frames == 200 && rec.Poisoned == null, rec.ToScript());
        c.Control("a different pad history is a different script", new Func<bool>(() => { var r2 = new InputRecorder(16); for (int f = 0; f < 200; f++) r2.Record(f is >= 61 and < 90 ? (ushort)PadButtons.Start : (ushort)0); return r2.ToScript() != text; })());

        var full = new InputRecorder(4);
        for (int f = 0; f < 20; f++) full.Record((ushort)(f % 2));
        c.Ok("a full log poisons the recording with a reason (never silently truncated)", full.Poisoned is { } p && p.Contains("full"), full.Poisoned ?? "");
        full.Reset();
        c.Ok("Reset starts again", full.Poisoned == null && full.Frames == 0 && full.Changes == 0);
        full.Poison("a core was swapped"); full.Poison("later reason");
        c.Ok("the first reason is the one kept", full.Poisoned == "a core was swapped");

        var big = new InputRecorder(4096);
        for (int i = 0; i < 1000; i++) big.Record((ushort)(i & 1));      // warm up
        big.Reset();
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100_000; i++) big.Record(i < 3000 ? (ushort)(i & 1) : (ushort)0);
        long a1 = GC.GetAllocatedBytesForCurrentThread();
        c.Ok("recording allocates nothing (it runs on an audio thread): 100,000 frames, 3,000 changes", a1 == a0, $"{a1 - a0} bytes");
    }

    // ===================================================================================================================== synth

    private static void Synth(Checks c, byte[] rom)
    {
        using var r = new SnesRun(rom);
        c.Ok("the synthetic ROM is a LoROM the loader accepts", r.Session.Title.StartsWith("BN CORRUPTION SYNTH"), r.Session.Title);
        r.Run(120);
        c.Ok("its sound program was uploaded through the boot loader and runs (ARAM holds the program and the instrument)",
             r.Spc.Aram[SynthSnesRom.AramProgram] == 0x8F && r.Spc.Aram[SynthSnesRom.AramSample] == 0x93 && r.Spc.Aram[SynthSnesRom.AramDirectory + 1] == 0x05,
             $"ARAM[$0200]={r.Spc.Aram[0x200]:X2} [$0500]={r.Spc.Aram[0x500]:X2}");
        c.Ok("the one-note instrument sounds (2 kHz square, peak over 1000)", r.Peak > 1000, $"peak {r.Peak}");
        c.Ok("the S-DSP was set up by the SPC700 (DIR=4, voice 0 pitch $1000, FLG=$20)", r.Spc.Dsp.Regs[0x5D] == 4 && r.Spc.Dsp.Regs[0x02] == 0 && r.Spc.Dsp.Regs[0x03] == 0x10 && r.Spc.Dsp.Regs[0x6C] == 0x20,
             $"DIR={r.Spc.Dsp.Regs[0x5D]:X2} PITCH={r.Spc.Dsp.Regs[0x03]:X2}{r.Spc.Dsp.Regs[0x02]:X2} FLG={r.Spc.Dsp.Regs[0x6C]:X2} program {BitConverter.ToString(r.Spc.Aram, 0x200, 24)} pc=${r.Spc.Smp.PC:X4}");
        byte c0 = r.Snes.Board.Wram[0x12]; r.Run(1); byte c1 = r.Snes.Board.Wram[0x12];
        c.Ok("the 65816's loop is running (its counter in work RAM moves from one frame to the next)", c0 != c1, $"{c0} -> {c1}");
        using var held = new SnesRun(rom);
        held.Run(120, f => f >= 60 ? PadButtons.A : PadButtons.None);
        c.Ok("a held button reaches work RAM ($10 = the pad's low byte, the sum $13-$14 grows) and the SPC700 (its counter $12-$13 grows)",
             held.Snes.Board.Wram[0x10] == 0x80 && (held.Snes.Board.Wram[0x13] | held.Snes.Board.Wram[0x14]) != 0 && (held.Spc.Aram[0x12] | held.Spc.Aram[0x13]) != 0,
             $"$10={held.Snes.Board.Wram[0x10]:X2} sum={held.Snes.Board.Wram[0x13] | held.Snes.Board.Wram[0x14] << 8} spc counter={held.Spc.Aram[0x12] | held.Spc.Aram[0x13] << 8}");
        c.Control("with no button held the SPC700's counter stays 0", (r.Spc.Aram[0x12] | r.Spc.Aram[0x13]) == 0);
    }

    // ===================================================================================================================== domains

    private static void Domains(Checks c, byte[] rom)
    {
        using var r = new SnesRun(rom);
        r.Run(4);
        var d = MachineDomains.For(r.Session);
        string ids = string.Join(",", d.Select(x => x.Id));
        c.Ok("the SNES has WRAM, VRAM, CGRAM, OAM, SRAM, ROM, ARAM and DSP", ids == "WRAM,VRAM,CGRAM,OAM,SRAM,ROM,ARAM,DSP", ids);
        var sizes = d.ToDictionary(x => x.Id, x => x.Size);
        c.Ok("the sizes are the memories' true ones", sizes["WRAM"] == 131072 && sizes["VRAM"] == 65536 && sizes["CGRAM"] == 512 && sizes["OAM"] == 544 && sizes["ROM"] == rom.Length && sizes["ARAM"] == 65536 && sizes["DSP"] == 128 && sizes["SRAM"] == 0,
             string.Join(" ", sizes.Select(kv => $"{kv.Key}={kv.Value}")));
        c.Ok("a cartridge with no save RAM lists SRAM as unavailable, with the reason", MachineDomains.Find(d, "SRAM")!.Unavailable is { Length: > 0 });

        foreach (var dom in d.Where(x => x.Unavailable == null && x.Id != "CGRAM" && x.Id != "OAM"))
        {
            int[] at = { 0, 1, dom.Size / 2 + 1, dom.Size - 1 };
            bool ok = true;
            foreach (int a in at) { byte old = dom.Peek(a); dom.Poke(a, (byte)(old ^ 0x5A)); ok &= dom.Peek(a) == (byte)(old ^ 0x5A); dom.Poke(a, old); ok &= dom.Peek(a) == old; }
            c.Ok($"{dom.Id}: poke and peek round trip at the first, second, middle and last byte", ok);
            byte first = dom.Peek(0);
            dom.Poke(dom.Size, 0xEE); dom.Poke(-1, 0xEE);
            c.Ok($"{dom.Id}: an address past either end is ignored (peek gives 0, poke changes nothing)", dom.Peek(dom.Size) == 0 && dom.Peek(-1) == 0 && dom.Peek(0) == first);
        }
        var cg = MachineDomains.Find(d, "CGRAM")!;
        cg.Poke(259, 0xFF); cg.Poke(258, 0xFF);
        c.Ok("CGRAM: the top bit of a colour is not stored (a high byte of $FF reads back $7F, the low byte is whole)", cg.Peek(259) == 0x7F && cg.Peek(258) == 0xFF);
        var oam = MachineDomains.Find(d, "OAM")!;
        oam.Poke(543, 0xAA); c.Ok("OAM: round trip", oam.Peek(543) == 0xAA);
        var rom0 = MachineDomains.Find(d, "ROM")!;
        c.Ok("ROM is the session's own copy: poking it does not change the bytes the test built the session from", new Func<bool>(() => { byte o = rom[0x40]; rom0.Poke(0x40, (byte)(o ^ 0xFF)); bool same = rom[0x40] == o; rom0.Poke(0x40, o); return same; })());

        // the sound unit that is not there
        using var hle = new SnesRun(rom, "HLE");
        var dh = MachineDomains.For(hle.Session);
        c.Ok("under the silent HLE sound unit ARAM and DSP are listed as unavailable, with the reason", MachineDomains.Find(dh, "ARAM")!.Unavailable!.Contains("HLE") && MachineDomains.Find(dh, "DSP")!.Unavailable != null && hle.Snes.Spc == null);

        SpriteHook(c, rom);
    }

    /// <summary>The OAM domain's after-write hook, shown by its consequence on the picture, with the domain without the hook as the control.</summary>
    private static void SpriteHook(Checks c, byte[] rom)
    {
        // Sprite 0 at the top left, the other 127 hidden below the screen; tile 0 has one row of pixels; palette entry 129 is white.
        void Setup(IReadOnlyList<IMemoryDomain> d)
        {
            MachineDomains.Find(d, "CGRAM")!.Poke(258, 0xFF); MachineDomains.Find(d, "CGRAM")!.Poke(259, 0x7F);
            MachineDomains.Find(d, "VRAM")!.Poke(0, 0xFF);
            var oam = MachineDomains.Find(d, "OAM")!;
            for (int n = 1; n < 128; n++) oam.Poke(n * 4 + 1, 240);
        }
        // early: the sprite is put at Y before the machine has run a single frame (nothing is cached yet, so no hook is needed: this is the oracle that does not depend on the hook)
        ulong Picture(int spriteY, bool early, bool withHook)
        {
            using var r = new SnesRun(rom);
            if (!early) r.Run(4);
            var d = MachineDomains.For(r.Session);
            Setup(d);
            var oam = MachineDomains.Find(d, "OAM")!;
            // the control domain is the same memory without the hook
            IMemoryDomain target = withHook ? oam : new ArrayDomain("OAM", "OAM without the hook", DomainKind.Video, r.Snes.Board.Ppu.Oam);
            if (early) { target.Poke(1, (byte)spriteY); r.Run(6); return r.Picture(); }
            r.Run(2);                       // the sprite evaluation caches this state (sprite 0 at Y=0)
            target.Poke(1, (byte)spriteY);  // ... and the sprite is moved afterwards
            r.Run(2);
            return r.Picture();
        }
        ulong before = Picture(0, early: true, withHook: true);
        ulong oracle = Picture(100, early: true, withHook: true);       // the sprite was at Y=100 from before the first frame
        ulong hooked = Picture(100, early: false, withHook: true);       // moved after the cache was built, through the domain
        ulong unhooked = Picture(100, early: false, withHook: false);    // moved the same way, without the hook
        c.Ok("the sprite is drawn, and where it is shows in the picture", before != oracle, $"Y=0 {before:X16}, Y=100 {oracle:X16}");
        c.Ok("OAM poke + hook: a sprite moved after the first frames gives the picture of a machine that had it there from the start", hooked == oracle, $"{hooked:X16} vs {oracle:X16}");
        c.Control("the same poke without the hook leaves the sprite cache stale and the picture is wrong", unhooked != oracle, $"{unhooked:X16}");
    }

    // ===================================================================================================================== apply

    private static void Apply(Checks c, byte[] rom)
    {
        using var r = new SnesRun(rom);
        r.Run(3);
        var d = MachineDomains.For(r.Session);
        var romDom = MachineDomains.Find(d, "ROM")!;
        ulong clean = Hash.Fnv(r.Snes.Board.Cart.Rom);
        var writes = new[] { new CorruptionWrite("ROM", 0x10, 0xAA), new CorruptionWrite("ROM", 0x20, 0xBB), new CorruptionWrite("ROM", 0x10, 0xCC) };
        var j = new WriteJournal();
        var rep = CorruptionApplier.Apply(d, writes, j);
        ulong dirty = Hash.Fnv(r.Snes.Board.Cart.Rom);
        c.Ok("three writes (one address twice) are applied and the ROM changes", rep.Applied == 3 && rep.Changed == 3 && dirty != clean && romDom.Peek(0x10) == 0xCC && romDom.Peek(0x20) == 0xBB, rep.Describe());
        CorruptionApplier.Revert(j);
        c.Ok("Revert puts the ROM back exactly (last write first, so the twice-written byte returns to its original)", Hash.Fnv(r.Snes.Board.Cart.Rom) == clean && j.Count == 0);
        var j2 = new WriteJournal();
        CorruptionApplier.Apply(d, writes, j2); ulong once = Hash.Fnv(r.Snes.Board.Cart.Rom);
        CorruptionApplier.Apply(d, writes, null); ulong twice = Hash.Fnv(r.Snes.Board.Cart.Rom);
        c.Ok("applying the same writes twice leaves the same bytes", once == twice);
        CorruptionApplier.Revert(j2);
        CorruptionApplier.Apply(d, writes, null);
        CorruptionApplier.Revert(new WriteJournal());
        c.Control("without a journal there is nothing to revert (the ROM stays corrupted)", Hash.Fnv(r.Snes.Board.Cart.Rom) != clean);
        // put it back for the rest
        r.Snes.Board.Cart.Rom[0x10] = rom[0x10]; r.Snes.Board.Cart.Rom[0x20] = rom[0x20];

        var odd = CorruptionApplier.Apply(d, new[] { new CorruptionWrite("ZZZ", 0, 1), new CorruptionWrite("WRAM", 1 << 20, 1), new CorruptionWrite("SRAM", 0, 1), new CorruptionWrite("WRAM", 0x100, 7) }, null);
        c.Ok("a memory the machine lacks, an address past the end and an unavailable memory are skipped and counted, the valid write still lands",
             odd.Requested == 4 && odd.Applied == 1 && odd.UnknownDomain == 1 && odd.OutOfRange == 1 && odd.Unavailable == 1 && r.Snes.Board.Wram[0x100] == 7, odd.Describe());
        c.Ok("the skipped ones say why (and what the machine does have)", odd.Problems.Any(p => p.Contains("no memory called 'ZZZ'") && p.Contains("WRAM")) && odd.Problems.Any(p => p.Contains("past its end")) && odd.Problems.Any(p => p.Contains("not available")));
        var later = CorruptionApplier.Apply(d, new[] { new CorruptionWrite("WRAM", 0x200, 9, -1, 30) }, null, at: 0);
        c.Ok("a write for a later frame is not applied now", later.Requested == 0 && r.Snes.Board.Wram[0x200] != 9);
        var timeline = new CorruptionTimeline(d, new[] { new CorruptionWrite("WRAM", 0x200, 9, -1, 30) });
        bool early = timeline.Tick(29) == null && r.Snes.Board.Wram[0x200] != 9;
        var rt = timeline.Tick(30);
        c.Ok("a timeline applies it exactly at its frame", early && rt != null && rt.Applied == 1 && r.Snes.Board.Wram[0x200] == 9 && timeline.Done);
    }

    // ===================================================================================================================== blast

    private static void Blast(Checks c, byte[] rom)
    {
        using var r = new SnesRun(rom);
        r.Run(60);
        var d = MachineDomains.For(r.Session);
        var sel = new[] { "WRAM", "ARAM", "VRAM" };
        var a = BlastGenerator.Generate(d, sel, 32, BlastType.Random, 42, ConsoleKind.Snes);
        var b = BlastGenerator.Generate(d, sel, 32, BlastType.Random, 42, ConsoleKind.Snes);
        var other = BlastGenerator.Generate(d, sel, 32, BlastType.Random, 43, ConsoleKind.Snes);
        c.Ok("32 random writes, the same seed gives the same writes", a.Count == 32 && a.SequenceEqual(b));
        c.Control("another seed gives other writes", !a.SequenceEqual(other));
        // the golden uses memories of fixed contents, so it moves only when the generator does (not when a core's timing changes what a live machine holds)
        IMemoryDomain Fixed(string id, int size) { var bytes = new byte[size]; for (int i = 0; i < size; i++) bytes[i] = (byte)(i * 31 + 7); return new ArrayDomain(id, id, DomainKind.Work, bytes); }
        var fixedDomains = new[] { Fixed("WRAM", 4096), Fixed("ARAM", 2048), Fixed("VRAM", 1000) };
        var g = BlastGenerator.Generate(fixedDomains, sel, 32, BlastType.Random, 42, ConsoleKind.Snes);
        ulong h = Hash.Fnv(string.Join(";", g.Select(w => $"{w.Domain}:{w.Address}={w.Value}/{w.Was}")));
        c.Ok("golden: seed 42 over three memories of fixed contents gives the recorded writes (the generator is ours, so this holds on every .NET version)", h == GoldenBlast, $"hash {h:X16}" + (GoldenBlast == 0 ? " (not recorded yet)" : ""));
        c.Control("the golden moves with the seed", Hash.Fnv(string.Join(";", BlastGenerator.Generate(fixedDomains, sel, 32, BlastType.Random, 43, ConsoleKind.Snes).Select(w => $"{w.Domain}:{w.Address}={w.Value}/{w.Was}"))) != h);
        c.Ok("every write is inside its domain and records the byte that was there", a.All(w => MachineDomains.Find(d, w.Domain) is { } dom && w.Address >= 0 && w.Address < dom.Size && w.Was == dom.Peek(w.Address)));

        var tilt = BlastGenerator.Generate(d, sel, 400, BlastType.Tilt, 7, ConsoleKind.Snes);
        c.Ok("TILT writes the byte that was there plus or minus one (the stash path used to write a random byte)", tilt.All(w => (byte)(w.Value - w.Was) is 1 or 255) && tilt.Any(w => (byte)(w.Value - w.Was) == 1) && tilt.Any(w => (byte)(w.Value - w.Was) == 255));
        var flip = BlastGenerator.Generate(d, sel, 400, BlastType.BitFlip, 7, ConsoleKind.Snes);
        c.Ok("BITFLIP flips exactly one bit", flip.All(w => System.Numerics.BitOperations.PopCount((uint)(w.Value ^ w.Was)) == 1));
        var nop = BlastGenerator.Generate(d, sel, 50, BlastType.Nop, 7, ConsoleKind.Snes);
        c.Ok("NOP writes the 65816's no-op ($EA) on the SNES, $00 on the Game Boy and $4E / $71 by address parity on the Genesis",
             nop.All(w => w.Value == 0xEA) && BlastGenerator.NopByte(ConsoleKind.GameBoy, 5) == 0 && BlastGenerator.NopByte(ConsoleKind.Genesis, 4) == 0x4E && BlastGenerator.NopByte(ConsoleKind.Genesis, 5) == 0x71);
        var rt = BlastGenerator.Generate(d, sel, 400, BlastType.RandomTilt, 7, ConsoleKind.Snes);
        c.Ok("RANDOMTILT mixes both", rt.Count(w => (byte)(w.Value - w.Was) is 1 or 255) is > 100 and < 300);
        c.Ok("intensity is clamped to 1..65535", BlastGenerator.Generate(d, sel, 0, BlastType.Random, 1, ConsoleKind.Snes).Count == 1 && BlastGenerator.Generate(d, sel, 1_000_000, BlastType.Random, 1, ConsoleKind.Snes).Count == 65535);

        var uni = BlastGenerator.Generate(d, new[] { "WRAM", "ARAM" }, 6000, BlastType.Random, 9, ConsoleKind.Snes);
        var wei = BlastGenerator.Generate(d, new[] { "WRAM", "ARAM" }, 6000, BlastType.Random, 9, ConsoleKind.Snes, weightBySize: true);
        double uniShare = uni.Count(w => w.Domain == "WRAM") / 6000.0, weiShare = wei.Count(w => w.Domain == "WRAM") / 6000.0;
        c.Ok("by default each selected domain is picked equally (WRAM about 50%); weighted by size WRAM (twice ARAM's size) about 67%", uniShare is > 0.46 and < 0.54 && weiShare is > 0.63 and < 0.70, $"{uniShare:P1} / {weiShare:P1}");
        using var hle = new SnesRun(rom, "HLE");
        c.Ok("an unavailable domain is never chosen (ARAM under HLE gives no writes at all)", BlastGenerator.Generate(MachineDomains.For(hle.Session), new[] { "ARAM" }, 10, BlastType.Random, 1, ConsoleKind.Snes).Count == 0);
    }

    // ===================================================================================================================== anchors (a test machine)

    /// <summary>A machine of 256 bytes: RunFrame adds the pad to byte 0, so a replay that ignores the input ends in a different machine. It has save states.</summary>
    private sealed class TestMachine : IConsoleSession, IMemoryDomainSource
    {
        public readonly byte[] Ram = new byte[256];
        public int Frames, FrameDelayMs; public bool Disposed; public byte[]? Imported; private PadButtons pad;
        private readonly string id;
        public TestMachine(string gameId = "game") { id = gameId; MemoryDomains = new IMemoryDomain[] { new ArrayDomain("RAM", "test RAM", DomainKind.Work, Ram) }; }
        public IReadOnlyList<IMemoryDomain> MemoryDomains { get; }
        public ConsoleKind Console => ConsoleKind.Snes; public string Title => "test"; public string Description => "test machine"; public double FramesPerSecond => 60;
        public void SetPad(int player, PadButtons b) { if (player == 0) pad = b; }
        public void RunFrame() { Frames++; Ram[0] += (byte)((ushort)pad & 0xFF); Ram[1]++; if (FrameDelayMs > 0) System.Threading.Thread.Sleep(FrameDelayMs); }
        public void Reset() { }
        public uint[] Frame { get; } = new uint[4]; public int FrameWidth => 2; public int FrameHeight => 2; public int DisplayWidth => 2; public int SampleRate => 32000;
        public int ReadSamples(short[] buffer) => 0;
        public bool HasBattery => true; public byte[] ExportSave() => Array.Empty<byte>(); public void ImportSave(byte[] data) { Imported = data; Ram[2] = (byte)data.Length; }
        public string GameId => id; public bool TrySwapCore(CoreSlot slot, string id) => false;
        public void Dispose() { Disposed = true; }
        public bool SupportsState => true;
        public byte[] SaveState() { var b = new byte[256 + 8]; Array.Copy(System.Text.Encoding.ASCII.GetBytes("TESTSTAT"), b, 8); Ram.CopyTo(b, 8); return b; }
        public void LoadState(byte[] state)
        {
            if (state.Length != 264 || System.Text.Encoding.ASCII.GetString(state, 0, 8) != "TESTSTAT") throw new InvalidDataException("This is not a test machine's state.");
            Array.Copy(state, 8, Ram, 0, 256);
        }
    }

    private static void Anchors(Checks c)
    {
        var rom = new byte[] { 1, 2, 3, 4, 5 };
        string sha = AnchorStart.Sha1Hex(rom);
        TestMachine? lastBuilt = null;
        var opts = new StartOptions { SessionFactory = (k, copy, m, f) => lastBuilt = new TestMachine() };
        StockpileEntry Entry(Anchor a, params CorruptionWrite[] w) => new() { Name = "t", Game = new GameRef { Console = "snes", Sha1 = sha, Title = "test" }, Anchor = a, Writes = w.ToList() };

        // none: applies to the running machine
        var run = new TestMachine();
        var r1 = AnchorStart.Start(Entry(new Anchor { Kind = AnchorKind.None }, new CorruptionWrite("RAM", 10, 99)), null, rom, "t.sfc", ConsoleKind.Snes, run, opts);
        c.Ok("none: the writes land in the running machine", r1.Ok && ReferenceEquals(r1.Session, run) && !r1.SessionIsNew && run.Ram[10] == 99, r1.Message);

        // state
        var src = new TestMachine(); src.Ram[50] = 77; var stateBytes = src.SaveState();
        var r2 = AnchorStart.Start(Entry(new Anchor { Kind = AnchorKind.State, Console = "snes", StateData = stateBytes }, new CorruptionWrite("RAM", 10, 99)), null, rom, "t.sfc", ConsoleKind.Snes, run, opts);
        c.Ok("state: the machine is loaded from the saved state, then the writes land", r2.Ok && run.Ram[50] == 77 && run.Ram[10] == 99 && r2.Used == AnchorKind.State, r2.Message);
        var r2b = AnchorStart.Start(Entry(new Anchor { Kind = AnchorKind.State, Console = "snes", StateData = new byte[] { 1, 2, 3 } }, new CorruptionWrite("RAM", 10, 1)), null, rom, "t.sfc", ConsoleKind.Snes, run, opts);
        c.Ok("state: a saved state the machine refuses is refused with the machine's own reason", !r2b.Ok && r2b.Message.Contains("not a test machine's state"), r2b.Message);
        var r2c = AnchorStart.Start(Entry(new Anchor { Kind = AnchorKind.State, Console = "nes", StateData = stateBytes }), null, rom, "t.sfc", ConsoleKind.Snes, run, opts);
        c.Ok("state: a state of another console is refused", !r2c.Ok && r2c.Message.Contains("NES"), r2c.Message);

        // boot: input matters; two runs agree
        var boot = new Anchor { Kind = AnchorKind.Boot, Frames = 40, Inputs = "10:A,20:" };
        var r3 = AnchorStart.Start(Entry(boot, new CorruptionWrite("RAM", 10, 99)), null, rom, "t.sfc", ConsoleKind.Snes, null, opts);
        var m3 = (TestMachine)r3.Session!;
        c.Ok("boot: a new machine runs the frames with the recorded pad, then the writes land", r3.Ok && r3.SessionIsNew && m3.Frames == 40 && m3.Ram[0] == (byte)(10 * (ushort)PadButtons.A) && m3.Ram[10] == 99 && r3.FramesReplayed == 40, $"RAM[0]={m3.Ram[0]} frames={m3.Frames}");
        var r3b = AnchorStart.Start(Entry(new Anchor { Kind = AnchorKind.Boot, Frames = 40, Inputs = "10:A,21:" }), null, rom, "t.sfc", ConsoleKind.Snes, null, opts);
        c.Control("boot: the test machine sees the input (A held one frame longer ends in another machine)", r3b.Ok && ((TestMachine)r3b.Session!).Ram[0] != m3.Ram[0]);
        byte[]? factoryCopy = null;
        AnchorStart.Start(Entry(new Anchor { Kind = AnchorKind.Boot, Frames = 1 }), null, rom, "t.sfc", ConsoleKind.Snes, null, new StartOptions { SessionFactory = (k, copy, m, f) => { factoryCopy = copy; return new TestMachine(); } });
        c.Ok("boot: the machine is built on a private copy of the ROM (equal bytes, not the caller's array)", factoryCopy != null && !ReferenceEquals(factoryCopy, rom) && factoryCopy.SequenceEqual(rom));
        var seen = new List<string>();
        var r4 = AnchorStart.Start(Entry(new Anchor { Kind = AnchorKind.Boot, Frames = 2000 }), null, rom, "t.sfc", ConsoleKind.Snes, null, new StartOptions { SessionFactory = opts.SessionFactory, MaxReplayFrames = 1000 });
        c.Ok("boot: a replay longer than the cap is refused with the numbers", !r4.Ok && r4.Message.Contains("2,000") && r4.Message.Contains("1,000"), r4.Message);
        int polls = 0; TestMachine? cancelled = null;
        var r5 = AnchorStart.Start(Entry(new Anchor { Kind = AnchorKind.Boot, Frames = 500 }, new CorruptionWrite("RAM", 10, 99)), null, rom, "t.sfc", ConsoleKind.Snes, null,
            new StartOptions { SessionFactory = (k, copy, m, f) => cancelled = new TestMachine(), Cancelled = () => ++polls > 7 });
        c.Ok("boot: cancelling stops the replay, applies nothing and lets the half-built machine go", !r5.Ok && r5.Cancelled && cancelled != null && cancelled.Disposed && cancelled.Ram[10] == 0 && cancelled.Frames == 7, r5.Message);
        var r6 = AnchorStart.Start(Entry(new Anchor { Kind = AnchorKind.Boot, Frames = 700 }), null, rom, "t.sfc", ConsoleKind.Snes, null, new StartOptions { SessionFactory = (k, copy, m, f) => new TestMachine { FrameDelayMs = 2 }, Status = seen.Add });
        c.Ok("boot: progress is reported while it runs (700 frames at 2 ms each)", r6.Ok && seen.Count >= 1 && seen.All(x => x.StartsWith("replaying ") && x.EndsWith(" / 700 frames")), $"{seen.Count} messages, last '{seen.LastOrDefault()}'");
        var bat = new byte[] { 1, 2, 3 };
        AnchorStart.Start(Entry(new Anchor { Kind = AnchorKind.Boot, Frames = 1, Battery = bat }), null, rom, "t.sfc", ConsoleKind.Snes, null, opts);
        c.Ok("boot: the save RAM the game started with is loaded before frame 0", lastBuilt!.Imported != null && lastBuilt.Imported.SequenceEqual(bat));

        // the alternative anchor
        var withAlt = new Anchor { Kind = AnchorKind.State, Console = "snes", StateData = new byte[] { 9 }, Alt = new Anchor { Kind = AnchorKind.Boot, Frames = 3, Inputs = "" } };
        var r7 = AnchorStart.Start(Entry(withAlt, new CorruptionWrite("RAM", 10, 5)), null, rom, "t.sfc", ConsoleKind.Snes, null, opts);
        c.Ok("alt: when the state is refused the boot recipe is used, and the user is told", r7.Ok && r7.Used == AnchorKind.Boot && r7.Warnings.Any(w => w.Contains("first anchor was refused") && w.Contains("not a test machine's state")), r7.Warnings.FirstOrDefault() ?? "");

        // the game and the console
        var wrongGame = Entry(new Anchor { Kind = AnchorKind.None }, new CorruptionWrite("RAM", 1, 1));
        wrongGame.Game.Sha1 = "deadbeef";
        var r8 = AnchorStart.Start(wrongGame, null, rom, "t.sfc", ConsoleKind.Snes, run, opts);
        c.Ok("a corruption made on another game is refused, naming both", !r8.Ok && r8.Message.Contains("was made on \"test\"") && r8.Message.Contains("t.sfc"), r8.Message);
        var r9 = AnchorStart.Start(wrongGame, null, rom, "t.sfc", ConsoleKind.Snes, run, new StartOptions { SessionFactory = opts.SessionFactory, AllowOtherGame = true });
        c.Ok("...unless the user allows it, with a warning", r9.Ok && r9.Warnings.Any(w => w.Contains("anyway")));
        var wrongConsole = Entry(new Anchor { Kind = AnchorKind.None }); wrongConsole.Game.Console = "md";
        c.Ok("a corruption for another console is refused", !AnchorStart.Start(wrongConsole, null, rom, "t.sfc", ConsoleKind.Snes, run, opts).Ok);
        var noId = Entry(new Anchor { Kind = AnchorKind.None }); noId.Game.Sha1 = "";
        c.Ok("an entry that does not say which game it was made on starts with a warning that says so", AnchorStart.Start(noId, null, rom, "t.sfc", ConsoleKind.Snes, run, opts).Warnings.Any(w => w.Contains("does not say which game")));

        // shared bases
        var sp = new Stockpile(); sp.Bases.Add(new StockpileBase { Id = "b", Anchor = new Anchor { Kind = AnchorKind.Boot, Frames = 5 } });
        var viaRef = AnchorStart.Start(Entry(new Anchor { Kind = AnchorKind.Ref, BaseId = "b" }, new CorruptionWrite("RAM", 10, 5)), sp, rom, "t.sfc", ConsoleKind.Snes, null, opts);
        c.Ok("a shared base is resolved", viaRef.Ok && viaRef.FramesReplayed == 5);
        c.Ok("a missing shared base is refused", !AnchorStart.Start(Entry(new Anchor { Kind = AnchorKind.Ref, BaseId = "gone" }), sp, rom, "t.sfc", ConsoleKind.Snes, null, opts).Ok);

        // nothing to write to
        var noWrite = AnchorStart.Start(Entry(new Anchor { Kind = AnchorKind.None }, new CorruptionWrite("NOPE", 1, 1)), null, rom, "t.sfc", ConsoleKind.Snes, run, opts);
        c.Ok("when no write can land the start is refused with the reason, not reported as started", !noWrite.Ok && noWrite.Message.Contains("could not be applied") && noWrite.Message.Contains("NOPE"), noWrite.Message);
        var target = new TestMachine();
        c.Ok("writes for later frames are handed back for a timeline, not applied now", AnchorStart.Start(Entry(new Anchor { Kind = AnchorKind.None }, new CorruptionWrite("RAM", 1, 1), new CorruptionWrite("RAM", 2, 2, -1, 9)), null, rom, "t.sfc", ConsoleKind.Snes, target, opts) is { Ok: true } l && l.Later.Count == 1 && target.Ram[1] == 1 && target.Ram[2] == 0);
    }

    // ===================================================================================================================== boot (SNES)

    private sealed record Fingerprint(ulong Wram, ulong Aram, ulong Dsp, ulong Audio, ulong Picture)
    {
        public string Show() => $"WRAM {Wram:X16} ARAM {Aram:X16} DSP {Dsp:X16} audio {Audio:X16} picture {Picture:X16}";
    }

    private static StockpileEntry SynthEntry(byte[] rom, Anchor anchor, params CorruptionWrite[] writes) => new()
    {
        Name = "synth", Game = new GameRef { Console = "snes", Sha1 = AnchorStart.Sha1Hex(rom), Title = "BN CORRUPTION SYNTH" },
        Machine = new MachineSpec { Cpu = "SFC", Ppu = "SFC", Apu = "SFC" }, Anchor = anchor, Writes = writes.ToList(),
    };

    /// <summary>Starts an entry and fingerprints the machine right after the start (and after more frames, for the sound).</summary>
    private static Fingerprint Fingerprint_(byte[] rom, StockpileEntry e, int more, out StartResult started)
    {
        started = AnchorStart.Start(e, null, rom, "synth.sfc", ConsoleKind.Snes, null);
        if (!started.Ok) throw new InvalidOperationException(started.Message);
        using var run = new SnesRun(started.Session!);
        ulong wram = run.Wram(), aram = run.Aram(), dsp = run.Dsp();
        run.Run(more);
        var fp = new Fingerprint(wram, aram, dsp, run.AudioHash, run.Picture());
        return fp;
    }

    private static void Boot(Checks c, byte[] rom)
    {
        var anchor = new Anchor { Kind = AnchorKind.Boot, Frames = 240, Inputs = "100:A,160:" };
        var a1 = Fingerprint_(rom, SynthEntry(rom, anchor), 30, out var s1);
        var a2 = Fingerprint_(rom, SynthEntry(rom, anchor), 30, out _);
        c.Ok("the same boot anchor replayed twice gives the same work RAM, SPC700 ARAM, S-DSP, sound and picture", a1 == a2, a1.Show());
        c.Ok($"(the replay ran {s1.FramesReplayed} frames from power-on)", s1.FramesReplayed == 240);
        // one frame of input changed: the button held one frame longer
        var longer = new Anchor { Kind = AnchorKind.Boot, Frames = 240, Inputs = "100:A,161:" };
        var b = Fingerprint_(rom, SynthEntry(rom, longer), 30, out _);
        c.Ok("one frame of input changed (A held 61 frames instead of 60): the SPC700's ARAM differs", b.Aram != a1.Aram, $"{b.Aram:X16} vs {a1.Aram:X16}");
        c.Ok("...and so does the 65816's work RAM", b.Wram != a1.Wram);
        var shifted = new Anchor { Kind = AnchorKind.Boot, Frames = 240, Inputs = "101:A,161:" };
        var sh = Fingerprint_(rom, SynthEntry(rom, shifted), 30, out _);
        c.Ok("the same 60 frames of A pressed one frame later: the 65816's work RAM differs (its loop samples the pad at its own pace)", sh.Wram != a1.Wram);
        Console.WriteLine($"      (information: the SPC700's counter counts frames held, so a shifted press leaves its ARAM {(sh.Aram == a1.Aram ? "equal" : "different")})");
        var ignoring = new Anchor { Kind = AnchorKind.Boot, Frames = 240, Inputs = "" };
        var m = Fingerprint_(rom, SynthEntry(rom, ignoring), 30, out _);
        c.Control("a replay that ignores the input is told apart from the real one (so the hashes above can fail)", m.Aram != a1.Aram && m.Wram != a1.Wram);
        var fewer = new Anchor { Kind = AnchorKind.Boot, Frames = 239, Inputs = "100:A,160:" };
        c.Control("one frame fewer is told apart too", Fingerprint_(rom, SynthEntry(rom, fewer), 30, out _).Wram != a1.Wram);
        // in the Workshop's own runner, the recorded script is the one --console-run --input takes
        using var direct = new SnesRun(rom);
        InputScript.TryParse(anchor.Inputs, out var scr, out _);
        var player = new InputScript.Player(scr);
        direct.Run(240, f => (PadButtons)player.Bits(f));
        c.Ok("the anchor's machine is the machine a plain run with the same pad reaches (WRAM and ARAM equal)", direct.Wram() == a1.Wram && direct.Aram() == a1.Aram);
    }

    // ===================================================================================================================== audio

    private static void Audio(Checks c, byte[] rom)
    {
        var anchor = new Anchor { Kind = AnchorKind.Boot, Frames = 120 };
        int s = SynthSnesRom.AramSample;
        // the instrument's data bytes (the 8 after the header) changed to other values: the square wave changes shape
        var data = Enumerable.Range(1, 8).Select(i => new CorruptionWrite("ARAM", s + i, i <= 4 ? (byte)0x33 : (byte)0xDD, i <= 4 ? 0x77 : 0x99)).ToArray();
        var clean = Fingerprint_(rom, SynthEntry(rom, anchor), 60, out var cs);
        var bad = Fingerprint_(rom, SynthEntry(rom, anchor, data), 60, out var bs);
        var bad2 = Fingerprint_(rom, SynthEntry(rom, anchor, data), 60, out _);
        c.Ok("the corruption starts: 8 of 8 writes applied, all changing a byte", bs.Ok && bs.Report!.Applied == 8 && bs.Report.Changed == 8, bs.Message);
        c.Ok("the sound of the corrupted instrument is not the sound of the clean one", bad.Audio != clean.Audio, $"{clean.Audio:X16} vs {bad.Audio:X16}");
        c.Ok("...and the same corruption sounds the same every time", bad.Audio == bad2.Audio && bad == bad2);
        c.Ok("the picture and the 65816's work RAM are untouched (the corruption is in the audio RAM only)", bad.Picture == clean.Picture && bad.Wram == clean.Wram);
        // a corruption that changes nothing must not look different (the control of the check above)
        var noop = data.Select(w => w with { Value = (byte)w.Was }).ToArray();
        var same = Fingerprint_(rom, SynthEntry(rom, anchor, noop), 60, out var ns);
        c.Control("a corruption that writes the bytes already there sounds exactly like no corruption (so the difference above is the writes)", same.Audio == clean.Audio && same.Aram == clean.Aram && ns.Report!.Changed == 0);

        // what Instrument Runaway captures: the plugin's SpcSnapshot of the frozen machine, right after Start
        SpcSnapshot Snap(StockpileEntry e)
        {
            var st = AnchorStart.Start(e, null, rom, "synth.sfc", ConsoleKind.Snes, null);
            using var run = new SnesRun(st.Session!);
            return new SpcSnapshot(run.Spc.Aram, run.Spc.Dsp.Regs);
        }
        var snapClean = Snap(SynthEntry(rom, anchor));
        var snapBad = Snap(SynthEntry(rom, anchor, data));
        int diff = 0; var diffAt = new List<int>();
        for (int i = 0; i < SpcSnapshot.AramSize; i++) if (snapClean.Aram[i] != snapBad.Aram[i]) { diff++; diffAt.Add(i); }
        c.Ok("Runaway's SpcSnapshot of the corrupted machine differs from the clean one in exactly the 8 bytes written", diff == 8 && diffAt.SequenceEqual(data.Select(w => w.Address)), $"{diff} bytes differ");
        c.Ok("the DSP registers are the same (only the sample changed)", snapClean.Dsp.AsSpan().SequenceEqual(snapBad.Dsp));
        var ic = snapClean.Instruments(); var ib = snapBad.Instruments();
        c.Ok("the instrument picker still lists the instrument (slot 0 at $0500, looping), its bytes now corrupted", ic.Count == 1 && ib.Count == 1 && ic[0].Start == s && ib[0].Start == s && ic[0].Loops && ib[0].Loops && ic[0].Sounding, $"{ic.Count}/{ib.Count} listed");
        // a header corruption: range 13 is not a BRR block; the picker's list loses the instrument
        var header = new CorruptionWrite("ARAM", s, 0xD3, 0x93);
        var snapHeader = Snap(SynthEntry(rom, anchor, header));
        c.Ok("a corrupted sample header (range 13) makes the instrument fall out of the picker's list", snapHeader.Instruments().Count == 0 && ic.Count == 1);
        // the sample directory pointing elsewhere: a different instrument
        var dir = new[] { new CorruptionWrite("ARAM", SynthSnesRom.AramDirectory + 0, 0x00, 0x00), new CorruptionWrite("ARAM", SynthSnesRom.AramDirectory + 1, 0x06, 0x05) };
        var snapDir = Snap(SynthEntry(rom, anchor, dir));
        c.Ok("a sample directory entry moved to an address that holds no sample drops the instrument too", snapDir.Instruments().Count == 0);

        // an S-DSP register corruption: the voice's pitch (the game keeps its own copy: the DSP register is what the chip plays)
        var pitch = new[] { new CorruptionWrite("DSP", 0x03, 0x08, 0x10) };
        var fpPitch = Fingerprint_(rom, SynthEntry(rom, anchor, pitch), 60, out var ps);
        c.Ok("an S-DSP pitch register corruption changes the sound (the DSP domain writes through the chip's register write)", ps.Ok && fpPitch.Audio != clean.Audio && fpPitch.Dsp != clean.Dsp);
    }

    // ===================================================================================================================== cli

    private static void Cli(Checks c, byte[] rom)
    {
        string dir = Path.Combine(Path.GetTempPath(), "bn-corrupt-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string romPath = Path.Combine(dir, "synth.sfc"); File.WriteAllBytes(romPath, rom);
        int s = SynthSnesRom.AramSample;
        var sp = new Stockpile { Name = "cli" };
        sp.Entries.Add(SynthEntry(rom, new Anchor { Kind = AnchorKind.Boot, Frames = 120 }, Enumerable.Range(1, 8).Select(i => new CorruptionWrite("ARAM", s + i, i <= 4 ? (byte)0x33 : (byte)0xDD, i <= 4 ? 0x77 : 0x99)).ToArray()));
        sp.Entries[0].Name = "Growl";
        string stock = Path.Combine(dir, "cli" + Stockpile.FileSuffix);
        StockpileJson.SaveFile(stock, sp);

        string Run(string[] a, out int code)
        {
            var old = Console.Out; var sw = new StringWriter(); Console.SetOut(sw);
            try { code = CorruptApply.Run(a); } finally { Console.SetOut(old); }
            return sw.ToString();
        }
        string o = Run(new[] { "--corrupt-apply", "--rom", romPath, "--stockpile", stock, "--entry", "Growl", "--frames", "60" }, out int rc);
        Console.WriteLine(string.Join("\n", o.Split('\n').Select(l => "      | " + l.TrimEnd())));
        c.Ok("--corrupt-apply starts the entry by name and exits 0", rc == 0 && o.Contains("started 'Growl'") && o.Contains("8 of 8 writes applied"));
        var aramLine = o.Split('\n').Where(l => l.TrimStart().StartsWith("ARAM")).ToList();
        c.Ok("it reports the audio RAM as the memory that differs, 8 bytes, with their addresses, and no other memory", aramLine.Count == 2 && aramLine.All(l => l.Contains("8 of") && l.Contains("$501")) && !o.Split('\n').Any(l => (l.TrimStart().StartsWith("WRAM") || l.TrimStart().StartsWith("VRAM")) && !l.Contains("   0 of")), aramLine.FirstOrDefault() ?? "");
        c.Ok("it reports the sound as different and the picture as the same", o.Contains("audio:") && o.Contains("DIFFERENT") && o.Contains("picture:") && o.Split('\n').First(l => l.StartsWith("picture:")).Contains("SAME"));
        c.Ok("it reports what Instrument Runaway would capture (ARAM bytes that differ, instruments listed)", o.Contains("SpcSnapshot right after the writes: 8 ARAM bytes and 0 DSP registers differ"));
        string o2 = Run(new[] { "--corrupt-apply", "--rom", romPath, "--stockpile", stock, "--entry", "nope" }, out int rc2);
        c.Ok("an entry that is not there exits 2 with a message", rc2 == 2);
        string other = Path.Combine(dir, "other.sfc"); var rom2 = (byte[])rom.Clone(); rom2[0x300] ^= 1; File.WriteAllBytes(other, rom2);
        string o3 = Run(new[] { "--corrupt-apply", "--rom", other, "--stockpile", stock }, out int rc3);
        c.Ok("a ROM that is not the entry's game is refused (exit 1) and says which game it was made on", rc3 == 1 && o3.Contains("was made on"), o3.Split('\n').FirstOrDefault(l => l.Contains("was made on")) ?? "");
        string bad = Path.Combine(dir, "bad.json"); File.WriteAllText(bad, "{ not json");
        string o4 = Run(new[] { "--corrupt-apply", "--rom", romPath, "--stockpile", bad }, out int rc4);
        c.Ok("a stockpile that cannot be read exits 1 and says why", rc4 == 1 && o4.Contains("refused:") && o4.Contains("not valid JSON"));
    }

    // ===================================================================================================================== SNES state (guarded)

    private static void SnesState(Checks c, byte[] rom)
    {
        using var probe = new SnesRun(rom);
        if (!probe.Session.SupportsState) { c.Skip("a state anchor on the SNES: load a saved machine, then the writes", "the SNES has no save states yet: " + probe.Session.StateUnsupportedReason); return; }
        // with states: save at frame 120, corrupt a fresh machine through the state anchor, compare with corrupting the saved machine directly
        probe.Run(120);
        byte[] state = probe.Session.SaveState();
        var data = Enumerable.Range(1, 8).Select(i => new CorruptionWrite("ARAM", SynthSnesRom.AramSample + i, i <= 4 ? (byte)0x33 : (byte)0xDD)).ToArray();
        var entry = SynthEntry(rom, new Anchor { Kind = AnchorKind.State, Console = "snes", StateFormat = "snes", StateData = state }, data);
        var st = AnchorStart.Start(entry, null, rom, "synth.sfc", ConsoleKind.Snes, null);
        c.Ok("the state anchor starts (state loaded, 8 writes applied)", st.Ok && st.Used == AnchorKind.State && st.Report!.Applied == 8, st.Message);
        if (!st.Ok) return;
        using var viaState = new SnesRun(st.Session!);
        CorruptionApplier.Apply(MachineDomains.For(probe.Session), data, null);
        viaState.Run(60); probe.Run(60);
        c.Ok("the machine after Start equals the saved machine corrupted directly (ARAM, WRAM, sound after 60 more frames)", viaState.Aram() == probe.Aram() && viaState.Wram() == probe.Wram() && viaState.AudioHash == probe.AudioHash);
        var wrong = new SnesRun(rom);
        var refused = AnchorStart.Start(SynthEntry(rom, new Anchor { Kind = AnchorKind.State, Console = "snes", StateData = new byte[] { 1, 2, 3 } }, data), null, rom, "synth.sfc", ConsoleKind.Snes, wrong.Session);
        c.Control("a damaged state is refused and the running machine is left as it was", !refused.Ok);
    }

    // ===================================================================================================================== Super Mario World

    private static void Smw(Checks c, string path)
    {
        if (!File.Exists(path)) { c.Skip("Super Mario World checks", $"no ROM at {path} (pass --smw <rom> or set BROKENNES_TEST_SMW)"); return; }
        var raw = File.ReadAllBytes(path);
        var rom = RomDetect.Unwrap(raw, path, out _);
        string sha = AnchorStart.Sha1Hex(rom);
        Console.WriteLine($"  using {Path.GetFileName(path)} sha1 {sha}");
        StockpileEntry E(Anchor a, params CorruptionWrite[] w) => new() { Name = "smw", Game = new GameRef { Console = "snes", Sha1 = sha, Title = "SMW" }, Anchor = a, Writes = w.ToList() };

        // determinism of a replay against the real driver, and one frame of input
        var anchor = new Anchor { Kind = AnchorKind.Boot, Frames = 1200, Inputs = "600:Start,606:" };
        var f1 = FingerprintAt(rom, E(anchor), 0, out _);
        var f2 = FingerprintAt(rom, E(anchor), 0, out _);
        c.Ok("SMW: the same boot anchor replayed twice gives the same SPC700 ARAM, DSP and work RAM", f1 == f2, f1.Show());
        var moved = FingerprintAt(rom, E(new Anchor { Kind = AnchorKind.Boot, Frames = 1200, Inputs = "601:Start,607:" }), 0, out _);
        c.Ok("SMW: Start pressed one frame later gives a different work RAM", moved.Wram != f1.Wram, $"{moved.Wram:X16} vs {f1.Wram:X16}");
        Console.WriteLine($"      (information: the SPC700's ARAM is {(moved.Aram == f1.Aram ? "the same" : "different")} then; at the title screen the game's music does not depend on when Start is pressed, scanned for shifts of 1, 2, 4 and 8 frames at frames 400 and 600)");
        var none = FingerprintAt(rom, E(new Anchor { Kind = AnchorKind.Boot, Frames = 1200 }), 0, out _);
        c.Control("SMW: a replay with the input left out is different in both the SPC700's ARAM and the work RAM (so the comparisons can fail)", none.Aram != f1.Aram && none.Wram != f1.Wram);

        // an instrument of the game: corrupt its BRR data and listen
        var anchor2 = new Anchor { Kind = AnchorKind.Boot, Frames = 900 };
        var st = AnchorStart.Start(E(anchor2), null, rom, "smw.smc", ConsoleKind.Snes, null);
        using var look = new SnesRun(st.Session!);
        var snap = new SpcSnapshot(look.Spc.Aram, look.Spc.Dsp.Regs);
        var list = snap.Instruments();
        c.Ok("SMW at frame 900: the instrument picker lists the game's samples", list.Count > 0, $"{list.Count} instruments");
        if (list.Count == 0) return;
        var pick = list.OrderByDescending(i => i.Sounding).ThenByDescending(i => i.Blocks).First();
        Console.WriteLine($"  corrupting slot {pick.Slot} at ${pick.Start:X4}, {pick.Blocks} blocks, sounding={pick.Sounding}");
        var writes = new List<CorruptionWrite>();
        for (int b = 1; b < Math.Min(pick.Blocks, 40); b++)
            for (int k = 1; k <= 8; k++)
            {
                int a = pick.Start + b * 9 + k;
                writes.Add(new CorruptionWrite("ARAM", a, (byte)(snap.Aram[a] ^ 0xFF), snap.Aram[a]));
            }
        var clean = FingerprintAt(rom, E(anchor2), 300, out _);
        var bad = FingerprintAt(rom, E(anchor2, writes.ToArray()), 300, out var bs);
        var bad2 = FingerprintAt(rom, E(anchor2, writes.ToArray()), 300, out _);
        c.Ok("SMW: the corrupted instrument changes the game's sound over the next 300 frames", bad.Audio != clean.Audio, $"{writes.Count} writes, {bs.Report!.Describe()}");
        c.Ok("SMW: ... the same way every time", bad == bad2);
        var snapBad = Snap(rom, E(anchor2, writes.ToArray()));
        int diff = 0; for (int i = 0; i < SpcSnapshot.AramSize; i++) if (snap.Aram[i] != snapBad.Aram[i]) diff++;
        c.Ok("SMW: Runaway's SpcSnapshot of the corrupted game differs from the clean one in the written bytes", diff == writes.Count(w => w.Value != w.Was), $"{diff} bytes");
    }

    private static Fingerprint FingerprintAt(byte[] rom, StockpileEntry e, int more, out StartResult started)
    {
        started = AnchorStart.Start(e, null, rom, "smw.smc", ConsoleKind.Snes, null);
        if (!started.Ok) throw new InvalidOperationException(started.Message);
        using var run = new SnesRun(started.Session!);
        ulong wram = run.Wram(), aram = run.Aram(), dsp = run.Dsp();
        run.Run(more);
        return new Fingerprint(wram, aram, dsp, run.AudioHash, run.Picture());
    }

    private static SpcSnapshot Snap(byte[] rom, StockpileEntry e)
    {
        var st = AnchorStart.Start(e, null, rom, "smw.smc", ConsoleKind.Snes, null);
        using var run = new SnesRun(st.Session!);
        return new SpcSnapshot(run.Spc.Aram, run.Spc.Dsp.Regs);
    }
}
