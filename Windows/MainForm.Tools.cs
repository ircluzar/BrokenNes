using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using System.Threading.Tasks;
using BrokenNes;
using BrokenNes.CorruptorModels;
using NesEmulator;
using NesEmulator.Shaders;
using BrokenNes.Windows.Rendering;
using BrokenNes.Windows.Tools;
using PngPayloadEmbedding;
using System.Text;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Web.WebView2.Core;

namespace BrokenNes.Windows
{
    public partial class MainForm
    {
        private void OpenRtcTool_Click(object? sender, EventArgs e)
        {
            if (rtcForm == null || rtcForm.IsDisposed)
            {
                rtcForm = new RealTimeCorruptorForm(this);
                rtcForm.FormClosed += (_, _) => rtcForm = null;
            }

            rtcForm.Show(this);
            rtcForm.Focus();
        }

        private void OpenGhTool_Click(object? sender, EventArgs e)
        {
            if (ghForm == null || ghForm.IsDisposed)
            {
                ghForm = new GlitchHarvesterForm(this);
                ghForm.FormClosed += (_, _) => ghForm = null;
            }

            ghForm.Show(this);
            ghForm.Focus();
        }

        private void OpenImagineTool_Click(object? sender, EventArgs e)
        {
            if (imagineForm == null || imagineForm.IsDisposed)
            {
                imagineForm = new ImagineForm(this);
                imagineForm.FormClosed += (_, _) => imagineForm = null;
            }

            imagineForm.Show(this);
            imagineForm.Focus();
        }

        private void OpenHexEditor_Click(object? sender, EventArgs e)
        {
            if (hexEditorForm == null || hexEditorForm.IsDisposed)
            {
                hexEditorForm = new HexEditorForm(this);
                hexEditorForm.FormClosed += (_, _) => hexEditorForm = null;
            }

            hexEditorForm.Show(this);
            hexEditorForm.Focus();
        }
    }
}
