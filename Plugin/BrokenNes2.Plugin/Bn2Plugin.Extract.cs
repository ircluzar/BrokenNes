// Instrument extraction in ROM mode: the self-test's hooks (Fpd.TestBase + 1140..1143). The user's way in is the Presets tab's "Capture next note" (PresetLibrary.Capture / CapturePoll).
using BrokenNes2.Extract;

namespace BrokenNes2;

public sealed unsafe partial class Bn2Plugin
{
    private string extractText = "";
    private byte[]? extractFile;

    /// <summary>1140 start a capture of channel <c>index</c> on this instance's game (0 started, 1 refused: words in 1143), 1141 its state (0 waiting, 1 a preset is ready, 2 failed: words in 1143; a ready
    /// preset is made here), 1142 copy the ready preset file into the <c>index</c> bytes at <c>value</c> (returns its length, -1 none), 1143 copy the last words.</summary>
    private nint ExtractTestHook(int n, nint index, nint value)
    {
        var emu = Emulator;
        switch (n)
        {
            case 1140:
            {
                extractFile = null;
                string? why = emu == null ? "no emulator" : emu.StartExtract((int)index);
                extractText = why ?? "waiting";
                return why == null ? 0 : 1;
            }
            case 1141:
            {
                if (extractFile != null) return 1;
                if (emu == null) return 2;
                var (note, failed) = emu.TakeExtracted();
                if (failed != null) { extractText = failed; return 2; }
                if (note == null) return emu.ExtractWaiting ? 0 : 2;
                extractFile = CapturePreset.Write(note, "", emu.ExtractGameName, "", 0, out string? problem);
                extractText = extractFile == null ? "no preset: " + problem : note.Describe();
                return extractFile == null ? 2 : 1;
            }
            case 1142:
            {
                if (extractFile == null) return -1;
                if (index >= extractFile.Length) extractFile.CopyTo(new Span<byte>((void*)value, (int)index));
                return extractFile.Length;
            }
            case 1143: return CopyText(extractText, index, value);
            default: return 0;
        }
    }
}
