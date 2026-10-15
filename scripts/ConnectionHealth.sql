/*
================================================================================
 ConnectionHealth.sql

 Admin and analytics queries over the health connectors report with their
 heartbeat (printlog-bridge#37): which agent, Klipper and Moonraker versions are
 in the field, and which connections have a stuck outbox or keep failing.

 Read only. Run any section against the production database (Azure Data Studio,
 sqlcmd), one at a time or all together. Each reads dbo.Connections alone and
 never shows who a connection belongs to beyond its UserId.

 "Active" means a heartbeat within the last 15 minutes, the same rule as
 Connection.StaleAfter (agents beat every 5 minutes). Connections from bridges
 older than the health fields have them all NULL and show as "(not reported)".

 The thresholds are variables: change them here, not in each query.
================================================================================
*/

DECLARE @ActiveSince     datetime2 = DATEADD(MINUTE, -15, SYSUTCDATETIME());
DECLARE @StuckAfter      datetime2 = DATEADD(HOUR, -1, SYSUTCDATETIME());  -- oldest outbox entry queued before this
DECLARE @RepeatedErrors  int       = 3;                                     -- errors between two heartbeats
DECLARE @RecentErrorSince datetime2 = DATEADD(HOUR, -1, SYSUTCDATETIME());

-- 1. Active bridges by agent version, and how they reach the printer.
SELECT
    c.Kind,
    COALESCE(c.AgentVersion, '(not reported)') AS AgentVersion,
    COUNT(*)                                    AS ActiveConnections,
    COUNT(DISTINCT c.UserId)                    AS Users,
    SUM(CASE WHEN c.Mode = 'local'  THEN 1 ELSE 0 END) AS LocalMode,
    SUM(CASE WHEN c.Mode = 'remote' THEN 1 ELSE 0 END) AS RemoteMode,
    SUM(CASE WHEN c.Snapshots = 1 THEN 1 ELSE 0 END)      AS SnapshotsOn,
    SUM(CASE WHEN c.SlicerSettings = 1 THEN 1 ELSE 0 END) AS SlicerSettingsOn
FROM dbo.Connections AS c
WHERE c.LastSeenAt >= @ActiveSince
GROUP BY c.Kind, COALESCE(c.AgentVersion, '(not reported)')
ORDER BY c.Kind, ActiveConnections DESC;

-- 2. Klipper versions on active connections, with how many are failing.
SELECT
    COALESCE(c.KlipperVersion, '(not reported)') AS KlipperVersion,
    COUNT(*)                                      AS ActiveConnections,
    SUM(CASE WHEN c.ErrorCount >= @RepeatedErrors THEN 1 ELSE 0 END) AS WithRepeatedErrors,
    SUM(CASE WHEN c.OutboxOldestQueuedAt < @StuckAfter THEN 1 ELSE 0 END) AS WithStuckOutbox
FROM dbo.Connections AS c
WHERE c.LastSeenAt >= @ActiveSince
GROUP BY COALESCE(c.KlipperVersion, '(not reported)')
ORDER BY ActiveConnections DESC;

-- 3. Moonraker versions on active connections, with how many are failing.
SELECT
    COALESCE(c.MoonrakerVersion, '(not reported)') AS MoonrakerVersion,
    COUNT(*)                                        AS ActiveConnections,
    SUM(CASE WHEN c.ErrorCount >= @RepeatedErrors THEN 1 ELSE 0 END) AS WithRepeatedErrors,
    SUM(CASE WHEN c.OutboxOldestQueuedAt < @StuckAfter THEN 1 ELSE 0 END) AS WithStuckOutbox
FROM dbo.Connections AS c
WHERE c.LastSeenAt >= @ActiveSince
GROUP BY COALESCE(c.MoonrakerVersion, '(not reported)')
ORDER BY ActiveConnections DESC;

-- 4. Printer models the slicers report, on active connections.
SELECT
    COALESCE(c.PrinterModel, '(not reported)') AS PrinterModel,
    COUNT(*)                                    AS ActiveConnections
FROM dbo.Connections AS c
WHERE c.LastSeenAt >= @ActiveSince
GROUP BY COALESCE(c.PrinterModel, '(not reported)')
ORDER BY ActiveConnections DESC;

-- 5. Active connections with a stuck outbox: events queued over an hour ago and still unsent.
SELECT
    c.Id,
    c.UserId,
    c.Kind,
    c.AgentVersion,
    c.KlipperVersion,
    c.MoonrakerVersion,
    c.OutboxDepth,
    c.OutboxOldestQueuedAt,
    DATEDIFF(MINUTE, c.OutboxOldestQueuedAt, SYSUTCDATETIME()) AS WaitingMinutes,
    c.LastError,
    c.LastErrorAt,
    c.LastSeenAt
FROM dbo.Connections AS c
WHERE c.LastSeenAt >= @ActiveSince
  AND c.OutboxDepth > 0
  AND c.OutboxOldestQueuedAt < @StuckAfter
ORDER BY c.OutboxOldestQueuedAt;

-- 6. Active connections with repeated errors: many between two heartbeats, or one in the last hour.
SELECT
    c.Id,
    c.UserId,
    c.Kind,
    c.AgentVersion,
    c.KlipperVersion,
    c.MoonrakerVersion,
    c.Mode,
    c.ErrorCount,
    c.LastError,
    c.LastErrorAt,
    c.LastSeenAt
FROM dbo.Connections AS c
WHERE c.LastSeenAt >= @ActiveSince
  AND (c.ErrorCount >= @RepeatedErrors OR c.LastErrorAt >= @RecentErrorSince)
ORDER BY c.ErrorCount DESC, c.LastErrorAt DESC;

-- 7. The most common recent errors across all active connections (already scrubbed by the agent).
SELECT TOP (25)
    c.LastError,
    COUNT(*)              AS Connections,
    MAX(c.LastErrorAt)    AS MostRecent
FROM dbo.Connections AS c
WHERE c.LastSeenAt >= @ActiveSince
  AND c.LastErrorAt >= DATEADD(DAY, -1, SYSUTCDATETIME())
GROUP BY c.LastError
ORDER BY Connections DESC, MostRecent DESC;
