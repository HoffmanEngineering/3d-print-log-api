# Email runbook

Operating notes for the email platform: how addresses get into the database, and what to do
before and while campaigns send. Measurement queries are in [email-metrics.md](email-metrics.md).

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

## Configuration

Everything lives in the `Email` section. On App Service, set it as application settings with
double underscores, for example `Email__Enabled` or `Email__Campaigns__monthly-recap__Enabled`.
Arrays take an index: `Email__AllowListUserIds__0`. Every key and its default is in
`EmailOptions.cs`; the ones you will touch:

| Setting | Use |
|---|---|
| `Email__Enabled` | Master switch. `false` stops all sending within one dispatch tick (30 s). |
| `Email__DryRun` | Render and record everything, send nothing. |
| `Email__AllowListUserIds__N` | When any are set, only these users receive mail; everyone else waits. |
| `Email__DailyCap` | Maximum sends per UTC day. Empty means unlimited. |
| `Email__Campaigns__<name>__Enabled` | Per-campaign switch: `onboarding`, `printer-silent`, `monthly-recap`. |
| `Email__Onboarding__StartDate` | Only users who sign up on or after this instant get onboarding. |
| `Email__Notice__Required` | Leave `true`: only users who have seen the in-app notice get engagement mail. |

With `Enabled` and `DryRun` both `false` (the default), the workers run but do nothing. The
app refuses to start when either is on and a required key (`PostalAddress`, `FromAddress`,
`WebBaseUrl`, `ApiBaseUrl`, `UnsubscribeSigningKeys`, `SuppressionPeppers`) is missing. The
`Ses` credentials are not checked at startup; a wrong key shows up as `Email_Failed` events on
the first real send, so confirm one send after changing them.

**Do not scale the App Service plan out while email is enabled.** The daily cap, the send rate
and one-email-per-user-per-tick are enforced per process. Two instances would not send the same
row twice, but they would send at double the rate and could exceed the cap. Keep **Always On**
enabled: the workers only run while the process is alive.

## Dry run

1. Set `Email__DryRun=true` and enable every campaign you want to measure.
2. Let it run for about a week. Rows end as `Skipped` with `SkipReason = 'dry-run'`, and the
   `Email_DryRun` event shows the real volume per campaign.
3. Check volumes:

   ```sql
   SELECT Campaign, CAST(CreatedAt AS date) AS Day, COUNT(*) AS Rows
   FROM EmailOutbox WHERE SkipReason = 'dry-run'
   GROUP BY Campaign, CAST(CreatedAt AS date) ORDER BY Day, Campaign;
   ```

4. **Before going live, delete the dry-run rows.** They hold the `(UserId, Campaign,
   PeriodKey)` key, so leaving them would stop those periods from ever being sent, and their
   exposure would pollute the holdout metrics:

   ```sql
   DELETE FROM EmailOutbox WHERE SkipReason = 'dry-run';
   ```

   Then set `Email__DryRun=false`.

## Going live

1. `Email__Enabled=true` with `Email__AllowListUserIds__0=<your user id>`. Look up your id with
   `SELECT Id FROM Users WHERE Email = '<your address>'`.
2. Check a real email in Gmail, Outlook and Apple Mail: layout, links, footer address.
3. In Gmail, "Show original": SPF, DKIM and DMARC all `PASS`, and the DKIM signature's `h=`
   list includes `List-Unsubscribe` and `List-Unsubscribe-Post`. Without those two in `h=`,
   Gmail ignores one-click unsubscribe.
4. Press Gmail's own "Unsubscribe" link next to the sender. The category should switch off in
   Settings > Email.
5. Remove the allow-list entries. Enable `onboarding` first (new signups only), then
   `printer-silent`, then `monthly-recap`.

### Monthly recap ramp

The first recap is the largest single send. Ramp the daily cap over the first days of the
month: `500`, then `1000`, `2500`, `5000`. Rows over the cap wait for the next UTC day; a
recap row expires five days after its send time, so the ramp must finish by the 5th.

**Re-run the Auth0 backfill (above) a few days before each recap** until nearly every active
user has signed in since the Auth0 Action was deployed. A user without a verified address is
skipped as `unverified`.

## Watching reputation

The numbers SES enforces are the CloudWatch alarms on `Reputation.ComplaintRate` and
`Reputation.BounceRate`, which page the alert subscription. SES pauses sending at its own
thresholds. The dispatcher then logs `Email_SendingPaused`, puts the row back untouched and
ends the tick; it tries again every tick, so the event repeats every 30 seconds until AWS
resumes the account. Set `Email__Enabled=false` to quiet it while you deal with AWS.

To find which campaign is behind a rise, compare complaints with sends per campaign over the
last 7 days. Complaints are recorded as suppressions keyed by the same address hash the outbox
records in `SentTo`:

```sql
WITH sent AS (
    SELECT Campaign, SentTo FROM EmailOutbox
    WHERE Status = 2 AND SentAt >= DATEADD(day, -7, SYSDATETIMEOFFSET())
)
SELECT s.Campaign,
       COUNT(*) AS Sent,
       COUNT(c.Id) AS Complaints,
       CAST(100.0 * COUNT(c.Id) / COUNT(*) AS decimal(6, 3)) AS ComplaintPercent
FROM sent s
LEFT JOIN EmailSuppressions c
       ON c.EmailHash = s.SentTo AND c.Reason = 2
      AND c.CreatedAt >= DATEADD(day, -7, SYSDATETIMEOFFSET())
GROUP BY s.Campaign;
```

Anything above 0.1% for a campaign is worth pausing it (`Email__Campaigns__<name>__Enabled=false`)
while you look at its content and audience. This is a leading indicator, not the rate SES
computes.

## Rotating keys

- **SES access key.** Create a second key for the IAM user in the infra repo, set
  `Email__Ses__AccessKeyId` / `Email__Ses__SecretAccessKey` to it, restart, confirm a send,
  then delete the old key. There is no overlap problem: the client is created per process.
- **Unsubscribe signing keys.** Prepend a new base64 key (32+ bytes) to
  `Email__UnsubscribeSigningKeys` and keep the old one second. New links are signed with the
  first; links already in inboxes still verify against the second. Remove the old key after
  at least 60 days (the manage-link lifetime). Unsubscribe links in old emails signed only by a
  removed key stop working, so keep it longer if in doubt.
- **Suppression peppers.** Never rotate in place. Prepending a new pepper is supported (all are
  checked on lookup), but removing one forgets every suppression hashed with it.

## Pausing everything

Set `Email__Enabled=false`. Pending rows stay pending and resume when it is turned back on,
unless they pass their expiry first (they then end as `Skipped(expired)`).
