// Screenshots a Nano page in headless Edge/Chrome, optionally after running some script in it:
//   node Nano/test/shot.mjs <page relative to Nano/> <out.png> [--width 1100] [--height 900] [--eval "js to run first (may be async)"] [--wait ms]
// Used to look at the demo page (the Browser pane of the desktop app is too narrow to judge a desktop layout).
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawn, execFileSync } from 'node:child_process';
import { startServer } from './serve.mjs';
import { nanoRoot } from './lib.mjs';

const argv = process.argv.slice(2);
const opt = (n, d) => (argv.includes(n) ? argv[argv.indexOf(n) + 1] : d);
const page = argv[0], out = argv[1];
const width = Number(opt('--width', 1100)), height = Number(opt('--height', 900));
const browser = [process.env.NANO_BROWSER, 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', 'C:/Program Files/Google/Chrome/Application/chrome.exe'].filter(Boolean).find(p => fs.existsSync(p));
const profile = fs.mkdtempSync(path.join(os.tmpdir(), 'nano-shot-'));
const { server, port } = await startServer(nanoRoot);
const child = spawn(browser, ['--headless=new', '--remote-debugging-port=0', `--user-data-dir=${profile}`, '--no-first-run', '--autoplay-policy=no-user-gesture-required', '--disable-gpu', '--hide-scrollbars', 'about:blank'], { stdio: 'ignore' });
try {
  let devPort = 0;
  for (let i = 0; i < 100 && !devPort; i++) { await new Promise(r => setTimeout(r, 100)); const f = path.join(profile, 'DevToolsActivePort'); if (fs.existsSync(f)) devPort = Number(fs.readFileSync(f, 'utf8').split('\n')[0]); }
  const targets = await (await fetch(`http://127.0.0.1:${devPort}/json/list`)).json();
  const ws = new WebSocket(targets.find(t => t.type === 'page').webSocketDebuggerUrl);
  await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; });
  let id = 1; const waiting = new Map(); const logs = [];
  ws.onmessage = (m) => { const d = JSON.parse(m.data); if (d.id && waiting.has(d.id)) { waiting.get(d.id)(d); waiting.delete(d.id); } else if (d.method === 'Runtime.exceptionThrown') logs.push('EXCEPTION ' + (d.params.exceptionDetails.exception?.description ?? d.params.exceptionDetails.text)); else if (d.method === 'Runtime.consoleAPICalled') logs.push(d.params.args.map(a => a.value ?? a.description).join(' ')); };
  const send = (method, params = {}) => new Promise((res) => { const i = id++; waiting.set(i, res); ws.send(JSON.stringify({ id: i, method, params })); });
  await send('Runtime.enable'); await send('Page.enable');
  await send('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: width < 500 });
  await send('Page.navigate', { url: `http://127.0.0.1:${port}/${page}` });
  await new Promise(r => setTimeout(r, 1500));
  if (opt('--eval')) {
    const r = await send('Runtime.evaluate', { expression: '(async () => {' + opt('--eval') + '})()', awaitPromise: true, returnByValue: true });
    if (r.result?.exceptionDetails) console.log('eval error: ' + JSON.stringify(r.result.exceptionDetails).slice(0, 300));
    else if (r.result?.result?.value !== undefined) console.log('eval: ' + JSON.stringify(r.result.result.value));
  }
  await new Promise(r => setTimeout(r, Number(opt('--wait', 600))));
  const shot = await send('Page.captureScreenshot', { format: 'png' });
  fs.writeFileSync(out, Buffer.from(shot.result.data, 'base64'));
  for (const l of logs) console.log('console: ' + l);
  console.log('wrote ' + out);
  ws.close();
} finally {
  try { execFileSync('taskkill', ['/PID', String(child.pid), '/T', '/F'], { stdio: 'ignore' }); } catch { }
  server.close(); await new Promise(r => setTimeout(r, 500));
  try { fs.rmSync(profile, { recursive: true, force: true }); } catch { }
  process.exit(0);
}
