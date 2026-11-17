# Spoolman-compatible API

## Spoolman-compatible API (#173)

Moonraker's `[spoolman]` component can use 3D Print Log as its spool server:
`server: https://printer-<id>:<secret>@api.3dprintlog.com/spoolman`. Moonraker appends `/api/v1`, so
the routes are `/spoolman/api/v1/...` (`SpoolmanController`), outside `/api`.

- **Credential:** a per-printer `SpoolmanConnection` secret, sent by Moonraker as HTTP Basic (URL
  userinfo). The `Spoolman` policy pins `SpoolmanBasic`, so no JWT, API key or dev bypass works
  there, and the secret works nowhere else. Minted only by `InteractiveUserOnly`. Stored as a hash.
- **Lookups are cached with negative results** (`SpoolmanCredentialCache`), because Moonraker
  retries a refused socket every 2 s forever. Every path that revokes, rotates, pauses or deletes a
  connection must evict it. **A secret matching no row is never cached** (#193): those entries
  are bounded only by what a caller sends, and they would evict other users' entries from the
  shared 8 MiB budget. The factory throws to keep it out, as `GetUserIdByApiKey` does. The
  failed-attempt limiter counts only unknown secrets and never refuses a valid one.
- **Tier:** Free is one *active* connection. A downgrade pauses (never deletes) all but the
  oldest, from `SubscriptionService`, at the handshake, and on the socket timer. A paused
  connection also stops notifier logging (401).
- **Bodies are Spoolman's v0.27.0 shapes**, written by `SpoolmanJsonContext` and never by MVC.
  Errors are `{"message"}`, and the path is excluded from `ProblemDetailsStatusCodePages`.
  Writes return 400 until #172.
- **`PUT /spool/{id}/use` accumulates** (#171) into `SpoolmanUsages`, one row per (print, spool),
  on the printer's open *notifier* print only: not with a live printlog-bridge (it records usage
  itself, even when a notifier print from before it went live is still open, #295) and not
  without an open print. A spool's first report of a print loads it into slot 0
  on a single-slot printer (so A->B->A leaves B loaded). Increments are one `ExecuteUpdate` and
  never bump the cache version. A `/use` racing the finish can be lost, bounded to one report
  interval. A notifier print whose finish never arrives stays open and keeps accumulating idle
  extrusion.
- **`POST /spoolman/notifier`** (`SpoolmanNotifierController`, outside `/spoolman/api/v1`) takes
  Moonraker's `[notifier 3d_print_log]` events over the same Basic credential, through the
  `MoonrakerNotifierHandler` the user-level notifier shares. The printer comes from the credential,
  never the body. For a printer without printlog-bridge, the connection's config block carries
  `[spoolman]` and the notifier, so one paste sets up both. A printer with a live printlog-bridge
  has its events dropped: **the bridge stays authoritative** and the notifier tier is only for
  printers without one. A start for a printer that no longer exists is a 404 `{"message"}`: deleting
  a printer unbinds its connection, but the credential is cached, so for a moment the credential can
  still name it (#309).
- **A printer with a printlog-bridge connection gets `[spoolman]` alone.** Any `Connection` row
  of a bridge kind (`Connection.BridgeKinds`, printlog-bridge#82) for the printer counts, stale or
  not: the block is pasted once and kept, and a
  bridge that is briefly down would otherwise hand the user a notifier that duplicates the
  bridge's prints when it catches up (the bridge's own check tells them to delete it anyway).
  Removing the connection in the web app deletes the row, so the next connect includes the
  notifier again. The response shape does not change; the UI looks for `[notifier` in
  `configBlock`.
- **A Snapmaker U1 gets `[spoolman]` alone too**, bridge or not (Make contains "snapmaker",
  Model matches `\bu1\b`, the web app's rule). Its stock Moonraker ships without `apprise`, so
  `[notifier]` fails to load ("failed plugins: notifier", seen on firmware 2.0.0). Some vendor
  builds (older Creality Sonic Pads) lack it too, but nothing identifies them, so they keep the
  block and the docs cover the error.
- **Finishing a notifier print replaces its usage rows** with the accumulated `SpoolmanUsages`
  (`PrintEventService.Finished`): clamped at 0, deleted spools skipped, and when every reported
  spool is deleted it falls back to the notifier's own total. Rows carry no fabricated estimate: a
  single spool keeps the started row's, several get null, so accuracy analytics skip them. A
  single spool on a multi-slot printer gets no slot.
- **The inventory is cached on the user's version GUID.** Filament and print writes must bump it,
  as for the summaries. A miss aggregates each spool's first and last use in SQL, one row per spool
  (#309). SQL Server compares `datetimeoffset` by its UTC instant, as the in-memory grouping it
  replaced did; SQLite (the tests) compares wall-clock ticks, so no test may depend on the order
  of two starts closer together than their offsets differ.
- **Websockets are in-process, on one instance.** `SpoolmanSocketHost` holds them open and sends no
  events (#172). It re-checks each socket every 5 minutes and closes it when the connection is
  revoked or paused. Scaling out needs a backplane before #172's events can reach a socket held
  by another instance, so scale *up* (S1/P0v3 at about 250 sockets; B1 allows 350). App Service
  must have **Web sockets** switched on.
- **Spoolman's web UI paths redirect to the web app** (`SpoolmanWebUiController`): Fluidd links
  "Manage spools" to the bare server URL and its AFC panel to `{server}/spool/show/{id}`. They are
  anonymous and go to `Email:WebBaseUrl`, so a local test that clicks them needs that pointed at
  the local UI.
- **`moonraker.log` prints the URL with the secret in it**, and users post that log publicly.
  That is why the credential is narrow and easy to rotate. Never widen what a Spoolman secret
  can reach. There are two deliberate, owner-approved widenings: the notifier route (the secret
  also logs prints) and persisted `/use` (the secret can charge length to any of the user's
  spools on its printer's open notifier print, and load a spool into slot 0 on a single-slot
  printer). Both are for the connection's own printer only, and still only under `/spoolman`.
