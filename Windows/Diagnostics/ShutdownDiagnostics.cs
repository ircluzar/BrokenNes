using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BrokenNes.Windows.Diagnostics
{
    /// <summary>
    /// Forensics for "the process just vanished".
    ///
    /// WHY THIS EXISTS
    /// ---------------
    /// A UAT pass (UAT/findings/direct-play-r2.md) reproduced a total, silent death of
    /// BrokenNes.Windows.exe: no crash dialog, no WerFault, no Application-log entry, exit code 0.
    /// Every one of those symptoms says "clean shutdown", not "crash" - which means the usual
    /// crash-forensics tools (dumps, WER, the event log) have nothing to look at by definition.
    /// The only way to see what happened is from inside the process, and it has to be written to
    /// disk *before* the exit completes, because there is no "after".
    ///
    /// Console.WriteLine is useless here for two reasons: the console is hidden by default
    /// (EmulatorConfig.ShowConsole) and it dies with the process. So everything lands in a real
    /// file, opened with FileOptions.WriteThrough and flushed to the device on every single line.
    /// That is slow, and it is meant to be: correctness of the last line written beats throughput
    /// when the whole point is capturing the moment of death.
    ///
    /// COST / GATING
    /// -------------
    /// The always-on part is just event-handler registration plus a line per lifecycle event, so
    /// it costs nothing during play. The high-volume part - per-keystroke Win32 message tracing
    /// and first-chance exception logging - is off unless BROKENNES_DIAG is set, because that one
    /// genuinely writes to disk on every key press.
    ///
    ///     set BROKENNES_DIAG=1        -> lifecycle + input/message tracing
    ///     set BROKENNES_DIAG=all      -> the above plus first-chance exceptions (very noisy)
    ///     set BROKENNES_DIAG_DIR=...  -> override the log directory
    ///
    /// Default log directory: %LOCALAPPDATA%\BrokenNes\diagnostics\
    /// One file per process: shutdown-&lt;pid&gt;-&lt;yyyyMMdd-HHmmss&gt;.log
    /// </summary>
    internal static class ShutdownDiagnostics
    {
        private static readonly object Gate = new object();
        private static StreamWriter? writer;
        private static bool installed;
        private static Stopwatch? uptime;

        /// <summary>True when BROKENNES_DIAG asks for per-keystroke/message tracing.</summary>
        internal static bool VerboseInput { get; private set; }

        /// <summary>True when BROKENNES_DIAG=all asks for first-chance exception logging.</summary>
        internal static bool VerboseExceptions { get; private set; }

        /// <summary>Path of this process's log file, or null when logging could not be set up.</summary>
        internal static string? LogPath { get; private set; }

        /// <summary>
        /// Wire up every hook that can observe a shutdown. Safe to call more than once; only the
        /// first call does anything. Must never throw - diagnostics failing is not a reason for
        /// the emulator to fail to start.
        /// </summary>
        internal static void Install()
        {
            lock (Gate)
            {
                if (installed) return;
                installed = true;
            }

            try
            {
                var mode = (Environment.GetEnvironmentVariable("BROKENNES_DIAG") ?? string.Empty).Trim();
                VerboseInput = mode.Length > 0 && !mode.Equals("0", StringComparison.OrdinalIgnoreCase)
                                              && !mode.Equals("off", StringComparison.OrdinalIgnoreCase);
                VerboseExceptions = mode.Equals("all", StringComparison.OrdinalIgnoreCase);

                var dir = Environment.GetEnvironmentVariable("BROKENNES_DIAG_DIR");
                if (string.IsNullOrWhiteSpace(dir))
                {
                    dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "BrokenNes", "diagnostics");
                }
                Directory.CreateDirectory(dir);

                int pid = Environment.ProcessId;
                LogPath = Path.Combine(dir, $"shutdown-{pid}-{DateTime.Now:yyyyMMdd-HHmmss}.log");

                // WriteThrough + AutoFlush: the last line written must already be on disk when the
                // process disappears mid-sentence. Share ReadWrite so a test script can tail it live.
                var fs = new FileStream(LogPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite,
                                        bufferSize: 1, options: FileOptions.WriteThrough);
                writer = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };

                uptime = Stopwatch.StartNew();

                Log($"=== BrokenNes shutdown diagnostics ===");
                Log($"pid={pid} exe={Environment.ProcessPath} verboseInput={VerboseInput} verboseExceptions={VerboseExceptions}");
                Log($"cmdline={Environment.CommandLine}");

                // --- The hooks. Each one names a different way a .NET process can end. ---

                AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                {
                    // Fires for a normal Main return AND for Environment.Exit. It does NOT fire for
                    // a hard crash, so its presence alone already distinguishes "clean exit" from
                    // "killed". The stack is often just runtime shutdown machinery, but when
                    // Environment.Exit is the culprit the caller is right there in it.
                    Log("ProcessExit fired. Stack:\n" + SafeStack());
                };

                AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                {
                    Log($"UnhandledException terminating={e.IsTerminating}:\n{e.ExceptionObject}");
                };

                AppDomain.CurrentDomain.DomainUnload += (_, _) => Log("DomainUnload");

                TaskScheduler.UnobservedTaskException += (_, e) =>
                {
                    Log($"UnobservedTaskException:\n{e.Exception}");
                };

                Application.ThreadException += (_, e) =>
                {
                    Log($"Application.ThreadException:\n{e.Exception}");
                };

                Application.ApplicationExit += (_, _) =>
                {
                    // THE one that matters most for this bug. Application.Exit() and "last form
                    // closed" both land here, and the stack tells them apart: an Exit() call keeps
                    // its caller (the menu item handler, a hotkey, whatever) on the stack, while a
                    // natural message-loop end does not.
                    Log("Application.ApplicationExit fired. Stack:\n" + SafeStack());
                };

                Application.ThreadExit += (_, _) => Log("Application.ThreadExit. Stack:\n" + SafeStack());

                InstallHardwareFaultLog();

                if (VerboseExceptions)
                {
                    AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
                    {
                        // Deliberately shallow - first-chance is extremely noisy and the message
                        // plus type is enough to spot an exception being swallowed somewhere.
                        Log($"FirstChance [{Thread.CurrentThread.ManagedThreadId}] {e.Exception.GetType().Name}: {e.Exception.Message}");
                    };
                }
            }
            catch
            {
                // Never let diagnostics break startup.
            }
        }

        /// <summary>
        /// Append one timestamped line (uptime-stamped too, so ordering against the repro script's
        /// own clock is unambiguous). Silently does nothing when logging is not set up.
        /// </summary>
        internal static void Log(string message)
        {
            var w = writer;
            if (w == null) return;
            try
            {
                lock (Gate)
                {
                    w.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [+{uptime?.ElapsedMilliseconds ?? 0,8}ms] [t{Environment.CurrentManagedThreadId}] {message}");
                }
            }
            catch
            {
                // A failed diagnostic write must never become the crash we are hunting.
            }
        }

        /// <summary>Log only when BROKENNES_DIAG asked for the high-volume stream.</summary>
        internal static void LogVerbose(string message)
        {
            if (VerboseInput) Log(message);
        }

        /// <summary>
        /// Log a message together with the current call stack. Use on any code path that can end
        /// the process, so the log names the culprit rather than just the fact of the exit.
        /// </summary>
        internal static void LogWithStack(string message)
        {
            Log(message + "\n" + SafeStack());
        }

        private static string SafeStack()
        {
            try { return new StackTrace(1, true).ToString(); }
            catch (Exception ex) { return "<stack unavailable: " + ex.Message + ">"; }
        }

        // -----------------------------------------------------------------------------------
        // Hardware-origin managed exceptions.
        //
        // This used to be a vectored exception handler (AddVectoredExceptionHandler with a managed
        // delegate), meant to name native faults. It was the crash it was meant to explain: the
        // runtime implements NullReferenceException and DivideByZeroException as hardware exceptions
        // (0xC0000005 / 0xC0000094) raised INSIDE managed code, and a first-in-line vectored handler
        // is called for them before the runtime's own - a reverse P/Invoke into a thread that is
        // still in cooperative mode, which the runtime answers with a fail-fast: "Invalid Program:
        // attempted to call a UnmanagedCallersOnly method from managed code", .NET Runtime event
        // 1023, exit code 0x80131506. So any caught NRE anywhere killed the process (BrokenNes 2
        // desktop, 2026-09-27; reproduced in isolation). A managed delegate can never safely be a
        // vectored handler, so it is gone; true native crashes are still recorded by Windows Error
        // Reporting (Application event log 1000/1001, .NET Runtime 1023).
        //
        // What it was really catching - managed code faulting on null, a zero divisor or an
        // overflow, even when something catches it - is logged here instead, from the managed
        // FirstChanceException event (safe: it is raised by the runtime, not by the OS). Rate-limited
        // per exception type so a hot loop cannot flood the log.
        // -----------------------------------------------------------------------------------

        private const int HardwareFaultLogLimit = 20;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> hardwareFaultCounts = new();

        private static void InstallHardwareFaultLog()
        {
            AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
            {
                var ex = e.Exception;
                if (ex is not (NullReferenceException or DivideByZeroException or OverflowException or AccessViolationException)) return;
                string key = ex.GetType().Name;
                int n = hardwareFaultCounts.AddOrUpdate(key, 1, (_, c) => c + 1);
                if (n > HardwareFaultLogLimit) return;
                Log($"FirstChance {key} #{n}{(n == HardwareFaultLogLimit ? " (further ones not logged)" : "")} " +
                    $"[thread {Thread.CurrentThread.ManagedThreadId}]: {ex.Message}\n{SafeStack()}");
            };
            Log("Hardware-fault first-chance log installed");
        }
    }
}
