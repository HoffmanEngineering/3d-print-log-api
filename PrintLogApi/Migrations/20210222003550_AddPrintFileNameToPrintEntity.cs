using Microsoft.EntityFrameworkCore.Migrations;

namespace PrintLogApi.Migrations
{
    public partial class AddPrintFileNameToPrintEntity : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FileName",
                table: "Prints",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FileName",
                table: "Prints");
        }
    }
}
