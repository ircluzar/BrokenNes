using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;

namespace BrokenNes.Windows.Virtual
{
    /// <summary>
    /// A virtual cartridge's own server (the Remastered's Node launcher): started on a free loopback port, inside a Windows job object that kills it when
    /// BrokenNes closes the job, and also when BrokenNes itself dies (the job's last handle goes with the process), so no server is ever left behind.
    /// </summary>
    internal sealed class VirtualCartServer : IDisposable
    {
        private readonly Process process;
        private readonly IntPtr job;
        private readonly ConcurrentQueue<string> tail = new();
        private int disposed;

        public int Port { get; }
        public int ProcessId { get; }
        public string BaseUrl => $"http://127.0.0.1:{Port}";
        /// <summary>Null while the server runs; why it stopped once it has (and BrokenNes did not stop it).</summary>
        public string? Exited { get; private set; }

        private VirtualCartServer(Process p, IntPtr job, int port)
        {
            process = p; this.job = job; Port = port; ProcessId = p.Id;
        }

        /// <summary>Starts the server and waits until it listens (its ready line, or the port answering). Throws <see cref="InvalidOperationException"/> with a message for the user.</summary>
        public static VirtualCartServer Start(VirtualCartManifest m, TimeSpan timeout)
        {
            int port = FreePort();
            var psi = new ProcessStartInfo
            {
                FileName = m.Command,
                WorkingDirectory = m.Folder,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
            };
            foreach (var a in m.ArgsFor(port)) psi.ArgumentList.Add(a);

            IntPtr job = CreateKillOnCloseJob();
            Process p;
            try { p = Process.Start(psi) ?? throw new InvalidOperationException("no process"); }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                CloseHandle(job);
                throw new InvalidOperationException($"Could not start the server of {m.Title} ({m.Command}): {ex.Message}");
            }
            if (job != IntPtr.Zero) AssignProcessToJobObject(job, p.Handle);
            var server = new VirtualCartServer(p, job, port);
            var ready = new ManualResetEventSlim(false);
            void Line(string? s)
            {
                if (s == null) return;
                server.tail.Enqueue(s); while (server.tail.Count > 20) server.tail.TryDequeue(out _);
                if (m.ReadyText.Length > 0 && s.Contains(m.ReadyText, StringComparison.OrdinalIgnoreCase)) ready.Set();
            }
            p.OutputDataReceived += (_, e) => Line(e.Data);
            p.ErrorDataReceived += (_, e) => Line(e.Data);
            p.EnableRaisingEvents = true;
            p.Exited += (_, _) => { if (Volatile.Read(ref server.disposed) == 0) server.Exited = $"the server stopped by itself (exit code {SafeExitCode(p)}){server.LastOutput()}"; ready.Set(); };
            p.BeginOutputReadLine(); p.BeginErrorReadLine();

            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                if (ready.Wait(100) && m.ReadyText.Length > 0) break;
                if (server.Exited != null) break;
                if (m.ReadyText.Length == 0 && PortAnswers(port)) break;
            }
            if (server.Exited != null) { string why = server.Exited; server.Dispose(); throw new InvalidOperationException($"The server of {m.Title} did not start: {why}"); }
            if (sw.Elapsed >= timeout) { string why = server.LastOutput(); server.Dispose(); throw new InvalidOperationException($"The server of {m.Title} did not say it was ready within {timeout.TotalSeconds:0} s{why}"); }
            return server;
        }

        public string LastOutput()
        {
            var lines = tail.ToArray();
            return lines.Length == 0 ? "" : " - its last output: " + string.Join(" | ", lines.TakeLast(4));
        }

        private static int SafeExitCode(Process p) { try { return p.ExitCode; } catch { return -1; } }

        public bool HasExited { get { try { return process.HasExited; } catch { return true; } } }

        /// <summary>Stops the server and waits for it (closing the job kills the whole tree).</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try { if (!process.HasExited) { if (job != IntPtr.Zero) TerminateJobObject(job, 0); else process.Kill(entireProcessTree: true); } } catch { }
            try { process.WaitForExit(5000); } catch { }
            if (job != IntPtr.Zero) CloseHandle(job);
            process.Dispose();
        }

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        private static bool PortAnswers(int port)
        {
            try { using var c = new TcpClient(); return c.ConnectAsync(IPAddress.Loopback, port).Wait(200) && c.Connected; }
            catch { return false; }
        }

        // ---- the job object: kill on close
        private static IntPtr CreateKillOnCloseJob()
        {
            IntPtr h = CreateJobObject(IntPtr.Zero, null);
            if (h == IntPtr.Zero) return IntPtr.Zero;
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = 0x2000;   // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!SetInformationJobObject(h, 9 /* JobObjectExtendedLimitInformation */, ptr, (uint)size)) { CloseHandle(h); return IntPtr.Zero; }
            }
            finally { Marshal.FreeHGlobal(ptr); }
            return h;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS { public ulong a, b, c, d, e, f; }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr attrs, string? name);
        [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
        [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll")] private static extern bool TerminateJobObject(IntPtr job, uint exitCode);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    }
}
