using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class AddUniqueUserSettingIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserSettings_UserId",
                table: "UserSettings");

            // Keeps the most recently updated row per (UserId, UserSettingTypeId). This DELETES data.
            // TransferUserData.sql treats choosing between pre-existing duplicates as a judgment call
            // and aborts rather than guessing; here we take "latest wins" deliberately, because the
            // alternative is that the index cannot be created at all.
            migrationBuilder.Sql(@"
                WITH Ranked AS (
                    SELECT Id,
                           ROW_NUMBER() OVER (
                               PARTITION BY UserId, UserSettingTypeId
                               ORDER BY UpdatedDate DESC, Id DESC
                           ) AS rn
                    FROM UserSettings
                    WHERE UserId IS NOT NULL
                )
                DELETE FROM UserSettings
                WHERE Id IN (SELECT Id FROM Ranked WHERE rn > 1);
            ");

            migrationBuilder.CreateIndex(
                name: "IX_UserSettings_UserId_UserSettingTypeId",
                table: "UserSettings",
                columns: new[] { "UserId", "UserSettingTypeId" },
                unique: true,
                filter: "[UserId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserSettings_UserId_UserSettingTypeId",
                table: "UserSettings");

            migrationBuilder.CreateIndex(
                name: "IX_UserSettings_UserId",
                table: "UserSettings",
                column: "UserId");
        }
    }
}
