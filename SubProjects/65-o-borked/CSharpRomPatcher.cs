using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace NesRomPatcher
{
    /// <summary>
    /// Configuration for the 6502 span predictor model
    /// </summary>
    public class ModelConfig
    {
        public int seq_len { get; set; }
        public int vocab_size { get; set; }
        public int embed_size { get; set; }
        public int hidden_size { get; set; }
        public int num_heads { get; set; }
        public int num_layers { get; set; }
        public float dropout { get; set; }
        public int mask_token { get; set; }
        public float model_accuracy { get; set; }
        public float best_accuracy { get; set; }
        public string export_timestamp { get; set; }
        public string pytorch_version { get; set; }
    }

    /// <summary>
    /// Prediction result for a span of bytes
    /// </summary>
    public class PredictionResult
    {
        public byte[] PredictedBytes { get; set; }
        public float[] ConfidenceScores { get; set; }
        public float AverageConfidence => ConfidenceScores?.Average() ?? 0f;
        public float MinConfidence => ConfidenceScores?.Min() ?? 0f;
        public float MaxConfidence => ConfidenceScores?.Max() ?? 0f;
    }

    /// <summary>
    /// C# implementation of NES ROM hole reconstruction using ONNX inference
    /// </summary>
    public class CSharpRomPatcher : IDisposable
    {
        private readonly InferenceSession _session;
        private readonly ModelConfig _config;
        private readonly Random _random;
        private bool _disposed = false;

        public ModelConfig Config => _config;

        public CSharpRomPatcher(string onnxModelPath, string configPath)
        {
            Console.WriteLine("?? Initializing C# ROM Patcher with ONNX");
            Console.WriteLine($"?? Model: {onnxModelPath}");
            Console.WriteLine($"??  Config: {configPath}");

            // Load configuration
            if (!File.Exists(configPath))
                throw new FileNotFoundException($"Configuration file not found: {configPath}");

            var configJson = File.ReadAllText(configPath);
            _config = JsonSerializer.Deserialize<ModelConfig>(configJson);

            Console.WriteLine($"?? Model Configuration:");
            Console.WriteLine($"   • Sequence Length: {_config.seq_len}");
            Console.WriteLine($"   • Vocabulary Size: {_config.vocab_size}");
            Console.WriteLine($"   • Embedding Size: {_config.embed_size}");
            Console.WriteLine($"   • Hidden Size: {_config.hidden_size}");
            Console.WriteLine($"   • Transformer Heads: {_config.num_heads}");
            Console.WriteLine($"   • Transformer Layers: {_config.num_layers}");
            Console.WriteLine($"   • Mask Token: {_config.mask_token}");
            Console.WriteLine($"   • Model Accuracy: {_config.model_accuracy:F4}");
            Console.WriteLine($"   • Best Accuracy: {_config.best_accuracy:F4}");

            // Initialize ONNX Runtime session
            if (!File.Exists(onnxModelPath))
                throw new FileNotFoundException($"ONNX model file not found: {onnxModelPath}");

            var sessionOptions = new SessionOptions();
            
            // Try to use GPU if available, fallback to CPU
            try
            {
                sessionOptions.AppendExecutionProvider_CUDA();
                Console.WriteLine("?? Attempting to use CUDA for inference...");
            }
            catch
            {
                Console.WriteLine("?? Using CPU for inference (CUDA not available)");
            }

            _session = new InferenceSession(onnxModelPath, sessionOptions);
            
            var fileSize = new FileInfo(onnxModelPath).Length / (1024.0 * 1024.0);
            Console.WriteLine($"? ONNX model loaded successfully! ({fileSize:F2} MB)");

            // Print input/output info
            Console.WriteLine($"?? Model Inputs:");
            foreach (var input in _session.InputMetadata)
            {
                var shape = string.Join(", ", input.Value.Dimensions);
                Console.WriteLine($"   ?? {input.Key}: {input.Value.ElementType} [{shape}]");
            }

            Console.WriteLine($"?? Model Outputs:");
            foreach (var output in _session.OutputMetadata)
            {
                var shape = string.Join(", ", output.Value.Dimensions);
                Console.WriteLine($"   ?? {output.Key}: {output.Value.ElementType} [{shape}]");
            }

            _random = new Random();
        }

        /// <summary>
        /// Prepare a sequence with masked hole for prediction
        /// </summary>
        private (long[] sequence, int holeStartRel, int holeEndRel) PrepareSequence(
            byte[] romData, int holeStart, int holeEnd)
        {
            var contextSize = _config.seq_len;
            
            // Calculate sequence boundaries
            var seqStart = Math.Max(0, holeStart - (contextSize - (holeEnd - holeStart)) / 2);
            var seqEnd = Math.Min(romData.Length, seqStart + contextSize);

            // Adjust if we hit boundaries
            if (seqEnd - seqStart < contextSize)
            {
                seqStart = Math.Max(0, seqEnd - contextSize);
            }

            // Extract sequence and convert to long array for ONNX
            var sequence = new long[contextSize];
            var actualSeqLen = Math.Min(contextSize, seqEnd - seqStart);
            
            // Copy ROM data to sequence
            for (int i = 0; i < actualSeqLen; i++)
            {
                sequence[i] = romData[seqStart + i];
            }

            // Pad with zeros if needed
            for (int i = actualSeqLen; i < contextSize; i++)
            {
                sequence[i] = 0;
            }

            // Mark hole positions with MASK tokens
            var holeStartRel = Math.Max(0, holeStart - seqStart);
            var holeEndRel = Math.Min(sequence.Length, holeEnd - seqStart);

            for (int i = holeStartRel; i < holeEndRel; i++)
            {
                if (i < sequence.Length)
                {
                    sequence[i] = _config.mask_token;
                }
            }

            return (sequence, holeStartRel, holeEndRel);
        }

        /// <summary>
        /// Apply temperature scaling and top-k filtering to logits
        /// </summary>
        private float[] ApplyTemperatureAndTopK(float[] logits, float temperature = 1.0f, int? topK = null)
        {
            // Apply temperature scaling
            if (Math.Abs(temperature - 1.0f) > 1e-6)
            {
                for (int i = 0; i < logits.Length; i++)
                {
                    logits[i] /= temperature;
                }
            }

            // Apply top-k filtering if specified
            if (topK.HasValue && topK.Value < logits.Length)
            {
                var indexed = logits
                    .Select((value, index) => new { Value = value, Index = index })
                    .OrderByDescending(x => x.Value)
                    .ToArray();

                var threshold = indexed[topK.Value - 1].Value;
                
                for (int i = 0; i < logits.Length; i++)
                {
                    if (logits[i] < threshold)
                    {
                        logits[i] = float.NegativeInfinity;
                    }
                }
            }

            // Convert to probabilities using softmax
            var maxLogit = logits.Max();
            var expSum = 0f;
            var probs = new float[logits.Length];

            for (int i = 0; i < logits.Length; i++)
            {
                probs[i] = (float)Math.Exp(logits[i] - maxLogit);
                expSum += probs[i];
            }

            for (int i = 0; i < probs.Length; i++)
            {
                probs[i] /= expSum;
            }

            return probs;
        }

        /// <summary>
        /// Sample from probability distribution
        /// </summary>
        private int SampleFromDistribution(float[] probabilities, float temperature)
        {
            if (temperature <= 0)
            {
                // Deterministic: return argmax
                return Array.IndexOf(probabilities, probabilities.Max());
            }

            // Stochastic: sample from distribution
            var randValue = (float)_random.NextDouble();
            var cumulative = 0f;

            for (int i = 0; i < probabilities.Length; i++)
            {
                cumulative += probabilities[i];
                if (randValue <= cumulative)
                {
                    return i;
                }
            }

            return probabilities.Length - 1; // Fallback
        }

        /// <summary>
        /// Predict bytes for a masked span using ONNX inference
        /// </summary>
        private PredictionResult PredictSpan(long[] sequence, int holeStartRel, int holeEndRel, 
            float temperature = 0.5f, int? topK = 50)
        {
            // Create input tensor (batch_size=1, seq_len)
            var inputTensor = new DenseTensor<long>(new[] { 1, _config.seq_len });
            for (int i = 0; i < sequence.Length; i++)
            {
                inputTensor[0, i] = sequence[i];
            }

            // Create input for ONNX
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", inputTensor)
            };

            // Run inference
            using var results = _session.Run(inputs);
            var logits = results.First().AsTensor<float>();

            // Extract predictions for hole region
            var holeSize = holeEndRel - holeStartRel;
            var predictedBytes = new byte[holeSize];
            var confidenceScores = new float[holeSize];

            for (int i = 0; i < holeSize; i++)
            {
                var position = holeStartRel + i;
                
                // Extract logits for this position
                var positionLogits = new float[_config.vocab_size];
                for (int j = 0; j < _config.vocab_size; j++)
                {
                    positionLogits[j] = logits[0, position, j];
                }

                // Apply temperature and top-k
                var probabilities = ApplyTemperatureAndTopK(positionLogits, temperature, topK);

                // Sample prediction
                var predictedToken = SampleFromDistribution(probabilities, temperature);
                
                // Ensure predicted token is valid byte (0-255)
                predictedToken = Math.Min(255, Math.Max(0, predictedToken));
                
                predictedBytes[i] = (byte)predictedToken;
                confidenceScores[i] = probabilities[predictedToken];
            }

            return new PredictionResult
            {
                PredictedBytes = predictedBytes,
                ConfidenceScores = confidenceScores
            };
        }

        /// <summary>
        /// Patch a hole in ROM data using forward prediction
        /// </summary>
        public PredictionResult PatchHole(byte[] romData, int holeStart, int holeEnd, 
            float temperature = 0.5f, int? topK = 50)
        {
            Console.WriteLine($"?? Patching hole at positions {holeStart}-{holeEnd} ({holeEnd - holeStart} bytes)");
            Console.WriteLine($"??  Temperature: {temperature}, Top-k: {topK?.ToString() ?? "None"}");

            if (holeStart < 0 || holeEnd > romData.Length || holeStart >= holeEnd)
                throw new ArgumentException($"Invalid hole bounds: {holeStart}-{holeEnd}");

            // Prepare sequence with masked hole
            var (sequence, holeStartRel, holeEndRel) = PrepareSequence(romData, holeStart, holeEnd);

            Console.WriteLine($"?? Sequence prepared: relative hole at {holeStartRel}-{holeEndRel}");

            // Predict the hole contents
            var result = PredictSpan(sequence, holeStartRel, holeEndRel, temperature, topK);

            Console.WriteLine($"? Prediction completed:");
            Console.WriteLine($"   ?? Average confidence: {result.AverageConfidence:F3}");
            Console.WriteLine($"   ?? Min confidence: {result.MinConfidence:F3}");
            Console.WriteLine($"   ?? Max confidence: {result.MaxConfidence:F3}");
            Console.WriteLine($"   ?? Predicted bytes: {string.Join(" ", result.PredictedBytes.Select(b => $"{b:X2}"))}");

            return result;
        }

        /// <summary>
        /// Patch a ROM file and save the result
        /// </summary>
        public PredictionResult PatchRomFile(string inputPath, string outputPath, int holeStart, int holeEnd,
            float temperature = 0.5f, int? topK = 50)
        {
            Console.WriteLine($"?? Patching ROM file: {inputPath}");

            if (!File.Exists(inputPath))
                throw new FileNotFoundException($"Input ROM file not found: {inputPath}");

            // Read ROM data
            var romData = File.ReadAllBytes(inputPath);
            Console.WriteLine($"?? ROM size: {romData.Length:N0} bytes");

            // Show context around hole
            ShowHoleContext(romData, holeStart, holeEnd);

            // Perform patching
            var result = PatchHole(romData, holeStart, holeEnd, temperature, topK);

            // Apply patch to ROM data
            var patchedRom = new byte[romData.Length];
            Array.Copy(romData, patchedRom, romData.Length);

            for (int i = 0; i < result.PredictedBytes.Length; i++)
            {
                patchedRom[holeStart + i] = result.PredictedBytes[i];
            }

            // Create output directory if needed
            var outputDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            // Save patched ROM
            File.WriteAllBytes(outputPath, patchedRom);

            Console.WriteLine($"?? Patched ROM saved to: {outputPath}");
            Console.WriteLine($"? Patching completed successfully!");

            return result;
        }

        /// <summary>
        /// Show context around the hole for debugging
        /// </summary>
        private void ShowHoleContext(byte[] romData, int holeStart, int holeEnd, int contextSize = 32)
        {
            Console.WriteLine($"\n?? Context around hole (±{contextSize} bytes):");
            
            var contextStart = Math.Max(0, holeStart - contextSize);
            var contextEnd = Math.Min(romData.Length, holeEnd + contextSize);

            for (int addr = contextStart; addr < contextEnd; addr += 16)
            {
                var lineEnd = Math.Min(addr + 16, contextEnd);
                var hexParts = new List<string>();
                var asciiParts = new List<char>();

                for (int i = addr; i < lineEnd; i++)
                {
                    if (i >= holeStart && i < holeEnd)
                    {
                        hexParts.Add("??");
                        asciiParts.Add('?');
                    }
                    else
                    {
                        var b = romData[i];
                        hexParts.Add($"{b:X2}");
                        asciiParts.Add(b >= 32 && b <= 126 ? (char)b : '.');
                    }
                }

                var hexStr = string.Join(" ", hexParts).PadRight(47);
                var asciiStr = new string(asciiParts.ToArray());
                Console.WriteLine($"  {addr:X4}: {hexStr} |{asciiStr}|");
            }
            Console.WriteLine();
        }

        /// <summary>
        /// Test the C# implementation against sample data
        /// </summary>
        public void RunTest(string testDataPath)
        {
            Console.WriteLine($"?? Running test against sample data: {testDataPath}");

            if (!File.Exists(testDataPath))
            {
                Console.WriteLine($"? Test data file not found: {testDataPath}");
                return;
            }

            var testJson = File.ReadAllText(testDataPath);
            var testData = JsonSerializer.Deserialize<JsonElement>(testJson);

            var maskedSequence = testData.GetProperty("masked_sequence").EnumerateArray()
                .Select(x => (byte)x.GetInt32()).ToArray();
            var originalSequence = testData.GetProperty("original_sequence").EnumerateArray()
                .Select(x => (byte)x.GetInt32()).ToArray();
            var holeStart = testData.GetProperty("hole_start").GetInt32();
            var holeEnd = testData.GetProperty("hole_end").GetInt32();
            var expectedBytes = testData.GetProperty("expected_bytes").EnumerateArray()
                .Select(x => (byte)x.GetInt32()).ToArray();

            Console.WriteLine($"?? Test data loaded:");
            Console.WriteLine($"   • Sequence length: {maskedSequence.Length}");
            Console.WriteLine($"   • Hole: {holeStart}-{holeEnd} ({holeEnd - holeStart} bytes)");
            Console.WriteLine($"   • Expected bytes: {string.Join(" ", expectedBytes.Select(b => $"{b:X2}"))}");

            // Run prediction
            var result = PatchHole(maskedSequence, holeStart, holeEnd, temperature: 0.1f); // Low temp for deterministic

            Console.WriteLine($"?? Test Results:");
            Console.WriteLine($"   • Predicted: {string.Join(" ", result.PredictedBytes.Select(b => $"{b:X2}"))}");
            Console.WriteLine($"   • Expected:  {string.Join(" ", expectedBytes.Select(b => $"{b:X2}"))}");
            
            var matches = result.PredictedBytes.Zip(expectedBytes, (pred, exp) => pred == exp).Count(x => x);
            var accuracy = (float)matches / expectedBytes.Length;
            
            Console.WriteLine($"   • Accuracy: {accuracy:P1} ({matches}/{expectedBytes.Length} bytes correct)");
            Console.WriteLine($"   • Avg Confidence: {result.AverageConfidence:F3}");
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _session?.Dispose();
                _disposed = true;
            }
        }
    }
}