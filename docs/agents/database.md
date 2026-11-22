# Database, background jobs and migrations

The two rules every migration follows (auto-applied only in Development and E2ETesting;
backwards compatible, additive only) are in AGENTS.md, Database. This file has the rest.

## Database

Production is Azure SQL **Standard S0 (10 DTU)**. It reaches 100% DTU in short bursts, so a query
that runs on a timer or a poll needs an index that serves it (api#309 added three). Index builds on
big tables follow `ReduceDatabaseLoadIndexes`: hand-written, `IF NOT EXISTS`, `WITH (ONLINE =
ON)` with an offline retry on error 1712 (Express), and `suppressTransaction: true` so one index's
schema lock is not held while the next one builds (EF logs warning 20410 for that; it is expected).
A **filtered index** is used only by queries whose predicate is the filter as a literal: EF writes
`[IsRead] = CAST(0 AS bit)` for `!n.IsRead`, but a value passed in as a variable becomes a parameter
and the index is ignored. Pin the shape as `DatabaseLoadIndexTests` does. A session writing to a
table with a filtered index needs `QUOTED_IDENTIFIER ON` (SqlClient's default; `sqlcmd` needs `-I`).

### The email dispatcher polls every 2 minutes

`EmailDispatcherService` runs about five statements per tick even with nothing due, and at 30 s
that was most of the API's background SQL. `EmailOptions.DispatchInterval` is now 2 minutes. What
must go out promptly wakes it instead: `DeviceGrantsController` calls `EmailDispatchSignal.Wake()`
after a pairing that queued the `device-paired` notice commits. Wakes coalesce and are per process
(the dispatcher is single-instance anyway). Anything else that queues time-sensitive mail must wake
it the same way. Verification email is Auth0's, not ours; the welcome email is queued by the
15-minute evaluator, so two more minutes do not matter there.

### Slicer uploader settings are deleted after 7 days

`CuraSettings` holds two kinds of record. The Cura plugin's and Slic3r uploader's
(`POST /api/Cura/settings`, no `PrintId`) carry the slicer's snapshot image as base64 and exist
only to fill the add-print form; the print keeps nothing that points back to one. A connector's
(#164) is linked by `PrintId` and holds parsed settings, no image.
`CuraSettingsRetentionJob` deletes the first kind once `CuraSettingsRetention:RetentionDays` (7)
old, in batches of `BatchSize` (50) with `BatchPause` (5 s) between them, oldest first
through the filtered index `IX_CuraSettings_Unlinked_CreatedDate`. A run stops after
`MaxRunDuration` (30 min) or at the first failed batch; if expired records are left either way, the
next run is `BacklogRetryInterval` (1 h) later, otherwise `RunInterval` (1 day). That is what clears
a backlog: the first version used 200-row batches (14 to 28 s each on S0, against SqlClient's 30 s
timeout), stopped at the first timeout and waited a day, and deleted 3,600 of 554,000 rows in two
days. The first run waits ten minutes after start, apart from `PrinterPopularityJob`'s five; the two
timed out together. **It never deletes a record with
a `PrintId`**: keep the literal `PrintId IS NULL` in `Expired`, which is also what matches the
index's filter (`CuraSettingsRetentionSqlServerTranslationTests`). A form opened from an older
upload gets a 404 from `GET /api/Cura/settings`, and a print created with an expired id still
saves, just without slicer-plugin provenance (`ApplyProvenance`).

### Deactivated accounts are deleted by an in-process job

`POST /api/Users/me/deactivate` only stamps `DeactivationDateTime`. `PendingUserDeletionJob`
(`Services/UserDeletion/`) deletes each account once that is `PendingUserDeactivationTimeInMinutes`
(1440) old, through `IUserDeletionService.DeletePendingDeactivatedUsers`. Configuration:
`PendingUserDeletion:Enabled` (true) and `PendingUserDeletion:RunInterval` (`02:00:00`, validated
at startup to between one minute and one day); App Service names are `PendingUserDeletion__Enabled`
and `PendingUserDeletion__RunInterval`. The first run waits five minutes after start. It is forced
off in E2ETesting (the E2E users are fixtures) and the integration-test host removes it, so tests
drive `RunOnceAsync` instead. Like every hosted service here it runs only while the process is
alive, so the App Service needs **Always On**.

- **It replaced an Azure Function** (`DeleteUsersPendingDeactivation`, every 2 hours) that called
  an anonymous `DELETE /api/Users/pending-deactivation`, which let anyone start the job. That route
  is gone (it now answers 405), so once a release with the job is deployed the function only logs
  a warning every two hours and can be disabled.
- **One user's failure never stops the others.** It is logged with the user id and tracked as
  `DeletePendingDeactivatedUserFailed` (`UserId`, `Stage`, `ExceptionType`); the run's
  `DeletePendingDeactivatedUsers` event carries `Count`, `Deleted` and `Failed`.
- **Auth0 is deleted before the database, on purpose.** The user row is the last thing
  `DeleteAllDataForUser` removes, so any partial failure leaves it and the next run repeats the
  whole user. `Auth0Service.DeleteUser` treats a 404 as done for that repeat. The opposite order
  orphaned the Auth0 account whenever Auth0 failed after the rows were gone. A user whose database
  delete keeps failing has no Auth0 account, so cannot sign in, until a run succeeds.
- `DeleteAllDataForUser` has no enclosing transaction (each `ExecuteDelete` commits on its own),
  which is what makes the retry finish the job. Keep every step safe to run twice.

### Migrations are never executed by the test suite

Integration tests build their schema with `EnsureCreated()` on SQLite, so a migration can be
completely broken and the whole suite still passes. Two SQL Server rules EF's conventions will
happily violate:

- **Multiple cascade paths (error 1785).** An entity deriving from `TimestampEntity` gets Cascade
  FKs to `Users` on `CreatedById`/`UpdatedById` by convention. If that table *also* cascades from
  an owner that itself cascades from `Users`, SQL Server refuses to create the constraint. Hand-edit
  the generated migration to `ReferentialAction.NoAction` for the two audit FKs, leave the model's
  Cascade alone, and say why in a comment — `AddProjects`, `AddFilamentImage` and `AddPrinterImage`
  all had to. Account deletion removes those rows explicitly in `UserDeletionService`, so nothing
  depends on the cascade.
- **Correlated aggregates (error 8124).** Inside a correlated `Sum`, reach related columns through
  the row's own navigation, never off the outer entity. Guard with a translation test in the style
  of `FilamentSqlServerTranslationTests`.

**Verify a new migration against real SQL Server — it takes two minutes.** `docker-compose.yml`
already runs SQL Server 2022 on host port 1434:

```bash
dotnet ef database update --project PrintLogApi --connection "Server=localhost,1434;Database=PrintLogDb_MigrationCheck;User Id=sa;Password=YourStrong@Passw0rd;TrustServerCertificate=True"
```

Drop the scratch database afterwards. On Git Bash, `docker exec` needs `MSYS_NO_PATHCONV=1` or the
container path is mangled into a Windows path.

### Before asking for production approval

Before asking for production approval of a release with migrations, list every index or table
rebuild in the generated migrations SQL with row counts and expected time. The deploy flow and the
`Command Timeout=900` ceiling are in `docs/agents/deployment.md`.
