using System;
using System.Drawing;
using System.Windows.Forms;

namespace BrokenNes.Windows.Helpers
{
    public static class FullscreenHelper
    {
        public static bool ToggleFullscreen(Form form, bool isFullscreen, bool hideMenuBar, 
            ref FormBorderStyle previousBorderStyle, ref FormWindowState previousWindowState, ref Rectangle previousBounds)
        {
            if (isFullscreen)
            {
                // Exit fullscreen
                form.FormBorderStyle = previousBorderStyle;
                form.WindowState = previousWindowState;
                form.Bounds = previousBounds;
                
                // Return new state
                return false;
            }
            else
            {
                // Enter fullscreen
                previousBorderStyle = form.FormBorderStyle;
                previousWindowState = form.WindowState;
                previousBounds = form.Bounds;
                
                form.FormBorderStyle = FormBorderStyle.None;
                form.WindowState = FormWindowState.Normal;
                form.Bounds = Screen.FromControl(form).Bounds;
                
                return true;
            }
        }
    }
}
