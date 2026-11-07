using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintLogApi.Migrations
{
    /// <summary>
    /// Indexes for the queries behind the production DTU spikes (api#309): the public print
    /// lists, the unread notification counts and the email dispatcher's sent-today count.
    /// </summary>
    /// <remarks>
    /// Hand-written instead of CreateIndex/DropIndex, for three reasons:
    /// <list type="bullet">
    /// <item>Each statement runs outside the migration's transaction (<c>suppressTransaction</c>).
    /// Inside one, the schema lock each index takes on its table at the end of its build would be
    /// held until the whole migration commits, so Notifications would be blocked for as long as
    /// the Prints index takes to build, with the old app still serving requests.</item>
    /// <item>Each is guarded by <c>IF [NOT] EXISTS</c>, so a migration that fails part way (and,
    /// outside a transaction, keeps what it did) can simply run again.</item>
    /// <item>Indexes are built <c>ONLINE</c>, which Azure SQL Database supports, so the tables
    /// stay readable and writable during the build. An edition without online builds (Express)
    /// answers error 1712, and the build is retried offline there.</item>
    /// </list>
    /// The new unread index is created before the one it replaces is dropped, so the unread
    /// counts always have an index. Statements are ordered longest first.
    /// </remarks>
    public partial class ReduceDatabaseLoadIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            CreateOnline(migrationBuilder, "Prints", "IX_Prints_ViewStatus",
                "([ViewStatus]) INCLUDE ([UpdatedDate])");

            CreateOnline(migrationBuilder, "Notifications", "IX_Notifications_UserId_Type_Unread",
                "([UserId], [Type]) WHERE [IsRead] = CAST(0 AS bit)");

            // A metadata-only change. Both indexes already cover what this one did; nothing refers
            // to it by name.
            migrationBuilder.Sql(@"
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Notifications_UserId_UnreadCount' AND object_id = OBJECT_ID(N'[Notifications]'))
    DROP INDEX [IX_Notifications_UserId_UnreadCount] ON [Notifications];
", suppressTransaction: true);

            CreateOnline(migrationBuilder, "EmailOutbox", "IX_EmailOutbox_Status_SentAt",
                "([Status], [SentAt])");
        }

        private static void CreateOnline(MigrationBuilder migrationBuilder, string table, string name, string definition)
        {
            var create = $"CREATE INDEX [{name}] ON [{table}] {definition}";
            migrationBuilder.Sql($@"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{name}' AND object_id = OBJECT_ID(N'[{table}]'))
BEGIN
    BEGIN TRY
        {create} WITH (ONLINE = ON);
    END TRY
    BEGIN CATCH
        IF ERROR_NUMBER() <> 1712 THROW;
        {create};
    END CATCH
END
", suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Notifications_UserId_UnreadCount' AND object_id = OBJECT_ID(N'[Notifications]'))
    CREATE INDEX [IX_Notifications_UserId_UnreadCount] ON [Notifications] ([UserId]) INCLUDE ([IsRead]);
", suppressTransaction: true);

            foreach (var (table, name) in new[]
            {
                ("EmailOutbox", "IX_EmailOutbox_Status_SentAt"),
                ("Notifications", "IX_Notifications_UserId_Type_Unread"),
                ("Prints", "IX_Prints_ViewStatus"),
            })
            {
                migrationBuilder.Sql($@"
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{name}' AND object_id = OBJECT_ID(N'[{table}]'))
    DROP INDEX [{name}] ON [{table}];
", suppressTransaction: true);
            }
        }
    }
}
