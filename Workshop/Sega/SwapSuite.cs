using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using NesEmulator.Mix;
using NesEmulator.Sega;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// Hot swapping of every core slot of the Master System and the Game Gear, mid-game, through <see cref="IConsoleSession.TrySwapCore"/> on the session the apps build (<see cref="ConsoleSessions.Create"/>:
/// the board session wrapped by <c>SegaPictureSession</c>), for every id the console's menus offer (<see cref="CoreCatalog.Options"/>): the CPU slot, the picture slot (the console's own chip and every
/// <c>NES:&lt;chip&gt;</c>, which draws the game's picture on that NES, Game Boy, SNES or Genesis chip) and the sound slot (the console's own unit, <c>SMS-FM</c> and every <c>NES:&lt;chip&gt;</c>).
/// <list type="bullet">
/// <item>A program the suite assembles (a scrolling three-colour picture with one steady PSG tone, a frame counter in work RAM) runs 90 frames, a core is swapped, and 90 more frames run. The same program runs once
/// with no swap as the <b>control</b>; after the swap the game must be where the control is (work RAM, video RAM, colour RAM, the Z80's PC and cycle count equal: a swap never disturbs the machine), the picture is the
/// control's pixel for pixel for the console's own chip and a real picture (non-blank, the right canvas, moving) on any other chip, and the sound is the tone the game was playing (its pitch within 2 percent, audible),
/// sample for sample the control's for the console's own units.</item>
/// <item>An id the menu does not offer is refused, with the session untouched; every id of the menus also builds a session of its own (the path the app takes when a swap cannot be done in place).</item>
/// <item>Chains of swaps in one run (sound and picture), a swap on every frame, and, with <c>--roms</c>, the same on real games (the game goes on exactly as the control does).</item>
/// </list>
/// </summary>
internal static class SwapSuite
{
    private const int Warm = 90, After = 90;
    private const double ToneHz = 3579545.0 / (32 * 254);   // period $0FE on the NTSC clock

    /// <summary>NES sound chips that do not play the bridged tone at the PSG's pitch, with the factor they play it at. Measured on a fresh session on the chip (no swap involved): NES:QLQ sounds the same PSG tone an octave up and
    /// at about half the level (the QuickNES-style core's pulse channel; the bridge writes the same registers as for every other chip). The swap must give what a fresh session on that chip gives, so the exception is named here
    /// and the check fails if the chip is ever corrected (then this line goes).</summary>
    private static readonly Dictionary<string, double> PitchFactor = new(StringComparer.OrdinalIgnoreCase) { ["NES:QLQ"] = 2.0 };

    /// <summary>The program: Mode 4 stripes that scroll one pixel a frame whatever the pad does, PSG tone 1 at period $0FE at full volume, a frame counter at $C100 (also the scroll register).</summary>
    internal static byte[] ToneScroller(bool gg)
    {
        int[] regs = { 0x04, 0x80, 0x80, 0x81, 0xFF, 0x82, 0xFF, 0x83, 0xFF, 0x84, 0xFF, 0x85, 0xFF, 0x86, 0x00, 0x87, 0x00, 0x88, 0x00, 0x89, 0xFF, 0x8A };
        var a = new Z80Asm(0x100);
        a.Db(0xF3, 0xED, 0x56, 0x31, 0xF0, 0xDF);
        if (gg) a.OutImm(0x06, 0xFF);                                                           // Game Gear stereo: every voice on both sides
        a.OutImm(0x7F, 0x8E).OutImm(0x7F, 0x0F).OutImm(0x7F, 0x90);                             // tone 1: latch low nibble $E, data $0F (period $0FE), volume 0 (loudest)
        a.OutImm(0x7F, 0xDF).OutImm(0x7F, 0xFF);                                                // tone 3 and noise off (attenuation 15)
        a.Db(0x0E, 0xBF, 0x06, regs.Length).LdHl("regs").Db(0xED, 0xB3);
        a.OutImm(0xBF, 0x00).OutImm(0xBF, 0xC0);
        a.Db(0x0E, 0xBE, 0x06, gg ? 64 : 32).LdHl("pal").Db(0xED, 0xB3);
        a.OutImm(0xBF, 0x00).OutImm(0xBF, 0x40);
        a.Db(0x06, 128).LdHl("tiles").Db(0xED, 0xB3);
        a.OutImm(0xBF, 0x00).OutImm(0xBF, 0x78);
        a.Db(0x11, 0x00, 0x03, 0x0E, 0x01);
        a.Label("fill").Db(0x79).OutN(0xBE).Db(0xAF).OutN(0xBE).Db(0x0C, 0x79, 0xFE, 0x04).JrNz("nowrap").Db(0x0E, 0x01);
        a.Label("nowrap").Db(0x1B, 0x7A, 0xB3).JrNz("fill");
        a.OutImm(0xBF, 0x00).OutImm(0xBF, 0x7F).OutImm(0xBE, 0xD0);
        a.OutImm(0xBF, 0xC0).OutImm(0xBF, 0x81);
        a.Label("main").InN(0x7E).Db(0xFE, 0xC0).JrNz("main");
        a.Db(0x3A, 0x00, 0xC1, 0x3C, 0x32, 0x00, 0xC1).OutN(0xBF).OutImm(0xBF, 0x88);          // counter++ ; R8 = counter (the picture scrolls every frame)
        a.Label("idle").InN(0x7E).Db(0xFE, 0xC0).JrZ("idle");
        a.Jr("main");
        a.Label("regs").Bytes(regs.Select(x => (byte)x));
        var pal = new byte[gg ? 64 : 32];
        if (!gg) { pal[1] = 0x30; pal[2] = 0x0C; pal[3] = 0x03; } else { pal[2] = 0x00; pal[3] = 0x0F; pal[4] = 0xF0; pal[6] = 0x0F; }
        a.Label("pal").Bytes(pal);
        var tiles = new byte[128];
        for (int tile = 1; tile <= 3; tile++)
            for (int row = 0; row < 8; row++) { tiles[tile * 32 + row * 4] = (byte)((tile & 1) != 0 ? 0xFF : 0); tiles[tile * 32 + row * 4 + 1] = (byte)((tile & 2) != 0 ? 0xFF : 0); }
        a.Label("tiles").Bytes(tiles);
        return SmsProg.Rom(a.Assemble(), null, null, 0x100);
    }

    // ---------------------------------------------------------------------------------------------------- plumbing

    private static IConsoleSession Make(ConsoleKind console, byte[] rom, string name, string? ppu = null, string? apu = null) =>
        ConsoleSessions.Create(console, rom, CoreCatalog.Default(console, CoreSlot.Cpu), ppu ?? CoreCatalog.Default(console, CoreSlot.Ppu), apu ?? CoreCatalog.Default(console, CoreSlot.Apu), null, name);

    private sealed record Snap(string Ram, string Vram, string Cram, ushort Pc, long Cycles, string Pic, int Colours, byte Counter);

    private static IConsoleSession Real(IConsoleSession s) => s is BrokenSwap b ? b.Inner : s;

    private static string Sha(ReadOnlySpan<byte> b) => Convert.ToHexString(SHA1.HashData(b))[..16];

    private static Snap Take(IConsoleSession s)
    {
        var b = SmsRomIo.Concrete(Real(s)).Board;
        int n = s.FrameWidth * s.FrameHeight;
        var px = s.Frame.AsSpan(0, n);
        return new Snap(Sha(b.Ram), Sha(b.Vdp.Vram), Sha(Enumerable.Range(0, 64).Select(i => b.Vdp.CramByte(i)).ToArray()), b.Cpu.PC, b.Cycles,
            Sha(MemoryMarshal.AsBytes(px)), px.ToArray().Distinct().Count(), b.Ram[0x100]);
    }

    /// <summary>Runs frames with the given pad script and returns the mono audio of them.</summary>
    private static List<short> Play(IConsoleSession s, int frames, Dictionary<int, PadButtons>? script = null, int from = 0)
    {
        var mono = new List<short>(); var buf = new short[16384]; PadButtons held = 0;
        if (script != null) foreach (var kv in script.Where(k => k.Key <= from).OrderBy(k => k.Key)) held = kv.Value;
        for (int f = from; f < from + frames; f++)
        {
            if (script != null && script.TryGetValue(f, out var h)) held = h;
            s.SetPad(0, held);
            s.RunFrame();
            int n; while ((n = s.ReadSamples(buf)) > 0) for (int i = 0; i + 1 < n; i += 2) mono.Add((short)((buf[i] + buf[i + 1]) / 2));
        }
        return mono;
    }

    /// <summary>The pitch of a steady tone: rising zero crossings (with hysteresis a quarter of the peak) per second; also the peak.</summary>
    private static (double Hz, int Peak) Tone(List<short> mono, int rate)
    {
        if (mono.Count == 0) return (0, 0);
        double mean = 0; foreach (var v in mono) mean += v; mean /= mono.Count;       // a NES chip's output is not centred on zero
        double min = double.MaxValue, max = double.MinValue; foreach (var v in mono) { min = Math.Min(min, v); max = Math.Max(max, v); }
        int peak = (int)Math.Max(Math.Abs(max - mean), Math.Abs(min - mean));
        if (peak < 50) return (0, peak);
        double hi = mean + peak / 4.0, lo = mean - peak / 4.0; int crossings = 0; bool armed = false; int first = -1, last = -1;
        for (int i = 0; i < mono.Count; i++)
        {
            if (mono[i] < lo) armed = true;
            else if (armed && mono[i] > hi) { armed = false; crossings++; if (first < 0) first = i; last = i; }
        }
        return crossings < 3 ? (0, peak) : ((crossings - 1) * (double)rate / (last - first), peak);
    }

    private static bool SameAudio(List<short> a, List<short> b) => a.Count == b.Count && a.SequenceEqual(b);

    private static string Describe(Snap a, Snap b)
    {
        var d = new List<string>();
        if (a.Ram != b.Ram) d.Add("work RAM"); if (a.Vram != b.Vram) d.Add("VRAM"); if (a.Cram != b.Cram) d.Add("colour RAM"); if (a.Pc != b.Pc) d.Add($"PC {a.Pc:X4}/{b.Pc:X4}"); if (a.Cycles != b.Cycles) d.Add($"cycles {a.Cycles}/{b.Cycles}");
        return string.Join(", ", d);
    }

    /// <summary>A session that looks like the app's but swaps like a broken one: it says yes to every id and, for the sound slot, silences the PSG instead of handing it over. The proof that the checks can fail.</summary>
    private sealed class BrokenSwap : IConsoleSession
    {
        private readonly IConsoleSession inner;
        public IConsoleSession Inner => inner;
        public BrokenSwap(IConsoleSession inner) { this.inner = inner; }
        public ConsoleKind Console => inner.Console; public string Title => inner.Title; public string Description => inner.Description; public double FramesPerSecond => inner.FramesPerSecond;
        public void SetPad(int player, PadButtons buttons) => inner.SetPad(player, buttons); public void RunFrame() => inner.RunFrame(); public void Reset() => inner.Reset();
        public uint[] Frame => inner.Frame; public int FrameWidth => inner.FrameWidth; public int FrameHeight => inner.FrameHeight; public int DisplayWidth => inner.DisplayWidth; public int SampleRate => inner.SampleRate;
        public int ReadSamples(short[] buffer) => inner.ReadSamples(buffer); public bool HasBattery => inner.HasBattery; public byte[] ExportSave() => inner.ExportSave(); public void ImportSave(byte[] data) => inner.ImportSave(data);
        public string GameId => inner.GameId; public void Dispose() => inner.Dispose();
        public bool TrySwapCore(CoreSlot slot, string id) { if (slot == CoreSlot.Apu) SmsRomIo.Concrete(inner).Board.Sound.Reset(); return true; }
    }

    private static IConsoleSession Maybe(SmsSuiteContext ctx, IConsoleSession s) => ctx.Poison == "swap" ? new BrokenSwap(s) : s;

    // ---------------------------------------------------------------------------------------------------- the suite

    public static IEnumerable<SmsCheck> Run(SmsSuiteContext ctx)
    {
        foreach (var console in new[] { ConsoleKind.MasterSystem, ConsoleKind.GameGear })
        {
            bool gg = console == ConsoleKind.GameGear;
            string tag = gg ? "Game Gear" : "Master System", file = gg ? "swap.gg" : "swap.sms";
            byte[] rom = ToneScroller(gg);

            // the control: no swap
            var control = Make(console, rom, file);
            Play(control, Warm);
            byte warmCounter = SmsRomIo.Concrete(control).Board.Ram[0x100];
            int rate = control.SampleRate;
            var controlAudio = Play(control, After);
            var want = Take(control);
            var (ctlHz, ctlPeak) = Tone(controlAudio, rate);
            yield return new SmsCheck($"{tag}: the control program plays its tone ({ctlHz:F1} Hz wanted {ToneHz:F1}) and scrolls ({want.Counter - warmCounter} frames counted in the {After} it ran)", Math.Abs(ctlHz / ToneHz - 1) < 0.01 && ctlPeak > 1000 && (byte)(want.Counter - warmCounter) == After, $"peak {ctlPeak}");

            // the control twice is the same: a swap test that compared against a moving target would prove nothing
            var again = Make(console, rom, file); Play(again, Warm); var againAudio = Play(again, After);
            yield return new SmsCheck($"{tag}: the control run is repeatable (machine, picture and every sample equal to a second run)", Describe(Take(again), want).Length == 0 && Take(again).Pic == want.Pic && SameAudio(againAudio, controlAudio));

            // ---- every id of every slot of the console's menus, swapped in the running game
            foreach (var slot in new[] { CoreSlot.Cpu, CoreSlot.Ppu, CoreSlot.Apu })
                foreach (var option in CoreCatalog.Options(console, slot))
                {
                    var s = Maybe(ctx, Make(console, rom, file));
                    Play(s, Warm);
                    bool accepted = s.TrySwapCore(slot, option.Id);
                    var audio = Play(s, After);
                    var got = Take(s);
                    var problems = new List<string>();
                    if (!accepted) problems.Add("the swap was refused (a menu id must swap in place)");
                    string differs = Describe(got, want);
                    if (differs != "") problems.Add("the game was disturbed: " + differs);
                    if (!s.Description.Contains(slot switch { CoreSlot.Cpu => "CPU " + option.Id, CoreSlot.Ppu => option.Id.StartsWith("NES:") ? "PICTURE " + option.Id : "PPU " + option.Id, _ => "APU " + option.Id }, StringComparison.OrdinalIgnoreCase))
                        problems.Add("the description does not name the new core: " + s.Description);
                    if (Math.Abs(audio.Count - controlAudio.Count) > 8) problems.Add($"audio {audio.Count} samples, control {controlAudio.Count}");   // a NES chip runs on its own sample clock: a sample or two over 90 frames
                    var (hz, peak) = Tone(audio, s.SampleRate);
                    double wantHz = ToneHz * (slot == CoreSlot.Apu ? PitchFactor.GetValueOrDefault(option.Id, 1.0) : 1.0);
                    if (Math.Abs(hz / wantHz - 1) > 0.02 || peak < 400) problems.Add($"the tone is {hz:F1} Hz at peak {peak}, wanted {wantHz:F1} Hz audible");
                    bool ownUnit = slot == CoreSlot.Apu && option.Family == Consoles.Family(console) && option.Id != "SMS-FM";
                    if (slot != CoreSlot.Apu || ownUnit)
                        if (!SameAudio(audio, controlAudio)) problems.Add("the sound is not the control's sample for sample");
                    if (slot == CoreSlot.Ppu)
                    {
                        if (option.Id.StartsWith("NES:"))
                        {
                            var w = Real(s) as SegaPictureSession;
                            bool bridged = w != null && w.Bridged;
                            if (!bridged || s.FrameWidth != 256 || s.FrameHeight != 240) problems.Add($"not drawn on the chip: {s.FrameWidth}x{s.FrameHeight}");
                            if (got.Colours < 2) problems.Add("the picture is blank");
                            var before = got.Pic; Play(s, 1); if (Take(s).Pic == before) problems.Add("the picture does not move");
                        }
                        else if (got.Pic != want.Pic) problems.Add("the picture is not the control's");
                    }
                    yield return new SmsCheck($"{tag}: swap {slot} -> {option.Id} mid-game keeps the game, the picture and the sound", problems.Count == 0, string.Join("; ", problems));
                }

            // ---- an id that is not on the menu is refused and nothing is touched
            foreach (var (slot, bogus) in new[] { (CoreSlot.Cpu, "BOGUS"), (CoreSlot.Ppu, "NES:NOPE"), (CoreSlot.Ppu, "MD"), (CoreSlot.Apu, "NO-SUCH-UNIT"), (CoreSlot.Apu, "NES:NOPE"), (CoreSlot.Apu, gg ? "SMS-FM" : "GG"), (CoreSlot.Apu, "") })
            {
                var s = Maybe(ctx, Make(console, rom, file));
                Play(s, Warm);
                string d0 = s.Description;
                bool refused = !s.TrySwapCore(slot, bogus);
                var audio = Play(s, After);
                bool same = Describe(Take(s), want).Length == 0 && s.Description == d0 && SameAudio(audio, controlAudio);
                yield return new SmsCheck($"{tag}: swapping {slot} to \"{bogus}\" is refused and the game goes on untouched", refused && same, refused ? (same ? "" : "the session changed: " + s.Description) : "accepted");
            }

            // ---- the restart path: the app builds a new session on an id a swap refused; every id of every menu must build one that runs
            var broken = new List<string>();
            foreach (var slot in new[] { CoreSlot.Ppu, CoreSlot.Apu })
                foreach (var option in CoreCatalog.Options(console, slot))
                {
                    try
                    {
                        using var s = slot == CoreSlot.Ppu ? Make(console, rom, file, ppu: option.Id) : Make(console, rom, file, apu: option.Id);
                        var audio = Play(s, 20);
                        if (Take(s).Colours < 2 || audio.Count == 0) broken.Add($"{slot} {option.Id}: blank or silent");
                    }
                    catch (Exception e) { broken.Add($"{slot} {option.Id}: {e.GetType().Name} {e.Message}"); }
                }
            yield return new SmsCheck($"{tag}: a session can be started on every picture and sound id of the menus (the restart path of a refused swap)", broken.Count == 0, string.Join("; ", broken.Take(4)));

            // ---- chains in one run
            foreach (var (name, steps) in new (string, (CoreSlot, string)[])[]
            {
                ("sound: own -> SMS-FM -> NES:FIX -> NES:FIXS -> own", gg ? new[] { (CoreSlot.Apu, "NES:FIX"), (CoreSlot.Apu, "NES:FIXS"), (CoreSlot.Apu, "GG") } : new[] { (CoreSlot.Apu, "SMS-FM"), (CoreSlot.Apu, "NES:FIX"), (CoreSlot.Apu, "NES:FIXS"), (CoreSlot.Apu, "SMS") }),
                ("picture: own -> NES:FIXS -> NES:DMG -> NES:MD -> NES:SNES -> NES:LQ -> own", new[] { (CoreSlot.Ppu, "NES:FIXS"), (CoreSlot.Ppu, "NES:DMG"), (CoreSlot.Ppu, "NES:MD"), (CoreSlot.Ppu, "NES:SNES"), (CoreSlot.Ppu, "NES:LQ"), (CoreSlot.Ppu, gg ? "GG" : "SMS") }),
                ("all three slots interleaved", gg ? new[] { (CoreSlot.Ppu, "NES:FIX"), (CoreSlot.Apu, "NES:FMC"), (CoreSlot.Cpu, "Z80"), (CoreSlot.Ppu, "NES:DMG"), (CoreSlot.Apu, "GG"), (CoreSlot.Ppu, "GG") }
                                           : new[] { (CoreSlot.Ppu, "NES:FIX"), (CoreSlot.Apu, "SMS-FM"), (CoreSlot.Cpu, "Z80"), (CoreSlot.Ppu, "NES:DMG"), (CoreSlot.Apu, "NES:FIXS"), (CoreSlot.Apu, "SMS"), (CoreSlot.Ppu, "SMS") }),
            })
            {
                var s = Maybe(ctx, Make(console, rom, file));
                Play(s, Warm - steps.Length * 5);
                var problems = new List<string>();
                var audioAll = new List<short>();
                foreach (var (slot, id) in steps)
                {
                    if (!s.TrySwapCore(slot, id)) problems.Add($"{slot} {id} refused");
                    audioAll.AddRange(Play(s, 5));
                }
                var audio = Play(s, After);
                string differs = Describe(Take(s), want);
                var (hz, peak) = Tone(audio, s.SampleRate);
                if (differs != "") problems.Add("the game was disturbed: " + differs);
                if (Math.Abs(hz / ToneHz - 1) > 0.02 || peak < 400) problems.Add($"the tone is {hz:F1} Hz at peak {peak}");
                if (Take(s).Pic != want.Pic) problems.Add("back on the console's own chips the picture is not the control's");
                // (not sample for sample: a unit that came back from a NES chip took over the PSG's registers, not the phase of its square waves, so its first cycle starts afresh)
                yield return new SmsCheck($"{tag}: chain, {name}", problems.Count == 0, string.Join("; ", problems));
            }

            // ---- a swap on every frame for 60 frames, in both directions
            {
                var s = Maybe(ctx, Make(console, rom, file));
                Play(s, Warm - 60);
                var problems = new List<string>();
                string[] units = gg ? new[] { "GG", "NES:FIX" } : new[] { "SMS", "SMS-FM", "NES:FIX" };
                string[] chips = { gg ? "GG" : "SMS", "NES:FIXS" };
                for (int i = 0; i < 60; i++)
                {
                    if (!s.TrySwapCore(CoreSlot.Apu, units[i % units.Length]) || !s.TrySwapCore(CoreSlot.Ppu, chips[i % chips.Length])) { problems.Add($"refused at swap {i}"); break; }
                    Play(s, 1);
                }
                string last = units[59 % units.Length];
                s.TrySwapCore(CoreSlot.Apu, gg ? "GG" : "SMS"); s.TrySwapCore(CoreSlot.Ppu, gg ? "GG" : "SMS");
                var audio = Play(s, After);
                string differs = Describe(Take(s), want);
                var (hz, peak) = Tone(audio, s.SampleRate);
                if (differs != "") problems.Add("the game was disturbed: " + differs);
                if (Math.Abs(hz / ToneHz - 1) > 0.02 || peak < 400) problems.Add($"the tone is {hz:F1} Hz at peak {peak}");
                yield return new SmsCheck($"{tag}: a sound and a picture swap on each of 60 frames in a row leave the game, its tone and its picture as they were", problems.Count == 0, string.Join("; ", problems));
            }
        }

        // ---- real games (needs --roms): swap in a game that is really being played; the game must go on exactly as the control does
        if (ctx.RomDir == null) { yield return SmsCheck.Skipped("swaps in real games", "no ROM directory (--roms <dir> with sms and gg folders)"); yield break; }
        foreach (var (folder, file) in new[] { ("sms", "Shinobi (USA, Europe).sms"), ("sms", "Wonder Boy (USA, Europe).sms"), ("gg", "Wonder Boy (E) [!].gg"), ("gg", "Columns (U) [!].gg") })
        {
            string path = Path.Combine(ctx.RomDir, folder, file);
            if (!File.Exists(path)) { yield return SmsCheck.Skipped("swaps in " + file, "ROM not found"); continue; }
            bool gg = folder == "gg";
            var console = gg ? ConsoleKind.GameGear : ConsoleKind.MasterSystem;
            byte[] rom = SmsRomIo.Load(path, out string inner);
            var script = SmsRomIo.ParseInput(gg ? GoldenSuite.PadGg : GoldenSuite.PadSms);
            const int WarmReal = 520, AfterReal = 120;
            var control = Make(console, rom, inner);
            Play(control, WarmReal, script);
            var controlAudio = Play(control, AfterReal, script, WarmReal);
            var want = Take(control);
            var (_, ctlPeak) = Tone(controlAudio, control.SampleRate);
            var swaps = new List<(CoreSlot, string)>();
            swaps.AddRange(CoreCatalog.Options(console, CoreSlot.Apu).Where(o => o.Id is "SMS-FM" or "NES:FIX" or "NES:FIXS" or "NES:SPD" or "NES:WF").Select(o => (CoreSlot.Apu, o.Id)));
            swaps.AddRange(CoreCatalog.Options(console, CoreSlot.Ppu).Where(o => o.Id is "NES:FIXS" or "NES:FIX" or "NES:LQ" or "NES:DMG" or "NES:DMGS" or "NES:SNES" or "NES:MD").Select(o => (CoreSlot.Ppu, o.Id)));
            swaps.Add((CoreSlot.Cpu, "Z80"));
            foreach (var (slot, id) in swaps)
            {
                var s = Maybe(ctx, Make(console, rom, inner));
                Play(s, WarmReal, script);
                bool ok = s.TrySwapCore(slot, id);
                var audio = Play(s, AfterReal, script, WarmReal);
                var got = Take(s);
                var problems = new List<string>();
                if (!ok) problems.Add("refused");
                string differs = Describe(got, want);
                if (differs != "") problems.Add("the game was disturbed: " + differs);
                var (_, peak) = Tone(audio, s.SampleRate);
                if (ctlPeak > 300 && slot == CoreSlot.Apu && peak < 100) problems.Add($"silent after the swap (peak {peak}, control {ctlPeak})");
                if (slot == CoreSlot.Ppu && got.Colours < 2) problems.Add("blank picture");
                yield return new SmsCheck($"{inner}: swap {slot} -> {id} at frame {WarmReal} of a played game: the game goes on as the control does", problems.Count == 0, string.Join("; ", problems));
            }
        }
    }
}
