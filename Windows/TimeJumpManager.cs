using System;
using System.Collections.Generic;
using System.Linq;
using NesEmulator;

namespace BrokenNes.Windows
{
    /// <summary>
    /// Manages TimeJump functionality using BlobSearcher for similar state detection.
    /// Stores NES RAM (2KB) as blobs with full savestates as payloads.
    /// </summary>
    public class TimeJumpManager
    {
        private const int NES_RAM_SIZE = 2048; // 2KB NES main RAM
        private const int TOP_K_RESULTS = 5; // Number of similar states to query
        private const int QUERY_MULTIPLIER = 10; // Query more to account for burned states
        private const double MIN_SIMILARITY_THRESHOLD = 0.3; // Minimum similarity for evaporation (30%)
        
        private readonly BlobSearcher _blobSearcher;
        private readonly Random _random;
        
        // Track burned/used states that should not be returned in future queries
        private readonly HashSet<string> _burnedStates;
        
        // Track if a capture is currently in progress to prevent concurrent requests
        private volatile bool _isCapturing;
        
        /// <summary>
        /// Gets the total number of states stored (including burned states)
        /// </summary>
        public int TotalStatesStored { get; private set; }
        
        /// <summary>
        /// Gets the number of available (non-burned) states
        /// </summary>
        public int AvailableStatesCount => TotalStatesStored - _burnedStates.Count;

        public TimeJumpManager()
        {
            _blobSearcher = new BlobSearcher(NES_RAM_SIZE);
            _random = new Random();
            _burnedStates = new HashSet<string>();
            _isCapturing = false;
            TotalStatesStored = 0;
        }

        /// <summary>
        /// Captures the current state atomically and adds it to the blob searcher.
        /// The blob is the NES RAM (2KB), and the payload is the full savestate JSON.
        /// DEPRECATED: Use CaptureStateAsync() instead for atomic frame-boundary capture.
        /// </summary>
        /// <param name="nes">NES emulator instance</param>
        /// <returns>Tuple of (hash, thumbnailBase64), or null if capture failed</returns>
        [Obsolete("Use CaptureStateAsync() for atomic frame-boundary capture to prevent desync")]
        public (string hash, string thumbnail)? CaptureState(NES nes)
        {
            if (nes == null)
            {
                Console.WriteLine("[TimeJump] Cannot capture state: NES is null");
                return null;
            }

            try
            {
                // Get the full savestate (WARNING: May capture mid-frame, causing potential desync)
                string savestateJson = nes.SaveState();
                if (string.IsNullOrEmpty(savestateJson))
                {
                    Console.WriteLine("[TimeJump] SaveState returned empty");
                    return null;
                }

                // Extract RAM as the blob (2KB) using the public API
                byte[] ram = new byte[NES_RAM_SIZE];
                for (int i = 0; i < NES_RAM_SIZE; i++)
                {
                    ram[i] = nes.PeekSystemRam(i);
                }

                // Convert savestate JSON to bytes for storage
                byte[] savestateBytes = System.Text.Encoding.UTF8.GetBytes(savestateJson);

                // Compute hash before adding
                string stateHash = ComputeStateHash(savestateBytes);

                // Capture thumbnail of current frame
                string thumbnailBase64 = CaptureThumbnail(nes);

                // Add to blob searcher
                _blobSearcher.Add(ram, savestateBytes);
                TotalStatesStored++;

                Console.WriteLine($"[TimeJump] State captured successfully. Hash: {stateHash.Substring(0, 8)}..., Total states: {TotalStatesStored}, Available: {AvailableStatesCount}");
                return (stateHash, thumbnailBase64);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TimeJump] Failed to capture state: {ex.Message}");
                return null;
            }
        }
        
        /// <summary>
        /// Request atomic capture of the current state at the next frame boundary.
        /// This is the RECOMMENDED method for passive background recording as it prevents
        /// subsystem desync by capturing all components at a synchronized moment.
        /// Uses a TaskCompletionSource to return the result asynchronously.
        /// </summary>
        /// <param name="nes">NES emulator instance</param>
        /// <returns>Task that completes with (hash, thumbnailBase64) or null if failed</returns>
        public System.Threading.Tasks.Task<(string hash, string thumbnail)?> CaptureStateAsync(NES nes)
        {
            if (nes == null)
            {
                Console.WriteLine("[TimeJump] Cannot capture state: NES is null");
                return System.Threading.Tasks.Task.FromResult<(string, string)?>(null);
            }

            // Prevent concurrent captures - if already capturing, return null immediately
            if (_isCapturing)
            {
                Console.WriteLine("[TimeJump] Skipping capture - previous capture still in progress");
                return System.Threading.Tasks.Task.FromResult<(string, string)?>(null);
            }

            _isCapturing = true;
            var tcs = new System.Threading.Tasks.TaskCompletionSource<(string, string)?>();
            
            try
            {
                // Request atomic snapshot at next frame boundary
                nes.RequestAtomicSnapshot(savestateJson =>
                {
                    try
                    {
                        if (string.IsNullOrEmpty(savestateJson))
                        {
                            Console.WriteLine("[TimeJump] Atomic snapshot returned empty");
                            tcs.SetResult(null);
                            return;
                        }

                        // Extract RAM as the blob (2KB) using the public API
                        byte[] ram = new byte[NES_RAM_SIZE];
                        for (int i = 0; i < NES_RAM_SIZE; i++)
                        {
                            ram[i] = nes.PeekSystemRam(i);
                        }

                        // Convert savestate JSON to bytes for storage
                        byte[] savestateBytes = System.Text.Encoding.UTF8.GetBytes(savestateJson);

                        // Compute hash before adding
                        string stateHash = ComputeStateHash(savestateBytes);

                        // Capture thumbnail of current frame
                        string thumbnailBase64 = CaptureThumbnail(nes);

                        // Add to blob searcher
                        _blobSearcher.Add(ram, savestateBytes);
                        TotalStatesStored++;

                        Console.WriteLine($"[TimeJump] Atomic state captured. Hash: {stateHash.Substring(0, 8)}..., Total: {TotalStatesStored}, Available: {AvailableStatesCount}");
                        tcs.SetResult((stateHash, thumbnailBase64));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[TimeJump] Failed to process atomic snapshot: {ex.Message}");
                        tcs.SetException(ex);
                    }
                    finally
                    {
                        _isCapturing = false;
                    }
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TimeJump] Failed to request atomic snapshot: {ex.Message}");
                _isCapturing = false;
                tcs.SetException(ex);
            }

            return tcs.Task;
        }

        /// <summary>
        /// Captures a thumbnail of the current NES frame as a base64 PNG
        /// </summary>
        private string CaptureThumbnail(NES nes)
        {
            try
            {
                byte[] frameBuffer = nes.GetFrameBuffer();
                if (frameBuffer == null || frameBuffer.Length == 0)
                {
                    return string.Empty;
                }

                // NES frame buffer is 256x240, BGRA format (4 bytes per pixel)
                const int width = 256;
                const int height = 240;
                const int bytesPerPixel = 4;

                // Convert BGRA to ARGB by swapping R and B channels
                byte[] convertedBuffer = new byte[frameBuffer.Length];
                for (int i = 0; i < frameBuffer.Length; i += bytesPerPixel)
                {
                    convertedBuffer[i + 0] = frameBuffer[i + 2]; // R from B
                    convertedBuffer[i + 1] = frameBuffer[i + 1]; // G stays
                    convertedBuffer[i + 2] = frameBuffer[i + 0]; // B from R
                    convertedBuffer[i + 3] = frameBuffer[i + 3]; // A stays
                }

                using (var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    var bitmapData = bitmap.LockBits(
                        new System.Drawing.Rectangle(0, 0, width, height),
                        System.Drawing.Imaging.ImageLockMode.WriteOnly,
                        System.Drawing.Imaging.PixelFormat.Format32bppArgb);

                    System.Runtime.InteropServices.Marshal.Copy(convertedBuffer, 0, bitmapData.Scan0, convertedBuffer.Length);
                    bitmap.UnlockBits(bitmapData);

                    // Convert to PNG and encode as base64
                    using (var ms = new System.IO.MemoryStream())
                    {
                        bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                        byte[] imageBytes = ms.ToArray();
                        return Convert.ToBase64String(imageBytes);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TimeJump] Failed to capture thumbnail: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// Performs a TimeJump with a specific query state: uses that state's blob to find top 3 similar states,
        /// picks a random one to load, and burns the top 8 states.
        /// </summary>
        /// <param name="nes">NES emulator instance</param>
        /// <param name="queryStateHash">Hash of the state to use as query</param>
        /// <returns>Tuple of (loadedHash, burnedHashes) or null if query failed</returns>
        public (string loadedHash, List<string> burnedHashes)? QueryState(NES nes, string queryStateHash)
        {
            if (nes == null)
            {
                Console.WriteLine("[TimeJump] Cannot query: NES is null");
                return null;
            }

            try
            {
                // Find the state with matching hash to use as query blob
                byte[]? queryBlob = null;
                var allResults = _blobSearcher.FindTopSimilar(new byte[NES_RAM_SIZE], TotalStatesStored);
                
                foreach (var result in allResults)
                {
                    string hash = ComputeStateHash(result.AuxData);
                    if (hash == queryStateHash)
                    {
                        queryBlob = result.Blob;
                        break;
                    }
                }

                if (queryBlob == null)
                {
                    Console.WriteLine($"[TimeJump] Query state not found: {queryStateHash.Substring(0, 8)}...");
                    return null;
                }

                Console.WriteLine($"[TimeJump] Querying with state hash: {queryStateHash.Substring(0, 8)}...");

                // Search using the clicked state's blob, query more than needed for burning
                int queryCount = Math.Min(8 * QUERY_MULTIPLIER, TotalStatesStored);
                var results = _blobSearcher.FindTopSimilar(queryBlob, queryCount);
                
                Console.WriteLine($"[TimeJump] Query returned {results?.Length ?? 0} results");
                
                if (results == null || results.Length == 0)
                {
                    Console.WriteLine("[TimeJump] No states found for query");
                    return null;
                }

                // Filter out burned states
                var availableResults = results
                    .Where(r => !IsStateBurned(r.AuxData))
                    .ToArray();
                
                Console.WriteLine($"[TimeJump] After filtering burned: {availableResults.Length} available");

                if (availableResults.Length < 3)
                {
                    Console.WriteLine("[TimeJump] Not enough available states for query (need at least 3)");
                    return null;
                }

                // Take top 3 for loading selection
                var top3 = availableResults.Take(3).ToArray();
                
                // Take top 4 for burning
                var top4ToBurn = availableResults.Take(4).ToArray();

                // Pick random from top 3
                var selectedResult = top3[_random.Next(top3.Length)];

                // Convert auxData back to JSON string
                string savestateJson = System.Text.Encoding.UTF8.GetString(selectedResult.AuxData);

                // Load the selected state
                nes.LoadState(savestateJson);

                // Get hash of loaded state
                string loadedHash = ComputeStateHash(selectedResult.AuxData);

                // Burn the top 4
                var burnedHashes = new List<string>();
                foreach (var result in top4ToBurn)
                {
                    string hash = ComputeStateHash(result.AuxData);
                    BurnState(result.AuxData);
                    burnedHashes.Add(hash);
                }

                Console.WriteLine($"[TimeJump] Query successful! Loaded random from top 3 (similarity {selectedResult.SimilarityScore:F3}). Burned {top4ToBurn.Length} states.");
                Console.WriteLine($"[TimeJump] Remaining available states: {AvailableStatesCount}");
                
                return (loadedHash, burnedHashes);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TimeJump] Failed to perform query: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Performs a TimeJump: searches for similar states, picks a random one from top 5,
        /// loads it, and burns all queried results so they can't be used again.
        /// </summary>
        /// <param name="nes">NES emulator instance</param>
        /// <returns>Tuple of (loadedHash, burnedHashes) or null if jump failed</returns>
        public (string loadedHash, List<string> burnedHashes)? Jump(NES nes)
        {
            if (nes == null)
            {
                Console.WriteLine("[TimeJump] Cannot jump: NES is null");
                return null;
            }

            try
            {
                // Get current RAM for search using the public API
                byte[] currentRam = new byte[NES_RAM_SIZE];
                for (int i = 0; i < NES_RAM_SIZE; i++)
                {
                    currentRam[i] = nes.PeekSystemRam(i);
                }

                // Search for similar states - query more than needed to account for burned states
                int queryCount = Math.Min(TOP_K_RESULTS * QUERY_MULTIPLIER, TotalStatesStored);
                var results = _blobSearcher.FindTopSimilar(currentRam, queryCount);
                
                Console.WriteLine($"[TimeJump] Query returned {results?.Length ?? 0} results from {TotalStatesStored} total states");
                
                if (results == null || results.Length == 0)
                {
                    Console.WriteLine("[TimeJump] No states found to jump to - BlobSearcher returned empty");
                    return null;
                }

                // Filter out burned states and take top K available
                var availableResults = results
                    .Where(r => !IsStateBurned(r.AuxData))
                    .Take(TOP_K_RESULTS)
                    .ToArray();
                
                Console.WriteLine($"[TimeJump] After filtering burned: {availableResults.Length} available (burned count: {_burnedStates.Count})");

                if (availableResults.Length == 0)
                {
                    Console.WriteLine("[TimeJump] All similar states have been burned");
                    return null;
                }

                // Pick a random state from available results
                var selectedResult = availableResults[_random.Next(availableResults.Length)];

                // Convert auxData back to JSON string
                string savestateJson = System.Text.Encoding.UTF8.GetString(selectedResult.AuxData);

                // Load the selected state
                nes.LoadState(savestateJson);

                // Get hash of loaded state
                string loadedHash = ComputeStateHash(selectedResult.AuxData);

                // Burn only the top 3 available results
                var top3ToBurn = availableResults.Take(3).ToArray();
                var burnedHashes = new List<string>();
                foreach (var result in top3ToBurn)
                {
                    string hash = ComputeStateHash(result.AuxData);
                    BurnState(result.AuxData);
                    burnedHashes.Add(hash);
                }

                Console.WriteLine($"[TimeJump] Jump successful! Loaded state with similarity {selectedResult.SimilarityScore:F3}. Burned {top3ToBurn.Length} states.");
                Console.WriteLine($"[TimeJump] Remaining available states: {AvailableStatesCount}");
                
                return (loadedHash, burnedHashes);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TimeJump] Failed to perform jump: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Checks if a state has been burned (used in a previous jump)
        /// </summary>
        private bool IsStateBurned(byte[] savestateBytes)
        {
            string hash = ComputeStateHash(savestateBytes);
            return _burnedStates.Contains(hash);
        }

        /// <summary>
        /// Burns a state so it won't be used in future jumps
        /// </summary>
        private void BurnState(byte[] savestateBytes)
        {
            string hash = ComputeStateHash(savestateBytes);
            _burnedStates.Add(hash);
        }

        /// <summary>
        /// Computes a hash for a savestate to track burned states
        /// </summary>
        private string ComputeStateHash(byte[] savestateBytes)
        {
            using (var sha256 = System.Security.Cryptography.SHA256.Create())
            {
                byte[] hashBytes = sha256.ComputeHash(savestateBytes);
                return Convert.ToBase64String(hashBytes);
            }
        }

        /// <summary>
        /// Gets statistics about the TimeJump system
        /// </summary>
        public TimeJumpStats GetStats()
        {
            return new TimeJumpStats
            {
                TotalStatesStored = TotalStatesStored,
                AvailableStates = AvailableStatesCount,
                BurnedStates = _burnedStates.Count
            };
        }

        /// <summary>
        /// Clears all stored states and resets the system
        /// </summary>
        public void Reset()
        {
            // Clear the BlobSearcher index
            _blobSearcher.Clear();
            
            // Clear the burned states list
            _burnedStates.Clear();
            
            // Reset the counter
            TotalStatesStored = 0;
            
            Console.WriteLine("[TimeJump] Reset: cleared BlobSearcher index, burned states, and reset counters");
        }
    }

    /// <summary>
    /// Statistics about the TimeJump system
    /// </summary>
    public class TimeJumpStats
    {
        public int TotalStatesStored { get; set; }
        public int AvailableStates { get; set; }
        public int BurnedStates { get; set; }
    }
}
