// Client: node mcp.mjs --port P [--out DIR] <tool|tools|start|stop|budget> [json-args]
//   start [--max-steps N] [--exe PATH] -> spawns a daemon on a free port, prints {"port":P,"pid":N}; pid file in <run>\P.pid
//   stop   -> stops the daemon on --port
//   budget -> prints the play_step budget used / left
// The compat catalog's T2/T3 agents reach BrokenNes's MCP server through this (docs/projects/compat-catalog/prompt-t2-player.md).
// Fixes after the NES pilot (2026-11-15): the session defaults to the one this daemon opened (agents passed "S" or a stale id);
// RAM addresses may be written 0x075A (a "$" in double quotes is eaten by bash); play_step stops at the step budget (default 60).
import http from 'node:http'; import fs from 'node:fs'; import path from 'node:path'; import net from 'node:net';
import { spawn } from 'node:child_process'; import { fileURLToPath } from 'node:url';
const here = path.dirname(fileURLToPath(import.meta.url));
const runDir = process.env.BN_BRIDGE_RUN || path.join(here, 'run');
let a = process.argv.slice(2); const opt = k => { const i = a.indexOf(k); if (i < 0) return; const v = a[i + 1]; a.splice(i, 2); return v; };
const port = opt('--port'), out = opt('--out') || path.join(here, 'shots'), exe = opt('--exe'), maxSteps = opt('--max-steps');
const [cmd, json] = a;
// json: inline JSON, '@file.json', or '-' (stdin). Use forward slashes in paths (Git Bash eats backslashes).
function parseArgs(j) { if (!j) return {}; if (j === '-') j = fs.readFileSync(0, 'utf8'); else if (j.startsWith('@')) j = fs.readFileSync(j.slice(1), 'utf8');
  try { return JSON.parse(j); } catch { return JSON.parse(j.replace(/\\(?![\\"\/bfnrtu])/g, '/')); } }
const post = (p, o) => new Promise((ok, no) => { const r = http.request({ host: '127.0.0.1', port, path: p, method: 'POST', timeout: 600000 }, s => { let b = ''; s.on('data', d => b += d); s.on('end', () => ok(JSON.parse(b))); }); r.on('error', no); r.end(JSON.stringify(o || {})); });
const stateFile = () => path.join(runDir, port + '.state.json');
const readState = () => { try { return JSON.parse(fs.readFileSync(stateFile(), 'utf8')); } catch { return { steps: 0, maxSteps: 60 }; } };

if (cmd === 'start') {
  const p = await new Promise(ok => { const s = net.createServer(); s.listen(0, '127.0.0.1', () => { const x = s.address().port; s.close(() => ok(x)); }); });
  fs.mkdirSync(runDir, { recursive: true });
  const log = fs.openSync(path.join(runDir, p + '.log'), 'w');
  const ch = spawn(process.execPath, [path.join(here, 'daemon.mjs'), '--port', p, ...(exe ? ['--exe', exe] : [])], { detached: true, stdio: ['ignore', log, log], windowsHide: true });
  ch.unref(); fs.writeFileSync(path.join(runDir, p + '.pid'), String(ch.pid));
  fs.writeFileSync(path.join(runDir, p + '.state.json'), JSON.stringify({ steps: 0, maxSteps: +(maxSteps || process.env.BN_MAX_STEPS || 60), frames: 0 }));
  for (let i = 0; i < 150; i++) { await new Promise(r => setTimeout(r, 200)); if (fs.readFileSync(path.join(runDir, p + '.log'), 'utf8').includes('ready')) { console.log(JSON.stringify({ port: p, pid: ch.pid })); process.exit(0); } }
  console.log(JSON.stringify({ error: 'daemon did not start', log: path.join(runDir, p + '.log') })); process.exit(1);
}
if (!port) { console.error('need --port'); process.exit(2); }
if (cmd === 'stop') { try { await post('/stop'); } catch {} const pf = path.join(runDir, port + '.pid'); try { process.kill(+fs.readFileSync(pf, 'utf8')); } catch {} try { fs.unlinkSync(pf); } catch {} console.log('{"stopped":' + port + '}'); process.exit(0); }
if (cmd === 'tools') { const r = await post('/tools'); console.log((r.result?.tools || []).map(t => t.name).join('\n')); process.exit(0); }
const st = readState();
if (cmd === 'budget') { console.log(JSON.stringify({ steps: st.steps, maxSteps: st.maxSteps, left: st.maxSteps - st.steps, frame: st.frames, session: st.session })); process.exit(0); }

const args = parseArgs(json);
// the session: the one this daemon opened (one agent = one session), whatever the call says
const noSession = ['open_rom', 'open_project', 'open_movie', 'list_sessions', 'list_instances', 'attach_live', 'brokendev'];
let sessionNote;
if (!noSession.includes(cmd) && st.session && String(args.session ?? '') !== st.session) {
  if (args.session) sessionNote = `session "${args.session}" replaced by this daemon's session ${st.session}`;
  args.session = st.session;
}
// RAM: 0x075A and [0x075A] are accepted as $075A
const hex = v => typeof v === 'string' ? v.replace(/0x([0-9a-fA-F]+)/g, '$$$1') : v;
if (Array.isArray(args.ram)) args.ram = args.ram.map(hex);
if (typeof args.until === 'string') args.until = hex(args.until);
// the step budget
if (cmd === 'play_step') {
  if (st.steps >= st.maxSteps) {
    console.log(JSON.stringify({ isError: true, text: `step budget spent (${st.steps}/${st.maxSteps} play_step calls): stop playing now. Stop the recording, close the reference and the session, grade from what you saw (Not tested if you never reached gameplay), write the record, report.` }));
    process.exit(1);
  }
  st.steps++;
  fs.writeFileSync(stateFile(), JSON.stringify(st));
}

const r = await post('/call', { tool: cmd, args });
if (r.error) { console.log(JSON.stringify({ error: r.error })); process.exit(1); }
const outp = { isError: !!r.result?.isError, text: [], images: [] };
for (const c of r.result?.content || []) {
  if (c.type === 'text') { try { outp.text.push(JSON.parse(c.text)); } catch { outp.text.push(c.text); } }
  else if (c.type === 'image') { fs.mkdirSync(out, { recursive: true }); const f = path.join(out, `${cmd}_${Date.now()}_${outp.images.length}.${(c.mimeType || 'image/png').split('/')[1]}`).replace(/\\/g, '/'); fs.writeFileSync(f, Buffer.from(c.data, 'base64')); outp.images.push(f); }
}
if (outp.text.length === 1) outp.text = outp.text[0];
const st2 = readState();   // re-read: another call may have changed it
if (cmd === 'open_rom' && !outp.isError && outp.text?.session != null) st2.session = String(outp.text.session);
if (cmd === 'close_session' && !outp.isError) delete st2.session;
if (cmd === 'play_step' && outp.text && typeof outp.text === 'object') { st2.frames = outp.text.frame; outp.budget = `${st2.steps}/${st2.maxSteps} steps`; }
fs.writeFileSync(stateFile(), JSON.stringify(st2));
if (sessionNote) outp.note = sessionNote;
console.log(JSON.stringify(outp, null, 1)); process.exit(outp.isError ? 1 : 0);
