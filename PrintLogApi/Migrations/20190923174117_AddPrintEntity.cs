using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

namespace PrintLogApi.Migrations
{
    public partial class AddPrintEntity : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Prints",
                columns: table => new
                {
                    Id = table.Column<long>(nullable: false)
                        .Annotation("SqlServer:ValueGenerationStrategy", SqlServerValueGenerationStrategy.IdentityColumn),
                    PrinterId = table.Column<long>(nullable: false),
                    EstimatedPrintTimeInSeconds = table.Column<int>(nullable: true),
                    EstimatedFilamentUsageMg = table.Column<int>(nullable: true),
                    PrintTimeInSeconds = table.Column<int>(nullable: true),
                    FilamentUsageMg = table.Column<int>(nullable: true),
                    FilamentType = table.Column<string>(nullable: true),
                    Notes = table.Column<string>(maxLength: 1000, nullable: true),
                    Url = table.Column<string>(maxLength: 1000, nullable: true),
                    Status = table.Column<int>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Prints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Prints_Printers_PrinterId",
                        column: x => x.PrinterId,
                        principalTable: "Printers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Prints_PrinterId",
                table: "Prints",
                column: "PrinterId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Prints");
        }
    }
}
