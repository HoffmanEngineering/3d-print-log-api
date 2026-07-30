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
