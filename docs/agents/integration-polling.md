# Integration polling

## Integration polling (#234)

`GET /api/sync/version` and `GET /api/sync/snapshot` (`SyncController`) are what the Home
Assistant integration (#236) polls. The field rules are on the response records in
`Models/DTOs/Sync/SyncSnapshot.cs` and in the OpenAPI document.

- **`/version` reads no data, and assumes a single instance.** It is a hash of the user id and
  the `ICacheVersionService` GUID, which lives in process memory. A restart mints a new one
  (harmless: one extra snapshot fetch). Scaled out, it is worse than spurious changes: a write
  bumps only the instance that served it, so polls landing on another instance *miss* the change
  until that instance's entry expires. A write that forgets to bump the version is invisible here
  too, as it is to the summaries.
- **Connection health is never cached.** Heartbeats do not bump the version (and must not: one per
  printer every 5 minutes would flush every cache the user has), and going stale is the absence
  of an event. Each request overlays `connection` from one live query. The body carries no
  `lastSeenAt`, so an unchanged status keeps the `ETag` and the 304.
- **Four cache entries, all on the version GUID** (`SyncSnapshotService`): the core (printers and
  spools plus a hash of their bytes, 15 min, about 109 KB for 300 spools, see
  `CacheBudget.SyncSnapshotCoreBytes`), the core's hash alone (1 h), the month totals keyed on
  canonical zone and month (1 h, so they roll over without a write), and the saved time zone
  (1 h; settings writes bump the version).
- **A 304 never builds the core.** The tag is computed from the version, the cached core hash,
  the totals and the overlay; only a 200 reads the core. So a conditional refresh costs the
  overlay query and cache hits, even after the core expired.
- **The ETag follows content, not just the version.** A change that missed its bump gets a new tag
  once the hash entry expires (at most an hour) and the core is rebuilt. Tags are weak (`W/`),
  because compression serves several encodings under one tag.
- **Time zones are canonical** (`SyncZone`): resolved, then mapped through their Windows zone to
  CLDR's primary IANA id, so aliases share one entry and tag. The echoed id can be a sibling
  (`Europe/Paris` reads back `Europe/Berlin`) with identical month boundaries. Omitted means the
  user's saved setting, else UTC.
- **`remainingGrams` is null for an untracked spool**, pending #230; never `BalanceMg`'s negative
  figure. Changing it is a contract change for the integration.
- **A full miss is six statements, whatever the printer count** (four for the core, two for the
  totals: month prints grouped by status, which gives the count and the success/failed split, then
  grams; `SyncSnapshotQueryShapeTests`).
  The current and last print per printer are correlated `FirstOrDefault`s that EF turns into
  `ROW_NUMBER()` joins, one statement; the current prints' per-slot estimates are a fourth, one for all printers.
- **Fields added for the integration (#236), all additive.** Per printer: `printTimeSeconds`
  (all-time, the analytics figure with no date filter: recorded time, else estimate, else 0),
  `lastMaintenance` (newest entry marked done, or null) and `openMaintenance` (`count`, and
  `nextDate` when `count` > 0; never null). `currentPrint` and `lastFinishedPrint` carry `source`
  (the print's origin) and `url`, a link to the print in the web app built from `Email:WebBaseUrl`;
  `currentPrint` adds `filament`, one `{slot, spoolId, estimatedGrams}` per PrintFilament row (`estimatedGrams` is null
  when a row has no estimate), read in one statement for all printers. `account` adds `successCount` and `failedCount` (prints started
  this month; canceled and partial are in `printCount` only). The ETag covers all of them.
- **Two filament writes for the integration** (`FilamentsController`): `POST /api/Filaments/{id}/adjustments`
  (signed delta, wraps the MCP `adjust_material_remaining` logic) and `PUT /api/Filaments/{id}/active`.
  Both bump the user's cache version (so the next poll sees them), accept API keys, and answer
  failures with problem details carrying a machine-readable `code`: **404 `material_not_found`**
  (missing and foreign spools look the same), **400 `material_untracked`**,
  **`adjustment_out_of_range`**, **`invalid_adjustment`** (bad unit, zero or non-finite delta, a delta
  over 2,000,000 in magnitude, a length or volume delta on a spool without the density or diameter
  needed to convert it, notes over 1000 characters) and **`invalid_request`** (`isActive` missing). **A client tells an old API
  apart from a missing spool by the code**: an unmatched route is also a 404 but has no
  `material_not_found`, so a 404 without that code means the API predates these endpoints.
  `FilamentIntegrationEndpointsTests` pins the codes; they are a contract with the integration.
- `schemaVersion` versions the shape. Adding a field is fine; renaming, removing or changing a
  meaning needs a new version and the integration's agreement.
