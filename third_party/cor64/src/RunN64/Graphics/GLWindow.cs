using System.Text;
using System.Globalization;
using System.Threading;

using cor64.IO;
using cor64.RCP;
using GLFW;
using cor64;
using System;
using System.Runtime.InteropServices;
using NLog;
using Veldrid;
using ImGuiNET;
using cor64.Mips.Rsp;
using cor64.PIF;
using RunN64.Workbench.VisualRsp;
using RunN64.Workbench;

namespace RunN64.Graphics
{
    public class GLWindow
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private readonly N64System m_System;
        private readonly NativeWindow m_Window;
        private readonly Video m_VideoInterface;
        private readonly DPCInterface m_RdpInterface;
        private readonly cor64.HLE.GraphicsHLEDevice m_HLEGraphicsDevice;
        private readonly Cartridge m_Cart;
        private readonly IntPtr m_GLContextPtr;
        private Veldrid.GraphicsDevice m_VelGLDevice;
        private readonly Veldrid.ImGuiRenderer m_ImGuiRenderer;
        private BackendInfoOpenGL m_GLBackend;
        private CommandList m_ImGuiCommandList;

        private readonly KeyCallback m_KeyboardCallback;
        private readonly CharCallback m_CharCallback;



        private bool m_Created;
        private bool m_WinInit = true;

        private readonly bool m_WorkbenchMode;
        private bool m_IsClosed;

        private readonly RgbaFloat CLEAR_COLOR_WB = new(0.545f, 0.458f, 0.666f, 1.0f);
        private readonly RgbaFloat CLEAR_COLOR = new(0.160f, 0.160f, 0.160f, 1.0f);


        private readonly FBViewWindow m_FBWIndow;
        private readonly RspWindow m_WinRsp;
        private readonly RspDisassemblyWindow m_WinRspDisasm;
        private readonly CpuDisassemblyWindow m_WinCpuDisasm;
        private readonly DebugWindow m_WinDebug;
        private readonly RdpWindow m_WinRdp;
        private readonly DmaWindow m_WinDma;
        private readonly RspDissectWindow m_WinRspDissect;
        private readonly ScriptsWindow m_WinScripts;

        private readonly GlfwInputSnapshot m_Inputs;


        public event Action OnFrame;

        private readonly cor64.HLE.GraphicsHLEDevice.GLGetProcAddress m_Callback_GetProcAddress;
        private readonly cor64.HLE.GraphicsHLEDevice.GLSwapBuffers m_Callback_SwapBuffers;


        public GLWindow(N64System system, bool workbenchMode, ScriptHost scriptHost)
        {
            m_System = system;

            Glfw.WindowHint(Hint.ContextVersionMajor, 2);
            Glfw.WindowHint(Hint.ContextVersionMinor, 1);
            Glfw.WindowHint(Hint.Focused, true);
            Glfw.WindowHint(Hint.Resizable, true);
            Glfw.WindowHint(Hint.Doublebuffer, true);

            m_WorkbenchMode = workbenchMode;

            if (!workbenchMode)
            {
                m_Window = new NativeWindow(FBViewWindow.N64_MAX_RESOLUTION_X + 10, FBViewWindow.N64_MAX_RESOLUTION_Y, "Cor64");
            }
            else
            {
                m_Window = new NativeWindow(FBViewWindow.N64_MAX_RESOLUTION_X + 10, FBViewWindow.N64_MAX_RESOLUTION_Y + 80, "Cor64 Workbench");
                Glfw.MaximizeWindow(m_Window);
            }

            m_Inputs = new GlfwInputSnapshot(m_Window);

            m_KeyboardCallback = this.OnKeyboard;
            m_CharCallback = this.OnChar;

            Glfw.SetKeyCallback(m_Window, m_KeyboardCallback);
            Glfw.SetCharCallback(m_Window, m_CharCallback);

            m_Window.SizeChanged += (s, e) => {
                // m_RepositionFB = true;
                if (m_VelGLDevice == null || m_ImGuiRenderer == null) return; // [cer64plus] size event fires before GL init (IconifyWindow)
                m_VelGLDevice.MainSwapchain.Resize((uint)e.Size.Width, (uint)e.Size.Height);
                m_ImGuiRenderer.WindowResized(e.Size.Width, e.Size.Height);
            };

            m_Window.Closing += (s, e) => {
               m_VelGLDevice.WaitForIdle();
                m_ImGuiRenderer.Dispose();
                m_VelGLDevice.Dispose();
            };

            m_Window.Closed += (s, e) => {
                Console.WriteLine("GLFW Window Closed");
                m_IsClosed = true;
            };

            Glfw.IconifyWindow(m_Window);

            m_VideoInterface = system.DeviceRcp.VideoInterface;
            m_RdpInterface = system.DeviceRcp.DisplayProcessorCommandInterface;
            m_Cart = system.AttachedCartridge;
            m_HLEGraphicsDevice = system.DeviceRcp.GraphicsHLEDevice;

            m_GLContextPtr = GetNativeContext(m_Window);

            if (m_GLContextPtr == IntPtr.Zero) {
                throw new InvalidOperationException("Failed to get GL Context");
            }

            OpenTK.Graphics.OpenGL.GL.LoadBindings(new GlfwOpenTKContext());
            Console.WriteLine("GL Version: " + OpenTK.Graphics.OpenGL.GL.GetString(OpenTK.Graphics.OpenGL.StringName.Version));
            Console.WriteLine("GL Renderer: " + OpenTK.Graphics.OpenGL.GL.GetString(OpenTK.Graphics.OpenGL.StringName.Renderer));

            Glfw.MakeContextCurrent(m_Window);

            if (m_HLEGraphicsDevice != null) {
                m_Callback_GetProcAddress = new cor64.HLE.GraphicsHLEDevice.GLGetProcAddress(Glfw.GetProcAddress);

                m_Callback_SwapBuffers = () => {
                    Render();
                };

                m_HLEGraphicsDevice.AttachGL(
                    0,
                    m_Callback_GetProcAddress,
                    m_Callback_SwapBuffers
                );
            }

            if (m_HLEGraphicsDevice != null) {
                m_HLEGraphicsDevice.Init();
            }

            BindVeldrid();

            try {
                m_ImGuiRenderer = new ImGuiRenderer(
                    m_VelGLDevice,
                    m_VelGLDevice.MainSwapchain.Framebuffer.OutputDescription,
                    m_Window.Size.Width,
                    m_Window.Size.Height
                );
            }
            catch (System.Exception e) {
                Log.Error("Failed to create ImGUI renderer: " + e.Message);
                throw;
            }

           m_FBWIndow = new FBViewWindow(system, m_VelGLDevice, m_ImGuiRenderer, workbenchMode);


            if (workbenchMode) {
                m_WinRsp = new RspWindow(system);
                m_WinDebug = new DebugWindow(system);
                m_WinRspDisasm = new RspDisassemblyWindow(system);
                m_WinCpuDisasm = new CpuDisassemblyWindow(system);
                m_WinRdp = new RdpWindow(system);
                m_WinDma = new DmaWindow(system);
                m_WinRspDissect = new RspDissectWindow(system);
                m_WinScripts = new ScriptsWindow(scriptHost);
            }
        }

        private void OnKeyboard(IntPtr window, Keys key, int scanCode, InputState state, GLFW.ModifierKeys mods) {

            // [cer64plus] press and release; arrows drive the analog stick, the numeric keypad 8/2/4/6 the D-pad
            // [cer64plus] F5 quick save, F7 quick load (applied between execution batches)
            if (state == InputState.Press && key == Keys.F5) { SingleThreadHost.Current?.RequestSave("quicksave.cor64state"); return; }
            if (state == InputState.Press && key == Keys.F7) { if (System.IO.File.Exists("quicksave.cor64state")) SingleThreadHost.Current?.RequestLoad("quicksave.cor64state"); return; }
            if (state != InputState.Repeat) {
                bool down = state == InputState.Press;
                var joy = m_System.Joys[0];
                void Btn(JoyController.ButtonPress b) { if (down) joy.Press(b); else joy.Release(b); }
                switch (key) {
                    case Keys.A: Btn(JoyController.ButtonPress.A); break;
                    case Keys.B: Btn(JoyController.ButtonPress.B); break;
                    case Keys.Z: Btn(JoyController.ButtonPress.Z); break;
                    case Keys.Enter: Btn(JoyController.ButtonPress.Start); break;
                    case Keys.Q: Btn(JoyController.ButtonPress.ShoulderL); break;
                    case Keys.W: Btn(JoyController.ButtonPress.ShoulderR); break;
                    case Keys.J: Btn(JoyController.ButtonPress.CLeft); break;
                    case Keys.L: Btn(JoyController.ButtonPress.CRight); break;
                    case Keys.I: Btn(JoyController.ButtonPress.CUp); break;
                    case Keys.K: Btn(JoyController.ButtonPress.CDown); break;
                    case Keys.Numpad8: Btn(JoyController.ButtonPress.DPadUp); break;
                    case Keys.Numpad2: Btn(JoyController.ButtonPress.DPadDown); break;
                    case Keys.Numpad4: Btn(JoyController.ButtonPress.DPadLeft); break;
                    case Keys.Numpad6: Btn(JoyController.ButtonPress.DPadRight); break;
                    case Keys.Left: joy.Stick(JoyController.StickDir.Left, down); break;
                    case Keys.Right: joy.Stick(JoyController.StickDir.Right, down); break;
                    case Keys.Up: joy.Stick(JoyController.StickDir.Up, down); break;
                    case Keys.Down: joy.Stick(JoyController.StickDir.Down, down); break;
                    default: break;
                }
            }

            m_Inputs.AppendKeyEvent(
                KeyTranslator.MakeKeyEvent(
                    key,
                    state == InputState.Press,
                    state == InputState.Repeat,
                    mods
                ));
        }

        // [cer64plus] First connected gamepad (XInput/SDL mapping via GLFW) drives controller 1, ORed with the keyboard.
        // Left stick = analog stick, right stick = C buttons, A = A, B/X = B, LT = Z, LB = L, RB = R, Start = Start, D-pad = D-pad.
        // Skipped when a COR64_INPUT script owns the scripted input.
        private static readonly bool s_PadEnabled = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("COR64_INPUT"));
        private bool m_PadWasActive;

        private void PollGamepad() {
            if (!s_PadEnabled) return;
            int pad = -1;
            for (int j = 0; j <= 15; j++) if (Glfw.JoystickIsGamepad(j)) { pad = j; break; }
            var joy = m_System.Joys[0];
            if (pad < 0 || !Glfw.GetGamepadState(pad, out GamePadState st)) {
                if (m_PadWasActive) { joy.SetScript(JoyController.ButtonPress.None, 0, 0); m_PadWasActive = false; }
                return;
            }
            m_PadWasActive = true;
            var b = JoyController.ButtonPress.None;
            void Map(GamePadButton g, JoyController.ButtonPress n) { if (st.GetButtonState(g) == InputState.Press) b |= n; }
            Map(GamePadButton.A, JoyController.ButtonPress.A);
            Map(GamePadButton.B, JoyController.ButtonPress.B);
            Map(GamePadButton.X, JoyController.ButtonPress.B);
            Map(GamePadButton.Start, JoyController.ButtonPress.Start);
            Map(GamePadButton.LeftBumper, JoyController.ButtonPress.ShoulderL);
            Map(GamePadButton.RightBumper, JoyController.ButtonPress.ShoulderR);
            Map(GamePadButton.DpadUp, JoyController.ButtonPress.DPadUp);
            Map(GamePadButton.DpadDown, JoyController.ButtonPress.DPadDown);
            Map(GamePadButton.DpadLeft, JoyController.ButtonPress.DPadLeft);
            Map(GamePadButton.DpadRight, JoyController.ButtonPress.DPadRight);
            if (st.GetAxis(GamePadAxis.LeftTrigger) > 0.3f || st.GetAxis(GamePadAxis.RightTrigger) > 0.3f) b |= JoyController.ButtonPress.Z;
            float rx = st.GetAxis(GamePadAxis.RightX), ry = st.GetAxis(GamePadAxis.RightY);
            if (rx < -0.5f) b |= JoyController.ButtonPress.CLeft;
            if (rx > 0.5f) b |= JoyController.ButtonPress.CRight;
            if (ry < -0.5f) b |= JoyController.ButtonPress.CUp;
            if (ry > 0.5f) b |= JoyController.ButtonPress.CDown;
            static sbyte Axis(float v) {
                const float dead = 0.15f;
                if (Math.Abs(v) < dead) return 0;
                float s = (Math.Abs(v) - dead) / (1f - dead) * Math.Sign(v);
                return (sbyte)Math.Clamp((int)MathF.Round(s * JoyController.StickFull), -JoyController.StickFull, JoyController.StickFull);
            }
            joy.SetScript(b, Axis(st.GetAxis(GamePadAxis.LeftX)), Axis(-st.GetAxis(GamePadAxis.LeftY)));
        }

        private void OnChar(IntPtr window, uint codePoint) {
            m_Inputs.AppendKeyPress(Char.ConvertFromUtf32((int)codePoint)[0]);
        }

        private void BindVeldrid() {
            GraphicsDeviceOptions options = new(false);

            Veldrid.OpenGL.OpenGLPlatformInfo platformInfo = new(
                m_GLContextPtr,
                Glfw.GetProcAddress,
                _ => Glfw.MakeContextCurrent(m_Window),
                () => Glfw.CurrentContext,
                () => Glfw.MakeContextCurrent(Window.None),
                _ => {}, // No destroying the context
                () => Glfw.SwapBuffers(m_Window),
                sync => Glfw.SwapInterval(sync ? 1 : 0)
            );

            m_VelGLDevice = GraphicsDevice.CreateOpenGL(
                options,
                platformInfo,
                (uint)m_Window.Size.Width,
                (uint)m_Window.Size.Height
            );

            m_GLBackend = m_VelGLDevice.GetOpenGLInfo();
            m_ImGuiCommandList = m_VelGLDevice.ResourceFactory.CreateCommandList();
        }

        public void Start() {
            m_Created = true;

            while (!m_Window.IsClosed)
            {
                if (m_HLEGraphicsDevice == null) {
                   m_FBWIndow.Scan();
                }

                Render();
                PollGamepad();

                Thread.Sleep(17);
            }
        }
        

        public bool IsCreated => m_Created;

        public bool IsClosed => m_IsClosed;

        private void Render()
        {
            if (m_WinInit) {
                Glfw.SetWindowSize(m_Window, m_Window.Size.Width, m_Window.Size.Height);
            }

            Glfw.PollEvents();

            if (m_Window.IsClosed || m_Window.IsClosing) {
                return;
            }

            if (m_HLEGraphicsDevice == null) {
                m_FBWIndow.StageRdramTexture();
                m_ImGuiCommandList.Begin();
                    m_FBWIndow.CopyRdramToGPU(m_ImGuiCommandList);
                    m_ImGuiCommandList.End();
                    m_VelGLDevice.SubmitCommands(m_ImGuiCommandList);
                m_VelGLDevice.WaitForIdle();

                m_Inputs.UpdateMouse();

                m_ImGuiRenderer.Update(1f / 60f, m_Inputs);

                m_Inputs.ClearMouse();
                m_Inputs.ClearKeyboard();

                m_FBWIndow.UpdatePosition(
                    new(
                        (m_Window.Size.Width / 2) - (FBViewWindow.N64_MAX_RESOLUTION_X / 2),
                        1f
                    )
                );

                m_FBWIndow.UpdatePosition(
                    new(
                        0,
                        0f
                    )
                );

                OnFrame?.Invoke();

                BuildUI();
                RenderImGui();
            }
            else {
                m_GLBackend.ExecuteOnGLThread(() => {
                    if (m_HLEGraphicsDevice.PendingTasks > 0) m_HLEGraphicsDevice.GetNextTask().Invoke();
                    m_HLEGraphicsDevice.Render();
                });
                m_GLBackend.FlushAndFinish();
            }

            m_VelGLDevice.SwapBuffers();

            m_WinInit = false;
        }

        private void RenderImGui() {
            m_ImGuiCommandList.Begin();
                m_ImGuiCommandList.SetFramebuffer(m_VelGLDevice.MainSwapchain.Framebuffer);

                if (m_WorkbenchMode)
                    m_ImGuiCommandList.ClearColorTarget(0, CLEAR_COLOR_WB);
                else
                    m_ImGuiCommandList.ClearColorTarget(0, CLEAR_COLOR);

                m_ImGuiRenderer.Render(m_VelGLDevice, m_ImGuiCommandList);
            m_ImGuiCommandList.End();

            m_VelGLDevice.SubmitCommands(m_ImGuiCommandList);
        }

        private static IntPtr GetNativeContext(NativeWindow nativeWindow)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return Native.GetWglContext(nativeWindow);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // Try getting a XGL context (OpenGL on X11)
                var glx = Native.GetGLXContext(nativeWindow);

                // If this fails, try to get the Wayland (EGL) context
                if (glx == GLXContext.None) {
                    var egl = Native.GetEglContext(nativeWindow);

                    if (egl == EGLContext.None) {
                        throw new PlatformNotSupportedException("Could not find a context for X11/Wayland");
                    }

                    return egl;
                }

                return glx;
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return Native.GetNSGLContext(nativeWindow);
            }

            throw new PlatformNotSupportedException();
        }

        public void TriggerVI()
        {
            m_VideoInterface.SetVideoInterrupt();
        }

        private unsafe void BuildUI()
        {
            m_FBWIndow.Build();

            if (m_WorkbenchMode) {
                m_WinRsp.Build();
                m_WinDebug.Build();
                m_WinRspDisasm.Build();
                //m_WinCpuDisasm.Build(); // Still issues
                m_WinRdp.Build();
                m_WinDma.Build();
                // m_WinRspDissect.Build();
                m_WinScripts.Build();
            }
        }
    }
}