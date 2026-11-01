// test-lite-snes-state.mjs - SNES save states in BrokenNes Lite (the Blazor WebAssembly AOT build), in a real Edge/Chrome over the DevTools protocol.
//
//   node test-lite-snes-state.mjs --publish <AOT publish dir> --roms <scratch dir> [--library <SNES library dir>] [--json out.json] [--show]
//
// The page's Save state / Load state buttons are JsSaveState / JsLoadState (the same calls the buttons make); the state goes to IndexedDB under state_v1:snes:<game id>.quick.
// Games: a 32 KB program of ours that counts frames while Right is held (written to <roms>\snes\counter.sfc here, the bytes of Workshop/SnesProgs.cs, no game data) and, when the library has it,
// Super Mario World. What it checks: Save state stores a state (and says its size), after Load state the next 60 frames are the same pictures as the first time, the sound goes on, the state kept in the
// browser loads into a fresh page of the same game (IndexedDB, not the page), a state saved with the real sound unit is refused after the sound unit is changed (with the reason, and the game keeps running),
// another game's state under this game's slot is refused with the reason, Load state with nothing saved says so, and how long a save and a load take in WebAssembly.
// It cannot see sound through a loudspeaker (samples handed to the page are counted), nor a phone.
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { startLab, args } from './litelab.mjs';

const a = args();
const sleep = ms => new Promise(r => setTimeout(r, ms));
const results = [];
function check(name, ok, detail = '') {
  results.push({ group: 'snes-states', name, ok: !!ok, detail: String(detail) });
  console.log(`${ok ? 'PASS' : 'FAIL'} [snes-states] ${name}${detail !== '' ? ' - ' + detail : ''}`);
  return !!ok;
}
const sha1 = bytes => crypto.createHash('sha1').update(bytes).digest('hex');

// the counter program (Workshop/SnesProgs.cs; UAT/entrypoints/snes-state-smoke.ps1 carries the same hex)
const COUNTER_HEX = '78A9018D0042A90F8D0021A90085108511AD124230FBAD124210FBAD12422901D0F9AD19422901F006E610D002E611A9008D2121A5108D2221A511297F8D22214C1180';
function counterRom() {
  const rom = Buffer.alloc(0x8000); Buffer.from(COUNTER_HEX, 'hex').copy(rom, 0);
  const h = 0x7FC0;
  Buffer.from('BROKENNES SNES SMOKE ', 'ascii').copy(rom, h);
  rom[h + 0x15] = 0x20; rom[h + 0x17] = 0x05; rom[h + 0x19] = 0x01; rom[h + 0x1A] = 0x33;
  rom[h + 0x3C] = 0x00; rom[h + 0x3D] = 0x80; rom[h + 0x2C] = 0x00; rom[h + 0x2D] = 0x80;
  rom[h + 0x1C] = 0xFF; rom[h + 0x1D] = 0xFF; rom[h + 0x1E] = 0; rom[h + 0x1F] = 0;
  let sum = 0; for (const b of rom) sum += b;
  const cs = sum & 0xFFFF, ncs = ~cs & 0xFFFF;
  rom[h + 0x1E] = cs & 0xFF; rom[h + 0x1F] = cs >> 8; rom[h + 0x1C] = ncs & 0xFF; rom[h + 0x1D] = ncs >> 8;
  return rom;
}

const FRAMES = `window.__frames = async (n, pad) => {
  const ref = window.__ref || (window.__ref = nesInterop._dotNetRef);
  if (nesInterop._loopActive) nesInterop.stopEmulationLoop();
  const main = nesInterop._mainRef || ref;
  if (pad) { const st = new Array(12).fill(false); for (const i of pad) st[i] = true; main.invokeMethodAsync('UpdateInput', st); }
  else { main.invokeMethodAsync('UpdateInput', new Array(12).fill(false)); }
  let samples = 0, w = 0, h = 0, hashes = [], withAudio = 0;
  for (let i = 0; i < n; i++) {
    const p = await ref.invokeMethodAsync('FrameTick');
    if (!p) continue;
    const fb = p.fb || p.Framebuffer;
    if (fb) { const u = new Uint32Array(fb.buffer, fb.byteOffset, fb.byteLength >> 2); let hh = 2166136261; for (let k = 0; k < u.length; k++) { hh ^= u[k]; hh = Math.imul(hh, 16777619); } hashes.push(hh >>> 0); }
    const au = p.pcm ? p.pcm.byteLength >> 2 : (p.audio ? p.audio.length : 0);
    if (au) withAudio++; samples += au; w = p.w || w; h = p.h || h;
  }
  return { samples, w, h, hashes, withAudio, frames: n };
};
window.__resume = () => { try { nesInterop.startEmulationLoop(window.__ref); } catch {} };
1`;

async function withLab(fn) {
  const lab = await startLab({ publishDir: a.publish, romsDir: a.roms, headless: !a.show });
  try { return await fn(lab); } finally { await lab.close(); }
}
async function openRom(lab, rom) {
  const page = await lab.open('/?rom=' + encodeURIComponent('roms/' + rom), { timeout: 240000 });
  await page.waitFor("document.body.innerText.includes('ROM: roms/')", 240000);
  await page.eval(FRAMES);
  return page;
}
const selectOption = (page, selector, value) => page.eval(`(() => { const s = document.querySelector(${JSON.stringify(selector)}); if (!s) return 'no select'; const o = [...s.options].find(x => x.value === ${JSON.stringify(value)}); if (!o) return 'no option'; s.value = o.value; s.dispatchEvent(new Event('change', { bubbles: true })); return 'ok'; })()`);
/** Calls the page's save / load and returns [the page text afterwards, milliseconds]. */
const press = (page, which) => page.eval(`(async () => { const t0 = performance.now(); await nesInterop._mainRef.invokeMethodAsync('Js${which}State'); const ms = performance.now() - t0; return [document.body.innerText, ms]; })()`);
const line = (text, re) => (text.match(new RegExp('[^\\n]*(?:' + re + ')[^\\n]*')) || [''])[0].trim();

fs.mkdirSync(path.join(a.roms, 'snes'), { recursive: true });
fs.writeFileSync(path.join(a.roms, 'snes', 'counter.sfc'), counterRom());
const lib = a.library || 'X:\\EMULATION\\Super Nintendo\\Super Nintendo (U)';
const smwName = 'Super Mario World (U) [!].smc';
if (fs.existsSync(path.join(lib, smwName))) fs.copyFileSync(path.join(lib, smwName), path.join(a.roms, 'snes', 'smw.smc'));
// Lite's pad array is A B Select Start Up Down Left Right (then X Y L R): Right = 7, Start = 3
const games = [['counter program', 'snes/counter.sfc', [7]]];
if (fs.existsSync(path.join(a.roms, 'snes', 'smw.smc'))) games.push(['Super Mario World', 'snes/smw.smc', [3]]);
else console.log('SKIP Super Mario World: not found under ' + lib);

for (const [name, rom, hold] of games) {
  const slotKey = `state_v1:snes:sfc${sha1(fs.readFileSync(path.join(a.roms, rom)))}.quick`;
  await withLab(async lab => {
    const page = await openRom(lab, rom);
    // the console selected is the SNES, with its own cores
    const consoleSel = await page.eval(`document.getElementById('consoleSelect')?.value`);
    check(`${name}: the page runs it as a SNES game`, consoleSel === 'snes', `console=${consoleSel}`);
    await page.eval(`window.__frames(${name === 'Super Mario World' ? 520 : 40})`);
    await page.eval(`window.__frames(${name === 'Super Mario World' ? 12 : 30}, [${hold.join(',')}])`);   // Right held (the counter) / Start pressed (the title)
    await page.eval(`window.__frames(20)`);
    const [saved, saveMs] = await press(page, 'Save');
    const ok = /State saved/.test(saved);
    check(`${name}: Save state stores a state and says its size`, ok, `${line(saved, 'State saved')}; the call took ${saveMs.toFixed(0)} ms in WebAssembly`);
    if (!ok) return;
    const stored = await page.eval(`(async () => { const b = await nesInterop.idbGetBytes(${JSON.stringify(slotKey)}); return b ? b.length : 0; })()`);
    check(`${name}: the state is in IndexedDB under state_v1:snes:<game id>.quick`, stored > 2000, `${stored} bytes`);
    const timeline = await page.eval(`window.__frames(60, [${hold.join(',')}])`);
    const [loaded, loadMs] = await press(page, 'Load');
    await sleep(300);
    const again = await page.eval(`window.__frames(60, [${hold.join(',')}])`);
    const same = timeline.hashes.length === again.hashes.length && timeline.hashes.length === 60 && timeline.hashes.every((h, i) => h === again.hashes[i]);
    check(`${name}: after Load state the next 60 frames are the same pictures as the first time (the state is the whole machine)`, same && /State loaded/.test(loaded), `${timeline.hashes.filter((h, i) => h === again.hashes[i]).length}/60 equal; ${line(loaded, 'State loaded|Load error')}; the call took ${loadMs.toFixed(0)} ms`);
    check(`${name}: the picture moves (the game really runs between save and load)`, new Set(timeline.hashes).size > 1, `${new Set(timeline.hashes).size} distinct pictures in 60 frames`);
    check(`${name}: sound goes on after a load`, again.withAudio >= 55, `${again.withAudio}/60 frames with samples`);
    // a fresh page of the same game: the state is in the browser's store
    const page2 = await openRom(lab, rom);
    await page2.eval(`window.__frames(30)`);
    const [loaded2] = await press(page2, 'Load');
    await sleep(300);
    const fresh = await page2.eval(`window.__frames(60, [${hold.join(',')}])`);
    const sameFresh = timeline.hashes.length === fresh.hashes.length && timeline.hashes.every((h, i) => h === fresh.hashes[i]);
    check(`${name}: a state kept in the browser loads into a fresh page of the same game and the next 60 frames are the same pictures`, sameFresh && /State loaded/.test(loaded2), `${timeline.hashes.filter((h, i) => h === fresh.hashes[i]).length}/60 equal; ${line(loaded2, 'State loaded|Load error')}`);
    // another sound unit: refused, with the reason, and the game goes on
    const set = await selectOption(page2, 'select[aria-label="Apu core"]', 'NES:FIX');
    await sleep(2500);
    const [msg] = await press(page2, 'Load');
    await sleep(300);
    const run = await page2.eval(`window.__frames(30, [${hold.join(',')}])`);
    check(`${name}: a state saved with the real sound unit is refused after the sound unit was changed, with the reason, and the game keeps running`, set === 'ok' && /saved with the SFC sound unit, but the game is running the NES:FIX sound unit/.test(msg) && run.hashes.length === 30 && new Set(run.hashes).size > 1, `${line(msg, 'sound unit')}`.slice(0, 200));
  });
}

// the counter program's state under Super Mario World's slot: another game
if (games.length > 1) {
  const counterKey = `state_v1:snes:sfc${sha1(fs.readFileSync(path.join(a.roms, 'snes/counter.sfc')))}.quick`;
  const smwKey = `state_v1:snes:sfc${sha1(fs.readFileSync(path.join(a.roms, 'snes/smw.smc')))}.quick`;
  await withLab(async lab => {
    const page = await openRom(lab, 'snes/counter.sfc');
    await page.eval(`window.__frames(60, [7])`);
    await press(page, 'Save');
    await page.eval(`(async () => { const b = await nesInterop.idbGetBytes(${JSON.stringify(counterKey)}); await nesInterop.idbSetBytes(${JSON.stringify(smwKey)}, b); })()`);
    const page2 = await openRom(lab, 'snes/smw.smc');
    await page2.eval(`window.__frames(60)`);
    const [msg] = await press(page2, 'Load');
    await sleep(300);
    const run = await page2.eval(`window.__frames(30)`);
    check("another game's state is refused with the reason and the game keeps running", /This state belongs to .*not to/.test(msg) && run.withAudio >= 25, line(msg, 'This state belongs').slice(0, 200));
  });
}
await withLab(async lab => {
  const page = await openRom(lab, 'snes/counter.sfc');
  await page.eval(`window.__frames(30)`);
  await page.eval(`(async () => { }) ()`);
  const [msg] = await press(page, 'Load');
  // (the previous runs stored a state for this game in other labs' profiles; each lab has a fresh profile, so nothing is stored here)
  check('Load state with nothing saved says so', /No saved state for this game/.test(msg), line(msg, 'saved state'));
});

const failed = results.filter(r => !r.ok);
console.log(`\n${results.length - failed.length} passed, ${failed.length} failed`);
if (a.json) fs.writeFileSync(a.json, JSON.stringify(results, null, 2));
process.exit(failed.length ? 1 : 0);
