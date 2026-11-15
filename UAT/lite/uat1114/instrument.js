// Injected into the TEST page only: counts presented frames and measures audio level, without changing behaviour.
(() => { if (window.__uat) return true; const ni = window.nesInterop; if (!ni) return false;
  const u = window.__uat = { frames: 0, t0: performance.now(), dts: [], last: 0, audioCalls: 0, rmsMax: 0, rmsSum: 0, rmsN: 0 };
  const lvl = b => { if (!b || !b.length) return; let s = 0, n = Math.min(b.length, 4096); for (let i = 0; i < n; i++) { const v = +b[i] || 0; s += v * v; } const r = Math.sqrt(s / n); u.audioCalls++; u.rmsSum += r; u.rmsN++; if (r > u.rmsMax) u.rmsMax = r; };
  const tick = () => { const t = performance.now(); if (u.last) { u.dts.push(t - u.last); if (u.dts.length > 600) u.dts.shift(); } u.last = t; u.frames++; };
  for (const name of ['presentFrame', 'drawFrame']) { const f = ni[name]; if (typeof f !== 'function') continue;
    ni[name] = function (...a) { if (name === 'presentFrame') { u.inPresent = true; tick(); lvl(a[2]); try { return f.apply(this, a); } finally { u.inPresent = false; } } if (!u.inPresent) tick(); return f.apply(this, a); }; }
  for (const name of ['playAudio', 'playAudioClassic']) { const f = ni[name]; if (typeof f !== 'function') continue; ni[name] = function (...a) { lvl(a[0]); return f.apply(this, a); }; }
  u.reset = () => { u.frames = 0; u.t0 = performance.now(); u.dts = []; u.audioCalls = 0; u.rmsMax = 0; u.rmsSum = 0; u.rmsN = 0; };
  u.read = () => { const el = (performance.now() - u.t0) / 1000; const d = [...u.dts].sort((a, b) => a - b);
    return { fps: +(u.frames / el).toFixed(1), p50: +(d[d.length >> 1] || 0).toFixed(2), p95: +(d[Math.floor(d.length * .95)] || 0).toFixed(2),
      audioCalls: u.audioCalls, rmsAvg: u.rmsN ? +(u.rmsSum / u.rmsN).toFixed(5) : 0, rmsMax: +u.rmsMax.toFixed(5), ctx: window.nesAudioCtx?.state || 'none',
      badge: [...document.querySelectorAll('.badge')].map(b => b.textContent.trim()).filter(t => /FPS|State|ROM|Console|SNES|NES|Game|Genesis|Master|WideMess/.test(t)).slice(0, 6) }; };
  return true; })()
