// `FruityHost musictest-ctwsms <music test.sms> <its manifest.txt> <out.sms> <out fence.txt> [song.flp]`: the CTW SMS MUSIC TEST ROM
// (VRUN_CTW_SMS_dev musictest/build-musictest.ps1: the game's own Sound Test as a ROM, the game's sound driver) with an FL project's song
// in its FL TEST slot. The song is made by the CTW SMS back end (Music/Flp/CtwSmsBackend.cs: Build, then Patch over the slot's four
// padded streams, exactly as the plugin patches the title song of Resources/flpctwsms.sms); the slot's loop length (the screen's
// position / loop counter) is written at the manifest's `loopframes` offset and the Sega checksum redone.
// Without a .flp: the ROM as built (the placeholder song in the slot) and a fence that says so.
// A song with no Master System instance plays its NES instances on the channels of the same number (the back end's own rule since 2026-11-12, FlpNesFallback:
// NES is the inter-console standard; said in the fence). --nes-as-sms: always, even when the song has Master System instances (square 1 / 2 / triangle / noise =
// tone 1 / 2 / 3 / noise: CTW SMS plays VRUN NES's music, so these are the channels the game's streams were made for).
// Exit 0 when the ROM is written (the fence may still have lines over the limit: the song plays without them), 1 + a "FAIL" line otherwise.
using BrokenNes2.Music.Flp;

namespace BrokenNes.FruityHost;

internal static class MusicTestCtwSms
{
    public static int Main(string[] args)
    {
        bool nesAsSms = args.Contains("--nes-as-sms");
        args = args.Where(a => a != "--nes-as-sms").ToArray();
        if (args.Length < 5) { Console.WriteLine("FAIL  usage: musictest-ctwsms <music test.sms> <manifest.txt> <out.sms> <out fence.txt> [song.flp] [--nes-as-sms]"); return 1; }
        try
        {
            string tpl = args[1], man = args[2], outRom = args[3], outFence = args[4], flp = args.Length > 5 ? args[5] : "";
            var template = File.ReadAllBytes(tpl);
            var manifest = File.ReadAllText(man);
            var m = VocGbBackend.Manifest(manifest, "bn2-flp-ctwsms", CtwSmsBackend.TemplateVersion);
            if (template.Length.ToString() != m["rom-bytes"]) { Console.WriteLine("FAIL  the music test ROM is not the one its manifest describes"); return 1; }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outRom))!);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFence))!);
            if (flp.Length == 0)
            {
                File.WriteAllBytes(outRom, template);
                File.WriteAllLines(outFence, ["(no .flp) for ctw-sms-mt: 0 over the limit, 0 played differently, 1 inside",
                    "OK    slot: FL TEST plays the music test's placeholder song (pass -Flp to put an FL project's song there)"]);
                Console.WriteLine($"wrote {outRom} (placeholder)");
                return 0;
            }
            string name = Path.GetFileNameWithoutExtension(flp);
            var model = FlpSong.Read(File.ReadAllBytes(flp));
            var reread = new List<string>();
            if (nesAsSms)
                foreach (var ins in model.Instruments)
                    if (ins.Bn2 is { Console: 0, Mode: "direct" } b && ins.Notes.Count > 0 && b.Channel is >= 0 and <= 3 && b.Core is not (>= FlpPresetSound.FirstPresetChip and <= FlpPresetSound.LastPresetChip))
                    { b.Console = 3; b.ConsoleName = "Master System"; reread.Add($"channel {ins.Rack} \"{ins.Name}\" (NES {CtwSmsBackend.Names[b.Channel]})"); }
            var v = CtwSmsBackend.Build(model, name);
            if (reread.Count > 0) v.Fence.Insert(0, new FenceLine("console", FenceLevel.Note, $"--nes-as-sms: {reread.Count} NES instances read as the Master System channel of the same number: {string.Join(", ", reread)}"));
            var rom = CtwSmsBackend.Patch(template, manifest, v, out var why);
            var lines = FlpFence.Lines(v.Fence).ToList();
            File.WriteAllLines(outFence, [$"{Path.GetFileName(flp)} for ctw-sms-mt: {FlpFence.Summary(v.Fence)}", .. lines]);
            Console.WriteLine($"{Path.GetFileName(flp)} for ctw-sms-mt: {FlpFence.Summary(v.Fence)}");
            foreach (var l in lines) Console.WriteLine("  " + l);
            if (rom == null) { Console.WriteLine("FAIL  no ROM: " + why); return 1; }
            if (m.ContainsKey("loopframes"))
            {
                var (off, _) = VocGbBackend.Region(m, "loopframes");
                int lf = Math.Clamp(v.Build!.Facts.LoopFrames, 0, 65535);
                rom[off] = (byte)lf; rom[off + 1] = (byte)(lf >> 8);
                CtwSmsBackend.SegaChecksum(rom);
            }
            File.WriteAllBytes(outRom, rom);
            Console.WriteLine($"wrote {outRom}");
            return 0;
        }
        catch (Exception e) { Console.WriteLine("FAIL  " + e.Message); return 1; }
    }
}
