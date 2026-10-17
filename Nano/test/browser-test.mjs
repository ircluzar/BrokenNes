// Runs Nano/test/browser-test.html in headless Edge (or Chrome) and prints what it found.
//   node Nano/test/browser-test.mjs [--file] [--browser <path-to-msedge-or-chrome>] [--page <test page relative to Nano/>]
// --file opens the page as file:// instead of http://127.0.0.1 (Nano has to work from a plain file too).
// The browser is started with --autoplay-policy=no-user-gesture-required so the real-time test can run without a click; this machine has no sound card,
// so "real time" here means the browser's own clock and null audio output. It is a different thing from hearing it, and the report says so.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawn, execFileSync } from 'node:child_process';
import { pathToFileURL } from 'node:url';
import { startServer } from './serve.mjs';
import { nanoRoot } from './lib.mjs';

const argv = process.argv.slice(2);
const useFile = argv.includes('--file');
const page = argv.includes('--page') ? argv[argv.indexOf('--page') + 1] : 'test/browser-test.html';
const candidates = [argv.includes('--browser') ? argv[argv.indexOf('--browser') + 1] : null, process.env.NANO_BROWSER,
  'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', 'C:/Program Files/Microsoft/Edge/Application/msedge.exe', 'C:/Program Files/Google/Chrome/Application/chrome.exe'].filter(Boolean);
const browser = candidates.find(p => fs.existsSync(p));
if (!browser) { console.log('NOT RUN: no Edge or Chrome found (pass --browser <path>)'); process.exit(2); }

const profile = fs.mkdtempSync(path.join(os.tmpdir(), 'nano-browser-'));
// --mutant N: serve a copy of Nano/ in which the worklet wrapper has one deliberate error; the test has to fail (the chip and the engine are
// the same code on both sides of this test, so only the wrapper can be broken here: parity.mjs and plugin-parity.mjs prove the rest)
const MUTANTS = [
  ['timed events land one sample late', 'Math.round(at) - this.base)', 'Math.round(at) - this.base + 1)'],
  ['timed events are applied at once', 'return at === null || at === undefined ? this.engine.frame :', 'return true ? this.engine.frame :'],
  ['the last sample of every block is dropped', 'this.engine.render(out, 0, out.length);', 'this.engine.render(out, 0, out.length - 1);'],
  ['the processor ignores volume', "volume: po.volume === undefined ? 1 : po.volume", 'volume: 0.5'],
];
let root = nanoRoot, mutantName = '';
if (argv.includes('--mutant')) {
  const [name, find, replace] = MUTANTS[Number(argv[argv.indexOf('--mutant') + 1])];
  mutantName = name;
  root = fs.mkdtempSync(path.join(os.tmpdir(), 'nano-mutant-'));
  for (const f of ['dist/nano.js', 'dist/nano-worklet.js', 'test/browser-test.html']) {
    fs.mkdirSync(path.dirname(path.join(root, f)), { recursive: true });
    let t = fs.readFileSync(path.join(nanoRoot, f), 'utf8');
    if (f.startsWith('dist/')) { if (t.split(find).length !== 2) throw new Error('mutation target not unique in ' + f); t = t.replace(find, replace); }
    fs.writeFileSync(path.join(root, f), t);
  }
  console.log('MUTANT: ' + name);
}
const { server, port } = await startServer(root);
const url = useFile ? pathToFileURL(path.join(root, page)).href : `http://127.0.0.1:${port}/${page}`;
const child = spawn(browser, ['--headless=new', '--remote-debugging-port=0', `--user-data-dir=${profile}`, '--no-first-run', '--no-default-browser-check',
  '--autoplay-policy=no-user-gesture-required', '--disable-gpu', '--disable-extensions', 'about:blank'], { stdio: 'ignore' });
let exitCode = 1;
try {
  // the port the browser chose
  let devPort = 0;
  for (let i = 0; i < 100 && !devPort; i++) {
    await new Promise(r => setTimeout(r, 100));
    const f = path.join(profile, 'DevToolsActivePort');
    if (fs.existsSync(f)) devPort = Number(fs.readFileSync(f, 'utf8').split('\n')[0]);
  }
  if (!devPort) throw new Error('the browser did not open a debugging port');
  const targets = await (await fetch(`http://127.0.0.1:${devPort}/json/list`)).json();
  const target = targets.find(t => t.type === 'page');
  const ws = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; });
  let nextId = 1; const waiting = new Map(); const logs = [];
  ws.onmessage = (m) => {
    const d = JSON.parse(m.data);
    if (d.id && waiting.has(d.id)) { waiting.get(d.id)(d); waiting.delete(d.id); }
    else if (d.method === 'Runtime.consoleAPICalled') logs.push(d.params.args.map(a => a.value ?? a.description).join(' '));
    else if (d.method === 'Runtime.exceptionThrown') logs.push('EXCEPTION ' + (d.params.exceptionDetails.exception?.description ?? d.params.exceptionDetails.text));
  };
  const send = (method, params = {}) => new Promise((res) => { const id = nextId++; waiting.set(id, res); ws.send(JSON.stringify({ id, method, params })); });
  await send('Runtime.enable'); await send('Page.enable');
  await send('Page.navigate', { url });
  await new Promise(r => setTimeout(r, 1500));
  const r = await send('Runtime.evaluate', { expression: 'window.runNanoBrowserTests()', awaitPromise: true, returnByValue: true, timeout: 150000 });
  if (r.error || r.result.exceptionDetails) throw new Error('page error: ' + JSON.stringify(r.error ?? r.result.exceptionDetails).slice(0, 400));
  const results = r.result.result.value;
  console.log(`browser: ${browser}\nurl: ${url}`);
  for (const t of results) console.log(`${t.status.toUpperCase().padEnd(5)} ${t.name}${t.detail ? '  (' + t.detail + ')' : ''}`);
  for (const l of logs) console.log('console: ' + l);
  const failed = results.filter(t => t.status === 'fail').length;
  exitCode = failed ? 1 : 0;
  console.log(failed ? 'BROWSER TEST FAILED' : 'BROWSER TEST OK');
  ws.close();
} catch (e) {
  console.log('BROWSER TEST ERROR: ' + (e && e.message || e));
} finally {
  try { execFileSync('taskkill', ['/PID', String(child.pid), '/T', '/F'], { stdio: 'ignore' }); } catch { }
  server.close();
  await new Promise(r => setTimeout(r, 500));
  try { fs.rmSync(profile, { recursive: true, force: true }); } catch { }
  if (root !== nanoRoot) try { fs.rmSync(root, { recursive: true, force: true }); } catch { }   // the copy a --mutant run served
  process.exit(exitCode);
}
