using System;
using System.Threading.Tasks;
using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;

namespace BrokenNes
{
    public partial class Emulator
    {
        // ================= UI / Mobile View Helpers (migrated) =================
    // UI state (owned here)
    private string mobileFsView = "controller"; // controller | rtc | gh
    private bool touchControllerInitialized = false;
    public string MobileFullscreenView => mobileFsView;
        public void ViewController() => SetMobileFsView("controller");
        public void ViewRtc() => SetMobileFsView("rtc");
        public void ViewGh() => SetMobileFsView("gh");

        private void SetMobileFsView(string v)
        {
            v = v.ToLowerInvariant();
            if (v != "controller" && v != "rtc" && v != "gh") return;
            if (mobileFsView == v) return;
            mobileFsView = v;
            if (mobileFsView == "controller") touchControllerInitialized = false;
            try { JS.InvokeVoidAsync("nesInterop.syncMobileView", v); } catch {}
            StateHasChanged();
        }

        [JSInvokable]
        public void JsSetMobileFsView(string v) => SetMobileFsView(v);

        // Debug dump exposure
        public string DebugDump => debugDump;
        public async Task DumpStatePublicAsync(){ DumpState(); await Task.CompletedTask; }
    }
}
