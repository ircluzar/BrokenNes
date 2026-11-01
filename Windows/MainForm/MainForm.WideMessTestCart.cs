using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using NesEmulator.Systems;

namespace BrokenNes.Windows
{
    /// <summary>
    /// Console > WideMess test cartridge: the WideMess conformance cartridge (WideMess/testcart, embedded in the program as widemess-testcart.wms) started on the WideMess console. Its menu
    /// runs WideMess's picture, sound, speed and memory tests live; HTTP: POST /api/widemess/testcart does the same.
    /// </summary>
    public partial class MainForm
    {
        private ToolStripMenuItem? wmsTestCartItem;

        private ToolStripItem BuildWideMessTestCartItem()
        {
            wmsTestCartItem = new ToolStripMenuItem("WideMess test cartridge", null, (s, e) =>
            {
                string? why = OpenWideMessTestCart();
                if (why != null) MessageBox.Show(this, why, "WideMess test cartridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
            })
            {
                Visible = Consoles.WideMessEnabled,
                ToolTipText = "WideMess's test cartridge on the WideMess console: its menu runs the 8 layers, sprites, windows and colour math, the SNES modes 400 wide, the 32 voices, fast CPU and DMA, the memories and the identity tests, live (hold Select at power-on to run every self-check)",
            };
            if (consoleMenu != null) consoleMenu.DropDownOpening += (s, e) => wmsTestCartItem.Visible = Consoles.WideMessEnabled;
            return wmsTestCartItem;
        }

        /// <summary>The embedded cartridge on the WideMess console; null when it started, else why not.</summary>
        private string? OpenWideMessTestCart()
        {
            if (!Consoles.IsAvailable(ConsoleKind.WideMess)) return "The WideMess console is not offered in this build.";
            byte[]? cart = EmbeddedWideMessTestCart();
            if (cart == null) return "The test cartridge is not in this build (WideMess/testroms/bin/widemess-testcart.wms).";
            LoadRomBytes("widemess-testcart.wms", cart, null, addToRecentRoms: false);
            return null;
        }

        /// <summary>widemess-testcart.wms from the program's resources.</summary>
        internal static byte[]? EmbeddedWideMessTestCart()
        {
            var assembly = typeof(MainForm).Assembly;
            string? name = assembly.GetManifestResourceNames().FirstOrDefault(r => r.EndsWith("widemess-testcart.wms", StringComparison.OrdinalIgnoreCase));
            if (name == null) return null;
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream == null) return null;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }

        private void WireWideMessTestCartApi(WebApi.WebApiServer server)
        {
            server.OpenWideMessTestCart = () =>
            {
                string? why = null;
                Invoke(() => why = OpenWideMessTestCart());
                return why;
            };
        }
    }
}
