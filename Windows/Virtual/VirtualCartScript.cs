namespace BrokenNes.Windows.Virtual
{
    /// <summary>
    /// The script BrokenNes puts into a virtual cartridge's page before any of the page's own scripts run (nothing is changed in the game's files):
    /// <list type="bullet">
    /// <item>WebGL canvases keep their drawing buffer (<c>preserveDrawingBuffer</c>), so the compositor can read the game's WebGL layer after the frame was drawn.</item>
    /// <item>Every Web Audio context gets a host gain in front of its speakers: BrokenNes's volume.</item>
    /// <item><c>navigator.getGamepads()</c> reports one standard-mapping pad, BrokenNes's pad (1P). The game's own input module reads it like any gamepad, so its
    /// own bindings, menus included, stay the reference.</item>
    /// <item>The compositor: each animation frame (at most 60 a second) it draws the stacked layers, back to front with their opacity, into one W x H picture and
    /// copies its RGBA pixels into the shared buffer BrokenNes posted (two slots), then tells BrokenNes which slot holds the new frame.</item>
    /// </list>
    /// </summary>
    internal static class VirtualCartScript
    {
        public static string For(int width, int height, string layers) => Template
            .Replace("__W__", width.ToString())
            .Replace("__H__", height.ToString())
            .Replace("__LAYERS__", System.Text.Json.JsonSerializer.Serialize(layers));

        private const string Template = """
(function () {
  "use strict";
  if (window.top !== window || window.__brokenNesCart) return;
  var W = __W__, H = __H__, LAYERS = __LAYERS__;
  var host = window.chrome && window.chrome.webview;
  var cart = window.__brokenNesCart = { frames: 0, pad: 0, padSeq: 0, volume: 1, error: "" };

  // ---- 1. readable WebGL layers
  var getContext = HTMLCanvasElement.prototype.getContext;
  HTMLCanvasElement.prototype.getContext = function (type, attrs) {
    if (type === "webgl" || type === "webgl2" || type === "experimental-webgl") {
      var a = {}; if (attrs) for (var k in attrs) a[k] = attrs[k];
      a.preserveDrawingBuffer = true; attrs = a;
    }
    return getContext.call(this, type, attrs);
  };

  // ---- 2. BrokenNes's volume on every Web Audio context
  var gains = [];
  function wrapAudio(name) {
    var AC = window[name]; if (!AC) return;
    var destGetter = Object.getOwnPropertyDescriptor(AC.prototype, "destination") || Object.getOwnPropertyDescriptor(BaseAudioContext.prototype, "destination");
    function HostAudioContext(opts) {
      var ctx = opts === undefined ? new AC() : new AC(opts);
      try {
        var real = destGetter.get.call(ctx);
        var g = ctx.createGain(); g.gain.value = cart.volume; g.connect(real);
        Object.defineProperty(ctx, "destination", { value: g, configurable: true });
        gains.push(g);
      } catch (e) { cart.error = "volume: " + e; }
      return ctx;
    }
    HostAudioContext.prototype = AC.prototype;
    window[name] = HostAudioContext;
  }
  wrapAudio("AudioContext"); wrapAudio("webkitAudioContext");
  function setVolume(v) { cart.volume = v; gains.forEach(function (g) { try { g.gain.value = v; } catch (e) {} }); }

  // ---- 3. BrokenNes's pad as a standard gamepad (bit i = button i of the standard mapping)
  var buttons = []; for (var i = 0; i < 18; i++) buttons.push({ pressed: false, touched: false, value: 0 });
  var pad = { id: "BrokenNes pad (standard)", index: 0, connected: true, mapping: "standard", timestamp: 0, axes: [0, 0, 0, 0], buttons: buttons, vibrationActuator: null };
  function setPad(mask) {
    cart.pad = mask;
    for (var i = 0; i < 18; i++) { var on = (mask >>> i & 1) === 1; buttons[i] = { pressed: on, touched: on, value: on ? 1 : 0 }; }
    pad.buttons = buttons.slice(); pad.timestamp = performance.now();
  }
  try { Object.defineProperty(Navigator.prototype, "getGamepads", { value: function () { return [pad, null, null, null]; }, configurable: true }); } catch (e) { cart.error = "pad: " + e; }

  // ---- 4. messages from BrokenNes
  var shared = null;
  if (host) {
    host.addEventListener("message", function (e) {
      var m = e.data || {};
      if (typeof m.pad === "number") { setPad(m.pad); cart.padSeq = m.seq || 0; }
      if (typeof m.volume === "number") setVolume(m.volume);
    });
    host.addEventListener("sharedbufferreceived", function (e) { shared = new Uint8Array(e.getBuffer()); });
  }

  // ---- 5. the compositor
  var out = null, octx = null, last = 0, slot = 0;
  function opacityOf(el, root) {
    var o = 1;
    for (var n = el; n && n.nodeType === 1; n = n.parentElement) {
      var cs = getComputedStyle(n);
      if (cs.display === "none" || cs.visibility === "hidden") return 0;
      o *= parseFloat(cs.opacity);
      if (n === root || o <= 0) break;
    }
    return o;
  }
  function composite(now) {
    requestAnimationFrame(composite);
    if (!shared || now - last < 15) return;
    last = now;
    try {
      if (!out) { out = document.createElement("canvas"); out.width = W; out.height = H; octx = out.getContext("2d", { willReadFrequently: true, alpha: false }); octx.imageSmoothingEnabled = false; }
      octx.globalAlpha = 1; octx.fillStyle = "#000"; octx.fillRect(0, 0, W, H);
      var els = document.querySelectorAll(LAYERS);
      var root = document.getElementById("screen") || document.body;
      for (var i = 0; i < els.length; i++) {
        var el = els[i];
        if (el.tagName === "VIDEO" && (el.readyState < 2 || !el.videoWidth)) continue;
        if (el.tagName === "CANVAS" && (!el.width || !el.height)) continue;
        var a = opacityOf(el, root); if (a <= 0) continue;
        octx.globalAlpha = Math.min(1, a);
        octx.drawImage(el, 0, 0, W, H);
      }
      var px = octx.getImageData(0, 0, W, H).data;
      slot ^= 1;
      shared.set(px, slot * W * H * 4);
      cart.frames++;
      host.postMessage("f," + cart.frames + "," + slot + "," + cart.padSeq);
    } catch (e) { cart.error = "compositor: " + e; host.postMessage("e," + String(e)); }
  }
  requestAnimationFrame(composite);
})();
""";
    }
}
