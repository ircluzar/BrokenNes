// bench-lite.mjs - the Lite speed suite for the Sega consoles: frames per second in a real browser window (Edge/Chrome, the WebAssembly AOT build), per console and per path.
//
//   node bench-lite.mjs --publish <AOT publish dir> --roms <dir with sms\ gg\ md\> [--cases a,b] [--frames 600] [--json out.json] [--label name] [--show]
//
// How it measures: the page's own frame call (the one its animation loop makes every frame, FrameTick: the core runs one frame and the picture and the audio come back to JavaScript)
// is driven in a JavaScript loop and timed with performance.now() around each call, so the number does not depend on the browser's animation clock (which a hidden window throttles).
// The same pad script as the desktop's `--sega-bench` plays (menus, then walking and jumping), so the frames measured are gameplay, not a title screen. Each case runs in a fresh browser profile.
// Reported: mean / median / p95 / max ms per frame, frames per second, the multiple of the console's real frame rate (60 NTSC), and the split the page's profile hook gives
// (core RunFrame, audio read, pixel conversion; the rest of a call is the crossing to JavaScript). The bar is 1.5x real time (90 fps for an NTSC console).
import fs from 'node:fs';
import path from 'node:path';
import { startLab, args } from './litelab.mjs';

const a = args();
const frames = parseInt(a.frames || '600', 10);
const label = a.label || 'run';
const sleep = ms => new Promise(r => setTimeout(r, ms));

// [name, console, rom (under --roms), { ppu, apu, cpu }, note]
const CASES = [
  ['sms-alexkidd', 'sms', 'sms/Alex Kidd in Miracle World (USA, Europe) (v1.1).sms'],
  ['sms-fantasyzone', 'sms', 'sms/Fantasy Zone (World) (v1.2).sms'],
  ['sms-sonic', 'sms', 'sms/Sonic The Hedgehog (USA, Europe).sms'],
  ['sms-alexkidd+fm', 'sms', 'sms/Alex Kidd in Miracle World (USA, Europe) (v1.1).sms', { apu: 'SMS-FM' }, 'YM2413 on'],
  ['sms-alexkidd>fixs', 'sms', 'sms/Alex Kidd in Miracle World (USA, Europe) (v1.1).sms', { ppu: 'NES:FIXS' }, 'picture on PPU_FIXS'],
  ['sms-alexkidd>fix', 'sms', 'sms/Alex Kidd in Miracle World (USA, Europe) (v1.1).sms', { ppu: 'NES:FIX' }, 'picture on PPU_FIX'],
  ['sms-alexkidd>nesapu', 'sms', 'sms/Alex Kidd in Miracle World (USA, Europe) (v1.1).sms', { apu: 'NES:FIX' }, 'PSG on APU_FIX'],
  ['gg-columns', 'gg', 'gg/Columns (U) [!].gg'],
  ['gg-sonic', 'gg', 'gg/Sonic The Hedgehog (U) (V1.0) [!].gg'],
  ['gg-sonic>fixs', 'gg', 'gg/Sonic The Hedgehog (U) (V1.0) [!].gg', { ppu: 'NES:FIXS' }, 'picture on PPU_FIXS'],
  ['md-sonic', 'md', 'md/Sonic the Hedgehog (JUE) [!].bin'],
  ['md-sonic2', 'md', 'md/Sonic the Hedgehog 2 (JUE) [!].bin'],
  ['md-streetsofrage', 'md', 'md/Streets of Rage (JUE) (REV 00) [!].bin'],
  ['md-gunstar', 'md', 'md/Gunstar Heroes (E) [!].bin'],
  ['md-sonic>fixs', 'md', 'md/Sonic the Hedgehog (JUE) [!].bin', { ppu: 'NES:FIXS' }, 'picture on PPU_FIXS'],
  ['md-sonic>nesapu', 'md', 'md/Sonic the Hedgehog (JUE) [!].bin', { apu: 'NES:FIX' }, 'sound on APU_FIX'],
];

const KEY = { A: 0, B: 1, Select: 2, Start: 3, Up: 4, Down: 5, Left: 6, Right: 7 };

/** The same script as Workshop's --sega-bench PlayScript, as { frame: [button indices] }. */
function playScript(n) {
  const steps = new Map([[90, ['Start']], [96, []], [150, ['A']], [156, []], [210, ['Start']], [216, []], [270, ['B']], [276, []]]);
  const pat = ['Right', 'Right+A', 'Right', 'Right+B', 'Down', 'Left', 'Left+A', 'Up', 'Right+A+B', 'Right'];
  let k = 0;
  for (let f = 330; f < n; f += 25) steps.set(f, pat[k++ % pat.length].split('+'));
  const out = {};
  for (const [f, names] of steps) out[f] = names.filter(Boolean).map(x => KEY[x]);
  return out;
}

const wanted = a.cases ? String(a.cases).split(',') : null;
const results = [];
for (const [name, cons, rom, cores = {}, note = ''] of CASES) {
  if (wanted && !wanted.includes(name)) continue;
  const file = path.join(a.roms, rom);
  if (!fs.existsSync(file)) { console.log(`SKIP ${name}: no ROM at ${file}`); continue; }
  const lab = await startLab({ publishDir: a.publish, romsDir: a.roms, headless: !a.show });
  try {
    const page = await lab.open('/?rom=' + encodeURIComponent('roms/' + rom), { timeout: 240000 });
    await page.waitFor("document.body.innerText.includes('ROM: roms/')", 240000);
    for (const [slot, id] of Object.entries(cores)) {
      const ok = await page.eval(`(() => { const s = document.querySelector('select[aria-label="${slot[0].toUpperCase() + slot.slice(1)} core"]'); if (!s) return 'no select'; const o = [...s.options].find(x => x.value === ${JSON.stringify(id)}); if (!o) return 'no option ' + [...s.options].map(x => x.value).join(','); s.value = ${JSON.stringify(id)}; s.dispatchEvent(new Event('change', { bubbles: true })); return 'ok'; })()`);
      if (ok !== 'ok') throw new Error(`${name}: cannot pick ${slot} ${id}: ${ok}`);
      await sleep(1500);
    }
    await page.eval(`(async () => {
      const ref = nesInterop._dotNetRef, main = nesInterop._mainRef || ref;
      window.__ref = ref; window.__main = main;
      nesInterop.stopEmulationLoop();   // the page's own animation loop would add frames between ours; the core keeps its running state, FrameTick still runs one frame per call
      window.__script = ${JSON.stringify(playScript(frames + 200))};
      window.__state = new Array(12).fill(false);
      window.__apply = f => { const b = window.__script[f]; if (!b) return; window.__state.fill(false); for (const i of b) window.__state[i] = true; main.invokeMethodAsync('UpdateInput', window.__state); };
      window.__frame = 0;
      // warm-up: 200 frames (they fill caches and the garbage collector's heap)
      for (let i = 0; i < 200; i++) { window.__apply(window.__frame++); await ref.invokeMethodAsync('FrameTick'); }
      return 1;
    })()`, { timeout: 900000 });
    await sleep(500);
    const cpu0 = await lab.processCpuSeconds();
    const r = await page.eval(`(async () => {
      const ref = window.__ref;
      let hasProfile = true; try { await ref.invokeMethodAsync('ResetSessionProfile'); } catch { hasProfile = false; }
      const times = []; let audioSamples = 0, sr = 0, w = 0, h = 0, aw = 0;
      const t00 = performance.now();
      for (let i = 0; i < ${frames}; i++) {
        window.__apply(window.__frame++);
        const t0 = performance.now();
        const p = await ref.invokeMethodAsync('FrameTick');
        times.push(performance.now() - t0);
        if (p) { const au = p.pcm ? p.pcm.byteLength >> 2 : (p.audio ? p.audio.length : 0); audioSamples += au; sr = p.sr || sr; w = p.w || w; h = p.h || h; aw = p.aw || aw; }
      }
      const total = performance.now() - t00;
      const prof = hasProfile ? await ref.invokeMethodAsync('GetSessionProfile') : [1, 0, 0, 0, 0];
      times.sort((x, y) => x - y);
      const q = f => times[Math.min(times.length - 1, Math.floor(times.length * f))];
      return { total, mean: times.reduce((x, y) => x + y, 0) / times.length, median: q(0.5), p95: q(0.95), p99: q(0.99), max: times[times.length - 1], audioSamples, sr, w, h, aw, prof };
    })()`, { timeout: 900000 });
    const cpu1 = await lab.processCpuSeconds();
    r.cpuMs = (cpu1 - cpu0) * 1000 / frames;
    const fps = 1000 / r.mean;
    const cpuFps = r.cpuMs > 0 ? 1000 / r.cpuMs : 0;
    const target = 60;   // NTSC; the PAL games are not in this roster
    const [pf, run, aud, pix, body] = r.prof;
    const row = {
      case: name, console: cons, note, size: `${r.w}x${r.h}`, aw: r.aw, sampleRate: r.sr, audioPerFrame: +(r.audioSamples / frames).toFixed(1),
      meanMs: +r.mean.toFixed(2), medianMs: +r.median.toFixed(2), p95Ms: +r.p95.toFixed(2), maxMs: +r.max.toFixed(2), fps: +fps.toFixed(1), xRealTime: +(fps / target).toFixed(2), cpuMs: +r.cpuMs.toFixed(2), cpuFps: +cpuFps.toFixed(1), cpuXRealTime: +(cpuFps / target).toFixed(2),
      coreMs: +(run / pf).toFixed(2), audioMs: +(aud / pf).toFixed(2), pixelMs: +(pix / pf).toFixed(2), bodyMs: +(body / pf).toFixed(2), glueMs: +(r.mean - body / pf).toFixed(2),
      errors: page.consoleErrors().length,
    };
    results.push(row);
    console.log(`${name.padEnd(22)} ${row.size.padEnd(8)} mean ${row.meanMs.toString().padStart(6)} ms  med ${row.medianMs.toString().padStart(6)}  p95 ${row.p95Ms.toString().padStart(6)}  max ${row.maxMs.toString().padStart(6)}  ->  ${row.fps.toString().padStart(6)} fps = ${row.xRealTime}x  [cpu ${row.cpuMs} ms = ${row.cpuXRealTime}x] | core ${row.coreMs} audio ${row.audioMs} pixels ${row.pixelMs} glue ${row.glueMs}  ${note}`);
  } catch (e) {
    console.log(`${name}: FAILED ${e.message}`);
    results.push({ case: name, console: cons, error: String(e.message) });
  } finally { await lab.close(); }
}
if (a.json) fs.writeFileSync(a.json, JSON.stringify({ label, frames, results }, null, 2));
