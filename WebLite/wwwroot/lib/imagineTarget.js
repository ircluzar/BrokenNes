// imagineTarget.js - "Target the Beam" scanline-range picker overlay for Imagine.
// Pointer Events unify touch and mouse in one code path, which is why this uses pointerdown/
// move/up rather than separate touch/mouse listeners - simpler and mobile-native either way.
window.imagineTargetOverlay = (function () {
    let overlayEl = null;
    let highlightEl = null;
    let canvasEl = null;
    let dotNetRef = null;
    let dragging = false;
    let startY = 0;

    function getFractionY(e) {
        const rect = canvasEl.getBoundingClientRect();
        const clientY = typeof e.clientY === 'number' ? e.clientY : 0;
        return Math.max(0, Math.min(1, (clientY - rect.top) / rect.height));
    }

    function updateHighlight(y0, y1) {
        const rect = canvasEl.getBoundingClientRect();
        const top = Math.min(y0, y1) * rect.height;
        const h = Math.max(2, Math.abs(y1 - y0) * rect.height);
        highlightEl.style.top = top + 'px';
        highlightEl.style.height = h + 'px';
        highlightEl.style.display = 'block';
    }

    function onDown(e) {
        e.preventDefault();
        dragging = true;
        startY = getFractionY(e);
        updateHighlight(startY, startY);
    }

    function onMove(e) {
        if (!dragging) return;
        e.preventDefault();
        updateHighlight(startY, getFractionY(e));
    }

    function onUp(e) {
        if (!dragging) return;
        dragging = false;
        const endY = getFractionY(e);
        const y0 = Math.min(startY, endY);
        const y1 = Math.max(startY, endY);
        const ref = dotNetRef;
        stop();
        if (ref) {
            try { ref.invokeMethodAsync('OnImagineTargetSelected', y0, y1); } catch (err) { console.warn('imagineTargetOverlay callback failed', err); }
        }
    }

    function start(canvasId, ref) {
        canvasEl = document.getElementById(canvasId);
        if (!canvasEl) return;
        dotNetRef = ref;
        const parent = canvasEl.parentElement;
        if (parent) parent.style.position = 'relative';

        overlayEl = document.createElement('div');
        overlayEl.id = 'imagine-target-overlay';
        overlayEl.style.cssText = 'position:absolute;inset:0;z-index:50;cursor:crosshair;touch-action:none;';

        highlightEl = document.createElement('div');
        highlightEl.style.cssText = 'position:absolute;left:0;right:0;display:none;pointer-events:none;background:rgba(111,178,255,0.25);border-top:2px solid rgba(111,178,255,0.9);border-bottom:2px solid rgba(111,178,255,0.9);';
        overlayEl.appendChild(highlightEl);

        (parent || canvasEl).appendChild(overlayEl);

        overlayEl.addEventListener('pointerdown', onDown);
        overlayEl.addEventListener('pointermove', onMove);
        overlayEl.addEventListener('pointerup', onUp);
        overlayEl.addEventListener('pointercancel', stop);
    }

    function stop() {
        dragging = false;
        if (overlayEl) { overlayEl.remove(); overlayEl = null; }
        highlightEl = null;
        canvasEl = null;
        dotNetRef = null;
    }

    return { start, stop };
})();
