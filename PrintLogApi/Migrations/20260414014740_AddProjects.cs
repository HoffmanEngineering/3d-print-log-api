using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class AddProjects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ProjectId",
                table: "Prints",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Projects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Url = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ViewStatus = table.Column<int>(type: "int", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedById = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Projects_Users_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.NoAction);
                    table.ForeignKey(
                        name: "FK_Projects_Users_UpdatedById",
                        column: x => x.UpdatedById,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.NoAction);
                });

            migrationBuilder.CreateTable(
                name: "ProjectImages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedById = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectImages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectImages_Files_FileId",
                        column: x => x.FileId,
                        principalTable: "Files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProjectImages_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProjectImages_Users_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.NoAction);
                    table.ForeignKey(
                        name: "FK_ProjectImages_Users_UpdatedById",
                        column: x => x.UpdatedById,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.NoAction);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Prints_ProjectId",
                table: "Prints",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectImages_CreatedById",
                table: "ProjectImages",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectImages_FileId",
                table: "ProjectImages",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectImages_ProjectId",
                table: "ProjectImages",
                column: "ProjectId")
                .Annotation("SqlServer:Include", new[] { "Id", "FileId", "IsDefault", "DisplayOrder", "CreatedDate", "CreatedById" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectImages_UpdatedById",
                table: "ProjectImages",
                column: "UpdatedById");

            migrationBuilder.CreateIndex(
                name: "IX_Projects_CreatedById_CreatedDate",
                table: "Projects",
                columns: new[] { "CreatedById", "CreatedDate" })
                .Annotation("SqlServer:Include", new[] { "Id", "Name", "Status", "ViewStatus", "Reference" });

            migrationBuilder.CreateIndex(
                name: "IX_Projects_UpdatedById",
                table: "Projects",
                column: "UpdatedById");

            migrationBuilder.AddForeignKey(
                name: "FK_Prints_Projects_ProjectId",
                table: "Prints",
                column: "ProjectId",
                principalTable: "Projects",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Prints_Projects_ProjectId",
                table: "Prints");

            migrationBuilder.DropTable(
                name: "ProjectImages");

            migrationBuilder.DropTable(
                name: "Projects");

            migrationBuilder.DropIndex(
                name: "IX_Prints_ProjectId",
                table: "Prints");

            migrationBuilder.DropColumn(
                name: "ProjectId",
                table: "Prints");
        }
    }
}
