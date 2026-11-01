using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using NesEmulator.Snes;
using NesEmulator.Systems;

namespace BrokenNes.Workshop;

/// <summary>
/// BrokenDev's <c>verify</c> for a WideMess project (console <c>wms</c>, BrokenDev/widemess): the scenario runs on the WideMess console and its
/// <c>expect.wms</c> keys check what the game uses, measured from outside it (<see cref="WideMessCli.Measure"/>):
/// <c>unlocked</c>, <c>width</c>, <c>layers8</c>, <c>fastCpu</c> (true/false, at the last frame); <c>minLayers</c> (layers that draw when drawn alone),
/// <c>minSprites</c> (WideMess-table sprites on the picture), <c>minBigWramBytes</c> (non-zero bytes of work RAM above the SNES's 128 KB), all at the
/// last frame; <c>minVoices</c> (the most of voices 8-31 sounding in one frame, from <c>expect.audio.fromFrame</c>), <c>minVoiceFrames</c> (frames with
/// at least one of voices 8-31 sounding, same window).
/// </summary>
internal static partial class BrokenDevCli
{
    private sealed partial class ScenarioResult
    {
        public WideMessCli.DevkitReport? Wms;
        public int WmsMaxVoices = -1, WmsVoiceFrames;
    }

    /// <summary>Each frame of a WideMess scenario: voices 8-31 sounding (counted in the audio window).</summary>
    private static void WideMessFrame(ScenarioResult res, SnesSession s, bool inWindow)
    {
        if (!inWindow) return;
        int n = WideMessCli.WideVoicesSounding(s);
        if (n > res.WmsMaxVoices) res.WmsMaxVoices = n;
        if (n > 0) res.WmsVoiceFrames++;
    }

    private static void EvaluateWideMess(ScenarioResult r, JsonElement? expect)
    {
        if (expect is not { } e || !e.TryGetProperty("wms", out var w)) return;
        void Add(string id, bool ok, string detail) => r.Checks.Add(new CheckResult { Id = id, Ok = ok, Detail = detail });
        var m = r.Wms;
        if (m == null) { Add("wms", false, "the scenario did not run on the WideMess console (the manifest's console must be wms)"); return; }
        if (w.TryGetProperty("unlocked", out var u)) Add("wms-unlocked", m.Unlocked == u.GetBoolean(), $"unlocked={m.Unlocked}");
        if (w.TryGetProperty("width", out var wd)) Add("wms-width", m.Width == wd.GetInt32(), $"the picture is {m.Width} x {m.Height}");
        if (w.TryGetProperty("layers8", out var l8)) Add("wms-layers8", m.Layers8 == l8.GetBoolean(), $"WMCTRL LAYERS8={m.Layers8}");
        if (w.TryGetProperty("fastCpu", out var fc)) Add("wms-fast-cpu", m.FastCpu == fc.GetBoolean(), $"fast CPU={m.FastCpu}");
        if (w.TryGetProperty("minLayers", out var ml)) Add("wms-layers", m.LayersDrawing >= ml.GetInt32(), $"{m.LayersDrawing} layers draw (pixels each: {string.Join(" ", m.LayerPixels)}), need >= {ml.GetInt32()}");
        if (w.TryGetProperty("minSprites", out var ms)) Add("wms-sprites", m.SpritesOnScreen >= ms.GetInt32(), $"{m.SpritesOnScreen} WideMess sprites on the picture (table on: {m.WideSprites}), need >= {ms.GetInt32()}");
        if (w.TryGetProperty("minBigWramBytes", out var mb)) Add("wms-big-wram", m.BigWramBytes >= mb.GetInt32(), $"{m.BigWramBytes:N0} non-zero bytes above the SNES's 128 KB, need >= {mb.GetInt32():N0}");
        if (w.TryGetProperty("minVoices", out var mv)) Add("wms-voices", r.WmsMaxVoices >= mv.GetInt32(), $"at most {Math.Max(0, r.WmsMaxVoices)} of voices 8-31 sounding in a frame, need >= {mv.GetInt32()}");
        if (w.TryGetProperty("minVoiceFrames", out var mf)) Add("wms-voice-frames", r.WmsVoiceFrames >= mf.GetInt32(), $"{r.WmsVoiceFrames} frames with voices 8-31 sounding, need >= {mf.GetInt32()}");
    }

    private static JsonNode? WideMessJson(ScenarioResult r) => r.Wms is not { } m ? null : new JsonObject
    {
        ["unlocked"] = m.Unlocked, ["width"] = m.Width, ["height"] = m.Height, ["wide"] = m.Wide, ["layers8"] = m.Layers8,
        ["layersDrawing"] = m.LayersDrawing, ["layerPixels"] = new JsonArray(Array.ConvertAll(m.LayerPixels, p => (JsonNode)p)),
        ["spritesOnScreen"] = m.SpritesOnScreen, ["wideSpriteTable"] = m.WideSprites, ["fastCpu"] = m.FastCpu, ["fastDma"] = m.FastDma,
        ["bigWramBytes"] = m.BigWramBytes, ["maxWideVoices"] = r.WmsMaxVoices, ["wideVoiceFrames"] = r.WmsVoiceFrames,
    };
}
