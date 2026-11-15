// Per-core cost split of one console in the page (FrameTick timed in a loop), switching one slot at a time.
// usage: node split.mjs --base URL --out DIR --rom FILE --port N --swaps "APU:SPD,PPU:SPD"
import fs from 'node:fs'; import path from 'node:path';
import { launchEdge, Cdp, openPage, sleep, killTree } from './cdp.mjs';
import * as L from './lib.mjs';
const A = {}; { const v = process.argv.slice(2); for (let i = 0; i < v.length; i += 2) A[v[i].replace(/^--/, '')] = v[i + 1]; }
fs.mkdirSync(A.out, { recursive: true });
const e = await launchEdge(path.join(A.out, 'profile-split'), +A.port); fs.appendFileSync(path.join(A.out, 'pids.txt'), `${e.proc.pid} split\n`);
const c = new Cdp(e.ws); await c.ready; const p = await openPage(c);
const time = async (n = 300) => p.eval(`(async()=>{ for(let k=0;k<100&&!nesInterop._dotNetRef;k++) await new Promise(r=>setTimeout(r,100)); const ref = nesInterop._dotNetRef || window.__ref; window.__ref = ref; nesInterop.stopEmulationLoop && nesInterop.stopEmulationLoop();
  for(let i=0;i<60;i++) await ref.invokeMethodAsync('FrameTick'); const t=[]; for(let i=0;i<${n};i++){ const t0=performance.now(); await ref.invokeMethodAsync('FrameTick'); t.push(performance.now()-t0); }
  t.sort((a,b)=>a-b); return +t[t.length>>1].toFixed(2); })()`);
try {
  if (!await L.load(p, A.base, A.rom)) throw new Error('no boot');
  await sleep(1500); for (let i = 0; i < 3; i++) { await p.key('Enter', 'Enter'); await sleep(1200); }
  console.log(JSON.stringify({ step: 'stock', medianMs: await time() }));
  for (const sw of (A.swaps || '').split(',').filter(Boolean)) { const [slot, id] = sw.split(':');
    await L.openMenu(p, slot); await L.pickCore(p, id); await sleep(1500); await L.closeMenu(p);
    console.log(JSON.stringify({ step: sw, label: await L.coreLabel(p, slot), status: await L.status(p), medianMs: A.notime ? null : await time() })); }
} catch (er) { console.log("ERR", String(er).slice(0,300)); } finally { killTree(e.proc.pid); process.exit(0); }
