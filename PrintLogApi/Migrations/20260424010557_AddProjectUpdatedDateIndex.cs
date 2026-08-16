using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectUpdatedDateIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Projects_CreatedById_UpdatedDate",
                table: "Projects",
                columns: new[] { "CreatedById", "UpdatedDate" })
                .Annotation("SqlServer:Include", new[] { "Id", "Name", "Status", "ViewStatus", "Reference" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Projects_CreatedById_UpdatedDate",
                table: "Projects");
        }
    }
}
