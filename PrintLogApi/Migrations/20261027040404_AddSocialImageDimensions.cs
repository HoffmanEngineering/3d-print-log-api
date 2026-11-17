using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class AddSocialImageDimensions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SocialHeight",
                table: "ProjectImages",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SocialWidth",
                table: "ProjectImages",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SocialHeight",
                table: "PrintImages",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SocialWidth",
                table: "PrintImages",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SocialHeight",
                table: "ProjectImages");

            migrationBuilder.DropColumn(
                name: "SocialWidth",
                table: "ProjectImages");

            migrationBuilder.DropColumn(
                name: "SocialHeight",
                table: "PrintImages");

            migrationBuilder.DropColumn(
                name: "SocialWidth",
                table: "PrintImages");
        }
    }
}
