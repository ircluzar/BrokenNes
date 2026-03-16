using System;
using System.Threading.Tasks;
using System.Linq;

#if BLAZOR_WASM
using Microsoft.JSInterop;
#endif

namespace NesRomPatcher.Wasm
{
    /// <summary>
    /// WebAssembly-compatible Transformer implementation for NES ROM hole reconstruction
    /// This replaces the ONNX-based approach for mobile/web deployment
    /// </summary>
    public class WasmTransformerPredictor : IDisposable
    {
        private readonly ModelConfiguration _config;
        private readonly float[,] _tokenEmbeddings;      // [vocab_size, embed_size]
        private readonly float[,] _positionalEncoding;   // [seq_len, embed_size]
        private readonly WasmTransformerLayer[] _layers;
        private readonly float[,] _outputProjection;     // [embed_size, vocab_size]
        private readonly float[] _outputBias;            // [vocab_size]

#if BLAZOR_WASM
        private readonly IJSRuntime _jsRuntime;
        private readonly IWebGLAccelerator _webglAccelerator;
#endif

        private bool _disposed = false;

        public WasmTransformerPredictor(ModelConfiguration config
#if BLAZOR_WASM
            , IJSRuntime jsRuntime = null
#endif
        )
        {
            _config = config;

#if BLAZOR_WASM
            _jsRuntime = jsRuntime;
            _webglAccelerator = new WebGLAccelerator(_jsRuntime);
#endif

            // Initialize model components
            _tokenEmbeddings = new float[config.VocabSize, config.EmbedSize];
            _positionalEncoding = new float[config.SeqLen, config.EmbedSize];
            _outputProjection = new float[config.EmbedSize, config.VocabSize];
            _outputBias = new float[config.VocabSize];
            
            _layers = new WasmTransformerLayer[config.NumLayers];
            for (int i = 0; i < config.NumLayers; i++)
            {
                _layers[i] = new WasmTransformerLayer(config
#if BLAZOR_WASM
                    , _webglAccelerator
#endif
                );
            }
        }

        /// <summary>
        /// Load model weights from extracted PyTorch state dict
        /// </summary>
        public async Task LoadWeightsAsync(ModelWeights weights)
        {
            Console.WriteLine("?? Loading model weights for WASM inference...");
            
            // Load token embeddings
            Array.Copy(weights.TokenEmbeddings, _tokenEmbeddings, weights.TokenEmbeddings.Length);
            
            // Generate positional encodings (these aren't usually saved, we compute them)
            GeneratePositionalEncodings();
            
            // Load transformer layers
            for (int i = 0; i < _layers.Length; i++)
            {
                await _layers[i].LoadWeightsAsync(weights.LayerWeights[i]);
            }
            
            // Load output projection
            Array.Copy(weights.OutputProjectionWeight, _outputProjection, weights.OutputProjectionWeight.Length);
            Array.Copy(weights.OutputProjectionBias, _outputBias, weights.OutputProjectionBias.Length);
            
            Console.WriteLine("? Model weights loaded successfully");
        }

        /// <summary>
        /// Main inference method - predicts bytes for a masked span
        /// </summary>
        public async Task<PredictionResult> PredictSpanAsync(
            int[] inputSequence, 
            int holeStartRel, 
            int holeEndRel,
            float temperature = 0.5f,
            int? topK = 50)
        {
            // Input validation
            if (inputSequence.Length != _config.SeqLen)
                throw new ArgumentException($"Input sequence must be {_config.SeqLen} tokens");

            // 1. Token embedding lookup
            var embeddings = await TokenEmbeddingAsync(inputSequence);
            
            // 2. Add positional encoding
            var withPositions = AddPositionalEncoding(embeddings);
            
            // 3. Pass through transformer layers
            var encoded = withPositions;
            for (int i = 0; i < _layers.Length; i++)
            {
                encoded = await _layers[i].ForwardAsync(encoded);
                
                // Optional: Progress callback for UI
#if BLAZOR_WASM
                if (_jsRuntime != null && i % 2 == 0)
                {
                    await _jsRuntime.InvokeVoidAsync("updateProgress", $"Layer {i + 1}/{_layers.Length}");
                }
#endif
            }
            
            // 4. Output projection to vocabulary logits
            var logits = await OutputProjectionAsync(encoded);
            
            // 5. Extract predictions for hole region and sample
            return await SamplePredictionsAsync(logits, holeStartRel, holeEndRel, temperature, topK);
        }

        /// <summary>
        /// Token embedding lookup - convert token IDs to dense vectors
        /// </summary>
        private async Task<float[,]> TokenEmbeddingAsync(int[] tokens)
        {
            var seqLen = tokens.Length;
            var embedSize = _config.EmbedSize;
            var result = new float[seqLen, embedSize];

#if BLAZOR_WASM
            // Use WebGL for parallel embedding lookup if available
            if (_webglAccelerator?.IsAvailable == true)
            {
                return await _webglAccelerator.EmbeddingLookupAsync(_tokenEmbeddings, tokens);
            }
#endif

            // Fallback to CPU implementation
            for (int i = 0; i < seqLen; i++)
            {
                var tokenId = Math.Min(Math.Max(tokens[i], 0), _config.VocabSize - 1);
                for (int j = 0; j < embedSize; j++)
                {
                    result[i, j] = _tokenEmbeddings[tokenId, j];
                }
            }

            return result;
        }

        /// <summary>
        /// Add positional encoding to embeddings
        /// </summary>
        private float[,] AddPositionalEncoding(float[,] embeddings)
        {
            var seqLen = embeddings.GetLength(0);
            var embedSize = embeddings.GetLength(1);
            var result = new float[seqLen, embedSize];

            for (int i = 0; i < seqLen; i++)
            {
                for (int j = 0; j < embedSize; j++)
                {
                    result[i, j] = embeddings[i, j] + _positionalEncoding[i, j];
                }
            }

            return result;
        }

        /// <summary>
        /// Output projection from hidden states to vocabulary logits
        /// </summary>
        private async Task<float[,]> OutputProjectionAsync(float[,] hiddenStates)
        {
            var seqLen = hiddenStates.GetLength(0);
            var hiddenSize = hiddenStates.GetLength(1);
            var vocabSize = _config.VocabSize;

#if BLAZOR_WASM
            // Use WebGL for matrix multiplication if available
            if (_webglAccelerator?.IsAvailable == true)
            {
                var logits = await _webglAccelerator.MatrixMultiplyAsync(hiddenStates, _outputProjection);
                return await _webglAccelerator.AddBiasAsync(logits, _outputBias);
            }
#endif

            // CPU fallback
            var result = new float[seqLen, vocabSize];
            
            for (int i = 0; i < seqLen; i++)
            {
                for (int j = 0; j < vocabSize; j++)
                {
                    float sum = _outputBias[j];
                    for (int k = 0; k < hiddenSize; k++)
                    {
                        sum += hiddenStates[i, k] * _outputProjection[k, j];
                    }
                    result[i, j] = sum;
                }
            }

            return result;
        }

        /// <summary>
        /// Sample predictions from logits using temperature and top-k
        /// </summary>
        private async Task<PredictionResult> SamplePredictionsAsync(
            float[,] logits, int holeStartRel, int holeEndRel, 
            float temperature, int? topK)
        {
            var holeSize = holeEndRel - holeStartRel;
            var predictedBytes = new byte[holeSize];
            var confidenceScores = new float[holeSize];
            var random = new Random();

            for (int pos = 0; pos < holeSize; pos++)
            {
                var absolutePos = holeStartRel + pos;
                
                // Extract logits for this position
                var positionLogits = new float[_config.VocabSize];
                for (int i = 0; i < _config.VocabSize; i++)
                {
                    positionLogits[i] = logits[absolutePos, i];
                }

                // Apply temperature scaling and top-k filtering
                var probabilities = await ApplyTemperatureAndTopKAsync(positionLogits, temperature, topK);
                
                // Sample from distribution
                var predictedToken = SampleFromDistribution(probabilities, random);
                
                // Ensure valid byte range
                predictedToken = Math.Min(255, Math.Max(0, predictedToken));
                
                predictedBytes[pos] = (byte)predictedToken;
                confidenceScores[pos] = probabilities[predictedToken];
            }

            return new PredictionResult
            {
                PredictedBytes = predictedBytes,
                ConfidenceScores = confidenceScores
            };
        }

        /// <summary>
        /// Apply temperature scaling and top-k filtering to logits
        /// </summary>
        private async Task<float[]> ApplyTemperatureAndTopKAsync(float[] logits, float temperature, int? topK)
        {
#if BLAZOR_WASM
            // Use WebGL for parallel softmax if available
            if (_webglAccelerator?.IsAvailable == true)
            {
                return await _webglAccelerator.SoftmaxAsync(logits, temperature, topK);
            }
#endif

            // CPU implementation
            var processedLogits = new float[logits.Length];
            Array.Copy(logits, processedLogits, logits.Length);

            // Apply temperature scaling
            if (Math.Abs(temperature - 1.0f) > 1e-6)
            {
                for (int i = 0; i < processedLogits.Length; i++)
                {
                    processedLogits[i] /= temperature;
                }
            }

            // Apply top-k filtering
            if (topK.HasValue && topK.Value < processedLogits.Length)
            {
                var indexed = processedLogits
                    .Select((value, index) => new { Value = value, Index = index })
                    .OrderByDescending(x => x.Value)
                    .ToArray();

                var threshold = indexed[topK.Value - 1].Value;
                
                for (int i = 0; i < processedLogits.Length; i++)
                {
                    if (processedLogits[i] < threshold)
                    {
                        processedLogits[i] = float.NegativeInfinity;
                    }
                }
            }

            // Softmax
            return Softmax(processedLogits);
        }

        /// <summary>
        /// CPU implementation of softmax
        /// </summary>
        private static float[] Softmax(float[] logits)
        {
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
        private static int SampleFromDistribution(float[] probabilities, Random random)
        {
            var randValue = (float)random.NextDouble();
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
        /// Generate positional encodings using sinusoidal patterns
        /// </summary>
        private void GeneratePositionalEncodings()
        {
            var seqLen = _config.SeqLen;
            var embedSize = _config.EmbedSize;

            for (int pos = 0; pos < seqLen; pos++)
            {
                for (int i = 0; i < embedSize; i++)
                {
                    var angle = pos / Math.Pow(10000.0, (2.0 * i) / embedSize);
                    
                    if (i % 2 == 0)
                        _positionalEncoding[pos, i] = (float)Math.Sin(angle);
                    else
                        _positionalEncoding[pos, i] = (float)Math.Cos(angle);
                }
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
#if BLAZOR_WASM
                _webglAccelerator?.Dispose();
#endif
                _disposed = true;
            }
        }
    }

    /// <summary>
    /// Configuration for the WASM Transformer model
    /// </summary>
    public class ModelConfiguration
    {
        public int SeqLen { get; set; } = 128;
        public int VocabSize { get; set; } = 257;
        public int EmbedSize { get; set; } = 256;
        public int HiddenSize { get; set; } = 512;
        public int NumHeads { get; set; } = 8;
        public int NumLayers { get; set; } = 6;
        public int MaskToken { get; set; } = 256;
    }

    /// <summary>
    /// Model weights loaded from PyTorch state dict
    /// </summary>
    public class ModelWeights
    {
        public float[] TokenEmbeddings { get; set; }
        public LayerWeights[] LayerWeights { get; set; }
        public float[] OutputProjectionWeight { get; set; }
        public float[] OutputProjectionBias { get; set; }
    }

    public class LayerWeights
    {
        public float[] AttentionQueryWeight { get; set; }
        public float[] AttentionKeyWeight { get; set; }
        public float[] AttentionValueWeight { get; set; }
        public float[] AttentionOutputWeight { get; set; }
        public float[] AttentionQueryBias { get; set; }
        public float[] AttentionKeyBias { get; set; }
        public float[] AttentionValueBias { get; set; }
        public float[] AttentionOutputBias { get; set; }
        public float[] FeedForwardWeight1 { get; set; }
        public float[] FeedForwardBias1 { get; set; }
        public float[] FeedForwardWeight2 { get; set; }
        public float[] FeedForwardBias2 { get; set; }
        public float[] LayerNorm1Weight { get; set; }
        public float[] LayerNorm1Bias { get; set; }
        public float[] LayerNorm2Weight { get; set; }
        public float[] LayerNorm2Bias { get; set; }
    }

    /// <summary>
    /// Individual transformer layer implementation
    /// </summary>
    public class WasmTransformerLayer
    {
        private readonly ModelConfiguration _config;
        private LayerWeights _weights;

#if BLAZOR_WASM
        private readonly IWebGLAccelerator _webglAccelerator;
#endif

        public WasmTransformerLayer(ModelConfiguration config
#if BLAZOR_WASM
            , IWebGLAccelerator webglAccelerator = null
#endif
        )
        {
            _config = config;
#if BLAZOR_WASM
            _webglAccelerator = webglAccelerator;
#endif
        }

        public async Task LoadWeightsAsync(LayerWeights weights)
        {
            _weights = weights;
        }

        public async Task<float[,]> ForwardAsync(float[,] input)
        {
            // Self-attention with residual connection
            var attentionOutput = await MultiHeadSelfAttentionAsync(input);
            var postAttention = await LayerNormAndResidualAsync(input, attentionOutput, _weights.LayerNorm1Weight, _weights.LayerNorm1Bias);

            // Feed-forward with residual connection
            var ffOutput = await FeedForwardAsync(postAttention);
            var output = await LayerNormAndResidualAsync(postAttention, ffOutput, _weights.LayerNorm2Weight, _weights.LayerNorm2Bias);

            return output;
        }

        private async Task<float[,]> MultiHeadSelfAttentionAsync(float[,] input)
        {
            // This is the most complex part - multi-head attention computation
            // Implementation would include:
            // 1. Q, K, V projections
            // 2. Multi-head splitting
            // 3. Attention computation (Q * K^T / sqrt(d_k))
            // 4. Softmax
            // 5. Attention * V
            // 6. Concatenation and output projection

#if BLAZOR_WASM
            if (_webglAccelerator?.IsAvailable == true)
            {
                return await _webglAccelerator.MultiHeadAttentionAsync(
                    input, _weights, _config.NumHeads, _config.EmbedSize / _config.NumHeads);
            }
#endif

            // CPU fallback implementation
            return await MultiHeadAttentionCPUAsync(input);
        }

        private async Task<float[,]> MultiHeadAttentionCPUAsync(float[,] input)
        {
            var seqLen = input.GetLength(0);
            var embedSize = input.GetLength(1);
            var numHeads = _config.NumHeads;
            var headDim = embedSize / numHeads;
            
            // 1. Compute Q, K, V projections
            var queries = MatrixMultiplyCPU(input, ReshapeWeights(_weights.AttentionQueryWeight, embedSize, embedSize));
            var keys = MatrixMultiplyCPU(input, ReshapeWeights(_weights.AttentionKeyWeight, embedSize, embedSize));
            var values = MatrixMultiplyCPU(input, ReshapeWeights(_weights.AttentionValueWeight, embedSize, embedSize));
            
            // Add biases
            AddBiasCPU(queries, _weights.AttentionQueryBias);
            AddBiasCPU(keys, _weights.AttentionKeyBias);
            AddBiasCPU(values, _weights.AttentionValueBias);
            
            // 2. Split into multiple heads and compute attention
            var attentionOutputs = new float[numHeads][,];
            
            for (int head = 0; head < numHeads; head++)
            {
                var startIdx = head * headDim;
                var endIdx = startIdx + headDim;
                
                // Extract head-specific Q, K, V
                var qHead = ExtractHeadFeatures(queries, startIdx, headDim);
                var kHead = ExtractHeadFeatures(keys, startIdx, headDim);
                var vHead = ExtractHeadFeatures(values, startIdx, headDim);
                
                // Compute attention scores: Q * K^T / sqrt(d_k)
                var attentionScores = MatrixMultiplyCPU(qHead, TransposeCPU(kHead));
                var scale = 1.0f / (float)Math.Sqrt(headDim);
                ScaleMatrixCPU(attentionScores, scale);
                
                // Apply softmax
                var attentionWeights = SoftmaxMatrixCPU(attentionScores);
                
                // Apply attention to values: Attention * V
                attentionOutputs[head] = MatrixMultiplyCPU(attentionWeights, vHead);
            }
            
            // 3. Concatenate heads
            var concatenated = ConcatenateHeads(attentionOutputs);
            
            // 4. Output projection
            var output = MatrixMultiplyCPU(concatenated, ReshapeWeights(_weights.AttentionOutputWeight, embedSize, embedSize));
            AddBiasCPU(output, _weights.AttentionOutputBias);
            
            return output;
        }

        private async Task<float[,]> FeedForwardAsync(float[,] input)
        {
#if BLAZOR_WASM
            if (_webglAccelerator?.IsAvailable == true)
            {
                return await _webglAccelerator.FeedForwardAsync(input, _weights);
            }
#endif

            // CPU implementation: Linear -> GELU -> Linear
            return await FeedForwardCPUAsync(input);
        }

        private async Task<float[,]> FeedForwardCPUAsync(float[,] input)
        {
            var seqLen = input.GetLength(0);
            var embedSize = input.GetLength(1);
            var hiddenSize = _config.HiddenSize;
            
            // First linear transformation
            var hidden = MatrixMultiplyCPU(input, ReshapeWeights(_weights.FeedForwardWeight1, embedSize, hiddenSize));
            AddBiasCPU(hidden, _weights.FeedForwardBias1);
            
            // GELU activation
            ApplyGELUCPU(hidden);
            
            // Second linear transformation
            var output = MatrixMultiplyCPU(hidden, ReshapeWeights(_weights.FeedForwardWeight2, hiddenSize, embedSize));
            AddBiasCPU(output, _weights.FeedForwardBias2);
            
            return output;
        }

        private async Task<float[,]> LayerNormAndResidualAsync(float[,] input, float[,] output, float[] weight, float[] bias)
        {
            var seqLen = input.GetLength(0);
            var embedSize = input.GetLength(1);
            var result = new float[seqLen, embedSize];
            
            // Add residual connection
            for (int i = 0; i < seqLen; i++)
            {
                for (int j = 0; j < embedSize; j++)
                {
                    result[i, j] = input[i, j] + output[i, j];
                }
            }
            
            // Apply layer normalization
            for (int i = 0; i < seqLen; i++)
            {
                // Compute mean
                float mean = 0f;
                for (int j = 0; j < embedSize; j++)
                {
                    mean += result[i, j];
                }
                mean /= embedSize;
                
                // Compute variance
                float variance = 0f;
                for (int j = 0; j < embedSize; j++)
                {
                    var diff = result[i, j] - mean;
                    variance += diff * diff;
                }
                variance /= embedSize;
                
                // Normalize and apply learned parameters
                var std = (float)Math.Sqrt(variance + 1e-12); // Add epsilon for numerical stability
                for (int j = 0; j < embedSize; j++)
                {
                    result[i, j] = (result[i, j] - mean) / std * weight[j] + bias[j];
                }
            }
            
            return result;
        }

        // Helper methods for CPU operations
        private static float[,] MatrixMultiplyCPU(float[,] a, float[,] b)
        {
            var aRows = a.GetLength(0);
            var aCols = a.GetLength(1);
            var bCols = b.GetLength(1);
            var result = new float[aRows, bCols];
            
            for (int i = 0; i < aRows; i++)
            {
                for (int j = 0; j < bCols; j++)
                {
                    float sum = 0f;
                    for (int k = 0; k < aCols; k++)
                    {
                        sum += a[i, k] * b[k, j];
                    }
                    result[i, j] = sum;
                }
            }
            
            return result;
        }
        
        private static void AddBiasCPU(float[,] matrix, float[] bias)
        {
            var rows = matrix.GetLength(0);
            var cols = matrix.GetLength(1);
            
            for (int i = 0; i < rows; i++)
            {
                for (int j = 0; j < cols; j++)
                {
                    matrix[i, j] += bias[j];
                }
            }
        }
        
        private static float[,] ReshapeWeights(float[] weights, int rows, int cols)
        {
            var result = new float[rows, cols];
            for (int i = 0; i < rows; i++)
            {
                for (int j = 0; j < cols; j++)
                {
                    result[i, j] = weights[i * cols + j];
                }
            }
            return result;
        }
        
        private static float[,] ExtractHeadFeatures(float[,] matrix, int startIdx, int headDim)
        {
            var rows = matrix.GetLength(0);
            var result = new float[rows, headDim];
            
            for (int i = 0; i < rows; i++)
            {
                for (int j = 0; j < headDim; j++)
                {
                    result[i, j] = matrix[i, startIdx + j];
                }
            }
            
            return result;
        }
        
        private static float[,] TransposeCPU(float[,] matrix)
        {
            var rows = matrix.GetLength(0);
            var cols = matrix.GetLength(1);
            var result = new float[cols, rows];
            
            for (int i = 0; i < rows; i++)
            {
                for (int j = 0; j < cols; j++)
                {
                    result[j, i] = matrix[i, j];
                }
            }
            
            return result;
        }
        
        private static void ScaleMatrixCPU(float[,] matrix, float scale)
        {
            var rows = matrix.GetLength(0);
            var cols = matrix.GetLength(1);
            
            for (int i = 0; i < rows; i++)
            {
                for (int j = 0; j < cols; j++)
                {
                    matrix[i, j] *= scale;
                }
            }
        }
        
        private static float[,] SoftmaxMatrixCPU(float[,] matrix)
        {
            var rows = matrix.GetLength(0);
            var cols = matrix.GetLength(1);
            var result = new float[rows, cols];
            
            for (int i = 0; i < rows; i++)
            {
                // Find max for numerical stability
                float maxVal = float.NegativeInfinity;
                for (int j = 0; j < cols; j++)
                {
                    maxVal = Math.Max(maxVal, matrix[i, j]);
                }
                
                // Compute exponentials and sum
                float sum = 0f;
                for (int j = 0; j < cols; j++)
                {
                    result[i, j] = (float)Math.Exp(matrix[i, j] - maxVal);
                    sum += result[i, j];
                }
                
                // Normalize
                for (int j = 0; j < cols; j++)
                {
                    result[i, j] /= sum;
                }
            }
            
            return result;
        }
        
        private static float[,] ConcatenateHeads(float[][,] heads)
        {
            if (heads.Length == 0) throw new ArgumentException("No heads to concatenate");
            
            var seqLen = heads[0].GetLength(0);
            var headDim = heads[0].GetLength(1);
            var totalDim = heads.Length * headDim;
            var result = new float[seqLen, totalDim];
            
            for (int i = 0; i < seqLen; i++)
            {
                for (int head = 0; head < heads.Length; head++)
                {
                    for (int j = 0; j < headDim; j++)
                    {
                        result[i, head * headDim + j] = heads[head][i, j];
                    }
                }
            }
            
            return result;
        }
        
        private static void ApplyGELUCPU(float[,] matrix)
        {
            var rows = matrix.GetLength(0);
            var cols = matrix.GetLength(1);
            
            for (int i = 0; i < rows; i++)
            {
                for (int j = 0; j < cols; j++)
                {
                    var x = matrix[i, j];
                    // GELU approximation: 0.5 * x * (1 + tanh(sqrt(2/?) * (x + 0.044715 * x^3)))
                    var inner = Math.Sqrt(2.0 / Math.PI) * (x + 0.044715 * x * x * x);
                    matrix[i, j] = 0.5f * x * (1.0f + (float)Math.Tanh(inner));
                }
            }
        }
    }

#if BLAZOR_WASM
    /// <summary>
    /// WebGL acceleration interface for GPU-accelerated operations
    /// </summary>
    public interface IWebGLAccelerator : IDisposable
    {
        bool IsAvailable { get; }
        
        Task<float[,]> MatrixMultiplyAsync(float[,] a, float[,] b);
        Task<float[,]> EmbeddingLookupAsync(float[,] embeddings, int[] indices);
        Task<float[,]> AddBiasAsync(float[,] input, float[] bias);
        Task<float[]> SoftmaxAsync(float[] logits, float temperature = 1.0f, int? topK = null);
        Task<float[,]> MultiHeadAttentionAsync(float[,] input, LayerWeights weights, int numHeads, int headDim);
        Task<float[,]> FeedForwardAsync(float[,] input, LayerWeights weights);
    }

    /// <summary>
    /// WebGL acceleration implementation using JavaScript interop
    /// </summary>
    public class WebGLAccelerator : IWebGLAccelerator
    {
        private readonly IJSRuntime _jsRuntime;
        private bool _isAvailable;
        private bool _disposed = false;

        public bool IsAvailable => _isAvailable;

        public WebGLAccelerator(IJSRuntime jsRuntime)
        {
            _jsRuntime = jsRuntime;
            // Initialize WebGL context in JavaScript
            _ = InitializeAsync();
        }

        private async Task InitializeAsync()
        {
            try
            {
                _isAvailable = await _jsRuntime.InvokeAsync<bool>("initializeWebGLAccelerator");
            }
            catch
            {
                _isAvailable = false;
            }
        }

        public async Task<float[,]> MatrixMultiplyAsync(float[,] a, float[,] b)
        {
            if (!_isAvailable) throw new InvalidOperationException("WebGL not available");
            
            var result = await _jsRuntime.InvokeAsync<float[]>("webglMatrixMultiply", 
                FlattenMatrix(a), FlattenMatrix(b), 
                a.GetLength(0), a.GetLength(1), b.GetLength(1));
                
            return ReshapeMatrix(result, a.GetLength(0), b.GetLength(1));
        }

        public async Task<float[,]> EmbeddingLookupAsync(float[,] embeddings, int[] indices)
        {
            if (!_isAvailable) throw new InvalidOperationException("WebGL not available");
            
            var result = await _jsRuntime.InvokeAsync<float[]>("webglEmbeddingLookup",
                FlattenMatrix(embeddings), indices, embeddings.GetLength(1));
                
            return ReshapeMatrix(result, indices.Length, embeddings.GetLength(1));
        }

        public async Task<float[,]> AddBiasAsync(float[,] input, float[] bias)
        {
            if (!_isAvailable) throw new InvalidOperationException("WebGL not available");
            
            var result = await _jsRuntime.InvokeAsync<float[]>("webglAddBias",
                FlattenMatrix(input), bias, input.GetLength(0), input.GetLength(1));
                
            return ReshapeMatrix(result, input.GetLength(0), input.GetLength(1));
        }

        public async Task<float[]> SoftmaxAsync(float[] logits, float temperature = 1.0f, int? topK = null)
        {
            if (!_isAvailable) throw new InvalidOperationException("WebGL not available");
            
            return await _jsRuntime.InvokeAsync<float[]>("webglSoftmax", logits, temperature, topK);
        }

        public async Task<float[,]> MultiHeadAttentionAsync(float[,] input, LayerWeights weights, int numHeads, int headDim)
        {
            if (!_isAvailable) throw new InvalidOperationException("WebGL not available");
            
            // This would be a complex WebGL compute shader implementation
            throw new NotImplementedException("WebGL multi-head attention not yet implemented");
        }

        public async Task<float[,]> FeedForwardAsync(float[,] input, LayerWeights weights)
        {
            if (!_isAvailable) throw new InvalidOperationException("WebGL not available");
            
            // WebGL implementation of feed-forward network
            throw new NotImplementedException("WebGL feed-forward not yet implemented");
        }

        private static float[] FlattenMatrix(float[,] matrix)
        {
            var rows = matrix.GetLength(0);
            var cols = matrix.GetLength(1);
            var result = new float[rows * cols];
            
            for (int i = 0; i < rows; i++)
                for (int j = 0; j < cols; j++)
                    result[i * cols + j] = matrix[i, j];
                    
            return result;
        }

        private static float[,] ReshapeMatrix(float[] array, int rows, int cols)
        {
            var result = new float[rows, cols];
            
            for (int i = 0; i < rows; i++)
                for (int j = 0; j < cols; j++)
                    result[i, j] = array[i * cols + j];
                    
            return result;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                if (_jsRuntime != null)
                {
                    _ = _jsRuntime.InvokeVoidAsync("disposeWebGLAccelerator");
                }
                _disposed = true;
            }
        }
    }
#endif
}