using System;
using System.Collections.Generic;

namespace BrokenNes.Workshop.Timeflow;

/// <summary><c>--timeflow search-measure</c>: T4's measurements (filled in below).</summary>
internal static partial class TimeflowSearchMeasure
{
    public static int Run(Func<string, string, string> Opt, Func<string, List<string>> All, Func<string, bool> Has, bool json) => Measure(Opt, All, Has, json);
}
