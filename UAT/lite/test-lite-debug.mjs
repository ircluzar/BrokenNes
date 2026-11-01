// test-lite-debug.mjs - the Lite debug bridge (BrokenDev milestone D8: window.brokenNesDebug and the BrokenDev pages with ?lite=1) in BrokenNes Lite
// (the Blazor WebAssembly AOT build), in a real Edge/Chrome over the DevTools protocol.
//
//   node test-lite-debug.mjs --publish <AOT publish dir> --roms <scratch dir with nes\debugtest.nes and gb\debugtest.gb> [--json out.json] [--show]
//
// Frames are run with the page's own FrameTick (the page's loop stopped), so the machine is still between calls. What it checks:
//   - the bridge answers /api/debug/state like the desktop route (console, CPUs, registers), read only (nothing attached), and twice the same
//     while no frame runs; one frame later the frame count moved;
//   - memory through the bridge is the ROM file's own bytes (PRG after the 16-byte iNES header), an independent truth;
//   - the debugger page and the viewers page, in an iframe of Lite with ?lite=1, show the bridge's PC and the ROM's bytes;
//   - a Game Boy session through the same bridge;
//   - negative controls: an unknown or control route is refused by name; with the bridge removed the page says there is no bridge (never empty);
//   - the speed: frame times before the bridge was ever called and after it was used, interleaved: the bridge costs a frame nothing.
import fs from 'node:fs';
import path from 'node:path';
import { startLab, args } from './litelab.mjs';

const a = args();
const results = [];
function check(name, ok, detail = '') {
  results.push({ group: 'debug-bridge', name, ok: !!ok, detail: String(detail) });
  console.log(`${ok ? 'PASS' : 'FAIL'} [debug-bridge] ${name}${detail !== '' ? ' - ' + detail : ''}`);
  return !!ok;
}
const nesRom = path.join(a.roms, 'nes', 'debugtest.nes'), gbRom = path.join(a.roms, 'gb', 'debugtest.gb');
for (const f of [nesRom, gbRom]) if (!fs.existsSync(f)) { console.log(`missing ${f}`); process.exit(2); }
const romBytes = fs.readFileSync(nesRom);

const HELPERS = `window.__frames = async n => {
  const ref = (window.__ref = nesInterop._dotNetRef || window.__ref);
  if (nesInterop._loopActive) nesInterop.stopEmulationLoop();
  const times = [];
  for (let i = 0; i < n; i++) { const t0 = performance.now(); await ref.invokeMethodAsync('FrameTick'); times.push(performance.now() - t0); }
  return times;
};
window.__frame = async (src, waitMs = 20000) => {
  document.querySelectorAll('iframe.__dbg').forEach(f => f.remove());
  const f = document.createElement('iframe'); f.className = '__dbg'; f.style.width = '900px'; f.style.height = '700px'; f.src = src;
  document.body.appendChild(f);
  const t0 = performance.now();
  while (performance.now() - t0 < waitMs) {
    await new Promise(r => setTimeout(r, 200));
    const d = f.contentDocument;
    if (d && d.body && d.body.dataset.refreshed) {
      const pre = id => (d.getElementById(id) || {}).textContent || '';
      const st = d.getElementById('status');
      return { status: st ? st.textContent : '', pc: st ? st.dataset.pc || '' : '', console: st ? st.dataset.console || '' : '', registers: pre('registers'), disasm: pre('disasm'), memory: pre('memory') };
    }
  }
  return null;
};
1`;

async function open(lab, rom) {
  const page = await lab.open('/?rom=' + encodeURIComponent(rom), { timeout: 240000 });
  await page.waitFor("document.body.innerText.includes('ROM: roms/')", 240000);
  await page.eval(HELPERS);
  return page;
}
const mean = t => t.reduce((x, y) => x + y, 0) / t.length;
const hex2 = b => b.toString(16).toUpperCase().padStart(2, '0');

const lab = await startLab({ publishDir: a.publish, romsDir: a.roms, headless: !a.show });
try {
  // ------------------------------------------------------------ the NES
  let page = await open(lab, 'roms/nes/debugtest.nes');
  const before1 = await page.eval(`window.__frames(300)`, { timeout: 600000 });   // the bridge never called yet
  const s1 = await page.eval(`brokenNesDebug.get('/api/debug/state')`);
  const s2 = await page.eval(`brokenNesDebug.get('/api/debug/state')`);
  check('the bridge answers /api/debug/state (the NES, its CPU and registers), read only', s1.success && s1.console === 'nes' && s1.lite && !s1.attached && !s1.armed && s1.cpus?.[0]?.registers?.A !== undefined,
        `${s1.console}: ${s1.cpus?.[0]?.name} PC ${s1.cpus?.[0]?.pcHex}, A ${s1.cpus?.[0]?.registers?.A}; attached ${s1.attached}`);
  check('two readings with no frame between are the same moment', s1.frame === s2.frame && s1.cpus[0].pcHex === s2.cpus[0].pcHex, `frame ${s1.frame} / ${s2.frame}, PC ${s1.cpus[0].pcHex} / ${s2.cpus[0].pcHex}`);
  await page.eval(`window.__frames(1)`);
  const s3 = await page.eval(`brokenNesDebug.get('/api/debug/state')`);
  check('NEGATIVE: one frame later the frame count moved', s3.frame === s1.frame + 1, `frame ${s1.frame} -> ${s3.frame}`);

  const mem = await page.eval(`brokenNesDebug.get('/api/debug/memory?spec=' + encodeURIComponent('prg:$0') + '&length=16')`);
  const want = Array.from(romBytes.subarray(16, 32)).map(hex2).join(' ');
  const got = (mem.data || []).map(hex2).join(' ');
  check("memory through the bridge is the ROM file's own PRG bytes", mem.success && got === want, `bridge prg:$0: ${got}; file after the header: ${want}`);

  const dv = await page.eval(`window.__frame('debug/debugger.html?lite=1&once=1&mem=' + encodeURIComponent('prg:$0'))`, { timeout: 60000 });
  check('the debugger page in Lite (?lite=1) shows the bridge\'s PC and the ROM\'s bytes', dv && dv.console === 'nes' && dv.pc === s3.cpus[0].pcHex && dv.registers.includes('PC ' + s3.cpus[0].pcHex) && dv.memory.includes(want) && dv.disasm.split('\n').some(l => l.startsWith('> ' + s3.cpus[0].pcHex)),
        dv ? `page PC ${dv.pc}, bridge ${s3.cpus[0].pcHex}; ${dv.status}` : 'the page did not refresh');
  const vv = await page.eval(`window.__frame('debug/viewers.html?lite=1&once=1&domain=PRG&address=0')`, { timeout: 60000 });
  check('the viewers page in Lite shows the ROM\'s PRG bytes', vv && vv.memory.split('\n')[0] === '0000: ' + want, vv ? `${vv.memory.split('\n')[0]} ; ${vv.status}` : 'the page did not refresh');

  const btn = await page.eval(`(() => { const b = document.getElementById('debugPagesBtn'); return b ? b.getAttribute('onclick') : null; })()`);
  check("the page's Debug button opens the debug pages with ?lite=1", btn && btn.includes('debug/viewers.html?lite=1'), String(btn));
  const bad = await page.eval(`brokenNesDebug.get('/api/debug/breakpoints')`);
  const poke = await page.eval(`brokenNesDebug.get('/api/memory/poke?domain=RAM&address=0&value=1')`);
  check('NEGATIVE: a route the bridge does not answer is refused by name (no control routes)', !bad.success && /read-only debug routes only/.test(bad.error) && !poke.success, `${bad.error} | ${poke.error}`);
  const none = await page.eval(`(async () => { const keep = window.brokenNesDebug; delete window.brokenNesDebug; try { return await window.__frame('debug/debugger.html?lite=1&once=1'); } finally { window.brokenNesDebug = keep; } })()`, { timeout: 60000 });
  check('NEGATIVE: with no bridge the page says so (not an empty page)', none && /no Lite debug bridge/.test(none.status) && none.registers === '', none ? none.status : 'the page did not refresh');

  // the speed: before the bridge was ever called (above) and after it was used, interleaved
  const after1 = await page.eval(`window.__frames(300)`, { timeout: 600000 });
  const before2 = await page.eval(`window.__frames(300)`, { timeout: 600000 });
  await page.eval(`brokenNesDebug.get('/api/debug/tiles?count=256')`);
  const after2 = await page.eval(`window.__frames(300)`, { timeout: 600000 });
  const mb = mean(before1.slice(60).concat(before2)), ma = mean(after1.concat(after2));
  check('the bridge costs a frame nothing (frame times before it was ever used and after, within 15%)', Math.abs(ma - mb) / mb < 0.15, `before ${mb.toFixed(3)} ms, after ${ma.toFixed(3)} ms a frame`);
  results.at(-1).speed = { beforeMs: mb, afterMs: ma };

  // ------------------------------------------------------------ a Game Boy session
  page = await open(lab, 'roms/gb/debugtest.gb');
  await page.eval(`window.__frames(120)`, { timeout: 600000 });
  const g = await page.eval(`brokenNesDebug.get('/api/debug/state')`);
  const gd = await page.eval(`brokenNesDebug.get('/api/memory/domains')`);
  const gv = await page.eval(`window.__frame('debug/debugger.html?lite=1&once=1')`, { timeout: 60000 });
  check('a Game Boy session through the bridge (state, domains, the debugger page)', g.success && g.console === 'gb' && (gd.domains || []).some(d => d.name === 'HRAM') && gv && gv.pc === g.cpus[0].pcHex,
        `${g.console} PC ${g.cpus?.[0]?.pcHex}, page ${gv?.pc}; domains ${(gd.domains || []).map(d => d.name).join(', ')}`);
} catch (e) {
  check('run', false, e.stack || String(e));
} finally {
  await lab.close?.();
}
const pass = results.every(r => r.ok);
if (a.json) fs.writeFileSync(a.json, JSON.stringify({ suite: 'lite-debug', pass, results }, null, 2));
console.log(JSON.stringify({ suite: 'lite-debug', pass, passed: results.filter(r => r.ok).length, failed: results.filter(r => !r.ok).length }));
process.exit(pass ? 0 : 1);
