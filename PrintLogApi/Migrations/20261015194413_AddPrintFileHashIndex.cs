using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class AddPrintFileHashIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Prints_User_FileHash",
                table: "Prints",
                columns: new[] { "CreatedById", "FileHash" },
                filter: "[FileHash] IS NOT NULL")
                .Annotation("SqlServer:Online", true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Prints_User_FileHash",
                table: "Prints");
        }
    }
}
