// Lite-specific helpers on top of cdp.mjs.
import fs from 'node:fs'; import { sleep } from './cdp.mjs'; import { execSync } from 'node:child_process';
const INST = fs.readFileSync(new URL('./instrument.js', import.meta.url), 'utf8');
export function cpuLoad() { try { return +execSync('powershell -NoProfile -c "(Get-CimInstance Win32_Processor | Measure-Object LoadPercentage -Average).Average"').toString().trim(); } catch { return -1; } }
export async function load(p, base, rom, extra = '') {
  await p.goto(`${base}/?rom=${base}/roms/${encodeURIComponent(rom)}${extra}`);
  let ok = false; for (let i = 0; i < 90; i++) { await sleep(500); try { if (await p.eval(INST)) { ok = true; break; } } catch {} }
  if (!ok) return false;
  for (let i = 0; i < 60; i++) { const r = await p.eval('__uat.read()'); if (r.badge.some(b => /State: Running/.test(b))) return true; await sleep(500); }
  return false;
}
export async function measure(p, secs = 4) { await p.eval('__uat.reset()'); await sleep(secs * 1000); return p.eval('__uat.read()'); }
export async function canvasShot(p, file) { const r = await p.rect('#nes-canvas'); if (!r || !r.visible) { await p.shot(file); return 0; } await p.shot(file, { x: r.x, y: r.y, width: r.w, height: r.h }); return fs.statSync(file).size; }
export const status = p => p.eval(`document.querySelector('[role=status]')?.textContent?.trim()||''`);
export const ariaBtn = l => `button[aria-label="${l}"]`;
export async function clickText(p, text) { return p.eval(`(()=>{const b=[...document.querySelectorAll('button')].find(b=>(b.getAttribute('aria-label')||b.textContent).trim()===${JSON.stringify(text)}); if(!b) return false; b.scrollIntoView({block:'center'}); return true})()`).then(async ok => ok && p.click(`button[aria-label="${text}"]`).then(x => x || p.eval(`[...document.querySelectorAll('button')].find(b=>b.textContent.trim()===${JSON.stringify(text)})?.click()||true`))); }
export async function coreSlots(p) { return p.eval(`['CPU','PPU','APU'].filter(s=>{const b=document.querySelector('button[aria-label="'+s+' core menu"]'); return b && b.offsetParent})`); }
export async function coreLabel(p, slot) { return p.eval(`document.querySelector('button[aria-label="${slot} core menu"]')?.textContent.trim()`); }
export async function openMenu(p, slot) { await p.eval(`document.querySelector('button[aria-label="${slot} core menu"]').scrollIntoView({block:'center'})`); await p.click(`button[aria-label="${slot} core menu"]`); for (let i = 0; i < 20; i++) { await sleep(150); if (await p.eval(`!!document.getElementById('core-menu-sheet')`)) return true; } return false; }
export async function menuInfo(p) { return p.eval(`(()=>{const s=document.getElementById('core-menu-sheet'); s.querySelectorAll('details').forEach(d=>d.open=true); const h=[...s.querySelectorAll('.cm-h')].map(e=>e.textContent.trim()); return {heads:h, current:s.querySelector('#cm-current')?.textContent.trim(), currentId:s.querySelector('.cm-on[data-core]')?.dataset.core, cores:[...new Set([...s.querySelectorAll('[data-core]')].map(b=>b.dataset.core))], recent: s.textContent.includes('(the cores you switch to appear here)')?0:1}})()`); }
export async function pickCore(p, id) { const sel = `#core-menu-sheet [data-core="${id}"]`; await p.eval(`(()=>{const s=document.getElementById('core-menu-sheet'); s.querySelectorAll('details').forEach(d=>d.open=true); document.querySelector(${JSON.stringify(sel)})?.scrollIntoView({block:'center'})})()`); await sleep(100); return p.click(sel); }
export async function closeMenu(p) { await p.eval(`document.querySelector('.cm-close')?.click()`); await sleep(150); }
export const IDB = `(async()=>{const out={}; const dbs=await indexedDB.databases(); for(const d of dbs){const db=await new Promise(r=>{const q=indexedDB.open(d.name);q.onsuccess=()=>r(q.result)}); for(const s of db.objectStoreNames){const keys=await new Promise(r=>{const q=db.transaction(s).objectStore(s).getAllKeys();q.onsuccess=()=>r(q.result)}); out[d.name+'/'+s]=keys.map(String)} db.close()} out.localStorage=Object.keys(localStorage); return out})()`;
export async function btn(p, sel) { await p.eval(`document.querySelector(${JSON.stringify(sel)})?.scrollIntoView({block:'center'})`); await sleep(100); return p.click(sel); }
