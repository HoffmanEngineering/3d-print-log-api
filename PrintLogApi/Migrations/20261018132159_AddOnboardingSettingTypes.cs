using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class AddOnboardingSettingTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "UserSettingTypes",
                columns: new[] { "Id", "Description", "Name" },
                values: new object[,]
                {
                    { 29, "When the user finished or skipped the new-user welcome flow (ISO 8601).", "Onboarding_WelcomeFinishedAt" },
                    { 30, "When the user dismissed the Getting Started checklist (ISO 8601).", "Onboarding_ChecklistDismissedAt" }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "UserSettingTypes",
                keyColumn: "Id",
                keyValue: 29);

            migrationBuilder.DeleteData(
                table: "UserSettingTypes",
                keyColumn: "Id",
                keyValue: 30);
        }
    }
}
