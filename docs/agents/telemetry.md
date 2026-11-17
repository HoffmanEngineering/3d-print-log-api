# Health checks and telemetry

## Health Checks

- `/health` — **liveness**, and the path that must be configured under _App Service → Monitoring →
  Health check_. **It was found unset (`healthCheckPath` null) on 2026-11-06** and the owner is
  setting it by hand; check it after any change to the App Service. Process-only, no
  dependencies, and it must stay that way: App Service pulls a failing instance from rotation and replaces it after a sustained failure, so a check that touched SQL would report
  every instance unhealthy during a database blip and turn a recoverable outage into an app-wide
  restart loop.
- `/health/ready` — **readiness**. Probes SQL Server and returns per-check JSON. `deploy.yml` polls
  it after every deploy (see `docs/agents/deployment.md`); nothing else does. Never point the platform's health
  check at it.

The JSON omits each check's exception and description on purpose — the endpoint is anonymous, and a
SQL connection failure message carries the server name and often the credentials it tried.

`GET /` answers an empty 200 (anonymous, not in OpenAPI). App Service's Always On pings it; before
it was mapped that was about 300 404s a day.

## Telemetry (Application Insights)

The instrumentation key in `appsettings.json` is production's, so any environment that doesn't
switch telemetry off sends there. Code: `Telemetry/`, wired in `Startup.ConfigureServices`.

- **Off in IntegrationTesting, E2ETesting and Development** (`TelemetryEnvironment`). Development
  opts in with `ApplicationInsights:EnableInDevelopment=true`; point the key at a non-production
  resource when you do. A developer machine's requests and its SQL calls to `localhost,1434` were
  found in the production resource. Tests that assert on telemetry re-enable it on an in-memory
  channel (`CustomWebApplicationFactory.CaptureTelemetry`).
- **SQL command text is kept only on slow (500 ms or more) or failed calls.**
  `EnableSqlCommandTextInstrumentation` is on so a DTU spike can be traced to a statement, and
  `SqlCommandTextTelemetryProcessor` clears `Data` on every other SQL dependency: there are about
  1.1M a month, and keeping all the text would push ingestion past the free allowance. The text is
  EF's parameterized SQL and the SDK records no parameter values; keep it that way, so never build
  SQL text from user values (interpolated `SqlQuery`/`ExecuteSql` is parameterized, `*Raw` with
  concatenation is not).
- **`NoiseTelemetryProcessor` drops successful rows that carry no information**: CORS preflights,
  the unread-count poll and its SQL, `GET /`. The only exception it drops is the MCP
  SDK's `UnsupportedProtocolVersionException` (a client offering 2026-07-28 through `initialize`;
  the SDK answers -32022 in a 200 and logs it at Warning). Match by exact type, not category, so
  real MCP errors stay visible.
- **Credentials in URLs are redacted by initializers**: the email token `t`
  (`EmailTokenRedactionInitializer`), spool tag UIDs (`TagUidRedactionInitializer`), and the
  `api_key` query parameter on every request (`ApiKeyRedactionInitializer`, which decodes each
  parameter name the way ASP.NET Core does, so `API_KEY` and `api%5Fkey` are caught too). A new
  query-string credential needs the same treatment.
- **Application Insights is the only telemetry sink.** The anonymous Prometheus `/metrics`
  endpoint and prometheus-net were removed; `/metrics` now answers 404 like any unknown route
  (`MetricsTests`). Count things with `TelemetryClient.TrackEvent`, and record a sampled or summed
  value with `TelemetryClient.GetMetric` (pre-aggregated per minute, e.g. `Email_OutboxPending`,
  `Spoolman_OpenSockets`, `Spoolman_UseMm`).

### Querying it (KQL)

- KQL: `first` and `last` are reserved words; alias columns differently.
