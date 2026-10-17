// Round 6 (imodel): snapshots of REAL games, taken the way the plugin takes them when Instrument Runaway starts (SessionGame.CaptureSpc / CaptureGb / CaptureSega), from games in the user's library. The repository carries
// no ROM: a check that needs one reports "not tested" when the file is not there, never a pass.
using System.IO.Compression;
using BrokenNes2;
using BrokenNes2.Instruments;
using NesEmulator.Gb;
using NesEmulator.Sega;
using NesEmulator.Systems;

namespace BrokenNes.FruityHost;

internal static class ImodelGames
{
    public const string Smw = @"X:\EMULATION\Super Nintendo\Super Nintendo (U)\Super Mario World (U) [!].smc";
    public const string Sonic1 = @"X:\EMULATION\Sega Genesis\Sonic the Hedgehog (JUE) [!].bin";

    /// <summary>The game's bytes: a plain ROM file, or the first ROM inside a zip. Null when the file is not there.</summary>
    public static byte[]? LoadRom(string path)
    {
        if (!File.Exists(path)) return null;
        if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return File.ReadAllBytes(path);
        using var z = ZipFile.OpenRead(path);
        var e = z.Entries.FirstOrDefault(x => x.Name.EndsWith(".gb", StringComparison.OrdinalIgnoreCase) || x.Name.EndsWith(".gbc", StringComparison.OrdinalIgnoreCase) || x.Name.EndsWith(".sms", StringComparison.OrdinalIgnoreCase)
            || x.Name.EndsWith(".gg", StringComparison.OrdinalIgnoreCase) || x.Name.EndsWith(".sfc", StringComparison.OrdinalIgnoreCase) || x.Name.EndsWith(".smc", StringComparison.OrdinalIgnoreCase) || x.Name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
            || x.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || x.Name.EndsWith(".gen", StringComparison.OrdinalIgnoreCase));
        if (e == null) return null;
        using var s = e.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>The first existing file of a list (a game may be named differently in the library).</summary>
    public static string? First(params string[] paths) => paths.FirstOrDefault(File.Exists);

    /// <summary>Finds a game in a library folder by a name fragment (the library's names carry region tags).</summary>
    public static string? Find(string folder, string fragment, params string[] extensions)
    {
        if (!Directory.Exists(folder)) return null;
        foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            string n = Path.GetFileName(f);
            if (n.Contains(fragment, StringComparison.OrdinalIgnoreCase) && (extensions.Length == 0 || extensions.Any(e => n.EndsWith(e, StringComparison.OrdinalIgnoreCase)))) return f;
        }
        return null;
    }

    private static IConsoleSession Run(byte[] rom, ConsoleKind kind, string apu, string fileName, int frames, Action<IConsoleSession>? before = null, Func<int, int>? pad = null)
    {
        var s = ConsoleSessions.Create(kind, (byte[])rom.Clone(), CoreCatalog.Default(kind, CoreSlot.Cpu), CoreCatalog.Default(kind, CoreSlot.Ppu), apu, null, fileName);
        before?.Invoke(s);
        var buf = new short[1 << 16];
        for (int i = 0; i < frames; i++)
        {
            if (pad != null) s.SetPad(0, (PadButtons)pad(i));
            s.RunFrame();
            while (s.ReadSamples(buf) > 0) { }
        }
        return s;
    }

    public static SpcSnapshot? Snes(string path, int frames, out string info)
    {
        info = "";
        var rom = LoadRom(path);
        if (rom == null) { info = "ROM not found"; return null; }
        var s = Run(rom, ConsoleKind.Snes, CoreCatalog.Default(ConsoleKind.Snes, CoreSlot.Apu), Path.GetFileName(path), frames);
        if (s is not SnesSession snes || snes.Apu is not NesEmulator.Snes.APU_SFC spc) { info = "not a SNES session"; return null; }
        return new SpcSnapshot(spc.Aram, spc.Dsp.Regs);
    }

    public static GbSnapshot? Gb(string path, int frames, out string info)
    {
        info = "";
        var rom = LoadRom(path);
        if (rom == null) { info = "ROM not found"; return null; }
        bool color = path.EndsWith(".gbc", StringComparison.OrdinalIgnoreCase);
        var s = Run(rom, color ? ConsoleKind.GameBoyColor : ConsoleKind.GameBoy, "GB", Path.GetFileName(path), frames);
        if (s is not GbSession gb || gb.Apu is not APU_GB apu) { info = "not a Game Boy session"; return null; }
        return GbSnapshot.FromApu(apu);
    }

    /// <summary>A NES game's APU registers (the register latch) and DMC sample after <paramref name="frames"/> frames, on the FIX cores.</summary>
    public static NesSnapshot? Nes(string path, int frames, out string info)
    {
        info = "";
        var rom = LoadRom(path);
        if (rom == null) { info = "ROM not found"; return null; }
        var m = new NesEmulator.Plugin.NesAudioMachine(rom, null, "FIX", "FIX", "FIX") { HostSampleRate = 48000 };
        var buf = new float[4096];
        for (int i = 0; i < frames; i++) { m.RunFrame(); while (m.ReadAvailable(buf) > 0) { } }
        var latch = new byte[NesSnapshot.RegCount];
        uint written = m.CopyApuLatch(latch);
        return NesSnapshot.FromLatch(latch, written, a => m.Nes.PeekCpu(a));
    }

    public static SegaSnapshot? Genesis(string path, int frames, out string info)
    {
        info = "";
        var rom = LoadRom(path);
        if (rom == null) { info = "ROM not found"; return null; }
        var rec = new DacRecorder();
        MdSoundUnit? hooked = null;
        var s = Run(rom, ConsoleKind.Genesis, "MD", Path.GetFileName(path), frames, before: sess =>
        {
            var md = (sess is NesEmulator.Mix.SegaPictureSession w ? w.Inner : sess) as MdSession;
            if (md?.Board.Sound is MdSoundUnit u) { u.YmPortTap = rec.OnWrite; hooked = u; }
        });
        var inner = s is NesEmulator.Mix.SegaPictureSession w2 ? w2.Inner : s;
        if (inner is not MdSession mds || mds.Board.Sound is not MdSoundUnit unit) { info = "not a Genesis session"; return null; }
        return SegaSnapshot.FromOpn(unit, mds.Board.Timeline.MasterHz, rec);
    }

    public static SegaSnapshot? MasterSystem(string path, int frames, bool fm, out string info)
    {
        info = "";
        var rom = LoadRom(path);
        if (rom == null) { info = "ROM not found"; return null; }
        bool gg = path.EndsWith(".gg", StringComparison.OrdinalIgnoreCase);
        var kind = gg ? ConsoleKind.GameGear : ConsoleKind.MasterSystem;
        var s = Run(rom, kind, gg ? CoreCatalog.Default(kind, CoreSlot.Apu) : fm ? "SMS-FM" : "SMS", Path.GetFileName(path), frames);
        var inner = s is NesEmulator.Mix.SegaPictureSession w ? w.Inner : s;
        if (inner is not SmsSession sms) { info = "not a Master System session"; return null; }
        var t = sms.Board.Timeline;
        double clock = t.MasterHz / t.Z80.Divider;
        return sms.Board.Sound switch
        {
            SmsFmSound f => SegaSnapshot.FromOpll(f, clock),
            SmsSound psg => SegaSnapshot.FromPsg(psg.Psg, clock),
            _ => null,
        };
    }
}
