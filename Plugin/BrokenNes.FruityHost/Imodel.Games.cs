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

    private static readonly string[] RomExtensions = [".gb", ".gbc", ".sms", ".gg", ".sfc", ".smc", ".bin", ".md", ".gen", ".nes"];
    private const string SevenZip = @"C:\Program Files\7-Zip\7z.exe";

    /// <summary>The game's bytes: a plain ROM file, or the first ROM inside a zip or 7z archive (7-Zip is read through its command line, extracted to a temporary folder that is removed again); <paramref name="name"/> is the ROM's own file name
    /// (the Master System family takes its region from it). Null when the file is not there.</summary>
    public static byte[]? LoadRom(string path, out string name)
    {
        name = Path.GetFileName(path);
        if (!File.Exists(path)) return null;
        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var z = ZipFile.OpenRead(path);
            var e = z.Entries.FirstOrDefault(x => RomExtensions.Any(ext => x.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)));
            if (e == null) return null;
            name = e.Name;
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        if (path.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(SevenZip)) return null;
            string dir = Path.Combine(Path.GetTempPath(), "bn2_imodel_" + Guid.NewGuid().ToString("N"));
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(SevenZip, $"x -y -o\"{dir}\" \"{path}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                using (var proc = System.Diagnostics.Process.Start(psi)!) { proc.StandardOutput.ReadToEnd(); proc.WaitForExit(); if (proc.ExitCode != 0) return null; }
                var file = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).FirstOrDefault(f => RomExtensions.Any(ext => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase)));
                if (file == null) return null;
                name = Path.GetFileName(file);
                return File.ReadAllBytes(file);
            }
            finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
        }
        return File.ReadAllBytes(path);
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
        var rom = LoadRom(path, out string romName);
        if (rom == null) { info = "ROM not found"; return null; }
        var s = Run(rom, ConsoleKind.Snes, CoreCatalog.Default(ConsoleKind.Snes, CoreSlot.Apu), romName, frames);
        if (s is not SnesSession snes || snes.Apu is not NesEmulator.Snes.APU_SFC spc) { info = "not a SNES session"; return null; }
        return new SpcSnapshot(spc.Aram, spc.Dsp.Regs);
    }

    public static GbSnapshot? Gb(string path, int frames, out string info)
    {
        info = "";
        var rom = LoadRom(path, out string romName);
        if (rom == null) { info = "ROM not found"; return null; }
        bool color = path.EndsWith(".gbc", StringComparison.OrdinalIgnoreCase);
        var s = Run(rom, color ? ConsoleKind.GameBoyColor : ConsoleKind.GameBoy, "GB", romName, frames);
        if (s is not GbSession gb || gb.Apu is not APU_GB apu) { info = "not a Game Boy session"; return null; }
        return GbSnapshot.FromApu(apu);
    }

    /// <summary>A NES game's APU registers (the register latch) and DMC sample after <paramref name="frames"/> frames, on the FIX cores.</summary>
    public static NesSnapshot? Nes(string path, int frames, out string info)
    {
        info = "";
        var rom = LoadRom(path, out string romName);
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
        var rom = LoadRom(path, out string romName);
        if (rom == null) { info = "ROM not found"; return null; }
        var rec = new DacRecorder();
        MdSoundUnit? hooked = null;
        var s = Run(rom, ConsoleKind.Genesis, "MD", romName, frames, before: sess =>
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
        var rom = LoadRom(path, out string romName);
        if (rom == null) { info = "ROM not found"; return null; }
        bool gg = path.EndsWith(".gg", StringComparison.OrdinalIgnoreCase);
        var kind = gg ? ConsoleKind.GameGear : ConsoleKind.MasterSystem;
        var s = Run(rom, kind, gg ? CoreCatalog.Default(kind, CoreSlot.Apu) : fm ? "SMS-FM" : "SMS", romName, frames);
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
