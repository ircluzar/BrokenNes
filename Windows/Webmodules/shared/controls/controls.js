// BrokenNes controller standard (docs/projects/controller-standard-plan.md).
// A panel is built from a LAYOUT (data: grid size, sections, controls placed on cells) and PARAMETERS (one record per value:
// range, default, unit, taper, get / set). Controls never talk to the engine; they move their parameter.
//
//   const panel = BNControls.createPanel(host, layout, params, { onChange(id, value) {} });
//   panel.refresh();            // re-read every parameter's get() (call when the engine changed something)
//   panel.flash('blast');       // blink an LED
//   panel.setText('status', 'running');   // a display bound to text instead of a parameter
//
// Components: knob (continuous; stepped when the parameter has steps), slider (vertical when h > w), button, toggle (with LED),
// display (LCD read-out of a parameter or of text), label (caption), led. All are keyboard and screen-reader usable.
(() => {
  const U = 56, GAP_X = 12, GAP_Y = 14;   // a cell and the gutters between cells (controls.css --u / --gap-x / --gap-y)
  const GAP = GAP_X;
  const span = (cells, vertical) => cells * U + (cells - 1) * (vertical ? GAP_Y : GAP_X);
  const SVGNS = 'http://www.w3.org/2000/svg';

  // ------------------------------------------------------------------ parameters
  // { id, label, short, min, max, default, step, unit, taper ('linear' | 'log' | 'exp'), steps ([{ value, label }] or [label]),
  //   bipolar, format(v), get(), set(v) }
  function param(def) {
    const p = Object.assign({ min: 0, max: 1, step: 0, unit: '', taper: 'linear', bipolar: false }, def);
    if (p.steps) {
      p.steps = p.steps.map((s, i) => (typeof s === 'object' ? s : { value: i, label: String(s) }));
      p.min = p.steps[0].value; p.max = p.steps[p.steps.length - 1].value;
    }
    if (p.default === undefined) p.default = p.min;
    let local = p.default;
    if (!p.get) p.get = () => local;
    const userSet = p.set;
    p.set = (v) => { local = v; if (userSet) userSet(v); };
    if (!p.format) p.format = (v) => {
      if (p.steps) return (p.steps.find(s => s.value === v) || p.steps[0]).label;
      if (typeof v === 'boolean') return v ? 'on' : 'off';
      const abs = Math.abs(v);
      const txt = abs >= 1000 ? Math.round(v).toString() : abs >= 100 ? v.toFixed(0) : abs >= 10 ? v.toFixed(1) : v.toFixed(2);
      return txt.replace(/\.0+$/, '').replace(/(\.\d*?)0+$/, '$1') + (p.unit ? (p.unit === '%' ? '%' : ' ' + p.unit) : '');
    };
    return p;
  }

  // value <-> 0..1 along the control's travel
  function toNorm(p, v) {
    if (p.steps) { const i = p.steps.findIndex(s => s.value === v); return p.steps.length > 1 ? Math.max(0, i) / (p.steps.length - 1) : 0; }
    const lo = p.min, hi = p.max;
    if (hi === lo) return 0;
    let n;
    if (p.taper === 'log' && lo > 0) n = Math.log(v / lo) / Math.log(hi / lo);
    else if (p.taper === 'exp') n = Math.sqrt((v - lo) / (hi - lo));
    else n = (v - lo) / (hi - lo);
    return Math.min(1, Math.max(0, n));
  }
  function fromNorm(p, n) {
    n = Math.min(1, Math.max(0, n));
    if (p.steps) return p.steps[Math.round(n * (p.steps.length - 1))].value;
    const lo = p.min, hi = p.max;
    let v;
    if (p.taper === 'log' && lo > 0) v = lo * Math.pow(hi / lo, n);
    else if (p.taper === 'exp') v = lo + (hi - lo) * n * n;
    else v = lo + (hi - lo) * n;
    if (p.step) v = Math.round(v / p.step) * p.step;
    return Math.min(hi, Math.max(lo, +v.toFixed(6)));
  }

  // ------------------------------------------------------------------ helpers
  function el(tag, attrs, ...kids) {
    const n = document.createElement(tag);
    for (const [k, v] of Object.entries(attrs || {})) {
      if (v == null || v === false) continue;
      if (k === 'class') n.className = v; else if (k === 'style') n.style.cssText = v; else if (k.startsWith('on')) n.addEventListener(k.slice(2), v); else n.setAttribute(k, v);
    }
    for (const c of kids) if (c != null) n.append(c);
    return n;
  }
  function svg(tag, attrs) { const n = document.createElementNS(SVGNS, tag); for (const [k, v] of Object.entries(attrs || {})) n.setAttribute(k, v); return n; }
  function arcPath(cx, cy, r, a0, a1) {
    const p = (a) => [cx + r * Math.cos(a), cy + r * Math.sin(a)];
    const [x0, y0] = p(a0), [x1, y1] = p(a1);
    return `M ${x0.toFixed(2)} ${y0.toFixed(2)} A ${r} ${r} 0 ${a1 - a0 > Math.PI ? 1 : 0} 1 ${x1.toFixed(2)} ${y1.toFixed(2)}`;
  }
  // knob travel: 270 degrees, from 7:30 to 4:30
  const A0 = Math.PI * 0.75, A1 = Math.PI * 2.25;

  // the shared drag / wheel / keyboard behaviour of every continuous control
  function continuous(node, ctl, axis) {
    let drag = null;
    node.addEventListener('pointerdown', (e) => {
      if (ctl.disabled || e.button !== 0) return;
      node.setPointerCapture(e.pointerId);
      drag = { x: e.clientX, y: e.clientY, n: ctl.norm() };
      node.classList.add('bnc-active');
      if (axis.jump) { ctl.setNorm(axis.jump(e)); drag.n = ctl.norm(); }
      e.preventDefault();
    });
    node.addEventListener('pointermove', (e) => {
      if (!drag) return;
      const fine = e.shiftKey ? 0.2 : 1;
      const d = axis.delta(e.clientX - drag.x, e.clientY - drag.y) * fine;
      ctl.setNorm(drag.n + d);
    });
    const end = (e) => { if (!drag) return; drag = null; node.classList.remove('bnc-active'); try { node.releasePointerCapture(e.pointerId); } catch { /* gone */ } };
    node.addEventListener('pointerup', end); node.addEventListener('pointercancel', end);
    node.addEventListener('dblclick', () => { if (!ctl.disabled) ctl.setValue(ctl.p.default); });
    node.addEventListener('wheel', (e) => {
      if (ctl.disabled) return;
      e.preventDefault();
      ctl.nudge(e.deltaY < 0 ? 1 : -1, e.shiftKey);
    }, { passive: false });
    node.addEventListener('keydown', (e) => {
      if (ctl.disabled) return;
      const k = e.key;
      if (k === 'ArrowUp' || k === 'ArrowRight') ctl.nudge(1, e.shiftKey);
      else if (k === 'ArrowDown' || k === 'ArrowLeft') ctl.nudge(-1, e.shiftKey);
      else if (k === 'PageUp') ctl.nudge(10, false);
      else if (k === 'PageDown') ctl.nudge(-10, false);
      else if (k === 'Home') ctl.setNorm(0);
      else if (k === 'End') ctl.setNorm(1);
      else if (k === 'Delete' || k === 'Backspace') ctl.setValue(ctl.p.default);
      else return;
      e.preventDefault();
    });
  }

  // a control bound to a parameter: the common part (value, norm, aria, value line)
  function bound(p, node, valueNode, onChange) {
    const ctl = {
      p, node, disabled: false,
      value: () => p.get(),
      norm: () => toNorm(p, p.get()),
      setValue(v, quiet) {
        if (v === p.get() && !quiet) { ctl.paint(); return; }
        p.set(v); ctl.paint();
        if (!quiet && onChange) onChange(p.id, v);
      },
      setNorm(n) { ctl.setValue(fromNorm(p, n)); },
      nudge(dir, fine) {
        if (p.steps) { const i = p.steps.findIndex(s => s.value === p.get()); ctl.setValue(p.steps[Math.min(p.steps.length - 1, Math.max(0, i + Math.sign(dir) * Math.max(1, Math.abs(dir) / 10 | 0)))].value); return; }
        const n = ctl.norm() + dir * (fine ? 0.002 : 0.01);
        ctl.setNorm(n);
      },
      paint() {},
      setDisabled(d) { ctl.disabled = !!d; node.classList.toggle('bnc-disabled', ctl.disabled); node.setAttribute('aria-disabled', ctl.disabled ? 'true' : 'false'); node.tabIndex = ctl.disabled ? -1 : 0; },
    };
    const base = () => {
      const v = p.get();
      node.setAttribute('aria-valuetext', p.format(v));
      if (typeof v === 'number') { node.setAttribute('aria-valuenow', String(v)); node.setAttribute('aria-valuemin', String(p.min)); node.setAttribute('aria-valuemax', String(p.max)); }
      if (valueNode) valueNode.textContent = p.format(v);
    };
    ctl.paintBase = base;
    return ctl;
  }

  // ------------------------------------------------------------------ components
  function knob(spec, p, onChange) {
    const big = (spec.w || 1) >= 2;
    const size = big ? 76 : 34, stroke = big ? 5.5 : 3.5, r = size / 2 - stroke / 2 - 1;
    const c = size / 2;
    const s = svg('svg', { width: size, height: size, viewBox: `0 0 ${size} ${size}`, 'aria-hidden': 'true' });
    s.append(svg('path', { class: 'bnc-arc-bg', d: arcPath(c, c, r, A0, A1), fill: 'none', 'stroke-width': stroke, 'stroke-linecap': 'round' }));
    const arc = svg('path', { class: 'bnc-arc', fill: 'none', 'stroke-width': stroke, 'stroke-linecap': 'round' });
    s.append(arc);
    const capR = r - stroke - (big ? 2 : 1);
    s.append(svg('circle', { class: 'bnc-capc', cx: c, cy: c, r: capR, 'stroke-width': 1 }));
    const ptr = svg('line', { class: 'bnc-ptr', 'stroke-width': big ? 2.5 : 1.8 });
    s.append(ptr);
    const ticks = [];
    if (p.steps) p.steps.forEach((_, i) => { const a = A0 + (A1 - A0) * (p.steps.length > 1 ? i / (p.steps.length - 1) : 0); const t = svg('circle', { class: 'bnc-tick', cx: c + (r + stroke) * Math.cos(a), cy: c + (r + stroke) * Math.sin(a), r: big ? 1.6 : 1.1 }); ticks.push(t); s.append(t); });
    const value = el('div', { class: 'bnc-value' });
    const node = el('div', { class: 'bnc-ctl bnc-knob', role: 'slider', tabindex: 0, 'aria-label': spec.label || p.label, title: spec.label || p.label }, s, el('div', { class: 'bnc-label' }, spec.label || (big ? p.label : (p.short || p.label))), value);
    const ctl = bound(p, node, value, onChange);
    ctl.paint = () => {
      const n = ctl.norm();
      const a = A0 + (A1 - A0) * n;
      const from = p.bipolar ? A0 + (A1 - A0) * toNorm(p, p.bipolar === true ? (p.min + p.max) / 2 : p.bipolar) : A0;
      arc.setAttribute('d', Math.abs(a - from) < 0.001 ? '' : arcPath(c, c, r, Math.min(a, from), Math.max(a, from)));
      ptr.setAttribute('x1', c + capR * 0.25 * Math.cos(a)); ptr.setAttribute('y1', c + capR * 0.25 * Math.sin(a));
      ptr.setAttribute('x2', c + capR * 0.9 * Math.cos(a)); ptr.setAttribute('y2', c + capR * 0.9 * Math.sin(a));
      if (ticks.length) { const i = p.steps.findIndex(st => st.value === p.get()); ticks.forEach((t, k) => t.classList.toggle('on', k === i)); }
      ctl.paintBase();
    };
    continuous(node, ctl, { delta: (dx, dy) => (-dy + dx * 0.25) / (p.steps ? 18 * p.steps.length : 160) });
    return ctl;
  }

  function slider(spec, p, onChange) {
    const vertical = (spec.h || 1) > (spec.w || 1);
    const len = span(vertical ? spec.h : spec.w, vertical) - (vertical ? 34 : 24);
    const track = el('div', { class: 'bnc-track', style: vertical ? `width:6px;height:${len}px` : `height:6px;width:${len}px` });
    const fill = el('div', { class: 'bnc-fill' }), thumb = el('div', { class: 'bnc-thumb' });
    track.append(fill, thumb);
    const value = el('div', { class: 'bnc-value' });
    const node = el('div', { class: 'bnc-ctl bnc-slider' + (vertical ? ' bnc-v' : ' bnc-h'), role: 'slider', tabindex: 0, 'aria-label': spec.label || p.label, 'aria-orientation': vertical ? 'vertical' : 'horizontal', title: spec.label || p.label },
      track, el('div', { class: 'bnc-label' }, spec.label || p.short || p.label), value);
    const ctl = bound(p, node, value, onChange);
    ctl.paint = () => {
      const n = ctl.norm();
      if (vertical) { fill.style.cssText = `left:0;right:0;bottom:0;height:${n * len}px`; thumb.style.cssText = `left:-6px;bottom:${n * len - 5}px`; }
      else { fill.style.cssText = `top:0;bottom:0;left:0;width:${n * len}px`; thumb.style.cssText = `top:-6px;left:${n * len - 5}px`; }
      ctl.paintBase();
    };
    const posNorm = (e) => { const b = track.getBoundingClientRect(); return vertical ? 1 - (e.clientY - b.top) / b.height : (e.clientX - b.left) / b.width; };
    continuous(node, ctl, { jump: posNorm, delta: (dx, dy) => (vertical ? -dy : dx) / len });
    return ctl;
  }

  function button(spec, onPress) {
    const node = el('div', { class: 'bnc-ctl bnc-btn' + (spec.danger ? ' bnc-danger' : ''), role: 'button', tabindex: 0, 'aria-label': spec.label, title: spec.label },
      el('div', { class: 'bnc-cap' }, spec.glyph || ''), el('div', { class: 'bnc-label' }, spec.label || ''));
    const ctl = { node, disabled: false, paint() {}, setDisabled(d) { ctl.disabled = !!d; node.classList.toggle('bnc-disabled', ctl.disabled); node.tabIndex = ctl.disabled ? -1 : 0; } };
    const press = () => { if (ctl.disabled) return; node.classList.add('bnc-active'); setTimeout(() => node.classList.remove('bnc-active'), 120); if (onPress) onPress(spec.id || spec.label); };
    node.addEventListener('click', press);
    node.addEventListener('keydown', (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); press(); } });
    return ctl;
  }

  function toggle(spec, p, onChange) {
    const led = el('div', { class: 'bnc-led' });
    const node = el('div', { class: 'bnc-ctl bnc-tog', role: 'switch', tabindex: 0, 'aria-label': spec.label || p.label, title: spec.label || p.label },
      el('div', { class: 'bnc-cap' }, led), el('div', { class: 'bnc-label' }, spec.label || p.short || p.label));
    const ctl = bound(p, node, null, onChange);
    ctl.paint = () => { const on = !!p.get(); led.classList.toggle('on', on); node.classList.toggle('bnc-active', on); node.setAttribute('aria-checked', on ? 'true' : 'false'); node.setAttribute('aria-valuetext', on ? 'on' : 'off'); };
    const flip = () => { if (!ctl.disabled) ctl.setValue(!p.get()); };
    node.addEventListener('click', flip);
    node.addEventListener('keydown', (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); flip(); } });
    return ctl;
  }

  function display(spec, p) {
    const lcd = el('div', { class: 'bnc-lcd' });
    const node = el('div', { class: 'bnc-ctl bnc-disp', role: 'status', 'aria-label': spec.label || (p && p.label) || 'display', 'aria-live': spec.live ? 'polite' : 'off' }, lcd, spec.label ? el('div', { class: 'bnc-label' }, spec.label) : null);
    let text = spec.text || '';
    const ctl = { node, p, disabled: false, paint() { lcd.textContent = p ? p.format(p.get()) : text; }, setText(t) { text = String(t); ctl.paint(); }, setDisabled(d) { node.classList.toggle('bnc-disabled', !!d); } };
    return ctl;
  }

  function led(spec, p) {
    const dot = el('div', { class: 'bnc-led' });
    const node = el('div', { class: 'bnc-ctl bnc-ledc', role: 'img', 'aria-label': spec.label || 'light' }, dot, spec.label ? el('div', { class: 'bnc-label' }, spec.label) : null);
    let timer = 0;
    const ctl = { node, p, disabled: false, paint() { if (p) dot.classList.toggle('on', !!p.get()); }, flash(ms) { dot.classList.add('on'); clearTimeout(timer); timer = setTimeout(() => { if (!p || !p.get()) dot.classList.remove('on'); }, ms || 90); }, setDisabled() {} };
    return ctl;
  }

  function caption(spec) {
    const node = el('div', { class: 'bnc-ctl bnc-caption' }, spec.text || spec.label || '');
    return { node, disabled: false, paint() {}, setDisabled() {} };
  }

  // ------------------------------------------------------------------ panels
  // layout: { title, cols, rows, accent, sections: [{ title, x, y, w, h }], controls: [{ type, param, id, x, y, w, h, label, ... }] }
  // params: an array of param() records (or plain definitions), matched by id.
  function createPanel(host, layout, params, opts = {}) {
    const byId = new Map();
    for (const d of params || []) { const p = d && d.__bnc ? d : Object.assign(param(d), { __bnc: true }); byId.set(p.id, p); }
    const plate = el('div', { class: 'bnc-plate', role: 'group', 'aria-label': layout.title || 'controller' });
    if (layout.accent) plate.style.setProperty('--accent', layout.accent);
    if (layout.title) plate.append(el('div', { class: 'bnc-title' }, layout.title));
    const grid = el('div', { class: 'bnc-grid', style: `--cols:${layout.cols || 8};grid-template-rows:repeat(${layout.rows || 4}, var(--u))` });
    plate.append(grid);
    const place = (node, s) => { node.style.gridColumn = `${(s.x || 0) + 1} / span ${s.w || 1}`; node.style.gridRow = `${(s.y || 0) + 1} / span ${s.h || 1}`; };
    for (const s of layout.sections || []) { const n = el('div', { class: 'bnc-section' }, s.title ? el('span', {}, s.title) : null); place(n, s); grid.append(n); }
    const controls = new Map();
    const onChange = (id, v) => { if (opts.onChange) opts.onChange(id, v, byId.get(id)); };
    for (const spec of layout.controls || []) {
      const p = spec.param ? byId.get(spec.param) : null;
      if (spec.param && !p) { console.warn('[BNControls] no parameter', spec.param); continue; }
      let ctl;
      switch (spec.type) {
        case 'knob': ctl = knob(spec, p, onChange); break;
        case 'slider': ctl = slider(spec, p, onChange); break;
        case 'button': ctl = button(spec, opts.onPress); break;
        case 'toggle': ctl = toggle(spec, p, onChange); break;
        case 'display': ctl = display(spec, p); break;
        case 'led': ctl = led(spec, p); break;
        case 'label': ctl = caption(spec); break;
        default: console.warn('[BNControls] unknown control', spec.type); continue;
      }
      place(ctl.node, spec);
      if (spec.disabled) ctl.setDisabled(true);
      grid.append(ctl.node);
      ctl.paint();
      controls.set(spec.id || spec.param || spec.label, ctl);
    }
    host.append(plate);
    return {
      node: plate, controls, params: byId,
      refresh() { for (const c of controls.values()) c.paint(); },
      get(id) { return byId.get(id)?.get(); },
      set(id, v) { const p = byId.get(id); if (!p) return; p.set(v); for (const c of controls.values()) if (c.p === p) c.paint(); },
      setText(id, t) { controls.get(id)?.setText?.(t); },
      flash(id, ms) { controls.get(id)?.flash?.(ms); },
      setDisabled(id, d) { controls.get(id)?.setDisabled(d); },
      destroy() { plate.remove(); },
    };
  }

  // the knob cap's gradient, once per document
  function ensureDefs() {
    if (document.getElementById('bnc-defs')) return;
    const s = svg('svg', { id: 'bnc-defs', width: 0, height: 0, style: 'position:absolute', 'aria-hidden': 'true' });
    const g = svg('radialGradient', { id: 'bnc-cap-grad', cx: '35%', cy: '30%', r: '75%' });
    g.append(svg('stop', { offset: '0%', 'stop-color': '#4a4e58' }), svg('stop', { offset: '100%', 'stop-color': '#1c1e23' }));
    s.append(g);
    (document.body || document.documentElement).append(s);
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', ensureDefs); else ensureDefs();

  window.BNControls = { createPanel, param, toNorm, fromNorm, CELL: U, GAP_X, GAP_Y, span };
})();
