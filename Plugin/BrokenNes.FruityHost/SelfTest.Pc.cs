// The x86 PC console (console 5) in the plugin (2026-11-10): the x86 console's sound devices as chips, Direct mode only, one instance = one voice.
// Checks: the 11 PC chips appended after every earlier chip, in order, on console 5 (the earlier lists pinned by `harvest` stay where they are); ROM mode refused; each chip renders A4 non-silent,
// at its pitch (the device's own step: the 8254 divisor, the F-number ...) and identical twice; every OPL patch of the bank sounds; the patch editor changes the sound, survives a saved project
// (section OPC1); velocity scales the level; three deliberate bugs through hook 1139 must be caught.
using BrokenNes.Fruity;
using BrokenNes.SynthHost;

namespace BrokenNes.FruityHost;

public sealed unsafe partial class SelfTest
{
    internal static readonly string[] PcChips =
        ["PC_ADLIB", "PC_ADLIB_M036", "PC_ADLIB_D072", "PC_ADLIB_D074C", "PC_ADLIB_D074F", "PC_ADLIB_NUK174", "PC_SPEAKER", "PC_SB_D072", "PC_TANDY_D072", "PC_GUS_D074", "PC_COVOX"];
    private const int HkPcMode = 1100, HkPcKeyed = 1103, HkPcWrites = 1104, HkPcSet = 1105, HkPcGet = 1106, HkPcHas = 1108, HkPcMutant = 1139;

    /// <summary>The Instrument value that makes a near-sine on the chip (pitch is measured from it): OPL "SV triangle" (3), DAC "Sine" (5), Tandy tone (0), speaker (none).</summary>
    private static int PcSineInstrument(string id) => id.StartsWith("PC_ADLIB") ? 3 : id is "PC_SB_D072" or "PC_GUS_D074" or "PC_COVOX" ? 5 : 0;

    private (double Rms, double Hz, string Hash) PcNote(int chip, int instrument, double key = 69, double velocity = 0.78, Action<PluginInstance>? extra = null, double seconds = 0.6)
    {
        var (audio, mixer, plugin) = Play(One(key, 0, seconds, velocity), setup: p => { p.SetParam(PCore, chip); p.SetParam(PInstrument, instrument); extra?.Invoke(p); });
        using (plugin)
        {
            var mono = Mono(audio);
            var log = mixer.Log[0];
            double rms = Audio.Rms(mono, log.StartSample + 2000, log.EndSample - 500);
            double hz = SteadyHz(mono, 44100, log);
            return (rms, hz, HashAudio(audio));
        }
    }

    private void PcConsole(T t)
    {
        List<string> chips;
        using (var probe = lib.Create()) chips = Chips(probe);
        int first = chips.FindIndex(c => c == PcChips[0]);
        t.Expect(first >= PinnedChips.Length + HarvestChips.Length, $"PC_ADLIB is chip {first}: expected after the {PinnedChips.Length} pinned and {HarvestChips.Length} harvested chips");
        if (first < 0) return;
        for (int i = 0; i < PinnedChips.Length; i++) t.Expect(chips[i] == PinnedChips[i], $"chip {i} moved: {chips[i]}");
        for (int k = 0; k < PcChips.Length; k++) t.Expect(first + k < chips.Count && chips[first + k] == PcChips[k], $"PC chip {k} is {(first + k < chips.Count ? chips[first + k] : "missing")}, expected {PcChips[k]}");

        var rows = new List<string>();
        for (int k = 0; k < PcChips.Length; k++)
        {
            string id = PcChips[k]; int index = first + k;
            using (var a = lib.Create())
            {
                a.SetParam(PCore, index);
                t.Expect(a.GetParam(PConsole) == 5, $"{id}: picking it left the console at {a.GetParam(PConsole)}, expected 5 (PC)");
                t.Expect(a.Test(HkPcMode) == 1, $"{id}: not in PC Direct mode");
                a.SetParam(PMode, 1);
                t.Expect(a.GetParam(PMode) == 0, $"{id}: ROM mode was accepted on the PC console (it has no DOS program to run)");
                var blob = a.SaveState();
                using var b = lib.Create();
                b.LoadState(blob);
                t.Expect(b.GetParam(PCore) == index && b.GetParam(PConsole) == 5, $"{id}: a saved project came back as chip {b.GetParam(PCore)} console {b.GetParam(PConsole)}");
            }
            int ins = PcSineInstrument(id);
            var n1 = PcNote(index, ins);
            var n2 = PcNote(index, ins);
            t.Expect(n1.Rms > 1e-3 && !double.IsNaN(n1.Rms), $"{id}: A4 is silent (rms {n1.Rms:0.00000})");
            t.Expect(n1.Hash == n2.Hash, $"{id}: two renders of the same note differ");
            double cents = 1200 * Math.Log2(n1.Hz / 440.0);
            // the devices' own pitch steps at 440 Hz are far below 10 cents (F-number 0.1 cent, 8254 0.02, SN76496 ~4, GUS FC ~0.5, DAC phase exact)
            t.Expect(Math.Abs(cents) < 12, $"{id}: A4 measured {n1.Hz:0.00} Hz ({cents:+0.0;-0.0} cents)");
            rows.Add($"{id} rms {n1.Rms:0.000} {n1.Hz:0.0}Hz");
        }
        t.Note(string.Join("; ", rows));

        int adlib = first;
        // every patch of the OPL bank sounds (short notes)
        int bankCount; using (var a = lib.Create()) bankCount = (int)a.Test(1102);
        int silent = 0; var silentNames = new List<int>();
        for (int i = 0; i < bankCount; i++)
        {
            var r = PcNote(adlib, i, key: 60, seconds: 0.3);
            if (!(r.Rms > 3e-4)) { silent++; silentNames.Add(i); }
        }
        t.Expect(silent == 0, $"{silent} of {bankCount} OPL patches are silent at C5: {string.Join(",", silentNames)}");
        t.Note($"{bankCount} OPL patches, {silent} silent");

        // velocity scales the level (deliberate bug 3 = velocity ignored)
        bool VelocityWorks()
        {
            var loud = PcNote(adlib, 2, velocity: 0.95); var soft = PcNote(adlib, 2, velocity: 0.25);
            return soft.Rms < loud.Rms * 0.6;
        }
        t.Expect(VelocityWorks(), "a soft note is not quieter than a loud one on the AdLib");

        // the patch editor: Mod MULT (field 4) to 7 changes the sound and survives a saved project (deliberate bug 2 = the custom patch ignored)
        string plain = PcNote(adlib, 2).Hash;
        bool EditWorks()
        {
            byte[] blob; string edited;
            var (audio, _, plugin) = Play(One(69, 0, 0.6), setup: p => { p.SetParam(PCore, adlib); p.SetParam(PInstrument, 2); p.Test(HkPcSet, 4, 7); p.Test(HkPcSet, 24, 7); });
            using (plugin) { edited = HashAudio(audio); blob = plugin.SaveState(); }
            if (edited == plain) return false;
            using var b = lib.Create();
            b.LoadState(blob);
            bool kept = b.Test(HkPcHas) == 1 && b.Test(HkPcGet, 4) == 7 && b.Test(HkPcGet, 24) == 7;
            var mixer = new Mixer { SampleRate = 44100, BlockSize = 256 };
            string again = HashAudio(mixer.Run(b, One(69, 0, 0.6)));
            return kept && again == edited;
        }
        t.Expect(EditWorks(), "the patch editor's change did not change the sound, or was not kept in the saved project (OPC1)");

        // a slide on the speaker follows the pitch (deliberate bug 1 = bends ignored)
        double SlideEnd()
        {
            var s = new Scenario { Name = "slide", Tempo = 60, LengthBeats = 1.4 };
            s.Notes.Add(new SimNote { StartBeat = 0, LengthBeats = 1.2, Key = 69, Channel = 0, Velocity = 0.8, Bend = [[0, 0], [0.3, 1200], [1.2, 1200]] });
            var (audio, mixer, plugin) = Play(s, setup: p => p.SetParam(PCore, first + 6));
            using (plugin) { var mono = Mono(audio); var log = mixer.Log[0]; return Audio.Frequency(mono, 44100, log.EndSample - 20000, log.EndSample - 2000); }
        }
        double end = SlideEnd();
        t.Expect(Math.Abs(1200 * Math.Log2(end / 880.0)) < 15, $"a bend A4 -> A5 on the speaker ended at {end:0.0} Hz (expected 880)");

        // the deliberate bugs must be caught
        using (var ctl = lib.Create())
        {
            ctl.Test(HkPcMutant, 0, 1); bool c1 = Math.Abs(1200 * Math.Log2(SlideEnd() / 880.0)) >= 15;
            ctl.Test(HkPcMutant, 0, 2); bool c2 = !EditWorks();
            ctl.Test(HkPcMutant, 0, 3); bool c3 = !VelocityWorks();
            ctl.Test(HkPcMutant, 0, 0);
            t.Expect(c1 && c2 && c3, $"deliberate bugs not all caught: bends ignored {c1}, custom patch ignored {c2}, velocity ignored {c3}");
            t.Note($"deliberate bugs caught: bends {c1}, custom patch {c2}, velocity {c3}");
        }
    }
}
