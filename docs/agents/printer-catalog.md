# Printer catalog

## Printer catalog

`PrintLogApi/PrinterCatalog/printer-catalog.json` is the list of printer models the quick-add and
print form search. It is **generated** from Cura and OrcaSlicer printer profiles by
`PrintLogApi.Tools` and embedded in the API. Never hand-edit it. Correct a model in
`catalog-overrides.json`, and map a maker's spelling in `make-aliases.json`; the next run applies
both. Which release each source is read at is `catalog-sources.json`.

- **Ids are permanent.** A published id (`bambu-lab-p1s`) never changes and is never reused, because
  printers store it. The generator reads the committed file to keep them. A model that leaves the
  sources or is hidden stays in `reserved`; `mergedInto` marks an id folded into another when two
  entries came to describe one model.
- **Facts only.** The file holds names, aliases and the spec fields, with where each came from. Never
  copy profile text, G-code, meshes or images into it or the repo. That is the license rule: Cura is
  LGPL-3.0 and OrcaSlicer is AGPL-3.0, and the file carries their attribution.
- **Run it locally** with `dotnet run --project PrintLogApi.Tools -- printer-catalog --out
  <absolute path>`; the working directory is the project folder, so pass absolute paths. It clones
  both sources shallowly into `--work`, which on Chris's machine must be on D: (C: is full). Add
  `--summary <file>` for a Markdown diff of what changed.
- **The golden list** (`PrinterCatalogGoldenTests`) pins the specs of well-known models. A refresh
  that moves one fails it, which is a prompt to look at the change, not necessarily to revert it.
- **A monthly workflow** (`printer-catalog-refresh.yml`) resolves the latest stable release of each
  source, regenerates, and opens a `chore/printer-catalog-refresh-<yyyy-mm>` PR with the summary and
  the catalog test result. It skips while one is open and never merges. It uses the
  `CATALOG_REFRESH_TOKEN` secret (a fine-grained PAT: Contents and Pull requests, read/write) when one
  exists, else `GITHUB_TOKEN`. A PR from `GITHUB_TOKEN` gets no CI until you close and reopen it or
  push a commit; one from the PAT runs CI on its own. **`GITHUB_TOKEN` needs a setting:** GitHub
  refuses its PR unless *Allow GitHub Actions to create and approve pull requests* is on. It is on
  for the HoffmanEngineering organization (2026-10-18). Without it the run deletes the branch it
  pushed and fails with an `::error::` that names the setting.
- **The startup seeder** (`PrinterCatalogSeeder`, every environment, after ApplicationStarted)
  writes the embedded file into `PrinterModels` when its version differs from the one in the
  `PrinterCatalogVersions` marker row, in one transaction (an applock on SQL Server, so two instances
  seed one after the other). **An older file is skipped** (its `generatedAt` is before the seeded
  one's), so a rollback deploy never undoes a newer catalog. Rows are never deleted: a model the file
  no longer lists, or one hidden by an override, is stored as **retired**, which still resolves by id
  but is never offered.
- **Until the index is built, the catalog endpoints answer 503 `catalog_not_ready`.** A seed that
  fails with no visible model in the table leaves it that way and retries every 5 minutes, 12 times;
  one that fails with models present rebuilds from the table as it is.
- **The backfill runs on every start** once the index is built: it links printers with no
  `CatalogModelId` by make and model (exact or alias match only). Owner counts are recomputed then
  only when the seed changed something or the backfill linked a printer; otherwise daily.
- **The anonymous endpoints are rate-limited per socket peer** (`Catalog:RateLimitPerMinute`, 600).
  That is a client only with the host's forwarded headers on (`ASPNETCORE_FORWARDEDHEADERS_ENABLED`,
  which must be set on the App Service; Startup never calls `UseForwardedHeaders`); without them
  every caller shares one budget.
