// test-lite-bugs.mjs - the bug capture (BrokenDev milestone D6) in BrokenNes Lite (the Blazor WebAssembly AOT build), in a real Edge/Chrome over the
// DevTools protocol.
//
//   node test-lite-bugs.mjs --publish <AOT publish dir> --roms <scratch dir> --nes <vrun_game.nes> --gb <vrun_voc.gbc> --workshop <BrokenNes.Workshop.exe>
//                           --out <dir for the zips> [--json out.json] [--show]
//
// Frames are run with the page's own FrameTick (as the other Lite suites), the pad through UpdateInput. What it checks:
//   - a NES game (VRUN) played 35 s with input is being recorded: at least 30 s held, a state's cost in WebAssembly;
//   - the page's Bug button (a real click on it; the browser's note prompt answered, the download caught where the page hands it to the browser)
//     gives a zip with the note, and Workshop --bug play replays that zip exactly (every keyframe and the last picture);
//   - the negative controls: the prompt cancelled gives no download; the capture off records nothing and refuses;
//   - a console session (the user's Game Boy game, BrokenDev's build of vrun-voc: any version replays): its zip replays exactly;
//   - the speed: frame times with the capture on and off (600 frames each, interleaved twice): Lite still holds 60 fps with it on.
// It cannot see the browser's own download bar or a phone.
import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { startLab, args } from './litelab.mjs';

const a = args();
const results = [];
function check(name, ok, detail = '') {
  results.push({ group: 'bugs', name, ok: !!ok, detail: String(detail) });
  console.log(`${ok ? 'PASS' : 'FAIL'} [bugs] ${name}${detail !== '' ? ' - ' + detail : ''}`);
  return !!ok;
}
for (const k of ['nes', 'gb', 'workshop']) if (!a[k] || !fs.existsSync(a[k])) { console.log(`missing --${k}: ${a[k]}`); process.exit(2); }
fs.mkdirSync(path.join(a.roms, 'nes'), { recursive: true }); fs.mkdirSync(path.join(a.roms, 'gb'), { recursive: true });
fs.copyFileSync(a.nes, path.join(a.roms, 'nes', 'vrun_game.nes'));
fs.copyFileSync(a.gb, path.join(a.roms, 'gb', 'vrun_voc.gbc'));
fs.mkdirSync(a.out, { recursive: true });

// frames with a pad script (the same as Workshop --bug-test's: Start now and then, Right, A), timed one by one
const FRAMES = `window.__frames = async (from, n) => {
  const ref = (window.__ref = nesInterop._dotNetRef || window.__ref);
  if (nesInterop._loopActive) nesInterop.stopEmulationLoop();
  const main = nesInterop._mainRef || ref;
  const times = [];
  let last = '';
  for (let f = from; f < from + n; f++) {
    const st = new Array(12).fill(false);
    if (f % 240 >= 60 && f % 240 < 66) st[3] = true;
    if (f % 97 < 20) st[7] = true;
    if (f % 151 < 9) st[0] = true;
    const key = st.join();
    if (key !== last) { await main.invokeMethodAsync('UpdateInput', st); last = key; }
    const t0 = performance.now();
    await ref.invokeMethodAsync('FrameTick');
    times.push(performance.now() - t0);
  }
  return times;
};
window.__status = async () => JSON.parse(await (nesInterop._dotNetRef || window.__ref).invokeMethodAsync('BugStatusJson'));
window.__b64 = u8 => { let s = ''; const c = 0x8000; for (let i = 0; i < u8.length; i += c) s += String.fromCharCode.apply(null, u8.subarray(i, i + c)); return btoa(s); };
// the page's download and prompt, caught: what the Bug button hands to the browser
window.__dl = null; window.__note = 'Lite smoke: the Bug button';
const realDl = nesInterop.downloadBytes.bind(nesInterop);
nesInterop.downloadBytes = (name, bytes, mime) => { window.__dl = { name, mime, b64: window.__b64(bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes)) }; return true; };
window.prompt = () => window.__note;
1`;

async function open(lab, rom) {
  const page = await lab.open('/?rom=' + encodeURIComponent(rom), { timeout: 240000 });
  await page.waitFor("document.body.innerText.includes('ROM: roms/')", 240000);
  await page.eval(FRAMES);
  return page;
}
async function run(page, from, n) {
  const times = [];
  for (let f = from; f < from + n; f += 300) times.push(...await page.eval(`window.__frames(${f}, ${Math.min(300, from + n - f)})`, { timeout: 600000 }));
  return times;
}
function replay(zip) {
  const r = spawnSync(a.workshop, ['--bug', 'play', '--bug', zip, '--json'], { encoding: 'utf8', maxBuffer: 64 << 20 });
  let j = null; try { j = JSON.parse(r.stdout); } catch { }
  return { code: r.status, j, text: (r.stdout || '') + (r.stderr || '') };
}
function stats(t) {
  const s = [...t].sort((x, y) => x - y), mean = t.reduce((x, y) => x + y, 0) / t.length;
  return { mean, p95: s[Math.floor(s.length * 0.95)], max: s[s.length - 1], over: t.filter(x => x > 1000 / 60).length };
}
async function clickBug(page) {
  await page.eval(`window.__dl = null; document.getElementById('bugCaptureBtn').click(); 1`);
  for (let i = 0; i < 60; i++) { const d = await page.eval(`window.__dl ? 1 : 0`); if (d) break; await new Promise(r => setTimeout(r, 250)); }
  const dl = await page.eval(`window.__dl`, { timeout: 120000 });
  await page.eval(`nesInterop._loopActive && nesInterop.stopEmulationLoop(); 1`);   // the button restarts the page's own loop; the test drives the frames
  return dl;
}

const lab = await startLab({ publishDir: a.publish, romsDir: a.roms, headless: !a.show });
try {
  // ------------------------------------------------------------ the NES (VRUN)
  let page = await open(lab, 'roms/nes/vrun_game.nes');
  let t = await run(page, 0, 2160);
  let st = await page.eval(`window.__status()`);
  check('a NES game played 36 s is being recorded (at least 30 s held)', st.Recording && st.Console === 'nes' && st.HeldSeconds >= 30, `${JSON.stringify(st)}`);

  const dl = await clickBug(page);
  let zip = dl ? path.join(a.out, dl.name) : null;
  if (dl) fs.writeFileSync(zip, Buffer.from(dl.b64, 'base64'));
  check('the Bug button hands a zip to the browser (the note asked, the download named after the game)', dl && /^bug_vrun_game_\d{4}-\d\d-\d\d_\d{6}\.zip$/.test(dl.name) && dl.mime === 'application/zip' && fs.statSync(zip).size > 100000, dl ? `${dl.name}, ${fs.statSync(zip).size} bytes` : 'no download');
  if (zip) {
    const r = replay(zip);
    check('Workshop --bug play replays the Lite zip exactly (every keyframe and the last picture)', r.code === 0 && r.j?.inSync && r.j?.note === 'Lite smoke: the Bug button' && r.j?.app === 'BrokenNes Lite', r.j ? `${r.j.summary}; note '${r.j.note}', ${r.j.seconds} s` : r.text.slice(0, 400));
  }
  await page.eval(`window.__note = null; 1`);
  const none = await clickBug(page);
  check('NEGATIVE: the note prompt cancelled gives no download', none === null, none ? `a download: ${none.name}` : 'no download');

  // the speed, on and off (interleaved, after a warm-up), and the state's cost in WebAssembly
  const on1 = await run(page, 2160, 600);
  await page.eval(`(nesInterop._dotNetRef || window.__ref).invokeMethodAsync('SetBugCapture', false)`);
  const off1 = await run(page, 2760, 600);
  await page.eval(`(nesInterop._dotNetRef || window.__ref).invokeMethodAsync('SetBugCapture', true)`);
  const on2 = await run(page, 3360, 600);
  await page.eval(`(nesInterop._dotNetRef || window.__ref).invokeMethodAsync('SetBugCapture', false)`);
  const off2 = await run(page, 3960, 600);
  st = await page.eval(`window.__status()`);
  const off = await page.eval(`(nesInterop._dotNetRef || window.__ref).invokeMethodAsync('CaptureBugZip', 'off')`);
  check('NEGATIVE: the capture off records nothing and refuses to capture', !st.Recording && !st.Enabled && off.error && /off/.test(off.error), `${JSON.stringify(st)}; ${off.error}`);
  const sOn = stats(on1.concat(on2)), sOff = stats(off1.concat(off2));
  check('Lite holds 60 fps with the capture on (mean frame well under 16.7 ms; the frames over it counted)', 1000 / sOn.mean >= 60 && sOn.p95 < 1000 / 60,
        `on: ${sOn.mean.toFixed(2)} ms mean (${(1000 / sOn.mean).toFixed(0)} fps), p95 ${sOn.p95.toFixed(2)}, max ${sOn.max.toFixed(1)}, ${sOn.over} of 1200 over 16.7 ms; off: ${sOff.mean.toFixed(2)} ms (${(1000 / sOff.mean).toFixed(0)} fps), p95 ${sOff.p95.toFixed(2)}, max ${sOff.max.toFixed(1)}, ${sOff.over} over`);
  results.at(-1).speed = { on: sOn, off: sOff };

  // ------------------------------------------------------------ a console session (the user's Game Boy game, BrokenDev's build of vrun-voc: any version replays)
  await page.eval(`(nesInterop._dotNetRef || window.__ref).invokeMethodAsync('SetBugCapture', true)`);
  page = await open(lab, 'roms/gb/vrun_voc.gbc');
  await run(page, 0, 1500);
  const z = await page.eval(`(async () => { const r = await (nesInterop._dotNetRef || window.__ref).invokeMethodAsync('CaptureBugZip', 'Lite smoke: a Game Boy session'); return r.error ? { error: r.error } : { name: r.name, b64: typeof r.data === 'string' ? r.data : window.__b64(r.data), seconds: r.seconds }; })()`, { timeout: 120000 });
  if (z.error) check('a Game Boy session: CaptureBugZip', false, z.error);
  else {
    const gz = path.join(a.out, z.name);
    fs.writeFileSync(gz, Buffer.from(z.b64, 'base64'));
    const r = replay(gz);
    check('a Game Boy session (vrun-voc, 25 s): its zip replays exactly', r.code === 0 && r.j?.inSync && r.j?.console === 'gbc', r.j ? `${r.j.summary}` : r.text.slice(0, 400));
  }
  const errs = page.consoleErrors();
  check('no errors in the page', errs.length === 0, errs.slice(0, 3).join(' | '));
} finally { await lab.close(); }

const failed = results.filter(r => !r.ok);
console.log(`\n${results.length - failed.length} passed, ${failed.length} failed`);
if (a.json) fs.writeFileSync(a.json, JSON.stringify(results, null, 2));
process.exit(failed.length ? 1 : 0);
