using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DirectWrite;
using SharpDX.DXGI;
using AlphaMode = SharpDX.Direct2D1.AlphaMode;
using FactoryD2D = SharpDX.Direct2D1.Factory;
using FactoryDW = SharpDX.DirectWrite.Factory;
using FactoryDXGI = SharpDX.DXGI.Factory;
using D2DFactoryType = SharpDX.Direct2D1.FactoryType;
using DWFontStyle = SharpDX.DirectWrite.FontStyle;
using RectangleF = SharpDX.Mathematics.Interop.RawRectangleF;
using Color = SharpDX.Color;

namespace BrokenNes.Windows.Tools
{
    /// <summary>
    /// GPU-accelerated hex grid renderer using Direct2D/DirectWrite. Designed for fast repainting of large memory pages.
    /// </summary>
    internal sealed class DxHexGridControl : Control
    {
        private SharpDX.Direct3D11.Device device;
        private SwapChain swapChain;
        private RenderTarget renderTarget;
        private FactoryD2D d2dFactory;
        private FactoryDW dwFactory;
        private TextFormat textFormat;
        private SolidColorBrush textBrush;
        private SolidColorBrush dimTextBrush;
        private SolidColorBrush headerBrush;
        private SolidColorBrush backgroundBrush;
        private SolidColorBrush gridLineBrush;
        private bool initialized;

        private string domainLabel = string.Empty;
        private int baseAddress;
        private int domainSize;
        private byte[] data = Array.Empty<byte>();
        private int bytesPerRow = 16;

        private float cellWidth = 44f;
        private float rowHeight = 22f;
        private float headerHeight = 26f;
        private float addressColWidth = 90f;
        private const float Padding = 8f;

        public event Action<int>? CellClicked;

        public DxHexGridControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque | ControlStyles.UserPaint, true);
            ResizeRedraw = true;
            DoubleBuffered = false;
        }

        public void UpdateData(string label, int startAddress, int totalSize, byte[] pageData, int bytesPerRow = 16)
        {
            domainLabel = label;
            baseAddress = startAddress;
            domainSize = totalSize;
            data = pageData ?? Array.Empty<byte>();
            this.bytesPerRow = Math.Max(1, bytesPerRow);
            Invalidate();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (!DesignMode)
            {
                InitializeDirectX();
            }
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            DisposeResources();
            base.OnHandleDestroyed(e);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (!initialized) return;

            renderTarget?.Dispose();
            swapChain?.ResizeBuffers(1, Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height), Format.B8G8R8A8_UNorm, SwapChainFlags.None);
            CreateRenderTarget();
            Invalidate();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (!initialized || data.Length == 0) return;

            var (rowIdx, colIdx) = HitTestCell(e.Location);
            if (rowIdx < 0 || colIdx < 0) return;

            int offset = (rowIdx * bytesPerRow) + colIdx;
            if (offset < 0 || offset >= data.Length) return;

            int address = baseAddress + offset;
            if (address >= domainSize) return;

            CellClicked?.Invoke(address);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Render();
        }

        private void InitializeDirectX()
        {
            if (initialized) return;

            d2dFactory = new FactoryD2D(D2DFactoryType.SingleThreaded);
            dwFactory = new FactoryDW();

            var swapChainDesc = new SwapChainDescription
            {
                BufferCount = 1,
                ModeDescription = new ModeDescription(Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height), new Rational(60, 1), Format.B8G8R8A8_UNorm),
                Usage = Usage.RenderTargetOutput,
                OutputHandle = Handle,
                SampleDescription = new SampleDescription(1, 0),
                IsWindowed = true,
                SwapEffect = SwapEffect.Discard,
                Flags = SwapChainFlags.None
            };

            SharpDX.Direct3D11.Device.CreateWithSwapChain(SharpDX.Direct3D.DriverType.Hardware,
                SharpDX.Direct3D11.DeviceCreationFlags.BgraSupport,
                swapChainDesc,
                out device,
                out swapChain);

            // Disable ALT+ENTER handling
            var dxgiFactory = swapChain.GetParent<FactoryDXGI>();
            dxgiFactory.MakeWindowAssociation(Handle, WindowAssociationFlags.IgnoreAltEnter | WindowAssociationFlags.IgnoreAll);
            dxgiFactory.Dispose();

            CreateRenderTarget();

            textFormat = new TextFormat(dwFactory, "Consolas", FontWeight.Medium, DWFontStyle.Normal, 12f)
            {
                TextAlignment = TextAlignment.Leading,
                ParagraphAlignment = ParagraphAlignment.Near
            };

            initialized = true;
        }

        private void CreateRenderTarget()
        {
            DisposeBrushes();

            using var backBuffer = swapChain.GetBackBuffer<Surface>(0);
            var props = new RenderTargetProperties(new PixelFormat(Format.Unknown, AlphaMode.Premultiplied));
            renderTarget = new RenderTarget(d2dFactory, backBuffer, props)
            {
                TextAntialiasMode = SharpDX.Direct2D1.TextAntialiasMode.Grayscale,
                AntialiasMode = AntialiasMode.PerPrimitive
            };

            textBrush = new SolidColorBrush(renderTarget, new Color4(0.95f, 0.95f, 0.95f, 1f));
            dimTextBrush = new SolidColorBrush(renderTarget, new Color4(0.6f, 0.6f, 0.6f, 1f));
            headerBrush = new SolidColorBrush(renderTarget, new Color4(0.18f, 0.18f, 0.24f, 1f));
            backgroundBrush = new SolidColorBrush(renderTarget, new Color4(0.10f, 0.10f, 0.12f, 1f));
            gridLineBrush = new SolidColorBrush(renderTarget, new Color4(0.28f, 0.28f, 0.30f, 1f));
        }

        private void Render()
        {
            if (!initialized || renderTarget == null) return;

            renderTarget.BeginDraw();
            renderTarget.Clear(new Color4(0.08f, 0.08f, 0.1f, 1f));

            float x = Padding;
            float y = Padding;

            // Draw header bar
            var headerRect = new RectangleF(x, y, ClientSize.Width - (Padding * 2), headerHeight);
            renderTarget.FillRectangle(headerRect, headerBrush);
            renderTarget.DrawText($"{domainLabel} — Base 0x{baseAddress:X6}", textFormat, headerRect, textBrush);
            y += headerHeight + 6f;

            // Column headers
            var colHeaderRect = new RectangleF(x + addressColWidth, y, ClientSize.Width - addressColWidth - (Padding * 2), rowHeight);
            for (int col = 0; col < bytesPerRow; col++)
            {
                float cx = x + addressColWidth + (col * cellWidth);
                var cellRect = new RectangleF(cx, y, cellWidth, rowHeight);
                renderTarget.DrawText(col.ToString("X1"), textFormat, cellRect, textBrush);
            }
            y += rowHeight;

            // Grid lines and data
            int rows = (int)Math.Ceiling(data.Length / (float)bytesPerRow);
            for (int row = 0; row < rows; row++)
            {
                float ry = y + (row * rowHeight);
                int rowAddress = baseAddress + (row * bytesPerRow);
                // Address column
                var addrRect = new RectangleF(x, ry, addressColWidth, rowHeight);
                renderTarget.DrawText($"0x{rowAddress:X6}", textFormat, addrRect, dimTextBrush);

                for (int col = 0; col < bytesPerRow; col++)
                {
                    int idx = (row * bytesPerRow) + col;
                    float cx = x + addressColWidth + (col * cellWidth);
                    var cellRect = new RectangleF(cx, ry, cellWidth, rowHeight);

                    bool inRange = idx < data.Length && (rowAddress + col) < domainSize;
                    var brush = inRange ? textBrush : dimTextBrush;
                    string text = inRange ? data[idx].ToString("X2") : "";

                    renderTarget.FillRectangle(cellRect, backgroundBrush);
                    renderTarget.DrawText(text, textFormat, cellRect, brush);
                }
            }

            // Grid lines (vertical)
            float gridTop = y - rowHeight; // include column header line
            float gridBottom = y + (rows * rowHeight);
            for (int col = 0; col <= bytesPerRow; col++)
            {
                float cx = x + addressColWidth + (col * cellWidth);
                renderTarget.DrawLine(new SharpDX.Mathematics.Interop.RawVector2(cx, gridTop), new SharpDX.Mathematics.Interop.RawVector2(cx, gridBottom), gridLineBrush, 1f);
            }
            // Horizontal lines
            float gridLeft = x;
            float gridRight = x + addressColWidth + (bytesPerRow * cellWidth);
            for (int row = 0; row <= rows; row++)
            {
                float ry = y + (row * rowHeight);
                renderTarget.DrawLine(new SharpDX.Mathematics.Interop.RawVector2(gridLeft, ry), new SharpDX.Mathematics.Interop.RawVector2(gridRight, ry), gridLineBrush, 1f);
            }

            renderTarget.EndDraw();
            swapChain.Present(1, PresentFlags.None);
        }

        private (int row, int col) HitTestCell(System.Drawing.Point point)
        {
            float x = Padding;
            float y = Padding + headerHeight + 6f + rowHeight; // start of first data row

            float relX = point.X - (x + addressColWidth);
            float relY = point.Y - y;

            if (relX < 0 || relY < 0) return (-1, -1);

            int col = (int)(relX / cellWidth);
            int row = (int)(relY / rowHeight);

            if (col < 0 || col >= bytesPerRow) return (-1, -1);
            if (row < 0) return (-1, -1);

            return (row, col);
        }

        private void DisposeResources()
        {
            DisposeBrushes();
            textFormat?.Dispose();
            renderTarget?.Dispose();
            swapChain?.Dispose();
            device?.Dispose();
            dwFactory?.Dispose();
            d2dFactory?.Dispose();
            initialized = false;
        }

        private void DisposeBrushes()
        {
            gridLineBrush?.Dispose();
            backgroundBrush?.Dispose();
            headerBrush?.Dispose();
            dimTextBrush?.Dispose();
            textBrush?.Dispose();
            gridLineBrush = null;
            backgroundBrush = null;
            headerBrush = null;
            dimTextBrush = null;
            textBrush = null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeResources();
            }
            base.Dispose(disposing);
        }
    }
}
