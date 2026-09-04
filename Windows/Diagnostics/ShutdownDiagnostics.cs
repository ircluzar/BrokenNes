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

                InstallVectoredHandler();

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
        // Native / structured exception capture.
        //
        // Managed handlers (UnhandledException, ThreadException) never see a hardware fault or a
        // stack overflow - the runtime tears the process down without running them, which is
        // exactly the "no dialog, no log, just gone" signature this class exists to explain. A
        // vectored exception handler runs before ANY frame-based handler, managed or native, so
        // it is the only in-process place that can name a 0xC0000005 or a 0xC00000FD.
        // -----------------------------------------------------------------------------------

        private delegate int VectoredHandler(IntPtr exceptionPointers);

        // Held in a static field on purpose: if this delegate is collected, the native callback
        // pointer dangles and the "diagnostics" become the crash.
        private static VectoredHandler? vectoredHandler;

        [DllImport("kernel32.dll")]
        private static extern IntPtr AddVectoredExceptionHandler(uint first, VectoredHandler handler);

        private const int EXCEPTION_CONTINUE_SEARCH = 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct EXCEPTION_RECORD
        {
            public uint ExceptionCode;
            public uint ExceptionFlags;
            public IntPtr ExceptionRecord;
            public IntPtr ExceptionAddress;
            public uint NumberParameters;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct EXCEPTION_POINTERS
        {
            public IntPtr ExceptionRecord;
            public IntPtr ContextRecord;
        }

        private static void InstallVectoredHandler()
        {
            try
            {
                vectoredHandler = ptrs =>
                {
                    try
                    {
                        var ep = Marshal.PtrToStructure<EXCEPTION_POINTERS>(ptrs);
                        var er = Marshal.PtrToStructure<EXCEPTION_RECORD>(ep.ExceptionRecord);

                        // 0xE0434352 is every managed throw ("CCR" for CLR) and 0x40010006 is
                        // OutputDebugString - both far too common to log. Everything listed here
                        // is fatal or near enough that one line per occurrence is fine.
                        switch (er.ExceptionCode)
                        {
                            case 0xC0000005: // ACCESS_VIOLATION
                            case 0xC00000FD: // STACK_OVERFLOW
                            case 0xC0000374: // HEAP_CORRUPTION
                            case 0xC000041D: // FATAL_USER_CALLBACK_EXCEPTION
                            case 0x80000003: // BREAKPOINT
                            case 0xC0000409: // STACK_BUFFER_OVERRUN / FailFast
                            case 0xC0000006: // IN_PAGE_ERROR
                            case 0xC000001D: // ILLEGAL_INSTRUCTION
                            case 0xC0000094: // INTEGER_DIVIDE_BY_ZERO
                            case 0xC0000095: // INTEGER_OVERFLOW
                                Log($"*** NATIVE EXCEPTION code=0x{er.ExceptionCode:X8} at 0x{(long)er.ExceptionAddress:X} "
                                  + $"flags=0x{er.ExceptionFlags:X} (0x1 = non-continuable) ***\n" + SafeStack());
                                break;
                        }
                    }
                    catch
                    {
                        // Never let the diagnostic handler itself fault.
                    }
                    return EXCEPTION_CONTINUE_SEARCH; // observe only; change nothing
                };

                AddVectoredExceptionHandler(1, vectoredHandler);
                Log("Vectored exception handler installed");
            }
            catch (Exception ex)
            {
                Log("Could not install vectored exception handler: " + ex.Message);
            }
        }
    }
}
