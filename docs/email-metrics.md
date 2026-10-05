# Email metrics

How to tell whether each campaign changes behavior. Every campaign holds out a stable
`HoldoutPercent` of eligible users (default 10%). Held-out users go through every gate and
render check like everyone else, then end as `Skipped(holdout)` instead of being sent. Both
arms write the same `Exposure` JSON to their `EmailOutbox` row:

| Key | Meaning |
|---|---|
| `eligibleAt` | When the dispatcher decided: the start of the measurement window |
| `arm` | `treatment` (sent) or `holdout` (not sent) |
| campaign keys | `month` / `printCount` (recap), `printers[]` (printer-silent), `step` (onboarding) |

A treatment row that later failed to send has `Status = 3` (Failed) and still carries its
exposure. The queries below count only `Sent` (2) and `Skipped(holdout)` (4) rows, so both
arms mean the same thing: "eligible, relevant, and either treated or held out".

Run these against the production database (SQL Server). Review after about three months of
data, then keep, shrink or remove the holdout.

## Shared base

Every query starts from the exposed rows of one campaign:

```sql
-- Exposed rows for a campaign, with their arm and window start.
SELECT o.Id, o.UserId, o.PeriodKey,
       JSON_VALUE(o.Exposure, '$.arm') AS Arm,
       CAST(JSON_VALUE(o.Exposure, '$.eligibleAt') AS datetimeoffset) AS EligibleAt
FROM EmailOutbox o
WHERE o.Campaign = 'monthly-recap'
  AND (o.Status = 2 OR (o.Status = 4 AND o.SkipReason = 'holdout'))
  AND o.Exposure IS NOT NULL;
```

Dry-run rows (`SkipReason = 'dry-run'`) also carry exposure. Delete them at go-live (see the
runbook) so they never reach these numbers.

## Onboarding: badges unlocked within 14 days

Share of exposed users who unlock `plugged-in` (connected an integration) or `first-print`
within 14 days of their **first** onboarding exposure.

```sql
WITH exposed AS (
    SELECT o.UserId,
           JSON_VALUE(o.Exposure, '$.arm') AS Arm,
           MIN(CAST(JSON_VALUE(o.Exposure, '$.eligibleAt') AS datetimeoffset)) AS EligibleAt
    FROM EmailOutbox o
    WHERE o.Campaign = 'onboarding'
      AND (o.Status = 2 OR (o.Status = 4 AND o.SkipReason = 'holdout'))
      AND o.Exposure IS NOT NULL
    GROUP BY o.UserId, JSON_VALUE(o.Exposure, '$.arm')
)
SELECT e.Arm,
       COUNT(*) AS Users,
       SUM(CASE WHEN EXISTS (
               SELECT 1 FROM UserAchievements a
               WHERE a.UserId = e.UserId
                 AND a.AchievementKey IN ('plugged-in', 'first-print')
                 AND a.UnlockedAt >= CAST(e.EligibleAt AS datetime2)
                 AND a.UnlockedAt < DATEADD(day, 14, CAST(e.EligibleAt AS datetime2)))
           THEN 1 ELSE 0 END) AS Converted,
       CAST(100.0 * SUM(CASE WHEN EXISTS (
               SELECT 1 FROM UserAchievements a
               WHERE a.UserId = e.UserId
                 AND a.AchievementKey IN ('plugged-in', 'first-print')
                 AND a.UnlockedAt >= CAST(e.EligibleAt AS datetime2)
                 AND a.UnlockedAt < DATEADD(day, 14, CAST(e.EligibleAt AS datetime2)))
           THEN 1 ELSE 0 END) / COUNT(*) AS decimal(5, 1)) AS ConvertedPercent
FROM exposed e
WHERE e.EligibleAt < DATEADD(day, -14, SYSDATETIMEOFFSET())   -- only complete windows
GROUP BY e.Arm;
```

`UnlockedAt` is a UTC `datetime2`; `CAST(datetimeoffset AS datetime2)` keeps the UTC clock
time because `eligibleAt` is written in UTC.

## Printer silent: printers back within 14 days

Share of printers listed in an exposure that report another automated print within 14 days.
`lastAutomatedPrintUtc` is the print that started the silence. Automated sources are
`SlicerPlugin` (2), `OctoPrint` (3), `Moonraker` (4) and `ApiKey` (6). "Print date" is
`CreatedDate`, as the campaign uses.

```sql
WITH listed AS (
    SELECT JSON_VALUE(o.Exposure, '$.arm') AS Arm,
           CAST(JSON_VALUE(o.Exposure, '$.eligibleAt') AS datetimeoffset) AS EligibleAt,
           CAST(p.[id] AS bigint) AS PrinterId
    FROM EmailOutbox o
    CROSS APPLY OPENJSON(o.Exposure, '$.printers') WITH ([id] bigint '$.id') p
    WHERE o.Campaign = 'printer-silent'
      AND (o.Status = 2 OR (o.Status = 4 AND o.SkipReason = 'holdout'))
      AND o.Exposure IS NOT NULL
)
SELECT l.Arm,
       COUNT(*) AS Printers,
       SUM(CASE WHEN EXISTS (
               SELECT 1 FROM Prints pr
               WHERE pr.PrinterId = l.PrinterId
                 AND pr.Source IN (2, 3, 4, 6)
                 AND pr.CreatedDate >= CAST(l.EligibleAt AS datetime2)
                 AND pr.CreatedDate < DATEADD(day, 14, CAST(l.EligibleAt AS datetime2)))
           THEN 1 ELSE 0 END) AS Recovered
FROM listed l
WHERE l.EligibleAt < DATEADD(day, -14, SYSDATETIMEOFFSET())
GROUP BY l.Arm;
```

## Monthly recap: prints logged afterwards

Share of exposed users who log any print in the 30, 60 and 90 days after exposure. A user
appears once per recap month; read each month separately with the `PeriodKey` filter, or
leave it out to pool them.

```sql
WITH exposed AS (
    SELECT o.UserId, o.PeriodKey,
           JSON_VALUE(o.Exposure, '$.arm') AS Arm,
           CAST(JSON_VALUE(o.Exposure, '$.eligibleAt') AS datetimeoffset) AS EligibleAt
    FROM EmailOutbox o
    WHERE o.Campaign = 'monthly-recap'
      AND (o.Status = 2 OR (o.Status = 4 AND o.SkipReason = 'holdout'))
      AND o.Exposure IS NOT NULL
      -- AND o.PeriodKey = '2026-11'
)
SELECT e.Arm,
       COUNT(*) AS Exposures,
       AVG(CASE WHEN EXISTS (SELECT 1 FROM Prints p WHERE p.CreatedById = e.UserId
                    AND p.CreatedDate >= CAST(e.EligibleAt AS datetime2)
                    AND p.CreatedDate < DATEADD(day, 30, CAST(e.EligibleAt AS datetime2)))
                THEN 100.0 ELSE 0 END) AS Within30Percent,
       AVG(CASE WHEN EXISTS (SELECT 1 FROM Prints p WHERE p.CreatedById = e.UserId
                    AND p.CreatedDate >= CAST(e.EligibleAt AS datetime2)
                    AND p.CreatedDate < DATEADD(day, 60, CAST(e.EligibleAt AS datetime2)))
                THEN 100.0 ELSE 0 END) AS Within60Percent,
       AVG(CASE WHEN EXISTS (SELECT 1 FROM Prints p WHERE p.CreatedById = e.UserId
                    AND p.CreatedDate >= CAST(e.EligibleAt AS datetime2)
                    AND p.CreatedDate < DATEADD(day, 90, CAST(e.EligibleAt AS datetime2)))
                THEN 100.0 ELSE 0 END) AS Within90Percent
FROM exposed e
WHERE e.EligibleAt < DATEADD(day, -90, SYSDATETIMEOFFSET())   -- shorten for early reads
GROUP BY e.Arm;
```

`CreatedDate` (when the print was logged) is the right clock here: the question is whether the
user came back and logged something, not when the print itself ran.

## Optional: site visits after a recap (Application Insights)

Authenticated requests are not in the database. The telemetry initializer attaches the user id
as `user_AuthenticatedId`, so visits can be counted in Log Analytics. Export the exposed user
ids and arms from the base query above, then:

```kusto
let exposed = datatable(UserId: string, Arm: string, EligibleAt: datetime) [
    // paste rows from the SQL base query
];
requests
| where timestamp > ago(120d)
| where isnotempty(user_AuthenticatedId)
| join kind=inner exposed on $left.user_AuthenticatedId == $right.UserId
| where timestamp between (EligibleAt .. EligibleAt + 30d)
| summarize Visited = dcount(user_AuthenticatedId) by Arm
```

## Operational counters

Prometheus, next to the existing `printlog_*` metrics:

| Metric | Labels |
|---|---|
| `printlog_email_sent_total` | `campaign` |
| `printlog_email_skipped_total` | `campaign`, `reason` |
| `printlog_email_failed_total` | `campaign`, `kind` |
| `printlog_email_bounced_total` | |
| `printlog_email_complained_total` | |
| `printlog_email_outbox_pending` (gauge) | |

Application Insights events carry the same dimensions: `Email_Queued`, `Email_Sent`,
`Email_Skipped`, `Email_Failed`, `Email_Deferred`, `Email_Bounced`, `Email_Complained`,
`Email_DryRun`, `Email_SendingPaused`, `Email_WebhookFailed`, `EmailSync_Failed` and
`EmailPreference_Unrecognized`.

The reputation numbers SES enforces are its own `Reputation.ComplaintRate` and
`Reputation.BounceRate` CloudWatch metrics, which the infra repo alarms on. The per-campaign
complaint ratio in the runbook is a leading indicator for finding the campaign behind a rise,
not a reproduction of those numbers.
