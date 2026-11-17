# Caching and response compression

The basics (version-based invalidation through `ICacheVersionService`; every create, update and
delete must invalidate) are in AGENTS.md, Caching. This file has the rest.

## Caching

**The grouped feed caches its order, not its pages (#309).** `GET /api/Prints/grouped` used to read
the sort key of every print and project the user has on every request (5 to 25 s at the database's
busiest). `PrintService.LoadGroupedFeedOrderAsync` now builds the ordered rows once per user,
version and query (search, filters, sort; not the page), and every page of that query is served
from the one entry: a page loads only its own rows. The entry is compact on purpose (`GroupedFeedEntry`:
a print row is its id) because the budget charges its serialized size and HybridCache refuses an
entry over 1 MiB, which would put such a user back to computing it on every request. The order
reads prints, their usage rows, projects and spools (density and diameter resolve a usage row), so
those writes must bump the version, as they already do for the summaries. A test that seeds any of
them straight into the database must bump it too (`ProjectDateSeedHelpers` does).

### HybridCache for compute-on-miss, IMemoryCache for counters (#68)

Everything that computes a value on a miss goes through `HybridCache.GetOrCreateAsync`, for
stampede protection: concurrent callers on one key await a single computation. The cold window is
not hypothetical — a version bump invalidates all of a user's entries at once, so their misses
arrive together. Converted: both summary endpoints, all six analytics tabs, `ClaimsTransformer`
and `UserApiKeyService.GetUserIdByApiKey`, and later the grouped feed's order (#309).

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
- **The public print index (api#309)** is one HybridCache entry (`PublicPrintIndexCache`, 10
  minutes) behind both `GET api/Prints/public` and `public/sitemap`; a print made public or private
  can take that long to show. It has its own binary serializer (`PublicPrintIndexSerializer`,
  registered on `AddHybridCache`): HybridCache serializes every entry to charge its size, and
  **silently stops caching an entry over its 1 MiB `MaximumPayloadBytes`**. As JSON the ~31,000
  public prints of November 2026 were near that; the binary form is about 10 bytes a row.
  `PublicPrintIndexCacheTests` checks three times today's size. Tests that change a print and then
  read either list evict `PublicPrintIndexCache.CacheKey` first.

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
  before that ships** — start at the responses that return a secret alongside caller-supplied
  text: `POST /api/UserApiKeys`, and `POST /api/Webhooks` and `POST /api/Webhooks/{id}/rotate-secret`
  (the signing secret next to the caller's URL and description).
- **Both providers are on `CompressionLevel.Fastest`, and raising either is a CPU decision, not a
  tuning preference.** The measurements are in the doc comment. Brotli `SmallestSize` is a 600x CPU
  increase for three percentage points — never use it on a request thread. Gzip `Optimal` is the
  more tempting one (35% fewer bytes for 0.2 ms) and was deliberately rejected: gzip is reached
  only by clients that cannot do brotli, but it is also whichever codec an attacker names in
  `Accept-Encoding`, so the extra CPU is spent mostly on adversaries. That trade is only as good as
  the bound on response size. `PagedRequest.PageSize` is clamped to `PagedRequest.MaxPageSize`
  (250, #314); before that it was an unconstrained `int` that flowed into `Take()`.

`text/event-stream` is deliberately absent from `MimeTypes`: compressing a streaming body buffers
it, and `/mcp` negotiates that content type. `ResponseCompressionTests` pins that, the encoding
negotiation, the compression levels, and that compressed bytes decode back to the uncompressed
response. One test there is load-bearing in a way that is easy to miss: `Compression_AppliesOverHttps`
drives an `https://` base address, because every other test runs over plain HTTP where compression
happens whatever `EnableForHttps` says — revert that option and it is the _only_ test that fails.
