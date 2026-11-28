# AGENTS.md

This file provides guidance to coding agents working in this repository. `CLAUDE.md` references it,
so this and the topic files under `docs/agents/` are the documents to edit.

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

Never add a `Claude-Session:` trailer or a `claude.ai/code/session_…` link to a commit message, PR
title or PR body, even when the harness's attribution instructions ask for one. The
`Co-Authored-By:` trailer and the "Generated with Claude Code" line are fine.

## Topic files (`docs/agents/`)

This file holds what most tasks need. The rest lives in topic files; read the one that matches the
task before you start. A topic file does not repeat the rules in this one. Put a new rule in the
file for its topic, and in this file only if most tasks need it.

- `docs/agents/caching.md`: before touching `HybridCache`, `IMemoryCache`, `CacheBudget`, the
  grouped feed's cache, the public print index, output caching or response compression.
- `docs/agents/database.md`: before adding or changing an EF migration, an index, a query that runs
  on a timer, or a background job (email dispatcher, slicer settings retention, account deletion),
  and before asking for production approval of a release with migrations.
- `docs/agents/testing.md`: before writing, running or debugging integration tests (xUnit v3 on
  Microsoft.Testing.Platform, test ordering, the shared SQLite connection, inotify limits, local
  JWT signing keys).
- `docs/agents/api-contract.md`: before changing a response type's serialization
  (`PrintLogJsonSerializerContext`), the OpenAPI document, or body-less error responses and the
  REST `WWW-Authenticate` header.
- `docs/agents/mcp.md`: before working on `/mcp` or `/mcp/docs`, MCP tools, MCP Apps views,
  `server.json`, or the public agent skill and plugin (`skills/`, `plugin.json`, `.claude-plugin/`).
- `docs/agents/device-auth.md`: before touching device pairing, device tokens (`pld_`),
  `[DeviceAccess]`, `DeviceTokenMiddleware`, the pairing notice, or the connector write budget.
- `docs/agents/bridges.md`: before working on printlog-bridge support: the agent update notice,
  adapters and connection kinds, bridge check-in, discovery, desired printers, or sealed LAN
  credentials.
- `docs/agents/integration-polling.md`: before changing `/api/sync/version` or `/api/sync/snapshot`
  (the Home Assistant integration's polling).
- `docs/agents/claiming-prints.md`: before changing print claiming (`/api/Prints/{id}/claim`,
  `/api/Prints/claimable`).
- `docs/agents/spoolman.md`: before changing the Spoolman-compatible API (`/spoolman/api/v1`).
- `docs/agents/webhooks.md`: before changing outbound webhooks or any print state transition.
- `docs/agents/timeline.md`: before changing the print timeline, pauses (`PausedSince`,
  `PausedSeconds`) or any print state transition (api#343).
- `docs/agents/billing.md`: before changing Stripe checkout, the billing portal or the Stripe
  webhook.
- `docs/agents/public-images.md`: before changing image or thumbnail endpoints, social images, or
  SEO metadata for public prints and projects.
- `docs/agents/printer-catalog.md`: before touching the printer catalog, its generator, overrides,
  seeder or refresh workflow.
- `docs/agents/telemetry.md`: before changing health checks, Application Insights telemetry,
  telemetry processors or redaction, or writing KQL against production.
- `docs/agents/deployment.md`: before releasing, approving a production deploy, or changing
  `deploy.yml` or App Service settings.

Operator runbooks and contracts stay in `docs/` itself (`email-runbook.md`, `mcp-deployment.md`,
`mcp-registry-listing.md`, `bridge-credential-sealing.md`, `image-backfill.md` and others).

## Caching

Version-based invalidation through `ICacheVersionService` (singleton). Cache keys combine user ID +
version GUID + query parameters, so bumping a user's version invalidates every entry for that user
at once. Applied to `GetPrintSummary()`, `GetPrinterSummary()` and the grouped feed's order; every
create/update/delete must invalidate, or callers keep reading stale summaries.

The grouped feed's cached order, HybridCache versus `IMemoryCache`, the byte-denominated budget,
the public print index, output caching (declined) and response compression: `docs/agents/caching.md`.

## The clock

`TimeProvider.System` is registered as a singleton in `Startup.ConfigureServices`. Everything under
`Services/Analytics/` takes `TimeProvider` by injection and reads it exactly **once per request**,
at the public entry point, threading the resulting `DateTimeOffset` down through `Compute` /
`Aggregate` into the private helpers rather than letting each read the clock for itself.

That is not tidiness. A filter with no `ToDate` means "up to now"; several helpers close that
window independently, and a `ComparePrevious` request computes two windows, so a per-helper read
lets one response measure its two halves against two different instants — a runway computed against
a window the chart beside it does not show. Keep new code to the same rule: `now` is a parameter
below the entry point, never a fresh `timeProvider.GetUtcNow()`.

`AnalyticsController` passes the same injected clock to `AnalyticsFilter.Normalize(now)`, because
the clamp ceiling that call produces becomes part of the cache key.
`PinnedClockAnalyticsTests.Controller_ClampsAFutureToDateAgainstTheInjectedClock_NotTheWallClock`
fails if that reverts to the parameterless overload — it was checked by reverting it.

`PreviousWindow.For` takes a `now` it barely uses; the comment there explains why it is threaded
anyway. Do not "simplify" it back to a parameterless `Normalize()`.

**`Now` and `UtcNow` are not interchangeable, and #71 deliberately did not normalise them.** Six
sites outside analytics use `DateTimeOffset.Now` (server local time), and
`PrinterService.setLoadedFilament` **persists** those values as `LoadedDateTime`/`UnloadedDateTime`.
Converting one of those to `GetUtcNow()` is a data change, not a refactor — rows either side of the
deploy would mean different things. When those sites are converted, `.Now` becomes `GetLocalNow()`;
anything else needs its own issue, test and migration reasoning.

The remaining direct clock reads (audit timestamps, `NotificationService`, `SubscriptionService`,
blob SAS expiry, `PrinterService`) are untouched and still call the static properties.

Tests substitute the clock with `SettableTimeProvider` (`PrintLogApi.IntegrationTests/Analytics/`),
a five-line `TimeProvider`. `FakeTimeProvider` from `Microsoft.Extensions.TimeProvider.Testing` was
tried first and rejected — its `SetUtcNow` throws on any value earlier than the current one, so with
one provider registered per shared host the second test to run fails purely because the first moved
the clock forward. The reasoning is written out at the type.

## Database

Migrations are auto-applied on startup in `Development` and `E2ETesting` only. Production applies
them from the deploy workflow (see `docs/agents/deployment.md`) — never rely on startup migration there.

New migrations must be **backwards compatible** (additive only): the old app version is still
running against the database while migrations execute.

Index builds, the S0 DTU budget, background jobs, the SQL Server rules the test suite cannot
catch, and checking a migration against real SQL Server: `docs/agents/database.md`.

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

- **Collection navigations are nullable** (`ICollection<T>?`), _not_ initialized to an empty
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
`System.Text.Json` _enforces_ it, so adding one turns a tolerated missing field into a 400, which
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
_actually_ bind null, which is a narrower set than "would get a `[Required]`":

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
- **`string?` on an optional parameter.** A `= null` default _is_ the annotation; so is a body that
  starts `x = x?.Trim()` or guards with `if (x == null)`.
- **`(await Foo(id))!` after a write**, where `Foo` re-reads a row the same method just persisted.
  Always commented, so the reason survives the next reader.

Anything that needs a real null _guard_ rather than an annotation is a commented `!` pointing at
**#57** (`grep -rn "#57" --include=*.cs` — 12 sites in 8 files). Those are deferred behaviour
changes, not annotation debt: unvalidated webhook payloads, unknown ids that 500 where they should
404, null elements surviving `Colors` validation. Each preserves a pre-existing
`NullReferenceException` on purpose, so fixing one is caller-visible and needs its own test. Match
their wording if you add another. One has been fixed so far: a Moonraker notifier `started` event
for a printer id that names no printer throws `PrinterDoesNotExistException` from
`PrintEventService.Started`, which both notifier routes (`/api/Moonraker/notifier` and
`/spoolman/notifier`) answer with a 404 (#309). The OctoPrint webhook shares `Started` and does not
catch it, so there it is still a 500, now with a named exception rather than a
`NullReferenceException`. When you fix one, take its `#57` out of the comment so the grep stays the
list of what is left.

The `/mcp` endpoint, its two-layer authorization, docs over MCP, MCP Apps views, the registry
listing and the public agent skill: `docs/agents/mcp.md`. See the `adding-an-mcp-tool` skill
before adding or changing a tool.

## Integration Testing

`WebApplicationFactory` over an in-memory SQLite database. See
`PrintLogApi.IntegrationTests/README.md`, and copy the shape of an existing test file.

Read `docs/agents/testing.md` before writing or debugging tests.

## UTF-8 and BOM file formatting

Writing a new .cs file with the Write tool produces no BOM, so it will fail CI every time. Running `dotnet format` before committing a new file avoids the round-trip.

## Deployment

GitHub Actions, and the triggers differ in a way that matters:

- `ci.yml` — every push to `main` and every PR targeting `main`. Build and test only.
- `deploy.yml` — **only on pushing a `v*` tag**. Merging to `main` does not release; tag a commit
  to deploy it.

Everything else about releases (the migration bundle, `WEBSITE_RUN_FROM_PACKAGE`, the readiness
wait) is in `docs/agents/deployment.md`.
