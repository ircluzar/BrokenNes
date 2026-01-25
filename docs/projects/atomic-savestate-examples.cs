// Example: How to use Atomic Savestate Capture in Windows Forms

// ==============================================================================
// Example 1: Background Auto-Save (Recommended for periodic saves)
// ==============================================================================

private System.Windows.Forms.Timer? autoSaveTimer;
private bool autoSaveEnabled = false;

private void EnableBackgroundAutoSave(int intervalSeconds = 60)
{
    autoSaveEnabled = true;
    
    if (autoSaveTimer == null)
    {
        autoSaveTimer = new System.Windows.Forms.Timer();
        autoSaveTimer.Interval = intervalSeconds * 1000;
        autoSaveTimer.Tick += (s, e) =>
        {
            if (nes != null && !isPaused && autoSaveEnabled)
            {
                // Request atomic snapshot - no hitching!
                nes.RequestAtomicSnapshot(savestateJson =>
                {
                    try
                    {
                        // Save to disk or memory
                        File.WriteAllText("autosave.state", savestateJson);
                        Console.WriteLine($"[AutoSave] State saved ({savestateJson.Length} bytes)");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[AutoSave] Failed: {ex.Message}");
                    }
                });
            }
        };
    }
    
    autoSaveTimer.Start();
}

// ==============================================================================
// Example 2: Quick Save Slot (No hitching - instant response)
// ==============================================================================

private void QuickSave_Click(object sender, EventArgs e)
{
    if (nes == null) return;
    
    // Request atomic snapshot
    nes.RequestAtomicSnapshot(savestateJson =>
    {
        try
        {
            // Extend state with UI settings if needed
            string extendedState = ExtendStateWithUISettings(savestateJson);
            
            // Save to slot
            File.WriteAllText("quicksave.state", extendedState);
            
            // Update UI on main thread
            if (InvokeRequired)
            {
                Invoke(() => UpdateStatusText("Quick save complete"));
            }
            else
            {
                UpdateStatusText("Quick save complete");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[QuickSave] Error: {ex.Message}");
        }
    });
    
    // UI responds immediately - save happens at next frame boundary
    UpdateStatusText("Saving...");
}

// ==============================================================================
// Example 3: TimeJump Integration (Passive background recording)
// ==============================================================================

private TimeJumpManager? timeJumpManager;
private System.Windows.Forms.Timer? timeJumpRecorder;

private void EnableTimeJumpRecording(int captureIntervalFrames = 180) // Every 3 seconds at 60fps
{
    if (timeJumpManager == null)
    {
        timeJumpManager = new TimeJumpManager();
    }
    
    if (timeJumpRecorder == null)
    {
        timeJumpRecorder = new System.Windows.Forms.Timer();
        timeJumpRecorder.Interval = (captureIntervalFrames * 1000) / 60; // Convert frames to ms
        timeJumpRecorder.Tick += async (s, e) =>
        {
            if (nes != null && !isPaused)
            {
                try
                {
                    // Async capture with zero hitching
                    var result = await timeJumpManager.CaptureStateAsync(nes);
                    
                    if (result != null)
                    {
                        var stats = timeJumpManager.GetStats();
                        Console.WriteLine($"[TimeJump] State captured. Total: {stats.TotalStatesStored}, Available: {stats.AvailableStates}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[TimeJump] Capture failed: {ex.Message}");
                }
            }
        };
    }
    
    timeJumpRecorder.Start();
}

// ==============================================================================
// Example 4: Compare Old vs New Approach
// ==============================================================================

// ❌ OLD WAY (May cause desync - NOT RECOMMENDED)
private void OldWay_SaveState()
{
    bool wasPaused = isPaused;
    isPaused = true; // Pause causes audio dropout and visual hitch
    
    string stateJson;
    lock (emulationLock) // Lock blocks emulation thread
    {
        stateJson = nes.SaveState(); // Mid-frame capture risk
    }
    
    isPaused = wasPaused; // Resume causes another hitch
    
    // Process state...
}

// ✅ NEW WAY (Atomic, zero hitching - RECOMMENDED)
private void NewWay_SaveState()
{
    // No pause needed - request happens immediately
    nes.RequestAtomicSnapshot(stateJson =>
    {
        // This callback runs at frame boundary on emulation thread
        // All subsystems are synchronized
        
        // Process state (save to disk, send to network, etc.)
        ProcessState(stateJson);
    });
    
    // UI remains responsive - no blocking, no hitching
}

// ==============================================================================
// Example 5: Polling-based Approach (Alternative to callbacks)
// ==============================================================================

private void PollingBased_Example()
{
    // Request snapshot without callback
    nes.RequestAtomicSnapshot(null);
    
    // Later in a timer or update loop:
    if (nes.HasPendingSnapshot())
    {
        string snapshot = nes.GetPendingSnapshot();
        if (snapshot != null)
        {
            ProcessState(snapshot);
        }
    }
}

// ==============================================================================
// Example 6: Error Handling
// ==============================================================================

private void SaveStateWithErrorHandling()
{
    if (nes == null)
    {
        MessageBox.Show("No emulator instance", "Error");
        return;
    }
    
    nes.RequestAtomicSnapshot(savestateJson =>
    {
        try
        {
            if (string.IsNullOrEmpty(savestateJson))
            {
                throw new InvalidOperationException("Snapshot returned empty");
            }
            
            // Validate state has expected structure
            if (!savestateJson.Contains("\"cpu\"") || !savestateJson.Contains("\"ppu\""))
            {
                throw new InvalidOperationException("Invalid state structure");
            }
            
            // Save with atomic write
            string tempFile = "savestate.tmp";
            string finalFile = "savestate.json";
            
            File.WriteAllText(tempFile, savestateJson);
            File.Move(tempFile, finalFile, overwrite: true);
            
            UpdateStatus("Save complete");
        }
        catch (Exception ex)
        {
            if (InvokeRequired)
            {
                Invoke(() => MessageBox.Show($"Save failed: {ex.Message}", "Error"));
            }
            else
            {
                MessageBox.Show($"Save failed: {ex.Message}", "Error");
            }
        }
    });
}
