// The .flp music pipeline, phase 1 (docs/projects/flp-music-pipeline.md), in the DLL: the Test MIDI export window's "This FL project" button with real mouse messages. FL's window
// title is given by editor hook 623 (FL's real title form is not verified), the file by hook 621 (the one-time pick); the project is the synthetic one FruityHost flp-test writes.
using BrokenNes.Fruity;
using BrokenNes2.Music.Flp;

namespace BrokenNes.FruityHost;

public sealed unsafe partial class SelfTest
{
    private const int IdMidFlpBtn = 2008, EdSTitle = 623;

    private void SongFlpDll(T t)
    {
        string flp = Path.Combine(MidiDir(), "flp song.flp");
        File.WriteAllBytes(flp, FlpChecks.SyntheticProject());
        // FL's window names the project; the remembered pick is replaced by the test's file
        t.Expect(FlpTitle.NameFromTitle("flp song.flp - FL Studio 2026") == "flp song" && FlpTitle.NameFromTitle("*flp song - FL Studio 2026") == "flp song" && FlpTitle.NameFromTitle("Untitled") == null,
            "FL's window title is not read as the project's name");
        using var p = SongPlugin();
        nint parent = Win32Host.CreateParent();
        try
        {
            p.ShowEditor(parent);
            for (int k = 0; k < 3; k++) Win32Host.Pump(40);
            ClickControl(t, p, IdMidBtn, "Test MIDI export button");
            t.Expect(p.Test(30, IdMidFlpBtn) != 0, "the window has no \"This FL project\" button");
            fixed (char* c = "flp song.flp - FL Studio 2026\0") p.Test(EdSTitle, 0, (nint)c);
            fixed (char* c = flp + "\0") p.Test(EdSPick, 0, (nint)c);
            ClickControl(t, p, IdMidFlpBtn, "This FL project");
            t.Expect(SWait(p, () => p.Test(HkSRunning) == 1), $"the project's song did not become the game: '{SText(p, 0)}'");
            t.Expect(SText(p, 0).StartsWith("Loaded flp song.flp"), $"the window said '{SText(p, 0)}'");
            string facts = SText(p, 1);
            t.Expect(facts.Contains("square 1 <- MIDI 1") && facts.Contains("triangle <- MIDI 3") && facts.Contains("noise <- MIDI 10"), "the facts are not the synthetic project's: " + facts);
            t.Expect(facts.Contains("from the .flp not played as in FL"), "the facts do not say what the game plays differently from FL: " + facts);
            var audio = RenderBlocks(p, 1.5);
            t.Expect(Audio.Rms(audio, 0, audio.Length) > 0.003, $"the project's song is silent (rms {Audio.Rms(audio, 0, audio.Length):0.0000})");
            // Reload reads the .flp again: unchanged, then after a "save" with another tempo
            Win32Host.Pump(40);
            ClickControl(t, p, IdMidReloadBtn, "Reload");
            t.Expect(SWait(p, () => p.Test(HkSBusy) == 0 && SText(p, 0).StartsWith("Reloaded")), $"Reload said '{SText(p, 0)}'");
            t.Expect(SText(p, 0).Contains("save the project in FL"), $"Reload of an unchanged .flp said '{SText(p, 0)}'");
            t.Info.Add("flp: " + facts.Replace("\n", " | "));
        }
        finally { p.Test(EdSTitle, 0, 0); Win32Host.DestroyParent(parent); }
    }
}
