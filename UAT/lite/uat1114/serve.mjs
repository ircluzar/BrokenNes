// Static server for a published Lite wwwroot plus a ROM folder at /roms/ (CORS on, SPA fallback).
// usage: node serve.mjs <wwwroot> <romDir> <port>
import http from 'node:http'; import fs from 'node:fs'; import path from 'node:path';
const [root, roms, port] = process.argv.slice(2);
const types = { '.html':'text/html', '.js':'text/javascript', '.mjs':'text/javascript', '.css':'text/css', '.json':'application/json', '.wasm':'application/wasm', '.dat':'application/octet-stream', '.png':'image/png', '.svg':'image/svg+xml', '.ico':'image/x-icon' };
http.createServer((req, res) => {
  let p = decodeURIComponent(new URL(req.url, 'http://x').pathname);
  let base = root;
  if (p.startsWith('/roms/')) { base = roms; p = p.slice(5); }
  let f = path.join(base, p);
  if (!f.startsWith(path.resolve(base))) { res.writeHead(403); return res.end(); }
  if (!fs.existsSync(f) || fs.statSync(f).isDirectory()) f = base === roms ? null : path.join(root, 'index.html');
  if (!f || !fs.existsSync(f)) { res.writeHead(404); return res.end(); }
  res.writeHead(200, { 'Content-Type': types[path.extname(f).toLowerCase()] || 'application/octet-stream', 'Access-Control-Allow-Origin': '*', 'Cache-Control': 'no-store' });
  fs.createReadStream(f).pipe(res);
}).listen(+port, '127.0.0.1', () => console.log('serving', root, 'on', port));
