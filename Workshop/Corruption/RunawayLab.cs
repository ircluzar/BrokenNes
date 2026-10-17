using System;

namespace BrokenNes.Workshop.Corruption;

/// <summary>
/// --runaway-lab &lt;what&gt; ...   The Instrument Runaway lab (round 6): measures, on real games, which corruptions the plugin's Instrument Runaway carries into FL and which it does not.
/// See docs/projects/round6-runaway.md for the method and the results.
///   snes-probe  --rom X [--frames N] [--input script]     what a SNES game's sound driver does
///   snes-resynth --rom X [--name N] [--setup N] [--window N] [--seeds N]     the game's own notes played again from the recorded macros (what a recipe model can carry)
///   snes-matrix --rom X [--name N] [--input script] [--setup N] [--window N] [--seeds N]     corruption classes against the game's sound and Instrument Runaway
/// </summary>
internal static class RunawayLab
{
    public static int Run(string[] args)
    {
        string what = args.Length > 1 ? args[1] : "";
        switch (what.ToLowerInvariant())
        {
            case "snes-probe": return LabSnes.Probe(args);
            case "snes-matrix": return LabSnes.Matrix(args);
            case "snes-resynth": return LabSnes.ResynthLab(args);
            case "md-probe": return LabMd.Probe(args);
            case "md-resynth": return LabMd.ResynthLab(args);
            case "md-matrix": return LabMd.Matrix(args);
            default:
                Console.Error.WriteLine("usage: --runaway-lab snes-probe --rom X [--frames N] [--input script]");
                return 2;
        }
    }
}
