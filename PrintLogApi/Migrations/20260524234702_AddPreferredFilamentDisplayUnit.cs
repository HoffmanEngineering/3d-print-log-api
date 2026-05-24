using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class AddPreferredFilamentDisplayUnit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "UserSettingTypes",
                columns: new[] { "Id", "Description", "Name" },
                values: new object[] { 14, "The user's preferred unit for displaying filament usage (1=Weight, 2=Length, 3=Volume).", "Prints_PreferredFilamentDisplayUnit" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "UserSettingTypes",
                keyColumn: "Id",
                keyValue: 14);
        }
    }
}
