USE PrintLogTransferTest;
GO
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;

-- Wipe, child-first, so the seed is re-runnable.
DELETE FROM dbo.Notifications;
DELETE FROM dbo.PrintComments;
DELETE FROM dbo.Comments;
DELETE FROM dbo.PrintFilament;
DELETE FROM dbo.PrinterFilament;
DELETE FROM dbo.PrintAttachments;
DELETE FROM dbo.PrintImages;
DELETE FROM dbo.Prints;
DELETE FROM dbo.ProjectImages;
DELETE FROM dbo.Projects;
DELETE FROM dbo.FilamentAdjustments;
DELETE FROM dbo.Filaments;
DELETE FROM dbo.PrinterMaintenance;
DELETE FROM dbo.Printers;
DELETE FROM dbo.Files;
DELETE FROM dbo.Feedback;
DELETE FROM dbo.UserSettings;
DELETE FROM dbo.CuraSettings;
DELETE FROM dbo.McpIdempotencyRecords;
DELETE FROM dbo.UserApiKeys;
DELETE FROM dbo.Subscriptions;
DELETE FROM dbo.Users;

DECLARE @now datetime2 = SYSUTCDATETIME();
DECLARE @nowoff datetimeoffset = SYSDATETIMEOFFSET();

-- Three accounts: OLD (source), NEW (target), BYSTANDER (must not be touched).
INSERT INTO dbo.Users (DisplayName, OAuthUserId, ViewStatus, Bio)
VALUES ('Old Account', 'auth0|old', 0, 'old'),
       ('New Account', 'auth0|new', 0, 'new'),
       ('Bystander',   'auth0|by',  0, 'by');

DECLARE @Src bigint = (SELECT Id FROM dbo.Users WHERE OAuthUserId = 'auth0|old');
DECLARE @Tgt bigint = (SELECT Id FROM dbo.Users WHERE OAuthUserId = 'auth0|new');
DECLARE @Oth bigint = (SELECT Id FROM dbo.Users WHERE OAuthUserId = 'auth0|by');

-------------------------------------------------------------------------------
-- Printers
-------------------------------------------------------------------------------
INSERT INTO dbo.Printers (Name, UserId, IsActive, CategoryNickname)
VALUES ('Old Ender', @Src, 1, 'FFF'),
       ('New Bambu', @Tgt, 1, 'FFF'),
       ('By Printer', @Oth, 1, 'FFF');

DECLARE @SrcPrinter bigint = (SELECT Id FROM dbo.Printers WHERE Name = 'Old Ender');
DECLARE @OthPrinter bigint = (SELECT Id FROM dbo.Printers WHERE Name = 'By Printer');

INSERT INTO dbo.PrinterMaintenance (Id, Category, CreatedById, CreatedDate, Date, Description, Done, PrinterId, UpdatedById, UpdatedDate)
VALUES (NEWID(), 'nozzle', @Src, @now, @nowoff, 'changed nozzle', 1, @SrcPrinter, @Src, @now);

-------------------------------------------------------------------------------
-- Filaments
-------------------------------------------------------------------------------
DECLARE @SrcFil uniqueidentifier = NEWID();
INSERT INTO dbo.Filaments (Id, DisplayName, CreatedById, CreatedDate, UpdatedById, UpdatedDate,
                           IsActive, IsFavorite, MaterialCategoryNickname, MaterialDensityGramPerCubicCm, Source)
VALUES (@SrcFil, 'Old PLA', @Src, @now, @Src, @now, 1, 0, 'filament', 1.24, 0);

INSERT INTO dbo.FilamentAdjustments (Id, AmountMg, CreatedById, CreatedDate, FilamentId, Source, UpdatedById, UpdatedDate)
VALUES (NEWID(), 1000, @Src, @now, @SrcFil, 0, @Src, @now);

INSERT INTO dbo.PrinterFilament (Id, FilamentId, LoadedDateTime, PrinterId)
VALUES (NEWID(), @SrcFil, @nowoff, @SrcPrinter);

-------------------------------------------------------------------------------
-- Files / Projects / Prints
-------------------------------------------------------------------------------
DECLARE @F1 uniqueidentifier = NEWID(), @F2 uniqueidentifier = NEWID(), @F3 uniqueidentifier = NEWID();
INSERT INTO dbo.Files (Id, CreatedById, CreatedDate, UpdatedById, UpdatedDate, Path, Size)
VALUES (@F1, @Src, @now, @Src, @now, 'c/1.png', 10),
       (@F2, @Src, @now, @Src, @now, 'c/2.png', 20),
       (@F3, @Src, @now, @Src, @now, 'c/3.png', 30);

DECLARE @SrcProj uniqueidentifier = NEWID();
INSERT INTO dbo.Projects (Id, CreatedById, CreatedDate, UpdatedById, UpdatedDate, Name, Status, ViewStatus)
VALUES (@SrcProj, @Src, @now, @Src, @now, 'Old Project', 0, 0);

INSERT INTO dbo.ProjectImages (CreatedById, CreatedDate, DisplayOrder, FileId, IsDefault, ProjectId, UpdatedById, UpdatedDate)
VALUES (@Src, @now, 0, @F3, 1, @SrcProj, @Src, @now);

INSERT INTO dbo.Prints (AllowComments, AllowFileDownloads, CreatedById, CreatedDate, UpdatedById, UpdatedDate,
                        PrinterId, ProjectId, Status, ViewStatus, Title)
VALUES (1, 1, @Src, @now, @Src, @now, @SrcPrinter, @SrcProj, 0, 0, 'Old Print');

DECLARE @SrcPrint bigint = (SELECT Id FROM dbo.Prints WHERE Title = 'Old Print');

INSERT INTO dbo.PrintImages (CreatedById, CreatedDate, DisplayOrder, FileId, IsDefault, PrintId, UpdatedById, UpdatedDate)
VALUES (@Src, @now, 0, @F1, 1, @SrcPrint, @Src, @now);

INSERT INTO dbo.PrintAttachments (ContentType, CreatedById, CreatedDate, DisplayOrder, FileId, OriginalFileName, PrintId, UpdatedById, UpdatedDate)
VALUES ('model/stl', @Src, @now, 0, @F2, 'part.stl', @SrcPrint, @Src, @now);

INSERT INTO dbo.PrintFilament (Id, PrintId, FilamentId, EstimatedSource, Source, AmountMg)
VALUES (NEWID(), @SrcPrint, @SrcFil, 0, 0, 500);

-- A print owned by the BYSTANDER that the source user commented on.
INSERT INTO dbo.Prints (AllowComments, AllowFileDownloads, CreatedById, CreatedDate, UpdatedById, UpdatedDate,
                        PrinterId, Status, ViewStatus, Title)
VALUES (1, 1, @Oth, @now, @Oth, @now, @OthPrinter, 0, 2, 'Bystander Print');

DECLARE @OthPrint bigint = (SELECT Id FROM dbo.Prints WHERE Title = 'Bystander Print');

INSERT INTO dbo.Comments (Body, CreatedById, CreatedDate, UpdatedById, UpdatedDate)
VALUES ('nice print', @Src, @now, @Src, @now);
DECLARE @SrcComment bigint = SCOPE_IDENTITY();

INSERT INTO dbo.PrintComments (CommentId, CreatedById, CreatedDate, PrintId, UpdatedById, UpdatedDate)
VALUES (@SrcComment, @Src, @now, @OthPrint, @Src, @now);

-------------------------------------------------------------------------------
-- Feedback, settings, cura
-------------------------------------------------------------------------------
INSERT INTO dbo.Feedback (Id, CreatedById, CreatedDate, Email, Note, Type, UpdatedById, UpdatedDate)
VALUES (NEWID(), @Src, @now, 'old@x.com', 'a bug', 0, @Src, @now);

-- UserSettingTypeId 5 exists for BOTH users -> collision, target must win.
-- UserSettingTypeId 7 exists only on the source -> must move.
INSERT INTO dbo.UserSettings (CreatedById, CreatedDate, UpdatedById, UpdatedDate, UserId, UserSettingTypeId, Value)
VALUES (@Src, @now, @Src, @now, @Src, 5, 'SRC-CURRENCY'),
       (@Tgt, @now, @Tgt, @now, @Tgt, 5, 'TGT-CURRENCY'),
       (@Src, @now, @Src, @now, @Src, 7, 'SRC-DIAMETER');

-- A global setting (UserId NULL) audited by the source user: audit cols must move,
-- the row itself must stay global.
INSERT INTO dbo.UserSettings (CreatedById, CreatedDate, UpdatedById, UpdatedDate, UserId, UserSettingTypeId, Value)
VALUES (@Src, @now, @Src, @now, NULL, 8, 'GLOBAL-DEFAULT');

INSERT INTO dbo.CuraSettings (Id, CreatedDate, CuraVersion, PluginVersion, UserId, Settings)
VALUES (NEWID(), @nowoff, '5.0', '1.0', @Src, '{}');

-------------------------------------------------------------------------------
-- MCP idempotency: one colliding key, one unique to the source.
-------------------------------------------------------------------------------
INSERT INTO dbo.McpIdempotencyRecords (CreatedAt, IdempotencyKey, RequestFingerprint, ToolName, UserId, CreatedPrintId)
VALUES (@nowoff, 'KEY-SHARED', 'fp-src', 'create_print', @Src, @SrcPrint),
       (@nowoff, 'KEY-SHARED', 'fp-tgt', 'create_print', @Tgt, NULL),
       (@nowoff, 'KEY-SRCONLY', 'fp-src2', 'create_printer', @Src, NULL);

-------------------------------------------------------------------------------
-- Notifications: recipient-side, actor-side, and a cross-pair that collapses
-- into a self-notification after the merge.
-------------------------------------------------------------------------------
INSERT INTO dbo.Notifications (Id, CreatedDate, IsRead, Title, Type, UserId, TriggeredByUserId)
VALUES (NEWID(), @now, 0, 'to source from bystander', 0, @Src, @Oth),
       (NEWID(), @now, 0, 'to bystander from source', 0, @Oth, @Src),
       (NEWID(), @now, 0, 'to source from target',    0, @Src, @Tgt),
       (NEWID(), @now, 0, 'to target from source',    0, @Tgt, @Src),
       (NEWID(), @now, 0, 'to source, no actor',      0, @Src, NULL);

-------------------------------------------------------------------------------
-- API keys and a subscription on the SOURCE only.
-------------------------------------------------------------------------------
INSERT INTO dbo.UserApiKeys (Id, CreatedById, CreatedDate, Description, HashAlgorithm, HashedKey, IsDeleted, UpdatedById, UpdatedDate, UserId)
VALUES (NEWID(), @Src, @now, 'old key', 'SHA256', 'hash-old', 0, @Src, @now, @Src);

INSERT INTO dbo.Subscriptions (CancelAtPeriodEnd, CreatedById, CreatedDate, [Plan], Status, StripeCustomerId, StripeSubscriptionId, UpdatedById, UpdatedDate, UserId)
VALUES (0, @Src, @now, 1, 1, 'cus_old', 'sub_old', @Src, @now, @Src);

SELECT @Src AS SourceUserId, @Tgt AS TargetUserId, @Oth AS BystanderUserId;
