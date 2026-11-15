// BrokenNes Lite UAT harness: headless Edge over CDP (see README.md).
// usage: node run.mjs --base http://127.0.0.1:5098 --out <dir> --phase boot|speed|cores|touch|state|battery|misc [--roms a,b|console] [--build aot] [--port 9333] [--romdir <dir>]
import fs from 'node:fs'; import path from 'node:path';
import { launchEdge, Cdp, openPage, sleep, killTree } from './cdp.mjs';
import * as L from './lib.mjs';
const A = {}; { const v = process.argv.slice(2); for (let i = 0; i < v.length; i += 2) A[v[i].replace(/^--/, '')] = v[i + 1]; }
const base = A.base, out = A.out, phase = A.phase, build = A.build || 'build'; fs.mkdirSync(out, { recursive: true });
// Our own games first; c_* are commercial copies kept outside the repository (never commit them or their screenshots).
export const ROMS = {
  nes: ['VRUN_CTW_MMC3_2026-11-14_6838abc.nes', 'VRUN_CTW_m30_2026-11-14_6838abc.nes', 'SMB1_Remake_mmc3.nes', 'c_kirby.nes'],
  snes: ['supervrun-cgram-11-07.sfc', 'SMB1_Remake_mmc3_SNES.smc', 'c_smw.smc', 'c_dkc.smc'],
  wms: ['SuperVRUN_WMS.wms'],
  gb: ['dmg-acid2.gb', 'c_tetris.zip'],
  gbc: ['VRUN_Vaults_of_Obsolete_Code_V6_68475f9.gbc', 'cgb-acid2.gbc', 'c_drmariodx.gbc'],
  sms: ['VRUN_CTW_SMS_2026-11-10_mmc3.sms', 'c_sonic2.sms'],
  gg: ['VRUN_Vaults_GG_9ea616a.gg', 'c_sonictails.gg'],
  md: ['supervrun_md_338a853.bin', 'c_sonic1.bin'],
};
const pick = A.roms ? A.roms.split(',') : null;
const want = (con, rom) => !pick || pick.includes(con) || (rom && pick.includes(rom));
const res = []; const log = o => { res.push(o); console.log(JSON.stringify(o).slice(0, 600)); fs.writeFileSync(path.join(out, `${phase}-${build}.json`), JSON.stringify(res, null, 1)); };
const shotName = (...s) => path.join(out, s.join('_').replace(/[^\w.-]+/g, '_') + '.png');
const FS = 'button[aria-label="Toggle Fullscreen"],button[title="Toggle Fullscreen"]';
// Generic "get to gameplay" keyboard script: Start a few times, then walk right and jump.
async function playKeys(p) { for (let i = 0; i < 3; i++) { await p.key('Enter', 'Enter'); await sleep(1500); } await p.key('KeyX', 'x'); await sleep(1200); await p.key('ArrowRight', 'ArrowRight', 1500); await p.key('KeyX', 'x'); await sleep(500); }

const e = await launchEdge(path.join(out, `profile-${build}-${phase}`), +(A.port || 9333));
fs.appendFileSync(path.join(out, 'pids.txt'), `${e.proc.pid} ${phase} ${build}\n`);
const c = new Cdp(e.ws); await c.ready; const p = await openPage(c);
try {
  if (phase === 'boot') {
    for (const [con, list] of Object.entries(ROMS)) for (const rom of list) { if (!want(con, rom)) continue;
      p.logs.length = 0; const t0 = Date.now(); const ok = await L.load(p, base, rom); const bootMs = Date.now() - t0;
      if (!ok) { await p.shot(shotName(build, con, rom, 'fail')); log({ con, rom, boot: false, bootMs, logs: p.logs.slice(0, 5) }); continue; }
      await sleep(3000); const sz0 = await L.canvasShot(p, shotName(build, con, rom, '0title'));
      const cpu = L.cpuLoad(); const m = await L.measure(p, 5);
      await playKeys(p); await sleep(1500); const sz1 = await L.canvasShot(p, shotName(build, con, rom, '1play'));
      const m2 = await L.measure(p, 3);
      log({ con, rom, boot: true, bootMs, cpu, ...m, playFps: m2.fps, playRms: m2.rmsAvg, shotBytes: [sz0, sz1], cores: { CPU: await L.coreLabel(p, 'CPU'), PPU: await L.coreLabel(p, 'PPU'), APU: await L.coreLabel(p, 'APU') }, logs: p.logs.slice(0, 5) });
    }
  }
  if (phase === 'speed') { // the page's own frame call (FrameTick), driven in a loop and timed: independent of the animation clock
    for (const [con, list] of Object.entries(ROMS)) { if (!want(con)) continue; const rom = list[0];
      if (!await L.load(p, base, rom)) { log({ con, rom, boot: false }); continue; }
      await sleep(2000); await playKeys(p); const cpu = L.cpuLoad();
      const r = await p.eval(`(async()=>{ const ref = nesInterop._dotNetRef; if(!ref) return {err:'no _dotNetRef'}; nesInterop.stopEmulationLoop && nesInterop.stopEmulationLoop();
        try { for(let i=0;i<120;i++) await ref.invokeMethodAsync('FrameTick'); } catch(e){ return {err:String(e).slice(0,200)} }
        const t=[]; for(let i=0;i<${+(A.frames || 400)};i++){ const t0=performance.now(); await ref.invokeMethodAsync('FrameTick'); t.push(performance.now()-t0); }
        t.sort((a,b)=>a-b); const mean=t.reduce((a,b)=>a+b,0)/t.length; return {meanMs:+mean.toFixed(2), medianMs:+t[t.length>>1].toFixed(2), p95Ms:+t[Math.floor(t.length*.95)].toFixed(2), maxFps:+(1000/mean).toFixed(1)} })()`);
      log({ con, rom, build, cpu, cpuAfter: L.cpuLoad(), ...r });
    }
  }
  if (phase === 'cores') { // every core of every slot, once, on the console's first ROM; hot-switched mid-game
    for (const [con, list] of Object.entries(ROMS)) { if (!want(con)) continue; const rom = list[0];
      if (!await L.load(p, base, rom)) { log({ con, rom, boot: false }); continue; }
      await sleep(2000); await playKeys(p);
      for (const slot of await L.coreSlots(p)) {
        await L.openMenu(p, slot); const info = await L.menuInfo(p); await L.closeMenu(p);
        log({ con, slot, menu: info.heads, current: info.current, count: info.cores.length, ids: info.cores });
        const ids = A.max ? info.cores.slice(0, +A.max) : info.cores;
        for (const id of ids) { p.logs.length = 0;
          if (!await L.openMenu(p, slot)) { log({ con, slot, id, err: 'menu did not open' }); await L.load(p, base, rom); continue; }
          const clicked = await L.pickCore(p, id); await sleep(2500); await L.closeMenu(p);
          let m; try { m = await L.measure(p, 2.5); } catch (er) { m = { err: String(er).slice(0, 200) }; }
          const sz = await L.canvasShot(p, shotName(build, 'core', con, slot, id)).catch(() => 0);
          const running = !!m.badge?.some(b => /Running/.test(b));
          log({ con, slot, id, clicked, label: await L.coreLabel(p, slot).catch(() => null), fps: m.fps, p95: m.p95, rmsAvg: m.rmsAvg, ctx: m.ctx, running, err: m.err, shotBytes: sz, cpu: L.cpuLoad(), logs: p.logs.slice(0, 3) });
          if (m.err || p.logs.some(l => l.level === 'exception') || !running) { await L.load(p, base, rom); await sleep(1500); }
        }
        await L.load(p, base, rom); await sleep(1500);
      }
      const slot = (await L.coreSlots(p))[0]; await L.openMenu(p, slot); const after = await L.menuInfo(p);
      await p.click('#cm-star'); await sleep(400); const star = await p.eval(`document.getElementById('cm-star')?.textContent.trim()`); await L.closeMenu(p);
      log({ con, menuAfter: after.heads, recentFilled: after.recent, starButton: star });
    }
  }
  if (phase === 'touch') {
    const IDX = { a: 0, b: 1, select: 2, start: 3, up: 4, down: 5, left: 6, right: 7, x: 8, y: 9, l: 10, r: 11 };
    for (const [con, list] of Object.entries(ROMS)) { if (!want(con)) continue;
      for (const [orient, w, h] of [['portrait', 390, 844], ['landscape', 844, 390]]) {
        await p.s('Emulation.setDeviceMetricsOverride', { width: w, height: h, deviceScaleFactor: 2, mobile: true, screenOrientation: { type: orient === 'portrait' ? 'portraitPrimary' : 'landscapePrimary', angle: orient === 'portrait' ? 0 : 90 } });
        await p.s('Emulation.setTouchEmulationEnabled', { enabled: true, maxTouchPoints: 5 });
        if (!await L.load(p, base, list[0])) { log({ con, orient, boot: false }); continue; }
        await sleep(1500);
        await p.eval(`document.querySelector('${FS}')?.scrollIntoView({block:'center'})`); const r = await p.rect(FS);
        if (r) await p.touch([{ x: r.x + r.w / 2, y: r.y + r.h / 2 }], 60);
        await sleep(1500); await p.shot(shotName('touch', con, orient, 'layout'));
        const lay = await p.eval(`(()=>{const tc=document.getElementById('touch-controller'); const cv=document.getElementById('nes-canvas').getBoundingClientRect(); const vis=e=>{const r=e.getBoundingClientRect(), cs=getComputedStyle(e); return cs.display!=='none'&&cs.visibility!=='hidden'&&r.width>0&&r.height>0}; const C={x:cv.x,y:cv.y,w:cv.width,h:cv.height}; if(!tc) return {present:false, canvas:C}; const btns=[...tc.querySelectorAll('[data-btn],[data-act]')].filter(vis).map(e=>{const r=e.getBoundingClientRect(); return {k:e.dataset.btn||('act:'+e.dataset.act), label:e.textContent.trim(), x:Math.round(r.x),y:Math.round(r.y),w:Math.round(r.width),h:Math.round(r.height)}}); const inter=(a,b)=>!(a.x+a.w<=b.x||b.x+b.w<=a.x||a.y+a.h<=b.y||b.y+b.h<=a.y); const ab=tc.querySelector("[data-btn=a]"); const ar=ab?ab.getBoundingClientRect():null; const hit=ar?document.elementFromPoint(ar.x+ar.width/2,ar.y+ar.height/2):null; return {present:vis(tc), bound:!!tc._nesBound, hitA: hit?(hit.closest("[data-btn]")?.dataset.btn||hit.tagName+"#"+hit.id+"."+hit.className):null, layout:tc.dataset.layout, canvas:C, vw:innerWidth, vh:innerHeight, btns, overCanvas:btns.filter(b=>inter(b,C)).map(b=>b.k), offscreen:btns.filter(b=>b.y+b.h>innerHeight+1||b.x+b.w>innerWidth+1||b.x<0||b.y<0).map(b=>b.k), small:btns.filter(b=>Math.min(b.w,b.h)<40).map(b=>b.k+':'+b.w+'x'+b.h)}})()`);
        log({ con, orient, layout: lay });
        if (!lay.present) continue;
        const per = {};
        for (const b of lay.btns.filter(b => !b.k.startsWith('act:'))) {
          for (const [tag, fx, fy] of [['c', .5, .5], ['edge', .15, .15]]) {
            const pt = { x: b.x + b.w * fx, y: b.y + b.h * fy, id: 1 }; await p.s('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [pt] }); await sleep(80);
            const down = await p.eval(`(window.nesInputState||[])[${IDX[b.k]}]===true`); await p.s('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] }); await sleep(60);
            const up = await p.eval(`(window.nesInputState||[])[${IDX[b.k]}]===false`); per[b.k + ':' + tag] = down && up ? 'ok' : `down=${down} up=${up}`; } }
        const multi = {}; const dp = lay.btns.find(b => b.k === 'right'), fa = lay.btns.find(b => b.k === 'a'), upb = lay.btns.find(b => b.k === 'up'), save = lay.btns.find(b => b.k === 'act:save');
        const mid = b => ({ x: b.x + b.w / 2, y: b.y + b.h / 2 });
        if (dp && fa) {
          await p.s('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ ...mid(dp), id: 1 }, { ...mid(fa), id: 2 }] }); await sleep(80);
          multi.rightPlusA = await p.eval(`[nesInputState[7],nesInputState[0]]`); await p.s('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] }); await sleep(60);
          await p.s('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ ...mid(dp), id: 1 }] }); await sleep(50);
          await p.s('Input.dispatchTouchEvent', { type: 'touchMove', touchPoints: [{ ...mid(upb), id: 1 }] }); await sleep(80);
          multi.slideRightToUp = await p.eval(`[nesInputState[7],nesInputState[4]]`); await p.s('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] }); await sleep(60);
          // a second finger landing while the first holds a direction
          await p.s('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ ...mid(dp), id: 1 }] }); await sleep(50);
          await p.s('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ ...mid(dp), id: 1 }, { ...mid(fa), id: 2 }] }); await sleep(80);
          multi.holdRightThenA = await p.eval(`[nesInputState[7],nesInputState[0]]`); await p.s('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] }); await sleep(60);
          await p.s('Input.dispatchMouseEvent', { type: 'mousePressed', ...mid(fa), button: 'left', clickCount: 1 }); await sleep(80);
          multi.mouseA = await p.eval(`nesInputState[0]`); await p.s('Input.dispatchMouseEvent', { type: 'mouseReleased', ...mid(fa), button: 'left', clickCount: 1 });
        }
        if (dp && save) { // nesInterop.js:2053 - a touch on a system button in the same event as a pad touch
          await p.s('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ ...mid(save), id: 1 }, { ...mid(dp), id: 2 }] }); await sleep(80);
          multi.saveTouchWithRight = await p.eval(`nesInputState[7]`); await p.s('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] }); await sleep(1500);
        }
        const tap = async k => { const b = lay.btns.find(x => x.k === k); if (b) await p.touch([mid(b)], 120); };
        const sz0 = await L.canvasShot(p, shotName('touch', con, orient, '0before'));
        for (let i = 0; i < 3; i++) { await tap(con === 'sms' ? 'a' : 'start'); await sleep(1500); } await tap('a'); await sleep(1000);
        if (dp) { await p.s('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ ...mid(dp), id: 1 }] }); await sleep(1500); await p.s('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] }); }
        const sz1 = await L.canvasShot(p, shotName('touch', con, orient, '1after'));
        log({ con, orient, perButton: per, multi, shotBytes: [sz0, sz1], logs: p.logs.slice(0, 3) });
      }
      await p.s('Emulation.clearDeviceMetricsOverride'); await p.s('Emulation.setTouchEmulationEnabled', { enabled: false });
    }
  }
  if (phase === 'state') {
    for (const [con, list] of Object.entries(ROMS)) { if (!want(con)) continue; const rom = list[0];
      if (!await L.load(p, base, rom)) { log({ con, boot: false }); continue; }
      await sleep(2000); await playKeys(p); p.logs.length = 0;
      const slots = await p.eval(`[...document.querySelectorAll('select,button,input')].filter(e=>/slot/i.test((e.id||'')+(e.className||'')+(e.getAttribute('aria-label')||'')+(e.title||''))).map(e=>(e.getAttribute('aria-label')||e.textContent).trim().slice(0,30))`);
      const st0 = await L.status(p); await L.btn(p, '.btn-save-state'); await sleep(2500); const st1 = await L.status(p);
      await L.canvasShot(p, shotName('state', con, '0saved')); const idb1 = await p.eval(L.IDB);
      await p.key('ArrowRight', 'ArrowRight', 2500); await L.canvasShot(p, shotName('state', con, '1moved'));
      await L.btn(p, '.btn-load-state'); await sleep(250); const st2 = await L.status(p); await L.canvasShot(p, shotName('state', con, '2loaded'));
      let swapped = null; const sl = await L.coreSlots(p); const slot = sl.includes('APU') ? 'APU' : sl[0];
      if (slot) { await L.openMenu(p, slot); const info = await L.menuInfo(p); const alt = info.cores.find(id => id !== info.currentId && !id.includes(':')); if (alt) { await L.pickCore(p, alt); await sleep(2500); swapped = slot + ':' + alt; } await L.closeMenu(p); }
      p.logs.length = 0; await L.btn(p, '.btn-load-state'); await sleep(1500); const st3 = await L.status(p); const after = await p.eval('__uat.read()'); await L.canvasShot(p, shotName('state', con, '3loadedAfterSwap'));
      const swapLogs = p.logs.slice(0, 3);
      await L.load(p, base, rom); await sleep(1500); await L.btn(p, '.btn-load-state'); await sleep(1500); const st4 = await L.status(p); await L.canvasShot(p, shotName('state', con, '4afterReload'));
      log({ con, rom, slotUi: slots, status: { before: st0, afterSave: st1, afterLoad: st2, afterSwapLoad: st3, afterReloadLoad: st4 }, swappedTo: swapped, runningAfterSwapLoad: after.badge.some(b => /Running/.test(b)), swapLogs, stateKeys: Object.values(idb1).flat().filter(k => /state|save|manifest/i.test(k)).slice(0, 12), logs: p.logs.slice(0, 5) });
    }
  }
  if (phase === 'battery') { // VRUN CTW NES: save via pause+Select, then reload; plus two SRAM games
    const rom = ROMS.nes[0]; await L.load(p, base, rom); await sleep(2000); const k0 = await p.eval(L.IDB);
    for (let i = 0; i < 2; i++) { await p.key('Enter', 'Enter'); await sleep(2000); } await sleep(4000); await L.canvasShot(p, shotName('battery', 'ingame'));
    await p.key('Enter', 'Enter'); await sleep(800); await p.key('Space', ' '); await sleep(3000); await L.canvasShot(p, shotName('battery', 'paused_select'));
    await sleep(12000); const k1 = await p.eval(L.IDB);
    await L.load(p, base, rom); await sleep(3000); await L.canvasShot(p, shotName('battery', 'title_after_reload'));
    const diff = o => Object.fromEntries(Object.entries(o).map(([s, ks]) => [s, ks.filter(k => !(k0[s] || []).includes(k))]).filter(([, v]) => v.length));
    log({ test: 'battery-vrun', newKeys: diff(k1) });
    for (const rom2 of ['c_drmariodx.gbc', 'c_smw.smc']) { await L.load(p, base, rom2); await sleep(2000); await playKeys(p); await sleep(12000); log({ test: 'battery-sram', rom: rom2, newKeys: diff(await p.eval(L.IDB)) }); }
  }
  if (phase === 'misc') {
    const romDir = A.romdir; const rows = () => p.eval(`[...document.querySelectorAll('#rom-table tbody tr')].map(r=>r.textContent.trim().replace(/\\s+/g,' ').slice(0,60))`);
    await L.load(p, base, ROMS.nes[0]); await sleep(1500); const rows0 = await rows();
    const { root } = await p.s('DOM.getDocument', { depth: -1 }); const { nodeId } = await p.s('DOM.querySelector', { nodeId: root.nodeId, selector: '#rom-upload' });
    await p.s('DOM.setFileInputFiles', { nodeId, files: [path.join(romDir, 'VRUN_Vaults_GG_9ea616a.gg')] }); await sleep(8000);
    const afterPick = { rows: await rows(), badge: (await p.eval('__uat.read()')).badge, status: await L.status(p) }; await L.canvasShot(p, shotName('misc', 'filepicker'));
    await p.eval(`document.getElementById('rom-table')?.scrollIntoView({block:'center'})`); const r = await p.rect('#rom-table');
    let afterDrop = { err: 'no #rom-table' };
    if (r) { const pt = { x: r.x + r.w / 2, y: r.y + Math.min(r.h / 2, 40) }; const data = { items: [], files: [path.join(romDir, 'supervrun_md_338a853.bin')], dragOperationsMask: 1 };
      for (const type of ['dragEnter', 'dragOver', 'drop']) await p.s('Input.dispatchDragEvent', { type, ...pt, data }).catch(er => { afterDrop.dragErr = String(er); });
      await sleep(8000); afterDrop = { ...afterDrop, rows: await rows(), badge: (await p.eval('__uat.read()')).badge, status: await L.status(p) }; await L.canvasShot(p, shotName('misc', 'dragdrop')); }
    // the recent list / ROM list across a reload, and core-choice persistence
    await L.load(p, base, ROMS.nes[0]); await sleep(1000); await L.openMenu(p, 'APU'); const info = await L.menuInfo(p); const alt = info.cores.find(id => id !== info.currentId && !id.includes(':')); await L.pickCore(p, alt); await sleep(2000); await L.closeMenu(p);
    const apuSet = await L.coreLabel(p, 'APU');
    await p.goto(base + '/'); await sleep(10000); const apuAfter = await L.coreLabel(p, 'APU'); const rowsAfterReload = await rows(); const consoleNow = await p.eval(`document.getElementById('consoleSelect')?.value`);
    await L.load(p, base, ROMS.nes[0]); await sleep(1000); await p.eval(`document.querySelector('${FS}').scrollIntoView()`); await p.click(FS); await sleep(1500);
    const fsState = await p.eval(`({fsEl: !!document.fullscreenElement, shell: document.getElementById('screen-shell')?.className})`); await p.shot(shotName('misc', 'fullscreen'));
    log({ rows0, afterPick, afterDrop, apuPicked: alt, apuSet, apuAfterReload: apuAfter, rowsAfterReload, consoleNow, fsState, logs: p.logs.slice(0, 5) });
  }
} finally { c.close(); killTree(e.proc.pid); }
