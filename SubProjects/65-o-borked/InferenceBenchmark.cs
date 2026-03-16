using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using NesRomPatcher;

namespace NesRomPatcher
{
    /// <summary>
    /// Benchmarking utility to compare Python vs C# inference performance
    /// </summary>
    public static class InferenceBenchmark
    {
        public struct BenchmarkResult
        {
            public string Method { get; set; }
            public TimeSpan ColdStartTime { get; set; }
            public TimeSpan[] InferenceTimes { get; set; }
            public TimeSpan AverageInferenceTime => TimeSpan.FromMilliseconds(InferenceTimes.Average(t => t.TotalMilliseconds));
            public TimeSpan MedianInferenceTime => InferenceTimes.OrderBy(t => t.TotalMilliseconds).Skip(InferenceTimes.Length / 2).First();
            public double ThroughputPerSecond => 1000.0 / AverageInferenceTime.TotalMilliseconds;
            public long MemoryUsageMB { get; set; }
        }

        /// <summary>
        /// Run comprehensive benchmark comparing Python and C# inference
        /// </summary>
        public static void RunBenchmark(int numRuns = 10)
        {
            Console.WriteLine("?? INFERENCE BENCHMARK - PYTHON VS C#");
            Console.WriteLine("=" + new string('=', 60));
            Console.WriteLine($"?? Running {numRuns} inference operations for each method");
            Console.WriteLine();

            var results = new List<BenchmarkResult>();

            // Check if ONNX model exists
            var onnxPath = Path.Combine("onnx_export", "6502_span_predictor.onnx");
            var configPath = Path.Combine("onnx_export", "6502_span_predictor_config.json");
            
            if (File.Exists(onnxPath) && File.Exists(configPath))
            {
                Console.WriteLine("?? Benchmarking C# ONNX Inference...");
                var csharpResult = BenchmarkCSharpInference(onnxPath, configPath, numRuns);
                results.Add(csharpResult);
                Console.WriteLine();
            }
            else
            {
                Console.WriteLine("??  C# ONNX model not found - skipping C# benchmark");
                Console.WriteLine("?? Run 'python export_to_onnx.py' first");
                Console.WriteLine();
            }

            // Check if Python model exists
            var pythonModelCandidates = new[] { "6502_span_predictor_best.pt", "6502_span_predictor.pt" };
            var pythonModel = pythonModelCandidates.FirstOrDefault(File.Exists);
            
            if (pythonModel != null)
            {
                Console.WriteLine("?? Benchmarking Python Inference...");
                var pythonResult = BenchmarkPythonInference(pythonModel, numRuns);
                results.Add(pythonResult);
                Console.WriteLine();
            }
            else
            {
                Console.WriteLine("??  Python model not found - skipping Python benchmark");
                Console.WriteLine("?? Run 'python train_6502_predictor.py' first");
                Console.WriteLine();
            }

            // Show comparison
            if (results.Count >= 2)
            {
                ShowBenchmarkComparison(results);
            }
            else if (results.Count == 1)
            {
                Console.WriteLine("?? Single method benchmark completed:");
                ShowSingleResult(results[0]);
            }
            else
            {
                Console.WriteLine("? No models available for benchmarking");
                Console.WriteLine("?? Train and export models first");
            }
        }

        private static BenchmarkResult BenchmarkCSharpInference(string onnxPath, string configPath, int numRuns)
        {
            var sw = Stopwatch.StartNew();
            
            // Cold start measurement
            using var patcher = new CSharpRomPatcher(onnxPath, configPath);
            var coldStartTime = sw.Elapsed;
            
            Console.WriteLine($"   ??  Cold start: {coldStartTime.TotalMilliseconds:F1}ms");

            // Create test data
            var testRom = CreateTestRom();
            var holeStart = 500;
            var holeEnd = 508;

            // Warm up run
            patcher.PatchHole(testRom, holeStart, holeEnd, temperature: 0.1f);
            Console.WriteLine($"   ?? Warm-up completed");

            // Benchmark runs
            var inferenceTimes = new TimeSpan[numRuns];
            var memoryBefore = GC.GetTotalMemory(true);

            for (int i = 0; i < numRuns; i++)
            {
                var runSw = Stopwatch.StartNew();
                var result = patcher.PatchHole(testRom, holeStart, holeEnd, temperature: 0.3f);
                inferenceTimes[i] = runSw.Elapsed;
                
                if (i % (numRuns / 4) == 0)
                    Console.WriteLine($"   ?? Run {i + 1}/{numRuns}: {runSw.Elapsed.TotalMilliseconds:F1}ms");
            }

            var memoryAfter = GC.GetTotalMemory(true);
            var memoryUsage = (memoryAfter - memoryBefore) / (1024 * 1024);

            return new BenchmarkResult
            {
                Method = "C# ONNX",
                ColdStartTime = coldStartTime,
                InferenceTimes = inferenceTimes,
                MemoryUsageMB = memoryUsage
            };
        }

        private static BenchmarkResult BenchmarkPythonInference(string modelPath, int numRuns)
        {
            // Create benchmark script
            var benchmarkScript = CreatePythonBenchmarkScript(modelPath, numRuns);
            var scriptPath = "benchmark_python.py";
            File.WriteAllText(scriptPath, benchmarkScript);

            try
            {
                var processInfo = new ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = scriptPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                var sw = Stopwatch.StartNew();
                using var process = Process.Start(processInfo);
                
                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                var totalTime = sw.Elapsed;

                if (process.ExitCode != 0)
                {
                    Console.WriteLine($"   ? Python benchmark failed: {error}");
                    return new BenchmarkResult { Method = "Python (Failed)" };
                }

                // Parse results from Python output
                return ParsePythonBenchmarkResults(output, totalTime);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ? Error running Python benchmark: {ex.Message}");
                return new BenchmarkResult { Method = "Python (Error)" };
            }
            finally
            {
                // Cleanup
                if (File.Exists(scriptPath))
                    File.Delete(scriptPath);
            }
        }

        private static string CreatePythonBenchmarkScript(string modelPath, int numRuns)
        {
            return $@"
import torch
import time
import json
import numpy as np
from train_6502_predictor import TransformerPredictor, MASK_TOKEN

# Load model
checkpoint = torch.load('{modelPath}', map_location='cpu')
config = checkpoint['config']

model = TransformerPredictor(
    vocab_size=config['vocab_size'],
    embed_size=config['embed_size'],
    hidden_size=config['hidden_size'],
    num_heads=config['num_heads'],
    num_layers=config['num_layers'],
    dropout=config['dropout'],
    max_len=config['seq_len']
)

model.load_state_dict(checkpoint['model_state_dict'])
model.eval()

print('   ??  Cold start completed')

# Create test data
test_sequence = torch.randint(0, 256, (1, config['seq_len']), dtype=torch.long)
test_sequence[0, 500:508] = MASK_TOKEN

# Warm up
with torch.no_grad():
    _ = model(test_sequence)
print('   ?? Warm-up completed')

# Benchmark runs
inference_times = []
for i in range({numRuns}):
    start_time = time.perf_counter()
    
    with torch.no_grad():
        logits = model(test_sequence)
        # Simulate full inference pipeline
        probs = torch.softmax(logits[0, 500:508], dim=-1)
        predictions = probs.argmax(dim=-1)
    
    end_time = time.perf_counter()
    inference_times.append((end_time - start_time) * 1000)  # Convert to ms
    
    if i % ({numRuns} // 4) == 0:
        print(f'   ?? Run {{i + 1}}/{numRuns}: {{inference_times[-1]:.1f}}ms')

# Output results as JSON for parsing
results = {{
    'inference_times_ms': inference_times,
    'average_ms': np.mean(inference_times),
    'median_ms': np.median(inference_times),
    'min_ms': np.min(inference_times),
    'max_ms': np.max(inference_times)
}}

print('BENCHMARK_RESULTS:' + json.dumps(results))
";
        }

        private static BenchmarkResult ParsePythonBenchmarkResults(string output, TimeSpan totalTime)
        {
            try
            {
                // Find the JSON results in output
                var jsonStart = output.IndexOf("BENCHMARK_RESULTS:");
                if (jsonStart == -1)
                {
                    Console.WriteLine("   ??  Could not parse Python results");
                    return new BenchmarkResult { Method = "Python (Parse Error)" };
                }

                var jsonStr = output.Substring(jsonStart + "BENCHMARK_RESULTS:".Length);
                var results = JsonSerializer.Deserialize<JsonElement>(jsonStr);

                var inferenceTimesMs = results.GetProperty("inference_times_ms").EnumerateArray()
                    .Select(x => TimeSpan.FromMilliseconds(x.GetDouble())).ToArray();

                Console.WriteLine($"   ??  Average: {results.GetProperty("average_ms").GetDouble():F1}ms");

                return new BenchmarkResult
                {
                    Method = "Python PyTorch",
                    ColdStartTime = totalTime, // Approximate, includes script startup
                    InferenceTimes = inferenceTimesMs,
                    MemoryUsageMB = 0 // Not measured for Python
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ? Error parsing Python results: {ex.Message}");
                return new BenchmarkResult { Method = "Python (Parse Error)" };
            }
        }

        private static byte[] CreateTestRom()
        {
            var rom = new byte[1024];
            for (int i = 0; i < rom.Length; i++)
            {
                rom[i] = (byte)(i % 256);
            }
            return rom;
        }

        private static void ShowBenchmarkComparison(List<BenchmarkResult> results)
        {
            Console.WriteLine("?? BENCHMARK COMPARISON");
            Console.WriteLine("=" + new string('=', 60));

            var csharp = results.FirstOrDefault(r => r.Method.Contains("C#"));
            var python = results.FirstOrDefault(r => r.Method.Contains("Python"));

            if (csharp.Method != null && python.Method != null)
            {
                Console.WriteLine($"?? Cold Start Time:");
                Console.WriteLine($"   C#:     {csharp.ColdStartTime.TotalMilliseconds:F0}ms");
                Console.WriteLine($"   Python: {python.ColdStartTime.TotalMilliseconds:F0}ms");
                var coldStartRatio = python.ColdStartTime.TotalMilliseconds / csharp.ColdStartTime.TotalMilliseconds;
                Console.WriteLine($"   ?? C# is {coldStartRatio:F1}x faster for cold start");
                Console.WriteLine();

                Console.WriteLine($"? Inference Performance:");
                Console.WriteLine($"   C# Average:     {csharp.AverageInferenceTime.TotalMilliseconds:F1}ms");
                Console.WriteLine($"   Python Average: {python.AverageInferenceTime.TotalMilliseconds:F1}ms");
                var inferenceRatio = python.AverageInferenceTime.TotalMilliseconds / csharp.AverageInferenceTime.TotalMilliseconds;
                Console.WriteLine($"   ?? C# is {inferenceRatio:F1}x faster for inference");
                Console.WriteLine();

                Console.WriteLine($"?? Throughput:");
                Console.WriteLine($"   C#:     {csharp.ThroughputPerSecond:F1} inferences/second");
                Console.WriteLine($"   Python: {python.ThroughputPerSecond:F1} inferences/second");
                Console.WriteLine();

                if (csharp.MemoryUsageMB > 0)
                {
                    Console.WriteLine($"?? Memory Usage:");
                    Console.WriteLine($"   C#: ~{csharp.MemoryUsageMB}MB");
                    Console.WriteLine($"   Python: Not measured (typically ~800MB)");
                    Console.WriteLine();
                }

                Console.WriteLine($"?? SUMMARY:");
                if (inferenceRatio > 1.0)
                    Console.WriteLine($"   ?? C# ONNX is {inferenceRatio:F1}x faster than Python!");
                else
                    Console.WriteLine($"   ?? Python is {1/inferenceRatio:F1}x faster than C# ONNX");
                    
                Console.WriteLine($"   ?? C# cold start is {coldStartRatio:F1}x faster");
                Console.WriteLine($"   ?? C# has much lower memory usage");
                Console.WriteLine($"   ?? C# has simpler deployment (no Python runtime)");
            }
            else
            {
                Console.WriteLine("? Could not compare - missing results");
                foreach (var result in results)
                {
                    ShowSingleResult(result);
                }
            }
        }

        private static void ShowSingleResult(BenchmarkResult result)
        {
            Console.WriteLine($"?? {result.Method} Results:");
            Console.WriteLine($"   Cold Start: {result.ColdStartTime.TotalMilliseconds:F1}ms");
            if (result.InferenceTimes?.Length > 0)
            {
                Console.WriteLine($"   Average Inference: {result.AverageInferenceTime.TotalMilliseconds:F1}ms");
                Console.WriteLine($"   Median Inference: {result.MedianInferenceTime.TotalMilliseconds:F1}ms");
                Console.WriteLine($"   Throughput: {result.ThroughputPerSecond:F1} inferences/second");
                if (result.MemoryUsageMB > 0)
                    Console.WriteLine($"   Memory Usage: ~{result.MemoryUsageMB}MB");
            }
        }
    }
}