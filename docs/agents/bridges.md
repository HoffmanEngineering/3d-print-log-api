# printlog-bridge: update notice, adapters, discovery and sealed credentials

## Agent update notice (printlog-bridge#39)

`ConnectionDto.latestAgentVersion` and `updateAvailable` tell a connector, in its own check-in,
that a newer release is out, so Docker bridges hear about one without calling GitHub themselves.
`AgentReleaseCatalog` reads the agent repo's `vX.Y.Z` tags (the bridge makes Docker images from
tags and no GitHub releases, so not `releases/latest`), keyed by connection kind in
`AgentReleases:Repositories`. Every bridge kind maps to printlog-bridge; a kind missing there
never gets `updateAvailable`.

- **Tags are read every `CacheMinutes` (6 h); a failure keeps the last version** and retries after
  `FailureCacheMinutes` (15), so a GitHub outage never turns each 5-minute heartbeat into a call.
  Unauthenticated GitHub allows 60 calls an hour per IP; this makes about 4 a day per instance.
- **Every test host routes the `AgentReleases` client to `CustomWebApplicationFactory.GitHub`**,
  which 404s unless a test seeds it, so no test reaches GitHub. The catalog is a singleton that
  keeps what it reads, so seed it before the host's first connection request.

## Bridge adapters (printlog-bridge#82)

printlog-bridge registers one connection kind per adapter: `moonraker`, `octoprint`, `prusalink`,
`bambu`, `sdcp` (Elegoo), `duet` (RepRapFirmware), `flashforge` and `anycubic` (LAN mode, FDM and
resin). `Connection.BridgeKinds` and `IsBridgeKind` are the list; add a new adapter there, in
`AgentReleases:Repositories`, `Connection.PrintSourceForKind`, `BridgeRules.DefaultPort`,
`SealedCredentials` and the silent-printer email's label and guide link.

- **Source.** A print created with an API key that links to a connection (`connectionInstanceId`)
  takes `PrintSourceForKind`: `octoprint` → OctoPrint (3), `prusalink` → PrusaLink (7), `bambu` →
  Bambu (8), `sdcp` → Sdcp (9), `duet` → Duet (10), `flashforge` → FlashForge (11), `anycubic` →
  Anycubic (12). A Cura setting still wins. **`moonraker` maps to nothing and stays ApiKey**, what
  bridge prints have always been; `PrintSource.Moonraker` means the notifier, and
  moving bridge prints there changes their achievements, sync `source` and silent-printer email.
- **"Has a bridge" is not one question.** The Spoolman config block leaves out the notifier for a
  bridge of any kind. The event dedupe (`PrintEventService.LiveBridges`) matches one kind: the
  Moonraker notifier is dropped for a live `moonraker` bridge (#150), the OctoPrint Webhooks plugin
  (`OctoprintController`: Print Started, Print Done, Print Failed, Error) for a live `octoprint`
  one (#291). Both count on the same `Connection.DroppedNotifierEventCount`, so `showNotifierNotice`
  on an `octoprint` connection means "turn off the Webhooks plugin" and the UI must word it by the
  connection's kind. Spoolman `/use` stays Moonraker-only, because it must agree with the notifier
  drop. A non-Moonraker bridge plus a hand-added notifier logs twice.
- **The webhook only finishes its own prints** (#291): its `PrintFinishedEvent` carries
  `ExternalSource = octoprint-webhook` and the device identifier's printer, so a Print Done can't
  close the bridge's `octoprint` print of the same file. **A webhook finish is never dropped while
  it has its own print to close** (`IPrintEventService.HasPrintToFinish`, same matching as
  `Finished`): a print the webhook started before its bridge went live (installed mid-print, or
  back from stale) is closed by the webhook and not counted, because a duplicate beats a lost
  print (#290). Only `Print Started`, and finishes with nothing of the webhook's to close, are
  dropped. `HasPrintToFinish` only counts prints created within
  `PrintEventService.FinishForBridgeWithin` (72 hours, #295): an older open print is one whose
  finish was lost, and a later job of the same file, which the bridge logs and charges, must not
  close it and charge the spool again. `Finished` itself has no bound.
- **The Moonraker notifier follows the same rule** (#295): its finish carries
  `ExternalSource = moonraker-notifier` and its printer, so it never closes the bridge's
  `moonraker` print of the same file, and `complete`, `cancelled` and `error` go through, uncounted,
  while `HasPrintToFinish` finds a notifier print of that file open on that printer. `started` is
  always dropped. Spoolman `/use` still records nothing while the bridge is live, so a print the
  notifier started before its bridge went live is closed with the `/use` totals reported before
  that (or, with none, the notifier's own `filament_used`).
- **`PrintFilament.ActualIsEstimate`.** True when a row's actual figures are the slicer's estimate
  (most firmwares other than Klipper report only that). The spool is charged as usual; accuracy
  analytics drop the row from both sides of the material sample. `/complete` resets it with the
  values, absent meaning measured. PUT and PATCH replace every row, so a client that does not send
  it back resets it to false.
- **Heartbeat software fields.** `firmwareName`/`firmwareVersion` and
  `hostSoftwareName`/`hostSoftwareVersion` sit beside `klipperVersion`/`moonrakerVersion`, each
  filling the other (`Klipper` and `Moonraker` by name, case-insensitive). Unlike the rest of the
  health, a missing, null or blank name or version keeps the stored one (#80).

## Bridges, discovery and desired printers (printlog-bridge#100, #103)

A paired printlog-bridge reports what it found on its LAN, and the user picks in the app which
printers it watches. Code: `Models/Bridge.cs`, `Services/Bridges/`, `Controllers/BridgesController.cs`.

- **A `Bridge` row shares its `DeviceGrant`'s id** and is created by the grant's first check-in
  (`PUT /api/bridges/me`, scope `bridge:sync`). It is a table of its own, not grant columns: the
  grant is the credential every device request reads, check-ins write every few minutes, and Home
  Assistant grants are not bridges. The bridge id is always the token's `device_grant_id`, never a
  route or body value.
- **Revoking the grant deletes the bridge** (database cascade) and with it its discovered, ignored
  and desired printers. `Connection.BridgeId` has no cascade (1785), so `DeviceGrantService` nulls
  it in the same transaction and retries if a heartbeat relinks one in between. Connections and
  prints are kept. `PrinterService.DeletePrinter` removes a printer's desired entries and bumps
  their bridges' version. `UserDeletionService` deletes device grants (so the tokens die and the
  bridges cascade) before connections and printers, with the same unlink-and-retry.
- **Nothing a device sends creates a desired printer.** `BridgePrinter` rows come only from
  `POST /api/bridges/{id}/printers` (`InteractiveUserOnly`). Discovered printers are suggestions
  (printlog-bridge#100 rejects auto-connect: a neighbor's printer on a shared network). Keep it so;
  `ACheckIn_NeverConnectsAPrinter_WhateverItSends` is the guard.
- **One watcher per printer, or every print is logged twice** (each watcher has its own instance
  id, and nothing downstream dedupes). `BridgePrinters.PrinterId` is unique account-wide (409
  `printer_connected_elsewhere`, with the other `bridgeId`); on one bridge the same identity or
  kind and address is 409 `discovered_printer_already_connected`, and a hand entry gets an
  identity key too (`kind:ip:host:port`); a stable identity (serial, mainboard id, MAC) is also
  refused on the user's other bridges. A printer a bridge reported as in its config file is 409
  `printer_configured_on_bridge` (with that `bridgeId`): on that bridge by identity or kind and
  address, on the others by stable identity. It shows as Configured even when an app entry
  matches. A missing port is the kind's default (`BridgeRules.DefaultPort`), so two instances on
  one host stay apart. **Known limit:** across bridges only the derived identity key is
  compared, so bridges that identify one printer by different fields (MAC on one segment,
  address across a VLAN, a serial only one sees) are not caught, and two concurrent connects of
  one serial to two bridges under different printers can both pass (no unique constraint spans
  bridges). The event safety net below counts what connect misses and merges what it safely can.
- **Validation is two-level** (`BridgeCheckInValidator`, `BridgeRules`): over a cap (16 networks,
  50 printers, 8 MACs) or a malformed `agentVersion`/`mode`/`hostName` is a 400 `invalid_check_in`;
  a network or printer that breaks a rule is dropped and named in `rejected`, so one odd printer
  cannot lock a bridge out of its desired list. Addresses are IPv4 RFC 1918 + 169.254/16 and IPv6
  fc00::/7 only, parsed strictly (no `10.1`, hex or zone ids); networks /24 (v4) or /64 (v6) and
  smaller. Hosts the user types are those or a local host name; the bridge must still check what a
  name resolves to.
- **A check-in replaces the discovered list by identity** (`BridgeRules.IdentityKey`: serial, else
  mainboard id, else lowest MAC plus port, else address plus port), so a printer keeps its row and
  id across address changes. A missing or null field or list keeps what is stored; `{}` is a fetch.
- **A config-file printer names the printer it logs to.** A `discovered[]` entry may carry
  `printerId` (the config section's `printer_id`). It is kept on `BridgeDiscoveredPrinter.PrinterId`
  only when the entry is `configured: true` and the id is one of the grant owner's printers (one
  query per check-in); anything else (not configured, `<= 0`, another user's, missing) is stored
  as null **silently**, never `rejected` and never a 400, so an old or misconfigured bridge keeps
  working. Like the rest of the entry, a report without it replaces it with null. A plain column,
  no FK (as `Bridge.LastSharedJobPrinterId`): an FK from here to `Printers` would be a second
  cascade path (1785), and a printer delete must not touch bridge rows. `GET
  /api/bridges/{id}/discovered-printers` returns `printerId` as stored and `printerName`, the
  printer's current name from one query scoped to the user, null once it is deleted (the id stays
  until the next check-in drops it).
- **The desired list is versioned by `Bridge.DesiredRevision`**, a new GUID on every change
  (connect, disconnect, printer delete, scan request), so concurrent edits need no counter.
  `GET /api/bridges/me/desired` answers 304 on a matching weak `ETag` after one small read. A
  poll also refreshes the bridge's last-seen time, at most every two minutes
  (`BridgeService.PollSeenResolution`), so a bridge whose check-ins a backfill holds back stays
  online.
- **Check-ins are not exempt from the connector write budget**, unlike the heartbeat: starving one
  only delays discovery and desired-list updates, never duplicates prints, and the poll is a read,
  so the bridge still sees changes during a backfill. Nothing here bumps the cache version.
- **`Connection.BridgeId` comes from the heartbeat's token**: a device whose grant has a bridge row
  sets it, anything else (API key, a bridge that never checked in) sets null. A superseded token
  (re-pair grace) leaves it unchanged.
- **LAN secrets are sealed to the bridge's key, never plaintext** (story 14, next section). Don't
  add a plaintext secret column.
- **Release order for `bridge:sync`:** add the permission to the Auth0 device API first, then deploy
  the API, then release a bridge that requests it. A bridge requests only the scopes the pairing
  discovery lists, and on 403 `insufficient_scope` re-pairs once, then reports it instead of
  looping.
- **Moving a printer between bridges** (printlog-bridge#103, story 15a):
  `POST /api/bridges/{id}/printers/{entryId}/move` rebinds the entry **in place** (same id, so a
  bridge's `app-<id>` state survives a move back) in one save, bumping both bridges' versions; the
  old bridge drops it at its next poll with no bridge change (story 12). **Credentials move with
  it, in the same save**: values sealed to the old key (and bound to the old bridge id) can't be
  opened by the new bridge, so the body carries `secrets` (`{field: sealed}`, sealed in the browser
  to the new bridge's current key with AD `printlog-credential:v1:{toBridgeId}:{entryId}:{field}`)
  and `keyFingerprint`, checked exactly as a seal (`EnsureSealableTo`). A field the printer can't
  connect without that was set (`SealedCredentials.CarriedGroups`: Bambu access code, PrusaLink
  password or API key, OctoPrint API key, FlashForge check code, a Moonraker API key, set only
  with `[authorization]`, and a Duet password, set only when RepRapFirmware has one) must be sent, or 400 `secrets_required` with `fields`: otherwise the old
  bridge lets go and the new one can't connect, and every print in between is lost. Fields not
  sent are deleted and named in `droppedSecretFields`. Refused when either bridge has the printer
  in its config file (`printer_configured_on_bridge`). Concurrency rests on
  `BridgePrinter.IssuesStamp`, which the move changes: a racing move, seal or check-in issue write
  fails its save and reruns against where the entry is now (a losing move answers 409
  `printer_moved` with the entry's `bridgeId`), and a seal inserting the field the move inserts
  reruns it too, and a unique-index loss on the last attempt answers 409
  `discovered_printer_already_connected` (or what connect would say), never a 500.
  `BridgeMoveRaceTests` pins them. `BridgePrinter.MovedAt` records the move; the safety net reads
  it.
- **The cross-bridge safety net** (`BridgePrintGuard`, story 15a). **A duplicate print is
  acceptable; a lost print, or two jobs merged into one, is not.** So it never refuses: printlog-bridge
  drops a 400/403 from its outbox for good, and nothing the API sees proves the bound bridge is
  logging the printer (Bambu and PrusaLink adapters heartbeat every printer whether or not they
  reach it, and log "unreachable" below the level that sets `lastError`; a dead bridge looks live
  for `Connection.StaleAfter`). Don't add a refusal back on liveness or credential state. A job
  is matched to an existing print only when it is evidently the same job: both file names present
  and equal, starts within `SameJobTolerance` (2 minutes; 10 for `bambu`, whose first look
  stands in for a missing `gcode_start_time`), the existing print not finished before the job
  started, and not started after the job ended when that is known. A replay of an existing pair,
  any write to an existing print, API keys, devices that never checked in as a bridge and printers
  with no desired entry are never judged.
  - **Two bridges, one printer:** another bridge's create or claim for a printer bound elsewhere,
    for a job that started since it was bound (`MovedAt ?? CreatedDate`), gets the bound bridge's
    print of the same job (200, as a replay would), else is logged. Both bridges then `complete`
    that print; a second complete is a replay of a finished print (usage merged per spool, the
    spool charged once, no second notification). Either way the job is counted on the caller.
  - **Counter:** `Bridge.SharedJobCount`, distinct jobs a bridge reported for a printer another
    bridge is bound to, keyed on the external pair in `BridgeSharedJobs` (30 days; each new row
    prunes its bridge's older rows), so a job sent at its start, again at its end and as a claim
    counts once. A job without a pair counts each time. `LastSharedJobAt` moves on every one, so
    the UI can hide a warning that stopped happening. It feeds the "two bridges watch this
    printer" warning.
  - **Hand-off after a move:** the old bridge stops watching within a poll and never sees the
    in-flight job end. The bound bridge's create for a job that started no later than `MovedAt`
    plus 2 minutes takes over another bridge's Printing print of the same job that started at or
    before `MovedAt` and no more than 48 hours before it. A job that started after the move never
    takes a print over, so a reprint after an unseen failure is never merged. Its pair and
    connection replace the old ones in one conditional `ExecuteUpdate`, the one place an external
    pair is rewritten; the connection is the one the create names, else the bound bridge's latest
    for the printer, never the old bridge's.
  - **The old bridge's late create** for a job that started before the move gets another bridge's
    print of the same job, in any status, that started no later than the move plus 2 minutes
    (never one from after the move), instead of a second one nobody would finish. Not counted.
  - **Known limit:** an adapter that reports its first look as the start (PrusaLink after a pause,
    whose `time_printing` leaves pauses out; Bambu Lab without `gcode_start_time`) can miss the
    hand-off. That leaves a duplicate and the old bridge's print stuck in Printing. The owner
    finishes it with the web app's print edit (status, a `PATCH /api/Prints/{id}`).
  - Start times are compared in memory (SQLite can't compare `DateTimeOffset`).
- No tier limit counts bridges or desired printers (printlog-bridge#103 open question; Free is
  limited on Spoolman connections only).

## Sealed LAN credentials (printlog-bridge#100, story 14)

A printer's access code, password or API key is sealed in the browser to the bridge's public key
and only the bridge can open it. **The scheme is a contract with printlog-bridge and the web app:
`docs/bridge-credential-sealing.md`, with test vectors in `bridge-credential-sealing.vectors.json`
that both repos copy.** `printlog-credential` v1 is ECDH P-256 + HKDF-SHA256 + AES-256-GCM, a
fresh ephemeral key per value, associated data `printlog-credential:v1:{bridgeId}:{entryId}:{field}`.
Code: `Services/Bridges/SealedCredentials.cs`, `BridgePrinterSecret` in `Models/Bridge.cs`; the
reference sealer and opener are in the test project (`CredentialSealer`).

- **The API never decrypts and holds nothing that could.** It parses the bridge's key strictly
  (`SealedCredentials.TryParsePublicKey`: exactly the 91-byte SPKI of an uncompressed P-256 point on
  the curve, padded standard base64; anything else is dropped as `rejected` `invalid_public_key`,
  keeping the stored key and the rest of the check-in) and checks a sealed
  value's shape (prefix, canonical base64url, 94 to 349 bytes, ephemeral point on the curve). A
  change to either rule, or to the vectors, is a change to the other two repos' contract.
- **Sealed values leave the API only through the owning bridge's device endpoints** (the check-in
  response and `GET /api/bridges/me/desired`). The owner's endpoints return field names,
  fingerprints, times and `stale`, never `sealed` (the browser can't open them anyway). Never put
  one in a log, telemetry property, problem detail, the sync snapshot, MCP or a webhook.
  `SealedCredentialLeakTests` checks all of those at Trace.
- **Only the registering check-in's key is trusted; every other new key waits for the owner.**
  That includes the first key of a bridge that registered without one: a stolen token could send
  it weeks later. A check-in with a new key stores it as `PendingPublicKey` (the current key, if
  any, stays in use), and seals to it are 409 `bridge_key_unconfirmed`. Only
  `POST /api/bridges/{id}/public-key/confirm` with the pending key's fingerprint makes it current,
  in one conditional `ExecuteUpdate` (a newer pending key makes it 409 `pending_key_changed`). A
  stolen device token can report its own key; this is what stops the next seal going to it. Never
  let a device-reported key become current without that confirmation, except on registration.
  - **The owner is notified only when no key was pending, at most once per bridge per UTC day**
    (`Bridge.KeyNotifiedDay`), after the save and inside a try/catch: a failed notice never fails
    a committed check-in.
  - **Past `BridgeService.MaxKeyChangesPerDay` (5) changes in a UTC day, a new key no longer
    replaces a pending one**, so a flapping key can't keep moving what the owner compares.
    `BridgeDto.keyChangesThrottled` flags it; `BridgeKeyChanged` telemetry (`Change`,
    `ChangesToday` from `Bridge.KeyChangesToday`) reports `throttled` once.
  - **Re-pairing clears the pending key** (`DeviceGrantService.PairAsync`, same save as the token
    rotation): the old token is on its way out (it lives on for the re-pair grace, for prints and
    heartbeats only, and can't check in), so a key it reported must not stay confirmable.
  - **A pending key equal to the current one is cleared** (a check-in reporting the current key,
    and a conditional update after a check-in that set one), and seals compare with the current
    key first, so that race can't block every seal.
- **The fingerprint the browser sends is its own.** The web app computes it from the SPKI it seals
  to; the API's `publicKeyFingerprint`/`pendingPublicKeyFingerprint` are convenience only. A
  malicious API could pair its own key with the real fingerprint, so the doc makes this normative.
- **Confirming a key makes values stale, derived, not written.** A value is stale when its
  `KeyFingerprint` isn't the bridge's current one: the bridge isn't sent it, the owner sees it
  marked. Nothing is deleted, and a seal racing a confirmation is simply stale (the web app checks
  `stale` on the PUT response). Confirming bumps the desired version.
- **Setting or clearing a value bumps the desired version** and clears that field's reported issue.
  A fingerprint that isn't the current key's is 409 `bridge_key_changed` (with `keyFingerprint`);
  no key yet is 409 `bridge_key_missing`.
- **The bridge reports what it can't use** (`credentialIssues`: `missing`, `undecryptable`,
  `invalid_plaintext`, `rejected_by_printer`) on every check-in, applied only when
  `credentialIssuesVersion` is the current desired version (read after the entries). The write is
  guarded by `BridgePrinter.IssuesStamp` (`[ConcurrencyCheck]`), which every seal changes, so a
  check-in that read an entry before a seal can't restore the issue that seal cleared
  (`BridgeCredentialIssueRaceTests`). Anything that saves a `BridgePrinter` can now get a
  `DbUpdateConcurrencyException`: seal and disconnect retry, and printer delete refreshes the stamp.
- **Fields per kind** are printlog-bridge's adapters' `app_secrets` (`SealedCredentials.FieldsFor`);
  a new adapter with secrets adds its fields there and in the doc's table.
- **Trust limits, documented in the doc:** the API could substitute the key the browser seals to,
  and anyone can seal a value the bridge accepts. The defense is the fingerprint the web app
  computes and the bridge shows. Values cascade with their entry, so disconnect, printer delete,
  revoke and account deletion take them.
