using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using NesEmulator.Debugging.Port;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Debug;

/// <summary>
/// <c>--port --rom &lt;file&gt; [--sym &lt;file&gt;[,&lt;file&gt;]] [--frames N] [--input "f:Btn+Btn,f:,..."] [--console nes|snes|wms|gb|gbc] [--quiet] [--json &lt;file&gt;]</c>:
/// a ROM run headless with the debug port on (BrokenDev milestone D7): every print, assert, marker and break as it happens, then the timers.
/// Exit 0; 1 when the game asserted; 2 usage.
/// </summary>
internal static class PortRunCli
{
    public static int Run(string[] args)
    {
        RomTestCli.EnsureConsole();
        string Opt(string n, string d) { int i = Array.FindIndex(args, a => a.Equals("--" + n, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : d; }
        string romPath = Opt("rom", "");
        if (romPath.Length == 0 || !File.Exists(romPath)) { Console.Error.WriteLine("usage: --port --rom <file> [--sym <file>] [--frames N] [--input \"f:Btn,...\"] [--console ...] [--quiet] [--json <file>]"); return 2; }
        int frames = int.Parse(Opt("frames", "600"), CultureInfo.InvariantCulture);
        ConsoleKind? console = Opt("console", "") switch
        {
            "nes" => ConsoleKind.Nes, "snes" => ConsoleKind.Snes, "wms" or "widemess" => ConsoleKind.WideMess, "gb" => ConsoleKind.GameBoy, "gbc" => ConsoleKind.GameBoyColor, _ => null,
        };
        if (console == ConsoleKind.WideMess || romPath.EndsWith(".wms", StringComparison.OrdinalIgnoreCase)) Consoles.WideMessEnabled = true;
        byte[] rom = File.ReadAllBytes(romPath);
        rom = RomDetect.Unwrap(rom, Path.GetFileName(romPath), out string inner);
        using var game = CheckedGame.Open(rom, inner, console);
        foreach (var s in Opt("sym", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            Console.WriteLine($"symbols: {game.Dbg.Symbols.Load(s, game.Family)} from {s}");
        using var port = new DebugPort(game.Host.Machine, game.Dbg);
        bool quiet = args.Any(a => a.Equals("--quiet", StringComparison.OrdinalIgnoreCase));
        if (!quiet) port.Log = Console.Out;
        port.Attach();
        Console.WriteLine($"debug port: {port.Where}");
        game.Run(frames, CheckedGame.ParseInput(Opt("input", "")));
        Console.WriteLine($"{game.Description}: {frames} frames; {port.EventCount} events ({port.Count(PortEventKind.Print)} prints, {port.Count(PortEventKind.Assert)} asserts, {port.Count(PortEventKind.Marker)} markers, {port.Count(PortEventKind.Break)} breaks, {port.Count(PortEventKind.Error)} errors)");
        foreach (var line in port.TimerText()) Console.WriteLine(line);
        foreach (var a in port.Asserts) Console.WriteLine("ASSERT " + a);
        string json = Opt("json", "");
        if (json.Length > 0) File.WriteAllText(json, port.Json(1000).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return port.Asserts.Count > 0 ? 1 : 0;
    }
}
