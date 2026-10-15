using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class AddPrintTags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Tags",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    NormalizedName = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Color = table.Column<string>(type: "nvarchar(7)", maxLength: 7, nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedById = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tags", x => x.Id);
                    // NoAction, not the Cascade the model implies. PrintTags cascades from both
                    // Prints and Tags, and Users -> Printers -> Prints -> PrintTags is already a
                    // cascade path, so Users -> Tags -> PrintTags would be a second one (SQL
                    // Server error 1785) — as would the two audit FKs between them. The model
                    // keeps Cascade and this migration overrides it, the same split AddProjects,
                    // AddFilamentImage and AddPrinterImage made; account deletion removes tags
                    // and their links explicitly in UserDeletionService. SQLite tests use
                    // EnsureCreated and never run this migration.
                    table.ForeignKey(
                        name: "FK_Tags_Users_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.NoAction);
                    table.ForeignKey(
                        name: "FK_Tags_Users_UpdatedById",
                        column: x => x.UpdatedById,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.NoAction);
                });

            migrationBuilder.CreateTable(
                name: "PrintTags",
                columns: table => new
                {
                    PrintId = table.Column<long>(type: "bigint", nullable: false),
                    TagId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrintTags", x => new { x.PrintId, x.TagId });
                    table.ForeignKey(
                        name: "FK_PrintTags_Prints_PrintId",
                        column: x => x.PrintId,
                        principalTable: "Prints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PrintTags_Tags_TagId",
                        column: x => x.TagId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PrintTags_TagId",
                table: "PrintTags",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_Tags_CreatedById_NormalizedName",
                table: "Tags",
                columns: new[] { "CreatedById", "NormalizedName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tags_UpdatedById",
                table: "Tags",
                column: "UpdatedById");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PrintTags");

            migrationBuilder.DropTable(
                name: "Tags");
        }
    }
}
