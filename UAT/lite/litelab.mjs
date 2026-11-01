// litelab.mjs - a small lab for driving BrokenNes Lite (the Blazor WebAssembly build) in a real Edge/Chrome window over the
// DevTools protocol, with no packages. Used by the Lite Sega speed suite (bench-lite.mjs) and the Lite functional suite
// (test-lite-sega.mjs). Node 22 or newer (global WebSocket and fetch).
//
//   import { startLab } from './litelab.mjs';
//   const lab = await startLab({ publishDir, romsDir });          // serves the publish folder and /roms/ and opens a browser
//   const page = await lab.open('/?rom=roms/Sonic.md');            // navigate; returns the page helper
//   await page.waitFor("document.body.innerText.includes('State: Running')", 120000);
//   const v = await page.eval('1+1');                              // Runtime.evaluate with awaitPromise, returnByValue
//   await page.shot('x.png'); await lab.close();
//
// The page's own animation clock is a requestAnimationFrame loop. A hidden or backgrounded window throttles it, so speed is measured
// by driving the page's FrameTick call in a JavaScript loop (see bench-lite.mjs), and the page's own FPS badge is read as the user's view.
// Every process started here is stopped by PID. Nothing is written into the repository.
import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { spawn, spawnSync } from 'node:child_process';

const MIME = {
  '.html': 'text/html; charset=utf-8', '.js': 'text/javascript', '.mjs': 'text/javascript', '.css': 'text/css', '.json': 'application/json',
  '.wasm': 'application/wasm', '.png': 'image/png', '.svg': 'image/svg+xml', '.ico': 'image/x-icon', '.txt': 'text/plain', '.map': 'application/json',
};

export function serve({ publishDir, romsDir, port = 0 }) {
  let root = path.resolve(publishDir);
  if (fs.existsSync(path.join(root, 'wwwroot'))) root = path.join(root, 'wwwroot');
  const server = http.createServer((req, res) => {
    try {
      const u = new URL(req.url, 'http://x');
      let rel = decodeURIComponent(u.pathname).replace(/^\/+/, '');
      let base = root;
      if (romsDir && rel.startsWith('roms/')) { base = path.resolve(romsDir); rel = rel.slice(5); }
      if (rel === '') rel = 'index.html';
      let full = path.resolve(base, rel);
      if (!full.toLowerCase().startsWith(base.toLowerCase())) { res.writeHead(404); return res.end(); }
      if (!fs.existsSync(full) || fs.statSync(full).isDirectory()) {
        if (path.extname(rel)) { res.writeHead(404); return res.end(); }
        full = path.join(root, 'index.html');   // SPA route
      }
      const ext = path.extname(full).toLowerCase();
      const headers = { 'Content-Type': MIME[ext] || 'application/octet-stream', 'Cache-Control': 'no-cache' };
      const ae = String(req.headers['accept-encoding'] || '');
      if (ae.includes('br') && fs.existsSync(full + '.br')) {
        headers['Content-Encoding'] = 'br'; headers['Vary'] = 'Accept-Encoding';
        res.writeHead(200, headers); return fs.createReadStream(full + '.br').pipe(res);
      }
      res.writeHead(200, headers); fs.createReadStream(full).pipe(res);
    } catch (e) { res.writeHead(500); res.end(String(e)); }
  });
  return new Promise(resolve => server.listen(port, '127.0.0.1', () => resolve({ server, port: server.address().port })));
}

function findBrowser() {
  const c = [
    process.env.LITELAB_BROWSER,
    'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe', 'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe',
    'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe', 'C:\\Program Files (x86)\\Google\\Chrome\\Application\\chrome.exe',
  ].filter(Boolean);
  const found = c.find(p => fs.existsSync(p));
  if (!found) throw new Error('no Edge or Chrome found; set LITELAB_BROWSER');
  return found;
}

class Cdp {
  constructor(ws) { this.ws = ws; this.id = 0; this.pending = new Map(); this.events = []; this.listeners = [];
    ws.addEventListener('message', ev => {
      const m = JSON.parse(ev.data);
      if (m.id && this.pending.has(m.id)) { const { res, rej } = this.pending.get(m.id); this.pending.delete(m.id); m.error ? rej(new Error(m.error.message)) : res(m.result); }
      else { this.events.push(m); for (const l of this.listeners) l(m); }
    });
  }
  send(method, params = {}, timeoutMs = 90000) {
    const id = ++this.id;
    return new Promise((res, rej) => {
      const t = setTimeout(() => { if (this.pending.delete(id)) rej(new Error(`CDP ${method} timed out after ${timeoutMs} ms`)); }, timeoutMs);
      this.pending.set(id, { res: v => { clearTimeout(t); res(v); }, rej: e => { clearTimeout(t); rej(e); } });
      try { this.ws.send(JSON.stringify({ id, method, params })); } catch (e) { clearTimeout(t); this.pending.delete(id); rej(e); }
    });
  }
  /** The browser went away: every call still waiting fails now instead of never returning. */
  failAll(reason) { for (const [id, p] of this.pending) { this.pending.delete(id); p.rej(new Error(reason)); } }
}

const sleep = ms => new Promise(r => setTimeout(r, ms));

/** Stops the browser this lab started: the PID spawned (when still there) and every browser process whose command line names this lab's own profile folder (a unique temp folder, so only
 * this lab's processes match), each by PID with its tree. Never by name: the user's own browser is left alone. */
function killProfile(profile, launcherPid) {
  const pids = new Set();
  if (launcherPid) pids.add(String(launcherPid));
  if (process.platform === 'win32') {
    const q = `Get-CimInstance Win32_Process -Filter "Name='msedge.exe' or Name='chrome.exe'" | Where-Object { $_.CommandLine -and $_.CommandLine.Contains('${profile.replace(/'/g, "''")}') -and $_.CommandLine -notlike '*--type=*' } | ForEach-Object { $_.ProcessId }`;
    const r = spawnSync('powershell', ['-NoProfile', '-NonInteractive', '-Command', q], { encoding: 'utf8', windowsHide: true });
    for (const l of String(r.stdout || '').split(/\r?\n/)) if (/^\d+$/.test(l.trim())) pids.add(l.trim());
    for (const pid of pids) spawnSync('taskkill', ['/T', '/F', '/PID', pid], { stdio: 'ignore', windowsHide: true });
  } else for (const pid of pids) { try { process.kill(+pid); } catch {} }
}

async function startLabOnce({ publishDir, romsDir, headless = true, width = 1280, height = 900, mobile = false, gpu = process.env.LITELAB_GPU === '1', log = () => {} }) {
  const { server, port } = await serve({ publishDir, romsDir });
  const base = `http://127.0.0.1:${port}`;
  const profile = fs.mkdtempSync(path.join(os.tmpdir(), 'litelab-'));
  // gpu: the browser's own GPU path (ANGLE on the machine's graphics card) instead of SwiftShader, the software renderer the labs use by default (it draws on the CPU, so a speed
  // measured with the picture drawn is then the CPU's drawing, not a phone's or a desktop's GPU)
  const args = [`--remote-debugging-port=0`, `--user-data-dir=${profile}`, '--no-first-run', '--no-default-browser-check', `--window-size=${width},${height}`,
    '--disable-background-timer-throttling', '--disable-renderer-backgrounding', '--disable-backgrounding-occluded-windows', '--autoplay-policy=no-user-gesture-required',
    ...(gpu ? [] : ['--enable-unsafe-swiftshader', '--use-angle=swiftshader']), 'about:blank'];
  if (headless) args.unshift('--headless=new');
  const proc = spawn(findBrowser(), args, { stdio: 'ignore', windowsHide: true });
  // Edge's launcher may start the real browser as a child and exit at once (seen with Edge in 2026-10: the PID spawned here is gone a second later while the browser runs and has written its
  // DevTools port), so the browser is judged by its DevTools socket, not by the PID spawned; close() stops every process that uses this lab's own profile folder, by PID.
  let gone = false; let cdpRef = null;
  proc.on('exit', () => {});
  let dbgPort = 0, cdp, ws;
  try {
  for (let i = 0; i < 80 && !dbgPort; i++) {
    await sleep(250);
    try { dbgPort = parseInt(fs.readFileSync(path.join(profile, 'DevToolsActivePort'), 'utf8').split('\n')[0], 10); } catch {}
  }
  if (!dbgPort) { killProfile(profile, proc.pid); throw new Error('browser DevTools port did not appear'); }
  const list = await (await fetch(`http://127.0.0.1:${dbgPort}/json/list`, { signal: AbortSignal.timeout(15000) })).json();
  const target = list.find(t => t.type === 'page');
  ws = new WebSocket(target.webSocketDebuggerUrl);
  { let t; await Promise.race([new Promise((res, rej) => { ws.addEventListener('open', res); ws.addEventListener('error', rej); }), new Promise((_, rej) => { t = setTimeout(() => rej(new Error('DevTools socket did not open')), 15000); })]); clearTimeout(t); }
  cdp = new Cdp(ws); cdpRef = cdp;
  ws.addEventListener('close', () => { gone = true; cdpRef?.failAll('the browser closed its DevTools socket'); });
  if (gone) throw new Error('the browser exited while starting');
  for (const d of ['Page', 'Runtime', 'Network', 'Log']) await cdp.send(d + '.enable');
  if (mobile) {
    await cdp.send('Emulation.setDeviceMetricsOverride', { width: 390, height: 844, deviceScaleFactor: 2, mobile: true });
    await cdp.send('Emulation.setTouchEmulationEnabled', { enabled: true });
  }
  } catch (e) { killProfile(profile, proc.pid); server.close(); try { fs.rmSync(profile, { recursive: true, force: true }); } catch {} throw e; }

  const page = {
    cdp, base,
    async eval(expr, { timeout = 120000 } = {}) {
      let timer; const guard = new Promise((_, rej) => { timer = setTimeout(() => rej(new Error('eval timeout: ' + expr.slice(0, 80))), timeout); });
      let r; try { r = await Promise.race([cdp.send('Runtime.evaluate', { expression: expr, returnByValue: true, awaitPromise: true }, timeout + 5000), guard]); } finally { clearTimeout(timer); }
      if (r.exceptionDetails) throw new Error('JS error: ' + (r.exceptionDetails.exception?.description || r.exceptionDetails.text));
      return r.result.value;
    },
    async waitFor(expr, timeout = 60000, every = 250) {
      const t0 = Date.now();
      for (;;) {
        let v = false; try { v = await this.eval(expr, { timeout: 20000 }); } catch {}
        if (v) return v;
        if (Date.now() - t0 > timeout) throw new Error('waitFor timed out: ' + expr.slice(0, 120));
        await sleep(every);
      }
    },
    async shot(file, clip) {
      const p = { format: 'png' }; if (clip) p.clip = { ...clip, scale: 1 };
      const r = await cdp.send('Page.captureScreenshot', p);
      if (file) fs.writeFileSync(file, Buffer.from(r.data, 'base64'));
      return Buffer.from(r.data, 'base64');
    },
    /** Sample the page's CPU (JS and WebAssembly functions) while `expr` runs; returns { value, totalMs, top:[{name, selfMs, selfPct}] } by self time. */
    async profile(expr, { intervalUs = 250, top = 40, symbols = null } = {}) {
      // symbols: path of the build's dotnet.native.js.symbols (one "index:name" a line; the AOT publish with -p:WasmEmitSymbolMap=true writes it next to the wasm in obj\...\for-publish) names the wasm-function[N] frames
      let names = null;
      if (symbols) { names = new Map(); for (const line of fs.readFileSync(symbols, 'utf8').split('\n')) { const i = line.indexOf(':'); if (i > 0) names.set(line.slice(0, i), line.slice(i + 1).trim()); } }
      await cdp.send('Profiler.enable');
      await cdp.send('Profiler.setSamplingInterval', { interval: intervalUs });
      await cdp.send('Profiler.start');
      let value, prof;
      try { value = await this.eval(expr, { timeout: 600000 }); } finally { prof = (await cdp.send('Profiler.stop')).profile; await cdp.send('Profiler.disable'); }
      const byId = new Map(prof.nodes.map(n => [n.id, n]));
      const self = new Map(); let total = 0;
      for (let i = 0; i < prof.samples.length; i++) {
        const dt = (prof.timeDeltas[i] || 0) / 1000; total += dt;
        const f = byId.get(prof.samples[i]).callFrame;
        let fn = f.functionName || '(anonymous)';
        const m = names && /^wasm-function\[(\d+)\]/.exec(fn);
        if (m && names.has(m[1])) fn = names.get(m[1]);
        const key = fn + (f.url && !m ? ' ' + f.url.split('/').pop() : '');
        self.set(key, (self.get(key) || 0) + dt);
      }
      const arr = [...self.entries()].sort((a, b) => b[1] - a[1]).slice(0, top).map(([name, ms]) => ({ name, selfMs: +ms.toFixed(1), selfPct: +(100 * ms / total).toFixed(1) }));
      return { value, totalMs: +total.toFixed(1), top: arr, profile: prof };
    },
    async key(code, down = true) {
      await cdp.send('Input.dispatchKeyEvent', { type: down ? 'keyDown' : 'keyUp', code, key: code.replace(/^Key/, '').toLowerCase(), windowsVirtualKeyCode: 0 });
    },
    consoleErrors() { return cdp.events.filter(e => e.method === 'Runtime.exceptionThrown' || (e.method === 'Runtime.consoleAPICalled' && e.params.type === 'error')).map(e => e.method === 'Runtime.exceptionThrown' ? e.params.exceptionDetails.exception?.description || e.params.exceptionDetails.text : (e.params.args || []).map(a => a.value ?? a.description).join(' ')); },
    badResponses() { return cdp.events.filter(e => e.method === 'Network.responseReceived' && e.params.response.status >= 400 && !/favicon/.test(e.params.response.url)).map(e => `${e.params.response.status} ${e.params.response.url}`); },
  };

  // A second connection, to the browser itself (not the page): SystemInfo.getProcessInfo gives the CPU time every browser process has used. The page's renderer is the busiest one, so the difference of its CPU
  // time across a run is the CPU the page used, whatever else the machine was doing: the speed that does not depend on how loaded the machine is.
  let browserCdp = null;
  async function processCpuSeconds() {
    if (!browserCdp) {
      const url = `ws://127.0.0.1:${dbgPort}` + fs.readFileSync(path.join(profile, 'DevToolsActivePort'), 'utf8').split('\n')[1].trim();
      const bws = new WebSocket(url);
      await new Promise((res, rej) => { bws.addEventListener('open', res); bws.addEventListener('error', rej); });
      browserCdp = new Cdp(bws);
    }
    const r = await browserCdp.send('SystemInfo.getProcessInfo');
    const renderers = r.processInfo.filter(p => p.type === 'renderer').map(p => p.cpuTime);
    return renderers.length ? Math.max(...renderers) : 0;
  }

  return {
    base, page, cdp, processCpuSeconds,
    /** Navigate (path relative to the server) and wait for the Blazor app to render. */
    async open(p = '/', { timeout = 180000 } = {}) {
      cdp.events.length = 0;
      await cdp.send('Page.navigate', { url: base + p });
      // the canvas is there and the emulator has started its frame loop (the console preferences have been restored by then: a choice made earlier would be overwritten by them)
      await page.waitFor("!!document.querySelector('#nes-canvas') && !!window.nesInterop && !!window.nesInterop._dotNetRef", timeout);
      return page;
    },
    async close() {
      try { await cdp.send('Browser.close'); } catch {}
      try { ws.close(); } catch {}
      await sleep(300);
      killProfile(profile, proc.pid);
      server.close();
      try { fs.rmSync(profile, { recursive: true, force: true }); } catch {}
    },
  };
}

/** Starts a browser and the server; a browser that dies while starting is tried again (up to three times: under load Edge sometimes exits right after it has written its DevTools port). */
export async function startLab(opts) {
  let last;
  for (let attempt = 1; attempt <= 3; attempt++) {
    try { return await startLabOnce(opts); } catch (e) { last = e; await sleep(1500); }
  }
  throw last;
}

export function args(argv = process.argv.slice(2)) {
  const o = { _: [] };
  for (let i = 0; i < argv.length; i++) {
    if (argv[i].startsWith('--')) { const k = argv[i].slice(2); const v = argv[i + 1] && !argv[i + 1].startsWith('--') ? argv[++i] : true; o[k] = v; } else o._.push(argv[i]);
  }
  return o;
}
