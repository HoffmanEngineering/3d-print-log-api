using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class AddFilamentColorPattern : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ColorPattern",
                table: "Filaments",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Colors",
                table: "Filaments",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Effects",
                table: "Filaments",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FinishType",
                table: "Filaments",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ColorPattern",
                table: "Filaments");

            migrationBuilder.DropColumn(
                name: "Colors",
                table: "Filaments");

            migrationBuilder.DropColumn(
                name: "Effects",
                table: "Filaments");

            migrationBuilder.DropColumn(
                name: "FinishType",
                table: "Filaments");
        }
    }
}
