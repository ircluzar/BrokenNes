// test-dpad-guard.mjs - the d-pad protection of the web builds (Lite), tested in four parts.
//
//   node test-dpad-guard.mjs [--repo <repository root>] [--no-browser] [--publish <Lite publish dir>] [--json out.json]
//
//   A. the pure function (WebLite/wwwroot/lib/dpadGuard.js, loaded as the page loads it): all 16 combinations of Up, Down, Left, Right, protected and not, against a table
//      written out by hand; the raw list is never changed; only a strict `true` allows opposites; short lists and null are harmless.
//   B. the wiring on paper: in nesInterop.js every call that hands the pad to the emulator (invokeMethodAsync 'UpdateInput' / 'UpdateInputForPlayer') is inside the one funnel
//      `_sendPad`, which calls the guard; a new human source that talks to the emulator by itself fails here.
//   C. the real nesInterop.js and dpadGuard.js in a real Edge/Chrome over the DevTools protocol (UAT/lite/litelab.mjs), with a stand-in for Blazor's call-back object that records what
//      the emulator would have been handed: real key events (the configured keyboard and the legacy keys), a gamepad (the d-pad buttons and the stick), a two-finger touch; protected by
//      default, with the setting on, with the setting off again, with a configuration that has no such field (what an older save gives).
//   D. (--publish) the real Lite site (a published build of WebLite): the Input settings page has the "Allow opposite d-pad directions" box, ticking it and saving stores
//      AllowOppositeDpad in the browser's IndexedDB and the game page picks it up; a fresh profile and an old saved setting (no such field) are protected; real key events
//      reach the recorded call-back as expected in each case.
//
// Every check prints PASS or FAIL with what it saw. Exit 0 only if every check passed. Part C and D are NOT RUN (said so, exit 2 only if nothing failed) when there is no browser.
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import vm from 'node:vm';
import { startLab, args } from './litelab.mjs';

const a = args();
const REPO = path.resolve(a.repo && a.repo !== true ? a.repo : path.join(import.meta.dirname, '..', '..'));
const results = [];
function check(group, name, ok, detail = '') {
  results.push({ group, name, ok: !!ok, detail: String(detail) });
  console.log(`${ok ? 'PASS' : 'FAIL'} [${group}] ${name}${detail !== '' ? ' - ' + detail : ''}`);
  return !!ok;
}
const sleep = ms => new Promise(r => setTimeout(r, ms));
const LIB = path.join(REPO, 'WebLite', 'wwwroot', 'lib');

// ------------------------------------------------------------------------------------------------------------------------- A. the pure function
// Up 1, Down 2, Left 4, Right 8. What a protected pad must show for each of the 16 combinations (Up+Down cancel, Left+Right cancel, a lone direction stays):
const PROTECTED = [0, 1, 2, 0, 4, 5, 6, 4, 8, 9, 10, 8, 0, 1, 2, 0];
const dirName = n => n === 0 ? 'neutral' : [[1, 'Up'], [2, 'Down'], [4, 'Left'], [8, 'Right']].filter(([b]) => n & b).map(([, s]) => s).join('+');
const listOf = (n, others) => { const l = new Array(12).fill(false); if (others) for (const i of [0, 1, 2, 3, 8, 9, 10, 11]) l[i] = true; l[4] = !!(n & 1); l[5] = !!(n & 2); l[6] = !!(n & 4); l[7] = !!(n & 8); return l; };
const dirsOf = l => (l[4] ? 1 : 0) | (l[5] ? 2 : 0) | (l[6] ? 4 : 0) | (l[7] ? 8 : 0);

{
  const sandbox = { window: {}, console };
  vm.runInNewContext(fs.readFileSync(path.join(LIB, 'dpadGuard.js'), 'utf8'), sandbox);
  const g = sandbox.window.dpadGuard;
  check('pure', 'dpadGuard.js defines window.dpadGuard.resolve', !!g && typeof g.resolve === 'function');
  const bad = [];
  for (let n = 0; n < 16; n++) for (const others of [false, true]) {
    const raw = listOf(n, others), copy = raw.slice();
    const p = g.resolve(raw, false), u = g.resolve(raw, true);
    const want = listOf(PROTECTED[n], others);
    if (JSON.stringify(p) !== JSON.stringify(want)) bad.push(`protected ${dirName(n)}${others ? ' + others' : ''} -> ${dirName(dirsOf(p))}`);
    if (JSON.stringify(u) !== JSON.stringify(raw)) bad.push(`unprotected ${dirName(n)}`);
    if (JSON.stringify(raw) !== JSON.stringify(copy)) bad.push(`the raw list was changed for ${dirName(n)}`);
    if (p === raw || u === raw) bad.push('the result is the caller\'s own array (must be a copy)');
  }
  check('pure', 'all 16 combinations of the four directions, with and without the other buttons held, protected and not; the raw list is kept and a copy returned', bad.length === 0, bad.slice(0, 3).join('; '));
  check('pure', 'Left+Right -> neutral and Up+Down -> neutral (the two cases named)', dirsOf(g.resolve(listOf(12, false), false)) === 0 && dirsOf(g.resolve(listOf(3, false), false)) === 0);
  check('pure', 'only a strict `true` allows opposites (undefined, null, 1, "true" all protect)', [undefined, null, 1, 'true', 0, false].every(v => dirsOf(g.resolve(listOf(12, false), v)) === 0) && dirsOf(g.resolve(listOf(12, false), true)) === 12);
  check('pure', 'a list shorter than eight, an empty one and null are harmless', g.resolve([true, true], false).length === 2 && g.resolve([], false).length === 0 && g.resolve(null, false).length === 0);
}

// ------------------------------------------------------------------------------------------------------------------------- B. the wiring on paper
{
  const js = fs.readFileSync(path.join(LIB, 'nesInterop.js'), 'utf8');
  const start = js.indexOf('_sendPad: function');
  const end = start < 0 ? -1 : js.indexOf('\n    },', start);
  const funnel = start >= 0 && end > start ? js.slice(start, end) : '';
  const rest = start >= 0 && end > start ? js.slice(0, start) + js.slice(end) : js;
  check('wiring', '_sendPad exists, calls dpadGuard.resolve once and then both emulator entries', /dpadGuard\.resolve\(/.test(funnel) && (funnel.match(/dpadGuard\.resolve\(/g) || []).length === 1 && /invokeMethodAsync\('UpdateInputForPlayer'/.test(funnel) && /invokeMethodAsync\('UpdateInput'/.test(funnel));
  const strays = [...rest.matchAll(/invokeMethodAsync\(\s*'(UpdateInput|UpdateInputForPlayer)'/g)].map(m => m[1]);
  check('wiring', 'no other code in nesInterop.js hands the pad to the emulator behind the guard\'s back', strays.length === 0, strays.join(', '));
  const idx = fs.readFileSync(path.join(REPO, 'WebLite', 'wwwroot', 'index.html'), 'utf8');
  check('wiring', 'index.html loads dpadGuard.js before nesInterop.js', idx.indexOf('lib/dpadGuard.js') > 0 && idx.indexOf('lib/dpadGuard.js') < idx.indexOf('lib/nesInterop.js'));
  const web = fs.readFileSync(path.join(REPO, 'Web', 'wwwroot', 'lib', 'nesInterop.js'), 'utf8');
  const wstart = web.indexOf('_sendPad()'), wend = wstart < 0 ? -1 : web.indexOf('\n    },', wstart);
  const wrest = wstart >= 0 && wend > wstart ? web.slice(0, wstart) + web.slice(wend) : web;
  check('wiring', 'the minimal Web build: the keyboard reaches the emulator only through its protected _sendPad', wstart >= 0 && /s\[4\] && s\[5\]/.test(web) && /s\[6\] && s\[7\]/.test(web) && !/invokeMethodAsync\(\s*'UpdateInput'/.test(wrest));
}

// ------------------------------------------------------------------------------------------------------------------------- C. a real browser, the real files
const SITE_HTML = `<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>dpad</title></head><body style="margin:0">
<div id="touch-controller" style="position:fixed;left:0;top:0;width:390px;height:120px">
  <div data-btn="left"  style="position:absolute;left:0px;  top:0;width:80px;height:80px"></div>
  <div data-btn="right" style="position:absolute;left:100px;top:0;width:80px;height:80px"></div>
  <div data-btn="up"    style="position:absolute;left:200px;top:0;width:80px;height:80px"></div>
  <div data-btn="down"  style="position:absolute;left:300px;top:0;width:80px;height:80px"></div>
</div>
<script src="lib/dpadGuard.js"></script><script src="lib/nesInterop.js"></script></body></html>`;

/** In the page: a stand-in for Blazor's call-back object that records what the emulator would have been handed, and helpers to read it. */
const SETUP = `(() => {
  window.__sent = [];
  window.__ref = { invokeMethodAsync(name, ...a) { window.__sent.push({ name, a: JSON.parse(JSON.stringify(a)) }); return Promise.resolve(); } };
  window.__last = () => { const s = window.__sent[window.__sent.length - 1]; return s ? s.a[s.a.length - 1] : null; };
  window.__dirs = () => { const l = window.__last(); return l ? (l[4] ? 1 : 0) | (l[5] ? 2 : 0) | (l[6] ? 4 : 0) | (l[7] ? 8 : 0) : -1; };
  window.__rawDirs = () => { const l = window.nesInputState; return (l[4] ? 1 : 0) | (l[5] ? 2 : 0) | (l[6] ? 4 : 0) | (l[7] ? 8 : 0); };
  return true; })()`;

const KEYS = { up: 'ArrowUp', down: 'ArrowDown', left: 'ArrowLeft', right: 'ArrowRight' };

async function testBrowser() {
  let lab;
  const site = fs.mkdtempSync(path.join(os.tmpdir(), 'dpad-site-'));
  try {
    fs.mkdirSync(path.join(site, 'lib'));
    for (const f of ['dpadGuard.js', 'nesInterop.js']) fs.copyFileSync(path.join(LIB, f), path.join(site, 'lib', f));
    fs.writeFileSync(path.join(site, 'index.html'), SITE_HTML);
    lab = await startLab({ publishDir: site, mobile: true });
  } catch (e) {
    console.log('NOT RUN [browser] part C: ' + e.message);
    return 'not-run';
  }
  try {
    const page = lab.page;
    const load = async () => {
      await lab.cdp.send('Page.navigate', { url: lab.base + '/index.html' });
      await page.waitFor('!!window.nesInterop && !!window.dpadGuard', 30000);
      await page.eval(SETUP);
    };
    const dirs = async () => page.eval('window.__dirs()');
    const key = async (name, down) => { await page.key(KEYS[name] ?? name, down); await sleep(40); };

    // ---- the configured keyboard, protected by default
    await load();
    await page.eval(`nesInterop.registerInput(window.__ref); nesInterop.configureInput(window.__ref, { allowOppositeDpad: false, player1: { device: 'Keyboard' }, player2: { device: 'Touch' } }); true`);
    const seq = [];
    await key('left', true); seq.push(dirName(await dirs()));
    await key('right', true); seq.push(dirName(await dirs()));
    const rawBoth = await page.eval('window.__rawDirs()');
    await key('right', false); seq.push(dirName(await dirs()));
    await key('up', true); await key('down', true); seq.push(dirName(await dirs()));
    await key('down', false); seq.push(dirName(await dirs()));
    await key('up', false); await key('left', false); seq.push(dirName(await dirs()));
    check('browser', 'configured keyboard, protected: Left, +Right, Right let go, +Up+Down, Down let go, all let go', seq.join(' | ') === 'Left | neutral | Left | Left | Up+Left | neutral', seq.join(' | '));
    check('browser', '... and the raw state kept both Left and Right while neither was sent', rawBoth === 12, 'raw ' + rawBoth);
    await key('left', true); await key('right', true); await page.key('KeyX', true); await sleep(40);
    const withA = await page.eval('window.__last()');
    check('browser', 'with Left+Right held, the A key (X) still reaches the emulator and the pair stays neutral', withA[0] === true && !withA[6] && !withA[7]);
    await page.key('KeyX', false); await key('left', false); await key('right', false);

    // ---- the setting on, then off again, in the same page (configureInput is how the saved setting arrives)
    await page.eval(`nesInterop.configureInput(window.__ref, { allowOppositeDpad: true, player1: { device: 'Keyboard' }, player2: { device: 'Touch' } }); true`);
    await key('left', true); await key('right', true);
    check('browser', 'allowOppositeDpad true: Left+Right both reach the emulator', (await dirs()) === 12, dirName(await dirs()));
    await key('up', true); await key('down', true);
    check('browser', '... and Up+Down too', (await dirs()) === 15, dirName(await dirs()));
    for (const k of ['left', 'right', 'up', 'down']) await key(k, false);
    await page.eval(`nesInterop.configureInput(window.__ref, { allowOppositeDpad: false, player1: { device: 'Keyboard' }, player2: { device: 'Touch' } }); true`);
    await key('left', true); await key('right', true);
    check('browser', 'allowOppositeDpad false again: Left+Right neutral again', (await dirs()) === 0, dirName(await dirs()));
    await key('left', false); await key('right', false);

    // ---- a configuration with no such field (what an older save gives) and the legacy keys (configureInput never called) are protected
    await page.eval(`nesInterop.configureInput(window.__ref, { player1: { device: 'Keyboard' }, player2: { device: 'Touch' } }); true`);
    await key('left', true); await key('right', true);
    check('browser', 'a configuration without the field is protected', (await dirs()) === 0, dirName(await dirs()));
    await key('left', false); await key('right', false);
    await load();
    await page.eval('nesInterop.registerInput(window.__ref); true');           // no configureInput: the legacy fixed keys
    await key('left', true); await key('right', true);
    const legacyBoth = await dirs();
    await key('right', false);
    check('browser', 'the legacy keyboard (no configuration at all) is protected: Left+Right neutral, Right let go gives Left back', legacyBoth === 0 && (await dirs()) === 4, `${dirName(legacyBoth)} then ${dirName(await dirs())}`);
    await key('left', false);

    // ---- a gamepad: the d-pad buttons and the stick together
    await load();
    await page.eval(`window.__pad = { index: 0, id: 'fake pad', connected: true, buttons: Array.from({ length: 17 }, () => ({ pressed: false })), axes: [0, 0, 0, 0] };
      navigator.getGamepads = () => [window.__pad];
      nesInterop.registerInput(window.__ref);
      nesInterop.configureInput(window.__ref, { allowOppositeDpad: false, player1: { device: 'Gamepad', gamepadIndex: 0 }, player2: { device: 'Touch' } }); true`);
    const padWait = async (cond, what) => { try { await page.waitFor(cond, 5000, 50); return true; } catch { return false; } };
    await page.eval('window.__pad.buttons[14].pressed = true; true');
    const okLeft = await padWait('window.__dirs() === 4');
    await page.eval('window.__pad.buttons[15].pressed = true; true');
    await sleep(300);
    const padBoth = await dirs(), padRaw = await page.eval('window.__rawDirs()');
    check('browser', 'gamepad: d-pad Left reaches the emulator; Left+Right together are neutral (the raw pad state still has both)', okLeft && padBoth === 0 && padRaw === 12, `left ${okLeft}, both -> ${dirName(padBoth)}, raw ${padRaw}`);
    await page.eval('window.__pad.buttons[14].pressed = false; true');
    const okRight = await padWait('window.__dirs() === 8');
    await page.eval('window.__pad.buttons[15].pressed = false; window.__pad.axes[0] = -1; window.__pad.buttons[15].pressed = true; true');   // stick left + d-pad right
    await sleep(300);
    check('browser', 'gamepad: Left let go gives Right back; the stick pushed left while d-pad Right is held is neutral too', okRight && (await dirs()) === 0, `${okRight}, ${dirName(await dirs())}`);
    await page.eval('window.__pad.buttons.forEach(b => b.pressed = false); window.__pad.axes[0] = 0; navigator.getGamepads = () => []; true');
    await sleep(200);

    // ---- the touch controller: two fingers on Left and Right
    await load();
    await page.eval(`nesInterop._mainRef = window.__ref; nesInterop.registerInput(window.__ref); nesInterop.configureInput(window.__ref, { allowOppositeDpad: false, player1: { device: 'Touch' }, player2: { device: 'Touch' } }); nesInterop.initTouchController('touch-controller'); true`);
    const pt = (id, x, y = 40) => ({ x, y, id, radiusX: 4, radiusY: 4, force: 1 });
    await lab.cdp.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [pt(1, 40)] });
    await sleep(60);
    const touchLeft = await dirs();
    await lab.cdp.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [pt(1, 40), pt(2, 140)] });
    await sleep(60);
    const touchBoth = await dirs(), touchRaw = await page.eval('window.__rawDirs()');
    await lab.cdp.send('Input.dispatchTouchEvent', { type: 'touchMove', touchPoints: [pt(1, 40), pt(2, 140, 110)] });   // the second finger slides off the Right button
    await sleep(60);
    const touchAfter = await dirs();
    await lab.cdp.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
    await sleep(60);
    check('browser', 'touch controller: a finger on Left; a second on Right makes it neutral; the second sliding off Right gives Left back; all lifted is neutral',
      touchLeft === 4 && touchBoth === 0 && touchRaw === 12 && touchAfter === 4 && (await dirs()) === 0, `${dirName(touchLeft)} | ${dirName(touchBoth)} (raw ${touchRaw}) | ${dirName(touchAfter)} | ${dirName(await dirs())}`);
    const errs = page.consoleErrors();
    check('browser', 'no script errors in the page', errs.length === 0, errs.slice(0, 2).join(' / '));
  } finally {
    await lab.close();
    try { fs.rmSync(site, { recursive: true, force: true }); } catch {}
  }
  return 'ran';
}

// ------------------------------------------------------------------------------------------------------------------------- D. the real Lite site
async function testSite(publish) {
  let lab;
  try { lab = await startLab({ publishDir: publish }); } catch (e) { console.log('NOT RUN [site] part D: ' + e.message); return 'not-run'; }
  try {
    const page = lab.page;
    const SPY = `(() => { const r = nesInterop._mainRef; if (!r || r.__spied) return !!r; window.__sent = []; const f = r.invokeMethodAsync.bind(r);
      r.invokeMethodAsync = (name, ...a) => { if (name.startsWith('UpdateInput')) window.__sent.push({ name, a: JSON.parse(JSON.stringify(a)) }); return f(name, ...a); }; r.__spied = true; return true; })()`;
    const LAST = `(() => { const s = window.__sent[window.__sent.length - 1]; if (!s) return -1; const l = s.a[s.a.length - 1]; return (l[4] ? 1 : 0) | (l[5] ? 2 : 0) | (l[6] ? 4 : 0) | (l[7] ? 8 : 0); })()`;
    const key = async (code, down) => { await page.key(code, down); await sleep(60); };
    const game = async () => {           // the game page, with the emulator up and the input settings applied
      await lab.open('/', { timeout: 240000 });
      await page.waitFor('window.nesInterop._mainRef != null && window.nesInterop._kbdInstalled === true', 120000);
      await page.eval(SPY);
    };
    const leftRight = async () => {
      await key('ArrowLeft', true); await key('ArrowRight', true);
      const both = await page.eval(LAST);
      await key('ArrowLeft', false); await key('ArrowRight', false);
      return both;
    };

    // a fresh profile: protected
    await game();
    check('site', 'a fresh profile: nesInterop._allowOpposite is false', (await page.eval('window.nesInterop._allowOpposite')) === false);
    check('site', 'a fresh profile: real Left+Right key events reach the emulator as neutral', (await leftRight()) === 0);

    // the Input settings page has the box, unticked
    await lab.open('/input', { timeout: 240000 }).catch(() => {});
    await page.waitFor("!!document.getElementById('allow-opposite-dpad')", 60000);
    check('site', 'Input settings: the "Allow opposite d-pad directions" box exists and is unticked', (await page.eval("document.getElementById('allow-opposite-dpad').checked")) === false
      && (await page.eval("document.body.innerText.includes('Allow opposite d-pad directions')")));
    await page.eval("document.getElementById('allow-opposite-dpad').click(); true");
    await sleep(300);
    check('site', '... clicking it ticks it', (await page.eval("document.getElementById('allow-opposite-dpad').checked")) === true);
    await page.eval("[...document.querySelectorAll('button.opt-link')].find(b => b.innerText.trim() === 'Save').click(); true");
    await page.waitFor("!!document.querySelector('#nes-canvas') && !!window.nesInterop && !!window.nesInterop._dotNetRef", 240000);
    await page.waitFor('window.nesInterop._mainRef != null && window.nesInterop._kbdInstalled === true', 120000);
    await sleep(500);
    await page.eval(SPY);
    const stored = await page.eval("nesInterop.idbGetItem('input_settings_v1')");
    check('site', 'Save stores AllowOppositeDpad: true in the browser (IndexedDB input_settings_v1)', /"AllowOppositeDpad"\s*:\s*true/.test(String(stored)), String(stored).slice(0, 60));
    check('site', 'the game page applies it: nesInterop._allowOpposite is true', (await page.eval('window.nesInterop._allowOpposite')) === true);
    check('site', 'with the box ticked, real Left+Right key events reach the emulator together', (await leftRight()) === 12);

    // untick and save: protected again
    await lab.open('/input', { timeout: 240000 }).catch(() => {});
    await page.waitFor("!!document.getElementById('allow-opposite-dpad')", 60000);
    check('site', 'Input settings reopened: the box is ticked (the setting was remembered)', (await page.eval("document.getElementById('allow-opposite-dpad').checked")) === true);
    await page.eval("document.getElementById('allow-opposite-dpad').click(); [...document.querySelectorAll('button.opt-link')].find(b => b.innerText.trim() === 'Save').click(); true");
    await page.waitFor("!!document.querySelector('#nes-canvas') && !!window.nesInterop && !!window.nesInterop._dotNetRef", 240000);
    await page.waitFor('window.nesInterop._mainRef != null && window.nesInterop._kbdInstalled === true', 120000);
    await sleep(500);
    await page.eval(SPY);
    check('site', 'unticked and saved: protected again, Left+Right neutral', (await page.eval('window.nesInterop._allowOpposite')) === false && (await leftRight()) === 0);

    // an old saved setting (no AllowOppositeDpad in it) is protected
    await page.eval(`nesInterop.idbSetItem('input_settings_v1', JSON.stringify({ Player1: { Player: 1, Device: 1 }, Player2: { Player: 2, Device: 0 } })).then(() => true)`);
    await game();
    check('site', 'an old saved input setting without the field: protected', (await page.eval('window.nesInterop._allowOpposite')) === false && (await leftRight()) === 0);
    const errs = page.consoleErrors().filter(e => !/favicon/.test(e));
    check('site', 'no script errors in the page', errs.length === 0, errs.slice(0, 2).join(' / '));
  } finally { await lab.close(); }
  return 'ran';
}

// ------------------------------------------------------------------------------------------------------------------------- run
let notRun = 0;
if (!a['no-browser']) { if ((await testBrowser()) === 'not-run') notRun++; } else { console.log('NOT RUN [browser] part C (--no-browser)'); notRun++; }
if (a.publish && a.publish !== true) { if ((await testSite(path.resolve(a.publish))) === 'not-run') notRun++; } else { console.log('NOT RUN [site] part D (no --publish <dir>)'); notRun++; }

const fail = results.filter(r => !r.ok).length;
console.log(`\n${results.length - fail} passed, ${fail} failed${notRun ? `, ${notRun} part(s) NOT RUN` : ''}`);
if (a.json && a.json !== true) fs.writeFileSync(a.json, JSON.stringify({ passed: results.length - fail, failed: fail, notRun, results }, null, 1));
process.exit(fail ? 1 : (notRun ? 2 : 0));
