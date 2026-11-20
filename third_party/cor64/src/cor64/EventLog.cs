// [cer64plus] append-only RCP event log for handshake debugging. Flushed by sampler.
using System;
using System.IO;
namespace cor64
{
    public static class EventLog
    {
        private static StreamWriter s_W;
        private static readonly object s_L = new object();
        private static int s_N;
        public const int Max = 400000;
        public static readonly bool Enabled = Environment.GetEnvironmentVariable("COR64_EVENTLOG") == "1";
        public static void Open(string path) { if (!Enabled) return; lock (s_L) { s_W = new StreamWriter(path, false); } }
        public static void Flush() { lock (s_L) { s_W?.Flush(); } }
        // [audio_fix] COR64_EVENTLOG_SKIP=KIND,KIND drops noisy kinds; COR64_EVENTLOG_FROM=<tick> starts late
        private static readonly string[] s_Skip = (Environment.GetEnvironmentVariable("COR64_EVENTLOG_SKIP") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        private static readonly long s_From = long.TryParse(Environment.GetEnvironmentVariable("COR64_EVENTLOG_FROM"), out long f) ? f : 0;
        public static void E(string kind, string detail = "")
        {
            if (!Enabled || s_W == null || s_N >= Max) return;
            if (Telemetry.Ticks < s_From || Array.IndexOf(s_Skip, kind) >= 0) return;
            lock (s_L) { s_N++; s_W.WriteLine(Telemetry.Ticks + " " + kind + " " + detail); }
        }
    }
}
