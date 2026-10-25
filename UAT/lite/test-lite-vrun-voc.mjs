// test-lite-vrun-voc.mjs - the Game Boy hello world of VRUN: Vaults of Obsolete Code (BrokenDev milestone D2) in BrokenNes Lite (the Blazor
// WebAssembly AOT build), in a real Edge/Chrome over the DevTools protocol.
//
//   node test-lite-vrun-voc.mjs --publish <AOT publish dir> --roms <scratch dir> --rom <vrun_voc.gbc> [--json out.json] [--show]
//
// The ROM is BrokenDev's build (out\vrun-voc\vrun_voc.gbc), copied to <roms>\gb\. Frames are run with the page's own FrameTick (as the other Lite
// suites), the pad through UpdateInput (Lite's pad array: A B Select Start Up Down Left Right ...). What it checks:
//   - the page runs it on the Game Boy Color console, at 160 x 144, in colour (more than the original's 4 colours);
//   - Right held moves the sprite (the pictures change while it is held, and stop changing when it is released: the hello world is still);
//   - A makes a sound (the samples' peak after A is far above the silent frames before it);
//   - the battery save: leaving the game commits its save to IndexedDB (battery_v1:gb<sha1>.sav, 32 KB, the magic "VOC0" and the boot counter 1);
//     a fresh page of the same game finds it (the counter goes to 2, and the screen shows it: another picture than the first boot's);
//   - the same cartridge on the Game Boy console (the console menu): four shades at most.
// It cannot see sound through a loudspeaker (samples handed to the page are measured), nor a phone.
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { startLab, args } from './litelab.mjs';

const a = args();
const sleep = ms => new Promise(r => setTimeout(r, ms));
const results = [];
function check(name, ok, detail = '') {
  results.push({ group: 'vrun-voc', name, ok: !!ok, detail: String(detail) });
  console.log(`${ok ? 'PASS' : 'FAIL'} [vrun-voc] ${name}${detail !== '' ? ' - ' + detail : ''}`);
  return !!ok;
}
if (!a.rom || !fs.existsSync(a.rom)) { console.log(`the ROM is missing: ${a.rom} (build it: BrokenDev\\brokendev.ps1 build vrun-voc)`); process.exit(2); }
fs.mkdirSync(path.join(a.roms, 'gb'), { recursive: true });
fs.copyFileSync(a.rom, path.join(a.roms, 'gb', 'vrun_voc.gbc'));
const romBytes = fs.readFileSync(a.rom);
const sha1 = crypto.createHash('sha1').update(romBytes).digest('hex');
const saveKey = `battery_v1:gb${sha1}.sav`;

const FRAMES = `window.__frames = async (n, pad) => {
  const ref = window.__ref || (window.__ref = nesInterop._dotNetRef);
  if (nesInterop._loopActive) nesInterop.stopEmulationLoop();
  const main = nesInterop._mainRef || ref;
  const st = new Array(12).fill(false); if (pad) for (const i of pad) st[i] = true;
  await main.invokeMethodAsync('UpdateInput', st);
  let w = 0, h = 0, hashes = [], peak = 0, colors = 0;
  for (let i = 0; i < n; i++) {
    const p = await ref.invokeMethodAsync('FrameTick');
    if (!p) continue;
    const fb = p.fb || p.Framebuffer;
    if (fb) {
      const u = new Uint32Array(fb.buffer, fb.byteOffset, fb.byteLength >> 2); let hh = 2166136261;
      for (let k = 0; k < u.length; k++) { hh ^= u[k]; hh = Math.imul(hh, 16777619); }
      hashes.push(hh >>> 0);
      if (i === n - 1) { const s = new Set(); const len = (p.w || 0) * (p.h || 0) || u.length; for (let k = 0; k < len && k < u.length; k++) s.add(u[k] & 0xFFFFFF); colors = s.size; }
    }
    if (p.pcm) { const f = new Float32Array(p.pcm.buffer, p.pcm.byteOffset, p.pcm.byteLength >> 2); for (let k = 0; k < f.length; k++) { const v = Math.abs(f[k]); if (v > peak) peak = v; } }
    w = p.w || w; h = p.h || h;
  }
  return { w, h, hashes, peak, colors, frames: n };
};
1`;

async function withLab(fn) {
  const lab = await startLab({ publishDir: a.publish, romsDir: a.roms, headless: !a.show });
  try { return await fn(lab); } finally { await lab.close(); }
}
async function openRom(lab) {
  const page = await lab.open('/?rom=' + encodeURIComponent('roms/gb/vrun_voc.gbc'), { timeout: 240000 });
  await page.waitFor("document.body.innerText.includes('ROM: roms/')", 240000);
  await page.eval(FRAMES);
  return page;
}
const readSave = page => page.eval(`(async () => { const b = await nesInterop.idbGetBytes(${JSON.stringify(saveKey)}); return b ? { len: b.length, head: Array.from(b.slice(0, 7)) } : null; })()`);
const leave = async page => { await page.eval(`nesInterop._mainRef.invokeMethodAsync('JsVisibilityChanged', false)`); await sleep(800); };

await withLab(async lab => {
  const page = await openRom(lab);
  const consoleSel = await page.eval(`document.getElementById('consoleSelect')?.value`);
  const start = await page.eval(`window.__frames(60)`);
  check('the page runs it on the Game Boy Color, 160 x 144, in colour', consoleSel === 'gbc' && start.w === 160 && start.h === 144 && start.colors > 4, `console=${consoleSel}, ${start.w} x ${start.h}, ${start.colors} colours`);
  const still = await page.eval(`window.__frames(30)`);
  const right = await page.eval(`window.__frames(30, [7])`);
  const after = await page.eval(`window.__frames(30)`);
  check('Right moves the sprite (the pictures change while it is held, and not before or after)', new Set(still.hashes).size === 1 && new Set(right.hashes).size >= 25 && new Set(after.hashes).size === 1 && after.hashes[0] !== still.hashes[0],
        `distinct pictures: ${new Set(still.hashes).size} still, ${new Set(right.hashes).size} of 30 with Right, ${new Set(after.hashes).size} after`);
  const quiet = await page.eval(`window.__frames(30)`);
  const beep = await page.eval(`window.__frames(20, [0])`);
  check('A makes a sound (the peak of the samples after A, against the silent frames before it)', quiet.peak < 0.01 && beep.peak > 0.05, `peak ${quiet.peak.toFixed(4)} before, ${beep.peak.toFixed(4)} after A`);
  await page.eval(`window.__frames(20)`);
  await leave(page);
  const first = await readSave(page);
  const magic = first ? String.fromCharCode(...first.head.slice(0, 4)) : '';
  check('leaving the game commits its battery save to IndexedDB (32 KB, "VOC0", boot 1)', first && first.len === 32768 && magic === 'VOC0' && first.head[4] === 1 && first.head[5] === 0, JSON.stringify(first));
  const page2 = await openRom(lab);
  const second = await page2.eval(`window.__frames(60)`);
  await leave(page2);
  const saved2 = await readSave(page2);
  check('a fresh page of the same game finds the save: the boot counter goes to 2 and the screen shows it', saved2 && saved2.head[4] === 2 && second.hashes[59] !== start.hashes[59], `${JSON.stringify(saved2)}; first-boot picture ${start.hashes[59]}, second-boot picture ${second.hashes[59]}`);
  // the same cartridge on an original Game Boy: the console menu
  const sel = await page2.eval(`(() => { const s = document.getElementById('consoleSelect'); if (!s) return 'no select'; const o = [...s.options].find(x => x.value === 'gb'); if (!o) return 'no gb option'; s.value = 'gb'; s.dispatchEvent(new Event('change', { bubbles: true })); return 'ok'; })()`);
  await sleep(3000);
  await page2.eval(FRAMES);
  const dmg = await page2.eval(`window.__frames(60)`);
  const now = await page2.eval(`document.getElementById('consoleSelect')?.value`);
  check('on the Game Boy console the same cartridge shows four shades at most', sel === 'ok' && now === 'gb' && dmg.colors >= 2 && dmg.colors <= 4, `${sel}; console=${now}; ${dmg.colors} colours`);
});

const failed = results.filter(r => !r.ok);
console.log(`\n${results.length - failed.length} passed, ${failed.length} failed`);
if (a.json) fs.writeFileSync(a.json, JSON.stringify(results, null, 2));
process.exit(failed.length ? 1 : 0);
