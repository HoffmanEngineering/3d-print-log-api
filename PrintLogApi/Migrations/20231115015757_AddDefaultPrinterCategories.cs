using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class AddDefaultPrinterCategories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Printers_PrinterCategories_TypeNickname",
                table: "Printers");

            migrationBuilder.DropIndex(
                name: "IX_Printers_TypeNickname",
                table: "Printers");

            migrationBuilder.DropColumn(
                name: "TypeNickname",
                table: "Printers");

            migrationBuilder.AddColumn<string>(
                name: "CategoryNickname",
                table: "Printers",
                type: "nvarchar(50)",
                nullable: true,
                defaultValue: "FFF");

            migrationBuilder.CreateIndex(
                name: "IX_Printers_CategoryNickname",
                table: "Printers",
                column: "CategoryNickname");

            migrationBuilder.AddForeignKey(
                name: "FK_Printers_PrinterCategories_CategoryNickname",
                table: "Printers",
                column: "CategoryNickname",
                principalTable: "PrinterCategories",
                principalColumn: "Nickname");
            
            // Most printers are FFF printers
            migrationBuilder.Sql(@"
UPDATE Printers
SET CategoryNickname = 'FFF'
where CategoryNickname is NULL
");

            // Most printers are FFF printers, but a handful are SLA, so we can help those users
            migrationBuilder.Sql(@"
UPDATE Printers
SET CategoryNickname = 'SLA'
where (make = 'elegoo' and (model like '%Mars%' or model like '%jupiter%' or model like '%saturn%')) 
    or (make = 'Anycubic' and model like '%photon%')
    or (make = 'formlabs' and model not like '%fuse%') 
    or (model like '%alkaid%') 
    or description like '%resin%' or name like '%resin%'
    or ((description like '%SLA%' or name like '%SLA%') and Model not like '%Ender%' and make not like '%Prusa%' and model not like '%P1P%' and model not like '%SV06%');
");

            // There are a couple SLS printers
            migrationBuilder.Sql(@"
UPDATE Printers
SET CategoryNickname = 'SLS'
where (make = 'formlabs' and model like '%fuse%') 
");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Printers_PrinterCategories_CategoryNickname",
                table: "Printers");

            migrationBuilder.DropIndex(
                name: "IX_Printers_CategoryNickname",
                table: "Printers");

            migrationBuilder.DropColumn(
                name: "CategoryNickname",
                table: "Printers");

            migrationBuilder.AddColumn<string>(
                name: "TypeNickname",
                table: "Printers",
                type: "nvarchar(50)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Printers_TypeNickname",
                table: "Printers",
                column: "TypeNickname");

            migrationBuilder.AddForeignKey(
                name: "FK_Printers_PrinterCategories_TypeNickname",
                table: "Printers",
                column: "TypeNickname",
                principalTable: "PrinterCategories",
                principalColumn: "Nickname");
        }
    }
}
