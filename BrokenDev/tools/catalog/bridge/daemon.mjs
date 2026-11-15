// BrokenNes MCP bridge daemon: one stdio MCP server, exposed on 127.0.0.1:PORT (POST /call, GET /tools, POST /stop)
import { spawn } from 'node:child_process';
import http from 'node:http';
const a = process.argv.slice(2), opt = k => { const i = a.indexOf(k); return i >= 0 ? a[i + 1] : undefined; };
const port = +opt('--port');
import path from 'node:path'; import { fileURLToPath } from 'node:url';
// the server: --exe, $BROKENNES_MCP, or the compat catalog's own build (never the user's installed copy)
const exe = opt('--exe') || process.env.BROKENNES_MCP || 'C:/BrokenNes-work/cc_t2_mcp/BrokenNes.Mcp.exe';
const repo = opt('--repo') || process.env.BN_REPO || path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../..');
const srv = spawn(exe, ['--repo', repo], { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
srv.stderr.on('data', () => {});
srv.on('exit', c => { console.error('server exited', c); process.exit(1); });
let buf = '', nextId = 1; const pend = new Map();
srv.stdout.on('data', d => { buf += d; let i; while ((i = buf.indexOf('\n')) >= 0) { const line = buf.slice(0, i).trim(); buf = buf.slice(i + 1); if (!line) continue;
  try { const m = JSON.parse(line); if (m.id != null && pend.has(m.id)) { pend.get(m.id)(m); pend.delete(m.id); } } catch {} } });
const rpc = (method, params) => new Promise(res => { const id = nextId++; pend.set(id, res); srv.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n'); });
let q = Promise.resolve(); const serial = f => (q = q.then(f, f));
await rpc('initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'cc-bridge', version: '1' } });
srv.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');
http.createServer((req, res) => { let body = ''; req.on('data', d => body += d); req.on('end', async () => {
  const send = o => { res.writeHead(200, { 'content-type': 'application/json' }); res.end(JSON.stringify(o)); };
  try {
    if (req.url === '/tools') return send(await serial(() => rpc('tools/list', {})));
    if (req.url === '/stop') { send({ ok: true }); srv.kill(); setTimeout(() => process.exit(0), 200); return; }
    const { tool, args } = JSON.parse(body || '{}');
    send(await serial(() => rpc('tools/call', { name: tool, arguments: args || {} })));
  } catch (e) { send({ error: { message: String(e) } }); } }); }).listen(port, '127.0.0.1', () => console.log('ready ' + port));
