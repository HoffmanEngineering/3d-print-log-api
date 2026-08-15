# User transfer tests

End-to-end test for [`../TransferUserData.sql`](../TransferUserData.sql), the script that
moves one user's data onto another user.

That script runs by hand against production, so it gets a test that runs it against the real
schema rather than a mock: the database is built from the EF migrations, so a migration that
adds a new user-referencing column shows up here.

## Running

```bash
docker compose up -d sqlserver          # from the repo root
pwsh scripts/user-transfer-tests/run-tests.ps1
```

Exits non-zero if anything fails. `-SkipMigrations` reuses an existing test database and skips
the `dotnet ef` step; the rest is safe to re-run as often as you like.

The test database (`PrintLogTransferTest` by default) is separate from `PrintLogDb` and is
never written to by the app. Override with `-Database`, `-Container`, `-SaPassword`, `-HostPort`.

## Files

| File | Purpose |
| --- | --- |
| `run-tests.ps1` | Driver: builds schema, seeds, runs the transfer, asserts, exercises guards |
| `seed.sql` | Source, target and an untouched bystander, with deliberate collisions |
| `assert.sql` | 15 post-transfer checks plus `DBCC CHECKCONSTRAINTS` |

## What is covered

The seed builds three accounts. The **bystander** exists to prove the transfer is surgical: they
own a print the source user commented on, so the test can confirm the comment is reattributed
without the print moving.

Deliberate collisions in the seed:

- the same `UserSettingTypeId` on both accounts (target's value must win)
- the same MCP idempotency key on both accounts
- notifications in both directions between source and target, which collapse into
  self-notifications once the identities merge
- a `UserId IS NULL` global setting audited by the source user, which must stay global while its
  audit columns move
- a subscription on the source only, which the default toggles leave behind

Beyond the assertions, the driver checks the dry run really rolls back, that a second committed
run moves zero rows, and that all five guards abort: same-user (50002), missing user (50004),
schema drift (50006), subscription collision (50007), duplicate settings (50009). The drift guard
is tested by actually creating a table with an unhandled FK to `dbo.Users`.

## Notes

- User ids are resolved by `OAuthUserId`, never hardcoded. `DELETE` does not reset an `IDENTITY`
  seed, so each seed run hands out higher ids than the last.
- `sqlcmd` connects with `QUOTED_IDENTIFIER` **OFF**, which SQL Server rejects for DML that has to
  maintain a filtered index. `TransferUserData.sql` sets it explicitly; `seed.sql` does too.
- The concurrency window documented in `TransferUserData.sql` is not covered here. Run the real
  transfer while the source account is idle.
