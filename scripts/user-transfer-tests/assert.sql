USE PrintLogTransferTest;
GO
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;

-- Resolved by OAuthUserId, never hardcoded: DELETE does not reset an IDENTITY
-- seed, so re-running seed.sql hands out fresh Ids (4,5,6 on the second pass).
DECLARE @Src bigint = (SELECT Id FROM dbo.Users WHERE OAuthUserId = 'auth0|old');
DECLARE @Tgt bigint = (SELECT Id FROM dbo.Users WHERE OAuthUserId = 'auth0|new');
DECLARE @Oth bigint = (SELECT Id FROM dbo.Users WHERE OAuthUserId = 'auth0|by');

IF @Src IS NULL OR @Tgt IS NULL OR @Oth IS NULL
    THROW 50100, 'Seed users not found. Run seed.sql before assert.sql.', 1;

DECLARE @r TABLE (Check_ varchar(80), Expected varchar(40), Actual varchar(40), Result varchar(6));

DECLARE @a varchar(40), @e varchar(40);

-- 1. No row anywhere still points at the source, except the intentionally-kept
--    Subscriptions row (@TransferSubscription = 0).
SELECT @a = CAST(SUM(c) AS varchar(40)) FROM (
  SELECT COUNT(*) c FROM dbo.Prints WHERE CreatedById=@Src OR UpdatedById=@Src
  UNION ALL SELECT COUNT(*) FROM dbo.Printers WHERE UserId=@Src
  UNION ALL SELECT COUNT(*) FROM dbo.Filaments WHERE CreatedById=@Src OR UpdatedById=@Src
  UNION ALL SELECT COUNT(*) FROM dbo.FilamentAdjustments WHERE CreatedById=@Src OR UpdatedById=@Src
  UNION ALL SELECT COUNT(*) FROM dbo.Projects WHERE CreatedById=@Src OR UpdatedById=@Src
  UNION ALL SELECT COUNT(*) FROM dbo.ProjectImages WHERE CreatedById=@Src OR UpdatedById=@Src
  UNION ALL SELECT COUNT(*) FROM dbo.PrintImages WHERE CreatedById=@Src OR UpdatedById=@Src
  UNION ALL SELECT COUNT(*) FROM dbo.PrintAttachments WHERE CreatedById=@Src OR UpdatedById=@Src
  UNION ALL SELECT COUNT(*) FROM dbo.Files WHERE CreatedById=@Src OR UpdatedById=@Src
  UNION ALL SELECT COUNT(*) FROM dbo.Comments WHERE CreatedById=@Src OR UpdatedById=@Src
  UNION ALL SELECT COUNT(*) FROM dbo.PrintComments WHERE CreatedById=@Src OR UpdatedById=@Src
  UNION ALL SELECT COUNT(*) FROM dbo.PrinterMaintenance WHERE CreatedById=@Src OR UpdatedById=@Src
  UNION ALL SELECT COUNT(*) FROM dbo.Feedback WHERE CreatedById=@Src OR UpdatedById=@Src
  UNION ALL SELECT COUNT(*) FROM dbo.UserSettings WHERE UserId=@Src OR CreatedById=@Src OR UpdatedById=@Src
  UNION ALL SELECT COUNT(*) FROM dbo.CuraSettings WHERE UserId=@Src
  UNION ALL SELECT COUNT(*) FROM dbo.McpIdempotencyRecords WHERE UserId=@Src
  UNION ALL SELECT COUNT(*) FROM dbo.Notifications WHERE UserId=@Src OR TriggeredByUserId=@Src
  UNION ALL SELECT COUNT(*) FROM dbo.UserApiKeys WHERE UserId=@Src OR CreatedById=@Src OR UpdatedById=@Src
) x;
INSERT INTO @r VALUES ('no residual references to source', '0', @a, CASE WHEN @a='0' THEN 'PASS' ELSE 'FAIL' END);

-- 2. Subscription intentionally retained on the source.
SELECT @a = CAST(COUNT(*) AS varchar(40)) FROM dbo.Subscriptions WHERE UserId=@Src;
INSERT INTO @r VALUES ('subscription retained on source', '1', @a, CASE WHEN @a='1' THEN 'PASS' ELSE 'FAIL' END);

-- 3. Target now owns the print, printer, filament, project.
SELECT @a = CAST(COUNT(*) AS varchar(40)) FROM dbo.Prints WHERE CreatedById=@Tgt AND Title='Old Print';
INSERT INTO @r VALUES ('print moved to target', '1', @a, CASE WHEN @a='1' THEN 'PASS' ELSE 'FAIL' END);
SELECT @a = CAST(COUNT(*) AS varchar(40)) FROM dbo.Printers WHERE UserId=@Tgt;
INSERT INTO @r VALUES ('target owns both printers', '2', @a, CASE WHEN @a='2' THEN 'PASS' ELSE 'FAIL' END);

-- 4. Bystander untouched: still owns their printer and print.
SELECT @a = CAST(COUNT(*) AS varchar(40)) FROM dbo.Printers WHERE UserId=@Oth;
INSERT INTO @r VALUES ('bystander printer untouched', '1', @a, CASE WHEN @a='1' THEN 'PASS' ELSE 'FAIL' END);
SELECT @a = CAST(COUNT(*) AS varchar(40)) FROM dbo.Prints WHERE CreatedById=@Oth;
INSERT INTO @r VALUES ('bystander print untouched', '1', @a, CASE WHEN @a='1' THEN 'PASS' ELSE 'FAIL' END);

-- 5. UserSettings: target's currency preference won; source-only setting moved;
--    no duplicate type for the target.
SELECT @a = ISNULL(MAX(Value),'(none)') FROM dbo.UserSettings WHERE UserId=@Tgt AND UserSettingTypeId=5;
INSERT INTO @r VALUES ('target currency preference wins', 'TGT-CURRENCY', @a, CASE WHEN @a='TGT-CURRENCY' THEN 'PASS' ELSE 'FAIL' END);
SELECT @a = ISNULL(MAX(Value),'(none)') FROM dbo.UserSettings WHERE UserId=@Tgt AND UserSettingTypeId=7;
INSERT INTO @r VALUES ('source-only setting moved', 'SRC-DIAMETER', @a, CASE WHEN @a='SRC-DIAMETER' THEN 'PASS' ELSE 'FAIL' END);
SELECT @a = CAST(ISNULL(MAX(c),0) AS varchar(40)) FROM (SELECT COUNT(*) c FROM dbo.UserSettings WHERE UserId=@Tgt GROUP BY UserSettingTypeId) d;
INSERT INTO @r VALUES ('no duplicate setting types on target', '1', @a, CASE WHEN @a='1' THEN 'PASS' ELSE 'FAIL' END);

-- 6. The global (UserId NULL) setting stayed global but its audit cols moved.
SELECT @a = CAST(COUNT(*) AS varchar(40)) FROM dbo.UserSettings WHERE UserId IS NULL AND CreatedById=@Tgt;
INSERT INTO @r VALUES ('global setting stays global, audit moved', '1', @a, CASE WHEN @a='1' THEN 'PASS' ELSE 'FAIL' END);

-- 7. MCP idempotency: source rows gone, target's own row intact.
SELECT @a = CAST(COUNT(*) AS varchar(40)) FROM dbo.McpIdempotencyRecords;
INSERT INTO @r VALUES ('only target MCP record remains', '1', @a, CASE WHEN @a='1' THEN 'PASS' ELSE 'FAIL' END);
SELECT @a = ISNULL(MAX(RequestFingerprint),'(none)') FROM dbo.McpIdempotencyRecords;
INSERT INTO @r VALUES ('surviving MCP record is the target''s', 'fp-tgt', @a, CASE WHEN @a='fp-tgt' THEN 'PASS' ELSE 'FAIL' END);

-- 8. Notifications: the two cross-pair rows collapsed and were deleted;
--    the bystander pair and the actorless one survive, now owned by target.
SELECT @a = CAST(COUNT(*) AS varchar(40)) FROM dbo.Notifications;
INSERT INTO @r VALUES ('self-notifications removed', '3', @a, CASE WHEN @a='3' THEN 'PASS' ELSE 'FAIL' END);
SELECT @a = CAST(COUNT(*) AS varchar(40)) FROM dbo.Notifications WHERE UserId=@Tgt AND TriggeredByUserId=@Tgt;
INSERT INTO @r VALUES ('no self-notification survived', '0', @a, CASE WHEN @a='0' THEN 'PASS' ELSE 'FAIL' END);

-- 9. The comment the source left on the bystander's print is now the target's,
--    and still attached to the bystander's print.
SELECT @a = CAST(COUNT(*) AS varchar(40))
FROM dbo.PrintComments pc
JOIN dbo.Prints p ON p.Id = pc.PrintId
WHERE pc.CreatedById=@Tgt AND p.CreatedById=@Oth;
INSERT INTO @r VALUES ('cross-user comment reattributed', '1', @a, CASE WHEN @a='1' THEN 'PASS' ELSE 'FAIL' END);

-- 10. Referential integrity holds across the whole database.
DBCC CHECKCONSTRAINTS WITH ALL_CONSTRAINTS, NO_INFOMSGS;
INSERT INTO @r VALUES ('DBCC CHECKCONSTRAINTS', 'no violations', 'see above', 'INFO');

SELECT Check_, Expected, Actual, Result FROM @r;

SELECT CASE WHEN EXISTS (SELECT 1 FROM @r WHERE Result='FAIL')
            THEN '*** SOME CHECKS FAILED ***' ELSE '=== ALL CHECKS PASSED ===' END AS Verdict;
