using Microsoft.EntityFrameworkCore.Migrations;

namespace PrintLogApi.Migrations
{
    public partial class AddPrintAllowComments : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowComments",
                table: "Prints",
                nullable: false,
                defaultValue: true);

            migrationBuilder.InsertData(
                table: "UserSettingTypes",
                columns: new[] { "Id", "Description", "Name" },
                values: new object[] { 3, "The value of the last changed Allow Comments on prints.", "Prints_LastSelectedAllowComments" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "UserSettingTypes",
                keyColumn: "Id",
                keyValue: 3);

            migrationBuilder.DropColumn(
                name: "AllowComments",
                table: "Prints");
        }
    }
}
