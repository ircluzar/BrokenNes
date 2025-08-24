/* imagine.js: onnxruntime-web interop for 6502 span predictor
 * Exposes window.imagine with:
 *  - loadModel(epoch) -> { ok, info|error }
 *  - predictSpan({ window, holeStart, holeEnd, temperature, topK }) -> { bytes }
 */
(function () {
  const CDN_ORT = "https://cdn.jsdelivr.net/npm/onnxruntime-web/dist/ort.min.js";
  const CDN_ORT_BASE = "https://cdn.jsdelivr.net/npm/onnxruntime-web/dist/";
  const LOG = "[imagine]";

  let session = null;
  let epUsed = null;

  async function ensureOrt() {
    if (globalThis.ort) return globalThis.ort;
    await new Promise((resolve, reject) => {
      const s = document.createElement("script");
      s.src = CDN_ORT;
      s.async = true;
      s.onload = () => resolve();
      s.onerror = (e) => reject(new Error("Failed to load onnxruntime-web"));
      document.head.appendChild(s);
    });
    // Configure WASM asset path explicitly
    if (globalThis.ort && globalThis.ort.env && globalThis.ort.env.wasm) {
      globalThis.ort.env.wasm.wasmPaths = CDN_ORT_BASE;
    }
  console.info(LOG, "onnxruntime-web loaded");
    return globalThis.ort;
  }

  async function tryCreateSession(modelUrl, providers) {
    const ort = await ensureOrt();
    const opts = { executionProviders: providers.filter(Boolean) };
    return await ort.InferenceSession.create(modelUrl, opts);
  }

  async function loadModel(epoch) {
    try {
  const base = document.querySelector('base')?.getAttribute('href') || './';
  const modelUrl = `${base}models/6502_span_predictor_epoch${epoch}.onnx`;
      console.info(LOG, "loading model", modelUrl);
      // Prefer WASM; optionally try WebGL if available
      try {
        session = await tryCreateSession(modelUrl, ["wasm"]);
        epUsed = "wasm";
        console.info(LOG, "session created", { ep: epUsed });
      } catch (e1) {
        try {
          session = await tryCreateSession(modelUrl, ["webgl", "wasm"]);
          epUsed = "webgl";
          console.info(LOG, "session created after wasm fallback", { ep: epUsed });
        } catch (e2) {
          throw new Error(`Failed to create ONNX session: ${e2?.message || e2}`);
        }
      }
      console.info(LOG, "model ready", { epoch, ep: epUsed });
      return { ok: true, info: epUsed };
    } catch (err) {
      console.error(LOG, "loadModel error", err);
      return { ok: false, error: String(err?.message || err) };
    }
  }

  function softmaxInPlace(arr, temperature) {
    const n = arr.length;
    if (!temperature || temperature <= 0) {
      // No softmax when temperature <= 0; caller will argmax directly
      return;
    }
    const invT = 1.0 / temperature;
    let maxv = -Infinity;
    for (let i = 0; i < n; i++) if (arr[i] > maxv) maxv = arr[i];
    let sum = 0;
    for (let i = 0; i < n; i++) {
      const v = Math.exp((arr[i] - maxv) * invT);
      arr[i] = v;
      sum += v;
    }
    if (sum > 0) {
      for (let i = 0; i < n; i++) arr[i] /= sum;
    }
  }

  function sampleFromRow(logitsRow, temperature, topK) {
    // logitsRow length 257; ignore index 256 (MASK)
    const N = logitsRow.length;
    const probs = new Float32Array(N);
    // Copy logits and zero out MASK channel by setting to very low before softmax
    for (let i = 0; i < N; i++) probs[i] = logitsRow[i];
    probs[256] = -1e9;

    if (!temperature || temperature <= 0) {
      // Argmax over first 256 entries
      let bestI = 0, bestV = -Infinity;
      for (let i = 0; i < 256; i++) if (probs[i] > bestV) { bestV = probs[i]; bestI = i; }
      return bestI | 0;
    }

    softmaxInPlace(probs, temperature);

    if (topK && topK > 0 && topK < 256) {
      // Build topK list
      const pairs = [];
      for (let i = 0; i < 256; i++) pairs.push([probs[i], i]);
      pairs.sort((a, b) => b[0] - a[0]);
      const K = Math.min(topK, pairs.length);
      let sum = 0;
      for (let i = 0; i < K; i++) sum += pairs[i][0];
      let r = Math.random() * sum;
      for (let i = 0; i < K; i++) {
        r -= pairs[i][0];
        if (r <= 0) return pairs[i][1];
      }
      return pairs[K - 1][1];
    }

    // Argmax after softmax
    let bestI = 0, bestV = -1;
    for (let i = 0; i < 256; i++) if (probs[i] > bestV) { bestV = probs[i]; bestI = i; }
    return bestI | 0;
  }

  async function predictSpan(req) {
    if (!session) throw new Error("Model not loaded. Call imagine.loadModel(epoch) first.");
    const windowArr = req.window;
    const L = 128;
    if (!Array.isArray(windowArr) || windowArr.length !== L) throw new Error("window must be length 128");
    const holeStart = req.holeStart | 0;
    const holeEnd = req.holeEnd | 0;
    if (holeStart < 0 || holeEnd > 128 || holeEnd <= holeStart) throw new Error("invalid holeStart/holeEnd");
    const temperature = (req.temperature ?? 0.4);
    const topK = req.topK ?? 32;

    // Prepare input tensor: int64 [1, 128] as BigInt64Array
    const { ort } = globalThis;
    const data = new BigInt64Array(L);
    for (let i = 0; i < L; i++) data[i] = BigInt(windowArr[i] | 0);
    const input = new ort.Tensor("int64", data, [1, L]);

    const feeds = { tokens: input };
    const outputs = await session.run(feeds);
    const logits = outputs.logits; // ort.Tensor float32 [1,128,257]
    const shape = logits.dims;
    if (!(shape.length === 3 && shape[1] === 128 && shape[2] === 257)) {
      throw new Error(`unexpected logits shape: ${shape}`);
    }
    // Log once per page load on first predict
    if (!predictSpan._loggedShape) {
      console.info(LOG, "predict ready", { ep: epUsed, logitsShape: shape });
      predictSpan._loggedShape = true;
    }
    const rowSize = 257;
    const bytes = [];
    const buf = logits.data; // Float32Array

    for (let i = holeStart; i < holeEnd; i++) {
      const offset = i * rowSize;
      const row = new Float32Array(buf.buffer, buf.byteOffset + offset * 4, rowSize);
      const b = sampleFromRow(row, temperature, topK);
      bytes.push(b);
    }
    return { bytes };
  }

  globalThis.imagine = {
    loadModel,
    predictSpan,
    _debug: () => ({ ep: epUsed, hasSession: !!session })
  };
})();
