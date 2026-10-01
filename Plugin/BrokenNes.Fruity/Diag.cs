namespace BrokenNes.Fruity;

/// <summary>
/// Opt-in call log for diagnosing a plugin inside a host that gives no output (FL Studio):
/// set the environment variable BROKENNES_PLUGIN_LOG to a file path and every host -> plugin
/// control call is appended and flushed immediately, so after a crash the last line shows what
/// the host was doing. Off by default; per-block calls (render, new tick) are never logged.
/// </summary>
public static class Diag
{
    private static readonly string? path = Environment.GetEnvironmentVariable("BROKENNES_PLUGIN_LOG");
    private static readonly object gate = new();

    public static bool Enabled => path is { Length: > 0 };

    public static void Log(string message)
    {
        if (!Enabled) return;
        try
        {
            lock (gate)
                File.AppendAllText(path!, $"{DateTime.Now:HH:mm:ss.fff} [{Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}");
        }
        catch
        {
            // logging must never take the host down
        }
    }
}
