// Round 6 (inject): the feasibility probe. `FruityHost inject-probe snes|genesis [--frames N]` runs a real game from the user's library and reports what its sound driver does to the sound memory and the chips while it plays,
// which is the evidence for the table in docs/projects/round6-inject.md: which parts of an instrument a push can make stick, and which the driver rewrites.
//   SNES (Super Mario World): every write to a voice's registers (the DSP's write tap), counted against the key-ons; every change of the audio RAM outside the echo buffer, by frame.
//   Genesis (Sonic the Hedgehog): every write to the YM2612's ports (the sound unit's tap), per channel and per key-on; the DAC writes.
using BrokenNes2;
using NesEmulator.Sega;
using NesEmulator.Snes;
using NesEmulator.Systems;

namespace BrokenNes.FruityHost;

internal static class InjectProbe
{
    public static int Main(string[] args)
    {
        string which = args.Length > 1 ? args[1] : "snes";
        int frames = int.TryParse(Opt(args, "--frames"), out int f) ? f : which == "snes" ? 4300 : 2400;
        return which switch { "snes" => Snes(Opt(args, "--rom") ?? InjectRun.Smw, frames, Opt(args, "--script") ?? InjectRun.SmwScript), "genesis" => Genesis(Opt(args, "--rom") ?? InjectRun.Sonic1, frames, Opt(args, "--script") ?? InjectRun.SonicScript), _ => 2 };
    }

    private static string? Opt(string[] a, string name) { int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }

    // ======================================================================================== SNES

    private static readonly string[] VoiceRegs = ["VOL L", "VOL R", "PITCH lo", "PITCH hi", "SRCN", "ADSR1", "ADSR2", "GAIN"];

    private static int Snes(string path, int frames, string script)
    {
        var rom = InjectRun.Rom(path, out string name);
        if (rom == null) { Console.WriteLine($"not tested: {path} is not there"); return 0; }
        var s = InjectRun.Create(ConsoleKind.Snes, rom, name);
        if (s is not SnesSession snes || snes.Apu is not APU_SFC spc) { Console.WriteLine("not an SFC audio unit"); return 1; }
        var dsp = spc.Dsp;
        int frame = 0;
        var written = new bool[8, 8];
        var changed = new bool[8, 8];
        var atKon = new int[8, 8];
        var rewrote = new int[8]; var changedAtKon = new int[8];
        var midNote = new int[8];
        var onVoice = new bool[8];
        int keyOns = 0;
        var perSlot = new Dictionary<int, (int Count, HashSet<int> Env, HashSet<int> Vol, HashSet<int> Pitch)>();
        dsp.WriteTap = (addr, value) =>
        {
            int v = addr >> 4, k = addr & 15;
            if (addr == 0x4C)
            {
                for (int i = 0; i < 8; i++)
                {
                    if ((value >> i & 1) == 0) continue;
                    keyOns++;
                    for (int r = 0; r < 8; r++) { if (written[i, r]) rewrote[r]++; if (changed[i, r]) changedAtKon[r]++; }
                    int srcn = dsp.Regs[i * 16 + 4];
                    if (!perSlot.TryGetValue(srcn, out var e)) perSlot[srcn] = e = (0, new(), new(), new());
                    e.Env.Add(dsp.Regs[i * 16 + 5] << 16 | dsp.Regs[i * 16 + 6] << 8 | dsp.Regs[i * 16 + 7]);
                    e.Vol.Add(dsp.Regs[i * 16] << 8 | dsp.Regs[i * 16 + 1]);
                    e.Pitch.Add(dsp.Regs[i * 16 + 2] | dsp.Regs[i * 16 + 3] << 8);
                    perSlot[srcn] = (e.Count + 1, e.Env, e.Vol, e.Pitch);
                    for (int r = 0; r < 8; r++) { written[i, r] = false; changed[i, r] = false; atKon[i, r] = dsp.Regs[i * 16 + r]; }
                    onVoice[i] = true;
                }
                return;
            }
            if (addr == 0x5C) { for (int i = 0; i < 8; i++) if ((value >> i & 1) != 0) onVoice[i] = false; return; }
            if (addr < 0x80 && v < 8 && k < 8)
            {
                written[v, k] = true;
                if (dsp.Regs[addr] != value && value != atKon[v, k]) changed[v, k] = true;
                if (onVoice[v]) midNote[k]++;
            }
        };

        // the audio RAM outside the echo buffer, page by page: when does the game load samples?
        var pageHash = new ulong[256];
        var events = new List<string>();
        var slotHash = new Dictionary<int, (int Start, int Len, ulong Hash)>();
        var slotEvents = new List<string>();
        void Watch(int f)
        {
            int esa = dsp.Regs[0x6D], edl = (dsp.Regs[0x7D] & 15) * 8;       // echo buffer: ESA pages, EDL x 2 KB = 8 pages each
            int changedPages = 0;
            for (int p = 2; p < 0xF0; p++)
            {
                if (p >= esa && p < esa + Math.Max(edl, 1)) continue;
                ulong h = InjectRun.Hash(spc.Aram.AsSpan(p * 256, 256));
                if (pageHash[p] != h && pageHash[p] != 0) changedPages++;
                pageHash[p] = h;
            }
            if (changedPages >= 4) events.Add($"frame {f}: {changedPages} pages of audio RAM (outside the echo buffer) changed");
            var snap = new SpcSnapshot(spc.Aram, dsp.Regs);
            foreach (var inst in snap.Instruments())
            {
                var key = (inst.Start, inst.Blocks * 9, InjectRun.Hash(spc.Aram.AsSpan(inst.Start, Math.Min(inst.Blocks * 9, 0x10000 - inst.Start))));
                if (slotHash.TryGetValue(inst.Slot, out var old) && old != key) slotEvents.Add($"frame {f}: sample {inst.Slot} changed (${old.Start:X4}+{old.Len} -> ${key.Item1:X4}+{key.Item2})");
                slotHash[inst.Slot] = key;
            }
        }
        InjectRun.Frames(s, 0, frames, InjectRun.Pad(script), null, f => { frame = f; if (f % 20 == 0) Watch(f); });

        Console.WriteLine($"SNES probe: {name}, {frames} frames, {keyOns} key-ons");
        Console.WriteLine("Before each key-on, how often the driver wrote each voice register since that voice's previous key-on (value changed in brackets):");
        for (int r = 0; r < 8; r++)
            Console.WriteLine($"  {VoiceRegs[r],-9} written at {rewrote[r] * 100.0 / Math.Max(1, keyOns),5:0.0}% of key-ons   (differing from the value at the previous key-on: {changedAtKon[r] * 100.0 / Math.Max(1, keyOns):0.0}%)   writes while the note sounds: {midNote[r]}");
        Console.WriteLine("Samples keyed on (slot: key-ons, distinct envelope triples, distinct volume pairs, distinct pitches):");
        foreach (var kv in perSlot.OrderByDescending(x => x.Value.Count).Take(14))
            Console.WriteLine($"  slot {kv.Key,3}: {kv.Value.Count,5} key-ons, {kv.Value.Env.Count} envelopes, {kv.Value.Vol.Count} volume pairs, {kv.Value.Pitch.Count} pitches");
        Console.WriteLine($"Audio RAM outside the echo buffer, sampled every 20 frames: {events.Count} loads");
        foreach (var e in events.Take(20)) Console.WriteLine("  " + e);
        Console.WriteLine($"The sample table's entries changing (start, length or bytes): {slotEvents.Count}");
        foreach (var e in slotEvents.Take(25)) Console.WriteLine("  " + e);
        return 0;
    }

    // ======================================================================================== Genesis

    private static int Genesis(string path, int frames, string script)
    {
        var rom = InjectRun.Rom(path, out string name);
        if (rom == null) { Console.WriteLine($"not tested: {path} is not there"); return 0; }
        var s = InjectRun.Create(ConsoleKind.Genesis, rom, name);
        var inner = s is NesEmulator.Mix.SegaPictureSession w ? w.Inner : s;
        if (inner is not MdSession md || md.Board.Sound is not MdSoundUnit unit) { Console.WriteLine("not a Genesis session"); return 1; }
        // register classes: 0 DT/MUL, 1 TL, 2 RS/AR, 3 AM/D1R, 4 D2R, 5 SL/RR, 6 SSG, 7 FB/ALG, 8 pan/AMS/FMS, 9 frequency
        string[] classes = ["DT/MUL", "TL", "RS/AR", "AM/D1R", "D2R", "SL/RR", "SSG-EG", "FB/ALG", "pan/AMS/FMS", "frequency"];
        var wrote = new bool[6, 10];
        var rewrote = new int[10];
        var keyOns = new int[6];
        var patchSeen = new HashSet<string>[6];
        for (int c = 0; c < 6; c++) patchSeen[c] = new();
        var ym = unit.Fm;
        int addr0 = 0, addr1 = 0, dacWrites = 0, dacStretches = 0;
        long lastDac = -1000000;
        var carrier = new bool[8, 4]
        {
            { false, false, false, true }, { false, false, false, true }, { false, false, false, true }, { false, false, false, true },
            { false, false, true, true }, { false, true, true, true }, { false, true, true, true }, { true, true, true, true },
        };

        unit.YmPortTap = (m, port, value) =>
        {

            if (port == 0) { addr0 = value; return; }
            if (port == 2) { addr1 = value; return; }
            int reg = port == 1 ? addr0 : 0x100 | addr1;
            int low = reg & 0xFF, part = reg >> 8;
            if (reg == 0x2A) { dacWrites++; if (m - lastDac > 400000) dacStretches++; lastDac = m; return; }
            if (reg == 0x28)
            {
                int c = value & 3; if (c == 3) return;
                c += (value >> 2 & 1) * 3;
                if ((value >> 4) == 0) return;
                keyOns[c]++;
                for (int k = 0; k < 10; k++) if (wrote[c, k]) rewrote[k]++;
                // the patch the channel holds now, ignoring the carriers' total level (the driver's volume)
                var sb = new System.Text.StringBuilder();
                int alg = ym.Register(part * 0 + 0xB0 + c % 3 + (c / 3) * 0x100) & 7;
                int[] off = [0, 8, 4, 12];
                for (int n = 0; n < 4; n++)
                    for (int k = 0; k < 7; k++)
                    {
                        if (k == 1 && carrier[alg, n]) continue;
                        sb.Append(ym.Register((c / 3) * 0x100 + 0x30 + k * 0x10 + off[n] + c % 3).ToString("X2"));
                    }
                sb.Append(ym.Register((c / 3) * 0x100 + 0xB0 + c % 3).ToString("X2"));
                patchSeen[c].Add(sb.ToString());
                for (int k = 0; k < 10; k++) wrote[c, k] = false;
                return;
            }
            if (low >= 0x30 && low <= 0x9F && (low & 3) != 3)
            {
                int c = (low & 3) + part * 3;
                wrote[c, (low - 0x30) >> 4] = true;
            }
            else if (low >= 0xB0 && low <= 0xB2) wrote[(low & 3) + part * 3, 7] = true;
            else if (low >= 0xB4 && low <= 0xB6) wrote[(low & 3) + part * 3, 8] = true;
            else if (low >= 0xA0 && low <= 0xA6) wrote[(low & 3) + part * 3, 9] = true;
        };
        InjectRun.Frames(s, 0, frames, InjectRun.Pad(script));
        Console.WriteLine($"Genesis probe: {name}, {frames} frames, key-ons per FM channel: {string.Join(" ", keyOns)}; DAC writes {dacWrites} in {dacStretches} stretches");
        int total = keyOns.Sum();
        Console.WriteLine("Before each key-on, how often the driver wrote each register class of that channel since the previous key-on:");
        for (int k = 0; k < 10; k++) Console.WriteLine($"  {classes[k],-12} {rewrote[k] * 100.0 / Math.Max(1, total),5:0.0}% of {total} key-ons");
        Console.WriteLine("Distinct patches each channel played at a key-on (all registers but the carriers' total level): " + string.Join(", ", Enumerable.Range(0, 6).Select(c => $"FM{c + 1} {patchSeen[c].Count}")));
        return 0;
    }
}
