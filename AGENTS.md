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

## Nullable Reference Types

The migration (#46) is **done and closed**. Both projects are on `<Nullable>enable</Nullable>` with
`<WarningsAsErrors>nullable</WarningsAsErrors>`, so a new nullable warning fails the build rather
than accumulating. There are no carve-outs and no per-file `#nullable` directives outside
`Migrations/` (where EF's generated `#nullable disable` stays) — the project setting is the single
source of truth, so do not add a header to a new file.

When a nullable warning fires on a value you know is non-null, the fix is to **move the proof** to
where C# flow analysis sees it — a `[MemberNotNullWhen]` attribute, an `is { } x` pattern, a
`Select`+`OfType` unwrap — never to suppress it. Most of the CS8629 sweep in #39 was exactly that:
the proof already existed, but it sat in a `bool` local, a computed property, or a `Where` clause in
a prior lambda.

Two caller-visible behaviour changes came out of that sweep. Both are still true and neither is
obvious from the code:

- `RequireMcpConvertibleUsage` now rejects a half-populated `source`/`amount` pair itself. The check
  existed only in `PrintLogWriteTools.ValidateUsageRow`, and `IPrintService` is reachable without
  the tool layer — so a direct caller got an `InvalidOperationException` where it now gets
  `invalid_arguments`.
- `FilamentService`'s Length branch **clears** the derived fields when the material tracks no
  diameter, matching its own Volume and Weight siblings. On an update path that removes a
  previously-stored `InitialNominalWeightMg`/`VolumeMl` rather than leaving it stale.
  `PrintService`'s siblings simply skip, so its Length branch does too. The asymmetry follows each
  file's existing convention deliberately.

Three rules bound how far that rewriting goes, and they still apply:

- **Inside an EF expression tree the only permitted fix is `!`.** It is erased at compile time, so
  the generated SQL is unchanged; a pattern or `OfType` rewrite there changes the translation.
- **`Select`+`OfType<T>()` is only for an id-only projection.** Where the `Where` unwraps an id as a
  dictionary or grouping key while the value selector still needs the row, `OfType` discards the
  rest of the row — those sites take `!` with a comment saying why.
- **Prefer a throwing fallback to a `when` clause in a `switch` arm.** A `when` that fails silently
  falls through to the next arm; in the measurement conversions that meant converting millimetres as
  if they were grams, which is worse than throwing.

Annotations in `Models/` are load-bearing, not cosmetic:

**On an entity, a `?` is a database column decision.** EF Core infers required/optional from the
annotation, so dropping a `?` silently makes a nullable column NOT NULL. That does not fail the
build — it fails the migration against production data. The nullability of every entity property
was taken from `PrintLogContextModelSnapshot.cs` (`.IsRequired()` present or not), which is the
ground truth for what is already deployed. CI runs
`dotnet-ef migrations has-pending-model-changes` to catch a mistake; keep it green.

Two conventions in `Models/`:

- **Collection navigations are nullable** (`ICollection<T>?`), *not* initialized to an empty
  collection. This deviates from the usual EF guidance on purpose: an unloaded navigation really is
  null today, and initializing it would change "not loaded" from a `NullReferenceException` into a
  silent empty result. That is a runtime behaviour change, and it does not belong in an annotation
  change. Revisit it deliberately, on its own, if wanted.
- **Required reference navigations and required scalars use `= null!`**, which keeps the property
  non-nullable for EF while staying a no-op at runtime (the field was already null).

### DTOs

Every reference-typed property under `Models/DTOs/` is nullable. That is deliberate and applies to
responses as well as requests:

- **A request DTO is not a domain model.** It is deserialized across a trust boundary, so the
  declared type says nothing about what actually arrives. Non-nullable there would only remove the
  compiler's warning, not the null. Enforce required-ness with validation, never with the
  annotation.
- **Response DTOs mirror the entity they are mapped from**, and the entities are nullable almost
  everywhere, so nearly every response property is nullable on the merits too.

Do **not** reach for `= null!` or `required` here. `= null!` is for framework-initialized members;
on a DTO it asserts something about deserialized data that nothing enforces. `required` is worse —
`System.Text.Json` *enforces* it, so adding one turns a tolerated missing field into a 400, which
is a runtime behaviour change dressed as an annotation.

Positional records (`Analytics/`, `ConnectedAgentDto`) keep their non-nullable parameters: the
constructor is the enforcement, and every one of them is built server-side. #44 corrected the five
where that premise turned out to be false — the service provably passes null on a documented
branch, so the parameter is now nullable: `HighlightRef.Id`/`Label`, all four of
`OverviewHighlights`, `AccuracyGroup`/`AccuracyCallout.Label`, `PrintCostRef.Title`,
`MaintenanceEvent.Category`/`Description`, and `ActivityResponse.Currency`. Check the call sites
before adding a non-nullable parameter to one of these.

The rule is uniform on purpose, and it does over-nullable a handful of properties — the ones with a
single construction site that provably assigns them (`GetUploadUrlResponse`,
`GetDownloadUrlResponse`, and the three `Filament*Dto` string arrays). Annotating those accurately
costs a `= null!` each, which reintroduces exactly the idiom the paragraph above bans and turns a
rule you can apply by reading one property into one that needs a call-site audit. It buys nothing
today: no code dereferences those properties, and Swagger does not read nullability
(`SupportNonNullableReferenceTypes` is off), so the published contract is identical either way.
Revisit if NRT-aware schema generation is ever turned on.

### Bound parameters and MVC's implicit `[Required]`

With the annotation context on, MVC attaches an implicit `[Required(AllowEmptyStrings = true)]` to
every non-nullable **reference** type it binds. A request that omits that value stops binding null
and starts returning 400, and no compiler diagnostic says so. #41 suppressed this; #45 removed the
suppression and adopted the behaviour.

The exposure was in **action parameters**, not the DTOs — `[FromQuery] string searchText` and its
kin, which bind null on every request that omits them. Six such parameters are now `string?`
(`searchText` on the filament, printer, print and maintenance summary endpoints, plus
`filterByMaterialCategoryNickname` and `filterByStorageLocation`), threaded down through
`IFilamentService`, `IPrinterMaintenanceService` and the two cache-key helpers.

`ImplicitRequiredInferenceTests` is the permanent guard. It reflects over every action and fails
with the offending member named. Read its comments before adding to it — it reports only what can
*actually* bind null, which is a narrower set than "would get a `[Required]`":

- A **complex type bound from the query** is always constructed by the binder, so `PagedRequest`,
  `SortRequest<T>` and `AnalyticsFilter` are never null.
- A **collection** binds empty, which satisfies `[Required]`. This is why
  `PrinterMaintenanceService` reads `filterByPrinterIds.Length` unguarded.
- A **property with an initializer** keeps it when the field is omitted (`AddFilamentDto.Colors =
  new()`).
- A **route value** is present or the route did not match, so it 404s before validation.

Enumerating on the annotation alone flagged 90 members; 6 could take null. Do not annotate the
other 84 nullable to quiet a diagnostic — that weakens declarations that are correct.

### Services, MCP tools and controllers

Annotated in #44, under one rule: **the annotation states what the code already does**, and
nothing about behaviour changed. Four idioms account for nearly all of it.

- **`!` on an optional navigation inside an EF or AutoMapper expression** (`p.FilamentUsage!.Sum(…)`,
  `.Include(p => p.Images!)`, `.ThenInclude(pf => pf.Filament!)`). These are translated to SQL and
  never dereferenced in process. Do not "fix" one with `?.` — that would change a LEFT JOIN into a
  skipped call.
- **`Task<T?>` on anything backed by `FirstOrDefaultAsync`/`SingleOrDefaultAsync`.** CS8603 is the
  one to be strict about; never `return null!` to keep a return type non-nullable. Note that some
  MCP reads throw `McpToolException.NotFound` instead of returning null (`GetPrinterForMcp`) —
  read the body, do not assume from the name.
- **`string?` on an optional parameter.** A `= null` default *is* the annotation; so is a body that
  starts `x = x?.Trim()` or guards with `if (x == null)`.
- **`(await Foo(id))!` after a write**, where `Foo` re-reads a row the same method just persisted.
  Always commented, so the reason survives the next reader.

Anything that needs a real null *guard* rather than an annotation is a commented `!` pointing at
**#57** (`grep -rn "#57" --include=*.cs` — 14 sites in 9 files). Those are deferred behaviour
changes, not annotation debt: unvalidated webhook payloads, unknown ids that 500 where they should
404, null elements surviving `Colors` validation. Each preserves a pre-existing
`NullReferenceException` on purpose, so fixing one is caller-visible and needs its own test. Match
their wording if you add another.

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

## JSON serialization

`PrintLogJsonSerializerContext` (#67) supplies compile-time metadata for the highest-volume
responses: the two cached summary lists and the six analytics tabs. It is **partial by design** and
is `Insert`ed at position 0 of `JsonSerializerOptions.TypeInfoResolverChain`, with ASP.NET Core's
`DefaultJsonTypeInfoResolver` still behind it. Assigning `TypeInfoResolver` instead would drop that
fallback, and the failure surfaces on some unrelated endpoint rather than at the edit.

Adding a `[JsonSerializable]` root is the whole change — the generator walks the type graph, so
nested types need no attribute. Do **not** add `required` or `[JsonRequired]` to make a type fit;
see the DTO rules above, both are enforced by `System.Text.Json` and turn a tolerated missing field
into a 400. `JsonSourceGenerationTests` pairs each root with the endpoint that produces one and
asserts the generated payload is byte-identical to the reflection-produced one.

Two non-obvious facts, both verified rather than assumed:

- **MVC serializes through a *copy* of the registered options.**
  `SystemTextJsonOutputFormatter` substitutes `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` when
  `JsonOptions` leaves `Encoder` null, which this app does. So a real response body escapes `+` and
  `<` differently from anything serialized with the options DI hands out, and a test that compares
  the two must reproduce the copy.
- **The generated fast-path writer is unreachable here, and that is fine.** It requires
  `Encoder` to be null, which the copy above rules out for every MVC response. What the context
  buys is the metadata path — compile-time property discovery and build-time member accessors in
  place of runtime reflection and IL emit. The `[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]`
  on the context is not what enables that; it only keeps `Default.Options` from being PascalCase
  for anyone serializing through the context directly.

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
- `/health/ready` — **readiness**. Probes SQL Server and returns per-check JSON. Nothing polls it
  automatically — it is for manual checks, and is what a post-deploy smoke test should call if one
  is added to `deploy.yml`. Never point the platform's health check at it.

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
