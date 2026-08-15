/*
================================================================================
 TransferUserData.sql

 Re-points the rows owned or authored by one PrintLog user (@SourceUserId) at
 another PrintLog user (@TargetUserId). Intended for the case where a person
 signed up again under a new email address and wants their history merged into
 the new account.

 Not everything is re-pointed. Three categories are deliberately handled
 differently, each documented at its step: colliding user settings and the MCP
 idempotency cache are deleted rather than moved, and the subscription and API
 keys are governed by the toggles below. The dry-run report names every one.

 Both users must already exist in dbo.Users. Nothing is deleted from dbo.Users
 by this script -- the source account is left behind with no prints, printers,
 filaments, projects or settings. Two things may deliberately stay with it, per
 the toggles below: its Stripe subscription row and its API keys. Deactivate or
 delete the source account afterwards through the normal account-deletion flow
 if desired (UserDeletionService removes whatever the toggles left behind).

 USAGE
   1. Set @SourceUserId / @TargetUserId below.
   2. Run with @DryRun = 1 first. The script executes everything inside a
      transaction, prints the per-table row counts, then ROLLS BACK.
   3. Review the report, then re-run with @DryRun = 0 to commit.

 NOTES ON ORDERING
   Re-pointing a foreign key column at an already-existing parent row is legal
   at any point, so FK ordering does not constrain these UPDATEs the way it
   would constrain INSERTs or DELETEs. What *does* constrain ordering are the
   uniqueness rules -- dbo.Subscriptions' unique index on UserId, and the
   one-row-per-(UserId, UserSettingTypeId) invariant dbo.UserSettings relies on.
   Those collisions are resolved in Step 3, before any UPDATE runs. The
   remaining statements are ordered parent-to-child anyway so the printed report
   reads in a sensible order.

 CONCURRENCY
   Run this while the SOURCE account is idle. The transaction runs at the
   ambient isolation level; Step 8 re-checks every column and rolls the whole
   thing back if a concurrent write re-created a reference to the source user,
   but a write landing between Step 8 and COMMIT would still be stranded on the
   old account. Raising this to SERIALIZABLE is not worth it -- the range locks
   would cover all of dbo.Prints and dbo.Filaments and block live traffic.

 SCHEMA DRIFT
   Step 2 fails the script if dbo.Users has gained a referencing foreign key
   that this script does not know about. Add the new table to the script (and
   to @CoveredColumns) rather than bypassing the check.
================================================================================
*/

SET NOCOUNT ON;
SET XACT_ABORT ON;

-- Required, not cosmetic. Several tables in this schema carry filtered indexes
-- (PrinterFilament, PrintImages, the unique index on Users.OAuthUserId), and
-- SQL Server refuses DML that has to maintain a filtered index unless
-- QUOTED_IDENTIFIER is ON. SSMS connects with it ON; sqlcmd connects with it
-- OFF, so running this script through sqlcmd without this line fails with
-- "DELETE failed because the following SET options have incorrect settings".
SET QUOTED_IDENTIFIER ON;

-------------------------------------------------------------------------------
-- Parameters
-------------------------------------------------------------------------------
DECLARE @SourceUserId       bigint = 0;   -- <<< the OLD account (data moves FROM here)
DECLARE @TargetUserId       bigint = 0;   -- <<< the NEW account (data moves TO here)

DECLARE @DryRun             bit    = 1;   -- 1 = roll back at the end and just report

-- API keys issued under the old account keep working, but authenticate as the
-- new user. Set to 0 to leave them on the old account instead -- note that this
-- leaves live credentials attached to the abandoned account; they can no longer
-- reach the transferred data, but they should be revoked separately.
DECLARE @TransferApiKeys    bit    = 1;

-- Stripe billing is keyed off the Subscriptions row. Transferring it only makes
-- sense when the TARGET has no subscription of its own; the unique index on
-- Subscriptions.UserId makes that a hard requirement. Leave at 0 and handle
-- billing in Stripe unless you know the target is unsubscribed.
DECLARE @TransferSubscription bit  = 0;

-- Notifications the source user sent to the target (or vice versa) collapse
-- into self-notifications once both identities merge. 1 deletes them.
DECLARE @DeleteSelfNotifications bit = 1;

-------------------------------------------------------------------------------
-- Report accumulator
-------------------------------------------------------------------------------
DECLARE @Report TABLE
(
    Seq         int IDENTITY(1,1),
    TableName   sysname,
    ColumnName  sysname,
    Action      varchar(20),
    RowsAffected int
);

DECLARE @n int;

BEGIN TRY
BEGIN TRANSACTION;

-------------------------------------------------------------------------------
-- Step 1: validate the two accounts
-------------------------------------------------------------------------------
IF @SourceUserId IS NULL OR @TargetUserId IS NULL OR @SourceUserId = 0 OR @TargetUserId = 0
    THROW 50001, 'Set @SourceUserId and @TargetUserId before running this script.', 1;

IF @SourceUserId = @TargetUserId
    THROW 50002, 'Source and target user are the same. Nothing to do.', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.Users WHERE Id = @SourceUserId)
    THROW 50003, 'Source user does not exist in dbo.Users.', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.Users WHERE Id = @TargetUserId)
    THROW 50004, 'Target user does not exist in dbo.Users.', 1;

IF EXISTS (SELECT 1 FROM dbo.Users WHERE Id = @TargetUserId AND DeactivationDateTime IS NOT NULL)
    THROW 50005, 'Target user is pending deactivation. Clear DeactivationDateTime first, or the transferred data will be deleted by the cleanup job.', 1;

PRINT CONCAT('Transferring data from user ', @SourceUserId, ' to user ', @TargetUserId, '.');

-------------------------------------------------------------------------------
-- Step 2: schema-drift guard
--
-- Every foreign key column that points at dbo.Users must be handled below.
-- If a migration adds one, this fails loudly instead of silently orphaning it.
-------------------------------------------------------------------------------
DECLARE @CoveredColumns TABLE (TableName sysname, ColumnName sysname);

INSERT INTO @CoveredColumns (TableName, ColumnName) VALUES
    ('Comments',            'CreatedById'),
    ('Comments',            'UpdatedById'),
    ('Feedback',            'CreatedById'),
    ('Feedback',            'UpdatedById'),
    ('FilamentAdjustments', 'CreatedById'),
    ('FilamentAdjustments', 'UpdatedById'),
    ('Filaments',           'CreatedById'),
    ('Filaments',           'UpdatedById'),
    ('Files',               'CreatedById'),
    ('Files',               'UpdatedById'),
    ('Notifications',       'UserId'),
    ('Notifications',       'TriggeredByUserId'),
    ('PrintAttachments',    'CreatedById'),
    ('PrintAttachments',    'UpdatedById'),
    ('PrintComments',       'CreatedById'),
    ('PrintComments',       'UpdatedById'),
    ('PrintImages',         'CreatedById'),
    ('PrintImages',         'UpdatedById'),
    ('PrinterMaintenance',  'CreatedById'),
    ('PrinterMaintenance',  'UpdatedById'),
    ('Printers',            'UserId'),
    ('Prints',              'CreatedById'),
    ('Prints',              'UpdatedById'),
    ('ProjectImages',       'CreatedById'),
    ('ProjectImages',       'UpdatedById'),
    ('Projects',            'CreatedById'),
    ('Projects',            'UpdatedById'),
    ('Subscriptions',       'UserId'),
    ('Subscriptions',       'CreatedById'),
    ('Subscriptions',       'UpdatedById'),
    ('UserApiKeys',         'UserId'),
    ('UserApiKeys',         'CreatedById'),
    ('UserApiKeys',         'UpdatedById'),
    ('UserSettings',        'UserId'),
    ('UserSettings',        'CreatedById'),
    ('UserSettings',        'UpdatedById');

-- CuraSettings.UserId and McpIdempotencyRecords.UserId reference users without a
-- declared foreign key, so they never show up in sys.foreign_keys. They are
-- handled explicitly below; they are listed here only for documentation.

DECLARE @Uncovered nvarchar(max);

SELECT @Uncovered = STRING_AGG(CONCAT(t.name, '.', c.name), ', ')
FROM sys.foreign_keys fk
JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
JOIN sys.tables  t ON t.object_id  = fk.parent_object_id
JOIN sys.columns c ON c.object_id  = fkc.parent_object_id AND c.column_id = fkc.parent_column_id
JOIN sys.tables  rt ON rt.object_id = fk.referenced_object_id
WHERE rt.name = 'Users'
  AND SCHEMA_NAME(rt.schema_id) = 'dbo'
  AND NOT EXISTS (SELECT 1 FROM @CoveredColumns cc
                  WHERE cc.TableName = t.name AND cc.ColumnName = c.name);

IF @Uncovered IS NOT NULL
BEGIN
    DECLARE @DriftMsg nvarchar(2048) =
        CONCAT('Schema drift: these columns reference dbo.Users but are not handled by this script: ', @Uncovered);
    THROW 50006, @DriftMsg, 1;
END

-------------------------------------------------------------------------------
-- Step 3: resolve uniqueness collisions BEFORE any UPDATE
--
-- 3a. UserSettings has NO database unique index on (UserId, UserSettingTypeId),
--     but the application enforces the invariant anyway: CreateUserSetting in
--     UserSettingsController uses SingleOrDefaultAsync over
--     (UserId, UserSettingTypeId), so a duplicate pair makes any later attempt
--     to create that setting throw. (UpdateUserSetting looks the row up by Id
--     and is unaffected, so the damage is confined to creation.) Merging both
--     users' rows verbatim would leave the account permanently unable to
--     create that setting.
--
--     The target has been actively using the app, so the target's preference
--     wins and the source's duplicate row is dropped. This discards data --
--     the dry run reports the count as 'deleted-dup'; check it before
--     committing if the old account's preferences matter.
-------------------------------------------------------------------------------
--     First, refuse to run if EITHER account already violates the invariant.
--     Deduping source-vs-target collisions does not help if one account is
--     already carrying two rows for the same setting type: both would survive
--     the move and land on the target together. Which of two pre-existing
--     duplicates to keep is a judgement call about the user's real preference,
--     so this stops and hands it back rather than guessing.
IF EXISTS (SELECT 1
           FROM dbo.UserSettings
           WHERE UserId IN (@SourceUserId, @TargetUserId)
           GROUP BY UserId, UserSettingTypeId
           HAVING COUNT(*) > 1)
BEGIN
    SELECT UserId, UserSettingTypeId, COUNT(*) AS DuplicateRows
    FROM dbo.UserSettings
    WHERE UserId IN (@SourceUserId, @TargetUserId)
    GROUP BY UserId, UserSettingTypeId
    HAVING COUNT(*) > 1;

    THROW 50009, 'One of the two accounts already has duplicate UserSettings rows for the same UserSettingTypeId (listed above). Decide which row to keep, delete the other, then re-run.', 1;
END

DELETE us
FROM dbo.UserSettings us
WHERE us.UserId = @SourceUserId
  AND EXISTS (SELECT 1 FROM dbo.UserSettings t
              WHERE t.UserId = @TargetUserId
                AND t.UserSettingTypeId = us.UserSettingTypeId);
SET @n = @@ROWCOUNT;
INSERT INTO @Report VALUES ('UserSettings', 'UserId', 'deleted-dup', @n);

-------------------------------------------------------------------------------
-- 3b. McpIdempotencyRecords is a replay cache for MCP write tools, keyed
--     UNIQUE on (UserId, ToolName, IdempotencyKey). The source user's records
--     are DELETED rather than moved.
--
--     Moving them is the tempting option and it is wrong. The lookup
--     (FindIdempotentPrint / FindIdempotentPrinter / ...) matches on
--     (UserId, ToolName, IdempotencyKey) alone, so if both accounts ever used
--     the same key for the same tool, merging the two identities would make a
--     retry replay resolve to whichever row survived -- handing the caller a
--     CreatedPrintId for an entity it never asked for. Deleting sidesteps that
--     entirely: a missing record makes the lookup return null and the tool
--     simply creates fresh, which is the correct behaviour for an abandoned
--     account whose tokens nobody is retrying with.
-------------------------------------------------------------------------------
DELETE FROM dbo.McpIdempotencyRecords WHERE UserId = @SourceUserId;
SET @n = @@ROWCOUNT;
INSERT INTO @Report VALUES ('McpIdempotencyRecords', 'UserId', 'deleted-cache', @n);

-------------------------------------------------------------------------------
-- 3c. Subscriptions has a UNIQUE index on UserId. Refuse to transfer into a
--     target that already has one.
-------------------------------------------------------------------------------
IF @TransferSubscription = 1
   AND EXISTS (SELECT 1 FROM dbo.Subscriptions WHERE UserId = @SourceUserId)
   AND EXISTS (SELECT 1 FROM dbo.Subscriptions WHERE UserId = @TargetUserId)
    THROW 50007, 'Both users have a Subscriptions row and the unique index on UserId forbids merging them. Resolve billing in Stripe first, delete the redundant row, then re-run.', 1;

-------------------------------------------------------------------------------
-- Step 4: transfer ownership, parent tables first
-------------------------------------------------------------------------------

-- Files: referenced by PrintImages, PrintAttachments and ProjectImages.
UPDATE dbo.Files SET CreatedById = @TargetUserId WHERE CreatedById = @SourceUserId;
SET @n = @@ROWCOUNT; INSERT INTO @Report VALUES ('Files', 'CreatedById', 'moved', @n);
UPDATE dbo.Files SET UpdatedById = @TargetUserId WHERE UpdatedById = @SourceUserId;
SET @n = @@ROWCOUNT; INSERT INTO @Report VALUES ('Files', 'UpdatedById', 'moved', @n);

-- Printers: owner column, plus every print that points at them.
UPDATE dbo.Printers SET UserId = @TargetUserId WHERE UserId = @SourceUserId;
SET @n = @@ROWCOUNT; INSERT INTO @Report VALUES ('Printers', 'UserId', 'moved', @n);

UPDATE dbo.PrinterMaintenance SET CreatedById = @TargetUserId WHERE CreatedById = @SourceUserId;
SET @n = @@ROWCOUNT; INSERT INTO @Report VALUES ('PrinterMaintenance', 'CreatedById', 'moved', @n);
UPDATE dbo.PrinterMaintenance SET UpdatedById = @TargetUserId WHERE UpdatedById = @SourceUserId;
SET @n = @@ROWCOUNT; INSERT INTO @Report VALUES ('PrinterMaintenance', 'UpdatedById', 'moved', @n);

-- Filaments and their adjustment ledger.
UPDATE dbo.Filaments SET CreatedById = @TargetUserId WHERE CreatedById = @SourceUserId;
SET @n = @@ROWCOUNT; INSERT INTO @Report VALUES ('Filaments', 'CreatedById', 'moved', @n);
UPDATE dbo.Filaments SET UpdatedById = @TargetUserId WHERE UpdatedById = @SourceUserId;
SET @n = @@ROWCOUNT; INSERT INTO @Report VALUES ('Filaments', 'UpdatedById', 'moved', @n);

UPDATE dbo.FilamentAdjustments SET CreatedById = @TargetUserId WHERE CreatedById = @SourceUserId;
SET @n = @@ROWCOUNT; INSERT INTO @Report VALUES ('FilamentAdjustments', 'CreatedById', 'moved', @n);
UPDATE dbo.FilamentAdjustments SET UpdatedById = @TargetUserId WHERE UpdatedById = @SourceUserId;
SET @n = @@ROWCOUNT; INSERT INTO @Report VALUES ('FilamentAdjustments', 'UpdatedById', 'moved', @n);

-- Projects and their images.
UPDATE dbo.Projects SET CreatedById = @TargetUserId WHERE CreatedById = @SourceUserId;
SET @n = @@ROWCOUNT; INSERT INTO @Report VALUES ('Projects', 'CreatedById', 'moved', @n);
UPDATE dbo.Projects SET UpdatedById = @TargetUserId WHERE UpdatedById = @SourceUserId;
SET @n = @@ROWCOUNT; INSERT INTO @Report VALUES ('Projects', 'UpdatedById', 'moved', @n);

UPDATE dbo.ProjectImages SET CreatedById = @TargetUserId WHERE CreatedById = @SourceUserId;
SET @n = @@ROWCOUNT; INSERT INTO @Report VALUES ('ProjectImages', 'CreatedById', 'moved', @n);
UPDATE dbo.ProjectImages SET UpdatedById = @TargetUserId WHERE UpdatedById = @SourceUserId;
SET @n = @@ROWCOUNT; INSERT INTO @Report VALUES ('ProjectImages', 'UpdatedById', 'moved', @n);

-- Prints and everything hanging off them.
UPDATE dbo.Prints SET CreatedById = @TargetUserId WHERE CreatedById = @SourceUserId;
SET @n = @@ROWCOUNT; INSERT INTO @Report VALUES ('Prints', 'CreatedById', 'moved', @n);
UPDATE dbo.Prints SET UpdatedById = @TargetUserId WHERE UpdatedById = @SourceUserId;
SET @n = @@ROWCOUNT; INSERT INTO @Report VALUES ('Prints', 'UpdatedById', 'moved', @n);

UPDATE dbo.PrintImages SET CreatedById = @TargetUserId WHERE CreatedById = @SourceUserId;
SET @n = @@ROWCOUNT; INSERT INTO @Report VALUES ('PrintImages', 'CreatedById', 'moved', @n);
UPDATE dbo.PrintImages SET UpdatedById = @TargetUserId WHERE UpdatedById = @SourceUserId;
SET @n = @@ROWCOUNT; INSERT INTO @Report VALUES ('PrintImages', 'UpdatedById', 'moved', @n);

UPDATE dbo.PrintAttachments SET CreatedById = @TargetUserId WHERE CreatedById = @SourceUserId;
SET @n = @@ROWCOUNT; INSERT INTO @Report VALUES ('PrintAttachments', 'CreatedById', 'moved', @n);
UPDATE dbo.PrintAttachments SET UpdatedById = @TargetUserId WHERE UpdatedById = @SourceUserId;
SET @n = @@ROWCOUNT; INSERT INTO @Report VALUES ('PrintAttachments', 'UpdatedById', 'moved', @n);

-- Comments authored by the source user, on their own prints or anyone else's.
UPDATE dbo.Comments SET CreatedById = @TargetUserId WHERE CreatedById = @SourceUserId;
SET @n = @@ROWCOUNT; INSERT INTO @Report VALUES ('Comments', 'CreatedById', 'moved', @n);
UPDATE dbo.Comments SET UpdatedById = @TargetUserId WHERE UpdatedById = @SourceUserId;
SET @n = @@ROWCOUNT; INSERT INTO @Report VALUES ('Comments', 'UpdatedById', 'moved', @n);

UPDATE dbo.PrintComments SET CreatedById = @TargetUserId WHERE CreatedById = @SourceUserId;
SET @n = @@ROWCOUNT; INSERT INTO @Report VALUES ('PrintComments', 'CreatedById', 'moved', @n);
UPDATE dbo.PrintComments SET UpdatedById = @TargetUserId WHERE UpdatedById = @SourceUserId;
SET @n = @@ROWCOUNT; INSERT INTO @Report VALUES ('PrintComments', 'UpdatedById', 'moved', @n);

-- Feedback submitted by the source user.
UPDATE dbo.Feedback SET CreatedById = @TargetUserId WHERE CreatedById = @SourceUserId;
SET @n = @@ROWCOUNT; INSERT INTO @Report VALUES ('Feedback', 'CreatedById', 'moved', @n);
UPDATE dbo.Feedback SET UpdatedById = @TargetUserId WHERE UpdatedById = @SourceUserId;
SET @n = @@ROWCOUNT; INSERT INTO @Report VALUES ('Feedback', 'UpdatedById', 'moved', @n);

-- User settings. Step 3a already removed rows that would duplicate a target
-- preference, so what is left is safe to move. Rows with UserId NULL are global
-- defaults and are deliberately untouched -- but their audit columns still move.
UPDATE dbo.UserSettings SET UserId = @TargetUserId WHERE UserId = @SourceUserId;
SET @n = @@ROWCOUNT; INSERT INTO @Report VALUES ('UserSettings', 'UserId', 'moved', @n);
UPDATE dbo.UserSettings SET CreatedById = @TargetUserId WHERE CreatedById = @SourceUserId;
SET @n = @@ROWCOUNT; INSERT INTO @Report VALUES ('UserSettings', 'CreatedById', 'moved', @n);
UPDATE dbo.UserSettings SET UpdatedById = @TargetUserId WHERE UpdatedById = @SourceUserId;
SET @n = @@ROWCOUNT; INSERT INTO @Report VALUES ('UserSettings', 'UpdatedById', 'moved', @n);

-- Cura plugin settings blobs (no declared FK, nullable UserId).
UPDATE dbo.CuraSettings SET UserId = @TargetUserId WHERE UserId = @SourceUserId;
SET @n = @@ROWCOUNT; INSERT INTO @Report VALUES ('CuraSettings', 'UserId', 'moved', @n);

-- McpIdempotencyRecords is intentionally not moved -- Step 3b cleared it.

-------------------------------------------------------------------------------
-- Step 5: notifications
--
-- Both the recipient (UserId) and the actor (TriggeredByUserId, nullable) move.
-- Once merged, a notification the source sent to the target becomes a
-- self-notification, which the app never generates on its own.
-------------------------------------------------------------------------------
IF @DeleteSelfNotifications = 1
BEGIN
    DELETE FROM dbo.Notifications
    WHERE (UserId = @SourceUserId AND TriggeredByUserId = @TargetUserId)
       OR (UserId = @TargetUserId AND TriggeredByUserId = @SourceUserId)
       OR (UserId = @SourceUserId AND TriggeredByUserId = @SourceUserId);
    SET @n = @@ROWCOUNT;
    INSERT INTO @Report VALUES ('Notifications', 'UserId', 'deleted-self', @n);
END

UPDATE dbo.Notifications SET UserId = @TargetUserId WHERE UserId = @SourceUserId;
SET @n = @@ROWCOUNT; INSERT INTO @Report VALUES ('Notifications', 'UserId', 'moved', @n);
UPDATE dbo.Notifications SET TriggeredByUserId = @TargetUserId WHERE TriggeredByUserId = @SourceUserId;
SET @n = @@ROWCOUNT; INSERT INTO @Report VALUES ('Notifications', 'TriggeredByUserId', 'moved', @n);

-------------------------------------------------------------------------------
-- Step 6: optional -- API keys
-------------------------------------------------------------------------------
IF @TransferApiKeys = 1
BEGIN
    UPDATE dbo.UserApiKeys SET UserId = @TargetUserId WHERE UserId = @SourceUserId;
    SET @n = @@ROWCOUNT; INSERT INTO @Report VALUES ('UserApiKeys', 'UserId', 'moved', @n);
    UPDATE dbo.UserApiKeys SET CreatedById = @TargetUserId WHERE CreatedById = @SourceUserId;
    SET @n = @@ROWCOUNT; INSERT INTO @Report VALUES ('UserApiKeys', 'CreatedById', 'moved', @n);
    UPDATE dbo.UserApiKeys SET UpdatedById = @TargetUserId WHERE UpdatedById = @SourceUserId;
    SET @n = @@ROWCOUNT; INSERT INTO @Report VALUES ('UserApiKeys', 'UpdatedById', 'moved', @n);
END
ELSE
BEGIN
    INSERT INTO @Report VALUES ('UserApiKeys', 'UserId', 'SKIPPED', 0);
END

-------------------------------------------------------------------------------
-- Step 7: optional -- subscription
-------------------------------------------------------------------------------
IF @TransferSubscription = 1
BEGIN
    UPDATE dbo.Subscriptions SET UserId = @TargetUserId WHERE UserId = @SourceUserId;
    SET @n = @@ROWCOUNT; INSERT INTO @Report VALUES ('Subscriptions', 'UserId', 'moved', @n);
    UPDATE dbo.Subscriptions SET CreatedById = @TargetUserId WHERE CreatedById = @SourceUserId;
    SET @n = @@ROWCOUNT; INSERT INTO @Report VALUES ('Subscriptions', 'CreatedById', 'moved', @n);
    UPDATE dbo.Subscriptions SET UpdatedById = @TargetUserId WHERE UpdatedById = @SourceUserId;
    SET @n = @@ROWCOUNT; INSERT INTO @Report VALUES ('Subscriptions', 'UpdatedById', 'moved', @n);
END
ELSE
BEGIN
    INSERT INTO @Report VALUES ('Subscriptions', 'UserId', 'SKIPPED', 0);
END

-------------------------------------------------------------------------------
-- Step 8: verify nothing that should have moved is still pointing at the source
-------------------------------------------------------------------------------
DECLARE @Leftover TABLE (TableName sysname, ColumnName sysname, RowsLeft int);

INSERT INTO @Leftover
SELECT 'Comments',            'CreatedById',       COUNT(*) FROM dbo.Comments            WHERE CreatedById       = @SourceUserId
UNION ALL SELECT 'Comments',            'UpdatedById',       COUNT(*) FROM dbo.Comments            WHERE UpdatedById       = @SourceUserId
UNION ALL SELECT 'CuraSettings',        'UserId',            COUNT(*) FROM dbo.CuraSettings        WHERE UserId            = @SourceUserId
UNION ALL SELECT 'Feedback',            'CreatedById',       COUNT(*) FROM dbo.Feedback            WHERE CreatedById       = @SourceUserId
UNION ALL SELECT 'Feedback',            'UpdatedById',       COUNT(*) FROM dbo.Feedback            WHERE UpdatedById       = @SourceUserId
UNION ALL SELECT 'FilamentAdjustments', 'CreatedById',       COUNT(*) FROM dbo.FilamentAdjustments WHERE CreatedById       = @SourceUserId
UNION ALL SELECT 'FilamentAdjustments', 'UpdatedById',       COUNT(*) FROM dbo.FilamentAdjustments WHERE UpdatedById       = @SourceUserId
UNION ALL SELECT 'Filaments',           'CreatedById',       COUNT(*) FROM dbo.Filaments           WHERE CreatedById       = @SourceUserId
UNION ALL SELECT 'Filaments',           'UpdatedById',       COUNT(*) FROM dbo.Filaments           WHERE UpdatedById       = @SourceUserId
UNION ALL SELECT 'Files',               'CreatedById',       COUNT(*) FROM dbo.Files               WHERE CreatedById       = @SourceUserId
UNION ALL SELECT 'Files',               'UpdatedById',       COUNT(*) FROM dbo.Files               WHERE UpdatedById       = @SourceUserId
UNION ALL SELECT 'McpIdempotencyRecords','UserId',           COUNT(*) FROM dbo.McpIdempotencyRecords WHERE UserId          = @SourceUserId
UNION ALL SELECT 'Notifications',       'UserId',            COUNT(*) FROM dbo.Notifications       WHERE UserId            = @SourceUserId
UNION ALL SELECT 'Notifications',       'TriggeredByUserId', COUNT(*) FROM dbo.Notifications       WHERE TriggeredByUserId = @SourceUserId
UNION ALL SELECT 'PrintAttachments',    'CreatedById',       COUNT(*) FROM dbo.PrintAttachments    WHERE CreatedById       = @SourceUserId
UNION ALL SELECT 'PrintAttachments',    'UpdatedById',       COUNT(*) FROM dbo.PrintAttachments    WHERE UpdatedById       = @SourceUserId
UNION ALL SELECT 'PrintComments',       'CreatedById',       COUNT(*) FROM dbo.PrintComments       WHERE CreatedById       = @SourceUserId
UNION ALL SELECT 'PrintComments',       'UpdatedById',       COUNT(*) FROM dbo.PrintComments       WHERE UpdatedById       = @SourceUserId
UNION ALL SELECT 'PrintImages',         'CreatedById',       COUNT(*) FROM dbo.PrintImages         WHERE CreatedById       = @SourceUserId
UNION ALL SELECT 'PrintImages',         'UpdatedById',       COUNT(*) FROM dbo.PrintImages         WHERE UpdatedById       = @SourceUserId
UNION ALL SELECT 'PrinterMaintenance',  'CreatedById',       COUNT(*) FROM dbo.PrinterMaintenance  WHERE CreatedById       = @SourceUserId
UNION ALL SELECT 'PrinterMaintenance',  'UpdatedById',       COUNT(*) FROM dbo.PrinterMaintenance  WHERE UpdatedById       = @SourceUserId
UNION ALL SELECT 'Printers',            'UserId',            COUNT(*) FROM dbo.Printers            WHERE UserId            = @SourceUserId
UNION ALL SELECT 'Prints',              'CreatedById',       COUNT(*) FROM dbo.Prints              WHERE CreatedById       = @SourceUserId
UNION ALL SELECT 'Prints',              'UpdatedById',       COUNT(*) FROM dbo.Prints              WHERE UpdatedById       = @SourceUserId
UNION ALL SELECT 'ProjectImages',       'CreatedById',       COUNT(*) FROM dbo.ProjectImages       WHERE CreatedById       = @SourceUserId
UNION ALL SELECT 'ProjectImages',       'UpdatedById',       COUNT(*) FROM dbo.ProjectImages       WHERE UpdatedById       = @SourceUserId
UNION ALL SELECT 'Projects',            'CreatedById',       COUNT(*) FROM dbo.Projects            WHERE CreatedById       = @SourceUserId
UNION ALL SELECT 'Projects',            'UpdatedById',       COUNT(*) FROM dbo.Projects            WHERE UpdatedById       = @SourceUserId
UNION ALL SELECT 'UserSettings',        'UserId',            COUNT(*) FROM dbo.UserSettings        WHERE UserId            = @SourceUserId
UNION ALL SELECT 'UserSettings',        'CreatedById',       COUNT(*) FROM dbo.UserSettings        WHERE CreatedById       = @SourceUserId
UNION ALL SELECT 'UserSettings',        'UpdatedById',       COUNT(*) FROM dbo.UserSettings        WHERE UpdatedById       = @SourceUserId;

-- Only checked when the corresponding toggle asked for a transfer.
IF @TransferApiKeys = 1
    INSERT INTO @Leftover
    SELECT 'UserApiKeys', 'UserId',      COUNT(*) FROM dbo.UserApiKeys WHERE UserId      = @SourceUserId
    UNION ALL SELECT 'UserApiKeys', 'CreatedById', COUNT(*) FROM dbo.UserApiKeys WHERE CreatedById = @SourceUserId
    UNION ALL SELECT 'UserApiKeys', 'UpdatedById', COUNT(*) FROM dbo.UserApiKeys WHERE UpdatedById = @SourceUserId;

IF @TransferSubscription = 1
    INSERT INTO @Leftover
    SELECT 'Subscriptions', 'UserId',      COUNT(*) FROM dbo.Subscriptions WHERE UserId      = @SourceUserId
    UNION ALL SELECT 'Subscriptions', 'CreatedById', COUNT(*) FROM dbo.Subscriptions WHERE CreatedById = @SourceUserId
    UNION ALL SELECT 'Subscriptions', 'UpdatedById', COUNT(*) FROM dbo.Subscriptions WHERE UpdatedById = @SourceUserId;

IF EXISTS (SELECT 1 FROM @Leftover WHERE RowsLeft > 0)
BEGIN
    SELECT TableName, ColumnName, RowsLeft
    FROM @Leftover
    WHERE RowsLeft > 0
    ORDER BY TableName, ColumnName;

    THROW 50008, 'Verification failed: rows still reference the source user after the transfer. Transaction rolled back.', 1;
END

-------------------------------------------------------------------------------
-- Step 9: report and commit (or roll back for a dry run)
-------------------------------------------------------------------------------
SELECT Seq, TableName, ColumnName, Action, RowsAffected
FROM @Report
ORDER BY Seq;

-- Broken out by action rather than one grand total. A single row updated on
-- both CreatedById and UpdatedById counts twice within 'moved', so this is a
-- count of column updates, not of distinct rows -- and summing it together
-- with the deletions would be meaningless.
SELECT Action, SUM(RowsAffected) AS RowsAffected
FROM @Report
GROUP BY Action
ORDER BY Action;

IF @DryRun = 1
BEGIN
    ROLLBACK TRANSACTION;
    PRINT 'DRY RUN -- transaction rolled back. Set @DryRun = 0 to apply.';
END
ELSE
BEGIN
    COMMIT TRANSACTION;
    PRINT 'Transfer committed.';
    PRINT 'IMPORTANT: restart the API (or wait out the cache TTL) so the in-memory';
    PRINT 'print/printer summary caches for both users are rebuilt.';
END

END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK TRANSACTION;

    PRINT 'Transfer failed -- no changes were made.';
    THROW;
END CATCH
