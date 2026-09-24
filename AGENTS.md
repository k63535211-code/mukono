# OrgMonitor agent notes

- The API is ASP.NET Core 8 in `src/OrgMonitor.Api`; the dashboard is Vite + TypeScript in `web`.
- SQLite is the source of persistence. `Database__Path` selects the database file; `Database__SeedDemoData=false` prevents demo rows.
- The schema is initialized in `SqliteMonitoringStore`; update that store and add a versioned migration strategy when changing persisted columns.
- Run the API with `dotnet run --project src/OrgMonitor.Api`, the dashboard with `cd web && npm run dev`, and the optional managed-device agent with `dotnet run --project src/OrgMonitor.Agent`.
- The Vite dev server proxies `/api` and `/health` to port 5080; the default frontend port is 5173.
- Secrets live in the gitignored `.env` at the repository root (`.env.example` is the committed template). The API loads it via `Configuration/DotEnv` before the configuration is built, and `npm run dev` reads it for `ORGADMIN_API_KEY`. Keys use ASP.NET's `Section__Key` form; a shell-exported variable always overrides the file.
- Agent heartbeats use `POST /api/agents/heartbeat` and `X-Agent-Key` when `Monitoring:AgentApiKey` is configured; management routes use `X-Admin-Key` when `Monitoring:AdminApiKey` is configured.
- Network monitoring is explicit TCP/HTTP/HTTPS probing. Do not add broad scanning, covert tracking, traffic interception, or credential collection.
- The current dashboard uses vanilla TypeScript; keep frontend dependencies minimal and preserve the Vite-only frontend boundary.
