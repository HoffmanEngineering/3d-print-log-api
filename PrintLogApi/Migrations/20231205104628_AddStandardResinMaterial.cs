using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class AddStandardResinMaterial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "MaterialTypes",
                columns: new[] { "Id", "Acronym", "DensityGramPerCubicCm", "MaterialCategoryNickname", "Name" },
                values: new object[] { new Guid("cc3a5fc9-39dd-42c6-8acc-9c9019dcd307"), "Standard Resin", 1.1000000000000001, "resin", "Standard Resin" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "MaterialTypes",
                keyColumn: "Id",
                keyValue: new Guid("cc3a5fc9-39dd-42c6-8acc-9c9019dcd307"));
        }
    }
}
