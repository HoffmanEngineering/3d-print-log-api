using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class AdapterReadiness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ActualIsEstimate",
                table: "PrintFilament",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "FirmwareName",
                table: "Connections",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FirmwareVersion",
                table: "Connections",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HostSoftwareName",
                table: "Connections",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HostSoftwareVersion",
                table: "Connections",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActualIsEstimate",
                table: "PrintFilament");

            migrationBuilder.DropColumn(
                name: "FirmwareName",
                table: "Connections");

            migrationBuilder.DropColumn(
                name: "FirmwareVersion",
                table: "Connections");

            migrationBuilder.DropColumn(
                name: "HostSoftwareName",
                table: "Connections");

            migrationBuilder.DropColumn(
                name: "HostSoftwareVersion",
                table: "Connections");
        }
    }
}
