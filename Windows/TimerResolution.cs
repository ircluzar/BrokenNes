using System;
using System.Runtime.InteropServices;

namespace BrokenNes.Windows
{
    /// <summary>
    /// Requests a 1 ms system timer for this process. Without it Thread.Sleep(1) in the emulation pacing loop
    /// sleeps ~15.6 ms, which is as long as a whole audio device buffer, so the audio queue is refilled too
    /// late and the output underruns (audible stutter). Windows 10 2004+/11 no longer lets one process
    /// inherit another process's timer request, and may throttle it for background/occluded windows, so the
    /// request must be made by this process and the throttle opted out of explicitly.
    /// </summary>
    internal static class TimerResolution
    {
        private static bool enabled;

        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")]
        private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref ProcessPowerThrottlingState info, int size);

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessPowerThrottlingState { public uint Version, ControlMask, StateMask; }

        public static void Enable()
        {
            if (enabled) return;
            enabled = true;
            try
            {
                timeBeginPeriod(1);
                // ProcessPowerThrottling (4): IGNORE_TIMER_RESOLUTION (0x4) controlled, state 0 = do not ignore
                var s = new ProcessPowerThrottlingState { Version = 1, ControlMask = 0x4, StateMask = 0 };
                SetProcessInformation(GetCurrentProcess(), 4, ref s, Marshal.SizeOf<ProcessPowerThrottlingState>());
            }
            catch { }
        }
    }
}
