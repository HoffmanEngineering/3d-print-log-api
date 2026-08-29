using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class AddPushNotificationUserSettingTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "UserSettingTypes",
                columns: new[] { "Id", "Description", "Name" },
                values: new object[,]
                {
                    { 15, "Send a push notification to the user's devices when a print completes.", "Push_PrintCompleted" },
                    { 16, "Send a push notification to the user's devices when a print fails.", "Push_PrintFailed" }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "UserSettingTypes",
                keyColumn: "Id",
                keyValue: 15);

            migrationBuilder.DeleteData(
                table: "UserSettingTypes",
                keyColumn: "Id",
                keyValue: 16);
        }
    }
}
