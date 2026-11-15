// Minimal Chrome DevTools Protocol client over Node's built-in WebSocket (Node 22+). No packages.
import { spawn, execSync } from 'node:child_process'; import fs from 'node:fs';
export const EDGE = 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe';
export const sleep = ms => new Promise(r => setTimeout(r, ms));
export async function launchEdge(profileDir, port) {
  fs.mkdirSync(profileDir, { recursive: true });
  const p = spawn(EDGE, ['--headless=new', `--remote-debugging-port=${port}`, `--user-data-dir=${profileDir}`,
    '--disable-background-timer-throttling', '--disable-renderer-backgrounding', '--disable-backgrounding-occluded-windows',
    '--autoplay-policy=no-user-gesture-required', '--no-first-run', '--no-default-browser-check', '--window-size=1280,900', 'about:blank'], { stdio: 'ignore' });
  for (let i = 0; i < 50; i++) { try { const v = await (await fetch(`http://127.0.0.1:${port}/json/version`)).json(); return { proc: p, ws: v.webSocketDebuggerUrl }; } catch { await sleep(200); } }
  p.kill(); throw new Error('edge did not start');
}
export function killTree(pid) { try { execSync(`taskkill /PID ${pid} /T /F`, { stdio: 'ignore' }); } catch {} }
export class Cdp {
  constructor(url) { this.ws = new WebSocket(url); this.id = 0; this.pending = new Map(); this.handlers = [];
    this.ready = new Promise((ok, bad) => { this.ws.onopen = ok; this.ws.onerror = bad; });
    this.ws.onmessage = ev => { const m = JSON.parse(ev.data);
      if (m.id && this.pending.has(m.id)) { const { ok, bad } = this.pending.get(m.id); this.pending.delete(m.id); m.error ? bad(new Error(m.error.message)) : ok(m.result); }
      else for (const h of this.handlers) h(m); }; }
  send(method, params = {}, sessionId) { const id = ++this.id; this.ws.send(JSON.stringify({ id, method, params, sessionId }));
    return new Promise((ok, bad) => { this.pending.set(id, { ok, bad }); setTimeout(() => { if (this.pending.delete(id)) bad(new Error('timeout ' + method)); }, 60000); }); }
  close() { try { this.ws.close(); } catch {} }
}
// A page session with helpers.
export async function openPage(cdp) {
  const { targetId } = await cdp.send('Target.createTarget', { url: 'about:blank' });
  const { sessionId } = await cdp.send('Target.attachToTarget', { targetId, flatten: true });
  const s = (m, p) => cdp.send(m, p, sessionId);
  const logs = [];
  cdp.handlers.push(m => { if (m.sessionId !== sessionId) return;
    if (m.method === 'Runtime.exceptionThrown') logs.push({ level: 'exception', text: m.params.exceptionDetails?.exception?.description || m.params.exceptionDetails?.text });
    if (m.method === 'Runtime.consoleAPICalled' && (m.params.type === 'error' || m.params.type === 'warning')) logs.push({ level: m.params.type, text: m.params.args.map(a => a.value ?? a.description).join(' ').slice(0, 300) }); });
  await s('Page.enable'); await s('Runtime.enable'); await s('DOM.enable');
  const page = {
    targetId, sessionId, s, logs,
    async eval(expr) { const r = await s('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true }); if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description || r.exceptionDetails.text); return r.result.value; },
    async goto(url) { await s('Page.navigate', { url }); },
    async shot(file, clip) { const r = await s('Page.captureScreenshot', { format: 'png', ...(clip ? { clip: { ...clip, scale: 1 } } : {}) }); fs.writeFileSync(file, Buffer.from(r.data, 'base64')); },
    async key(code, key, holdMs = 120) { await s('Input.dispatchKeyEvent', { type: 'rawKeyDown', code, key, windowsVirtualKeyCode: VK[code] || 0 }); await sleep(holdMs); await s('Input.dispatchKeyEvent', { type: 'keyUp', code, key, windowsVirtualKeyCode: VK[code] || 0 }); },
    async touch(points, holdMs = 150) { // points: [{x,y}] pressed together
      await s('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: points.map((p, i) => ({ x: p.x, y: p.y, id: i })) }); await sleep(holdMs);
      await s('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] }); },
    async rect(sel) { return this.eval(`(()=>{const e=document.querySelector(${JSON.stringify(sel)}); if(!e) return null; const r=e.getBoundingClientRect(); const cs=getComputedStyle(e); return {x:r.x,y:r.y,w:r.width,h:r.height,visible: cs.display!=='none' && cs.visibility!=='hidden' && r.width>0}})()`); },
    async click(sel) { const r = await this.rect(sel); if (!r || !r.visible) return false; for (const type of ['mousePressed', 'mouseReleased']) await s('Input.dispatchMouseEvent', { type, x: r.x + r.w / 2, y: r.y + r.h / 2, button: 'left', clickCount: 1 }); return true; },
  };
  return page;
}
const VK = { ArrowUp: 38, ArrowDown: 40, ArrowLeft: 37, ArrowRight: 39, Enter: 13, Space: 32, KeyZ: 90, KeyX: 88, KeyA: 65, KeyS: 83, KeyQ: 81, KeyW: 87 };
