using System;
using Microsoft.EntityFrameworkCore.Migrations;

namespace PrintLogApi.Migrations
{
    public partial class AddFilamentAdjustments : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FilamentAdjustments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FilamentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AmountMg = table.Column<long>(type: "bigint", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedById = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FilamentAdjustments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FilamentAdjustments_Filaments_FilamentId",
                        column: x => x.FilamentId,
                        principalTable: "Filaments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FilamentAdjustments_Users_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.NoAction);
                    table.ForeignKey(
                        name: "FK_FilamentAdjustments_Users_UpdatedById",
                        column: x => x.UpdatedById,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.NoAction);
                });

            migrationBuilder.UpdateData(
                table: "Materials",
                keyColumn: "Id",
                keyValue: new Guid("3dbc49c5-a493-4e21-a4d5-d94b8c0d53da"),
                columns: new[] { "Acronym", "Name" },
                values: new object[] { "Nylon", null });

            migrationBuilder.CreateIndex(
                name: "IX_FilamentAdjustments_CreatedById",
                table: "FilamentAdjustments",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_FilamentAdjustments_FilamentId",
                table: "FilamentAdjustments",
                column: "FilamentId");

            migrationBuilder.CreateIndex(
                name: "IX_FilamentAdjustments_UpdatedById",
                table: "FilamentAdjustments",
                column: "UpdatedById");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FilamentAdjustments");

            migrationBuilder.UpdateData(
                table: "Materials",
                keyColumn: "Id",
                keyValue: new Guid("3dbc49c5-a493-4e21-a4d5-d94b8c0d53da"),
                columns: new[] { "Acronym", "Name" },
                values: new object[] { null, "Nylon" });
        }
    }
}
