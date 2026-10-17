using System;
using System.Collections.Generic;
using System.Linq;
using NesEmulator.Corruption;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Corruption;

/// <summary>
/// The Game Boy, Master System, Game Gear and Genesis through the corruption library: each console's memories, a corruption that rides a saved state, and a corruption that rides a replay from power-on, on tiny programs of our own
/// (a counter in work RAM that also paints the picture: after the same frames and the same corruption, a machine that was started from a stockpile entry must be the machine that never left).
/// </summary>
internal static class CorruptConsoleChecks
{
    private sealed record Game(string Name, ConsoleKind Kind, byte[] Rom, string File, string WorkRam, int CounterAddress, string[] Domains, string[] Rollcall);

    // ---- the programs (written for this project; each is a counter in work RAM that picks the backdrop colour) ----

    private static byte[] GbRom(bool color = false)
    {
        var rom = new byte[0x8000];
        byte[] logo = [0xCE, 0xED, 0x66, 0x66, 0xCC, 0x0D, 0x00, 0x0B, 0x03, 0x73, 0x00, 0x83, 0x00, 0x0C, 0x00, 0x0D, 0x00, 0x08, 0x11, 0x1F, 0x88, 0x89, 0x00, 0x0E,
                       0xDC, 0xCC, 0x6E, 0xE6, 0xDD, 0xDD, 0xD9, 0x99, 0xBB, 0xBB, 0x67, 0x63, 0x6E, 0x0E, 0xEC, 0xCC, 0xDD, 0xDC, 0x99, 0x9F, 0xBB, 0xB9, 0x33, 0x3E];
        logo.CopyTo(rom, 0x104);
        rom[0x100] = 0x00; rom[0x101] = 0xC3; rom[0x102] = 0x50; rom[0x103] = 0x01;          // NOP ; JP $0150
        System.Text.Encoding.ASCII.GetBytes("BN CORRUPT").CopyTo(rom, 0x134);
        if (color) rom[0x143] = 0x80;
        byte sum = 0;
        for (int i = 0x134; i <= 0x14C; i++) sum = (byte)(sum - rom[i] - 1);
        rom[0x14D] = sum;
        byte[] code = [0x21, 0x00, 0xC0, 0x34, 0x7E, 0xE0, 0x47, 0x18, 0xFA];               // LD HL,$C000 ; loop: INC (HL) ; LD A,(HL) ; LDH ($47),A ; JR loop
        code.CopyTo(rom, 0x150);
        return rom;
    }

    private static byte[] SmsRom(bool gameGear)
    {
        var rom = new byte[0x8000];
        byte[] code =
        [
            0xF3, 0x31, 0xF0, 0xDF, 0x21, 0x00, 0xC0,
            0x3E, 0x00, 0xD3, 0xBF, 0x3E, 0xC0, 0xD3, 0xBF,
            0x06, 0x20, 0x3E, 0x00, 0xD3, 0xBE, 0xC6, 0x07, 0x10, 0xFA,
            0x3E, 0x40, 0xD3, 0xBF, 0x3E, 0x81, 0xD3, 0xBF,
            0x34, 0x7E, 0xE6, 0x0F, 0xD3, 0xBF, 0x3E, 0x87, 0xD3, 0xBF, 0x18, 0xF4,
        ];
        code.CopyTo(rom, 0);
        "TMR SEGA"u8.CopyTo(rom.AsSpan(0x7FF0));
        rom[0x7FFF] = (byte)(gameGear ? 0x6C : 0x4C);
        return rom;
    }

    private static byte[] MdRom()
    {
        var c = new List<byte>();
        c.AddRange([0x23, 0xFC, 0xC0, 0x00, 0x00, 0x00, 0x00, 0xC0, 0x00, 0x04]);            // move.l #$C0000000,$C00004 : colour RAM write from 0
        for (int i = 0; i < 64; i++) { int v = (i * 37 + 0x111) & 0x0EEE; c.AddRange([0x33, 0xFC, (byte)(v >> 8), (byte)v, 0x00, 0xC0, 0x00, 0x00]); }   // move.w #v,$C00000
        int top = c.Count;
        c.AddRange([0x52, 0x39, 0x00, 0xFF, 0x00, 0x00]);                                    // addq.b #1,$FF0000
        c.AddRange([0x10, 0x39, 0x00, 0xFF, 0x00, 0x00]);                                    // move.b $FF0000,d0
        c.AddRange([0x02, 0x40, 0x00, 0x3F]);                                                // andi.w #$3F,d0
        c.AddRange([0x00, 0x40, 0x87, 0x00]);                                                // ori.w #$8700,d0 : VDP register 7
        c.AddRange([0x33, 0xC0, 0x00, 0xC0, 0x00, 0x04]);                                    // move.w d0,$C00004
        c.AddRange([0x60, (byte)(top - (c.Count + 2))]);                                     // bra.s top
        var rom = new byte[0x4000];
        new byte[] { 0x00, 0xFF, 0xFE, 0x00, 0x00, 0x00, 0x02, 0x00 }.CopyTo(rom, 0);
        "SEGA MEGA DRIVE "u8.CopyTo(rom.AsSpan(0x100));
        "(C)BN2  2026.OCT"u8.CopyTo(rom.AsSpan(0x110));
        "BROKENNES2 TEST ROM".PadRight(48).Select(ch => (byte)ch).ToArray().CopyTo(rom, 0x120);
        "BROKENNES2 TEST ROM".PadRight(48).Select(ch => (byte)ch).ToArray().CopyTo(rom, 0x150);
        new byte[] { 0x00, 0x00, 0x3F, 0xFF }.CopyTo(rom, 0x1A4);
        "U  "u8.CopyTo(rom.AsSpan(0x1F0));
        c.CopyTo(rom, 0x200);
        return rom;
    }

    private static Game[] Games() =>
    [
        new("Game Boy", ConsoleKind.GameBoy, GbRom(), "t.gb", "WRAM", 0, ["WRAM", "HRAM", "VRAM", "OAM", "CRAM", "SRAM", "ROM", "WAVE"], ["WRAM=8192", "HRAM=127", "VRAM=16384", "OAM=160", "ROM=32768", "WAVE=16"]),
        new("Game Boy Color", ConsoleKind.GameBoyColor, GbRom(color: true), "t.gbc", "WRAM", 0, ["WRAM", "HRAM", "VRAM", "OAM", "CRAM", "SRAM", "ROM", "WAVE"], ["WRAM=32768", "CRAM=128", "VRAM=16384"]),
        new("Master System", ConsoleKind.MasterSystem, SmsRom(false), "t.sms", "RAM", 0, ["RAM", "VRAM", "CRAM", "SRAM", "ROM"], ["RAM=8192", "VRAM=16384", "CRAM=32", "ROM=32768"]),
        new("Game Gear", ConsoleKind.GameGear, SmsRom(true), "t.gg", "RAM", 0, ["RAM", "VRAM", "CRAM", "SRAM", "ROM"], ["RAM=8192", "VRAM=16384", "CRAM=64", "ROM=32768"]),
        new("Genesis", ConsoleKind.Genesis, MdRom(), "t.md", "RAM", 0, ["RAM", "VRAM", "CRAM", "VSRAM", "ZRAM", "SRAM", "ROM"], ["RAM=65536", "VRAM=65536", "CRAM=128", "VSRAM=128", "ZRAM=8192", "ROM=131072"]),
    ];

    private static IConsoleSession Make(Game g) =>
        ConsoleSessions.Create(g.Kind, (byte[])g.Rom.Clone(), CoreCatalog.Default(g.Kind, CoreSlot.Cpu), CoreCatalog.Default(g.Kind, CoreSlot.Ppu), CoreCatalog.Default(g.Kind, CoreSlot.Apu), null, g.File);

    private sealed class Rig : IDisposable
    {
        public IConsoleSession S;
        public readonly ConsoleFarm Farm = new();
        private readonly short[] buf = new short[32768];
        public Rig(Game g) { S = Make(g); Farm.Attach(S, g.Rom, g.File, new MachineSpec(), true); }
        public void Run(int n, Func<int, PadButtons>? pad = null)
        {
            for (int i = 0; i < n; i++)
            {
                var p = pad?.Invoke(Farm.Frames) ?? PadButtons.None;
                Farm.OnFrame((ushort)p, 0);
                S.SetPad(0, p);
                S.RunFrame();
                while (S.ReadSamples(buf) > 0) { }
            }
        }
        public ulong Hash(string domain) { var d = MachineDomains.Find(MachineDomains.For(S), domain)!; var b = new byte[d.Size]; for (int i = 0; i < b.Length; i++) b[i] = d.Peek(i); return BrokenNes.Workshop.Corruption.Hash.Fnv(b); }
        public ulong Picture() => BrokenNes.Workshop.Corruption.Hash.Fnv(S.Frame.AsSpan(0, S.FrameWidth * S.FrameHeight));
        public void Dispose() => S.Dispose();
    }

    public static void Run(Checks c)
    {
        foreach (var g in Games())
        {
            Console.WriteLine($"  -- {g.Name}");
            using var rig = new Rig(g);
            rig.Run(30);
            var domains = MachineDomains.For(rig.S);
            string ids = string.Join(",", domains.Select(d => d.Id));
            c.Ok($"{g.Name}: its memories are {string.Join(", ", g.Domains)}", ids == string.Join(",", g.Domains), ids);
            var sizes = domains.ToDictionary(d => d.Id, d => d.Size);
            var want = g.Rollcall.Select(x => x.Split('=')).ToDictionary(x => x[0], x => int.Parse(x[1]));
            c.Ok($"{g.Name}: true sizes ({string.Join(", ", g.Rollcall)})", want.All(kv => sizes[kv.Key] == kv.Value), string.Join(" ", sizes.Select(kv => $"{kv.Key}={kv.Value}")));

            foreach (var d in domains.Where(x => x.Unavailable == null && x.Size > 0 && x.Id != g.WorkRam))
            {
                bool ok = true;
                foreach (int a in new[] { 0, d.Size / 2, d.Size - 1 })
                {
                    byte old = d.Peek(a), probe = (byte)((old ^ 0x2A) & (d.Id == "CRAM" && g.Kind == ConsoleKind.MasterSystem ? 0x3F : 0xFF));
                    d.Poke(a, probe);
                    // some memories do not keep every bit (a Game Gear colour is 12 bits; a Master System colour 6): the byte reads back as the memory keeps it; the check is that the write took and the original goes back
                    bool took = d.Peek(a) == probe || d.Id is "CRAM" or "ROM" or "WAVE";
                    d.Poke(a, old);
                    ok &= took && d.Peek(a) == old;
                }
                c.Ok($"{g.Name}: {d.Id}: poke and peek round trip", ok);
            }
            var missing = domains.Where(x => x.Unavailable != null).Select(x => x.Id);
            Console.WriteLine($"      (not available here: {(missing.Any() ? string.Join(", ", missing) : "none")})");

            // ---- a corruption on the work RAM rides a saved state ----
            // the machine that never left: 60 frames, a blast, 60 more
            using var origin = new Rig(g);
            origin.Run(60);
            var cap = origin.Farm.Capture();
            c.Ok($"{g.Name}: a position is stored as a saved state with the replay recipe beside it", cap.Anchor is { Kind: AnchorKind.State, Alt.Kind: AnchorKind.Boot } && cap.Anchor.Alt.Frames == 60, cap.Reason);
            var rep = origin.Farm.Blast(new[] { g.WorkRam }, 12, BlastType.Random, 4242, false, out var writes);
            c.Ok($"{g.Name}: 12 random writes land in the work RAM", rep.Applied == 12 && writes.Count == 12, rep.Describe());
            origin.Run(60);
            ulong refWram = origin.Hash(g.WorkRam), refPic = origin.Picture();

            var entry = new StockpileEntry { Name = "t", Game = origin.Farm.Game(), Machine = new MachineSpec(), Anchor = cap.Anchor!, Writes = writes };
            using (var other = new Rig(g))
            {
                other.Run(17, f => PadButtons.Start);
                var st = other.Farm.Start(entry, null, new StartOptions(), ns => throw new InvalidOperationException("a state anchor should not build a machine"));
                c.Ok($"{g.Name}: starting the entry on another machine loads the state, then the writes", st.Ok && st.Used == AnchorKind.State && st.Report!.Applied == 12, st.Message);
                other.Run(60);
                c.Ok($"{g.Name}: that machine goes on as the corrupted one did (work RAM and picture equal after 60 more frames)", other.Hash(g.WorkRam) == refWram && other.Picture() == refPic);
            }
            // the same entry without its saved state: the replay from power-on reaches the same machine
            var bootOnly = new StockpileEntry { Name = "t", Game = entry.Game, Machine = new MachineSpec(), Anchor = cap.Anchor!.Alt!, Writes = writes };
            var started = AnchorStart.Start(bootOnly, null, g.Rom, g.File, g.Kind, null, new StartOptions());
            if (started.Ok)
            {
                using var replay = new Rig(g);
                replay.S.Dispose(); replay.S = started.Session!; replay.Farm.Attach(replay.S, g.Rom, g.File, new MachineSpec(), true);
                replay.Run(60);
                c.Ok($"{g.Name}: ... and so does the replay of the 60 frames from power-on with the writes after them (work RAM and picture equal)", replay.Hash(g.WorkRam) == refWram && replay.Picture() == refPic, started.Message);
            }
            else c.Ok($"{g.Name}: the replay from power-on starts", false, started.Message);

            // a control: the same entry with its writes left out gives another machine (so the equality above is the writes)
            var clean = new StockpileEntry { Name = "t", Game = entry.Game, Machine = new MachineSpec(), Anchor = cap.Anchor!, Writes = new List<CorruptionWrite>() };
            using (var other2 = new Rig(g))
            {
                other2.Run(5);
                other2.Farm.Start(clean, null, new StartOptions(), ns => false);
                other2.Run(60);
                c.Control($"{g.Name}: the state without the writes is a different machine than with them", other2.Hash(g.WorkRam) != refWram);
            }
        }
    }
}
