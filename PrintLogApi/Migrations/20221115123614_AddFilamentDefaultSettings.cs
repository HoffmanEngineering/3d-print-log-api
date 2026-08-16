using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class AddFilamentDefaultSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "UserSettingTypes",
                columns: new[] { "Id", "Description", "Name" },
                values: new object[,]
                {
                    { 7, "The default diameter of new filament (in millimeters).", "Filaments_DefaultDiameterMm" },
                    { 8, "The default price of filament, for when pricing wasn't added.", "Filaments_DefaultPrice" }
                });

            migrationBuilder.Sql(@"
/* Add new default filament diameters based on the most-common diameter for each user. */
INSERT INTO UserSettings (
	UserId
	,UserSettingTypeId
	,Value
	,CreatedDate
	,CreatedById
	,UpdatedDate
	,UpdatedById
	)
SELECT CreatedById
	,7
	,DiameterMm
	,getutcdate()
	,CreatedById
	,getutcdate()
	,CreatedById
FROM (
	SELECT CreatedById
		,DiameterMm
		,ROW_NUMBER() OVER (
			PARTITION BY CreatedById ORDER BY Count(DiameterMm) DESC
				,DiameterMm DESC
			) AS rn
	FROM Filaments
	GROUP BY Filaments.CreatedById
		,DiameterMm
	) f
WHERE rn = 1
    AND DiameterMm IS NOT NULL
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM UserSettings WHERE UserSettingTypeId in (7,8)");

            migrationBuilder.DeleteData(
                table: "UserSettingTypes",
                keyColumn: "Id",
                keyValue: 7);

            migrationBuilder.DeleteData(
                table: "UserSettingTypes",
                keyColumn: "Id",
                keyValue: 8);
        }
    }
}
