import { api, DEVICE_KINDS, type DeviceCreate, type DeviceKind, type HealthStatus, type ManagedDevice, type MonitorAlert, type NetworkTarget, type Overview } from './api';
import './styles.css';

type View = 'overview' | 'devices' | 'network' | 'alerts';

/**
 * Working copy of the device form, so re-renders never discard typing.
 * `id === null` means the form is creating a device rather than editing one.
 */
interface DeviceDraft {
  id: string | null;
  name: string;
  hostname: string;
  address: string;
  kind: DeviceKind;
  operatingSystem: string;
  tags: string;
}

interface AppState {
  activeView: View;
  overview: Overview | null;
  devices: ManagedDevice[];
  network: NetworkTarget[];
  alerts: MonitorAlert[];
  error: string;
  loading: boolean;
  toast: string;
  showTargetForm: boolean;
  deviceQuery: string;
  deviceStatus: HealthStatus | 'All';
  deviceDraft: DeviceDraft | null;
  pendingDeleteId: string | null;
}

const state: AppState = {
  activeView: 'overview',
  overview: null,
  devices: [],
  network: [],
  alerts: [],
  error: '',
  loading: true,
  toast: '',
  showTargetForm: false,
  deviceQuery: '',
  deviceStatus: 'All',
  deviceDraft: null,
  pendingDeleteId: null
};

const root = document.querySelector<HTMLDivElement>('#app');
if (!root) {
  throw new Error('App root was not found');
}

const viewLabels: Record<View, string> = {
  overview: 'Overview',
  devices: 'Managed devices',
  network: 'Network & wireless',
  alerts: 'Alerts'
};

/** True while an open form is on screen; background refreshes must not wipe it. */
function formIsOpen(): boolean {
  if (state.activeView === 'devices') return state.deviceDraft !== null;
  if (state.activeView === 'network') return state.showTargetForm;
  return false;
}

async function refresh(): Promise<void> {
  state.loading = true;
  setRefreshButtonState(true);
  try {
    const [overview, devices, network, alerts] = await Promise.all([
      api.overview(),
      api.devices(),
      api.network(),
      api.alerts()
    ]);
    state.overview = overview;
    state.devices = devices;
    state.network = network;
    state.alerts = alerts;
    state.error = '';
    if (!formIsOpen()) {
      render();
    }
  } catch (error) {
    state.error = error instanceof Error ? error.message : 'Unable to reach the monitoring API';
    if (!formIsOpen()) {
      render();
    }
  } finally {
    state.loading = false;
    setRefreshButtonState(false);
  }
}

function setRefreshButtonState(refreshing: boolean): void {
  root!.querySelectorAll<HTMLButtonElement>('[data-action="refresh"]').forEach(button => {
    button.classList.toggle('refreshing', refreshing);
    button.setAttribute('aria-busy', String(refreshing));
  });
}

function render(): void {
  root!.innerHTML = `
    <div class="app-shell">
      <aside class="sidebar">
        <div class="brand">
          <div class="brand-mark"><span></span><span></span><span></span></div>
          <div>
            <strong>Mukono District Manager</strong>
            <small>Infrastructure intelligence</small>
          </div>
        </div>
        <div class="workspace-label">Workspace</div>
        <nav class="nav-list" aria-label="Primary navigation">
          ${navItem('overview', '⌂', 'Overview')}
          ${navItem('devices', '▣', 'Managed devices')}
          ${navItem('network', '◈', 'Network & wireless')}
          ${navItem('alerts', '△', 'Alerts', state.overview?.openAlerts ?? 0)}
        </nav>
        <div class="sidebar-bottom">
          <div class="agent-card">
            <div class="agent-card-icon">✓</div>
            <div>
              <strong>Agent service</strong>
              <span>Ready for enrollment</span>
            </div>
          </div>
          <div class="sidebar-footer"><span class="status-dot online"></span> API connected <span class="version">v0.1</span></div>
        </div>
      </aside>
      <main class="main-content">
        <header class="topbar">
          <div>
            <div class="eyebrow">Operations / ${viewLabels[state.activeView]}</div>
            <h1>${viewLabels[state.activeView]}</h1>
          </div>
          <div class="topbar-actions">
            <span class="last-sync">${state.overview ? `Synced ${relativeTime(state.overview.generatedAt)}` : 'Waiting for data'}</span>
            <button class="icon-button" data-action="refresh" title="Refresh data" aria-label="Refresh data">↻</button>
            <div class="profile"><span>IT</span><div><strong>IT Operations</strong><small>Administrator</small></div></div>
          </div>
        </header>
        ${state.error ? `<div class="alert-banner error"><strong>Connection issue</strong><span>${escapeHtml(state.error)}</span><button data-action="dismiss-error">Dismiss</button></div>` : ''}
        ${state.toast ? `<div class="alert-banner success"><strong>Done</strong><span>${escapeHtml(state.toast)}</span><button data-action="dismiss-toast">Dismiss</button></div>` : ''}
        <div class="page-content">${renderActiveView()}</div>
      </main>
    </div>
  `;
  bindEvents();
}

function renderActiveView(): string {
  if (state.activeView === 'devices') return renderDevices();
  if (state.activeView === 'network') return renderNetwork();
  if (state.activeView === 'alerts') return renderAlerts();
  return renderOverview();
}

function renderOverview(): string {
  const overview = state.overview;
  const onlinePercent = overview?.totalDevices ? Math.round((overview.onlineDevices / overview.totalDevices) * 100) : 0;
  return `
    <section class="hero-panel">
      <div>
        <div class="eyebrow accent">Operations center</div>
        <h2>Good ${timeGreeting()}, IT team.</h2>
        <p>Here is the current health of your managed estate and authorized network targets.</p>
      </div>
      <div class="hero-score">
        <div class="score-ring" style="--score: ${onlinePercent * 3.6}deg"><strong>${onlinePercent}%</strong><span>healthy</span></div>
        <div class="score-copy"><strong>Estate health</strong><span>Based on managed device status</span></div>
      </div>
    </section>
    <section class="metric-grid">
      ${metricCard('Managed devices', overview?.totalDevices ?? 0, `${overview?.onlineDevices ?? 0} online`, 'blue', '▣')}
      ${metricCard('Network targets', overview?.totalNetworkTargets ?? 0, `${overview?.onlineNetworkTargets ?? 0} reachable`, 'violet', '◈')}
      ${metricCard('Open alerts', overview?.openAlerts ?? 0, overview?.openAlerts ? 'Needs attention' : 'All clear', (overview?.openAlerts ?? 0) > 0 ? 'orange' : 'green', '△')}
      ${metricCard('Last scan', overview ? relativeTime(overview.generatedAt) : '—', 'Automatic every 30 sec', 'slate', '◷')}
    </section>
    <div class="content-grid two-thirds">
      ${renderDeviceHealthPanel()}
      ${renderAlertPanel(true)}
    </div>
    <div class="content-grid">
      ${renderNetworkPanel(true)}
      ${renderQuickActions()}
    </div>
  `;
}

function renderDevices(): string {
  const filteredDevices = state.devices.filter(device => {
    const query = state.deviceQuery.trim().toLowerCase();
    const matchesQuery = !query || [device.name, device.hostname, device.address, device.kind, device.operatingSystem]
      .some(value => value.toLowerCase().includes(query));
    return matchesQuery && (state.deviceStatus === 'All' || device.status === state.deviceStatus);
  });

  return `
    <section class="section-heading">
      <div><div class="eyebrow accent">Managed estate</div><h2>Devices</h2><p>Agent-reported health plus manually tracked laptops, desktops, and other equipment.</p></div>
      <div class="heading-actions">
        <button class="button primary" data-action="toggle-device-form">＋ Add device</button>
        <button class="button secondary" data-action="export">Export inventory <span>↗</span></button>
      </div>
    </section>
    ${state.deviceDraft ? renderDeviceForm() : ''}
    <section class="panel table-panel">
      <div class="panel-heading"><div><h3>All managed devices</h3><span class="muted">${filteredDevices.length} of ${state.devices.length} records in SQLite</span></div><div class="table-tools"><label class="table-search"><span>⌕</span><input data-device-search type="search" value="${escapeHtml(state.deviceQuery)}" placeholder="Search devices" aria-label="Search devices" /></label><select data-device-status aria-label="Filter devices by status"><option value="All" ${state.deviceStatus === 'All' ? 'selected' : ''}>All statuses</option><option value="Online" ${state.deviceStatus === 'Online' ? 'selected' : ''}>Online</option><option value="Warning" ${state.deviceStatus === 'Warning' ? 'selected' : ''}>Warning</option><option value="Offline" ${state.deviceStatus === 'Offline' ? 'selected' : ''}>Offline</option><option value="Unknown" ${state.deviceStatus === 'Unknown' ? 'selected' : ''}>Unknown</option></select>${state.deviceQuery || state.deviceStatus !== 'All' ? '<button class="text-button" data-action="clear-device-filter">Clear</button>' : ''}</div></div>
      <div class="table-scroll"><table><thead><tr><th>Device</th><th>Type</th><th>Address</th><th>Status</th><th>CPU</th><th>Memory</th><th>Disk</th><th>Last seen</th><th class="actions-column">Actions</th></tr></thead><tbody>${filteredDevices.length ? filteredDevices.map(deviceRow).join('') : emptyRow(9, state.devices.length ? 'No devices match the current filters.' : 'No managed devices have reported in yet.')}</tbody></table></div>
    </section>
    <section class="callout"><div class="callout-icon">i</div><div><strong>Enrollment endpoint</strong><p>Managed agents send authenticated heartbeats to <code>POST /api/agents/heartbeat</code>. Configure a production agent key before deployment.</p></div></section>
  `;
}

function renderDeviceForm(): string {
  const draft = state.deviceDraft!;
  const creating = draft.id === null;
  const hint = creating
    ? 'Manually tracked devices have no agent, so their status stays Unknown until something reports for them.'
    : 'Blank fields keep their current value. Agents overwrite the fields they report on their next heartbeat.';

  return `
    <div class="target-form-shell">
      <form id="device-form" class="panel target-form">
        <div class="panel-heading"><div><h3>${creating ? 'Add device' : 'Edit device'}</h3><span class="muted">${hint}</span></div><button type="button" class="icon-button" data-action="cancel-device-edit" aria-label="Close device form">×</button></div>
        <div class="form-grid">
          <label>Display name<input name="name" required maxlength="120" placeholder="Accounts laptop" value="${escapeHtml(draft.name)}" /></label>
          <label>Device type<select name="kind">${DEVICE_KINDS.map(kind => `<option value="${kind}" ${draft.kind === kind ? 'selected' : ''}>${kind}</option>`).join('')}</select></label>
          <label>Hostname<input name="hostname" maxlength="120" placeholder="LAPTOP-014" value="${escapeHtml(draft.hostname)}" /></label>
          <label>Address<input name="address" maxlength="120" placeholder="10.20.4.14 or laptop.example.org" value="${escapeHtml(draft.address)}" ${creating ? 'required' : ''} /></label>
          <label>Operating system<input name="operatingSystem" maxlength="120" placeholder="Windows 11 Pro" value="${escapeHtml(draft.operatingSystem)}" /></label>
          <label>Tags<input name="tags" maxlength="500" placeholder="finance,laptop" value="${escapeHtml(draft.tags)}" /></label>
        </div>
        <div class="form-actions"><button class="button ghost" type="button" data-action="cancel-device-edit">Cancel</button><button class="button primary" type="submit">${creating ? 'Add device' : 'Save changes'}</button></div>
      </form>
    </div>
  `;
}

function renderNetwork(): string {
  return `
    <section class="section-heading">
      <div><div class="eyebrow accent">Authorized network</div><h2>Network & wireless targets</h2><p>Explicitly configured probes for infrastructure, access points, and controllers.</p></div>
      <button class="button primary" data-action="toggle-target-form">＋ Add target</button>
    </section>
    <div class="target-form-shell ${state.showTargetForm ? '' : 'hidden'}" id="target-form-shell">
      <form id="target-form" class="panel target-form">
        <div class="panel-heading"><div><h3>Add authorized target</h3><span class="muted">Only monitor systems you own or are authorized to administer.</span></div><button type="button" class="icon-button" data-action="toggle-target-form">×</button></div>
        <div class="form-grid">
          <label>Display name<input name="name" required placeholder="Core access point" /></label>
          <label>Address<input name="address" required placeholder="ap.example.org or 10.20.4.2" /></label>
          <label>Port<input name="port" type="number" min="1" max="65535" value="443" required /></label>
          <label>Probe type<select name="protocol"><option value="tcp">TCP</option><option value="https">HTTPS</option><option value="http">HTTP</option></select></label>
          <label>Device type<select name="kind"><option value="AccessPoint">Access point</option><option value="Router">Router</option><option value="Switch">Switch</option><option value="Server">Server</option><option value="Other">Other</option></select></label>
          <label class="wide">Notes<input name="notes" placeholder="Controller, site, or ownership notes" /></label>
        </div>
        <div class="form-actions"><button class="button ghost" type="button" data-action="toggle-target-form">Cancel</button><button class="button primary" type="submit">Save target</button></div>
      </form>
    </div>
    <section class="panel table-panel">
      <div class="panel-heading"><div><h3>Configured targets</h3><span class="muted">${state.network.length} targets persisted in SQLite</span></div><span class="legend"><i class="legend-dot online"></i> Reachable <i class="legend-dot offline"></i> Unreachable</span></div>
      <div class="table-scroll"><table><thead><tr><th>Target</th><th>Type</th><th>Probe</th><th>Status</th><th>Latency</th><th>Last check</th><th>Notes</th></tr></thead><tbody>${state.network.length ? state.network.map(networkRow).join('') : emptyRow(7, 'No network targets configured yet.')}</tbody></table></div>
    </section>
    <section class="content-grid two-thirds"><div class="panel info-panel"><div class="info-icon">⌁</div><div><h3>Wireless visibility</h3><p>Add access points and wireless controllers as targets now. The production integration can consume their authorized APIs or SNMPv3 data for channel utilization, client counts, and hardware health.</p></div></div><div class="panel privacy-panel"><div class="privacy-icon">◌</div><div><h3>Privacy by design</h3><p>Mukono District Manager reports infrastructure health. It does not intercept traffic, capture content, or collect private user activity.</p></div></div></section>
  `;
}

function renderAlerts(): string {
  return `
    <section class="section-heading"><div><div class="eyebrow accent">Attention queue</div><h2>Alerts</h2><p>Operational events generated by device heartbeats and network probes.</p></div><span class="count-badge">${state.alerts.length} open</span></section>
    <section class="panel alerts-panel">
      ${state.alerts.length ? state.alerts.map(alertRow).join('') : '<div class="empty-state"><div class="empty-state-icon">✓</div><h3>No open alerts</h3><p>Your monitored estate is quiet for now.</p></div>'}
    </section>
  `;
}

function renderDeviceHealthPanel(): string {
  const counts = { Online: 0, Warning: 0, Offline: 0, Unknown: 0 };
  state.devices.forEach(device => { counts[device.status] += 1; });
  const total = state.devices.length || 1;
  return `<section class="panel device-health-panel"><div class="panel-heading"><div><h3>Managed device health</h3><span class="muted">Live status from enrolled agents</span></div><button class="text-button" data-view="devices">View all →</button></div><div class="health-visual"><div class="health-donut" style="--online: ${(counts.Online / total) * 100}%; --warning: ${((counts.Online + counts.Warning) / total) * 100}%"><div><strong>${state.devices.length}</strong><span>devices</span></div></div><div class="health-legend"><div><i class="legend-dot online"></i><span>Online</span><strong>${counts.Online}</strong></div><div><i class="legend-dot warning"></i><span>Warning</span><strong>${counts.Warning}</strong></div><div><i class="legend-dot offline"></i><span>Offline</span><strong>${counts.Offline}</strong></div><div><i class="legend-dot unknown"></i><span>Unknown</span><strong>${counts.Unknown}</strong></div></div></div></section>`;
}

function renderAlertPanel(compact: boolean): string {
  const alerts = state.alerts.slice(0, compact ? 4 : state.alerts.length);
  return `<section class="panel alert-panel"><div class="panel-heading"><div><h3>Recent alerts</h3><span class="muted">Events needing attention</span></div><button class="text-button" data-view="alerts">View all →</button></div><div class="alert-list">${alerts.length ? alerts.map(alertRow).join('') : '<div class="mini-empty">No open alerts. Nice work.</div>'}</div></section>`;
}

function renderNetworkPanel(compact: boolean): string {
  const targets = state.network.slice(0, compact ? 5 : state.network.length);
  return `<section class="panel network-panel"><div class="panel-heading"><div><h3>Network reachability</h3><span class="muted">Configured infrastructure probes</span></div><button class="text-button" data-view="network">Manage →</button></div><div class="target-list">${targets.length ? targets.map(target => `<div class="target-row"><div class="target-icon">${target.kind === 'AccessPoint' ? '⌁' : '◈'}</div><div class="target-info"><strong>${escapeHtml(target.name)}</strong><span>${escapeHtml(target.address)}:${target.port}</span></div><div class="target-status">${targetStatus(target)}${target.latencyMs !== null ? `<small>${Math.round(target.latencyMs)} ms</small>` : ''}</div></div>`).join('') : '<div class="mini-empty">No targets configured.</div>'}</div></section>`;
}

function renderQuickActions(): string {
  return `<section class="panel quick-panel"><div class="panel-heading"><div><h3>Quick actions</h3><span class="muted">Common operations</span></div></div><div class="quick-actions"><button data-view="network"><span>＋</span><div><strong>Add network target</strong><small>Monitor a router, switch, or AP</small></div><b>→</b></button><button data-view="devices"><span>▣</span><div><strong>Review managed estate</strong><small>Check agent-reported health</small></div><b>→</b></button><button data-view="alerts"><span>△</span><div><strong>Review alerts</strong><small>Resolve open operational events</small></div><b>→</b></button></div></section>`;
}

function navItem(view: View, icon: string, label: string, badge?: number): string {
  return `<button class="nav-item ${state.activeView === view ? 'active' : ''}" data-view="${view}"><span class="nav-icon">${icon}</span><span>${label}</span>${badge ? `<b class="nav-badge">${badge}</b>` : ''}</button>`;
}

function metricCard(label: string, value: string | number, detail: string, tone: string, icon: string): string {
  return `<article class="metric-card ${tone}"><div class="metric-top"><span>${label}</span><i>${icon}</i></div><strong>${value}</strong><small>${detail}</small></article>`;
}

function deviceRow(device: ManagedDevice): string {
  return `<tr><td><div class="entity-cell"><div class="entity-avatar ${statusTone(device.status)}">${initials(device.name)}</div><div><strong>${escapeHtml(device.name)}</strong><span>${escapeHtml(device.hostname)}</span></div></div></td><td>${escapeHtml(device.kind)}</td><td><code>${escapeHtml(device.address)}</code></td><td>${statusPill(device.status)}</td><td>${metricCell(device.cpuPercent)}</td><td>${metricCell(device.memoryPercent)}</td><td>${metricCell(device.diskPercent)}</td><td>${device.lastSeen ? relativeTime(device.lastSeen) : 'Never'}</td><td>${deviceActions(device)}</td></tr>`;
}

function deviceActions(device: ManagedDevice): string {
  if (state.pendingDeleteId === device.id) {
    return `<div class="row-actions confirm"><span>Delete ${escapeHtml(device.name)}?</span><button class="text-button danger" data-action="confirm-delete-device" data-device-id="${device.id}">Yes, delete</button><button class="text-button" data-action="cancel-delete-device">Cancel</button></div>`;
  }

  const editing = state.deviceDraft?.id === device.id;
  return `<div class="row-actions"><button class="text-button" data-action="edit-device" data-device-id="${device.id}">${editing ? 'Editing…' : 'Edit'}</button><button class="text-button danger" data-action="delete-device" data-device-id="${device.id}">Delete</button></div>`;
}

function networkRow(target: NetworkTarget): string {
  return `<tr><td><div class="entity-cell"><div class="entity-avatar ${statusTone(target.status)}">${target.kind === 'AccessPoint' ? '⌁' : '◈'}</div><div><strong>${escapeHtml(target.name)}</strong><span>${escapeHtml(target.notes || 'Authorized target')}</span></div></div></td><td>${escapeHtml(target.kind)}</td><td><code>${escapeHtml(target.address)}:${target.port}</code><small class="protocol">${escapeHtml(target.protocol.toUpperCase())}</small></td><td>${targetStatus(target)}</td><td>${target.latencyMs === null ? '—' : `${Math.round(target.latencyMs)} ms`}</td><td>${target.lastChecked ? relativeTime(target.lastChecked) : 'Pending'}</td><td class="notes-cell">${escapeHtml(target.notes || '—')}</td></tr>`;
}

function alertRow(alert: MonitorAlert): string {
  const icon = alert.severity === 'critical' ? '!' : alert.severity === 'warning' ? '△' : 'i';
  return `<div class="alert-row"><div class="alert-type ${alert.severity}">${icon}</div><div class="alert-copy"><strong>${escapeHtml(alert.title)}</strong><p>${escapeHtml(alert.message)}</p><span>${escapeHtml(alert.deviceName || 'System')} · ${relativeTime(alert.createdAt)}</span></div><button class="text-button acknowledge" data-acknowledge="${alert.id}">Acknowledge</button></div>`;
}

function emptyRow(columns: number, message: string): string {
  return `<tr><td colspan="${columns}" class="table-empty">${message}</td></tr>`;
}

function statusPill(status: HealthStatus): string {
  return `<span class="status-pill ${statusTone(status)}"><i></i>${status}</span>`;
}

function targetStatus(target: NetworkTarget): string {
  return target.enabled
    ? statusPill(target.status)
    : '<span class="status-pill unknown"><i></i>Disabled</span>';
}

function metricCell(value: number | null): string {
  if (value === null) return '<span class="muted">—</span>';
  return `<div class="usage-cell"><div class="usage-bar"><i class="${value >= 80 ? 'danger' : value >= 65 ? 'warning' : ''}" style="width: ${Math.min(value, 100)}%"></i></div><span>${Math.round(value)}%</span></div>`;
}

function initials(name: string): string {
  return name.split(/\s+/).filter(Boolean).slice(0, 2).map(part => part[0]).join('').toUpperCase() || '?';
}

function statusTone(status: HealthStatus): string {
  return status.toLowerCase();
}

function relativeTime(value: string): string {
  const date = new Date(value);
  const seconds = Math.max(0, Math.floor((Date.now() - date.getTime()) / 1000));
  if (seconds < 60) return `${seconds}s ago`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours}h ago`;
  return `${Math.floor(hours / 24)}d ago`;
}

function timeGreeting(): string {
  const hour = new Date().getHours();
  if (hour < 12) return 'morning';
  if (hour < 18) return 'afternoon';
  return 'evening';
}

function escapeHtml(value: string): string {
  const entities: Record<string, string> = { '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' };
  return value.replace(/[&<>'"]/g, character => entities[character] ?? character);
}

function bindEvents(): void {
  root!.querySelectorAll<HTMLElement>('[data-view]').forEach(element => element.addEventListener('click', () => {
    state.activeView = element.dataset.view as View;
    render();
  }));
  root!.querySelectorAll<HTMLElement>('[data-action="refresh"]').forEach(element => element.addEventListener('click', () => { void refresh(); }));
  root!.querySelectorAll<HTMLElement>('[data-action="dismiss-error"]').forEach(element => element.addEventListener('click', () => { state.error = ''; render(); }));
  root!.querySelectorAll<HTMLElement>('[data-action="dismiss-toast"]').forEach(element => element.addEventListener('click', () => { state.toast = ''; render(); }));
  root!.querySelector<HTMLInputElement>('[data-device-search]')?.addEventListener('input', event => {
    state.deviceQuery = (event.currentTarget as HTMLInputElement).value;
    render();
    const search = root!.querySelector<HTMLInputElement>('[data-device-search]');
    search?.focus();
    search?.setSelectionRange(state.deviceQuery.length, state.deviceQuery.length);
  });
  root!.querySelector<HTMLSelectElement>('[data-device-status]')?.addEventListener('change', event => {
    state.deviceStatus = (event.currentTarget as HTMLSelectElement).value as HealthStatus | 'All';
    render();
  });
  root!.querySelectorAll<HTMLElement>('[data-action="clear-device-filter"]').forEach(element => element.addEventListener('click', () => {
    state.deviceQuery = '';
    state.deviceStatus = 'All';
    render();
  }));
  root!.querySelectorAll<HTMLElement>('[data-action="toggle-target-form"]').forEach(element => element.addEventListener('click', () => {
    state.showTargetForm = !state.showTargetForm;
    render();
  }));
  root!.querySelectorAll<HTMLElement>('[data-action="edit-device"]').forEach(element => element.addEventListener('click', () => {
    const device = state.devices.find(item => item.id === element.dataset.deviceId);
    if (!device) return;
    // Re-opening the row already being edited keeps whatever was typed.
    if (state.deviceDraft?.id === device.id) return;
    state.pendingDeleteId = null;
    state.deviceDraft = {
      id: device.id,
      name: device.name,
      hostname: device.hostname,
      address: device.address,
      kind: device.kind,
      operatingSystem: device.operatingSystem,
      tags: device.tags
    };
    render();
    root!.querySelector<HTMLInputElement>('#device-form input[name="name"]')?.focus();
  }));
  root!.querySelectorAll<HTMLElement>('[data-action="cancel-device-edit"]').forEach(element => element.addEventListener('click', () => {
    state.deviceDraft = null;
    render();
  }));
  root!.querySelectorAll<HTMLElement>('[data-action="toggle-device-form"]').forEach(element => element.addEventListener('click', () => {
    // Toggles the create form; an open editor is replaced rather than left open.
    const closing = state.deviceDraft?.id === null;
    state.deviceDraft = closing
      ? null
      : { id: null, name: '', hostname: '', address: '', kind: 'Workstation', operatingSystem: '', tags: '' };
    state.pendingDeleteId = null;
    render();
    if (!closing) {
      root!.querySelector<HTMLInputElement>('#device-form input[name="name"]')?.focus();
    }
  }));
  root!.querySelectorAll<HTMLElement>('[data-action="delete-device"]').forEach(element => element.addEventListener('click', () => {
    const id = element.dataset.deviceId ?? null;
    // Only close the editor when it is the row being deleted.
    if (state.deviceDraft?.id === id) {
      state.deviceDraft = null;
    }
    state.pendingDeleteId = id;
    render();
  }));
  root!.querySelectorAll<HTMLElement>('[data-action="cancel-delete-device"]').forEach(element => element.addEventListener('click', () => {
    state.pendingDeleteId = null;
    render();
  }));
  root!.querySelectorAll<HTMLElement>('[data-action="confirm-delete-device"]').forEach(element => element.addEventListener('click', () => {
    void deleteDevice(element.dataset.deviceId);
  }));
  root!.querySelector<HTMLFormElement>('#target-form')?.addEventListener('submit', event => {
    event.preventDefault();
    void createTarget(event.currentTarget as HTMLFormElement);
  });
  const deviceForm = root!.querySelector<HTMLFormElement>('#device-form');
  if (deviceForm) {
    // Keep the draft in step with typing, so any re-render reproduces exactly
    // what is on screen instead of restoring the values from when the form opened.
    const syncDraft = (event: Event) => {
      const draft = state.deviceDraft;
      const field = event.target as HTMLInputElement | HTMLSelectElement;
      if (!draft || !field.name) return;
      if (field.name === 'kind') {
        draft.kind = field.value as DeviceKind;
        return;
      }
      const key = field.name as 'name' | 'hostname' | 'address' | 'operatingSystem' | 'tags';
      draft[key] = field.value;
    };
    deviceForm.addEventListener('input', syncDraft);
    deviceForm.addEventListener('change', syncDraft);
    deviceForm.addEventListener('submit', event => {
      event.preventDefault();
      void saveDevice(deviceForm);
    });
  }
  root!.querySelectorAll<HTMLElement>('[data-action="export"]').forEach(element => element.addEventListener('click', () => {
    const rows = [['Name', 'Hostname', 'Type', 'Address', 'Status'], ...state.devices.map(device => [device.name, device.hostname, device.kind, device.address, device.status])];
    const csv = rows.map(row => row.map(value => `"${value.replaceAll('"', '""')}"`).join(',')).join('\n');
    const link = document.createElement('a');
    link.href = URL.createObjectURL(new Blob([csv], { type: 'text/csv' }));
    link.download = `mukono-district-manager-inventory-${new Date().toISOString().slice(0, 10)}.csv`;
    link.click();
    URL.revokeObjectURL(link.href);
  }));
  root!.querySelectorAll<HTMLElement>('[data-acknowledge]').forEach(element => element.addEventListener('click', () => {
    void acknowledgeAlert(element.dataset.acknowledge!);
  }));
}

async function acknowledgeAlert(id: string | undefined): Promise<void> {
  if (!id) return;
  try {
    await api.acknowledgeAlert(id);
    state.toast = 'Alert acknowledged.';
    await refresh();
  } catch (error) {
    state.error = error instanceof Error ? error.message : 'Unable to acknowledge alert';
    render();
  }
}

async function createTarget(form: HTMLFormElement): Promise<void> {
  const values = new FormData(form);
  try {
    await api.addTarget({
      name: String(values.get('name') ?? ''),
      address: String(values.get('address') ?? ''),
      port: Number(values.get('port') ?? 443),
      protocol: String(values.get('protocol') ?? 'tcp'),
      kind: String(values.get('kind') ?? 'Other') as DeviceKind,
      notes: String(values.get('notes') ?? '')
    });
    state.toast = 'Network target added.';
    state.showTargetForm = false;
    await refresh();
  } catch (error) {
    state.error = error instanceof Error ? error.message : 'Unable to add network target';
    render();
  }
}

async function saveDevice(form: HTMLFormElement): Promise<void> {
  const draft = state.deviceDraft;
  if (!draft) return;

  const values = new FormData(form);
  const payload: DeviceCreate = {
    name: String(values.get('name') ?? '').trim(),
    hostname: String(values.get('hostname') ?? '').trim(),
    address: String(values.get('address') ?? '').trim(),
    kind: String(values.get('kind') ?? draft.kind) as DeviceKind,
    operatingSystem: String(values.get('operatingSystem') ?? '').trim(),
    tags: String(values.get('tags') ?? '').trim()
  };

  try {
    if (draft.id === null) {
      await api.addDevice(payload);
      // Clear filters so the device that was just added is actually visible.
      state.deviceQuery = '';
      state.deviceStatus = 'All';
      state.deviceDraft = null;
      state.toast = `${payload.name} added.`;
    } else {
      await api.updateDevice(draft.id, payload);
      state.deviceDraft = null;
      state.toast = 'Device updated.';
    }
    await refresh();
  } catch (error) {
    // Keep the draft so a rejected save never discards what was typed.
    state.error = error instanceof Error ? error.message : 'Unable to save device';
    render();
  }
}

async function deleteDevice(id: string | undefined): Promise<void> {
  if (!id) return;
  const label = state.devices.find(device => device.id === id)?.name ?? 'Device';

  try {
    await api.deleteDevice(id);
    state.pendingDeleteId = null;
    if (state.deviceDraft?.id === id) {
      state.deviceDraft = null;
    }
    state.toast = `${label} deleted.`;
    await refresh();
  } catch (error) {
    state.pendingDeleteId = null;
    state.error = error instanceof Error ? error.message : 'Unable to delete device';
    render();
  }
}

void refresh();
window.setInterval(() => { void refresh(); }, 30_000);
