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
using PngPayloadEmbedding;
using System.Text;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Web.WebView2.Core;

namespace BrokenNes.Windows
{
    public partial class MainForm
    {
        private void ShowContinueButton()
        {
            try 
            {
                string continuePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "continue.png");
                if (!File.Exists(continuePath) || continueButton != null) return;

                // Load to memory so we don't lock the file
                Bitmap img;
                using (var fs = new FileStream(continuePath, FileMode.Open, FileAccess.Read))
                {
                    using (var temp = new Bitmap(fs))
                    {
                        img = new Bitmap(temp);
                    }
                }
                    
                using (Graphics g = Graphics.FromImage(img))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

                    string text = "Continue?";
                    using (Font f = new Font("Segoe UI", 16, FontStyle.Bold))
                    {
                            // Thicker and blurrier shadow
                            using (var shadowBrush = new SolidBrush(Color.FromArgb(30, Color.Black)))
                            {
                                for (int y = 1; y <= 5; y++)
                                {
                                    for (int x = 1; x <= 5; x++)
                                    {
                                        g.DrawString(text, f, shadowBrush, new PointF(10 + x, 10 + y));
                                    }
                                }
                            }
                             
                            g.DrawString(text, f, Brushes.White, new PointF(10, 10));
                        }

                        // Outline surrounding the continue box
                        using (var outlinePen = new Pen(Color.White, 3))
                        {
                            outlinePen.Alignment = System.Drawing.Drawing2D.PenAlignment.Inset;
                            g.DrawRectangle(outlinePen, 0, 0, img.Width, img.Height);
                        }
                    }

                continueButton = new PictureBox
                {
                    Image = img,
                    SizeMode = PictureBoxSizeMode.AutoSize,
                    Cursor = Cursors.Hand,
                    Location = new Point(20, 20),
                    BackColor = Color.Transparent
                };
                continueButton.Click += ContinueSession_Click;
                
                displayPanel.Controls.Add(continueButton);
                continueButton.BringToFront();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load continue.png: {ex.Message}");
            }
        }

        private void HideContinueButton()
        {
            if (continueButton != null)
            {
                if (displayPanel.Controls.Contains(continueButton))
                    displayPanel.Controls.Remove(continueButton);
                
                if (continueButton.Image != null) continueButton.Image.Dispose();
                continueButton.Dispose();
                continueButton = null;
            }
        }

        private void ContinueSession_Click(object? sender, EventArgs e)
        {
             HideContinueButton();
             string continuePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "continue.png");
             if (File.Exists(continuePath))
             {
                 LoadStateFile(continuePath);
                 // Delete after loading so it doesn't appear again on next launch unless saved again
                 try { File.Delete(continuePath); } catch {}
             }
        }
    }
}
