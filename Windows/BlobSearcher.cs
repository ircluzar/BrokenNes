using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

public class BlobSearcher
{
    private readonly int _blobSize;
    private readonly int _signatureBits = 64;
    
    // The projection matrix: 64 vectors of random weights (-1 or +1)
    // Used to project the high-dim blob into 64-bit space.
    private readonly sbyte[][] _projectionMatrix;
    
    // Storage for our indexed blobs
    private readonly List<BlobEntry> _index;

    // Thread-safe lock for adding items
    private readonly object _lock = new object();

    public BlobSearcher(int blobSize)
    {
        if (blobSize <= 0) throw new ArgumentException("Size must be > 0");
        _blobSize = blobSize;
        _index = new List<BlobEntry>();

        // 1. Pre-calculate the projection matrix (Random Hyperplanes)
        // This is a one-time setup cost.
        var rnd = new Random(1337); // Fixed seed for reproducibility
        _projectionMatrix = new sbyte[_signatureBits][];
        
        for (int i = 0; i < _signatureBits; i++)
        {
            _projectionMatrix[i] = new sbyte[_blobSize];
            for (int j = 0; j < _blobSize; j++)
            {
                // Assign random weights of -1 or 1
                _projectionMatrix[i][j] = rnd.Next(0, 2) == 0 ? (sbyte)-1 : (sbyte)1;
            }
        }
    }

    /// <summary>
    /// Adds a blob and its auxiliary data to the search index.
    /// The similarity vector (Signature) is pre-cached immediately.
    /// </summary>
    public void Add(byte[] blob, byte[] auxData)
    {
        if (blob.Length != _blobSize) 
            throw new ArgumentException($"Blob size mismatch. Expected {_blobSize}, got {blob.Length}");

        // Pre-cache the similarity vector (Signature)
        ulong signature = ComputeSignature(blob);

        var entry = new BlobEntry
        {
            Blob = blob,
            AuxData = auxData,
            Signature = signature
        };

        lock (_lock)
        {
            _index.Add(entry);
        }
    }

    /// <summary>
    /// Queries the index for the Top K most similar blobs.
    /// Uses Hamming Distance on the signatures for extreme speed.
    /// </summary>
    public SearchResult[] FindTopSimilar(byte[] queryBlob, int topK)
    {
        if (queryBlob.Length != _blobSize) 
            throw new ArgumentException($"Blob size mismatch.");

        // 1. Compute signature for the query
        ulong querySig = ComputeSignature(queryBlob);

        // 2. Perform a linear scan over the cached signatures.
        // For < 10 million items, a linear scan of ulongs is faster 
        // than maintaining a complex tree structure due to CPU cache locality.
        // We use a tuple to store (Distance, Index) to avoid copying objects.
        var candidates = new (int distance, int index)[_index.Count];

        // Parallel loop for speed on large datasets
        Parallel.For(0, _index.Count, i =>
        {
            // Hamming Distance: XOR the signatures, count the 1s.
            ulong xor = _index[i].Signature ^ querySig;
            int dist = BitOperations.PopCount(xor);
            candidates[i] = (dist, i);
        });

        // 3. Sort by lowest distance and take Top K
        // Using LINQ here for brevity, but a PriorityQueue would be faster for massive K.
        var topHits = candidates
            .OrderBy(x => x.distance)
            .Take(topK);

        // 4. Map back to full results
        return topHits.Select(h => new SearchResult
        {
            Blob = _index[h.index].Blob,
            AuxData = _index[h.index].AuxData,
            SimilarityScore = 1.0 - (h.distance / 64.0) // Normalize 0..1 (1 is identical)
        }).ToArray();
    }

    /// <summary>
    /// Clears all indexed blobs from the search index.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _index.Clear();
        }
    }

    /// <summary>
    /// Projects the 2KB blob onto 64 random vectors to generate a 64-bit fingerprint.
    /// Positive dot product = 1, Negative = 0.
    /// </summary>
    private ulong ComputeSignature(byte[] blob)
    {
        ulong signature = 0;

        // For every bit in our 64-bit signature...
        for (int i = 0; i < _signatureBits; i++)
        {
            int dotProduct = 0;
            sbyte[] vector = _projectionMatrix[i];

            // Calculate Dot Product (Vector . Blob)
            // Optimization: Unrolling this loop or using SIMD (Vector<T>) 
            // would make this 10x faster, but keeping it simple for now.
            for (int j = 0; j < _blobSize; j++)
            {
                // We treat byte as unsigned 0-255. 
                // Using the pre-computed -1/+1 weights.
                dotProduct += blob[j] * vector[j];
            }

            if (dotProduct > 0)
            {
                signature |= (1UL << i);
            }
        }
        return signature;
    }

    // Inner struct to hold data compactly
    private struct BlobEntry
    {
        public ulong Signature; // The pre-cached "Vector"
        public byte[] Blob;
        public byte[] AuxData;
    }

    public class SearchResult
    {
        public byte[] Blob { get; set; }
        public byte[] AuxData { get; set; }
        public double SimilarityScore { get; set; } // 1.0 = Exact Match, 0.0 = Opposite
    }
}