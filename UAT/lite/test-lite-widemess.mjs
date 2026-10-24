// test-lite-widemess.mjs - WideMess in BrokenNes Lite (the Blazor WebAssembly AOT build), in a real Edge/Chrome over the DevTools protocol (WideMess milestone 7a).
//
//   node test-lite-widemess.mjs --publish <AOT publish dir> --roms <scratch dir> [--wms <WideMess\testroms\bin>] [--library <SNES library dir>] [--only group,group] [--frames 600] [--json out.json] [--show]
//
// The WideMess test ROMs (WideMess\testroms\bin) are copied into <roms>\wms, the SNES games (Super Mario World, when the library has it) into <roms>\snes. Nothing is written into the repository.
// Groups and what each proves:
//   console  the Console menu lists WideMess; a .wms file (?rom=) starts the WideMess console with its own cores (WMS, and the SNES sound chip as the alternative).
//   picture  the 8-layer test ROM (layers.wms) arrives at 400 x 224 with more than 800 colours and moves; the page shows it at its square-pixel 16:9 shape (the canvas and the page's aspect are 400 / 224).
//   model    the milestone-2 picture ROMs (sprites.wms, widesnes.wms, windows.wms) are, pixel for pixel, the pictures the generator's independent model of the spec draws (<rom>.expect.png).
//   control  the negative control (layers_nounlock.wms: every WideMess register written without the unlock) stays a SNES picture: 256 x 224, black.
//   snes     with WideMess selected a SNES image (layers.sfc) runs on the WideMess and finds it (400 wide); switching to the SNES moves it there (256 wide, its red "not a WideMess" screen);
//            a SNES game (Super Mario World, else the SNES image) is frame for frame and sample for sample the same on the WideMess console as on the SNES console.
//   sound    the sound ROM (sound.wms) plays the WideMess voices: its sound is not the negative control's (sound_nounlock.wms: the same program locked, only the S-DSP's 8 voices), and louder.
//   saves    battery RAM: the 4 MB WideMess-map ROM (bigmap.wms, 256 KB battery RAM) keeps a boot counter there; it is in IndexedDB (battery_v1:wms<sha1>.srm) and counts up in a fresh page.
//   states   Save state / Load state on WideMess games (the same calls as the buttons): the next 60 frames are the same pictures and the same sound, also in a fresh page; the slot is state_v1:wms:<game id>.quick.
//   layout   the touch controller has the SNES pad on the WideMess (data-layout wms: X, Y, L and R shown).
//   enhance  WideMess milestone 6 in Lite: Super Mario World on the WideMess console finds its profile (built into the app), widescreen gives a 400-wide picture whose centre is, frame for frame from one
//            saved moment, the SNES picture; the choice is kept per game in IndexedDB (fast CPU never) and comes back in a fresh page; "Use the profile" sets exactly the profile's recommendations;
//            the controls: a .wms game has no enhancements (said, switches disabled), the SNES console has no panel.
//   speed    frames per second in the browser (the page's own frame call, FrameTick, in a JavaScript loop, as bench-lite.mjs does; then once more with the picture drawn to the canvas each frame):
//            layers, sprites, windows, widesnes and the sample game Wide Blaster (played with M5's input script) must hold 60 frames a second. LITELAB_GPU=1 draws on the machine's GPU (the
//            entrypoint sets it for this group); otherwise the drawn figures are SwiftShader's (the CPU draws) and are reported only.
// It cannot see: sound through a loudspeaker (samples handed to the page are measured), a real phone.
import fs from 'node:fs';
import path from 'node:path';
import zlib from 'node:zlib';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { startLab, args } from './litelab.mjs';

const a = args();
const only = a.only ? String(a.only).split(',') : null;
const want = g => !only || only.includes(g);
const sleep = ms => new Promise(r => setTimeout(r, ms));
const results = [];
function check(group, name, ok, detail = '') {
  results.push({ group, name, ok: !!ok, detail: String(detail) });
  console.log(`${ok ? 'PASS' : 'FAIL'} [${group}] ${name}${detail !== '' ? ' - ' + detail : ''}`);
  return !!ok;
}
const sha1 = bytes => crypto.createHash('sha1').update(bytes).digest('hex');
const here = path.dirname(fileURLToPath(import.meta.url));
const wmsBin = a.wms || path.resolve(here, '..', '..', 'WideMess', 'testroms', 'bin');
const speedFrames = parseInt(a.frames || '600', 10);

// ---- the games
fs.mkdirSync(path.join(a.roms, 'wms'), { recursive: true });
fs.mkdirSync(path.join(a.roms, 'snes'), { recursive: true });
for (const f of fs.readdirSync(wmsBin)) if (/\.(wms|sfc|png)$/i.test(f)) fs.copyFileSync(path.join(wmsBin, f), path.join(a.roms, 'wms', f));
const lib = a.library || 'X:\\EMULATION\\Super Nintendo\\Super Nintendo (U)';
const smwName = 'Super Mario World (U) [!].smc';
if (fs.existsSync(path.join(lib, smwName))) fs.copyFileSync(path.join(lib, smwName), path.join(a.roms, 'snes', 'smw.smc'));
const haveSmw = fs.existsSync(path.join(a.roms, 'snes', 'smw.smc'));

// ---- a PNG reader (the model's pictures: 8-bit RGB or RGBA, not interlaced) -> { w, h, rgb: Buffer of w*h*3 }
function readPng(file) {
  const b = fs.readFileSync(file);
  let o = 8, w = 0, h = 0, depth = 0, type = 0; const idat = [];
  while (o < b.length) {
    const len = b.readUInt32BE(o), kind = b.toString('ascii', o + 4, o + 8), d = b.subarray(o + 8, o + 8 + len);
    if (kind === 'IHDR') { w = d.readUInt32BE(0); h = d.readUInt32BE(4); depth = d[8]; type = d[9]; if (d[12] !== 0) throw new Error('interlaced PNG'); }
    else if (kind === 'IDAT') idat.push(d);
    o += 12 + len;
  }
  if (depth !== 8 || (type !== 2 && type !== 6)) throw new Error(`PNG type ${type} depth ${depth} not read here`);
  const bpp = type === 6 ? 4 : 3, stride = w * bpp, raw = zlib.inflateSync(Buffer.concat(idat)), px = Buffer.alloc(h * stride), rgb = Buffer.alloc(w * h * 3);
  for (let y = 0; y < h; y++) {
    const f = raw[y * (stride + 1)], src = raw.subarray(y * (stride + 1) + 1, (y + 1) * (stride + 1)), row = px.subarray(y * stride, (y + 1) * stride), up = y ? px.subarray((y - 1) * stride, y * stride) : null;
    for (let x = 0; x < stride; x++) {
      const l = x >= bpp ? row[x - bpp] : 0, u = up ? up[x] : 0, ul = up && x >= bpp ? up[x - bpp] : 0;
      let v = src[x];
      if (f === 1) v += l; else if (f === 2) v += u; else if (f === 3) v += (l + u) >> 1;
      else if (f === 4) { const p = l + u - ul, pa = Math.abs(p - l), pb = Math.abs(p - u), pc = Math.abs(p - ul); v += pa <= pb && pa <= pc ? l : pb <= pc ? u : ul; }
      row[x] = v & 255;
    }
  }
  for (let i = 0; i < w * h; i++) { rgb[i * 3] = px[i * bpp]; rgb[i * 3 + 1] = px[i * bpp + 1]; rgb[i * 3 + 2] = px[i * bpp + 2]; }
  return { w, h, rgb };
}

// ---- the page helpers: frames through FrameTick (the page's own loop stopped), with picture hashes, sizes, colours, sound
const HELPERS = `(() => {
window.__ref = window.__ref || nesInterop._dotNetRef;
window.__pad = s => { const st = new Array(12).fill(false); for (const i of (s || [])) st[i] = true; (nesInterop._mainRef || window.__ref).invokeMethodAsync('UpdateInput', st); };
window.__frames = async (n, pad, opt) => {
  opt = opt || {};
  const ref = window.__ref;
  if (nesInterop._loopActive) nesInterop.stopEmulationLoop();
  window.__pad(pad);
  let samples = 0, sum2 = 0, w = 0, h = 0, aw = 0, sr = 0, withAudio = 0; const hashes = [], audioHashes = []; let colours = 0, last = null;
  for (let i = 0; i < n; i++) {
    const p = await ref.invokeMethodAsync('FrameTick');
    if (!p) continue;
    const fb = p.fb;
    w = p.w || w; h = p.h || h; aw = p.aw || aw; sr = p.sr || sr;
    if (fb) { const u = new Uint32Array(fb.buffer, fb.byteOffset, fb.byteLength >> 2); let hh = 2166136261; for (let k = 0; k < u.length; k++) { hh ^= u[k]; hh = Math.imul(hh, 16777619); } hashes.push(hh >>> 0); last = fb; }
    if (p.pcm) {
      const f = new Float32Array(p.pcm.buffer.slice(p.pcm.byteOffset, p.pcm.byteOffset + p.pcm.byteLength));
      let ah = 2166136261; for (let k = 0; k < f.length; k++) { sum2 += f[k] * f[k]; const q = Math.round(f[k] * 32768); ah ^= q & 0xFFFF; ah = Math.imul(ah, 16777619); }
      samples += f.length; withAudio++; audioHashes.push(ah >>> 0);
    }
    if (opt.draw && fb) nesInterop.drawFrame('nes-canvas', fb, p.w, p.h, p.aw);
  }
  if (last && opt.colours) { const s = new Set(); const u = new Uint32Array(last.buffer, last.byteOffset, last.byteLength >> 2); for (let k = 0; k < u.length; k++) s.add(u[k]); colours = s.size; }
  window.__last = last;
  return { n, samples, rms: samples ? Math.sqrt(sum2 / samples) : 0, withAudio, w, h, aw, sr, hashes, audioHashes, colours };
};
/** The last frame against a model picture (base64 RGB): how many pixels differ, and whether the sizes match. */
window.__diff = (b64, w, h) => {
  const fb = window.__last; if (!fb) return { diff: -1, why: 'no frame' };
  if (fb.length !== w * h * 4) return { diff: -1, why: 'size ' + fb.length / 4 + ' pixels, the model ' + w * h };
  const bin = atob(b64); let d = 0;
  for (let i = 0; i < w * h; i++) if (fb[i * 4] !== bin.charCodeAt(i * 3) || fb[i * 4 + 1] !== bin.charCodeAt(i * 3 + 1) || fb[i * 4 + 2] !== bin.charCodeAt(i * 3 + 2)) d++;
  return { diff: d };
};
window.__allColour = rgb => { const fb = window.__last; if (!fb) return false; for (let i = 0; i < fb.length; i += 4) if (((fb[i] << 16) | (fb[i + 1] << 8) | fb[i + 2]) !== rgb) return false; return true; };
return 1; })()`;

const selectOption = (page, selector, value) => page.eval(`(() => { const s = document.querySelector(${JSON.stringify(selector)}); if (!s) return 'no select'; const o = [...s.options].find(x => x.value === ${JSON.stringify(value)}); if (!o) return 'no option ' + [...s.options].map(x => x.value).join(','); s.value = o.value; s.dispatchEvent(new Event('change', { bubbles: true })); return 'ok'; })()`);
const press = (page, which) => page.eval(`(async () => { const t0 = performance.now(); await nesInterop._mainRef.invokeMethodAsync('Js${which}State'); return [document.body.innerText, performance.now() - t0]; })()`);
const line = (text, re) => (text.match(new RegExp('[^\\n]*(?:' + re + ')[^\\n]*')) || [''])[0].trim();

async function withLab(fn, opts = {}) {
  const lab = await startLab({ publishDir: a.publish, romsDir: a.roms, headless: !a.show, ...opts });
  try { return await fn(lab); } finally { await lab.close(); }
}
async function openRom(lab, rom) {
  const page = await lab.open('/?rom=' + encodeURIComponent('roms/' + rom), { timeout: 240000 });
  await page.waitFor("document.body.innerText.includes('ROM: roms/')", 240000);
  await page.eval(HELPERS);
  return page;
}
async function console_(page) { return page.eval(`document.getElementById('consoleSelect')?.value`); }

// Lite's pad: A B Select Start Up Down Left Right X Y L R
const KEY = { A: 0, B: 1, Select: 2, Start: 3, Up: 4, Down: 5, Left: 6, Right: 7, X: 8, Y: 9, L: 10, R: 11 };

// ============================================================================================= console + picture + layout
if (want('console') || want('picture')) await withLab(async lab => {
  const page = await openRom(lab, 'wms/layers.wms');
  const opts = await page.eval(`[...document.getElementById('consoleSelect').options].map(o => o.value + '=' + o.text)`);
  check('console', 'the Console menu lists WideMess', opts.includes('wms=WideMess'), opts.join(', '));
  const cons = await console_(page);
  const cores = await page.eval(`[...document.querySelectorAll('.console-core-select')].map(s => s.getAttribute('aria-label') + '=' + s.value + ' [' + [...s.options].map(o => o.value).join(' ') + ']')`);
  check('console', 'a .wms file starts the WideMess console with its own cores (WMS; the SNES sound chip offered as the alternative)',
    cons === 'wms' && cores.length === 3 && cores.every(c => /=WMS \[/.test(c)) && /Apu core=WMS \[WMS SFC/.test(cores.join(' | ')), `console ${cons}; ${cores.join(' | ')}`.slice(0, 400));
  let r = null;
  for (let i = 0; i < 10 && !(r && r.colours > 800); i++) r = await page.eval(`window.__frames(60, null, { colours: true })`);   // (the desktop smoke waits the same way: the ROM fills its layers over the first frames)
  check('picture', 'the 8-layer test ROM arrives at 400 x 224, shown 400 wide, with more than 800 colours', r.w === 400 && r.h === 224 && r.aw === 400 && r.colours > 800, `${r.w} x ${r.h}, shown ${r.aw} wide, ${r.colours} colours`);
  check('picture', 'the layers scroll (the picture moves)', new Set(r.hashes.slice(-30)).size > 10, `${new Set(r.hashes.slice(-30)).size} distinct pictures in the last 30 frames`);
  // the page shows it at its own shape: square pixels, 400 / 224 (16:9 within half a percent)
  await page.eval(`window.__frames(1, null, { draw: true })`);
  const shape = await page.eval(`(() => { const c = document.getElementById('nes-canvas'); const r = c.getBoundingClientRect(); return { cw: c.width, ch: c.height, aspect: c.style.aspectRatio, root: getComputedStyle(document.documentElement).getPropertyValue('--screen-aspect').trim(), bw: r.width, bh: r.height }; })()`);
  const ratio = shape.bh > 0 ? shape.bw / shape.bh : 0;
  check('picture', 'the page shows the picture at its square-pixel 16:9 shape (canvas 800 x 448, aspect 400 / 224 on the canvas and the page)',
    shape.cw === 800 && shape.ch === 448 && /400\s*\/\s*224/.test(shape.aspect) && /400\s*\/\s*224/.test(shape.root) && Math.abs(ratio - 400 / 224) < 0.02,
    `canvas ${shape.cw} x ${shape.ch}, style aspect "${shape.aspect}", --screen-aspect "${shape.root}", on screen ${shape.bw.toFixed(0)} x ${shape.bh.toFixed(0)} (${ratio.toFixed(3)}, 400/224 = ${(400 / 224).toFixed(3)})`);
});
if (want('layout')) await withLab(async lab => {
  // the touch controller lives in the mobile fullscreen view (a phone's screen, touch emulated): choose WideMess there and read the buttons it shows
  const page = await lab.open('/', { timeout: 240000 });
  await page.waitFor("!!document.getElementById('consoleSelect')", 120000);
  await page.eval(`(() => { const b = document.querySelector('button[title*="ullscreen"], .fullscreen-btn, #fs-btn'); if (b) b.click(); })()`).catch(() => {});
  await selectOption(page, '#consoleSelect', 'wms');
  await sleep(800);
  const info = await page.eval(`(() => {
    const tc = document.getElementById('touch-controller'); if (!tc) return { layout: nesInterop._touchLayout, vis: [], none: true };
    const vis = [...tc.querySelectorAll('[data-btn]')].filter(e => e.getClientRects().length > 0 && getComputedStyle(e).visibility !== 'hidden' && getComputedStyle(e).display !== 'none').map(e => ({ btn: e.dataset.btn, label: e.innerText.trim(), r: e.getBoundingClientRect().toJSON() }));
    return { layout: tc.getAttribute('data-layout'), vis };
  })()`);
  check('layout', 'choosing WideMess sets the touch layout to wms', info.layout === 'wms', `layout ${info.layout}${info.none ? ' (the controller view is not open: the layout the page keeps for it)' : ''}`);
  if (!info.none) {
    const labels = info.vis.map(v => v.label).filter(x => x && !/^[▲▼◀▶]$/.test(x));
    check('layout', 'the WideMess pad is the SNES pad: A B X Y L R SELECT START', ['A', 'B', 'X', 'Y', 'L', 'R', 'SELECT', 'START'].every(l => labels.includes(l)), labels.join(' '));
    const boxes = info.vis.filter(v => v.r.width > 0); let overlap = '';
    for (let i = 0; i < boxes.length && !overlap; i++) for (let j = i + 1; j < boxes.length; j++) {
      const p = boxes[i].r, q = boxes[j].r, ix = Math.min(p.right, q.right) - Math.max(p.left, q.left), iy = Math.min(p.bottom, q.bottom) - Math.max(p.top, q.top);
      if (ix > 3 && iy > 3 && !(boxes[i].btn.match(/up|down|left|right/) && boxes[j].btn.match(/up|down|left|right/))) { overlap = `${boxes[i].label} overlaps ${boxes[j].label}`; break; }
    }
    const vw = await page.eval('window.innerWidth');
    check('layout', 'no two buttons overlap and all are inside the screen', !overlap && boxes.every(b => b.r.left >= -1 && b.r.right <= vw + 1), overlap || `${boxes.length} buttons inside ${vw}px`);
  }
}, { mobile: true });

// ============================================================================================= model pictures
if (want('model')) for (const rom of ['sprites', 'widesnes', 'windows']) await withLab(async lab => {
  const png = readPng(path.join(a.roms, 'wms', `${rom}.expect.png`));
  const page = await openRom(lab, `wms/${rom}.wms`);
  const b64 = png.rgb.toString('base64');
  let res = null, frames = 0;
  for (let i = 0; i < 12; i++) {
    await page.eval(`window.__frames(10)`); frames += 10;
    res = await page.eval(`window.__diff(${JSON.stringify(b64)}, ${png.w}, ${png.h})`);
    if (res.diff === 0) break;
  }
  check('model', `${rom}.wms is, pixel for pixel, the picture the spec's model draws (${rom}.expect.png)`, res && res.diff === 0, res.diff === 0 ? `${png.w} x ${png.h} equal after ${frames} frames` : res.why || `${res.diff} pixels differ after ${frames} frames`);
});

// ============================================================================================= negative control
if (want('control')) await withLab(async lab => {
  const page = await openRom(lab, 'wms/layers_nounlock.wms');
  const r = await page.eval(`window.__frames(90)`);
  const black = await page.eval(`window.__allColour(0)`);
  check('control', 'the negative control (every WideMess register written without the unlock) stays a SNES picture: 256 x 224, black', r.w === 256 && r.h === 224 && black && (await console_(page)) === 'wms', `${r.w} x ${r.h}, all black ${black}`);
});

// ============================================================================================= SNES games on the WideMess
if (want('snes')) {
  await withLab(async lab => {
    const page = await lab.open('/', { timeout: 240000 });
    await page.waitFor("!!document.getElementById('consoleSelect')", 120000);
    await selectOption(page, '#consoleSelect', 'wms');
    await sleep(800);
    // the SNES image of the 8-layer ROM, loaded while WideMess is selected (the choice is kept in IndexedDB: the page opened with ?rom= restores it before it loads the file)
    await lab.open('/?rom=' + encodeURIComponent('roms/wms/layers.sfc'), { timeout: 240000 });
    await page.waitFor("document.body.innerText.includes('ROM: roms/')", 240000);
    await page.eval(HELPERS);
    const cons = await console_(page);
    const r = await page.eval(`window.__frames(60)`);
    check('snes', 'with WideMess selected a SNES image runs on the WideMess and finds it (400 wide)', cons === 'wms' && r.w === 400, `console ${cons}, ${r.w} x ${r.h}`);
    await selectOption(page, '#consoleSelect', 'snes');
    await page.waitFor(`document.getElementById('consoleSelect')?.value === 'snes'`, 20000).catch(() => {});
    await sleep(800);
    await page.eval(HELPERS);
    const r2 = await page.eval(`window.__frames(60)`);
    const red = await page.eval(`window.__allColour(0xFF0000)`);
    check('snes', 'switching the console to the SNES moves it there: 256 wide, its red "not a WideMess" screen', (await console_(page)) === 'snes' && r2.w === 256 && red, `console ${await console_(page)}, ${r2.w} x ${r2.h}, all red ${red}`);
  });
  // the same SNES game on both consoles: every frame's picture and sound the same
  // (no input: the page's own loop runs a few frames before this test takes the frame calls over, a different number on each page, so the two runs are lined up by their pictures and then compared
  // frame for frame from there: the game runs from power-on without input, its title and its demo)
  const game = haveSmw ? ['Super Mario World', 'snes/smw.smc', 1200] : ['the SNES image of the layers ROM', 'wms/layers.sfc', 400];
  const runOn = async consoleKey => withLab(async lab => {
    const page = await lab.open('/', { timeout: 240000 });
    await page.waitFor("!!document.getElementById('consoleSelect')", 120000);
    await selectOption(page, '#consoleSelect', consoleKey);
    await sleep(800);
    const p2 = await lab.open('/?rom=' + encodeURIComponent('roms/' + game[1]), { timeout: 240000 });
    await p2.waitFor("document.body.innerText.includes('ROM: roms/')", 240000);
    await p2.eval(HELPERS);
    const cons = await console_(p2);
    const r = await p2.eval(`window.__frames(${game[2]})`, { timeout: 600000 });
    return { cons, hashes: r.hashes, audio: r.audioHashes, w: r.w };
  });
  const onSnes = await runOn('snes'), onWms = await runOn('wms');
  // the offset k (WideMess frame i = SNES frame i + k) where the pictures agree best
  let best = { k: 0, eq: -1, n: 0 };
  for (let k = -240; k <= 240; k++) {
    let eq = 0, n = 0;
    for (let i = Math.max(0, -k); i < onWms.hashes.length && i + k < onSnes.hashes.length; i++) { n++; if (onWms.hashes[i] === onSnes.hashes[i + k]) eq++; }
    if (eq > best.eq) best = { k, eq, n };
  }
  let sndEq = 0, sndN = 0;
  for (let i = Math.max(0, -best.k); i < onWms.audio.length && i + best.k < onSnes.audio.length; i++) { sndN++; if (onWms.audio[i] === onSnes.audio[i + best.k]) sndEq++; }
  const distinct = new Set(onSnes.hashes).size;
  check('snes', `${game[0]} is the same game on the WideMess as on the SNES: every frame's picture and sound (lined up from power-on)`,
    onSnes.cons === 'snes' && onWms.cons === 'wms' && best.eq === best.n && best.n >= game[2] - 250 && sndEq === sndN && sndN >= game[2] - 250 && distinct > 20,
    `consoles ${onSnes.cons} / ${onWms.cons}; offset ${best.k} frames; ${best.eq} of ${best.n} pictures equal (${distinct} distinct), ${sndEq} of ${sndN} frames of sound equal; ${onWms.w} wide on the WideMess (locked: a SNES)`);
}

// ============================================================================================= sound
if (want('sound')) {
  const play = rom => withLab(async lab => { const page = await openRom(lab, rom); return page.eval(`(async () => { await window.__frames(60); return window.__frames(360); })()`); });
  const on = await play('wms/sound.wms'), off = await play('wms/sound_nounlock.wms');
  const differ = on.audioHashes.some((h, i) => h !== off.audioHashes[i]);
  check('sound', 'the sound ROM plays the WideMess voices: its sound differs from the locked control and is louder (the control has only the S-DSP\'s 8 voices)',
    on.withAudio >= 355 && on.sr === 32000 && differ && on.rms > off.rms * 1.5 && on.rms > 0.01, `unlocked rms ${on.rms.toFixed(4)} (${on.samples} samples at ${on.sr} Hz in ${on.withAudio} frames), locked control rms ${off.rms.toFixed(4)}, differ ${differ}`);
}

// ============================================================================================= battery saves
if (want('saves')) {
  const rom = 'wms/bigmap.wms';
  const key = `battery_v1:wms${sha1(fs.readFileSync(path.join(a.roms, rom)))}.srm`;
  const read = page => page.eval(`(async () => { const b = await nesInterop.idbGetBytes(${JSON.stringify(key)}); return b ? { len: b.length, magic: String.fromCharCode(b[0], b[1], b[2], b[3]), count: b[4] } : null; })()`);
  await withLab(async lab => {
    const page = await openRom(lab, rom);
    await page.eval(`window.__frames(120)`);
    await selectOption(page, '#consoleSelect', 'nes');           // leaving the game commits its save
    await sleep(1500);
    const first = await read(page);
    check('saves', 'the WideMess-map game keeps its 256 KB battery RAM in IndexedDB (battery_v1:wms<sha1>.srm) with its boot counter', first && first.len === 262144 && first.magic === 'WMSV' && first.count >= 1, first ? `${first.len} bytes, "${first.magic}", count ${first.count}` : 'nothing stored');
    const page2 = await openRom(lab, rom);
    await page2.eval(`window.__frames(120)`);
    await selectOption(page2, '#consoleSelect', 'nes');
    await sleep(1500);
    const second = await read(page2);
    check('saves', 'the save comes back in a fresh page: the game counts one more boot', first && second && second.count === first.count + 1, `${first?.count} then ${second?.count}`);
  });
}

// ============================================================================================= save states
if (want('states')) for (const [rom, hold] of [['wms/layers.wms', []], ['wms/devkit_sample.wms', [KEY.Right, KEY.B]], ['wms/sound.wms', []]]) await withLab(async lab => {
  const id = 'wms' + sha1(fs.readFileSync(path.join(a.roms, rom)));
  const slot = `state_v1:wms:${id}.quick`;
  const page = await openRom(lab, rom);
  await page.eval(`window.__frames(150, ${JSON.stringify(hold)})`);
  const [saved, saveMs] = await press(page, 'Save');
  const stored = await page.eval(`(async () => { const b = await nesInterop.idbGetBytes(${JSON.stringify(slot)}); return b ? b.length : 0; })()`);
  check('states', `${rom}: Save state stores the machine under state_v1:wms:<game id>.quick`, /State saved/.test(saved) && stored > 10000, `${line(saved, 'State saved|Save error')}; ${stored} bytes; ${saveMs.toFixed(0)} ms`);
  const before = await page.eval(`window.__frames(60, ${JSON.stringify(hold)})`);
  const [loaded, loadMs] = await press(page, 'Load');
  await sleep(300);
  const after = await page.eval(`window.__frames(60, ${JSON.stringify(hold)})`);
  const samePic = before.hashes.length === 60 && before.hashes.every((h, i) => h === after.hashes[i]);
  const sameSnd = before.audioHashes.length >= 55 && before.audioHashes.every((h, i) => h === after.audioHashes[i]);
  check('states', `${rom}: after Load state the next 60 frames are the same pictures and the same sound (the state is the whole machine: 1 MB work RAM, the sound RAM, voices 8-31)`,
    /State loaded/.test(loaded) && samePic && sameSnd && (new Set(before.hashes).size > 1 || new Set(before.audioHashes).size > 30), `${before.hashes.filter((h, i) => h === after.hashes[i]).length}/60 pictures, ${before.audioHashes.filter((h, i) => h === after.audioHashes[i]).length}/${before.audioHashes.length} sound frames equal; ${line(loaded, 'State loaded|Load error')}; ${loadMs.toFixed(0)} ms`);
  const page2 = await openRom(lab, rom);
  await page2.eval(`window.__frames(20)`);
  const [loaded2] = await press(page2, 'Load');
  await sleep(300);
  const fresh = await page2.eval(`window.__frames(60, ${JSON.stringify(hold)})`);
  check('states', `${rom}: the state kept in the browser loads into a fresh page and the next 60 frames are the same pictures`, /State loaded/.test(loaded2) && before.hashes.every((h, i) => h === fresh.hashes[i]), `${before.hashes.filter((h, i) => h === fresh.hashes[i]).length}/60 equal`);
});

// ============================================================================================= the milestone-6 enhancements (a SNES game on the WideMess console)
if (want('enhance')) {
  if (!haveSmw) check('enhance', 'Super Mario World is needed for the enhancements group (its profile is in WideMess/profiles)', false, 'not found under ' + lib);
  else {
    const smwBytes = fs.readFileSync(path.join(a.roms, 'snes', 'smw.smc'));
    const imageSha = sha1(smwBytes.length % 1024 === 512 ? smwBytes.subarray(512) : smwBytes);
    const keptKey = `pref_wmsenh_${imageSha}`;
    const panel = page => page.eval(`(() => { const p = document.getElementById('wms-enhance'); if (!p) return null; return { status: document.getElementById('wms-enh-status')?.innerText || '', boxes: [0,1,2,3].map(i => { const b = document.getElementById('wms-enh-' + i); return b ? { on: b.checked, off: b.disabled, why: b.parentElement.title } : null; }), profileButton: document.getElementById('wms-use-profile')?.innerText || '' }; })()`);
    const click = (page, id) => page.eval(`(() => { const b = document.getElementById(${JSON.stringify(id)}); if (!b) return false; b.click(); return true; })()`);
    // the centre of a 400-wide frame (columns 72-327) hashed the way a 256-wide frame is hashed whole: equal hashes = the same SNES picture
    const CENTRE = `window.__centre = async (n, wide) => {
      const ref = window.__ref; if (nesInterop._loopActive) nesInterop.stopEmulationLoop();
      const out = []; let w = 0;
      for (let i = 0; i < n; i++) {
        const p = await ref.invokeMethodAsync('FrameTick'); if (!p || !p.fb) continue;
        w = p.w; const u = new Uint32Array(p.fb.buffer, p.fb.byteOffset, p.fb.byteLength >> 2); let hh = 2166136261;
        const x0 = wide ? 72 : 0, cols = 256;
        for (let y = 0; y < p.h; y++) for (let x = 0; x < cols; x++) { hh ^= u[y * p.w + x0 + x]; hh = Math.imul(hh, 16777619); }
        out.push(hh >>> 0);
      }
      return { w, hashes: out };
    }; 1`;
    await withLab(async lab => {
      const page = await lab.open('/', { timeout: 240000 });
      await page.waitFor("!!document.getElementById('consoleSelect')", 120000);
      await selectOption(page, '#consoleSelect', 'wms');
      await sleep(800);
      // the control: a WideMess game has no enhancements
      await lab.open('/?rom=' + encodeURIComponent('roms/wms/layers.wms'), { timeout: 240000 });
      await page.waitFor("document.body.innerText.includes('ROM: roms/')", 240000);
      await page.waitFor("!!document.getElementById('wms-enhance')", 30000).catch(() => {});
      const ctl = await panel(page);
      check('enhance', 'the control: a WideMess game has no enhancements (the panel says so and its switches are off and disabled)', ctl && /a WideMess game has no enhancements/.test(ctl.status) && ctl.boxes.every(b => b && !b.on && b.off), JSON.stringify(ctl).slice(0, 300));
      // Super Mario World on the WideMess console: its profile is found
      await lab.open('/?rom=' + encodeURIComponent('roms/snes/smw.smc'), { timeout: 240000 });
      await page.waitFor("document.body.innerText.includes('ROM: roms/')", 240000);
      await page.eval(HELPERS); await page.eval(CENTRE);
      await page.waitFor("!!document.getElementById('wms-enhance')", 30000).catch(() => {});
      const p0 = await panel(page);
      check('enhance', 'a SNES game on the WideMess console finds its profile (the built-in profiles in the browser\'s file system)', p0 && /profile: Super Mario World/.test(p0.status) && p0.boxes.every(b => b && !b.on && !b.off), p0 ? p0.status : 'no panel');
      // widescreen: from one saved moment, the SNES picture's 30 frames, then the same 30 frames with widescreen on: the centre of each 400-wide frame is that SNES frame
      await page.eval(`window.__frames(360)`);
      const [saved] = await press(page, 'Save');
      const plain = await page.eval(`window.__centre(30, false)`);
      const [loaded] = await press(page, 'Load');
      await sleep(300);
      await click(page, 'wms-enh-0');
      await sleep(500);
      const wide = await page.eval(`window.__centre(30, true)`);
      const same = plain.hashes.length === 30 && plain.hashes.every((h, i) => h === wide.hashes[i]);
      check('enhance', 'widescreen: the picture is 400 wide and its centre (columns 72-327) is the SNES picture, frame for frame from the same saved moment', /State saved/.test(saved) && /State loaded/.test(loaded) && plain.w === 256 && wide.w === 400 && same,
        `${plain.w} -> ${wide.w} wide; ${plain.hashes.filter((h, i) => h === wide.hashes[i]).length}/30 centres equal`);
      // kept per game: in IndexedDB, and on again in a fresh page; fast CPU is never kept
      await click(page, 'wms-enh-3');
      await sleep(400);
      const kept = await page.eval(`nesInterop.idbGetItem(${JSON.stringify(keptKey)})`);
      const p1 = await panel(page);
      check('enhance', 'the choice is kept for the game (IndexedDB pref_wmsenh_<image SHA-1>), without fast CPU (never kept: spec 6.3)', kept === 'w' && p1.boxes[0].on && p1.boxes[3].on, `kept "${kept}", widescreen ${p1.boxes[0].on}, fast CPU ${p1.boxes[3].on} (on now, not kept)`);
      await lab.open('/?rom=' + encodeURIComponent('roms/snes/smw.smc'), { timeout: 240000 });
      await page.waitFor("document.body.innerText.includes('ROM: roms/')", 240000);
      await page.eval(HELPERS);
      const again = await page.eval(`window.__frames(60)`);
      const p2 = await panel(page);
      check('enhance', 'a fresh page of the game switches the kept widescreen on again (and fast CPU stays off)', again.w === 400 && p2 && p2.boxes[0].on && !p2.boxes[3].on, `${again.w} wide; ${JSON.stringify(p2?.boxes.map(b => b.on))}`);
      // Use the profile: what the profile recommends, nothing else; again: all off, back to the SNES picture
      await click(page, 'wms-use-profile');
      await sleep(400);
      const p3 = await panel(page);
      await click(page, 'wms-use-profile');
      await sleep(400);
      const off = await page.eval(`window.__frames(30)`);
      const p4 = await panel(page);
      check('enhance', '"Use the profile" switches on what the profile recommends (Super Mario World\'s: widescreen and no flicker, nothing else); pressed again, everything is off and the picture is the SNES\'s 256', p3 && /Profile on/i.test(p3.profileButton) && p3.boxes[0].on && p3.boxes[1].on && !p3.boxes[2].on && !p3.boxes[3].on && p4 && p4.boxes.every(b => !b.on) && off.w === 256,
        `after: ${JSON.stringify(p3?.boxes.map(b => b.on))} "${p3?.profileButton}"; again: ${JSON.stringify(p4?.boxes.map(b => b.on))}, ${off.w} wide`);
    });
    // the SNES console has no enhancements panel
    await withLab(async lab => {
      const page = await lab.open('/?rom=' + encodeURIComponent('roms/snes/smw.smc'), { timeout: 240000 });
      await page.waitFor("document.body.innerText.includes('ROM: roms/')", 240000);
      const has = await page.eval(`!!document.getElementById('wms-enhance')`);
      check('enhance', 'the control: on the SNES console there is no WideMess enhancements panel', !has && (await console_(page)) === 'snes', `panel ${has}, console ${await console_(page)}`);
    });
  }
}

// ============================================================================================= speed
const speed = [];
if (want('speed')) {
  const play = '30:Right+B,200:Up+B,260:Down+Y,400:B,700:Left+Y';        // Workshop --wms-test M5.3's script for Wide Blaster
  const script = {}; for (const part of play.split(',')) { const [f, b] = part.split(':'); script[f] = b.split('+').filter(Boolean).map(n => KEY[n]); }
  const cases = [['layers', 'wms/layers.wms', null], ['sprites', 'wms/sprites.wms', null], ['windows', 'wms/windows.wms', null], ['widesnes', 'wms/widesnes.wms', null], ['Wide Blaster', 'wms/devkit_sample.wms', script]];
  if (haveSmw) cases.push(['SMW (SNES, reference)', 'snes/smw.smc', null]);
  for (const [name, rom, sc] of cases) await withLab(async lab => {
    const page = await openRom(lab, rom);
    const r = await page.eval(`(async () => {
      const ref = window.__ref; if (nesInterop._loopActive) nesInterop.stopEmulationLoop();
      const script = ${JSON.stringify(sc || {})}; let frame = 0;
      const step = () => { const b = script[frame]; if (b) window.__pad(b); frame++; };
      for (let i = 0; i < 200; i++) { step(); await ref.invokeMethodAsync('FrameTick'); }      // warm-up (and the game's loading frames)
      const run = async (n, draw) => {
        const t = []; let w = 0, h = 0;
        for (let i = 0; i < n; i++) { step(); const t0 = performance.now(); const p = await ref.invokeMethodAsync('FrameTick'); if (draw && p && p.fb) nesInterop.drawFrame('nes-canvas', p.fb, p.w, p.h, p.aw); t.push(performance.now() - t0); if (p) { w = p.w || w; h = p.h || h; } }
        t.sort((x, y) => x - y); const q = f => t[Math.min(t.length - 1, Math.floor(t.length * f))];
        return { mean: t.reduce((x, y) => x + y, 0) / t.length, median: q(0.5), p95: q(0.95), max: t[t.length - 1], w, h };
      };
      try { await ref.invokeMethodAsync('ResetSessionProfile'); } catch {}
      const core = await run(${speedFrames}, false);
      const prof = await ref.invokeMethodAsync('GetSessionProfile');
      const drawn = await run(${Math.max(120, speedFrames >> 1)}, true);
      let renderer = '2D canvas';
      try { const gl = nesInterop._gl; if (gl) { const d = gl.getExtension('WEBGL_debug_renderer_info'); renderer = d ? gl.getParameter(d.UNMASKED_RENDERER_WEBGL) : gl.getParameter(gl.RENDERER); } } catch {}
      return { core, drawn, prof, renderer };
    })()`, { timeout: 900000 });
    if (!speed.renderer) { speed.renderer = r.renderer; console.log(`  (the picture is drawn by: ${r.renderer})`); }
    const [pf, run, aud, pix] = r.prof;
    const row = { case: name, size: `${r.core.w}x${r.core.h}`, fps: +(1000 / r.core.mean).toFixed(1), p95Ms: +r.core.p95.toFixed(2), maxMs: +r.core.max.toFixed(2), fpsDrawn: +(1000 / r.drawn.mean).toFixed(1), p95DrawnMs: +r.drawn.p95.toFixed(2),
                  coreMs: +(run / pf).toFixed(2), audioMs: +(aud / pf).toFixed(2), pixelMs: +(pix / pf).toFixed(2), errors: page.consoleErrors().length };
    speed.push(row);
    console.log(`  ${name.padEnd(22)} ${row.size.padEnd(8)} ${String(row.fps).padStart(6)} fps (p95 ${row.p95Ms} ms, max ${row.maxMs} ms)  drawn: ${String(row.fpsDrawn).padStart(6)} fps (p95 ${row.p95DrawnMs} ms) | core ${row.coreMs} audio ${row.audioMs} pixels ${row.pixelMs} ms`);
    // the bar: the frame call (emulation, sound, pixels, the crossing to JavaScript) at 60 frames a second or more with its 95th percentile inside a 60 Hz frame; with the picture drawn too, the
    // same, when the browser draws on a GPU. SwiftShader (the labs' default software renderer: the CPU draws the 800 x 448 canvas) is reported, not required: no phone or desktop browser draws that way.
    const software = /swiftshader/i.test(speed.renderer || '');
    if (!name.includes('reference')) check('speed', `${name}: holds 60 frames a second in the browser (the frame call${software ? '; drawn on the software renderer: reported only' : ', and with the picture drawn on the GPU'})`,
      row.fps >= 60 && row.p95Ms < 1000 / 60 && (software || (row.fpsDrawn >= 60 && row.p95DrawnMs < 1000 / 60)) && row.errors === 0,
      `${row.fps} fps (p95 ${row.p95Ms} ms), drawn ${row.fpsDrawn} fps (p95 ${row.p95DrawnMs} ms), ${row.size}; ${speed.renderer}`);
  });
}

const failed = results.filter(r => !r.ok).length;
console.log(`\n${results.length - failed} passed, ${failed} failed`);
if (a.json) fs.writeFileSync(a.json, JSON.stringify({ results, speed }, null, 2));
process.exit(failed === 0 && results.length > 0 ? 0 : 1);
