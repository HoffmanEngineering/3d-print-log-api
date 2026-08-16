using Microsoft.EntityFrameworkCore.Migrations;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.Migrations
{
    public partial class AddPrintViewStatus : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ViewStatus",
                table: "Prints",
                nullable: false,
                defaultValue: PrintViewStatus.Private);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ViewStatus",
                table: "Prints");
        }
    }
}
