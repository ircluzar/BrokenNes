// Relink (2026-10-18): the plugin's host log (HostLog.cs) goes to the user's Documents\BrokenNes2 host log.txt; the r12 test runs wrote 35 lines into the user's real file (the record scenario put
// the variable back to "unset" while the plugin's writer still had lines queued). From now on every run of this test bench names a scratch file (BROKENNES_HOSTLOG_FILE) and a scratch folder that
// stands for Documents (BROKENNES_HOSTLOG_DEFAULT_DIR) before any plugin is loaded, for the whole process, and the selftest's last check (`hostlog`) fails when the real file changed during the run.
namespace BrokenNes.FruityHost;

public static class HostLogGuard
{
    private static (bool Exists, long Length, DateTime Written) before, beforeOld;
    private static bool installed;

    /// <summary>The user's real log (never written by a test).</summary>
    public static string RealPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BrokenNes2 host log.txt");
    public static string RealOldPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BrokenNes2 host log.old.txt");

    /// <summary>The scratch file the plugin logs into during this run, and the scratch folder standing for Documents.</summary>
    public static string ScratchFile => Environment.GetEnvironmentVariable("BROKENNES_HOSTLOG_FILE") ?? "";
    public static string ScratchDocuments => Environment.GetEnvironmentVariable("BROKENNES_HOSTLOG_DEFAULT_DIR") ?? "";

    /// <summary>Called first thing by Program: the variables for the whole process (a caller's own values are kept), and what the real files look like now.</summary>
    public static void Install()
    {
        if (installed) return;
        installed = true;
        string root = Path.Combine(Path.GetTempPath(), $"bn2_hostlog_{Environment.ProcessId}");
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BROKENNES_HOSTLOG_FILE"))) Environment.SetEnvironmentVariable("BROKENNES_HOSTLOG_FILE", Path.Combine(root, "host log.txt"));
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BROKENNES_HOSTLOG_DEFAULT_DIR"))) Environment.SetEnvironmentVariable("BROKENNES_HOSTLOG_DEFAULT_DIR", Path.Combine(root, "Documents"));
        Directory.CreateDirectory(Path.GetDirectoryName(ScratchFile)!);
        Directory.CreateDirectory(ScratchDocuments);
        before = Snap(RealPath);
        beforeOld = Snap(RealOldPath);
    }

    /// <summary>What a file looks like now (exists, length, last write): the comparison <see cref="RealFileTouched"/> makes (the `hostlog` check shows it can fail on a scratch file).</summary>
    public static (bool Exists, long Length, DateTime Written) SnapFile(string path) => Snap(path);

    private static (bool Exists, long Length, DateTime Written) Snap(string path)
    {
        try { var f = new FileInfo(path); return f.Exists ? (true, f.Length, f.LastWriteTimeUtc) : (false, 0, default); }
        catch (Exception) { return (false, -1, default); }
    }

    /// <summary>Null when the user's real log files are as they were when the run began; else what changed.</summary>
    public static string? RealFileTouched()
    {
        var now = Snap(RealPath);
        var nowOld = Snap(RealOldPath);
        if (now != before) return $"{RealPath} changed during the run (it {(before.Exists ? $"had {before.Length:N0} bytes" : "did not exist")}, now {(now.Exists ? $"{now.Length:N0} bytes, written {now.Written.ToLocalTime():HH:mm:ss}" : "gone")}); if FL Studio itself ran BrokenNes2 meanwhile, that may be FL's, run again with FL closed";
        if (nowOld != beforeOld) return $"{RealOldPath} changed during the run";
        return null;
    }
}
