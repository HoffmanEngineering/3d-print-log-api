# AGENTS.md

This file provides guidance to coding agents working in this repository. `CLAUDE.md` references it,
so this is the single document to edit.

PrintLogApi is an ASP.NET Core Web API for [3dprintlog.com](https://3dprintlog.com), a 3D print
logging platform. Users manage printers, filaments, and print logs with image uploads.

This file covers only what is costly to work out from the code. Stack, layout, and conventions are
discoverable — read the `.csproj`, the directory tree, and `.editorconfig`.

## Commands

```bash
dotnet build --configuration Release
dotnet test --verbosity quiet   # re-run failures with --verbosity minimal
dotnet ef migrations add <MigrationName> --project=PrintLogApi
```

Open PRs with the `gh` CLI against `main`.

## Caching

Version-based invalidation through `ICacheVersionService` (singleton). Cache keys combine user ID +
version GUID + query parameters, so bumping a user's version invalidates every entry for that user
at once. Applied to `GetPrintSummary()` and `GetPrinterSummary()`; every create/update/delete must
invalidate, or callers keep reading stale summaries.

## Database

Migrations are auto-applied on startup in `Development` and `E2ETesting` only. Production applies
them from the deploy workflow (see Deployment) — never rely on startup migration there.

New migrations must be **backwards compatible** (additive only): the old app version is still
running against the database while migrations execute.

## MCP Server

The `/mcp` endpoint (Streamable HTTP, stateless, MCP revision 2026-07-28 via SDK 2.0.0) exposes
tools to agents in two classes: `PrintLogReadTools` (`McpRead`, `read:printdata`) and
`PrintLogWriteTools` (`McpWrite`, `write:printdata`). Both are creator-only — the user is always
token-derived, never a tool argument, and a foreign or missing id returns a uniform `not_found`
rather than an existence oracle.

Authorization is two-layered, and **the endpoint is the weaker layer**. The `"Mcp"` policy requires
a mapped internal user plus *at least one* data scope, so a read-only token legitimately reaches
`/mcp`. The read/write split is enforced per tool *class* by the SDK's authorization filter
(`AddAuthorizationFilters()`), which also hides write tools from a read-only token's `tools/list`.
Write-tool denial rests entirely on that filter — never assume the endpoint blocks it.

Do not "simplify" these; each is load-bearing and looks redundant:

- `options.Stateless = true` — now the SDK default, kept to document the invariant it relies on
  (no session state, no standalone SSE `GET`/`DELETE`).
- `cacheScope: private` on `tools/list` — the list varies by token scope, so a shared cache would
  disclose the write-tool surface to a read-only caller (`McpCachingHintsTests`).
- The assertions in `McpWritePolicyTests` — read their comments before editing; which status codes
  they accept is what makes them cover the tool layer rather than the endpoint.
- The call-tool filter in `Startup.ConfigureMcpServer` — the single choke point for tool errors and
  telemetry, so exceptions never reach a caller as detail.

No hard-delete tools. Every write invalidates `ICacheVersionService` after commit.

See the `adding-an-mcp-tool` skill before adding or changing a tool — it carries the checklist, the
domain rules, and the known gaps.

## Integration Testing

`WebApplicationFactory` over an in-memory SQLite database. See
`PrintLogApi.IntegrationTests/README.md`, and copy the shape of an existing test file.

### Gotcha: testing JWT-protected endpoints against a local signing key

When validating a real `JwtBearer`/`McpBearer` scheme against a locally-issued token (e.g. the
MCP audience-isolation tests), you MUST null out the metadata configuration after re-pointing the
options:

```csharp
options.Authority = null;
options.MetadataAddress = null;
options.ConfigurationManager = null; // <-- critical
options.TokenValidationParameters = new() { IssuerSigningKey = TestJwt.SigningKey, /* ... */ };
```

The built-in `JwtBearerPostConfigureOptions` already created a `ConfigurationManager` from the
original `Authority`, so setting `Authority = null` alone is not enough. If it survives, every
request performs an OIDC-metadata fetch to a non-existent tenant that DNS-times-out (~30-40s per
request), turning a 9-test suite into a 6-minute one and causing SDK MCP-client tests to fail on
their init timeout. See `CustomWebApplicationFactory.ConfigureLocalJwt`.

## Health Checks

- `/health` — **liveness**, and the path configured under *App Service → Monitoring → Health check*.
  Process-only, no dependencies, and it must stay that way: App Service pulls a failing instance
  from rotation and replaces it after a sustained failure, so a check that touched SQL would report
  every instance unhealthy during a database blip and turn a recoverable outage into an app-wide
  restart loop.
- `/health/ready` — **readiness**. Probes SQL Server and returns per-check JSON, for humans and
  post-deploy verification. Never point the platform's health check at it.

The JSON omits each check's exception and description on purpose — the endpoint is anonymous, and a
SQL connection failure message carries the server name and often the credentials it tried.

## Deployment

GitHub Actions, and the triggers differ in a way that matters:

- `ci.yml` — every push to `main` and every PR targeting `main`. Build and test only.
- `deploy.yml` — **only on pushing a `v*` tag**. Merging to `main` does not release; tag a commit
  to deploy it.

`deploy.yml` builds, tests, publishes, and produces two artifacts — a `migration-script` for review
and an `efbundle` to apply. The deploy job is gated on the `production` GitHub Environment, so it
waits for a required reviewer before running the bundle against the production database and
deploying to App Service. Review the `migration-script` artifact before approving.

To generate a migration script manually (e.g. for emergency patching):

```bash
dotnet ef migrations script <LastAppliedMigrationId> --project PrintLogApi --output migrations.sql --idempotent
```
