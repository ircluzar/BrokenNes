// Round 6 (imodel): the snapshots the checks run on, the report they fill in, and a few helpers. Real-game snapshots are captured once per process (the repository carries no ROM: a missing game is reported, never passed).
using BrokenNes2;
using BrokenNes2.Instruments;

namespace BrokenNes.FruityHost;

internal sealed class ImodelReport
{
    public readonly List<string> Failures = new(), Warnings = new(), Notes = new();
    public int Assertions;
    public void Expect(bool ok, string what) { Assertions++; if (!ok) Failures.Add(what); }
    public void Warn(string what) => Warnings.Add(what);
    public void Note(string what) => Notes.Add(what);
}

internal sealed record ImodelFixture(string Name, object Snapshot, bool Real);

internal static class ImodelFixtures
{
    /// <summary>A NES snapshot with known register values (see the checks that read it back).</summary>
    public static NesSnapshot NesSynthetic()
    {
        var regs = new byte[NesSnapshot.RegCount];
        regs[0x00] = 0xBF; regs[0x01] = 0x88; regs[0x02] = 0xFD; regs[0x03] = 0xF8;       // pulse 1: duty 2, halt, constant volume 15; sweep on, down, shift 0; timer; length 31
        regs[0x04] = 0x5A; regs[0x05] = 0x00; regs[0x06] = 0x80; regs[0x07] = 0x10;       // pulse 2: duty 1, constant volume 10; no sweep; length 2
        regs[0x08] = 0xC1; regs[0x0A] = 0x20; regs[0x0B] = 0x08;                          // triangle: control, linear 65; length 1
        regs[0x0C] = 0x3A; regs[0x0E] = 0x85; regs[0x0F] = 0x18;                          // noise: halt, constant volume 10; short mode, period 5; length 3
        regs[0x10] = 0x4E; regs[0x11] = 0x40; regs[0x12] = 0x00; regs[0x13] = 0x01;       // DMC: loop, rate 14; load 64; address $C000; 17 bytes
        var dmc = new byte[17];
        for (int i = 0; i < dmc.Length; i++) dmc[i] = (byte)(0x55 ^ i * 37);
        return new NesSnapshot(regs, dmc);
    }

    public static List<ImodelFixture> Synthetic() =>
    [
        new("snes-testsample", SpcSnapshot.TestSample(), false),
        new("snes-samples", ImodelData.SnesWithSamples(), false),
        new("gb-dmg", ImodelData.GbWithSettings(false), false),
        new("gb-cgb", ImodelData.GbWithSettings(true), false),
        new("psg", ImodelData.PsgOnly(), false),
        new("opll", ImodelData.OpllWithUser(), false),
        new("opn", ImodelData.OpnWithPatches(), false),
        new("nes", NesSynthetic(), false),
    ];

    private static List<ImodelFixture>? real;
    private static readonly List<string> realMissing = new();
    public static IReadOnlyList<string> RealMissing => realMissing;

    /// <summary>Snapshots of real games from the user's library (captured once).</summary>
    public static List<ImodelFixture> Real()
    {
        if (real != null) return real;
        real = new List<ImodelFixture>();
        void Add(string name, object? snap, string info) { if (snap != null) real.Add(new ImodelFixture(name, snap, true)); else realMissing.Add($"{name} ({info})"); }
        var smw = ImodelGames.Snes(ImodelGames.Smw, 1800, out string i1); Add("smw", smw, i1);
        var sonic = ImodelGames.Genesis(ImodelGames.Sonic1, 900, out string i2); Add("sonic1", sonic, i2);
        string? zelda = ImodelGames.Find(@"X:\EMULATION\GameBoy", "Link's Awakening", ".zip");
        Add("zelda-gb", zelda == null ? null : ImodelGames.Gb(zelda, 1800, out _), zelda ?? "ROM not found");
        string? dkc = ImodelGames.Find(@"X:\EMULATION\Super Nintendo\Super Nintendo (U)", "Donkey Kong Country (U) (V1.2)", ".smc");
        Add("dkc", dkc == null ? null : ImodelGames.Snes(dkc, 2400, out _), dkc ?? "ROM not found");
        string? gbc = ImodelGames.Find(@"X:\EMULATION\GameBoy Color", "Dr. Mario DX", ".gbc");
        Add("drmario-gbc", gbc == null ? null : ImodelGames.Gb(gbc, 1500, out _), gbc ?? "ROM not found");
        string? gg = ImodelGames.Find(@"X:\EMULATION\Sega Game Gear", "Addams Family", ".gg");
        Add("addams-gg", gg == null ? null : ImodelGames.MasterSystem(gg, 1500, false, out _), gg ?? "ROM not found");
        string? sms = ImodelGames.First(@"X:\EMULATION\Sega Master System\Alex Kidd in Miracle World.7z");
        Add("alexkidd-sms", sms == null ? null : ImodelGames.MasterSystem(sms, 1500, false, out _), sms ?? "ROM not found");
        string? smsfm = ImodelGames.Find(@"X:\EMULATION\Sega Master System", "Argos no Juujiken", ".7z");
        Add("argos-smsfm", smsfm == null ? null : ImodelGames.MasterSystem(smsfm, 1500, true, out _), smsfm ?? "ROM not found");
        string? nes = ImodelGames.Find(@"X:\EMULATION\Nintendo Entertainment System", "Contra (U)", ".nes");
        Add("contra-nes", nes == null ? null : ImodelGames.Nes(nes, 900, out _), nes ?? "ROM not found");
        return real;
    }

    /// <summary>The bytes of a snapshot, overlay included: what "identical" means for a round trip.</summary>
    public static byte[] Bytes(object snap) => snap switch
    {
        SpcSnapshot s => s.ToBytes().Concat(s.Overlay?.ToBytes() ?? []).ToArray(),
        SegaSnapshot s => s.ToBytes(),
        GbSnapshot s => s.ToBytes(),
        NesSnapshot s => s.ToBytes(),
        _ => throw new ArgumentException("not a snapshot"),
    };
}
