using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace BrokenNes.Windows
{
    /// <summary>
    /// Lightweight performance profiler for identifying bottlenecks
    /// </summary>
    public static class PerformanceProfiler
    {
        private class SampleData
        {
            public long TotalTicks;
            public int Count;
            public long MinTicks = long.MaxValue;
            public long MaxTicks;
            
            public void Add(long ticks)
            {
                TotalTicks += ticks;
                Count++;
                if (ticks < MinTicks) MinTicks = ticks;
                if (ticks > MaxTicks) MaxTicks = ticks;
            }
            
            public double AverageMs => Count > 0 ? (TotalTicks / (double)Count) / Stopwatch.Frequency * 1000.0 : 0;
            public double TotalMs => TotalTicks / (double)Stopwatch.Frequency * 1000.0;
            public double MinMs => MinTicks != long.MaxValue ? MinTicks / (double)Stopwatch.Frequency * 1000.0 : 0;
            public double MaxMs => MaxTicks / (double)Stopwatch.Frequency * 1000.0;
        }
        
        private static readonly ConcurrentDictionary<string, SampleData> samples = new();
        private static readonly Stopwatch globalTimer = Stopwatch.StartNew();
        private static bool enabled = false;
        
        public static bool Enabled 
        { 
            get => enabled;
            set => enabled = value;
        }
        
        /// <summary>
        /// Time a code block and record the result
        /// </summary>
        public static IDisposable Time(string name)
        {
            if (!enabled) return new DummyTimer();
            return new ScopedTimer(name);
        }
        
        private class ScopedTimer : IDisposable
        {
            private readonly string name;
            private readonly long startTicks;
            
            public ScopedTimer(string name)
            {
                this.name = name;
                startTicks = Stopwatch.GetTimestamp();
            }
            
            public void Dispose()
            {
                long elapsed = Stopwatch.GetTimestamp() - startTicks;
                var data = samples.GetOrAdd(name, _ => new SampleData());
                data.Add(elapsed);
            }
        }
        
        private class DummyTimer : IDisposable
        {
            public void Dispose() { }
        }
        
        /// <summary>
        /// Get a snapshot of current profiling data
        /// </summary>
        public static string GetReport(int topCount = 20)
        {
            var sb = new StringBuilder();
            var elapsed = globalTimer.Elapsed;
            
            sb.AppendLine("=== Performance Profile Report ===");
            sb.AppendLine($"Session Duration: {elapsed.TotalSeconds:F1}s");
            sb.AppendLine($"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();
            
            var sorted = samples
                .Select(kvp => new { Name = kvp.Key, Data = kvp.Value })
                .OrderByDescending(x => x.Data.TotalMs)
                .Take(topCount)
                .ToList();
            
            if (sorted.Count == 0)
            {
                sb.AppendLine("No profiling data collected.");
                return sb.ToString();
            }
            
            sb.AppendLine($"{"Operation",-40} {"Total(ms)",10} {"Avg(ms)",10} {"Min(ms)",10} {"Max(ms)",10} {"Count",8} {"%Time",8}");
            sb.AppendLine(new string('-', 110));
            
            double totalTime = sorted.Sum(x => x.Data.TotalMs);
            
            foreach (var item in sorted)
            {
                double percent = totalTime > 0 ? (item.Data.TotalMs / totalTime * 100.0) : 0;
                sb.AppendLine($"{item.Name,-40} {item.Data.TotalMs,10:F2} {item.Data.AverageMs,10:F3} {item.Data.MinMs,10:F3} {item.Data.MaxMs,10:F3} {item.Data.Count,8} {percent,7:F1}%");
            }
            
            sb.AppendLine();
            sb.AppendLine($"Total measured time: {totalTime:F2} ms");
            
            return sb.ToString();
        }
        
        /// <summary>
        /// Write profiling report to file
        /// </summary>
        public static void SaveReport(string filePath, int topCount = 50)
        {
            try
            {
                var report = GetReport(topCount);
                File.WriteAllText(filePath, report);
                Console.WriteLine($"Performance report saved to: {filePath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to save report: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Reset all collected data
        /// </summary>
        public static void Reset()
        {
            samples.Clear();
            globalTimer.Restart();
        }
        
        /// <summary>
        /// Get quick summary for display overlay
        /// </summary>
        public static string GetQuickSummary(int topCount = 5)
        {
            var sorted = samples
                .Select(kvp => new { Name = kvp.Key, Data = kvp.Value })
                .OrderByDescending(x => x.Data.TotalMs)
                .Take(topCount)
                .ToList();
            
            if (sorted.Count == 0) return "No profiling data";
            
            var sb = new StringBuilder();
            sb.AppendLine("Top Hotspots:");
            foreach (var item in sorted)
            {
                sb.AppendLine($"  {item.Name}: {item.Data.AverageMs:F2}ms avg ({item.Data.Count} calls)");
            }
            return sb.ToString();
        }
    }
}
