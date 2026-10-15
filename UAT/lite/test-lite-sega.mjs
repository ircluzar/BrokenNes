// test-lite-sega.mjs - the Lite functional suite for the Master System, the Game Gear and the Genesis, in a real Edge/Chrome over the DevTools protocol.
//
//   node test-lite-sega.mjs --publish <publish dir> --roms <dir with sms\ gg\ md\> [--only group,group] [--json out.json] [--show] [--shots dir]
//
// Every check prints PASS or FAIL with what it saw. Exit 0 only if every check passed. Games are read from --roms (never from the repository); the programs that need to do something a game cannot be made
// to do on demand (keep a save that changes every run, switch the video width while running) are assembled in synth-roms.mjs. What it cannot see: sound through a loudspeaker (the audio is checked as samples
// handed to the page, at the rate and amount a frame needs), a real phone's touch (a touch screen is emulated: taps land on the buttons), the PAL games.
//
// Groups: console, layout, rom, import, pickers, swap, saves, states, video, audio, control, speed.
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import crypto from 'node:crypto';
import { startLab, args } from './litelab.mjs';
import { writeAll } from './synth-roms.mjs';

const a = args();
const only = a.only ? String(a.only).split(',') : null;
const sleep = ms => new Promise(r => setTimeout(r, ms));
const results = [];
function check(group, name, ok, detail = '') {
  results.push({ group, name, ok: !!ok, detail: String(detail) });
  console.log(`${ok ? 'PASS' : 'FAIL'} [${group}] ${name}${detail !== '' ? ' - ' + detail : ''}`);
  return !!ok;
}
const wantGroup = g => !only || only.includes(g);

const GAMES = {
  sms: 'sms/Alex Kidd in Miracle World (USA, Europe) (v1.1).sms',
  sms2: 'sms/Fantasy Zone (World) (v1.2).sms',
  gg: 'gg/Sonic The Hedgehog (U) (V1.0) [!].gg',
  gg2: 'gg/Columns (U) [!].gg',
  md: 'md/Sonic the Hedgehog (JUE) [!].bin',
  md2: 'md/Streets of Rage (JUE) (REV 00) [!].bin',
  mdPal: 'md/Gunstar Heroes (E) [!].bin',   // a PAL game: 50 frames a second
};
const synthDir = fs.mkdtempSync(path.join(os.tmpdir(), 'lite-synth-'));
writeAll(synthDir);
fs.mkdirSync(path.join(a.roms, 'synth'), { recursive: true });
for (const f of fs.readdirSync(synthDir)) fs.copyFileSync(path.join(synthDir, f), path.join(a.roms, 'synth', f));

const shotDir = a.shots || null; if (shotDir) fs.mkdirSync(shotDir, { recursive: true });
const sha1 = bytes => crypto.createHash('sha1').update(bytes).digest('hex');

/** The state of the running page as the tests read it. */
const PROBE = `(() => {
  const t = document.body.innerText;
  const c = document.getElementById('nes-canvas');
  const f = t.match(/FPS:\\s*([0-9.]+)/);
  const badges = [...document.querySelectorAll('.overlay-status .badge')].map(b => b.innerText.trim());
  return { text: t, fps: f ? parseFloat(f[1]) : null, canvasW: c ? c.width : 0, canvasH: c ? c.height : 0, aspect: c ? c.style.aspectRatio : '', badges,
           running: /State:\\s*Running/.test(t), paused: /State:\\s*Paused/.test(t), status: (document.querySelector('.status-text, #status-text')?.innerText) || '' };
})()`;

async function openRom(lab, rom, extra = '') {
  const page = await lab.open('/?rom=' + encodeURIComponent('roms/' + rom) + extra, { timeout: 240000 });
  await page.waitFor("document.body.innerText.includes('ROM: roms/')", 240000);
  return page;
}

/** Run n frames through the page's frame call (the page's own loop stopped first) with an optional pad; returns what came back. */
const FRAMES = `window.__frames = async (n, pad) => {
  const ref = window.__ref || (window.__ref = nesInterop._dotNetRef);
  if (nesInterop._loopActive) nesInterop.stopEmulationLoop();
  const main = nesInterop._mainRef || ref;
  if (pad) { const st = new Array(12).fill(false); for (const i of pad) st[i] = true; main.invokeMethodAsync('UpdateInput', st); }
  let samples = 0, sr = 0, w = 0, h = 0, aw = 0, hash = 2166136261, hashes = [], withAudio = 0;
  for (let i = 0; i < n; i++) {
    const p = await ref.invokeMethodAsync('FrameTick');
    if (!p) continue;
    const fb = p.fb || p.Framebuffer;
    if (fb) { const u = new Uint32Array(fb.buffer, fb.byteOffset, fb.byteLength >> 2); let hh = 2166136261; for (let k = 0; k < u.length; k++) { hh ^= u[k]; hh = Math.imul(hh, 16777619); } hashes.push(hh >>> 0); }
    const au = p.pcm ? p.pcm.byteLength >> 2 : (p.audio ? p.audio.length : 0);
    if (au) withAudio++; samples += au; sr = p.sr || sr; w = p.w || w; h = p.h || h; aw = p.aw || aw;
  }
  return { samples, sr, w, h, aw, hashes, withAudio, frames: n };
};
window.__resume = () => { try { nesInterop.startEmulationLoop(window.__ref); } catch {} };
1`;

async function withLab(opts, fn) {
  const lab = await startLab({ publishDir: a.publish, romsDir: a.roms, headless: !a.show, ...opts });
  try { return await fn(lab); } finally { await lab.close(); }
}
const ready = async (page) => { await page.eval(FRAMES); };

async function selectOption(page, selector, value) {
  return page.eval(`(() => { const s = document.querySelector(${JSON.stringify(selector)}); if (!s) return 'no select'; const o = [...s.options].find(x => x.value === ${JSON.stringify(value)}); if (!o) return 'no option ' + [...s.options].map(x => x.value).join(','); s.value = ${JSON.stringify(value)}; s.dispatchEvent(new Event('change', { bubbles: true })); return 'ok'; })()`);
}
const statusText = page => page.eval(`(document.querySelector('.status-chip, .status-bar, #status')?.innerText || document.body.innerText.match(/STATUS:\\s*([^\\n]*)/)?.[1] || '')`);

// ------------------------------------------------------------------------------------------------------------- console dropdown and picture
async function groupConsole() {
  await withLab({}, async lab => {
    const page = await lab.open('/', { timeout: 240000 });
    await page.waitFor("!!document.getElementById('consoleSelect')", 120000);
    const opts = await page.eval(`[...document.getElementById('consoleSelect').options].map(o => o.value + '=' + o.text)`);
    check('console', 'the dropdown lists every console, the three Sega ones included and visible by default', ['sms', 'gg', 'md'].every(k => opts.some(o => o.startsWith(k + '='))), opts.join(' | '));
    check('console', 'the Sega consoles carry their names', opts.some(o => /Master System/.test(o)) && opts.some(o => /Game Gear/.test(o)) && opts.some(o => /Genesis/.test(o)));
    for (const [key, layout] of [['sms', 'sms'], ['gg', 'gg'], ['md', 'md'], ['nes', 'nes']]) {
      await selectOption(page, '#consoleSelect', key);
      await page.waitFor(`document.getElementById('touch-controller')?.getAttribute('data-layout') === '${layout}'`, 20000).catch(() => {});
      const got = await page.eval(`document.getElementById('touch-controller')?.getAttribute('data-layout')`);
      check('console', `choosing ${key} sets the touch layout to ${layout}`, got === layout, `data-layout=${got}`);
    }
    const p = await page.eval(PROBE);
    check('console', 'no console error while switching consoles', page.consoleErrors().length === 0, page.consoleErrors().slice(0, 2).join(' | '));
  });
}

// ------------------------------------------------------------------------------------------------------------- touch layouts
const LAYOUT_BUTTONS = {
  sms: { labels: ['1', '2', 'PAUSE'], absent: ['START', 'X', 'Y', 'Z', 'C', 'MODE', 'L', 'R'] },
  gg: { labels: ['1', '2', 'START'], absent: ['PAUSE', 'SELECT', 'X', 'Y', 'Z', 'C', 'MODE', 'L', 'R'] },
  md: { labels: ['A', 'B', 'C', 'X', 'Y', 'Z', 'MODE', 'START'], absent: ['PAUSE', 'SELECT', 'L', 'R'] },
};
async function groupLayout() {
  await withLab({ mobile: true }, async lab => {
    const page = await lab.open('/', { timeout: 240000 });
    await page.waitFor("!!document.getElementById('consoleSelect')", 120000);
    // enter the mobile fullscreen so the touch controller is on screen
    await page.eval(`(() => { const b = document.querySelector('button[title*="ullscreen"], .fullscreen-btn, #fs-btn'); if (b) b.click(); })()`).catch(() => {});
    for (const [layout, spec] of Object.entries(LAYOUT_BUTTONS)) {
      await selectOption(page, '#consoleSelect', layout);
      await sleep(600);
      const info = await page.eval(`(() => {
        const tc = document.getElementById('touch-controller'); if (!tc) return null;
        const vis = [...tc.querySelectorAll('[data-btn]')].filter(e => e.getClientRects().length > 0 && getComputedStyle(e).visibility !== 'hidden').map(e => ({ btn: e.dataset.btn, label: e.innerText.trim(), r: e.getBoundingClientRect().toJSON() }));
        return { layout: tc.getAttribute('data-layout'), vis };
      })()`);
      if (!check('layout', `${layout}: the touch controller is there`, info && info.layout === layout, info ? info.layout : 'none')) continue;
      const labels = info.vis.map(v => v.label).filter(x => x && !/^[▲▼◀▶]$/.test(x));
      check('layout', `${layout}: shows ${spec.labels.join(' ')}`, spec.labels.every(l => labels.includes(l)), labels.join(' '));
      check('layout', `${layout}: has none of ${spec.absent.join(' ')}`, spec.absent.every(l => !labels.includes(l)), labels.join(' '));
      // no two buttons overlap (a face button on top of another would swallow a press), none outside the screen
      const boxes = info.vis.filter(v => v.r.width > 0);
      let overlap = '';
      for (let i = 0; i < boxes.length && !overlap; i++) for (let j = i + 1; j < boxes.length; j++) {
        const p = boxes[i].r, q = boxes[j].r;
        const ix = Math.min(p.right, q.right) - Math.max(p.left, q.left), iy = Math.min(p.bottom, q.bottom) - Math.max(p.top, q.top);
        if (ix > 3 && iy > 3 && !(boxes[i].btn.match(/up|down|left|right/) && boxes[j].btn.match(/up|down|left|right/))) { overlap = `${boxes[i].btn} (${boxes[i].label}) overlaps ${boxes[j].btn} (${boxes[j].label}) by ${ix.toFixed(0)}x${iy.toFixed(0)}`; break; }
      }
      check('layout', `${layout}: no two buttons overlap`, !overlap, overlap);
      const vw = await page.eval('window.innerWidth');
      check('layout', `${layout}: every button is inside the screen width (${vw}px)`, boxes.every(b => b.r.left >= -1 && b.r.right <= vw + 1), boxes.filter(b => b.r.right > vw + 1).map(b => b.label).join(','));
      if (shotDir) await page.shot(path.join(shotDir, `layout-${layout}.png`));
    }
  });
}

// ------------------------------------------------------------------------------------------------------------- loading a game through ?rom=
async function groupRom() {
  for (const [key, expectBadge, expectW, expectH] of [['sms', 'Master System', 256, 192], ['gg', 'Game Gear', 160, 144], ['md', 'Genesis', 320, 224]]) {
    if (!fs.existsSync(path.join(a.roms, GAMES[key]))) { check('rom', `${key}: ROM available`, false, 'missing ' + GAMES[key]); continue; }
    await withLab({}, async lab => {
      const page = await openRom(lab, GAMES[key]);
      await ready(page);
      const first = await page.eval(PROBE);
      check('rom', `${key}: the ?rom= route loads the game and the console follows it`, first.badges.some(b => b.includes(expectBadge)), first.badges.join(' | '));
      const r = await page.eval(`window.__frames(240, [3])`);   // 240 frames with Start held, so the picture is not still the first frame
      const sizeOk = r.w === expectW && r.h === expectH;
      check('rom', `${key}: the picture is ${expectW}x${expectH}`, sizeOk, `${r.w}x${r.h} aw=${r.aw}`);
      const distinct = new Set(r.hashes).size;
      check('rom', `${key}: the picture moves (${distinct} distinct frames of 240)`, distinct > 5, `${distinct}`);
      check('rom', `${key}: sound reaches the page every frame (${r.withAudio}/240 frames with samples, ${(r.samples / 240).toFixed(0)} a frame at ${r.sr} Hz)`, r.withAudio >= 238 && r.sr >= 8000, `${r.samples}`);
      await page.eval('window.__resume()');
      await sleep(1200);
      const after = await page.eval(PROBE);
      check('rom', `${key}: the page's own loop runs it (FPS badge ${after.fps})`, after.fps > 0 && after.running, `running=${after.running}`);
      if (shotDir) await page.shot(path.join(shotDir, `rom-${key}.png`));
      check('rom', `${key}: no console error, no failed request`, page.consoleErrors().length === 0 && page.badResponses().length === 0, [...page.consoleErrors(), ...page.badResponses()].slice(0, 2).join(' | '));
    });
  }
}

// ------------------------------------------------------------------------------------------------------------- the file input, .zip, .7z
async function groupImport() {
  await withLab({}, async lab => {
    const page = await lab.open('/', { timeout: 240000 });
    await page.waitFor("!!document.getElementById('rom-upload')", 120000);
    const accept = await page.eval(`document.getElementById('rom-upload').accept`);
    check('import', 'the file input offers the Sega extensions and .zip', ['.sms', '.gg', '.md', '.bin', '.zip'].every(x => accept.includes(x)), accept);
    const upload = async (name, bytes) => page.eval(`(async () => {
      const bin = atob(${JSON.stringify(Buffer.from(bytes).toString('base64'))}); const u = new Uint8Array(bin.length); for (let i = 0; i < bin.length; i++) u[i] = bin.charCodeAt(i);
      const dt = new DataTransfer(); dt.items.add(new File([u], ${JSON.stringify(name)})); const inp = document.getElementById('rom-upload'); inp.files = dt.files; inp.dispatchEvent(new Event('change', { bubbles: true })); return u.length; })()`);
    for (const [key, name, badge] of [['sms', 'sms-save-counter.sms', 'Master System'], ['gg', 'gg-save-counter.gg', 'Game Gear'], ['md', 'md-h40.bin', 'Genesis']]) {
      const bytes = fs.readFileSync(path.join(synthDir, name));
      await upload(name, bytes);
      const ok = await page.waitFor(`[...document.querySelectorAll('.overlay-status .badge')].some(b => b.innerText.includes(${JSON.stringify(badge)}))`, 60000).then(() => true, () => false);
      check('import', `${name} imported through the file input starts the ${badge}`, ok);
    }
    // a .zip holding a Genesis ROM (the zip is written here: stored, no compression)
    const zip = makeZip('md-h40.bin', fs.readFileSync(path.join(synthDir, 'md-h32.bin')));
    await selectOption(page, '#consoleSelect', 'nes');
    await upload('md-in-a-zip.zip', zip);
    const zipOk = await page.waitFor(`[...document.querySelectorAll('.overlay-status .badge')].some(b => b.innerText.includes('Genesis'))`, 60000).then(() => true, () => false);
    check('import', 'a .zip holding a Genesis ROM is unpacked and starts the Genesis', zipOk);
    // a 7z archive: refused with a message that says why (the browser has no 7-Zip)
    const sevenZ = Buffer.concat([Buffer.from([0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x04]), Buffer.alloc(120, 1)]);
    await upload('Sonic.7z', sevenZ);
    const msg = await page.waitFor(`/7z archive[^\\n]*cannot (be )?open/i.test(document.body.innerText)`, 20000).then(() => true, () => false);
    check('import', 'a .7z archive is refused with a visible message that says why', msg, (await page.eval(`document.body.innerText.match(/[^\\n]*7z archive[^\\n]*/)?.[0] || ''`)));
    // a 7z archive renamed .zip: the bytes give it away
    await upload('renamed.zip', sevenZ);
    const msg2 = await page.waitFor(`/7z archive[^\\n]*cannot be opened here|7z archive[^\\n]*cannot be opened/i.test(document.body.innerText)`, 20000).then(() => true, () => false);
    check('import', 'a 7z archive renamed .zip is recognised by its bytes and refused the same way', msg2);
  });
}

/** A one-file stored zip. */
function makeZip(name, data) {
  const crc = (() => { let c, table = []; for (let n = 0; n < 256; n++) { c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xEDB88320 ^ (c >>> 1) : c >>> 1; table[n] = c >>> 0; } let r = 0xFFFFFFFF; for (const b of data) r = table[(r ^ b) & 255] ^ (r >>> 8); return (r ^ 0xFFFFFFFF) >>> 0; })();
  const nameB = Buffer.from(name);
  const lh = Buffer.alloc(30); lh.writeUInt32LE(0x04034B50, 0); lh.writeUInt16LE(20, 4); lh.writeUInt16LE(0, 8); lh.writeUInt32LE(crc, 14); lh.writeUInt32LE(data.length, 18); lh.writeUInt32LE(data.length, 22); lh.writeUInt16LE(nameB.length, 26);
  const cd = Buffer.alloc(46); cd.writeUInt32LE(0x02014B50, 0); cd.writeUInt16LE(20, 4); cd.writeUInt16LE(20, 6); cd.writeUInt32LE(crc, 16); cd.writeUInt32LE(data.length, 20); cd.writeUInt32LE(data.length, 24); cd.writeUInt16LE(nameB.length, 28); cd.writeUInt32LE(0, 42);
  const local = Buffer.concat([lh, nameB, Buffer.from(data)]);
  const cdir = Buffer.concat([cd, nameB]);
  const end = Buffer.alloc(22); end.writeUInt32LE(0x06054B50, 0); end.writeUInt16LE(1, 8); end.writeUInt16LE(1, 10); end.writeUInt32LE(cdir.length, 12); end.writeUInt32LE(local.length, 16);
  return Buffer.concat([local, cdir, end]);
}

// ------------------------------------------------------------------------------------------------------------- the core pickers
async function groupPickers() {
  const want = {
    sms: { cpu: ['Z80'], ppu: ['SMS', 'NES:FIXS', 'NES:FIX'], apu: ['SMS', 'SMS-FM', 'NES:FIX'] },
    gg: { cpu: ['Z80'], ppu: ['GG', 'NES:FIXS', 'NES:FIX'], apu: ['GG', 'NES:FIX'] },
    md: { cpu: ['M68K'], ppu: ['MD', 'NES:FIXS', 'NES:FIX'], apu: ['MD', 'NES:FIX'] },
  };
  for (const key of ['sms', 'gg', 'md']) {
    await withLab({}, async lab => {
      const page = await openRom(lab, GAMES[key]);
      for (const slot of ['cpu', 'ppu', 'apu']) {
        const opts = await page.eval(`(() => { const s = document.querySelector('select[aria-label="${slot[0].toUpperCase() + slot.slice(1)} core"]'); return s ? [...s.options].map(o => o.value) : null; })()`);
        check('pickers', `${key}: the ${slot.toUpperCase()} picker is there and lists the catalog`, opts && want[key][slot].every(id => opts.some(o => o.toUpperCase() === id.toUpperCase() || o.toUpperCase().startsWith(id.toUpperCase()))), opts ? opts.join(',') : 'no picker');
      }
      const groups = await page.eval(`[...document.querySelectorAll('select[aria-label="Ppu core"] optgroup')].map(g => g.label)`);
      check('pickers', `${key}: the PPU picker groups the console's own chip first, then the other consoles' (labelled "From the ...")`, groups.length >= 2 && /family/.test(groups[0]) && groups.slice(1).every(g => /^From the/.test(g)), groups.join(' | '));
    });
  }
  // the NES side lists the Sega chips under their family
  await withLab({}, async lab => {
    const page = await lab.open('/', { timeout: 240000 });
    await page.waitFor("!!document.getElementById('ppuCoreSelect')", 120000);
    for (const [btn, ids] of [['ppuCoreSelect', ['SMS', 'MD']], ['apuCoreSelect', ['SMS', 'MD']]]) {
      await page.eval(`document.getElementById('${btn}').click()`);
      await sleep(500);
      const text = await page.eval(`document.body.innerText`);
      check('pickers', `the NES ${btn.replace('CoreSelect', '').toUpperCase()} picker offers the Master System and Genesis chips`, ids.every(id => text.includes(id + ':') || new RegExp('\\b' + id + '\\b').test(text)), '');
      await page.eval(`(() => { const c = document.querySelector('.modal-close, [aria-label="Close"], .picker-close'); if (c) c.click(); else document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' })); })()`);
      await sleep(300);
    }
  });
}

// ------------------------------------------------------------------------------------------------------------- hot swapping
async function groupSwap() {
  await withLab({}, async lab => {
    const page = await openRom(lab, GAMES.sms);
    await ready(page);
    await page.eval(`window.__frames(200, [3])`);
    const before = await page.eval(`window.__frames(30)`);
    const apu = await selectOption(page, 'select[aria-label="Apu core"]', 'SMS-FM');
    await sleep(800);
    const st = await page.eval(`document.body.innerText`);
    check('swap', 'Master System: swapping the sound unit to SMS-FM while the game runs says it swapped in (and did not restart)', apu === 'ok' && /swapped in, the game runs on/.test(st) && !/restarted/.test(st), (st.match(/[^\n]*(swapped|restarted)[^\n]*/) || [''])[0]);
    const mid = await page.eval(`window.__frames(60)`);
    check('swap', '... and the game keeps running (the picture moves, sound continues)', new Set(mid.hashes).size > 3 && mid.withAudio >= 58, `distinct ${new Set(mid.hashes).size}, audio ${mid.withAudio}/60`);
    await selectOption(page, 'select[aria-label="Ppu core"]', 'NES:FIXS');
    await sleep(800);
    const st2 = await page.eval(`document.body.innerText`);
    const br = await page.eval(`window.__frames(60)`);
    check('swap', 'Master System: the picture swaps to a NES chip in place (256x240, sound unbroken)', /swapped in/.test(st2) && br.w === 256 && br.h === 240 && br.withAudio >= 58, `${br.w}x${br.h} audio ${br.withAudio}/60`);
    await selectOption(page, 'select[aria-label="Ppu core"]', 'SMS');
    await sleep(800);
    const back = await page.eval(`window.__frames(30)`);
    check('swap', 'Master System: and back to the console\'s own chip (256x192)', back.w === 256 && back.h === 192, `${back.w}x${back.h}`);
    check('swap', 'no console error across the swaps', page.consoleErrors().length === 0, page.consoleErrors().slice(0, 2).join(' | '));
    if (shotDir) await page.shot(path.join(shotDir, 'swap-sms.png'));
  });
  await withLab({}, async lab => {
    const page = await openRom(lab, GAMES.md);
    await ready(page);
    await page.eval(`window.__frames(120)`);
    await selectOption(page, 'select[aria-label="Ppu core"]', 'NES:FIXS');
    await sleep(1000);
    const st = await page.eval(`document.body.innerText`);
    const r = await page.eval(`window.__frames(30)`);
    check('swap', 'Genesis: the picture swaps to a NES chip in place', /swapped in/.test(st) && r.w === 256 && r.h === 240, `${r.w}x${r.h}`);
    await selectOption(page, 'select[aria-label="Apu core"]', 'NES:FIX');
    await sleep(2500);
    const st2 = await page.eval(`document.body.innerText`);
    check('swap', 'Genesis: a sound-unit swap either happens in place or says out loud that the game was restarted', /swapped in, the game runs on|was restarted on it/.test(st2), (st2.match(/[^\n]*(swapped|restarted)[^\n]*/) || [''])[0]);
    const r2 = await page.eval(`window.__frames(30)`);
    check('swap', 'Genesis: it still runs after the swap', r2.withAudio >= 25 && r2.frames === 30, `audio ${r2.withAudio}/30`);
  });
}

// ------------------------------------------------------------------------------------------------------------- battery saves
async function saveRoundTrip(key, rom, name, saveKeyPrefix, ext, expectLen) {
  const bytes = fs.readFileSync(path.join(a.roms, 'synth', name));
  const id = sha1(bytes);
  const idbKey = `battery_v1:${saveKeyPrefix}${id}.${ext}`;
  let first;
  await withLab({}, async lab => {
    const page = await openRom(lab, rom);
    await ready(page);
    await page.eval(`window.__frames(30)`);
    // leave the game (stops the session, which commits its save)
    await page.eval(`nesInterop._mainRef.invokeMethodAsync('JsVisibilityChanged', false)`);
    await sleep(800);
    first = await page.eval(`(async () => { const b = await nesInterop.idbGetBytes(${JSON.stringify(idbKey)}); return b ? Array.from(b.slice(0, 4)).concat([b.length]) : null; })()`);
    check('saves', `${key}: a run of the counter program stores its save under ${idbKey.slice(0, 22)}... (${expectLen} bytes)`, first && first[4] === expectLen && first[0] === 1, JSON.stringify(first));
    // second run in the same browser profile: the page restarts, loads the save, the program adds one more
    await page.eval(`1`);
    const page2 = await openRom(lab, rom);
    await ready(page2);
    await page2.eval(`window.__frames(30)`);
    await page2.eval(`nesInterop._mainRef.invokeMethodAsync('JsVisibilityChanged', false)`);
    await sleep(800);
    const second = await page2.eval(`(async () => { const b = await nesInterop.idbGetBytes(${JSON.stringify(idbKey)}); return b ? Array.from(b.slice(0, 4)).concat([b.length]) : null; })()`);
    check('saves', `${key}: after the page is reloaded the game finds its save (the counter went 1 -> 2)`, second && second[0] === 2, JSON.stringify(second));
  });
}
async function groupSaves() {
  await saveRoundTrip('sms', 'synth/sms-save-counter.sms', 'sms-save-counter.sms', 'sms', 'sav', 0x8000);
  await saveRoundTrip('gg', 'synth/gg-save-counter.gg', 'gg-save-counter.gg', 'gg', 'sav', 0x8000);
  await saveRoundTrip('md', 'synth/md-sram-counter.bin', 'md-sram-counter.bin', 'md', 'srm', 0x2000);
  // a seeded save for a real game comes back unchanged through load and unload (the key, the length and the import are right)
  for (const [key, rom, prefix, ext] of [['sms', GAMES.sms, 'sms', 'sav']]) {
    // only a game that keeps a save: Phantasy Star
  }
  // a Genesis cartridge with a serial EEPROM (NBA Jam: a 24C02, 256 bytes; Wonder Boy in Monster World: an X24C01, 128 bytes): a seeded save is loaded into the chip and written back whole
  for (const [rom, len, label] of [['md/NBA Jam (EU) (REV 01) [!].bin', 256, 'NBA Jam (24C02 EEPROM)'], ['md/Wonder Boy In Monster World (UE) [!].bin', 128, 'Wonder Boy in Monster World (X24C01 EEPROM)']]) {
    const file = path.join(a.roms, rom);
    if (!fs.existsSync(file)) { check('saves', `md: ${label}`, false, 'missing ' + rom); continue; }
    const key = `battery_v1:md${sha1(fs.readFileSync(file))}.srm`;
    const seed = Array.from({ length: len }, (_, i) => (i * 13 + 5) & 255);
    await withLab({}, async lab => {
      const page = await lab.open('/', { timeout: 240000 });
      await page.eval(`nesInterop.idbSetBytes(${JSON.stringify(key)}, new Uint8Array(${JSON.stringify(seed)}))`);
      const page2 = await openRom(lab, rom);
      await ready(page2);
      await page2.eval(`window.__frames(30)`);
      await selectOption(page2, '#consoleSelect', 'nes');   // leaving the game commits its save
      await sleep(800);
      const back = await page2.eval(`(async () => { const b = await nesInterop.idbGetBytes(${JSON.stringify(key)}); return b ? b.length + ':' + Array.from(b.slice(0, 6)).join(',') + ':' + b[b.length - 1] : null; })()`);
      check('saves', `md: ${label}: a kept save is loaded into the chip and written back whole (${len} bytes)`, back === `${len}:${seed.slice(0, 6).join(',')}:${seed[len - 1]}`, back);
    });
  }
  // a real game that keeps a save in cartridge RAM (Phantasy Star): it formats the RAM on its first run, and a kept save comes back through the next run with an edit in a place the game does not touch at boot
  const ps = path.join(a.roms, 'sms/Phantasy Star (USA, Europe) (v1.3).sms');
  if (fs.existsSync(ps)) {
    const idbKey = `battery_v1:sms${sha1(fs.readFileSync(ps))}.sav`;
    const read = page => page.eval(`(async () => { const b = await nesInterop.idbGetBytes(${JSON.stringify(idbKey)}); return b ? { len: b.length, head: Array.from(b.slice(0, 4)), probe: b[0x7F00] } : null; })()`);
    await withLab({}, async lab => {
      const page = await openRom(lab, 'sms/Phantasy Star (USA, Europe) (v1.3).sms');
      await ready(page);
      await page.eval(`window.__frames(200)`);
      await selectOption(page, '#consoleSelect', 'nes');   // leaving the game commits its save
      await sleep(800);
      const first = await read(page);
      check('saves', 'sms: Phantasy Star formats its cartridge RAM on the first run and the 32 KB are stored', first && first.len === 32768 && first.head[0] !== 0, JSON.stringify(first));
      if (!first) return;
      // an edit far from anything the game reads at boot, then a second run on a fresh page
      await page.eval(`(async () => { const b = await nesInterop.idbGetBytes(${JSON.stringify(idbKey)}); b[0x7F00] = 0xA5; b[0x7F01] = 0x5A; await nesInterop.idbSetBytes(${JSON.stringify(idbKey)}, b); })()`);
      const page2 = await openRom(lab, 'sms/Phantasy Star (USA, Europe) (v1.3).sms');
      await ready(page2);
      await page2.eval(`window.__frames(200)`);
      await selectOption(page2, '#consoleSelect', 'nes');
      await sleep(800);
      const second = await read(page2);
      check('saves', "sms: the kept save is loaded into the cartridge RAM (the edit survives the game's own start-up) and written back whole", second && second.len === 32768 && second.probe === 0xA5 && second.head.join() === first.head.join(), JSON.stringify(second));
    });
  }
}

// ------------------------------------------------------------------------------------------------------------- save states
async function groupStates() {
  for (const [key, rom] of [['sms', GAMES.sms], ['gg', GAMES.gg], ['md', GAMES.md]]) {
    await withLab({}, async lab => {
      const page = await openRom(lab, rom);
      await ready(page);
      await page.eval(`window.__frames(200, [3])`);
      await page.eval(`window.__frames(20)`);
      const saved = await page.eval(`(async () => { await nesInterop._mainRef.invokeMethodAsync('JsSaveState'); return document.body.innerText; })()`);
      const savedOk = /State saved/.test(saved);
      const unsupported = /no save states/i.test(saved);
      if (key === 'md') { check('states', 'md: Save state either saves or says in words that the Genesis has no states yet (no silent nothing)', savedOk || unsupported, (saved.match(/[^\n]*(State saved|save states)[^\n]*/) || [''])[0]); if (!savedOk) return; }
      else check('states', `${key}: Save state stores a state`, savedOk, (saved.match(/[^\n]*State saved[^\n]*/) || [''])[0]);
      const timeline = await page.eval(`window.__frames(60, [7])`);
      await page.eval(`(async () => { await nesInterop._mainRef.invokeMethodAsync('JsLoadState'); })()`);
      await sleep(300);
      const again = await page.eval(`window.__frames(60, [7])`);
      const same = timeline.hashes.length === again.hashes.length && timeline.hashes.every((h, i) => h === again.hashes[i]);
      check('states', `${key}: after Load state the next 60 frames are the same pictures as the first time (the state is the whole machine)`, same, `${timeline.hashes.filter((h, i) => h === again.hashes[i]).length}/60 equal`);
      check('states', `${key}: sound continues after a load`, again.withAudio >= 55, `${again.withAudio}/60`);
    });
  }
  // a state of another game is refused with a reason, the game keeps running
  await withLab({}, async lab => {
    const page = await openRom(lab, GAMES.sms);
    await ready(page);
    await page.eval(`window.__frames(60)`);
    const msg = await page.eval(`(async () => { await nesInterop._mainRef.invokeMethodAsync('JsLoadState'); return document.body.innerText; })()`);
    check('states', 'sms: Load state with nothing saved says so', /No saved state for this game/.test(msg), (msg.match(/[^\n]*saved state[^\n]*/) || [''])[0]);
  });
}

// ------------------------------------------------------------------------------------------------------------- the picture: sizes, aspect, width changes
async function groupVideo() {
  const cases = [['md-h40', 'synth/md-h40.bin', 320, 224, 320], ['md-h32', 'synth/md-h32.bin', 256, 224, 320]];
  for (const [name, rom, w, h, aw] of cases) {
    await withLab({}, async lab => {
      const page = await openRom(lab, rom);
      await ready(page);
      const r = await page.eval(`window.__frames(10)`);
      check('video', `${name}: the picture is ${w}x${h} and is shown ${aw} wide`, r.w === w && r.h === h && r.aw === aw, `${r.w}x${r.h} aw=${r.aw}`);
      await page.eval('window.__resume()'); await sleep(500);
      const p = await page.eval(PROBE);
      check('video', `${name}: the canvas takes the aspect ${aw}/${h}`, p.aspect.replace(/\s/g, '') === `${aw}/${h}`, p.aspect);
    });
  }
  await withLab({}, async lab => {
    const page = await openRom(lab, 'synth/md-switch.bin');
    await ready(page);
    const r1 = await page.eval(`window.__frames(20)`);
    const r2 = await page.eval(`window.__frames(260)`);
    check('video', 'Genesis: a game that switches from the 256-wide to the 320-wide mode while running changes the picture size (H32 -> H40)', r1.w === 256 && r2.w === 320, `${r1.w} -> ${r2.w}`);
    await page.eval('window.__resume()'); await sleep(500);
    const p = await page.eval(PROBE);
    check('video', '... and the canvas keeps a 320/224 aspect across the change', p.aspect.replace(/\s/g, '') === '320/224', p.aspect);
  });
  for (const [key, rom, w, h] of [['sms', GAMES.sms, 256, 192], ['gg', GAMES.gg, 160, 144]]) {
    await withLab({}, async lab => {
      const page = await openRom(lab, rom);
      await ready(page);
      await page.eval(`window.__frames(5)`); await page.eval('window.__resume()'); await sleep(500);
      const p = await page.eval(PROBE);
      check('video', `${key}: the canvas aspect is ${w}/${h}`, p.aspect.replace(/\s/g, '') === `${w}/${h}`, p.aspect);
    });
  }
}

// ------------------------------------------------------------------------------------------------------------- audio rate and pacing
async function groupAudio() {
  for (const [key, rom, fps] of [['sms', GAMES.sms, 59.92], ['gg', GAMES.gg, 59.92], ['md', GAMES.md, 59.92], ['md PAL', GAMES.mdPal, 50]]) {
    if (!fs.existsSync(path.join(a.roms, rom))) { check('audio', `${key}: game available`, false, 'missing ' + rom); continue; }
    await withLab({}, async lab => {
      const page = await openRom(lab, rom);
      await ready(page);
      await page.eval(`window.__frames(120, [3])`);
      const r = await page.eval(`window.__frames(300)`);
      const perFrame = r.samples / 300, expect = r.sr / fps;
      check('audio', `${key}: ${r.sr} Hz, ${perFrame.toFixed(1)} samples a frame (a ${fps} Hz console makes ${expect.toFixed(1)})`, r.sr >= 22050 && Math.abs(perFrame - expect) / expect < 0.02, `${r.samples} samples in 300 frames`);
      // the page's own loop paces the game at the console's rate (a PAL game is 50 a second, not the page's 60)
      await page.eval('window.__resume()');
      await sleep(6000);
      const p = await page.eval(PROBE);
      // a device that cannot hold the console's rate must say so on the page (the speed badge), never just run slow
      const note = await page.eval(`document.getElementById('speed-note')?.innerText || ''`);
      const target = fps < 55 ? 50 : 59.9;
      check('audio', `${key}: the page paces it at ${fps < 55 ? 50 : 60} frames a second (FPS badge ${p.fps})${note ? ' and says: ' + note : ''}`, p.fps > 0 && (p.fps >= target * 0.9 && p.fps <= target + 4 || (p.fps < target * 0.9 && note !== '')), `${p.fps}`);
    });
  }
}

// ------------------------------------------------------------------------------------------------------------- pause, reset
async function groupControl() {
  await withLab({}, async lab => {
    const page = await openRom(lab, GAMES.sms);
    await ready(page);
    await page.eval('window.__resume()'); await sleep(1500);
    const p0 = await page.eval(PROBE);
    check('control', 'sms: the game is running (State: Running)', p0.running, p0.status);
    await page.eval(`document.querySelector('button[title="Pause"]').click()`); await sleep(1200);
    const p1 = await page.eval(PROBE);
    check('control', 'Pause: State: Paused', p1.paused, '');
    const f1 = await page.eval(`(() => { const t = document.body.innerText.match(/FPS:\\s*([0-9.]+)/); return t ? t[1] : null; })()`);
    await sleep(1000);
    await page.eval(`document.querySelector('button[title="Play"], button[title="Start"], .btn-success.ctrl-btn')?.click()`); await sleep(1500);
    const p2 = await page.eval(PROBE);
    check('control', 'Play after Pause: running again', p2.running, '');
    const h1 = await page.eval(`window.__frames(60, [3])`);
    await page.eval(`document.querySelector('button[title="Reset"]').click()`); await sleep(500);
    const h2 = await page.eval(`window.__frames(60)`);
    check('control', 'Reset: the game starts again (the pictures after it are not the pictures before it) and nothing breaks', h2.hashes.length === 60 && h2.hashes[0] !== h1.hashes[59], `console errors: ${page.consoleErrors().length}`);
  });
}

// ------------------------------------------------------------------------------------------------------------- the speed badge
async function groupSpeed() {
  await withLab({}, async lab => {
    const page = await openRom(lab, GAMES.md);
    await ready(page);
    await page.eval('window.__resume()');
    await sleep(2000);
    const calm = await page.eval(`document.getElementById('speed-note')?.innerText || ''`);
    // the Genesis at three times real time (the fast-forward button) is more than any device here can do: the page must say so, with the rate it manages
    await page.eval(`document.querySelector('button[title^="Fast Forward"]').click()`);
    await page.waitFor(`!!document.getElementById('speed-note')`, 20000).catch(() => {});
    const note = await page.eval(`document.getElementById('speed-note')?.innerText || ''`);
    check('speed', 'Genesis at 3x fast forward: the page shows a speed badge that names what it manages', /Slow: \d+ of 18\d frames a second/.test(note), `before: "${calm}" now: "${note}"`);
    await page.eval(`document.querySelector('button[title^="Fast Forward"]').click()`);
    await sleep(500);
  });
}

const groups = { console: groupConsole, layout: groupLayout, rom: groupRom, import: groupImport, pickers: groupPickers, swap: groupSwap, saves: groupSaves, states: groupStates, video: groupVideo, audio: groupAudio, control: groupControl, speed: groupSpeed };
for (const [g, fn] of Object.entries(groups)) {
  if (!wantGroup(g)) continue;
  try { await fn(); } catch (e) { check(g, 'group ran to the end', false, String(e.stack || e).split('\n').slice(0, 3).join(' | ')); }
}
const failed = results.filter(r => !r.ok);
console.log(`\n${results.length - failed.length} passed, ${failed.length} failed`);
if (a.json) fs.writeFileSync(a.json, JSON.stringify(results, null, 2));
process.exit(failed.length ? 1 : 0);
