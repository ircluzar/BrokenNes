// Client: node mcp.mjs --port P [--out DIR] <tool|tools|start|stop> [json-args]
//   start [--pc NAME] [--max-steps N] [--max-frames N] [--exe X]: --pc runs the server on that fleet PC over ssh (daemon stays here)
//   start  -> spawns a daemon on a free port, prints {"port":P,"pid":N}; pid file in bridge\run\P.pid
//   stop   -> stops the daemon on --port
import http from 'node:http'; import fs from 'node:fs'; import path from 'node:path'; import net from 'node:net';
import { spawn } from 'node:child_process'; import { fileURLToPath } from 'node:url';
const here = path.dirname(fileURLToPath(import.meta.url));
let a = process.argv.slice(2); const opt = k => { const i = a.indexOf(k); if (i < 0) return; const v = a[i + 1]; a.splice(i, 2); return v; };
const port = opt('--port'), out = opt('--out') || path.join(here, 'shots'), exe = opt('--exe');
const pass = []; for (const k of ['--pc', '--max-steps', '--max-frames', '--fleet']) { const v = opt(k); if (v != null) pass.push(k, v); }
const [cmd, json] = a;
// json: inline JSON, '@file.json', or '-' (stdin). Use forward slashes in paths (Git Bash eats backslashes).
function parseArgs(j) { if (!j) return {}; if (j === '-') j = fs.readFileSync(0, 'utf8'); else if (j.startsWith('@')) j = fs.readFileSync(j.slice(1), 'utf8');
  try { return JSON.parse(j); } catch { return JSON.parse(j.replace(/\\(?![\\"\/bfnrtu])/g, '/')); } }
const post = (p, o) => new Promise((ok, no) => { const r = http.request({ host: '127.0.0.1', port, path: p, method: 'POST', timeout: 600000 }, s => { let b = ''; s.on('data', d => b += d); s.on('end', () => ok(JSON.parse(b))); }); r.on('error', no); r.end(JSON.stringify(o || {})); });
if (cmd === 'start') {
  const p = await new Promise(ok => { const s = net.createServer(); s.listen(0, '127.0.0.1', () => { const x = s.address().port; s.close(() => ok(x)); }); });
  fs.mkdirSync(path.join(here, 'run'), { recursive: true });
  const log = fs.openSync(path.join(here, 'run', p + '.log'), 'w');
  const ch = spawn(process.execPath, [path.join(here, 'daemon.mjs'), '--port', p, ...(exe ? ['--exe', exe] : []), ...pass], { detached: true, stdio: ['ignore', log, log], windowsHide: true });
  ch.unref(); fs.writeFileSync(path.join(here, 'run', p + '.pid'), String(ch.pid));
  for (let i = 0; i < 300; i++) { await new Promise(r => setTimeout(r, 200)); if (fs.readFileSync(path.join(here, 'run', p + '.log'), 'utf8').includes('ready')) { console.log(JSON.stringify({ port: p, pid: ch.pid })); process.exit(0); } }
  console.log(JSON.stringify({ error: 'daemon did not start', log: path.join(here, 'run', p + '.log') })); process.exit(1);
}
if (!port) { console.error('need --port'); process.exit(2); }
if (cmd === 'stop') { try { await post('/stop'); } catch {} const pf = path.join(here, 'run', port + '.pid'); try { const pid = +fs.readFileSync(pf, 'utf8'); for (let i = 0; i < 150; i++) { try { process.kill(pid, 0); } catch { break; } await new Promise(r => setTimeout(r, 200)); } process.kill(pid); } catch {} try { fs.unlinkSync(pf); } catch {} console.log('{"stopped":' + port + '}'); process.exit(0); }
if (cmd === 'tools') { const r = await post('/tools'); console.log((r.result?.tools || []).map(t => t.name).join('\n')); process.exit(0); }
const r = await post('/call', { tool: cmd, args: parseArgs(json) });
if (r.error) { console.log(JSON.stringify({ error: r.error })); process.exit(1); }
const outp = { isError: !!r.result?.isError, text: [], images: [] };
for (const c of r.result?.content || []) {
  if (c.type === 'text') { try { outp.text.push(JSON.parse(c.text)); } catch { outp.text.push(c.text); } }
  else if (c.type === 'image') { fs.mkdirSync(out, { recursive: true }); const f = path.join(out, `${cmd}_${Date.now()}_${outp.images.length}.${(c.mimeType || 'image/png').split('/')[1]}`); fs.writeFileSync(f, Buffer.from(c.data, 'base64')); outp.images.push(f); }
}
if (outp.text.length === 1) outp.text = outp.text[0];
console.log(JSON.stringify(outp, null, 1)); process.exit(outp.isError ? 1 : 0);
