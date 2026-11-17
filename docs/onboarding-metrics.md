# Onboarding metrics

How to tell whether new users get started. The funnel groups sign-ups by week and reports, for each
group, the share who add a printer, add a material, log a first print within 24 hours and within 7
days, and come back on a later day. The UI's `Auth_SignedUp` event carries a `source` (`home`,
`slicer` or `direct`), so each weekly group is split by where the user came from.
The printer and material percentages have no time bound: they count an add at any point up to now.
The same query is the "New-user onboarding funnel" panel on the Usage tab of the monitoring
workbook (`monitoring/build_workbook.py`).

Run it in Application Insights (Logs) on the API resource. Replace `<ui-app-insights>` with the UI
resource name, and the time range sets `windowStart` and `windowEnd`.

```kusto
let windowStart = ago(90d);
let windowEnd = now();
let apiEvents = customEvents | where cloud_RoleName == '3d-print-log-api-prod';  // the workbook's ApiRole
let signups = apiEvents
| where name == 'UserSignedUp' and timestamp between (windowStart .. windowEnd)
| extend UserId = tostring(customDimensions.UserId)
| where isnotempty(UserId)
| summarize SignedUpAt = min(timestamp) by UserId;
let sources = app('<ui-app-insights>').customEvents
| where name == 'Auth_SignedUp' and timestamp >= windowStart and isnotempty(user_AuthenticatedId)
| summarize arg_min(timestamp, customDimensions) by UserId = user_AuthenticatedId
| project UserId, Source = tostring(customDimensions.source);
let firsts = apiEvents
| where timestamp >= windowStart and isnotempty(user_AuthenticatedId)
| where tostring(customDimensions.AspNetCoreEnvironment) == 'Production'
| summarize
    PrinterAt = minif(timestamp, name in ('PrinterAdded', 'McpPrinterAdded')),
    MaterialAt = minif(timestamp, name == 'FilamentAdd'),
    PrintAt = minif(timestamp, name == 'PrintAdded')
    by UserId = user_AuthenticatedId;
let activity = requests
| where cloud_RoleName == '3d-print-log-api-prod' and timestamp >= windowStart and isnotempty(user_AuthenticatedId)
| where not(name startswith 'OPTIONS ')
| where name !in ('GET Notifications/GetUnreadCount', 'GET /metrics', 'GET /')
| summarize LastActiveAt = max(timestamp) by UserId = user_AuthenticatedId;
signups
| lookup kind=leftouter sources on UserId
| lookup kind=leftouter firsts on UserId
| lookup kind=leftouter activity on UserId
| extend Cohort = startofweek(SignedUpAt), Source = coalesce(Source, 'unknown')
| summarize SignedUp = count(),
    Printers = countif(isnotnull(PrinterAt)),
    Materials = countif(isnotnull(MaterialAt)),
    Prints24h = countif(PrintAt <= SignedUpAt + 1d),
    Prints7d = countif(PrintAt <= SignedUpAt + 7d),
    Returned = countif(startofday(LastActiveAt) > startofday(SignedUpAt))
    by Cohort, Source
| project Cohort, Source, SignedUp,
    PrinterPct = round(100.0 * Printers / SignedUp, 1),
    MaterialPct = round(100.0 * Materials / SignedUp, 1),
    Print24hPct = round(100.0 * Prints24h / SignedUp, 1),
    Print7dPct = round(100.0 * Prints7d / SignedUp, 1),
    ReturnedPct = round(100.0 * Returned / SignedUp, 1)
| order by Cohort desc, SignedUp desc
```

## Caveats

- **Data starts at the release.** `UserSignedUp` gained `UserId` and `identityProvider` custom
  dimensions with onboarding, and `Auth_SignedUp` only exists from the UI release. Earlier sign-ups
  have no `UserId` and drop out of the query, so the first weeks after release are small.
- **Everything joins on `user_AuthenticatedId`**, the local user id. The sign-up event, the first
  actions and the requests must all carry it. A user whose UI event never arrived (ad blocker,
  closed tab) shows as `Source = unknown`.
- **Only production counts.** The first-action events are filtered to
  `AspNetCoreEnvironment == 'Production'`, which leaves out `E2ETesting` runs.
- **Percentages cover everyone in the group**, including users who signed up in the last day or
  week, so the 24-hour and 7-day columns undercount for the newest group. Compare complete weeks.
- **"Returned" means any request on a later calendar day (UTC)** than the sign-up day. Background
  polling from an open tab counts, so treat it as an upper bound on real return visits.
- **Sampling.** Application Insights sampling drops events at random, which can undercount small
  groups. Check the sampling rate before reading much into a week with few sign-ups.
