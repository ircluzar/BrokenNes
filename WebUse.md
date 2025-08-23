# WebUse: Deploying 6502_span_predictor_epoch25.pt in a Web App

This document explains how to use the trained 6502 span predictor model (6502_span_predictor_epoch25.pt) to predict/repair bytes in a web application.

Contents
- What the model expects (inputs and outputs)
- How to predict masked spans (bidirectional context)
- How to generate a sequence with only previous bytes (forward/autoregressive)
- Integration patterns for the web (server-backed and browser-only)
- Best-case use scenarios and practical tips

1) What the model expects
- Task: Masked-span reconstruction for 6502 PRG bytes.
- Context length (seq_len): 128 tokens.
- Vocabulary (vocab_size): 257 tokens ? 0..255 are byte values, 256 is the MASK token.
- Input format: A contiguous window of 128 integers where any unknown bytes (the “hole”) are replaced by MASK (256). The window should include real bytes before and after the hole whenever available (bidirectional context).
- Output: For every position in the 128-long window, the model produces a distribution over 257 tokens. For masked positions, select/sampler the top predicted value in 0..255 and use it as the reconstructed byte. Optionally capture per-byte confidence from the softmax.

Important: The model performs best when both left (preceding) and right (following) context are present around the hole.

2) Predicting a masked span (bidirectional)
Given a byte array rom[0..N-1] and a hole [holeStart, holeEnd):
- Extract a 128-byte window centered on the hole. If the hole is near the start/end of the ROM, shift the window so it stays inside bounds.
- Convert the window to int[] tokens: 0..255 for known bytes; 256 for masked bytes at indices inside [holeStart, holeEnd) relative to the window.
- Run a forward pass. From the logits for each masked index, sample or argmax to produce a byte in 0..255.
- Write the predicted bytes back into rom.

Pseudocode (framework agnostic):
```
windowStart = best_start_around(holeStart, holeEnd, seq_len=128, romLength=N)
windowBytes = rom[windowStart .. windowStart+128)

tokens = int[128]
for i in 0..127:
  globalIndex = windowStart + i
  if holeStart <= globalIndex < holeEnd:
    tokens[i] = 256  # MASK token
  else:
    tokens[i] = windowBytes[i]  # 0..255

logits = model.forward(tokens)             # shape: [128, 257]
probs  = softmax(logits / temperature)

pred = byte[holeLen]
for i in 0..holeLen-1:
  pos = (holeStart - windowStart) + i
  dist = probs[pos][0..256]
  dist[256] = 0                           # don’t sample MASK as output
  pred[i] = sample_topk(dist, k=topK)     # or argmax

rom[holeStart .. holeEnd) = pred
```

Notes
- temperature: 0.1–0.7 typical; lower is more deterministic.
- topK: 20–50 typical; set null/None to disable.
- If the hole is larger than 32 bytes or context is weak, consider doing multiple passes, or bidirectional + ensemble strategies.

3) Generating bytes with only previous context (forward/autoregressive)
Even though the model is trained for masked spans with both sides of context, you can still generate a sequence using only the left context.

Method
- Place the hole at the end of your 128-token window (no right context). Mask the region you want to generate.
- Predict one byte at a time: after you choose the next byte, write it back into the window, slide the window (if needed), and predict the next.

Pseudocode (generate L bytes):
```
# leftContext holds the last up-to-128 bytes prior to generation
window = make_window_of_len_128(leftContext, maskCount=L)
for t in 0..L-1:
  tokens = to_tokens_with_tail_mask(window)  # final L-t positions set to MASK=256
  logits = model.forward(tokens)
  pos = 128 - (L - t)                        # current masked position index
  probs = softmax(logits[pos] / temperature)
  probs[256] = 0
  nextByte = sample_topk(probs, k=topK)      # or argmax
  window[pos] = nextByte                     # unmask by writing the choice

  # Optionally slide the window if you want a rolling 128-byte context
  # window = slide_right(window)

emit(window[128-L .. 128))
```

4) Integration patterns for the web
A) Server-backed (Python + PyTorch, simplest path)
- Keep 6502_span_predictor_epoch25.pt on the server.
- Expose a /predict-span API that accepts: bytes (window), holeStart (relative to window), holeEnd (relative), temperature, topK.
- Return predicted bytes and optional confidence.

Minimal Python (sketch):
```python
import torch
from fastapi import FastAPI
from pydantic import BaseModel

class Req(BaseModel):
    window: list[int]     # length 128, ints in [0..256]
    hole_start: int
    hole_end: int
    temperature: float = 0.5
    top_k: int | None = 50

app = FastAPI()
model = torch.load("6502_span_predictor_epoch25.pt", map_location="cpu")
model.eval()

@torch.no_grad()
@app.post("/predict-span")
def predict_span(req: Req):
    x = torch.tensor(req.window, dtype=torch.long).unsqueeze(0)  # [1, 128]
    logits = model(x)[0]  # [128, 257]

    def sample(logit_row):
        if req.temperature > 0:
            logit_row = logit_row / req.temperature
        probs = torch.softmax(logit_row, dim=-1)
        probs[256] = 0.0  # never output MASK
        if req.top_k and req.top_k > 0:
            v, i = torch.topk(probs, req.top_k)
            i = i.tolist(); v = (v / v.sum()).tolist()
            return int(random.choices(i, weights=v, k=1)[0])
        return int(torch.argmax(probs[:256]))

    out = []
    for i in range(req.hole_start, req.hole_end):
        out.append(sample(logits[i]))
    return {"bytes": out}
```

B) Browser-only, Pure WebAssembly (Blazor/TypeScript)
- Convert/export model weights to a web-friendly format (JSON/array buffers) and implement inference in the browser.
- In this repository, the WasmTransformerPredictor API demonstrates how to feed a 128-token window with MASK=256 and get predictions per masked index. Use it directly in Blazor or port the same logic to TypeScript.

C# (Blazor, sketch):
```csharp
var padded = new int[128];
// copy context bytes into padded, set masked indexes to 256
var result = await predictor.PredictSpanAsync(padded, holeStart, holeEnd, temperature: 0.4f, topK: 32);
// result.PredictedBytes are the reconstructed bytes
```

C) Browser with ONNX Runtime Web
- If you export to ONNX, you can run inference via onnxruntime-web (WASM/WebGL backends).
- Feed a [1, 128] int64/int32 tensor with values in [0..256]. Read back logits [1, 128, 257], then sample masked indices.

5) Best-case use scenarios and tips
What the model was trained for
- Masked language modeling over 6502 PRG bytes: it learns to reconstruct contiguous spans of length 8–32 using both left and right context.
- Sequence length 128: predictions depend on a 128-byte window around the hole.

Works best when
- Hole length is 8–32 bytes.
- Both sides of context are available in the 128-byte window (center your window on the hole).
- The region shares patterns with nearby code/data (repeated instruction motifs, common tables, mirrored data, bank-local patterns).

Less ideal when
- The hole exceeds ~32 bytes, or very limited context exists on either side.
- The content is truly novel (not represented in training data) or crosses unrelated banks.
- The hole sits at ROM boundaries where you can’t provide bidirectional context.

Operational tips
- Use temperature 0.2–0.5 and topK 20–50 for deterministic, reliable fixes.
- For long spans, consider iterative filling (predict a part, then re-run with updated context) or forward/backward passes with confidence-based merge.
- Keep your window length at 128. If your file is large, select a centered 128-byte slice around the hole rather than padding with many irrelevant bytes.
- Validate critical patches in an emulator; use ensembles (multiple samples) for higher confidence.

Appendix: Shapes and tokens
- Input: [128] int tokens, values in [0..256]; 256 is MASK.
- Output: [128, 257] logits; apply softmax, ignore token 256 when sampling outputs.
- Byte mapping: model token 0..255 ? actual byte value 0x00..0xFF.

This guidance applies directly to 6502_span_predictor_epoch25.pt and later checkpoints trained with the same configuration (seq_len=128, vocab_size=257 with MASK=256).