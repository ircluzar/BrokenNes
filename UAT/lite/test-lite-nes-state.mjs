// test-lite-nes-state.mjs - NES save states in BrokenNes Lite (the Blazor WebAssembly AOT build) continue EXACTLY: after Load state the next
// frames are the same pictures AND the same sound samples as the frames that followed Save state, on the stock FIX cores and on foreign APUs.
//
//   node test-lite-nes-state.mjs --publish <AOT publish dir> --roms <scratch dir> --nes <a .nes with music> [--apus FIX,INES,...] [--json out.json] [--show]
//
// The page's Save state / Load state buttons are JsSaveState / JsLoadState (the same calls the buttons make). Frames are driven with FrameTick (no rAF),
// so the run is the same frame by frame. Negative control: the frames after the load are also compared with frames from a different moment (they must differ).
import fs from 'node:fs';
import path from 'node:path';
import { startLab, args, pickCore } from './litelab.mjs';

const a = args();
const sleep = ms => new Promise(r => setTimeout(r, ms));
const results = [];
function check(name, ok, detail = '') {
  results.push({ group: 'nes-states', name, ok: !!ok, detail: String(detail) });
  console.log(`${ok ? 'PASS' : 'FAIL'} [nes-states] ${name}${detail !== '' ? ' - ' + detail : ''}`);
  return !!ok;
}

const FRAMES = `window.__frames = async (n, pad) => {
  const ref = window.__ref || (window.__ref = nesInterop._dotNetRef);
  if (nesInterop._loopActive) nesInterop.stopEmulationLoop(); if (!window.__noRaf) { window.__noRaf = window.requestAnimationFrame; window.requestAnimationFrame = () => 0; }
  const main = nesInterop._mainRef || ref;
  const st = new Array(12).fill(false); if (pad) for (const i of pad) st[i] = true; await main.invokeMethodAsync('UpdateInput', st);
  const fnv = (u, hh) => { for (let k = 0; k < u.length; k++) { hh ^= u[k]; hh = Math.imul(hh, 16777619); } return hh >>> 0; };
  let hashes = [], audio = [], samples = 0;
  for (let i = 0; i < n; i++) {
    const p = await ref.invokeMethodAsync('FrameTick');
    if (!p) { hashes.push(0); audio.push(0); continue; }
    const fb = p.fb || p.framebuffer || p.Framebuffer;
    hashes.push(fb ? fnv(new Uint32Array(fb.buffer, fb.byteOffset, fb.byteLength >> 2), 2166136261) : 0);
    const au = p.audio || p.Audio;
    if (au && au.length) { const f = au instanceof Float32Array ? au : Float32Array.from(au); samples += f.length; audio.push(fnv(new Uint32Array(f.buffer, f.byteOffset, f.length), 2166136261 ^ f.length)); }
    else audio.push(0);
  }
  return { hashes, audio, samples };
};
1`;
// only FrameTick runs frames (requestAnimationFrame is switched off: Load's StartEmulation restarts the page's loop, and Blazor caches the loop function so it cannot be replaced), so the comparison is frame for frame
const press = (page, which) => page.eval(`(async () => { await nesInterop._mainRef.invokeMethodAsync('Js${which}State'); if (nesInterop._loopActive) nesInterop.stopEmulationLoop(); return document.body.innerText; })()`);
const line = (text, re) => (text.match(new RegExp('[^\\n]*(?:' + re + ')[^\\n]*')) || [''])[0].trim();
const eq = (x, y) => x.length === y.length && x.every((h, i) => h === y[i]);
const count = (x, y) => x.filter((h, i) => h === y[i]).length;

fs.mkdirSync(path.join(a.roms, 'nes'), { recursive: true });
fs.copyFileSync(a.nes, path.join(a.roms, 'nes', 'game.nes'));
const apus = (a.apus || 'FIX,INES,NSTC,DARC,FCEU,NOFR,TUXN').split(',');
const N = 90;

const lab = await startLab({ publishDir: a.publish, romsDir: a.roms, headless: !a.show });
try {
  {
    // a fresh page (no stored cores) runs the stock FIX picture and sound chips, as the desktop does
    const page = await lab.open('/?rom=' + encodeURIComponent('roms/nes/game.nes'), { timeout: 240000 });
    await page.waitFor("document.body.innerText.includes('ROM: roms/')", 240000);
    const ids = await page.eval(`[...document.querySelectorAll('[id$="CoreSelect"]')].map(b => b.id + '=' + (b.dataset.core || b.value || b.textContent.trim())).join(' ')`);
    check('a fresh page runs NES games on the PPU FIX and APU FIX cores', /ppuCoreSelect=[^ ]*FIX/i.test(ids) && /apuCoreSelect=[^ ]*FIX/i.test(ids), ids.slice(0, 200));
  }
  for (const apu of apus) {
    const page = await lab.open('/?rom=' + encodeURIComponent('roms/nes/game.nes'), { timeout: 240000 });
    await page.waitFor("document.body.innerText.includes('ROM: roms/')", 240000);
    await page.eval(FRAMES);
    // the stock FIX picture and sound chips (Lite's own default may be another), then the APU under test
    const rp = await pickCore(page, 'select[aria-label="Ppu core"]', 'FIX'); await sleep(800);
    const r = await pickCore(page, 'select[aria-label="Apu core"]', apu); await sleep(1500);
    const cores = await page.eval(`(async () => { const s = await brokenNesDebug.get('/api/debug/state'); return s.cpus[0].name; })()`);
    if (!check(`APU ${apu}: PPU FIX and APU ${apu} picked`, rp === 'ok' && r === 'ok', `${rp} / ${r}; ${cores}`)) continue;
    await page.eval(`window.__frames(60)`);
    await page.eval(`window.__frames(8, [3])`);   // Start: the music begins
    await page.eval(`window.__frames(97)`);
    const probe = () => page.eval(`(async () => { try { return JSON.stringify(await brokenNesDebug.get('/api/debug/state')).slice(0, 400); } catch (e) { return 'probe ' + e; } })()`);
    const p0 = process.env.LITE_DIAG ? await probe() : '';
    const saved = await press(page, 'Save');
    if (!check(`APU ${apu}: Save state`, /State saved/.test(saved), line(saved, 'State saved|error'))) continue;
    const first = await page.eval(`window.__frames(${N}, [7])`);
    const later = await page.eval(`window.__frames(${N}, [7])`);
    const loaded = await press(page, 'Load');
    if (process.env.LITE_DIAG) console.log('  before save: ' + p0 + ' | after load: ' + await probe());
    const again = await page.eval(`window.__frames(${N}, [7])`);
    check(`APU ${apu}: after Load state the next ${N} frames are the same pictures`, eq(first.hashes, again.hashes), `${count(first.hashes, again.hashes)}/${N}`);
    check(`APU ${apu}: ... and the same sound samples, frame by frame`, first.samples > 0 && eq(first.audio, again.audio), `${count(first.audio, again.audio)}/${N} frames equal, ${first.samples} vs ${again.samples} samples`);
    if (process.env.LITE_DIAG) { for (let k = -3; k <= 3; k++) { let m = 0; for (let i = 0; i < N; i++) if (first.audio[i + k] !== undefined && first.audio[i + k] === again.audio[i]) m++; console.log(`  lag ${k}: ${m} audio frames equal`); }
      console.log('  pics first ' + first.hashes.slice(0, 4).join(',') + ' again ' + again.hashes.slice(0, 4).join(','));
      console.log('  loaded text: ' + loaded.split('\n').filter(l => /oad|rror|tate/.test(l)).join(' | ').slice(0, 600)); }
    check(`APU ${apu}: negative control (another moment differs)`, !eq(later.audio, again.audio) || !eq(later.hashes, again.hashes), `${count(later.audio, again.audio)}/${N}`);
  }
} finally { await lab.close(); }

const failed = results.filter(r => !r.ok);
console.log(`\n${results.length - failed.length} passed, ${failed.length} failed`);
if (a.json) fs.writeFileSync(a.json, JSON.stringify(results, null, 2));
process.exit(failed.length ? 1 : 0);
