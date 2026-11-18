using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using NesEmulator.Systems;
using NesEmulator.X86;

namespace BrokenNes.Windows
{
    /// <summary>
    /// The x86 PC's PS/2 mouse on the desktop (the Windows 98 machine has one): a click on the picture captures the host mouse (the cursor hides and
    /// stays inside the picture, its motion goes to the PC as relative mickeys and the buttons as the mouse's buttons, like a real mouse on the PC);
    /// Ctrl+Alt (pressed together), or leaving the window, lets it go. Nothing happens unless the session's PC has a mouse.
    /// </summary>
    public partial class MainForm
    {
        private Control? pcMousePicture;
        private bool pcMouseCaptured;
        private Point pcMouseCenter;                 // screen coordinates the cursor is put back to
        private int pcMouseDx, pcMouseDy, pcMouseButtons, pcMouseDirty;   // UI thread writes (Interlocked), emulation thread drains

        private bool PcMouseAvailable => session is PcSession pc && pc.HasMouse;

        /// <summary>Hooks the picture control (after the renderer exists).</summary>
        private void HookPcMouse(Control picture)
        {
            pcMousePicture = picture;
            picture.MouseDown += (s, e) =>
            {
                if (!PcMouseAvailable) return;
                if (!pcMouseCaptured) { CapturePcMouse(); return; }        // the capturing click is not passed on
                PcMouseButton(e.Button, true);
            };
            picture.MouseUp += (s, e) => { if (pcMouseCaptured) PcMouseButton(e.Button, false); };
            picture.MouseMove += (s, e) =>
            {
                if (!pcMouseCaptured) return;
                if (!PcMouseAvailable) { ReleasePcMouse(); return; }
                var p = Cursor.Position;
                int dx = p.X - pcMouseCenter.X, dy = p.Y - pcMouseCenter.Y;
                if (dx == 0 && dy == 0) return;
                Interlocked.Add(ref pcMouseDx, dx); Interlocked.Add(ref pcMouseDy, dy); Interlocked.Exchange(ref pcMouseDirty, 1);
                Cursor.Position = pcMouseCenter;
            };
            Deactivate += (_, _) => ReleasePcMouse();
        }

        private void PcMouseButton(MouseButtons b, bool down)
        {
            int bit = b switch { MouseButtons.Left => 1, MouseButtons.Right => 2, MouseButtons.Middle => 4, _ => 0 };
            if (bit == 0) return;
            int v, n;
            do { v = pcMouseButtons; n = down ? v | bit : v & ~bit; } while (Interlocked.CompareExchange(ref pcMouseButtons, n, v) != v);
            Interlocked.Exchange(ref pcMouseDirty, 1);
        }

        private void CapturePcMouse()
        {
            if (pcMousePicture == null || pcMouseCaptured) return;
            var r = pcMousePicture.RectangleToScreen(pcMousePicture.ClientRectangle);
            pcMouseCenter = new Point(r.Left + r.Width / 2, r.Top + r.Height / 2);
            Cursor.Clip = r;
            Cursor.Position = pcMouseCenter;
            Cursor.Hide();
            pcMouseCaptured = true;
            pcMousePicture.Capture = true;
            try { Text = Text.EndsWith(" [mouse captured: Ctrl+Alt releases]") ? Text : Text + " [mouse captured: Ctrl+Alt releases]"; } catch { }
        }

        /// <summary>Lets the host mouse go (Ctrl+Alt, the window lost focus, the session changed); the PC's buttons are released.</summary>
        private void ReleasePcMouse()
        {
            if (!pcMouseCaptured) return;
            pcMouseCaptured = false;
            Cursor.Clip = Rectangle.Empty;
            Cursor.Show();
            if (pcMousePicture != null) pcMousePicture.Capture = false;
            Interlocked.Exchange(ref pcMouseButtons, 0); Interlocked.Exchange(ref pcMouseDirty, 1);
            try { Text = Text.Replace(" [mouse captured: Ctrl+Alt releases]", ""); } catch { }
        }

        /// <summary>Ctrl+Alt while captured (from the key filter): true when it released the mouse.</summary>
        private bool PcMouseReleaseChord(int vk, bool down)
        {
            if (!pcMouseCaptured || !down || vk is not (0x11 or 0x12)) return false;
            if ((Control.ModifierKeys & (Keys.Control | Keys.Alt)) != (Keys.Control | Keys.Alt)) return false;
            BeginInvoke(new Action(ReleasePcMouse));
            return true;
        }

        /// <summary>The emulation thread, before the frame: the motion and buttons since the last frame reach the PC's mouse.</summary>
        private void DrainPcMouse(IConsoleSession s)
        {
            if (Interlocked.Exchange(ref pcMouseDirty, 0) == 0) return;
            int dx = Interlocked.Exchange(ref pcMouseDx, 0), dy = Interlocked.Exchange(ref pcMouseDy, 0);
            if (s is PcSession pc) pc.HostMouse(dx, dy, Volatile.Read(ref pcMouseButtons));
        }
    }
}
