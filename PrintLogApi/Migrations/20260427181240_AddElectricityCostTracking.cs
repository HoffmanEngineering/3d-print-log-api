using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class AddElectricityCostTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "WattageW",
                table: "Printers",
                type: "float",
                nullable: true);

            migrationBuilder.InsertData(
                table: "UserSettingTypes",
                columns: new[] { "Id", "Description", "Name" },
                values: new object[,]
                {
                    { 12, "The user's electricity rate in currency per kWh.", "Electricity_KwhRate" },
                    { 13, "Default printer wattage in watts used when a printer has no specific wattage set.", "Electricity_DefaultWattageW" }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WattageW",
                table: "Printers");

            migrationBuilder.DeleteData(
                table: "UserSettingTypes",
                keyColumn: "Id",
                keyValue: 12);

            migrationBuilder.DeleteData(
                table: "UserSettingTypes",
                keyColumn: "Id",
                keyValue: 13);
        }
    }
}
