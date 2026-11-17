using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class AddPrintExternalIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalId",
                table: "Prints",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalSource",
                table: "Prints",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Prints_User_ExternalSource_ExternalId",
                table: "Prints",
                columns: new[] { "CreatedById", "ExternalSource", "ExternalId" },
                unique: true,
                filter: "[ExternalSource] IS NOT NULL AND [ExternalId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Prints_User_ExternalSource_ExternalId",
                table: "Prints");

            migrationBuilder.DropColumn(
                name: "ExternalId",
                table: "Prints");

            migrationBuilder.DropColumn(
                name: "ExternalSource",
                table: "Prints");
        }
    }
}
