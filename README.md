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
- Responsive operations dashboard with overview, inventory, network, and alerts view
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

## Configuration and secrets

Secrets live in a `.env` file at the repository root. It is gitignored; `.env.example` is the committed template:

```bash
cp .env.example .env
chmod 600 .env
```

The API loads `.env` at startup (`src/OrgMonitor.Api/Configuration/DotEnv.cs`), and `npm run dev` reads the same file for the development-only Vite proxy — so one file holds every secret for local work. Keys use ASP.NET's environment-variable form, where `__` nests one level, so `Monitoring__AdminApiKey` sets `Monitoring:AdminApiKey`.

Rules the loader follows:

- A variable already exported in the shell **always wins** over `.env`, so production can keep using systemd, Docker, or key-vault secrets unchanged.
- `#` starts a comment only at the beginning of a line, so generated secrets may contain `#`.
- Values may be single- or double-quoted; quotes are stripped.
- The file is searched upward from the working directory and from the assembly location, so it is found whether you run `dotnet run` from the repository root or execute the built binary.

| Key | Purpose |
| --- | --- |
| `Monitoring__AdminApiKey` | Required outside Development; `X-Admin-Key` for management routes |
| `Monitoring__AgentApiKey` | `X-Agent-Key` for agent heartbeats |
| `Auth__BootstrapPassword` | Password for the initial `admin` account, used only while no users exist |
| `Auth__PublicBaseUrl` | Absolute base URL used to build password-reset links |
| `Smtp__Host`, `Smtp__Username`, `Smtp__Password`, `Smtp__From`, … | SMTP delivery for resets and notifications |
| `Database__Path`, `Database__SeedDemoData` | Storage location and demo-row seeding |
| `ORGADMIN_API_KEY` | Read only by `npm run dev`; sent as `X-Admin-Key` by the proxy, never bundled into the browser |

Agents on other machines do not read this file: they take `ORGMONITOR_API_KEY` from their own environment.

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

If the API has `Monitoring:AdminApiKey` configured, put the same value in `.env` as `ORGADMIN_API_KEY` so the **development-only Vite proxy** can send it (the key stays server-side and is not bundled into the browser).

Open <http://127.0.0.1:5173>. The Vite proxy sends `/api` and `/health` requests to `http://localhost:5080`.

The default database is `orgmonitor.db` in the process working directory. Set `Database__Path` in `.env` to move it:

```bash
Database__Path=/var/lib/orgmonitor/orgmonitor.db
Database__SeedDemoData=false
```

The schema is created automatically on first start.

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
- `POST /api/devices` — add a manually tracked device (no agent required)
- `PUT /api/devices/{id}` — edit a device's name, hostname, address, type, OS, and tags
- `DELETE /api/devices/{id}` — remove a device from the inventory
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
