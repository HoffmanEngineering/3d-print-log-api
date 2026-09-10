using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class AddPrinterImage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Printers_UserId",
                table: "Printers");

            migrationBuilder.CreateTable(
                name: "PrinterImages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PrinterId = table.Column<long>(type: "bigint", nullable: false),
                    FileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ThumbnailFileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ContentType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedById = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrinterImages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PrinterImages_Files_FileId",
                        column: x => x.FileId,
                        principalTable: "Files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PrinterImages_Files_ThumbnailFileId",
                        column: x => x.ThumbnailFileId,
                        principalTable: "Files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PrinterImages_Printers_PrinterId",
                        column: x => x.PrinterId,
                        principalTable: "Printers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    // NoAction, not the Cascade the model implies. Users -> Printers ->
                    // PrinterImages is already a cascade path, so a second one through the
                    // audit columns makes SQL Server reject the table with error 1785.
                    // The model keeps Cascade and this migration overrides it, which is the
                    // same split AddProjects and AddFilamentImage made; account deletion
                    // removes these rows explicitly through UserDeletionService, not by
                    // cascade. SQLite tests use EnsureCreated and never run this migration,
                    // so nothing here is caught before deploy.
                    table.ForeignKey(
                        name: "FK_PrinterImages_Users_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.NoAction);
                    table.ForeignKey(
                        name: "FK_PrinterImages_Users_UpdatedById",
                        column: x => x.UpdatedById,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.NoAction);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Printers_UserId_IsActive",
                table: "Printers",
                columns: new[] { "UserId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_PrinterImages_CreatedById",
                table: "PrinterImages",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_PrinterImages_FileId",
                table: "PrinterImages",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_PrinterImages_PrinterId_DisplayOrder",
                table: "PrinterImages",
                columns: new[] { "PrinterId", "DisplayOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_PrinterImages_PrinterId_IsDefault",
                table: "PrinterImages",
                column: "PrinterId",
                unique: true,
                filter: "[IsDefault] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_PrinterImages_ThumbnailFileId",
                table: "PrinterImages",
                column: "ThumbnailFileId");

            migrationBuilder.CreateIndex(
                name: "IX_PrinterImages_UpdatedById",
                table: "PrinterImages",
                column: "UpdatedById");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PrinterImages");

            migrationBuilder.DropIndex(
                name: "IX_Printers_UserId_IsActive",
                table: "Printers");

            migrationBuilder.CreateIndex(
                name: "IX_Printers_UserId",
                table: "Printers",
                column: "UserId");
        }
    }
}
