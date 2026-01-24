using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using BrokenNes;
using BrokenNes.CorruptorModels;
using NesEmulator;
using NesEmulator.Shaders;
using BrokenNes.Windows.Rendering;
using BrokenNes.Windows.WebApi;
using Microsoft.Web.WebView2.WinForms;

namespace BrokenNes.Windows
{
    public partial class MainForm
    {
        // Core emulation fields
        private NES? nes;
        private Thread? emulatorThread;
        private volatile bool isEmulationRunning;
        private volatile bool isPaused;
        private readonly object emulationLock = new object();
        
        // Rendering fields
        private NesDirectXRenderer dxRenderer;
        private Panel displayPanel;
        private DirectBitmap? frameBuffer;
        private DirectBitmap? backBuffer; // Double buffering
        private bool useDirectX = true;
        
        // Audio fields
        private AudioManager? audioManager;
        
        // ROM and configuration
        private string currentRomPath = string.Empty;
        private EmulatorConfig config = new EmulatorConfig();
        
        // State management
        private string? quickSaveState; // Quick save slot
        
        // Input fields
        private InputManager? inputManager;
        private InputManager? inputManager2; // Player 2 input
        private Dictionary<Keys, int> keyMap = new();
        
        // FPS tracking for audio speed adjustment
        private double currentFps = 60.0;
        private int fpsFrameCount = 0;
        private System.Diagnostics.Stopwatch? fpsStopwatch;
        
        // Speed control
        private SpeedControlForm? speedControlForm;
        private volatile float speedOverride = 1.0f;
        private volatile bool hasSpeedOverride = false;
        private volatile bool resetTimingAccumulator = false;
        
        // Auto-scramble cores testing
        private System.Windows.Forms.Timer? autoScrambleTimer;
        private Random scrambleRandom = new Random();
        
        // Continue feature
        private PictureBox? continueButton;

        // Menu items
        private ToolStripMenuItem shaderMenu;
        private ToolStripMenuItem apuMenu;
        private ToolStripMenuItem cpuMenu;
        private ToolStripMenuItem ppuMenu;
        private ToolStripMenuItem recentRomsMenu;
        private ToolStripMenuItem webModulesMenu;
        
        // NES display dimensions
        private const int NES_WIDTH = 256;
        private const int NES_HEIGHT = 240;

        // Corruptor + Imagine
        private readonly Corruptor corruptor = new();
        private readonly object corruptorLock = new();
        private ImagineEngine? imagineEngine;
        private readonly ConcurrentQueue<Action> emulationActions = new();
        public event Action? CorruptorStateChanged;
        
        // Fullscreen support
        private bool isFullscreen = false;
        private FormBorderStyle previousBorderStyle;
        private FormWindowState previousWindowState;
        private Rectangle previousBounds;
        
        // WebView2 and view modes
        private WebView2? webView;
        private ViewMode currentViewMode = ViewMode.Emulator;
        private bool isWebViewInitialized = false;
        private WebModuleInfo? currentToolOrActivityModule = null;
        
        // Web API server
        private WebApiServer? webApiServer;
        private readonly System.Threading.SemaphoreSlim webApiServerLock = new System.Threading.SemaphoreSlim(1, 1);
    }
}
