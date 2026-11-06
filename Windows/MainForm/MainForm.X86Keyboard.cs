using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using NesEmulator.X86;
using NesEmulator.Systems;

namespace BrokenNes.Windows
{
    /// <summary>
    /// The x86 PC console's real keyboard: while an x86 session runs, the keys typed in the main window go to the PC as its keyboard (scan codes into the
    /// session's 8042), alongside the pad's keys (a game pad still presses its keys, <see cref="PcSession.KeyFor"/>). The keyboard then presses no pad buttons
    /// (<see cref="InputManager.KeyboardToPad"/>), and the window's own key handling (the menu's Alt / F10, the menu shortcuts) steps aside, since DOS
    /// programs use those keys; Alt+Enter still toggles full screen, and the mouse reaches the menus.
    /// <para>The scan codes are the ones Windows reports for the physical keys (the message's lParam), so the PC sees the keyboard as it is, whatever the
    /// Windows layout. Releasing the window lets go of every held key, so nothing sticks down in the PC.</para>
    /// </summary>
    public partial class MainForm
    {
        private const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
        private readonly ConcurrentQueue<(int Scan, bool Extended, bool Down)> pcKeys = new();
        private readonly HashSet<(int, bool)> pcHeld = new();     // UI thread only
        private PcKeyboardFilter? pcKeyboardFilter;

        [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint uCode, uint uMapType);

        /// <summary>Installs the filter once (the window's constructor); it does nothing unless an x86 session runs.</summary>
        private void InstallPcKeyboard()
        {
            if (pcKeyboardFilter != null) return;
            pcKeyboardFilter = new PcKeyboardFilter(this);
            Application.AddMessageFilter(pcKeyboardFilter);
            Deactivate += (_, _) => ReleasePcKeys();
        }

        private bool PcKeyboardActive => session is PcSession;
        /// <summary>The input managers ask it (keyboard polling, <see cref="InputManager.KeyboardToPad"/>): false while the x86 PC has the keyboard.</summary>
        private bool KeyboardToPadNow() => !PcKeyboardActive;

        /// <summary>One key message for the PC (UI thread): true when it was taken (the window does not see it).</summary>
        internal bool OnPcKeyMessage(ref Message m)
        {
            if (!PcKeyboardActive) return false;
            if (m.HWnd != Handle && Control.FromChildHandle(m.HWnd)?.FindForm() != this) return false;   // another window (a debug window, a dialog)
            int vk = (int)m.WParam & 0xff; long lp = m.LParam.ToInt64();
            bool down = m.Msg is WM_KEYDOWN or WM_SYSKEYDOWN;
            bool alt = (lp & (1L << 29)) != 0;
            if (vk == (int)Keys.Return && alt) return false;                   // Alt+Enter: full screen (MainForm_KeyDown)
            int scan = (int)((lp >> 16) & 0xff); bool ext = (lp & (1L << 24)) != 0;
            if (scan == 0)
            {
                uint sc = MapVirtualKey((uint)vk, 4);                          // MAPVK_VK_TO_VSC_EX: E0xx for an extended key
                scan = (int)(sc & 0xff); ext = (sc & 0xff00) == 0xe000;
            }
            if (vk == 0x90) ext = false;                                        // Num Lock: Windows marks it extended, the PC's is plain 45h
            if (vk == 0x13) { scan = 0x45; ext = true; }                        // Pause: its own sequence (PcSession.HostKey)
            if (scan == 0) return true;
            if (down) pcHeld.Add((scan, ext)); else pcHeld.Remove((scan, ext));
            pcKeys.Enqueue((scan, ext, down));
            return true;
        }

        /// <summary>Lets go of every key the PC holds (the window lost the keyboard, or the session changed).</summary>
        private void ReleasePcKeys()
        {
            foreach (var (scan, ext) in pcHeld) if (!(scan == 0x45 && ext)) pcKeys.Enqueue((scan, ext, false));
            pcHeld.Clear();
        }

        /// <summary>The emulation thread, under the emulation lock, before the frame: the keys typed since the last frame reach the PC in order.</summary>
        private void DrainPcKeys(IConsoleSession s)
        {
            if (s is not PcSession pc) { while (pcKeys.TryDequeue(out _)) { } return; }
            while (pcKeys.TryDequeue(out var k)) pc.HostKey(k.Scan, k.Extended, k.Down);
        }

        private sealed class PcKeyboardFilter : IMessageFilter
        {
            private readonly MainForm form;
            public PcKeyboardFilter(MainForm form) { this.form = form; }
            public bool PreFilterMessage(ref Message m) =>
                m.Msg is WM_KEYDOWN or WM_KEYUP or WM_SYSKEYDOWN or WM_SYSKEYUP && form.OnPcKeyMessage(ref m);
        }
    }
}
