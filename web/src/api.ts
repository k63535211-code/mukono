export type HealthStatus = 'Unknown' | 'Online' | 'Warning' | 'Offline';
export type DeviceKind =
  | 'Unknown'
  | 'Workstation'
  | 'Server'
  | 'Router'
  | 'Switch'
  | 'AccessPoint'
  | 'Firewall'
  | 'Printer'
  | 'Controller'
  | 'Storage'
  | 'Mobile'
  | 'LoadBalancer'
  | 'Laptop'
  | 'Desktop'
  | 'Other';

/** Every value of the API's DeviceKind enum, for building selects. */
export const DEVICE_KINDS: DeviceKind[] = [
  'Workstation',
  'Laptop',
  'Desktop',
  'Server',
  'Router',
  'Switch',
  'AccessPoint',
  'Firewall',
  'Printer',
  'Controller',
  'Storage',
  'Mobile',
  'LoadBalancer',
  'Other',
  'Unknown'
];

export interface Overview {
  totalDevices: number;
  onlineDevices: number;
  warningDevices: number;
  offlineDevices: number;
  totalNetworkTargets: number;
  onlineNetworkTargets: number;
  openAlerts: number;
  generatedAt: string;
}

export interface ManagedDevice {
  id: string;
  name: string;
  hostname: string;
  address: string;
  kind: DeviceKind;
  operatingSystem: string;
  agentVersion: string;
  lastSeen: string | null;
  status: HealthStatus;
  cpuPercent: number | null;
  memoryPercent: number | null;
  diskPercent: number | null;
  tags: string;
}

export interface NetworkTarget {
  id: string;
  name: string;
  address: string;
  port: number;
  protocol: string;
  kind: DeviceKind;
  enabled: boolean;
  status: HealthStatus;
  latencyMs: number | null;
  lastChecked: string | null;
  notes: string;
}

export interface MonitorAlert {
  id: string;
  severity: string;
  title: string;
  message: string;
  deviceId: string | null;
  deviceName: string;
  createdAt: string;
  acknowledged: boolean;
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const headers = new Headers(init?.headers);
  if (!headers.has('Content-Type')) {
    headers.set('Content-Type', 'application/json');
  }

  const response = await fetch(path, { ...init, headers });

  if (!response.ok) {
    const body = await response.text();
    throw new Error(readErrorMessage(body) || `Request failed with ${response.status}`);
  }

  return (await response.json()) as T;
}

/** The API reports failures as `{"error":"..."}`; surface just that message. */
function readErrorMessage(body: string): string {
  if (!body) return '';
  try {
    const parsed = JSON.parse(body) as { error?: unknown };
    return typeof parsed.error === 'string' ? parsed.error : '';
  } catch {
    return body;
  }
}

/**
 * Every editable device field.
 * On update, blank values mean "keep the current value".
 * On create, `name` and `address` are required.
 */
export interface DeviceFields {
  name: string;
  hostname: string;
  address: string;
  kind: DeviceKind;
  operatingSystem: string;
  tags: string;
}

export type DeviceUpdate = DeviceFields;
export type DeviceCreate = DeviceFields;

export const api = {
  overview: () => request<Overview>('/api/overview'),
  devices: () => request<ManagedDevice[]>('/api/devices'),
  addDevice: (payload: DeviceCreate) =>
    request<ManagedDevice>('/api/devices', { method: 'POST', body: JSON.stringify(payload) }),
  updateDevice: (id: string, patch: DeviceUpdate) =>
    request<ManagedDevice>(`/api/devices/${id}`, { method: 'PUT', body: JSON.stringify(patch) }),
  deleteDevice: (id: string) =>
    request<{ deleted: boolean }>(`/api/devices/${id}`, { method: 'DELETE' }),
  network: () => request<NetworkTarget[]>('/api/network'),
  alerts: () => request<MonitorAlert[]>('/api/alerts'),
  addTarget: (target: {
    name: string;
    address: string;
    port: number;
    protocol: string;
    kind: DeviceKind;
    notes: string;
  }) => request<NetworkTarget>('/api/network/targets', { method: 'POST', body: JSON.stringify(target) }),
  acknowledgeAlert: (id: string) =>
    request<{ acknowledged: boolean }>(`/api/alerts/${id}/acknowledge`, { method: 'POST' })
};
