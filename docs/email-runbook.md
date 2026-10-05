# Email runbook

Operating notes for the email platform: how addresses get into the database, and what to do
before and while campaigns send. Later sections are added as the sending pipeline lands.

## Where addresses come from

The API never asks users for an email address. It copies the one Auth0 holds:

- **Live sync.** The Auth0 post-login Action in [`docs/auth0/add-email-claims.js`](auth0/add-email-claims.js)
  adds `https://3dprintlog.com/email` and `https://3dprintlog.com/email_verified` to the API
  access token. On each authenticated request the claims transformer copies them onto
  `Users.Email` / `Users.EmailVerified`, at most once a day per distinct value, and writes only
  when something changed (`EmailUpdatedAt` records the last real change).
- **Backfill.** Users who have not signed in since the Action was deployed carry no address
  until they do. The offline backfill below fills them from an Auth0 export.

A token without the claim is treated as unknown and never blanks a stored address.

### Deploying the Auth0 Action (one time)

1. Auth0 dashboard > Actions > Library > Create Action > Login / Post Login, Node 22.
2. Paste `docs/auth0/add-email-claims.js`, Deploy.
3. Actions > Flows > Login: drag the Action into the flow, Apply.
4. Sign in to the site, then call `GET /api/Users/me/email`: it should return your address.

## Backfilling email addresses

Run this once after deploying the Action, and again before each monthly recap until most
active users have signed in since. It is idempotent: re-running changes only rows whose Auth0
data changed.

1. **Export from Auth0.** Create a Management API token with `read:users` (dashboard >
   Applications > APIs > Auth0 Management API > API Explorer), then:

   ```powershell
   ./scripts/auth0-export-users.ps1 -Domain <tenant>.auth0.com -Token $token -OutFile $HOME/auth0-users.json.gz
   ```

   The file holds every user's address. Keep it outside the repository.

2. **Dry run.** Nothing is written; the counts show what would change.

   ```powershell
   dotnet run --project PrintLogApi.Tools -c Release -- email-backfill `
     --file $HOME/auth0-users.json.gz --connection "<production connection string>" --dry-run
   ```

3. **Check the counts.**
   - `Read`: Auth0 accounts in the export.
   - `Matched`: those that are 3D Print Log users. Close to the `Users` row count.
   - `Unmatched`: Auth0 accounts that never reached the API (abandoned signups). Expected, not an error.
   - `Updated`: rows that change. On the first run, roughly `Matched`; later, small.
   - `Verified` / `Unverified`: only verified addresses receive email.

4. **Real run.** The same command without `--dry-run`. Re-running it should report `Updated 0`.

5. **Delete the export file.**

The backfill also fills `Users.CreatedDate` from Auth0's `created_at` where the API has no
signup date. It never overwrites one the API recorded.
