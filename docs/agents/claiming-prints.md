# Claiming a print

## Claiming a print (#165)

`POST /api/Prints/{id}/claim` lets a connector adopt a Printing print the slicer uploader, the
Cura plugin or a hand entry created, instead of creating a second one. `GET /api/Prints/claimable`
is its lookup. The lookup is a separate endpoint, not a filter on `/summary`, because that one is
cached, partly anonymous, and has no `fileHash`.

- **The write is one conditional `ExecuteUpdate`** (`WHERE` Printing and no pair), so two racing
  claims cannot both win and the second overwrite the first. `ExecuteUpdate` bypasses
  `SaveChanges`, so it stamps `UpdatedDate` itself, and a unique-index loss surfaces as a
  provider `DbException`, not `DbUpdateException`.
- **The same pair replays before the status check**, so a retried claim still succeeds after the
  print finished.
- **External ids are matched case-insensitively** (#204), and that is the contract, not a bug.
  `Prints.ExternalId` has no explicit collation, so SQL Server's default `CI_AS` decides the
  lookups and the unique index. A binary-collation migration was declined: it rebuilds a unique
  index on `Prints` for collisions no current id format produces. Any in-memory comparison of a
  pair (the claim replay) uses `OrdinalIgnoreCase` to agree. SQLite compares binary, so the suite
  cannot see the database half.
- **Prints carrying an `mcp` pair are never claimable.** That pair is `create_print`'s idempotency
  key, and overwriting it would break that key's replay.
- **On a single-tool printer (`SlotCount == 1`), `/complete` treats an unlinked row with no slot as
  slot 0's** and stamps it 0 when a slot pass takes it. The Slic3r uploader writes its estimate
  with no slot, and the bridge completes that tool as `{slot: 0, filamentId}`; without the rule
  that added a second row. On a multi-tool printer an unslotted row stays ambiguous, so it is left
  to the in-order match. A connector that knows which unslotted row a measurement belongs to
  names it with **`rowId`** (printlog-bridge#128: a sliced filament the bridge couldn't place on a
  slot). It's a hint, matched before every other pass: the row keeps a spool the user linked, and
  an id not on the print (the row deleted, or renumbered by a PATCH, which gives every row a new
  id) is ignored rather than refused, so a completion never fails over a stale id.
- **"Other" rows fill from what is loaded (printlog-bridge#128).** `PrinterService.LoadSlot`,
  when the spool is newly loaded there (not a bridge reasserting slots on reconnect), links it to
  the row with no spool on that slot of the printer's **newest Printing** print (never a finished
  one, nor an older one left Printing); the printer page's PUT does the same for slot 0 on a
  one-slot printer, when it adds exactly one spool. `/complete`'s slot-only measurement then
  finds that row by its slot, though it now has a spool. The other way round, picking a spool for a row with a
  slot whose slot had none (PATCH or PUT of the print) loads it there, on a printer with more
  than one slot, for a Printing print or the printer's latest (by start date, else by id). A
  create that links no spool leaves a one-slot printer's loaded spool alone, and a replayed
  create keeps a spool the user linked to a slot's row since.
- `hasSlicerSettings` is `Slicer != null`. The print stores no link to its `CuraSetting`; `Slicer`
  is set exactly when settings were recorded, and #164 should keep that true.
- **Finishing is claimed the same way (#199).** `/complete` and `PrintEventService.Finished` save
  through `PrintFinishTransition`: a conditional `ExecuteUpdate` (still unfinished) and the
  `SaveChanges` in **one** transaction, so the loser of two overlapping finishes waits for the
  winner's usage rows, writes nothing, then clears its tracker and reruns as a replay of a
  finished print (no second notification, row or 500).
