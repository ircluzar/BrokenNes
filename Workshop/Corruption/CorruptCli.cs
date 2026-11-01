using System;
using System.IO;

namespace BrokenNes.Workshop.Corruption;

/// <summary>
/// Headless tools for the corruption library (NesEmulator.Corruption), the code the desktop app, Lite and the FL plugin share:
///   --corrupt-test [--smw &lt;rom&gt;] [--only a,b]     the checks (every one shown able to fail by a control that is caught)
///   --corrupt-apply --rom X --stockpile S [--entry id|name|n] [--frames N] [--out dir]   start a stockpile entry on a ROM and report what changed
/// </summary>
internal static class CorruptCli
{
    public static int Test(string[] args) => CorruptChecks.Run(args);
    public static int Apply(string[] args) => CorruptApply.Run(args);
}
