using Microsoft.EntityFrameworkCore.Migrations;
using PrintLogApi.Models;

#nullable disable

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class SetDefaultMaterialCategory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Default existing filaments to the "filament" category
            migrationBuilder.Sql($@"
UPDATE Filaments
SET MaterialCategoryNickname = 'filament'
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
