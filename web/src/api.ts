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
  | 'Other';

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
    const message = await response.text();
    throw new Error(message || `Request failed with ${response.status}`);
  }

  return (await response.json()) as T;
}

export const api = {
  overview: () => request<Overview>('/api/overview'),
  devices: () => request<ManagedDevice[]>('/api/devices'),
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
