using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace BrokenNes.Windows.WebApi
{
    public partial class WebApiServer
    {
        // ------------------------------------------------------------------ the window must exist before a request touches it
        // The server starts at the very top of the MainForm constructor (so a harness can reach /api/health even while a
        // start-up dialog is up), long before the window has a handle. Until it has one, Control.InvokeRequired is FALSE on
        // every thread, so each handler's "if (_uiControl.InvokeRequired) Invoke(...) else call directly" ran the call
        // right there on the Kestrel thread - concurrently with the UI thread still running the constructor's
        // LoadEmbeddedRom. A POST /api/emulator/load-rom in that window rebuilt the core menus from two threads at once
        // ("Error loading embedded ROM: Index was out of range" from FlowLayout, or "Collection was modified").
        // So every request except /api/health waits here until the window has been shown (MarkUiReady); from then on
        // InvokeRequired is true off the UI thread and the handlers' Invoke runs the work on the UI thread, in order.

        private readonly TaskCompletionSource uiReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>How long a request waits for the window before it is refused with 503 (it is never run off the UI thread instead).</summary>
        internal static TimeSpan UiReadyTimeout = TimeSpan.FromSeconds(90);

        /// <summary>True once the window is shown and requests are served (also in /api/health as uiReady).</summary>
        public bool IsUiReady => _uiControl == null || uiReady.Task.IsCompleted;

        /// <summary>The window is shown: requests stop waiting. Called from MainForm.OnShown.</summary>
        public void MarkUiReady()
        {
            if (uiReady.TrySetResult()) Console.WriteLine("[WebApi] window ready: requests are served from now on");
        }

        /// <summary>Requests that never touch the window, answered at once even during start-up.</summary>
        private static bool AnsweredBeforeUiReady(PathString path)
            => path.Equals("/api/health", StringComparison.OrdinalIgnoreCase);

        /// <summary>Start an async UI operation (a ROM load, which builds menus and talks to the WebView) on the UI thread, so its
        /// continuations stay there too, and hand back its task to await here.</summary>
        private Task<T> OnUiAsync<T>(Func<Task<T>> work)
            => _uiControl != null && _uiControl.InvokeRequired ? (Task<T>)_uiControl.Invoke(work) : work();

        private async Task WaitForUiReadyAsync(HttpContext context, RequestDelegate next)
        {
            if (_uiControl != null && !uiReady.Task.IsCompleted && !AnsweredBeforeUiReady(context.Request.Path))
            {
                Task done;
                try { done = await Task.WhenAny(uiReady.Task, Task.Delay(UiReadyTimeout, context.RequestAborted)).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                if (done != uiReady.Task)
                {
                    if (context.RequestAborted.IsCancellationRequested) return;
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    await context.Response.WriteAsJsonAsync(new { success = false, uiReady = false,
                        error = $"BrokenNes is still starting: its window was not ready after {UiReadyTimeout.TotalSeconds:F0} s" });
                    return;
                }
            }
            await next(context);
        }
    }
}
