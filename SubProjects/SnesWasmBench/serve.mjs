// Minimal static file server for a published Blazor WASM folder (no dependencies).
// Usage: node serve.mjs <wwwroot dir> <port>
import http from "node:http";
import fs from "node:fs";
import path from "node:path";

const root = path.resolve(process.argv[2] ?? ".");
const port = Number(process.argv[3] ?? 5021);
const types = {
  ".html": "text/html", ".js": "text/javascript", ".mjs": "text/javascript", ".json": "application/json",
  ".wasm": "application/wasm", ".dat": "application/octet-stream", ".dll": "application/octet-stream",
  ".css": "text/css", ".ico": "image/x-icon", ".smc": "application/octet-stream", ".sfc": "application/octet-stream",
};

http.createServer((req, res) => {
  let rel = decodeURIComponent(new URL(req.url, "http://x").pathname);
  if (rel.endsWith("/")) rel += "index.html";
  const file = path.join(root, rel);
  if (!file.startsWith(root) || !fs.existsSync(file) || fs.statSync(file).isDirectory()) {
    res.writeHead(404); res.end("not found"); return;
  }
  res.writeHead(200, { "Content-Type": types[path.extname(file)] ?? "application/octet-stream", "Cache-Control": "no-store" });
  fs.createReadStream(file).pipe(res);
}).listen(port, "127.0.0.1", () => console.log(`serving ${root} on http://localhost:${port}`));
