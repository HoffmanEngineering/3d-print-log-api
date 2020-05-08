using Microsoft.EntityFrameworkCore.Migrations;
using PrintLogApi.Models;

namespace PrintLogApi.Migrations
{
    public partial class AddUserProfileFields : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Bio",
                table: "Users",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CoverPicture",
                table: "Users",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DisplayName",
                table: "Users",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProfilePicture",
                table: "Users",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ViewStatus",
                table: "Users",
                nullable: false,
                defaultValue: User.ProfileViewStatus.Public);


            // Make any prints with non-valid ViewStatus private by default
            migrationBuilder.Sql("UPDATE [Prints] SET ViewStatus = 3 Where ViewStatus = 0");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Bio",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CoverPicture",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "DisplayName",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ProfilePicture",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ViewStatus",
                table: "Users");
        }
    }
}
