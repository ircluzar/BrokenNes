using System;
using System.Windows.Forms;

namespace BrokenNes.Windows.Diagnostics
{
    /// <summary>
    /// An <see cref="IMessageFilter"/> that records every keyboard and shutdown-ish window message
    /// as it enters the UI thread's message loop, before WinForms has had a chance to interpret it.
    ///
    /// WHY A MESSAGE FILTER AND NOT A KeyDown HANDLER
    /// ----------------------------------------------
    /// The silent-death bug is about how WinForms *routes* a keystroke, not about what the game
    /// does with it. A Form.KeyDown handler only sees keys that already survived
    /// Control.PreProcessMessage - which is exactly the stage that turns a plain letter into a
    /// menu mnemonic and swallows it. A message filter runs earlier than all of that, so it sees
    /// the raw WM_KEYDOWN/WM_CHAR pairs even when they are about to be eaten by the menu.
    ///
    /// It also logs the menu-relevant state at that instant (which control has focus, whether the
    /// MenuStrip is involved, the live modifier keys), because that state is what decides whether
    /// a keystroke becomes gameplay input or a menu command.
    ///
    /// This filter always returns false: it observes, it never consumes. It is only installed when
    /// BROKENNES_DIAG is set.
    /// </summary>
    internal sealed class InputTraceFilter : IMessageFilter
    {
        private const int WM_DESTROY = 0x0002;
        private const int WM_CLOSE = 0x0010;
        private const int WM_QUIT = 0x0012;
        private const int WM_QUERYENDSESSION = 0x0011;
        private const int WM_ENDSESSION = 0x0016;
        private const int WM_SYSCOMMAND = 0x0112;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_CHAR = 0x0102;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private const int WM_SYSCHAR = 0x0106;
        private const int WM_INITMENU = 0x0116;
        private const int WM_INITMENUPOPUP = 0x0117;
        private const int WM_MENUCHAR = 0x0120;
        private const int WM_EXITMENULOOP = 0x0212;
        private const int WM_ENTERMENULOOP = 0x0211;

        private readonly Form owner;

        internal InputTraceFilter(Form owner)
        {
            this.owner = owner;
        }

        public bool PreFilterMessage(ref Message m)
        {
            switch (m.Msg)
            {
                case WM_KEYDOWN:
                case WM_KEYUP:
                case WM_SYSKEYDOWN:
                case WM_SYSKEYUP:
                    ShutdownDiagnostics.LogVerbose(
                        $"MSG {Name(m.Msg)} vk={(Keys)(int)m.WParam}({(int)m.WParam}) lParam=0x{(long)m.LParam:X} {Context()}");
                    break;

                case WM_CHAR:
                case WM_SYSCHAR:
                    ShutdownDiagnostics.LogVerbose(
                        $"MSG {Name(m.Msg)} char='{Printable((int)m.WParam)}'(0x{(int)m.WParam:X2}) {Context()}");
                    break;

                case WM_MENUCHAR:
                    // Raised when a menu is open and a key with no matching mnemonic is pressed.
                    // Seeing this at all proves a menu had the keyboard at that moment.
                    ShutdownDiagnostics.LogVerbose($"MSG WM_MENUCHAR wParam=0x{(long)m.WParam:X} {Context()}");
                    break;

                case WM_INITMENU:
                case WM_INITMENUPOPUP:
                case WM_ENTERMENULOOP:
                case WM_EXITMENULOOP:
                case WM_SYSCOMMAND:
                case WM_CLOSE:
                case WM_DESTROY:
                case WM_QUIT:
                case WM_QUERYENDSESSION:
                case WM_ENDSESSION:
                    // These are rare enough that they go to the always-on stream: any one of them
                    // showing up around the moment of death is a direct answer.
                    ShutdownDiagnostics.Log($"MSG {Name(m.Msg)} hwnd=0x{(long)m.HWnd:X} wParam=0x{(long)m.WParam:X} {Context()}");
                    break;
            }

            return false; // observe only
        }

        /// <summary>
        /// The WinForms state that decides how the next keystroke is routed. `menuFocus` is the
        /// interesting one: once the MenuStrip has (or contains) focus, ToolStrip.ProcessMnemonic
        /// starts accepting bare letters as menu accelerators.
        /// </summary>
        private string Context()
        {
            try
            {
                var menu = owner.MainMenuStrip;
                string active;
                try { active = owner.ActiveControl?.GetType().Name ?? "<null>"; }
                catch { active = "<err>"; }

                return $"| active={active} menuFocused={menu?.Focused} menuContainsFocus={menu?.ContainsFocus} "
                     + $"menuVisible={menu?.Visible} mods={Control.ModifierKeys} activeForm={Form.ActiveForm?.GetType().Name ?? "<null>"}";
            }
            catch (Exception ex)
            {
                return "| <context unavailable: " + ex.Message + ">";
            }
        }

        private static string Printable(int c)
            => c >= 32 && c < 127 ? ((char)c).ToString() : ".";

        private static string Name(int msg) => msg switch
        {
            WM_DESTROY => "WM_DESTROY",
            WM_CLOSE => "WM_CLOSE",
            WM_QUIT => "WM_QUIT",
            WM_QUERYENDSESSION => "WM_QUERYENDSESSION",
            WM_ENDSESSION => "WM_ENDSESSION",
            WM_SYSCOMMAND => "WM_SYSCOMMAND",
            WM_KEYDOWN => "WM_KEYDOWN",
            WM_KEYUP => "WM_KEYUP",
            WM_CHAR => "WM_CHAR",
            WM_SYSKEYDOWN => "WM_SYSKEYDOWN",
            WM_SYSKEYUP => "WM_SYSKEYUP",
            WM_SYSCHAR => "WM_SYSCHAR",
            WM_INITMENU => "WM_INITMENU",
            WM_INITMENUPOPUP => "WM_INITMENUPOPUP",
            WM_ENTERMENULOOP => "WM_ENTERMENULOOP",
            WM_EXITMENULOOP => "WM_EXITMENULOOP",
            _ => "0x" + msg.ToString("X4"),
        };
    }
}
