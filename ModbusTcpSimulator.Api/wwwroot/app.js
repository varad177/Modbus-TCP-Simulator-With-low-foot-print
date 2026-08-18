// ════════════════════════════════════════════════════════════
// Modbus TCP Simulator — Frontend Application
// Vanilla JS | WebSocket-driven live updates | No polling
// ════════════════════════════════════════════════════════════

const API = '';  // Same origin
let regMode = 'single'; // 'single' | 'range'

// Data type register sizes (word count)
const DT_SIZE = { UInt16:1, Int16:1, Bool:1, UInt32:2, Int32:2, Float32:2, UInt64:4, Int64:4, Float64:4 };
// Data types that should display as integers
const DT_INT = new Set(['UInt16','Int16','UInt32','Int32','UInt64','Int64','Bool']);
// Boolean register types
const BOOL_REG_TYPES = new Set(['Coil', 'DiscreteInput']);

let ws = null;
let wsReconnectTimer = null;
let wsReconnectDelay = 1000;

// In-memory live register table
const liveRegisters = new Map();
const registerHistory = new Map(); // key -> array of values (up to 40)
const activeCharts = new Set();    // set of register keys currently expanded
let autoTriggerOnSave = false;      // trigger quick-inject anomaly on save
let units = [];
let allRegisters = [];
let anomalies = [];
let serverStatus = {};
const collapsedUnits = new Set();

// ────────────────────────────────────────────────────────────
// Navigation
// ────────────────────────────────────────────────────────────
function navigate(btn) {
  document.querySelectorAll('.nav-item').forEach(b => b.classList.remove('active'));
  document.querySelectorAll('.screen').forEach(s => s.classList.remove('active'));
  btn.classList.add('active');
  const screenId = 'screen-' + btn.dataset.screen;
  document.getElementById(screenId).classList.add('active');

  // Refresh data for the selected screen
  if (btn.dataset.screen === 'dashboard') refreshDashboard();
  if (btn.dataset.screen === 'livevalues') { loadAnomalies().then(() => updateAnomalyCellsInPlace()); }
  if (btn.dataset.screen === 'anomalies') loadAnomalies();
}

// ────────────────────────────────────────────────────────────
// Theme
// ────────────────────────────────────────────────────────────
function toggleTheme() {
  document.documentElement.classList.toggle('dark');
  const isDark = document.documentElement.classList.contains('dark');
  localStorage.setItem('theme', isDark ? 'dark' : 'light');
  document.getElementById('theme-toggle').textContent = isDark ? '☀️' : '🌙';
}
(function initTheme() {
  const saved = localStorage.getItem('theme');
  const prefersDark = window.matchMedia('(prefers-color-scheme: dark)').matches;
  if (saved === 'dark' || (!saved && prefersDark)) {
    document.documentElement.classList.add('dark');
    setTimeout(() => { document.getElementById('theme-toggle').textContent = '☀️'; }, 0);
  }
})();

// ────────────────────────────────────────────────────────────
// Modals
// ────────────────────────────────────────────────────────────
function openModal(id) {
  document.getElementById(id).classList.add('open');
}
function closeModal(id) {
  document.getElementById(id).classList.remove('open');
}
// Close on backdrop click
document.querySelectorAll('.modal-backdrop').forEach(m => {
  m.addEventListener('click', e => { if (e.target === m) closeModal(m.id); });
});
// Close on Escape key
document.addEventListener('keydown', e => {
  if (e.key === 'Escape') {
    document.querySelectorAll('.modal-backdrop.open').forEach(m => closeModal(m.id));
  }
});

// ────────────────────────────────────────────────────────────
// Confirm dialog (replaces browser confirm())
// ────────────────────────────────────────────────────────────
let _confirmResolve = null;

function confirmAction(title, message, btnLabel) {
  return new Promise(resolve => {
    _confirmResolve = resolve;
    document.getElementById('confirm-title').textContent = title;
    document.getElementById('confirm-message').textContent = message;
    document.getElementById('confirm-ok-btn').textContent = btnLabel || 'Delete';
    openModal('modal-confirm');
  });
}
function confirmOk() {
  closeModal('modal-confirm');
  if (_confirmResolve) { _confirmResolve(true); _confirmResolve = null; }
}
function confirmCancel() {
  closeModal('modal-confirm');
  if (_confirmResolve) { _confirmResolve(false); _confirmResolve = null; }
}

// ────────────────────────────────────────────────────────────
// Toast notifications
// ────────────────────────────────────────────────────────────
function toast(message, type = 'info') {
  const c = document.getElementById('toast-container');
  const t = document.createElement('div');
  t.className = `toast ${type}`;
  const icons = { success: '✓', error: '✕', info: 'ℹ', warning: '⚠' };
  t.innerHTML = `<span>${icons[type] || 'ℹ'}</span><span>${message}</span>`;
  if (type === 'error') {
    const btn = document.createElement('button');
    btn.textContent = '✕';
    btn.className = 'toast-close';
    btn.onclick = () => t.remove();
    t.appendChild(btn);
  } else {
    setTimeout(() => t.remove(), 3500);
  }
  c.appendChild(t);
}

// ────────────────────────────────────────────────────────────
// Copy helpers
// ────────────────────────────────────────────────────────────
function copyToClipboard(text) {
  if (navigator.clipboard && navigator.clipboard.writeText) {
    return navigator.clipboard.writeText(text);
  }
  // Fallback
  const ta = document.createElement('textarea');
  ta.value = text;
  document.body.appendChild(ta);
  ta.select();
  document.execCommand('copy');
  ta.remove();
  return Promise.resolve();
}

function copyText(el, text) {
  copyToClipboard(text).then(() => {
    toast('Copied: ' + text, 'success');
  });
}

function copyConnectionInfo() {
  const display = document.getElementById('conn-display').textContent;
  copyToClipboard(display).then(() => {
    toast('Copied Modbus address: ' + display, 'success');
    const bar = document.getElementById('connection-bar');
    bar.style.borderColor = 'var(--c-emerald)';
    setTimeout(() => bar.style.borderColor = '', 800);
  });
}

// ────────────────────────────────────────────────────────────
// REST helpers
// ────────────────────────────────────────────────────────────
async function api(method, path, body = null) {
  const opts = {
    method,
    headers: { 'Content-Type': 'application/json' }
  };
  if (body) opts.body = JSON.stringify(body);
  const res = await fetch(API + path, opts);
  if (!res.ok) {
    let msg = `HTTP ${res.status}`;
    try { const j = await res.json(); msg = j.error || JSON.stringify(j); } catch {}
    throw new Error(msg);
  }
  if (res.status === 204) return null;
  const text = await res.text();
  return text ? JSON.parse(text) : null;
}

// ────────────────────────────────────────────────────────────
// WebSocket
// ────────────────────────────────────────────────────────────
function connectWebSocket() {
  const proto = location.protocol === 'https:' ? 'wss' : 'ws';
  const url = `${proto}://${location.host}/ws`;

  ws = new WebSocket(url);

  ws.onopen = () => {
    setWsStatus(true);
    wsReconnectDelay = 1000;
    clearTimeout(wsReconnectTimer);
  };

  ws.onmessage = (e) => {
    try {
      const updates = JSON.parse(e.data);
      processWsUpdate(updates);
    } catch {}
  };

  ws.onclose = () => {
    setWsStatus(false);
    // Exponential backoff
    wsReconnectTimer = setTimeout(connectWebSocket, wsReconnectDelay);
    wsReconnectDelay = Math.min(wsReconnectDelay * 1.5, 10000);
  };

  ws.onerror = () => {
    setWsStatus(false, true);
  };
}

function setWsStatus(connected, error = false) {
  const dot = document.getElementById('ws-dot');
  const label = document.getElementById('ws-label');
  const liveDot = document.getElementById('live-ws-dot');
  const liveLabel = document.getElementById('live-ws-label');

  if (dot) dot.className = 'ws-dot' + (connected ? ' connected' : error ? ' error' : '');
  if (label) label.textContent = connected ? 'Live' : error ? 'Error' : 'Reconnecting…';

  if (liveDot) {
    liveDot.className = 'ws-dot' + (connected ? ' connected' : '');
    liveDot.style.cssText = 'width:8px;height:8px';
  }
  if (liveLabel) liveLabel.textContent = connected ? 'Live' : 'Reconnecting…';
}

function processWsUpdate(updates) {
  const activeAnomalyKeys = new Set();
  anomalies.forEach(a => {
    if (a.isActive) {
      for (let addr = a.startAddress; addr <= a.endAddress; addr++)
        activeAnomalyKeys.add(`${a.simulatedUnitId}:${a.registerType}:${addr}`);
    }
  });

  let newKeysFound = false;

  for (const group of updates) {
    for (const change of group.changes) {
      const key = `${group.unitId}:${change.registerType}:${change.address}`;
      const isAnomaly = activeAnomalyKeys.has(key);
      const isNew = !liveRegisters.has(key);

      const regCfg = findRegisterConfig(group.unitId, change.registerType, change.address);

      const entry = {
        unitId: group.unitId,
        registerType: change.registerType,
        address: change.address,
        value: change.value,
        dataType: regCfg?.dataType || null,
        isBool: BOOL_REG_TYPES.has(change.registerType),
        isAnomaly,
        lastUpdated: new Date()
      };
      liveRegisters.set(key, entry);

      let history = registerHistory.get(key);
      if (!history) {
        history = [];
        registerHistory.set(key, history);
      }
      history.push(change.value);
      if (history.length > 40) history.shift();

      if (isNew) {
        newKeysFound = true;
      } else {
        updateLiveCell(key, entry);
      }
    }
  }

  if (newKeysFound) {
    scheduleRenderLiveTable();
  }

  document.getElementById('live-register-count').textContent =
    liveRegisters.size + ' registers';
}

function findRegisterConfig(unitId, registerType, address) {
  // Find unit by unitId (Modbus ID)
  const unit = units.find(u => u.unitId === unitId);
  if (!unit) return null;
  return allRegisters.find(r =>
    r.simulatedUnitId === unit.id &&
    r.registerType === registerType &&
    address >= r.startAddress &&
    address <= r.endAddress
  );
}

// Split a range register config into individual single-address configs.
// Returns the single-address config object for the requested address.
async function splitRangeIfNeeded(config, address) {
  if (config.startAddress === address && config.endAddress === address) return config;

  // Batch split on the backend (single reload, no value reset)
  await api('POST', `/api/register-configurations/${config.id}/split`, null);

  // Reload and return the single-address config for the requested address
  await loadAllRegisters();
  return allRegisters.find(r =>
    r.simulatedUnitId === config.simulatedUnitId &&
    r.registerType === config.registerType &&
    r.startAddress === address && r.endAddress === address
  ) || null;
}

function isAnomalyActive(unitId, registerType, address) {
  const dbUnit = units.find(u => u.unitId === unitId);
  if (!dbUnit) return false;
  return anomalies.some(a =>
    a.isActive &&
    a.simulatedUnitId === dbUnit.id &&
    a.registerType === registerType &&
    address >= a.startAddress &&
    address <= a.endAddress
  );
}

function updateLiveCell(key, entry) {
  const row = document.getElementById('live-row-' + CSS.escape(key));
  if (!row) return;

  entry.isAnomaly = isAnomalyActive(entry.unitId, entry.registerType, entry.address);

  const cells = row.querySelectorAll('td');
  if (cells.length < 8) return;

  const valCell = cells[3];
  const valueStr = formatLiveValue(entry);

  valCell.innerHTML = valueStr;
  valCell.classList.add('value-updated');
  setTimeout(() => valCell.classList.remove('value-updated'), 400);

  row.classList.toggle('anomaly-row', !!entry.isAnomaly);

  renderSparkline(key);
}

// ── Unit group expand/collapse ──
function toggleUnitGroup(unitId) {
  if (collapsedUnits.has(unitId)) {
    collapsedUnits.delete(unitId);
  } else {
    collapsedUnits.add(unitId);
  }
  // Toggle visibility of rows belonging to this unit
  document.querySelectorAll(`#live-tbody tr[data-unit="${unitId}"]`).forEach((tr, i) => {
    if (i === 0) return; // skip the header row itself
    tr.style.display = collapsedUnits.has(unitId) ? 'none' : '';
  });
  // Toggle header visual state
  const header = document.querySelector(`#live-tbody tr.unit-group-header[data-unit="${unitId}"]`);
  if (header) header.classList.toggle('collapsed', collapsedUnits.has(unitId));
}

// ── Lightweight: update only anomaly control cells in-place ──
function updateAnomalyCellsInPlace() {
  document.querySelectorAll('.live-register-row').forEach(row => {
    const cells = row.querySelectorAll('td');
    if (cells.length < 7) return;
    const key = row.id.replace('live-row-', '');
    const entry = liveRegisters.get(key);
    if (!entry) return;
    entry.isAnomaly = isAnomalyActive(entry.unitId, entry.registerType, entry.address);
    cells[5].innerHTML = entry.isAnomaly
      ? '<span class="state-anomaly">Anomaly</span>'
      : '<span class="state-normal">Normal</span>';
    cells[6].innerHTML = getAnomalyControlsHtml(entry);
    row.classList.toggle('anomaly-row', !!entry.isAnomaly);
  });
}

// ── Full table render (called on new keys / structural changes) ──
let renderTableTimer = null;
function scheduleRenderLiveTable() {
  if (!renderTableTimer) {
    renderTableTimer = setTimeout(() => {
      renderLiveTable();
      renderTableTimer = null;
    }, 80);
  }
}

function renderLiveTable() {
  const tbody = document.getElementById('live-tbody');
  let html = '';

  // Group by unit from WS data
  const grouped = {};
  liveRegisters.forEach((entry, key) => {
    const uid = entry.unitId;
    if (!grouped[uid]) grouped[uid] = [];
    grouped[uid].push({ key, ...entry });
  });

  // Also include units with no live data yet
  units.forEach(u => {
    if (!grouped[u.unitId]) grouped[u.unitId] = [];
  });

  const sortedUnitIds = Object.keys(grouped).map(Number).sort((a, b) => a - b);

  for (const unitId of sortedUnitIds) {
    const regs = grouped[unitId];
    const unit = units.find(u => u.unitId === unitId);

    regs.sort((a, b) => {
      if (a.registerType !== b.registerType) return a.registerType.localeCompare(b.registerType);
      return a.address - b.address;
    });

    const unitDb = units.find(u => u.unitId === unitId);

    html += `
      <tr class="unit-group-header${collapsedUnits.has(unitId) ? ' collapsed' : ''}" data-unit="${unitId}" onclick="toggleUnitGroup(${unitId})" style="cursor:pointer">
        <td colspan="8">
          <div class="unit-group-toggle">
            <span class="unit-group-chevron">${collapsedUnits.has(unitId) ? '▶' : '▼'}</span>
            <span class="badge badge-cyan">Unit ${unitId}</span>
            ${unit?.label ? `<span class="unit-group-label">${unit.label}</span>` : ''}
            <span class="text-muted text-sm">${regs.length} registers</span>
            <span style="margin-left:auto;display:flex;gap:4px;" onclick="event.stopPropagation()">
              <button class="btn btn-ghost btn-xs" onclick="editUnit(${unitDb?.id || 0})" title="Edit unit">
                <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M17 3a2.85 2.83 0 1 1 4 4L7.5 20.5 2 22l1.5-5.5Z"/><path d="m15 5 4 4"/></svg>
                Edit
              </button>
              <button class="btn btn-danger btn-xs" onclick="deleteUnit(${unitDb?.id || 0})" title="Delete unit">
                <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M3 6h18"/><path d="M19 6v14c0 1-1 2-2 2H7c-1 0-2-1-2-2V6"/><path d="M8 6V4c0-1 1-2 2-2h4c1 0 2 1 2 2v2"/></svg>
                Del
              </button>
            </span>
          </div>
        </td>
      </tr>
    `;

    for (const { key, ...entry } of regs) {
      const valueStr = formatLiveValue(entry);
      const stateEl = entry.isAnomaly
        ? '<span class="state-anomaly">Anomaly</span>'
        : '<span class="state-normal">Normal</span>';
      const controlsHtml = getAnomalyControlsHtml(entry);

      const regConfig = findRegisterConfig(entry.unitId, entry.registerType, entry.address);
      let editDelHtml = '';
      if (regConfig) {
        editDelHtml = `
          <button class="btn btn-ghost btn-xs" onclick="editRegister(${regConfig.id}, ${entry.address})" title="Edit register">
            <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M17 3a2.85 2.83 0 1 1 4 4L7.5 20.5 2 22l1.5-5.5Z"/><path d="m15 5 4 4"/></svg>
            Edit
          </button>
          <button class="btn btn-danger btn-xs" onclick="deleteRegister(${regConfig.id}, ${entry.address})" title="Delete register">
            <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M3 6h18"/><path d="M19 6v14c0 1-1 2-2 2H7c-1 0-2-1-2-2V6"/><path d="M8 6V4c0-1 1-2 2-2h4c1 0 2 1 2 2v2"/></svg>
            Del
          </button>
        `;
      }

      html += `
        <tr class="live-register-row${entry.isAnomaly ? ' anomaly-row' : ''}" 
            data-unit="${unitId}" 
            id="live-row-${CSS.escape(key)}">
          <td class="mono">${entry.unitId}</td>
          <td><span class="badge badge-purple">${formatRegType(entry.registerType)}</span></td>
          <td class="mono">${formatAddress(entry.registerType, entry.address)}</td>
          <td class="mono live-value">${valueStr}</td>
          <td><canvas class="sparkline-canvas" id="spark-${key}"></canvas></td>
          <td>${stateEl}</td>
          <td>${controlsHtml}</td>
          <td>
            <div class="action-grid">
              <button class="btn btn-danger btn-xs" onclick="quickInjectAnomaly(${entry.unitId}, '${entry.registerType}', ${entry.address})" title="Quick inject anomaly">
                <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M12 9v4m0 0v4m0-4h4m-4 0H8"/><circle cx="12" cy="12" r="10"/></svg>
                Inject
              </button>
              ${editDelHtml}
              <button class="btn btn-ghost btn-xs" onclick="copyMbpollCommand(${entry.unitId}, '${entry.registerType}', ${entry.address}, ${entry.address}, '${entry.dataType || 'UInt16'}')" title="Copy mbpoll command">
                <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="9" y="9" width="13" height="13" rx="2"/><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"/></svg>
                Copy
              </button>
            </div>
          </td>
        </tr>
      `;
    }
  }

  tbody.innerHTML = html;

  requestAnimationFrame(() => {
    liveRegisters.forEach((entry, key) => {
      renderSparkline(key);
    });
  });

  document.getElementById('live-register-count').textContent = liveRegisters.size + ' registers';
}

function formatLiveValue(entry) {
  const val = entry.value;
  if (typeof val !== 'number') return String(val ?? '—');

  // Bool display for Coil/DI
  if (entry.isBool) {
    const isOn = val >= 0.5;
    return `<span class="bool-indicator"><span class="bool-dot ${isOn ? 'on' : 'off'}"></span>${isOn ? 'ON' : 'OFF'}</span>`;
  }

  if (entry.dataType && !DT_INT.has(entry.dataType)) {
    return parseFloat(val.toFixed(4)).toString();
  }
  return Math.round(val).toString();
}

// ── Search / Filter ──────────────────────────────────────────
function filterLiveTable() {
  const q = (document.getElementById('live-search')?.value || '').toLowerCase();
  document.querySelectorAll('#live-tbody tr').forEach(tr => {
    if (!q) { tr.style.display = ''; return; }
    const text = tr.textContent.toLowerCase();
    tr.style.display = text.includes(q) ? '' : 'none';
  });
}

function filterAnomalies() {
  const q = (document.getElementById('anomaly-search')?.value || '').toLowerCase();
  document.querySelectorAll('#anomalies-tbody tr').forEach(tr => {
    if (!q) { tr.style.display = ''; return; }
    const text = tr.textContent.toLowerCase();
    tr.style.display = text.includes(q) ? '' : 'none';
  });
}

function clearLiveTable() {
  liveRegisters.clear();
  document.getElementById('live-tbody').innerHTML = '';
  document.getElementById('live-register-count').textContent = '0 registers';
}

// ────────────────────────────────────────────────────────────
// Dashboard
// ────────────────────────────────────────────────────────────
async function refreshDashboard() {
  try {
    serverStatus = await api('GET', '/api/simulator/status');
    document.getElementById('stat-units').textContent = serverStatus.unitCount;
    document.getElementById('stat-registers').textContent = serverStatus.registerCount;
    document.getElementById('stat-anomalies-active').textContent = serverStatus.activeAnomalyCount;
    document.getElementById('stat-anomalies-total').textContent = serverStatus.totalAnomalyCount;
    document.getElementById('stat-ws-clients').textContent = serverStatus.webSocketClients;
    document.getElementById('dash-host').textContent = serverStatus.modbusHost;
    document.getElementById('dash-port').textContent = serverStatus.modbusPort;
    document.getElementById('dash-ip').textContent = serverStatus.localIp || '127.0.0.1';

    // Update connection bar in header
    const connIp = serverStatus.localIp || '127.0.0.1';
    document.getElementById('conn-display').textContent = connIp + ':' + serverStatus.modbusPort;

    const badge = document.getElementById('sim-status-badge');
    if (serverStatus.isRunning) {
      badge.className = 'status-badge running';
      badge.textContent = '● Running';
    } else {
      badge.className = 'status-badge stopped';
      badge.textContent = '● Stopped';
    }
  } catch (e) {
    toast('Failed to load status: ' + e.message, 'error');
  }
}

async function startSimulator() {
  try {
    await api('POST', '/api/simulator/start');
    toast('Simulator started', 'success');
    refreshDashboard();
  } catch (e) { toast(e.message, 'error'); }
}

async function stopSimulator() {
  toast('Simulator runs as a background service — restart the application to stop it', 'warning');
}

// ────────────────────────────────────────────────────────────
// Units
// ────────────────────────────────────────────────────────────
async function loadUnits() {
  try {
    units = await api('GET', '/api/units');
    updateUnitSelects();
    renderLiveTable();
  } catch (e) { toast('Failed to load units: ' + e.message, 'error'); }
}

async function loadAllRegisters() {
  try {
    allRegisters = await api('GET', '/api/register-configurations');

    // Prune liveRegisters to only keep configured registers
    const activeKeys = new Set();
    for (const r of allRegisters) {
      const u = units.find(unit => unit.id == r.simulatedUnitId);
      if (!u) continue;
      for (let addr = r.startAddress; addr <= r.endAddress; addr++) {
        activeKeys.add(`${u.unitId}:${r.registerType}:${addr}`);
      }
    }

    liveRegisters.forEach((value, key) => {
      if (!activeKeys.has(key)) {
        liveRegisters.delete(key);
      }
    });

    renderLiveTable();
  } catch (e) {
    toast('Failed to load registers: ' + e.message, 'error');
  }
}

function openAddUnit() {
  document.getElementById('unit-edit-id').value = '';
  document.getElementById('modal-unit-title').textContent = 'Add Unit ID';
  document.getElementById('unit-id-input').value = '';
  document.getElementById('unit-label-input').value = '';
  document.getElementById('unit-enabled-input').value = 'true';
  openModal('modal-unit');
}

async function editUnit(id) {
  const u = units.find(x => x.id === id);
  if (!u) return;
  document.getElementById('unit-edit-id').value = u.id;
  document.getElementById('modal-unit-title').textContent = 'Edit Unit ID';
  document.getElementById('unit-id-input').value = u.unitId;
  document.getElementById('unit-label-input').value = u.label || '';
  document.getElementById('unit-enabled-input').value = String(u.enabled);
  openModal('modal-unit');
}

async function saveUnit() {
  const editId = document.getElementById('unit-edit-id').value;
  const unitId = parseInt(document.getElementById('unit-id-input').value);
  const label = document.getElementById('unit-label-input').value.trim() || null;
  const enabled = document.getElementById('unit-enabled-input').value === 'true';

  if (!unitId || unitId < 1 || unitId > 247) {
    toast('Unit ID must be 1–247', 'error'); return;
  }

  const btn = document.querySelector('#modal-unit .btn-primary');
  if (btn) { btn.disabled = true; btn.dataset.origText = btn.textContent; btn.textContent = 'Saving…'; }
  try {
    if (editId) {
      await api('PUT', `/api/units/${editId}`, { label, enabled });
      toast('Unit updated', 'success');
    } else {
      await api('POST', '/api/units', { unitId, label, enabled });
      toast('Unit created', 'success');
    }
    closeModal('modal-unit');
    await loadUnits();
    await loadAllRegisters();
  } catch (e) { toast(e.message, 'error'); }
  finally { if (btn) { btn.disabled = false; btn.textContent = btn.dataset.origText || 'Save'; } }
}

async function deleteUnit(id) {
  if (!await confirmAction('Delete Unit', 'Delete this unit and all its registers?')) return;
  try {
    await api('DELETE', `/api/units/${id}`);
    toast('Unit deleted', 'info');
    await loadUnits();
    await loadAllRegisters();
    await loadAnomalies();
  } catch (e) { toast(e.message, 'error'); }
}

function updateUnitSelects() {
  const regSelect = document.getElementById('reg-unit-input');
  const anomalySelect = document.getElementById('anomaly-unit-input');
  const opts = units.map(u =>
    `<option value="${u.id}">Unit ${u.unitId}${u.label ? ' — ' + u.label : ''}</option>`
  ).join('');
  if (regSelect) regSelect.innerHTML = opts || '<option value="">No units</option>';
  if (anomalySelect) anomalySelect.innerHTML = opts || '<option value="">No units</option>';
}

// ────────────────────────────────────────────────────────────
// Live Units List
// ────────────────────────────────────────────────────────────
function renderLiveUnitsList() {
  const container = document.getElementById('live-units-list');
  if (!container) return;

  if (!units.length) {
    container.innerHTML = '<span class="text-muted text-sm">No units configured. Click "Add Unit" to start.</span>';
    return;
  }

  container.innerHTML = units.map(u => `
    <div class="unit-pill" style="display:flex;align-items:center;gap:6px;background:var(--c-bg-2);border:1px solid var(--c-border);padding:4px 8px;border-radius:var(--radius-sm);font-size:12.5px;">
      <span class="badge badge-cyan">Unit ${u.unitId}</span>
      <span style="font-weight:600;color:var(--c-text-primary);">${u.label || ''}</span>
      <button class="btn btn-ghost btn-xs" style="padding:2px 6px;height:auto;font-size:10.5px;" onclick="editUnit(${u.id})">Edit</button>
      <button class="btn btn-danger btn-xs" style="padding:2px 6px;height:auto;font-size:10.5px;" onclick="deleteUnit(${u.id})">Del</button>
    </div>
  `).join('');
}

// ────────────────────────────────────────────────────────────
// Registers
// ────────────────────────────────────────────────────────────
function onRegisterTypeChange() {
  const regType = document.getElementById('reg-type-input').value;
  const isBool = BOOL_REG_TYPES.has(regType);
  const notice = document.getElementById('reg-bool-notice');
  const dtGroup = document.getElementById('reg-datatype-group');
  const genInput = document.getElementById('reg-gen-input');
  const minGroup = document.getElementById('reg-min-group');
  const maxGroup = document.getElementById('reg-max-group');

  notice.style.display = isBool ? '' : 'none';

  if (isBool) {
    // Lock data type to Bool
    document.getElementById('reg-datatype-input').value = 'Bool';
    dtGroup.classList.add('locked');
    document.getElementById('reg-datatype-input').disabled = true;

    // Lock min/max to 0/1
    document.getElementById('reg-min-input').value = '0';
    document.getElementById('reg-max-input').value = '1';
    document.getElementById('reg-min-input').disabled = true;
    document.getElementById('reg-max-input').disabled = true;
    minGroup.classList.add('locked');
    maxGroup.classList.add('locked');

    // Only Constant and Random
    genInput.innerHTML = `
      <option value="Random">Random (0 or 1)</option>
      <option value="Constant">Constant</option>
    `;

    // Hide byte order (irrelevant for bool)
    document.getElementById('reg-byteorder-group').style.display = 'none';
  } else {
    dtGroup.classList.remove('locked');
    document.getElementById('reg-datatype-input').disabled = false;
    document.getElementById('reg-min-input').disabled = false;
    document.getElementById('reg-max-input').disabled = false;
    minGroup.classList.remove('locked');
    maxGroup.classList.remove('locked');

    genInput.innerHTML = `
      <option value="Random">Random (between Min/Max)</option>
      <option value="Constant">Constant</option>
      <option value="Increment">Increment (counts up)</option>
      <option value="Decrement">Decrement (counts down)</option>
      <option value="Sine">Sine Wave</option>
    `;

    document.getElementById('reg-byteorder-group').style.display = '';
  }

  onGenTypeChange();
  updateRegPreview();
}

function onGenTypeChange() {
  const v = document.getElementById('reg-gen-input').value;
  document.getElementById('reg-constant-group').style.display = v === 'Constant' ? '' : 'none';
  document.getElementById('reg-step-group').style.display = (v === 'Increment' || v === 'Decrement') ? '' : 'none';
  document.getElementById('reg-period-group').style.display = v === 'Sine' ? '' : 'none';
}

function openAddRegister() {
  resetRegisterForm();
  openModal('modal-register');
}

function openAddRegisterForUnit(unitId) {
  resetRegisterForm();
  document.getElementById('reg-unit-input').value = unitId;
  openModal('modal-register');
}

function resetRegisterForm() {
  document.getElementById('reg-edit-id').value = '';
  document.getElementById('modal-reg-title').textContent = 'Add Register';
  document.getElementById('reg-start-input').value = '';
  document.getElementById('reg-end-input').value = '';
  document.getElementById('reg-min-input').value = '0';
  document.getElementById('reg-max-input').value = '100';
  document.getElementById('reg-type-input').value = 'HoldingRegister';
  document.getElementById('reg-datatype-input').value = 'Float32';
  document.getElementById('reg-gen-input').value = 'Random';
  const prev = document.getElementById('reg-addr-preview');
  if (prev) prev.textContent = '';
  setRegMode('single');

  // Collapse advanced details by default
  const details = document.getElementById('reg-advanced-details');
  if (details) details.removeAttribute('open');

  onRegisterTypeChange();
}

function setRegMode(mode) {
  regMode = mode;
  const endGroup = document.getElementById('reg-end-group');
  const btnSingle = document.getElementById('reg-mode-single');
  const btnRange = document.getElementById('reg-mode-range');

  if (mode === 'single') {
    endGroup.style.display = 'none';
    btnSingle.className = 'btn btn-primary btn-sm';
    btnRange.className = 'btn btn-ghost btn-sm';
  } else {
    endGroup.style.display = '';
    btnSingle.className = 'btn btn-ghost btn-sm';
    btnRange.className = 'btn btn-primary btn-sm';
  }
  updateRegPreview();
}

function updateRegPreview() {
  const start = parseInt(document.getElementById('reg-start-input').value);
  const endRaw = parseInt(document.getElementById('reg-end-input').value);
  const dt = document.getElementById('reg-datatype-input').value;
  const type = document.getElementById('reg-type-input').value;
  const base = getModiconBase(type);
  const size = DT_SIZE[dt] || 1;
  const preview = document.getElementById('reg-addr-preview');

  if (isNaN(start)) { preview.textContent = ''; return; }

  const startMod = base + start;

  if (regMode === 'single') {
    const endMod = startMod + size - 1;
    preview.textContent = size > 1
      ? `Modicon ${startMod}–${endMod} · ${size} words`
      : `Modicon ${startMod}`;
  } else {
    const end = isNaN(endRaw) ? start : endRaw;
    const endMod = base + end;
    const count = Math.floor((end - start) / size) + 1;
    preview.textContent = `Modicon ${startMod}–${endMod} · ${count} values`;
  }
}

async function editRegister(id, address) {
  let reg = allRegisters.find(r => r.id === id);
  if (!reg) return;

  // If it's a range and an address was specified, split first
  if (address !== undefined && (reg.startAddress !== reg.endAddress)) {
    reg = await splitRangeIfNeeded(reg, address);
    if (!reg) { toast('Failed to split register range', 'error'); return; }
  }

  document.getElementById('reg-edit-id').value = reg.id;
  document.getElementById('modal-reg-title').textContent = 'Edit Register Configuration';
  document.getElementById('reg-unit-input').value = reg.simulatedUnitId;
  document.getElementById('reg-type-input').value = reg.registerType;

  // First set the register type to trigger UI adjustments
  onRegisterTypeChange();

  document.getElementById('reg-start-input').value = reg.startAddress;
  document.getElementById('reg-end-input').value = reg.endAddress;
  document.getElementById('reg-datatype-input').value = reg.dataType;
  document.getElementById('reg-byteorder-input').value = reg.byteOrder;
  document.getElementById('reg-gen-input').value = reg.generationType;
  document.getElementById('reg-interval-input').value = reg.updateIntervalMs;
  document.getElementById('reg-min-input').value = reg.minValue;
  document.getElementById('reg-max-input').value = reg.maxValue;
  if (document.getElementById('reg-initial-input')) document.getElementById('reg-initial-input').value = reg.initialValue ?? 0;
  if (document.getElementById('reg-constant-input')) document.getElementById('reg-constant-input').value = reg.constantValue;
  if (document.getElementById('reg-step-input')) document.getElementById('reg-step-input').value = reg.incrementStep;
  if (document.getElementById('reg-period-input')) document.getElementById('reg-period-input').value = reg.sinePeriodSeconds;
  setRegMode(reg.startAddress === reg.endAddress ? 'single' : 'range');
  onGenTypeChange();
  openModal('modal-register');
}

async function saveRegister() {
  const editId = document.getElementById('reg-edit-id').value;
  const unitId = document.getElementById('reg-unit-input').value;
  const startAddress = parseInt(document.getElementById('reg-start-input').value) || 0;
  const endAddress = regMode === 'single'
    ? startAddress
    : (parseInt(document.getElementById('reg-end-input').value) || startAddress);

  const body = {
    simulatedUnitId: parseInt(unitId),
    registerType: document.getElementById('reg-type-input').value,
    startAddress,
    endAddress,
    dataType: document.getElementById('reg-datatype-input').value,
    byteOrder: document.getElementById('reg-byteorder-input').value,
    generationType: document.getElementById('reg-gen-input').value,
    updateIntervalMs: parseInt(document.getElementById('reg-interval-input').value),
    minValue: parseFloat(document.getElementById('reg-min-input').value) || 0,
    maxValue: parseFloat(document.getElementById('reg-max-input').value) || 100,
    initialValue: parseFloat(document.getElementById('reg-initial-input')?.value) || 0,
    constantValue: parseFloat(document.getElementById('reg-constant-input')?.value) || 0,
    incrementStep: parseFloat(document.getElementById('reg-step-input')?.value) || 1,
    sinePeriodSeconds: parseFloat(document.getElementById('reg-period-input')?.value) || 60,
    scatternessType: 'None',
    scatternessValue: 0,
    enabled: true
  };

  if (body.startAddress > body.endAddress) {
    toast('Start address must be ≤ End address', 'error'); return;
  }

  const btn = document.querySelector('#modal-register .btn-primary');
  if (btn) { btn.disabled = true; btn.dataset.origText = btn.textContent; btn.textContent = 'Saving…'; }
  try {
    if (editId) {
      await api('PUT', `/api/register-configurations/${editId}`, body);
      toast('Register updated', 'success');
    } else {
      await api('POST', `/api/units/${unitId}/registers`, body);
      toast('Register created', 'success');
    }
    closeModal('modal-register');
    document.getElementById('reg-edit-id').value = '';
    await loadAllRegisters();
  } catch (e) { toast(e.message, 'error'); }
  finally { if (btn) { btn.disabled = false; btn.textContent = btn.dataset.origText || 'Save'; } }
}

async function deleteRegister(id, address) {
  let reg = allRegisters.find(r => r.id === id);
  if (!reg) return;

  // If it's a range and an address was specified, split first
  if (address !== undefined && (reg.startAddress !== reg.endAddress)) {
    reg = await splitRangeIfNeeded(reg, address);
    if (!reg) { toast('Failed to split register range', 'error'); return; }
  }

  if (!await confirmAction('Delete Register', 'Delete this register configuration?')) return;
  try {
    await api('DELETE', `/api/register-configurations/${reg.id}`);
    toast('Register deleted', 'info');
    await loadAllRegisters();
  } catch (e) { toast(e.message, 'error'); }
}

// ────────────────────────────────────────────────────────────
// Anomalies
// ────────────────────────────────────────────────────────────
async function loadAnomalies() {
  try {
    anomalies = await api('GET', '/api/anomalies');
    renderAnomalies();

    // Update anomaly controls in-place without rebuilding entire table
    updateAnomalyCellsInPlace();

    const active = anomalies.filter(a => a.isActive);
    const countBadge = document.getElementById('anomalies-count-badge');
    const activeBadge = document.getElementById('active-anomaly-count-badge');
    const navBadge = document.getElementById('nav-badge-anomalies');

    if (countBadge) countBadge.textContent = anomalies.length;
    if (activeBadge) activeBadge.textContent = active.length;
    if (navBadge) {
      navBadge.textContent = active.length;
      navBadge.style.display = active.length ? '' : 'none';
    }
  } catch (e) { toast('Failed to load anomalies: ' + e.message, 'error'); }
}

function renderAnomalies() {
  const active = anomalies.filter(a => a.isActive);
  const activeBody = document.getElementById('active-anomalies-tbody');
  activeBody.innerHTML = active.length
    ? active.map(a => {
        const remaining = Math.max(0, Math.ceil((new Date(a.endsAt).getTime() - Date.now()) / 1000));
        return `
        <tr class="anomaly-row">
          <td><strong>${a.name}</strong></td>
          <td><span class="badge badge-cyan">Unit ${getUnitById(a.simulatedUnitId)?.unitId || '?'}</span></td>
          <td class="mono">${formatRegType(a.registerType)} ${formatAddressShort(a.registerType, a.startAddress, a.endAddress)}</td>
          <td><span class="countdown" data-end="${a.endsAt}">${remaining}s</span></td>
        </tr>
      `}).join('')
    : '<tr><td colspan="4" class="empty-state text-muted" style="padding:12px">No anomalies active</td></tr>';

  const tbody = document.getElementById('anomalies-tbody');
  if (!anomalies.length) {
    tbody.innerHTML = '<tr><td colspan="8" class="empty-state">No anomalies configured.</td></tr>';
    return;
  }
  tbody.innerHTML = anomalies.map(a => {
    const u = getUnitById(a.simulatedUnitId);
    const nextSched = a.nextScheduled
      ? `<span class="text-xs text-muted" style="display:block;margin-top:2px">Next: ${new Date(a.nextScheduled).toLocaleTimeString()}</span>`
      : '';
    return `
      <tr${a.isActive ? ' class="anomaly-row"' : ''}>
        <td><strong>${a.name}</strong></td>
        <td>
          <span class="badge badge-cyan">Unit ${u?.unitId || '?'}</span>
          <span class="mono text-sm" style="margin-left:4px">${formatRegType(a.registerType)} ${formatAddressShort(a.registerType, a.startAddress, a.endAddress)}</span>
        </td>
        <td><span class="badge ${dirBadge(a.direction)}">${formatDirection(a)}</span></td>
        <td><span class="badge badge-gray">${a.pattern}</span></td>
        <td class="mono">${a.durationSeconds}s</td>
        <td>
          <span class="badge ${a.triggerMode === 'Scheduled' ? 'badge-purple' : 'badge-blue'}">${a.triggerMode}</span>
          ${nextSched}
        </td>
        <td>${a.enabled
          ? '<span class="badge badge-emerald">On</span>'
          : '<span class="badge badge-gray">Off</span>'
        }</td>
        <td>
          <div class="action-row">
            <button class="btn btn-amber btn-xs" onclick="triggerAnomaly(${a.id})" title="Trigger now">Trigger</button>
            <button class="btn btn-ghost btn-xs" onclick="editAnomaly(${a.id})">Edit</button>
            <button class="btn btn-danger btn-xs" onclick="deleteAnomaly(${a.id})">Del</button>
          </div>
        </td>
      </tr>
    `;
  }).join('');
}

function dirBadge(d) {
  if (d === 'Increase') return 'badge-red';
  if (d === 'Decrease') return 'badge-blue';
  return 'badge-amber';
}
function formatDirection(a) {
  if (a.direction === 'Increase') return `+${a.amount}%`;
  if (a.direction === 'Decrease') return `-${a.amount}%`;
  return `Custom`;
}

// ── Schedule a UI refresh exactly when an anomaly expires ──
function scheduleAnomalyExpiryRefresh(anomalyId) {
  const a = anomalies.find(x => x.id === anomalyId);
  if (!a || !a.durationSeconds) return;
  // Gradual recovery adds another DurationSeconds after the anomaly ends
  const multiplier = a.recoveryType === 'Gradual' ? 2 : 1;
  const ms = a.durationSeconds * multiplier * 1000 + 500;
  setTimeout(async () => {
    await loadAnomalies();
    scheduleRenderLiveTable();
  }, ms);
}

async function triggerAnomaly(id) {
  try {
    await api('POST', `/api/anomalies/${id}/trigger`);
    toast('Anomaly triggered!', 'success');
    await loadAnomalies();
    scheduleAnomalyExpiryRefresh(id);
  } catch (e) { toast(e.message, 'error'); }
}

// Smart anomaly form logic
function openAddAnomaly() {
  document.getElementById('anomaly-edit-id').value = '';
  document.getElementById('modal-anomaly-title').textContent = 'Create Anomaly';
  document.getElementById('anomaly-name-input').value = '';
  document.getElementById('anomaly-regtype-input').value = 'HoldingRegister';
  document.getElementById('anomaly-direction-input').value = 'Increase';
  document.getElementById('anomaly-amount-input').value = '50';
  document.getElementById('anomaly-custom-min').value = '0';
  document.getElementById('anomaly-custom-max').value = '100';
  document.getElementById('anomaly-pattern-input').value = 'InstantSpike';
  document.getElementById('anomaly-recovery-input').value = 'Immediate';
  document.getElementById('anomaly-duration-input').value = '10';
  document.getElementById('anomaly-trigger-input').value = 'OnDemand';
  document.getElementById('anomaly-start-input').value = '0';
  document.getElementById('anomaly-end-input').value = '0';

  autoTriggerOnSave = false;
  const saveBtn = document.querySelector('#modal-anomaly .modal-footer .btn-primary');
  if (saveBtn) saveBtn.textContent = 'Save Anomaly';

  const details = document.getElementById('anomaly-advanced-details');
  if (details) details.removeAttribute('open');

  onAnomalyRegTypeChange();
  onAnomalyDirectionChange();
  onTriggerModeChange();
  onAnomalyUnitChange();
  openModal('modal-anomaly');
}

function onAnomalyUnitChange() {
  // When unit changes, refresh the register picker
  updateAnomalyRegisterPicker();
}

function onAnomalyRegTypeChange() {
  const regType = document.getElementById('anomaly-regtype-input').value;
  const isBool = BOOL_REG_TYPES.has(regType);
  const notice = document.getElementById('anomaly-bool-notice');
  const dirGroup = document.getElementById('anomaly-direction-group');
  const boolValGroup = document.getElementById('anomaly-bool-value-group');
  const customTypeGroup = document.getElementById('anomaly-custom-type-group');
  const amountGroup = document.getElementById('anomaly-amount-group');
  const minGroup = document.getElementById('anomaly-custom-min-group');
  const maxGroup = document.getElementById('anomaly-custom-max-group');
  const patternGroup = document.getElementById('anomaly-pattern-group');
  const recoveryInput = document.getElementById('anomaly-recovery-input');

  notice.style.display = isBool ? '' : 'none';

  if (isBool) {
    dirGroup.style.display = 'none';
    customTypeGroup.style.display = 'none';
    amountGroup.style.display = 'none';
    minGroup.style.display = 'none';
    maxGroup.style.display = 'none';
    patternGroup.style.display = 'none';
    if (recoveryInput && recoveryInput.parentElement) recoveryInput.parentElement.style.display = 'none';

    boolValGroup.style.display = '';
  } else {
    dirGroup.style.display = '';
    patternGroup.style.display = '';
    if (recoveryInput && recoveryInput.parentElement) recoveryInput.parentElement.style.display = '';

    boolValGroup.style.display = 'none';

    onAnomalyDirectionChange();
  }

  updateAnomalyRegisterPicker();
}

function updateAnomalyRegisterPicker() {
  const unitId = parseInt(document.getElementById('anomaly-unit-input').value);
  const regType = document.getElementById('anomaly-regtype-input').value;
  const picker = document.getElementById('anomaly-register-picker');

  if (isNaN(unitId)) {
    picker.innerHTML = '<option value="">— Select a register —</option>';
    return;
  }

  const matching = allRegisters.filter(r =>
    r.simulatedUnitId === unitId && r.registerType === regType
  );

  let html = '<option value="">— Select a register —</option>';
  if (matching.length === 0) {
    html += '<option value="" disabled>No registers of this type configured</option>';
  } else {
    for (const r of matching) {
      const addrText = r.startAddress === r.endAddress
        ? `Address ${r.startAddress}`
        : `Address ${r.startAddress}–${r.endAddress}`;
      html += `<option value="${r.id}">${formatRegType(r.registerType)} ${addrText} (${r.dataType}, ${r.generationType})</option>`;
    }
  }
  picker.innerHTML = html;
}

function onAnomalyRegisterPicked() {
  const regId = parseInt(document.getElementById('anomaly-register-picker').value);
  if (!regId) return;

  const reg = allRegisters.find(r => r.id === regId);
  if (!reg) return;

  document.getElementById('anomaly-start-input').value = reg.startAddress;
  document.getElementById('anomaly-end-input').value = reg.endAddress;
}

function onAnomalyDirectionChange() {
  const dir = document.getElementById('anomaly-direction-input').value;
  const isCustom = dir === 'CustomValue';
  const customTypeGroup = document.getElementById('anomaly-custom-type-group');
  const amountLabel = document.getElementById('anomaly-amount-label');

  if (isCustom) {
    customTypeGroup.style.display = '';
    onAnomalyCustomTypeChange();
  } else {
    customTypeGroup.style.display = 'none';
    document.getElementById('anomaly-custom-min-group').style.display = 'none';
    document.getElementById('anomaly-custom-max-group').style.display = 'none';
    document.getElementById('anomaly-amount-group').style.display = '';
    if (amountLabel) amountLabel.textContent = 'Amount (%)';
  }
}

function onAnomalyCustomTypeChange() {
  const type = document.getElementById('anomaly-custom-type-input').value;
  const amountGroup = document.getElementById('anomaly-amount-group');
  const amountLabel = document.getElementById('anomaly-amount-label');
  const minGroup = document.getElementById('anomaly-custom-min-group');
  const maxGroup = document.getElementById('anomaly-custom-max-group');

  if (type === 'Constant') {
    amountGroup.style.display = '';
    if (amountLabel) amountLabel.textContent = 'Constant Value';
    minGroup.style.display = 'none';
    maxGroup.style.display = 'none';
  } else {
    amountGroup.style.display = 'none';
    minGroup.style.display = '';
    maxGroup.style.display = '';
  }
}

function onTriggerModeChange() {
  const isScheduled = document.getElementById('anomaly-trigger-input').value === 'Scheduled';
  document.getElementById('anomaly-schedule-group').style.display = isScheduled ? '' : 'none';
  document.getElementById('anomaly-schedule-enabled-group').style.display = isScheduled ? '' : 'none';
  if (isScheduled) {
    onScheduleIntervalChange();
  } else {
    document.getElementById('anomaly-schedule-custom-group').style.display = 'none';
  }
}

function onScheduleIntervalChange() {
  const selectVal = document.getElementById('anomaly-schedule-interval-input').value;
  const customGroup = document.getElementById('anomaly-schedule-custom-group');
  if (selectVal === 'custom') {
    customGroup.style.display = '';
  } else {
    customGroup.style.display = 'none';
  }
}

async function editAnomaly(id) {
  const a = anomalies.find(x => x.id === id);
  if (!a) return;
  document.getElementById('anomaly-edit-id').value = a.id;
  document.getElementById('modal-anomaly-title').textContent = 'Edit Anomaly';
  document.getElementById('anomaly-name-input').value = a.name;
  document.getElementById('anomaly-unit-input').value = a.simulatedUnitId;
  document.getElementById('anomaly-regtype-input').value = a.registerType;

  onAnomalyRegTypeChange();

  const picker = document.getElementById('anomaly-register-picker');
  picker.value = "";
  for (const option of picker.options) {
    const regId = parseInt(option.value);
    if (!regId) continue;
    const reg = allRegisters.find(r => r.id === regId);
    if (reg && reg.startAddress === a.startAddress && reg.endAddress === a.endAddress) {
      picker.value = option.value;
      break;
    }
  }

  document.getElementById('anomaly-start-input').value = a.startAddress;
  document.getElementById('anomaly-end-input').value = a.endAddress;

  const isBool = BOOL_REG_TYPES.has(a.registerType);
  if (isBool) {
    document.getElementById('anomaly-bool-value-input').value = String(Math.round(a.amount));
  } else {
    document.getElementById('anomaly-direction-input').value = a.direction;
    if (a.direction === 'CustomValue') {
      document.getElementById('anomaly-custom-type-input').value = a.customPerRegister ? 'Random' : 'Constant';
      if (!a.customPerRegister) {
        document.getElementById('anomaly-amount-input').value = a.amount;
      } else {
        document.getElementById('anomaly-custom-min').value = a.customMin;
        document.getElementById('anomaly-custom-max').value = a.customMax;
      }
    } else {
      document.getElementById('anomaly-amount-input').value = a.amount;
    }
  }

  document.getElementById('anomaly-pattern-input').value = a.pattern;
  document.getElementById('anomaly-recovery-input').value = a.recoveryType;
  document.getElementById('anomaly-duration-input').value = a.durationSeconds;
  document.getElementById('anomaly-trigger-input').value = a.triggerMode;
  const selectVal = String(a.scheduleIntervalSeconds);
  const selectEl = document.getElementById('anomaly-schedule-interval-input');
  let found = false;
  for (let i = 0; i < selectEl.options.length; i++) {
    if (selectEl.options[i].value === selectVal) {
      found = true;
      break;
    }
  }
  if (found) {
    selectEl.value = selectVal;
    document.getElementById('anomaly-schedule-custom-group').style.display = 'none';
  } else {
    selectEl.value = 'custom';
    document.getElementById('anomaly-schedule-custom-group').style.display = '';
    document.getElementById('anomaly-schedule-custom-sec-input').value = a.scheduleIntervalSeconds;
  }
  document.getElementById('anomaly-schedule-enabled-input').value = String(a.isScheduleEnabled);

  autoTriggerOnSave = false;
  const saveBtn = document.querySelector('#modal-anomaly .modal-footer .btn-primary');
  if (saveBtn) saveBtn.textContent = 'Save Anomaly';

  const details = document.getElementById('anomaly-advanced-details');
  if (details) details.removeAttribute('open');

  if (isBool) {
    // Handled
  } else {
    onAnomalyDirectionChange();
  }
  onTriggerModeChange();
  openModal('modal-anomaly');
}

async function saveAnomaly() {
  const editId = document.getElementById('anomaly-edit-id').value;
  const regType = document.getElementById('anomaly-regtype-input').value;
  const isBool = BOOL_REG_TYPES.has(regType);

  let direction = document.getElementById('anomaly-direction-input').value;
  let amount = 0;
  let customPerRegister = false;
  let customMin = 0;
  let customMax = 0;
  let pattern = document.getElementById('anomaly-pattern-input').value;
  let recoveryType = document.getElementById('anomaly-recovery-input').value;

  if (isBool) {
    direction = 'CustomValue';
    amount = parseFloat(document.getElementById('anomaly-bool-value-input').value) || 0;
    customPerRegister = false;
    customMin = 0;
    customMax = 1;
    pattern = 'InstantSpike';
    recoveryType = 'Immediate';
  } else {
    if (direction === 'CustomValue') {
      const type = document.getElementById('anomaly-custom-type-input').value;
      if (type === 'Constant') {
        amount = parseFloat(document.getElementById('anomaly-amount-input').value) || 0;
        customPerRegister = false;
      } else {
        customPerRegister = true;
        customMin = parseFloat(document.getElementById('anomaly-custom-min').value) || 0;
        customMax = parseFloat(document.getElementById('anomaly-custom-max').value) || 100;
      }
    } else {
      amount = parseFloat(document.getElementById('anomaly-amount-input').value) || 0;
      customPerRegister = false;
    }
  }

  let intervalSec = 600;
  const selectVal = document.getElementById('anomaly-schedule-interval-input').value;
  if (selectVal === 'custom') {
    const sec = parseFloat(document.getElementById('anomaly-schedule-custom-sec-input').value);
    if (isNaN(sec) || sec < 0.05) {
      toast('Schedule interval must be at least 0.05 seconds (50 ms)', 'error'); return;
    }
    intervalSec = sec;
  } else {
    intervalSec = parseFloat(selectVal) || 600;
  }

  const body = {
    name: document.getElementById('anomaly-name-input').value.trim(),
    simulatedUnitId: parseInt(document.getElementById('anomaly-unit-input').value),
    registerType: regType,
    startAddress: parseInt(document.getElementById('anomaly-start-input').value) || 0,
    endAddress: parseInt(document.getElementById('anomaly-end-input').value) || 0,
    direction,
    amount,
    customPerRegister,
    customMin,
    customMax,
    pattern,
    recoveryType,
    durationSeconds: parseInt(document.getElementById('anomaly-duration-input').value) || 10,
    triggerMode: document.getElementById('anomaly-trigger-input').value,
    scheduleIntervalSeconds: intervalSec,
    isScheduleEnabled: document.getElementById('anomaly-schedule-enabled-input').value === 'true',
    enabled: true
  };

  if (!body.name) { toast('Name is required', 'error'); return; }
  if (!body.simulatedUnitId) { toast('Select a unit', 'error'); return; }

  const btn = document.querySelector('#modal-anomaly .btn-primary');
  if (btn) { btn.disabled = true; btn.dataset.origText = btn.textContent; btn.textContent = 'Saving…'; }
  try {
    let savedAnomalyId = editId;
    if (editId) {
      await api('PUT', `/api/anomalies/${editId}`, body);
      toast('Anomaly updated', 'success');
    } else {
      const created = await api('POST', '/api/anomalies', body);
      savedAnomalyId = created.id;
      toast('Anomaly created', 'success');
    }
    closeModal('modal-anomaly');
    document.getElementById('anomaly-edit-id').value = '';

    if (autoTriggerOnSave && savedAnomalyId) {
      await api('POST', `/api/anomalies/${savedAnomalyId}/trigger`);
      toast('Anomaly injected & triggered!', 'success');
      scheduleAnomalyExpiryRefresh(savedAnomalyId);
    }

    await loadAnomalies();
  } catch (e) { toast(e.message, 'error'); }
  finally { if (btn) { btn.disabled = false; btn.textContent = btn.dataset.origText || 'Save'; } }
}

async function deleteAnomaly(id) {
  if (!await confirmAction('Delete Anomaly', 'Delete this anomaly?')) return;
  try {
    await api('DELETE', `/api/anomalies/${id}`);
    toast('Anomaly deleted', 'info');
    await loadAnomalies();
  } catch (e) { toast(e.message, 'error'); }
}

// ────────────────────────────────────────────────────────────
// Usability Enhancements: Charting & Quick Inject
// ────────────────────────────────────────────────────────────
async function quickInjectAnomaly(unitId, registerType, address) {
  document.getElementById('anomaly-edit-id').value = '';
  document.getElementById('modal-anomaly-title').textContent = `Quick Anomaly Injection`;

  const unit = units.find(u => u.unitId === parseInt(unitId));
  if (!unit) return;

  document.getElementById('anomaly-name-input').value = `Quick Anomaly for ${formatRegType(registerType)} ${address}`;
  document.getElementById('anomaly-unit-input').value = unit.id;
  document.getElementById('anomaly-regtype-input').value = registerType;

  onAnomalyRegTypeChange();

  document.getElementById('anomaly-start-input').value = address;
  document.getElementById('anomaly-end-input').value = address;

  const picker = document.getElementById('anomaly-register-picker');
  picker.value = "";
  for (const option of picker.options) {
    const regId = parseInt(option.value);
    if (!regId) continue;
    const reg = allRegisters.find(r => r.id === regId);
    if (reg && reg.startAddress <= address && reg.endAddress >= address) {
      picker.value = option.value;
      break;
    }
  }

  const isBool = BOOL_REG_TYPES.has(registerType);
  if (isBool) {
    document.getElementById('anomaly-bool-value-input').value = '1';
  } else {
    document.getElementById('anomaly-direction-input').value = 'CustomValue';
    document.getElementById('anomaly-custom-type-input').value = 'Constant';
    document.getElementById('anomaly-amount-input').value = '100';
    onAnomalyDirectionChange();
  }

  const details = document.getElementById('anomaly-advanced-details');
  if (details) details.removeAttribute('open');

  autoTriggerOnSave = true;
  const saveBtn = document.querySelector('#modal-anomaly .modal-footer .btn-primary');
  if (saveBtn) saveBtn.textContent = 'Inject Anomaly';

  openModal('modal-anomaly');
}

function renderSparkline(key) {
  const canvas = document.getElementById('spark-' + key);
  if (!canvas) return;

  const ctx = canvas.getContext('2d');
  const history = registerHistory.get(key) || [];

  const w = canvas.clientWidth || 100;
  const h = canvas.clientHeight || 28;

  if (canvas.width !== w || canvas.height !== h) {
    canvas.width = w;
    canvas.height = h;
  }

  ctx.clearRect(0, 0, w, h);

  if (history.length < 2) {
    return;
  }

  let min = Math.min(...history);
  let max = Math.max(...history);

  if (min === max) {
    min -= 1;
    max += 1;
  } else {
    const pad = (max - min) * 0.05;
    min -= pad;
    max += pad;
  }

  const range = max - min;
  const maxPoints = 20;
  const points = history.slice(-maxPoints);

  ctx.beginPath();
  const dx = w / (points.length - 1);

  points.forEach((val, index) => {
    const x = index * dx;
    const y = h - ((val - min) / range) * h;
    if (index === 0) {
      ctx.moveTo(x, y);
    } else {
      ctx.lineTo(x, y);
    }
  });

  const entry = liveRegisters.get(key);
  const isAnomaly = entry && entry.isAnomaly;

  ctx.strokeStyle = isAnomaly ? '#DC2626' : '#0284C7';
  ctx.lineWidth = 1.8;
  ctx.lineCap = 'round';
  ctx.lineJoin = 'round';
  ctx.stroke();

  const grad = ctx.createLinearGradient(0, 0, 0, h);
  if (isAnomaly) {
    grad.addColorStop(0, 'rgba(220, 38, 38, 0.08)');
    grad.addColorStop(1, 'rgba(220, 38, 38, 0)');
  } else {
    grad.addColorStop(0, 'rgba(2, 132, 199, 0.08)');
    grad.addColorStop(1, 'rgba(2, 132, 199, 0)');
  }
  ctx.lineTo((points.length - 1) * dx, h);
  ctx.lineTo(0, h);
  ctx.closePath();
  ctx.fillStyle = grad;
  ctx.fill();
}

function getAnomalyControlsHtml(entry) {
  const dbUnit = units.find(u => u.unitId === entry.unitId);
  if (!dbUnit) return '<span class="inline-anomaly-none">No unit</span>';

  const matching = anomalies.filter(a =>
    a.simulatedUnitId === dbUnit.id &&
    a.registerType === entry.registerType &&
    entry.address >= a.startAddress &&
    entry.address <= a.endAddress
  );

  if (matching.length === 0) {
    return `<button class="btn btn-ghost btn-xs" onclick="quickInjectAnomaly(${entry.unitId}, '${entry.registerType}', ${entry.address})" title="Configure anomaly for this register">+ Config</button>`;
  }

  return `<div class="inline-anomaly-controls" style="display:flex;flex-direction:column;width:100%;gap:4px;">` +
    matching.map(a => {
      let actionsHtml = '';

      if (a.isActive) {
        actionsHtml += `<button class="btn-icon-anomaly" style="background:var(--c-red-soft);color:var(--c-red);border-color:var(--c-red);" onclick="stopAnomalyInline(${a.id}, event)" title="Stop anomaly">⏹</button>`;
      } else {
        actionsHtml += `<button class="btn-icon-anomaly" style="background:var(--c-amber-soft);color:var(--c-amber);border-color:var(--c-amber);" onclick="triggerAnomalyInline(${a.id}, event)" title="Trigger anomaly">▶</button>`;
      }

      if (a.triggerMode === 'Scheduled') {
        if (a.enabled) {
          actionsHtml += `<button class="btn-icon-anomaly" style="color:var(--c-cyan);border-color:var(--c-cyan);" onclick="toggleScheduleInline(${a.id}, false, event)" title="Pause schedule">⏸</button>`;
        } else {
          actionsHtml += `<button class="btn-icon-anomaly" style="color:var(--c-text-dim);" onclick="toggleScheduleInline(${a.id}, true, event)" title="Resume schedule">▶</button>`;
        }
      }

      return `
        <div class="inline-anomaly-item">
          <span class="inline-anomaly-name" title="${a.name}">${a.name}</span>
          <div class="inline-anomaly-actions">
            ${actionsHtml}
          </div>
        </div>
      `;
    }).join('') +
    `</div>`;
}

async function triggerAnomalyInline(id, event) {
  if (event) event.stopPropagation();
  try {
    await api('POST', `/api/anomalies/${id}/trigger`);
    toast('Anomaly triggered!', 'success');
    await loadAnomalies();
    loadSimulatorView();
    scheduleAnomalyExpiryRefresh(id);
  } catch (e) {
    toast('Trigger failed: ' + e.message, 'error');
  }
}

async function stopAnomalyInline(id, event) {
  if (event) event.stopPropagation();
  try {
    await api('POST', `/api/anomalies/${id}/stop`);
    toast('Anomaly stopped!', 'info');
  } catch (e) {
    toast('Anomaly already expired — refreshed', 'info');
  } finally {
    await loadAnomalies();
    loadSimulatorView();
  }
}

async function toggleScheduleInline(id, enable, event) {
  if (event) event.stopPropagation();
  try {
    const endpoint = enable ? 'enable' : 'disable';
    await api('POST', `/api/anomalies/${id}/${endpoint}`);
    toast(enable ? 'Schedule enabled!' : 'Schedule paused/stopped!', 'success');
    await loadAnomalies();
    loadSimulatorView();
  } catch (e) { toast(e.message, 'error'); }
}

// ────────────────────────────────────────────────────────────
// Helpers
// ────────────────────────────────────────────────────────────
function loadSimulatorView() {
  scheduleRenderLiveTable();
}

function formatRegType(t) {
  const map = {
    HoldingRegister: 'HR', InputRegister: 'IR',
    Coil: 'Coil', DiscreteInput: 'DI'
  };
  return map[t] || t;
}
function getModiconBase(type) {
  switch (type) {
    case 'HoldingRegister': return 40001;
    case 'InputRegister': return 30001;
    case 'DiscreteInput': return 10001;
    case 'Coil': return 1;
    default: return 40001;
  }
}
function formatAddress(type, startAddr, endAddr) {
  const base = getModiconBase(type);
  const startMod = base + startAddr;
  if (endAddr === undefined || endAddr === startAddr) {
    return `${startMod} <span class="text-muted" style="font-size:11px">(offset ${startAddr})</span>`;
  }
  const endMod = base + endAddr;
  return `${startMod}–${endMod} <span class="text-muted" style="font-size:11px">(${startAddr}–${endAddr})</span>`;
}
function formatAddressShort(type, startAddr, endAddr) {
  const base = getModiconBase(type);
  if (endAddr === undefined || endAddr === startAddr) {
    return `${base + startAddr}`;
  }
  return `${base + startAddr}–${base + endAddr}`;
}
function getUnitById(id) { return units.find(u => u.id === id); }

function getMbpollCommand(unitId, regType, startAddr, endAddr, dataType) {
  let typeFlag = '3';
  if (regType === 'HoldingRegister') typeFlag = '3';
  else if (regType === 'InputRegister') typeFlag = '4';
  else if (regType === 'Coil') typeFlag = '0';
  else if (regType === 'DiscreteInput') typeFlag = '1';

  if (dataType === 'Float32') typeFlag += ':float';
  else if (dataType === 'Int32' || dataType === 'UInt32') typeFlag += ':int';

  const dt = dataType || 'UInt16';
  const regSize = DT_SIZE[dt] || 1;
  const end = (endAddr !== undefined && endAddr >= startAddr) ? endAddr : startAddr;
  const count = Math.floor((end - startAddr) / regSize) + 1;
  const startReg = startAddr + 1;

  const ip = serverStatus?.localIp || '127.0.0.1';
  const port = serverStatus?.modbusPort || '502';
  return `mbpoll -m tcp -a ${unitId} -p ${port} -t ${typeFlag} -r ${startReg} -c ${count} ${ip}`;
}

function copyMbpollCommand(unitId, regType, startAddr, endAddr, dataType) {
  const cmd = getMbpollCommand(unitId, regType, startAddr, endAddr, dataType);
  copyToClipboard(cmd).then(() => {
    toast('Copied mbpoll command!', 'success');
  }).catch(() => {
    prompt('Copy mbpoll command:', cmd);
  });
}

// ────────────────────────────────────────────────────────────
// Export / Import
// ────────────────────────────────────────────────────────────
async function exportConfig() {
  try {
    const data = await api('GET', '/api/export');
    const json = JSON.stringify(data, null, 2);
    const blob = new Blob([json], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    const ts = new Date().toISOString().replace(/[:.]/g, '-').slice(0, 19);
    a.href = url;
    a.download = `modbus-simulator-config-${ts}.json`;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
    toast('Configuration exported', 'success');
  } catch (e) {
    toast('Export failed: ' + e.message, 'error');
  }
}

async function importConfig(input) {
  const file = input.files?.[0];
  if (!file) return;
  input.value = ''; // reset so same file can be re-selected

  let text;
  try {
    text = await file.text();
  } catch (e) {
    toast('Failed to read file', 'error');
    return;
  }

  let payload;
  try {
    payload = JSON.parse(text);
  } catch (e) {
    toast('Invalid JSON file', 'error');
    return;
  }

  if (!payload.units && !payload.registers && !payload.anomalies) {
    toast('File does not contain simulator configuration', 'error');
    return;
  }

  if (!await confirmAction(
    'Import Configuration',
    `Import ${payload.units?.length || 0} units, ${payload.registers?.length || 0} registers, ${payload.anomalies?.length || 0} anomalies?\n\nExisting entries with the same name/ID will be skipped.`
  )) return;

  try {
    const result = await api('POST', '/api/import', payload);
    const parts = [];
    if (result.units.imported || result.units.skipped)
      parts.push(`Units: ${result.units.imported} imported, ${result.units.skipped} skipped`);
    if (result.registers.imported || result.registers.skipped)
      parts.push(`Registers: ${result.registers.imported} imported, ${result.registers.skipped} skipped`);
    if (result.anomalies.imported || result.anomalies.skipped)
      parts.push(`Anomalies: ${result.anomalies.imported} imported, ${result.anomalies.skipped} skipped`);

    toast(parts.length ? parts.join(' | ') : 'Import completed — everything already up to date', 'success');

    // Reload everything
    await loadUnits();
    await loadAllRegisters();
    await loadAnomalies();
    scheduleRenderLiveTable();
  } catch (e) {
    toast('Import failed: ' + e.message, 'error');
  }
}

// ────────────────────────────────────────────────────────────
// Auto-refresh anomaly status every 5s
// ────────────────────────────────────────────────────────────
setInterval(() => {
  loadAnomalies().catch(() => {});
}, 5000);

// ── Live countdown updater (every 1s) ──────────────────────
setInterval(() => {
  let anyExpired = false;
  document.querySelectorAll('.countdown[data-end]').forEach(el => {
    const end = new Date(el.dataset.end).getTime();
    const remaining = Math.max(0, Math.ceil((end - Date.now()) / 1000));
    el.textContent = remaining + 's';
    if (remaining <= 0 && !el.classList.contains('expired')) {
      el.classList.add('expired');
      anyExpired = true;
    }
  });
  if (anyExpired) loadAnomalies();
}, 1000);

// ────────────────────────────────────────────────────────────
// Init
// ────────────────────────────────────────────────────────────
(async function init() {
  connectWebSocket();
  await loadUnits();
  await loadAllRegisters();
  await loadAnomalies();
  await refreshDashboard();
})();
