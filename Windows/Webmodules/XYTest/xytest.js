// XYTest Webmodule - Visualize X/Y input events

const state = {
  counts: { X: 0, Y: 0 },
  lastEvent: { X: null, Y: null },
  lastPressed: { X: false, Y: false },
  log: []
};

const elements = {
  indicatorX: null,
  indicatorY: null,
  countX: null,
  countY: null,
  lastX: null,
  lastY: null,
  logList: null,
  btnClear: null
};

const MAX_LOG_ENTRIES = 25;

document.addEventListener('DOMContentLoaded', () => {
  initializeElements();
  attachHandlers();
  startInputPolling();
  renderAll();
});

function initializeElements() {
  elements.indicatorX = document.getElementById('indicatorX');
  elements.indicatorY = document.getElementById('indicatorY');
  elements.countX = document.getElementById('countX');
  elements.countY = document.getElementById('countY');
  elements.lastX = document.getElementById('lastX');
  elements.lastY = document.getElementById('lastY');
  elements.logList = document.getElementById('logList');
  elements.btnClear = document.getElementById('btnClear');
}

function attachHandlers() {
  elements.btnClear.addEventListener('click', clearLog);
}

function startInputPolling() {
  if (window.webapi && window.webapi.input && window.webapi.input.startPolling) {
    window.webapi.input.startPolling(50);

    window.addEventListener('buttonPressed', (event) => {
      const button = event.detail.button;
      if (button === 'X' || button === 'Y') {
        handleButtonEvent(button, 'pressed');
      }
    });

    window.addEventListener('buttonReleased', (event) => {
      const button = event.detail.button;
      if (button === 'X' || button === 'Y') {
        handleButtonEvent(button, 'released');
      }
    });

    window.addEventListener('webmoduleButton', (event) => {
      const { button, pressed } = event.detail;
      if (button === 'X' || button === 'Y') {
        handleButtonEvent(button, pressed ? 'pressed' : 'released');
      }
    });
  } else {
    pushLog('Input API not available', 'system');
  }
}

function handleButtonEvent(button, action) {
  const timestamp = new Date();
  state.lastEvent[button] = timestamp;

  if (action === 'pressed') {
    state.counts[button] += 1;
    state.lastPressed[button] = true;
  } else {
    state.lastPressed[button] = false;
  }

  pushLog(`${button} ${action}`, 'event');
  renderAll();
}

function renderAll() {
  updateIndicator('X');
  updateIndicator('Y');
  updateStats('X');
  updateStats('Y');
  renderLog();
}

function updateIndicator(button) {
  const indicator = button === 'X' ? elements.indicatorX : elements.indicatorY;
  if (!indicator) return;

  const isPressed = state.lastPressed[button];
  indicator.classList.toggle('pressed', isPressed);
  indicator.classList.toggle('active', !isPressed && state.lastEvent[button] !== null);

  const label = indicator.querySelector('.xy-indicator-label');
  if (!label) return;

  if (isPressed) {
    label.textContent = 'Pressed';
  } else if (state.lastEvent[button]) {
    label.textContent = 'Released';
  } else {
    label.textContent = 'Idle';
  }
}

function updateStats(button) {
  const countEl = button === 'X' ? elements.countX : elements.countY;
  const lastEl = button === 'X' ? elements.lastX : elements.lastY;
  if (countEl) countEl.textContent = String(state.counts[button]);
  if (lastEl) lastEl.textContent = formatTimestamp(state.lastEvent[button]);
}

function renderLog() {
  if (!elements.logList) return;

  elements.logList.innerHTML = '';

  if (state.log.length === 0) {
    const empty = document.createElement('div');
    empty.className = 'xy-log-empty';
    empty.textContent = 'Waiting for input...';
    elements.logList.appendChild(empty);
    return;
  }

  state.log.forEach((entry) => {
    const item = document.createElement('div');
    item.className = 'xy-log-entry';
    item.innerHTML = `${entry.time} — <strong>${entry.message}</strong>`;
    elements.logList.appendChild(item);
  });
}

function pushLog(message, type) {
  const time = new Date();
  state.log.unshift({
    time: formatTimestamp(time),
    message: type === 'system' ? `[System] ${message}` : message
  });

  if (state.log.length > MAX_LOG_ENTRIES) {
    state.log.length = MAX_LOG_ENTRIES;
  }
}

function clearLog() {
  state.log = [];
  renderLog();
}

function formatTimestamp(date) {
  if (!date) return '—';
  return date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
}
