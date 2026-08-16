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

### HybridCache for compute-on-miss, IMemoryCache for counters (#68)

Everything that computes a value on a miss goes through `HybridCache.GetOrCreateAsync`, for
stampede protection: concurrent callers on one key await a single computation. The cold window is
not hypothetical — a version bump invalidates all of a user's entries at once, so their misses
arrive together. Converted: both summary endpoints, all six analytics tabs, `ClaimsTransformer`
and `UserApiKeyService.GetUserIdByApiKey`.

The version GUID stays in the key exactly as before. No tag-based eviction, and **no L2** — L1-only
was the scope, and a distributed store is a separate decision.

Three sites stay on `IMemoryCache` deliberately, each with the reasoning at the site:

- **`CacheVersionService`** — it is the _source_ of the GUIDs every HybridCache key is built from,
  its contract is synchronous, and the "computation" on a miss is `Guid.NewGuid()`. Nothing to
  deduplicate.
- **`ApiKeyMiddleware`'s failed-attempt counter** and **`UserApiKeyService`'s last-used throttle
  flag** — counters and flags, not caches. The value carries no information; its existence is the
  signal.
- **`Auth0Service`** — #68 listed it as a candidate and the code disagrees. Its TTL comes from the
  token's own `expires_in`, which is only known _inside_ the factory, and `HybridCacheEntryOptions`
  is fixed before the factory runs with no way to amend it after. Converting would swap a
  token-derived expiry for a guessed constant. Its semaphore already provides exactly the stampede
  protection HybridCache would add.

**The memory budget is now denominated in bytes, and that was forced, not chosen.** HybridCache
stores L1 entries in the DI-registered `IMemoryCache` — there is no second cache and no second
budget — but it charges each entry the **serialized byte length** of its payload as `Size`, and
that is not configurable. The old `SizeLimit = 8192` was in nominal ~1KB units; left alone it would
have capped the whole process cache at 8 KB, which one print summary exceeds. `CacheBudget`
carries the constants and the full reasoning; anything still writing to `IMemoryCache` directly
must charge bytes too, or the two halves of one budget mean different things.
**Total in-process cache ceiling after the change: 8 MiB**, the same ceiling the old units
intended.

Two consequences worth knowing before editing:

- **Cached response types carry `[ImmutableObject(true)]`** (`PagedList<T>`, the six analytics
  responses). That attribute is inert at runtime and exists solely to tell HybridCache it may share
  the stored instance. Without it a cache _hit_ pays a full JSON deserialize — precisely the cost
  #66 declined to spend on the serialize side, landing on endpoints whose expensive part the cache
  already skips. `CachingConfigurationTests` enumerates the analytics responses off the
  controller's own actions, so a seventh tab fails there by name rather than silently regressing.
  Treat anything read out of the cache as read-only.
- **Sliding expiration is gone.** HybridCache offers absolute expiry only, so entries that paired a
  sliding window with a longer absolute cap are now flat (15 min for summaries and analytics,
  24 h for the claims and API-key lookups). The cost is one extra query per hot key per window.
- **Every cache factory runs through `CachedComputation`, and must.** Stampede protection means
  one caller's factory produces the value all the others receive, which creates two ways for a
  single aborted request to break healthy ones — neither of which existed when every caller ran
  its own query:
  - Its **scoped services** are disposed when its pipeline unwinds, leaving the shared work on a
    dead `DbContext`. So the factory resolves what it needs from a scope `CachedComputation` owns,
    never from the instance injected into the controller or service.
  - Its **cancellation token** fires the moment it aborts. A factory that observes the originating
    request's token cancels the shared computation and hands that cancellation to every joiner.
    Use the token HybridCache passes the factory; it is cancelled only once every joiner has left.
    Verified against 10.0.0 and pinned by `CachingConfigurationTests`.

  Because the scope is disposed as soon as the factory returns, a factory must materialise its
  result — returning a lazily-enumerated query would reach into a scope that is already gone.

### Output caching was evaluated and declined (#66)

`AddOutputCache` is deliberately **not** in the pipeline. It was proposed to skip the JSON
serialization that every `IMemoryCache` _hit_ still pays, and it was measured rather than argued
about. Do not add it back without new numbers.

The reason it loses is that response compression, added in the same change, moved the goalposts.
A cache hit's remaining cost is serialize-then-compress, and brotli at quality 1 costs ~0.15 ms on
a 52 KB summary — the same order as the serialization output caching would remove. So the ceiling
on the win is roughly half of an already-sub-millisecond step, on endpoints whose expensive part
(the SQL aggregation) is _already_ skipped by the existing cache. Caching the compressed bytes
instead would beat that, but only by varying on `Accept-Encoding` on top of everything below.

What it would cost to get there:

- **The framework's own safety guard has to be switched off.** `OutputCache`'s `DefaultPolicy`
  refuses to cache a request that is authenticated or carries an `Authorization` header. Every
  endpoint worth caching here is authenticated, so adopting output caching means writing a custom
  policy that deliberately disables that check — and then re-deriving the tenant in the cache key
  by hand. A mistake there is a cross-user data leak, not a stale read.
- **`GetPrintSummary` is `[AllowAnonymous]` and takes a `userId` query parameter**, so its response
  varies by _both_ the target user and the caller. The key would need both, plus the target user's
  version GUID, plus `Accept-Encoding`.
- **A second, untracked memory budget.** `IMemoryCache` here is capped at 8192 units
  (`Startup.ConfigureServices`). The output cache store has its own default 100 MB limit that the
  existing budget knows nothing about, holding a serialized copy of objects already cached.

Revisit only if the shape changes — a distributed output cache store, cookie or session
authentication, or a profile showing serialization is actually material. Any revival must still
satisfy the two rules the original issue set: vary by `User.GetUserId()`, and participate in
`ICacheVersionService` invalidation rather than relying on a TTL.

## Response Compression

Brotli and Gzip, wired in `Startup.ConfigureResponseCompression` and placed in the pipeline
immediately **inside** `UseClientAbortHandling`. Both of those details carry reasoning that is not
obvious and is written out in full at those two sites — read them before changing either.

The binding ordering constraint is that compression replaces `IHttpResponseBodyFeature`, so it must
sit outside anything that writes a body. Its position relative to `UseClientAbortHandling` is the
weaker half of the decision and the comment there says so explicitly: `FinishCompressionAsync` runs
inside the middleware's `try`, **not** a `finally`, so a downstream abort skips finalization
entirely. Do not restate the old "flush throws from a finally" rationale — it was wrong, and it was
checked against `dotnet/aspnetcore` `release/10.0`.

The two things most likely to be "tidied" into a regression:

- **`EnableForHttps = true` is not the framework default.** It is a considered BREACH decision that
  rests on this API being token-authenticated, so no request can be forged with the victim's
  ambient credentials. **If cookie or session authentication is ever added, this must be revisited
  before that ships** — start at `POST /api/UserApiKeys`, the one response that returns a secret
  alongside caller-supplied text.
- **Both providers are on `CompressionLevel.Fastest`, and raising either is a CPU decision, not a
  tuning preference.** The measurements are in the doc comment. Brotli `SmallestSize` is a 600x CPU
  increase for three percentage points — never use it on a request thread. Gzip `Optimal` is the
  more tempting one (35% fewer bytes for 0.2 ms) and was deliberately rejected: gzip is reached
  only by clients that cannot do brotli, but it is also whichever codec an attacker names in
  `Accept-Encoding`, so the extra CPU is spent mostly on adversaries. That trade is only as good as
  the bound on response size, and there is none — `PagedRequest.PageSize` is an unconstrained `int`
  that flows into `Take()`. Capping it is a caller-visible change and belongs in its own issue.

`text/event-stream` is deliberately absent from `MimeTypes`: compressing a streaming body buffers
it, and `/mcp` negotiates that content type. `ResponseCompressionTests` pins that, the encoding
negotiation, the compression levels, and that compressed bytes decode back to the uncompressed
response. One test there is load-bearing in a way that is easy to miss: `Compression_AppliesOverHttps`
drives an `https://` base address, because every other test runs over plain HTTP where compression
happens whatever `EnableForHttps` says — revert that option and it is the _only_ test that fails.

One thing `UseHttpMetrics` readers should know: the codec runs during the endpoint's `WriteAsync`
calls, so `http_request_duration_seconds` **includes** compression. prometheus-net 8.2.1 has no
response-size metric, so no byte count is distorted.

## The clock

`TimeProvider.System` is registered as a singleton in `Startup.ConfigureServices`. Everything under
`Services/Analytics/` takes `TimeProvider` by injection and reads it exactly **once per
computation**, threading the resulting `DateTimeOffset` into the private helpers rather than letting
each read the clock for itself. That is not tidiness: a filter with no `ToDate` means "up to now",
several helpers close that window independently, and two reads a query apart could disagree — a
runway computed against a window the chart beside it does not show. `AnalyticsController` passes the
same injected clock to `AnalyticsFilter.Normalize(now)`, because the clamp ceiling that call
produces becomes part of the cache key.

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
a mapped internal user plus _at least one_ data scope, so a read-only token legitimately reaches
`/mcp`. The read/write split is enforced per tool _class_ by the SDK's authorization filter
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
into a 400.

`JsonSourceGenerationTests` covers this from two directions. The structural test enumerates the
closure off the context's own generated `JsonTypeInfo<T>` properties — so it needs no maintenance
when a root's graph changes — and compares member names, order, types, accessors and required-ness
against the reflection resolver for all ~100 types. The endpoint test additionally pairs each root
with a URL that produces one and asserts the body is byte-identical either way; that pairing _is_
hand-maintained, so a new `[JsonSerializable]` root needs a new row in `HotResponses()`.

Worth knowing when judging how much test coverage a change here needs: a resolver supplies metadata
only. Converters are shared by both paths, so the two cannot disagree on how a `decimal`, `DateOnly`
or `DateTimeOffset` is _formatted_ — only on which members exist, their names and their order.
Fixture variety in values proves nothing here; structural comparison proves everything.

Two non-obvious facts, both verified rather than assumed:

- **MVC serializes through a _copy_ of the registered options.**
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

### xUnit v3 on Microsoft.Testing.Platform (#70)

The runner is linked into the test assembly, which builds as an executable. There is no
`Microsoft.NET.Test.Sdk`, no `xunit.runner.visualstudio` and no `coverlet.collector`, because all
three are VSTest components. Three facts follow, and each is a trap:

- **`global.json` selects the runner** (`"test": { "runner": "Microsoft.Testing.Platform" }`).
  It is not a preference. Remove it and `dotnet test` routes to VSTest, which fails the build
  because no adapter is referenced. This is the one thing in the migration that fails loudly.
  `dotnet.config`'s `[dotnet.test.runner]` section — which the preview docs describe — is **not**
  read by the 10.0.1xx band; that was checked, not assumed.
- **Platform options go after a `--`.** `--coverage`, `--filter-class`, `--filter-method` and the
  rest belong to the executable, not the CLI. CI's
  `dotnet test --no-build --configuration Release --verbosity normal` is unchanged and still works.
- **A run that executes zero tests exits 8, not 0.** Verified by running a filter that matches
  nothing. This is strictly better than what VSTest did, and it is the property that makes the
  runner-selection trap above survivable: a misconfiguration cannot present as a green CI run that
  tested nothing. Do not add anything that swallows the test step's exit code.
- **The test project's `Properties/launchSettings.json` is gone, and must not come back.**
  `dotnet test` now *launches* the project, so it applied that file's launch profile — which was
  Web SDK scaffolding nobody had looked at, and set `ASPNETCORE_ENVIRONMENT=Development` plus an
  `applicationUrl`. `CustomWebApplicationFactory.UseEnvironment("IntegrationTesting")` overrode the
  environment, so nothing failed, but the suite was one `IHostEnvironment` read away from
  configuring itself as Development. Under VSTest the file was simply inert.
- **Coverage is `Microsoft.Testing.Extensions.CodeCoverage`**, driven from `coverage.ps1` /
  `coverage-check.sh`. Both were rewritten and both were run. See the coverage section of the
  tests README before editing either — the Cobertura format and the absolute results directory are
  each load-bearing, and the failure mode of getting them wrong is a missing report, not an error.

`xUnit1051` is the new analyzer that matters: every call taking a `CancellationToken` now passes
`TestContext.Current.CancellationToken`, applied across ~1080 sites by `dotnet format`. It has a
code fix, so the CI format gate — not just the build — is what enforces it. Do not add a call
without the token; do not suppress the rule.

**A test may not depend on running before its siblings.** v3 orders cases within a class
differently from v2, and that alone broke two tests that had been asserting on seeded rows a
sibling deletes (`DeleteAllNotifications_*`) or pages past (the summary endpoints default to
`PageSize = 10`). Both were latent; nothing about v3 made them wrong. Arrange what you assert on.

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

- `/health` — **liveness**, and the path configured under _App Service → Monitoring → Health check_.
  Process-only, no dependencies, and it must stay that way: App Service pulls a failing instance
  from rotation and replaces it after a sustained failure, so a check that touched SQL would report
  every instance unhealthy during a database blip and turn a recoverable outage into an app-wide
  restart loop.
- `/health/ready` — **readiness**. Probes SQL Server and returns per-check JSON. Nothing polls it
  automatically — it is for manual checks, and is what a post-deploy smoke test should call if one
  is added to `deploy.yml`. Never point the platform's health check at it.

The JSON omits each check's exception and description on purpose — the endpoint is anonymous, and a
SQL connection failure message carries the server name and often the credentials it tried.

## UTF-8 and BOM file formatting

Writing a new .cs file with the Write tool produces no BOM, so it will fail the ci everytime. Running `dotnet format` before committing a new file avoids the round-trip.

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
