using Microsoft.EntityFrameworkCore.Migrations;

namespace PrintLogApi.Migrations
{
    public partial class SplitPrintFilamentLengthsIntoSeparateFields : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "LengthIsSource",
                table: "PrintFilament",
                newName: "IsEstimatedLengthSource");

            migrationBuilder.AddColumn<bool>(
                name: "IsActualLengthSource",
                table: "PrintFilament",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsActualLengthSource",
                table: "PrintFilament");

            migrationBuilder.RenameColumn(
                name: "IsEstimatedLengthSource",
                table: "PrintFilament",
                newName: "LengthIsSource");
        }
    }
}
