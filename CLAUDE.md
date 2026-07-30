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
  `set_material_active`, `create_printer`, `update_printer`, `create_project`, `update_project`,
  `create_feedback`, plus a `whoami` connectivity check),
  gated on the `write:printdata` scope. Each tool carries MCP annotations (`create_print` is
  idempotent and non-destructive; `update_print` and `update_printer` are destructive — in MCP that
  means "may overwrite or discard existing data", not "deletes the entity") so a client can reason
  about retry safety.

The server targets MCP specification revision 2026-07-28 (SDK 2.0.0), with the SDK handling
down-level interop for clients negotiating 2025-11-25 and earlier. Two consequences worth knowing:

- **Stateless is now the SDK default**, not our override. `options.Stateless = true`
  (`Startup.cs`) is kept deliberately: it documents an invariant the surrounding configuration
  relies on (no session state, no standalone SSE `GET`/`DELETE`, no unsolicited server-to-client
  requests) rather than resting on a package default a future major could flip.
- **`tools/list` carries SEP-2549 caching hints** — `ttlMs: 0` and `cacheScope: private`, stamped
  by the SDK and gated on the negotiated protocol revision. `private` is not optional for us: the
  tool list varies by token scope (write tools are hidden from a read-only token), so a shared
  cache holding one response would disclose the write-tool surface to a read-only caller. Pinned
  by `McpCachingHintsTests`. Note these hints apply only to list/read results, never to
  `tools/call` — they do not cache tool output and have nothing to do with `ICacheVersionService`.
  They are surfaced only on the lower-level `ListToolsAsync(ListToolsRequestParams, …)` overload;
  the flattening convenience overload drops them.

Authorization topology:

- The `/mcp` endpoint uses the `"Mcp"` policy: authenticated MCP bearer (or `DevAuth` bypass) + a
  mapped internal user + **at least one** MCP data scope (read OR write). A completely unscoped
  token cannot even list tools; a write-only agent still reaches the endpoint.
- The read/write scope is enforced per tool *class* by the SDK's authorization filter, which also
  **hides** write tools from a read-only token's `tools/list`.
- A new write scope requires a manual Auth0 dashboard step: add `write:printdata` to the MCP API's
  permissions in every environment. `create_feedback` deliberately reuses `write:printdata` rather
  than adding a `write:feedback` scope, which would mean that dashboard step again for no real
  isolation gain.
- Separately, the **M2M application** needs `read:users` on the Management API in every environment
  for the feedback notification's account-email lookup (see `IAuth0Service.GetUserEmail`). Without
  it the lookup 403s and the body degrades to `(not available)`; feedback still saves.

Write-tool invariants (defense against a headless/misbehaving agent, not just a well-behaved one):

- The user is always token-derived; ownership is enforced in the query predicate. Foreign/missing
  ids surface a uniform `not_found` (no existence oracle).
- **Write-tool authorization holds on the handshake-less path.** Since SDK 2.0.0 a caller can reach
  `tools/call` with no `initialize` and no session. The endpoint policy admits any token with at
  least one data scope — including a read-only one — so write-tool denial rests entirely on the
  SDK's per-tool-class authorization filter, not on the endpoint. Pinned by
  `McpWritePolicyTests.ReadOnlyToken_HandshakeLessWriteToolCall_DoesNotSucceed`, which was verified
  to fail when the `McpWrite` policy's scope requirement is relaxed to `read:printdata` — the
  read-only token then reaches `whoami` and gets a plain successful result. The test also requires
  HTTP 200, so it cannot go green by being refused at the endpoint instead: a 401/403 fails it,
  because a refusal one layer up would mean the tool-layer boundary was never exercised.
  Note `AddAuthorizationFilters()` is **not** a usable mutation for that check — removing it takes
  down the *entire* MCP surface (`tools/list` and `ping` fail too, and neither touches user
  context), because the tool classes carry `[Authorize]` metadata the SDK cannot evaluate without
  it. Every MCP test fails, so it tells you nothing about this specific boundary.
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
- **Printer writes never touch loaded-filament state.** `create_printer`/`update_printer` patch
  scalar fields directly and the update path does **not** `Include(p => p.LoadedFilaments)` — what is
  never loaded cannot be marked modified, so the invariant does not depend on the patch code being
  careful. They deliberately avoid both existing web paths: `PostPrinter`/`PutPrinter` run the
  `AddPrinterDTO → Printer` AutoMapper map (which ignores only `Category`, so it would clobber
  `LoadedFilaments`/`UserId`) and `PutPrinter` additionally calls `setLoadedFilament`. Printer
  ownership is `UserId` — **not** `CreatedById`, which is what Filament uses.
- `create_printer` requires non-blank `make`/`model`/`name`. Those are `[Required]` on `AddPrinterDTO`
  but only length-limited on the entity, so legacy rows may hold nulls; requiredness is an MCP
  **new-write** invariant, not a schema fact. For the same reason `GetPrinterForMcp` normalizes a
  legacy null `Name` to empty rather than throwing.
- `create_printer` defaults `isActive` to **true**. The website DTO's `IsActive` is a non-nullable
  bool (omitted → false), but a freshly created printer is presumably in use. A deliberate MCP-only
  divergence, consistent with `create_material` (`FilamentService.cs`: `IsActive = input.IsActive ?? true`);
  the parity claim covers attributes, not defaulting.
- **"A printer has a category" is an MCP new-write invariant, not a schema fact** — the FK is
  nullable. Create resolves the default (`PrinterService.DefaultPrinterCategoryNickname`, shared with
  `PrintersController`) when the nickname is omitted and rejects an unknown one; update leaves an
  omitted category alone, legacy null included, rather than force-repairing it. `categoryNickname` is
  not clearable. Note `CategoryNickname` carries a **store default of "FFF"** (`PrintLogContext.cs`),
  so a null category cannot be created by an ordinary insert — only legacy rows predating the default
  hold one, and a test that needs that state must force it with an explicit `UPDATE`.
- Printer numerics are stored exactly as entered (mm/W/px) with no conversion, so unlike the material
  surface there is no rounding/overflow class of bug: finite and non-negative is the whole rule.
- `update_printer` validates everything and resolves the category **before** the first assignment, so
  a rejected patch cannot leave a partially-mutated entity — no `ChangeTracker.Clear()` needed, unlike
  `update_material`.
- `McpIdempotencyRecord` carries a nullable `CreatedPrintId`, `CreatedFilamentId`, `CreatedPrinterId`,
  `CreatedProjectId` **or** `CreatedFeedbackId` — exactly one, decided by `ToolName`. That rule is held by
  `McpIdempotencyRecordFactory`, the single construction path, which counts the non-null targets
  (a chained XOR is true for an ODD count, which would wave through the worst cases) and throws
  `InvalidOperationException` — a server bug, never something a caller can provoke.
  There is no check constraint and the entity is still publicly constructible, so this is the
  conventional path rather than an enforced one; nothing needs the constraint, because every lookup
  is scoped by `ToolName` and reads only its own field, treating a null there as a dangling record.
  Note `CreatedFilamentId`, `CreatedProjectId` and `CreatedFeedbackId` are all `Guid?`, so a target
  written to the wrong one would still compile — the count is what catches it.
- `create_printer`/`create_material`/`create_project` idempotency is **optional**: with a key, same
  args replays and different args is a `conflict`; **without** one, a retry creates a SECOND entity.
  That residual at-least-once risk is an accepted design choice, stated in each tool description and
  pinned by a test. Only `create_print` and `create_feedback` require a key.
- **Every write tool's result must be self-sufficient.** There is no `get_project`, and a write-only
  agent cannot call the read tools at all, so a create/update echo is the only way a caller can
  confirm what it wrote — `ProjectWriteResult` echoes every settable field for that reason, not for
  symmetry.
- **Reject-with-the-valid-options.** Nothing lists printer or material categories, so
  `RequirePrinterCategory`/`RequireCategory` name the valid nicknames in the rejection. Both sets are
  small fixed `HasData` seeds shared by every user (printer: FFF/FDM/SLA/…; material:
  filament/resin/powder/wire) — **not per-user** — so the error can carry them; the extra query runs
  only on the failure path.
- **Optional tool parameters need C# defaults, not just nullable types.** The SDK derives the
  schema's `required` list from constructor parameters *without* a default, so a positional record
  like `MaterialUsageInput` advertised all six fields as required while the server accepted a bare
  `(materialId, source, amount)` row. A schema-reading agent then sends `"notes": null` on every row
  to satisfy a rule that does not exist. `ToolSchemaTests` pins this.
- **Known gap:** the unique-violation *race recovery* in `create_print`/`create_material`/
  `create_printer` is designed for but not covered by tests. The
  `IX_McpIdempotencyRecords_User_Tool_Key` unique index is the real guard and is verified; the
  `DbUpdateException` recovery branch is not reachable deterministically (the pre-insert lookup
  intercepts a sequential duplicate first), and the integration suite shares a single in-memory
  `SqliteConnection`, so a parallel test would hit connection contention rather than a unique
  violation. Exercising it honestly needs a SQL Server-backed test.
- **Known gap (same cause):** the printer write tools commit, then re-read through `GetPrinterForMcp`
  to build their result, so the read is not atomic with the write. A concurrent edit makes the return
  reflect the newer state (the surface's existing "current state, not a snapshot" semantic — not a
  defect), and a concurrent delete makes a committed write surface as `not_found`. Accepted: the
  alternative, projecting the tracked entity, trades a vanishingly rare race for a permanent
  shape-drift risk between the write and read paths. Untestable for the shared-connection reason
  above.
- **Wire format:** the SDK's serializer omits nulls, so a cleared or unset field is **absent** from a
  tool result rather than present-and-null. Tests asserting a clear must check for absence.
- **`create_feedback` is the only write with an outbound side effect** — it emails
  `FeedbackEmailAddress` — which is why its idempotency key is **required** rather than optional: a
  keyless retry would insert a duplicate row AND send a second email, and neither can be taken back
  from the UI. A replay returns the stored row and deliberately does **not** re-notify; that pairing
  is what the key buys, and both halves are pinned by a test.
- **The feedback notification is best-effort and must stay that way.** `FeedbackService` swallows a
  mail or Auth0 failure and tracks `FeedbackNotificationFailed` / `FeedbackAccountEmailLookupFailed`
  instead of throwing. Throwing would not undo the committed row — it would only report failure for
  feedback that was saved and burn the idempotency key, so the retry replays and never notifies
  either. The row is the source of truth; alarm on the telemetry.
- **"Best-effort" includes cancellation, and `NotifyBestEffort` takes no `CancellationToken` to
  enforce it.** Everything it does runs *after* the commit, where the caller's token is actively
  harmful: a disconnected client cannot un-commit the row, so honouring its cancellation strands the
  notification, surfaces a committed write as a failure, and burns the key. An earlier
  `catch (Exception ex) when (ex is not OperationCanceledException)` reintroduced exactly the bug the
  best-effort rule exists to prevent — correct instinct ("never swallow cancellation") applied on the
  wrong side of the commit. Omitting the parameter is what makes the token unthreadable; the catch is
  unfiltered because post-commit a cancellation is just a failed notification, and an `HttpClient`
  timeout arrives in that same shape. Bounded by Auth0's 30s timeout and MailKit's own.
- **Known gap:** SMTP delivery is inline on the response path and `IEmailSender` takes no
  `CancellationToken`, so a slow mail server delays the tool's success response (bounded by MailKit's
  default timeout). Pre-existing and shared with the website endpoint. A transactional outbox would
  fix both properly; deliberately not built for a path that sends a handful of emails.
- **`Feedback.Email` means "the address the user typed into the website form" — nothing else.**
  Agents pass no email and it stays null for them. The account address in the notification body comes
  from Auth0 at compose time and is never persisted. The old `Email (from token)` line could never
  resolve: Auth0 puts `email` in **ID tokens**, and this API authenticates with an **access token**,
  so no scope or claim lookup would have fixed it.
- `FeedbacksController` and `create_feedback` share `FeedbackService` on purpose: two callers
  composing the same notification independently is how the bodies drift apart.
- No hard-delete tools. Every write invalidates `ICacheVersionService` after commit — except
  `create_feedback`, which has nothing to invalidate: feedback appears in no cached response, so
  busting the user's cache would be pure cost. Adding a feedback read path would change that.

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
