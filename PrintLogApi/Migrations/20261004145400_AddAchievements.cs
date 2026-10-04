using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class AddAchievements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AchievementCatalogVersion",
                table: "Users",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Slicer",
                table: "Prints",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SlicerVersion",
                table: "Prints",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Source",
                table: "Prints",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Slicer",
                table: "CuraSettings",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "UserAchievements",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    AchievementKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Tier = table.Column<int>(type: "int", nullable: false),
                    UnlockedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Retroactive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserAchievements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserAchievements_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "UserSettingTypes",
                columns: new[] { "Id", "Description", "Name" },
                values: new object[,]
                {
                    { 17, "Show the user's achievements on their public profile (true/false).", "Achievements_ShowOnProfile" },
                    { 18, "How new achievements are celebrated (on/quiet/off).", "Achievements_Celebrations" },
                    { 19, "The achievement hint the user dismissed, as key:tier.", "Achievements_DismissedHint" },
                    { 20, "The user's IANA time zone, used for daily and weekly streaks.", "General_TimeZone" },
                    { 21, "Send a push notification to the user's devices when they earn an achievement.", "Push_Achievement" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Prints_CreatedById",
                table: "Prints",
                column: "CreatedById")
                .Annotation("SqlServer:Include", new[] { "Source", "Slicer", "Status", "StartDate", "CreatedDate", "PrintTimeInSeconds", "EstimatedPrintTimeInSeconds", "FilamentUsageMg", "EstimatedFilamentUsageMg", "ViewStatus" });

            migrationBuilder.CreateIndex(
                name: "IX_UserAchievements_Key_Tier",
                table: "UserAchievements",
                columns: new[] { "AchievementKey", "Tier" });

            migrationBuilder.CreateIndex(
                name: "IX_UserAchievements_User_Key_Tier",
                table: "UserAchievements",
                columns: new[] { "UserId", "AchievementKey", "Tier" },
                unique: true);

            // Launch backfill: a claimed CuraSetting proves the user ran the slicer plugin, so
            // they get "Plugged In" retroactively. This is the only read of CuraSetting history;
            // afterwards that table can be trimmed freely. CuraSetting.UserId has no FK and
            // deleted users' settings remain, so the INNER JOIN drops orphaned ids that would
            // otherwise fail the FK insert. NOT EXISTS keeps a re-run a no-op.
            migrationBuilder.Sql(@"
INSERT INTO UserAchievements (UserId, AchievementKey, Tier, UnlockedAt, Retroactive)
SELECT DISTINCT cs.UserId, 'plugged-in', 1, SYSUTCDATETIME(), 1
FROM CuraSettings cs
INNER JOIN Users u ON u.Id = cs.UserId
WHERE cs.UserId IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM UserAchievements ua
                  WHERE ua.UserId = cs.UserId AND ua.AchievementKey = 'plugged-in' AND ua.Tier = 1);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserAchievements");

            migrationBuilder.DropIndex(
                name: "IX_Prints_CreatedById",
                table: "Prints");

            migrationBuilder.DeleteData(
                table: "UserSettingTypes",
                keyColumn: "Id",
                keyValue: 17);

            migrationBuilder.DeleteData(
                table: "UserSettingTypes",
                keyColumn: "Id",
                keyValue: 18);

            migrationBuilder.DeleteData(
                table: "UserSettingTypes",
                keyColumn: "Id",
                keyValue: 19);

            migrationBuilder.DeleteData(
                table: "UserSettingTypes",
                keyColumn: "Id",
                keyValue: 20);

            migrationBuilder.DeleteData(
                table: "UserSettingTypes",
                keyColumn: "Id",
                keyValue: 21);

            migrationBuilder.DropColumn(
                name: "AchievementCatalogVersion",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Slicer",
                table: "Prints");

            migrationBuilder.DropColumn(
                name: "SlicerVersion",
                table: "Prints");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "Prints");

            migrationBuilder.DropColumn(
                name: "Slicer",
                table: "CuraSettings");
        }
    }
}
