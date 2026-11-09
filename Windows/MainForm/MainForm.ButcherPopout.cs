using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace BrokenNes.Windows
{
    // Butchering popout (desktop only, decided 2026-11-07): the module page calls window.open('index.html?popout=1') and the
    // app gives it a normal window of its own (own taskbar entry, not on top). The page moves its whole UI there; closing the
    // window asks each time: dock back, or Exit & undo. It remembers nothing: a fresh default window every time.
    public partial class MainForm
    {
        private ButcherPopoutForm? butcherPopout;
        internal ButcherPopoutForm? ButcherPopout => butcherPopout is { IsDisposed: false } ? butcherPopout : null;

        private void HookWebViewNewWindows(WebView2 view)
        {
            if (view.CoreWebView2 == null) return;
            view.CoreWebView2.NewWindowRequested += async (_, e) =>
            {
                if (!IsButcherPopoutUri(e.Uri)) return;   // anything else keeps WebView2's own behaviour
                e.Handled = true;
                var deferral = e.GetDeferral();
                try
                {
                    butcherPopout?.CloseWithoutPrompt();
                    var form = new ButcherPopoutForm();
                    butcherPopout = form;
                    form.FormClosed += (_, _) => { if (butcherPopout == form) butcherPopout = null; };
                    form.Show();   // ownerless: a normal window with its own taskbar entry
                    if (await form.InitializeAsync()) e.NewWindow = form.CoreWebView2;
                    else form.CloseWithoutPrompt();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ButcherPopout] could not open: {ex.Message}");
                }
                finally { deferral.Complete(); }
            };
        }

        // HTTP API (tests): "status", "open" (the panel's Pop out button), "dock" / "exit" (the close prompt's two answers),
        // "prompt" (shows the close prompt as the X button does, without waiting for it). Runs on the UI thread.
        private Task<object> ButcherPopoutControlAsync(string action)
        {
            var tcs = new TaskCompletionSource<object>();
            BeginInvoke(new Action(async () =>
            {
                try
                {
                    var p = ButcherPopout;
                    object Status(object? extra = null) => new { success = true, open = ButcherPopout != null, handle = ButcherPopout?.Handle.ToInt64() ?? 0, result = extra };
                    switch (action)
                    {
                        case "open":
                            if (webView?.CoreWebView2 == null) { tcs.SetResult(new { success = false, error = "no overlay page" }); return; }
                            var r = await webView.CoreWebView2.ExecuteScriptAsync("(() => { const b = document.getElementById('popOut'); if (!b) return 'no-button'; b.click(); return 'clicked'; })()");
                            for (int i = 0; i < 40 && ButcherPopout == null; i++) await Task.Delay(50);
                            tcs.SetResult(Status(r.Trim('"')));
                            return;
                        case "dock":
                        case "exit":
                            if (p == null) { tcs.SetResult(new { success = false, error = "no popout open" }); return; }
                            tcs.SetResult(Status(await p.FinishAsync(action)));
                            return;
                        case "prompt":
                            if (p == null) { tcs.SetResult(new { success = false, error = "no popout open" }); return; }
                            tcs.SetResult(Status("asking"));
                            p.Close();   // the X button's path: the prompt shows; the call does not wait for the answer
                            return;
                        default:
                            tcs.SetResult(Status());
                            return;
                    }
                }
                catch (Exception ex) { tcs.TrySetResult(new { success = false, error = ex.Message }); }
            }));
            return tcs.Task;
        }

        private static bool IsButcherPopoutUri(string uri) =>
            uri.Contains("/ButcheringMode/", StringComparison.OrdinalIgnoreCase) && uri.Contains("popout=1", StringComparison.OrdinalIgnoreCase);
    }

    internal sealed class ButcherPopoutForm : Form
    {
        private readonly WebView2 view = new() { Dock = DockStyle.Fill };
        private bool allowClose;
        private bool asking;

        public CoreWebView2? CoreWebView2 => view.CoreWebView2;

        public ButcherPopoutForm()
        {
            Text = "Butchering Mode";
            StartPosition = FormStartPosition.WindowsDefaultLocation;
            Size = new Size(1180, 760);
            MinimumSize = new Size(560, 360);
            BackColor = Color.FromArgb(17, 18, 20);
            ShowInTaskbar = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { /* default icon */ }
            Controls.Add(view);
        }

        public async Task<bool> InitializeAsync()
        {
            if (!await Helpers.WebViewHelper.InitializeWebViewAsync(view, showErrorDialog: false)) return false;
            view.DefaultBackgroundColor = Color.FromArgb(17, 18, 20);
            var core = view.CoreWebView2;
            core.WindowCloseRequested += (_, _) => CloseWithoutPrompt();   // the page closed itself (docked, exited, or the game left the mode)
            core.DocumentTitleChanged += (_, _) => { if (!string.IsNullOrWhiteSpace(core.DocumentTitle)) Text = core.DocumentTitle; };
            return true;
        }

        public void CloseWithoutPrompt()
        {
            if (IsDisposed) return;
            allowClose = true;
            if (InvokeRequired) BeginInvoke(new Action(Close)); else Close();
        }

        /// <summary>Asks the page to dock back ("dock") or Exit &amp; undo ("exit"), then closes. Also used by the HTTP API.</summary>
        public async Task<string> FinishAsync(string choice)
        {
            string result = "gone";
            try
            {
                if (view.CoreWebView2 != null)
                {
                    var r = await view.CoreWebView2.ExecuteScriptAsync(
                        $"(window.__butcherPopoutClose ? window.__butcherPopoutClose('{(choice == "exit" ? "exit" : "dock")}') : 'gone')");
                    result = r.Trim('"');
                }
            }
            catch (Exception ex) { Console.WriteLine($"[ButcherPopout] close handoff failed: {ex.Message}"); }
            CloseWithoutPrompt();
            return result;
        }

        protected override async void OnFormClosing(FormClosingEventArgs e)
        {
            if (allowClose || e.CloseReason == CloseReason.WindowsShutDown || e.CloseReason == CloseReason.TaskManagerClosing)
            {
                base.OnFormClosing(e);
                return;
            }
            e.Cancel = true;
            if (asking) return;
            asking = true;
            try
            {
                var choice = AskHowToClose();
                if (choice != null) await FinishAsync(choice);
            }
            finally { asking = false; }
        }

        // Asked every time (nothing remembered): dock back, or Exit & undo, or keep the window.
        private string? AskHowToClose()
        {
            var dock = new TaskDialogButton("Dock back") { Tag = "dock" };
            var exit = new TaskDialogButton("Exit && undo") { Tag = "exit" };
            var page = new TaskDialogPage
            {
                Caption = "Butchering Mode",
                Heading = "Close the Butchering window?",
                Text = "Dock back: the controls return to the game window, every effect stays on.\n" +
                       "Exit & undo: leave Butchering mode; the state taken when you entered is reloaded (progress included).",
                Buttons = { dock, exit, TaskDialogButton.Cancel },
                DefaultButton = dock,
                AllowCancel = true,
            };
            var picked = TaskDialog.ShowDialog(this, page, TaskDialogStartupLocation.CenterOwner);
            return picked.Tag as string;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) view.Dispose();
            base.Dispose(disposing);
        }
    }
}
