# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

PrintLogApi is an ASP.NET Core 10.0 Web API for [3dprintlog.com](https://3dprintlog.com), a 3D print logging platform. Users manage printers, filaments, and print logs with image uploads.

## Build & Test Commands

```bash
# Build
dotnet build --configuration Release

# Run tests (use --verbosity quiet to minimize output; re-run with --verbosity minimal on failure)
dotnet test --verbosity quiet

# Run specific test project
dotnet test PrintLogApi.IntegrationTests

# EF Core migrations
dotnet ef migrations add <MigrationName> --project=PrintLogApi
dotnet ef database update  #Migrations also happen when the project runs
```

## Technology Stack

- **Framework**: ASP.NET Core 10.0
- **ORM**: Entity Framework Core 10.0 (SQL Server primary, SQLite for tests)
- **Authentication**: Auth0 (JWT Bearer) + Custom API Key authentication
- **Storage**: Azure Blob Storage for print images
- **Caching**: IMemoryCache with version-based invalidation
- **Mapping**: AutoMapper
- **Monitoring**: Application Insights + Prometheus

## Architecture

```
PrintLogApi/
├── Controllers/          # API endpoints (Prints, Printers, Filaments, etc.)
├── Services/             # Business logic (interface + implementation pairs)
├── Models/
│   ├── DTOs/             # Data transfer objects organized by domain
│   └── [Entity models]
├── Authentication/       # Auth0 + API Key middleware and handlers
├── Profiles/             # AutoMapper mapping profiles
├── Extensions/           # Helper extension methods
└── Migrations/           # EF Core migrations
```

## Key Patterns

### Service Layer
Services use interface-based design registered as Transient in `Startup.cs`:
```csharp
services.AddTransient<IPrintService, PrintService>();
```

### DTOs
Separate DTOs per operation type in `Models/DTOs/{Domain}/`:
- `Add{Entity}Dto` - Creation
- `Edit{Entity}Dto` - Updates
- `{Entity}DetailDto` - Single item response
- `{Entity}SummaryDto` - List item response

### Authentication
Dual authentication supported:
- JWT Bearer (Auth0)
- API Key via `X-Api-Key` header or `api_key` query param

Get user ID in controllers:
```csharp
var userId = this.User.FindFirst(ClaimTypes.NameIdentifier).Value;
// Or use extension: User.GetUserId()
```

### Caching Strategy
Version-based cache invalidation using `ICacheVersionService` (singleton):
- Cache keys include: user ID + version GUID + query parameters
- Applied to `GetPrintSummary()` and `GetPrinterSummary()` endpoints
- Invalidated on Create/Update/Delete operations
- Memory limit: 8192 units with 25% compaction

### Database
- SQL Server with connection retry (5 retries, 30s max delay)
- Split queries used for complex operations (see PR 229, 235)
- Migrations auto-applied on startup in `Development` and `E2ETesting`; Production applies migrations via the pipeline (see Deployment)

## MCP Server

The `/mcp` endpoint (Streamable HTTP, stateless) exposes tools to agents, split into two tool
classes by capability:

- **`PrintLogReadTools`** (`[Authorize(Policy = "McpRead")]`) — read tools, gated on the
  `read:printdata` scope.
- **`PrintLogWriteTools`** (`[Authorize(Policy = "McpWrite")]`) — write tools (`create_print`,
  `update_print`, `create_material`, `update_material`, `adjust_material_remaining`,
  `set_material_active`, `create_project`, `update_project`, plus a `whoami` connectivity check),
  gated on the `write:printdata` scope. Each tool carries MCP annotations (`create_print` is
  idempotent and non-destructive; `update_print` is destructive) so a client can reason about
  retry safety.

Authorization topology:

- The `/mcp` endpoint uses the `"Mcp"` policy: authenticated MCP bearer (or `DevAuth` bypass) + a
  mapped internal user + **at least one** MCP data scope (read OR write). A completely unscoped
  token cannot even list tools; a write-only agent still reaches the endpoint.
- The read/write scope is enforced per tool *class* by the SDK's authorization filter, which also
  **hides** write tools from a read-only token's `tools/list`.
- A new write scope requires a manual Auth0 dashboard step: add `write:printdata` to the MCP API's
  permissions in every environment.

Write-tool invariants (defense against a headless/misbehaving agent, not just a well-behaved one):

- The user is always token-derived; ownership is enforced in the query predicate. Foreign/missing
  ids surface a uniform `not_found` (no existence oracle).
- `create_print` is idempotent via `McpIdempotencyRecord` (unique index on user+tool+key), race-safe
  through unique-violation replay, and never mutates printer loaded-state (it does NOT call
  `setLoadedFilament`). Idempotency is **payload-bound**: the record stores a SHA-256
  `RequestFingerprint` of the caller's arguments (`McpRequestFingerprint`, length-prefixed so a field
  value cannot forge a boundary). Same key + same args replays; same key + **different** args is a
  `conflict`. A null fingerprint (legacy row) replays without comparison. Strings are canonicalized
  (trimmed) **once in the service, before both hashing and persistence** — the fingerprint hashes
  values exactly as given, so it can never assert two calls are equivalent while storing different
  rows. Keep it that way: normalizing inside the fingerprint alone reintroduces that split.
- `create_print` and `update_print` return the full `PrintDetailResult`, so a **write-only** agent can
  verify what it wrote without holding the read scope.
- `update_print` changes only the fields passed. Nullable fields are cleared by naming them in
  `clear` (`fileName`, `url`, `notes`, `startedAt`, `durationSeconds`, `estimatedDurationSeconds`,
  `projectId`); setting and clearing the same field is `invalid_arguments`. It validates everything
  before mutating, so a rejected edit leaves the print untouched.
- Material amounts use `{ source, amount }` and/or `{ estimatedSource, estimatedAmount }` pairs
  (Weight g / Length mm / Volume ml) converted via the existing measurement helpers; a row must carry
  at least one complete pair. Convertibility is checked on the **input rows** before persisting:
  Length usage requires a diameter-tracking material and Volume requires density; otherwise
  `invalid_arguments`. Convertibility is validated using the **same rounding the persistence path
  applies**, so an amount outside the recordable range — below 1 mg or beyond the int milligram
  column — is rejected rather than silently stored as 0 (which reads back as "unset") or overflowing.
- `viewStatus`/`allowComments` fall back to the user's saved settings when omitted (a malformed or
  undefined stored value falls back to Private / false); `allowFileDownloads` defaults to false.
- `adjust_material_remaining` rejects results below zero or above original capacity (no override).
- `create_material` idempotency is **optional**: with an `idempotencyKey`, same key + same args replays
  and same key + different args is a `conflict`; **without** one, a retry creates a SECOND material.
  That residual at-least-once risk is an accepted design choice, stated in the tool description.
  `McpIdempotencyRecord` carries a nullable `CreatedPrintId` **or** `CreatedFilamentId` — exactly one,
  decided by `ToolName`. Nothing enforces that; every lookup is scoped by `ToolName` and reads only
  its own field, treating a null there as a dangling record.
- `update_material` does **not** reuse `UpdateFilament`: that method loads via `GetFilamentById` with no
  creator filter (a cross-user edit hole) and never invalidates the cache. The MCP path uses the
  combined ownership predicate, validates everything before mutating, and invalidates after commit.
  A rejected patch clears the change tracker, so half-applied mutations can never be flushed later.
- Material capacity is **source-authoritative**, mirroring the website: `Source` names the field the
  user entered and the fill derives weight from it. Editing density/diameter on a Length/Volume
  material therefore recomputes its weight and its remaining-by-weight — documented in the tool
  description, not a bug. `adjust_material_remaining` is the tool for changing quantity.
- Every material capacity conversion goes through `McpMaterialConversion.RequireMgInRange` BEFORE the
  `long` cast. `MeasurementUtilities` casts **unchecked**, so an unguarded huge density or amount
  would store garbage rather than throw. The guard runs on the post-patch entity, so a density-only
  edit that overflows is caught too, and capacity passes `minMg: 1` — a capacity rounding to 0 is a
  material claiming a tracked capacity of nothing, not an empty one.
- A **Length source requires a diameter**. `UpdateFilamentMeasurements` only early-returns for
  diameter-*tracking* categories, so a resin with a Length source would reach `DiameterMm.Value` and
  throw. Both write paths reject that combination rather than defaulting the diameter.
- Clearing `colorHex` or `colors` clears **both**: the entity keeps `ColorHex` synced to `Colors[0]`, so
  clearing one alone lets a stale swatch resurrect it. On create, both fields are resolved *before*
  `AddFilament` sees them — it treats an empty `Colors` as absent and rebuilds it from `ColorHex`.
- No hard-delete tools. Every write invalidates `ICacheVersionService` after commit.

See the `adding-an-mcp-tool` skill for adding tools.

## Integration Testing

Integration tests use `WebApplicationFactory` with SQLite in-memory database. See `PrintLogApi.IntegrationTests/README.md` for full documentation.

### Key Components
- `CustomWebApplicationFactory` - Configures SQLite and test authentication
- `IntegrationTestSeeder` - Seeds minimal test data (user, printers, filaments, prints)
- `TestAuthHandler` - Handles test authentication via `X-Test-User-Id` header

### Writing Tests
```csharp
public class MyTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _httpClient;

    public MyTests(CustomWebApplicationFactory factory)
    {
        _httpClient = factory.CreateClient();
    }

    [Fact]
    public async Task AuthenticatedEndpoint_ReturnsSuccess()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/MyEndpoint");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        var response = await _httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
```

### Test Data Access
- `IntegrationTestSeeder.TestUserOAuthId` - OAuth ID for test authentication header
- `IntegrationTestSeeder.TestUserId` - Internal user ID (populated after seeding)
- `IntegrationTestSeeder.TestPrinterId` - Internal printer ID (populated after seeding)

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

## Code Style

Per `.editorconfig`:
- 4-space indentation
- Braces on new lines (`csharp_new_line_before_open_brace = all`)
- `var` preferred when type is apparent
- System usings sorted first
- PascalCase for constants

## Pull Requests

Use the GitHub CLI to create PRs:

```bash
# Create a pull request
gh pr create \
  --title "<title>" \
  --body "<body>" \
  --base main \
  --head <branch-name>

# List open PRs
gh pr list

# View a PR in the browser
gh pr view --web
```

## Deployment

Azure Pipelines deploys to Azure App Service (`3d-print-log-api-prod`) on main branch commits.

The pipeline has two stages:

1. **Build** — builds, tests, publishes the app, generates a SQL migration script artifact for review, and builds an `efbundle` for applying migrations.
2. **Deploy** — waits for manual approval (email sent to csh.hoffman@gmail.com), then runs the `efbundle` against the production database before deploying the app.

The `migration-script` artifact is available in the pipeline run's Artifacts panel and should be reviewed before approving the deployment.

To generate a migration script manually (e.g. for emergency patching):
```bash
dotnet ef migrations script <LastAppliedMigrationId> --project PrintLogApi --output migrations.sql --idempotent
```

New migrations must be **backwards compatible** (additive only) since the old app version runs against the database while migrations execute.
