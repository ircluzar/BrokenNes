// BrokenNes MCP bridge daemon: one stdio MCP server, exposed on 127.0.0.1:PORT (POST /call, POST /tools, POST /stop)
//   local:  the server runs on this computer (--exe, default the cc_pilot build)
//   --pc N: the server runs ON fleet PC N over ssh stdio (the emulator and Mesen run there); this daemon still listens here.
//           open_rom's ROM is pushed to C:\brokennes\roms\t2\ on the PC (removed again at close/stop); output paths
//           (record stop/checkpoint path, save_state/screenshot/record_sound path) are written on the PC under
//           C:\brokennes\mcp\out\<port>\ and pulled back to the path the agent asked for. Pictures come back inline (base64).
// --max-steps N (default 60) / --max-frames N (default 0 = none): play_step is refused past the budget.
// A call without "session" gets the session open_rom returned (auto-session).
import { spawn, spawnSync } from 'node:child_process';
import http from 'node:http'; import fs from 'node:fs'; import path from 'node:path';
const a = process.argv.slice(2), opt = k => { const i = a.indexOf(k); return i >= 0 ? a[i + 1] : undefined; };
const port = +opt('--port');
const pc = opt('--pc');
const maxSteps = +(opt('--max-steps') ?? 60), maxFrames = +(opt('--max-frames') ?? 0);
const exe = opt('--exe') || process.env.BROKENNES_MCP || 'C:/BrokenNes-work/cc_pilot/mcp/bin/BrokenNes.Mcp/release/BrokenNes.Mcp.exe';
const repo = opt('--repo') || 'C:/Users/philt/OneDrive/Documents/PROJECTS/BrokenNes';
const log = (...x) => console.log(new Date().toISOString(), ...x);

// ---------------------------------------------------------------- fleet (ssh) side
const FLEET = opt('--fleet') || process.env.FLEET_DIR || 'E:/fleet';
const SSH = 'C:/Windows/System32/OpenSSH/ssh.exe', SCP = 'C:/Windows/System32/OpenSSH/scp.exe';
let box, sshOpts = [];
if (pc) {
  const fl = JSON.parse(fs.readFileSync(path.join(FLEET, 'fleet.json'), 'utf8'));
  const b = fl[pc.toUpperCase()]; if (!b) { console.error('no PC ' + pc + ' in fleet.json'); process.exit(2); }
  box = { name: pc.toUpperCase(), user: b.user, ip: b.from || b.ips[0] };
  sshOpts = ['-i', path.join(FLEET, 'key', 'fleet_key'), '-o', 'BatchMode=yes', '-o', 'IdentitiesOnly=yes', '-o', 'StrictHostKeyChecking=accept-new',
    '-o', 'ConnectTimeout=8', '-o', 'ServerAliveInterval=15', '-o', 'UserKnownHostsFile=' + path.join(FLEET, 'key', 'known_hosts')];
}
const R = { exe: 'C:\\brokennes\\mcp\\mcp\\BrokenNes.Mcp.exe', repo: 'C:\\brokennes\\mcp\\repo', home: 'C:\\brokennes\\mcp\\env',
  roms: 'C:\\brokennes\\roms\\t2', out: `C:\\brokennes\\mcp\\out\\${port}` };
const at = () => `${box.user}@${box.ip}`;
const ps = s => 'powershell -NoProfile -ExecutionPolicy Bypass -EncodedCommand ' + Buffer.from("$ProgressPreference='SilentlyContinue'\n" + s, 'utf16le').toString('base64');
const remote = (s, t = 120000) => { const r = spawnSync(SSH, [...sshOpts, at(), ps(s)], { encoding: 'utf8', timeout: t, windowsHide: true }); return { rc: r.status, out: (r.stdout || '').trim(), err: (r.stderr || '').trim() }; };
const scp = (src, dst, t = 600000) => { const r = spawnSync(SCP, [...sshOpts, '-q', src, dst], { encoding: 'utf8', timeout: t, windowsHide: true }); return { rc: r.status, err: (r.stderr || '').trim() }; };
const fwd = p => p.replace(/\\/g, '/');
const pushedRoms = new Set();
function pushRom(local) {
  if (!fs.existsSync(local)) throw new Error('ROM not found here: ' + local);
  const name = `${port}_${path.basename(local).replace(/[^\w.\-()\[\]!,&]/g, '_')}`;
  const dst = `${R.roms}\\${name}`;
  remote(`New-Item -ItemType Directory -Force '${R.roms}','${R.out}' | Out-Null`);
  const r = scp(local, `${at()}:${fwd(dst)}`); if (r.rc) throw new Error('ROM push failed: ' + r.err);
  pushedRoms.add(dst); log('pushed', local, '->', dst); return dst;
}
function cleanupRemote(final) {
  const roms = [...pushedRoms].map(x => `'${x.replace(/'/g, "''")}'`).join(',');
  const s = (roms ? `Remove-Item ${roms} -Force -ErrorAction SilentlyContinue; ` : '') + `Get-ChildItem '${R.roms}' -Filter '${port}_*' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue; ` +
    (final ? `Remove-Item '${R.out}' -Recurse -Force -ErrorAction SilentlyContinue; ` +
      // leftovers of daemons that died without a stop: anything over 6 hours old
      `Get-ChildItem '${R.roms}','C:\\brokennes\\mcp\\out' -ErrorAction SilentlyContinue | ? { $_.LastWriteTime -lt (Get-Date).AddHours(-6) } | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue; ` +
      // orphaned Mesen (its server gone) and the session folders of dead servers (each holds a 70 MB Mesen copy)
      `Get-CimInstance Win32_Process -Filter "Name='Mesen.exe'" | ? { $_.ExecutablePath -like '*BrokenNes\\mcp\\sessions*' -and -not (Get-Process -Id $_.ParentProcessId -ErrorAction SilentlyContinue) } | % { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }; ` +
      `$sd = Join-Path $env:LOCALAPPDATA 'BrokenNes\\mcp\\sessions'; if (Test-Path $sd) { Get-ChildItem $sd -Directory | ? { $p = ($_.Name -split '-')[0]; -not (Get-Process -Id $p -ErrorAction SilentlyContinue) } | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue }; ` : '') + `'clean'`;
  pushedRoms.clear(); return remote(s);
}

// ---------------------------------------------------------------- the server
const srv = pc
  ? spawn(SSH, [...sshOpts, at(), `cmd /c "set BROKENDEV_HOME=${R.home}&& set BROKENNES_REPO=${R.repo}&& ${R.exe} --repo ${R.repo}"`], { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true })
  : spawn(exe, ['--repo', repo], { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
let errTail = ''; srv.stderr.on('data', d => { errTail = (errTail + d).slice(-2000); });
srv.on('exit', c => { console.error('server exited', c, errTail); if (pc) cleanupRemote(true); process.exit(1); });
let buf = '', nextId = 1; const pend = new Map();
srv.stdout.on('data', d => { buf += d; let i; while ((i = buf.indexOf('\n')) >= 0) { const line = buf.slice(0, i).trim(); buf = buf.slice(i + 1); if (!line) continue;
  try { const m = JSON.parse(line); if (m.id != null && pend.has(m.id)) { pend.get(m.id)(m); pend.delete(m.id); } } catch {} } });
const rpc = (method, params) => new Promise(res => { const id = nextId++; pend.set(id, res); srv.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n'); });
let q = Promise.resolve(); const serial = f => (q = q.then(f, f));
const init = await rpc('initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'cc-bridge', version: '2' } });
if (init.error) { console.error('initialize failed', JSON.stringify(init.error)); process.exit(1); }
srv.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');

// ---------------------------------------------------------------- per-call rewriting
let session = null, steps = 0, frame = 0;
const OUT_KEYS = { record: ['path'], save_state: ['path'], screenshot: ['path'], record_sound: ['path'] };
const errResult = msg => ({ result: { isError: true, content: [{ type: 'text', text: msg }] } });
const textOf = r => (r.result?.content || []).filter(c => c.type === 'text').map(c => c.text).join('\n');
const NO_SESSION = ['open_rom', 'open_project', 'attach_live', 'list_sessions', 'list_instances', 'list_domains', 'brokendev'];
async function call(tool, args) {
  args = { ...(args || {}) };
  if (session && args.session == null && !NO_SESSION.includes(tool)) args.session = session;
  if (tool === 'play_step') {
    if (steps >= maxSteps) return errResult(`step budget spent (${steps}/${maxSteps} play_step calls): stop now: record stop, reference close, close_session, write the record`);
    if (maxFrames > 0 && frame >= maxFrames) return errResult(`frame budget spent (frame ${frame} >= ${maxFrames}): stop now: record stop, reference close, close_session, write the record`);
  }
  const pulls = [];
  if (pc) {
    if (tool === 'open_rom' && args.path) args.path = pushRom(args.path);
    for (const k of OUT_KEYS[tool] || []) {
      const v = args[k]; if (typeof v !== 'string' || !v) continue;
      const rp = `${R.out}\\${Date.now()}_${path.basename(v.replace(/\\/g, '/'))}`;
      pulls.push({ rp, local: v }); args[k] = rp;
    }
    if (pulls.length) remote(`New-Item -ItemType Directory -Force '${R.out}' | Out-Null`);
  }
  const r = await rpc('tools/call', { name: tool, arguments: args });
  if (tool === 'open_rom' && !r.result?.isError) { try { session = JSON.parse(textOf(r)).session ?? session; } catch {} steps = 0; frame = 0; }
  if (tool === 'play_step' && !r.result?.isError) { steps++; try { const j = JSON.parse(textOf(r)); if (j.frame != null) frame = +j.frame; } catch {} }
  if (tool === 'close_session' && (!args.session || args.session === session)) { session = null; if (pc) cleanupRemote(false); }
  for (const p of pulls) {
    // record start only names the path: the file appears at stop
    if (tool === 'record' && !['stop', 'checkpoint'].includes(args.action)) continue;
    fs.mkdirSync(path.dirname(p.local), { recursive: true });
    for (const [rs, ls] of [[p.rp, p.local], [p.rp + '.ref.json', p.local + '.ref.json']]) {
      const x = scp(`${at()}:${fwd(rs)}`, ls);
      if (!x.rc) log('pulled', rs, '->', ls); else if (rs === p.rp) log('pull failed', rs, x.err);
    }
  }
  // the agent sees its own paths in the result, not the PC's
  for (const p of pulls) for (const c of r.result?.content || []) if (c.type === 'text')
    c.text = c.text.split(JSON.stringify(p.rp).slice(1, -1)).join(JSON.stringify(p.local).slice(1, -1)).split(p.rp).join(p.local);
  if (tool === 'play_step' && r.result && !r.result.isError) {
    const left = maxSteps - steps;
    const c = r.result.content.find(x => x.type === 'text');
    try { const j = JSON.parse(c.text); j.budget = { steps, maxSteps, left, ...(maxFrames ? { maxFrames } : {}) }; c.text = JSON.stringify(j); } catch {}
  }
  return r;
}

http.createServer((req, res) => { let body = ''; req.on('data', d => body += d); req.on('end', async () => {
  const send = o => { res.writeHead(200, { 'content-type': 'application/json' }); res.end(JSON.stringify(o)); };
  try {
    if (req.url === '/tools') return send(await serial(() => rpc('tools/list', {})));
    if (req.url === '/stop') { send({ ok: true }); if (pc) { srv.removeAllListeners('exit'); try { srv.stdin.end(); } catch {} await Promise.race([new Promise(r => srv.once('exit', r)), new Promise(r => setTimeout(r, 10000))]); srv.kill(); await new Promise(r => setTimeout(r, 1500)); cleanupRemote(true); } else srv.kill(); setTimeout(() => process.exit(0), 300); return; }
    const { tool, args } = JSON.parse(body || '{}');
    send(await serial(() => call(tool, args)));
  } catch (e) { send({ error: { message: String(e) } }); } }); }).listen(port, '127.0.0.1', () => log('ready ' + port + (pc ? ' on ' + box.name : '')));
