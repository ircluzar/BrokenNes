// profile-lite.mjs - where a Lite session's time goes in the WebAssembly build: samples the page's CPU (JS and wasm functions) over N frame calls and prints the top functions by self time.
//
//   node profile-lite.mjs --publish <dir> --roms <dir> --rom "md/Sonic the Hedgehog (JUE) [!].bin" --symbols <dotnet.native.js.symbols> [--frames 300] [--ppu NES:FIXS] [--apu id] [--top 60]
import { startLab, args } from './litelab.mjs';
const a = args();
const lab = await startLab({ publishDir: a.publish, romsDir: a.roms, headless: !a.show });
try {
  const page = await lab.open('/?rom=' + encodeURIComponent('roms/' + a.rom), { timeout: 240000 });
  await page.waitFor("document.body.innerText.includes('ROM: roms/')", 240000);
  for (const slot of ['cpu', 'ppu', 'apu']) {
    if (!a[slot]) continue;
    const r = await page.eval(`(() => { const s = document.querySelector('select[aria-label="${slot[0].toUpperCase() + slot.slice(1)} core"]'); const o = s && [...s.options].find(x => x.value === ${JSON.stringify(a[slot])}); if (!o) return 'no'; s.value = o.value; s.dispatchEvent(new Event('change', { bubbles: true })); return 'ok'; })()`);
    if (r !== 'ok') throw new Error('cannot pick ' + slot + ' ' + a[slot]);
    await new Promise(r => setTimeout(r, 1500));
  }
  const n = parseInt(a.frames || '300', 10);
  await page.eval(`(async()=>{ window.__r = nesInterop._dotNetRef; nesInterop.stopEmulationLoop(); for (let i = 0; i < 200; i++) await window.__r.invokeMethodAsync('FrameTick'); return 1; })()`);
  const prof = await page.profile(`(async()=>{ for (let i = 0; i < ${n}; i++) await window.__r.invokeMethodAsync('FrameTick'); return 1; })()`, { top: parseInt(a.top || '60', 10), symbols: a.symbols });
  console.log('sampled ms', prof.totalMs, ' per frame', (prof.totalMs / n).toFixed(2));
  for (const t of prof.top) console.log(String(t.selfPct).padStart(5), String(t.selfMs).padStart(9), t.name);
} finally { await lab.close(); }
