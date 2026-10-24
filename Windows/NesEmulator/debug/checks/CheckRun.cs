using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace NesEmulator.Debugging.Checks;

/// <summary>
/// Runs the hardware checks and the profiler on a machine: <see cref="Attach"/> adds the console's checks to a debugger (arming its hooks;
/// nothing breaks or parks), the caller runs frames as usual, then <see cref="ConsoleChecks.Finish"/> and the report. Detach the debugger
/// (or remove the observer) and the machine pays nothing again.
/// </summary>
public static class CheckRun
{
    public static ConsoleChecks Attach(Debugger debugger, CheckOptions options)
    {
        ConsoleChecks c = debugger.Machine switch
        {
            NesDebugMachine => new NesChecks(debugger, options),
            SnesDebugMachine => new SnesChecks(debugger, options),
            GbDebugMachine => new GbChecks(debugger, options),
            _ => throw new NotSupportedException($"the checks do not cover the {debugger.Machine.Console}"),
        };
        debugger.TraceRingSize = 0;   // the checks need no trace ring (it costs every instruction)
        debugger.AddObserver(c);
        return c;
    }

    // ------------------------------------------------------------------------------------------------ the profile

    /// <summary>The profile of a run from <paramref name="fromFrame"/> on (frames before it are not counted: boot, a title screen).</summary>
    public static ProfileSummary Summarize(ConsoleChecks c, long fromFrame = 0)
    {
        var frames = c.Frames.Where(f => f.Frame >= fromFrame && f.Cycles > c.NominalFrameCycles * 0.5).ToList();
        var s = new ProfileSummary { Unit = c.CycleUnit, FrameCycles = c.NominalFrameCycles, FromFrame = fromFrame, Frames = frames.Count };
        if (frames.Count > 0)
        {
            var busy = frames.Select(f => 100.0 * f.Busy / c.NominalFrameCycles).ToList();
            s.BusyAverage = busy.Average();
            int wi = busy.IndexOf(busy.Max());
            s.BusyWorst = busy[wi]; s.BusyWorstFrame = frames[wi].Frame;
            s.LagFrames = frames.Count(f => f.Lag);
            s.LagFrameList = frames.Where(f => f.Lag).Select(f => f.Frame).Take(60).ToList();
            var up = frames.Where(f => f.UploadCycles >= 0).Select(f => 100.0 * f.UploadCycles / c.VblankCycles).ToList();
            if (up.Count > 0) { s.UploadAverage = up.Average(); s.UploadWorst = up.Max(); }
            s.DmaAverage = frames.Average(f => (double)f.DmaBytes); s.DmaWorst = frames.Max(f => f.DmaBytes);
        }
        long total = Math.Max(1, c.Frames.Sum(f => f.Cycles));
        int n = Math.Max(1, c.Frames.Count);
        s.Functions = c.Functions.Where(f => f.Inclusive > 0 || f.Exclusive > 0)
            .OrderByDescending(f => Math.Max(f.Inclusive, f.Exclusive)).Take(30)
            .Select(f => new FunctionSummary
            {
                Name = f.Name, Calls = f.Calls, Inclusive = f.Inclusive, Exclusive = f.Exclusive,
                InclusivePercent = 100.0 * f.Inclusive / total, ExclusivePercent = 100.0 * f.Exclusive / total,
                InclusivePerFrame = f.Inclusive / (double)n,
            }).ToList();
        return s;
    }

    public static JsonObject Json(ConsoleChecks c, ProfileSummary p, IEnumerable<Finding>? findings = null)
    {
        var (deep, free) = c.StackUse();
        return new JsonObject
        {
            ["console"] = c.M.Console, ["family"] = c.Family,
            ["findings"] = new JsonArray((findings ?? c.Findings).OrderBy(f => f.Rule).ThenBy(f => f.FirstFrame).Select(FindingJson).ToArray()),
            ["profile"] = ProfileJson(p),
            ["memory"] = new JsonArray(c.MemoryUse().Select(m => (JsonNode)new JsonObject { ["name"] = m.name, ["size"] = m.size, ["written"] = m.written, ["free"] = m.size - m.written }).ToArray()),
            ["stack"] = new JsonObject { ["deepest"] = deep, ["free"] = free },
        };
    }

    public static JsonNode FindingJson(Finding f) => new JsonObject
    {
        ["rule"] = f.Rule, ["place"] = f.Place, ["address"] = f.Address, ["count"] = f.Count, ["firstFrame"] = f.FirstFrame, ["firstScanline"] = f.FirstScanline,
        ["pc"] = "$" + f.Pc.ToString(f.Pc > 0xFFFF ? "X6" : "X4"), ["pcSymbol"] = f.PcSymbol, ["callStack"] = f.CallStack, ["detail"] = f.Detail,
        ["scenario"] = f.Scenario, ["accepted"] = f.AcceptedReason != null, ["reason"] = f.AcceptedReason,
    };

    public static JsonObject ProfileJson(ProfileSummary p) => new()
    {
        ["cycleUnit"] = p.Unit, ["frameCycles"] = Math.Round(p.FrameCycles, 1), ["fromFrame"] = p.FromFrame, ["frames"] = p.Frames,
        ["busyPercentAverage"] = Math.Round(p.BusyAverage, 2), ["busyPercentWorst"] = Math.Round(p.BusyWorst, 2), ["busyWorstFrame"] = p.BusyWorstFrame,
        ["lagFrames"] = p.LagFrames, ["lagFrameList"] = new JsonArray(p.LagFrameList.Select(x => (JsonNode)x).ToArray()),
        ["vblankUploadPercentAverage"] = Math.Round(p.UploadAverage, 2), ["vblankUploadPercentWorst"] = Math.Round(p.UploadWorst, 2),
        ["dmaBytesPerFrameAverage"] = Math.Round(p.DmaAverage, 1), ["dmaBytesPerFrameWorst"] = p.DmaWorst,
        ["functions"] = new JsonArray(p.Functions.Select(f => (JsonNode)new JsonObject
        {
            ["name"] = f.Name, ["calls"] = f.Calls, ["inclusive"] = f.Inclusive, ["exclusive"] = f.Exclusive,
            ["inclusivePercent"] = Math.Round(f.InclusivePercent, 2), ["exclusivePercent"] = Math.Round(f.ExclusivePercent, 2),
            ["inclusivePerFrame"] = Math.Round(f.InclusivePerFrame, 1),
        }).ToArray()),
    };

    /// <summary>The profile as text (Workshop --profile, and the end of a check run).</summary>
    public static IEnumerable<string> ProfileText(ProfileSummary p, int top = 15)
    {
        yield return $"frames {p.Frames} (from frame {p.FromFrame}); a frame is {p.FrameCycles:F0} {p.Unit}";
        yield return $"busy: average {p.BusyAverage:F1}% of the frame, worst {p.BusyWorst:F1}% (frame {p.BusyWorstFrame}); lag frames {p.LagFrames}{(p.LagFrames > 0 ? " (" + string.Join(", ", p.LagFrameList.Take(12)) + (p.LagFrames > 12 ? ", ..." : "") + ")" : "")}";
        yield return $"vblank used for uploads: average {p.UploadAverage:F1}%, worst {p.UploadWorst:F1}%; DMA bytes per frame: average {p.DmaAverage:F0}, worst {p.DmaWorst}";
        yield return $"{"function",-40} {"calls",9} {"inclusive %",12} {"exclusive %",12} {"incl./frame",12}";
        foreach (var f in p.Functions.Take(top))
            yield return $"{(f.Name.Length > 40 ? f.Name[..40] : f.Name),-40} {f.Calls,9} {f.InclusivePercent,11:F2}% {f.ExclusivePercent,11:F2}% {f.InclusivePerFrame,12:F0}";
    }
}

public sealed class ProfileSummary
{
    public string Unit = ""; public double FrameCycles; public long FromFrame; public int Frames;
    public double BusyAverage, BusyWorst; public long BusyWorstFrame;
    public int LagFrames; public List<long> LagFrameList = new();
    public double UploadAverage, UploadWorst, DmaAverage; public long DmaWorst;
    public List<FunctionSummary> Functions = new();
}

public sealed class FunctionSummary
{
    public string Name = ""; public long Calls, Inclusive, Exclusive; public double InclusivePercent, ExclusivePercent, InclusivePerFrame;
}

/// <summary>Free space per ROM bank, read from the file (padding: runs of 64 or more equal $00 or $FF bytes; an estimate, since data can
/// hold long runs of zeros too).</summary>
public static class RomBanks
{
    public sealed record Bank(int Index, int Size, int Free);

    public static List<Bank> Free(byte[] rom, string family)
    {
        ReadOnlySpan<byte> data = rom;
        int bankSize;
        switch (family)
        {
            case "nes":
            {
                if (rom.Length < 16 || rom[0] != 'N' || rom[1] != 'E' || rom[2] != 'S') return new();
                int prg = rom[4] * 0x4000, start = 16 + ((rom[6] & 4) != 0 ? 512 : 0);
                if ((rom[7] & 0x0C) == 0x08) prg = ((rom[9] & 0x0F) << 8 | rom[4]) * 0x4000;   // NES 2.0
                data = data.Slice(start, Math.Min(prg, rom.Length - start));
                bankSize = 0x4000;
                break;
            }
            case "gb": bankSize = 0x4000; break;
            default:
            {
                if (rom.Length % 1024 == 512) data = data[512..];
                bankSize = 0x8000;
                if (data.Length >= 0x10000)
                {
                    int lo = data[0x7FDC] | data[0x7FDD] << 8, lc = data[0x7FDE] | data[0x7FDF] << 8;
                    int hi = data[0xFFDC] | data[0xFFDD] << 8, hc = data[0xFFDE] | data[0xFFDF] << 8;
                    if ((hi ^ hc) == 0xFFFF && (lo ^ lc) != 0xFFFF && (data[0xFFD5] & 1) != 0) bankSize = 0x10000;
                }
                break;
            }
        }
        var list = new List<Bank>();
        for (int b = 0; b * bankSize < data.Length; b++)
        {
            var bank = data.Slice(b * bankSize, Math.Min(bankSize, data.Length - b * bankSize));
            int free = 0, run = 0; byte prev = 0;
            for (int i = 0; i <= bank.Length; i++)
            {
                bool same = i < bank.Length && (bank[i] == 0x00 || bank[i] == 0xFF) && (run == 0 || bank[i] == prev);
                if (same) { if (run == 0) prev = bank[i]; run++; continue; }
                if (run >= 64) free += run;
                run = i < bank.Length && (bank[i] == 0x00 || bank[i] == 0xFF) ? 1 : 0;
                if (run == 1) prev = bank[i];
            }
            list.Add(new Bank(b, bank.Length, free));
        }
        return list;
    }
}
