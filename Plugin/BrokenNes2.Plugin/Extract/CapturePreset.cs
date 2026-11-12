// Instrument extraction: a CapturedNote becomes a preset. The instrument is the console's own instrument read from the key-on snapshot (NesInstruments / GbInstruments, as Runaway reads one),
// the context is what PresetContext wants for the console, and the note behaviour is version 4 (PresetNote.Captured): the captured life, which the NES player plays frame by frame
// (level, duty and pitch relative to FL's note) and the Game Boy player follows for pitch and the note's end (its level comes from the hardware envelope in the snapshot).
using BrokenNes2.Instruments;
using BrokenNes2.Presets;

namespace BrokenNes2.Extract;

public static class CapturePreset
{
    /// <summary>The preset of <paramref name="n"/>, or null with the reason. <paramref name="created"/> is given by the caller (the library never reads a clock).</summary>
    public static PresetData? Make(CapturedNote n, string name, string game, string sha1, long created, out string? problem)
    {
        problem = null;
        Instrument? inst;
        byte[] context;
        switch (n.Console)
        {
            case ConsoleId.Nes:
                if (n.Channel > 3) { problem = "the DMC cannot be extracted (its sample lives in the game's memory)"; return null; }
                inst = new NesInstruments(new NesSnapshot(n.Regs.Length >= NesSnapshot.RegCount ? n.Regs[..NesSnapshot.RegCount] : n.Regs, [])).Read(n.Channel);
                context = [];
                break;
            case ConsoleId.GameBoy:
            {
                var regs = n.Regs.Length >= GbSnapshot.RegCount ? n.Regs[..GbSnapshot.RegCount] : new byte[GbSnapshot.RegCount];
                var wave = n.Regs.Length >= GbSnapshot.RegCount + GbSnapshot.WaveSize ? n.Regs[GbSnapshot.RegCount..(GbSnapshot.RegCount + GbSnapshot.WaveSize)] : new byte[GbSnapshot.WaveSize];
                bool cgb = n.Regs.Length > GbSnapshot.RegCount + GbSnapshot.WaveSize && n.Regs[GbSnapshot.RegCount + GbSnapshot.WaveSize] != 0;
                inst = new GbInstruments(new GbSnapshot(regs, wave, cgb)).Read(n.Channel);
                context = [(byte)(cgb ? 1 : 0)];
                break;
            }
            case ConsoleId.Snes:
            case ConsoleId.MasterSystem:
            case ConsoleId.Genesis:
                inst = ExtractChips.MakeInstrument(n, out context, out problem);
                if (inst == null) return null;
                break;
            default: problem = $"extraction does not support the {n.Console} yet"; return null;
        }
        if (inst == null) { problem = "the channel has no instrument"; return null; }
        string label = CapturedNote.ChannelName(n.Console, n.Channel);
        inst.Name = Clip(name.Length > 0 ? name : $"{label} @ {n.KeyOnFrame}", PresetFile.MaxName);
        var packed = InstrumentCodec.Pack(inst);
        var history = new InstrumentHistory(inst).Serialize();
        string notes = $"Extracted by BrokenNes2 from {(game.Length > 0 ? game : "a game")}: {n.Describe()}. Plays the game's envelope frame by frame from FL's note.";
        var d = PresetMaker.Make(inst.Name, label, Clip(game, PresetFile.MaxGame), sha1.Length <= 40 && sha1.All(Uri.IsHexDigit) ? sha1 : "", Clip(notes, PresetFile.MaxNotes), created, packed, packed, history, context, out problem,
            [PresetFile.ConsoleName(n.Console).ToLowerInvariant(), PresetMaker.KindTag(inst.Kind), "extracted"]);
        if (d == null) return null;
        PresetNote.AttachCaptured(d, n);
        return d;
    }

    /// <summary><see cref="Make"/> then the file's bytes.</summary>
    public static byte[]? Write(CapturedNote n, string name, string game, string sha1, long created, out string? problem)
    {
        var d = Make(n, name, game, sha1, created, out problem);
        return d == null ? null : PresetFile.TryWrite(d, out problem);
    }

    private static string Clip(string s, int max) => s.Length > max ? s[..max] : s;
}
