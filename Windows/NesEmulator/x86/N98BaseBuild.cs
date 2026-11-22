using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NesEmulator.X86;

/// <summary>
/// "Build the Windows 98 base" (user spec 2026-11-21): the user's own Windows 98 SE ISO + their product key -> the nano98 seat base
/// (tools/nano98/build_seat.py stages 1-7: unattended Setup, seat, slim, settle, pack, warm state). The key is only handed to the build through an
/// environment variable of the child process: never on a command line, in a log, in git, in a package or in a settings file; the setup disk that
/// holds it in its answer file is deleted once Windows is installed. The user approved accepting the Windows 98 SE licence for our emulated PCs
/// (2026-11-21). Heavy (Windows Setup runs: ~150k frames): minutes.
/// </summary>
public static class N98BaseBuild
{
    public static bool KeyLooksRight(string key) => Regex.IsMatch(key.Trim().ToUpperInvariant(), "^[A-Z0-9]{5}(-[A-Z0-9]{5}){4}$");

    /// <summary>Where a built base goes by default: %LOCALAPPDATA%\BrokenNes\nano98\seat_base.n98d (what the app looks for).</summary>
    public static string DefaultOut => Path.Combine(Path.GetDirectoryName(N98Seat.DiscFolder)!, "seat_base.n98d");

    /// <summary>The Workshop exe the build runs Windows in: BROKENNES_WORKSHOP, else beside the app, else the repo's build.</summary>
    public static string? Workshop()
    {
        if (Environment.GetEnvironmentVariable("BROKENNES_WORKSHOP") is { Length: > 0 } w && File.Exists(w)) return w;
        string a = Path.Combine(AppContext.BaseDirectory, "BrokenNes.Workshop.exe");
        if (File.Exists(a)) return a;
        try
        {
            string repo = Path.GetDirectoryName(Path.GetDirectoryName(N98Seat.ToolsDir()))!;
            return Directory.Exists(Path.Combine(repo, "Workshop", "bin")) ? Directory.GetFiles(Path.Combine(repo, "Workshop", "bin"), "BrokenNes.Workshop.exe", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>The Python with pyfatfs the setup-disk stage needs: BROKENNES_N98_PYFAT, else the dev venv when it is there.</summary>
    private static string? PyFat()
    {
        if (Environment.GetEnvironmentVariable("BROKENNES_N98_PYFAT") is { Length: > 0 } p) return p;
        const string dev = @"C:\BrokenNes-work\tools\pyfat_venv\Scripts\python.exe";
        return File.Exists(dev) ? dev : null;
    }

    // ---- background job for the HTTP / MCP drivers: build-base returns at once, base-build-status polls ----
    static readonly object _jobLock = new();
    static System.Threading.Tasks.Task<JsonObject>? _job;
    static readonly List<string> _jobLog = new();
    static DateTime _jobStart;

    /// <summary>Starts the build in the background (one at a time). Returns {ok, started} at once, or {ok:false, error} when one is running
    /// or the inputs are wrong. The key goes only into the child's environment, as in Run.</summary>
    public static JsonObject Start(string iso, string key, string? outPath = null)
    {
        if (!KeyLooksRight(key)) return new JsonObject { ["ok"] = false, ["error"] = "the product key is 5 groups of 5 letters/digits: XXXXX-XXXXX-XXXXX-XXXXX-XXXXX" };
        if (!File.Exists(iso)) return new JsonObject { ["ok"] = false, ["error"] = "the Windows 98 SE disc image was not found" };
        lock (_jobLock)
        {
            if (_job is { IsCompleted: false }) return new JsonObject { ["ok"] = false, ["error"] = "a base build is already running", ["running"] = true };
            lock (_jobLog) _jobLog.Clear();
            _jobStart = DateTime.UtcNow;
            _job = System.Threading.Tasks.Task.Run(() =>
            {
                try { return Run(iso, key, outPath, null, l => { lock (_jobLog) { _jobLog.Add(l); if (_jobLog.Count > 400) _jobLog.RemoveAt(0); } }); }
                catch (Exception e) { return new JsonObject { ["ok"] = false, ["error"] = e.Message }; }
            });
        }
        return new JsonObject { ["ok"] = true, ["started"] = true, ["poll"] = "base-build-status" };
    }

    /// <summary>{ok, running, started, seconds, progress (last line), log (last 12 lines, key masked), result (when done)}.</summary>
    public static JsonObject JobStatus()
    {
        var j = _job;
        string[] tail; lock (_jobLog) tail = _jobLog.Skip(Math.Max(0, _jobLog.Count - 12)).ToArray();
        var r = new JsonObject
        {
            ["ok"] = true, ["running"] = j is { IsCompleted: false }, ["started"] = j != null,
            ["seconds"] = j == null ? 0 : (int)(DateTime.UtcNow - _jobStart).TotalSeconds,
            ["progress"] = tail.LastOrDefault(), ["log"] = new JsonArray(tail.Select(t => (JsonNode)t).ToArray()),
        };
        if (j is { IsCompleted: true }) r["result"] = j.Result.DeepClone();
        return r;
    }

    /// <summary>Runs the whole build. log gets the build's own lines (never the key). Returns {ok, base, warm} or {ok:false, error}.</summary>
    public static JsonObject Run(string iso, string key, string? outPath = null, string? work = null, Action<string>? log = null)
    {
        key = key.Trim().ToUpperInvariant();
        if (!KeyLooksRight(key)) return new JsonObject { ["ok"] = false, ["error"] = "the product key is 5 groups of 5 letters/digits: XXXXX-XXXXX-XXXXX-XXXXX-XXXXX" };
        if (!File.Exists(iso)) return new JsonObject { ["ok"] = false, ["error"] = "the Windows 98 SE disc image was not found" };
        string ws = Workshop() ?? throw new FileNotFoundException("the base build needs BrokenNes.Workshop.exe (set BROKENNES_WORKSHOP)");
        outPath ??= DefaultOut;
        work ??= Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath))!, "base_build");
        Directory.CreateDirectory(work);
        var (py, pre) = N98Seat.PythonCmd();
        var psi = new ProcessStartInfo(py, $"{pre}-I \"{Path.Combine(N98Seat.ToolsDir(), "build_seat.py")}\" --workshop \"{ws}\" --work \"{work}\" --out \"{outPath}\" --iso \"{iso}\" --accept-eula --key-env N98_PRODUCT_KEY" + (PyFat() is { } pf ? $" --pyfat \"{pf}\"" : ""))
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        psi.Environment["N98_PRODUCT_KEY"] = key;
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Python 3 was not found (set BROKENNES_PYTHON)");
        string last = "";
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) { last = e.Data.Replace(key, "*****"); log?.Invoke(last); } };
        p.BeginErrorReadLine();
        for (string? l; (l = p.StandardOutput.ReadLine()) != null;) { l = l.Replace(key, "*****"); if (l.StartsWith("N98BASE ")) last = l; log?.Invoke(l); }
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            int at = last.IndexOf("N98BASE ", StringComparison.Ordinal);
            return at >= 0 ? (JsonObject)JsonNode.Parse(last[(at + 8)..])! : new JsonObject { ["ok"] = false, ["error"] = $"the base build stopped ({p.ExitCode}): {last}" };
        }
        return new JsonObject { ["ok"] = true, ["base"] = Path.GetFullPath(outPath), ["warm"] = Path.GetFullPath(outPath) + ".warm" };
    }
}
