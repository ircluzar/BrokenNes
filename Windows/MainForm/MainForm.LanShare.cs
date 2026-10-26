using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using BrokenNes.Windows.WebApi;

namespace BrokenNes.Windows
{
    /// <summary>
    /// Emulator > Debug Windows > Share Read-Only Pages on the LAN (BrokenDev D8 case 2): the two debug pages and their read-only GET routes on
    /// the LAN address, token-gated (<see cref="DebugPagesLanShare"/>). The menu switches it for this run only (config <c>debugPagesLan</c>,
    /// off by default, only says whether it starts on); the URL with the token is shown in a small window; off closes the listener at once.
    /// </summary>
    public partial class MainForm
    {
        private DebugPagesLanShare? lanShare;
        private ToolStripMenuItem? lanShareItem;
        private Form? lanShareWindow;
        private string? lanShareWhy;

        private void AddLanShareMenu(ToolStripMenuItem debugMenu)
        {
            debugMenu.DropDownItems.Add(new ToolStripSeparator());
            lanShareItem = new ToolStripMenuItem("Share Read-Only Pages on the &LAN") { Name = "debugPagesLan", CheckOnClick = true };
            lanShareItem.ToolTipText = "Lets a phone on your network open the read-only debug pages (a token is required). Nothing else is reachable; off closes it.";
            lanShareItem.Click += async (_, _) => await SetLanShareAsync(lanShareItem.Checked);
            debugMenu.DropDownOpening += (_, _) => lanShareItem.Checked = lanShare?.IsOn == true;
            debugMenu.DropDownItems.Add(lanShareItem);
            FormClosed += (_, _) => { try { lanShare?.Dispose(); } catch { } lanShare = null; };
        }

        private object LanShareStatus() => new
        {
            success = true,
            on = lanShare?.IsOn == true,
            address = lanShare?.Address,
            port = lanShare?.Port ?? 0,
            routes = DebugPagesLanShare.Routes,
            why = lanShare?.IsOn == true ? null : lanShareWhy ?? "the share is off (Emulator > Debug Windows > Share Read-Only Pages on the LAN)",
        };

        private async Task SetLanShareAsync(bool on)
        {
            if (on)
            {
                if (lanShare?.IsOn == true) { ShowLanShareWindow(); return; }
                lanShare ??= new DebugPagesLanShare(() => webApiServer?.BaseUrl);
                if (webApiServer != null) webApiServer.GetLanShare = LanShareStatus;
                var err = await lanShare.StartAsync();
                lanShareWhy = err;
                if (lanShareItem != null) lanShareItem.Checked = lanShare.IsOn;
                if (err != null) { Console.WriteLine("[lan share] not on: " + err); MessageBox.Show(this, "The pages could not be shared on the LAN:\n" + err, "BrokenNes LAN Share", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                ShowLanShareWindow();
            }
            else
            {
                lanShareWhy = null;
                if (lanShare != null) await lanShare.StopAsync();
                if (lanShareItem != null) lanShareItem.Checked = false;
                if (lanShareWindow is { IsDisposed: false } w) w.Close();
            }
        }

        /// <summary>The URL to open on the phone (with the token), copyable; closing this window does not stop the share (the menu does).</summary>
        private void ShowLanShareWindow()
        {
            if (lanShare?.IsOn != true) return;
            if (lanShareWindow is { IsDisposed: false } open) { open.Activate(); return; }
            var f = new Form { Text = "BrokenNes LAN Share", Name = "lanShareWindow", FormBorderStyle = FormBorderStyle.FixedToolWindow, StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(560, 196), ShowInTaskbar = false };
            var note = new Label
            {
                Name = "lanShareNote", Location = new Point(10, 8), Size = new Size(540, 48),
                Text = "Open this on your phone (same network). Read only: the debug pages and their GET routes, nothing else. " +
                       "The token is new each time; switching the share off (Emulator > Debug Windows) closes it. Windows may ask about its firewall the first time.",
            };
            var url = new TextBox { Name = "lanShareUrl", ReadOnly = true, Location = new Point(10, 62), Width = 540, Text = lanShare.Url };
            var dbg = new TextBox { Name = "lanShareDebuggerUrl", ReadOnly = true, Location = new Point(10, 92), Width = 540, Text = lanShare.DebuggerUrl };
            var copy = new Button { Name = "lanShareCopy", Text = "Copy viewers URL", Location = new Point(10, 126), Width = 140 };
            copy.Click += (_, _) => { try { Clipboard.SetText(url.Text); } catch { } };
            var stop = new Button { Name = "lanShareStop", Text = "Stop sharing", Location = new Point(160, 126), Width = 120 };
            stop.Click += async (_, _) => await SetLanShareAsync(false);
            var where = new Label { Name = "lanShareWhere", Location = new Point(10, 162), Size = new Size(540, 24), Text = $"Listening on {lanShare.Address}:{lanShare.Port}" };
            f.Controls.AddRange(new Control[] { note, url, dbg, copy, stop, where });
            f.FormClosed += (_, _) => lanShareWindow = null;
            lanShareWindow = f;
            f.Show(this);
        }
    }
}
