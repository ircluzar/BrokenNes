using System;
using System.Diagnostics;
using System.IO;
using NesRomPatcher;

class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("=" + new string('=', 68));
        Console.WriteLine("🎮 NES ROM HOLE RECONSTRUCTION SYSTEM");
        Console.WriteLine("=" + new string('=', 68));
        Console.WriteLine("⚡ Updated with Transformer-based bidirectional span reconstruction");
        Console.WriteLine("🔥 NOW WITH NATIVE C# INFERENCE - NO PYTHON REQUIRED!");
        Console.WriteLine();

        string romsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "roms");
        string prgDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "prg");
        string onnxDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "onnx_export");

        // Check if extraction has been done
        if (!Directory.Exists(prgDir) || Directory.GetFiles(prgDir, "*.prg").Length == 0)
        {
            Console.WriteLine("📁 PRG directory is empty. Running ROM extraction...");
            if (Directory.Exists(romsDir))
            {
                Extractor.ExtractAllNesFiles(romsDir);
                Console.WriteLine("✅ ROM extraction completed.");
            }
            else
            {
                Console.WriteLine($"⚠️  ROM directory not found: {romsDir}");
                Console.WriteLine("Please place .nes files in the roms/ directory and run again.");
                return;
            }
        }
        else
        {
            var prgFiles = Directory.GetFiles(prgDir, "*.prg");
            Console.WriteLine($"📚 Found {prgFiles.Length} PRG files ready for training.");
        }

        Console.WriteLine();
        Console.WriteLine("🚀 AVAILABLE WORKFLOWS:");
        Console.WriteLine();
        
        // Check if we have ONNX models for C# inference
        string onnxModelPath = Path.Combine(onnxDir, "6502_span_predictor.onnx");
        string onnxConfigPath = Path.Combine(onnxDir, "6502_span_predictor_config.json");
        bool hasOnnxModel = File.Exists(onnxModelPath) && File.Exists(onnxConfigPath);

        if (hasOnnxModel)
        {
            Console.WriteLine("🔥 C# NATIVE INFERENCE (Recommended - No Python!):");
            Console.WriteLine("   • Patch ROMs directly in C# using ONNX model");
            Console.WriteLine("   • Fast inference without Python/CUDA dependencies");
            Console.WriteLine("   • Native .NET performance");
            Console.WriteLine();
        }

        Console.WriteLine("🐍 PYTHON TRAINING & VALIDATION:");
        Console.WriteLine("1. Train the neural network:");
        Console.WriteLine("   python train_6502_predictor.py");
        Console.WriteLine();
        Console.WriteLine("2. Export model for C# inference:");
        Console.WriteLine("   python export_to_onnx.py");
        Console.WriteLine();
        Console.WriteLine("3. Validate the trained model:");
        Console.WriteLine("   python validate_patcher.py --num-roms 10 --holes-per-rom 3");
        Console.WriteLine();
        Console.WriteLine("4. Patch using Python (if you prefer):");
        Console.WriteLine("   python patch_rom.py damaged.prg fixed.prg 0x1000 0x1020");
        Console.WriteLine();

        // Show menu options
        Console.WriteLine("🎯 CHOOSE YOUR WORKFLOW:");
        
        if (hasOnnxModel)
        {
            Console.WriteLine("A. 🔥 Use C# native inference (patch ROM now!)");
            Console.WriteLine("B. 🧪 Test C# inference with sample data");
            Console.WriteLine("C. 🏁 Benchmark C# vs Python performance");
        }
        
        Console.WriteLine("D. 🤖 Train new model with Python");
        Console.WriteLine("E. 📤 Export existing model to ONNX for C# use");
        Console.WriteLine("F. 🔍 Analyze PyTorch weights (for pure C# implementation)");
        Console.WriteLine("G. ℹ️  Show system info and exit");
        Console.WriteLine();

        Console.Write("Your choice: ");
        var choice = Console.ReadLine()?.ToUpper();

        switch (choice)
        {
            case "A" when hasOnnxModel:
                RunCSharpInference(onnxModelPath, onnxConfigPath);
                break;
                
            case "B" when hasOnnxModel:
                TestCSharpInference(onnxModelPath, onnxConfigPath);
                break;
                
            case "C" when hasOnnxModel:
                RunBenchmark();
                break;
                
            case "D":
                RunPythonTraining();
                break;
                
            case "E":
                ExportToOnnx();
                break;
                
            case "F":
                AnalyzePytorchWeights();
                break;
                
            case "G":
            default:
                ShowSystemInfo(hasOnnxModel, onnxModelPath, onnxConfigPath);
                break;
        }

        Console.WriteLine();
        Console.WriteLine("🏁 Program completed. Press any key to exit...");
        Console.ReadKey();
    }

    static void RunCSharpInference(string onnxModelPath, string onnxConfigPath)
    {
        Console.WriteLine();
        Console.WriteLine("🔥 STARTING C# NATIVE INFERENCE");
        Console.WriteLine("=" + new string('=', 50));

        try
        {
            using var patcher = new CSharpRomPatcher(onnxModelPath, onnxConfigPath);

            // Get ROM file to patch
            Console.Write("📂 Enter path to ROM file with holes: ");
            var inputRom = Console.ReadLine()?.Trim();
            
            if (string.IsNullOrEmpty(inputRom) || !File.Exists(inputRom))
            {
                Console.WriteLine("❌ Invalid ROM file path. Using demo mode...");
                
                // Create a demo ROM with a hole for testing
                var demoRom = CreateDemoRomWithHole();
                var demoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "demo_damaged.prg");
                File.WriteAllBytes(demoPath, demoRom.romData);
                
                Console.WriteLine($"📝 Created demo ROM: {demoPath}");
                Console.WriteLine($"🕳️ Hole at positions {demoRom.holeStart}-{demoRom.holeEnd}");
                
                inputRom = demoPath;
            }

            // Get hole positions
            Console.Write("🎯 Enter hole start position (hex, e.g., 0x1000): ");
            var startInput = Console.ReadLine()?.Trim();
            if (!TryParseHex(startInput, out int holeStart))
            {
                Console.WriteLine("❌ Invalid start position. Using demo values...");
                holeStart = CreateDemoRomWithHole().holeStart;
            }

            Console.Write("🎯 Enter hole end position (hex, e.g., 0x1010): ");
            var endInput = Console.ReadLine()?.Trim();
            if (!TryParseHex(endInput, out int holeEnd) || holeEnd <= holeStart)
            {
                Console.WriteLine("❌ Invalid end position. Using demo values...");
                holeEnd = CreateDemoRomWithHole().holeEnd;
            }

            // Get output path
            var outputRom = Path.ChangeExtension(inputRom, ".patched.prg");
            Console.WriteLine($"💾 Output will be saved to: {outputRom}");

            // Perform patching
            var result = patcher.PatchRomFile(inputRom, outputRom, holeStart, holeEnd, 
                temperature: 0.3f, topK: 30);

            Console.WriteLine();
            Console.WriteLine("🎉 C# INFERENCE COMPLETED!");
            Console.WriteLine($"   📊 {result.PredictedBytes.Length} bytes predicted");
            Console.WriteLine($"   🎯 Average confidence: {result.AverageConfidence:P1}");
            Console.WriteLine($"   📁 Patched ROM saved to: {outputRom}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ C# inference failed: {ex.Message}");
            Console.WriteLine("💡 Make sure the Microsoft.ML.OnnxRuntime package is installed");
            Console.WriteLine("💡 Install: dotnet add package Microsoft.ML.OnnxRuntime");
        }
    }

    static void TestCSharpInference(string onnxModelPath, string onnxConfigPath)
    {
        Console.WriteLine();
        Console.WriteLine("🧪 TESTING C# INFERENCE");
        Console.WriteLine("=" + new string('=', 50));

        try
        {
            using var patcher = new CSharpRomPatcher(onnxModelPath, onnxConfigPath);
            
            // Look for test data
            var testDataPath = Path.Combine("onnx_export", "test_data.json");
            
            if (File.Exists(testDataPath))
            {
                patcher.RunTest(testDataPath);
            }
            else
            {
                Console.WriteLine($"❌ Test data not found: {testDataPath}");
                Console.WriteLine("💡 Run 'python export_to_onnx.py' to generate test data");
                
                // Create simple test
                Console.WriteLine("\n🔧 Running simple functionality test...");
                var testRom = new byte[256];
                for (int i = 0; i < 256; i++) testRom[i] = (byte)i;
                
                var result = patcher.PatchHole(testRom, 100, 110, temperature: 0.1f);
                Console.WriteLine($"✅ Simple test completed: {result.PredictedBytes.Length} bytes predicted");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Test failed: {ex.Message}");
        }
    }

    static void RunBenchmark()
    {
        Console.WriteLine();
        Console.WriteLine("🏁 STARTING BENCHMARK");
        Console.WriteLine("=" + new string('=', 50));
        Console.Write("🎯 Number of inference runs (default 10): ");
        var runsInput = Console.ReadLine()?.Trim();
        var numRuns = int.TryParse(runsInput, out var runs) ? runs : 10;
        
        InferenceBenchmark.RunBenchmark(numRuns);
    }

    static void RunPythonTraining()
    {
        Console.WriteLine();
        Console.WriteLine("🐍 STARTING PYTHON TRAINING");
        Console.WriteLine("=" + new string('=', 50));
        Console.WriteLine("💡 This will take 1-2 hours depending on your hardware.");
        Console.WriteLine();

        try
        {
            var processInfo = new ProcessStartInfo
            {
                FileName = "python",
                Arguments = "train_6502_predictor.py",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = false
            };

            using (var process = Process.Start(processInfo))
            {
                process.OutputDataReceived += (sender, e) => {
                    if (!string.IsNullOrEmpty(e.Data))
                        Console.WriteLine(e.Data);
                };
                
                process.ErrorDataReceived += (sender, e) => {
                    if (!string.IsNullOrEmpty(e.Data))
                        Console.WriteLine($"ERROR: {e.Data}");
                };

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();

                if (process.ExitCode == 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("✅ Training completed successfully!");
                    Console.WriteLine("🎯 Next: Run 'E' to export the model for C# inference");
                }
                else
                {
                    Console.WriteLine();
                    Console.WriteLine($"❌ Training failed with exit code: {process.ExitCode}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error starting Python training: {ex.Message}");
            Console.WriteLine("💡 Ensure Python and PyTorch are installed");
        }
    }

    static void ExportToOnnx()
    {
        Console.WriteLine();
        Console.WriteLine("📤 EXPORTING MODEL TO ONNX");
        Console.WriteLine("=" + new string('=', 50));

        try
        {
            var processInfo = new ProcessStartInfo
            {
                FileName = "python",
                Arguments = "export_to_onnx.py",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = false
            };

            using (var process = Process.Start(processInfo))
            {
                process.OutputDataReceived += (sender, e) => {
                    if (!string.IsNullOrEmpty(e.Data))
                        Console.WriteLine(e.Data);
                };
                
                process.ErrorDataReceived += (sender, e) => {
                    if (!string.IsNullOrEmpty(e.Data))
                        Console.WriteLine($"ERROR: {e.Data}");
                };

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();

                if (process.ExitCode == 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("✅ ONNX export completed successfully!");
                    Console.WriteLine("🔥 You can now use C# native inference!");
                }
                else
                {
                    Console.WriteLine();
                    Console.WriteLine($"❌ Export failed with exit code: {process.ExitCode}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error exporting to ONNX: {ex.Message}");
            Console.WriteLine("💡 Ensure Python and PyTorch are installed");
        }
    }

    static void AnalyzePytorchWeights()
    {
        Console.WriteLine();
        Console.WriteLine("🔍 PYTORCH WEIGHTS ANALYSIS");
        Console.WriteLine("=" + new string('=', 50));
        
        var weightsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "model_weights.json");
        PytorchWeightLoader.AnalyzeModelWeights(weightsPath);
        
        Console.WriteLine();
        Console.Write("🏗️ Generate pure C# implementation template? (y/N): ");
        var response = Console.ReadLine()?.ToLower();
        if (response == "y" || response == "yes")
        {
            PytorchWeightLoader.GeneratePureCSharpTemplate();
        }
    }

    static void ShowSystemInfo(bool hasOnnxModel, string onnxModelPath, string onnxConfigPath)
    {
        Console.WriteLine();
        Console.WriteLine("ℹ️  SYSTEM INFORMATION");
        Console.WriteLine("=" + new string('=', 50));
        
        Console.WriteLine($"🎯 C# Native Inference: {(hasOnnxModel ? "✅ Available" : "❌ Not Available")}");
        if (hasOnnxModel)
        {
            var modelSize = new FileInfo(onnxModelPath).Length / (1024.0 * 1024.0);
            Console.WriteLine($"   📁 ONNX Model: {Path.GetFileName(onnxModelPath)} ({modelSize:F2} MB)");
            Console.WriteLine($"   ⚙️ Config: {Path.GetFileName(onnxConfigPath)}");
        }
        else
        {
            Console.WriteLine("   💡 Train a model first, then export with option E");
        }

        // Check for trained PyTorch models
        var pytorchModels = new[] { "6502_span_predictor_best.pt", "6502_span_predictor.pt" };
        Console.WriteLine($"\n🐍 PyTorch Models:");
        foreach (var model in pytorchModels)
        {
            if (File.Exists(model))
            {
                var size = new FileInfo(model).Length / (1024.0 * 1024.0);
                Console.WriteLine($"   ✅ {model} ({size:F2} MB)");
            }
            else
            {
                Console.WriteLine($"   ❌ {model} (not found)");
            }
        }

        // Check for PRG files
        string prgDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "prg");
        if (Directory.Exists(prgDir))
        {
            var prgFiles = Directory.GetFiles(prgDir, "*.prg");
            Console.WriteLine($"\n📚 Training Data: {prgFiles.Length} PRG files found");
        }
        else
        {
            Console.WriteLine($"\n📚 Training Data: No PRG files found");
        }

        Console.WriteLine($"\n📖 For detailed instructions, see:");
        Console.WriteLine($"   • README.md - Python training and validation");
        Console.WriteLine($"   • CSharp_Inference_Guide.md - C# native inference");
    }

    static (byte[] romData, int holeStart, int holeEnd) CreateDemoRomWithHole()
    {
        // Create a demo ROM with predictable patterns
        var rom = new byte[1024];
        
        // Fill with incrementing pattern
        for (int i = 0; i < rom.Length; i++)
        {
            rom[i] = (byte)(i % 256);
        }

        // Create hole
        int holeStart = 512;
        int holeEnd = 520;
        
        for (int i = holeStart; i < holeEnd; i++)
        {
            rom[i] = 0x00; // Zero out the hole
        }

        return (rom, holeStart, holeEnd);
    }

    static bool TryParseHex(string input, out int value)
    {
        value = 0;
        if (string.IsNullOrEmpty(input)) return false;
        
        input = input.Trim();
        if (input.StartsWith("0x") || input.StartsWith("0X"))
            input = input.Substring(2);
            
        return int.TryParse(input, System.Globalization.NumberStyles.HexNumber, null, out value);
    }
}
