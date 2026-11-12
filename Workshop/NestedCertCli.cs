using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using NesEmulator;
using NesEmulator.Snes;

namespace BrokenNes.Workshop;

/// <summary>
/// Nested certification lockstep (docs/projects/nested-cert): a NES game on BrokenNes's NES cores (FIX by default)
/// against its Project Nested SNES conversion on BrokenNes's SNES core, locked on the GAME's controller polls rather
/// than on frames.
///
/// Why polls: a Nested ROM runs the NES code at a different speed (boot, recompile stalls, slow paths), so frame N on
/// one side is not frame N on the other. Every NES $4016 write with bit 0 clear (the end of a strobe) is one poll on
/// the NES side; Project Nested turns exactly that write into a SNES $4016 1/0 strobe (IO__w4016_CopyStart), which is
/// one poll on the SNES side. The NES run records the buttons held at each poll; the SNES run presses those same
/// buttons at the same poll. Both sides then saw identical input per game step, and the NES work RAM ($0000-$07FF;
/// Nested keeps it at SNES WRAM $7E:0000) is compared at every poll, sampled at the same point of the game's code.
///
/// Lag: frames that end with no poll after the first poll (the game did not finish a step that frame), each side.
/// Drift: SNES frame - NES frame at the same poll; its growth is the SNES's extra lag.
///
/// Usage:
///   --nested-cert --nes game.nes --snes game.smc [--frames N (NES frames, default 3600)] [--input "frame:Buttons,..."]
///                 [--cpu FIX --ppu FIX --apu FIX] [--power-on-ram fceux|zeros|ones] [--ignore 0100-01FF,...]
///                 [--watch name=addr,...] [--snes-map default|direct] [--out-dir dir] [--png-polls p1,p2]
///                 [--snes-extra-frames N (default 1200)] [--json]
/// NES button names: A B Select Start Up Down Left Right. With the default (non-custom) Nested input, the SNES serial
/// order B Y Select Start Up Down Left Right is what the NES code reads, so NES A = SNES B and NES B = SNES Y.
/// Measures only: nothing in either core changes.
/// </summary>
internal static class NestedCertCli
{
    private const string Usage =
        "Usage: --nested-cert --nes <game.nes> --snes <game.smc> [--frames N] [--input \"f:Buttons,...\"] [--cpu FIX --ppu FIX --apu FIX]\n" +
        "       [--power-on-ram fceux|zeros|ones] [--ignore 0100-01FF,...] [--watch name=hex,...] [--out-dir dir] [--png-polls p,...]\n" +
        "       [--snes-extra-frames N] [--snes-sram file.srm] [--json] [--raw]";

    private sealed class Poll
    {
        public int Frame;          // 1-based frame the poll happened in
        public byte Buttons;       // NES order bits 0..7 = A B Select Start Up Down Left Right
        public byte[] Ram = Array.Empty<byte>();
    }

    private sealed class NesTap : IBusDebugTap
    {
        public readonly List<Poll> Polls = new();
        public NES Nes = null!;
        public int Frame;
        public byte Held;
        public void OnRead(ushort address, byte value) { }
        public void OnOamDma(byte page) { }
        public void OnDmcDmaHalt(ushort address) { }
        public void OnWrite(ushort address, byte value)
        {
            if (address != 0x4016 || (value & 1) != 0) return;
            var ram = new byte[2048];
            for (int i = 0; i < 2048; i++) ram[i] = Nes.PeekSystemRam(i);
            Polls.Add(new Poll { Frame = Frame, Buttons = Held, Ram = ram });
        }
    }

    public static int Run(string[] args)
    {
        RomTestCli.EnsureConsole();
        string? sync = null, nesPath = null, snesPath = null, input = null, outDir = null, ignore = "0100-01FF", watch = null, sramPath = null;
        string cpu = "FIX", ppu = "FIX", apu = "FIX", powerOnRam = "fceux", snesMap = "default";
        int frames = 3600, snesExtra = 1200;
        bool json = false, raw = false;
        var pngPolls = new HashSet<int>();
        try
        {
            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--nes": nesPath = args[++i]; break;
                    case "--snes": snesPath = args[++i]; break;
                    case "--frames": frames = int.Parse(args[++i]); break;
                    case "--input": input = args[++i]; break;
                    case "--cpu": cpu = args[++i]; break;
                    case "--ppu": ppu = args[++i]; break;
                    case "--apu": apu = args[++i]; break;
                    case "--power-on-ram": powerOnRam = args[++i].ToLowerInvariant(); break;
                    case "--ignore": ignore = args[++i]; break;
                    case "--watch": watch = args[++i]; break;
                    case "--snes-map": snesMap = args[++i].ToLowerInvariant(); break;
                    case "--out-dir": outDir = args[++i]; break;
                    case "--png-polls": foreach (var p in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries)) pngPolls.Add(int.Parse(p)); break;
                    case "--snes-extra-frames": snesExtra = int.Parse(args[++i]); break;
                    case "--snes-sram": sramPath = args[++i]; break;
                    case "--json": json = true; break;
                    case "--raw": raw = true; break;
                    case "--sync": sync = args[++i]; break;   // hex addrs/ranges copied NES->SNES at every poll (harness alignment, counted)   // every poll's 2 KB, both sides: <stem>.nes.ram / .snes.ram
                    default: Console.Error.WriteLine($"Unknown argument: {args[i]}\n{Usage}"); return 2;
                }
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or FormatException)
        {
            Console.Error.WriteLine($"Bad arguments: {ex.Message}\n{Usage}"); return 2;
        }
        if (nesPath == null || snesPath == null) { Console.Error.WriteLine(Usage); return 2; }
        outDir ??= Directory.GetCurrentDirectory();
        Directory.CreateDirectory(outDir);

        var ignored = new bool[2048];
        foreach (var r in (ignore ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var ab = r.Split('-');
            int a = Convert.ToInt32(ab[0], 16), b = ab.Length > 1 ? Convert.ToInt32(ab[1], 16) : a;
            for (int x = a; x <= b && x < 2048; x++) ignored[x] = true;
        }
        // --sync: bytes the harness copies from the NES poll into the SNES RAM at the same poll (before sampling), to take
        // known timing-only differences (an NMI counter, a frame-seeded RNG) out of the comparison. Every copy that changed a
        // byte is counted and reported: a synced byte is a measured difference, never a hidden one.
        var syncAddrs = new List<int>();
        foreach (var r in (sync ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var ab = r.Split('-');
            int a = Convert.ToInt32(ab[0], 16), b = ab.Length > 1 ? Convert.ToInt32(ab[1], 16) : a;
            for (int x = a; x <= b && x < 2048; x++) syncAddrs.Add(x);
        }
        var syncFixes = new Dictionary<int, int>(); int syncFirst = -1;
        var watches = new List<(string Name, int Addr)>();
        foreach (var w in (watch ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = w.Split('=');
            watches.Add((kv[0], Convert.ToInt32(kv[1], 16)));
        }

        var script = RomTestCli.ParseInputScript(input);

        // ---- NES run ----
        var nes = new NES { RomName = Path.GetFileName(nesPath) };
        nes.LoadROM(File.ReadAllBytes(nesPath));
        var ca = RomTestCli.ApplyCore("CPU", cpu, nes.SetCpuCore, nes.GetCpuCoreId);
        var pa = RomTestCli.ApplyCore("PPU", ppu, nes.SetPpuCore, nes.GetPpuCoreId);
        var aa = RomTestCli.ApplyCore("APU", apu, nes.SetApuCore, nes.GetApuCoreId);
        if (!ca.Applied || !pa.Applied || !aa.Applied) { Console.Error.WriteLine("NES core not applied"); return 3; }
        if (powerOnRam != "fceux") for (int ri = 0; ri < 2048; ri++) nes.PokeSystemRam(ri, powerOnRam == "ones" ? (byte)0xFF : (byte)0x00);
        var tap = new NesTap { Nes = nes };
        nes.DebugBus!.DebugTap = tap;
        var held = new bool[8];
        int next = 0;
        var nesPollsPerFrame = new int[frames + 1];
        for (int f = 0; f < frames; f++)
        {
            while (next < script.Count && script[next].Frame <= f) { held = (bool[])script[next].Held.Clone(); next++; }
            byte bits = 0;
            for (int k = 0; k < 8; k++) if (held[k]) bits |= (byte)(1 << k);
            tap.Held = bits; tap.Frame = f + 1;
            int before = tap.Polls.Count;
            nes.SetInputs(held, null);
            nes.RunFrame();
            nesPollsPerFrame[f + 1] = tap.Polls.Count - before;
            if (nes.IsCrashed()) { Console.Error.WriteLine($"NES crashed at frame {f + 1}"); break; }
        }
        nes.DebugBus!.DebugTap = null;
        var nesPolls = tap.Polls;

        // ---- SNES run, the same buttons per poll ----
        var file = File.ReadAllBytes(snesPath);
        var cart = SnesCartridge.Load(file);
        if (sramPath != null && File.Exists(sramPath) && cart.Sram.Length > 0)
        {
            var img = File.ReadAllBytes(sramPath);
            Array.Copy(img, cart.Sram, Math.Min(img.Length, cart.Sram.Length));
        }
        var chip = SnesFirmware.CreateCoprocessor(cart, snesPath, out _);
        var board = new BOARD_SFC(cart, SnesApuChoice.Create(null), chip);
        var snesPolls = new List<Poll>();
        int snesFrame = 0;
        ushort MapButtons(byte b)
        {
            // default Nested input: the NES code reads the SNES serial bits 15..8 = B Y Select Start Up Down Left Right
            ushort p = 0;
            for (int k = 0; k < 8; k++) if ((b & (1 << k)) != 0) p |= (ushort)(0x8000 >> k);
            return p;
        }
        // Buttons for the next SNES poll index; before the first NES poll and after the last, release / keep the last.
        byte ButtonsFor(int idx) => nesPolls.Count == 0 ? (byte)0 : nesPolls[Math.Min(idx, nesPolls.Count - 1)].Buttons;
        board.Pads[0] = MapButtons(ButtonsFor(0));
        board.WriteWatch = (bank, offset, value) =>
        {
            if ((bank & 0x40) != 0 || offset != 0x4016 || (value & 1) == 0) return;
            // The strobe-high write: Pads are latched right after this callback. Sample the NES RAM image Nested keeps at $7E:0000.
            int idx = snesPolls.Count;
            board.Pads[0] = MapButtons(ButtonsFor(idx));
            if (idx < nesPolls.Count)
                foreach (int x in syncAddrs)
                {
                    byte want = nesPolls[idx].Ram[x];
                    if (board.Wram[x] == want) continue;
                    board.Wram[x] = want; syncFixes[x] = syncFixes.GetValueOrDefault(x) + 1; if (syncFirst < 0) syncFirst = idx;
                }
            var ram = new byte[2048];
            Array.Copy(board.Wram, 0, ram, 0, 2048);
            snesPolls.Add(new Poll { Frame = snesFrame, Buttons = ButtonsFor(idx), Ram = ram });
        };
        int snesMaxFrames = frames + snesExtra;
        var snesPollsPerFrame = new List<int> { 0 };
        for (int f = 1; f <= snesMaxFrames; f++)
        {
            snesFrame = f;
            int before = snesPolls.Count;
            board.RunFrame();
            snesPollsPerFrame.Add(snesPolls.Count - before);
            if (board.Cpu.Stopped) { Console.Error.WriteLine($"SNES CPU stopped at frame {f}"); break; }
            if (snesPolls.Count >= nesPolls.Count + 2) break;
        }
        int snesFramesRun = snesFrame;

        // ---- compare ----
        int n = Math.Min(nesPolls.Count, snesPolls.Count);
        var addrDiffPolls = new int[2048];
        var addrFirst = Enumerable.Repeat(-1, 2048).ToArray();
        int firstDiff = -1, diffPolls = 0, lastDiff = -1;
        var diffRuns = new List<(int From, int To, int MaxBytes)>();
        int runFrom = -1, runMax = 0;
        for (int p = 0; p < n; p++)
        {
            var a = nesPolls[p].Ram; var b = snesPolls[p].Ram;
            int cnt = 0;
            for (int x = 0; x < 2048; x++)
            {
                if (ignored[x] || a[x] == b[x]) continue;
                cnt++; addrDiffPolls[x]++;
                if (addrFirst[x] < 0) addrFirst[x] = p;
            }
            if (cnt > 0)
            {
                diffPolls++; lastDiff = p;
                if (firstDiff < 0) firstDiff = p;
                if (runFrom < 0) { runFrom = p; runMax = 0; }
                runMax = Math.Max(runMax, cnt);
            }
            else if (runFrom >= 0) { diffRuns.Add((runFrom, p - 1, runMax)); runFrom = -1; }
        }
        if (runFrom >= 0) diffRuns.Add((runFrom, n - 1, runMax));

        // Lag: frames (after the first poll) that ended with no poll.
        static (int Lag, List<(int From, int Len)> Runs) Lag(IReadOnlyList<int> perFrame, int from, int to)
        {
            int lag = 0; var runs = new List<(int, int)>(); int s = -1;
            for (int f = from; f <= to && f < perFrame.Count; f++)
            {
                if (perFrame[f] == 0) { lag++; if (s < 0) s = f; }
                else if (s >= 0) { runs.Add((s, f - s)); s = -1; }
            }
            if (s >= 0) runs.Add((s, to - s + 1));
            return (lag, runs);
        }
        int nesFirstPollFrame = nesPolls.Count > 0 ? nesPolls[0].Frame : 0;
        int snesFirstPollFrame = snesPolls.Count > 0 ? snesPolls[0].Frame : 0;
        int nesLastCompared = n > 0 ? nesPolls[n - 1].Frame : 0;
        int snesLastCompared = n > 0 ? snesPolls[n - 1].Frame : 0;
        var nesLag = Lag(nesPollsPerFrame, Math.Max(1, nesFirstPollFrame), nesLastCompared);
        var snesLag = Lag(snesPollsPerFrame, Math.Max(1, snesFirstPollFrame), snesLastCompared);

        // PNGs at the requested polls: run each side again to that poll's frame (deterministic).
        var sb = new StringBuilder();
        sb.AppendLine($"nested-cert: NES {Path.GetFileName(nesPath)} ({ca.Effective}/{pa.Effective}/{aa.Effective}, power-on RAM {powerOnRam}) vs SNES {Path.GetFileName(snesPath)} \"{cart.Title}\"");
        sb.AppendLine($"NES frames {frames}, polls {nesPolls.Count}, first poll at frame {nesFirstPollFrame}; SNES frames run {snesFramesRun}, polls {snesPolls.Count}, first poll at frame {snesFirstPollFrame}");
        sb.AppendLine($"compared polls {n} (NES frames {nesFirstPollFrame}-{nesLastCompared}, SNES frames {snesFirstPollFrame}-{snesLastCompared}); ignored {(ignore ?? "")}");
        if (syncAddrs.Count > 0)
            sb.AppendLine($"synced (NES->SNES at each poll): {sync}; corrections {syncFixes.Values.Sum()} from poll {syncFirst}: " + string.Join(" ", syncFixes.OrderBy(k => k.Key).Select(k => $"${k.Key:X4}x{k.Value}")));
        sb.AppendLine($"polls with a RAM difference: {diffPolls}; first {Describe(firstDiff)}; last {Describe(lastDiff)}");
        string Describe(int p) => p < 0 ? "none" : $"poll {p} (NES frame {nesPolls[p].Frame}, SNES frame {snesPolls[p].Frame})";
        sb.AppendLine($"lag after the first poll: NES {nesLag.Lag} frames, SNES {snesLag.Lag} frames; boot offset {snesFirstPollFrame - nesFirstPollFrame}, end drift {snesLastCompared - nesLastCompared}");
        sb.AppendLine("SNES lag runs (frame x length, top 30 by length): " + string.Join(" ", snesLag.Runs.OrderByDescending(r => r.Len).Take(30).Select(r => $"{r.From}x{r.Len}")));
        sb.AppendLine("NES lag runs (frame x length, top 30 by length): " + string.Join(" ", nesLag.Runs.OrderByDescending(r => r.Len).Take(30).Select(r => $"{r.From}x{r.Len}")));
        // Drift timeline: every 300 NES frames.
        sb.AppendLine("drift timeline (poll: NES frame -> SNES frame, drift):");
        int lastBucket = -1;
        for (int p = 0; p < n; p++)
        {
            int bucket = nesPolls[p].Frame / 300;
            if (bucket == lastBucket) continue;
            lastBucket = bucket;
            sb.AppendLine($"  poll {p,6}: {nesPolls[p].Frame,6} -> {snesPolls[p].Frame,6}  drift {snesPolls[p].Frame - nesPolls[p].Frame}");
        }
        sb.AppendLine($"difference runs ({diffRuns.Count}; poll range, NES frames, max bytes):");
        foreach (var r in diffRuns.Take(60))
            sb.AppendLine($"  polls {r.From}-{r.To} (NES f{nesPolls[r.From].Frame}-{nesPolls[r.To].Frame}, SNES f{snesPolls[r.From].Frame}-{snesPolls[r.To].Frame}) max {r.MaxBytes} bytes");
        sb.AppendLine("addresses that ever differ (addr: polls differing, first poll / NES frame):");
        foreach (var x in Enumerable.Range(0, 2048).Where(x => addrDiffPolls[x] > 0).OrderBy(x => addrFirst[x]).ThenBy(x => x).Take(200))
            sb.AppendLine($"  ${x:X4}: {addrDiffPolls[x]} polls, first {addrFirst[x]} (NES f{nesPolls[addrFirst[x]].Frame})");
        if (firstDiff >= 0)
        {
            sb.AppendLine($"first difference, poll {firstDiff}: addr NES SNES");
            var a = nesPolls[firstDiff].Ram; var b = snesPolls[firstDiff].Ram;
            foreach (var x in Enumerable.Range(0, 2048).Where(x => !ignored[x] && a[x] != b[x]).Take(64))
                sb.AppendLine($"  ${x:X4}: {a[x]:X2} {b[x]:X2}");
        }
        if (watches.Count > 0)
        {
            sb.AppendLine("watches every 300 NES frames (NES/SNES): " + string.Join(" ", watches.Select(w => w.Name)));
            lastBucket = -1;
            for (int p = 0; p < n; p++)
            {
                int bucket = nesPolls[p].Frame / 300;
                if (bucket == lastBucket && p != n - 1) continue;
                lastBucket = bucket;
                sb.AppendLine($"  poll {p,6} f{nesPolls[p].Frame,6}: " + string.Join(" ", watches.Select(w => $"{w.Name}={nesPolls[p].Ram[w.Addr]:X2}/{snesPolls[p].Ram[w.Addr]:X2}")));
            }
        }
        string stem = Path.GetFileNameWithoutExtension(snesPath);
        File.WriteAllText(Path.Combine(outDir, stem + ".nested-cert.txt"), sb.ToString());
        // Per-poll CSV: poll, nes frame, snes frame, buttons, differing bytes.
        using (var csv = new StreamWriter(Path.Combine(outDir, stem + ".nested-cert.polls.csv")))
        {
            csv.WriteLine("poll,nesFrame,snesFrame,buttons,diffBytes");
            for (int p = 0; p < n; p++)
            {
                int cnt = 0; var a = nesPolls[p].Ram; var b = snesPolls[p].Ram;
                for (int x = 0; x < 2048; x++) if (!ignored[x] && a[x] != b[x]) cnt++;
                csv.WriteLine($"{p},{nesPolls[p].Frame},{snesPolls[p].Frame},{nesPolls[p].Buttons:X2},{cnt}");
            }
        }
        if (raw)
        {
            using (var fs = File.Create(Path.Combine(outDir, stem + ".nes.ram"))) for (int p = 0; p < n; p++) fs.Write(nesPolls[p].Ram);
            using (var fs = File.Create(Path.Combine(outDir, stem + ".snes.ram"))) for (int p = 0; p < n; p++) fs.Write(snesPolls[p].Ram);
        }
        if (pngPolls.Count > 0) SavePictures(nesPath, snesPath, cpu, ppu, apu, powerOnRam, script, nesPolls, snesPolls, pngPolls, outDir, stem, file, sramPath);
        if (json)
        {
            var o = new
            {
                nes = Path.GetFileName(nesPath), snes = Path.GetFileName(snesPath), nesPolls = nesPolls.Count, snesPolls = snesPolls.Count,
                compared = n, diffPolls, firstDiff, nesLag = nesLag.Lag, snesLag = snesLag.Lag,
                bootOffset = snesFirstPollFrame - nesFirstPollFrame, endDrift = snesLastCompared - nesLastCompared,
            };
            File.WriteAllText(Path.Combine(outDir, stem + ".nested-cert.json"), JsonSerializer.Serialize(o, new JsonSerializerOptions { WriteIndented = true }));
        }
        Console.Write(sb.ToString());
        return 0;
    }

    private static void SavePictures(string nesPath, string snesPath, string cpu, string ppu, string apu, string powerOnRam,
        List<RomTestCli.InputStep> script, List<Poll> nesPolls, List<Poll> snesPolls, HashSet<int> polls, string outDir, string stem, byte[] snesFile, string? sramPath)
    {
        var nesFrames = polls.Where(p => p < nesPolls.Count).ToDictionary(p => nesPolls[p].Frame, p => p);
        var snesFrames = polls.Where(p => p < snesPolls.Count).ToDictionary(p => snesPolls[p].Frame, p => p);
        var nes = new NES { RomName = Path.GetFileName(nesPath) };
        nes.LoadROM(File.ReadAllBytes(nesPath));
        RomTestCli.ApplyCore("CPU", cpu, nes.SetCpuCore, nes.GetCpuCoreId);
        RomTestCli.ApplyCore("PPU", ppu, nes.SetPpuCore, nes.GetPpuCoreId);
        RomTestCli.ApplyCore("APU", apu, nes.SetApuCore, nes.GetApuCoreId);
        if (powerOnRam != "fceux") for (int ri = 0; ri < 2048; ri++) nes.PokeSystemRam(ri, powerOnRam == "ones" ? (byte)0xFF : (byte)0x00);
        var held = new bool[8]; int next = 0; int maxF = nesFrames.Count > 0 ? nesFrames.Keys.Max() : 0;
        for (int f = 0; f < maxF; f++)
        {
            while (next < script.Count && script[next].Frame <= f) { held = (bool[])script[next].Held.Clone(); next++; }
            nes.SetInputs(held, null); nes.RunFrame();
            if (nesFrames.TryGetValue(f + 1, out int p)) MixLab.MixLabCli.SaveRgba(nes.GetFrameBuffer(), 256, 240, Path.Combine(outDir, $"{stem}_poll{p}_nes_f{f + 1}.png"));
        }
        var cart = SnesCartridge.Load(snesFile);
        if (sramPath != null && File.Exists(sramPath) && cart.Sram.Length > 0) { var img = File.ReadAllBytes(sramPath); Array.Copy(img, cart.Sram, Math.Min(img.Length, cart.Sram.Length)); }
        var board = new BOARD_SFC(cart, SnesApuChoice.Create(null), SnesFirmware.CreateCoprocessor(cart, snesPath, out _));
        int count = 0;
        ushort Map(byte b) { ushort v = 0; for (int k = 0; k < 8; k++) if ((b & (1 << k)) != 0) v |= (ushort)(0x8000 >> k); return v; }
        board.Pads[0] = Map(nesPolls.Count > 0 ? nesPolls[0].Buttons : (byte)0);
        board.WriteWatch = (bank, offset, value) =>
        {
            if ((bank & 0x40) != 0 || offset != 0x4016 || (value & 1) == 0) return;
            board.Pads[0] = Map(nesPolls[Math.Min(count, nesPolls.Count - 1)].Buttons); count++;
        };
        int maxS = snesFrames.Count > 0 ? snesFrames.Keys.Max() : 0;
        for (int f = 1; f <= maxS; f++)
        {
            board.RunFrame();
            if (snesFrames.TryGetValue(f, out int p)) SnesTestCli.SavePng(board.Ppu, Path.Combine(outDir, $"{stem}_poll{p}_snes_f{f}.png"));
        }
    }
}
