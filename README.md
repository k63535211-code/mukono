# OrgMonitor

OrgMonitor is an authorized IT infrastructure monitoring MVP for organizations that need a single view of managed devices and explicitly configured network/Wi-Fi targets.

## Stack

- **API:** C# / ASP.NET Core 8
- **Storage:** SQLite through `Microsoft.Data.Sqlite`
- **Dashboard:** Vite + TypeScript (no frontend framework)
- **Agent:** C# console service in `src/OrgMonitor.Agent`
- **Agent protocol:** authenticated HTTP heartbeat endpoint
- **Network probes:** TCP, HTTP, and HTTPS reachability checks

## What is implemented

- Managed-device inventory with agent-reported CPU, memory, disk, OS, hostname, and heartbeat status
- Persistent SQLite storage for devices, network targets, and alerts
- Automatic offline marking for managed devices that stop reporting
- Configurable TCP/HTTP/HTTPS network probes
- Network target management for routers, switches, servers, access points, and controllers
- Alert history, acknowledgement, and SQLite-backed status transitions
- Responsive operations dashboard with overview, inventory, network, and alerts views
- CSV inventory export
- CORS configuration for the Vite development server

This is an MVP foundation, not a finished enterprise product. It intentionally monitors only targets an administrator explicitly configures. Seeded example network targets are disabled and must be replaced with authorized production addresses. Production wireless metrics such as channel utilization and client counts require a supported AP/controller API or SNMPv3 integration.

## Prerequisites

- .NET 8 SDK
- Node.js 20 or newer
- npm 10 or newer

## Verify the project

With the .NET SDK and Node.js installed:

```bash
dotnet build OrgMonitor.sln
cd web && npm run build
```

## Run locally

Start the API:

```bash
dotnet run --project src/OrgMonitor.Api
```

In a second terminal, start the dashboard:

```bash
cd web
npm install
npm run dev
```

If the API has `Monitoring:AdminApiKey` configured, inject the same key into the **development-only Vite proxy** (the key stays server-side and is not bundled into the browser):

```bash
ORGADMIN_API_KEY='replace-with-a-different-long-random-secret' npm run dev
```

Open <http://127.0.0.1:5173>. The Vite proxy sends `/api` and `/health` requests to `http://localhost:5080`.

The default database is `orgmonitor.db` in the process working directory. To use a different location, set `Database__Path` before starting the API:

```bash
Database__Path=/var/lib/orgmonitor/orgmonitor.db \
Monitoring__AgentApiKey='replace-with-a-long-random-secret' \
Monitoring__AdminApiKey='replace-with-a-different-long-random-secret' \
dotnet run --project src/OrgMonitor.Api
```

The schema is created automatically on first start. Set `Database__SeedDemoData=false` for an empty deployment.

## Agent heartbeat

Run the bundled C# agent on each managed device after starting the API:

```bash
ORGMONITOR_SERVER='https://monitor.example.org' \
ORGMONITOR_API_KEY='replace-with-a-long-random-secret' \
ORGMONITOR_NAME='Example laptop' \
dotnet run --project src/OrgMonitor.Agent
```

The agent stores a generated device ID under the platform local application-data directory (or `ORGMONITOR_STATE_DIR`) and sends a heartbeat on the configured interval (60 seconds by default). Configure `ORGMONITOR_INTERVAL_SECONDS`, `ORGMONITOR_ADDRESS`, `ORGMONITOR_KIND`, `ORGMONITOR_TAGS`, and `ORGMONITOR_STATE_DIR` for the device. Linux agents report CPU and memory from `/proc`; other platforms report identity and disk usage until platform-specific collectors are added.

Managed agents send a JSON `POST` request to `/api/agents/heartbeat`. When `Monitoring:AgentApiKey` is configured, include it in the `X-Agent-Key` header.

```bash
curl -X POST http://localhost:5080/api/agents/heartbeat \
  -H 'Content-Type: application/json' \
  -H 'X-Agent-Key: replace-with-a-long-random-secret' \
  -d '{
    "deviceId": "7c4b2a9e-8d1d-4b55-9e8c-0a6cf4f20101",
    "name": "Example laptop",
    "hostname": "LAPTOP-014",
    "address": "10.20.4.14",
    "kind": "Workstation",
    "operatingSystem": "Windows 11 Pro",
    "agentVersion": "0.1.0",
    "cpuPercent": 24,
    "memoryPercent": 58,
    "diskPercent": 63,
    "tags": "finance,laptop"
  }'
```

## API surface

Management routes are open only in `Development` when `Monitoring:AdminApiKey` is empty. In every other configuration they require `X-Admin-Key`; production fails closed when the key is not configured. Agent heartbeats use `X-Agent-Key` when `Monitoring:AgentApiKey` is configured.

For a configured local admin key, test management access with:

```bash
curl -H 'X-Admin-Key: replace-with-a-different-long-random-secret' http://localhost:5080/api/overview
```

- `GET /health` — service and storage health
- `GET /api/overview` — dashboard counters
- `GET /api/devices` — managed-device inventory
- `POST /api/agents/heartbeat` — enroll or update an agent
- `GET /api/network` — configured network targets and probe status
- `POST /api/network/targets` — add an authorized target
- `GET /api/alerts` — open alerts; pass `?includeAcknowledged=true` for history
- `POST /api/alerts/{id}/acknowledge` — acknowledge an alert

## Production hardening still required

- Replace development defaults and put the API behind TLS and a reverse proxy.
- Replace the development API-key guard with organization SSO/RBAC and audit logging for dashboard and management endpoints.
- Use managed secrets for `Monitoring:AgentApiKey` and `Monitoring:AdminApiKey`; the API fails closed when keys are missing outside Development.
- Add agent packaging, signed installers, update service, retry/backoff, and per-device enrollment tokens.
- Add authenticated SNMPv3 or vendor-API integrations for switches, firewalls, wireless controllers, DHCP, and directory inventory.
- Move schema changes to versioned migrations before evolving the database.
- Add backups, retention policies, rate limiting, structured audit events, and encrypted database/storage handling as required by the organization.
- Test only against infrastructure the organization owns or is explicitly authorized to monitor.
