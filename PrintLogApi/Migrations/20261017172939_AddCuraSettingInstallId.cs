using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class AddCuraSettingInstallId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InstallId",
                table: "CuraSettings",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CuraSettings_InstallId_CreatedDate",
                table: "CuraSettings",
                columns: new[] { "InstallId", "CreatedDate" },
                filter: "[InstallId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CuraSettings_InstallId_CreatedDate",
                table: "CuraSettings");

            migrationBuilder.DropColumn(
                name: "InstallId",
                table: "CuraSettings");
        }
    }
}
