using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using NesEmulator.Snes;
using NesEmulator.Systems;

namespace BrokenNes.Windows
{
    // The WideMess pointer port (docs/projects/widemess-spec.md section 10) on the desktop: the picture's mouse events (touch and pen arrive as
    // Windows' promoted mouse messages, told apart by their message signature) mapped through the renderer's own destination rectangle to the
    // frame's pixels. No cursor is drawn by the console: the system cursor shows over the window. Outside the picture = not present.
    public partial class MainForm
    {
        private readonly object pointerLock = new();
        private WmsPointer hostPointer;          // the mouse / finger / pen over the picture (UI thread writes, emulation thread reads)
        private bool hostLeft, hostRight;

        [DllImport("user32.dll")] private static extern IntPtr GetMessageExtraInfo();

        /// <summary>Touch / pen promoted to mouse messages carry the signature 0xFF5157xx in the message's extra info (bit 7: touch, else pen).</summary>
        private static WmsPointerDevice HostDevice()
        {
            long info = GetMessageExtraInfo().ToInt64() & 0xFFFFFFFF;
            if ((info & 0xFFFFFF00) != 0xFF515700) return WmsPointerDevice.Mouse;
            return (info & 0x80) != 0 ? WmsPointerDevice.Touch : WmsPointerDevice.Pen;
        }

        /// <summary>Hooks the picture control (after the renderer exists).</summary>
        private void HookWmsPointer(Control picture)
        {
            picture.MouseMove += (s, e) => HostPointerAt(picture, e.X, e.Y, HostDevice());
            picture.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) hostLeft = true; else if (e.Button == MouseButtons.Right) hostRight = true; HostPointerAt(picture, e.X, e.Y, HostDevice()); };
            picture.MouseUp += (s, e) =>
            {
                if (e.Button == MouseButtons.Left) hostLeft = false; else if (e.Button == MouseButtons.Right) hostRight = false;
                var dev = HostDevice();
                HostPointerAt(picture, e.X, e.Y, dev);
                if (dev == WmsPointerDevice.Touch) lock (pointerLock) hostPointer = hostPointer with { Present = false, Left = false, Right = false };   // the finger lifted: nothing points any more
            };
            picture.MouseLeave += (s, e) => { hostLeft = hostRight = false; lock (pointerLock) hostPointer = hostPointer with { Present = false, Left = false, Right = false }; };
        }

        private void HostPointerAt(Control picture, int cx, int cy, WmsPointerDevice dev)
        {
            var s = session;
            if (s is not IPointerSession { HasPointer: true } || dxRenderer == null) { lock (pointerLock) hostPointer = WmsPointer.Idle; return; }
            var r = dxRenderer.GetViewportRect();
            var p = WmsPointer.FromHost(cx, cy, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, s.FrameWidth, s.FrameHeight, hostLeft, hostRight, dev);
            lock (pointerLock) hostPointer = p;
        }

        /// <summary>A WideMess game that unlocked the extensions may use the pointer: a double-click on the picture is then the game's, not the fullscreen toggle.</summary>
        private bool PointerGameRunning => session is SnesSession { WideMess.Unlocked: true };

        /// <summary>The frame's pointer (emulation thread): the HTTP API's, when a test holds one, else the host's; a Timeflow replay's while one runs.</summary>
        private uint FramePointer(IConsoleSession s)
        {
            if (s is not IPointerSession { HasPointer: true }) return 0;
            WmsPointer p;
            if (WebApi.ApiPointerInjector.Injected is WmsPointer inj) p = inj;
            else lock (pointerLock) p = hostPointer;
            uint v = p.Pack();
            if (tfReplay != null) v = tfReplay.Reader.Has(tfReplayFrame) ? tfReplay.Reader.Pointer(tfReplayFrame) : 0;
            return v;
        }

        private static void ApplyPointer(IConsoleSession s, uint v)
        {
            if (s is not IPointerSession { HasPointer: true } ps) return;
            ps.SetPointer(WmsPointer.Unpack(v));
            WebApi.ApiPointerInjector.Observed = WmsPointer.Unpack(v);
        }
    }
}
