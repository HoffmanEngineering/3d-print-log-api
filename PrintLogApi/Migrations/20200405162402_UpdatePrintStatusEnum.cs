using Microsoft.EntityFrameworkCore.Migrations;

namespace PrintLogApi.Migrations
{
    public partial class UpdatePrintStatusEnum : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Originally the PrintStatus Enum was 0-based. This is for the switch to 1 base.
            migrationBuilder.Sql("UPDATE[Prints] SET Status = Status + 1");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE[Prints] SET Status = Status - 1");
        }
    }
}
