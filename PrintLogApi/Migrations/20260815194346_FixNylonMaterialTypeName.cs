using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class FixNylonMaterialTypeName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "MaterialTypes",
                keyColumn: "Id",
                keyValue: new Guid("3dbc49c5-a493-4e21-a4d5-d94b8c0d53da"),
                column: "Name",
                value: "Polyamide");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "MaterialTypes",
                keyColumn: "Id",
                keyValue: new Guid("3dbc49c5-a493-4e21-a4d5-d94b8c0d53da"),
                column: "Name",
                value: null);
        }
    }
}
