using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintLogApi.Migrations
{
    public partial class ConvertToFilamentPriceNotes : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PurchaseNotes",
                table: "Filaments",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.Sql(@"
/* Trim any symbols off of numeric PurchasePriceValues */
UPDATE FILAMENTS
SET PurchasePriceValue = TRIM(REPLACE(REPLACE(REPLACE(PurchasePriceValue, '€', ''), '£', ''), '$', ''))
WHERE ISNUMERIC(Filaments.PurchasePriceValue) = 1;
");

            migrationBuilder.Sql(@"
/* Move any non-numeric prices to the PurchasePriceNotes */
UPDATE FILAMENTS
SET PurchaseNotes = TRIM(REPLACE(REPLACE(REPLACE(PurchasePriceValue, '€', ''), '£', ''), '$', ''))
WHERE Filaments.PurchasePriceValue IS NOT NULL
	AND ISNUMERIC(Filaments.PurchasePriceValue) <> 1;

");

            migrationBuilder.Sql(@"
/* Set non-numeric prices to null */
UPDATE FILAMENTS
SET PurchasePriceValue = NULL
WHERE Filaments.PurchasePriceValue IS NOT NULL
	AND ISNUMERIC(Filaments.PurchasePriceValue) <> 1;
");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
/* Set non-numeric prices to null */
UPDATE FILAMENTS
SET PurchasePriceValue = PurchaseNotes
WHERE Filaments.PurchaseNotes IS NOT NULL
");

            migrationBuilder.DropColumn(
                name: "PurchaseNotes",
                table: "Filaments");
        }
    }
}
