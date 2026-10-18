// Round 8 (record-nes): what the NES recording checks stand on. Real NES games run headless on the plugin's own machine (NesAudioMachine, FIX cores, the chip the plugin uses), with a pad script,
// and the games of the survey that exercise each feature of the chip a recording has to carry (found by `FruityHost nesrec-survey`, see round8-record.md): hardware envelopes, length counters,
// sweeps, duty changes, the triangle's linear counter, DMC samples.
using BrokenNes2.Record;
using NesEmulator;
using NesEmulator.Plugin;

namespace BrokenNes.FruityHost;

/// <summary>A NES game on the plugin's machine, a frame at a time, with its pad.</summary>
internal sealed class NesRecGame
{
    public readonly NesAudioMachine M;
    public readonly string Name, Path;
    public readonly byte[] Rom;
    public int Frame;
    public Func<int, int> Pad = _ => 0;
    private readonly bool[] pad = new bool[8];
    private readonly float[] buf = new float[4096];

    public const string Roster = @"X:\EMULATION\Nintendo Entertainment System";

    public NesRecGame(byte[] rom, string name, string path = "")
    {
        Rom = rom; Name = name; Path = path;
        M = new NesAudioMachine(rom);
    }

    /// <summary>A game of the roster by the start of its file name (null when the roster or the game is not on this machine).</summary>
    public static NesRecGame? Find(string fragment, string? script = null)
    {
        string? f = ImodelGames.Find(Roster, fragment, ".nes");
        if (f == null) return null;
        var g = new NesRecGame(File.ReadAllBytes(f), System.IO.Path.GetFileNameWithoutExtension(f), f);
        if (script != null) g.Pad = Script(script);
        return g;
    }

    /// <summary>The user's VRUN (the plugin's built-in game, Plugin/BrokenNes2.Plugin/Resources/default.nes in this source tree).</summary>
    public static NesRecGame Vrun() => new(File.ReadAllBytes(VrunPath()), "VRUN (built-in)", VrunPath());

    public static string VrunPath([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(here)!, "..", "BrokenNes2.Plugin", "Resources", "default.nes"));

    /// <summary>The user's VRUN builds (outside the repository), newest first.</summary>
    public static string[] VrunBuilds()
    {
        string dir = @"C:\Users\philt\OneDrive\Documents\PROJECTS\VRUN_Nes_Dev\builds";
        if (!Directory.Exists(dir)) return [];
        return Directory.GetFiles(dir, "*.nes", SearchOption.AllDirectories).OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>"300:start,4" style scripts: at frame 300 hold Start for 4 frames; buttons a b select start up down left right.</summary>
    public static Func<int, int> Script(string script)
    {
        var items = new List<(int At, int Bits, int Len)>();
        foreach (var part in script.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split(':');
            int at = int.Parse(kv[0]);
            var bl = kv[1].Split(',');
            int bits = 0;
            foreach (var b in bl[0].Split('+')) bits |= b switch { "a" => 1, "b" => 2, "select" => 4, "start" => 8, "up" => 16, "down" => 32, "left" => 64, "right" => 128, _ => 0 };
            items.Add((at, bits, bl.Length > 1 ? int.Parse(bl[1]) : 4));
        }
        return f => { int b = 0; foreach (var (at, bits, len) in items) if (f >= at && f < at + len) b |= bits; return b; };
    }

    public void RunFrame()
    {
        int b = Pad(Frame);
        for (int i = 0; i < 8; i++) pad[i] = (b >> i & 1) != 0;
        M.Nes.SetInput(pad);
        M.RunFrame();
        while (M.ReadAvailable(buf) > 0) { }
        Frame++;
    }

    public void Run(int frames) { for (int i = 0; i < frames && !M.Crashed; i++) RunFrame(); }

    public APU_FIX Apu => (APU_FIX)M.Nes.ActiveApu!;
}

/// <summary>The roster games the checks use, each for what the survey found it does (round8-record.md, NES: "the evidence").</summary>
internal static class NesRecGames
{
    public sealed record Pick(string Fragment, string What, string Script, int Park);

    public static readonly Pick[] Model =
    [
        new("Super Mario Bros. (JU) (PRG0)", "hardware envelopes, length counters, the triangle's linear counter", "180:start,4", 200),
        new("Adventures of Bayou Billy", "the hardware sweep on every pulse note", "300:start,4", 300),
        new("Abadox", "the sweep on half the pulse notes", "300:start,4", 300),
        new("Adventures of Rad Gravity", "the decay envelope on every pulse note", "300:start,4", 300),
        new("Dash Galaxy", "the decay envelope and length counters", "300:start,4", 300),
        new("Base Wars", "duty changes under sounding notes, DMC samples", "300:start,4", 300),
        new("Contra (U)", "DMC samples, the triangle's linear counter", "300:start,4", 300),
        new("Balloon Fight", "the sweep, envelopes, $4017 every frame", "300:start,4", 300),
        new("Anticipation", "envelopes, length counters, the linear counter", "300:start,4", 300),
        new("Alien 3", "duty changes", "300:start,4", 300),
    ];
}
