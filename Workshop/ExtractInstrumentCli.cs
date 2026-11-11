using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using BrokenNes2.Extract;
using BrokenNes2.Instruments;
using NesEmulator;
using NesEmulator.Gb;

namespace BrokenNes.Workshop;

/// <summary>
/// <c>--extract-instrument</c>: instrument extraction for batch work (BrokenDev's <c>instrument extract</c>). Runs a NES or Game Boy game headless on the FIX / stock cores, feeds the shared capture
/// core (Plugin/BrokenNes2.Plugin/Extract) every frame, picks the note of a channel at a frame and writes it as a .bn2preset the plugin loads. The definition of the capture is in
/// docs/projects/flp-music-pipeline.md ("Instrument extraction").
///
///   --extract-instrument --rom game.nes|.gb|.gbc --frame N --channel C [--out file.bn2preset] [--name text] [--input "40:Start,44:"] [--json]
///   channels: NES 0 pulse 1, 1 pulse 2, 2 triangle, 3 noise; Game Boy 0 square 1, 1 square 2, 2 wave, 3 noise
///   exit 0 written, 1 no note / no preset, 2 usage
/// </summary>
internal static class ExtractInstrumentCli
{
    public static int Run(string[] args)
    {
        string? Opt(string n) { int i = Array.FindIndex(args, a => a.Equals(n, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        string? rom = Opt("--rom");
        if (rom == null || !File.Exists(rom) || !int.TryParse(Opt("--frame") ?? "", out int frame) || !int.TryParse(Opt("--channel") ?? "", out int channel))
        {
            Console.Error.WriteLine("Usage: --extract-instrument --rom <game.nes|.gb|.gbc> --frame N --channel C [--out file.bn2preset] [--name text] [--input \"40:Start,44:\"] [--json]");
            return 2;
        }
        bool json = args.Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase));
        string input = Opt("--input") ?? "";
        int total = frame + CapturedNote.MaxFrames + 2;
        byte[] bytes = File.ReadAllBytes(rom);
        string sha1 = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
        string game = Path.GetFileNameWithoutExtension(rom);
        string ext = Path.GetExtension(rom).ToLowerInvariant();
        IExtractFeed feed;
        try
        {
            if (ext is ".gb" or ".gbc") feed = RunGb(bytes, ext == ".gbc", input, total);
            else feed = RunNes(rom, bytes, input, total);
        }
        catch (Exception e) { Console.Error.WriteLine("extract: " + e.Message); return 1; }

        var n = feed.Recorder.Pick(channel, frame, out string? why);
        if (n == null) { Console.Error.WriteLine("extract: " + why); return 1; }
        var file = CapturePreset.Write(n, Opt("--name") ?? "", game, sha1, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), out string? problem);
        if (file == null) { Console.Error.WriteLine("extract: no preset: " + problem); return 1; }
        string outPath = Opt("--out") ?? Path.Combine(Directory.GetCurrentDirectory(), $"{Sanitize(game)}_{CapturedNote.ChannelName(n.Console, n.Channel).Replace(" ", "").ToLowerInvariant()}_f{n.KeyOnFrame}.bn2preset");
        File.WriteAllBytes(outPath, file);
        string levels = string.Join(",", n.Life.Select(x => x.Level));
        string cents = string.Join(",", n.Life.Select((_, i) => n.CentsAt(i).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)));
        if (json)
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { file = outPath, console = n.Console.ToString(), channel = n.Channel, keyOnFrame = n.KeyOnFrame, frames = n.Frames, end = n.End.ToString(), midiKey = Math.Round(n.MidiKey, 2), levels = n.Life.Select(x => (int)x.Level), duty = n.Life.Select(x => (int)x.Duty), cents = n.Life.Select((_, i) => Math.Round(n.CentsAt(i), 1)) }));
        else
        {
            Console.WriteLine($"captured: {n.Describe()}, key {n.MidiKey:0.00}");
            Console.WriteLine($"levels: {levels}");
            Console.WriteLine($"pitch (cents from the key-on): {cents}");
            Console.WriteLine($"key-on registers: {Convert.ToHexString(n.Regs)}");
            Console.WriteLine($"preset: {outPath} ({file.Length} bytes)");
        }
        return 0;
    }

    private static string Sanitize(string s) => new string(s.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');

    private static IExtractFeed RunNes(string path, byte[] bytes, string input, int total)
    {
        var nes = new NES { RomName = Path.GetFileName(path), RomPath = path };
        nes.LoadROM(bytes);
        nes.SetCpuCore("FIX"); nes.SetPpuCore("FIX"); nes.SetApuCore("FIX");
        var fix = NesExtractFeed.FixOf(nes) ?? throw new InvalidOperationException("the APU is not APU_FIX");
        var feed = new NesExtractFeed(fix, nes.CopyApuLatch);
        var script = RomTestCli.ParseInputScript(input.Length > 0 ? input : null);
        var held = new bool[8];
        int next = 0;
        for (int f = 0; f < total; f++)
        {
            while (next < script.Count && script[next].Frame <= f) { held = (bool[])script[next].Held.Clone(); next++; }
            nes.SetInputs(held, null);
            nes.RunFrame();
            int q = fix.GetQueuedSampleCount(); if (q > 0) fix.GetAudioSamples(q);
            feed.EndFrame();
        }
        feed.Detach();
        return feed;
    }

    private static IExtractFeed RunGb(byte[] bytes, bool cgb, string input, int total)
    {
        var board = new BOARD_GB(GbCartridge.Load(bytes), cgb ? GbModel.Cgb : GbModel.Dmg);
        var apu = board.Apu as APU_GB ?? throw new InvalidOperationException("the sound chip is not APU_GB");
        var feed = new GbExtractFeed(apu);
        var script = Gb.GbRunCli.ParseInput(input);
        var buf = new short[16384];
        for (int f = 0; f < total; f++)
        {
            if (script.TryGetValue(f, out var held)) { board.Buttons = held; board.UpdateJoypadIrq(); }
            board.RunFrame();
            while (board.Apu.ReadSamples(buf) > 0) { }
            feed.EndFrame();
        }
        feed.Detach();
        return feed;
    }
}
